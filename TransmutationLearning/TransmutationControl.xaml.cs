using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace TransmutationLearning
{
    public partial class TransmutationControl : UserControl
    {
        private string? _parquetPath;
        private string? _classificationPath;
        private TransmutationDataset? _dataset;

        public TransmutationControl()
        {
            InitializeComponent();
        }

        /// <summary>
        /// Gets the loaded dataset (null if not loaded)
        /// </summary>
        public TransmutationDataset? Dataset => _dataset;

        /// <summary>
        /// Event raised when data is successfully loaded
        /// </summary>
        public event EventHandler? DataLoaded;

        private void BrowseParquet_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFileDialog
            {
                Filter = "Parquet Files (*.parquet)|*.parquet|All Files (*.*)|*.*",
                Title = "Select DIA-NN Parquet File"
            };

            if (dialog.ShowDialog() == true)
            {
                _parquetPath = dialog.FileName;
                ParquetPathTextBox.Text = _parquetPath;
                UpdateLoadButtonState();
            }
        }

        private void BrowseClassification_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFileDialog
            {
                Filter = "Text Files (*.txt)|*.txt|TSV Files (*.tsv)|*.tsv|All Files (*.*)|*.*",
                Title = "Select Classification Metadata File"
            };

            if (dialog.ShowDialog() == true)
            {
                _classificationPath = dialog.FileName;
                ClassificationPathTextBox.Text = _classificationPath;
                UpdateLoadButtonState();
            }
        }

        private void UpdateLoadButtonState()
        {
            LoadDataButton.IsEnabled = !string.IsNullOrEmpty(_parquetPath) && 
                                       !string.IsNullOrEmpty(_classificationPath);
        }

        private async void LoadData_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(_parquetPath) || string.IsNullOrEmpty(_classificationPath))
                return;

            LoadDataButton.IsEnabled = false;
            StatusText.Text = "Loading data...";

            try
            {
                _dataset = await LoadAndJoinDataAsync(_parquetPath, _classificationPath);

                // Update UI with summary
                UpdateSummaryDisplay();

                StatusText.Text = $"✓ Data loaded successfully. {_dataset.TotalMatchedRuns} matched runs ready for processing.";

                // Raise event
                DataLoaded?.Invoke(this, EventArgs.Empty);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error loading data:\n\n{ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                StatusText.Text = "Error loading data. Check files and try again.";
            }
            finally
            {
                LoadDataButton.IsEnabled = true;
            }
        }

        private async Task<TransmutationDataset> LoadAndJoinDataAsync(string parquetPath, string classificationPath)
        {
            var dataset = new TransmutationDataset();

            // Load parquet
            StatusText.Text = "Loading parquet file...";
            var parquetLoader = new ParquetLoader();
            var progress = new Progress<string>(msg => StatusText.Text = msg);
            var parquetData = await parquetLoader.LoadAsync(parquetPath, progress);

            dataset.ProteinMatrix = parquetData.ProteinMatrix;
            dataset.ProteinToGeneMap = parquetData.ProteinToGeneMap;

            // Load classification metadata
            StatusText.Text = "Loading classification metadata...";
            var metadataParser = new ClassificationMetadataParser();
            var (classifications, cellTypes) = await metadataParser.ParseAsync(classificationPath);

            dataset.CellTypes = cellTypes;

            foreach (var classification in classifications)
            {
                dataset.Classifications[classification.RunName] = classification;
            }

            // Join datasets
            StatusText.Text = "Joining datasets...";
            var proteomicRuns = new HashSet<string>(parquetData.UniqueRuns);
            var classificationRuns = new HashSet<string>(dataset.Classifications.Keys);

            // Find matched runs
            foreach (var run in proteomicRuns)
            {
                if (classificationRuns.Contains(run))
                {
                    dataset.MatchedRuns.Add(run);
                }
                else
                {
                    dataset.UnmatchedProteomicRuns.Add(run);
                }
            }

            foreach (var run in classificationRuns)
            {
                if (!proteomicRuns.Contains(run))
                {
                    dataset.UnmatchedClassificationRuns.Add(run);
                }
            }

            return dataset;
        }

        private void UpdateSummaryDisplay()
        {
            if (_dataset == null)
            {
                EmptyStatePanel.Visibility = Visibility.Visible;
                SummaryPanel.Visibility = Visibility.Collapsed;
                return;
            }

            EmptyStatePanel.Visibility = Visibility.Collapsed;
            SummaryPanel.Visibility = Visibility.Visible;

            // Proteomic data
            TotalProteinsText.Text = _dataset.TotalProteins.ToString("N0");
            TotalRunsText.Text = (_dataset.MatchedRuns.Count + _dataset.UnmatchedProteomicRuns.Count).ToString("N0");

            // Classification data
            ClassifiedRunsText.Text = _dataset.Classifications.Count.ToString("N0");
            CellTypesCountText.Text = _dataset.CellTypes.Count.ToString();

            // Matching
            MatchedRunsText.Text = _dataset.MatchedRuns.Count.ToString("N0");
            int unmatched = _dataset.UnmatchedProteomicRuns.Count + _dataset.UnmatchedClassificationRuns.Count;
            UnmatchedRunsText.Text = unmatched.ToString("N0");

            // Cell type distribution
            var cellTypeCounts = _dataset.GetCellTypeCounts();
            var distribution = cellTypeCounts
                .OrderByDescending(kvp => kvp.Value)
                .Select(kvp => new CellTypeCountItem { CellType = kvp.Key, Count = kvp.Value })
                .ToList();
            CellTypeDistribution.ItemsSource = distribution;

            // Delta score statistics
            var deltas = _dataset.MatchedRuns
                .Where(r => _dataset.Classifications.ContainsKey(r))
                .Select(r => _dataset.Classifications[r].DeltaNext)
                .OrderBy(d => d)
                .ToList();

            if (deltas.Count > 0)
            {
                DeltaMinText.Text = deltas.Min().ToString("F4");
                DeltaMaxText.Text = deltas.Max().ToString("F4");
                
                // Median
                int mid = deltas.Count / 2;
                double median = deltas.Count % 2 == 0 
                    ? (deltas[mid - 1] + deltas[mid]) / 2.0 
                    : deltas[mid];
                DeltaMedianText.Text = median.ToString("F4");
            }
        }
    }

    /// <summary>
    /// Helper class for cell type distribution display
    /// </summary>
    public class CellTypeCountItem
    {
        public string CellType { get; set; } = string.Empty;
        public int Count { get; set; }
    }
}
