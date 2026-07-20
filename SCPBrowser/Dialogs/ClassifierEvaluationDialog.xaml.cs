using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Input;
using Microsoft.Win32;
using SCPBrowser.Services;

namespace SCPBrowser
{
    /// <summary>
    /// Cross-validated benchmark of the cell-type classifier against the ground-truth biological conditions.
    ///
    /// Two invariants this dialog must never break:
    ///  - It PERSISTS NOTHING. The normal reference-build path calls WriteTranscriptomicDataAsync(clearExistingData:true),
    ///    which wipes the project's reference, and stored classifications are global (not import-scoped). Every fold
    ///    here builds its reference purely in memory; results escape only through the CSV the user exports.
    ///  - It does not touch the scoring engine, so the number it reports is the method the app actually ships.
    /// </summary>
    public partial class ClassifierEvaluationDialog : Window
    {
        public sealed class ConditionItem : INotifyPropertyChanged
        {
            public string Name { get; set; }
            public int Count { get; set; }
            public string Display => $"{Name}  ({Count} cells)";

            private bool _isIncluded = true;
            public bool IsIncluded
            {
                get => _isIncluded;
                set { _isIncluded = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsIncluded))); }
            }
            public event PropertyChangedEventHandler PropertyChanged;
        }

        private readonly ProteomicsData _data;
        private readonly string _datasetName;
        private readonly ObservableCollection<ConditionItem> _conditions = new();
        private ClassifierEvaluationService.Report _report;

        public ClassifierEvaluationDialog(ProteomicsData data, string datasetName = null)
        {
            InitializeComponent();
            _data = data ?? throw new ArgumentNullException(nameof(data));
            _datasetName = datasetName;

            var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in _data.BiologicalConditionPerFile)
            {
                if (string.IsNullOrWhiteSpace(kv.Value)) continue;
                counts[kv.Value] = counts.GetValueOrDefault(kv.Value) + 1;
            }
            foreach (var kv in counts.OrderBy(k => k.Key, StringComparer.Ordinal))
                _conditions.Add(new ConditionItem { Name = kv.Key, Count = kv.Value });

            ConditionsList.ItemsSource = _conditions;

            if (_conditions.Count == 0)
                ResultsBox.Text = "No cells have a biological condition, so there is no ground truth to benchmark against.\n\n" +
                                  "Conditions are auto-derived from each run's folder on import.";
        }

        private async void Run_Click(object sender, RoutedEventArgs e)
        {
            var chosen = _conditions.Where(c => c.IsIncluded).Select(c => c.Name).ToList();
            if (chosen.Count < 2)
            {
                MessageBox.Show("Select at least two conditions — a classifier needs something to discriminate between.",
                    "Evaluate Classifier", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            int folds = int.TryParse(FoldsBox.Text, out var f) && f >= 2 ? f : 5;
            int repeats = int.TryParse(RepeatsBox.Text, out var r) && r >= 1 ? r : 10;

            int smallest = _conditions.Where(c => c.IsIncluded).Min(c => c.Count);
            if (smallest < folds)
            {
                var proceed = MessageBox.Show(
                    $"The smallest selected condition has only {smallest} cells but {folds} folds were requested, " +
                    $"so some folds will contain no cell of that class.\n\nContinue anyway?",
                    "Evaluate Classifier", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (proceed != MessageBoxResult.Yes) return;
            }

            var options = new ClassifierEvaluationService.Options
            {
                IncludeConditions = new HashSet<string>(chosen, StringComparer.OrdinalIgnoreCase),
                Folds = folds,
                Repeats = repeats,
                RunLoocv = LoocvCheck.IsChecked == true,
                RunSubsetStability = StabilityCheck.IsChecked == true,
                UseQuantitativeScorer = QuantScorerCheck.IsChecked == true,
                // Learning-curve sizes must stay below the smallest class or it has no test cells left.
                LearningCurveSizes = new List<int> { 2, 3, 5, 10 }.Where(k => k < smallest).ToList()
            };

            RunButton.IsEnabled = false;
            SaveCsvButton.IsEnabled = false;
            SaveReportButton.IsEnabled = false;
            Mouse.OverrideCursor = Cursors.Wait;
            ResultsBox.Text = "Running...";

            // Reuse SCPBrowser's shared wait screen (with rotating tips) over the dialog while the benchmark runs.
            Overlay.SetMessage("Evaluating classifier…");
            Overlay.SetProgress("Preparing cross-validation…");
            Overlay.Show();

            var progress = new Progress<string>(msg => { StatusText.Text = msg; Overlay.SetProgress(msg); });
            try
            {
                bool wantChannels = ChannelCheck.IsChecked == true;
                var (report, text) = await System.Threading.Tasks.Task.Run(() =>
                {
                    var rep = ClassifierEvaluationService.Run(_data, options, progress);
                    var sb = new StringBuilder();
                    sb.Append(ClassifierEvaluationService.FormatSummary(rep));
                    if (wantChannels)
                    {
                        sb.AppendLine();
                        sb.AppendLine("──────── PER-CHANNEL CONTRIBUTION ────────");
                        sb.AppendLine("How much does each scoring metric actually contribute? A channel whose softmax is");
                        sb.AppendLine("uniform adds a constant to every class: it cannot discriminate, yet still takes a");
                        sb.AppendLine("full share of the composite.");
                        sb.AppendLine();
                        sb.Append(ClassifierEvaluationService.AnalyzeChannels(rep));
                        sb.AppendLine();
                        sb.Append(ClassifierEvaluationService.AnalyzeChannelCombinations(rep));
                    }
                    return (rep, sb.ToString());
                });

                _report = report;
                ResultsBox.Text = text;
                SaveCsvButton.IsEnabled = _report?.PerCell?.Count > 0;
                SaveReportButton.IsEnabled = _report?.PerCell?.Count > 0;
                StatusText.Text = "Done.";
            }
            catch (Exception ex)
            {
                ResultsBox.Text = "Evaluation failed:\n\n" + ex;
                StatusText.Text = "Failed.";
            }
            finally
            {
                Overlay.Hide();
                RunButton.IsEnabled = true;
                Mouse.OverrideCursor = null;
            }
        }

        /// <summary>Writes a self-contained HTML report (inline SVG figures) and opens it in the default browser.</summary>
        private void SaveReport_Click(object sender, RoutedEventArgs e)
        {
            if (_report == null) return;
            var dlg = new SaveFileDialog
            {
                Filter = "HTML report (*.html)|*.html",
                FileName = "classifier_evaluation_report.html",
                Title = "Export evaluation report"
            };
            if (dlg.ShowDialog() != true) return;

            try
            {
                string html = EvaluationReportBuilder.Build(_report, _datasetName);
                System.IO.File.WriteAllText(dlg.FileName, html, System.Text.Encoding.UTF8);
                StatusText.Text = "Report exported.";
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(dlg.FileName) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                MessageBox.Show("Could not write the report:" + Environment.NewLine + Environment.NewLine + ex.Message,
                    "Export", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void SaveCsv_Click(object sender, RoutedEventArgs e)
        {
            if (_report == null) return;
            var dlg = new SaveFileDialog
            {
                Filter = "CSV (*.csv)|*.csv",
                FileName = "classifier_evaluation_per_cell.csv",
                Title = "Export per-cell evaluation"
            };
            if (dlg.ShowDialog() != true) return;

            try
            {
                ClassifierEvaluationService.WritePerCellCsv(_report, dlg.FileName);
                StatusText.Text = "Exported.";
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Could not write the CSV:\n\n{ex.Message}", "Export",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void Close_Click(object sender, RoutedEventArgs e) => Close();
    }
}
