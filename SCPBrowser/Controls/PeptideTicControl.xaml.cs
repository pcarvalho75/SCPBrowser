using SCPBrowser.GOTools;
using SCPBrowser.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace SCPBrowser
{
    public partial class PeptideTicControl : UserControl
    {
        private ProteomicsData _currentData;
        private bool _isInitialized = false;
        private Dictionary<string, CellTypePredictionResult> _cellTypePredictions;
        private Dictionary<string, Color> _cellTypeColorMap;
        private Dictionary<string, RunGoEnrichmentResult> _goEnrichmentResults;
        private Dictionary<string, Color> _goTermColorMap;
        private List<DataPoint> _currentSelectedPoints = new List<DataPoint>();
        private HashSet<string> _checkedBioConditions = new HashSet<string>();
        // Add this new event
        public event EventHandler CellTypePredictionsRequested;

        public PeptideTicControl()
        {
            InitializeComponent();

            ScatterPlot.SelectionChanged += ScatterPlot_SelectionChanged;
            ScatterPlot.PointClicked += ScatterPlot_PointClicked;
            SelectedPointsGridPanel.GridSelectionChanged += SelectedPointsGridPanel_GridSelectionChanged;

            _isInitialized = true;
        }

        public void UpdateChart(ProteomicsData data)
        {
            _currentData = data;
            RefreshChart();
        }

        public void SetImageBaseDirectory(string directory)
        {
            RunDetailPanel.SetImageBaseDirectory(directory);
        }

        public void SetCellTypePredictions(Dictionary<string, CellTypePredictionResult> predictions, Dictionary<string, Color> colorMap)
        {
            _cellTypePredictions = predictions;
            _cellTypeColorMap = colorMap;
            ColorByCellTypeRadio.IsEnabled = predictions != null && predictions.Count > 0;

            if (_currentData != null)
            {
                // If cell type mode is selected, populate the checkboxes now that we have the color map
                if (ColorByCellTypeRadio.IsChecked == true && _cellTypeColorMap != null && _cellTypeColorMap.Count > 0)
                {
                    PopulateCellTypeCheckboxes();
                    BioConditionPanel.Visibility = Visibility.Visible;
                }

                RefreshChart();
            }
        }

        public void EnableCellTypeClassification(bool isAvailable)
        {
            ColorByCellTypeRadio.IsEnabled = isAvailable;
        }

        public void SetGoEnrichmentResults(Dictionary<string, RunGoEnrichmentResult> results, Dictionary<string, Color> colorMap)
        {
            _goEnrichmentResults = results;
            _goTermColorMap = colorMap;
            ColorByBioConditionRadio.IsEnabled = results != null && results.Count > 0;

            // Update GO tab if we have selected points
            if (_currentSelectedPoints.Count > 0)
            {
                GoEnrichmentTab.UpdateGoEnrichment(_currentSelectedPoints, _goEnrichmentResults);
            }
        }

        private Dictionary<string, Color> GenerateBioConditionColorMap()
        {
            if (_currentData == null || _currentData.BiologicalConditionPerFile.Count == 0)
                return new Dictionary<string, Color>();

            var uniqueConditions = _currentData.BiologicalConditionPerFile.Values
                .Where(c => !string.IsNullOrEmpty(c))
                .Distinct()
                .OrderBy(c => c)
                .ToList();

            var colorMap = new Dictionary<string, Color>();
            for (int i = 0; i < uniqueConditions.Count; i++)
            {
                // Simple hue-based color generation
                double hue = (double)i / uniqueConditions.Count * 360.0;
                colorMap[uniqueConditions[i]] = HsvToRgb(hue, 0.7, 0.9);
            }
            return colorMap;
        }

        // Helper to convert HSV to
        private Color HsvToRgb(double h, double s, double v)
        {
            double c = v * s;
            double x = c * (1 - Math.Abs((h / 60.0) % 2 - 1));
            double m = v - c;

            double r, g, b;

            if (h < 60) { r = c; g = x; b = 0; }
            else if (h < 120) { r = x; g = c; b = 0; }
            else if (h < 180) { r = 0; g = c; b = x; }
            else if (h < 240) { r = 0; g = x; b = c; }
            else if (h < 300) { r = x; g = 0; b = c; }
            else { r = c; g = 0; b = x; }

            return Color.FromRgb(
                (byte)((r + m) * 255),
                (byte)((g + m) * 255),
                (byte)((b + m) * 255)
            );
        }

        public void EnableBioConditionClassification(bool isAvailable)
        {
            ColorByBioConditionRadio.IsEnabled = isAvailable;
        }

        private async void ColorMode_Changed(object sender, RoutedEventArgs e)
        {
            if (!_isInitialized || ColorByCellTypeRadio == null)
                return;

            Console.WriteLine($"ColorMode_Changed fired");
            Console.WriteLine($"  ColorByBioConditionRadio.IsChecked: {ColorByBioConditionRadio.IsChecked}");
            Console.WriteLine($"  ColorByCellTypeRadio.IsChecked: {ColorByCellTypeRadio.IsChecked}");
            Console.WriteLine($"  ColorByTrypsinRadio.IsChecked: {ColorByTrypsinRadio.IsChecked}");

            // Show/hide panel and populate checkboxes based on selection
            if (ColorByCellTypeRadio.IsChecked == true)
            {
                Console.WriteLine($"  -> Entering cell type branch");

                // If we don't have predictions yet, request them first
                if (_cellTypePredictions == null || _cellTypePredictions.Count == 0)
                {
                    CellTypePredictionsRequested?.Invoke(this, EventArgs.Empty);
                    return;
                }

                PopulateCellTypeCheckboxes();
                BioConditionPanel.Visibility = Visibility.Visible;
            }
            else if (ColorByBioConditionRadio.IsChecked == true)
            {
                Console.WriteLine($"  -> Entering bio condition branch");
                PopulateBioConditionCheckboxes();
                BioConditionPanel.Visibility = Visibility.Visible;
            }
            else
            {
                BioConditionPanel.Visibility = Visibility.Collapsed;
            }

            if (_currentData != null)
            {
                RefreshChart();
            }
        }

        private void PopulateCellTypeCheckboxes()
        {
            BioConditionCheckboxes.Children.Clear();

            Console.WriteLine($"PopulateCellTypeCheckboxes called");
            Console.WriteLine($"  _cellTypeColorMap is null: {_cellTypeColorMap == null}");

            if (_cellTypeColorMap == null || _cellTypeColorMap.Count == 0)
            {
                Console.WriteLine($"  No cell type color map available");
                return;
            }

            Console.WriteLine($"  _cellTypeColorMap.Count: {_cellTypeColorMap.Count}");

            foreach (var cellType in _cellTypeColorMap.OrderBy(kvp => kvp.Key))
            {
                Console.WriteLine($"  Adding checkbox for: {cellType.Key}");

                var checkBox = new CheckBox
                {
                    IsChecked = false,
                    Margin = new Thickness(0, 0, 0, 4),
                    Tag = cellType.Key
                };

                var stackPanel = new StackPanel { Orientation = Orientation.Horizontal };

                var colorRect = new Rectangle
                {
                    Width = 12,
                    Height = 12,
                    Fill = new SolidColorBrush(cellType.Value),
                    Stroke = new SolidColorBrush(Colors.Black),
                    StrokeThickness = 1,
                    Margin = new Thickness(0, 0, 6, 0),
                    VerticalAlignment = VerticalAlignment.Center
                };

                var label = new TextBlock
                {
                    Text = cellType.Key,
                    FontSize = 11,
                    VerticalAlignment = VerticalAlignment.Center
                };

                stackPanel.Children.Add(colorRect);
                stackPanel.Children.Add(label);
                checkBox.Content = stackPanel;

                BioConditionCheckboxes.Children.Add(checkBox);
            }

            Console.WriteLine($"  Total checkboxes added: {BioConditionCheckboxes.Children.Count}");
        }


        private void PopulateBioConditionCheckboxes()
        {
            BioConditionCheckboxes.Children.Clear();

            Console.WriteLine($"PopulateBioConditionCheckboxes called");
            Console.WriteLine($"  _currentData is null: {_currentData == null}");

            if (_currentData != null)
            {
                Console.WriteLine($"  BiologicalConditionPerFile.Count: {_currentData.BiologicalConditionPerFile.Count}");
            }

            if (_currentData == null || _currentData.BiologicalConditionPerFile.Count == 0)
                return;

            var colorMap = GenerateBioConditionColorMap();
            Console.WriteLine($"  colorMap.Count: {colorMap.Count}");

            foreach (var condition in colorMap.OrderBy(kvp => kvp.Key))
            {
                Console.WriteLine($"  Adding checkbox for: {condition.Key}");

                // Check if this condition was previously checked (default to unchecked for new conditions)
                bool isChecked = _checkedBioConditions.Contains(condition.Key);

                var checkBox = new CheckBox
                {
                    IsChecked = isChecked,
                    Margin = new Thickness(0, 0, 0, 4),
                    Tag = condition.Key
                };

                // Wire up event to track checked state
                checkBox.Checked += BioConditionCheckbox_Changed;
                checkBox.Unchecked += BioConditionCheckbox_Changed;

                var stackPanel = new StackPanel { Orientation = Orientation.Horizontal };

                var colorRect = new System.Windows.Shapes.Rectangle
                {
                    Width = 12,
                    Height = 12,
                    Fill = new SolidColorBrush(condition.Value),
                    Stroke = new SolidColorBrush(Colors.Black),
                    StrokeThickness = 1,
                    Margin = new Thickness(0, 0, 6, 0),
                    VerticalAlignment = VerticalAlignment.Center
                };

                var label = new TextBlock
                {
                    Text = condition.Key,
                    FontSize = 11,
                    VerticalAlignment = VerticalAlignment.Center
                };

                stackPanel.Children.Add(colorRect);
                stackPanel.Children.Add(label);
                checkBox.Content = stackPanel;

                BioConditionCheckboxes.Children.Add(checkBox);
            }

            Console.WriteLine($"  Total checkboxes added: {BioConditionCheckboxes.Children.Count}");
        }

        private void BioConditionCheckbox_Changed(object sender, RoutedEventArgs e)
        {
            if (sender is CheckBox checkBox && checkBox.Tag is string condition)
            {
                if (checkBox.IsChecked == true)
                {
                    _checkedBioConditions.Add(condition);
                }
                else
                {
                    _checkedBioConditions.Remove(condition);
                }
            }
        }
        private void RefreshChart()
        {
            if (_currentData == null || _currentData.PeptideCountPerFile.Count == 0)
            {
                SelectedPointsGridPanel.ClearGrid();
                GoEnrichmentTab.ClearData();
                ClearSelectionButton.IsEnabled = false;
                RunDetailPanel.ClearDetails();

                // Clear the scatter plot too!
                ScatterPlot.UpdatePlot(null, new ScatterPlotOptions());
                return;
            }

            var options = new ScatterPlotOptions
            {
                UseLogLog = LogLogCheckBox.IsChecked == true,
                UseCellTypeColoring = ColorByCellTypeRadio.IsChecked == true,
                CellTypePredictions = _cellTypePredictions,
                CellTypeColorMap = _cellTypeColorMap,
                GoEnrichmentResults = _goEnrichmentResults,
                GoTermColorMap = _goTermColorMap,
                UseBioConditionColoring = ColorByBioConditionRadio.IsChecked == true,
                BioConditionPerFile = _currentData.BiologicalConditionPerFile,
                BioConditionColorMap = GenerateBioConditionColorMap()
            };

            ScatterPlot.UpdatePlot(_currentData, options);
            UpdatePlotHeader(_currentData.PeptideCountPerFile.Count, options);
        }

        private void UpdatePlotHeader(int fileCount, ScatterPlotOptions options)
        {
            if (options.UseCellTypeColoring && _cellTypePredictions != null)
            {
                int predictedCount = _cellTypePredictions.Count(p => p.Value.TopCellType != null);
                PlotGroupBoxHeader.Text = $"Peptides vs Total Ion Current per Raw File ({fileCount} files, {predictedCount} with cell type predictions)";
            }
            else if (options.UseBioConditionColoring && _currentData.BiologicalConditionPerFile.Count > 0)
            {
                int conditionCount = _currentData.BiologicalConditionPerFile.Values.Where(c => !string.IsNullOrEmpty(c)).Distinct().Count();
                PlotGroupBoxHeader.Text = $"Peptides vs Total Ion Current per Raw File ({fileCount} files, {conditionCount} biological conditions)";
            }
            else
            {
                PlotGroupBoxHeader.Text = $"Peptides vs Total Ion Current per Raw File ({fileCount} files)";
            }
        }

        private void LogLogCheckBox_Changed(object sender, RoutedEventArgs e)
        {
            if (_currentData != null)
            {
                RefreshChart();
            }
        }

        private void ClearSelectionButton_Click(object sender, RoutedEventArgs e)
        {
            ScatterPlot.ClearSelection();
            SelectedPointsGridPanel.ClearGrid();
            GoEnrichmentTab.ClearData();
            ClearSelectionButton.IsEnabled = false;
            RunDetailPanel.ClearDetails();
            _currentSelectedPoints.Clear();
        }

        private void ScatterPlot_SelectionChanged(object sender, PlotSelectionChangedEventArgs e)
        {
            _currentSelectedPoints = e.SelectedPoints;

            if (e.SelectedPoints.Count > 0)
            {
                var gridData = e.SelectedPoints.Select(p => new SelectedPointData
                {
                    RunName = p.RunName,
                    PeptideCount = p.PeptideCount,
                    TicValue = p.TicValue,
                    ProteinCount = p.ProteinCount,
                    TrypsinRatioPercent = $"{p.TrypsinRatio * 100:F2}%",
                    CellType = p.PredictedCellType ?? "Unknown"
                }).ToList();

                SelectedPointsGridPanel.UpdateGrid(gridData);
                GoEnrichmentTab.UpdateGoEnrichment(e.SelectedPoints, _goEnrichmentResults);
                ClearSelectionButton.IsEnabled = true;

                if (e.SelectedPoints.Count == 1)
                {
                    RunDetailPanel.ShowRunDetails(e.SelectedPoints[0], _goEnrichmentResults);
                }
                else
                {
                    RunDetailPanel.ShowSelectionSummary(e.SelectedPoints, _goEnrichmentResults);
                }
            }
            else
            {
                SelectedPointsGridPanel.ClearGrid();
                GoEnrichmentTab.ClearData();
                ClearSelectionButton.IsEnabled = false;
                RunDetailPanel.ClearDetails();
            }
        }

        private void ScatterPlot_PointClicked(object sender, PointInteractionEventArgs e)
        {
            RunDetailPanel.ShowRunDetails(e.DataPoint, _goEnrichmentResults);

            // Also update GO enrichment tab for the clicked point
            if (e.DataPoint != null)
            {
                GoEnrichmentTab.UpdateGoEnrichment(new List<DataPoint> { e.DataPoint }, _goEnrichmentResults);
            }
        }

        private void SelectedPointsGridPanel_GridSelectionChanged(object sender, SelectedPointData selectedData)
        {
            if (selectedData == null)
                return;

            var dataPoint = _currentSelectedPoints.FirstOrDefault(p => p.RunName == selectedData.RunName);
            if (dataPoint != null)
            {
                RunDetailPanel.ShowRunDetails(dataPoint, _goEnrichmentResults);
                ScatterPlot.HighlightPoints(new List<string> { selectedData.RunName });
            }
        }
    }
}