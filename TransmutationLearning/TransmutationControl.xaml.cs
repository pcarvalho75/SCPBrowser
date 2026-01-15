using Microsoft.Win32;
using Parquet;
using SCPBrowser.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace TransmutationLearning
{
    public partial class TransmutationControl : UserControl
    {
        public TransmutationDataset Dataset { get; private set; }
        public FilteredDataset FilteredData { get; private set; }
        
        private double _otsuThreshold;
        private bool _isUpdating;
        private Dictionary<string, Color> _cellTypeColors = new Dictionary<string, Color>();
        private HashSet<string> _validCellTypes = new HashSet<string>();
        
        // Soft pastel colors for pie chart and histogram
        private static readonly Color[] PastelColors = new[]
        {
            Color.FromRgb(255, 179, 186), // soft pink
            Color.FromRgb(255, 223, 186), // soft peach
            Color.FromRgb(255, 255, 186), // soft yellow
            Color.FromRgb(186, 255, 201), // soft mint
            Color.FromRgb(186, 225, 255), // soft sky
            Color.FromRgb(219, 186, 255), // soft lavender
            Color.FromRgb(255, 186, 255), // soft magenta
            Color.FromRgb(186, 255, 255), // soft cyan
            Color.FromRgb(255, 209, 186), // soft coral
            Color.FromRgb(209, 255, 186), // soft lime
            Color.FromRgb(186, 209, 255), // soft periwinkle
            Color.FromRgb(255, 186, 209), // soft rose
        };

        public event EventHandler DataLoaded;
        
        public TransmutationControl()
        {
            InitializeComponent();
            SizeChanged += (s, e) => RedrawCharts();
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
                UpdateLoadButtonState();
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
                UpdateLoadButtonState();
            }
        }

        private void UpdateLoadButtonState()
        {
            LoadButton.IsEnabled = !string.IsNullOrEmpty(ParquetPathTextBox.Text) && 
                                   !string.IsNullOrEmpty(MetadataPathTextBox.Text);
        }

        private async void LoadData_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                LoadingOverlay.Visibility = Visibility.Visible;
                LoadButton.IsEnabled = false;

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
                
                // Assign fixed colors to each cell type (alphabetically sorted for consistency)
                _cellTypeColors.Clear();
                var sortedCellTypes = Dataset.CellTypes.OrderBy(ct => ct).ToList();
                for (int i = 0; i < sortedCellTypes.Count; i++)
                {
                    _cellTypeColors[sortedCellTypes[i]] = PastelColors[i % PastelColors.Length];
                }
                
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
                MainContentGrid.Visibility = Visibility.Visible;
                
                // Apply initial filtering
                ApplyThresholdFilter(ThresholdSlider.Value);
                
                // Draw charts
                RedrawCharts();
                
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
                LoadButton.IsEnabled = true;
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
            DrawPieChart();
        }

        private void DrawHistogram()
        {
            HistogramCanvas.Children.Clear();
            
            if (Dataset == null || HistogramCanvas.ActualWidth <= 0 || HistogramCanvas.ActualHeight <= 0)
                return;
            
            // Draw histogram only for valid-labeled cells
            var deltaValues = Dataset.Classifications
                .Where(c => _validCellTypes.Contains(c.Labels))
                .Select(c => c.DeltaNext)
                .ToList();
                
            if (deltaValues.Count == 0) return;
            
            var (binEdges, counts) = StatisticsHelper.ComputeHistogram(deltaValues, 40);
            if (counts.Length == 0) return;
            
            double width = HistogramCanvas.ActualWidth;
            double height = HistogramCanvas.ActualHeight;
            double barWidth = width / counts.Length;
            int maxCount = counts.Max();
            
            double threshold = ThresholdSlider?.Value ?? 0;
            double minDelta = binEdges[0];
            double maxDelta = binEdges[binEdges.Length - 1];
            
            // Draw bars
            for (int i = 0; i < counts.Length; i++)
            {
                double barHeight = maxCount > 0 ? (counts[i] * (height - 20) / maxCount) : 0;
                double binCenter = (binEdges[i] + binEdges[i + 1]) / 2;
                
                // Color based on threshold
                bool aboveThreshold = binCenter >= threshold;
                var brush = aboveThreshold 
                    ? new SolidColorBrush(Color.FromRgb(74, 144, 226)) 
                    : new SolidColorBrush(Color.FromRgb(200, 200, 200));
                
                var rect = new Rectangle
                {
                    Width = Math.Max(barWidth - 1, 1),
                    Height = barHeight,
                    Fill = brush
                };
                
                Canvas.SetLeft(rect, i * barWidth);
                Canvas.SetBottom(rect, 20);
                HistogramCanvas.Children.Add(rect);
            }
            
            // Draw threshold line
            if (threshold >= minDelta && threshold <= maxDelta)
            {
                double thresholdX = ((threshold - minDelta) / (maxDelta - minDelta)) * width;
                
                var line = new Line
                {
                    X1 = thresholdX,
                    Y1 = 0,
                    X2 = thresholdX,
                    Y2 = height - 20,
                    Stroke = new SolidColorBrush(Color.FromRgb(220, 38, 38)),
                    StrokeThickness = 2,
                    StrokeDashArray = new DoubleCollection { 4, 2 }
                };
                HistogramCanvas.Children.Add(line);
                
                // Threshold label
                var label = new TextBlock
                {
                    Text = $"τ={threshold:F3}",
                    FontSize = 9,
                    Foreground = new SolidColorBrush(Color.FromRgb(220, 38, 38))
                };
                Canvas.SetLeft(label, thresholdX + 3);
                Canvas.SetTop(label, 2);
                HistogramCanvas.Children.Add(label);
            }
            
            // Draw x-axis labels
            DrawAxisLabel(minDelta.ToString("F2"), 0, height - 15);
            DrawAxisLabel(maxDelta.ToString("F2"), width - 30, height - 15);
        }

        private void DrawAxisLabel(string text, double x, double y)
        {
            var label = new TextBlock
            {
                Text = text,
                FontSize = 9,
                Foreground = new SolidColorBrush(Color.FromRgb(100, 100, 100))
            };
            Canvas.SetLeft(label, x);
            Canvas.SetTop(label, y);
            HistogramCanvas.Children.Add(label);
        }

        private void DrawPieChart()
        {
            PieChartCanvas.Children.Clear();
            
            if (FilteredData == null || PieChartCanvas.ActualWidth <= 0 || PieChartCanvas.ActualHeight <= 0)
                return;
            
            var stats = FilteredData.CellTypeStats.Where(s => s.RetainedCount > 0).ToList();
            if (stats.Count == 0) return;
            
            double width = PieChartCanvas.ActualWidth;
            double height = PieChartCanvas.ActualHeight;
            double centerX = width * 0.35;
            double centerY = height / 2;
            double radius = Math.Min(centerX, centerY) - 10;
            
            int total = stats.Sum(s => s.RetainedCount);
            double startAngle = -90; // Start from top
            
            for (int i = 0; i < stats.Count; i++)
            {
                var stat = stats[i];
                double sweepAngle = (stat.RetainedCount * 360.0) / total;
                
                if (sweepAngle < 0.5) continue; // Skip tiny slices

                var color = _cellTypeColors.TryGetValue(stat.CellType, out var c) ? c : PastelColors[i % PastelColors.Length];
                DrawPieSlice(centerX, centerY, radius, startAngle, sweepAngle, color);

                startAngle += sweepAngle;
            }
            
            // Draw legend
            double legendX = width * 0.55;
            double legendY = 5;
            double legendItemHeight = Math.Min(14, (height - 10) / stats.Count);
            
            for (int i = 0; i < stats.Count && legendY < height - 10; i++)
            {
                var stat = stats[i];
                var color = _cellTypeColors.TryGetValue(stat.CellType, out var c) ? c : PastelColors[i % PastelColors.Length];

                // Color box
                var rect = new Rectangle
                {
                    Width = 10,
                    Height = 10,
                    Fill = new SolidColorBrush(color)
                };
                Canvas.SetLeft(rect, legendX);
                Canvas.SetTop(rect, legendY);
                PieChartCanvas.Children.Add(rect);
                
                // Label (truncate if needed)
                string label = stat.CellType.Length > 12 
                    ? stat.CellType.Substring(0, 10) + "..." 
                    : stat.CellType;
                    
                var text = new TextBlock
                {
                    Text = $"{label} ({stat.RetainedCount})",
                    FontSize = 9,
                    Foreground = Brushes.Black
                };
                Canvas.SetLeft(text, legendX + 14);
                Canvas.SetTop(text, legendY - 1);
                PieChartCanvas.Children.Add(text);
                
                legendY += legendItemHeight;
            }
        }

        private void DrawPieSlice(double cx, double cy, double r, double startAngle, double sweepAngle, Color color)
        {
            if (sweepAngle >= 360)
            {
                // Full circle
                var ellipse = new Ellipse
                {
                    Width = r * 2,
                    Height = r * 2,
                    Fill = new SolidColorBrush(color)
                };
                Canvas.SetLeft(ellipse, cx - r);
                Canvas.SetTop(ellipse, cy - r);
                PieChartCanvas.Children.Add(ellipse);
                return;
            }
            
            double startRad = startAngle * Math.PI / 180;
            double endRad = (startAngle + sweepAngle) * Math.PI / 180;
            
            double x1 = cx + r * Math.Cos(startRad);
            double y1 = cy + r * Math.Sin(startRad);
            double x2 = cx + r * Math.Cos(endRad);
            double y2 = cy + r * Math.Sin(endRad);
            
            bool largeArc = sweepAngle > 180;
            
            var path = new Path
            {
                Fill = new SolidColorBrush(color),
                Stroke = Brushes.White,
                StrokeThickness = 1
            };
            
            var figure = new PathFigure { StartPoint = new Point(cx, cy) };
            figure.Segments.Add(new LineSegment(new Point(x1, y1), false));
            figure.Segments.Add(new ArcSegment(
                new Point(x2, y2),
                new Size(r, r),
                0,
                largeArc,
                SweepDirection.Clockwise,
                false));
            figure.Segments.Add(new LineSegment(new Point(cx, cy), false));
            
            var geometry = new PathGeometry();
            geometry.Figures.Add(figure);
            path.Data = geometry;
            
            PieChartCanvas.Children.Add(path);
        }

        #endregion
    }
}
