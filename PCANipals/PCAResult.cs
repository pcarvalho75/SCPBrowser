namespace PCANipals
{
    /// <summary>
    /// Result of PCA computation via NIPALS algorithm.
    /// </summary>
    public class PcaResult
    {
        /// <summary>
        /// Score matrix (n samples × k components).
        /// Each row is a sample projected into PC space.
        /// </summary>
        public double[,] Scores { get; set; }

        /// <summary>
        /// Loading matrix (p variables × k components).
        /// Each column is a principal component direction.
        /// </summary>
        public double[,] Loadings { get; set; }

        /// <summary>
        /// Proportion of variance explained by each component (length k).
        /// Computed as reduction in residual SS over observed entries.
        /// </summary>
        public double[] VarianceExplained { get; set; }

        /// <summary>
        /// Number of iterations for each component to converge.
        /// </summary>
        public int[] Iterations { get; set; }

        /// <summary>
        /// Whether all components converged within tolerance.
        /// </summary>
        public bool Converged { get; set; }

        /// <summary>
        /// Column means used for centering (length p).
        /// Needed for projecting new samples consistently.
        /// </summary>
        public double[] Means { get; set; }

        /// <summary>
        /// Column standard deviations used for scaling (length p).
        /// All ones if scale=false was used.
        /// </summary>
        public double[] StandardDeviations { get; set; }

        /// <summary>
        /// Number of observed (non-NaN) values per sample (length n).
        /// Low values indicate unreliable scores.
        /// </summary>
        public int[] SupportPerSample { get; set; }

        /// <summary>
        /// Number of observed (non-NaN) values per variable (length p).
        /// Low values indicate unreliable loadings.
        /// </summary>
        public int[] SupportPerVariable { get; set; }

        /// <summary>
        /// Minimum support threshold used during computation.
        /// </summary>
        public int MinimumSupport { get; set; }
        public int[,] SupportPerScore { get; internal set; }
        public bool WasCentered { get; internal set; }
        public bool WasScaled { get; internal set; }
        public int ExtractedComponents { get; internal set; }
        public double InitialSse { get; internal set; }
        public double ResidualSse { get; internal set; }

        /// <summary>
        /// Projects new data using the learned loadings.
        /// Applies same centering/scaling as training data.
        /// </summary>
        /// <param name="newData">New data matrix (m samples × p variables). NaN allowed.</param>
        /// <returns>Projected scores (m samples × k components).</returns>
        public double[,] Transform(double[,] newData)
        {
            int m = newData.GetLength(0);
            int p = newData.GetLength(1);
            int k = Loadings.GetLength(1);

            if (p != Loadings.GetLength(0))
                throw new ArgumentException($"New data has {p} variables but model expects {Loadings.GetLength(0)}");

            double[,] scores = new double[m, k];

            for (int i = 0; i < m; i++)
            {
                for (int h = 0; h < k; h++)
                {
                    double num = 0;
                    double denom = 0;

                    for (int j = 0; j < p; j++)
                    {
                        if (!double.IsNaN(newData[i, j]))
                        {
                            double centered = (newData[i, j] - Means[j]) / StandardDeviations[j];
                            num += centered * Loadings[j, h];
                            denom += Loadings[j, h] * Loadings[j, h];
                        }
                    }

                    scores[i, h] = denom > 1e-10 ? num / denom : 0;
                }
            }

            return scores;
        }
    }
}