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
        private HashSet<string> _checkedCellTypes = new HashSet<string>();
        private bool _suppressCheckboxEvents = false;
        // Add this new event
        public event EventHandler CellTypePredictionsRequested;

        public PeptideTicControl()
        {
            InitializeComponent();

            ScatterPlot.SelectionChanged += ScatterPlot_SelectionChanged;
            SelectedPointsGridPanel.GridSelectionChanged += SelectedPointsGridPanel_GridSelectionChanged;

            _isInitialized = true;
        }

        public void UpdateChart(ProteomicsData data)
        {
            UpdateChart(data, clearSelections: true);
        }

        public void UpdateChart(ProteomicsData data, bool clearSelections)
        {
            if (clearSelections)
            {
                // Clear old selections when loading new data
                _checkedCellTypes.Clear();
                _checkedBioConditions.Clear();
                _currentSelectedPoints.Clear();
            }

            _currentData = data;
            RefreshChart();
        }

        public void SetImageBaseDirectory(string directory)
        {
            // No longer needed - RunDetailPanel was removed
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
        }

        private void ViewMode_Changed(object sender, RoutedEventArgs e)
        {
            if (!_isInitialized)
                return;

            bool isPcaMode = ViewPcaRadio.IsChecked == true;
            bool isUmapMode = ViewUmapRadio.IsChecked == true;

            // Disable Log-Log scale for PCA/UMAP (doesn't apply)
            LogLogCheckBox.IsEnabled = !isPcaMode && !isUmapMode;

            // Update header text
            if (isPcaMode)
            {
                PlotGroupBoxHeader.Text = "PCA - Principal Component Analysis";
            }
            else if (isUmapMode)
            {
                PlotGroupBoxHeader.Text = "UMAP - Uniform Manifold Approximation and Projection";
            }
            else
            {
                PlotGroupBoxHeader.Text = "Peptides vs Total Ion Current per Raw File";
            }

            if (_currentData != null)
            {
                RefreshChart();
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

        private void ColorMode_Changed(object sender, RoutedEventArgs e)
        {
            if (!_isInitialized || ColorByCellTypeRadio == null)
                return;

            if (ColorByCellTypeRadio.IsChecked == true)
            {
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
            PopulateColorCheckboxes(_cellTypeColorMap, _checkedCellTypes);
        }



        private void PopulateBioConditionCheckboxes()
        {
            var colorMap = GenerateBioConditionColorMap();
            PopulateColorCheckboxes(colorMap, _checkedBioConditions);
        }

        private void PopulateColorCheckboxes(Dictionary<string, Color> colorMap, HashSet<string> checkedItems)
        {
            BioConditionCheckboxes.Children.Clear();

            if (colorMap == null || colorMap.Count == 0)
                return;

            foreach (var item in colorMap.OrderBy(kvp => kvp.Key))
            {
                bool isChecked = checkedItems.Contains(item.Key);

                var checkBox = new CheckBox
                {
                    IsChecked = isChecked,
                    Margin = new Thickness(0, 0, 0, 4),
                    Tag = item.Key
                };

                checkBox.Checked += (s, e) =>
                {
                    if (_suppressCheckboxEvents)
                        return;

                    if (s is CheckBox cb && cb.Tag is string key)
                    {
                        checkedItems.Add(key);
                        ScatterPlot.UpdateSelectionWithFilters(_checkedCellTypes, _checkedBioConditions);
                    }
                };

                checkBox.Unchecked += (s, e) =>
                {
                    if (_suppressCheckboxEvents)
                        return;

                    if (s is CheckBox cb && cb.Tag is string key)
                    {
                        checkedItems.Remove(key);
                        ScatterPlot.UpdateSelectionWithFilters(_checkedCellTypes, _checkedBioConditions);
                    }
                };

                var stackPanel = new StackPanel { Orientation = Orientation.Horizontal };

                var colorRect = new Rectangle
                {
                    Width = 12,
                    Height = 12,
                    Fill = new SolidColorBrush(item.Value),
                    Stroke = new SolidColorBrush(Colors.Black),
                    StrokeThickness = 1,
                    Margin = new Thickness(0, 0, 6, 0),
                    VerticalAlignment = VerticalAlignment.Center
                };

                var label = new TextBlock
                {
                    Text = item.Key,
                    FontSize = 11,
                    VerticalAlignment = VerticalAlignment.Center
                };

                stackPanel.Children.Add(colorRect);
                stackPanel.Children.Add(label);
                checkBox.Content = stackPanel;

                BioConditionCheckboxes.Children.Add(checkBox);
            }
        }
        private void RefreshChart()
        {
            if (_currentData == null || _currentData.PeptideCountPerFile.Count == 0)
            {
                SelectedPointsGridPanel.ClearGrid();
                ClearSelectionButton.IsEnabled = false;

                ScatterPlot.UpdatePlot(null, new ScatterPlotOptions());
                return;
            }

            var options = new ScatterPlotOptions
            {
                UseLogLog = LogLogCheckBox.IsChecked == true,
                UsePcaView = ViewPcaRadio.IsChecked == true,
                UseUmapView = ViewUmapRadio.IsChecked == true,
                UseCellTypeColoring = ColorByCellTypeRadio.IsChecked == true,
                CellTypePredictions = _cellTypePredictions,
                CellTypeColorMap = _cellTypeColorMap,
                GoEnrichmentResults = _goEnrichmentResults,
                GoTermColorMap = _goTermColorMap,
                UseBioConditionColoring = ColorByBioConditionRadio.IsChecked == true,
                BioConditionPerFile = _currentData.BiologicalConditionPerFile,
                BioConditionColorMap = GenerateBioConditionColorMap(),
                CheckedCellTypes = _checkedCellTypes,
                CheckedBioConditions = _checkedBioConditions
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
            // Suppress checkbox events during bulk clear
            _suppressCheckboxEvents = true;

            try
            {
                // Clear polygon selection
                ScatterPlot.ClearSelection();

                // Clear checkbox selections
                _checkedCellTypes.Clear();
                _checkedBioConditions.Clear();

                // Uncheck all checkboxes visually
                foreach (var child in BioConditionCheckboxes.Children)
                {
                    if (child is CheckBox cb)
                    {
                        cb.IsChecked = false;
                    }
                }

                // Clear UI panels
                SelectedPointsGridPanel.ClearGrid();
                ClearSelectionButton.IsEnabled = false;
                _currentSelectedPoints.Clear();
            }
            finally
            {
                _suppressCheckboxEvents = false;
            }
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
                    CellType = p.PredictedCellType ?? "",
                    BiologicalCondition = p.BiologicalCondition ?? "",
                    CompositeScore = p.PredictionScore != null ? $"{p.PredictionScore.CompositeScore:F3}" : ""
                }).ToList();

                SelectedPointsGridPanel.UpdateGrid(gridData);
                ClearSelectionButton.IsEnabled = true;
            }
            else
            {
                // Selection is empty - clear checkboxes if any were checked
                bool hadCheckedItems = _checkedCellTypes.Count > 0 || _checkedBioConditions.Count > 0;

                if (hadCheckedItems)
                {
                    _suppressCheckboxEvents = true;
                    try
                    {
                        _checkedCellTypes.Clear();
                        _checkedBioConditions.Clear();

                        foreach (var child in BioConditionCheckboxes.Children)
                        {
                            if (child is CheckBox cb)
                            {
                                cb.IsChecked = false;
                            }
                        }
                    }
                    finally
                    {
                        _suppressCheckboxEvents = false;
                    }
                }

                SelectedPointsGridPanel.ClearGrid();
                ClearSelectionButton.IsEnabled = false;
            }
        }

        private void SelectedPointsGridPanel_GridSelectionChanged(object sender, SelectedPointData selectedData)
        {
            if (selectedData == null)
                return;

            // Highlight the selected point on the scatter plot
            ScatterPlot.HighlightPoints(new List<string> { selectedData.RunName });
        }
    }
}