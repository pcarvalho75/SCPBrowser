using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;

namespace SCPBrowser
{
    public partial class SelectedPointsGridControl : UserControl
    {
        private List<SelectedPointData> _currentGridData;
        private bool _isGeneMatrix;

        public event EventHandler<SelectedPointData> GridSelectionChanged;
        public event EventHandler<RunInclusionChangedEventArgs> RunInclusionChanged;
        public event EventHandler ClearAllExclusionsRequested;

        public SelectedPointsGridControl()
        {
            InitializeComponent();
        }

        /// <summary>
        /// A gene matrix has no peptide dimension: the "Peptides" column would just duplicate the detected-gene
        /// count already shown under "Proteins", so it is hidden (and omitted from the clipboard export) in that mode.
        /// </summary>
        public void SetGeneMatrixMode(bool isGeneMatrix)
        {
            _isGeneMatrix = isGeneMatrix;
            PeptidesColumn.Visibility = isGeneMatrix ? Visibility.Collapsed : Visibility.Visible;
        }

        public void UpdateGrid(List<SelectedPointData> gridData)
        {
            // Preserve the user's active column sort across selection refreshes — reassigning ItemsSource otherwise
            // clears the sort (and the header arrow) every time the scatter selection changes.
            var priorSorts = SelectedPointsGrid.Items.SortDescriptions.ToList();
            var priorDirs = SelectedPointsGrid.Columns.Select(c => c.SortDirection).ToList();

            _currentGridData = gridData;
            SelectedPointsGrid.ItemsSource = gridData;

            if (priorSorts.Count > 0)
            {
                foreach (var sd in priorSorts)
                    SelectedPointsGrid.Items.SortDescriptions.Add(sd);
                for (int i = 0; i < priorDirs.Count && i < SelectedPointsGrid.Columns.Count; i++)
                    SelectedPointsGrid.Columns[i].SortDirection = priorDirs[i];
                SelectedPointsGrid.Items.Refresh();
            }

            if (gridData.Count > 0)
            {
                SelectionStatusText.Text = $"{gridData.Count} point(s) selected. Click a row to view details. Uncheck to exclude from BioTessera.";
            }
            else
            {
                SelectionStatusText.Text = "Click a point or drag to select multiple points. Right-click a point to view details.";
            }

            UpdateClearExclusionsVisibility();
        }

        public void UpdateSelectionRuleText(string ruleText)
        {
            SelectionRuleText.Text = ruleText;
        }

        public void SetHasExclusions(bool hasExclusions)
        {
            ClearExclusionsButton.Visibility = hasExclusions ? Visibility.Visible : Visibility.Collapsed;
        }

        public void ClearGrid()
        {

            _currentGridData = null;
            SelectedPointsGrid.ItemsSource = null;
            SelectionStatusText.Text = "Click a point or drag to select multiple points. Right-click a point to view details.";
        }

        private void UpdateClearExclusionsVisibility()
        {
            if (_currentGridData == null || _currentGridData.Count == 0)
            {
                ClearExclusionsButton.Visibility = Visibility.Collapsed;
                return;
            }

            bool hasExclusions = _currentGridData.Any(d => !d.IsIncluded);
            ClearExclusionsButton.Visibility = hasExclusions ? Visibility.Visible : Visibility.Collapsed;
        }

        private void SelectedPointsGrid_LoadingRow(object sender, DataGridRowEventArgs e)
        {
            e.Row.Header = (e.Row.GetIndex() + 1).ToString();
        }

        private void SelectedPointsGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (SelectedPointsGrid.SelectedItem is SelectedPointData selectedData)
            {
                GridSelectionChanged?.Invoke(this, selectedData);
            }

            if (e.AddedItems.Count > 0 || e.RemovedItems.Count > 0)
            {
                CheckForInclusionChanges();
            }
        }

        private void CheckForInclusionChanges()
        {
        }

        protected override void OnInitialized(EventArgs e)
        {
            base.OnInitialized(e);
            SelectedPointsGrid.CellEditEnding += SelectedPointsGrid_CellEditEnding;
        }

