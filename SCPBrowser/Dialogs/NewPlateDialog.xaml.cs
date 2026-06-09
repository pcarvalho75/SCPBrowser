using System;
using System.IO;
using System.Windows;
using SCPBrowser.Models;
using SCPBrowser.Services;

namespace SCPBrowser
{
    public partial class NewPlateDialog : Window
    {
        public PlateInfo PlateInfo { get; private set; }

        /// <summary>
        /// If the user attached a cellenONE isolation run, the resolved .Run folder; otherwise null.
        /// The caller persists the plate first, then imports this run against the new plate_id.
        /// </summary>
        public string? CellenOneRunDir { get; private set; }

        /// <summary>Number of isolated cells found in the attached cellenONE run (0 if none attached).</summary>
        public int CellenOneCellCount { get; private set; }

        public NewPlateDialog()
        {
            InitializeComponent();
        }

        private void Input_Changed(object sender, System.Windows.Controls.TextChangedEventArgs e)
        {
            ValidateInputs();
        }

        private void ValidateInputs()
        {
            bool isValid = !string.IsNullOrWhiteSpace(PlateNameTextBox.Text);
            CreateButton.IsEnabled = isValid;
        }

        private void ImportCellenOne_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFolderDialog
            {
                Title = "Select a cellenONE isolation .Run folder"
            };
            if (dlg.ShowDialog() != true)
                return;

            string folder = dlg.FolderName;
            try
            {
                if (!CellenOneParser.IsIsolationRun(folder))
                {
                    MessageBox.Show(
                        "That folder doesn't look like a cellenONE isolation run (no *_isolated.xls found).\n\n" +
                        "Pick the '.Run' folder that contains the isolated cells.",
                        "No isolated cells",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                var run = CellenOneParser.ParseRun(folder);
                if (run.Cells.Count == 0)
                {
                    MessageBox.Show("No isolated cells were found in that run.", "Nothing to import",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                CellenOneRunDir = folder;
                CellenOneCellCount = run.Cells.Count;

                // Auto-fill empty fields only (never overwrite what the user already typed).
                if (string.IsNullOrWhiteSpace(PlateNameTextBox.Text))
                    PlateNameTextBox.Text = run.RunUid ?? Path.GetFileName(folder.TrimEnd('\\', '/'));
                if (RunDatePicker.SelectedDate == null && DateTime.TryParse(run.RunDate, out var dt))
                    RunDatePicker.SelectedDate = dt;
                if (string.IsNullOrWhiteSpace(InstrumentNameTextBox.Text) && !string.IsNullOrWhiteSpace(run.Instrument))
                    InstrumentNameTextBox.Text = run.Instrument;
                if (string.IsNullOrWhiteSpace(OperatorNameTextBox.Text) && !string.IsNullOrWhiteSpace(run.OperatorName))
                    OperatorNameTextBox.Text = run.OperatorName;

                CellenOneStatusText.Text = $"✓ {run.Cells.Count} cells from {Path.GetFileName(folder.TrimEnd('\\', '/'))}";
                ValidateInputs();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to read cellenONE run:\n\n{ex.Message}", "Import error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void Create_Click(object sender, RoutedEventArgs e)
        {
            PlateInfo = new PlateInfo
            {
                PlateName = PlateNameTextBox.Text.Trim(),
                RunDate = RunDatePicker.SelectedDate?.ToString("yyyy-MM-dd") ?? "",
                InstrumentName = InstrumentNameTextBox.Text.Trim(),
                OperatorName = OperatorNameTextBox.Text.Trim(),
                Description = DescriptionTextBox.Text.Trim()
            };

            DialogResult = true;
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
        }
    }
}
