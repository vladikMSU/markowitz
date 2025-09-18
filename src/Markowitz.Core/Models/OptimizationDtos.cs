using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;

namespace Markowitz.Core.Models;

public enum OptimizationMethod
{
    QuadraticProgramming,
    CvarLinearProgramming,
    Conic,
    Heuristic
}

public enum OptimizationTarget
{
    [Display(Name = "Min volatility")]
    MinVolatility,
    [Display(Name = "Target return")]
    TargetReturn,
    [Display(Name = "Max return")]
    MaxReturn,
    [Display(Name = "Max Sharpe")]
    MaxSharpe,
    [Display(Name = "Max Sortino")]
    MaxSortino
}

public class OptimizationRequest
{
    public Dictionary<string, List<PriceBar>> PricesByTicker { get; init; } = new();
    public int? LookbackDays { get; init; }
    public DateTime? Start { get; init; }
    public DateTime? End { get; init; }
    public double? PeriodsPerYearOverride { get; init; }

    public double? TargetReturnAnnual { get; init; }
    public double? TargetVolatilityAnnual { get; init; }
    public double RiskFreeAnnual { get; init; } = 0.0;

    public double? GlobalMinWeight { get; init; }
    public double? GlobalMaxWeight { get; init; }
    public Dictionary<string, double>? LowerBounds { get; init; }
    public Dictionary<string, double>? UpperBounds { get; init; }
    public bool AllowShort { get; init; } = false;

    public OptimizationMethod Method { get; init; } = OptimizationMethod.QuadraticProgramming;
    public OptimizationTarget Target { get; init; } = OptimizationTarget.MinVolatility;

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
    public OptimizationTarget? Target { get; init; }
    public string? Notes { get; init; }
}
