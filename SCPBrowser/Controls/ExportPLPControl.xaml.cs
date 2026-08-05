// ExportPLPControl.xaml.cs
// Code-behind for the PLP export control.
// Location: SCPBrowser/Controls/ExportPLPControl.xaml.cs

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using SCPBrowser.Services;

namespace SCPBrowser
{
    /// <summary>
    /// View model for class breakdown items in the summary.
    /// </summary>
    public class ClassBreakdownItem
    {
        public string Label { get; set; }
        public int Count { get; set; }
        public string CountText => $"{Count} runs";
        public double BarWidth { get; set; }
    }

    public partial class ExportPLPControl : UserControl
    {
        // Data references set by MainWindow
        private ProteomicsData _data;
        private HashSet<string> _excludedRunNames;
        private Dictionary<string, CellTypePredictionResult> _cellTypePredictions;
        private Dictionary<string, int> _rawFileToPlateId;
        private Dictionary<int, string> _plateIdToName;
        private Dictionary<string, FastaParserService.ProteinAnnotation> _fastaAnnotations;
        private HashSet<string> _checkedBioConditions;
        private HashSet<string> _checkedCellTypes;
        private HashSet<string> _checkedPlates;

        // Single source of truth from the Explorer scatter: the runs currently in the selection (filters + lasso,
        // minus manual exclusions). When non-null this supersedes the checkbox/exclusion re-derivation in
        // GetSelectedRuns so the lasso constrains the export. Null only if the Explorer hasn't rendered a selection.
        private List<string> _selectedRunNames;

        // Persisted k-means cluster labels (run name → "Cluster N"); enables the "K-means cluster" export label mode.
        private Dictionary<string, string> _kmeansLabels;

        private bool _isInitialized = false;

        // Merge map: source cell type → target cell type
        private Dictionary<string, string> _cellTypeMergeMap = new Dictionary<string, string>();

        public ExportPLPControl()
        {
            InitializeComponent();
        }

        /// <summary>
        /// Initializes the control with all required data from MainWindow.
        /// </summary>
        public void Initialize(
            ProteomicsData data,
            HashSet<string> excludedRunNames,
            Dictionary<string, CellTypePredictionResult> cellTypePredictions,
            Dictionary<string, int> rawFileToPlateId,
            Dictionary<int, string> plateIdToName,
            Dictionary<string, FastaParserService.ProteinAnnotation> fastaAnnotations,
            HashSet<string> checkedBioConditions = null,
            HashSet<string> checkedCellTypes = null,
            HashSet<string> checkedPlates = null,
            List<string> selectedRunNames = null,
            Dictionary<string, string> kmeansLabels = null)
        {
            _data = data;
            _excludedRunNames = excludedRunNames ?? new HashSet<string>();
            _cellTypePredictions = cellTypePredictions;
            _rawFileToPlateId = rawFileToPlateId;
            _plateIdToName = plateIdToName;
            _fastaAnnotations = fastaAnnotations;
            _checkedBioConditions = checkedBioConditions;
            _checkedCellTypes = checkedCellTypes;
            _checkedPlates = checkedPlates;
            _selectedRunNames = selectedRunNames;
            _kmeansLabels = kmeansLabels;

            _isInitialized = true;

            UpdateLabelModeAvailability();
            UpdateMergePanelVisibility();
            UpdateSummary();
        }

