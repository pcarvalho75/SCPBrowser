using SCPBrowser.GOTools;
using SCPBrowser.Models;
using SCPBrowser.Services;
using SCPBrowser.Controls;
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
        private Dictionary<string, Color> _bioConditionColorMap;

        private HashSet<string> _checkedBioConditions = new HashSet<string>();
        private HashSet<string> _checkedCellTypes = new HashSet<string>();
        private bool _suppressCheckboxEvents = false;
        private bool _suppressSettingsSave = false;

        public event EventHandler CellTypePredictionsRequested;
        public event EventHandler SelectionChangedForBioTessera;
        public event EventHandler<RunInclusionChangedEventArgs> RunInclusionChanged;
        public event EventHandler<double> ContaminantRatioCutoffChanged;
        public event EventHandler ClearAllExclusionsRequested;
        public event EventHandler ExportDiagnosticsRequested;
        private bool _isLassoActive = false;
        private List<HvpResult> _hvpResults;
        private int _hvpCount = 500;

        /// <summary>
        /// Set when the HVP filter was requested but no protein could be scored (VST did not run), so the filter
        /// was bypassed and ALL proteins were used. Surfaced in the plot header — otherwise the plot would claim a
        /// highly-variable selection that is really just the full protein set.
        /// </summary>
        private bool _hvpUnscored;
        private Dictionary<string, int> _plateMappingPerFile;
        private HashSet<string> _checkedPlates = new HashSet<string>();
        private ProjectDatabaseService _databaseService;
        private DimensionReductionSettings? _dimRedSettings;
        private HashSet<string> _contaminantRatioExcludedRuns = new HashSet<string>();
        private double _lastAppliedContaminantCutoff = 1.0;

        public HashSet<string> CheckedBioConditions => _checkedBioConditions;
        public HashSet<string> CheckedCellTypes => _checkedCellTypes;
        public HashSet<string> CheckedPlates => _checkedPlates;

        private async void SaveCheckedStatesAsync()
        {
            try
            {
                if (_databaseService == null) return;
                await _databaseService.SetSettingAsync("CheckedCellTypes", string.Join(",", _checkedCellTypes));
                await _databaseService.SetSettingAsync("CheckedBioConditions", string.Join(",", _checkedBioConditions));
                await _databaseService.SetSettingAsync("CheckedPlates", string.Join(",", _checkedPlates));
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[SaveCheckedStatesAsync] {ex}");
            }
        }

        private async void SaveColorMapsAsync()
        {
            try
            {
                if (_databaseService == null) return;

                static string SerializeColorMap(Dictionary<string, Color> map)
                {
                    if (map == null || map.Count == 0) return string.Empty;
                    return string.Join(",", map.Select(kvp =>
                        $"{Uri.EscapeDataString(kvp.Key)}={kvp.Value.R:X2}{kvp.Value.G:X2}{kvp.Value.B:X2}"));
                }

                await _databaseService.SetSettingAsync("CellTypeColors", SerializeColorMap(_cellTypeColorMap));
                await _databaseService.SetSettingAsync("BioConditionColors", SerializeColorMap(_bioConditionColorMap));
                await _databaseService.SetSettingAsync("PlateColors", SerializeColorMap(_plateColorMap));
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[SaveColorMapsAsync] {ex}");
            }
        }

        public void RestoreColorMaps(string cellTypeColors, string bioConditionColors, string plateColors)
        {
            static Dictionary<string, Color> Deserialize(string raw)
            {
                var map = new Dictionary<string, Color>();
                if (string.IsNullOrEmpty(raw)) return map;
                foreach (var entry in raw.Split(','))
                {
                    var eq = entry.LastIndexOf('=');
                    if (eq < 0) continue;
                    var key = Uri.UnescapeDataString(entry.Substring(0, eq));
                    var hex = entry.Substring(eq + 1);
                    if (hex.Length != 6) continue;
                    if (!byte.TryParse(hex.Substring(0, 2), System.Globalization.NumberStyles.HexNumber,
                            System.Globalization.CultureInfo.InvariantCulture, out var r) ||
                        !byte.TryParse(hex.Substring(2, 2), System.Globalization.NumberStyles.HexNumber,
                            System.Globalization.CultureInfo.InvariantCulture, out var g) ||
                        !byte.TryParse(hex.Substring(4, 2), System.Globalization.NumberStyles.HexNumber,
                            System.Globalization.CultureInfo.InvariantCulture, out var b))
                    {
                        System.Diagnostics.Debug.WriteLine($"[RestoreColorMaps] Skipping malformed hex for key '{key}': '{hex}'");
                        continue;
                    }
                    map[key] = Color.FromRgb(r, g, b);
                }
                return map;
            }

            var ct = Deserialize(cellTypeColors);
            var bc = Deserialize(bioConditionColors);
            var pl = Deserialize(plateColors);

            // Merge saved colors over generated defaults - only override keys that exist in both
            if (ct.Count > 0 && _cellTypeColorMap != null)
                foreach (var kvp in ct)
                    if (_cellTypeColorMap.ContainsKey(kvp.Key))
                        _cellTypeColorMap[kvp.Key] = kvp.Value;

            if (bc.Count > 0)
            {
                // Ensure bio condition map is initialized before merging
                GenerateBioConditionColorMap();
                if (_bioConditionColorMap != null)
                    foreach (var kvp in bc)
                        if (_bioConditionColorMap.ContainsKey(kvp.Key))
                            _bioConditionColorMap[kvp.Key] = kvp.Value;
            }

            if (pl.Count > 0 && _plateColorMap != null)
                foreach (var kvp in pl)
                    if (_plateColorMap.ContainsKey(kvp.Key))
                        _plateColorMap[kvp.Key] = kvp.Value;
        }

        public void RefreshCheckboxUI()
        {
            var selectedItem = ColorModeComboBox.SelectedItem as ComboBoxItem;
            string colorMode = selectedItem?.Tag?.ToString() ?? "TargetRatio";

            _suppressCheckboxEvents = true;
            try
            {
                switch (colorMode)
                {
                    case "CellType":
                        PopulateCellTypeCheckboxes();
                        break;
                    case "Plate":
                        PopulatePlateCheckboxes();
                        break;
                    case "BioCondition":
                        PopulateBioConditionCheckboxes();
                        break;
                }
            }
            finally
            {
                _suppressCheckboxEvents = false;
            }
        }

        public void RestoreCheckedStates(HashSet<string> cellTypes, HashSet<string> bioConditions, HashSet<string> plates)
        {
            _suppressCheckboxEvents = true;
            try
            {
                // CRITICAL: Modify HashSets in place rather than replacing references.
                // _currentOptions in ScatterPlotControl holds a reference to these same
                // HashSet objects. If we replace them with new instances, the options
                // object keeps pointing to the old ones, causing resize to use stale
                // filter state (all items checked) instead of the restored state.
                if (cellTypes != null)
                {
                    _checkedCellTypes.Clear();
                    foreach (var ct in cellTypes) _checkedCellTypes.Add(ct);
                }
                if (bioConditions != null)
                {
                    _checkedBioConditions.Clear();
                    foreach (var bc in bioConditions) _checkedBioConditions.Add(bc);
                }
                if (plates != null)
                {
                    _checkedPlates.Clear();
                    foreach (var p in plates) _checkedPlates.Add(p);
                }
            }
            finally
            {
                _suppressCheckboxEvents = false;
            }

            ScatterPlot.UpdateSelectionWithFilters(_checkedCellTypes, _checkedBioConditions, _checkedPlates, userInteraction: false);
        }

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

            ConfidenceThresholdSlider.Value = Settings.Default.ClassificationConfidenceThreshold;
            ConfidenceThresholdLabel.Text = $"{Settings.Default.ClassificationConfidenceThreshold:P0}";

            _isInitialized = true;
        }

        public void SetPlateColorMap(Dictionary<string, Color> colorMap)
        {
            _plateColorMap = colorMap;
        }

        /// <summary>
        /// Updates the plate color map and refreshes the chart and legend.
        /// Called when the user changes a plate color via the PlateFilterControl.
        /// </summary>
        public void UpdatePlateColors(Dictionary<string, Color> colorMap)
        {
            _plateColorMap = colorMap;

            if (_currentData == null || !_isInitialized)
                return;

            // Rebuild legend checkboxes with updated colors
            var selectedColorItem = ColorModeComboBox.SelectedItem as ComboBoxItem;
            string colorMode = selectedColorItem?.Tag?.ToString() ?? "TargetRatio";
            if (colorMode == "Plate")
            {
                PopulatePlateCheckboxes();
            }

            RefreshChart();
            SaveColorMapsAsync();
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
                PlotGroupBoxHeader.Text = XyAxisLabel();
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
            try
            {
                _databaseService = db;
                _dimRedSettings = await DimensionReductionSettings.LoadAsync(db);
                ApplySettingsToUI(_dimRedSettings);

                if (db != null)
                {
                    var confStr = await db.GetSettingAsync("ClassificationConfidenceThreshold");
                    if (confStr != null && double.TryParse(confStr, out var conf))
                    {
                        _suppressSettingsSave = true;
                        try { ConfidenceThresholdSlider.Value = conf; }
                        finally { _suppressSettingsSave = false; }
                        ConfidenceThresholdLabel.Text = $"{conf:P0}";
                    }

                    // Load any persisted k-means classification so the Selected Points "Cluster" column is populated
                    // on reopen, before the user re-runs k-means.
                    try { _persistedClusterLabels = await db.LoadKMeansLabelsAsync(); }
                    catch { _persistedClusterLabels = null; }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[PeptideTicControl.SetDatabaseService] {ex}");
            }
        }

        /// <summary>
        /// Initializes the Protein Coverage panel with the data sources it needs.
        /// </summary>
        public void InitializeProteinCoverage(
            string fastaPath,
            List<string> parquetPaths,
            Dictionary<string, FastaParserService.ProteinAnnotation> annotations)
        {
            if (_currentData == null)
                return;

            ProteinCoveragePanel.Initialize(
                fastaPath,
                parquetPaths,
                _currentData,
                annotations,
                () => GetSelectedRunNames());
        }

        /// <summary>
        /// Refreshes the protein coverage panel when selection changes.
        /// Only repopulates if the control is visible to avoid unnecessary work.
        /// </summary>
        private void RefreshProteinCoverageList()
        {
            if (ProteinCoveragePanel.IsVisible)
                ProteinCoveragePanel.RefreshForSelection();
        }

        private void BottomTabControl_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (e.Source != BottomTabControl)
                return;

            // Refresh protein list when switching to the coverage tab
            if (BottomTabControl.SelectedIndex == 2 && _currentData != null)
                ProteinCoveragePanel.RefreshForSelection();
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
            SelectComboByTag(NormalizationComboBox, ((int)s.Normalization).ToString());
            SelectComboByTag(MissingValuesComboBox, ((int)s.MissingValues).ToString());

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

        /// <summary>Selects the item whose Tag matches, leaving the selection unchanged if none does.</summary>
        private static void SelectComboByTag(System.Windows.Controls.ComboBox combo, string tag)
        {
            if (combo == null) return;
            foreach (var obj in combo.Items)
                if (obj is System.Windows.Controls.ComboBoxItem item &&
                    string.Equals(item.Tag?.ToString(), tag, StringComparison.Ordinal))
                {
                    combo.SelectedItem = item;
                    return;
                }
        }

        private static int ReadComboTag(System.Windows.Controls.ComboBox combo, int fallback)
        {
            if (combo?.SelectedItem is System.Windows.Controls.ComboBoxItem item &&
                int.TryParse(item.Tag?.ToString(), out int v))
                return v;
            return fallback;
        }

        private DimensionReductionSettings ReadSettingsFromUI()
        {
            var s = DimensionReductionSettings.CreateDefaults();
            s.ZScoreScale = ZScaleCheckBox.IsChecked == true;
            s.Normalization = (Models.CellNormalization)ReadComboTag(NormalizationComboBox, (int)s.Normalization);
            s.MissingValues = (Models.MissingValueMode)ReadComboTag(MissingValuesComboBox, (int)s.MissingValues);
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

        private void SelectedPointsGridPanel_RunInclusionChanged(object? sender, RunInclusionChangedEventArgs e)
        {
            // Bubble up to MainWindow for database persistence
            RunInclusionChanged?.Invoke(this, e);

            // Trigger BioTessera update
            SelectionChangedForBioTessera?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>
        /// Passthrough: tells the grid the exclusions really were deleted, so it can update. Kept as an explicit
        /// confirmation rather than an optimistic update because the host's delete is async and can fail.
        /// </summary>
        public void ConfirmExclusionsCleared() => SelectedPointsGridPanel?.ConfirmExclusionsCleared();

        private void SelectedPointsGridPanel_ClearAllExclusionsRequested(object? sender, EventArgs e)
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
                _checkedPlates.Clear();
                _currentSelectedPoints.Clear();
                _bioConditionColorMap = null;
            }

            _currentData = data;

            // A gene matrix has no peptide dimension — hide the redundant "Peptides" column in the selected-points grid.
            SelectedPointsGridPanel.SetGeneMatrixMode(data?.IsGeneMatrix == true);

            // Keep coverage panel in sync with current data
            ProteinCoveragePanel.UpdateProteomicsData(data);

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
            bool hasPredictions = predictions != null && predictions.Count > 0;
            ColorByCellTypeItem.IsEnabled = hasPredictions;
            UpdateCellTypeItemAppearance(hasPredictions);

            // Re-apply confidence threshold from slider to sync with current UI state
            if (hasPredictions)
            {
                double threshold = ConfidenceThresholdSlider.Value;
                ApplyConfidenceThreshold(threshold);
            }

            // Show/hide the export diagnostics button
            ExportDiagnosticsButton.Visibility = (predictions != null && predictions.Count > 0) 
                ? Visibility.Visible 
                : Visibility.Collapsed;

            if (_currentData != null)
            {
                // Auto-select Cell Type mode if requested and predictions are available
                if (selectCellTypeMode && predictions != null && predictions.Count > 0)
                {
                    ColorModeComboBox.SelectedIndex = 3; // Cell Type
                    // ColorModeComboBox_SelectionChanged will be triggered automatically, which populates checkboxes
                }

                // If cell type mode is already selected, populate the checkboxes
                var selectedItem = ColorModeComboBox.SelectedItem as ComboBoxItem;
                string colorMode = selectedItem?.Tag?.ToString() ?? "TargetRatio";
                if (colorMode == "CellType" && _cellTypeColorMap != null && _cellTypeColorMap.Count > 0)
                {
                    PopulateCellTypeCheckboxes();
                    BioConditionPanel.Visibility = Visibility.Visible;
                    ConfidenceThresholdPanel.Visibility = Visibility.Visible;
                }

                RefreshChart();
            }
        }

        public void EnableCellTypeClassification(bool isAvailable)
        {
            ColorByCellTypeItem.IsEnabled = isAvailable;
            UpdateCellTypeItemAppearance(isAvailable);
        }

        private void UpdateCellTypeItemAppearance(bool isAvailable)
        {
            if (isAvailable)
            {
                ColorByCellTypeItem.Background = null;
                ColorByCellTypeItem.ToolTip = "Color data points by predicted cell type";
            }
            else
            {
                ColorByCellTypeItem.Background = new SolidColorBrush(Color.FromRgb(0xFF, 0xEC, 0xEC));
                // There is no "Reference" menu - the importer is Import → Omic Profile.... This tooltip is the only
                // instruction a user gets for the step that unlocks cell type classification, so it has to name a
                // path that exists.
                ColorByCellTypeItem.ToolTip = "Import a transcriptomic or proteomic reference via Import → Omic Profile... to enable cell type classification.";
            }
        }

        private void ConfidenceThresholdSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (!_isInitialized || _cellTypePredictions == null || _cellTypePredictions.Count == 0)
                return;

            double threshold = ConfidenceThresholdSlider.Value;
            ConfidenceThresholdLabel.Text = $"{threshold:P0}";

            if (!_suppressSettingsSave)
            {
                Settings.Default.ClassificationConfidenceThreshold = threshold;
                Settings.Default.Save();
                if (_databaseService != null)
                    _ = _databaseService.SetSettingAsync("ClassificationConfidenceThreshold", threshold.ToString());
            }

            ApplyConfidenceThreshold(threshold);
        }

        public void ApplyConfidenceThresholdFromSettings()
        {
            double threshold = Settings.Default.ClassificationConfidenceThreshold;

            _suppressSettingsSave = true;
            try
            {
                ConfidenceThresholdSlider.Value = threshold;
            }
            finally
            {
                _suppressSettingsSave = false;
            }

            ConfidenceThresholdLabel.Text = $"{threshold:P0}";

            if (_cellTypePredictions == null || _cellTypePredictions.Count == 0)
                return;

            ApplyConfidenceThreshold(threshold);
        }

        private void ApplyConfidenceThreshold(double threshold)
        {
            foreach (var kvp in _cellTypePredictions)
            {
                var prediction = kvp.Value;
                if (prediction.Scores != null && prediction.Scores.Count > 0)
                {
                    string originalTop = prediction.Scores.First().Key;
                    if (originalTop != "Undetermined")
                        prediction.TopCellType = originalTop;
                }
                if (prediction.Confidence < threshold)
                {
                    prediction.TopCellType = "Undetermined";
                }
            }

            var selectedItem = ColorModeComboBox.SelectedItem as ComboBoxItem;
            if (selectedItem?.Tag?.ToString() == "CellType")
            {
                PopulateCellTypeCheckboxes();
                UpdatePieChart("CellType");
                if (_currentData != null)
                    RefreshChart();
            }
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

            // The k selector is only shown in k-means mode.
            KMeansPanel.Visibility = mode == "KMeans" ? Visibility.Visible : Visibility.Collapsed;

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
                ConfidenceThresholdPanel.Visibility = Visibility.Visible;
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
                ConfidenceThresholdPanel.Visibility = Visibility.Collapsed;
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
                ConfidenceThresholdPanel.Visibility = Visibility.Collapsed;
                PopulatePlateCheckboxes();
                UpdatePieChart("Plate");
                BioConditionPanel.Visibility = Visibility.Visible;
            }
            else if (mode == "KMeans")
            {
                // RefreshChart (below) renders, clusters on the displayed axes, and fills the pie legend.
                LegendPanelTitle.Text = "K-means Clusters";
                CheckboxScrollViewer.Visibility = Visibility.Collapsed;
                ContaminantRatioLegendPanel.Visibility = Visibility.Collapsed;
                DistributionPieChart.Visibility = Visibility.Visible;
                ConfidenceThresholdPanel.Visibility = Visibility.Collapsed;
                BioConditionPanel.Visibility = Visibility.Visible;
            }
            else
            {
                // Contaminant Ratio mode — show right panel with gradient + slider
                LegendPanelTitle.Text = "Contaminant Ratio";
                CheckboxScrollViewer.Visibility = Visibility.Collapsed;
                ContaminantRatioLegendPanel.Visibility = Visibility.Visible;
                DistributionPieChart.Visibility = Visibility.Collapsed;
                ConfidenceThresholdPanel.Visibility = Visibility.Collapsed;
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
                        SaveCheckedStatesAsync();
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
                        SaveCheckedStatesAsync();
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

        // ----- k-means clustering (Color-by = "K-means") -----------------------------------------------------------
        // Clusters are computed on the DISPLAYED axes as a post-render recolor, recomputed on k / view changes but NOT
        // on checkbox / lasso toggles, so labels stay stable for persistence + export. Fit on non-contaminant cells;
        // every cell is then assigned to its nearest centroid so all cells carry a label.
        private int _lastK = 5;
        private Dictionary<int, Color> _clusterColorsById = new Dictionary<int, Color>();
        private Dictionary<string, Color> _clusterColorMap = new Dictionary<string, Color>(); // "Cluster N" → colour, for the pie
        private Dictionary<string, string> _persistedClusterLabels; // run → "Cluster N" loaded from DB (fallback for the grid on reopen)

        private bool _kmeansRunning;
        private bool _kmeansRerunRequested;

        private async System.Threading.Tasks.Task ApplyKMeansAsync()
        {
            if (ScatterPlot.DataPoints == null || ScatterPlot.DataPoints.Count == 0) return;

            // Re-entrancy guard: if a clustering is already running (e.g. a debounced k-change lands mid-compute), just
            // request a re-run so the latest k/view wins, instead of overlapping two passes over the same points.
            if (_kmeansRunning) { _kmeansRerunRequested = true; return; }
            _kmeansRunning = true;

            // The fit runs off the UI thread (no GUI lock) — show a brief wait overlay while it computes + recolors.
            // Everything is inside the try so the finally always clears _kmeansRunning + hides the overlay.
            var mainWindow = Window.GetWindow(this) as MainWindow;
            try
            {
                mainWindow?.LoadingOverlay.SetMessage("Clustering cells");
                mainWindow?.LoadingOverlay.Show();
                do
                {
                    _kmeansRerunRequested = false;
                    await RunKMeansCoreAsync(mainWindow);
                } while (_kmeansRerunRequested);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[KMeans] {ex}");
            }
            finally
            {
                _kmeansRunning = false;
                mainWindow?.LoadingOverlay.Hide();
            }
        }

        private async System.Threading.Tasks.Task RunKMeansCoreAsync(MainWindow mainWindow)
        {
            var pts = ScatterPlot.DataPoints;
            if (pts == null || pts.Count == 0) return;

            int k = (KValueBox?.Value is int v && v >= 1) ? v : _lastK;
            _lastK = k;

            // Cluster on the DISPLAYED coordinates: PCA/UMAP store the embedding in XData/YData, but the default
            // Peptide/TIC view leaves those at 0 — so there we cluster on the actual axis values (peptide count, TIC).
            // KMeansClusteringService z-scores per dimension, so the wildly different peptide-vs-TIC scales are handled.
            var viewItem = ViewModeComboBox.SelectedItem as ComboBoxItem;
            string viewMode = viewItem?.Tag?.ToString() ?? "PeptideTic";
            bool dimReduced = viewMode == "PCA" || viewMode == "UMAP";

            int n = pts.Count;
            var coords = new double[n][];
            var fitMask = new bool[n];
            for (int i = 0; i < n; i++)
            {
                coords[i] = dimReduced
                    ? new[] { pts[i].XData, pts[i].YData }
                    : new[] { (double)pts[i].PeptideCount, pts[i].TicValue };
                fitMask[i] = (pts[i].ExclusionReasons & ExclusionReason.ContaminantRatio) == 0;
            }

            mainWindow?.LoadingOverlay.SetProgress($"Running k-means (k={k}) on {n:N0} cells…");

            var result = await System.Threading.Tasks.Task.Run(
                () => KMeansClusteringService.Fit(coords, fitMask, k, seed: 42));

            var runToCluster = new Dictionary<string, int>();
            for (int i = 0; i < n; i++)
                if (!string.IsNullOrEmpty(pts[i].RunName))
                    runToCluster[pts[i].RunName] = result.Assignments[i];

            _clusterColorsById = GenerateClusterColorMap(result.K);
            _clusterColorMap = _clusterColorsById.ToDictionary(kv => "Cluster " + (kv.Key + 1), kv => kv.Value);

            ScatterPlot.ApplyClusterColors(runToCluster, _clusterColorsById);

            // Refresh the cached classification + the Selected Points "Cluster" column to reflect this run.
            _persistedClusterLabels = runToCluster.Where(kv => kv.Value >= 0)
                .ToDictionary(kv => kv.Key, kv => "Cluster " + (kv.Value + 1));
            if (_currentSelectedPoints != null && _currentSelectedPoints.Count > 0)
                SelectedPointsGridPanel.UpdateGrid(BuildSelectedPointsGrid(_currentSelectedPoints));

            string basis = $"{CurrentViewBasis()}, k={result.K}";
            if (KMeansStatusLabel != null) KMeansStatusLabel.Text = basis;

            UpdatePieChart("KMeans");

            // Persist so the labels survive reopen and can drive the PLP export.
            if (_databaseService != null)
            {
                try { await _databaseService.SaveKMeansAssignmentsAsync(runToCluster, result.K, basis); }
                catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[KMeans persist] {ex}"); }
            }
        }

        private static Dictionary<int, Color> GenerateClusterColorMap(int k)
        {
            var map = new Dictionary<int, Color>();
            for (int i = 0; i < Math.Max(1, k); i++)
                map[i] = _colorPalette[i % _colorPalette.Length];
            return map;
        }

        /// <summary>Brush for a cluster label, matching its scatter colour. Falls back to the palette colour derived
        /// from the cluster number so persisted-only labels (before a re-run) still colour correctly.</summary>
        private Brush GetClusterBrush(string label)
        {
            if (string.IsNullOrEmpty(label)) return Brushes.Black;
            if (_clusterColorMap != null && _clusterColorMap.TryGetValue(label, out var c))
                return new SolidColorBrush(c);
            if (label.StartsWith("Cluster ", StringComparison.OrdinalIgnoreCase) &&
                int.TryParse(label.Substring(8), out int n) && n >= 1)
                return new SolidColorBrush(_colorPalette[(n - 1) % _colorPalette.Length]);
            return Brushes.Black;
        }

        /// <summary>Builds the Selected Points grid rows for the given points, including the live-or-persisted cluster.</summary>
        private List<SelectedPointData> BuildSelectedPointsGrid(IEnumerable<DataPoint> pts)
        {
            return pts.Select(p =>
            {
                // Prefer this session's live cluster; fall back to the persisted classification (so the column is
                // populated on reopen, before the user re-runs k-means).
                string clusterLabel = p.ClusterLabel;
                if (string.IsNullOrEmpty(clusterLabel) && _persistedClusterLabels != null)
                    _persistedClusterLabels.TryGetValue(p.RunName, out clusterLabel);

                return new SelectedPointData
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
                    IsIncluded = !_excludedRunNames.Contains(p.RunName),
                    ClusterLabel = clusterLabel ?? "",
                    ClusterId = ParseClusterId(clusterLabel),
                    ClusterBrush = GetClusterBrush(clusterLabel)
                };
            }).ToList();
        }

        /// <summary>"Cluster N" → N-1 (the 0-based sort key); -1 for unassigned/blank.</summary>
        private static int ParseClusterId(string label) =>
            !string.IsNullOrEmpty(label) && label.StartsWith("Cluster ", StringComparison.OrdinalIgnoreCase)
                && int.TryParse(label.Substring(8), out int n) && n >= 1 ? n - 1 : -1;

        /// <summary>Short label for the axes k-means clustered on, e.g. "UMAP axes" — for the legend + persisted basis.</summary>
        private string CurrentViewBasis()
        {
            var item = ViewModeComboBox.SelectedItem as ComboBoxItem;
            string viewMode = item?.Tag?.ToString() ?? "PeptideTic";
            return viewMode switch
            {
                "PCA" => "PCA axes",
                "UMAP" => "UMAP axes",
                _ => "Peptide/TIC axes"
            };
        }

        private System.Windows.Threading.DispatcherTimer _kmeansDebounce;

        private void KValueBox_ValueChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
        {
            if (!_isInitialized || _currentData == null) return;
            if (CurrentColoringMode() != "KMeans") return;

            // Debounce: coalesce rapid spinner changes into one re-cluster so dialing k doesn't fire a storm of
            // computes / wait overlays.
            if (_kmeansDebounce == null)
            {
                _kmeansDebounce = new System.Windows.Threading.DispatcherTimer
                {
                    Interval = TimeSpan.FromMilliseconds(250)
                };
                _kmeansDebounce.Tick += async (s, ev) =>
                {
                    _kmeansDebounce.Stop();
                    await ApplyKMeansAsync();
                };
            }
            _kmeansDebounce.Stop();
            _kmeansDebounce.Start();
        }

        private Dictionary<string, int> GetCategoryCounts(string coloringMode)
        {
            // Single source of truth: count the cells CURRENTLY in the selection — the same set the Selected Points
            // tab, the analyses, and the PLP export use (filters + lasso via _currentSelectedPoints, minus manual
            // exclusions). Previously this recomputed from the full _currentData and ignored the selection, so the
            // pie didn't track unchecking a condition / lassoing.
            var counts = new Dictionary<string, int>();
            foreach (var p in GetIncludedSelectedPoints())
            {
                string key = coloringMode switch
                {
                    "CellType" => string.IsNullOrEmpty(p.PredictedCellType) ? "Unknown" : p.PredictedCellType,
                    "BioCondition" => string.IsNullOrEmpty(p.BiologicalCondition) ? "Unknown" : p.BiologicalCondition,
                    "Plate" => string.IsNullOrEmpty(p.PlateName) ? "Unknown" : p.PlateName,
                    "KMeans" => string.IsNullOrEmpty(p.ClusterLabel) ? "Unassigned" : p.ClusterLabel,
                    _ => null
                };
                if (key == null) continue;
                counts[key] = counts.GetValueOrDefault(key) + 1;
            }
            return counts;
        }

        /// <summary>The cells currently in the selection AND not manually excluded — the single set the pie, the
        /// Selected Points tab, the analyses and the PLP export all share.</summary>
        private List<DataPoint> GetIncludedSelectedPoints()
        {
            if (_currentSelectedPoints == null) return new List<DataPoint>();
            return _currentSelectedPoints
                .Where(p => _excludedRunNames == null || !_excludedRunNames.Contains(p.RunName))
                .ToList();
        }

        /// <summary>The active scatter colour mode ("CellType" | "BioCondition" | "Plate" | "TargetRatio").</summary>
        private string CurrentColoringMode()
        {
            var item = ColorModeComboBox.SelectedItem as ComboBoxItem;
            return item?.Tag?.ToString() ?? "TargetRatio";
        }

        /// <summary>Honest base-scatter label: gene-matrix (.xlsx) imports have no peptide dimension, so the axis shows
        /// proteins/genes and summed intensity rather than peptides/TIC.</summary>
        private string XyAxisLabel() => (_currentData?.IsGeneMatrix == true) ? "Proteins vs Total Intensity" : "Peptides vs TIC";

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
            else if (coloringMode == "KMeans")
                colorMap = _clusterColorMap;

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
            if (_bioConditionColorMap != null && _bioConditionColorMap.Count > 0)
                return _bioConditionColorMap;

            if (_currentData == null || _currentData.BiologicalConditionPerFile.Count == 0)
                return new Dictionary<string, Color>();

            var uniqueConditions = _currentData.BiologicalConditionPerFile.Values
                .Where(c => !string.IsNullOrEmpty(c))
                .Distinct()
                .OrderBy(c => c)
                .ToList();

            _bioConditionColorMap = new Dictionary<string, Color>();
            for (int i = 0; i < uniqueConditions.Count; i++)
            {
                double hue = (double)i / uniqueConditions.Count * 360.0;
                _bioConditionColorMap[uniqueConditions[i]] = ColorMapper.HsvToRgb(hue, 0.7, 0.9);
            }
            return _bioConditionColorMap;
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
                        SaveCheckedStatesAsync();
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
                        SaveCheckedStatesAsync();
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
                    SaveColorMapsAsync();
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
        /// <summary>
        /// Fire-and-forget refresh, as used by the many UI handlers that do not need to await it.
        /// </summary>
        private void RefreshChart() => _ = RefreshChartAsync();

        /// <summary>
        /// Awaitable refresh, for callers that must know when the (possibly seconds-long) recompute has finished -
        /// e.g. to re-enable the control that triggered it.
        /// </summary>
        private async Task RefreshChartAsync()
        {
            try
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

            // The k selector is only relevant in k-means mode.
            KMeansPanel.Visibility = colorMode == "KMeans" ? Visibility.Visible : Visibility.Collapsed;

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
            else if (colorMode == "KMeans")
            {
                // The cluster distribution pie is the legend; it's refreshed by ApplyKMeansAsync after clustering.
                LegendPanelTitle.Text = "K-means Clusters";
                CheckboxScrollViewer.Visibility = Visibility.Collapsed;
                ContaminantRatioLegendPanel.Visibility = Visibility.Collapsed;
                DistributionPieChart.Visibility = Visibility.Visible;
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
                // Hiding or revealing grey dots changes WHICH cells the embedding is built from, which is just as
                // much a recompute as changing a setting. Without this the work fell through to UpdatePlot and ran
                // synchronously on the UI thread, freezing the window with no wait screen.
                bool cohortChanged = ScatterPlot.WillCohortChange(options);
                willInvalidate = batchChanged || dimRedChanged || dataChanged || cohortChanged;
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

            // Deferred SelectionChanged event is now handled inside UpdatePlot itself
            // (via Dispatcher.BeginInvoke at DispatcherPriority.Loaded). This ensures
            // every caller of UpdatePlot (RefreshChart, SizeChanged, etc.) gets the
            // deferred event automatically, eliminating the need for a duplicate here.
            //
            // ExclusionReasons are already set by the synchronous UpdateSelectionWithFilters
            // call inside UpdatePlot, so we can safely read them here.
            UpdateExcludedRunsGrid();

            // Surface any batch-correction warning (skipped / fell back to plate-only / failed) in the plot header.
            UpdateBatchCorrectionWarning();

            // K-means colours points by cluster computed on the displayed coordinates, so it must run AFTER UpdatePlot
            // (XData/YData and the per-point selection state now exist).
            if (colorMode == "KMeans")
                await ApplyKMeansAsync();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[RefreshChart] {ex}");
            }
        }

        /// <summary>Shows/hides the batch-correction warning in the plot header based on the scatter's last run.</summary>
        private void UpdateBatchCorrectionWarning()
        {
            string msg = ScatterPlot?.BatchCorrectionWarning;
            if (string.IsNullOrEmpty(msg))
            {
                BatchCorrectionWarningText.Visibility = Visibility.Collapsed;
                BatchCorrectionWarningText.ToolTip = null;
            }
            else
            {
                BatchCorrectionWarningText.Text = "⚠ " + msg;
                BatchCorrectionWarningText.ToolTip = msg; // full text on hover (header may trim)
                BatchCorrectionWarningText.Visibility = Visibility.Visible;
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

            // Filter OFF must mean "use every quantified protein". _hvpResults carries IsHighlyVariable = true for
            // the top 2000 of the ranking pool, and the embedding keeps only flagged proteins - so returning the
            // list unchanged here silently restricted PCA/UMAP to those 2000 even with the box unchecked. On a
            // 4000-protein Astral dataset that dropped half the quantified proteome from every embedding without
            // the user asking, and would be described incorrectly in a paper. Returning null makes the consumer
            // fall through to its all-proteins branch.
            if (_dimRedSettings == null || !_dimRedSettings.UseHvpFilter)
            {
                _hvpUnscored = false;
                return null;
            }

            {
                // If VST never ran (too few proteins cleared the detection filter — e.g. a filtered view with under
                // ~20 cells), every VarianceStandardized is 0.0 and this ordering degenerates to "alphabetically
                // first N" while still being labelled "top N highly variable". Fall back to ALL proteins instead of
                // silently handing PCA/UMAP an arbitrary protein set; UpdatePlotHeader flags it to the user.
                if (!_hvpResults.Any(h => h.IsScored))
                {
                    _hvpUnscored = true;
                    return _hvpResults;
                }
                _hvpUnscored = false;

                // Return only top N proteins by VarianceStandardized
                return _hvpResults
                    .Where(h => h.IsScored)
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
                        IsScored = h.IsScored,
                        IsHighlyVariable = true  // Mark all selected as HVP
                    })
                    .ToList();
            }
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
                baseHeader = $"{XyAxisLabel()} ({fileCount} files, {predictedCount} with cell type predictions)";
            }
            else if (options.UseBioConditionColoring && _currentData.BiologicalConditionPerFile.Count > 0)
            {
                int conditionCount = _currentData.BiologicalConditionPerFile.Values.Where(c => !string.IsNullOrEmpty(c)).Distinct().Count();
                baseHeader = $"{XyAxisLabel()} ({fileCount} files, {conditionCount} biological conditions)";
            }
            else if (options.UsePlateColoring && options.PlateColorMap != null && options.PlateColorMap.Count > 0)
            {
                int plateCount = options.PlateColorMap.Count;
                baseHeader = $"{XyAxisLabel()} ({fileCount} files, {plateCount} plates)";
            }
            else
            {
                baseHeader = $"{XyAxisLabel()} ({fileCount} files)";
            }

            // State how many proteins the embedding actually used. Which proteins enter PCA/UMAP is a methods-level
            // decision, and it was previously invisible - the filter checkbox did not mean what it appeared to.
            if (options.UsePcaView || options.UseUmapView)
            {
                int used = options.HvpResults?.Count(h => h.IsHighlyVariable)
                           ?? _currentData?.ProteinQuantMatrix?.Count
                           ?? 0;
                if (used > 0)
                {
                    bool filtered = options.DimRedSettings?.UseHvpFilter == true && !_hvpUnscored;
                    baseHeader += filtered
                        ? $"  |  {used:N0} highly variable proteins"
                        : $"  |  {used:N0} proteins (all quantified)";
                }

                // How much of the matrix was imputed rather than measured. At high missingness the embedding is
                // substantially a statement about detection, and the reader should be told so on the figure.
                double missing = ScatterPlot.LastMissingRate;
                if (missing > 0)
                    baseHeader += $"  |  {missing:P0} missing";

                // Say when the embedding was rebuilt on a subset. Two identical-looking UMAPs computed on
                // different cohorts are not comparable, so the cohort has to be visible on the figure itself.
                int droppedByPopulation = ScatterPlot.LastExcludedByPopulationFilter;
                if (droppedByPopulation > 0)
                    baseHeader += $"  |  recomputed without {droppedByPopulation} hidden cell{(droppedByPopulation == 1 ? "" : "s")}";

                if (ScatterPlot.LastCohortTooSmall)
                    baseHeader += "  |  too few cells left to compute an embedding — re-check a population";
            }

            // The HVP filter was asked for but nothing could be scored, so it was bypassed. Say so — a silent
            // fallback would present an embedding computed on ALL proteins as a highly-variable-protein embedding.
            if (_hvpUnscored && options.DimRedSettings?.UseHvpFilter == true)
                baseHeader += "  |  HVP filter inactive: too few cells to score variability — using all proteins";

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
                _checkedPlates.Clear();

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

        private void SelectAllButton_Click(object sender, RoutedEventArgs e)
        {
            _suppressCheckboxEvents = true;

            try
            {
                ScatterPlot.SelectAll();

                // Determine which checked set the current checkboxes belong to
                var selectedItem = ColorModeComboBox.SelectedItem as ComboBoxItem;
                string colorMode = selectedItem?.Tag?.ToString() ?? "TargetRatio";

                HashSet<string> activeSet = colorMode switch
                {
                    "CellType" => _checkedCellTypes,
                    "Plate" => _checkedPlates,
                    _ => _checkedBioConditions
                };

                activeSet.Clear();

                foreach (var child in BioConditionCheckboxes.Children)
                {
                    if (child is CheckBox cb)
                    {
                        cb.IsChecked = true;
                        if (cb.Tag is string tag)
                            activeSet.Add(tag);
                    }
                }

                _isLassoActive = false;
                SetCellTypeCheckboxesEnabled(true);
                ClearSelectionButton.IsEnabled = false;
            }
            finally
            {
                _suppressCheckboxEvents = false;
            }
        }

        private bool _hideGreyRecomputing;

        private async void HideGreyDotsCheckBox_Changed(object sender, RoutedEventArgs e)
        {
            ScatterPlot.SetHideUnselected(HideGreyDotsCheckBox.IsChecked == true);

            // In PCA/UMAP the greyed cells are not merely dimmed - they were used to BUILD the axes. Hiding them
            // therefore has to rebuild the embedding from the remaining cells, otherwise the survivors are shown
            // in a space defined partly by data the user just removed. In the raw scatter views the axes are
            // per-cell measurements, so hiding is purely cosmetic and no recompute is needed.
            var selectedItem = ViewModeComboBox.SelectedItem as ComboBoxItem;
            string viewMode = selectedItem?.Tag?.ToString() ?? "PeptideTic";
            if (_currentData == null || (viewMode != "PCA" && viewMode != "UMAP"))
                return;

            // The recompute takes seconds on a real dataset. Await it and hold the checkbox disabled meanwhile,
            // so a second toggle cannot queue an overlapping embedding computation on top of the first.
            if (_hideGreyRecomputing) return;
            _hideGreyRecomputing = true;
            HideGreyDotsCheckBox.IsEnabled = false;
            try
            {
                await RefreshChartAsync();
            }
            finally
            {
                HideGreyDotsCheckBox.IsEnabled = true;
                _hideGreyRecomputing = false;
            }
        }

        private System.Windows.Threading.DispatcherTimer _contaminantCutoffDebounce;
        private double _pendingContaminantCutoff = 1.0;

        private void ContaminantCutoffSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (!_isInitialized) return;

            double percent = ContaminantCutoffSlider.Value;
            ContaminantCutoffValueLabel.Text = percent >= 100 ? "Off" : $"{percent:F0}%";

            double newCutoff = percent / 100.0;
            if (Math.Abs(newCutoff - _lastAppliedContaminantCutoff) < 0.005)
            {
                // Back at the value that is already applied - drop any pending pass so a drag that returns to where
                // it started does not re-run the pipeline for nothing.
                _contaminantCutoffDebounce?.Stop();
                return;
            }

            // Debounce, like the k-means spinner above. The slider is continuous (no tick snapping), and every raise
            // runs the whole filter pipeline - three deep copies of the protein quant matrix plus the O(n^2 log n)
            // HVP LOESS fit - synchronously on this thread, with no loading overlay. Firing per tick froze the window
            // for the length of the drag; coalescing into one pass once the user settles keeps the reported numbers
            // identical while the UI stays alive.
            _pendingContaminantCutoff = newCutoff;

            if (_contaminantCutoffDebounce == null)
            {
                _contaminantCutoffDebounce = new System.Windows.Threading.DispatcherTimer
                {
                    Interval = TimeSpan.FromMilliseconds(400)
                };
                _contaminantCutoffDebounce.Tick += (s, ev) =>
                {
                    _contaminantCutoffDebounce.Stop();
                    if (Math.Abs(_pendingContaminantCutoff - _lastAppliedContaminantCutoff) < 0.005)
                        return;

                    // Set before raising: the filter pass ends up back in UpdateChart, which reads
                    // _lastAppliedContaminantCutoff as the cutoff currently in force.
                    _lastAppliedContaminantCutoff = _pendingContaminantCutoff;
                    ContaminantRatioCutoffChanged?.Invoke(this, _pendingContaminantCutoff);
                };
            }
            _contaminantCutoffDebounce.Stop();
            _contaminantCutoffDebounce.Start();
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

        private void ScatterPlot_SelectionChanged(object? sender, PlotSelectionChangedEventArgs e)
        {


            _currentSelectedPoints = e.SelectedPoints;

            // Keep the distribution pie chart in sync with the current selection (filters + lasso, minus manual
            // exclusions). Fires on every selection change — unchecking a condition, lassoing, clearing — so the pie
            // always matches the Selected Points tab and the PLP export.
            UpdatePieChart(CurrentColoringMode());

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
                SelectedPointsGridPanel.UpdateGrid(BuildSelectedPointsGrid(e.SelectedPoints));
                UpdateSelectionRuleText();
                ClearSelectionButton.IsEnabled = true;
            }
            else
            {
                // Selection is empty - clear ALL filter sets if any were checked.
                // All three sets must be cleared together for consistency.
                // The Clear button handler (ClearSelectionButton_Click) already clears
                // all three; this keeps the SelectionChanged path in sync.
                bool hadCheckedItems = _checkedCellTypes.Count > 0 || _checkedBioConditions.Count > 0 || _checkedPlates.Count > 0;

                if (hadCheckedItems)
                {

                    _suppressCheckboxEvents = true;
                    try
                    {
                        _checkedCellTypes.Clear();
                        _checkedBioConditions.Clear();
                        _checkedPlates.Clear();

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

            // Keep excluded runs grid in sync after any selection change
            UpdateExcludedRunsGrid();

            // Refresh protein coverage list for the new selection
            RefreshProteinCoverageList();

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

        private void SelectedPointsGridPanel_GridSelectionChanged(object? sender, SelectedPointData selectedData)
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