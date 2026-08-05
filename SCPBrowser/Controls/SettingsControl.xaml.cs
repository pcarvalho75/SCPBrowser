using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using SCPBrowser.Services;

namespace SCPBrowser
{
    public partial class SettingsControl : UserControl
    {
        // Must match MainWindow's SETTING_PROTEIN_CUTOFF: the Explorer spinner and this page edit the same
        // per-project value, and MainWindow reads it back when the project is opened.
        private const string SettingKeyProteinCutoff = "ProteinCutoff";

        public event EventHandler SettingsSaved;

        private ProjectDatabaseService _databaseService;

        // The cutoff as it stood when the page was loaded, so Save can tell the user when it actually changed.
        private int _loadedProteinCutoff = DataFilterService.DefaultProteinCutoff;

        public SettingsControl()
        {
            InitializeComponent();
            IsVisibleChanged += SettingsControl_IsVisibleChanged;
            LoadSettingsFromGlobal();
        }

        public void SetDatabaseService(ProjectDatabaseService db)
        {
            _databaseService = db;
            if (IsVisible)
                _ = LoadSettingsAsync();
        }

        private void LoadSettingsFromGlobal()
        {
            PValueUpDown.Value = Settings.Default.GOPValueCutoff;
            MinOverlapUpDown.Value = Settings.Default.GOMinimumOverlap;
            ConfidenceThresholdUpDown.Value = Settings.Default.ClassificationConfidenceThreshold;

            // The protein cutoff is stored per project, not globally, so with no project open all we can show
            // is the factory default.
            _loadedProteinCutoff = DataFilterService.DefaultProteinCutoff;
            ProteinCutoffUpDown.Value = _loadedProteinCutoff;
        }

        private async System.Threading.Tasks.Task LoadSettingsAsync()
        {
            if (_databaseService == null)
            {
                LoadSettingsFromGlobal();
                return;
            }

            var pValue = await _databaseService.GetSettingAsync("GOPValueCutoff");
            var minOverlap = await _databaseService.GetSettingAsync("GOMinimumOverlap");
            var confidence = await _databaseService.GetSettingAsync("ClassificationConfidenceThreshold");
            var proteinCutoff = await _databaseService.GetSettingAsync(SettingKeyProteinCutoff);

            PValueUpDown.Value = pValue != null && TryParsePersistedDouble(pValue, out var p)
                ? p : Settings.Default.GOPValueCutoff;

            MinOverlapUpDown.Value = minOverlap != null && int.TryParse(minOverlap, NumberStyles.Integer,
                CultureInfo.InvariantCulture, out var m)
                ? m : Settings.Default.GOMinimumOverlap;

            ConfidenceThresholdUpDown.Value = confidence != null && TryParsePersistedDouble(confidence, out var c)
                ? c : Settings.Default.ClassificationConfidenceThreshold;

            _loadedProteinCutoff = proteinCutoff != null && int.TryParse(proteinCutoff, NumberStyles.Integer,
                CultureInfo.InvariantCulture, out var pc)
                ? pc : DataFilterService.DefaultProteinCutoff;
            ProteinCutoffUpDown.Value = _loadedProteinCutoff;
        }

        /// <summary>
        /// Parses a persisted decimal setting. New values are written with InvariantCulture, but a project saved
        /// before that fix holds whatever the machine's culture produced ("0,05" on a comma-decimal machine).
        /// Falling straight through to the hardcoded default would silently change an existing analysis, so the
        /// current culture is tried as a fallback for those legacy values.
        /// </summary>
        private static bool TryParsePersistedDouble(string raw, out double value)
        {
            if (double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out value))
                return true;

            return double.TryParse(raw, NumberStyles.Float, CultureInfo.CurrentCulture, out value);
        }

        private async void SaveSettings_Click(object sender, RoutedEventArgs e)
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

            // Validate confidence threshold
            if (!ConfidenceThresholdUpDown.Value.HasValue || ConfidenceThresholdUpDown.Value.Value < 0 || ConfidenceThresholdUpDown.Value.Value > 1)
            {
                MessageBox.Show("Confidence Threshold must be between 0 and 1", "Invalid Value",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // Validate protein cutoff
            if (!ProteinCutoffUpDown.Value.HasValue || ProteinCutoffUpDown.Value.Value < 0)
            {
                MessageBox.Show("Min. Proteins per Cell must be zero or greater", "Invalid Value",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            double pVal = PValueUpDown.Value.Value;
            int minOvl = MinOverlapUpDown.Value.Value;
            double conf = ConfidenceThresholdUpDown.Value ?? 0.5;
            int proteinCutoff = ProteinCutoffUpDown.Value.Value;

            // Always save to global as fallback
            Settings.Default.GOPValueCutoff = pVal;
            Settings.Default.GOMinimumOverlap = minOvl;
            Settings.Default.ClassificationConfidenceThreshold = conf;
            Settings.Default.Save();

            // Save to project DB when a project is open. Write with InvariantCulture so the values survive a
            // reload on a comma-decimal machine - the current culture stores "0,05", which does not parse back.
            if (_databaseService != null)
            {
                await _databaseService.SetSettingAsync("GOPValueCutoff", pVal.ToString(CultureInfo.InvariantCulture));
                await _databaseService.SetSettingAsync("GOMinimumOverlap", minOvl.ToString(CultureInfo.InvariantCulture));
                await _databaseService.SetSettingAsync("ClassificationConfidenceThreshold", conf.ToString(CultureInfo.InvariantCulture));
                await _databaseService.SetSettingAsync(SettingKeyProteinCutoff, proteinCutoff.ToString(CultureInfo.InvariantCulture));
            }

            ShowStatusMessage("✓ Settings saved successfully!", true);
            SettingsSaved?.Invoke(this, EventArgs.Empty);

            // Changing the QC gate here does not re-filter the open session - MainWindow reads it when a project
            // is opened. Say so out loud rather than let the cell count change unannounced on the next open.
            if (_databaseService != null && proteinCutoff != _loadedProteinCutoff)
            {
                MessageBox.Show(
                    $"Min. Proteins per Cell saved as {proteinCutoff} (was {_loadedProteinCutoff}).\n\n" +
                    "It will be applied the next time this project is opened, which will change how many cells " +
                    "enter PCA, UMAP, classification and export.\n\n" +
                    "To apply it to the session you are in now, set 'Min. Proteins G.' on the Explorer tab.",
                    "Protein Cutoff Changed",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);

                _loadedProteinCutoff = proteinCutoff;
            }
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
                ConfidenceThresholdUpDown.Value = 0.1;
                ProteinCutoffUpDown.Value = DataFilterService.DefaultProteinCutoff;

                ShowStatusMessage("Settings reset to defaults (click Save to apply)", false);
            }
        }

        private void Close_Click(object sender, RoutedEventArgs e)
        {
            this.Visibility = Visibility.Collapsed;
        }

        private void SettingsControl_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            if (Visibility == Visibility.Visible)
                _ = LoadSettingsAsync();
        }

        private void ShowStatusMessage(string message, bool isSuccess)
        {
            SaveStatusText.Text = message;
            SaveStatusText.Foreground = isSuccess
                ? (System.Windows.Media.Brush)Application.Current.Resources["SuccessBrush"]
                : (System.Windows.Media.Brush)Application.Current.Resources["WarningBrush"];

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
