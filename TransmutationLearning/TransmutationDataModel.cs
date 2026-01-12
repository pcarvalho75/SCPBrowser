using System;
using System.Collections.Generic;
using System.Linq;

namespace TransmutationLearning
{
    /// <summary>
    /// Represents a single cell/run classification result from transcriptomic-based prediction
    /// </summary>
    public class CellClassification
    {
        public string RunName { get; set; } = string.Empty;
        public Dictionary<string, double> Scores { get; set; } = new Dictionary<string, double>();
        public string Label { get; set; } = string.Empty;
        public double DeltaNext { get; set; }
        public string PrunedLabel { get; set; } = string.Empty;

        /// <summary>
        /// The highest score among all cell types
        /// </summary>
        public double MaxScore => Scores.Count > 0 ? Scores.Values.Max() : 0;
    }

    /// <summary>
    /// Container for joined proteomic + classification data
    /// </summary>
    public class TransmutationDataset
    {
        public Dictionary<string, CellClassification> Classifications { get; set; } = new Dictionary<string, CellClassification>();

        /// <summary>
        /// Cell types found in the classification file
        /// </summary>
        public List<string> CellTypes { get; set; } = new List<string>();

        /// <summary>
        /// Protein quantification matrix: Protein -> (Run -> Intensity)
        /// </summary>
        public Dictionary<string, Dictionary<string, double>> ProteinMatrix { get; set; } = new Dictionary<string, Dictionary<string, double>>();

        /// <summary>
        /// Protein to Gene mapping
        /// </summary>
        public Dictionary<string, string> ProteinToGeneMap { get; set; } = new Dictionary<string, string>();

        /// <summary>
        /// Runs that exist in both proteomic and classification data
        /// </summary>
        public HashSet<string> MatchedRuns { get; set; } = new HashSet<string>();

        /// <summary>
        /// Runs in classification file but not in proteomic data
        /// </summary>
        public HashSet<string> UnmatchedClassificationRuns { get; set; } = new HashSet<string>();

        /// <summary>
        /// Runs in proteomic data but not in classification file
        /// </summary>
        public HashSet<string> UnmatchedProteomicRuns { get; set; } = new HashSet<string>();

        public int TotalProteins => ProteinMatrix.Count;
        public int TotalMatchedRuns => MatchedRuns.Count;

        /// <summary>
        /// Gets count of runs per cell type (using Label)
        /// </summary>
        public Dictionary<string, int> GetCellTypeCounts()
        {
            var counts = new Dictionary<string, int>();
            foreach (var cellType in CellTypes)
            {
                counts[cellType] = 0;
            }

            foreach (var run in MatchedRuns)
            {
                if (Classifications.TryGetValue(run, out var classification))
                {
                    var label = classification.Label;
                    if (counts.ContainsKey(label))
                        counts[label]++;
                    else
                        counts[label] = 1;
                }
            }

            return counts;
        }
    }

    /// <summary>
    /// Result of confidence-based filtering
    /// </summary>
    public class FilteredDataset
    {
        public TransmutationDataset? SourceDataset { get; set; }
        public double DeltaThreshold { get; set; }
        public double? MinScoreThreshold { get; set; }

        /// <summary>
        /// Runs that passed the confidence filter
        /// </summary>
        public HashSet<string> RetainedRuns { get; set; } = new HashSet<string>();

        /// <summary>
        /// Count of retained runs per cell type
        /// </summary>
        public Dictionary<string, int> RetainedPerCellType { get; set; } = new Dictionary<string, int>();
    }
}
