using ScottPlot;
using SCPBrowser.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace SCPBrowser.Controls
{
    public partial class ProteinDistributionControl : UserControl
    {
        private int _binSize = 100;

        // Cached data for redraws
        private ProteomicsData _cachedData;
        private Dictionary<string, int> _cachedRawFileToPlateId;
        private Dictionary<int, string> _cachedPlateIdToName;

        // Plate color palette (same as histogram)
        private static readonly string[] PlateColors = new[]
        {
            "#2563eb", "#dc2626", "#16a34a", "#9333ea", "#ea580c",
            "#0891b2", "#c026d3", "#4f46e5", "#059669", "#d97706"
        };

        public ProteinDistributionControl()
        {
            InitializeComponent();
        }

        public void UpdateChart(ProteomicsData data, Dictionary<string, int> rawFileToPlateId = null, Dictionary<int, string> plateIdToName = null)
        {
            _cachedData = data;
            _cachedRawFileToPlateId = rawFileToPlateId;
            _cachedPlateIdToName = plateIdToName;

            DistributionChart.Plot.Clear();

            if (data == null || data.ProteinCountPerFile.Count == 0)
            {
                DistributionChart.Refresh();
                return;
            }

            // Build plate mapping
            var plateIdToColorIndex = new Dictionary<int, int>();
            if (rawFileToPlateId != null)
            {
                var uniquePlateIds = rawFileToPlateId.Values.Distinct().OrderBy(id => id).ToList();
                for (int i = 0; i < uniquePlateIds.Count; i++)
                    plateIdToColorIndex[uniquePlateIds[i]] = i % PlateColors.Length;
            }

            // Group protein counts by plate
            var plateData = new Dictionary<int, List<int>>();
            foreach (var kvp in data.ProteinCountPerFile)
            {
                int plateId = 0;
                if (rawFileToPlateId != null)
                    rawFileToPlateId.TryGetValue(kvp.Key, out plateId);

                if (!plateData.ContainsKey(plateId))
                    plateData[plateId] = new List<int>();
                plateData[plateId].Add(kvp.Value);
            }

            // Compute global bin range
            int globalMin = data.ProteinCountPerFile.Values.Min();
            int globalMax = data.ProteinCountPerFile.Values.Max();
            int binStart = (globalMin / _binSize) * _binSize;
            int binEnd = ((globalMax / _binSize) + 1) * _binSize;
            int binCount = (binEnd - binStart) / _binSize;

            double[] binCenters = new double[binCount];
            for (int i = 0; i < binCount; i++)
                binCenters[i] = binStart + i * _binSize + _binSize / 2.0;

            // Draw one curve per plate
            foreach (var kvp in plateData.OrderBy(x => x.Key))
            {
                int plateId = kvp.Key;
                var counts = kvp.Value;

                double[] histogram = new double[binCount];
                foreach (var count in counts)
                {
                    int binIndex = (count - binStart) / _binSize;
                    if (binIndex >= 0 && binIndex < binCount)
                        histogram[binIndex]++;
                }

                string colorHex = "#2563eb";
                if (plateIdToColorIndex.TryGetValue(plateId, out int colorIndex))
                    colorHex = PlateColors[colorIndex];

                var color = ScottPlot.Color.FromHex(colorHex);

                var scatter = DistributionChart.Plot.Add.ScatterLine(binCenters, histogram);
                scatter.Color = color;
                scatter.LineWidth = 2;

                // Fill under the curve with transparency
                var fill = DistributionChart.Plot.Add.FillY(binCenters, histogram, new double[binCount]);
                fill.FillColor = color.WithAlpha(0.15);
                fill.LineWidth = 0;
            }

            // Add plate legend
            if (plateIdToName != null && plateIdToColorIndex.Count > 0)
            {
                DistributionChart.Plot.Legend.ManualItems.Clear();

                foreach (var kvp in plateIdToColorIndex.OrderBy(x => x.Key))
                {
                    string plateName = plateIdToName.TryGetValue(kvp.Key, out string name) ? name : $"Plate {kvp.Key}";
                    DistributionChart.Plot.Legend.ManualItems.Add(new LegendItem
                    {
                        LabelText = plateName,
                        FillColor = ScottPlot.Color.FromHex(PlateColors[kvp.Value])
                    });
                }

                DistributionChart.Plot.Legend.IsVisible = true;
                DistributionChart.Plot.Legend.Alignment = Alignment.UpperRight;
            }

            DistributionChart.Plot.Axes.Left.Label.Text = "Number of Cells";
            DistributionChart.Plot.Axes.Bottom.Label.Text = "Protein Groups";

            DistributionChart.Refresh();
        }

        private void BinSizeTextBox_LostFocus(object sender, RoutedEventArgs e)
        {
            ApplyBinSize();
        }

        private void BinSizeTextBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                ApplyBinSize();
                e.Handled = true;
            }
        }

        private void ApplyBinSize()
        {
            if (int.TryParse(BinSizeTextBox.Text, out int newBin) && newBin > 0)
            {
                _binSize = newBin;
                if (_cachedData != null)
                    UpdateChart(_cachedData, _cachedRawFileToPlateId, _cachedPlateIdToName);
            }
            else
            {
                BinSizeTextBox.Text = _binSize.ToString();
            }
        }

        private void ResetViewButton_Click(object sender, RoutedEventArgs e)
        {
            DistributionChart.Plot.Axes.AutoScale();
            DistributionChart.Refresh();
        }

        public void Clear()
        {
            _cachedData = null;
            _cachedRawFileToPlateId = null;
            _cachedPlateIdToName = null;
            DistributionChart.Plot.Clear();
            DistributionChart.Refresh();
        }
    }
}
