using Markowitz.Core.Models;
using Markowitz.Core.Services;
using Xunit;

namespace Markowitz.Tests;

public class RealDataSmokeTests
{
    private static string Td(string name) => 
        Path.Combine(AppContext.BaseDirectory, "TestData", name);

    [Fact]
    public void Optimize_On_Real_CSVs_Should_Produce_Finite_Weights()
    {
        var files = new[] { "BTC.csv", "ETH.csv", "SOL.csv", "SPY.csv" };
        var missing = files.Where(f => !File.Exists(Td(f))).ToList();
        if (missing.Count > 0)
        {
            // если файлов нет — пропускаем без ошибки
            Console.WriteLine("Skipping test because files are missing: " + string.Join(", ", missing));
            return;
        }

        var parser = new CsvParsingService();
        var dict = new Dictionary<string, List<PriceBar>>();
        foreach (var f in files)
        {
            using var s = File.OpenRead(Td(f));
            var bars = parser.Parse(s);
            var ticker = Path.GetFileNameWithoutExtension(f).ToUpperInvariant();
            dict[ticker] = bars;
        }

        var req = new OptimizationRequest
        {
            PricesByTicker = dict,
            LookbackDays = 252 // например, последний год
        };

        var opt = new MarkowitzOptimizer();
        var res = opt.Optimize(req);

        Assert.True(res.Weights.Count >= 2);
        Assert.True(Math.Abs(res.Weights.Values.Sum() - 1.0) < 1e-9);
        Assert.All(res.Weights, kv => Assert.True(double.IsFinite(kv.Value)));
        Assert.True(double.IsFinite(res.ExpectedReturnAnnual));
        Assert.True(double.IsFinite(res.VolatilityAnnual));
    }
}
