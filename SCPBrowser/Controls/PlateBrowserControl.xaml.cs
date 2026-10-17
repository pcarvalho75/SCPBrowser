// PlateBrowserControl.xaml.cs
// Child control for browsing plate data and raw files
// Location: SCPBrowser/Controls/PlateBrowserControl.xaml.cs

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using SCPBrowser.Models;
using SCPBrowser.Services;

namespace SCPBrowser
{
    public partial class PlateBrowserControl : UserControl
    {
        private PlateService _plateService;
        private ParquetDataService _parquetService;
        private System.Collections.Generic.List<PlateInfo> _plates;
        private List<RawFileInfo> _allRawFiles = new();

        // A dot clicked on the plot names a run; it is selected in the grid once its plate's runs are bound.
        private string? _pendingRunSelection;

        // Column headers plus two rows of the raw-file grid: what it keeps when the window is short.
        private const double RawFilesGridMinimum = 104;

        // The plot/grid split while a plate is selected, kept when the user drags the splitter.
        private GridLength _plotShare = new(2, GridUnitType.Star);
        private GridLength _gridShare = new(1, GridUnitType.Star);

        /// <summary>
        /// The data as loaded for analysis. Set by the host; the database stores only per-run counts, which cannot be
        /// combined into a plate's distinct proteins and peptides.
        /// </summary>
        public Func<ProteomicsData?>? LoadedDataSource { get; set; }

        /// <summary>Plate colors by plate id for the box plot (PlateFilterControl's, the single source).</summary>
        public Func<IReadOnlyDictionary<int, Color>?>? PlateColorSource
        {
            get => IdentificationsPlot.PlateColorSource;
            set => IdentificationsPlot.PlateColorSource = value;
        }

        public PlateBrowserControl()
        {
            InitializeComponent();
            IdentificationsPlot.CategoryClicked += IdentificationsPlot_CategoryClicked;
            IdentificationsPlot.RunClicked += IdentificationsPlot_RunClicked;
            ScrollingPaneLayout.Attach(RightPaneScroller, RightPane, RequiredRightPaneHeight);
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

            // Identifications per run of every plate, from the data as loaded for the Explorer
            _allRawFiles = allRawFiles;
            IdentificationsPlot.SetData(allRawFiles, _plates, LoadedDataSource?.Invoke(), await GetExcludedRunNamesAsync());
            IdentificationsPlot.HighlightedKey = (PlateListBox.SelectedItem as PlateInfo)?.PlateId.ToString(CultureInfo.InvariantCulture);
        }

        /// <summary>Manually excluded runs (excluded_runs); empty if the table cannot be read.</summary>
        private async Task<HashSet<string>> GetExcludedRunNamesAsync()
        {
            try
            {
                return await _parquetService.GetExcludedRunNamesAsync();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[PlateBrowserControl] Could not read excluded runs: {ex.Message}");
                return new HashSet<string>();
            }
        }

        private void IdentificationsPlot_CategoryClicked(object? sender, string plateKey)
        {
            var plate = _plates?.FirstOrDefault(p => p.PlateId.ToString(CultureInfo.InvariantCulture) == plateKey);
            if (plate == null)
                return;
            PlateListBox.SelectedItem = plate;
            PlateListBox.ScrollIntoView(plate);
        }

        private void IdentificationsPlot_RunClicked(object? sender, string runName)
        {
            var run = _allRawFiles.FirstOrDefault(rf => rf.RawFileName == runName);
            var plate = run?.PlateId is int plateId ? _plates?.FirstOrDefault(p => p.PlateId == plateId) : null;
            if (plate == null)
                return;

            if (ReferenceEquals(PlateListBox.SelectedItem, plate))
            {
                SelectRunInGrid(runName);
                return;
            }
            _pendingRunSelection = runName;
            PlateListBox.SelectedItem = plate;
            PlateListBox.ScrollIntoView(plate);
        }

