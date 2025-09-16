using Accord.Math;
using Accord.Math.Optimization;
using Markowitz.Core.Models;

namespace Markowitz.Core.Services.Optimizers;

public class QpOptimizer : IPortfolioOptimizer
{
    public OptimizationMethod Method => OptimizationMethod.QuadraticProgramming;

    public bool Supports(OptimizationProblem problem)
    {
        return problem.Method == Method && problem.Sigma.GetLength(0) == problem.Sigma.GetLength(1);
    }

    public OptimizationResult Optimize(OptimizationProblem problem)
    {
        if (problem.Tickers.Length == 0)
            throw new InvalidOperationException("Problem has no assets.");

        int n = problem.Tickers.Length;
        var Q = (double[,])problem.Sigma.Clone();
        ScaleMatrixInPlace(Q, 2.0);
        var linear = new double[n];

        var objective = new QuadraticObjectiveFunction(Q, linear, problem.Tickers);
        var constraints = new List<LinearConstraint>();

        constraints.Add(new LinearConstraint(n)
        {
            CombinedAs = Enumerable.Repeat(1.0, n).ToArray(),
            ShouldBe = ConstraintType.EqualTo,
            Value = 1.0
        });

        if (problem.TargetReturn is double target)
        {
            constraints.Add(new LinearConstraint(n)
            {
                CombinedAs = (double[])problem.Mu.Clone(),
                ShouldBe = ConstraintType.EqualTo,
                Value = target
            });
        }

        if (problem.LowerBounds is double[] lb)
        {
            for (int i = 0; i < n; i++)
            {
                double bound = lb[i];
                if (double.IsNegativeInfinity(bound))
                    continue;
                constraints.Add(new LinearConstraint(n)
                {
                    CombinedAs = UnitVector(n, i),
                    ShouldBe = ConstraintType.GreaterThanOrEqualTo,
                    Value = bound
                });
            }
        }

        if (problem.UpperBounds is double[] ub)
        {
            for (int i = 0; i < n; i++)
            {
                double bound = ub[i];
                if (double.IsPositiveInfinity(bound))
                    continue;
                constraints.Add(new LinearConstraint(n)
                {
                    CombinedAs = UnitVector(n, i),
                    ShouldBe = ConstraintType.LesserThanOrEqualTo,
                    Value = bound
                });
            }
        }

        var solver = new GoldfarbIdnani(objective, constraints);
        bool success = solver.Minimize();
        if (!success || solver.Status != GoldfarbIdnaniStatus.Success)
            throw new InvalidOperationException($"QP solver failed: {solver.Status}");

        var solution = solver.Solution;
        var weights = new Dictionary<string, double>(n);
        for (int i = 0; i < n; i++)
            weights[problem.Tickers[i]] = solution[i];

        return new OptimizationResult
        {
            Weights = weights,
            Method = Method
        };
    }

    private static void ScaleMatrixInPlace(double[,] matrix, double scale)
    {
        int rows = matrix.GetLength(0);
        int cols = matrix.GetLength(1);
        for (int i = 0; i < rows; i++)
        {
            for (int j = 0; j < cols; j++)
                matrix[i, j] *= scale;
        }
    }

    private static double[] UnitVector(int dimension, int index)
    {
        var v = new double[dimension];
        v[index] = 1.0;
        return v;
    }
}
