using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;

namespace SCPBrowser
{
    public partial class SelectedPointsGridControl : UserControl
    {
        private List<SelectedPointData> _currentGridData;

        public event EventHandler<SelectedPointData> GridSelectionChanged;
        public event EventHandler<RunInclusionChangedEventArgs> RunInclusionChanged;
        public event EventHandler ClearAllExclusionsRequested;

        public SelectedPointsGridControl()
        {
            InitializeComponent();
        }

        public void UpdateGrid(List<SelectedPointData> gridData)
        {
            _currentGridData = gridData;
            SelectedPointsGrid.ItemsSource = gridData;

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

        private void SelectedPointsGrid_CellEditEnding(object sender, DataGridCellEditEndingEventArgs e)
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
            if (_currentGridData != null)
            {
                foreach (var item in _currentGridData)
                {
                    item.IsIncluded = true;
                }
                SelectedPointsGrid.Items.Refresh();
            }

            ClearExclusionsButton.Visibility = Visibility.Collapsed;
            ClearAllExclusionsRequested?.Invoke(this, EventArgs.Empty);
        }

        private void CopyToClipboard_Click(object sender, RoutedEventArgs e)
        {
            if (_currentGridData == null || _currentGridData.Count == 0)
                return;

            var sb = new StringBuilder();
            sb.AppendLine("Run Name\tCondition\tPeptides\tTIC\tProteins\tContaminant Ratio\tCell Type\tClassification Score");

            foreach (var row in _currentGridData)
            {
                sb.AppendLine($"{row.RunName}\t{row.BiologicalCondition}\t{row.PeptideCount}\t{row.TicValue:E2}\t{row.ProteinCount}\t{row.ContaminantRatioPercent}\t{row.CellType}\t{row.CompositeScore}");
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