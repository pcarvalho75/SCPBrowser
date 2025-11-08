using System;
using System.Collections.Generic;
using System.Windows.Controls;

namespace SCPBrowser
{
    public partial class SelectedPointsGridControl : UserControl
    {
        // Event that fires when a row is selected in the grid
        public event EventHandler<SelectedPointData> GridSelectionChanged;

        public SelectedPointsGridControl()
        {
            InitializeComponent();
        }

        /// <summary>
        /// Updates the grid with a new list of selected points
        /// </summary>
        public void UpdateGrid(List<SelectedPointData> gridData)
        {
            SelectedPointsGrid.ItemsSource = gridData;

            if (gridData.Count > 0)
            {
                SelectionCountText.Text = $"({gridData.Count} selected)";
                SelectionStatusText.Text = $"{gridData.Count} point(s) selected";
                SelectedPointsExpander.IsExpanded = true;
            }
            else
            {
                SelectionCountText.Text = "";
                SelectionStatusText.Text = "Click a point or drag to select multiple points. Right-click a point to view details.";
                // We don't collapse the expander automatically, let the user do it
            }
        }

        /// <summary>
        /// Clears the grid and resets status text
        /// </summary>
        public void ClearGrid()
        {
            SelectedPointsGrid.ItemsSource = null;
            SelectionCountText.Text = "";
            SelectionStatusText.Text = "Click a point or drag to select multiple points. Right-click a point to view details.";
        }

        private void SelectedPointsGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (SelectedPointsGrid.SelectedItem is SelectedPointData selectedData)
            {
                // Fire the event to notify the parent (PeptideTicControl)
                GridSelectionChanged?.Invoke(this, selectedData);
            }
        }
    }
}