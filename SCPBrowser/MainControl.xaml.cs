using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using ScottPlot;


namespace SCPBrowser
{
    public partial class MainControl : UserControl
    {
        public event EventHandler DataLoaded;

        private readonly ParquetDataService _dataService;
        private readonly TranscriptomicManager _transcriptomicManager;
        private ProteomicsData _currentData;
        private string _currentFilePath;
        private string _currentFileDirectory;
        private Dictionary<string, CellTypePredictionResult> _cellTypePredictions;
        private readonly GoEnrichmentManager _goEnrichmentManager;
        private Dictionary<string, RunGoEnrichmentResult> _goEnrichmentResults;

        public MainControl()
        {
            InitializeComponent();
            _dataService = new ParquetDataService();
            _transcriptomicManager = new TranscriptomicManager();
            _goEnrichmentManager = new GoEnrichmentManager();
        }

        private async System.Threading.Tasks.Task LoadTranscriptomicReferenceAsync()
        {
            if (_transcriptomicManager.IsLoaded)
            {
                StatusText.Text = "Predicting cell types...";
                _cellTypePredictions = _transcriptomicManager.PredictCellTypesForAllRuns(_currentData);

                int predictedCount = _cellTypePredictions.Count(kvp => kvp.Value.TopCellType != null);
                StatusText.Text += $" | Cell type predictions: {predictedCount}/{_currentData.TotalRawFiles}";
                return;
            }

            var appDirectory = AppDomain.CurrentDomain.BaseDirectory;
            var referenceDataPath = Path.Combine(appDirectory, "ReferenceData");
            var databasePath = Path.Combine(referenceDataPath, "reference_data.db");

            if (File.Exists(databasePath))
            {
                try
                {
                    StatusText.Text = "Loading transcriptomic reference database...";
                    await _transcriptomicManager.LoadDatabaseAsync(databasePath);

                    var db = _transcriptomicManager.Database;
                    StatusText.Text = $"Reference loaded: {db.TotalGenes:N0} genes, {db.TotalCells:N0} cells, {db.TotalCellTypes} cell types";

                    StatusText.Text = "Predicting cell types...";
                    _cellTypePredictions = _transcriptomicManager.PredictCellTypesForAllRuns(_currentData);

                    int predictedCount = _cellTypePredictions.Count(kvp => kvp.Value.TopCellType != null);
                    StatusText.Text += $" | Predictions: {predictedCount}/{_currentData.TotalRawFiles}";
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Could not load transcriptomic reference: {ex.Message}\n\nContinuing without cell type predictions.",
                        "Warning", MessageBoxButton.OK, MessageBoxImage.Warning);
                    _cellTypePredictions = null;
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
                _goEnrichmentResults = _goEnrichmentManager.EnrichAllRuns(_currentData);

                int enrichedCount = _goEnrichmentResults.Count(kvp => !string.IsNullOrEmpty(kvp.Value.TopGoTermId));
                StatusText.Text += $" | GO enrichment: {enrichedCount}/{_currentData.TotalRawFiles} runs";
                return;
            }

            var appDirectory = AppDomain.CurrentDomain.BaseDirectory;
            var referenceDataPath = Path.Combine(appDirectory, "ReferenceData");
            var databasePath = Path.Combine(referenceDataPath, "reference_data.db");

            if (File.Exists(databasePath))
            {
                try
                {
                    StatusText.Text = "Loading GO enrichment database...";
                    await _goEnrichmentManager.LoadDatabaseAsync(databasePath);

                    var db = _goEnrichmentManager.AnnotationDatabase;
                    StatusText.Text = $"GO database loaded: {db.TotalProteins:N0} proteins, {db.GoTermToProteins.Count} GO terms";

                    StatusText.Text = "Running GO enrichment analysis...";
                    _goEnrichmentResults = _goEnrichmentManager.EnrichAllRuns(_currentData);

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

                _currentData = await _dataService.LoadParquetFileAsync(_currentFilePath, mapping);

                TotalRunsText.Text = _currentData.TotalRawFiles.ToString();
                TotalProteinsText.Text = _currentData.TotalProteinGroups.ToString();
                TotalPeptidesText.Text = _currentData.TotalPeptides.ToString();

                UpdateChart(_currentData);

                StatusPanel.Visibility = Visibility.Visible;

                // Raise event to hide the logo overlay
                DataLoaded?.Invoke(this, EventArgs.Empty);

                await LoadTranscriptomicReferenceAsync();
                await LoadGoEnrichmentAsync();

                var targetInfo = targetIds.Count > 0
                    ? $" (tracking {targetIds.Count} target protein(s))"
                    : "";
                StatusText.Text = $"Loaded successfully: {_currentData.TotalRawFiles} runs{targetInfo}";
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error reading file: {ex.Message}", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                StatusText.Text = "Error loading file";
            }
            finally
            {
                ReloadButton.IsEnabled = !string.IsNullOrEmpty(_currentFilePath);
            }
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
            return _transcriptomicManager.IsLoaded
                ? _transcriptomicManager.GenerateCellTypeColorMap()
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