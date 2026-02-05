using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
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
        private List<string> _allParquetFilePaths;
        public event EventHandler<int> ProteinCutoffChanged;
        private Dictionary<string, int> _rawFileToPlateId;
        private Dictionary<int, string> _plateIdToName;


        public int ProteinCutoff
        {
            get => ProteinCutoffControl.Value;
            set => ProteinCutoffControl.Value = value;
        }

        public MainControl()
        {
            InitializeComponent();
            _dataService = new ParquetDataService();
            _cellTypeClassificationManager = new CellTypeClassificationManager();
            _goEnrichmentManager = new GoEnrichmentManager();
        }


        private void ProteinCutoffControl_ValueChanged(object sender, int newValue)
        {
            if (_currentData != null)
            {
                ProteinHistogram.UpdateChart(_currentData, newValue, _rawFileToPlateId, _plateIdToName);
            }

            // Bubble up to MainWindow
            ProteinCutoffChanged?.Invoke(this, newValue);
        }

        /// <summary>
        /// Reloads the transcriptomic reference database (call after importing new omic data)
        /// </summary>
        public async System.Threading.Tasks.Task ReloadTranscriptomicReferenceAsync()
        {
            var mainWindow = Window.GetWindow(this) as MainWindow;
            if (mainWindow == null || !mainWindow.HasOpenProject)
                return;

            var databasePath = mainWindow.ProjectReferenceDatabasePath;

            if (File.Exists(databasePath))
            {
                try
                {
                    StatusText.Text = "Reloading transcriptomic reference database...";
                    await _cellTypeClassificationManager.LoadDatabaseAsync(databasePath);

                    var db = _cellTypeClassificationManager.Database;
                    StatusText.Text = $"Reference reloaded: {db.TotalGenes:N0} genes, {db.TotalCells:N0} cells, {db.TotalCellTypes} cell types";
                }
                catch (Exception ex)
                {
                    StatusText.Text = $"Error reloading reference: {ex.Message}";
                }
            }
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
            // Check if central GO database is available
            var goStatusService = new BioTessera.GO.GoStatusService();

            if (!goStatusService.DatabaseExists || !goStatusService.HasOntology)
            {
                StatusText.Text = "GO database not configured. Use Tools → GO Database to set up.";
                _goEnrichmentResults = null;
                return;
            }

            // Check if we have annotations for human (default)
            var humanSpecies = goStatusService.InstalledSpecies?.FirstOrDefault(s => s.TaxonId == 9606);
            if (humanSpecies == null)
            {
                StatusText.Text = "No GO annotations for Human. Use Tools → GO Database to download.";
                _goEnrichmentResults = null;
                return;
            }

            try
            {
                StatusText.Text = "Loading GO enrichment database...";

                // Load synchronously (runs on background via Task.Run to keep UI responsive)
                await System.Threading.Tasks.Task.Run(() => _goEnrichmentManager.LoadDatabase());

                if (!_goEnrichmentManager.IsLoaded)
                {
                    StatusText.Text = "Could not load GO database.";
                    _goEnrichmentResults = null;
                    return;
                }

                StatusText.Text = $"GO database loaded: {_goEnrichmentManager.GeneCount:N0} genes, {_goEnrichmentManager.TermCount:N0} GO terms";

                StatusText.Text = "Running GO enrichment analysis...";

                // Use settings for GO enrichment parameters
                var pValueCutoff = Settings.Default.GOPValueCutoff;
                var minOverlap = Settings.Default.GOMinimumOverlap;

                _goEnrichmentResults = await System.Threading.Tasks.Task.Run(() =>
                    _goEnrichmentManager.EnrichAllRuns(
                        _currentData,
                        pValueCutoff,
                        minOverlap));

                int enrichedCount = _goEnrichmentResults.Count(kvp => !string.IsNullOrEmpty(kvp.Value.TopGoTermId));
                StatusText.Text = $"GO enrichment: {enrichedCount}/{_currentData.TotalRawFiles} runs with significant terms";
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Could not run GO enrichment analysis: {ex.Message}\n\nContinuing without GO enrichment.",
                    "Warning", MessageBoxButton.OK, MessageBoxImage.Warning);
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


        public async Task LoadDataFromProject(List<string> parquetFilePaths, string databasePath)
        {
            _projectDatabasePath = databasePath; // Store it

            if (parquetFilePaths == null || parquetFilePaths.Count == 0)
            {
                // No data has been imported yet
                StatusPanel.Visibility = Visibility.Collapsed;
                TotalRunsText.Text = "0";
                TotalProteinsText.Text = "0";
                TotalPeptidesText.Text = "0";
                TotalPlatesText.Text = "0";
                StatusText.Text = "Project open. Please import a Parquet file to see data.";

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

            // Store first file path for reload functionality
            _currentFilePath = parquetFilePaths[0];
            _currentFileDirectory = Path.GetDirectoryName(parquetFilePaths[0]);

            // Store all paths for potential future use
            _allParquetFilePaths = parquetFilePaths;

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
                    mainWindow.LoadingOverlay.SetMessage("Loading Parquet Files...");
                    mainWindow.LoadingOverlay.SetProgress("Reading data structure...");
                    mainWindow.LoadingOverlay.Show();
                }

                StatusText.Text = "Loading data...";

                var mapping = new ColumnMapping
                {
                    RawFileColumn = "Run",
                    ProteinGroupColumn = "Protein.Group",
                    PeptideColumn = "Modified.Sequence",
                    TotalIonCurrentColumn = "Ms1.Area",
                    TargetProteinIdentifiers = new List<string>()
                };

                if (mainWindow != null)
                {
                    mainWindow.LoadingOverlay.SetProgress("Parsing proteomics data...");
                }

                // Load from multiple files if available, otherwise single file
                if (_allParquetFilePaths != null && _allParquetFilePaths.Count > 1)
                {
                    if (mainWindow != null)
                    {
                        mainWindow.LoadingOverlay.SetProgress($"Loading {_allParquetFilePaths.Count} parquet files...");
                    }
                    _currentData = await _dataService.LoadMultipleParquetFilesAsync(_allParquetFilePaths, mapping);
                }
                else
                {
                    _currentData = await _dataService.LoadParquetFileAsync(_currentFilePath, mapping);
                }

                // Populate biological conditions from database if available
                if (!string.IsNullOrEmpty(_projectDatabasePath))
                {
                    var dataServiceWithDb = new ParquetDataService(_projectDatabasePath);
                    await dataServiceWithDb.PopulateBiologicalConditionsAsync(_currentData);
                }

                TotalRunsText.Text = _currentData.TotalRawFiles.ToString();
                TotalProteinsText.Text = _currentData.TotalProteinGroups.ToString();
                TotalPeptidesText.Text = _currentData.TotalPeptides.ToString();

                // Set plate count
                if (!string.IsNullOrEmpty(_projectDatabasePath))
                {
                    var plateService = new PlateService(_projectDatabasePath);
                    var plates = await plateService.GetPlatesAsync();
                    TotalPlatesText.Text = plates.Count.ToString();
                }
                else
                {
                    TotalPlatesText.Text = _allParquetFilePaths?.Count.ToString() ?? "1";
                }

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

                StatusText.Text = $"Loaded successfully: {_currentData.TotalRawFiles} runs";

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
        }

        public async Task<Dictionary<string, CellTypePredictionResult>> GetCellTypePredictionsAsync(
            ProteomicsData proteomicsData,
            string projectDatabasePath,
            int importId,
            IProgressReporter progressReporter,
            Dictionary<string, HashSet<string>> keyMarkers = null,
            bool forceRecompute = false,
            HashSet<string> excludedCellTypes = null,
            Dictionary<string, double> priorWeights = null)
        {
            var predictions = await _cellTypeClassificationManager.GetOrComputePredictionsAsync(
                proteomicsData,
                projectDatabasePath,
                importId,
                progressReporter,
                keyMarkers,
                forceRecompute,
                excludedCellTypes,
                priorWeights);

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

        public void UpdateChart(ProteomicsData data, Dictionary<string, int> rawFileToPlateId = null, Dictionary<int, string> plateIdToName = null)
        {
            _currentData = data;
            _rawFileToPlateId = rawFileToPlateId;
            _plateIdToName = plateIdToName;
            ProteinHistogram.UpdateChart(data, ProteinCutoffControl.Value, rawFileToPlateId, plateIdToName);
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

        public async Task ExportClassificationDiagnosticsAsync(
            string outputPath,
            Dictionary<string, CellTypePredictionResult> predictions,
            ProteomicsData proteomicsData,
            IProgressReporter progress = null)
        {
            await _cellTypeClassificationManager.ExportClassificationDiagnosticsAsync(
                outputPath, predictions, proteomicsData, progress);
        }

        public async Task ExportGenePresenceByCellTypeAsync(
            string outputPath,
            Dictionary<string, CellTypePredictionResult> predictions,
            ProteomicsData proteomicsData,
            IProgressReporter progress = null)
        {
            await _cellTypeClassificationManager.ExportGenePresenceByCellTypeAsync(
                outputPath, predictions, proteomicsData, progress);
        }
    }
}