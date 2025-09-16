using Markowitz.Core.Models;
using MathNet.Numerics.LinearAlgebra;

namespace Markowitz.Core.Services.Optimizers;

public class ClosedFormOptimizer : IPortfolioOptimizer
{
    public OptimizationMethod Method => OptimizationMethod.ClosedForm;

    public bool Supports(OptimizationProblem problem)
    {
        return problem.Method == Method
               && problem.LowerBounds is null
               && problem.UpperBounds is null;
    }

    public OptimizationResult Optimize(OptimizationProblem problem)
    {
        if (!Supports(problem))
            throw new InvalidOperationException("Closed-form optimizer supports only unconstrained problems.");

        int n = problem.Tickers.Length;
        var sigma = Matrix<double>.Build.DenseOfArray(problem.Sigma);
        var mu = Vector<double>.Build.DenseOfArray(problem.Mu);
        var one = Vector<double>.Build.Dense(n, 1.0);

        Vector<double> w;
        Matrix<double> sigmaInv;
        try
        {
            sigmaInv = sigma.Inverse();
        }
        catch (Exception)
        {
            sigmaInv = sigma.PseudoInverse();
        }

        if (problem.TargetReturn is double target)
        {
            double A = one.DotProduct(sigmaInv * one);
            double B = one.DotProduct(sigmaInv * mu);
            double C = mu.DotProduct(sigmaInv * mu);

            var constraintMatrix = Matrix<double>.Build.DenseOfArray(new double[,] { { A, B }, { B, C } });
            var rhs = Vector<double>.Build.Dense(new[] { 1.0, target });
            var lambdas = constraintMatrix.Solve(rhs);
            double lambda1 = lambdas[0];
            double lambda2 = lambdas[1];

            w = sigmaInv * (one * lambda1 + mu * lambda2);
        }
        else
        {
            double A = one.DotProduct(sigmaInv * one);
            w = (sigmaInv * one) / A;
        }

        var weights = new Dictionary<string, double>(n);
        for (int i = 0; i < n; i++)
            weights[problem.Tickers[i]] = w[i];

        return new OptimizationResult
        {
            Weights = weights,
            Method = Method
        };
    }
}
