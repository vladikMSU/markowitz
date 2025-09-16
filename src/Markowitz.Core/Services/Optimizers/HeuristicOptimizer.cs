using Markowitz.Core.Models;

namespace Markowitz.Core.Services.Optimizers;

public class HeuristicOptimizer : IPortfolioOptimizer
{
    private const int DefaultMinPopulationSize = 30;
    private const int PopulationSizeMultiplier = 10;

    public OptimizationMethod Method => OptimizationMethod.Heuristic;

    public bool Supports(OptimizationProblem problem) => problem.Method == Method;

    public OptimizationResult Optimize(OptimizationProblem problem)
    {
        if (problem.ScenarioReturns is null)
            throw new InvalidOperationException("Scenario returns required for heuristic optimizer.");

        int assetCount = problem.Tickers.Length;
        if (assetCount == 0)
            throw new InvalidOperationException("Problem has no assets.");

        var lower = BuildLowerBounds(problem, assetCount);
        var upper = BuildUpperBounds(problem, assetCount);

        var rng = Random.Shared;
        int populationSize = Math.Max(DefaultMinPopulationSize, assetCount * PopulationSizeMultiplier);
        int generations = 300;
        double differentialWeight = 0.6;
        double crossoverRate = 0.7;

        var population = new double[populationSize][];
        var scores = new double[populationSize];

        for (int i = 0; i < populationSize; i++)
        {
            population[i] = CreateRandomCandidate(rng, lower, upper);
            scores[i] = Evaluate(population[i], problem);
        }

        double bestScore = scores[0];
        int bestIndex = 0;
        for (int i = 1; i < populationSize; i++)
        {
            if (scores[i] > bestScore)
            {
                bestScore = scores[i];
                bestIndex = i;
            }
        }

        for (int gen = 0; gen < generations; gen++)
        {
            for (int i = 0; i < populationSize; i++)
            {
                var trial = MutateAndCrossover(rng, i, population, differentialWeight, crossoverRate);
                ProjectCandidate(trial, lower, upper);
                double score = Evaluate(trial, problem);

                if (score > scores[i])
                {
                    population[i] = trial;
                    scores[i] = score;
                    if (score > bestScore)
                    {
                        bestScore = score;
                        bestIndex = i;
                    }
                }
            }
        }

        var bestWeights = population[bestIndex];
        var weightDict = new Dictionary<string, double>(assetCount);
        for (int j = 0; j < assetCount; j++)
            weightDict[problem.Tickers[j]] = bestWeights[j];

        return new OptimizationResult
        {
            Weights = weightDict,
            Method = Method,
            Notes = $"DE heuristic | Sortino ratio: {bestScore:F3}"
        };
    }

    private static double[] BuildLowerBounds(OptimizationProblem problem, int assetCount)
    {
        var lower = new double[assetCount];
        for (int i = 0; i < assetCount; i++)
            lower[i] = problem.LowerBounds?[i] ?? (problem.AllowShort ? -1.0 : 0.0);
        return lower;
    }

    private static double[] BuildUpperBounds(OptimizationProblem problem, int assetCount)
    {
        var upper = new double[assetCount];
        for (int i = 0; i < assetCount; i++)
            upper[i] = problem.UpperBounds?[i] ?? 1.0;
        return upper;
    }

    private static double[] CreateRandomCandidate(Random rng, double[] lower, double[] upper)
    {
        int n = lower.Length;
        var candidate = new double[n];
        double sum = 0.0;
        for (int i = 0; i < n; i++)
        {
            double min = lower[i];
            double max = upper[i];
            if (double.IsNegativeInfinity(min)) min = -1.0;
            if (double.IsPositiveInfinity(max)) max = 1.0;

            double range = max - min;
            if (range <= 0)
                candidate[i] = min;
            else
                candidate[i] = min + rng.NextDouble() * range;
            sum += candidate[i];
        }

        if (Math.Abs(sum) < 1e-9)
        {
            double equal = 1.0 / n;
            for (int i = 0; i < n; i++)
                candidate[i] = equal;
        }
        else
        {
            double scale = 1.0 / sum;
            for (int i = 0; i < n; i++)
                candidate[i] *= scale;
        }

        ProjectCandidate(candidate, lower, upper);
        return candidate;
    }

