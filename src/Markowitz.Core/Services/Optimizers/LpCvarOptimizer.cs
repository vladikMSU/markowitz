using Google.OrTools.LinearSolver;
using Markowitz.Core.Models;

namespace Markowitz.Core.Services.Optimizers;

public class LpCvarOptimizer : IPortfolioOptimizer
{
    public OptimizationMethod Method => OptimizationMethod.CvarLinearProgramming;

    public bool Supports(OptimizationProblem problem)
    {
        return problem.Method == Method && problem.ScenarioReturns is not null;
    }

    public OptimizationResult Optimize(OptimizationProblem problem)
    {
        var scenarios = problem.ScenarioReturns ?? throw new InvalidOperationException("Scenario matrix is required for CVaR optimization.");
        int scenarioCount = scenarios.GetLength(0);
        int assetCount = scenarios.GetLength(1);
        if (assetCount != problem.Tickers.Length)
            throw new InvalidOperationException("Scenario size does not match number of assets.");

        var solver = Solver.CreateSolver("GLOP");
        if (solver is null)
            throw new InvalidOperationException("Failed to create OR-Tools GLOP solver.");

        var weights = new Variable[assetCount];
        for (int j = 0; j < assetCount; j++)
        {
            double lb = problem.LowerBounds?[j] ?? (problem.AllowShort ? double.NegativeInfinity : 0.0);
            double ub = problem.UpperBounds?[j] ?? (problem.AllowShort ? double.PositiveInfinity : 1.0);
            weights[j] = solver.MakeNumVar(lb, ub, $"w_{problem.Tickers[j]}");
        }

        var t = solver.MakeNumVar(double.NegativeInfinity, double.PositiveInfinity, "t");
        var losses = new Variable[scenarioCount];

        double alpha = problem.CvarAlpha ?? 0.95;
        double normalization = 1.0 / ((1.0 - alpha) * scenarioCount);

        // Sum weights = 1
        var sumConstraint = solver.MakeConstraint(1.0, 1.0, "sum_w");
        for (int j = 0; j < assetCount; j++)
            sumConstraint.SetCoefficient(weights[j], 1.0);

        if (problem.TargetReturn is double target)
        {
            var retConstraint = solver.MakeConstraint(target, double.PositiveInfinity, "target_return");
            for (int j = 0; j < assetCount; j++)
                retConstraint.SetCoefficient(weights[j], problem.Mu[j]);
        }

        for (int i = 0; i < scenarioCount; i++)
        {
            losses[i] = solver.MakeNumVar(0.0, double.PositiveInfinity, $"u_{i}");
            var c = solver.MakeConstraint(double.NegativeInfinity, 0.0, $"cvar_{i}");
            for (int j = 0; j < assetCount; j++)
            {
                double scenarioReturn = scenarios[i, j];
                c.SetCoefficient(weights[j], -scenarioReturn);
            }
            c.SetCoefficient(t, -1.0);
            c.SetCoefficient(losses[i], -1.0);
        }

        var objective = solver.Objective();
        objective.SetCoefficient(t, 1.0);
        for (int i = 0; i < scenarioCount; i++)
            objective.SetCoefficient(losses[i], normalization);
        objective.SetMinimization();

        var resultStatus = solver.Solve();
        if (resultStatus != Solver.ResultStatus.OPTIMAL)
            throw new InvalidOperationException($"CVaR solver failed with status {resultStatus}.");

        var weightDict = new Dictionary<string, double>(assetCount);
        for (int j = 0; j < assetCount; j++)
            weightDict[problem.Tickers[j]] = weights[j].SolutionValue();

        return new OptimizationResult
        {
            Weights = weightDict,
            Method = Method,
            Notes = $"CVaR alpha={alpha:F2}"
        };
    }
}
