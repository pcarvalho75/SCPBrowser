using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace SCPBrowser
{
    public partial class SelectedPointsGridControl : UserControl
    {
        private List<SelectedPointData> _currentGridData;

        // Event that fires when a row is selected in the grid
        public event EventHandler<SelectedPointData> GridSelectionChanged;

        // Event that fires when a run's inclusion status changes
        public event EventHandler<RunInclusionChangedEventArgs> RunInclusionChanged;

        // Event that fires when "Clear All Exclusions" is clicked
        public event EventHandler ClearAllExclusionsRequested;

        public SelectedPointsGridControl()
        {
            InitializeComponent();
        }

        /// <summary>
        /// Updates the grid with a new list of selected points
        /// </summary>
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

            // Show/hide clear exclusions button based on whether there are exclusions
            UpdateClearExclusionsVisibility();
        }

        /// <summary>
        /// Updates the selection rule text display
        /// </summary>
        public void UpdateSelectionRuleText(string ruleText)
        {
            SelectionRuleText.Text = ruleText;
        }

        /// <summary>
        /// Shows or hides the Clear Exclusions button
        /// </summary>
        public void SetHasExclusions(bool hasExclusions)
        {
            ClearExclusionsButton.Visibility = hasExclusions ? Visibility.Visible : Visibility.Collapsed;
        }

        /// <summary>
        /// Clears the grid and resets status text
        /// </summary>
        public void ClearGrid()
        {
            Console.WriteLine("GridCleared");
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
                // Fire the event to notify the parent (PeptideTicControl)
                GridSelectionChanged?.Invoke(this, selectedData);
            }

            // Check for checkbox changes (IsIncluded property)
            if (e.AddedItems.Count > 0 || e.RemovedItems.Count > 0)
            {
                CheckForInclusionChanges();
            }
        }

        private void CheckForInclusionChanges()
        {
            // This gets called on selection change, but we need to detect checkbox changes
            // We'll handle this through the CellEditEnding event instead
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
                // Get the checkbox value
                if (e.EditingElement is CheckBox checkBox)
                {
                    bool isIncluded = checkBox.IsChecked ?? true;

                    // Fire event to notify parent
                    RunInclusionChanged?.Invoke(this, new RunInclusionChangedEventArgs
                    {
                        RawFileId = data.RawFileId,
                        RunName = data.RunName,
                        IsIncluded = isIncluded
                    });

                    // Update button visibility after a short delay to let binding update
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        UpdateClearExclusionsVisibility();
                    }), System.Windows.Threading.DispatcherPriority.Background);
                }
            }
        }

        private void ClearExclusionsButton_Click(object sender, RoutedEventArgs e)
        {
            // Re-check all items in the grid
            if (_currentGridData != null)
            {
                foreach (var item in _currentGridData)
                {
                    item.IsIncluded = true;
                }
                SelectedPointsGrid.Items.Refresh();
            }

            ClearExclusionsButton.Visibility = Visibility.Collapsed;

            // Notify parent to clear exclusions in database
            ClearAllExclusionsRequested?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>
    /// Event args for run inclusion changes
    /// </summary>
    public class RunInclusionChangedEventArgs : EventArgs
    {
        public int RawFileId { get; set; }
        public string RunName { get; set; }
        public bool IsIncluded { get; set; }
    }
}