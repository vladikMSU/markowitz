using Markowitz.Core.Models;
using Markowitz.Core.Services;
using Xunit;

namespace Markowitz.Tests;

public class ReturnServiceTests
{
    [Fact]
    public void BuildAlignedReturns_Uses_Date_Intersection()
    {
        var csvA = TestUtils.SampleCsv(
            ("2024-01-01", 100m, 100m, 100m, 100m, 0),
            ("2024-01-02", 110m, 110m, 110m, 110m, 0),
            ("2024-01-03", 121m, 121m, 121m, 121m, 0)
        );

        var csvB = TestUtils.SampleCsv(
            ("2024-01-02", 200m, 200m, 200m, 200m, 0),
            ("2024-01-03", 220m, 220m, 220m, 220m, 0),
            ("2024-01-04", 242m, 242m, 242m, 242m, 0)
        );

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
        var data = svc.BuildAlignedReturns(req);

        Assert.Equal(new[] { "AAA", "BBB" }, data.Tickers);

        Assert.Single(data.ReturnDates);
        Assert.Equal(new DateTime(2024, 1, 3), data.ReturnDates[0]);

        Assert.Equal(1, data.Returns.GetLength(0));
        Assert.Equal(2, data.Returns.GetLength(1));

        var expected = Math.Round(121.0 / 110.0 - 1.0, 12);
        Assert.Equal(expected, Math.Round(data.Returns[0, 0], 12));
        var expectedB = Math.Round(220.0 / 200.0 - 1.0, 12);
        Assert.Equal(expectedB, Math.Round(data.Returns[0, 1], 12));

        Assert.True(double.IsFinite(data.PeriodsPerYear));
        Assert.InRange(data.PeriodsPerYear, 300.0, 400.0);
    }
}
