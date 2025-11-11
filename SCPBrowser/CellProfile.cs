using System.Collections.Generic;

namespace SCPBrowser
{
    /// <summary>
    /// Represents an aggregated gene expression profile for a specific cell type
    /// </summary>
    public class CellTypeProfile
    {
        /// <summary>
        /// The name of the cell type (e.g., "Neuron", "T Cell")
        /// </summary>
        public string CellType { get; set; }

        /// <summary>
        /// Number of individual cells that contributed to this profile
        /// </summary>
        public int CellCount { get; set; }

        /// <summary>
        /// Gene expression profile: gene name -> median expression value
        /// Only stores genes that are expressed (non-zero) in this cell type
        /// </summary>
        public Dictionary<string, double> MedianExpression { get; set; } = new Dictionary<string, double>();

        /// <summary>
        /// Percentage of cells expressing each gene: gene name -> percentage (0-1)
        /// </summary>
        public Dictionary<string, double> PercentExpressing { get; set; } = new Dictionary<string, double>();

        /// <summary>
        /// Optional: Mean expression (less robust than median but useful for some analyses)
        /// </summary>
        public Dictionary<string, double> MeanExpression { get; set; } = new Dictionary<string, double>();

        /// <summary>
        /// Total number of unique genes expressed in this cell type
        /// </summary>
        public int TotalGenesExpressed => MedianExpression.Count;
    }

    /// <summary>
    /// Metadata about aggregation of cell types
    /// </summary>
    public class CellTypeMetadata
    {
        public string CellType { get; set; }
        public int CellCount { get; set; }
        public int GenesExpressed { get; set; }
        public string BatchInfo { get; set; }
        public string AgeRange { get; set; }
    }
}