using Markowitz.Core.Models;

namespace Markowitz.Core.Services;

public class ReturnService
{
    public (string[] tickers, double[,] returnsMatrix, int nObs, DateTime[] dates)
        BuildAlignedLogReturns(OptimizationRequest req)
    {
        // 1) Соберём close по каждому тикеру
        var series = new Dictionary<string, List<(DateTime dt, double close)>>();

        foreach (var (ticker, bars) in req.PricesByTicker)
        {
            var ordered = bars.OrderBy(b => b.Timestamp).ToList();
            var list = new List<(DateTime, double)>();
            foreach (var b in ordered)
                list.Add((b.Timestamp.Date, (double)b.Close));
            series[ticker] = list;
        }

        // 2) Пересечём даты
        var allDates = series.Values
            .Select(s => s.Select(p => p.dt).Distinct())
            .Aggregate((acc, s) => acc.Intersect(s).ToHashSet())
            .OrderBy(d => d)
            .ToList();

        // Период
        if (req.Start.HasValue) allDates = allDates.Where(d => d >= req.Start.Value.Date).ToList();
        if (req.End.HasValue)   allDates = allDates.Where(d => d <= req.End.Value.Date).ToList();
        if (req.LookbackDays is int lb && allDates.Count > lb) allDates = allDates.Skip(Math.Max(0, allDates.Count - lb)).ToList();

        // 3) Матрица цен
        var tickers = series.Keys.OrderBy(k => k).ToArray();
        var prices = new double[allDates.Count, tickers.Length];
        for (int j = 0; j < tickers.Length; j++)
        {
            var map = series[tickers[j]].ToDictionary(x => x.dt, x => x.close);
            for (int t = 0; t < allDates.Count; t++)
                prices[t, j] = map[allDates[t]];
        }

        // 4) Лог-доходности
        var nObs = allDates.Count - 1;
        var rets = new double[nObs, tickers.Length];
        var usedDates = allDates.Skip(1).ToArray();

        for (int t = 1; t < allDates.Count; t++)
        {
            for (int j = 0; j < tickers.Length; j++)
            {
                var rt = Math.Log(prices[t, j] / prices[t - 1, j]);
                rets[t - 1, j] = double.IsFinite(rt) ? rt : 0.0;
            }
        }

        return (tickers, rets, nObs, usedDates);
    }
}
