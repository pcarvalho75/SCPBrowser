// DeleteConditionConfirmationDialog.xaml.cs
// Modal confirmation for the cascade-delete of a biological condition.
// Shows blast radius, the backup destination, and requires the user to type
// the condition name to enable the destructive button.

using System;
using System.Windows;
using SCPBrowser.Services;

namespace SCPBrowser
{
    public partial class DeleteConditionConfirmationDialog : Window
    {
        private readonly string _conditionName;

        public DeleteConditionConfirmationDialog(
            string conditionName,
            ConditionStats stats,
            string backupDestinationPath,
            long projectSizeBytes)
        {
            InitializeComponent();

            _conditionName = conditionName ?? string.Empty;

            ConditionLine.Text =
                $"You are about to permanently remove the biological condition '{_conditionName}' and every row of data attached to its raw files.";

            RawFilesValue.Text = stats.RawFileCount.ToString("N0");
            PlatesValue.Text = stats.PlateCount.ToString("N0");
            ProteinQuantValue.Text = stats.ProteinQuantRowCount.ToString("N0");
            ClassificationsValue.Text = stats.ClassificationCount.ToString("N0");
            ExclusionsValue.Text = stats.ExclusionCount.ToString("N0");
            OrphanImportsValue.Text = stats.OrphanImportCount.ToString("N0");

            BackupPathValue.Text = string.IsNullOrEmpty(backupDestinationPath)
                ? "(unable to determine backup destination)"
                : backupDestinationPath;
            BackupSizeValue.Text = $"Estimated size: {FormatBytes(projectSizeBytes)}";

            TypeToConfirmLabel.Text = $"To confirm, type the condition name exactly: {_conditionName}";

            ConfirmationTextBox.Focus();
        }

        /// <summary>
        /// True if the user clicked "Backup and Delete" with a matching condition name.
        /// </summary>
        public bool Confirmed { get; private set; }

        private void ConfirmationTextBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        {
            DeleteButton.IsEnabled = string.Equals(
                ConfirmationTextBox.Text?.Trim(),
                _conditionName,
                StringComparison.Ordinal);
        }

        private void DeleteButton_Click(object sender, RoutedEventArgs e)
        {
            Confirmed = true;
            DialogResult = true;
            Close();
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            Confirmed = false;
            DialogResult = false;
            Close();
        }

        private static string FormatBytes(long bytes)
        {
            if (bytes <= 0) return "unknown";

            string[] units = { "B", "KB", "MB", "GB", "TB" };
            double value = bytes;
            int unit = 0;
            while (value >= 1024 && unit < units.Length - 1)
            {
                value /= 1024;
                unit++;
            }
            return $"{value:0.##} {units[unit]}";
        }
    }
}
