using Markowitz.Core.Models;
using MathNet.Numerics.LinearAlgebra;

namespace Markowitz.Core.Services;

public class MarkowitzOptimizer
{
    public OptimizationResult Optimize(OptimizationRequest req)
    {
        var (tickers, rets, nObs, _) = new ReturnService().BuildAlignedLogReturns(req);
        if (nObs < 2) throw new InvalidOperationException("Not enough observations.");

        int n = tickers.Length;

        // μ_daily
        var muDaily = Vector<double>.Build.Dense(n, j =>
        {
            double sum = 0;
            for (int t = 0; t < nObs; t++) sum += rets[t, j];
            return sum / nObs;
        });

        // Σ_daily
        var R = Matrix<double>.Build.DenseOfArray(rets);
        var muRow = muDaily.ToRowMatrix();
        var demeaned = R - Matrix<double>.Build.Dense(nObs, n, (i, j) => muDaily[j]);
        var sigmaDaily = (demeaned.TransposeThisAndMultiply(demeaned)) / (nObs - 1);

        // Годовые
        const double K = 252.0;
        var mu = muDaily * K;
        var sigma = sigmaDaily * K;

        // ... внутри Optimize после вычисления sigma (годовой ковариации)
        double ridge = 1e-8; // можно вынести в опции
        for (int i = 0; i < sigma.RowCount; i++)
            sigma[i, i] += ridge;

        // Инверсия Σ
        var sigmaInv = sigma.Inverse();
        var one = Vector<double>.Build.Dense(n, 1.0);

        Vector<double> w;

        if (req.TargetReturnAnnual is double Rtarget)
        {
            // A,B,C
            double A = one.DotProduct(sigmaInv * one);
            double B = one.DotProduct(sigmaInv * mu);
            double C = mu.DotProduct(sigmaInv * mu);

            // [A B; B C] * [λ1; λ2] = [1; R]
            var M = Matrix<double>.Build.DenseOfArray(new double[,] { { A, B }, { B, C } });
            var y = Vector<double>.Build.Dense(new[] { 1.0, Rtarget });
            var lambdas = M.Solve(y);
            double l1 = lambdas[0], l2 = lambdas[1];

            w = sigmaInv * (one * l1 + mu * l2);
        }
        else
        {
            // GMV
            double A = one.DotProduct(sigmaInv * one);
            w = (sigmaInv * one) / A;
        }

        // Нормировка (на всякий)
        w = w / w.Sum();

        // Метрики
        double expRet = mu.DotProduct(w);
        double vol = Math.Sqrt(w * sigma * w); // w' Σ w

        var weights = new Dictionary<string, double>();
        for (int j = 0; j < n; j++) weights[tickers[j]] = w[j];

        return new OptimizationResult
        {
            Weights = weights,
            ExpectedReturnAnnual = expRet,
            VolatilityAnnual = vol,
            Observations = nObs
        };
    }
}
