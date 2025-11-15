using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using ScottPlot;
using SCPBrowser.GOTools;
using SCPBrowser.Services;

namespace SCPBrowser
{
    public partial class MainControl : UserControl
    {
        public event EventHandler DataLoaded;

        private readonly ParquetDataService _dataService;
        private readonly CellTypeClassificationManager _cellTypeClassificationManager;
        private ProteomicsData _currentData;
        private string _currentFilePath;
        private string _currentFileDirectory;
        private Dictionary<string, CellTypePredictionResult> _cellTypePredictions;
        private readonly GoEnrichmentManager _goEnrichmentManager;
        private Dictionary<string, RunGoEnrichmentResult> _goEnrichmentResults;
        private string _projectDatabasePath;

        public MainControl()
        {
            InitializeComponent();
            _dataService = new ParquetDataService();
            _cellTypeClassificationManager = new CellTypeClassificationManager();
            _goEnrichmentManager = new GoEnrichmentManager();
        }


        private async System.Threading.Tasks.Task LoadTranscriptomicReferenceAsync()
        {
            if (_cellTypeClassificationManager.IsLoaded)
            {
                // Already loaded, nothing to do
                return;
            }

            // Get the project database path (contains both project and reference data)
            var mainWindow = Window.GetWindow(this) as MainWindow;
            if (mainWindow == null || !mainWindow.HasOpenProject)
            {
                _cellTypePredictions = null;
                return;
            }

            var databasePath = mainWindow.ProjectReferenceDatabasePath;

            if (File.Exists(databasePath))
            {
                try
                {
                    StatusText.Text = "Loading transcriptomic reference database...";
                    await _cellTypeClassificationManager.LoadDatabaseAsync(databasePath);

                    var db = _cellTypeClassificationManager.Database;
                    StatusText.Text = $"Reference loaded: {db.TotalGenes:N0} genes, {db.TotalCells:N0} cells, {db.TotalCellTypes} cell types";
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Could not load transcriptomic reference: {ex.Message}\n\nContinuing without cell type predictions.",
                        "Reference Load Error",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);

                    _cellTypePredictions = null;
                    StatusText.Text = "Transcriptomic reference not available";
                }
            }
            else
            {
                _cellTypePredictions = null;
            }
        }


