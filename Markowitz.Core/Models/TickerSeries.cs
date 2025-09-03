namespace Markowitz.Core.Models;

public class TickerSeries
{
    public string Ticker { get; init; } = default!;
    public List<PriceBar> Bars { get; } = new();
}