        private void SelectedPointsGrid_CellEditEnding(object? sender, DataGridCellEditEndingEventArgs e)
        {
            if (e.Column.Header?.ToString() == "✓" && e.Row.Item is SelectedPointData data)
            {
                if (e.EditingElement is CheckBox checkBox)
                {
                    bool isIncluded = checkBox.IsChecked ?? true;

                    RunInclusionChanged?.Invoke(this, new RunInclusionChangedEventArgs
                    {
                        RawFileId = data.RawFileId,
                        RunName = data.RunName,
                        IsIncluded = isIncluded
                    });

                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        UpdateClearExclusionsVisibility();
                    }), System.Windows.Threading.DispatcherPriority.Background);
                }
            }
        }

        private void ClearExclusionsButton_Click(object sender, RoutedEventArgs e)
        {
            int excludedHere = _currentGridData?.Count(d => !d.IsIncluded) ?? 0;

            // The handler behind this button deletes every row of excluded_runs, not just the ones in the current
            // selection, so the prompt has to say that outright - manually excluding cells is the QC step and can
            // take an hour to redo.
            var confirm = MessageBox.Show(
                "Restore every manually excluded run in this project?\n\n" +
                $"{excludedHere} excluded run(s) are visible in the current selection, but this clears the " +
                "exclusions for all runs in the project, including ones not shown here.\n\n" +
                "This cannot be undone - each cell would have to be excluded again by hand.",
                "Clear All Exclusions",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning,
                MessageBoxResult.No);

            if (confirm != MessageBoxResult.Yes)
                return;

            // Ask the host to perform the delete. The grid is NOT updated here: the handler is async, so ordering
            // the raise first only means the delete was STARTED, not that it succeeded. If it failed, flipping the
            // rows to "included" would show a state the database does not have. The host calls
            // ConfirmExclusionsCleared() once the delete has actually completed.
            ClearAllExclusionsRequested?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>
        /// Marks every row included and hides the clear button. Called by the host AFTER the exclusions have really
        /// been deleted, so the grid can never claim a state the database does not have.
        /// </summary>
        public void ConfirmExclusionsCleared()
        {
            if (_currentGridData != null)
            {
                foreach (var item in _currentGridData)
                {
                    item.IsIncluded = true;
                }
                SelectedPointsGrid.Items.Refresh();
            }

            ClearExclusionsButton.Visibility = Visibility.Collapsed;
        }

        private void CopyToClipboard_Click(object sender, RoutedEventArgs e)
        {
            if (_currentGridData == null || _currentGridData.Count == 0)
                return;

            var sb = new StringBuilder();

            // Match the visible columns: drop the redundant "Peptides" column for a gene matrix.
            if (_isGeneMatrix)
            {
                sb.AppendLine("Run Name\tCondition\tTIC\tProteins\tContaminant Ratio\tCell Type\tCluster\tClassification Score");
                foreach (var row in _currentGridData)
                {
                    sb.AppendLine($"{row.RunName}\t{row.BiologicalCondition}\t{row.TicValue:E2}\t{row.ProteinCount}\t{row.ContaminantRatioPercent}\t{row.CellType}\t{row.ClusterLabel}\t{row.CompositeScore}");
                }
            }
            else
            {
                sb.AppendLine("Run Name\tCondition\tPeptides\tTIC\tProteins\tContaminant Ratio\tCell Type\tCluster\tClassification Score");
                foreach (var row in _currentGridData)
                {
                    sb.AppendLine($"{row.RunName}\t{row.BiologicalCondition}\t{row.PeptideCount}\t{row.TicValue:E2}\t{row.ProteinCount}\t{row.ContaminantRatioPercent}\t{row.CellType}\t{row.ClusterLabel}\t{row.CompositeScore}");
                }
            }

            Clipboard.SetText(sb.ToString());
        }
    }

    public class RunInclusionChangedEventArgs : EventArgs
    {
        public int RawFileId { get; set; }
        public string RunName { get; set; }
        public bool IsIncluded { get; set; }
    }
}