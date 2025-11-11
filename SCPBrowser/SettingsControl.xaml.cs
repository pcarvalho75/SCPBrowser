using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Win32;

namespace SCPBrowser
{
    public partial class SettingsControl : UserControl
    {
        public SettingsControl()
        {
            InitializeComponent();
            LoadSettings();
        }

        private void LoadSettings()
        {
            PValueUpDown.Value = Settings.Default.GOPValueCutoff;
            MinOverlapUpDown.Value = Settings.Default.GOMinimumOverlap;

            string dbPath = Settings.Default.ReferenceDatabasePath;

            // Initialize default path if empty
            if (string.IsNullOrEmpty(dbPath))
            {
                // Default to Documents/SCPBrowser/reference_data.db
                var documentsFolder = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
                var scpBrowserFolder = Path.Combine(documentsFolder, "SCPBrowser");

                // Create the folder if it doesn't exist
                try
                {
                    if (!Directory.Exists(scpBrowserFolder))
                    {
                        Directory.CreateDirectory(scpBrowserFolder);
                    }
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Could not create SCPBrowser folder: {ex.Message}",
                        "Warning", MessageBoxButton.OK, MessageBoxImage.Warning);
                }

                dbPath = Path.Combine(scpBrowserFolder, "reference_data.db");

                // Save this default path
                Settings.Default.ReferenceDatabasePath = dbPath;
                Settings.Default.Save();
            }

            DatabasePathTextBox.Text = dbPath;
        }

        private void SaveSettings_Click(object sender, RoutedEventArgs e)
        {
            // Validate p-value
            if (!PValueUpDown.Value.HasValue || PValueUpDown.Value.Value <= 0 || PValueUpDown.Value.Value > 0.5)
            {
                MessageBox.Show("P-Value must be between 0.001 and 0.5", "Invalid Value",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // Validate minimum overlap
            if (!MinOverlapUpDown.Value.HasValue || MinOverlapUpDown.Value.Value < 1)
            {
                MessageBox.Show("Minimum Overlap must be at least 1", "Invalid Value",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // Save settings
            Settings.Default.GOPValueCutoff = PValueUpDown.Value.Value;
            Settings.Default.GOMinimumOverlap = MinOverlapUpDown.Value.Value;
            Settings.Default.ReferenceDatabasePath = DatabasePathTextBox.Text;
            Settings.Default.Save();

            // Show success message
            ShowStatusMessage("✓ Settings saved successfully!", true);
        }

        private void ResetDefaults_Click(object sender, RoutedEventArgs e)
        {
            var result = MessageBox.Show(
                "Reset all settings to default values?",
                "Reset Settings",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (result == MessageBoxResult.Yes)
            {
                PValueUpDown.Value = 0.05;
                MinOverlapUpDown.Value = 2;

                var documentsFolder = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
                var scpBrowserFolder = Path.Combine(documentsFolder, "SCPBrowser");
                DatabasePathTextBox.Text = Path.Combine(scpBrowserFolder, "reference_data.db");

                ShowStatusMessage("Settings reset to defaults (click Save to apply)", false);
            }
        }

        private void BrowseDatabasePath_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFileDialog
            {
                Filter = "SQLite Database (*.db)|*.db|All files (*.*)|*.*",
                Title = "Select Reference Database",
                CheckFileExists = false
            };

            // Start from current path if it exists
            if (!string.IsNullOrEmpty(DatabasePathTextBox.Text))
            {
                var dir = Path.GetDirectoryName(DatabasePathTextBox.Text);
                if (Directory.Exists(dir))
                {
                    dialog.InitialDirectory = dir;
                }
            }

            if (dialog.ShowDialog() == true)
            {
                DatabasePathTextBox.Text = dialog.FileName;
            }
        }

        private void ShowStatusMessage(string message, bool isSuccess)
        {
            SaveStatusText.Text = message;
            SaveStatusText.Foreground = isSuccess
                ? (System.Windows.Media.Brush)Application.Current.Resources["SuccessBrush"]
                : (System.Windows.Media.Brush)Application.Current.Resources["WarningBrush"];

            // Clear message after 3 seconds
            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            timer.Tick += (s, args) =>
            {
                SaveStatusText.Text = string.Empty;
                timer.Stop();
            };
            timer.Start();
        }
    }
}