        private async System.Threading.Tasks.Task LoadGoEnrichmentAsync()
        {
            if (_goEnrichmentManager.IsLoaded)
            {
                StatusText.Text = "Running GO enrichment analysis...";

                // Use settings for GO enrichment parameters
                var pValueCutoff = Settings.Default.GOPValueCutoff;
                var minOverlap = Settings.Default.GOMinimumOverlap;

                _goEnrichmentResults = _goEnrichmentManager.EnrichAllRuns(
                    _currentData,
                    pValueCutoff,
                    minOverlap);

                int enrichedCount = _goEnrichmentResults.Count(kvp => !string.IsNullOrEmpty(kvp.Value.TopGoTermId));
                StatusText.Text += $" | GO enrichment: {enrichedCount}/{_currentData.TotalRawFiles} runs";
                return;
            }

            // Get the project database path (contains both project and reference data)
            var mainWindow = Window.GetWindow(this) as MainWindow;
            if (mainWindow == null || !mainWindow.HasOpenProject)
            {
                _goEnrichmentResults = null;
                return;
            }

            var databasePath = mainWindow.ProjectReferenceDatabasePath;

            if (File.Exists(databasePath))
            {
                try
                {
                    StatusText.Text = "Loading GO enrichment database...";
                    await _goEnrichmentManager.LoadDatabaseAsync(databasePath);

                    var db = _goEnrichmentManager.AnnotationDatabase;
                    StatusText.Text = $"GO database loaded: {db.TotalProteins:N0} proteins, {db.GoTermToProteins.Count} GO terms";

                    StatusText.Text = "Running GO enrichment analysis...";

                    // Use settings for GO enrichment parameters
                    var pValueCutoff = Settings.Default.GOPValueCutoff;
                    var minOverlap = Settings.Default.GOMinimumOverlap;

                    _goEnrichmentResults = _goEnrichmentManager.EnrichAllRuns(
                        _currentData,
                        pValueCutoff,
                        minOverlap);

                    int enrichedCount = _goEnrichmentResults.Count(kvp => !string.IsNullOrEmpty(kvp.Value.TopGoTermId));
                    StatusText.Text += $" | GO enrichment: {enrichedCount}/{_currentData.TotalRawFiles} runs";
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Could not load GO enrichment database: {ex.Message}\n\nContinuing without GO enrichment.",
                        "Warning", MessageBoxButton.OK, MessageBoxImage.Warning);
                    _goEnrichmentResults = null;
                }
            }
            else
            {
                _goEnrichmentResults = null;
            }
        }

        public bool IsTranscriptomicDatabaseLoaded()
        {
            return _cellTypeClassificationManager.IsLoaded;
        }

        public async void OpenDiannFile()
        {
            var dialog = new OpenFileDialog
            {
                Filter = "DIA-NN Parquet files (*.parquet)|*.parquet|All files (*.*)|*.*",
                Title = "Open DIA-NN Parquet File"
            };

            if (dialog.ShowDialog() != true)
                return;

            _currentFilePath = dialog.FileName;
            _currentFileDirectory = Path.GetDirectoryName(dialog.FileName);
            await LoadDataAsync();
        }


        public async Task LoadDataFromProject(string parquetFilePath, string databasePath)
        {
            _projectDatabasePath = databasePath; // Store it

            if (string.IsNullOrEmpty(parquetFilePath))
            {
                // No data has been imported yet
                StatusPanel.Visibility = Visibility.Collapsed;
                TotalRunsText.Text = "0";
                TotalProteinsText.Text = "0";
                TotalPeptidesText.Text = "0";
                StatusText.Text = "Project open. Please import a Parquet file to see data.";
                ReloadButton.IsEnabled = false;

                MessageBox.Show(
                    "No data found in this project.\n\n" +
                    "To get started:\n" +
                    "1. Go to Import → Parquet File...\n" +
                    "2. Select your DIA-NN parquet file\n" +
                    "3. Assign biological conditions\n" +
                    "4. Click Import",
                    "No Data Available",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);

                return;
            }

            if (!File.Exists(parquetFilePath))
            {
                // File was imported but now missing
                StatusPanel.Visibility = Visibility.Collapsed;
                TotalRunsText.Text = "0";
                TotalProteinsText.Text = "0";
                TotalPeptidesText.Text = "0";
                StatusText.Text = $"Error: Data file not found at {Path.GetFileName(parquetFilePath)}";
                ReloadButton.IsEnabled = false;

                MessageBox.Show(
                    $"Associated data file not found:\n\n{Path.GetFileName(parquetFilePath)}\n\n" +
                    $"Expected location:\n{parquetFilePath}\n\n" +
                    "The file may have been moved or deleted. Please re-import your data.",
                    "Data File Missing",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);

                return;
            }

            // File exists - load it normally
            _currentFilePath = parquetFilePath;
            _currentFileDirectory = Path.GetDirectoryName(parquetFilePath);

            await LoadDataAsync();
        }



        private async void ReloadButton_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(_currentFilePath))
                return;

            await LoadDataAsync();
        }

        private async System.Threading.Tasks.Task LoadDataAsync()
        {
            try
            {
                // Get reference to the loading overlay from MainWindow
                var mainWindow = Window.GetWindow(this) as MainWindow;
                if (mainWindow != null)
                {
                    mainWindow.LoadingOverlay.SetMessage("Loading Parquet File...");
                    mainWindow.LoadingOverlay.SetProgress("Reading data structure...");
                    mainWindow.LoadingOverlay.Show();
                }

                StatusText.Text = "Loading data...";
                ReloadButton.IsEnabled = false;

                var targetIdsText = TargetProteinIdsTextBox.Text.Trim();
                var targetIds = string.IsNullOrWhiteSpace(targetIdsText)
                    ? new System.Collections.Generic.List<string>()
                    : targetIdsText.Split(',')
                        .Select(id => id.Trim())
                        .Where(id => !string.IsNullOrWhiteSpace(id))
                        .ToList();

                var mapping = new ColumnMapping
                {
                    RawFileColumn = "Run",
                    ProteinGroupColumn = "Protein.Group",
                    PeptideColumn = "Modified.Sequence",
                    TotalIonCurrentColumn = "Ms1.Area",
                    TargetProteinIdentifiers = targetIds
                };

                if (mainWindow != null)
                {
                    mainWindow.LoadingOverlay.SetProgress("Parsing proteomics data...");
                }

                _currentData = await _dataService.LoadParquetFileAsync(_currentFilePath, mapping);

                TotalRunsText.Text = _currentData.TotalRawFiles.ToString();
                TotalProteinsText.Text = _currentData.TotalProteinGroups.ToString();
                TotalPeptidesText.Text = _currentData.TotalPeptides.ToString();

                UpdateChart(_currentData);

                StatusPanel.Visibility = Visibility.Visible;

                if (mainWindow != null)
                {
                    mainWindow.LoadingOverlay.SetProgress("Loading reference databases...");
                }

                await LoadTranscriptomicReferenceAsync();
                await LoadGoEnrichmentAsync();

                var targetInfo = targetIds.Count > 0
                    ? $" (tracking {targetIds.Count} target protein(s))"
                    : "";
                StatusText.Text = $"Loaded successfully: {_currentData.TotalRawFiles} runs{targetInfo}";

                if (mainWindow != null)
                {
                    mainWindow.LoadingOverlay.Hide();
                }

                // *** CRITICAL FIX: Raise the DataLoaded event after everything is loaded ***
                Console.WriteLine("Raising DataLoaded event to populate other tabs");
                DataLoaded?.Invoke(this, EventArgs.Empty);
            }
            catch (Exception ex)
            {
                var mainWindow = Window.GetWindow(this) as MainWindow;
                if (mainWindow != null)
                {
                    mainWindow.LoadingOverlay.Hide();
                }

                MessageBox.Show($"Error reading file: {ex.Message}", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                StatusText.Text = "Error loading file";
            }
            finally
            {
                ReloadButton.IsEnabled = !string.IsNullOrEmpty(_currentFilePath);
            }
        }

        public async Task<Dictionary<string, CellTypePredictionResult>> GetCellTypePredictionsAsync(
            ProteomicsData proteomicsData,
            string projectDatabasePath,
            int importId,
            IProgressReporter progressReporter)
        {
            var predictions = await _cellTypeClassificationManager.GetOrComputePredictionsAsync(
                proteomicsData,
                projectDatabasePath,
                importId,
                progressReporter);

            // Store in memory for future use
            _cellTypePredictions = predictions;

            // =================================================================
            // === ADD THESE TWO LINES TO CALL THE DEBUG METHOD ===
            var firstRunName = proteomicsData.RawFileNames.FirstOrDefault();
            if (firstRunName != null)
            {
                _cellTypeClassificationManager.DiagnoseProteinGeneMapping(proteomicsData, firstRunName);
            }
            // =================================================================

            return predictions;
        }

        private void UpdateChart(ProteomicsData data)
        {
            ProteinChart.Plot.Clear();

            if (data.ProteinCountPerFile.Count == 0)
            {
                StatusText.Text = "No data to display";
                return;
            }

            var sortedData = data.ProteinCountPerFile
                .OrderByDescending(kvp => kvp.Value)
                .ToList();

            var positions = Enumerable.Range(0, sortedData.Count).Select(i => (double)i).ToArray();
            var values = sortedData.Select(kvp => (double)kvp.Value).ToArray();
            var labels = sortedData.Select(kvp => kvp.Key).ToArray();

            var barPlot = ProteinChart.Plot.Add.Bars(positions, values);

            foreach (var bar in barPlot.Bars)
            {
                bar.FillColor = ScottPlot.Color.FromHex("#2563eb");
            }

            ProteinChart.Plot.Axes.Bottom.TickGenerator = new ScottPlot.TickGenerators.NumericManual(
                positions.Select((pos, idx) => new Tick(pos, labels[idx])).ToArray()
            );

            ProteinChart.Plot.Axes.Bottom.TickLabelStyle.Rotation = 45;
            ProteinChart.Plot.Axes.Bottom.TickLabelStyle.Alignment = Alignment.MiddleLeft;

            ProteinChart.Plot.Axes.Left.Label.Text = "Number of Protein Groups";
            ProteinChart.Plot.Axes.Bottom.Label.Text = "Raw File";

            ProteinChart.Plot.Axes.Margins(bottom: 0);

            ProteinChart.Refresh();
        }

        public ProteomicsData GetCurrentData()
        {
            return _currentData;
        }

        public string GetCurrentFileDirectory()
        {
            return _currentFileDirectory;
        }

        public Dictionary<string, CellTypePredictionResult> GetCellTypePredictions()
        {
            return _cellTypePredictions;
        }

        public Dictionary<string, System.Windows.Media.Color> GetCellTypeColorMap()
        {
            return _cellTypeClassificationManager.IsLoaded
                ? _cellTypeClassificationManager.GenerateCellTypeColorMap()
                : new Dictionary<string, System.Windows.Media.Color>();
        }

        public Dictionary<string, RunGoEnrichmentResult> GetGoEnrichmentResults()
        {
            return _goEnrichmentResults;
        }

        public Dictionary<string, System.Windows.Media.Color> GetGoTermColorMap()
        {
            return _goEnrichmentManager.IsLoaded && _goEnrichmentResults != null
                ? _goEnrichmentManager.GenerateGoTermColorMap(_goEnrichmentResults)
                : new Dictionary<string, System.Windows.Media.Color>();
        }
    }
}