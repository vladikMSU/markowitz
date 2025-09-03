namespace Markowitz.Core.Models;

public class OptimizationRequest
{
    public Dictionary<string, List<PriceBar>> PricesByTicker { get; init; } = new();
    public int? LookbackDays { get; init; }
    public DateTime? Start { get; init; }
    public DateTime? End { get; init; }

    public double? TargetReturnAnnual { get; init; } // null => GMV
    public double RiskFreeAnnual { get; init; } = 0.0; // для future use

    // v2 (QP):
    public double? MinWeight { get; init; } // игнор в MVP
    public double? MaxWeight { get; init; } // игнор в MVP
    public bool AllowShort { get; init; } = false; // игнор в MVP
}

public class OptimizationResult
{
    public Dictionary<string, double> Weights { get; init; } = new();
    public double ExpectedReturnAnnual { get; init; }
    public double VolatilityAnnual { get; init; }
    public int Observations { get; init; }
}
