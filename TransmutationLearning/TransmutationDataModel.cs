using System;
using System.Collections.Generic;
using System.Linq;

namespace TransmutationLearning
{
    /// <summary>
    /// Represents a single cell's classification result from SingleR-style output
    /// </summary>
    public class CellClassification
    {
        public string Run { get; set; }
        public Dictionary<string, double> Scores { get; set; } = new Dictionary<string, double>();
        public string Labels { get; set; }
        public double DeltaNext { get; set; }
        public string PrunedLabels { get; set; }
        
        // Computed properties
        public double MaxScore => Scores.Count > 0 ? Scores.Values.Max() : 0;
        public bool IsPruned => PrunedLabels == "NA" || string.IsNullOrEmpty(PrunedLabels);
    }

    /// <summary>
    /// Holds the complete dataset after loading and joining
    /// </summary>
    public class TransmutationDataset
    {
        // Raw data
        public List<CellClassification> Classifications { get; set; } = new List<CellClassification>();
        public Dictionary<string, Dictionary<string, double>> ProteinMatrix { get; set; } = new Dictionary<string, Dictionary<string, double>>();
        
        // Metadata
        public List<string> AllProteins { get; set; } = new List<string>();
        public List<string> AllRuns { get; set; } = new List<string>();
        public List<string> CellTypes { get; set; } = new List<string>();
        
        // Summary statistics
        public int TotalProteins => AllProteins.Count;
        public int TotalRuns => AllRuns.Count;
        public int TotalMatchedRuns => Classifications.Count(c => ProteinMatrix.ContainsKey(c.Run));
        public int UnmatchedClassifications => Classifications.Count(c => !ProteinMatrix.ContainsKey(c.Run));
        
        // Delta statistics
        public double MinDelta => Classifications.Count > 0 ? Classifications.Min(c => c.DeltaNext) : 0;
        public double MaxDelta => Classifications.Count > 0 ? Classifications.Max(c => c.DeltaNext) : 0;
        public double MedianDelta => GetMedian(Classifications.Select(c => c.DeltaNext).ToList());
        
        private double GetMedian(List<double> values)
        {
            if (values.Count == 0) return 0;
            var sorted = values.OrderBy(v => v).ToList();
            int mid = sorted.Count / 2;
            return sorted.Count % 2 == 0 
                ? (sorted[mid - 1] + sorted[mid]) / 2.0 
                : sorted[mid];
        }
        
        /// <summary>
        /// Get cell type distribution
        /// </summary>
        public Dictionary<string, int> GetCellTypeCounts()
        {
            return Classifications
                .GroupBy(c => c.Labels)
                .ToDictionary(g => g.Key, g => g.Count());
        }
        
        /// <summary>
        /// Get delta values for histogram
        /// </summary>
        public List<double> GetDeltaValues()
        {
            return Classifications.Select(c => c.DeltaNext).ToList();
        }
    }

    /// <summary>
    /// Statistics for a single cell type
    /// </summary>
    public class CellTypeStatistics
    {
        public string CellType { get; set; }
        public int TotalCount { get; set; }
        public int RetainedCount { get; set; }
        public double MedianDelta { get; set; }
        public double MinDelta { get; set; }
        public double MaxDelta { get; set; }
        public double RetentionPercent => TotalCount > 0 ? (RetainedCount * 100.0 / TotalCount) : 0;
    }

    /// <summary>
    /// Result of confidence filtering
    /// </summary>
    public class FilteredDataset
    {
        public double DeltaThreshold { get; set; }
        public List<CellClassification> RetainedCells { get; set; } = new List<CellClassification>();
        public List<CellClassification> FilteredOutCells { get; set; } = new List<CellClassification>();
        public List<CellTypeStatistics> CellTypeStats { get; set; } = new List<CellTypeStatistics>();
        
        public int TotalRetained => RetainedCells.Count;
        public int TotalFiltered => FilteredOutCells.Count;
        public double RetentionPercent => (TotalRetained + TotalFiltered) > 0 
            ? (TotalRetained * 100.0 / (TotalRetained + TotalFiltered)) : 0;
    }

    /// <summary>
    /// Statistical helper methods
    /// </summary>
    public static class StatisticsHelper
    {
        /// <summary>
        /// Otsu's method for automatic threshold selection
        /// Finds threshold that minimizes intra-class variance
        /// </summary>
        public static double ComputeOtsuThreshold(List<double> values, int numBins = 100)
        {
            if (values == null || values.Count < 2)
                return 0;

            double min = values.Min();
            double max = values.Max();
            
            if (max - min < 1e-10)
                return min;

            // Create histogram
            double binWidth = (max - min) / numBins;
            int[] histogram = new int[numBins];
            
            foreach (var v in values)
            {
                int bin = Math.Min((int)((v - min) / binWidth), numBins - 1);
                histogram[bin]++;
            }

            int total = values.Count;
            double sum = 0;
            for (int i = 0; i < numBins; i++)
                sum += i * histogram[i];

            double sumB = 0;
            int wB = 0;
            double maxVariance = 0;
            int bestThreshold = 0;

            for (int t = 0; t < numBins; t++)
            {
                wB += histogram[t];
                if (wB == 0) continue;

                int wF = total - wB;
                if (wF == 0) break;

                sumB += t * histogram[t];
                double mB = sumB / wB;
                double mF = (sum - sumB) / wF;
                double variance = wB * wF * (mB - mF) * (mB - mF);

                if (variance > maxVariance)
                {
                    maxVariance = variance;
                    bestThreshold = t;
                }
            }

            return min + (bestThreshold + 0.5) * binWidth;
        }

        /// <summary>
        /// Compute histogram bins for visualization
        /// </summary>
        public static (double[] binEdges, int[] counts) ComputeHistogram(List<double> values, int numBins = 30)
        {
            if (values == null || values.Count == 0)
                return (new double[0], new int[0]);

            double min = values.Min();
            double max = values.Max();
            
            // Add small padding to include max value
            double range = max - min;
            if (range < 1e-10)
            {
                return (new double[] { min, min + 0.1 }, new int[] { values.Count });
            }

            double binWidth = range / numBins;
            double[] binEdges = new double[numBins + 1];
            int[] counts = new int[numBins];

            for (int i = 0; i <= numBins; i++)
                binEdges[i] = min + i * binWidth;

            foreach (var v in values)
            {
                int bin = Math.Min((int)((v - min) / binWidth), numBins - 1);
                counts[bin]++;
            }

            return (binEdges, counts);
        }

        /// <summary>
        /// Get median of a list
        /// </summary>
        public static double Median(IEnumerable<double> values)
        {
            var sorted = values.OrderBy(v => v).ToList();
            if (sorted.Count == 0) return 0;
            int mid = sorted.Count / 2;
            return sorted.Count % 2 == 0 
                ? (sorted[mid - 1] + sorted[mid]) / 2.0 
                : sorted[mid];
        }
    }
}
