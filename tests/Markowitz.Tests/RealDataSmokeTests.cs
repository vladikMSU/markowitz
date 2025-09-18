using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Accord.Math.Optimization;
using Markowitz.Core.Models;
using Markowitz.Core.Services;
using Xunit;

namespace Markowitz.Tests;

public class RealDataSmokeTests
{
    private const double SecondsPerYear = 365.25 * 24 * 3600;

    private static readonly string[] AssetFiles =
    {
        "BTC-USD.csv",
        "ETH-USD.csv",
        "SOL-USD.csv",
        "SPY.csv"
    };

    private static string Td(string name) =>
        Path.Combine(AppContext.BaseDirectory, "TestData", name);

    [Fact]
    public void BuildAlignedReturns_ShouldRespectRequestedRange()
    {
        var series = LoadRealSeries();
        var req = new OptimizationRequest
        {
            PricesByTicker = series,
            LookbackDays = 36,
            Start = new DateTime(2018, 1, 1),
            End = new DateTime(2025, 7, 31)
        };

        var manual = ComputeManualReturnData(series, req.LookbackDays, req.Start, req.End);

        var service = new ReturnService();
        var data = service.BuildAlignedReturns(req);

        Assert.Equal(manual.Tickers, data.Tickers);
        Assert.Equal(manual.ReturnDates.Count, data.Returns.GetLength(0));
        Assert.Equal(manual.ReturnDates, data.ReturnDates);
        AssertAlmostEqual(manual.PeriodsPerYear, data.PeriodsPerYear, 1e-9, "Periods per year mismatch");

        AssertMatrixAlmostEqual(manual.ReturnsMatrix, data.Returns, 1e-12);

        Assert.Equal(new DateTime(2022, 8, 1), manual.PriceDates.First());
        Assert.Equal(new DateTime(2025, 7, 1), manual.PriceDates.Last());
    }

    [Fact]
    public void QuadraticProgramming_OnRealData_ShouldMatchIndependentSolution()
    {
        var series = LoadRealSeries();
        var req = new OptimizationRequest
        {
            PricesByTicker = series,
            LookbackDays = 252,
            Method = OptimizationMethod.QuadraticProgramming,
            AllowShort = false
        };

        var manual = ComputeManualReturnData(series, req.LookbackDays, req.Start, req.End);
        var moments = ComputePerPeriodMoments(manual.ReturnsMatrix);
        ApplyRidge(moments.SigmaPeriod, 1e-8);

        var expectedWeights = SolveMinVarianceLongOnly(moments.SigmaPeriod, manual.Tickers);
        var expectedByTicker = manual.Tickers
            .Select((t, i) => (Ticker: t, Weight: expectedWeights[i]))
            .ToDictionary(x => x.Ticker, x => x.Weight, StringComparer.OrdinalIgnoreCase);

        var optimizer = TestUtils.CreateOptimizer();
        var result = optimizer.Optimize(req);

        Assert.Equal(OptimizationMethod.QuadraticProgramming, result.Method);
        Assert.Equal(manual.ReturnDates.Count, result.Observations);

        foreach (var ticker in manual.Tickers)
        {
            AssertAlmostEqual(expectedByTicker[ticker], result.Weights[ticker], 1e-6,
                $"Weight mismatch for {ticker}");
        }

        var expectedPeriodReturn = Dot(moments.MuPeriod, expectedWeights);
        var expectedAnnualReturn = expectedPeriodReturn * manual.PeriodsPerYear;
        var expectedVariancePeriod = QuadraticForm(expectedWeights, moments.SigmaPeriod);
        var expectedVolatilityAnnual = Math.Sqrt(Math.Max(expectedVariancePeriod, 0)) * Math.Sqrt(manual.PeriodsPerYear);

        AssertAlmostEqual(expectedAnnualReturn, result.ExpectedReturnAnnual, 1e-6, "Expected return mismatch");
        AssertAlmostEqual(expectedVolatilityAnnual, result.VolatilityAnnual, 1e-6, "Volatility mismatch");
    }

