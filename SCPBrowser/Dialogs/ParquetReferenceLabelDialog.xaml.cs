using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using SCPBrowser.Services;

namespace SCPBrowser
{
    /// <summary>
    /// Row model for the runs DataGrid in ParquetReferenceLabelDialog.
    /// </summary>
    public class RunLabelInfo : INotifyPropertyChanged
    {
        private string _cellTypeLabel = "";

        public string RunName { get; set; }
        public int ProteinCount { get; set; }

        public string CellTypeLabel
        {
            get => _cellTypeLabel;
            set
            {
                if (_cellTypeLabel != value)
                {
                    _cellTypeLabel = value;
                    OnPropertyChanged();
                }
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    /// <summary>
    /// Dialog for loading a parquet file and assigning cell type labels to runs
    /// to build a proteomic reference for cell type classification.
    /// </summary>
    public partial class ParquetReferenceLabelDialog : Window, INotifyPropertyChanged
    {
        private const int MinRunsPerCellType = 5;

        private string _currentFilter = string.Empty;
        private ObservableCollection<RunLabelInfo> _allRuns;
        private ObservableCollection<RunLabelInfo> _filteredRuns;
        private ObservableCollection<string> _cellTypeLabels;

        public ObservableCollection<RunLabelInfo> AllRuns
        {
            get => _allRuns;
            set { _allRuns = value; OnPropertyChanged(nameof(AllRuns)); }
        }

        public ObservableCollection<RunLabelInfo> FilteredRuns
        {
            get => _filteredRuns;
            set { _filteredRuns = value; OnPropertyChanged(nameof(FilteredRuns)); }
        }

        public ObservableCollection<string> CellTypeLabels
        {
            get => _cellTypeLabels;
            set { _cellTypeLabels = value; OnPropertyChanged(nameof(CellTypeLabels)); }
        }

        /// <summary>
        /// The loaded proteomics data (protein quant matrix, etc.) — available after successful build.
        /// </summary>
        public ProteomicsData LoadedData { get; private set; }

        /// <summary>
        /// Run name → cell type label mapping — available after successful build.
        /// </summary>
        public Dictionary<string, string> RunCellTypeMap { get; private set; }

        public bool BuildSuccessful { get; private set; }

        public event PropertyChangedEventHandler PropertyChanged;

        public ParquetReferenceLabelDialog(string parquetFilePath)
        {
            InitializeComponent();
            DataContext = this;

            AllRuns = new ObservableCollection<RunLabelInfo>();
            FilteredRuns = new ObservableCollection<RunLabelInfo>();
            CellTypeLabels = new ObservableCollection<string>();

            LoadParquetAsync(parquetFilePath);
        }

        private async void LoadParquetAsync(string filePath)
        {
            try
            {
                var parquetService = new ParquetDataService();
                var mapping = new ColumnMapping
                {
                    RawFileColumn = "Run",
                    ProteinGroupColumn = "Protein.Group",
                    PeptideColumn = "Stripped.Sequence",
                    TotalIonCurrentColumn = "Precursor.Quantity"
                };

                LoadedData = await parquetService.LoadParquetFileAsync(filePath, mapping);

                FileInfoText.Text = $"{System.IO.Path.GetFileName(filePath)}  —  " +
                                    $"{LoadedData.RawFileNames.Count} runs, " +
                                    $"{LoadedData.TotalProteinGroups:N0} proteins";

                AllRuns.Clear();
                foreach (var runName in LoadedData.RawFileNames.OrderBy(x => x))
                {
                    AllRuns.Add(new RunLabelInfo
                    {
                        RunName = runName,
                        ProteinCount = LoadedData.ProteinCountPerFile.ContainsKey(runName)
                            ? LoadedData.ProteinCountPerFile[runName] : 0
                    });
                }

                ApplyFilter();
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    $"Error loading parquet file:\n\n{ex.Message}",
                    "Load Error",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
                Close();
            }
        }

        #region Cell Type Label Management

        private void NewCellType_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new NewConditionDialog { Owner = this };
            dialog.Title = "New Cell Type Label";

            if (dialog.ShowDialog() == true)
            {
                string newLabel = dialog.ConditionName;

                if (CellTypeLabels.Any(c => c.Equals(newLabel, StringComparison.OrdinalIgnoreCase)))
                {
                    MessageBox.Show($"'{newLabel}' already exists.", "Duplicate",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                CellTypeLabels.Add(newLabel);
                CellTypeLabelComboBox.SelectedItem = newLabel;
            }
        }

        private void ApplyBatchAssignment_Click(object sender, RoutedEventArgs e)
        {
            if (CellTypeLabelComboBox.SelectedItem == null)
            {
                MessageBox.Show("Please select a cell type to assign.",
                    "No Cell Type Selected", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            string selectedLabel = CellTypeLabelComboBox.SelectedItem.ToString();
            var targets = FilteredRuns.ToList();

            if (targets.Count == 0)
            {
                MessageBox.Show("No runs match the current filter.",
                    "No Runs", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            // Preview
            var preview = $"Assign '{selectedLabel}' to {targets.Count} run(s)?";
            if (targets.Count <= 10)
            {
                preview += "\n\n" + string.Join("\n", targets.Select(r => $"• {r.RunName}"));
            }
            else
            {
                preview += "\n\n" + string.Join("\n", targets.Take(10).Select(r => $"• {r.RunName}"));
                preview += $"\n... and {targets.Count - 10} more";
            }

            if (MessageBox.Show(preview, "Confirm Batch Assignment",
                    MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                return;

            foreach (var run in targets)
            {
                // Update the original run in AllRuns
                var original = AllRuns.FirstOrDefault(r => r.RunName == run.RunName);
                if (original != null)
                    original.CellTypeLabel = selectedLabel;
            }

            RunsDataGrid.Items.Refresh();
            ValidateBuildButton();

            if (!string.IsNullOrEmpty(_currentFilter))
            {
                if (MessageBox.Show("Clear filter to see all runs?", "Clear Filter",
                        MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
                    FilterTextBox.Clear();
            }
        }

        #endregion

        #region Filter

        private void FilterTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            _currentFilter = FilterTextBox.Text?.Trim() ?? string.Empty;
            FilterClearButton.Visibility = string.IsNullOrEmpty(_currentFilter)
                ? Visibility.Collapsed : Visibility.Visible;
            ApplyFilter();
        }

        private void FilterClearButton_Click(object sender, RoutedEventArgs e)
        {
            FilterTextBox.Clear();
        }

        private void ApplyFilter()
        {
            if (AllRuns == null)
            {
                FilteredRuns = new ObservableCollection<RunLabelInfo>();
                return;
            }

            var filtered = string.IsNullOrEmpty(_currentFilter)
                ? AllRuns.ToList()
                : AllRuns.Where(r => MatchesFilter(r.RunName, _currentFilter) ||
                                     MatchesFilter(r.CellTypeLabel, _currentFilter)).ToList();

            FilteredRuns = new ObservableCollection<RunLabelInfo>(filtered);
            RunsDataGrid.ItemsSource = FilteredRuns;

            // Update status
            if (filtered.Count == AllRuns.Count)
            {
                FilterStatusText.Text = $"All {AllRuns.Count} runs shown";
                FilterStatusText.Foreground = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(100, 116, 139));
                RunsDataGrid.Tag = null;
            }
            else
            {
                FilterStatusText.Text = $"{filtered.Count} of {AllRuns.Count} runs shown";
                FilterStatusText.Foreground = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(37, 99, 235));
                RunsDataGrid.Tag = "Filtered";
            }

            bool hasFilter = !string.IsNullOrEmpty(_currentFilter);
            bool noResults = filtered.Count == 0;
            NoResultsBorder.Visibility = hasFilter && noResults ? Visibility.Visible : Visibility.Collapsed;
            RunsDataGrid.Visibility = hasFilter && noResults ? Visibility.Collapsed : Visibility.Visible;
            if (hasFilter && noResults)
                NoResultsHelpText.Text = $"No runs contain '{_currentFilter}'. Try a different search term.";
        }

        private bool MatchesFilter(string text, string filter)
        {
            if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(filter))
                return false;

            if (filter.Contains('*'))
            {
                string regexPattern = "^" + Regex.Escape(filter).Replace("\\*", ".*") + "$";
                return Regex.IsMatch(text, regexPattern, RegexOptions.IgnoreCase);
            }

            return text.Contains(filter, StringComparison.OrdinalIgnoreCase);
        }

        #endregion

        #region Validation

        private void RunsDataGrid_CellEditEnding(object sender, DataGridCellEditEndingEventArgs e)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                // If user typed a new cell type label inline, add it to the ComboBox list
                if (e.Column.Header.ToString() == "Cell Type" && e.EditingElement is TextBox tb)
                {
                    string typed = tb.Text?.Trim();
                    if (!string.IsNullOrEmpty(typed) &&
                        !CellTypeLabels.Any(c => c.Equals(typed, StringComparison.OrdinalIgnoreCase)))
                    {
                        CellTypeLabels.Add(typed);
                    }
                }

                ValidateBuildButton();
            }), System.Windows.Threading.DispatcherPriority.Background);
        }

        private void ValidateBuildButton()
        {
            if (AllRuns == null || AllRuns.Count == 0)
            {
                BuildButton.IsEnabled = false;
                return;
            }

            var labeled = AllRuns.Where(r => !string.IsNullOrWhiteSpace(r.CellTypeLabel)).ToList();
            var unlabeled = AllRuns.Count - labeled.Count;

            // Group by cell type
            var groups = labeled
                .GroupBy(r => r.CellTypeLabel, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.Count());

            // Build summary
            if (groups.Count > 0)
            {
                var parts = groups.OrderBy(g => g.Key)
                    .Select(g => $"{g.Key}: {g.Value} runs");
                LabelSummaryText.Text = string.Join("  |  ", parts);
            }
            else
            {
                LabelSummaryText.Text = "";
            }

            // Validate
            var tooFew = groups.Where(g => g.Value < MinRunsPerCellType).ToList();

            if (unlabeled > 0)
            {
                ValidationText.Text = $"{unlabeled} run(s) still need a cell type label.";
                BuildButton.IsEnabled = false;
            }
            else if (tooFew.Count > 0)
            {
                var names = string.Join(", ", tooFew.Select(g => $"'{g.Key}' ({g.Value})"));
                ValidationText.Text = $"Minimum {MinRunsPerCellType} runs per cell type. Too few: {names}";
                BuildButton.IsEnabled = false;
            }
            else
            {
                ValidationText.Text = "";
                BuildButton.IsEnabled = true;
            }
        }

        #endregion

        #region Build / Cancel

        private void Build_Click(object sender, RoutedEventArgs e)
        {
            // Final validation
            RunCellTypeMap = AllRuns.ToDictionary(r => r.RunName, r => r.CellTypeLabel);
            BuildSuccessful = true;
            DialogResult = true;
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
        }

        #endregion

        protected void OnPropertyChanged(string propertyName)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
