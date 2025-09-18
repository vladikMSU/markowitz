using System;
using System.Collections.Generic;
using System.Linq;
using Markowitz.Core.Models;
using Markowitz.Core.Services.Optimizers;

namespace Markowitz.Core.Services;

public class MarkowitzOptimizer
{
    private readonly ReturnService _returnService;
    private readonly Dictionary<OptimizationMethod, IPortfolioOptimizer> _optimizers;

    public MarkowitzOptimizer(ReturnService returnService, IEnumerable<IPortfolioOptimizer> optimizers)
    {
        _returnService = returnService;
        _optimizers = optimizers
            .GroupBy(o => o.Method)
            .ToDictionary(g => g.Key, g => g.First());
    }

    public OptimizationResult Optimize(OptimizationRequest req)
    {
        var returnData = _returnService.BuildAlignedReturns(req);
        var tickers = returnData.Tickers;
        var returnsMatrix = returnData.Returns;
        var periodsPerYear = returnData.PeriodsPerYear;
        var nObs = returnsMatrix.GetLength(0);

        if (nObs < 2)
            throw new InvalidOperationException("Not enough observations.");

        int n = tickers.Length;
        var muPeriod = new double[n];
        for (int j = 0; j < n; j++)
        {
            double sum = 0;
            for (int t = 0; t < nObs; t++)
                sum += returnsMatrix[t, j];
            muPeriod[j] = sum / nObs;
        }

        var sigmaPeriod = new double[n, n];
        for (int i = 0; i < n; i++)
        {
            for (int j = i; j < n; j++)
            {
                double cov = 0;
                for (int t = 0; t < nObs; t++)
                {
                    double di = returnsMatrix[t, i] - muPeriod[i];
                    double dj = returnsMatrix[t, j] - muPeriod[j];
                    cov += di * dj;
                }
                cov /= (nObs - 1);
                sigmaPeriod[i, j] = cov;
                sigmaPeriod[j, i] = cov;
            }
        }

        ApplyRidgeRegularization(sigmaPeriod, 1e-8);

        double riskFreePeriod = ConvertAnnualToPeriodic(req.RiskFreeAnnual, periodsPerYear);
        double? targetReturnPeriod = req.TargetReturnAnnual is double targetAnnual
            ? ConvertAnnualToPeriodic(targetAnnual, periodsPerYear)
            : null;

        var (lb, ub) = BuildBounds(req, tickers);

        double[,] scenarios;
        if (req.ScenarioReturns is { Count: > 0 })
            scenarios = ConvertScenarioList(req.ScenarioReturns, n);
        else
            scenarios = (double[,])returnsMatrix.Clone();

        var problem = new OptimizationProblem(
            tickers,
            muPeriod,
            sigmaPeriod,
            targetReturnPeriod,
            riskFreePeriod,
            lb,
            ub,
            req.AllowShort,
            scenarios,
            req.CvarAlpha ?? 0.95,
            req.Method,
            periodsPerYear);

        if (!_optimizers.TryGetValue(req.Method, out var optimizer))
            throw new InvalidOperationException($"Optimizer for method {req.Method} is not registered.");

        if (!optimizer.Supports(problem))
            throw new InvalidOperationException($"Optimizer {req.Method} does not support the provided problem configuration.");

        var rawResult = optimizer.Optimize(problem);
        if (rawResult.Weights == null || rawResult.Weights.Count == 0)
            throw new InvalidOperationException("Optimizer returned empty weights.");

        var normalized = NormalizeWeights(rawResult.Weights, tickers);

        var expectedPeriod = Dot(muPeriod, normalized);
        var variancePeriod = QuadraticForm(normalized, sigmaPeriod);
        var volatilityAnnual = Math.Sqrt(Math.Max(variancePeriod, 0)) * Math.Sqrt(periodsPerYear);

        var portfolioSeries = BuildPortfolioSeries(returnsMatrix, normalized);
        var expectedReturnAnnual = AnnualizeFromSeries(portfolioSeries, periodsPerYear, expectedPeriod * periodsPerYear);

        return new OptimizationResult
        {
            Weights = BuildWeightDictionary(tickers, normalized),
            ExpectedReturnAnnual = expectedReturnAnnual,
            VolatilityAnnual = volatilityAnnual,
            Observations = nObs,
            Method = req.Method,
            Notes = rawResult.Notes
        };
    }

    private static void ApplyRidgeRegularization(double[,] sigma, double ridge)
    {
        int n = sigma.GetLength(0);
        for (int i = 0; i < n; i++)
            sigma[i, i] += ridge;
    }

