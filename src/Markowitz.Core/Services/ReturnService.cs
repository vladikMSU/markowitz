using System;
using System.Collections.Generic;
using System.Linq;
using Markowitz.Core.Models;

namespace Markowitz.Core.Services;

public class ReturnService
{
    private const double SecondsPerYear = 365.25 * 24 * 3600;

    public ReturnData BuildAlignedReturns(OptimizationRequest req)
    {
        if (req.PricesByTicker is null || req.PricesByTicker.Count == 0)
            throw new ArgumentException("No tickers provided.");

        var tickers = req.PricesByTicker.Keys
            .OrderBy(t => t, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var perTicker = new Dictionary<string, SortedDictionary<DateTime, double>>(StringComparer.OrdinalIgnoreCase);
        foreach (var ticker in tickers)
        {
            var bars = req.PricesByTicker[ticker] ?? new List<PriceBar>();
            var sorted = new SortedDictionary<DateTime, double>();
            foreach (var bar in bars.OrderBy(b => b.Timestamp))
            {
                sorted[bar.Timestamp] = (double)bar.Close;
            }

            if (sorted.Count < 2)
                throw new InvalidOperationException($"Ticker '{ticker}' must contain at least two price points.");

            perTicker[ticker] = sorted;
        }

        var aligned = BuildAlignedTimeline(perTicker, req);
        if (aligned.Count < 2)
            throw new InvalidOperationException("Not enough aligned timestamps to compute returns.");

        var returnsMatrix = new double[aligned.Count - 1, tickers.Length];
        var returnDates = new DateTime[aligned.Count - 1];

        for (int t = 1; t < aligned.Count; t++)
        {
            var current = aligned[t];
            var previous = aligned[t - 1];
            returnDates[t - 1] = current;

            for (int j = 0; j < tickers.Length; j++)
            {
                var series = perTicker[tickers[j]];
                if (!series.TryGetValue(previous, out var prevClose) ||
                    !series.TryGetValue(current, out var currClose))
                {
                    throw new InvalidOperationException($"Missing price for '{tickers[j]}' between {previous:O} and {current:O}.");
                }

                if (!double.IsFinite(prevClose) || Math.Abs(prevClose) < 1e-12)
                    throw new InvalidOperationException($"Invalid price for '{tickers[j]}' at {previous:O}.");

                var ret = (currClose / prevClose) - 1.0;
                returnsMatrix[t - 1, j] = double.IsFinite(ret) ? ret : 0.0;
            }
        }

        double periodsPerYear = InferPeriodsPerYear(aligned, req.PeriodsPerYearOverride);
        return new ReturnData(tickers, returnsMatrix, returnDates, periodsPerYear);
    }

    private static List<DateTime> BuildAlignedTimeline(
        Dictionary<string, SortedDictionary<DateTime, double>> perTicker,
        OptimizationRequest req)
    {
        if (perTicker.Count == 0)
            throw new InvalidOperationException("No ticker data available.");

        var enumerator = perTicker.Values.GetEnumerator();
        enumerator.MoveNext();
        var aligned = new HashSet<DateTime>(enumerator.Current.Keys);
        while (enumerator.MoveNext())
            aligned.IntersectWith(enumerator.Current.Keys);

        var ordered = aligned.OrderBy(d => d).ToList();

        if (req.Start.HasValue)
            ordered = ordered.Where(d => d >= req.Start.Value).ToList();
        if (req.End.HasValue)
            ordered = ordered.Where(d => d <= req.End.Value).ToList();
        if (req.LookbackDays is int lb && ordered.Count > lb)
            ordered = ordered.Skip(ordered.Count - lb).ToList();

        return ordered;
    }

    private static double InferPeriodsPerYear(List<DateTime> aligned, double? overrideValue)
    {
        if (overrideValue is double manual)
        {
            if (!double.IsFinite(manual) || manual <= 0)
                throw new InvalidOperationException("Override for periods per year must be a positive finite number.");
            return manual;
        }

        if (aligned.Count < 2)
            throw new InvalidOperationException("Cannot infer frequency from fewer than two timestamps.");

        double durationSeconds = (aligned[^1] - aligned[0]).TotalSeconds;
        if (durationSeconds <= 0)
            throw new InvalidOperationException("Cannot infer frequency when timestamps do not advance.");

        double observations = aligned.Count - 1;
        double periodsPerYear = observations * SecondsPerYear / durationSeconds;

        if (!double.IsFinite(periodsPerYear) || periodsPerYear <= 0)
            throw new InvalidOperationException("Failed to infer a valid sampling frequency.");

        return periodsPerYear;
    }
}

public sealed record ReturnData(
    string[] Tickers,
    double[,] Returns,
    DateTime[] ReturnDates,
    double PeriodsPerYear);