        /// <summary>
        /// Greys out the label modes whose backing data does not exist yet and moves the selection off them.
        /// Secondary defaults to Cell Type, so on a project with no classification every run was skipped for
        /// want of a label and the panel offered nothing but a skip count to explain it.
        /// </summary>
        private void UpdateLabelModeAvailability()
        {
            bool hasCellTypes = _cellTypePredictions != null &&
                                _cellTypePredictions.Values.Any(p => p != null && !string.IsNullOrEmpty(p.TopCellType));
            bool hasKMeans = _kmeansLabels != null &&
                             _kmeansLabels.Values.Any(v => !string.IsNullOrEmpty(v));
            bool hasPlates = _rawFileToPlateId != null && _rawFileToPlateId.Count > 0 &&
                             _plateIdToName != null && _plateIdToName.Count > 0;

            SetLabelModeState(PrimaryCellTypeItem, "Cell Type", hasCellTypes, "requires classification");
            SetLabelModeState(SecondaryCellTypeItem, "Cell Type", hasCellTypes, "requires classification");
            SetLabelModeState(PrimaryKMeansItem, "K-means cluster", hasKMeans, "requires k-means");
            SetLabelModeState(SecondaryKMeansItem, "K-means cluster", hasKMeans, "requires k-means");
            SetLabelModeState(PrimaryPlateItem, "Plate", hasPlates, "requires plate metadata");
            SetLabelModeState(SecondaryPlateItem, "Plate", hasPlates, "requires plate metadata");

            // If the current selection is one of the modes we just disabled, fall back to one that can
            // actually yield a label. Biological Condition is the last resort: it may still be empty, but
            // then the skip message names it, which is more than the disabled item could say.
            if (SecondaryLabelComboBox.SelectedItem is ComboBoxItem secondary && !secondary.IsEnabled)
                SecondaryLabelComboBox.SelectedItem = hasPlates ? SecondaryPlateItem : SecondaryBioConditionItem;

            if (PrimaryLabelComboBox.SelectedItem is ComboBoxItem primary && !primary.IsEnabled)
                PrimaryLabelComboBox.SelectedItem = PrimaryBioConditionItem;
        }

        private static void SetLabelModeState(ComboBoxItem item, string modeName, bool available, string requirement)
        {
            if (item == null)
                return;

            item.IsEnabled = available;
            // Tag drives ParseLabelMode, so annotating Content is safe.
            item.Content = available ? modeName : $"{modeName} — {requirement}";
        }

        /// <summary>
        /// Shows the control (called from MainWindow menu).
        /// </summary>
        public void Show()
        {
            this.Visibility = Visibility.Visible;
            if (_isInitialized)
                UpdateSummary();
        }

        private PLPExportOptions BuildOptions()
        {
            var options = new PLPExportOptions
            {
                Description = DescriptionTextBox.Text ?? "SCPBrowser Export"
            };

            if (PrimaryLabelComboBox.SelectedItem is ComboBoxItem primaryItem)
                options.PrimaryLabelMode = ParseLabelMode(primaryItem.Tag?.ToString());

            if (SecondaryLabelComboBox.SelectedItem is ComboBoxItem secondaryItem)
                options.SecondaryLabelMode = ParseLabelMode(secondaryItem.Tag?.ToString());

            // Only include actual merges (where source != target)
            var activeMerges = _cellTypeMergeMap
                .Where(kvp => kvp.Key != kvp.Value)
                .ToDictionary(kvp => kvp.Key, kvp => kvp.Value);

            if (activeMerges.Count > 0)
                options.CellTypeMergeMap = activeMerges;

            return options;
        }

        private static PLPLabelMode ParseLabelMode(string tag)
        {
            return tag switch
            {
                "CellType" => PLPLabelMode.CellType,
                "Plate" => PLPLabelMode.Plate,
                "KMeans" => PLPLabelMode.KMeans,
                _ => PLPLabelMode.BioCondition,
            };
        }

        private List<string> GetSelectedRuns()
        {
            if (_data == null || _data.RawFileNames == null)
                return new List<string>();

            // Prefer the Explorer's actual selection as the single source of truth — it already bakes in the
            // checkboxes, the contaminant-ratio cutoff, manual exclusions AND the lasso. Intersect with the runs that
            // actually carry quant data so a stale selection can't introduce phantom runs.
            if (_selectedRunNames != null)
            {
                var inData = new HashSet<string>(_data.RawFileNames);
                return _selectedRunNames.Where(r => inData.Contains(r)).ToList();
            }

            // Fallback (Explorer hasn't rendered a selection yet): re-derive from checkboxes + manual exclusions.
            return _data.RawFileNames
                .Where(r => !_excludedRunNames.Contains(r))
                .Where(r => IsRunChecked(r))
                .ToList();
        }

