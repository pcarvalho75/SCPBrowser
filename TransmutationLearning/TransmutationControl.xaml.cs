using Microsoft.Win32;
using Parquet;
using SCPBrowser.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using TransmutationLearning.Services;

namespace TransmutationLearning
{
    public partial class TransmutationControl : UserControl
    {
        public TransmutationDataset Dataset { get; private set; }
        public FilteredDataset FilteredData { get; private set; }
        public DistilledDataset DistilledData { get; private set; }

        private double _otsuThreshold;
        private bool _isUpdating;
        private HashSet<string> _validCellTypes = new HashSet<string>();

        // Services
        private readonly ColorService _colorService = new ColorService();
        private readonly SettingsService _settingsService = new SettingsService();
        private readonly ChartRenderer _chartRenderer;

        // Distillation
        private KnnDistillationService _distillationService = new KnnDistillationService();
        private IterativeDistillationService _iterativeDistillationService = new IterativeDistillationService();
        private List<ExpectedCellTypeItem> _expectedDistributionItems = new List<ExpectedCellTypeItem>();
        private Point _dragStartPoint;

        // PPI Service (Biological Gravity)
        private PPIService _ppiService = new PPIService();
        private List<ProteinStatistics> _autoSelectedMarkers = null;

        // Sticky order: remembers user's expected distribution order across data reloads
        private static List<string> _savedExpectedOrder = new List<string>();

        public event EventHandler DataLoaded;

        public TransmutationControl()
        {
            InitializeComponent();
            _chartRenderer = new ChartRenderer(_colorService);
            SizeChanged += (s, e) => RedrawCharts();
        }

        private void MainTabControl_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            // Only respond to TabControl selection changes (not nested controls)
            if (e.Source != MainTabControl) return;

            // When navigating to Feature Selection tab (index 2), update the auto-selected markers banner
            if (MainTabControl.SelectedIndex == 2)
            {
                UpdateAutoSelectedMarkersBanner();
            }
        }

        #region File Loading

        private void BrowseParquet_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFileDialog
            {
                Filter = "Parquet files (*.parquet)|*.parquet|All files (*.*)|*.*",
                Title = "Select DIA-NN Parquet File"
            };

