using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
using System.Windows.Shapes;

namespace SCPBrowser
{
    [Flags]
    public enum ExclusionReason
    {
        None = 0,
        ContaminantRatio = 1,
        UncheckedCellType = 2,
        UncheckedBioCondition = 4,
        UncheckedPlate = 8,
        LassoExcluded = 16
    }

    public class DataPoint
    {
        public string RunName { get; set; }
        public int PeptideCount { get; set; }
        public double TicValue { get; set; }
        public int ProteinCount { get; set; }
        public double ContaminantRatio { get; set; }
        public double XScreen { get; set; }
        public double YScreen { get; set; }
        public double XData { get; set; }
        public double YData { get; set; }
        public Ellipse Visual { get; set; }
        public Color BaseColor { get; set; }
        public bool IsSelected { get; set; }
        public string PredictedCellType { get; set; }
        public CellTypeScore PredictionScore { get; set; }
        public string BiologicalCondition { get; set; }
        public string PlateName { get; set; }

        /// <summary>k-means cluster index (0-based) for the most recent clustering, or -1 if unassigned.</summary>
        public int ClusterId { get; set; } = -1;

        /// <summary>Human-readable k-means cluster label (e.g. "Cluster 3"), null if unassigned. Drives the
        /// categorical coloring/pie path exactly like PredictedCellType / BiologicalCondition / PlateName.</summary>
        public string ClusterLabel { get; set; }
        
        /// <summary>
        /// Full prediction result containing scores for all cell types (for detailed tooltip)
        /// </summary>
        public CellTypePredictionResult FullPrediction { get; set; }

        /// <summary>
        /// Flags indicating why this point is excluded/greyed. None = included.
        /// </summary>
        public ExclusionReason ExclusionReasons { get; set; } = ExclusionReason.None;

        /// <summary>
        /// Human-readable exclusion detail (e.g., "Contaminant ratio 87% > 30% cutoff")
        /// </summary>
        public string ExclusionDetail { get; set; }
    }

    public class SelectedPointData
    {
        public int RawFileId { get; set; }
        public string RunName { get; set; }
        public int PeptideCount { get; set; }
        public double TicValue { get; set; }
        public int ProteinCount { get; set; }
        public string ContaminantRatioPercent { get; set; }
        public string CellType { get; set; }
        public Brush CellTypeBrush { get; set; } = Brushes.Black;
        public string BiologicalCondition { get; set; }
        public string CompositeScore { get; set; }
        public bool IsIncluded { get; set; } = true;
        public string ExclusionReason { get; set; }

        /// <summary>k-means cluster label (e.g. "Cluster 3"), shown in the Selected Points grid.</summary>
        public string ClusterLabel { get; set; }

        /// <summary>Numeric cluster index (0-based, -1 = none) — the sort key for the "Cluster" column so it orders
        /// numerically (Cluster 2 before Cluster 10) rather than lexicographically.</summary>
        public int ClusterId { get; set; } = -1;

        /// <summary>Brush matching the cluster's scatter colour, for the Selected Points "Cluster" column.</summary>
        public Brush ClusterBrush { get; set; } = Brushes.Black;
    }
}