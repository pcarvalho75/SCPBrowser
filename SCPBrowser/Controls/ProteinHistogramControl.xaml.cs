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
    public partial class ProteinHistogramControl : UserControl
    {
        private int _currentCutoff = 800;
        private int _currentUpperCutoff = 99999;
        private bool _lineChartMode = false;

        // Tooltip and bar tracking data
        private double[] _barPositions;
        private string[] _barLabels;
        private double[] _barValues;
        private ToolTip _barToolTip;

        // Cached data for redraws
        private ProteomicsData _cachedData;
        private Dictionary<string, int> _cachedRawFileToPlateId;
        private Dictionary<int, string> _cachedPlateIdToName;
        private HashSet<string> _excludedRuns = new HashSet<string>();

        // Events
        public event EventHandler<string> RunExcludeRequested;
        public event EventHandler<string> RunRestoreRequested;
        public event EventHandler ClearExclusionsRequested;

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

            _barToolTip = new ToolTip { IsOpen = false };
            ProteinChart.ToolTip = _barToolTip;
            ProteinChart.MouseMove += ProteinChart_MouseMove;
            ProteinChart.MouseLeave += ProteinChart_MouseLeave;
            ProteinChart.MouseRightButtonUp += ProteinChart_MouseRightButtonUp;
        }

        public void SetExcludedRuns(HashSet<string> excludedRuns)
        {
            _excludedRuns = excludedRuns ?? new HashSet<string>();
            ClearExclusionsButton.Visibility = _excludedRuns.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        private void ProteinChart_MouseMove(object sender, MouseEventArgs e)
        {
            if (_barPositions == null || _barLabels == null || _barValues == null)
                return;

            var pos = e.GetPosition(ProteinChart);
            Pixel pixel = new Pixel((float)pos.X, (float)pos.Y);
            Coordinates coords = ProteinChart.Plot.GetCoordinates(pixel);

            int barIndex = (int)Math.Round(coords.X);
            if (barIndex >= 0 && barIndex < _barLabels.Length && coords.Y >= 0)
            {
                string status = "";
                if (_excludedRuns.Contains(_barLabels[barIndex]))
                    status = " [EXCLUDED]";
                else if (_barValues[barIndex] < _currentCutoff)
                    status = " [below min]";
                else if (_barValues[barIndex] > _currentUpperCutoff)
                    status = " [above max]";

                _barToolTip.Content = $"{_barLabels[barIndex]}\nProteins: {_barValues[barIndex]:N0}{status}";
                _barToolTip.IsOpen = true;
            }
            else
            {
                _barToolTip.IsOpen = false;
            }
        }

        private void ProteinChart_MouseLeave(object sender, MouseEventArgs e)
        {
            _barToolTip.IsOpen = false;
        }

        private void ProteinChart_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (_barPositions == null || _barLabels == null || _barValues == null)
                return;

            var pos = e.GetPosition(ProteinChart);
            Pixel pixel = new Pixel((float)pos.X, (float)pos.Y);
            Coordinates coords = ProteinChart.Plot.GetCoordinates(pixel);

            int barIndex = (int)Math.Round(coords.X);
            if (barIndex < 0 || barIndex >= _barLabels.Length || coords.Y < 0)
                return;

            string rawFileName = _barLabels[barIndex];
            bool isExcluded = _excludedRuns.Contains(rawFileName);

            var menu = new ContextMenu();

            if (isExcluded)
            {
                var restoreItem = new MenuItem { Header = $"Restore \"{rawFileName}\"" };
                restoreItem.Click += (s, args) => RunRestoreRequested?.Invoke(this, rawFileName);
                menu.Items.Add(restoreItem);
            }
            else
            {
                var excludeItem = new MenuItem { Header = $"Exclude \"{rawFileName}\"" };
                excludeItem.Click += (s, args) => RunExcludeRequested?.Invoke(this, rawFileName);
                menu.Items.Add(excludeItem);
            }

            menu.IsOpen = true;
            e.Handled = true;
        }

        private void LineChartToggle_Click(object sender, RoutedEventArgs e)
        {
            _lineChartMode = LineChartToggle.IsChecked == true;
            if (_cachedData != null)
            {
                UpdateChart(_cachedData, _currentCutoff, _cachedRawFileToPlateId, _cachedPlateIdToName, _currentUpperCutoff);
            }
        }

        private void ResetViewButton_Click(object sender, RoutedEventArgs e)
        {
            ProteinChart.Plot.Axes.AutoScale();
            ProteinChart.Refresh();
        }

        private void ClearExclusionsButton_Click(object sender, RoutedEventArgs e)
        {
            ClearExclusionsRequested?.Invoke(this, EventArgs.Empty);
        }

        public void UpdateChart(ProteomicsData data, int cutoff = 800, Dictionary<string, int> rawFileToPlateId = null, Dictionary<int, string> plateIdToName = null, int upperCutoff = 99999)
        {
            _currentCutoff = cutoff;
            _currentUpperCutoff = upperCutoff;
            _cachedData = data;
            _cachedRawFileToPlateId = rawFileToPlateId;
            _cachedPlateIdToName = plateIdToName;

            ProteinChart.Plot.Clear();

            if (data == null || data.ProteinCountPerFile.Count == 0)
            {
                _barPositions = null;
                _barLabels = null;
                _barValues = null;
                ProteinChart.Plot.Axes.Bottom.TickGenerator = new ScottPlot.TickGenerators.NumericManual(Array.Empty<Tick>());
                ProteinChart.Plot.Axes.Left.Label.Text = "Number of Protein Groups";
                ProteinChart.Plot.Axes.Bottom.Label.Text = "Raw File";
                ProteinChart.Refresh();
                return;
            }

            var sortedData = data.ProteinCountPerFile
                .OrderByDescending(kvp => kvp.Value)
                .ToList();

            _barPositions = Enumerable.Range(0, sortedData.Count).Select(i => (double)i).ToArray();
            _barValues = sortedData.Select(kvp => (double)kvp.Value).ToArray();
            _barLabels = sortedData.Select(kvp => kvp.Key).ToArray();

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

            // Show/hide line chart toggle based on plate count
            LineChartToggle.Visibility = plateIdToColorIndex.Count >= 2 ? Visibility.Visible : Visibility.Collapsed;
            if (plateIdToColorIndex.Count < 2)
                _lineChartMode = false;

            double xMin = -0.5;
            double xMax = _barPositions.Length - 0.5;

            // Draw yellow overlay rectangle (below lower cutoff region)
            var lowerOverlay = ProteinChart.Plot.Add.Rectangle(xMin, xMax, 0, cutoff);
            lowerOverlay.FillColor = ScottPlot.Color.FromHex("#fef9c3").WithAlpha(0.3);
            lowerOverlay.LineWidth = 0;

            // Draw red overlay rectangle (above upper cutoff region)
            if (upperCutoff < 99999)
            {
                double maxValue = _barValues.Max();
                var upperOverlay = ProteinChart.Plot.Add.Rectangle(xMin, xMax, upperCutoff, maxValue * 1.1);
                upperOverlay.FillColor = ScottPlot.Color.FromHex("#fee2e2").WithAlpha(0.3);
                upperOverlay.LineWidth = 0;
            }

            if (_lineChartMode && plateIdToColorIndex.Count >= 2)
            {
                DrawLineChart(rawFileToPlateId, plateIdToName, plateIdToColorIndex, cutoff, upperCutoff);
            }
            else
            {
                DrawBarChart(rawFileToPlateId, plateIdToColorIndex, cutoff, upperCutoff);
            }

            // Draw lower cutoff line (dashed, amber)
            var cutoffLine = ProteinChart.Plot.Add.HorizontalLine(cutoff);
            cutoffLine.Color = ScottPlot.Color.FromHex("#b45309");
            cutoffLine.LineWidth = 2;
            cutoffLine.LinePattern = LinePattern.Dashed;

            // Draw upper cutoff line (dashed, red)
            if (upperCutoff < 99999)
            {
                var upperLine = ProteinChart.Plot.Add.HorizontalLine(upperCutoff);
                upperLine.Color = ScottPlot.Color.FromHex("#dc2626");
                upperLine.LineWidth = 2;
                upperLine.LinePattern = LinePattern.Dashed;
            }

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
                _barPositions.Select((pos, idx) => new Tick(pos, _barLabels[idx])).ToArray()
            );

            ProteinChart.Plot.Axes.Bottom.TickLabelStyle.Rotation = 45;
            ProteinChart.Plot.Axes.Bottom.TickLabelStyle.Alignment = Alignment.MiddleLeft;

            ProteinChart.Plot.Axes.Left.Label.Text = "Number of Protein Groups";
            ProteinChart.Plot.Axes.Bottom.Label.Text = "Raw File";

            ProteinChart.Plot.Axes.Margins(bottom: 0);

            ProteinChart.Refresh();
        }

        private void DrawBarChart(Dictionary<string, int> rawFileToPlateId, Dictionary<int, int> plateIdToColorIndex, int cutoff, int upperCutoff)
        {
            var barPlot = ProteinChart.Plot.Add.Bars(_barPositions, _barValues);

            for (int i = 0; i < barPlot.Bars.Count; i++)
            {
                string rawFileName = _barLabels[i];

                if (_excludedRuns.Contains(rawFileName))
                {
                    // Manually excluded: dark gray with low opacity
                    barPlot.Bars[i].FillColor = ScottPlot.Color.FromHex("#6b7280").WithAlpha(0.4);
                    barPlot.Bars[i].LineColor = ScottPlot.Color.FromHex("#374151");
                    barPlot.Bars[i].LineWidth = 1.5f;
                }
                else if (_barValues[i] < cutoff)
                {
                    barPlot.Bars[i].FillColor = ScottPlot.Color.FromHex("#9ca3af");
                }
                else if (_barValues[i] > upperCutoff)
                {
                    barPlot.Bars[i].FillColor = ScottPlot.Color.FromHex("#f87171");
                }
                else
                {
                    string colorHex = "#2563eb";
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
        }

        private void DrawLineChart(Dictionary<string, int> rawFileToPlateId, Dictionary<int, string> plateIdToName, Dictionary<int, int> plateIdToColorIndex, int cutoff, int upperCutoff)
        {
            if (rawFileToPlateId == null)
                return;

            // Group bars by plate, each plate gets its own line
            var plateGroups = new Dictionary<int, List<(double x, double y)>>();

            for (int i = 0; i < _barLabels.Length; i++)
            {
                if (rawFileToPlateId.TryGetValue(_barLabels[i], out int plateId))
                {
                    if (!plateGroups.ContainsKey(plateId))
                        plateGroups[plateId] = new List<(double x, double y)>();

                    plateGroups[plateId].Add((_barPositions[i], _barValues[i]));
                }
            }

            foreach (var kvp in plateGroups.OrderBy(x => x.Key))
            {
                int plateId = kvp.Key;
                var points = kvp.Value;

                double[] xs = points.Select(p => p.x).ToArray();
                double[] ys = points.Select(p => p.y).ToArray();

                string colorHex = "#2563eb";
                if (plateIdToColorIndex.TryGetValue(plateId, out int colorIndex))
                    colorHex = PlateColors[colorIndex];

                var scatter = ProteinChart.Plot.Add.ScatterLine(xs, ys);
                scatter.Color = ScottPlot.Color.FromHex(colorHex);
                scatter.LineWidth = 2;
            }
        }

        public void Clear()
        {
            _barPositions = null;
            _barLabels = null;
            _barValues = null;
            _cachedData = null;
            _cachedRawFileToPlateId = null;
            _cachedPlateIdToName = null;
            _excludedRuns = new HashSet<string>();
            ClearExclusionsButton.Visibility = Visibility.Collapsed;
            ProteinChart.Plot.Clear();
            ProteinChart.Refresh();
        }
    }
}