    private static double[] MutateAndCrossover(Random rng, int currentIndex, double[][] population, double differentialWeight, double crossoverRate)
    {
        int n = population[currentIndex].Length;
        int size = population.Length;

        int r1, r2, r3;
        do { r1 = rng.Next(size); } while (r1 == currentIndex);
        do { r2 = rng.Next(size); } while (r2 == currentIndex || r2 == r1);
        do { r3 = rng.Next(size); } while (r3 == currentIndex || r3 == r1 || r3 == r2);

        var trial = new double[n];
        int mandatoryIndex = rng.Next(n);

        for (int j = 0; j < n; j++)
        {
            double candidateValue = population[currentIndex][j];
            if (j == mandatoryIndex || rng.NextDouble() < crossoverRate)
            {
                double mutated = population[r1][j] + differentialWeight * (population[r2][j] - population[r3][j]);
                trial[j] = double.IsNaN(mutated) ? candidateValue : mutated;
            }
            else
            {
                trial[j] = candidateValue;
            }
        }

        return trial;
    }

    private static void ProjectCandidate(double[] weights, double[] lower, double[] upper)
    {
        int n = weights.Length;
        for (int iter = 0; iter < 4; iter++)
        {
            for (int i = 0; i < n; i++)
            {
                if (!double.IsNegativeInfinity(lower[i]))
                    weights[i] = Math.Max(weights[i], lower[i]);
                if (!double.IsPositiveInfinity(upper[i]))
                    weights[i] = Math.Min(weights[i], upper[i]);
            }

            double sum = weights.Sum();
            if (Math.Abs(sum - 1.0) < 1e-6)
                break;

            if (Math.Abs(sum) < 1e-9)
            {
                double equal = 1.0 / n;
                for (int i = 0; i < n; i++)
                    weights[i] = equal;
                continue;
            }

            double scale = 1.0 / sum;
            for (int i = 0; i < n; i++)
                weights[i] *= scale;
        }

        // Final clamp to ensure bounds.
        for (int i = 0; i < n; i++)
        {
            if (!double.IsNegativeInfinity(lower[i]))
                weights[i] = Math.Max(weights[i], lower[i]);
            if (!double.IsPositiveInfinity(upper[i]))
                weights[i] = Math.Min(weights[i], upper[i]);
        }

        double finalSum = weights.Sum();
        if (Math.Abs(finalSum) < 1e-9)
        {
            double equal = 1.0 / n;
            for (int i = 0; i < n; i++)
                weights[i] = equal;
        }
        else
        {
            double scale = 1.0 / finalSum;
            for (int i = 0; i < n; i++)
                weights[i] *= scale;
        }
    }

    private static double Evaluate(double[] weights, OptimizationProblem problem)
    {
        var mu = problem.Mu;
        var scenarios = problem.ScenarioReturns ?? throw new InvalidOperationException("Scenario returns missing.");
        double expectedAnnual = Dot(mu, weights);
        double rfAnnual = problem.RiskFreeRate;
        double rfDaily = rfAnnual / 252.0;

        int scenarioCount = scenarios.GetLength(0);
        int assetCount = scenarios.GetLength(1);
        double downsideSum = 0.0;
        for (int i = 0; i < scenarioCount; i++)
        {
            double ret = 0.0;
            for (int j = 0; j < assetCount; j++)
                ret += scenarios[i, j] * weights[j];

            double downside = Math.Min(0.0, ret - rfDaily);
            downsideSum += downside * downside;
        }

        double downsideDevDaily = Math.Sqrt(downsideSum / Math.Max(1, scenarioCount));
        double downsideDevAnnual = downsideDevDaily * Math.Sqrt(252.0);
        double denom = downsideDevAnnual;
        if (denom < 1e-6)
            denom = 1e-6;

        double sortino = (expectedAnnual - rfAnnual) / denom;
        if (double.IsNaN(sortino) || double.IsInfinity(sortino))
            return -1e6;
        return sortino;
    }

    private static double Dot(double[] a, double[] b)
    {
        double sum = 0.0;
        for (int i = 0; i < a.Length; i++)
            sum += a[i] * b[i];
        return sum;
    }
}

