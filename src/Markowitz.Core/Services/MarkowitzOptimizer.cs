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

    public PortfolioVisualization? GenerateVisualization(OptimizationRequest req)
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

        var (lb, ub) = BuildBounds(req, tickers);

        double[,] scenarios;
        if (req.ScenarioReturns is { Count: > 0 })
            scenarios = ConvertScenarioList(req.ScenarioReturns, n);
        else
            scenarios = (double[,])returnsMatrix.Clone();

        var visualizationProblem = new OptimizationProblem(
            tickers,
            muPeriod,
            sigmaPeriod,
            null,
            riskFreePeriod,
            lb,
            ub,
            req.AllowShort,
            scenarios,
            req.CvarAlpha ?? 0.95,
            OptimizationMethod.QuadraticProgramming,
            periodsPerYear);

        if (!_optimizers.TryGetValue(OptimizationMethod.QuadraticProgramming, out var optimizer))
            throw new InvalidOperationException("Optimizer for method QuadraticProgramming is not registered.");

        var visualizationRequest = new OptimizationRequest
        {
            PricesByTicker = req.PricesByTicker,
            LookbackDays = req.LookbackDays,
            Start = req.Start,
            End = req.End,
            PeriodsPerYearOverride = req.PeriodsPerYearOverride,
            TargetReturnAnnual = null,
            RiskFreeAnnual = req.RiskFreeAnnual,
            GlobalMinWeight = req.GlobalMinWeight,
            GlobalMaxWeight = req.GlobalMaxWeight,
            LowerBounds = req.LowerBounds,
            UpperBounds = req.UpperBounds,
            AllowShort = req.AllowShort,
            Method = OptimizationMethod.QuadraticProgramming,
            Target = OptimizationTarget.MaxReturn,
            CvarAlpha = req.CvarAlpha,
            ScenarioReturns = req.ScenarioReturns
        };

        var outcome = OptimizeQuadratic(
            optimizer,
            visualizationProblem,
            visualizationRequest,
            muPeriod,
            sigmaPeriod,
            returnsMatrix,
            riskFreePeriod,
            req.RiskFreeAnnual,
            periodsPerYear);

        var frontierCandidates = outcome.FrontierCandidates;
        if (frontierCandidates.Count == 0)
            return null;

        var highlightCandidate = frontierCandidates
            .OrderBy(c => c.VolatilityAnnual)
            .ThenBy(c => Math.Abs(c.ExpectedAnnual))
            .First();

        var normalized = NormalizeWeights(highlightCandidate.Weights, tickers);
        var expectedPeriod = Dot(muPeriod, normalized);
        var expectedReturnAnnual = expectedPeriod * periodsPerYear;

        return BuildVisualization(
            muPeriod,
            sigmaPeriod,
            periodsPerYear,
            lb,
            ub,
            req.AllowShort,
            frontierCandidates,
            tickers,
            normalized,
            expectedReturnAnnual);
    }

    private QuadraticOutcome OptimizeQuadratic(
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
        var objective = req.Target;
        if (objective == OptimizationTarget.MinVolatility && targetReturnPeriod is double)
            objective = OptimizationTarget.TargetReturn;

        var frontierRegistry = new Dictionary<double, PortfolioCandidate>();
        double NormalizeKey(double value) => Math.Round(value, 10);

        void RegisterCandidate(PortfolioCandidate candidate)
        {
            double key = NormalizeKey(candidate.ExpectedAnnual);
            if (!frontierRegistry.TryGetValue(key, out var existing) || candidate.VolatilityAnnual < existing.VolatilityAnnual - 1e-9)
                frontierRegistry[key] = candidate;
        }

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
                var candidate = new PortfolioCandidate(dict, raw.Notes, expectedAnnual, volatilityAnnual, sharpe, sortino, targetReturn);
                RegisterCandidate(candidate);
                return candidate;
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

        PortfolioCandidate selectedCandidate;
        string? selectedNotes;

        switch (objective)
        {
            case OptimizationTarget.MinVolatility:
            {
                selectedCandidate = RequireCandidate(null, "Quadratic solver failed to compute the global minimum variance portfolio.");
                selectedNotes = ComposeNotes(selectedCandidate, periodsPerYear);
                break;
            }

            case OptimizationTarget.TargetReturn:
            {
                if (targetReturnPeriod is null)
                    throw new InvalidOperationException("Provide a target return to use the 'TargetReturn' objective.");
                selectedCandidate = RequireCandidate(targetReturnPeriod, "Quadratic solver could not find a portfolio for the requested target return.");
                selectedNotes = ComposeNotes(selectedCandidate, periodsPerYear);
                break;
            }

            case OptimizationTarget.MaxReturn:
            {
                selectedCandidate = SelectBy(c => c.ExpectedAnnual);
                selectedNotes = ComposeNotes(selectedCandidate, periodsPerYear);
                break;
            }

            case OptimizationTarget.MaxSharpe:
            {
                selectedCandidate = SelectBy(c => c.Sharpe);
                selectedNotes = ComposeNotes(selectedCandidate, periodsPerYear);
                break;
            }

            case OptimizationTarget.MaxSortino:
            {
                selectedCandidate = SelectBy(c => c.Sortino);
                selectedNotes = ComposeNotes(selectedCandidate, periodsPerYear);
                break;
            }

            default:
            {
                selectedCandidate = RequireCandidate(null, "Quadratic solver failed to compute the global minimum variance portfolio.");
                selectedNotes = ComposeNotes(selectedCandidate, periodsPerYear);
                break;
            }
        }

        var frontier = frontierRegistry
            .Values
            .OrderBy(c => c.ExpectedAnnual)
            .ToList();

        return new QuadraticOutcome(
            selectedCandidate.Weights,
            selectedNotes,
            objective,
            frontier);
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
        var lowerDict = req.LowerBounds is null
            ? new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, double>(req.LowerBounds, StringComparer.OrdinalIgnoreCase);
        var upperDict = req.UpperBounds is null
            ? new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, double>(req.UpperBounds, StringComparer.OrdinalIgnoreCase);

        bool hasCustomBounds = globalMin.HasValue || globalMax.HasValue || lowerDict.Count > 0 || upperDict.Count > 0;
        bool allowShort = req.AllowShort;
        bool enforceLongOnly = !allowShort;

        if (!hasCustomBounds && !enforceLongOnly)
            return (null, null);

        if (globalMin.HasValue && !double.IsFinite(globalMin.Value))
            throw new InvalidOperationException("Global min weight must be a finite number.");
        if (globalMax.HasValue && !double.IsFinite(globalMax.Value))
            throw new InvalidOperationException("Global max weight must be a finite number.");

        const double tolerance = 1e-9;
        if (globalMin.HasValue && globalMax.HasValue && globalMin.Value > globalMax.Value + tolerance)
            throw new InvalidOperationException("Global min weight cannot exceed global max weight.");

        int n = tickers.Length;
        var lb = new double[n];
        var ub = new double[n];

        for (int i = 0; i < n; i++)
        {
            var ticker = tickers[i];
            double min = allowShort ? (globalMin ?? -1.0) : Math.Max(globalMin ?? 0.0, 0.0);
            double max = allowShort ? (globalMax ?? 1.0) : Math.Min(globalMax ?? 1.0, 1.0);

            if (lowerDict.TryGetValue(ticker, out var perAssetMin))
            {
                if (!double.IsFinite(perAssetMin))
                    throw new InvalidOperationException($"Lower bound for '{ticker}' must be a finite number.");
                if (!allowShort)
                    perAssetMin = Math.Max(perAssetMin, 0.0);
                min = Math.Max(min, perAssetMin);
            }

            if (upperDict.TryGetValue(ticker, out var perAssetMax))
            {
                if (!double.IsFinite(perAssetMax))
                    throw new InvalidOperationException($"Upper bound for '{ticker}' must be a finite number.");
                if (!allowShort)
                    perAssetMax = Math.Min(perAssetMax, 1.0);
                max = Math.Min(max, perAssetMax);
            }

            if (!allowShort)
            {
                min = Math.Max(min, 0.0);
                max = Math.Min(max, 1.0);
            }

            if (min > max + tolerance)
                throw new InvalidOperationException($"Bounds for '{ticker}' are inconsistent (min {min:G4} exceeds max {max:G4}).");

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

    private static PortfolioVisualization BuildVisualization(
        double[] muPeriod,
        double[,] sigmaPeriod,
        double periodsPerYear,
        double[]? lb,
        double[]? ub,
        bool allowShort,
        IReadOnlyList<PortfolioCandidate> frontierCandidates,
        string[] tickers,
        double[] optimalWeights,
        double optimalReturnAnnual,
        int sampleCount = 5000)
    {
        var randomPortfolios = GenerateRandomPortfolios(
            muPeriod,
            sigmaPeriod,
            periodsPerYear,
            lb,
            ub,
            allowShort,
            sampleCount);

        var frontierPoints = frontierCandidates
            .GroupBy(c => Math.Round(c.ExpectedAnnual, 8))
            .Select(g => g.OrderBy(c => c.VolatilityAnnual).First())
            .OrderBy(c => c.ExpectedAnnual)
            .Select(c => new PortfolioFrontierPoint
            {
                ExpectedReturnAnnual = c.ExpectedAnnual,
                VolatilityAnnual = c.VolatilityAnnual,
                VarianceAnnual = Math.Pow(c.VolatilityAnnual, 2),
                Weights = new Dictionary<string, double>(c.Weights)
            })
            .ToList();

        if (frontierPoints.Count == 0)
        {
            return new PortfolioVisualization
            {
                PortfolioSpace = randomPortfolios,
                EfficientFrontier = Array.Empty<PortfolioFrontierPoint>(),
                SelectedFrontierIndex = null
            };
        }

        int selectedIndex = 0;
        double bestReturnDistance = double.PositiveInfinity;
        double bestWeightDistance = double.PositiveInfinity;

        for (int i = 0; i < frontierPoints.Count; i++)
        {
            var point = frontierPoints[i];
            double returnDistance = Math.Abs(point.ExpectedReturnAnnual - optimalReturnAnnual);
            double weightDistance = ComputeWeightDistance(point.Weights, tickers, optimalWeights);

            if (returnDistance < bestReturnDistance - 1e-12 ||
                (Math.Abs(returnDistance - bestReturnDistance) <= 1e-12 && weightDistance < bestWeightDistance - 1e-12))
            {
                bestReturnDistance = returnDistance;
                bestWeightDistance = weightDistance;
                selectedIndex = i;
            }
        }

        return new PortfolioVisualization
        {
            PortfolioSpace = randomPortfolios,
            EfficientFrontier = frontierPoints,
            SelectedFrontierIndex = selectedIndex
        };
    }

    private static List<PortfolioPoint> GenerateRandomPortfolios(
        double[] muPeriod,
        double[,] sigmaPeriod,
        double periodsPerYear,
        double[]? lb,
        double[]? ub,
        bool allowShort,
        int sampleCount)
    {
        int dimension = muPeriod.Length;
        var result = new List<PortfolioPoint>(sampleCount);
        if (dimension == 0 || sampleCount <= 0)
            return result;

        var rng = new Random(12345);
        var minVec = new double[dimension];
        var maxVec = new double[dimension];

        for (int i = 0; i < dimension; i++)
        {
            minVec[i] = lb is null ? (allowShort ? -1.0 : 0.0) : lb[i];
            maxVec[i] = ub is null ? 1.0 : ub[i];
        }

        int attemptLimit = Math.Max(sampleCount * 40, 20000);
        int attempts = 0;

        while (result.Count < sampleCount && attempts < attemptLimit)
        {
            attempts++;

            double[]? weights = allowShort
                ? SampleWeightsWithShorts(rng, dimension)
                : SampleWeightsLongOnly(rng, dimension);

            if (weights is null)
                continue;

            double sum = weights.Sum();
            if (Math.Abs(sum - 1.0) > 1e-6)
            {
                if (Math.Abs(sum) < 1e-9)
                    continue;
                for (int i = 0; i < dimension; i++)
                    weights[i] /= sum;
            }

            if (!RespectBounds(weights, minVec, maxVec))
                continue;

            double variancePeriod = QuadraticForm(weights, sigmaPeriod);
            if (!double.IsFinite(variancePeriod))
                continue;

            variancePeriod = Math.Max(variancePeriod, 0);
            double varianceAnnual = variancePeriod * periodsPerYear;
            double volatilityAnnual = Math.Sqrt(varianceAnnual);
            double expectedAnnual = Dot(muPeriod, weights) * periodsPerYear;

            result.Add(new PortfolioPoint
            {
                ExpectedReturnAnnual = expectedAnnual,
                VarianceAnnual = varianceAnnual,
                VolatilityAnnual = volatilityAnnual
            });
        }

        return result;
    }

    private static double[] SampleWeightsLongOnly(Random rng, int dimension)
    {
        var weights = new double[dimension];
        double sum = 0;

        for (int i = 0; i < dimension; i++)
        {
            double u = Math.Max(rng.NextDouble(), 1e-12);
            double value = -Math.Log(u);
            weights[i] = value;
            sum += value;
        }

        if (sum <= 0)
            return weights;

        for (int i = 0; i < dimension; i++)
            weights[i] /= sum;

        return weights;
    }

    private static double[]? SampleWeightsWithShorts(Random rng, int dimension)
    {
        var weights = new double[dimension];
        double sum = 0;

        for (int i = 0; i < dimension; i++)
        {
            double value = SampleStandardNormal(rng);
            weights[i] = value;
            sum += value;
        }

        if (Math.Abs(sum) < 1e-9)
            return null;

        for (int i = 0; i < dimension; i++)
            weights[i] /= sum;

        return weights;
    }

    private static bool RespectBounds(double[] weights, double[] minVec, double[] maxVec)
    {
        for (int i = 0; i < weights.Length; i++)
        {
            if (weights[i] < minVec[i] - 1e-6 || weights[i] > maxVec[i] + 1e-6)
                return false;
        }

        return true;
    }

    private static double SampleStandardNormal(Random rng)
    {
        double u1 = Math.Max(rng.NextDouble(), 1e-12);
        double u2 = rng.NextDouble();
        double radius = Math.Sqrt(-2.0 * Math.Log(u1));
        double theta = 2.0 * Math.PI * u2;
        return radius * Math.Cos(theta);
    }

    private static double ComputeWeightDistance(IReadOnlyDictionary<string, double> candidateWeights, string[] tickers, double[] referenceWeights)
    {
        double sum = 0;

        for (int i = 0; i < tickers.Length; i++)
        {
            double value = candidateWeights.TryGetValue(tickers[i], out var weight) ? weight : 0.0;
            double diff = value - referenceWeights[i];
            sum += diff * diff;
        }

        return sum;
    }

    private sealed record QuadraticOutcome(
        Dictionary<string, double> Weights,
        string? Notes,
        OptimizationTarget Target,
        IReadOnlyList<PortfolioCandidate> FrontierCandidates);

    private sealed record PortfolioCandidate(
        Dictionary<string, double> Weights,
        string? Notes,
        double ExpectedAnnual,
        double VolatilityAnnual,
        double Sharpe,
        double Sortino,
        double? TargetReturnPeriod);
}
