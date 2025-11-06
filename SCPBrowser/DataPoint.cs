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
        public double TrypsinRatio { get; set; }
        public double XScreen { get; set; }
        public double YScreen { get; set; }
        public Ellipse Visual { get; set; }
        public Color BaseColor { get; set; }
        public bool IsSelected { get; set; }
        public string PredictedCellType { get; set; }
        public CellTypeScore PredictionScore { get; set; }
    }

    public class SelectedPointData
    {
        public string RunName { get; set; }
        public int PeptideCount { get; set; }
        public double TicValue { get; set; }
        public int ProteinCount { get; set; }
        public string TrypsinRatioPercent { get; set; }
        public string CellType { get; set; }
    }
}