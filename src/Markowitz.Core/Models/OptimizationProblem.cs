namespace Markowitz.Core.Models;

public record OptimizationProblem(
    string[] Tickers,
    double[] Mu,
    double[,] Sigma,
    double? TargetReturn,
    double RiskFreeRate,
    double[]? LowerBounds,
    double[]? UpperBounds,
    bool AllowShort,
    double[,]? ScenarioReturns,
    double? CvarAlpha,
    OptimizationMethod Method);
