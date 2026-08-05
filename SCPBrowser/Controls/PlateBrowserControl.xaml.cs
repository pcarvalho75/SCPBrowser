// PlateBrowserControl.xaml.cs
// Child control for browsing plate data and raw files
// Location: SCPBrowser/Controls/PlateBrowserControl.xaml.cs

using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using SCPBrowser.Models;
using SCPBrowser.Services;

namespace SCPBrowser
{
    public partial class PlateBrowserControl : UserControl
    {
        private PlateService _plateService;
        private ParquetDataService _parquetService;
        private System.Collections.Generic.List<PlateInfo> _plates;

        public PlateBrowserControl()
        {
            InitializeComponent();

        }

        /// <summary>
        /// Loads plate data from the database and populates the UI
        /// </summary>
        /// <summary>
        /// Loads plate data from the database and populates the UI
        /// </summary>
        public async Task LoadDataAsync(string databasePath)
        {
            try
            {


                // Initialize services with the database path
                _plateService = new PlateService(databasePath);
                _parquetService = new ParquetDataService(databasePath);

                // Load plates
                _plates = await _plateService.GetPlatesAsync();

                // Populate the UI
                await PopulatePlateUIAsync();


            }
            catch (Exception)
            {

                throw; // Let the parent handle the error display
            }
        }

        private async Task PopulatePlateUIAsync()
        {
            // Update summary statistics
            TotalPlatesText.Text = _plates.Count.ToString("N0");

            // Get all raw files to calculate totals using ParquetDataService
            var allRawFiles = await _parquetService.GetRawFilesAsync();
            TotalRawFilesText.Text = allRawFiles.Count.ToString("N0");

            // Count unique biological conditions
            var uniqueConditions = allRawFiles
                .Where(rf => !string.IsNullOrEmpty(rf.BiologicalCondition))
                .Select(rf => rf.BiologicalCondition)
                .Distinct()
                .Count();
            TotalConditionsText.Text = uniqueConditions.ToString("N0");

            // Populate plate list
            PlateListBox.ItemsSource = _plates;
        }

        private async void PlateListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (PlateListBox.SelectedItem is PlateInfo selectedPlate)
            {
                await ShowPlateRawFilesAsync(selectedPlate);
            }
        }

        private async Task ShowPlateRawFilesAsync(PlateInfo plate)
        {
            // Update header
            RawFilesHeaderText.Text = $"Plate: {plate.PlateName}";

            // Show plate details
            var detailsParts = new System.Collections.Generic.List<string>();
            if (!string.IsNullOrEmpty(plate.RunDate))
                detailsParts.Add($"Run Date: {plate.RunDate}");
            if (!string.IsNullOrEmpty(plate.InstrumentName))
                detailsParts.Add($"Instrument: {plate.InstrumentName}");
            if (!string.IsNullOrEmpty(plate.OperatorName))
                detailsParts.Add($"Operator: {plate.OperatorName}");
            if (!string.IsNullOrEmpty(plate.Description))
                detailsParts.Add($"Description: {plate.Description}");

            if (detailsParts.Count > 0)
            {
                PlateDetailsText.Text = string.Join(" • ", detailsParts);
                PlateDetailsText.Visibility = Visibility.Visible;
            }
            else
            {
                PlateDetailsText.Visibility = Visibility.Collapsed;
            }

            // Get raw files for this plate using ParquetDataService
            var rawFiles = await _parquetService.GetRawFilesAsync(plateId: plate.PlateId);

            // Bind to DataGrid
            RawFilesGrid.ItemsSource = rawFiles;

            // Calculate and show statistics
            if (rawFiles.Count > 0)
            {
                SelectedPlateRawFilesText.Text = rawFiles.Count.ToString("N0");
                SelectedPlateProteinsText.Text = rawFiles.Sum(rf => rf.ProteinCount).ToString("N0");
                SelectedPlatePeptidesText.Text = rawFiles.Sum(rf => rf.PeptideCount).ToString("N0");
                SelectedPlateTICText.Text = rawFiles.Sum(rf => rf.TotalIonCurrent).ToString("N0");
                SelectedPlateStatsPanel.Visibility = Visibility.Visible;
            }
            else
            {
                SelectedPlateStatsPanel.Visibility = Visibility.Collapsed;
            }


        }
    }
}