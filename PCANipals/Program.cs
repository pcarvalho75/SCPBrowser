namespace PCANipals
{
    internal class Program
    {
        static void Main(string[] args)
        {
            Console.WriteLine("=== PCA NIPALS Tests ===\n");

            TestBasicPca();
            TestWithMissingValues();
            TestIrisLikeData();

            Console.WriteLine("\nAll tests completed. Press any key to exit.");
            Console.ReadKey();
        }

        static void TestBasicPca()
        {
            Console.WriteLine("--- Test 1: Basic PCA (no missing values) ---");

            // Simple 2D data with clear principal direction
            double[,] data = new double[,]
            {
                { 2.5, 2.4 },
                { 0.5, 0.7 },
                { 2.2, 2.9 },
                { 1.9, 2.2 },
                { 3.1, 3.0 },
                { 2.3, 2.7 },
                { 2.0, 1.6 },
                { 1.0, 1.1 },
                { 1.5, 1.6 },
                { 1.1, 0.9 }
            };

            var result = NipalsAlgorithm.Compute(data, nComponents: 2);

            Console.WriteLine($"Converged: {result.Converged}");
            Console.WriteLine($"Iterations: [{string.Join(", ", result.Iterations)}]");
            Console.WriteLine($"Variance Explained: [{string.Join(", ", result.VarianceExplained.Select(v => v.ToString("P1")))}]");
            Console.WriteLine($"Total Variance: {result.VarianceExplained.Sum():P1}");

            Console.WriteLine("\nScores (first 5 samples):");
            for (int i = 0; i < Math.Min(5, result.Scores.GetLength(0)); i++)
            {
                Console.WriteLine($"  Sample {i}: PC1={result.Scores[i, 0]:F4}, PC2={result.Scores[i, 1]:F4}");
            }

            Console.WriteLine("\nLoadings:");
            for (int j = 0; j < result.Loadings.GetLength(0); j++)
            {
                Console.WriteLine($"  Var {j}: PC1={result.Loadings[j, 0]:F4}, PC2={result.Loadings[j, 1]:F4}");
            }
            Console.WriteLine();
        }

        static void TestWithMissingValues()
        {
            Console.WriteLine("--- Test 2: PCA with Missing Values (NaN) ---");

            double[,] data = new double[,]
            {
        { 2.5, 2.4 },
        { 0.5, double.NaN },  // missing
        { 2.2, 2.9 },
        { double.NaN, 2.2 }, // missing
        { 3.1, 3.0 },
        { 2.3, 2.7 },
        { 2.0, double.NaN }, // missing
        { 1.0, 1.1 },
        { 1.5, 1.6 },
        { 1.1, 0.9 }
            };

            var result = NipalsAlgorithm.Compute(data, nComponents: 2);

            Console.WriteLine($"Converged: {result.Converged}");
            Console.WriteLine($"Iterations: [{string.Join(", ", result.Iterations)}]");
            Console.WriteLine($"Variance Explained: [{string.Join(", ", result.VarianceExplained.Select(v => v.ToString("P1")))}]");

            Console.WriteLine($"\nPreprocessing - Means: [{string.Join(", ", result.Means.Select(m => m.ToString("F3")))}]");
            Console.WriteLine($"Preprocessing - Stds:  [{string.Join(", ", result.StandardDeviations.Select(s => s.ToString("F3")))}]");

            Console.WriteLine("\nSupport per sample:");
            for (int i = 0; i < result.SupportPerSample.Length; i++)
            {
                string flag = result.SupportPerSample[i] < result.MinimumSupport ? " (LOW)" : "";
                Console.WriteLine($"  Sample {i}: {result.SupportPerSample[i]} observed{flag}");
            }

            Console.WriteLine("\nScores:");
            for (int i = 0; i < result.Scores.GetLength(0); i++)
            {
                Console.WriteLine($"  Sample {i}: PC1={result.Scores[i, 0]:F4}, PC2={result.Scores[i, 1]:F4}");
            }

            // Test Transform on new data
            Console.WriteLine("\n--- Testing Transform on new sample ---");
            double[,] newSample = new double[,] { { 2.0, 2.0 } };
            var projected = result.Transform(newSample);
            Console.WriteLine($"New sample [2.0, 2.0] projected: PC1={projected[0, 0]:F4}, PC2={projected[0, 1]:F4}");

            Console.WriteLine();
        }

        static void TestIrisLikeData()
        {
            Console.WriteLine("--- Test 3: Larger Matrix (simulated proteomics-like) ---");

            // Simulate: 20 runs × 50 proteins
            int nSamples = 20;
            int nProteins = 50;
            var random = new Random(42);

            double[,] data = new double[nSamples, nProteins];

            // Create data with 2 underlying factors + noise
            for (int i = 0; i < nSamples; i++)
            {
                double factor1 = random.NextDouble() * 10;
                double factor2 = random.NextDouble() * 5;

                for (int j = 0; j < nProteins; j++)
                {
                    // First 25 proteins load on factor1, rest on factor2
                    if (j < 25)
                        data[i, j] = factor1 * (0.8 + 0.4 * random.NextDouble()) + random.NextDouble();
                    else
                        data[i, j] = factor2 * (0.8 + 0.4 * random.NextDouble()) + random.NextDouble();
                }
            }

            // Add 10% missing values
            int missingCount = 0;
            for (int i = 0; i < nSamples; i++)
            {
                for (int j = 0; j < nProteins; j++)
                {
                    if (random.NextDouble() < 0.10)
                    {
                        data[i, j] = double.NaN;
                        missingCount++;
                    }
                }
            }

            Console.WriteLine($"Matrix size: {nSamples} samples × {nProteins} proteins");
            Console.WriteLine($"Missing values: {missingCount} ({100.0 * missingCount / (nSamples * nProteins):F1}%)");

            var result = NipalsAlgorithm.Compute(data, nComponents: 5);

            Console.WriteLine($"Converged: {result.Converged}");
            Console.WriteLine($"Iterations: [{string.Join(", ", result.Iterations)}]");
            Console.WriteLine($"Variance Explained:");
            double cumulative = 0;
            for (int i = 0; i < result.VarianceExplained.Length; i++)
            {
                cumulative += result.VarianceExplained[i];
                Console.WriteLine($"  PC{i + 1}: {result.VarianceExplained[i]:P1} (cumulative: {cumulative:P1})");
            }
            Console.WriteLine();
        }
    }
}