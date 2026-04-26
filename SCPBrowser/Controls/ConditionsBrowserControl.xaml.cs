// ConditionsBrowserControl.xaml.cs
// Child control for browsing biological conditions and triggering condition-level cascade deletes.
// Location: SCPBrowser/Controls/ConditionsBrowserControl.xaml.cs

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using SCPBrowser.Services;

namespace SCPBrowser
{
    /// <summary>
    /// Raised after a successful cascade delete so parent controls and the main
    /// window can refresh dependent data and views.
    /// </summary>
    public class ConditionDeletedEventArgs : EventArgs
    {
        public string Condition { get; init; } = string.Empty;
        public DeleteConditionResult DeleteResult { get; init; } = new();
        public string BackupPath { get; init; } = string.Empty;
    }

    public partial class ConditionsBrowserControl : UserControl
    {
        private string _databasePath = string.Empty;
        private ParquetDataService? _parquetService;

        /// <summary>
        /// Lightweight DTO for the left-side conditions list.
        /// </summary>
        private class ConditionListItem
        {
            public string Name { get; set; } = string.Empty;
            public int RawFileCount { get; set; }
        }

        /// <summary>
        /// Raised after a successful cascade delete. Parents should refresh dependent state.
        /// </summary>
        public event EventHandler<ConditionDeletedEventArgs>? ConditionDeleted;

        public ConditionsBrowserControl()
        {
            InitializeComponent();
        }

        /// <summary>
        /// Loads the list of biological conditions for the given project database.
        /// </summary>
        public async Task LoadDataAsync(string databasePath)
        {
            _databasePath = databasePath;
            _parquetService = new ParquetDataService(databasePath);
            await PopulateConditionsListAsync();
            ResetDetailsPane();
        }

        private async Task PopulateConditionsListAsync()
        {
            if (_parquetService == null)
                return;

            var allRawFiles = await _parquetService.GetRawFilesAsync();
            var items = allRawFiles
                .Where(rf => !string.IsNullOrEmpty(rf.BiologicalCondition))
                .GroupBy(rf => rf.BiologicalCondition!)
                .Select(g => new ConditionListItem
                {
                    Name = g.Key,
                    RawFileCount = g.Count()
                })
                .OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            ConditionsListBox.ItemsSource = items;
        }

        private async void ConditionsListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (ConditionsListBox.SelectedItem is ConditionListItem selected)
            {
                await LoadConditionStatsAsync(selected.Name);
            }
            else
            {
                ResetDetailsPane();
            }
        }

