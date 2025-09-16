using Markowitz.Core.Models;
using Markowitz.Core.Services;
using Xunit;

namespace Markowitz.Tests;

public class CsvParsingServiceTests
{
    [Fact]
    public void Parse_Should_Map_Columns_And_Sort_By_Date()
    {
        var csv = TestUtils.SampleCsv(
            ("2024-01-03", 103m, 105m, 99m, 100m, 1000),
            ("2024-01-01", 101m, 102m, 95m, 96m,  900),
            ("2024-01-02", 102m, 103m, 98m,  99m,  950)
        );

        var svc = new CsvParsingService();
        using var ms = TestUtils.ToStream(csv);
        var bars = svc.Parse(ms);

        Assert.Equal(3, bars.Count);
        Assert.True(bars[0].Timestamp <= bars[1].Timestamp && bars[1].Timestamp <= bars[2].Timestamp);

        // проверим маппинг Open/High/Low/Close
        Assert.Equal(96m, bars[0].Open);
        Assert.Equal(102m, bars[1].Close);
        Assert.Equal(105m, bars[2].High);
        Assert.Equal(98m, bars[1].Low);
    }

    [Fact]
    public void Parse_Should_Handle_Different_Date_Formats()
    {
        var csv = "Date,Close,High,Low,Open,Volume\n" +
                  "01/05/2024,100,101,99,100,1000\n" +         // MM/dd/yyyy
                  "2024-01-06 00:00:00,101,102,100,100,1200\n" + // with time
                  "2024-01-07T00:00:00Z,102,103,100,101,1300\n"; // ISO

        var svc = new CsvParsingService();
        using var ms = TestUtils.ToStream(csv);
        var bars = svc.Parse(ms);

        Assert.Equal(3, bars.Count);
        Assert.Equal(new DateTime(2024,1,5), bars[0].Timestamp.Date);
        Assert.Equal(new DateTime(2024,1,7), bars[2].Timestamp.Date);
    }
}
