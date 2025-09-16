using Markowitz.Core.Models;
using Markowitz.Core.Services;
using Markowitz.Core.Services.Optimizers;
using Xunit;


namespace Markowitz.Tests;

public class OptimizerTests
{
    [Fact]
    public void GMV_On_Diagonal_Covariance_Should_Weight_Inversely_To_Variance()
    {
        // Конструируем цены под заданные лог-доходности через exp(r)
        // A: r = [ +0.1, -0.1, +0.1, -0.1 ] → var ~ 0.1^2
        // B: r = [ +0.2, -0.2, -0.2, +0.2 ] → var ~ 0.2^2 (в 4 раза больше дисперсия)

        static IEnumerable<(string Date, decimal Close)> MakeSeriesA()
        {
            double[] r = { +0.1, -0.1, +0.1, -0.1 };
            var dates = new[] { "2024-01-01","2024-01-02","2024-01-03","2024-01-04","2024-01-05" };
            double p = 100.0;
            yield return (dates[0], (decimal)Math.Round(p, 6));
            for (int i = 0; i < r.Length; i++)
            {
                p *= Math.Exp(r[i]);
                yield return (dates[i+1], (decimal)Math.Round(p, 6));
            }
        }

        static IEnumerable<(string Date, decimal Close)> MakeSeriesB()
        {
            double[] r = { +0.2, -0.2, -0.2, +0.2 };
            var dates = new[] { "2024-01-01","2024-01-02","2024-01-03","2024-01-04","2024-01-05" };
            double p = 100.0;
            yield return (dates[0], (decimal)Math.Round(p, 6));
            for (int i = 0; i < r.Length; i++)
            {
                p *= Math.Exp(r[i]);
                yield return (dates[i+1], (decimal)Math.Round(p, 6));
            }
        }

        string CsvFromCloses(IEnumerable<(string Date, decimal Close)> seq)
        {
            var rows = seq.Select(t => (t.Date, t.Close, High: t.Close, Low: t.Close, Open: t.Close, Volume: 0L)).ToArray();
            return TestUtils.SampleCsv(rows);
        }

        var parser = new CsvParsingService();

        var csvA = CsvFromCloses(MakeSeriesA());
        var csvB = CsvFromCloses(MakeSeriesB());

        var barsA = parser.Parse(TestUtils.ToStream(csvA));
        var barsB = parser.Parse(TestUtils.ToStream(csvB));

        var req = TestUtils.MakeRequest(new Dictionary<string, List<PriceBar>>
        {
            ["A"] = barsA, ["B"] = barsB
        });

        var opt = TestUtils.CreateOptimizer();
        var res = opt.Optimize(req);

        var wA = res.Weights["A"];
        var wB = res.Weights["B"];

        // ждём ≈ 0.8 и 0.2 (расширенный допуск на численную погрешность)
        Assert.InRange(wA, 0.70, 0.90);
        Assert.InRange(wB, 0.10, 0.30);
        Assert.True(Math.Abs(1.0 - (wA + wB)) < 1e-9);
    }

    [Fact]
    public void MinVar_With_Target_Equal_To_GMV_Return_Should_Reproduce_GMV()
    {
        var csvA = TestUtils.SampleCsv(
            ("2024-01-01", 100m,0,0,0,0),
            ("2024-01-02", 101m,0,0,0,0),
            ("2024-01-03", 102m,0,0,0,0),
            ("2024-01-04", 103m,0,0,0,0),
            ("2024-01-05", 104m,0,0,0,0)
        );
        var csvB = TestUtils.SampleCsv(
            ("2024-01-01", 100m,0,0,0,0),
            ("2024-01-02", 102m,0,0,0,0),
            ("2024-01-03", 104m,0,0,0,0),
            ("2024-01-04", 106m,0,0,0,0),
            ("2024-01-05", 108m,0,0,0,0)
        );

        var parser = new CsvParsingService();
        var barsA = parser.Parse(TestUtils.ToStream(csvA));
        var barsB = parser.Parse(TestUtils.ToStream(csvB));

        var reqGMV = TestUtils.MakeRequest(new Dictionary<string, List<PriceBar>>
        {
            ["A"] = barsA, ["B"] = barsB
        });

        var opt = TestUtils.CreateOptimizer();
        var gmv = opt.Optimize(reqGMV);

        var target = gmv.ExpectedReturnAnnual;

        var reqTarget = TestUtils.MakeRequest(new Dictionary<string, List<PriceBar>>
        {
            ["A"] = barsA, ["B"] = barsB
        }, target: target);

        var minVarAtTarget = opt.Optimize(reqTarget);

        // веса должны быть очень близки
        Assert.Equal(gmv.Weights["A"], minVarAtTarget.Weights["A"], 3);
        Assert.Equal(gmv.Weights["B"], minVarAtTarget.Weights["B"], 3);
    }

    [Fact]
    public void GMV_With_Identical_Series_Should_Not_NaN_And_Weights_Sum_To_1()
    {
        var csv = TestUtils.SampleCsv(
            ("2024-01-01", 100m,0,0,0,0),
            ("2024-01-02", 101m,0,0,0,0),
            ("2024-01-03", 102m,0,0,0,0),
            ("2024-01-04", 103m,0,0,0,0)
        );

        var parser = new CsvParsingService();
        var barsA = parser.Parse(TestUtils.ToStream(csv));
        var barsB = parser.Parse(TestUtils.ToStream(csv));

        var req = TestUtils.MakeRequest(new Dictionary<string, List<PriceBar>>
        {
            ["A"] = barsA, ["B"] = barsB
        });

        var opt = TestUtils.CreateOptimizer();
        var res = opt.Optimize(req);

        var sum = res.Weights.Values.Sum();
        Assert.True(double.IsFinite(sum));
        Assert.True(Math.Abs(sum - 1.0) < 1e-9);
        Assert.All(res.Weights.Values, v => Assert.True(double.IsFinite(v)));
    }
}