        private async Task LoadConditionStatsAsync(string condition)
        {
            if (_parquetService == null)
                return;

            try
            {
                var stats = await _parquetService.GetConditionStatsAsync(condition);

                ConditionHeaderText.Text = $"Condition: {condition}";
                ConditionSubText.Text =
                    $"Removing this condition will purge {stats.RawFileCount:N0} raw file(s) and all dependent rows.";
                ConditionSubText.Visibility = Visibility.Visible;

                StatRawFilesText.Text = stats.RawFileCount.ToString("N0");
                StatPlatesText.Text = stats.PlateCount.ToString("N0");
                StatProteinQuantText.Text = stats.ProteinQuantRowCount.ToString("N0");
                StatClassificationsText.Text = stats.ClassificationCount.ToString("N0");
                StatExclusionsText.Text = stats.ExclusionCount.ToString("N0");
                StatOrphanImportsText.Text = stats.OrphanImportCount.ToString("N0");

                PlateNamesList.ItemsSource = stats.PlateNames;

                StatsPanel.Visibility = Visibility.Visible;
                DangerZonePanel.Visibility = Visibility.Visible;
                DeleteConditionButton.IsEnabled = stats.RawFileCount > 0;
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    $"Error loading condition statistics:\n\n{ex.Message}",
                    "Stats Error",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }

        private void ResetDetailsPane()
        {
            ConditionHeaderText.Text = "Select a condition to view its impact";
            ConditionSubText.Text = string.Empty;
            ConditionSubText.Visibility = Visibility.Collapsed;
            StatsPanel.Visibility = Visibility.Collapsed;
            DangerZonePanel.Visibility = Visibility.Collapsed;
            PlateNamesList.ItemsSource = null;
        }

        private async void DeleteConditionButton_Click(object sender, RoutedEventArgs e)
        {
            if (_parquetService == null || string.IsNullOrEmpty(_databasePath))
                return;

            if (ConditionsListBox.SelectedItem is not ConditionListItem selected)
                return;

            string condition = selected.Name;

            try
            {
                // Re-fetch stats fresh so the dialog never shows stale numbers.
                var stats = await _parquetService.GetConditionStatsAsync(condition);
                if (stats.RawFileCount == 0)
                {
                    MessageBox.Show(
                        $"No raw files are currently labeled '{condition}'. Nothing to delete.",
                        "Nothing To Delete",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                    await PopulateConditionsListAsync();
                    return;
                }

                var backupService = new ProjectBackupService();
                string reason = $"before-delete-{condition}";
                // Build the backup path ONCE so the dialog and the actual backup folder match exactly.
                string backupPath = backupService.BuildBackupPath(_databasePath, reason);
                long projectSize = await backupService.EstimateProjectSizeAsync(_databasePath);

                var dialog = new DeleteConditionConfirmationDialog(condition, stats, backupPath, projectSize)
                {
                    Owner = Window.GetWindow(this)
                };

                if (dialog.ShowDialog() != true || !dialog.Confirmed)
                    return;

                // 1. Backup first. Abort the whole flow if backup fails.
                DeleteConditionButton.IsEnabled = false;
                var backupResult = await backupService.CreateProjectBackupAsync(_databasePath, backupPath);
                if (!backupResult.Success)
                {
                    MessageBox.Show(
                        $"Backup failed; the delete was NOT performed.\n\n{backupResult.ErrorMessage}",
                        "Backup Failed",
                        MessageBoxButton.OK,
                        MessageBoxImage.Error);
                    DeleteConditionButton.IsEnabled = true;
                    return;
                }

                // 2. Cascade delete in a transaction.
                DeleteConditionResult deleteResult;
                try
                {
                    deleteResult = await _parquetService.DeleteConditionCascadeAsync(condition);
                }
                catch (Exception delEx)
                {
                    MessageBox.Show(
                        $"Backup succeeded at:\n{backupResult.BackupPath}\n\n" +
                        $"But the cascade delete failed and was rolled back:\n{delEx.Message}",
                        "Delete Failed (Backup OK)",
                        MessageBoxButton.OK,
                        MessageBoxImage.Error);
                    DeleteConditionButton.IsEnabled = true;
                    return;
                }

                // 3. Refresh local UI.
                await PopulateConditionsListAsync();
                ResetDetailsPane();

                // 4. Show summary BEFORE bubbling the event so the user sees confirmation
                //    before the parent's reload spinner takes over the screen.
                MessageBox.Show(
                    $"Condition '{condition}' was deleted.\n\n" +
                    $"Removed:\n" +
                    $"  Raw files: {deleteResult.DeletedRawFiles:N0}\n" +
                    $"  Protein quant rows: {deleteResult.DeletedProteinQuantRows:N0}\n" +
                    $"  Cell type classifications: {deleteResult.DeletedClassifications:N0}\n" +
                    $"  Run exclusions: {deleteResult.DeletedExclusions:N0}\n" +
                    $"  Orphan parquet imports: {deleteResult.DeletedOrphanImports:N0}\n\n" +
                    $"Backup saved to:\n{backupResult.BackupPath}",
                    "Delete Complete",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);

                // 5. Tell parents to refresh their own dependent state (plate browser, analysis tabs).
                ConditionDeleted?.Invoke(this, new ConditionDeletedEventArgs
                {
                    Condition = condition,
                    DeleteResult = deleteResult,
                    BackupPath = backupResult.BackupPath
                });
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    $"Unexpected error while deleting condition:\n\n{ex.Message}",
                    "Delete Error",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
                DeleteConditionButton.IsEnabled = true;
            }
        }
    }
}
