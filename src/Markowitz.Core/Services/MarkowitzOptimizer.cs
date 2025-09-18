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

        var baseProblem = new OptimizationProblem(
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

        Dictionary<string, double> rawWeights;
        string? notes = null;
        OptimizationTarget? appliedTarget = null;

        if (req.Method == OptimizationMethod.QuadraticProgramming)
        {
            var qpOutcome = OptimizeQuadratic(
                optimizer,
                baseProblem,
                req,
                muPeriod,
                sigmaPeriod,
                returnsMatrix,
                riskFreePeriod,
                req.RiskFreeAnnual,
                periodsPerYear);
            rawWeights = qpOutcome.Weights;
            notes = qpOutcome.Notes;
            appliedTarget = qpOutcome.Target;
        }
        else
        {
            if (!optimizer.Supports(baseProblem))
                throw new InvalidOperationException($"Optimizer {req.Method} does not support the provided problem configuration.");

            var rawResult = optimizer.Optimize(baseProblem);
            if (rawResult.Weights == null || rawResult.Weights.Count == 0)
                throw new InvalidOperationException("Optimizer returned empty weights.");

            rawWeights = rawResult.Weights;
            notes = rawResult.Notes;
            appliedTarget = rawResult.Target ?? (req.Method == OptimizationMethod.Heuristic ? OptimizationTarget.MaxSortino : null);
        }

        var normalized = NormalizeWeights(rawWeights, tickers);

        var expectedPeriod = Dot(muPeriod, normalized);
        var variancePeriod = QuadraticForm(normalized, sigmaPeriod);
        var volatilityAnnual = Math.Sqrt(Math.Max(variancePeriod, 0)) * Math.Sqrt(periodsPerYear);
        var expectedReturnAnnual = expectedPeriod * periodsPerYear;

        return new OptimizationResult
        {
            Weights = BuildWeightDictionary(tickers, normalized),
            ExpectedReturnAnnual = expectedReturnAnnual,
            VolatilityAnnual = volatilityAnnual,
            Observations = nObs,
            Method = req.Method,
            Target = appliedTarget ?? req.Target,
            Notes = notes
        };
    }

    private (Dictionary<string, double> Weights, string? Notes, OptimizationTarget Target) OptimizeQuadratic(
        IPortfolioOptimizer optimizer,
        OptimizationProblem baseProblem,
        OptimizationRequest req,
        double[] muPeriod,
        double[,] sigmaPeriod,
        double[,] returnsMatrix,
        double riskFreePeriod,
        double riskFreeAnnual,
        double periodsPerYear)
    {
        if (!optimizer.Supports(baseProblem with { TargetReturn = null }))
            throw new InvalidOperationException($"Optimizer {req.Method} does not support the provided problem configuration.");

        double? targetReturnPeriod = baseProblem.TargetReturn;
        double? volatilityCapAnnual = req.TargetVolatilityAnnual;
        var objective = req.Target;
        if (objective == OptimizationTarget.MinVolatility && targetReturnPeriod is double)
            objective = OptimizationTarget.TargetReturn;

        PortfolioCandidate? SolveCandidate(double? targetReturn)
        {
            var problem = baseProblem with { TargetReturn = targetReturn };
            try
            {
                var raw = optimizer.Optimize(problem);
                if (raw.Weights == null || raw.Weights.Count == 0)
                    return null;

                var normalized = NormalizeWeights(raw.Weights, baseProblem.Tickers);
                var expectedPeriod = Dot(muPeriod, normalized);
                var expectedAnnual = expectedPeriod * periodsPerYear;
                var variancePeriod = QuadraticForm(normalized, sigmaPeriod);
                var volatilityAnnual = Math.Sqrt(Math.Max(variancePeriod, 0)) * Math.Sqrt(periodsPerYear);
                var sharpe = ComputeSharpe(expectedAnnual, riskFreeAnnual, volatilityAnnual);
                var sortino = ComputeSortino(normalized, returnsMatrix, riskFreePeriod, periodsPerYear, expectedPeriod);
                var dict = BuildWeightDictionary(baseProblem.Tickers, normalized);
                return new PortfolioCandidate(dict, raw.Notes, expectedAnnual, volatilityAnnual, sharpe, sortino, targetReturn);
            }
            catch
            {
                return null;
            }
        }

        PortfolioCandidate RequireCandidate(double? targetReturn, string failureMessage)
        {
            var candidate = SolveCandidate(targetReturn);
            if (candidate is null)
                throw new InvalidOperationException(failureMessage);

            if (volatilityCapAnnual is double maxVol && candidate.VolatilityAnnual > maxVol + 1e-9)
                throw new InvalidOperationException("No feasible portfolio satisfies the requested volatility ceiling.");

            return candidate;
        }

        PortfolioCandidate SelectBy(Func<PortfolioCandidate, double> scoreSelector)
        {
            PortfolioCandidate? best = null;
            double bestScore = double.NegativeInfinity;

            void Consider(PortfolioCandidate? candidate)
            {
                if (candidate is null)
                    return;
                if (volatilityCapAnnual is double maxVol && candidate.VolatilityAnnual > maxVol + 1e-9)
                    return;

                double score = scoreSelector(candidate);
                if (!double.IsFinite(score))
                    return;

                if (best is null || score > bestScore + 1e-12)
                {
                    best = candidate;
                    bestScore = score;
                }
                else if (best is not null && Math.Abs(score - bestScore) <= 1e-12 && candidate.VolatilityAnnual < best.VolatilityAnnual)
                {
                    best = candidate;
                }
            }

            Consider(SolveCandidate(null));

            foreach (var gridTarget in BuildTargetReturnGrid(muPeriod, targetReturnPeriod))
                Consider(SolveCandidate(gridTarget));

            if (best is null)
                throw new InvalidOperationException("Quadratic solver could not produce a feasible portfolio for the requested objective.");

            return best;
        }

        switch (objective)
        {
            case OptimizationTarget.MinVolatility:
            {
                var candidate = RequireCandidate(null, "Quadratic solver failed to compute the global minimum variance portfolio.");
                return (candidate.Weights, ComposeNotes(candidate, periodsPerYear), objective);
            }

            case OptimizationTarget.TargetReturn:
            {
                if (targetReturnPeriod is null)
                    throw new InvalidOperationException("Provide a target return to use the 'TargetReturn' objective.");
                var candidate = RequireCandidate(targetReturnPeriod, "Quadratic solver could not find a portfolio for the requested target return.");
                return (candidate.Weights, ComposeNotes(candidate, periodsPerYear), objective);
            }

            case OptimizationTarget.MaxReturn:
            {
                var candidate = SelectBy(c => c.ExpectedAnnual);
                return (candidate.Weights, ComposeNotes(candidate, periodsPerYear), objective);
            }

            case OptimizationTarget.MaxSharpe:
            {
                var candidate = SelectBy(c => c.Sharpe);
                return (candidate.Weights, ComposeNotes(candidate, periodsPerYear), objective);
            }

            case OptimizationTarget.MaxSortino:
            {
                var candidate = SelectBy(c => c.Sortino);
                return (candidate.Weights, ComposeNotes(candidate, periodsPerYear), objective);
            }

            default:
            {
                var candidate = RequireCandidate(null, "Quadratic solver failed to compute the global minimum variance portfolio.");
                return (candidate.Weights, ComposeNotes(candidate, periodsPerYear), objective);
            }
        }
    }

    private static IEnumerable<double> BuildTargetReturnGrid(double[] muPeriod, double? explicitTarget)
    {
        if (muPeriod.Length == 0)
            yield break;

        var set = new SortedSet<double>();
        double min = muPeriod.Min();
        double max = muPeriod.Max();
        double span = max - min;
        double buffer = span * 0.25;
        if (buffer < 1e-4)
            buffer = Math.Max(Math.Abs(min), Math.Abs(max)) * 0.25 + 1e-4;

        double low = min - buffer;
        double high = max + buffer;
        const int steps = 21;

        for (int i = 0; i < steps; i++)
        {
            double value = steps == 1 ? low : low + (high - low) * i / (steps - 1);
            set.Add(value);
        }

        foreach (var mu in muPeriod)
            set.Add(mu);

        if (explicitTarget is double t)
            set.Add(t);

        foreach (var value in set)
            yield return value;
    }

    private static string? ComposeNotes(PortfolioCandidate candidate, double periodsPerYear)
    {
        if (candidate.TargetReturnPeriod is double targetPeriod)
        {
            double targetAnnual = targetPeriod * periodsPerYear;
            string addition = $"Target return constraint: {targetAnnual:P2}";
            return string.IsNullOrWhiteSpace(candidate.Notes) ? addition : $"{candidate.Notes}; {addition}";
        }

        return candidate.Notes;
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
        bool allowShort = req.AllowShort;
        bool enforceLongOnly = !allowShort;

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

        return annualRate / periodsPerYear;
    }

    private static double ComputeSharpe(double expectedAnnual, double riskFreeAnnual, double volatilityAnnual)
    {
        double numerator = expectedAnnual - riskFreeAnnual;
        if (volatilityAnnual < 1e-12)
            return numerator > 0 ? double.PositiveInfinity : double.NegativeInfinity;
        return numerator / volatilityAnnual;
    }

    private static double ComputeSortino(double[] weights, double[,] returnsMatrix, double riskFreePeriod, double periodsPerYear, double expectedPeriod)
    {
        double downside = ComputeDownsideDeviation(weights, returnsMatrix, riskFreePeriod);
        if (downside < 1e-12)
            return expectedPeriod > riskFreePeriod ? double.PositiveInfinity : double.NegativeInfinity;

        double periodRatio = (expectedPeriod - riskFreePeriod) / downside;
        return periodRatio * Math.Sqrt(periodsPerYear);
    }

    private static double ComputeDownsideDeviation(double[] weights, double[,] returnsMatrix, double riskFreePeriod)
    {
        int rows = returnsMatrix.GetLength(0);
        int cols = returnsMatrix.GetLength(1);

        if (rows == 0 || cols == 0)
            return 0.0;

        double sumSquares = 0.0;
        for (int i = 0; i < rows; i++)
        {
            double ret = 0.0;
            for (int j = 0; j < cols; j++)
                ret += returnsMatrix[i, j] * weights[j];

            double downside = Math.Min(0.0, ret - riskFreePeriod);
            sumSquares += downside * downside;
        }

        return Math.Sqrt(sumSquares / rows);
    }

    private sealed record PortfolioCandidate(
        Dictionary<string, double> Weights,
        string? Notes,
        double ExpectedAnnual,
        double VolatilityAnnual,
        double Sharpe,
        double Sortino,
        double? TargetReturnPeriod);
}
