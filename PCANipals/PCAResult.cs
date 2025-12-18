namespace PCANipals
{
    public class PcaResult
    {
        /// <summary>
        /// Score matrix (n samples × k components)
        /// Each row is a sample projected into PC space
        /// </summary>
        public double[,] Scores { get; set; }

        /// <summary>
        /// Loading matrix (p variables × k components)
        /// Each column is a principal component direction
        /// </summary>
        public double[,] Loadings { get; set; }

        /// <summary>
        /// Proportion of variance explained by each component (length k)
        /// Values sum to less than 1 if fewer than p components extracted
        /// </summary>
        public double[] VarianceExplained { get; set; }

        /// <summary>
        /// Number of iterations for each component to converge
        /// </summary>
        public int[] Iterations { get; set; }

        /// <summary>
        /// Whether all components converged within tolerance
        /// </summary>
        public bool Converged { get; set; }
    }
}