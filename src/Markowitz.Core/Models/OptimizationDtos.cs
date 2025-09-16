namespace Markowitz.Core.Models;

public enum OptimizationMethod
{
    ClosedForm,
    QuadraticProgramming,
    CvarLinearProgramming,
    Conic,
    Heuristic
}

public class OptimizationRequest
{
    public Dictionary<string, List<PriceBar>> PricesByTicker { get; init; } = new();
    public int? LookbackDays { get; init; }
    public DateTime? Start { get; init; }
    public DateTime? End { get; init; }

    public double? TargetReturnAnnual { get; init; }
    public double RiskFreeAnnual { get; init; } = 0.0;

    public double? GlobalMinWeight { get; init; }
    public double? GlobalMaxWeight { get; init; }
    public Dictionary<string, double>? LowerBounds { get; init; }
    public Dictionary<string, double>? UpperBounds { get; init; }
    public bool AllowShort { get; init; } = false;

    public OptimizationMethod Method { get; init; } = OptimizationMethod.ClosedForm;

    public double? CvarAlpha { get; init; }
    public List<double[]>? ScenarioReturns { get; init; }
}

public class OptimizationResult
{
    public Dictionary<string, double> Weights { get; init; } = new();
    public double ExpectedReturnAnnual { get; init; }
    public double VolatilityAnnual { get; init; }
    public int Observations { get; init; }
    public OptimizationMethod Method { get; init; }
    public string? Notes { get; init; }
}
