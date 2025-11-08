using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace SCPBrowser
{
    public partial class PeptideTicControl : UserControl
    {
        private ProteomicsData _currentData;
        private List<DataPoint> _dataPoints;
        private PlotRenderer _plotRenderer;
        private SelectionManager _selectionManager;
        private bool _isRefreshing = false;
        private bool _isInitialized = false;
        private Dictionary<string, CellTypePredictionResult> _cellTypePredictions;
        private Dictionary<string, Color> _cellTypeColorMap;
        private bool _useCellTypeColoring = false;
        private Dictionary<string, RunGoEnrichmentResult> _goEnrichmentResults;
        private Dictionary<string, Color> _goTermColorMap;
        private bool _isProcessingSelection = false;


        private List<DataPoint> _currentSelectedPoints = new List<DataPoint>();

        private const double HoverTolerance = 12;

        public PeptideTicControl()
        {
            InitializeComponent();

            _dataPoints = new List<DataPoint>();
            _plotRenderer = new PlotRenderer();
            _selectionManager = new SelectionManager();

            PlotCanvas.SizeChanged += PlotCanvas_SizeChanged;

            _isInitialized = true;
        }

        public void UpdateChart(ProteomicsData data)
        {
            _currentData = data;
            RefreshChart();
        }

        public void SetImageBaseDirectory(string directory)
        {
            // Pass the directory to the new details control
            RunDetailPanel.SetImageBaseDirectory(directory);
        }

        public void SetCellTypePredictions(Dictionary<string, CellTypePredictionResult> predictions, Dictionary<string, Color> colorMap)
        {
            _cellTypePredictions = predictions;
            _cellTypeColorMap = colorMap;
            ColorByCellTypeRadio.IsEnabled = predictions != null && predictions.Count > 0;

            if (_currentData != null)
            {
                RefreshChart();
            }
        }

        public void SetGoEnrichmentResults(Dictionary<string, RunGoEnrichmentResult> results, Dictionary<string, Color> colorMap)
        {
            _goEnrichmentResults = results;
            _goTermColorMap = colorMap;
            // Don't enable the radio button - we're not using it for coloring
            // ColorByGoTermRadio.IsEnabled = results != null && results.Count > 0;
        }

        private void ColorMode_Changed(object sender, RoutedEventArgs e)
        {
            if (!_isInitialized || ColorByCellTypeRadio == null)
                return;

            _useCellTypeColoring = ColorByCellTypeRadio.IsChecked == true;

            if (_currentData != null)
            {
                RefreshChart();
            }
        }



        private void RefreshChart()
        {
            if (_isRefreshing) return;
            _isRefreshing = true;

            try
            {
                // Store the current selection state before clearing
                bool hadSelection = _selectionManager.PolygonPointsData.Count > 0;
                var savedDataCoordinates = _selectionManager.PolygonPointsData.ToList();

                PlotCanvas.Children.Clear();
                _dataPoints.Clear();
                TooltipBorder.Visibility = Visibility.Collapsed;

                PlotCanvas.Children.Add(_selectionManager.GetDrawingPolyline());
                PlotCanvas.Children.Add(_selectionManager.GetSelectionPolygon());
                PlotCanvas.Children.Add(_selectionManager.GetStartPointIndicator());

                if (_currentData == null || _currentData.PeptideCountPerFile.Count == 0)
                {
                    SelectedPointsGrid.ItemsSource = null;
                    ClearSelectionButton.IsEnabled = false;
                    RunDetailPanel.ClearDetails();
                    return;
                }

                DrawChart();

                // Restore selection if we had one
                if (hadSelection && savedDataCoordinates.Count > 0)
                {
                    _selectionManager.SetPolygonPointsData(savedDataCoordinates);
                    RedrawSelectionFromDataCoordinates();
                }
            }
            finally
            {
                _isRefreshing = false;
            }
        }

        private void DrawChart()
        {
            double canvasWidth = PlotCanvas.ActualWidth;
            double canvasHeight = PlotCanvas.ActualHeight;

            if (canvasWidth < 100 || canvasHeight < 100)
                return;

            var rawFiles = _currentData.PeptideCountPerFile.Keys.ToList();
            var peptideCounts = rawFiles.Select(rf => _currentData.PeptideCountPerFile[rf]).ToList();
            var ticValues = rawFiles.Select(rf => _currentData.TotalIonCurrentPerFile.ContainsKey(rf) ?
                _currentData.TotalIonCurrentPerFile[rf] : 0).ToList();
            var proteinCounts = rawFiles.Select(rf => _currentData.ProteinCountPerFile.ContainsKey(rf) ?
                _currentData.ProteinCountPerFile[rf] : 0).ToList();
            var trypsinRatios = rawFiles.Select(rf => _currentData.TargetProteinRatioPerFile.ContainsKey(rf) ?
                _currentData.TargetProteinRatioPerFile[rf] : 0).ToList();

            bool useLogLog = LogLogCheckBox.IsChecked == true;

            _plotRenderer.CalculateAxisRanges(peptideCounts, ticValues, useLogLog);
            _plotRenderer.DrawAxesAndGrid(PlotCanvas, canvasWidth, canvasHeight);

            double maxRatio = trypsinRatios.Any() ? trypsinRatios.Max() : 0.05;
            if (maxRatio < 0.01) maxRatio = 0.05;

            if (_useCellTypeColoring && _cellTypePredictions != null)
            {
                _dataPoints = DrawDataPointsWithCellTypes(PlotCanvas, rawFiles, peptideCounts, ticValues,
                    proteinCounts, trypsinRatios, canvasWidth, canvasHeight);
                DrawCellTypeLegend(PlotCanvas, canvasWidth, canvasHeight);

                int predictedCount = _dataPoints.Count(p => !string.IsNullOrEmpty(p.PredictedCellType));
                PlotGroupBoxHeader.Text = $"Peptides vs Total Ion Current per Raw File ({rawFiles.Count} files, {predictedCount} with cell type predictions)";
            }
            else
            {
                _dataPoints = _plotRenderer.DrawDataPoints(PlotCanvas, rawFiles, peptideCounts, ticValues,
                    proteinCounts, trypsinRatios, canvasWidth, canvasHeight);
                _plotRenderer.DrawColorLegend(PlotCanvas, canvasWidth, canvasHeight, maxRatio);
                PlotGroupBoxHeader.Text = $"Peptides vs Total Ion Current per Raw File ({rawFiles.Count} files)";
            }

            if (_selectionManager.PolygonPointsData.Count > 0)
            {
                RedrawSelectionFromDataCoordinates();
            }
            else
            {
                UpdateSelectedPointsGrid(new List<DataPoint>());
            }
        }

        private List<DataPoint> DrawDataPointsWithCellTypes(Canvas canvas, List<string> rawFiles, List<int> peptideCounts,
            List<double> ticValues, List<int> proteinCounts, List<double> trypsinRatios,
            double canvasWidth, double canvasHeight)
        {
            var dataPoints = new List<DataPoint>();
            const double MarkerSize = 8;

            for (int i = 0; i < rawFiles.Count; i++)
            {
                if (ticValues[i] <= 0)
                    continue;

                Point screenPos = _plotRenderer.DataToScreen(peptideCounts[i], ticValues[i], canvasWidth, canvasHeight);

                Color markerColor = Color.FromRgb(200, 200, 200);
                string cellType = null;
                CellTypeScore predictionScore = null;

                if (_cellTypePredictions != null && _cellTypePredictions.ContainsKey(rawFiles[i]))
                {
                    var prediction = _cellTypePredictions[rawFiles[i]];
                    cellType = prediction.TopCellType;
                    predictionScore = prediction.TopScore;

                    if (!string.IsNullOrEmpty(cellType) && _cellTypeColorMap != null && _cellTypeColorMap.ContainsKey(cellType))
                    {
                        markerColor = _cellTypeColorMap[cellType];
                    }
                }

                var ellipse = new Ellipse
                {
                    Width = MarkerSize,
                    Height = MarkerSize,
                    Fill = new SolidColorBrush(markerColor),
                    Stroke = new SolidColorBrush(Color.FromRgb(50, 50, 50)),
                    StrokeThickness = 1
                };

                Canvas.SetLeft(ellipse, screenPos.X - MarkerSize / 2);
                Canvas.SetTop(ellipse, screenPos.Y - MarkerSize / 2);
                canvas.Children.Add(ellipse);

                dataPoints.Add(new DataPoint
                {
                    RunName = rawFiles[i],
                    PeptideCount = peptideCounts[i],
                    TicValue = ticValues[i],
                    ProteinCount = proteinCounts[i],
                    TrypsinRatio = trypsinRatios[i],
                    XScreen = screenPos.X,
                    YScreen = screenPos.Y,
                    Visual = ellipse,
                    BaseColor = markerColor,
                    IsSelected = false,
                    PredictedCellType = cellType,
                    PredictionScore = predictionScore
                });
            }

            return dataPoints;
        }

        private void DrawCellTypeLegend(Canvas canvas, double canvasWidth, double canvasHeight)
        {
            if (_cellTypeColorMap == null || _cellTypeColorMap.Count == 0)
                return;

            double legendX = canvasWidth - 110;
            double legendY = 20;
            double boxSize = 12;
            double spacing = 18;

            var titleText = new TextBlock
            {
                Text = "Cell Type",
                FontSize = 11,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Colors.Black)
            };
            Canvas.SetLeft(titleText, legendX);
            Canvas.SetTop(titleText, legendY);
            canvas.Children.Add(titleText);

            legendY += 20;

            foreach (var cellType in _cellTypeColorMap.OrderBy(kvp => kvp.Key))
            {
                var rect = new System.Windows.Shapes.Rectangle
                {
                    Width = boxSize,
                    Height = boxSize,
                    Fill = new SolidColorBrush(cellType.Value),
                    Stroke = new SolidColorBrush(Colors.Black),
                    StrokeThickness = 1
                };
                Canvas.SetLeft(rect, legendX);
                Canvas.SetTop(rect, legendY);
                canvas.Children.Add(rect);

                var label = new TextBlock
                {
                    Text = cellType.Key,
                    FontSize = 10,
                    Foreground = new SolidColorBrush(Colors.Black)
                };
                Canvas.SetLeft(label, legendX + boxSize + 5);
                Canvas.SetTop(label, legendY - 2);
                canvas.Children.Add(label);

                legendY += spacing;
            }
        }

        private void RedrawSelectionFromDataCoordinates()
        {
            if (_selectionManager.PolygonPointsData.Count < 3)
                return;

            double canvasWidth = PlotCanvas.ActualWidth;
            double canvasHeight = PlotCanvas.ActualHeight;

            _selectionManager.RedrawSelectionFromDataCoordinates(
                dataPoint => _plotRenderer.DataToScreen(dataPoint.X, dataPoint.Y, canvasWidth, canvasHeight));

            UpdateSelectionVisuals();
        }

        private void UpdateSelectionVisuals()
        {
            _isProcessingSelection = true;

            var selectedPoints = new List<DataPoint>();

            foreach (var point in _dataPoints)
            {
                Point testPoint = new Point(point.XScreen, point.YScreen);

                if (_selectionManager.IsPointInSelection(testPoint))
                {
                    point.IsSelected = true;
                    selectedPoints.Add(point);
                    point.Visual.Stroke = new SolidColorBrush(Color.FromRgb(220, 38, 38));
                    point.Visual.StrokeThickness = 2;
                }
                else
                {
                    point.IsSelected = false;
                    point.Visual.Stroke = new SolidColorBrush(Color.FromRgb(50, 50, 50));
                    point.Visual.StrokeThickness = 1;
                }
            }

            UpdateSelectedPointsGrid(selectedPoints);

            _isProcessingSelection = false;
        }

        private void UpdateSelectedPointsGrid(List<DataPoint> selectedPoints)
        {
            _isProcessingSelection = true;

            _currentSelectedPoints = selectedPoints;

            if (selectedPoints.Count > 0)
            {
                var gridData = selectedPoints.Select(p => new SelectedPointData
                {
                    RunName = p.RunName,
                    PeptideCount = p.PeptideCount,
                    TicValue = p.TicValue,
                    ProteinCount = p.ProteinCount,
                    TrypsinRatioPercent = $"{p.TrypsinRatio * 100:F2}%",
                    CellType = p.PredictedCellType ?? "Unknown"
                }).ToList();

                SelectedPointsGrid.ItemsSource = gridData;
                SelectionCountText.Text = $"({selectedPoints.Count} selected)";
                SelectionStatusText.Text = $"{selectedPoints.Count} point(s) selected";
                ClearSelectionButton.IsEnabled = true;

                // Auto-expand the expander when selection is made
                SelectedPointsExpander.IsExpanded = true;

                if (selectedPoints.Count == 1)
                {
                    // Single selection - show details directly
                    RunDetailPanel.ShowRunDetails(selectedPoints[0], _goEnrichmentResults);
                }
                else
                {
                    // Multiple selections - show summary and navigation
                    RunDetailPanel.ShowSelectionSummary(selectedPoints, _goEnrichmentResults);
                }
            }
            else
            {
                SelectedPointsGrid.ItemsSource = null;
                SelectionCountText.Text = "";
                SelectionStatusText.Text = "No points selected";
                ClearSelectionButton.IsEnabled = false;
                RunDetailPanel.ClearDetails();
            }

            _isProcessingSelection = false;
        }

        private void PlotCanvas_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            var mousePos = e.GetPosition(PlotCanvas);

            if (!_plotRenderer.IsPointInPlotArea(mousePos, PlotCanvas.ActualWidth, PlotCanvas.ActualHeight))
                return;

            _selectionManager.StartDrawing(mousePos);

            var startIndicator = _selectionManager.GetStartPointIndicator();
            Canvas.SetLeft(startIndicator, mousePos.X - 5);
            Canvas.SetTop(startIndicator, mousePos.Y - 5);

            PlotCanvas.CaptureMouse();
            TooltipBorder.Visibility = Visibility.Collapsed;
        }

        private void PlotCanvas_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (!_selectionManager.IsDrawing)
                return;

            PlotCanvas.ReleaseMouseCapture();
            _selectionManager.FinishDrawing();

            if (_selectionManager.PolygonPointsScreen.Count < 3)
            {
                ClearSelectionButton_Click(this, new RoutedEventArgs());
                return;
            }

            double canvasWidth = PlotCanvas.ActualWidth;
            double canvasHeight = PlotCanvas.ActualHeight;

            var dataCoordinates = _selectionManager.PolygonPointsScreen
                .Select(screenPoint => _plotRenderer.ScreenToData(screenPoint, canvasWidth, canvasHeight))
                .ToList();

            _selectionManager.StoreDataCoordinates(dataCoordinates);

            UpdateSelectionVisuals();
        }

        private void PlotCanvas_MouseMove(object sender, MouseEventArgs e)
        {
            var mousePos = e.GetPosition(PlotCanvas);

            if (_selectionManager.IsDrawing)
            {
                _selectionManager.UpdateDrawing(mousePos);
                return;
            }

            if (_dataPoints.Count == 0)
                return;

            DataPoint closest = null;
            double closestDistance = double.MaxValue;

            foreach (var point in _dataPoints)
            {
                double dx = point.XScreen - mousePos.X;
                double dy = point.YScreen - mousePos.Y;
                double distance = Math.Sqrt(dx * dx + dy * dy);

                if (distance < closestDistance)
                {
                    closestDistance = distance;
                    closest = point;
                }
            }

            if (closestDistance <= HoverTolerance && closest != null)
            {
                ShowHoverEffectAndTooltip(closest, mousePos);
            }
            else
            {
                ClearHoverEffect();
                TooltipBorder.Visibility = Visibility.Collapsed;
            }
        }

        private void PlotCanvas_MouseRightButtonDown(object sender, MouseButtonEventArgs e)
        {
            var mousePos = e.GetPosition(PlotCanvas);

            foreach (var point in _dataPoints)
            {
                double dx = point.XScreen - mousePos.X;
                double dy = point.YScreen - mousePos.Y;
                double distance = Math.Sqrt(dx * dx + dy * dy);

                if (distance <= HoverTolerance)
                {
                    // Right-click now selects a single point and shows details
                    RunDetailPanel.ShowRunDetails(point, _goEnrichmentResults);
                    e.Handled = true;
                    return;
                }
            }
        }

        private void ShowHoverEffectAndTooltip(DataPoint point, Point mousePos)
        {
            foreach (var p in _dataPoints)
            {
                p.Visual.Fill = new SolidColorBrush(p.BaseColor);
                p.Visual.Width = 8;
                p.Visual.Height = 8;
                Canvas.SetLeft(p.Visual, p.XScreen - 4);
                Canvas.SetTop(p.Visual, p.YScreen - 4);
            }

            point.Visual.Fill = new SolidColorBrush(Color.FromRgb(220, 38, 38));
            point.Visual.Width = 10.4;
            point.Visual.Height = 10.4;
            Canvas.SetLeft(point.Visual, point.XScreen - 5.2);
            Canvas.SetTop(point.Visual, point.YScreen - 5.2);

            string tooltipText = $"{point.RunName}\n" +
                                $"Peptides: {point.PeptideCount:N0}\n" +
                                $"TIC: {point.TicValue:E2}\n" +
                                $"Protein Groups: {point.ProteinCount:N0}\n" +
                                $"Trypsin Ratio: {point.TrypsinRatio * 100:F2}%";

            if (!string.IsNullOrEmpty(point.PredictedCellType))
            {
                tooltipText += $"\nCell Type: {point.PredictedCellType}";
            }

            TooltipText.Text = tooltipText;

            TooltipBorder.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));

            double tooltipX = point.XScreen + 15;
            double tooltipY = point.YScreen - 15;

            if (tooltipX + TooltipBorder.DesiredSize.Width > PlotCanvas.ActualWidth)
                tooltipX = point.XScreen - TooltipBorder.DesiredSize.Width - 15;

            if (tooltipY < 0)
                tooltipY = point.YScreen + 15;

            Canvas.SetLeft(TooltipBorder, tooltipX);
            Canvas.SetTop(TooltipBorder, tooltipY);
            TooltipBorder.Visibility = Visibility.Visible;
        }

        private void ClearHoverEffect()
        {
            foreach (var point in _dataPoints)
            {
                point.Visual.Fill = new SolidColorBrush(point.BaseColor);
                point.Visual.Width = 8;
                point.Visual.Height = 8;
                Canvas.SetLeft(point.Visual, point.XScreen - 4);
                Canvas.SetTop(point.Visual, point.YScreen - 4);
            }
        }

        private void PlotCanvas_MouseLeave(object sender, MouseEventArgs e)
        {
            if (_selectionManager.IsDrawing)
            {
                PlotCanvas.ReleaseMouseCapture();
                _selectionManager.CancelDrawing();
            }

            ClearHoverEffect();
            TooltipBorder.Visibility = Visibility.Collapsed;
        }

        private void PlotCanvas_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (_isRefreshing || _isProcessingSelection) return;

            if (_currentData != null)
            {
                RefreshChart();
            }
        }

        private void LogLogCheckBox_Changed(object sender, RoutedEventArgs e)
        {
            if (_currentData != null)
            {
                RefreshChart();
            }
        }

        private void ClearSelectionButton_Click(object sender, RoutedEventArgs e)
        {
            _selectionManager.ClearSelection();

            foreach (var point in _dataPoints)
            {
                point.IsSelected = false;
                point.Visual.Stroke = new SolidColorBrush(Color.FromRgb(50, 50, 50));
                point.Visual.StrokeThickness = 1;
            }

            SelectedPointsGrid.ItemsSource = null;
            SelectionCountText.Text = "";
            SelectionStatusText.Text = "Click a point or drag to select multiple points. Right-click a point to view details.";
            ClearSelectionButton.IsEnabled = false;
            RunDetailPanel.ClearDetails();
        }

        private void SelectedPointsGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (SelectedPointsGrid.SelectedItem is SelectedPointData selectedData)
            {
                var dataPoint = _dataPoints.FirstOrDefault(p => p.RunName == selectedData.RunName);
                if (dataPoint != null)
                {
                    // When clicking grid, show details for that *single* point
                    RunDetailPanel.ShowRunDetails(dataPoint, _goEnrichmentResults);
                }
            }
        }
    }
}