        private bool IsRunChecked(string runName)
        {
            // Filter by bio condition checkboxes
            if (_checkedBioConditions != null && _checkedBioConditions.Count > 0 &&
                _data.BiologicalConditionPerFile != null)
            {
                if (_data.BiologicalConditionPerFile.TryGetValue(runName, out var condition) &&
                    !string.IsNullOrEmpty(condition))
                {
                    if (!_checkedBioConditions.Contains(condition))
                        return false;
                }
            }

            // Filter by cell type checkboxes
            if (_checkedCellTypes != null && _checkedCellTypes.Count > 0 &&
                _cellTypePredictions != null)
            {
                if (_cellTypePredictions.TryGetValue(runName, out var prediction) &&
                    !string.IsNullOrEmpty(prediction.TopCellType))
                {
                    if (!_checkedCellTypes.Contains(prediction.TopCellType))
                        return false;
                }
            }

            // Filter by plate checkboxes
            if (_checkedPlates != null && _checkedPlates.Count > 0 &&
                _rawFileToPlateId != null && _plateIdToName != null)
            {
                if (_rawFileToPlateId.TryGetValue(runName, out var plateId) &&
                    _plateIdToName.TryGetValue(plateId, out var plateName) &&
                    !string.IsNullOrEmpty(plateName))
                {
                    if (!_checkedPlates.Contains(plateName))
                        return false;
                }
            }

            return true;
        }

        private void UpdateSummary()
        {
            if (!_isInitialized || _data == null)
                return;

            var options = BuildOptions();
            var selectedRuns = GetSelectedRuns();

            if (selectedRuns.Count == 0)
            {
                TotalRunsText.Text = "0";
                TotalProteinsText.Text = "0";
                SkippedRunsText.Text = "0";
                ClassBreakdownList.ItemsSource = null;
                NoClassesText.Visibility = Visibility.Visible;
                ExportButton.IsEnabled = false;
                StatusText.Text = "No runs selected. Check your filters in the Explorer.";
                return;
            }

            var summary = PLPExportService.GetExportSummary(
                selectedRuns, _data, options,
                _cellTypePredictions, _rawFileToPlateId, _plateIdToName, _kmeansLabels);

            TotalRunsText.Text = summary.TotalRuns.ToString();
            TotalProteinsText.Text = summary.TotalProteins.ToString();
            SkippedRunsText.Text = summary.SkippedRuns.ToString();

            if (summary.ClassBreakdown.Count > 0)
            {
                int maxCount = summary.ClassBreakdown.Values.Max();
                const double maxBarWidth = 200;

                var items = summary.ClassBreakdown
                    .OrderBy(kvp => kvp.Key, StringComparer.OrdinalIgnoreCase)
                    .Select(kvp => new ClassBreakdownItem
                    {
                        Label = $"[{kvp.Key}]",
                        Count = kvp.Value,
                        BarWidth = maxCount > 0 ? (kvp.Value / (double)maxCount) * maxBarWidth : 0
                    })
                    .ToList();

                ClassBreakdownList.ItemsSource = items;
                NoClassesText.Visibility = Visibility.Collapsed;
            }
            else
            {
                ClassBreakdownList.ItemsSource = null;
                NoClassesText.Visibility = Visibility.Visible;
            }

            ExportButton.IsEnabled = summary.TotalRuns > 0 && summary.ClassBreakdown.Count > 0;

            if (summary.SkippedRuns > 0)
                // SkipReason already carries its own run counts per label mode, and the total sits in the
                // Skipped Runs row above, so repeating the total here would just read as two numbers.
                StatusText.Text = $"⚠ {summary.SkipReason}";
            else
                StatusText.Text = $"Ready to export {summary.TotalRuns} runs across {summary.ClassBreakdown.Count} classes.";
        }

        private void LabelMode_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_isInitialized)
                return;

