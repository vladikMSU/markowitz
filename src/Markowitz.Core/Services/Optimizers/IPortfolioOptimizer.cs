using Markowitz.Core.Models;

namespace Markowitz.Core.Services.Optimizers;

public interface IPortfolioOptimizer
{
    OptimizationMethod Method { get; }
    OptimizationResult Optimize(OptimizationProblem problem);
    bool Supports(OptimizationProblem problem);
}
