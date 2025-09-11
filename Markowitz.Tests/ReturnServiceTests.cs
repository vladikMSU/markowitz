using Markowitz.Core.Models;
using Markowitz.Core.Services;
using Xunit;

namespace Markowitz.Tests;

public class ReturnServiceTests
{
    [Fact]
    public void BuildAlignedLogReturns_Uses_Date_Intersection()
    {
        var csvA = TestUtils.SampleCsv(
            ("2024-01-01", 100m, 100m, 100m, 100m, 0),
            ("2024-01-02", 110m, 110m, 110m, 110m, 0),
            ("2024-01-03", 121m, 121m, 121m, 121m, 0)
        ); // +10%, +10%

        var csvB = TestUtils.SampleCsv(
            ("2024-01-02", 200m, 200m, 200m, 200m, 0),
            ("2024-01-03", 220m, 220m, 220m, 220m, 0),
            ("2024-01-04", 242m, 242m, 242m, 242m, 0)
        ); // +10%, +10%

        var parser = new CsvParsingService();
        using var sA = TestUtils.ToStream(csvA);
        using var sB = TestUtils.ToStream(csvB);
        var barsA = parser.Parse(sA);
        var barsB = parser.Parse(sB);

        var req = TestUtils.MakeRequest(new Dictionary<string, List<PriceBar>>
        {
            ["AAA"] = barsA, ["BBB"] = barsB
        });

        var svc = new ReturnService();
        var (tickers, R, nObs, dates) = svc.BuildAlignedLogReturns(req);

        // Пересечение дат = 2024-01-02, 2024-01-03 ⇒ доходностей = 1
        Assert.Single(dates);
        Assert.Equal(1, nObs);

        // Тикеры должны быть оба: AAA и BBB
        Assert.Equal(2, tickers.Length);
        Assert.Contains("AAA", tickers);
        Assert.Contains("BBB", tickers);

        // Лог-доходности: ln(121/110)=~0.095..., ln(220/200)=~0.095...
        Assert.True(Math.Abs(R[0,0] - Math.Log(121.0/110.0)) < 1e-12);
        Assert.True(Math.Abs(R[0,1] - Math.Log(220.0/200.0)) < 1e-12);
    }
}