            if (dialog.ShowDialog() == true)
            {
                ParquetPathTextBox.Text = dialog.FileName;
                TryAutoLoad();
            }
        }

        private void BrowseMetadata_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFileDialog
            {
                Filter = "Text files (*.txt;*.tsv)|*.txt;*.tsv|All files (*.*)|*.*",
                Title = "Select Classification Metadata File"
            };

            if (dialog.ShowDialog() == true)
            {
                MetadataPathTextBox.Text = dialog.FileName;
                TryAutoLoad();
            }
        }

        private void TryAutoLoad()
        {
            if (!string.IsNullOrEmpty(ParquetPathTextBox.Text) && !string.IsNullOrEmpty(MetadataPathTextBox.Text))
            {
                // Both files selected - auto-load
                LoadData();
            }
        }

        private async void LoadData()
        {
            try
            {
                LoadingOverlay.Visibility = Visibility.Visible;

                // Capture UI values before background thread
                string parquetPath = ParquetPathTextBox.Text;
                string metadataPath = MetadataPathTextBox.Text;
                await System.Threading.Tasks.Task.Run(async () =>
                {
                    // Use SCPBrowser's ParquetDataService
                    var parquetService = new ParquetDataService();
                    var mapping = new ColumnMapping
                    {
                        RawFileColumn = "Run",
                        ProteinGroupColumn = "Protein.Group",
                        PeptideColumn = "Precursor.Id",
                        TotalIonCurrentColumn = "Precursor.Quantity"
                    };
                    var proteomicsData = await parquetService.LoadParquetFileAsync(parquetPath, mapping);

                    // Load classification metadata
                    var classifications = ClassificationMetadataParser.Parse(metadataPath);
                    var cellTypes = ClassificationMetadataParser.GetCellTypes(classifications);

                    // Create dataset - ProteinQuantMatrix is Dictionary<protein, Dictionary<run, double>>
                    Dataset = new TransmutationDataset
                    {
                        ProteinMatrix = proteomicsData.ProteinQuantMatrix,
                        AllProteins = proteomicsData.ProteinQuantMatrix.Keys.ToList(),
                        AllRuns = proteomicsData.RawFileNames,
                        Classifications = classifications,
                        CellTypes = cellTypes
                    };
                });

                // Initialize all cell types as valid by default
                _validCellTypes = new HashSet<string>(Dataset.CellTypes);

                // Assign fixed colors to each cell type
                _colorService.AssignColors(Dataset.CellTypes);

                // Build the valid cell types checkbox list
                BuildValidCellTypesCheckboxList();

                // Calculate Otsu threshold on valid labels only
                RecalculateOtsuOnValidLabels();

                // Update UI
                UpdateSummaryStats();

                // Set slider range based on data
                var deltaValues = Dataset.GetDeltaValues();
                if (deltaValues.Count > 0)
                {
                    ThresholdSlider.Minimum = 0;
                    ThresholdSlider.Maximum = Math.Min(deltaValues.Max(), 0.5);
                    ThresholdSlider.Value = Math.Min(_otsuThreshold, ThresholdSlider.Maximum);
                }

                OtsuSuggestionText.Text = $"(Otsu suggests: {_otsuThreshold:F4})";

                // Show main content
                MainTabControl.Visibility = Visibility.Visible;

                // Apply initial filtering
                ApplyThresholdFilter(ThresholdSlider.Value);

                // Draw charts after layout pass completes
                Dispatcher.BeginInvoke(new Action(() => RedrawCharts()), System.Windows.Threading.DispatcherPriority.Loaded);

                DataLoaded?.Invoke(this, EventArgs.Empty);
                StatusText.Text = $"Loaded {Dataset.TotalMatchedRuns:N0} matched runs. " +
                                  $"{Dataset.UnmatchedClassifications} classifications had no proteomic data.";
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error loading data: {ex.Message}", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                LoadingOverlay.Visibility = Visibility.Collapsed;
            }
        }

        private void UpdateSummaryStats()
        {
            if (Dataset == null) return;

            TotalRunsText.Text = Dataset.TotalMatchedRuns.ToString("N0");
            TotalProteinsText.Text = Dataset.TotalProteins.ToString("N0");
            CellTypesCountText.Text = Dataset.CellTypes.Count.ToString();
            DeltaRangeText.Text = $"{Dataset.MinDelta:F3} - {Dataset.MaxDelta:F3}";
        }

        #endregion

        #region Valid Cell Types Management

        private void BuildValidCellTypesCheckboxList()
        {
            ValidCellTypesPanel.Children.Clear();

            foreach (var cellType in Dataset.CellTypes.OrderBy(ct => ct))
            {
                var checkbox = new CheckBox
                {
                    Content = cellType,
                    IsChecked = true,
                    Tag = cellType,
                    Margin = new Thickness(0, 2, 0, 2)
                };
                checkbox.Checked += ValidCellTypeCheckbox_Changed;
                checkbox.Unchecked += ValidCellTypeCheckbox_Changed;
                ValidCellTypesPanel.Children.Add(checkbox);
            }
        }

        private void ValidCellTypeCheckbox_Changed(object sender, RoutedEventArgs e)
        {
            if (_isUpdating || Dataset == null) return;

            var checkbox = sender as CheckBox;
            var cellType = checkbox?.Tag as string;
            if (cellType == null) return;

            if (checkbox.IsChecked == true)
                _validCellTypes.Add(cellType);
            else
                _validCellTypes.Remove(cellType);

            // Recalculate Otsu on valid labels only
            RecalculateOtsuOnValidLabels();
            OtsuSuggestionText.Text = $"(Otsu suggests: {_otsuThreshold:F4})";

            // Re-apply filter with current threshold
            ApplyThresholdFilter(ThresholdSlider.Value);
            RedrawCharts();
        }

        private void SelectAllValid_Click(object sender, RoutedEventArgs e)
        {
            _isUpdating = true;
            foreach (CheckBox cb in ValidCellTypesPanel.Children)
            {
                cb.IsChecked = true;
                _validCellTypes.Add(cb.Tag as string);
            }
            _isUpdating = false;

            RecalculateOtsuOnValidLabels();
            OtsuSuggestionText.Text = $"(Otsu suggests: {_otsuThreshold:F4})";
            ApplyThresholdFilter(ThresholdSlider.Value);
            RedrawCharts();
        }

        private void SelectNoneValid_Click(object sender, RoutedEventArgs e)
        {
            _isUpdating = true;
            foreach (CheckBox cb in ValidCellTypesPanel.Children)
            {
                cb.IsChecked = false;
            }
            _validCellTypes.Clear();
            _isUpdating = false;

            RecalculateOtsuOnValidLabels();
            OtsuSuggestionText.Text = $"(Otsu suggests: {_otsuThreshold:F4})";
            ApplyThresholdFilter(ThresholdSlider.Value);
            RedrawCharts();
        }

        private void RecalculateOtsuOnValidLabels()
        {
            if (Dataset == null || _validCellTypes.Count == 0)
            {
                _otsuThreshold = 0;
                return;
            }

            // Compute Otsu ONLY on valid-labeled cells
            var validDeltas = Dataset.Classifications
                .Where(c => _validCellTypes.Contains(c.Labels))
                .Select(c => c.DeltaNext)
                .ToList();

            _otsuThreshold = validDeltas.Count >= 2
                ? StatisticsHelper.ComputeOtsuThreshold(validDeltas)
                : 0;
        }

        private void ShowMethodologyInfo_Click(object sender, RoutedEventArgs e)
        {
            MethodologyPopup.IsOpen = !MethodologyPopup.IsOpen;
        }

        #endregion

        #region Threshold Filtering

        private void ThresholdSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_isUpdating || Dataset == null) return;

            ThresholdValueText.Text = e.NewValue.ToString("F4");
            ApplyThresholdFilter(e.NewValue);
            RedrawCharts();
        }

        private void ApplyOtsu_Click(object sender, RoutedEventArgs e)
        {
            if (Dataset == null) return;

            _isUpdating = true;
            ThresholdSlider.Value = Math.Min(_otsuThreshold, ThresholdSlider.Maximum);
            _isUpdating = false;

            ApplyThresholdFilter(_otsuThreshold);
            RedrawCharts();
        }

        private void ApplyThresholdFilter(double threshold)
        {
            if (Dataset == null) return;

            FilteredData = new FilteredDataset
            {
                DeltaThreshold = threshold,
                ValidLabelSet = new HashSet<string>(_validCellTypes)
            };

            // Three-bucket filtering:
            // 1. Retained: valid label AND passes threshold
            // 2. FilteredOut: valid label BUT low delta
            // 3. InvalidLabel: invalid label (excluded regardless of delta)
            foreach (var cell in Dataset.Classifications)
            {
                bool isValidLabel = _validCellTypes.Contains(cell.Labels);
                bool passesThreshold = cell.DeltaNext >= threshold;

                if (!isValidLabel)
                {
                    // Invalid label - always excluded
                    FilteredData.InvalidLabelCells.Add(cell);
                }
                else if (passesThreshold)
                {
                    // Valid label + passes threshold = retained
                    FilteredData.RetainedCells.Add(cell);
                }
                else
                {
                    // Valid label + low delta = filtered out
                    FilteredData.FilteredOutCells.Add(cell);
                }
            }

            // Compute per-cell-type statistics (only for valid cell types)
            var validCellTypeGroups = Dataset.Classifications
                .Where(c => _validCellTypes.Contains(c.Labels))
                .GroupBy(c => c.Labels);

            foreach (var group in validCellTypeGroups.OrderBy(g => g.Key))
            {
                var deltas = group.Select(c => c.DeltaNext).ToList();
                var retained = group.Count(c => c.DeltaNext >= threshold);

                FilteredData.CellTypeStats.Add(new CellTypeStatistics
                {
                    CellType = group.Key,
                    TotalCount = group.Count(),
                    RetainedCount = retained,
                    MedianDelta = StatisticsHelper.Median(deltas),
                    MinDelta = deltas.Min(),
                    MaxDelta = deltas.Max()
                });
            }

            // Update main UI
            RetainedCountText.Text = FilteredData.TotalRetained.ToString("N0");
            FilteredCountText.Text = FilteredData.TotalFiltered.ToString("N0");
            RetentionPercentText.Text = $"{FilteredData.RetentionPercent:F1}%";

            // Update invalid label diagnostics
            InvalidCountText.Text = FilteredData.TotalInvalid.ToString("N0");
            InvalidPercentText.Text = $"{FilteredData.InvalidPercent:F1}%";

            // Build invalid label breakdown
            UpdateInvalidLabelDiagnostics();

            CellTypeStatsGrid.ItemsSource = FilteredData.CellTypeStats;

            // Initialize distillation tab with current valid cell types
            InitializeExpectedDistribution();
        }

        private void UpdateInvalidLabelDiagnostics()
        {
            InvalidLabelBreakdownPanel.Children.Clear();

            if (FilteredData == null || FilteredData.TotalInvalid == 0)
            {
                InvalidLabelBreakdownPanel.Children.Add(new TextBlock
                {
                    Text = "No invalid assignments",
                    FontStyle = FontStyles.Italic,
                    Foreground = Brushes.Gray
                });
                return;
            }

            var distribution = FilteredData.InvalidLabelDistribution;
            var medianDeltas = FilteredData.InvalidLabelMedianDelta;

            foreach (var kvp in distribution.OrderByDescending(x => x.Value))
            {
                var medianDelta = medianDeltas.TryGetValue(kvp.Key, out var md) ? md : 0;

                var panel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 1, 0, 1) };
                panel.Children.Add(new TextBlock
                {
                    Text = $"• {kvp.Key}: ",
                    FontWeight = FontWeights.SemiBold
                });
                panel.Children.Add(new TextBlock
                {
                    Text = $"{kvp.Value} cells (median Δ = {medianDelta:F4})"
                });

                InvalidLabelBreakdownPanel.Children.Add(panel);
            }
        }

        #endregion

        #region Chart Drawing

        private void RedrawCharts()
        {
            if (Dataset == null) return;

            DrawHistogram();
            _chartRenderer.DrawPieChart(PieChartCanvas, FilteredData?.CellTypeStats);
        }

        private void DrawHistogram()
        {
            if (Dataset == null) return;

            var deltaValues = Dataset.Classifications
                .Where(c => _validCellTypes.Contains(c.Labels))
                .Select(c => c.DeltaNext)
                .ToList();

            _chartRenderer.DrawHistogram(HistogramCanvas, deltaValues, ThresholdSlider?.Value ?? 0, _validCellTypes);
        }

        #endregion

        #region Feature Selection (Phase 2)

        private FeatureSelectionService _featureService = new FeatureSelectionService();
        private FeatureSelectionResult _featureResult;

        private async void RunFeatureSelection_Click(object sender, RoutedEventArgs e)
        {
            if (FilteredData == null || FilteredData.TotalRetained == 0)
            {
                MessageBox.Show("No retained cells available. Please complete confidence filtering first.",
                    "Info", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            try
            {
                RunFeatureSelectionButton.IsEnabled = false;
                LoadingOverlay.Visibility = Visibility.Visible;
                LoadingText.Text = "Computing protein statistics...";

                // Get criteria from UI
                var criteria = FeatureSelectionService.ParseCriteriaFromUI(
                    (MaxPValueCombo.SelectedItem as ComboBoxItem)?.Content?.ToString(),
                    (MinDetectionCombo.SelectedItem as ComboBoxItem)?.Content?.ToString(),
                    (MinSpecificityCombo.SelectedItem as ComboBoxItem)?.Content?.ToString(),
                    (MinCellFractionCombo.SelectedItem as ComboBoxItem)?.Content?.ToString(),
                    RequireRobustCheckbox.IsChecked == true);

                // Run feature selection on background thread
                HashSet<string> restrictToProteins = null;
                if (FilterFromDistillationCheckbox.IsChecked == true && _autoSelectedMarkers != null)
                {
                    restrictToProteins = new HashSet<string>(_autoSelectedMarkers.Select(m => m.ProteinName));
                }

                await System.Threading.Tasks.Task.Run(() =>
                {
                    _featureResult = _featureService.ComputeProteinStatistics(FilteredData, Dataset, criteria, DistilledData, restrictToProteins);
                });

                // Update UI
                ProteinStatsGrid.ItemsSource = _featureResult.AllProteinStats;
                UpdateFeatureSelectionSummary();

                // Show auto-selected markers banner if available
                UpdateAutoSelectedMarkersBanner();

                // Enable export buttons if we have selections
                ExportPrefButton.IsEnabled = _featureResult.TotalMarkersSelected > 0;
                ViewPanelReportButton.IsEnabled = _featureResult.TotalMarkersSelected > 0;

                string sourceText = (FilterFromDistillationCheckbox.IsChecked == true && _autoSelectedMarkers != null)
                    ? $"Showing {_featureResult.AllProteinStats.Count:N0} proteins (from distillation), "
                    : $"Showing {_featureResult.AllProteinStats.Count:N0} proteins, ";
                ProteinStatsCountText.Text = sourceText + $"{_featureResult.AllProteinStats.Count(p => p.PassesFilter):N0} pass filter";
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error during feature selection: {ex.Message}", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                RunFeatureSelectionButton.IsEnabled = true;
                LoadingOverlay.Visibility = Visibility.Collapsed;
                LoadingText.Text = "Loading...";
            }
        }

        private void UpdateAutoSelectedMarkersBanner()
        {
            // Show banner if we have auto-selected markers (regardless of whether feature selection has run)
            if (_autoSelectedMarkers != null && _autoSelectedMarkers.Count > 0)
            {
                AutoSelectedMarkersBanner.Visibility = Visibility.Visible;
                AutoSelectedMarkersText.Text = $"Auto-selected {_autoSelectedMarkers.Count} markers from Smart Distillation.";

                // Enable the "Distillation markers only" checkbox
                FilterFromDistillationCheckbox.IsEnabled = true;
            }
            else
            {
                AutoSelectedMarkersBanner.Visibility = Visibility.Collapsed;

                // Disable and uncheck the checkbox when no auto-selected markers
                FilterFromDistillationCheckbox.IsEnabled = false;
                FilterFromDistillationCheckbox.IsChecked = false;
            }
        }

        private void FilterFromDistillationCheckbox_Changed(object sender, RoutedEventArgs e)
        {
            // If feature selection has already run, we could re-run it automatically here
            // For now, just let the user click "Run Feature Selection" again
        }

        private void ApplyAutoMarkers_Click(object sender, RoutedEventArgs e)
        {
            if (_autoSelectedMarkers == null || _autoSelectedMarkers.Count == 0)
            {
                MessageBox.Show("No auto-selected markers available.", "Info", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            // If feature selection hasn't been run yet, we need to run it first
            if (_featureResult == null)
            {
                MessageBox.Show("Please click 'Run Feature Selection' first to compute protein statistics, then click 'Apply Auto-Selected' to select the markers.",
                    "Run Feature Selection First", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            // Create a set of auto-selected protein names for fast lookup
            var autoSelectedNames = new HashSet<string>(_autoSelectedMarkers.Select(m => m.ProteinName));

            // Select matching proteins in the feature result
            int matchedCount = 0;
            foreach (var protein in _featureResult.AllProteinStats)
            {
                if (autoSelectedNames.Contains(protein.ProteinName))
                {
                    protein.IsSelected = true;
                    matchedCount++;
                }
                else
                {
                    protein.IsSelected = false;
                }
            }

            // Refresh grid and summary
            ProteinStatsGrid.Items.Refresh();
            UpdateFeatureSelectionSummary();

            // Hide the banner after applying
            AutoSelectedMarkersBanner.Visibility = Visibility.Collapsed;

            MessageBox.Show($"Applied {matchedCount} of {_autoSelectedMarkers.Count} auto-selected markers.\n\nYou can review and adjust the selection in the grid below.",
                "Markers Applied", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void SelectAllPassing_Click(object sender, RoutedEventArgs e)
        {
            if (_featureResult == null) return;

            _featureService.SelectAllPassing(_featureResult);
            ProteinStatsGrid.Items.Refresh();
            UpdateFeatureSelectionSummary();
        }

        private void ClearSelection_Click(object sender, RoutedEventArgs e)
        {
            if (_featureResult == null) return;

            _featureService.ClearSelection(_featureResult);
            ProteinStatsGrid.Items.Refresh();
            UpdateFeatureSelectionSummary();
        }

        private void UpdateFeatureSelectionSummary()
        {
            if (_featureResult == null)
            {
                SelectedMarkersCountText.Text = "0";
                MedianDeltaText.Text = "-";
                CellTypesCoveredText.Text = "-";
                ExportPrefButton.IsEnabled = false;
                ViewPanelReportButton.IsEnabled = false;
                return;
            }

            // Recalculate from current selections
            _featureService.UpdateSelectedMarkers(_featureResult);

            SelectedMarkersCountText.Text = _featureResult.TotalMarkersSelected.ToString();
            MedianDeltaText.Text = _featureResult.TotalMarkersSelected > 0
                ? _featureResult.MedianSpecificityDelta.ToString("F2")
                : "-";
            CellTypesCoveredText.Text = _featureResult.MarkersByCellType.Count.ToString();

            ExportPrefButton.IsEnabled = _featureResult.TotalMarkersSelected > 0;
            ViewPanelReportButton.IsEnabled = _featureResult.TotalMarkersSelected > 0;
        }

        private void ExportPref_Click(object sender, RoutedEventArgs e)
        {
            if (_featureResult == null || _featureResult.TotalMarkersSelected == 0)
            {
                MessageBox.Show("No markers selected. Please select markers first.",
                    "Info", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var dialog = new SaveFileDialog
            {
                Filter = "Proteomics Reference (*.pref)|*.pref|All files (*.*)|*.*",
                Title = "Export Proteomics Reference",
                FileName = $"proteomics_reference_{DateTime.Now:yyyyMMdd}.pref"
            };

            if (dialog.ShowDialog() == true)
            {
                try
                {
                    var exporter = new ProteomicsReferenceExporter();
                    var reference = exporter.BuildReference(
                        _featureResult,
                        FilteredData,
                        Dataset,
                        System.IO.Path.GetFileName(ParquetPathTextBox.Text),
                        System.IO.Path.GetFileName(MetadataPathTextBox.Text),
                        DistilledData);

                    exporter.Export(reference, dialog.FileName);

                    MessageBox.Show($"Exported {_featureResult.TotalMarkersSelected} markers to:\n{dialog.FileName}",
                        "Export Complete", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Error exporting: {ex.Message}", "Error",
                        MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        private void ViewPanelReport_Click(object sender, RoutedEventArgs e)
        {
            if (_featureResult == null || _featureResult.TotalMarkersSelected == 0)
            {
                MessageBox.Show("No markers selected. Please select markers first.",
                    "Info", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            // Update Panel Report tab content
            UpdatePanelReport();

            // Switch to Panel Report tab (now index 3)
            MainTabControl.SelectedIndex = 3;
        }

        #endregion

        #region Panel Report (Phase 3)

        private void UpdatePanelReport()
        {
            if (_featureResult == null) return;

            // Update summary stats
            PanelTotalMarkersText.Text = _featureResult.TotalMarkersSelected.ToString();
            PanelCellTypesText.Text = _featureResult.MarkersByCellType.Count.ToString();
            PanelMedianDeltaText.Text = _featureResult.MedianSpecificityDelta.ToString("F2");
            PanelTrainingCellsText.Text = FilteredData?.TotalRetained.ToString("N0") ?? "-";

            // Build subtitle with optional distillation info
            var subtitle = $"Generated from TransmutationLearning • {_featureResult.TotalMarkersSelected} markers • {_featureResult.MarkersByCellType.Count} cell types";
            if (DistilledData != null && DistilledData.DistillationApplied)
            {
                subtitle += $" • Distilled (sensitivity={DistilledData.Sensitivity:F2}, {DistilledData.ReclassifiedCount} reclassified)";
            }
            PanelReportSubtitle.Text = subtitle;

            // Build top markers list
            BuildTopMarkersList();

            // Draw visualizations
            DrawDotPlot();
            DrawMarkersPerTypeChart();
        }

        private void BuildTopMarkersList()
        {
            TopMarkersPanel.Children.Clear();

            if (_featureResult == null) return;

            foreach (var kvp in _featureResult.MarkersByCellType.OrderBy(x => x.Key))
            {
                var cellType = kvp.Key;
                var markers = kvp.Value.Take(5).ToList(); // Top 5 per cell type

                // Cell type header
                var header = new TextBlock
                {
                    Text = $"{cellType} ({kvp.Value.Count})",
                    FontWeight = FontWeights.SemiBold,
                    FontSize = 12,
                    Margin = new Thickness(0, 8, 0, 4)
                };
                TopMarkersPanel.Children.Add(header);

                // Top markers
                for (int i = 0; i < markers.Count; i++)
                {
                    var marker = markers[i];
                    var stars = GetStarRating(marker.SpecificityDelta);

                    var markerPanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(8, 1, 0, 1) };
                    markerPanel.Children.Add(new TextBlock
                    {
                        Text = $"{i + 1}. {marker.ProteinName}",
                        FontSize = 11,
                        Width = 140
                    });
                    markerPanel.Children.Add(new TextBlock
                    {
                        Text = $"Δ={marker.SpecificityDelta:F2} {stars}",
                        FontSize = 10,
                        Foreground = Brushes.Gray
                    });
                    TopMarkersPanel.Children.Add(markerPanel);
                }
            }
        }

        private string GetStarRating(double delta)
        {
            if (delta >= 2.0) return "★★★";
            if (delta >= 1.0) return "★★☆";
            if (delta >= 0.5) return "★☆☆";
            return "☆☆☆";
        }

        private void DrawDotPlot()
        {
            if (_featureResult == null) return;

            var cellTypes = _featureResult.MarkersByCellType.Keys.OrderBy(c => c).ToList();
            _chartRenderer.DrawDotPlot(DotPlotCanvas, _featureResult.SelectedMarkers, cellTypes);
        }

        private void DrawMarkersPerTypeChart()
        {
            if (_featureResult == null) return;

            _chartRenderer.DrawMarkersPerTypeChart(MarkersPerTypeCanvas, _featureResult.MarkersByCellType);
        }

        private void DotPlotCanvas_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (_featureResult != null && e.NewSize.Width > 0 && e.NewSize.Height > 0)
            {
                DrawDotPlot();
            }
        }

        private void MarkersPerTypeCanvas_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (_featureResult != null && e.NewSize.Width > 0 && e.NewSize.Height > 0)
            {
                DrawMarkersPerTypeChart();
            }
        }

        private void ExportPanelPng_Click(object sender, RoutedEventArgs e)
        {
            // TODO: Implement PNG export if needed
            MessageBox.Show("PNG export not yet implemented.", "Info", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void ExportPanelPdf_Click(object sender, RoutedEventArgs e)
        {
            // Not implementing PDF export per user request
            MessageBox.Show("PDF export is not available.", "Info", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private ValidationService _validationService = new ValidationService();

        private async void RunValidation_Click(object sender, RoutedEventArgs e)
        {
            if (_featureResult == null || _featureResult.SelectedMarkers.Count == 0)
            {
                MessageBox.Show("No markers selected. Please select markers first.",
                    "Info", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            if (Dataset?.ProteinMatrix == null || FilteredData?.RetainedCells == null)
            {
                MessageBox.Show("No data available for validation.",
                    "Info", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            try
            {
                RunValidationButton.IsEnabled = false;
                LoadingOverlay.Visibility = Visibility.Visible;
                LoadingText.Text = "Running self-consistency validation...";

                // Build effective labels (distilled if available, otherwise original)
                Dictionary<string, string> effectiveLabels;
                if (DistilledData != null && DistilledData.DistillationApplied)
                {
                    effectiveLabels = DistilledData.DistilledLabels;
                }
                else
                {
                    effectiveLabels = FilteredData.RetainedCells.ToDictionary(c => c.Run, c => c.Labels);
                }

                ValidationResult validationResult = null;
                await System.Threading.Tasks.Task.Run(() =>
                {
                    validationResult = _validationService.RunSelfConsistencyCheck(
                        FilteredData.RetainedCells,
                        Dataset.ProteinMatrix,
                        _featureResult.SelectedMarkers,
                        effectiveLabels);
                });

                // Display results
                DisplayValidationResults(validationResult);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error during validation: {ex.Message}", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                RunValidationButton.IsEnabled = true;
                LoadingOverlay.Visibility = Visibility.Collapsed;
                LoadingText.Text = "Loading...";
            }
        }

        private void DisplayValidationResults(ValidationResult result)
        {
            ValidationResultsPanel.Visibility = Visibility.Visible;

            if (!result.IsValid)
            {
                ValidationAgreementText.Text = "-";
                ValidationStatusText.Text = "Error";
                ValidationStatusText.Foreground = Brushes.Red;
                ValidationWarningText.Text = result.ErrorMessage;
                ValidationWarningText.Visibility = Visibility.Visible;
                ValidationPanel.Background = new SolidColorBrush(Color.FromRgb(254, 242, 242)); // Red tint
                return;
            }

            // Show agreement rate
            ValidationAgreementText.Text = $"{result.AgreementRate:P1} ({result.AgreementCount}/{result.TotalCells})";

            if (result.PassesValidation)
            {
                ValidationStatusText.Text = "✓ PASSED";
                ValidationStatusText.Foreground = new SolidColorBrush(Color.FromRgb(22, 163, 74)); // Green
                ValidationWarningText.Visibility = Visibility.Collapsed;
                ValidationPanel.Background = new SolidColorBrush(Color.FromRgb(240, 253, 244)); // Green tint
            }
            else
            {
                ValidationStatusText.Text = "⚠ WARNING";
                ValidationStatusText.Foreground = new SolidColorBrush(Color.FromRgb(220, 38, 38)); // Red
                ValidationWarningText.Text = result.WarningMessage;
                ValidationWarningText.Visibility = Visibility.Visible;
                ValidationPanel.Background = new SolidColorBrush(Color.FromRgb(254, 252, 232)); // Yellow tint
            }

            // Show per-type accuracy
            PerTypeAccuracyPanel.Children.Clear();
            var perTypeAccuracy = result.GetPerTypeAccuracy();
            foreach (var kvp in perTypeAccuracy.OrderByDescending(x => x.Value))
            {
                var panel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 0) };

                // Cell type name
                panel.Children.Add(new TextBlock
                {
                    Text = kvp.Key,
                    Width = 120,
                    FontSize = 10
                });

                // Accuracy bar
                var barBorder = new Border
                {
                    Width = 80,
                    Height = 10,
                    Background = Brushes.LightGray,
                    CornerRadius = new CornerRadius(2),
                    Margin = new Thickness(5, 0, 5, 0)
                };
                var barFill = new Border
                {
                    Width = 80 * kvp.Value,
                    Height = 10,
                    Background = kvp.Value >= 0.8
                        ? new SolidColorBrush(Color.FromRgb(34, 197, 94))  // Green
                        : kvp.Value >= 0.6
                            ? new SolidColorBrush(Color.FromRgb(250, 204, 21)) // Yellow
                            : new SolidColorBrush(Color.FromRgb(239, 68, 68)), // Red
                    CornerRadius = new CornerRadius(2),
                    HorizontalAlignment = HorizontalAlignment.Left
                };
                barBorder.Child = barFill;
                panel.Children.Add(barBorder);

                // Percentage
                panel.Children.Add(new TextBlock
                {
                    Text = $"{kvp.Value:P0}",
                    FontSize = 10,
                    FontWeight = FontWeights.SemiBold
                });

                PerTypeAccuracyPanel.Children.Add(panel);
            }
        }

        private async void RunCrossValidation_Click(object sender, RoutedEventArgs e)
        {
            if (_featureResult == null || _featureResult.SelectedMarkers.Count == 0)
            {
                MessageBox.Show("No markers selected. Please select markers first.",
                    "Info", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            if (Dataset?.ProteinMatrix == null || FilteredData?.RetainedCells == null)
            {
                MessageBox.Show("No data available for validation.",
                    "Info", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            try
            {
                RunCrossValidationButton.IsEnabled = false;
                LoadingOverlay.Visibility = Visibility.Visible;
                LoadingText.Text = "Running 5-fold cross-validation...";

                // Build effective labels (distilled if available, otherwise original)
                Dictionary<string, string> effectiveLabels;
                if (DistilledData != null && DistilledData.DistillationApplied)
                {
                    effectiveLabels = DistilledData.DistilledLabels;
                }
                else
                {
                    effectiveLabels = FilteredData.RetainedCells
                        .ToDictionary(c => c.Run, c => c.Labels);
                }

                // Exclude "Unassigned" cells from validation
                var cellsForValidation = FilteredData.RetainedCells
                    .Where(c => effectiveLabels.TryGetValue(c.Run, out var label) && label != "Unassigned")
                    .ToList();

                var labelsForValidation = effectiveLabels
                    .Where(kvp => kvp.Value != "Unassigned")
                    .ToDictionary(kvp => kvp.Key, kvp => kvp.Value);

                // Run cross-validation
                var cvResult = await Task.Run(() =>
                    _validationService.RunCrossValidation(
                        cellsForValidation,
                        Dataset.ProteinMatrix,
                        _featureResult.SelectedMarkers,
                        labelsForValidation,
                        k: 5));

                // Display results
                DisplayCrossValidationResults(cvResult);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error during cross-validation: {ex.Message}", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                RunCrossValidationButton.IsEnabled = true;
                LoadingOverlay.Visibility = Visibility.Collapsed;
                LoadingText.Text = "Loading...";
            }
        }

        private void DisplayCrossValidationResults(CrossValidationResult result)
        {
            CVResultsPanel.Visibility = Visibility.Visible;

            if (!result.IsValid)
            {
                CVAccuracyText.Text = "-";
                CVStdText.Text = "-";
                CVStatusText.Text = "Error";
                CVStatusText.Foreground = Brushes.Red;
                CVWarningText.Text = result.ErrorMessage;
                CVWarningText.Visibility = Visibility.Visible;
                return;
            }

            // Show accuracy
            CVAccuracyText.Text = $"{result.MeanAccuracy:P1}";
            CVStdText.Text = $"± {result.StdAccuracy:P1}";

            if (result.PassesValidation)
            {
                CVStatusText.Text = "✓ PASSED";
                CVStatusText.Foreground = new SolidColorBrush(Color.FromRgb(22, 163, 74)); // Green
                CVWarningText.Visibility = Visibility.Collapsed;
            }
            else
            {
                CVStatusText.Text = "⚠ WARNING";
                CVStatusText.Foreground = new SolidColorBrush(Color.FromRgb(220, 38, 38)); // Red
                CVWarningText.Text = result.WarningMessage;
                CVWarningText.Visibility = Visibility.Visible;
            }

            // Show per-fold accuracy
            PerFoldAccuracyPanel.Children.Clear();
            for (int i = 0; i < result.FoldAccuracies.Count; i++)
            {
                var accuracy = result.FoldAccuracies[i];
                var panel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 0) };

                // Fold label
                panel.Children.Add(new TextBlock
                {
                    Text = $"Fold {i + 1}:",
                    Width = 50,
                    FontSize = 10
                });

                // Accuracy bar
                var barBorder = new Border
                {
                    Width = 80,
                    Height = 10,
                    Background = Brushes.LightGray,
                    CornerRadius = new CornerRadius(2),
                    Margin = new Thickness(5, 0, 5, 0)
                };
                var barFill = new Border
                {
                    Width = 80 * accuracy,
                    Height = 10,
                    Background = accuracy >= 0.8
                        ? new SolidColorBrush(Color.FromRgb(34, 197, 94))  // Green
                        : accuracy >= 0.6
                            ? new SolidColorBrush(Color.FromRgb(250, 204, 21)) // Yellow
                            : new SolidColorBrush(Color.FromRgb(239, 68, 68)), // Red
                    CornerRadius = new CornerRadius(2),
                    HorizontalAlignment = HorizontalAlignment.Left
                };
                barBorder.Child = barFill;
                panel.Children.Add(barBorder);

                // Percentage
                panel.Children.Add(new TextBlock
                {
                    Text = $"{accuracy:P0}",
                    FontSize = 10,
                    FontWeight = FontWeights.SemiBold
                });

                PerFoldAccuracyPanel.Children.Add(panel);
            }
        }

        #endregion

        #region Distillation (Phase 2)

        /// <summary>
        /// Helper class for the expected distribution listbox
        /// </summary>
        public class ExpectedCellTypeItem
        {
            public int Rank { get; set; }
            public string CellType { get; set; }
        }

        /// <summary>
        /// Initialize the expected distribution list after filtering is complete
        /// </summary>
        private void InitializeExpectedDistribution()
        {
            if (FilteredData == null) return;

            // Get cell types present in retained data
            var presentCellTypes = FilteredData.RetainedCells
                .Select(c => c.Labels)
                .Distinct()
                .ToList();

            // Try to load saved order from disk first, then fall back to in-memory saved order
            var savedOrder = _settingsService.LoadExpectedOrder(ParquetPathTextBox?.Text);
            if (savedOrder.Count == 0)
                savedOrder = _savedExpectedOrder;

            // Try to restore saved order if we have one and it contains relevant types
            if (savedOrder.Count > 0)
            {
                // Use saved order, putting saved types first (in saved order), then any new types alphabetically
                var orderedTypes = new List<string>();

                // Add types from saved order that are present
                foreach (var savedType in savedOrder)
                {
                    if (presentCellTypes.Contains(savedType))
                        orderedTypes.Add(savedType);
                }

                // Add any new types not in saved order
                foreach (var newType in presentCellTypes.OrderBy(t => t))
                {
                    if (!orderedTypes.Contains(newType))
                        orderedTypes.Add(newType);
                }

                _expectedDistributionItems = orderedTypes
                    .Select((ct, idx) => new ExpectedCellTypeItem { Rank = idx + 1, CellType = ct })
                    .ToList();
            }
            else
            {
                // No saved order - use frequency-based order (most frequent first as default)
                _expectedDistributionItems = FilteredData.RetainedCells
                    .GroupBy(c => c.Labels)
                    .OrderByDescending(g => g.Count())
                    .Select((g, idx) => new ExpectedCellTypeItem
                    {
                        Rank = idx + 1,
                        CellType = g.Key
                    })
                    .ToList();
            }

            ExpectedDistributionListBox.ItemsSource = _expectedDistributionItems;

            // Reset distillation state
            DistilledData = null;
            _autoSelectedMarkers = null;
            ReclassifiedCountText.Text = "0";
            DistillationTotalText.Text = FilteredData.TotalRetained.ToString();
            ReclassifiedPercentText.Text = "(0%)";
            ReclassificationGrid.ItemsSource = null;
            TransitionSummaryPanel.Children.Clear();

            // Hide iteration progress panel
            IterationProgressPanel.Visibility = Visibility.Collapsed;

            // Draw initial pie charts
            DrawDistillationPieCharts();

            // Update protection hint
            UpdateProtectionHint();

            // NOTE: Do NOT save here - only save when user explicitly changes the order
            // (via Move Up/Down, drag-drop, or Reset buttons)
            // Saving here would overwrite the full saved order with a truncated version
            // when some cell types are temporarily filtered out
        }

        /// <summary>
        /// Saves the current expected distribution order to a JSON file in the same directory as the parquet file
        /// </summary>
        private void SaveExpectedOrder()
        {
            _savedExpectedOrder = _expectedDistributionItems.Select(x => x.CellType).ToList();
            _settingsService.SaveExpectedOrder(ParquetPathTextBox?.Text, _savedExpectedOrder);
        }

        private void SensitivitySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (SensitivityValueText == null || SensitivityHintText == null) return;

            SensitivityValueText.Text = e.NewValue.ToString("F2");

            // Update hint text for kNN prior influence
            if (e.NewValue < 0.2)
                SensitivityHintText.Text = "(pure kNN - data only)";
            else if (e.NewValue < 0.5)
                SensitivityHintText.Text = "(kNN + mild prior)";
            else if (e.NewValue < 0.8)
                SensitivityHintText.Text = "(balanced)";
            else
                SensitivityHintText.Text = "(prior dominates)";
        }

        private void DetectionPatternSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (DetectionWeightValueText == null || DetectionWeightHintText == null) return;

            DetectionWeightValueText.Text = e.NewValue.ToString("F2");

            // Update hint text for detection pattern weight ("Dark Matter" metric)
            if (e.NewValue < 0.1)
                DetectionWeightHintText.Text = "(intensity only)";
            else if (e.NewValue < 0.25)
                DetectionWeightHintText.Text = "(mild pattern influence)";
            else if (e.NewValue < 0.4)
                DetectionWeightHintText.Text = "(recommended)";
            else if (e.NewValue < 0.55)
                DetectionWeightHintText.Text = "(strong pattern influence)";
            else
                DetectionWeightHintText.Text = "(pattern dominant)";
        }

        #region PPI (Biological Gravity)

        private async void PPIDownload_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                PPIDownloadButton.IsEnabled = false;
                PPIDownloadProgress.Visibility = Visibility.Visible;
                PPIStatusPanel.Visibility = Visibility.Collapsed;

                // Set data directory (same as parquet file location, or AppData)
                string dataDir = !string.IsNullOrEmpty(ParquetPathTextBox?.Text)
                    ? System.IO.Path.GetDirectoryName(ParquetPathTextBox.Text)
                    : System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TransmutationLearning", "STRING");

                _ppiService.SetDataDirectory(dataDir);

                // Download with progress
                var progress = new Progress<(string status, double percent)>(p =>
                {
                    PPIProgressText.Text = p.status;
                    PPIProgressBar.Value = p.percent;
                });

                await _ppiService.DownloadDataAsync(progress, System.Threading.CancellationToken.None);

                // Load the data
                PPIProgressText.Text = "Loading PPI data...";
                await _ppiService.LoadDataAsync(progress, System.Threading.CancellationToken.None);

                // Update UI
                UpdatePPIStatus();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error downloading STRING data: {ex.Message}", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                PPIStatusText.Text = "Download failed";
                PPIDetailsText.Text = ex.Message;
            }
            finally
            {
                PPIDownloadButton.IsEnabled = true;
                PPIDownloadProgress.Visibility = Visibility.Collapsed;
                PPIStatusPanel.Visibility = Visibility.Visible;
            }
        }

        private void PPIWeightSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (PPIWeightValueText == null || PPIWeightHintText == null) return;

            PPIWeightValueText.Text = e.NewValue.ToString("F2");

            // Update hint text
            if (e.NewValue < 0.1)
                PPIWeightHintText.Text = "(disabled)";
            else if (e.NewValue < 0.3)
                PPIWeightHintText.Text = "(mild biology)";
            else if (e.NewValue < 0.6)
                PPIWeightHintText.Text = "(balanced)";
            else if (e.NewValue < 0.8)
                PPIWeightHintText.Text = "(strong biology)";
            else
                PPIWeightHintText.Text = "(biology dominant)";
        }

        private void UpdatePPIStatus()
        {
            if (_ppiService.IsLoaded)
            {
                PPIStatusText.Text = $"✓ STRING data loaded";
                PPIDetailsText.Text = $"{_ppiService.TotalInteractions:N0} interactions, {_ppiService.TotalProteins:N0} proteins";
                PPIDownloadButton.Content = "Reload";
                PPIWeightPanel.Visibility = Visibility.Visible;

                // Show mapping stats if we have data
                if (Dataset?.ProteinMatrix != null)
                {
                    var (mapped, total) = _ppiService.GetMappingStats(Dataset.ProteinMatrix.Keys);
                    PPIMappingText.Text = $"Mapped {mapped:N0} of {total:N0} proteins ({100.0 * mapped / total:F1}%)";
                }
            }
            else
            {
                PPIStatusText.Text = "STRING data not loaded";
                PPIDetailsText.Text = "Download ~35MB to enable biological context";
                PPIDownloadButton.Content = "Download STRING Data";
                PPIWeightPanel.Visibility = Visibility.Collapsed;
            }
        }

        private double GetPPIWeight()
        {
            if (!_ppiService.IsLoaded || PPIWeightSlider == null)
                return 0.0;
            return PPIWeightSlider.Value;
        }

        private PPIService GetPPIServiceIfEnabled()
        {
            if (!_ppiService.IsLoaded || GetPPIWeight() < 0.01)
                return null;
            return _ppiService;
        }

        /// <summary>
        /// Try to load PPI data if it exists locally (called during initialization)
        /// </summary>
        private async Task TryLoadExistingPPIData()
        {
            try
            {
                string dataDir = !string.IsNullOrEmpty(ParquetPathTextBox?.Text)
                    ? System.IO.Path.GetDirectoryName(ParquetPathTextBox.Text)
                    : System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TransmutationLearning", "STRING");

                _ppiService.SetDataDirectory(dataDir);

                if (_ppiService.DataFilesExist())
                {
                    PPIStatusText.Text = "Loading existing STRING data...";
                    await _ppiService.LoadDataAsync(null, System.Threading.CancellationToken.None);
                    UpdatePPIStatus();
                }
            }
            catch
            {
                // Silently ignore - PPI is optional
            }
        }

        #endregion

        private void MoveExpectedUp_Click(object sender, RoutedEventArgs e)
        {
            var selected = ExpectedDistributionListBox.SelectedItem as ExpectedCellTypeItem;
            if (selected == null) return;

            int idx = _expectedDistributionItems.IndexOf(selected);
            if (idx > 0)
            {
                _expectedDistributionItems.RemoveAt(idx);
                _expectedDistributionItems.Insert(idx - 1, selected);
                UpdateExpectedRanks();
                ExpectedDistributionListBox.ItemsSource = null;
                ExpectedDistributionListBox.ItemsSource = _expectedDistributionItems;
                ExpectedDistributionListBox.SelectedItem = selected;
                UpdateProtectionHint();
                SaveExpectedOrder();
            }
        }

        private void MoveExpectedDown_Click(object sender, RoutedEventArgs e)
        {
            var selected = ExpectedDistributionListBox.SelectedItem as ExpectedCellTypeItem;
            if (selected == null) return;

            int idx = _expectedDistributionItems.IndexOf(selected);
            if (idx < _expectedDistributionItems.Count - 1)
            {
                _expectedDistributionItems.RemoveAt(idx);
                _expectedDistributionItems.Insert(idx + 1, selected);
                UpdateExpectedRanks();
                ExpectedDistributionListBox.ItemsSource = null;
                ExpectedDistributionListBox.ItemsSource = _expectedDistributionItems;
                ExpectedDistributionListBox.SelectedItem = selected;
                UpdateProtectionHint();
                SaveExpectedOrder();
            }
        }

        private void ResetExpectedOrder_Click(object sender, RoutedEventArgs e)
        {
            _expectedDistributionItems = _expectedDistributionItems
                .OrderBy(x => x.CellType)
                .Select((x, idx) => new ExpectedCellTypeItem { Rank = idx + 1, CellType = x.CellType })
                .ToList();

            ExpectedDistributionListBox.ItemsSource = null;
            ExpectedDistributionListBox.ItemsSource = _expectedDistributionItems;
            UpdateProtectionHint();
            SaveExpectedOrder();
        }

        private void UpdateExpectedRanks()
        {
            for (int i = 0; i < _expectedDistributionItems.Count; i++)
            {
                _expectedDistributionItems[i].Rank = i + 1;
            }
        }

        private void ExpectedList_PreviewMouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            _dragStartPoint = e.GetPosition(null);
        }

        private void ExpectedList_Drop(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(typeof(ExpectedCellTypeItem)))
            {
                var droppedData = e.Data.GetData(typeof(ExpectedCellTypeItem)) as ExpectedCellTypeItem;
                var target = GetItemAtPosition(e.GetPosition(ExpectedDistributionListBox));

                if (droppedData != null && target != null && droppedData != target)
                {
                    int oldIdx = _expectedDistributionItems.IndexOf(droppedData);
                    int newIdx = _expectedDistributionItems.IndexOf(target);

                    _expectedDistributionItems.RemoveAt(oldIdx);
                    _expectedDistributionItems.Insert(newIdx, droppedData);
                    UpdateExpectedRanks();

                    ExpectedDistributionListBox.ItemsSource = null;
                    ExpectedDistributionListBox.ItemsSource = _expectedDistributionItems;
                    UpdateProtectionHint();
                    SaveExpectedOrder();
                }
            }
        }

        private ExpectedCellTypeItem GetItemAtPosition(Point position)
        {
            var element = ExpectedDistributionListBox.InputHitTest(position) as FrameworkElement;
            while (element != null && !(element is ListBoxItem))
            {
                element = VisualTreeHelper.GetParent(element) as FrameworkElement;
            }
            return element?.DataContext as ExpectedCellTypeItem;
        }

        private ExpectedDistribution BuildExpectedDistribution()
        {
            return new ExpectedDistribution
            {
                RankedCellTypes = _expectedDistributionItems.Select(x => x.CellType).ToList()
            };
        }

        private void PreviewDistillation_Click(object sender, RoutedEventArgs e)
        {
            if (FilteredData == null || FilteredData.TotalRetained == 0)
            {
                MessageBox.Show("No retained cells available. Please complete confidence filtering first.",
                    "Info", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            if (Dataset?.ProteinMatrix == null || Dataset.ProteinMatrix.Count == 0)
            {
                MessageBox.Show("No protein expression data available.",
                    "Info", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var expectedDist = BuildExpectedDistribution();
            int k = GetKNeighbors();
            double sensitivity = SensitivitySlider.Value;
            double minConfidence = GetMinConfidence();
            int protectionRank = GetProtectionRank();
            double detectionWeight = GetDetectionPatternWeight();
            var ppiService = GetPPIServiceIfEnabled();
            double ppiWeight = GetPPIWeight();

            var (wouldReclassify, newDistribution) = _distillationService.PreviewKnnDistillation(
                FilteredData.RetainedCells,
                Dataset.ProteinMatrix,
                expectedDist,
                k,
                sensitivity,
                minConfidence,
                protectionRank,
                null, // no protein subset
                detectionWeight,
                ppiService,
                ppiWeight);

            ReclassifiedCountText.Text = wouldReclassify.ToString();
            ReclassifiedPercentText.Text = $"({(wouldReclassify * 100.0 / FilteredData.TotalRetained):F1}%)";
        }

        /// <summary>
        /// Gets protection rank from combo box (0 = None, 1-4 = Top N)
        /// </summary>
        private int GetProtectionRank()
        {
            if (ProtectionRankCombo == null) return 2; // Default
            return ProtectionRankCombo.SelectedIndex; // 0 = None, 1 = Top 1, 2 = Top 2, etc.
        }

        /// <summary>
        /// Gets detection pattern weight from slider (0-0.7, "Dark Matter" metric)
        /// </summary>
        private double GetDetectionPatternWeight()
        {
            if (DetectionPatternSlider == null) return 0.3; // Default
            return DetectionPatternSlider.Value;
        }

        /// <summary>
        /// Gets k (number of neighbors) from combo box
        /// </summary>
        private int GetKNeighbors()
        {
            if (KNeighborsCombo == null) return 10; // Default
            var content = (KNeighborsCombo.SelectedItem as ComboBoxItem)?.Content?.ToString();
            return int.TryParse(content, out int k) ? k : 10;
        }

        /// <summary>
        /// Gets minimum confidence threshold from combo box
        /// </summary>
        private double GetMinConfidence()
        {
            if (MinConfidenceCombo == null) return 0.6; // Default
            var content = (MinConfidenceCombo.SelectedItem as ComboBoxItem)?.Content?.ToString();
            if (content != null && content.EndsWith("%"))
            {
                var numStr = content.TrimEnd('%');
                if (double.TryParse(numStr, out double pct))
                    return pct / 100.0;
            }
            return 0.6;
        }

        /// <summary>
        /// Updates the protection hint text based on current settings
        /// </summary>
        private void UpdateProtectionHint()
        {
            if (ProtectionHintText == null || _expectedDistributionItems == null) return;

            int protectionRank = GetProtectionRank();
            if (protectionRank == 0)
            {
                ProtectionHintText.Text = "(no protection)";
                return;
            }

            var protectedTypes = _expectedDistributionItems
                .Where(item => item.Rank <= protectionRank)
                .Select(item => item.CellType)
                .ToList();

            if (protectedTypes.Count == 0)
            {
                ProtectionHintText.Text = "(no types to protect)";
            }
            else if (protectedTypes.Count == 1)
            {
                ProtectionHintText.Text = $"(protects {protectedTypes[0]})";
            }
            else
            {
                ProtectionHintText.Text = $"(protects {string.Join(", ", protectedTypes)})";
            }
        }

        private void ProtectionRankCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            UpdateProtectionHint();
        }

        private async void RunDistillation_Click(object sender, RoutedEventArgs e)
        {
            if (FilteredData == null || FilteredData.TotalRetained == 0)
            {
                MessageBox.Show("No retained cells available. Please complete confidence filtering first.",
                    "Info", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            if (Dataset?.ProteinMatrix == null || Dataset.ProteinMatrix.Count == 0)
            {
                MessageBox.Show("No protein expression data available.",
                    "Info", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            try
            {
                RunDistillationButton.IsEnabled = false;
                RunSmartDistillationButton.IsEnabled = false;
                LoadingOverlay.Visibility = Visibility.Visible;
                LoadingText.Text = "Computing kNN distillation...";

                var expectedDist = BuildExpectedDistribution();
                int k = GetKNeighbors();
                double sensitivity = SensitivitySlider.Value;
                double minConfidence = GetMinConfidence();
                int protectionRank = GetProtectionRank();
                double detectionWeight = GetDetectionPatternWeight();
                var ppiService = GetPPIServiceIfEnabled();
                double ppiWeight = GetPPIWeight();

                await System.Threading.Tasks.Task.Run(() =>
                {
                    DistilledData = _distillationService.RunKnnDistillation(
                        FilteredData.RetainedCells,
                        Dataset.ProteinMatrix,
                        expectedDist,
                        k,
                        sensitivity,
                        minConfidence,
                        protectionRank,
                        null, // no protein subset
                        detectionWeight,
                        ppiService,
                        ppiWeight);
                });

                // Clear auto-selected markers (manual distillation doesn't auto-select)
                _autoSelectedMarkers = null;

                // Update UI
                ReclassifiedCountText.Text = DistilledData.ReclassifiedCount.ToString();
                DistillationTotalText.Text = DistilledData.TotalCells.ToString();
                ReclassifiedPercentText.Text = $"({DistilledData.ReclassificationPercent:F1}%)";

                // Show reclassification details
                ReclassificationGrid.ItemsSource = DistilledData.ReclassificationHistory
                    .Where(r => r.WasReclassified)
                    .OrderBy(r => r.OriginalLabel)
                    .ThenBy(r => r.DistilledLabel)
                    .ToList();

                // Update transition summary
                UpdateTransitionSummary();

                // Redraw pie charts
                DrawDistillationPieCharts();

                // Hide iteration progress panel (if it was shown from a previous smart distillation)
                IterationProgressPanel.Visibility = Visibility.Collapsed;

                StatusText.Text = $"kNN Distillation complete. {DistilledData.ReclassifiedCount} cells reclassified.";
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error during distillation: {ex.Message}", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                RunDistillationButton.IsEnabled = true;
                RunSmartDistillationButton.IsEnabled = true;
                LoadingOverlay.Visibility = Visibility.Collapsed;
                LoadingText.Text = "Loading...";
            }
        }

        private void SkipDistillation_Click(object sender, RoutedEventArgs e)
        {
            // Move to Feature Selection tab without applying distillation
            DistilledData = null;
            _autoSelectedMarkers = null;
            MainTabControl.SelectedIndex = 2; // Feature Selection tab
        }

        private async void RunSmartDistillation_Click(object sender, RoutedEventArgs e)
        {
            if (FilteredData == null || FilteredData.TotalRetained == 0)
            {
                MessageBox.Show("No retained cells available. Please complete confidence filtering first.",
                    "Info", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            if (Dataset?.ProteinMatrix == null || Dataset.ProteinMatrix.Count == 0)
            {
                MessageBox.Show("No protein expression data available.",
                    "Info", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            // RAM usage warning for large datasets
            // The similarity matrix is O(N²): 15k cells = ~1.8GB, 20k cells = ~3.2GB
            int cellCount = FilteredData.TotalRetained;
            if (cellCount > 15000)
            {
                long estimatedBytes = (long)cellCount * cellCount * sizeof(double);
                double estimatedGB = estimatedBytes / (1024.0 * 1024.0 * 1024.0);

                var result = MessageBox.Show(
                    $"Warning: Your dataset has {cellCount:N0} cells.\n\n" +
                    $"The distillation algorithm requires computing a pairwise similarity matrix, " +
                    $"which will use approximately {estimatedGB:F1} GB of RAM.\n\n" +
                    $"This may cause the application to crash or become unresponsive on systems with limited memory.\n\n" +
                    $"Do you want to proceed?",
                    "Large Dataset Warning",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning);

                if (result != MessageBoxResult.Yes)
                    return;
            }

            try
            {
                // Disable buttons during processing
                RunSmartDistillationButton.IsEnabled = false;
                RunDistillationButton.IsEnabled = false;
                PreviewDistillationButton.IsEnabled = false;
                SkipDistillationButton.IsEnabled = false;

                // Show progress panel
                IterationProgressPanel.Visibility = Visibility.Visible;
                IterationHistoryList.ItemsSource = null;

                // Build settings from UI
                var settings = new IterativeDistillationSettings
                {
                    K = GetKNeighbors(),
                    MinConfidence = GetMinConfidence(),
                    PriorInfluence = SensitivitySlider.Value,
                    ProtectionRank = GetProtectionRank(),
                    DetectionPatternWeight = GetDetectionPatternWeight(),
                    PPIService = GetPPIServiceIfEnabled(),
                    PPIWeight = GetPPIWeight()
                };

                var expectedDist = BuildExpectedDistribution();

                // Create progress reporter
                var progress = new Progress<IterationProgress>(UpdateIterationProgress);

                // Run iterative distillation on background thread
                IterativeDistillationResult result = null;
                await System.Threading.Tasks.Task.Run(() =>
                {
                    result = _iterativeDistillationService.RunIterativeDistillation(
                        FilteredData.RetainedCells,
                        Dataset.ProteinMatrix,
                        expectedDist,
                        settings,
                        progress);
                });

                // Store results
                DistilledData = result.FinalLabels;
                _autoSelectedMarkers = result.AutoSelectedMarkers;

                // Update UI with final results
                DisplayIterativeResults(result);

                StatusText.Text = result.Converged
                    ? $"Smart Distillation converged after {result.IterationsRun} iterations. {result.FinalMarkerCount} markers selected."
                    : $"Smart Distillation completed {result.IterationsRun} iterations (max reached). {result.FinalMarkerCount} markers selected.";
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error during smart distillation: {ex.Message}", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                // Re-enable buttons
                RunSmartDistillationButton.IsEnabled = true;
                RunDistillationButton.IsEnabled = true;
                PreviewDistillationButton.IsEnabled = true;
                SkipDistillationButton.IsEnabled = true;
            }
        }

        private void UpdateIterationProgress(IterationProgress progress)
        {
            Dispatcher.Invoke(() =>
            {
                IterationStatusText.Text = progress.Status;
                IterationProgressBar.Value = progress.Percent;
            });
        }

        private void DisplayIterativeResults(IterativeDistillationResult result)
        {
            // Update iteration history list
            IterationHistoryList.ItemsSource = result.History;

            // Update stats
            ReclassifiedCountText.Text = result.TotalReclassified.ToString();
            DistillationTotalText.Text = result.TotalCells.ToString();
            ReclassifiedPercentText.Text = result.TotalCells > 0
                ? $"({result.TotalReclassified * 100.0 / result.TotalCells:F1}%)"
                : "(0%)";

            // Update reclassification details grid
            if (result.FinalLabels != null)
            {
                ReclassificationGrid.ItemsSource = result.FinalLabels.ReclassificationHistory
                    .Where(r => r.WasReclassified)
                    .OrderBy(r => r.OriginalLabel)
                    .ThenBy(r => r.DistilledLabel)
                    .ToList();

                // Update transition summary
                UpdateTransitionSummary();
            }

            // Redraw pie charts
            DrawDistillationPieCharts();

            // Update progress panel to show completion
            IterationStatusText.Text = result.Converged
                ? $"✓ Converged after {result.IterationsRun} iterations with {result.FinalMarkerCount} markers"
                : $"Completed {result.IterationsRun} iterations with {result.FinalMarkerCount} markers";
        }

        private void UpdateTransitionSummary()
        {
            TransitionSummaryPanel.Children.Clear();

            if (DistilledData == null) return;

            var transitions = DistilledData.GetTransitionCounts();
            if (transitions.Count == 0)
            {
                TransitionSummaryPanel.Children.Add(new TextBlock
                {
                    Text = "No reclassifications made",
                    FontStyle = FontStyles.Italic,
                    Foreground = Brushes.Gray,
                    FontSize = 10
                });
                return;
            }

            foreach (var kvp in transitions.OrderByDescending(x => x.Value))
            {
                var panel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 1, 0, 1) };
                panel.Children.Add(new TextBlock
                {
                    Text = $"{kvp.Key.From} → {kvp.Key.To}: ",
                    FontSize = 10
                });
                panel.Children.Add(new TextBlock
                {
                    Text = kvp.Value.ToString(),
                    FontWeight = FontWeights.SemiBold,
                    FontSize = 10,
                    Foreground = new SolidColorBrush(Color.FromRgb(217, 119, 6))
                });
                TransitionSummaryPanel.Children.Add(panel);
            }
        }

        private void DrawDistillationPieCharts()
        {
            _chartRenderer.DrawDistributionPieChart(OriginalDistPieCanvas, GetOriginalDistribution());
            _chartRenderer.DrawDistributionPieChart(DistilledDistPieCanvas, GetDistilledDistribution());
        }

        private Dictionary<string, int> GetOriginalDistribution()
        {
            if (FilteredData == null) return new Dictionary<string, int>();
            return FilteredData.RetainedCells
                .GroupBy(c => c.Labels)
                .ToDictionary(g => g.Key, g => g.Count());
        }

        private Dictionary<string, int> GetDistilledDistribution()
        {
            if (DistilledData != null && DistilledData.DistillationApplied)
            {
                return DistilledData.GetDistilledDistribution();
            }
            return GetOriginalDistribution();
        }

        private void DistillationPieCanvas_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (FilteredData != null && e.NewSize.Width > 0 && e.NewSize.Height > 0)
            {
                DrawDistillationPieCharts();
            }
        }

        #endregion
    }
}