            UpdateMergePanelVisibility();
            UpdateSummary();
        }

        private bool IsCellTypeInUse()
        {
            var options = BuildOptions();
            return options.PrimaryLabelMode == PLPLabelMode.CellType || options.SecondaryLabelMode == PLPLabelMode.CellType;
        }

        private void UpdateMergePanelVisibility()
        {
            if (MergeCellTypesExpander == null)
                return;

            if (IsCellTypeInUse())
            {
                MergeCellTypesExpander.Visibility = Visibility.Visible;
                PopulateMergeCellTypes();
            }
            else
            {
                MergeCellTypesExpander.Visibility = Visibility.Collapsed;
            }
        }

        private void PopulateMergeCellTypes()
        {
            MergeCellTypesPanel.Children.Clear();

            if (_cellTypePredictions == null || _cellTypePredictions.Count == 0)
                return;

            // Get distinct cell types from predictions
            var cellTypes = _cellTypePredictions.Values
                .Where(p => !string.IsNullOrEmpty(p.TopCellType))
                .Select(p => p.TopCellType)
                .Distinct()
                .OrderBy(ct => ct, StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (cellTypes.Count == 0)
                return;

            // Initialize merge map entries for any new cell types
            foreach (var ct in cellTypes)
            {
                if (!_cellTypeMergeMap.ContainsKey(ct))
                    _cellTypeMergeMap[ct] = ct;
            }

            foreach (var ct in cellTypes)
            {
                var row = new Grid { Margin = new Thickness(0, 2, 0, 2) };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(20, GridUnitType.Pixel) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

                var sourceLabel = new TextBlock
                {
                    Text = ct,
                    FontSize = 12,
                    FontWeight = FontWeights.Normal,
                    VerticalAlignment = VerticalAlignment.Center
                };
                Grid.SetColumn(sourceLabel, 0);

                var arrow = new TextBlock
                {
                    Text = "→",
                    FontSize = 12,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    Foreground = System.Windows.Media.Brushes.Gray
                };
                Grid.SetColumn(arrow, 1);

                var combo = new ComboBox
                {
                    FontSize = 12,
                    Tag = ct,
                    VerticalAlignment = VerticalAlignment.Center
                };

                foreach (var target in cellTypes)
                    combo.Items.Add(target);

                // Set current selection from merge map
                combo.SelectedItem = _cellTypeMergeMap.ContainsKey(ct) ? _cellTypeMergeMap[ct] : ct;

                combo.SelectionChanged += MergeCombo_SelectionChanged;
                Grid.SetColumn(combo, 2);

                row.Children.Add(sourceLabel);
                row.Children.Add(arrow);
                row.Children.Add(combo);
                MergeCellTypesPanel.Children.Add(row);
            }
        }

        private void MergeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (sender is ComboBox combo && combo.Tag is string sourceCellType && combo.SelectedItem is string targetCellType)
            {
                _cellTypeMergeMap[sourceCellType] = targetCellType;
                UpdateSummary();
            }
        }

        private async void ExportButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var options = BuildOptions();
                var selectedRuns = GetSelectedRuns();

                if (selectedRuns.Count == 0)
                {
                    MessageBox.Show("No runs selected for export.", "Export", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                var saveDialog = new SaveFileDialog
                {
                    Filter = "PatternLab Project (*.plp)|*.plp",
                    DefaultExt = ".plp",
                    FileName = "SCPBrowser_Export.plp",
                    Title = "Save PLP File"
                };

                if (saveDialog.ShowDialog() != true)
                    return;

                ExportButton.IsEnabled = false;
                StatusText.Text = "Exporting...";

                PLPExportService.Export(
                    selectedRuns, _data, options,
                    _cellTypePredictions, _rawFileToPlateId, _plateIdToName,
                    _fastaAnnotations, saveDialog.FileName, _kmeansLabels);

                var summary = PLPExportService.GetExportSummary(
                    selectedRuns, _data, options,
                    _cellTypePredictions, _rawFileToPlateId, _plateIdToName, _kmeansLabels);

                // Provenance sidecar. The .plp itself records no filter, seed, classifier or selection, so
                // write the settings block beside it. A failed sidecar must never read as a failed export.
                string sidecarLine;
                string sidecarPath = System.IO.Path.ChangeExtension(saveDialog.FileName, ".settings.txt");
                try
                {
                    System.IO.File.WriteAllText(sidecarPath, await BuildAnalysisReportAsync());
                    sidecarLine = $"Settings: {System.IO.Path.GetFileName(sidecarPath)}";
                }
                catch (Exception sidecarEx)
                {
                    sidecarLine = $"Settings file could not be written: {sidecarEx.Message}";
                }

                StatusText.Text = $"✅ Exported {summary.TotalRuns} runs, {summary.TotalProteins} proteins to {System.IO.Path.GetFileName(saveDialog.FileName)}";
                ExportButton.IsEnabled = true;

                MessageBox.Show(
                    $"PLP file saved successfully!\n\n" +
                    $"Runs: {summary.TotalRuns}\n" +
                    $"Proteins: {summary.TotalProteins}\n" +
                    $"Classes: {summary.ClassBreakdown.Count}\n\n" +
                    $"File: {saveDialog.FileName}\n" +
                    sidecarLine,
                    "Export Complete",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                StatusText.Text = $"❌ Export failed: {ex.Message}";
                ExportButton.IsEnabled = true;

                MessageBox.Show(
                    $"Error exporting PLP file:\n\n{ex.Message}",
                    "Export Error",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }

        private void PatternLabLink_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "http://patternlabforproteomics.org",
                    UseShellExecute = true
                });
            }
            catch { }
        }

        // --- Analysis report ---------------------------------------------------------------------------
        // Parameters live in three disconnected stores (per-project DB keys, global user settings, and
        // session-only UI state) and no screen shows them together, so "what settings produced this figure?"
        // had no answer. This block gathers everything that can be stated truthfully and names the rest as
        // not captured rather than printing a default that was never actually used.

        /// <summary>
        /// Writes the analysis report (the settings behind the current figures). Public so the same feature is
        /// reachable from the Utils menu, not only from this panel - reproducibility documentation that a reviewer
        /// may ask for should not be hidden inside the PLP export tab.
        /// </summary>
        public void ExportAnalysisReport() => ExportReportButton_Click(this, new RoutedEventArgs());

        private async void ExportReportButton_Click(object sender, RoutedEventArgs e)
        {
            if (!_isInitialized || _data == null)
            {
                MessageBox.Show("Load a project with omic data before writing an analysis report.",
                    "Analysis Report", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var saveDialog = new SaveFileDialog
            {
                Filter = "HTML report (*.html)|*.html|Text report (*.txt)|*.txt",
                DefaultExt = ".html",
                FileName = "SCPBrowser_AnalysisReport.html",
                Title = "Save Analysis Report"
            };

            if (saveDialog.ShowDialog() != true)
                return;

            try
            {
                ExportReportButton.IsEnabled = false;

                string report = await BuildAnalysisReportAsync();
                bool asHtml = saveDialog.FileName.EndsWith(".html", StringComparison.OrdinalIgnoreCase) ||
                              saveDialog.FileName.EndsWith(".htm", StringComparison.OrdinalIgnoreCase);

                System.IO.File.WriteAllText(saveDialog.FileName, asHtml ? WrapReportInHtml(report) : report);

                StatusText.Text = $"Analysis report written to {System.IO.Path.GetFileName(saveDialog.FileName)}";
            }
            catch (Exception ex)
            {
                StatusText.Text = $"Analysis report failed: {ex.Message}";

                MessageBox.Show(
                    $"Error writing the analysis report:\n\n{ex.Message}",
                    "Analysis Report",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
            finally
            {
                ExportReportButton.IsEnabled = true;
            }
        }

        /// <summary>
        /// Builds the plain-text provenance block shared by the report file and the PLP sidecar.
        /// </summary>
        private async Task<string> BuildAnalysisReportAsync()
        {
            var options = BuildOptions();
            var selectedRuns = GetSelectedRuns();
            var summary = PLPExportService.GetExportSummary(
                selectedRuns, _data, options,
                _cellTypePredictions, _rawFileToPlateId, _plateIdToName, _kmeansLabels);

            var inv = CultureInfo.InvariantCulture;
            var sb = new StringBuilder();

            sb.AppendLine("SCPBrowser analysis report");
            sb.AppendLine("==========================");
            sb.AppendLine();
            sb.AppendLine(ReportLine("Generated", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", inv)));
            sb.AppendLine(ReportLine("SCPBrowser version", GetApplicationVersion()));

            // The project database is the only per-project store; without it the DB-backed sections below
            // are unavailable and are reported as such instead of falling back to factory defaults.
            string databasePath = (Window.GetWindow(this) as MainWindow)?.ProjectReferenceDatabasePath;
            ProjectDatabaseService db = null;
            if (!string.IsNullOrEmpty(databasePath))
            {
                try { db = new ProjectDatabaseService(databasePath); }
                catch (Exception ex) { Debug.WriteLine($"[BuildAnalysisReportAsync] project DB unavailable: {ex}"); }
            }

            sb.AppendLine(ReportLine("Project file", databasePath));

            if (db != null)
            {
                try
                {
                    var projectInfo = await db.GetProjectInfoAsync();
                    if (projectInfo != null)
                    {
                        sb.AppendLine(ReportLine("Project name", projectInfo.ProjectName));
                        sb.AppendLine(ReportLine("Project created", projectInfo.CreatedDate.ToString("yyyy-MM-dd HH:mm:ss", inv)));
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[BuildAnalysisReportAsync] project info: {ex}");
                }
            }

            // --- Data ---
            sb.AppendLine();
            sb.AppendLine("Data");
            sb.AppendLine("----");

            if (db != null && !string.IsNullOrEmpty(databasePath))
            {
                try
                {
                    var importedFiles = await new ParquetDataService(databasePath).GetAllImportedParquetFilesAsync();
                    sb.AppendLine(ReportLine("Imported files", (importedFiles?.Count ?? 0).ToString(inv)));
                    if (importedFiles != null)
                    {
                        foreach (var file in importedFiles)
                            sb.AppendLine("    " + file);
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[BuildAnalysisReportAsync] imported files: {ex}");
                    sb.AppendLine(ReportLine("Imported files", "(could not be read from the project)"));
                }
            }

            sb.AppendLine(ReportLine("Runs available to export", (_data.RawFileNames?.Count ?? 0).ToString(inv)));
            sb.AppendLine(ReportLine("Runs in this selection", selectedRuns.Count.ToString(inv)));
            sb.AppendLine(ReportLine("Proteins in this selection", summary.TotalProteins.ToString(inv)));
            sb.AppendLine();
            sb.AppendLine("    Runs reaching this panel have already passed the plate filter, the protein-count");
            sb.AppendLine("    cutoff and the contaminant-ratio cutoff, so \"runs available to export\" is a filtered");
            sb.AppendLine("    count, not the number of runs imported. Per-import file hash and quantity-column");
            sb.AppendLine("    mapping are stored in the project database but are not surfaced here.");

            // --- Filters ---
            sb.AppendLine();
            sb.AppendLine("Filters");
            sb.AppendLine("-------");

            if (db != null)
            {
                sb.AppendLine(ReportLine("Min. proteins per cell", await ReadSettingAsync(db, "ProteinCutoff")));
                sb.AppendLine(ReportLine("Max. proteins per cell", await ReadSettingAsync(db, "UpperProteinCutoff")));
            }

            sb.AppendLine(ReportLine("Biological conditions kept", DescribeSet(_checkedBioConditions)));
            sb.AppendLine(ReportLine("Cell types kept", DescribeSet(_checkedCellTypes)));
            sb.AppendLine(ReportLine("Plates kept", DescribeSet(_checkedPlates)));
            sb.AppendLine(ReportLine("Manually excluded runs", (_excludedRunNames?.Count ?? 0).ToString(inv)));

            if (_excludedRunNames != null && _excludedRunNames.Count > 0)
            {
                foreach (var run in _excludedRunNames.OrderBy(r => r, StringComparer.OrdinalIgnoreCase))
                    sb.AppendLine("    " + run);
            }

            sb.AppendLine(ReportLine("Selection source", _selectedRunNames != null
                ? "Explorer selection (filters + lasso, minus exclusions)"
                : "re-derived from the filter checkboxes (Explorer had not rendered a selection)"));
            sb.AppendLine();
            sb.AppendLine("    The contaminant-ratio cutoff and the lasso outline are session-only state and are");
            sb.AppendLine("    not persisted with the project; the run list at the end of this report is the only");
            sb.AppendLine("    durable record of which cells this selection contained.");

            // --- Dimensionality reduction ---
            sb.AppendLine();
            sb.AppendLine("Dimensionality reduction (as stored in the project)");
            sb.AppendLine("---------------------------------------------------");

            if (db != null)
            {
                var dimRed = await Models.DimensionReductionSettings.LoadAsync(db);
                sb.AppendLine(ReportLine("Z-score scaling", dimRed.ZScoreScale.ToString()));
                sb.AppendLine(ReportLine("Clip max value", dimRed.ClipMaxValue.ToString("R", inv)));
                sb.AppendLine(ReportLine("PCA components", dimRed.NumPcaComponents.ToString(inv)));
                sb.AppendLine(ReportLine("PCs used for UMAP", dimRed.NumPcsForUmap.ToString(inv)));
                sb.AppendLine(ReportLine("UMAP neighbours", dimRed.UmapNeighbors.ToString(inv)));
                sb.AppendLine(ReportLine("UMAP seed", dimRed.UmapSeed.ToString(inv)));
                sb.AppendLine(ReportLine("Guided embedding", $"{dimRed.UseGuidedEmbedding} (weight {dimRed.GuidedWeight.ToString("R", inv)})"));
                sb.AppendLine(ReportLine("HVP filter", $"{dimRed.UseHvpFilter} (top {dimRed.HvpCount.ToString(inv)})"));
                sb.AppendLine(ReportLine("Batch correction", dimRed.ApplyBatchCorrection.ToString()));
                sb.AppendLine();
                sb.AppendLine("    These are the values currently stored in the project. If they were changed after");
                sb.AppendLine("    the embedding on screen was computed, the plot still reflects the earlier values —");
                sb.AppendLine("    re-run the embedding to make the two agree.");
            }
            else
            {
                sb.AppendLine("    Not available: the project database could not be read.");
            }

            // --- Classification ---
            sb.AppendLine();
            sb.AppendLine("Classification");
            sb.AppendLine("--------------");

            string classificationMethod = db != null ? await ReadSettingAsync(db, "classification_method") : null;
            if (string.IsNullOrEmpty(classificationMethod) || classificationMethod == NotRecorded)
            {
                // No explicit setting: state what actually produced the predictions in memory instead of
                // printing the resolution default, which may not be the method that ran.
                var scorers = _cellTypePredictions?.Values
                    .Where(p => p != null && !string.IsNullOrEmpty(p.ScorerMethod))
                    .Select(p => p.ScorerMethod)
                    .Distinct()
                    .OrderBy(m => m, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                classificationMethod = scorers != null && scorers.Count > 0
                    ? string.Join(", ", scorers) + " (from the loaded predictions; not stored as a project setting)"
                    : NotRecorded;
            }

            sb.AppendLine(ReportLine("Classifier method", classificationMethod));

            if (db != null)
                sb.AppendLine(ReportLine("Confidence threshold", await ReadSettingAsync(db, "ClassificationConfidenceThreshold")));

            int classifiedInSelection = selectedRuns.Count(r =>
                _cellTypePredictions != null &&
                _cellTypePredictions.TryGetValue(r, out var p) &&
                p != null && !string.IsNullOrEmpty(p.TopCellType));

            sb.AppendLine(ReportLine("Classified in this selection", $"{classifiedInSelection.ToString(inv)} of {selectedRuns.Count.ToString(inv)}"));
            sb.AppendLine(ReportLine("K-means labels available", (_kmeansLabels?.Count ?? 0).ToString(inv)));

            if (db != null)
            {
                sb.AppendLine(ReportLine("GO p-value cutoff", await ReadSettingAsync(db, "GOPValueCutoff")));
                sb.AppendLine(ReportLine("GO minimum overlap", await ReadSettingAsync(db, "GOMinimumOverlap")));
            }

            // --- Export ---
            sb.AppendLine();
            sb.AppendLine("PLP export");
            sb.AppendLine("----------");
            sb.AppendLine(ReportLine("Description", options.Description));
            sb.AppendLine(ReportLine("Primary label", PLPExportService.DescribeLabelMode(options.PrimaryLabelMode)));
            sb.AppendLine(ReportLine("Secondary label", PLPExportService.DescribeLabelMode(options.SecondaryLabelMode)));
            sb.AppendLine(ReportLine("Runs exported", summary.TotalRuns.ToString(inv)));
            sb.AppendLine(ReportLine("Runs skipped", summary.SkippedRuns.ToString(inv)));

            if (summary.SkippedRuns > 0)
                sb.AppendLine(ReportLine("Skip reason", summary.SkipReason));

            if (options.CellTypeMergeMap != null && options.CellTypeMergeMap.Count > 0)
            {
                sb.AppendLine(ReportLine("Cell type merges", options.CellTypeMergeMap.Count.ToString(inv)));
                foreach (var merge in options.CellTypeMergeMap.OrderBy(m => m.Key, StringComparer.OrdinalIgnoreCase))
                    sb.AppendLine($"    {merge.Key} -> {merge.Value}");
            }

            sb.AppendLine(ReportLine("Classes", summary.ClassBreakdown.Count.ToString(inv)));
            foreach (var cls in summary.ClassBreakdown.OrderBy(c => c.Key, StringComparer.OrdinalIgnoreCase))
                sb.AppendLine($"    [{cls.Key}]    {cls.Value.ToString(inv)} run(s)");

            // --- Run list ---
            sb.AppendLine();
            sb.AppendLine("Runs in this selection");
            sb.AppendLine("----------------------");
            sb.AppendLine("Run\tPrimary\tSecondary\tPlate\tBiological condition\tCell type");

            var labelPairs = PLPExportService.GetRunLabelPairs(
                selectedRuns, _data, options,
                _cellTypePredictions, _rawFileToPlateId, _plateIdToName, _kmeansLabels);

            foreach (var pair in labelPairs.OrderBy(p => p.Run, StringComparer.OrdinalIgnoreCase))
            {
                string plate = null;
                if (_rawFileToPlateId != null && _plateIdToName != null &&
                    _rawFileToPlateId.TryGetValue(pair.Run, out int plateId))
                    _plateIdToName.TryGetValue(plateId, out plate);

                string condition = null;
                if (_data.BiologicalConditionPerFile != null)
                    _data.BiologicalConditionPerFile.TryGetValue(pair.Run, out condition);

                string cellType = null;
                if (_cellTypePredictions != null && _cellTypePredictions.TryGetValue(pair.Run, out var prediction))
                    cellType = prediction?.TopCellType;

                sb.AppendLine(string.Join("\t", new[]
                {
                    pair.Run,
                    pair.Primary ?? "",
                    pair.Secondary ?? "",
                    plate ?? "",
                    condition ?? "",
                    cellType ?? ""
                }));
            }

            return sb.ToString();
        }

        /// <summary>Marker for values the app genuinely does not know, so no default is mistaken for a used value.</summary>
        private const string NotRecorded = "(not recorded)";

        private static string ReportLine(string name, string value)
        {
            return name.PadRight(30) + (string.IsNullOrWhiteSpace(value) ? NotRecorded : value);
        }

        private static async Task<string> ReadSettingAsync(ProjectDatabaseService db, string key)
        {
            try
            {
                return await db.GetSettingAsync(key) ?? NotRecorded;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ReadSettingAsync] {key}: {ex}");
                return NotRecorded;
            }
        }

        /// <summary>
        /// An empty or absent checkbox set means "no restriction" everywhere in IsRunChecked, so it must not
        /// be reported as "nothing kept".
        /// </summary>
        private static string DescribeSet(HashSet<string> values)
        {
            if (values == null || values.Count == 0)
                return "all (no restriction)";

            return string.Join(", ", values.OrderBy(v => v, StringComparer.OrdinalIgnoreCase));
        }

        private static string GetApplicationVersion()
        {
            return Environment.GetEnvironmentVariable("ClickOnce_CurrentVersion")
                ?? System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString()
                ?? "?";
        }

        private static string WrapReportInHtml(string reportText)
        {
            string escaped = (reportText ?? "")
                .Replace("&", "&amp;")
                .Replace("<", "&lt;")
                .Replace(">", "&gt;");

            var sb = new StringBuilder();
            sb.AppendLine("<!DOCTYPE html>");
            sb.AppendLine("<html><head><meta charset=\"utf-8\" />");
            sb.AppendLine("<title>SCPBrowser analysis report</title>");
            sb.AppendLine("<style>body{background:#f8fafc;color:#1e293b;font-family:'Segoe UI',Arial,sans-serif;margin:24px;}"
                + "pre{background:#ffffff;border:1px solid #e2e8f0;border-radius:6px;padding:20px;"
                + "font-family:Consolas,'Courier New',monospace;font-size:13px;line-height:1.45;white-space:pre-wrap;}</style>");
            sb.AppendLine("</head><body>");
            sb.AppendLine("<pre>" + escaped + "</pre>");
            sb.AppendLine("</body></html>");

            return sb.ToString();
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            this.Visibility = Visibility.Collapsed;
        }
    }
}
