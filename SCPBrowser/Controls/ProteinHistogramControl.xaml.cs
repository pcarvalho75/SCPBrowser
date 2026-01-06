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

        public ProteinHistogramControl()
        {
            InitializeComponent();
        }

        public void UpdateChart(ProteomicsData data, int cutoff = 800)
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

            // Draw yellow overlay rectangle (below cutoff region)
            double xMin = -0.5;
            double xMax = positions.Length - 0.5;
            var yellowOverlay = ProteinChart.Plot.Add.Rectangle(xMin, xMax, 0, cutoff);
            yellowOverlay.FillColor = ScottPlot.Color.FromHex("#fef9c3").WithAlpha(0.3);
            yellowOverlay.LineWidth = 0;

            // Draw bars
            var barPlot = ProteinChart.Plot.Add.Bars(positions, values);

            // Color bars: gray if below cutoff, blue if above
            for (int i = 0; i < barPlot.Bars.Count; i++)
            {
                if (values[i] < cutoff)
                {
                    barPlot.Bars[i].FillColor = ScottPlot.Color.FromHex("#9ca3af"); // Gray
                }
                else
                {
                    barPlot.Bars[i].FillColor = ScottPlot.Color.FromHex("#2563eb"); // Blue
                }
            }

            // Draw cutoff line (dashed horizontal)
            var cutoffLine = ProteinChart.Plot.Add.HorizontalLine(cutoff);
            cutoffLine.Color = ScottPlot.Color.FromHex("#b45309");
            cutoffLine.LineWidth = 2;
            cutoffLine.LinePattern = LinePattern.Dashed;

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