    private static (double[]? lb, double[]? ub) BuildBounds(OptimizationRequest req, string[] tickers)
    {
        double? globalMin = req.GlobalMinWeight;
        double? globalMax = req.GlobalMaxWeight;
        var lowerDict = req.LowerBounds ?? new Dictionary<string, double>();
        var upperDict = req.UpperBounds ?? new Dictionary<string, double>();

        bool hasCustomBounds = globalMin.HasValue || globalMax.HasValue || lowerDict.Count > 0 || upperDict.Count > 0;
        if (req.Method == OptimizationMethod.ClosedForm && hasCustomBounds)
            throw new InvalidOperationException("Closed-form optimizer does not support bounds. Choose 'QuadraticProgramming' or another supported solver.");

        bool allowShort = req.AllowShort || req.Method == OptimizationMethod.ClosedForm;
        bool enforceLongOnly = !allowShort && req.Method != OptimizationMethod.ClosedForm;

        if (!hasCustomBounds && !enforceLongOnly)
            return (null, null);

        int n = tickers.Length;
        var lb = new double[n];
        var ub = new double[n];

        for (int i = 0; i < n; i++)
        {
            var ticker = tickers[i];
            double min = allowShort ? (globalMin ?? -1.0) : Math.Max(globalMin ?? 0.0, 0.0);
            if (lowerDict.TryGetValue(ticker, out var perAssetMin))
                min = perAssetMin;

            double max = allowShort ? (globalMax ?? 1.0) : Math.Min(globalMax ?? 1.0, 1.0);
            if (upperDict.TryGetValue(ticker, out var perAssetMax))
                max = perAssetMax;

            lb[i] = min;
            ub[i] = max;
        }

        return (lb, ub);
    }

    private static double[,] ConvertScenarioList(List<double[]> scenarios, int assetCount)
    {
        int rows = scenarios.Count;
        var matrix = new double[rows, assetCount];
        for (int i = 0; i < rows; i++)
        {
            var row = scenarios[i];
            if (row.Length != assetCount)
                throw new InvalidOperationException("Scenario vector length does not match asset count.");
            for (int j = 0; j < assetCount; j++)
                matrix[i, j] = row[j];
        }
        return matrix;
    }

    private static double[] NormalizeWeights(Dictionary<string, double> weights, string[] tickers)
    {
        var vector = new double[tickers.Length];
        for (int i = 0; i < tickers.Length; i++)
        {
            if (!weights.TryGetValue(tickers[i], out var value))
                value = 0.0;
            vector[i] = value;
        }

        double sum = vector.Sum();
        if (Math.Abs(sum) > 1e-9)
        {
            for (int i = 0; i < vector.Length; i++)
                vector[i] /= sum;
        }
        return vector;
    }

    private static double Dot(double[] mu, double[] w)
    {
        double sum = 0;
        for (int i = 0; i < mu.Length; i++)
            sum += mu[i] * w[i];
        return sum;
    }

    private static double QuadraticForm(double[] w, double[,] sigma)
    {
        int n = w.Length;
        double sum = 0;
        for (int i = 0; i < n; i++)
        {
            for (int j = 0; j < n; j++)
                sum += w[i] * sigma[i, j] * w[j];
        }
        return sum;
    }

    private static Dictionary<string, double> BuildWeightDictionary(string[] tickers, double[] weights)
    {
        var dict = new Dictionary<string, double>(tickers.Length);
        for (int i = 0; i < tickers.Length; i++)
            dict[tickers[i]] = weights[i];
        return dict;
    }

    private static double ConvertAnnualToPeriodic(double annualRate, double periodsPerYear)
    {
        if (periodsPerYear <= 0)
            throw new InvalidOperationException("Periods per year must be positive.");

        if (Math.Abs(annualRate) < 1e-12)
            return 0;

        double baseValue = 1.0 + annualRate;
        if (baseValue <= 0)
            throw new InvalidOperationException("Annual rate must be greater than -100%.");

        return Math.Pow(baseValue, 1.0 / periodsPerYear) - 1.0;
    }

    private static double[] BuildPortfolioSeries(double[,] returnsMatrix, double[] weights)
    {
        int nObs = returnsMatrix.GetLength(0);
        int assets = returnsMatrix.GetLength(1);
        var series = new double[nObs];
        for (int t = 0; t < nObs; t++)
        {
            double value = 0.0;
            for (int j = 0; j < assets; j++)
                value += returnsMatrix[t, j] * weights[j];
            series[t] = value;
        }
        return series;
    }

    private static double AnnualizeFromSeries(double[] returns, double periodsPerYear, double fallbackArithmetic)
    {
        if (returns.Length == 0)
            return 0.0;

        double logSum = 0.0;
        for (int i = 0; i < returns.Length; i++)
        {
            double factor = 1.0 + returns[i];
            if (factor <= 0)
                return fallbackArithmetic;
            logSum += Math.Log(factor);
        }

        double avgLog = logSum / returns.Length;
        double annualized = Math.Exp(avgLog * periodsPerYear) - 1.0;
        if (!double.IsFinite(annualized))
            return fallbackArithmetic;
        return annualized;
    }
}

