using ScottPlot;
using SCPBrowser.Services;
using System.Linq;
using System.Windows.Controls;

namespace SCPBrowser.Controls
{
    public partial class ProteinHistogramControl : UserControl
    {
        public ProteinHistogramControl()
        {
            InitializeComponent();
        }

        public void UpdateChart(ProteomicsData data)
        {
            ProteinChart.Plot.Clear();

            if (data == null || data.ProteinCountPerFile.Count == 0)
            {
                // Clear axis labels
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

            var barPlot = ProteinChart.Plot.Add.Bars(positions, values);

            foreach (var bar in barPlot.Bars)
            {
                bar.FillColor = ScottPlot.Color.FromHex("#2563eb");
            }

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