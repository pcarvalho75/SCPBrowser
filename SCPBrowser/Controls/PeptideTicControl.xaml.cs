using SCPBrowser.GOTools;
using SCPBrowser.Models;
using SCPBrowser.Services;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
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
        private Dictionary<string, int> _runNameToRawFileId = new Dictionary<string, int>();
        private HashSet<string> _excludedRunNames = new HashSet<string>();
        private Dictionary<string, Color> _plateColorMap;

        private HashSet<string> _checkedBioConditions = new HashSet<string>();
        private HashSet<string> _checkedCellTypes = new HashSet<string>();
        private bool _suppressCheckboxEvents = false;

        public event EventHandler CellTypePredictionsRequested;
        public event EventHandler SelectionChangedForBioTessera;
        public event EventHandler<RunInclusionChangedEventArgs> RunInclusionChanged;
        public event EventHandler<double> ContaminantRatioCutoffChanged;
        public event EventHandler ClearAllExclusionsRequested;
        public event EventHandler ExportDiagnosticsRequested;
        private bool _isLassoActive = false;
        private List<HvpResult> _hvpResults;
        private int _hvpCount = 500;
        private Dictionary<string, int> _plateMappingPerFile;
        private HashSet<string> _checkedPlates = new HashSet<string>();
        private ProjectDatabaseService _databaseService;
        private DimensionReductionSettings? _dimRedSettings;
        private HashSet<string> _contaminantRatioExcludedRuns = new HashSet<string>();
        private double _lastAppliedContaminantCutoff = 1.0;

        public HashSet<string> CheckedBioConditions => _checkedBioConditions;
        public HashSet<string> CheckedCellTypes => _checkedCellTypes;
        public HashSet<string> CheckedPlates => _checkedPlates;

        public PeptideTicControl()
        {
            InitializeComponent();

            ScatterPlot.SelectionChanged += ScatterPlot_SelectionChanged;
            SelectedPointsGridPanel.GridSelectionChanged += SelectedPointsGridPanel_GridSelectionChanged;

            SelectedPointsGridPanel.RunInclusionChanged += SelectedPointsGridPanel_RunInclusionChanged;
            SelectedPointsGridPanel.ClearAllExclusionsRequested += SelectedPointsGridPanel_ClearAllExclusionsRequested;

            GuidedWeightSlider.ValueChanged += (s, e) =>
                GuidedWeightLabel.Text = GuidedWeightSlider.Value.ToString("F2");
            GuidedEmbeddingCheckBox.Checked += (s, e) =>
                GuidedWeightSlider.IsEnabled = true;
            GuidedEmbeddingCheckBox.Unchecked += (s, e) =>
                GuidedWeightSlider.IsEnabled = false;

            _isInitialized = true;
        }

        public void SetPlateColorMap(Dictionary<string, Color> colorMap)
        {
            _plateColorMap = colorMap;
        }

        private void ViewModeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_isInitialized)
                return;

            var selectedItem = ViewModeComboBox.SelectedItem as ComboBoxItem;
            if (selectedItem == null)
                return;

            string mode = selectedItem.Tag?.ToString() ?? "PeptideTic";

            bool isPcaMode = mode == "PCA";
            bool isUmapMode = mode == "UMAP";
            bool isDimensionalityReduction = isPcaMode || isUmapMode;

            // Hide Log-Log scale for PCA/UMAP (doesn't apply)
            LogLogCheckBox.Visibility = isDimensionalityReduction ? Visibility.Collapsed : Visibility.Visible;

            // Batch Correction stays enabled regardless of view mode (sticky toggle)

            // Enable dim reduction settings button always (controls view modes & feature selection)
            DimRedSettingsButton.IsEnabled = true;

            // Enable guided embedding only if cell type predictions exist
            bool hasClassifications = _cellTypePredictions != null && _cellTypePredictions.Count > 0;
            GuidedEmbeddingCheckBox.IsEnabled = hasClassifications;
            GuidedWeightSlider.IsEnabled = hasClassifications && GuidedEmbeddingCheckBox.IsChecked == true;

            // Show Loadings button only in PCA mode
            ShowLoadingsButton.Visibility = isPcaMode ? Visibility.Visible : Visibility.Collapsed;
            ShowLoadingsButton.IsEnabled = isPcaMode;

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
                PlotGroupBoxHeader.Text = "Peptides vs TIC";
            }

            if (_currentData != null)
            {
                RefreshChart();
            }
        }

        /// <summary>
        /// Sets the HVP results for use in PCA/UMAP dimensionality reduction
        /// </summary>
        public void SetHvpResults(List<HvpResult> hvpResults)
        {
            _hvpResults = hvpResults;
        }

        /// <summary>
        /// Sets the plate mapping for batch effect correction (plate = batch)
        /// </summary>
        public void SetPlateMapping(Dictionary<string, int> plateMapping)
        {
            _plateMappingPerFile = plateMapping ?? new Dictionary<string, int>();
        }

        public void SetContaminantRatioExcludedRuns(HashSet<string> excludedRuns)
        {
            _contaminantRatioExcludedRuns = excludedRuns ?? new HashSet<string>();
        }

        /// <summary>
        /// Sets the excluded run names (loaded from database on project open)
        /// </summary>
        public void SetExcludedRuns(HashSet<string> excludedRunNames)
        {
            _excludedRunNames = excludedRunNames ?? new HashSet<string>();
        }

        /// <summary>
        /// Sets the mapping from run names to raw file IDs for exclusion tracking
        /// </summary>
        public void SetRawFileIdMapping(Dictionary<string, int> mapping)
        {
            _runNameToRawFileId = mapping ?? new Dictionary<string, int>();
        }

        /// <summary>
        /// Sets the database service for loading/saving dim reduction settings.
        /// Call this after project open, before UpdateChart.
        /// </summary>
        public async void SetDatabaseService(ProjectDatabaseService db)
        {
            _databaseService = db;
            _dimRedSettings = await DimensionReductionSettings.LoadAsync(db);
            ApplySettingsToUI(_dimRedSettings);
        }

        private void ApplySettingsToUI(DimensionReductionSettings s)
        {
            if (s == null) return;
            ZScaleCheckBox.IsChecked = s.ZScoreScale;
            ClipMaxTextBox.Text = s.ClipMaxValue.ToString("G", CultureInfo.InvariantCulture);
            PcaComponentsTextBox.Text = s.NumPcaComponents.ToString();
            PcsForUmapTextBox.Text = s.NumPcsForUmap.ToString();
            UmapNeighborsTextBox.Text = s.UmapNeighbors.ToString();
            UmapSeedTextBox.Text = s.UmapSeed.ToString();
            GuidedEmbeddingCheckBox.IsChecked = s.UseGuidedEmbedding;
            GuidedWeightSlider.Value = s.GuidedWeight;
            GuidedWeightLabel.Text = s.GuidedWeight.ToString("F2");
            ShowPcaViewCheckBox.IsChecked = s.ShowPcaView;
            UseHvpCheckBox.IsChecked = s.UseHvpFilter;
            HvpCountTextBox.Text = s.HvpCount.ToString();
            _hvpCount = s.HvpCount;

            // Show/hide PCA view mode
            PcaViewItem.Visibility = s.ShowPcaView ? Visibility.Visible : Visibility.Collapsed;

            // Enable/disable HVP count controls
            bool hvpActive = s.UseHvpFilter;
            HvpCountTextBox.IsEnabled = hvpActive;
            HvpCountUp.IsEnabled = hvpActive;
            HvpCountDown.IsEnabled = hvpActive;

            // Restore batch correction state
            BatchCorrectionCheckBox.IsChecked = s.ApplyBatchCorrection;
        }

        private DimensionReductionSettings ReadSettingsFromUI()
        {
            var s = DimensionReductionSettings.CreateDefaults();
            s.ZScoreScale = ZScaleCheckBox.IsChecked == true;
            if (double.TryParse(ClipMaxTextBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double clip))
                s.ClipMaxValue = Math.Max(1, clip);
            if (int.TryParse(PcaComponentsTextBox.Text, out int nPca))
                s.NumPcaComponents = Math.Clamp(nPca, 2, 100);
            if (int.TryParse(PcsForUmapTextBox.Text, out int nPcsUmap))
                s.NumPcsForUmap = Math.Clamp(nPcsUmap, 2, 100);
            if (int.TryParse(UmapNeighborsTextBox.Text, out int neighbors))
                s.UmapNeighbors = Math.Clamp(neighbors, 2, 200);
            if (int.TryParse(UmapSeedTextBox.Text, out int seed))
                s.UmapSeed = Math.Max(0, seed);
            s.UseGuidedEmbedding = GuidedEmbeddingCheckBox.IsChecked == true;
            s.GuidedWeight = GuidedWeightSlider.Value;
            s.ShowPcaView = ShowPcaViewCheckBox.IsChecked == true;
            s.UseHvpFilter = UseHvpCheckBox.IsChecked == true;
            s.ApplyBatchCorrection = BatchCorrectionCheckBox.IsChecked == true;
            if (int.TryParse(HvpCountTextBox.Text, out int hvpCount))
                s.HvpCount = Math.Clamp(hvpCount, 10, 5000);

            // Ensure PCs for UMAP doesn't exceed total PCA components
            if (s.NumPcsForUmap > s.NumPcaComponents)
                s.NumPcsForUmap = s.NumPcaComponents;

            return s;
        }

        private void DimRedSettingsButton_Click(object sender, RoutedEventArgs e)
        {
            DimRedSettingsPopup.IsOpen = !DimRedSettingsPopup.IsOpen;
        }

        private async void DimRedApplyButton_Click(object sender, RoutedEventArgs e)
        {
            _dimRedSettings = ReadSettingsFromUI();
            _hvpCount = _dimRedSettings.HvpCount;
            await _dimRedSettings.SaveAsync(_databaseService);
            DimRedSettingsPopup.IsOpen = false;

            // Show/hide PCA in the view mode dropdown
            PcaViewItem.Visibility = _dimRedSettings.ShowPcaView ? Visibility.Visible : Visibility.Collapsed;

            // If user hid PCA while it was selected, switch to UMAP
            if (!_dimRedSettings.ShowPcaView && ViewModeComboBox.SelectedItem == PcaViewItem)
                ViewModeComboBox.SelectedIndex = 0; // PeptideTic

            if (_currentData != null)
                RefreshChart();
        }

        private void DimRedResetButton_Click(object sender, RoutedEventArgs e)
        {
            ApplySettingsToUI(DimensionReductionSettings.CreateDefaults());
        }

        private async void DimRedRefreshButton_Click(object sender, RoutedEventArgs e)
        {
            _dimRedSettings = ReadSettingsFromUI();
            _hvpCount = _dimRedSettings.HvpCount;
            await _dimRedSettings.SaveAsync(_databaseService);
            DimRedSettingsPopup.IsOpen = false;

            PcaViewItem.Visibility = _dimRedSettings.ShowPcaView ? Visibility.Visible : Visibility.Collapsed;
            if (!_dimRedSettings.ShowPcaView && ViewModeComboBox.SelectedItem == PcaViewItem)
                ViewModeComboBox.SelectedIndex = 0;

            // Force invalidation so caches are rebuilt (useful for random seed)
            ScatterPlot.InvalidateCaches();

            if (_currentData != null)
                RefreshChart();
        }

        private void NumericTextBox_PreviewTextInput(object sender, System.Windows.Input.TextCompositionEventArgs e)
        {
            // Allow digits and decimal point
            e.Handled = !char.IsDigit(e.Text[0]) && e.Text[0] != '.';
        }

        private void SelectedPointsGridPanel_RunInclusionChanged(object sender, RunInclusionChangedEventArgs e)
        {
            // Bubble up to MainWindow for database persistence
            RunInclusionChanged?.Invoke(this, e);

            // Trigger BioTessera update
            SelectionChangedForBioTessera?.Invoke(this, EventArgs.Empty);
        }

        private void SelectedPointsGridPanel_ClearAllExclusionsRequested(object sender, EventArgs e)
        {
            // Bubble up to MainWindow for database persistence
            ClearAllExclusionsRequested?.Invoke(this, e);

            // Trigger BioTessera update
            SelectionChangedForBioTessera?.Invoke(this, EventArgs.Empty);
        }

        public void UpdateChart(ProteomicsData data)
        {
            UpdateChart(data, clearSelections: true);
        }

        /// <summary>
        /// Enables or disables cell type checkboxes based on lasso state
        /// </summary>
        private void SetCellTypeCheckboxesEnabled(bool enabled)
        {
            var selectedItem = ColorModeComboBox.SelectedItem as ComboBoxItem;
            string colorMode = selectedItem?.Tag?.ToString() ?? "TargetRatio";
            if (colorMode != "CellType")
                return;

            foreach (var child in BioConditionCheckboxes.Children)
            {
                if (child is CheckBox cb)
                {
                    cb.IsEnabled = enabled;
                    cb.Opacity = enabled ? 1.0 : 0.5;
                }
            }
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

        public void SetCellTypePredictions(Dictionary<string, CellTypePredictionResult> predictions, Dictionary<string, Color> colorMap, bool selectCellTypeMode = false)
        {
            _cellTypePredictions = predictions;
            _cellTypeColorMap = colorMap;
            ColorByCellTypeItem.IsEnabled = predictions != null && predictions.Count > 0;
            
            // Show/hide the export diagnostics button
            ExportDiagnosticsButton.Visibility = (predictions != null && predictions.Count > 0) 
                ? Visibility.Visible 
                : Visibility.Collapsed;

            if (_currentData != null)
            {
                // Auto-select Cell Type mode if requested and predictions are available
                if (selectCellTypeMode && predictions != null && predictions.Count > 0)
                {
                    ColorModeComboBox.SelectedIndex = 1; // Cell Type
                    // ColorModeComboBox_SelectionChanged will be triggered automatically, which populates checkboxes
                }

                // If cell type mode is already selected, populate the checkboxes
                var selectedItem = ColorModeComboBox.SelectedItem as ComboBoxItem;
                string colorMode = selectedItem?.Tag?.ToString() ?? "TargetRatio";
                if (colorMode == "CellType" && _cellTypeColorMap != null && _cellTypeColorMap.Count > 0)
                {
                    PopulateCellTypeCheckboxes();
                    BioConditionPanel.Visibility = Visibility.Visible;
                }

                RefreshChart();
            }
        }

        public void EnableCellTypeClassification(bool isAvailable)
        {
            ColorByCellTypeItem.IsEnabled = isAvailable;
        }

        public void SetGoEnrichmentResults(Dictionary<string, RunGoEnrichmentResult> results, Dictionary<string, Color> colorMap)
        {
            _goEnrichmentResults = results;
            _goTermColorMap = colorMap;
        }

        private void ShowLoadingsButton_Click(object sender, RoutedEventArgs e)
        {
            var loadingsData = ScatterPlot.GetPcaLoadings();

            if (loadingsData == null)
            {
                MessageBox.Show("PCA has not been computed yet. Select some data first.",
                    "No PCA Data", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var dialog = new PcaLoadingsDialog(
                loadingsData.Value.ProteinNames,
                loadingsData.Value.Loadings,
                loadingsData.Value.VarianceExplained)
            {
                Owner = Window.GetWindow(this)
            };

            dialog.ShowDialog();
        }

        private void ColorModeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_isInitialized || ColorModeComboBox == null)
                return;

            var selectedItem = ColorModeComboBox.SelectedItem as ComboBoxItem;
            if (selectedItem == null)
                return;

            string mode = selectedItem.Tag?.ToString() ?? "TargetRatio";

            if (mode == "CellType")
            {
                if (_cellTypePredictions == null || _cellTypePredictions.Count == 0)
                {
                    CellTypePredictionsRequested?.Invoke(this, EventArgs.Empty);
                    return;
                }

                LegendPanelTitle.Text = "Cell Types";
                CheckboxScrollViewer.Visibility = Visibility.Visible;
                ContaminantRatioLegendPanel.Visibility = Visibility.Collapsed;
                DistributionPieChart.Visibility = Visibility.Visible;
                PopulateCellTypeCheckboxes();
                UpdatePieChart("CellType");
                BioConditionPanel.Visibility = Visibility.Visible;
            }
            else if (mode == "BioCondition")
            {
                LegendPanelTitle.Text = "Biological Conditions";
                CheckboxScrollViewer.Visibility = Visibility.Visible;
                ContaminantRatioLegendPanel.Visibility = Visibility.Collapsed;
                DistributionPieChart.Visibility = Visibility.Visible;
                PopulateBioConditionCheckboxes();
                UpdatePieChart("BioCondition");
                BioConditionPanel.Visibility = Visibility.Visible;
            }
            else if (mode == "Plate")
            {
                LegendPanelTitle.Text = "Plates";
                CheckboxScrollViewer.Visibility = Visibility.Visible;
                ContaminantRatioLegendPanel.Visibility = Visibility.Collapsed;
                DistributionPieChart.Visibility = Visibility.Visible;
                PopulatePlateCheckboxes();
                UpdatePieChart("Plate");
                BioConditionPanel.Visibility = Visibility.Visible;
            }
            else
            {
                // Contaminant Ratio mode — show right panel with gradient + slider
                LegendPanelTitle.Text = "Contaminant Ratio";
                CheckboxScrollViewer.Visibility = Visibility.Collapsed;
                ContaminantRatioLegendPanel.Visibility = Visibility.Visible;
                DistributionPieChart.Visibility = Visibility.Collapsed;
                BioConditionPanel.Visibility = Visibility.Visible;
                DrawContaminantGradient();
            }

            if (_currentData != null)
            {
                RefreshChart();
            }
        }

        private void PopulatePlateCheckboxes()
        {
            var colorMap = GeneratePlateColorMap();
            var counts = GetCategoryCounts("Plate");

            BioConditionCheckboxes.Children.Clear();

            if (colorMap == null || colorMap.Count == 0)
                return;

            // If no items are currently checked, check all by default
            bool checkAllByDefault = _checkedPlates.Count == 0;

            // Sort by count descending, then alphabetically as tiebreaker
            var sortedItems = counts != null && counts.Count > 0
                ? colorMap.OrderByDescending(kvp => counts.ContainsKey(kvp.Key) ? counts[kvp.Key] : 0)
                          .ThenBy(kvp => kvp.Key)
                : colorMap.OrderBy(kvp => kvp.Key);

            foreach (var item in sortedItems)
            {
                bool isChecked = checkAllByDefault || _checkedPlates.Contains(item.Key);

                if (checkAllByDefault)
                {
                    _checkedPlates.Add(item.Key);
                }

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
                        _checkedPlates.Add(key);
                        ScatterPlot.UpdateSelectionWithFilters(_checkedCellTypes, _checkedBioConditions, _checkedPlates, userInteraction: true);
                        UpdateExcludedRunsGrid();
                    }
                };

                checkBox.Unchecked += (s, e) =>
                {
                    if (_suppressCheckboxEvents)
                        return;

                    if (s is CheckBox cb && cb.Tag is string key)
                    {
                        _checkedPlates.Remove(key);
                        ScatterPlot.UpdateSelectionWithFilters(_checkedCellTypes, _checkedBioConditions, _checkedPlates, userInteraction: true);
                        UpdateExcludedRunsGrid();
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

        private Dictionary<string, int> GetCategoryCounts(string coloringMode)
        {
            var counts = new Dictionary<string, int>();

            if (_currentData == null)
                return counts;

            if (coloringMode == "CellType" && _cellTypePredictions != null)
            {
                foreach (var kvp in _cellTypePredictions)
                {
                    string cellType = kvp.Value.TopCellType ?? "Unknown";
                    if (!counts.ContainsKey(cellType))
                        counts[cellType] = 0;
                    counts[cellType]++;
                }
            }
            else if (coloringMode == "BioCondition" && _currentData.BiologicalConditionPerFile != null)
            {
                foreach (var kvp in _currentData.BiologicalConditionPerFile)
                {
                    string condition = string.IsNullOrEmpty(kvp.Value) ? "Unknown" : kvp.Value;
                    if (!counts.ContainsKey(condition))
                        counts[condition] = 0;
                    counts[condition]++;
                }
            }
            else if (coloringMode == "Plate" && _plateMappingPerFile != null)
            {
                var platePerFile = GeneratePlatePerFile();
                foreach (var kvp in platePerFile)
                {
                    string plate = string.IsNullOrEmpty(kvp.Value) ? "Unknown" : kvp.Value;
                    if (!counts.ContainsKey(plate))
                        counts[plate] = 0;
                    counts[plate]++;
                }
            }

            return counts;
        }

        private void UpdatePieChart(string coloringMode)
        {
            if (_currentData == null)
            {
                DistributionPieChart.Clear();
                return;
            }

            var counts = GetCategoryCounts(coloringMode);
            Dictionary<string, Color> colorMap = null;

            if (coloringMode == "CellType")
                colorMap = _cellTypeColorMap;
            else if (coloringMode == "BioCondition")
                colorMap = GenerateBioConditionColorMap();
            else if (coloringMode == "Plate")
                colorMap = GeneratePlateColorMap();

            if (counts.Count > 0 && colorMap != null)
            {
                DistributionPieChart.UpdateDistribution(counts, colorMap);
            }
            else
            {
                DistributionPieChart.Clear();
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
                colorMap[uniqueConditions[i]] = ColorMapper.HsvToRgb(hue, 0.7, 0.9);
            }
            return colorMap;
        }

        private Dictionary<string, Color> GeneratePlateColorMap()
        {
            // Use the color map passed from PlateFilterControl if available
            if (_plateColorMap != null && _plateColorMap.Count > 0)
                return _plateColorMap;

            // Fallback: generate based on plate mapping (shouldn't normally be needed)
            var colorMap = new Dictionary<string, Color>();

            if (_plateMappingPerFile == null || _plateMappingPerFile.Count == 0)
                return colorMap;

            var uniquePlateIds = _plateMappingPerFile.Values.Distinct().OrderBy(id => id).ToList();

            for (int i = 0; i < uniquePlateIds.Count; i++)
            {
                int plateId = uniquePlateIds[i];
                string plateName = $"Plate {plateId}";
                colorMap[plateName] = PlateFilterControl.PlateColorPalette[i % PlateFilterControl.PlateColorPalette.Length];
            }

            return colorMap;
        }

        public void EnableBioConditionClassification(bool isAvailable)
        {
            ColorByBioConditionItem.IsEnabled = isAvailable;
        }


        private void PopulateCellTypeCheckboxes()
        {
            var counts = GetCategoryCounts("CellType");
            PopulateColorCheckboxes(_cellTypeColorMap, _checkedCellTypes, counts);
        }



        private void PopulateBioConditionCheckboxes()
        {
            var colorMap = GenerateBioConditionColorMap();
            var counts = GetCategoryCounts("BioCondition");
            PopulateColorCheckboxes(colorMap, _checkedBioConditions, counts);
        }

        private static readonly Color[] _colorPalette = new Color[]
        {
            Color.FromRgb(231, 76, 60),    // Red
            Color.FromRgb(230, 126, 34),   // Orange
            Color.FromRgb(241, 196, 15),   // Yellow
            Color.FromRgb(46, 204, 113),   // Green
            Color.FromRgb(26, 188, 156),   // Teal
            Color.FromRgb(52, 152, 219),   // Blue
            Color.FromRgb(41, 128, 185),   // Dark Blue
            Color.FromRgb(155, 89, 182),   // Purple
            Color.FromRgb(142, 68, 173),   // Dark Purple
            Color.FromRgb(233, 30, 99),    // Pink
            Color.FromRgb(0, 150, 136),    // Dark Teal
            Color.FromRgb(76, 175, 80),    // Medium Green
            Color.FromRgb(255, 152, 0),    // Amber
            Color.FromRgb(121, 85, 72),    // Brown
            Color.FromRgb(96, 125, 139),   // Blue Grey
            Color.FromRgb(244, 67, 54),    // Bright Red
            Color.FromRgb(0, 188, 212),    // Cyan
            Color.FromRgb(63, 81, 181),    // Indigo
            Color.FromRgb(205, 220, 57),   // Lime
            Color.FromRgb(180, 180, 180),  // Grey
        };

        private void PopulateColorCheckboxes(Dictionary<string, Color> colorMap, HashSet<string> checkedItems, Dictionary<string, int> counts = null)
        {
            BioConditionCheckboxes.Children.Clear();

            if (colorMap == null || colorMap.Count == 0)
                return;

            // If no items are currently checked, check all by default
            bool checkAllByDefault = checkedItems.Count == 0;

            // Sort by count descending, then alphabetically as tiebreaker
            var sortedItems = counts != null && counts.Count > 0
                ? colorMap.OrderByDescending(kvp => counts.ContainsKey(kvp.Key) ? counts[kvp.Key] : 0)
                          .ThenBy(kvp => kvp.Key)
                : colorMap.OrderBy(kvp => kvp.Key);

            foreach (var item in sortedItems)
            {
                // Check by default if no previous selection, otherwise use existing state
                bool isChecked = checkAllByDefault || checkedItems.Contains(item.Key);

                // Add to checkedItems if checking by default
                if (checkAllByDefault)
                {
                    checkedItems.Add(item.Key);
                }

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
                        ScatterPlot.UpdateSelectionWithFilters(_checkedCellTypes, _checkedBioConditions, _checkedPlates, userInteraction: true);
                        UpdateExcludedRunsGrid();
                    }
                };

                checkBox.Unchecked += (s, e) =>
                {
                    if (_suppressCheckboxEvents)
                        return;

                    if (s is CheckBox cb && cb.Tag is string key)
                    {
                        checkedItems.Remove(key);
                        ScatterPlot.UpdateSelectionWithFilters(_checkedCellTypes, _checkedBioConditions, _checkedPlates, userInteraction: true);
                        UpdateExcludedRunsGrid();
                    }
                };

                var stackPanel = new StackPanel { Orientation = Orientation.Horizontal };

                int itemCount = counts != null && counts.ContainsKey(item.Key) ? counts[item.Key] : -1;

                var colorRect = new Rectangle
                {
                    Width = 12,
                    Height = 12,
                    Fill = new SolidColorBrush(item.Value),
                    Stroke = new SolidColorBrush(Colors.Black),
                    StrokeThickness = 1,
                    Margin = new Thickness(0, 0, 6, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                    Cursor = Cursors.Hand,
                    ToolTip = "Right-click to change color"
                };

                // Right-click color picker
                string itemKey = item.Key;
                colorRect.MouseRightButtonUp += (s, e) =>
                {
                    e.Handled = true;
                    ShowColorPicker(colorRect, itemKey, colorMap);
                };

                var label = new TextBlock
                {
                    Text = item.Key,
                    FontSize = 11,
                    VerticalAlignment = VerticalAlignment.Center,
                    Foreground = itemCount == 0 ? Brushes.LightGray : Brushes.Black
                };

                stackPanel.Children.Add(colorRect);
                stackPanel.Children.Add(label);
                checkBox.Content = stackPanel;

                BioConditionCheckboxes.Children.Add(checkBox);
            }
        }

        private Popup _colorPickerPopup;

        private Brush GetCellTypeBrush(string cellType)
        {
            if (string.IsNullOrEmpty(cellType) || _cellTypeColorMap == null)
                return Brushes.Black;

            if (_cellTypeColorMap.TryGetValue(cellType, out var color))
                return new SolidColorBrush(color);

            return Brushes.Black;
        }

        private void ShowColorPicker(Rectangle targetRect, string itemKey, Dictionary<string, Color> colorMap)
        {
            // Close any existing popup
            if (_colorPickerPopup != null)
                _colorPickerPopup.IsOpen = false;

            var wrapPanel = new WrapPanel { Width = 120, Margin = new Thickness(4) };

            foreach (var paletteColor in _colorPalette)
            {
                var swatch = new Rectangle
                {
                    Width = 18,
                    Height = 18,
                    Fill = new SolidColorBrush(paletteColor),
                    Stroke = new SolidColorBrush(Colors.DarkGray),
                    StrokeThickness = 1,
                    Margin = new Thickness(2),
                    Cursor = Cursors.Hand
                };

                Color capturedColor = paletteColor;
                swatch.MouseLeftButtonUp += (s, e) =>
                {
                    // Update the color map
                    colorMap[itemKey] = capturedColor;

                    // Update the rectangle in the legend
                    targetRect.Fill = new SolidColorBrush(capturedColor);

                    // Close popup and refresh chart
                    _colorPickerPopup.IsOpen = false;
                    if (_currentData != null)
                        RefreshChart();
                };

                // Hover highlight
                swatch.MouseEnter += (s, e) => swatch.StrokeThickness = 2;
                swatch.MouseLeave += (s, e) => swatch.StrokeThickness = 1;

                wrapPanel.Children.Add(swatch);
            }

            var border = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(27, 27, 47)),  // #1B1B2F
                BorderBrush = new SolidColorBrush(Color.FromRgb(108, 99, 255)), // #6C63FF
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(4),
                Child = wrapPanel
            };

            _colorPickerPopup = new Popup
            {
                PlacementTarget = targetRect,
                Placement = PlacementMode.Right,
                StaysOpen = false,
                AllowsTransparency = true,
                Child = border
            };

            _colorPickerPopup.IsOpen = true;
        }
        private async void RefreshChart()
        {
            if (_currentData == null || _currentData.PeptideCountPerFile.Count == 0)
            {
                SelectedPointsGridPanel.ClearGrid();
                ClearSelectionButton.IsEnabled = false;

                ScatterPlot.UpdatePlot(null, new ScatterPlotOptions());
                return;
            }

            var selectedViewItem = ViewModeComboBox.SelectedItem as ComboBoxItem;
            string viewMode = selectedViewItem?.Tag?.ToString() ?? "PeptideTic";

            var selectedColorItem = ColorModeComboBox.SelectedItem as ComboBoxItem;
            string colorMode = selectedColorItem?.Tag?.ToString() ?? "TargetRatio";

            // Initialize ALL checked sets with all available values if they're empty
            // This ensures filters work correctly even before user opens that category's panel
            if (_checkedBioConditions.Count == 0 && _currentData.BiologicalConditionPerFile != null)
            {
                var uniqueConditions = _currentData.BiologicalConditionPerFile.Values
                    .Where(c => !string.IsNullOrEmpty(c))
                    .Distinct();
                foreach (var condition in uniqueConditions)
                    _checkedBioConditions.Add(condition);
            }

            if (_checkedPlates.Count == 0 && _plateMappingPerFile != null)
            {
                var plateColorMap = GeneratePlateColorMap();
                foreach (var plateName in plateColorMap.Keys)
                    _checkedPlates.Add(plateName);
            }

            if (_checkedCellTypes.Count == 0 && _cellTypeColorMap != null)
            {
                foreach (var cellType in _cellTypeColorMap.Keys)
                    _checkedCellTypes.Add(cellType);
            }

            // Ensure legend and pie chart are populated for the current color mode
            if (colorMode == "BioCondition")
            {
                LegendPanelTitle.Text = "Biological Conditions";
                CheckboxScrollViewer.Visibility = Visibility.Visible;
                ContaminantRatioLegendPanel.Visibility = Visibility.Collapsed;
                DistributionPieChart.Visibility = Visibility.Visible;
                if (BioConditionCheckboxes.Children.Count == 0)
                    PopulateBioConditionCheckboxes();
                UpdatePieChart("BioCondition");
                BioConditionPanel.Visibility = Visibility.Visible;
            }
            else if (colorMode == "CellType" && _cellTypeColorMap != null)
            {
                LegendPanelTitle.Text = "Cell Types";
                CheckboxScrollViewer.Visibility = Visibility.Visible;
                ContaminantRatioLegendPanel.Visibility = Visibility.Collapsed;
                DistributionPieChart.Visibility = Visibility.Visible;
                if (BioConditionCheckboxes.Children.Count == 0)
                    PopulateCellTypeCheckboxes();
                UpdatePieChart("CellType");
                BioConditionPanel.Visibility = Visibility.Visible;
            }
            else if (colorMode == "Plate")
            {
                LegendPanelTitle.Text = "Plates";
                CheckboxScrollViewer.Visibility = Visibility.Visible;
                ContaminantRatioLegendPanel.Visibility = Visibility.Collapsed;
                DistributionPieChart.Visibility = Visibility.Visible;
                if (BioConditionCheckboxes.Children.Count == 0)
                    PopulatePlateCheckboxes();
                UpdatePieChart("Plate");
                BioConditionPanel.Visibility = Visibility.Visible;
            }
            else
            {
                // Contaminant Ratio mode
                LegendPanelTitle.Text = "Contaminant Ratio";
                CheckboxScrollViewer.Visibility = Visibility.Collapsed;
                ContaminantRatioLegendPanel.Visibility = Visibility.Visible;
                DistributionPieChart.Visibility = Visibility.Collapsed;
                BioConditionPanel.Visibility = Visibility.Visible;
                DrawContaminantGradient();
            }

            var options = new ScatterPlotOptions
            {
                UseLogLog = LogLogCheckBox.IsChecked == true,
                UsePcaView = viewMode == "PCA",
                UseUmapView = viewMode == "UMAP",
                ApplyBatchCorrection = BatchCorrectionCheckBox.IsChecked == true,
                BatchLabelPerFile = _plateMappingPerFile,
                UseCellTypeColoring = colorMode == "CellType",
                CellTypePredictions = _cellTypePredictions,
                CellTypeColorMap = _cellTypeColorMap,
                GoEnrichmentResults = _goEnrichmentResults,
                GoTermColorMap = _goTermColorMap,
                UseBioConditionColoring = colorMode == "BioCondition",
                BioConditionPerFile = _currentData.BiologicalConditionPerFile,
                BioConditionColorMap = GenerateBioConditionColorMap(),
                UsePlateColoring = colorMode == "Plate",
                PlatePerFile = GeneratePlatePerFile(),
                PlateColorMap = GeneratePlateColorMap(),
                CheckedCellTypes = _checkedCellTypes,
                CheckedBioConditions = _checkedBioConditions,
                    CheckedPlates = _checkedPlates,
                        HvpResults = GetFilteredHvpResults(),
                        DimRedSettings = _dimRedSettings,
                        ContaminantRatioExcludedRuns = _contaminantRatioExcludedRuns,
                        ContaminantRatioCutoff = _lastAppliedContaminantCutoff
                    };

            // Run heavy PCA/UMAP on background thread with loading overlay
            bool willInvalidate = false;
            if (options.UsePcaView || options.UseUmapView)
            {
                // Check if caches will be invalidated by changed settings
                bool batchChanged = (options.ApplyBatchCorrection) != ScatterPlot.PreviousApplyBatchCorrection;
                bool dimRedChanged = options.DimRedSettings != null && options.DimRedSettings.DiffersFrom(ScatterPlot.PreviousDimRedSettings);
                bool dataChanged = _currentData != ScatterPlot.CurrentData;
                willInvalidate = batchChanged || dimRedChanged || dataChanged;
            }

            bool needsHeavyCompute = (options.UsePcaView && (ScatterPlot.NeedsPcaCompute || willInvalidate))
                                  || (options.UseUmapView && (ScatterPlot.NeedsUmapCompute || willInvalidate));

            MainWindow? mainWindow = null;
            if (needsHeavyCompute)
            {
                mainWindow = Window.GetWindow(this) as MainWindow;
                if (mainWindow != null)
                {
                    string computeType = options.UseUmapView ? "UMAP" : "PCA";
                    if (willInvalidate && !ScatterPlot.NeedsPcaCompute)
                        mainWindow.LoadingOverlay.SetMessage($"Recomputing {computeType}");
                    else
                        mainWindow.LoadingOverlay.SetMessage($"Computing {computeType}");
                    mainWindow.LoadingOverlay.SetProgress("This may take a moment...");
                    mainWindow.LoadingOverlay.Show();
                    await Task.Delay(50); // Let UI render the overlay
                }

                // Precompute on background thread — caches will be filled when UpdatePlot runs
                await ScatterPlot.PrecomputeIfNeededAsync(_currentData, options);

                mainWindow?.LoadingOverlay.Hide();
            }

            ScatterPlot.UpdatePlot(_currentData, options);
            UpdatePlotHeader(_currentData.PeptideCountPerFile.Count, options);

            // All dots start as selected, so enable the clear button
            ClearSelectionButton.IsEnabled = true;

            // Ensure selection styling is applied after plot is fully rendered (fixes initial load)
            if (_checkedBioConditions.Count > 0 || _checkedCellTypes.Count > 0 || _checkedPlates.Count > 0)
            {
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    ScatterPlot.UpdateSelectionWithFilters(_checkedCellTypes, _checkedBioConditions, _checkedPlates);
                    UpdateExcludedRunsGrid();
                }), System.Windows.Threading.DispatcherPriority.Loaded);
            }
            else
            {
                UpdateExcludedRunsGrid();
            }
        }

        private Dictionary<string, string> GeneratePlatePerFile()
        {
            var platePerFile = new Dictionary<string, string>();

            if (_plateMappingPerFile == null || _plateMappingPerFile.Count == 0)
                return platePerFile;

            // Get plate names from the color map keys, or fall back to "Plate {id}"
            var plateIdToName = new Dictionary<int, string>();

            if (_plateColorMap != null)
            {
                // Color map keys are plate names
                var uniquePlateIds = _plateMappingPerFile.Values.Distinct().OrderBy(id => id).ToList();
                var plateNames = _plateColorMap.Keys.ToList();

                for (int i = 0; i < uniquePlateIds.Count && i < plateNames.Count; i++)
                {
                    plateIdToName[uniquePlateIds[i]] = plateNames[i];
                }
            }

            foreach (var kvp in _plateMappingPerFile)
            {
                string plateName = plateIdToName.TryGetValue(kvp.Value, out var name)
                    ? name
                    : $"Plate {kvp.Value}";
                platePerFile[kvp.Key] = plateName;
            }

            return platePerFile;
        }

        private List<HvpResult> GetFilteredHvpResults()
        {
            var selectedItem = ViewModeComboBox.SelectedItem as ComboBoxItem;
            string viewMode = selectedItem?.Tag?.ToString() ?? "PeptideTic";
            bool isPcaOrUmap = viewMode == "PCA" || viewMode == "UMAP";

            if (!isPcaOrUmap || _hvpResults == null || _hvpResults.Count == 0)
            {
                return _hvpResults;
            }

            if (_dimRedSettings != null && _dimRedSettings.UseHvpFilter)
            {
                // Return only top N proteins by VarianceStandardized
                return _hvpResults
                    .OrderByDescending(h => h.VarianceStandardized)
                    .Take(_hvpCount)
                    .Select(h => new HvpResult
                    {
                        ProteinId = h.ProteinId,
                        Mean = h.Mean,
                        Variance = h.Variance,
                        VarianceExpected = h.VarianceExpected,
                        VarianceStandardized = h.VarianceStandardized,
                        DetectionCount = h.DetectionCount,
                        DetectionRate = h.DetectionRate,
                        Rank = h.Rank,
                        IsHighlyVariable = true  // Mark all selected as HVP
                    })
                    .ToList();
            }

            return _hvpResults;
        }

        private void UpdatePlotHeader(int fileCount, ScatterPlotOptions options)
        {
            string baseHeader;

            if (options.UsePcaView)
            {
                baseHeader = "PCA - Principal Component Analysis";
            }
            else if (options.UseUmapView)
            {
                baseHeader = "UMAP - Uniform Manifold Approximation and Projection";
                var purity = ScatterPlot.LabelPurity;
                if (purity.HasValue)
                    baseHeader += $"  |  Label purity: {purity.Value:F2}";
                if (options.DimRedSettings?.UseGuidedEmbedding == true)
                    baseHeader += "  [guided]";
            }
            else if (options.UseCellTypeColoring && _cellTypePredictions != null)
            {
                int predictedCount = _cellTypePredictions.Count(p => p.Value.TopCellType != null);
                baseHeader = $"Peptides vs TIC ({fileCount} files, {predictedCount} with cell type predictions)";
            }
            else if (options.UseBioConditionColoring && _currentData.BiologicalConditionPerFile.Count > 0)
            {
                int conditionCount = _currentData.BiologicalConditionPerFile.Values.Where(c => !string.IsNullOrEmpty(c)).Distinct().Count();
                baseHeader = $"Peptides vs TIC ({fileCount} files, {conditionCount} biological conditions)";
            }
            else if (options.UsePlateColoring && options.PlateColorMap != null && options.PlateColorMap.Count > 0)
            {
                int plateCount = options.PlateColorMap.Count;
                baseHeader = $"Peptides vs TIC ({fileCount} files, {plateCount} plates)";
            }
            else
            {
                baseHeader = $"Peptides vs TIC ({fileCount} files)";
            }

            PlotGroupBoxHeader.Text = baseHeader;
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

                // Reset lasso state and re-enable checkboxes
                _isLassoActive = false;
                SetCellTypeCheckboxesEnabled(true);
            }
            finally
            {
                _suppressCheckboxEvents = false;
            }
        }

        private void HideGreyDotsCheckBox_Changed(object sender, RoutedEventArgs e)
        {
            ScatterPlot.SetHideUnselected(HideGreyDotsCheckBox.IsChecked == true);
        }

        private void ContaminantCutoffSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (!_isInitialized) return;

            double percent = ContaminantCutoffSlider.Value;
            ContaminantCutoffValueLabel.Text = percent >= 100 ? "Off" : $"{percent:F0}%";

            double newCutoff = percent / 100.0;
            if (Math.Abs(newCutoff - _lastAppliedContaminantCutoff) < 0.005)
                return;

            _lastAppliedContaminantCutoff = newCutoff;
            ContaminantRatioCutoffChanged?.Invoke(this, newCutoff);
        }

        private void DrawContaminantGradient()
        {
            GradientCanvas.Children.Clear();

            double gradientWidth = 25;
            double gradientHeight = 180;
            double offsetX = 10;
            double offsetY = 5;
            int segments = 50;
            double segmentHeight = gradientHeight / segments;

            // Find max ratio from current data
            double maxRatio = 0;
            if (_currentData?.TargetProteinRatioPerFile != null && _currentData.TargetProteinRatioPerFile.Count > 0)
            {
                maxRatio = _currentData.TargetProteinRatioPerFile.Values.Max();
            }
            if (maxRatio <= 0) maxRatio = 1.0;

            // Draw gradient bar
            for (int i = 0; i < segments; i++)
            {
                double value = 1.0 - ((double)i / segments);
                var color = ColorMapper.GetViridisColor(value);
                var rect = new System.Windows.Shapes.Rectangle
                {
                    Width = gradientWidth,
                    Height = segmentHeight + 1,
                    Fill = new System.Windows.Media.SolidColorBrush(color)
                };
                Canvas.SetLeft(rect, offsetX);
                Canvas.SetTop(rect, offsetY + i * segmentHeight);
                GradientCanvas.Children.Add(rect);
            }

            // Border
            var border = new System.Windows.Shapes.Rectangle
            {
                Width = gradientWidth,
                Height = gradientHeight,
                Stroke = System.Windows.Media.Brushes.Black,
                StrokeThickness = 1,
                Fill = null
            };
            Canvas.SetLeft(border, offsetX);
            Canvas.SetTop(border, offsetY);
            GradientCanvas.Children.Add(border);

            // Labels
            double labelX = offsetX + gradientWidth + 5;
            AddGradientLabel($"{maxRatio * 100:F1}%", labelX, offsetY - 3);
            AddGradientLabel($"{maxRatio * 50:F1}%", labelX, offsetY + gradientHeight / 2 - 5);
            AddGradientLabel("0%", labelX, offsetY + gradientHeight - 8);
        }

        private void AddGradientLabel(string text, double x, double y)
        {
            var label = new TextBlock
            {
                Text = text,
                FontSize = 10,
                Foreground = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(71, 85, 105))
            };
            Canvas.SetLeft(label, x);
            Canvas.SetTop(label, y);
            GradientCanvas.Children.Add(label);
        }

        private void ExcludedRunsGrid_LoadingRow(object sender, DataGridRowEventArgs e)
        {
            e.Row.Header = (e.Row.GetIndex() + 1).ToString();
        }

        private void UpdateExcludedRunsGrid()
        {
            var excludedData = ScatterPlot.DataPoints
                .Where(p => p.ExclusionReasons != ExclusionReason.None)
                .Select(p => new SelectedPointData
                {
                    RunName = p.RunName,
                    BiologicalCondition = p.BiologicalCondition ?? "",
                    PeptideCount = p.PeptideCount,
                    TicValue = p.TicValue,
                    ProteinCount = p.ProteinCount,
                    ContaminantRatioPercent = $"{p.ContaminantRatio * 100:F2}%",
                    ExclusionReason = p.ExclusionDetail ?? ""
                }).ToList();

            ExcludedRunsGrid.ItemsSource = excludedData;
            ExcludedRunsTab.Header = excludedData.Count > 0
                ? $"Excluded Runs ({excludedData.Count})"
                : "Excluded Runs";
        }

        private void ScatterPlot_SelectionChanged(object sender, PlotSelectionChangedEventArgs e)
        {
            _currentSelectedPoints = e.SelectedPoints;

            // Detect if this is a lasso selection (polygon-based) or checkbox selection
            bool wasLassoActive = _isLassoActive;
            _isLassoActive = ScatterPlot.HasPolygonSelection();

            // Handle lasso state change
            if (_isLassoActive != wasLassoActive)
            {
                var selectedItem = ColorModeComboBox.SelectedItem as ComboBoxItem;
                string colorMode = selectedItem?.Tag?.ToString() ?? "TargetRatio";

                if (_isLassoActive && colorMode == "CellType")
                {
                    // Lasso just became active - disable cell type checkboxes
                    SetCellTypeCheckboxesEnabled(false);
                }
                else if (!_isLassoActive)
                {
                    // Lasso cleared - re-enable cell type checkboxes
                    SetCellTypeCheckboxesEnabled(true);
                }
            }

            if (e.SelectedPoints.Count > 0)
            {
                var gridData = e.SelectedPoints.Select(p => new SelectedPointData
                {
                    RawFileId = _runNameToRawFileId.TryGetValue(p.RunName, out var id) ? id : 0,
                    RunName = p.RunName,
                    PeptideCount = p.PeptideCount,
                    TicValue = p.TicValue,
                    ProteinCount = p.ProteinCount,
                    ContaminantRatioPercent = $"{p.ContaminantRatio * 100:F2}%",
                    CellType = p.PredictedCellType ?? "",
                    CellTypeBrush = GetCellTypeBrush(p.PredictedCellType),
                    BiologicalCondition = p.BiologicalCondition ?? "",
                    CompositeScore = p.PredictionScore != null ? $"{p.PredictionScore.CompositeScore:F3}" : "",
                    IsIncluded = !_excludedRunNames.Contains(p.RunName)
                }).ToList();

                SelectedPointsGridPanel.UpdateGrid(gridData);
                UpdateSelectionRuleText();
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
                UpdateSelectionRuleText();
                ClearSelectionButton.IsEnabled = false;
            }

            // Notify MainWindow for BioTessera update
            SelectionChangedForBioTessera?.Invoke(this, EventArgs.Empty);
        }

        private async void BatchCorrectionCheckBox_Changed(object sender, RoutedEventArgs e)
        {
            // Persist batch correction state
            if (_dimRedSettings != null)
            {
                _dimRedSettings.ApplyBatchCorrection = BatchCorrectionCheckBox.IsChecked == true;
                await _dimRedSettings.SaveAsync(_databaseService);
            }

            LogLogCheckBox_Changed(sender, e);
        }

        /// <summary>
        /// Updates the selection rule text display in the bottom panel
        /// </summary>
        private void UpdateSelectionRuleText()
        {
            int selectedCount = _currentSelectedPoints?.Count ?? 0;

            if (selectedCount == 0)
            {
                SelectedPointsGridPanel.UpdateSelectionRuleText("📋 No selection - draw a lasso or check cell types/conditions to filter");
                return;
            }

            string ruleText;

            if (_isLassoActive)
            {
                // Lasso mode
                string bioConditionPart = GetBioConditionRulePart();
                if (string.IsNullOrEmpty(bioConditionPart))
                {
                    ruleText = $"📋 Lasso selection → {selectedCount} runs  [Cell type filter disabled]";
                }
                else
                {
                    ruleText = $"📋 Lasso ∩ {bioConditionPart} → {selectedCount} runs  [Cell type filter disabled]";
                }
            }
            else
            {
                // Checkbox mode
                string cellTypePart = GetCellTypeRulePart();
                string bioConditionPart = GetBioConditionRulePart();

                if (string.IsNullOrEmpty(cellTypePart) && string.IsNullOrEmpty(bioConditionPart))
                {
                    ruleText = $"📋 All runs → {selectedCount} runs";
                }
                else if (string.IsNullOrEmpty(bioConditionPart))
                {
                    ruleText = $"📋 {cellTypePart} → {selectedCount} runs";
                }
                else if (string.IsNullOrEmpty(cellTypePart))
                {
                    ruleText = $"📋 {bioConditionPart} → {selectedCount} runs";
                }
                else
                {
                    ruleText = $"📋 {cellTypePart} ∩ {bioConditionPart} → {selectedCount} runs";
                }
            }

            SelectedPointsGridPanel.UpdateSelectionRuleText(ruleText);
        }

        private string GetCellTypeRulePart()
        {
            if (_checkedCellTypes.Count == 0 || _cellTypeColorMap == null || _cellTypeColorMap.Count == 0)
                return null;

            if (_checkedCellTypes.Count == _cellTypeColorMap.Count)
                return "All Cell Types";

            var sorted = _checkedCellTypes.OrderBy(c => c).ToList();
            if (sorted.Count <= 3)
            {
                return "(" + string.Join(" ∪ ", sorted) + ")";
            }
            else
            {
                return $"({sorted.Count} cell types)";
            }
        }

        private string GetBioConditionRulePart()
        {
            if (_checkedBioConditions.Count == 0)
                return null;

            var bioConditionColorMap = GenerateBioConditionColorMap();
            if (bioConditionColorMap == null || bioConditionColorMap.Count == 0)
                return null;

            if (_checkedBioConditions.Count == bioConditionColorMap.Count)
                return "All Conditions";

            var sorted = _checkedBioConditions.OrderBy(c => c).ToList();
            if (sorted.Count <= 3)
            {
                return "(" + string.Join(" ∪ ", sorted) + ")";
            }
            else
            {
                return $"({sorted.Count} conditions)";
            }
        }

        /// <summary>
        /// Gets the names of currently selected runs (from lasso or checkbox selection), excluding any excluded runs
        /// </summary>
        public HashSet<string> GetSelectedRunNames()
        {
            if (_currentSelectedPoints == null || _currentSelectedPoints.Count == 0)
                return null; // null means "use all runs"

            var selectedRuns = new HashSet<string>(
                _currentSelectedPoints
                    .Select(p => p.RunName)
                    .Where(name => !_excludedRunNames.Contains(name))
            );

            return selectedRuns.Count > 0 ? selectedRuns : null;
        }

        private void SelectedPointsGridPanel_GridSelectionChanged(object sender, SelectedPointData selectedData)
        {
            if (selectedData == null)
                return;

            // Highlight the selected point on the scatter plot
            ScatterPlot.HighlightPoints(new List<string> { selectedData.RunName });
        }

        private void UseHvpCheckBox_Changed(object sender, RoutedEventArgs e)
        {
            bool isChecked = UseHvpCheckBox.IsChecked == true;

            // Enable/disable the count controls in the popup
            HvpCountTextBox.IsEnabled = isChecked;
            HvpCountUp.IsEnabled = isChecked;
            HvpCountDown.IsEnabled = isChecked;
        }

        private void HvpCountTextBox_PreviewTextInput(object sender, System.Windows.Input.TextCompositionEventArgs e)
        {
            foreach (char c in e.Text)
            {
                if (!char.IsDigit(c))
                {
                    e.Handled = true;
                    return;
                }
            }
        }

        private void HvpCountTextBox_LostFocus(object sender, RoutedEventArgs e)
        {
            ApplyHvpCountValue();
        }

        private void HvpCountTextBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key == System.Windows.Input.Key.Enter)
            {
                ApplyHvpCountValue();
                e.Handled = true;
            }
        }

        private void ApplyHvpCountValue()
        {
            if (int.TryParse(HvpCountTextBox.Text, out int parsed))
            {
                _hvpCount = Math.Max(10, Math.Min(5000, parsed));
                HvpCountTextBox.Text = _hvpCount.ToString();
            }
            else
            {
                HvpCountTextBox.Text = _hvpCount.ToString();
            }
        }

        private void HvpCountUp_Click(object sender, RoutedEventArgs e)
        {
            AdjustHvpCount(50);
        }

        private void HvpCountDown_Click(object sender, RoutedEventArgs e)
        {
            AdjustHvpCount(-50);
        }

        private void AdjustHvpCount(int delta)
        {
            _hvpCount = Math.Clamp(_hvpCount + delta, 10, 5000);
            HvpCountTextBox.Text = _hvpCount.ToString();
        }

        private void ExportDiagnosticsButton_Click(object sender, RoutedEventArgs e)
        {
            ExportDiagnosticsRequested?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>
        /// Gets the current cell type predictions for export
        /// </summary>
        public Dictionary<string, CellTypePredictionResult> GetCellTypePredictions()
        {
            return _cellTypePredictions;
        }

        /// <summary>
        /// Gets the current proteomics data
        /// </summary>
        public ProteomicsData GetCurrentData()
        {
            return _currentData;
        }
    }
}