    private static Dictionary<string, List<PriceBar>> LoadRealSeries()
    {
        var parser = new CsvParsingService();
        var dict = new Dictionary<string, List<PriceBar>>(StringComparer.OrdinalIgnoreCase);
        var missing = new List<string>();

        foreach (var file in AssetFiles)
        {
            var path = Td(file);
            if (!File.Exists(path))
            {
                missing.Add(file);
                continue;
            }

            using var stream = File.OpenRead(path);
            var bars = parser.Parse(stream);
            var ticker = Path.GetFileNameWithoutExtension(file).ToUpperInvariant();

            dict[ticker] = bars;
        }

        if (missing.Count > 0)
            throw new InvalidOperationException("Missing expected test data files: " + string.Join(", ", missing));

        Assert.Equal(AssetFiles.Length, dict.Count);
        return dict;
    }

    private static ManualReturnData ComputeManualReturnData(
        Dictionary<string, List<PriceBar>> byTicker,
        int? lookbackDays,
        DateTime? start,
        DateTime? end)
    {
        var tickers = byTicker.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase).ToArray();
        var perTicker = new Dictionary<string, SortedDictionary<DateTime, double>>(StringComparer.OrdinalIgnoreCase);

        foreach (var ticker in tickers)
        {
            var sorted = new SortedDictionary<DateTime, double>();
            foreach (var bar in byTicker[ticker])
            {
                sorted[bar.Timestamp] = (double)bar.Close;
            }
            if (sorted.Count < 2)
                throw new InvalidOperationException($"Ticker {ticker} does not have enough data.");
            perTicker[ticker] = sorted;
        }

        var aligned = perTicker.Values.First().Keys.ToHashSet();
        foreach (var series in perTicker.Values.Skip(1))
            aligned.IntersectWith(series.Keys);

        var ordered = aligned.OrderBy(d => d).ToList();

        if (start.HasValue)
            ordered = ordered.Where(d => d >= start.Value).ToList();
        if (end.HasValue)
            ordered = ordered.Where(d => d <= end.Value).ToList();
        if (lookbackDays is int lb && ordered.Count > lb)
            ordered = ordered.Skip(ordered.Count - lb).ToList();

        if (ordered.Count < 2)
            throw new InvalidOperationException("Not enough aligned timestamps.");

        var prices = new double[ordered.Count, tickers.Length];
        for (int j = 0; j < tickers.Length; j++)
        {
            var map = perTicker[tickers[j]];
            for (int t = 0; t < ordered.Count; t++)
                prices[t, j] = map[ordered[t]];
        }

        var returns = new double[ordered.Count - 1, tickers.Length];
        var returnDates = new List<DateTime>(ordered.Count - 1);
        for (int t = 1; t < ordered.Count; t++)
        {
            returnDates.Add(ordered[t]);
            for (int j = 0; j < tickers.Length; j++)
            {
                var prev = prices[t - 1, j];
                var curr = prices[t, j];
                if (!double.IsFinite(prev) || Math.Abs(prev) < 1e-12)
                    throw new InvalidOperationException($"Invalid price for {tickers[j]} at {ordered[t - 1]:O}.");
                var value = (curr / prev) - 1.0;
                returns[t - 1, j] = double.IsFinite(value) ? value : 0.0;
            }
        }

        double durationSeconds = (ordered[^1] - ordered[0]).TotalSeconds;
        if (durationSeconds <= 0)
            throw new InvalidOperationException("Cannot infer frequency when timestamps do not advance.");

        double observations = ordered.Count - 1;
        double periodsPerYear = observations * SecondsPerYear / durationSeconds;
        if (!double.IsFinite(periodsPerYear) || periodsPerYear <= 0)
            throw new InvalidOperationException("Failed to infer sampling frequency.");

