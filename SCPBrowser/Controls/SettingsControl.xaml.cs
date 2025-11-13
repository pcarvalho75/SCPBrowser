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

                ShowStatusMessage("Settings reset to defaults (click Save to apply)", false);
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