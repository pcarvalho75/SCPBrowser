using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
using System.Windows.Shapes;

namespace SCPBrowser
{
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
        
        /// <summary>
        /// Full prediction result containing scores for all cell types (for detailed tooltip)
        /// </summary>
        public CellTypePredictionResult FullPrediction { get; set; }
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
        public string BiologicalCondition { get; set; }
        public string CompositeScore { get; set; }
        public bool IsIncluded { get; set; } = true;
    }
}