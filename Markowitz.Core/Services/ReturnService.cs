using Markowitz.Core.Models;

namespace Markowitz.Core.Services;

public class ReturnService
{
    public (string[] tickers, double[,] returnsMatrix, int nObs, DateTime[] dates)
    BuildAlignedLogReturns(OptimizationRequest req)
    {
        if (req.PricesByTicker == null || req.PricesByTicker.Count == 0)
            throw new ArgumentException("No tickers provided.");

        // 1) Собираем последовательности (dt, close) ПО ВСЕМ тикерам
        var series = new Dictionary<string, List<(DateTime dt, double close)>>();
        foreach (var kv in req.PricesByTicker)
        {
            var ticker = kv.Key;
            var bars = kv.Value ?? new List<PriceBar>();
            var ordered = bars.OrderBy(b => b.Timestamp).ToList();

            var list = new List<(DateTime dt, double close)>(ordered.Count);
            foreach (var b in ordered)
            {
                // используем Date (без времени) для выравнивания по торговым дням
                list.Add((b.Timestamp.Date, (double)b.Close));
            }
            series[ticker] = list;
        }

        // 2) Пересечение дат
        var dateSets = series.Values
            .Select(lst => lst.Select(x => x.dt).ToHashSet())
            .ToList();

        if (dateSets.Count == 0) throw new InvalidOperationException("Empty series.");

        var intersect = new HashSet<DateTime>(dateSets[0]);
        foreach (var s in dateSets.Skip(1)) intersect.IntersectWith(s);

        var allDates = intersect.OrderBy(d => d).ToList();

        // Период/Lookback фильтры
        if (req.Start.HasValue) allDates = allDates.Where(d => d >= req.Start.Value.Date).ToList();
        if (req.End.HasValue)   allDates = allDates.Where(d => d <= req.End.Value.Date).ToList();
        if (req.LookbackDays is int lb && allDates.Count > lb)
            allDates = allDates.Skip(allDates.Count - lb).ToList();

        if (allDates.Count < 2)
            throw new InvalidOperationException("Not enough aligned dates for returns.");

        // 3) Тикеры — БЕРЁМ ИЗ ЗАПРОСА (не из временного словаря), чтобы ничего не потерять
        var tickers = req.PricesByTicker.Keys.OrderBy(k => k).ToArray();

        // 4) Матрица цен на allDates (пересечение — гарантирует наличие ключей)
        var nT = tickers.Length;
        var prices = new double[allDates.Count, nT];

        for (int j = 0; j < nT; j++)
        {
            var seq = series[tickers[j]];
            var map = seq.ToDictionary(x => x.dt, x => x.close);
            for (int t = 0; t < allDates.Count; t++)
            {
                if (!map.TryGetValue(allDates[t], out var px))
                    throw new InvalidOperationException($"Missing price for {tickers[j]} on {allDates[t]:yyyy-MM-dd}");
                prices[t, j] = px;
            }
        }

        // 5) Лог-доходности
        var nObs = allDates.Count - 1;
        var rets = new double[nObs, nT];
        var retDates = new DateTime[nObs]; // даты, на которые рассчитываются доходности (t=1..T-1)

        for (int t = 1; t < allDates.Count; t++)
        {
            retDates[t - 1] = allDates[t];
            for (int j = 0; j < nT; j++)
            {
                var r = Math.Log(prices[t, j] / prices[t - 1, j]);
                rets[t - 1, j] = double.IsFinite(r) ? r : 0.0;
            }
        }

        // возвращаем даты доходностей (как и было в твоём тесте сейчас)
        return (tickers, rets, nObs, retDates);
    }

}
