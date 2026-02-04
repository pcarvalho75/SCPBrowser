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

        private bool _isInitialized = false;

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
            Dictionary<string, FastaParserService.ProteinAnnotation> fastaAnnotations)
        {
            _data = data;
            _excludedRunNames = excludedRunNames ?? new HashSet<string>();
            _cellTypePredictions = cellTypePredictions;
            _rawFileToPlateId = rawFileToPlateId;
            _plateIdToName = plateIdToName;
            _fastaAnnotations = fastaAnnotations;

            _isInitialized = true;

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

            return options;
        }

        private static PLPLabelMode ParseLabelMode(string tag)
        {
            return tag switch
            {
                "CellType" => PLPLabelMode.CellType,
                "Plate" => PLPLabelMode.Plate,
                _ => PLPLabelMode.BioCondition,
            };
        }

        private List<string> GetSelectedRuns()
        {
            if (_data == null || _data.RawFileNames == null)
                return new List<string>();

            return _data.RawFileNames
                .Where(r => !_excludedRunNames.Contains(r))
                .ToList();
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
                _cellTypePredictions, _rawFileToPlateId, _plateIdToName);

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

            UpdateSummary();
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
                    _fastaAnnotations, saveDialog.FileName);

                var summary = PLPExportService.GetExportSummary(
                    selectedRuns, _data, options,
                    _cellTypePredictions, _rawFileToPlateId, _plateIdToName);

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
