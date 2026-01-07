using ScottPlot;
using SCPBrowser.Services;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Controls;

namespace SCPBrowser.Controls
{
    public partial class ProteinHistogramControl : UserControl
    {
        private int _currentCutoff = 800;

        // Plate color palette
        private static readonly string[] PlateColors = new[]
        {
            "#2563eb", // Blue
            "#dc2626", // Red
            "#16a34a", // Green
            "#9333ea", // Purple
            "#ea580c", // Orange
            "#0891b2", // Cyan
            "#c026d3", // Fuchsia
            "#4f46e5", // Indigo
            "#059669", // Emerald
            "#d97706"  // Amber
        };

        public ProteinHistogramControl()
        {
            InitializeComponent();
        }

        public void UpdateChart(ProteomicsData data, int cutoff = 800, Dictionary<string, int> rawFileToPlateId = null, Dictionary<int, string> plateIdToName = null)
        {
            _currentCutoff = cutoff;
            ProteinChart.Plot.Clear();

            if (data == null || data.ProteinCountPerFile.Count == 0)
            {
                ProteinChart.Plot.Axes.Bottom.TickGenerator = new ScottPlot.TickGenerators.NumericManual(System.Array.Empty<Tick>());
                ProteinChart.Plot.Axes.Left.Label.Text = "Number of Protein Groups";
                ProteinChart.Plot.Axes.Bottom.Label.Text = "Raw File";
                ProteinChart.Refresh();
                return;
            }

            var sortedData = data.ProteinCountPerFile
                .OrderByDescending(kvp => kvp.Value)
                .ToList();

            var positions = Enumerable.Range(0, sortedData.Count).Select(i => (double)i).ToArray();
            var values = sortedData.Select(kvp => (double)kvp.Value).ToArray();
            var labels = sortedData.Select(kvp => kvp.Key).ToArray();

            // Build plate ID to color index mapping
            var plateIdToColorIndex = new Dictionary<int, int>();
            if (rawFileToPlateId != null)
            {
                var uniquePlateIds = rawFileToPlateId.Values.Distinct().OrderBy(id => id).ToList();
                for (int i = 0; i < uniquePlateIds.Count; i++)
                {
                    plateIdToColorIndex[uniquePlateIds[i]] = i % PlateColors.Length;
                }
            }

            // Draw yellow overlay rectangle (below cutoff region)
            double xMin = -0.5;
            double xMax = positions.Length - 0.5;
            var yellowOverlay = ProteinChart.Plot.Add.Rectangle(xMin, xMax, 0, cutoff);
            yellowOverlay.FillColor = ScottPlot.Color.FromHex("#fef9c3").WithAlpha(0.3);
            yellowOverlay.LineWidth = 0;

            // Draw bars
            var barPlot = ProteinChart.Plot.Add.Bars(positions, values);

            // Color bars: gray if below cutoff, plate color if above
            for (int i = 0; i < barPlot.Bars.Count; i++)
            {
                string rawFileName = labels[i];

                if (values[i] < cutoff)
                {
                    barPlot.Bars[i].FillColor = ScottPlot.Color.FromHex("#9ca3af"); // Gray
                }
                else
                {
                    // Get plate color
                    string colorHex = "#2563eb"; // Default blue
                    if (rawFileToPlateId != null && rawFileToPlateId.TryGetValue(rawFileName, out int plateId))
                    {
                        if (plateIdToColorIndex.TryGetValue(plateId, out int colorIndex))
                        {
                            colorHex = PlateColors[colorIndex];
                        }
                    }
                    barPlot.Bars[i].FillColor = ScottPlot.Color.FromHex(colorHex);
                }
            }

            // Draw cutoff line (dashed horizontal)
            var cutoffLine = ProteinChart.Plot.Add.HorizontalLine(cutoff);
            cutoffLine.Color = ScottPlot.Color.FromHex("#b45309");
            cutoffLine.LineWidth = 2;
            cutoffLine.LinePattern = LinePattern.Dashed;

            // Add plate legend
            if (plateIdToName != null && plateIdToColorIndex.Count > 0)
            {
                ProteinChart.Plot.Legend.ManualItems.Clear();

                foreach (var kvp in plateIdToColorIndex.OrderBy(x => x.Key))
                {
                    int plateId = kvp.Key;
                    int colorIndex = kvp.Value;
                    string plateName = plateIdToName.TryGetValue(plateId, out string name) ? name : $"Plate {plateId}";

                    ProteinChart.Plot.Legend.ManualItems.Add(new LegendItem
                    {
                        LabelText = plateName,
                        FillColor = ScottPlot.Color.FromHex(PlateColors[colorIndex])
                    });
                }

                ProteinChart.Plot.Legend.IsVisible = true;
                ProteinChart.Plot.Legend.Alignment = Alignment.UpperRight;
            }

            // Configure axes
            ProteinChart.Plot.Axes.Bottom.TickGenerator = new ScottPlot.TickGenerators.NumericManual(
                positions.Select((pos, idx) => new Tick(pos, labels[idx])).ToArray()
            );

            ProteinChart.Plot.Axes.Bottom.TickLabelStyle.Rotation = 45;
            ProteinChart.Plot.Axes.Bottom.TickLabelStyle.Alignment = Alignment.MiddleLeft;

            ProteinChart.Plot.Axes.Left.Label.Text = "Number of Protein Groups";
            ProteinChart.Plot.Axes.Bottom.Label.Text = "Raw File";

            ProteinChart.Plot.Axes.Margins(bottom: 0);

            ProteinChart.Refresh();
        }

        public void Clear()
        {
            ProteinChart.Plot.Clear();
            ProteinChart.Refresh();
        }
    }
}