using System.Text;
using Markowitz.Core.Models;
using Markowitz.Core.Services;
using Markowitz.Core.Services.Optimizers;

namespace Markowitz.Tests;

public static class TestUtils
{
    public static MemoryStream ToStream(string s) => new MemoryStream(Encoding.UTF8.GetBytes(s));

    public static OptimizationRequest MakeRequest(Dictionary<string, List<PriceBar>> byTicker,
        int? lookbackDays = null, DateTime? start = null, DateTime? end = null, double? target = null)
        => new OptimizationRequest
        {
            PricesByTicker = byTicker,
            LookbackDays = lookbackDays,
            Start = start,
            End = end,
            TargetReturnAnnual = target
        };

    public static string SampleCsv(params (string Date, decimal Close, decimal High, decimal Low, decimal Open, long Volume)[] rows)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Date,Close,High,Low,Open,Volume");
        foreach (var r in rows)
            sb.AppendLine($"{r.Date},{r.Close},{r.High},{r.Low},{r.Open},{r.Volume}");
        return sb.ToString();
    }

    public static MarkowitzOptimizer CreateOptimizer()
    {
        IPortfolioOptimizer[] optimizers =
        {
            new ClosedFormOptimizer(),
            new QpOptimizer(),
            new LpCvarOptimizer(),
            new HeuristicOptimizer()
        };
        return new MarkowitzOptimizer(new ReturnService(), optimizers);
    }
}