        return new ManualReturnData(tickers, ordered, returnDates, returns, periodsPerYear);
    }

    private static PerPeriodMoments ComputePerPeriodMoments(double[,] returnsMatrix)
    {
        int observations = returnsMatrix.GetLength(0);
        int assets = returnsMatrix.GetLength(1);
        if (observations < 2)
            throw new InvalidOperationException("Not enough observations to compute statistics.");

        var mu = new double[assets];
        for (int j = 0; j < assets; j++)
        {
            double sum = 0.0;
            for (int t = 0; t < observations; t++)
                sum += returnsMatrix[t, j];
            mu[j] = sum / observations;
        }

        var sigma = new double[assets, assets];
        for (int i = 0; i < assets; i++)
        {
            for (int j = i; j < assets; j++)
            {
                double cov = 0.0;
                for (int t = 0; t < observations; t++)
                {
                    double di = returnsMatrix[t, i] - mu[i];
                    double dj = returnsMatrix[t, j] - mu[j];
                    cov += di * dj;
                }
                cov /= (observations - 1);
                sigma[i, j] = cov;
                sigma[j, i] = cov;
            }
        }

        return new PerPeriodMoments(mu, sigma);
    }

    private static void ApplyRidge(double[,] matrix, double ridge)
    {
        var n = matrix.GetLength(0);
        for (int i = 0; i < n; i++)
            matrix[i, i] += ridge;
    }

    private static double[] SolveMinVarianceLongOnly(double[,] sigma, string[] variableNames)
    {
        int n = sigma.GetLength(0);
        var q = (double[,])sigma.Clone();
        ScaleMatrix(q, 2.0);

        var objective = new QuadraticObjectiveFunction(q, new double[n], variableNames);

        var constraints = new List<LinearConstraint>
        {
            new LinearConstraint(n)
            {
                CombinedAs = Enumerable.Repeat(1.0, n).ToArray(),
                ShouldBe = ConstraintType.EqualTo,
                Value = 1.0
            }
        };

        for (int i = 0; i < n; i++)
        {
            constraints.Add(new LinearConstraint(n)
            {
                CombinedAs = UnitVector(n, i),
                ShouldBe = ConstraintType.GreaterThanOrEqualTo,
                Value = 0.0
            });

            constraints.Add(new LinearConstraint(n)
            {
                CombinedAs = UnitVector(n, i),
                ShouldBe = ConstraintType.LesserThanOrEqualTo,
                Value = 1.0
            });
        }

        var solver = new GoldfarbIdnani(objective, constraints);
        if (!solver.Minimize() || solver.Status != GoldfarbIdnaniStatus.Success)
            throw new InvalidOperationException($"QP solver failed with status {solver.Status}.");

        return solver.Solution;
    }

    private static void ScaleMatrix(double[,] matrix, double factor)
    {
        var rows = matrix.GetLength(0);
        var cols = matrix.GetLength(1);
        for (int i = 0; i < rows; i++)
        {
            for (int j = 0; j < cols; j++)
                matrix[i, j] *= factor;
        }
    }

    private static double[] UnitVector(int dimension, int index)
    {
        var v = new double[dimension];
        v[index] = 1.0;
        return v;
    }

    private static double Dot(double[] vector, double[] weights)
    {
        double sum = 0.0;
        for (int i = 0; i < vector.Length; i++)
            sum += vector[i] * weights[i];
        return sum;
    }

    private static double QuadraticForm(double[] weights, double[,] matrix)
    {
        double sum = 0.0;
        var n = weights.Length;
        for (int i = 0; i < n; i++)
        {
            for (int j = 0; j < n; j++)
                sum += weights[i] * matrix[i, j] * weights[j];
        }
        return sum;
    }


    private static void AssertMatrixAlmostEqual(double[,] expected, double[,] actual, double tolerance)
    {
        Assert.Equal(expected.GetLength(0), actual.GetLength(0));
        Assert.Equal(expected.GetLength(1), actual.GetLength(1));

        for (int i = 0; i < expected.GetLength(0); i++)
        {
            for (int j = 0; j < expected.GetLength(1); j++)
            {
                var delta = Math.Abs(expected[i, j] - actual[i, j]);
                Assert.True(delta <= tolerance,
                    $"Mismatch at ({i}, {j}): expected {expected[i, j]}, actual {actual[i, j]}, delta {delta}");
            }
        }
    }

    private static void AssertAlmostEqual(double expected, double actual, double tolerance, string message)
    {
        var delta = Math.Abs(expected - actual);
        Assert.True(delta <= tolerance,
            $"{message}: expected {expected}, actual {actual}, delta {delta}");
    }

    private sealed record ManualReturnData(
        string[] Tickers,
        List<DateTime> PriceDates,
        List<DateTime> ReturnDates,
        double[,] ReturnsMatrix,
        double PeriodsPerYear);

    private sealed record PerPeriodMoments(double[] MuPeriod, double[,] SigmaPeriod);
}