        /// <summary>
        /// Height the right pane needs: the plate header, the plot at its minimum plot area, and, while a plate is
        /// selected, the raw-file grid at two rows and the summary bar. Also keeps the plot row's minimum at the
        /// plot's, so the grid gives up its room first.
        /// </summary>
        private double RequiredRightPaneHeight()
        {
            double plot = IdentificationsPlot.MinimumHeight() + IdentificationsPlot.Margin.Top + IdentificationsPlot.Margin.Bottom;
            if (Math.Abs(PlotRow.MinHeight - plot) > 0.5)
                PlotRow.MinHeight = plot;
            double required = ScrollingPaneLayout.OuterHeight(PlateHeaderBorder) + plot
                + ScrollingPaneLayout.OuterHeight(SelectedPlateStatsPanel);
            if (PlotGridSplitter.Visibility == Visibility.Visible)
                required += ScrollingPaneLayout.OuterHeight(PlotGridSplitter) + RawFilesRow.MinHeight;
            return required;
        }

        /// <summary>
        /// The raw-file grid takes room under the plot only while a plate is selected (it is empty otherwise); the plot
        /// has the whole area until then. A split the user dragged is restored the next time a plate is selected.
        /// </summary>
        private void ShowGridArea(bool show)
        {
            bool shown = PlotGridSplitter.Visibility == Visibility.Visible;
            if (show == shown)
                return;
            if (show)
            {
                PlotRow.Height = _plotShare;
                RawFilesRow.Height = _gridShare;
                RawFilesRow.MinHeight = RawFilesGridMinimum;
                PlotGridSplitter.Visibility = Visibility.Visible;
            }
            else
            {
                if (PlotRow.Height.IsStar && RawFilesRow.Height.IsStar && RawFilesRow.Height.Value > 0)
                    (_plotShare, _gridShare) = (PlotRow.Height, RawFilesRow.Height);
                RawFilesRow.MinHeight = 0;
                RawFilesRow.Height = new GridLength(0);
                PlotRow.Height = new GridLength(1, GridUnitType.Star);
                PlotGridSplitter.Visibility = Visibility.Collapsed;
            }
        }

        private void SelectRunInGrid(string runName)
        {
            if (RawFilesGrid.ItemsSource is not IEnumerable<RawFileInfo> rows)
                return;
            var row = rows.FirstOrDefault(rf => rf.RawFileName == runName);
            if (row == null)
                return;
            RawFilesGrid.SelectedItem = row;
            RawFilesGrid.ScrollIntoView(row);
        }

        private async void PlateListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (PlateListBox.SelectedItem is PlateInfo selectedPlate)
            {
                IdentificationsPlot.HighlightedKey = selectedPlate.PlateId.ToString(CultureInfo.InvariantCulture);
                ShowGridArea(true);
                await ShowPlateRawFilesAsync(selectedPlate);
            }
            else
            {
                // Back to the pane as it is before any plate is chosen, so no stale summary takes the plot's room.
                IdentificationsPlot.HighlightedKey = null;
                RawFilesHeaderText.Text = "Select a plate to view raw files";
                PlateDetailsText.Visibility = Visibility.Collapsed;
                RawFilesGrid.ItemsSource = null;
                SelectedPlateStatsPanel.Visibility = Visibility.Collapsed;
                ShowGridArea(false);
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

            // Another plate may have been selected while these runs were read; its grid must not be overwritten.
            if (!ReferenceEquals(PlateListBox.SelectedItem, plate))
                return;

            // Bind to DataGrid
            RawFilesGrid.ItemsSource = rawFiles;
            if (_pendingRunSelection != null)
            {
                SelectRunInGrid(_pendingRunSelection);
                _pendingRunSelection = null;
            }

            // Calculate and show statistics
            if (rawFiles.Count > 0)
            {
                // A protein or peptide identified in several runs is still one protein or peptide, so the plate's
                // totals are distinct counts across its runs. Summing the per-run counts in the grid counts each one
                // once for every run that saw it.
                var loadedData = LoadedDataSource?.Invoke();
                var runNames = rawFiles.Select(rf => rf.RawFileName).ToList();
                var (distinctProteins, distinctPeptides) = loadedData == null
                    ? (null, null)
                    : await Task.Run(() => loadedData.CountDistinctAcrossRuns(runNames));

                // Another plate may have been selected while counting; its summary must not be overwritten.
                if (!ReferenceEquals(PlateListBox.SelectedItem, plate))
                    return;

                SelectedPlateRawFilesText.Text = rawFiles.Count.ToString("N0");
                SelectedPlateProteinsText.Text = distinctProteins?.ToString("N0") ?? "n/a";
                SelectedPlatePeptidesText.Text = distinctPeptides?.ToString("N0") ?? "n/a";
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