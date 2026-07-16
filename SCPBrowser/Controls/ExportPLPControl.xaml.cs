// ExportPLPControl.xaml.cs
// Code-behind for the PLP export control.
// Location: SCPBrowser/Controls/ExportPLPControl.xaml.cs

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
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

            UpdateMergePanelVisibility();
            UpdateSummary();
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
                StatusText.Text = $"⚠ {summary.SkippedRuns} run(s) skipped: {summary.SkipReason}";
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

        private void ExportButton_Click(object sender, RoutedEventArgs e)
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

                StatusText.Text = $"✅ Exported {summary.TotalRuns} runs, {summary.TotalProteins} proteins to {System.IO.Path.GetFileName(saveDialog.FileName)}";
                ExportButton.IsEnabled = true;

                MessageBox.Show(
                    $"PLP file saved successfully!\n\n" +
                    $"Runs: {summary.TotalRuns}\n" +
                    $"Proteins: {summary.TotalProteins}\n" +
                    $"Classes: {summary.ClassBreakdown.Count}\n\n" +
                    $"File: {saveDialog.FileName}",
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

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            this.Visibility = Visibility.Collapsed;
        }
    }
}
