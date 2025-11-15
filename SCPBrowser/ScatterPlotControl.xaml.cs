using SCPBrowser.GOTools;
using SCPBrowser.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace SCPBrowser
{
    public class ScatterPlotOptions
    {
        public bool UseLogLog { get; set; } = true;
        public bool UseCellTypeColoring { get; set; } = false;
        public Dictionary<string, CellTypePredictionResult> CellTypePredictions { get; set; }
        public Dictionary<string, Color> CellTypeColorMap { get; set; }
        public Dictionary<string, RunGoEnrichmentResult> GoEnrichmentResults { get; set; }
        public Dictionary<string, Color> GoTermColorMap { get; set; }

        // ADD THESE THREE NEW PROPERTIES
        public bool UseBioConditionColoring { get; set; } = false;
        public Dictionary<string, string> BioConditionPerFile { get; set; }
        public Dictionary<string, Color> BioConditionColorMap { get; set; }
    }

    public class PlotSelectionChangedEventArgs : EventArgs
    {
        public List<DataPoint> SelectedPoints { get; set; }
    }

    public class PointInteractionEventArgs : EventArgs
    {
        public DataPoint DataPoint { get; set; }
    }

    public partial class ScatterPlotControl : UserControl
    {
        private ProteomicsData _currentData;
        private ScatterPlotOptions _currentOptions;
        private List<DataPoint> _dataPoints;
        private PlotRenderer _plotRenderer;
        private SelectionManager _selectionManager;
        private bool _isRefreshing = false;
        private const double HoverTolerance = 12;
        private bool _suppressSelectionEvents = false;

        public event EventHandler<PlotSelectionChangedEventArgs> SelectionChanged;
        public event EventHandler<PointInteractionEventArgs> PointHovered;
        public event EventHandler<PointInteractionEventArgs> PointClicked;

        public ScatterPlotControl()
        {
            InitializeComponent();

            _dataPoints = new List<DataPoint>();
            _plotRenderer = new PlotRenderer();
            _selectionManager = new SelectionManager();

            PlotCanvas.SizeChanged += PlotCanvas_SizeChanged;
        }

        public void UpdatePlot(ProteomicsData data, ScatterPlotOptions options)
        {
            var stackTrace = new System.Diagnostics.StackTrace(true);
            Console.WriteLine("UpdatePlot called from:");
            for (int i = 1; i < Math.Min(5, stackTrace.FrameCount); i++)
            {
                var frame = stackTrace.GetFrame(i);
                Console.WriteLine($"  [{i}] {frame.GetMethod().DeclaringType?.Name}.{frame.GetMethod().Name}");
            }

            if (_isRefreshing)
            {
                Console.WriteLine("  -> Already refreshing, exiting");
                return;
            }

            _isRefreshing = true;
            _suppressSelectionEvents = true;

            try
            {
                _currentData = data;
                _currentOptions = options;

                bool hadSelection = _selectionManager.PolygonPointsData.Count > 0;
                var savedDataCoordinates = _selectionManager.PolygonPointsData.ToList();

                PlotCanvas.Children.Clear();
                _dataPoints.Clear();
                TooltipBorder.Visibility = Visibility.Collapsed;

                PlotCanvas.Children.Add(_selectionManager.GetDrawingPolyline());
                PlotCanvas.Children.Add(_selectionManager.GetSelectionPolygon());
                PlotCanvas.Children.Add(_selectionManager.GetStartPointIndicator());

                if (data == null || data.PeptideCountPerFile.Count == 0)
                {
                    return;
                }

                DrawChart(data, options);

                if (hadSelection && savedDataCoordinates.Count > 0)
                {
                    _selectionManager.SetPolygonPointsData(savedDataCoordinates);
                    RedrawSelectionFromDataCoordinates();
                }
            }
            finally
            {
                _suppressSelectionEvents = false;
                _isRefreshing = false;
            }
        }

        public void ClearSelection()
        {
            Console.WriteLine("Selection cleared");
            _selectionManager.ClearSelection();

            foreach (var point in _dataPoints)
            {
                point.IsSelected = false;
                point.Visual.Stroke = new SolidColorBrush(Color.FromRgb(50, 50, 50));
                point.Visual.StrokeThickness = 1;
            }

            SelectionChanged?.Invoke(this, new PlotSelectionChangedEventArgs { SelectedPoints = new List<DataPoint>() });
        }

        public void HighlightPoints(List<string> runNames)
        {
            if (runNames == null || runNames.Count == 0)
            {
                ClearHoverEffect();
                return;
            }

            foreach (var point in _dataPoints)
            {
                if (runNames.Contains(point.RunName))
                {
                    point.Visual.Stroke = new SolidColorBrush(Color.FromRgb(220, 38, 38));
                    point.Visual.StrokeThickness = 2;
                }
                else
                {
                    point.Visual.Stroke = new SolidColorBrush(Color.FromRgb(50, 50, 50));
                    point.Visual.StrokeThickness = 1;
                }
            }
        }

        private void DrawChart(ProteomicsData data, ScatterPlotOptions options)
        {
            double canvasWidth = PlotCanvas.ActualWidth;
            double canvasHeight = PlotCanvas.ActualHeight;

            if (canvasWidth < 100 || canvasHeight < 100)
                return;

            var rawFiles = data.PeptideCountPerFile.Keys.ToList();
            var peptideCounts = rawFiles.Select(rf => data.PeptideCountPerFile[rf]).ToList();
            var ticValues = rawFiles.Select(rf => data.TotalIonCurrentPerFile.ContainsKey(rf) ?
                data.TotalIonCurrentPerFile[rf] : 0).ToList();
            var proteinCounts = rawFiles.Select(rf => data.ProteinCountPerFile.ContainsKey(rf) ?
                data.ProteinCountPerFile[rf] : 0).ToList();
            var trypsinRatios = rawFiles.Select(rf => data.TargetProteinRatioPerFile.ContainsKey(rf) ?
                data.TargetProteinRatioPerFile[rf] : 0).ToList();

            _plotRenderer.CalculateAxisRanges(peptideCounts, ticValues, options.UseLogLog);
            _plotRenderer.DrawAxesAndGrid(PlotCanvas, canvasWidth, canvasHeight);

            double maxRatio = trypsinRatios.Any() ? trypsinRatios.Max() : 0.05;
            if (maxRatio < 0.01) maxRatio = 0.05;

            // *** NEW LOGIC HERE ***
            if (options.UseCellTypeColoring && options.CellTypePredictions != null)
            {
                _dataPoints = DrawDataPointsWithCellTypes(PlotCanvas, rawFiles, peptideCounts, ticValues,
                    proteinCounts, trypsinRatios, canvasWidth, canvasHeight, options.CellTypePredictions, options.CellTypeColorMap);
                DrawCellTypeLegend(PlotCanvas, canvasWidth, canvasHeight, options.CellTypeColorMap);
            }
            else if (options.UseBioConditionColoring && options.BioConditionPerFile != null)
            {
                _dataPoints = DrawDataPointsWithBioConditions(PlotCanvas, rawFiles, peptideCounts, ticValues,
                    proteinCounts, trypsinRatios, canvasWidth, canvasHeight, options.BioConditionPerFile, options.BioConditionColorMap);
                DrawBioConditionLegend(PlotCanvas, canvasWidth, canvasHeight, options.BioConditionColorMap);
            }
            else // Default: Color by Target Protein Ratio
            {
                _dataPoints = _plotRenderer.DrawDataPoints(PlotCanvas, rawFiles, peptideCounts, ticValues,
                    proteinCounts, trypsinRatios, canvasWidth, canvasHeight);
                _plotRenderer.DrawColorLegend(PlotCanvas, canvasWidth, canvasHeight, maxRatio);
            }
            // *** END OF NEW LOGIC ***

            if (_selectionManager.PolygonPointsData.Count > 0)
            {
                RedrawSelectionFromDataCoordinates();
            }
        }

        private List<DataPoint> DrawDataPointsWithCellTypes(Canvas canvas, List<string> rawFiles, List<int> peptideCounts,
            List<double> ticValues, List<int> proteinCounts, List<double> trypsinRatios,
            double canvasWidth, double canvasHeight, Dictionary<string, CellTypePredictionResult> predictions,
            Dictionary<string, Color> colorMap)
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

                if (predictions != null && predictions.ContainsKey(rawFiles[i]))
                {
                    var prediction = predictions[rawFiles[i]];
                    cellType = prediction.TopCellType;
                    predictionScore = prediction.TopScore;

                    if (!string.IsNullOrEmpty(cellType) && colorMap != null && colorMap.ContainsKey(cellType))
                    {
                        markerColor = colorMap[cellType];
                    }
                }

                var ellipse = new System.Windows.Shapes.Ellipse
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

        private void DrawCellTypeLegend(Canvas canvas, double canvasWidth, double canvasHeight, Dictionary<string, Color> colorMap)
        {
            if (colorMap == null || colorMap.Count == 0)
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

            foreach (var cellType in colorMap.OrderBy(kvp => kvp.Key))
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

        private List<DataPoint> DrawDataPointsWithBioConditions(Canvas canvas, List<string> rawFiles, List<int> peptideCounts,
            List<double> ticValues, List<int> proteinCounts, List<double> trypsinRatios,
            double canvasWidth, double canvasHeight, Dictionary<string, string> bioConditions,
            Dictionary<string, Color> colorMap)
        {
            var dataPoints = new List<DataPoint>();
            const double MarkerSize = 8;
            var unassignedColor = Color.FromRgb(200, 200, 200); // Gray for unassigned

            for (int i = 0; i < rawFiles.Count; i++)
            {
                if (ticValues[i] <= 0)
                    continue;

                Point screenPos = _plotRenderer.DataToScreen(peptideCounts[i], ticValues[i], canvasWidth, canvasHeight);

                Color markerColor = unassignedColor;
                string condition = null;

                if (bioConditions != null && bioConditions.TryGetValue(rawFiles[i], out condition) && !string.IsNullOrEmpty(condition))
                {
                    if (colorMap != null && colorMap.ContainsKey(condition))
                    {
                        markerColor = colorMap[condition];
                    }
                }

                var ellipse = new System.Windows.Shapes.Ellipse
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
                    BiologicalCondition = condition // Store the condition
                });
            }

            return dataPoints;
        }

        private void DrawBioConditionLegend(Canvas canvas, double canvasWidth, double canvasHeight, Dictionary<string, Color> colorMap)
        {
            if (colorMap == null || colorMap.Count == 0)
                return;

            double legendX = canvasWidth - 110;
            double legendY = 20;
            double boxSize = 12;
            double spacing = 18;

            var titleText = new TextBlock
            {
                Text = "Biological Condition",
                FontSize = 11,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Colors.Black)
            };
            Canvas.SetLeft(titleText, legendX);
            Canvas.SetTop(titleText, legendY);
            canvas.Children.Add(titleText);

            legendY += 20;

            foreach (var condition in colorMap.OrderBy(kvp => kvp.Key))
            {
                var rect = new System.Windows.Shapes.Rectangle
                {
                    Width = boxSize,
                    Height = boxSize,
                    Fill = new SolidColorBrush(condition.Value),
                    Stroke = new SolidColorBrush(Colors.Black),
                    StrokeThickness = 1
                };
                Canvas.SetLeft(rect, legendX);
                Canvas.SetTop(rect, legendY);
                canvas.Children.Add(rect);

                var label = new TextBlock
                {
                    Text = condition.Key,
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
            var stackTrace = new System.Diagnostics.StackTrace();
            var callingMethod = stackTrace.GetFrame(1)?.GetMethod()?.Name;

            Console.WriteLine($"RedrawSelectionFromDataCoordinates called by {callingMethod}:");
            Console.WriteLine($"  -> PolygonPointsData.Count = {_selectionManager.PolygonPointsData.Count}");
            Console.WriteLine($"  -> _dataPoints.Count = {_dataPoints.Count}");
            Console.WriteLine($"  -> Canvas size = {PlotCanvas.ActualWidth}x{PlotCanvas.ActualHeight}");

            if (_selectionManager.PolygonPointsData.Count < 3)
            {
                Console.WriteLine($"  -> Skipped: Less than 3 polygon points");
                return;
            }

            double canvasWidth = PlotCanvas.ActualWidth;
            double canvasHeight = PlotCanvas.ActualHeight;

            _selectionManager.RedrawSelectionFromDataCoordinates(
                dataPoint => _plotRenderer.DataToScreen(dataPoint.X, dataPoint.Y, canvasWidth, canvasHeight));

            Console.WriteLine($"  -> After redraw, PolygonPointsScreen.Count = {_selectionManager.PolygonPointsScreen.Count}");
            UpdateSelectionVisuals();
        }

        private void UpdateSelectionVisuals()
        {
            var stackTrace = new System.Diagnostics.StackTrace();
            var callingMethod = stackTrace.GetFrame(1)?.GetMethod()?.Name;
            Console.WriteLine($"UpdateSelectionVisuals called by {callingMethod}:");
            Console.WriteLine($"  -> _dataPoints.Count = {_dataPoints.Count}");
            Console.WriteLine($"  -> PolygonPointsScreen.Count = {_selectionManager.PolygonPointsScreen.Count}");

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

            Console.WriteLine($"  -> Found {selectedPoints.Count} points in selection");

            // Only fire SelectionChanged event if not suppressed
            if (!_suppressSelectionEvents)
            {
                Console.WriteLine($"  -> Firing SelectionChanged event");
                SelectionChanged?.Invoke(this, new PlotSelectionChangedEventArgs { SelectedPoints = selectedPoints });
            }
            else
            {
                Console.WriteLine($"  -> SelectionChanged event suppressed");
            }
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
                ClearSelection();
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
                PointHovered?.Invoke(this, new PointInteractionEventArgs { DataPoint = closest });
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
                    PointClicked?.Invoke(this, new PointInteractionEventArgs { DataPoint = point });
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

            if (!string.IsNullOrEmpty(point.BiologicalCondition))
            {
                tooltipText += $"\nCondition: {point.BiologicalCondition}";
            }

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
            Console.WriteLine($"PlotCanvas_SizeChanged fired: {e.PreviousSize.Width}x{e.PreviousSize.Height} -> {e.NewSize.Width}x{e.NewSize.Height}");

            if (_currentData == null)
            {
                Console.WriteLine("  -> Skipped: No data loaded");
                return;
            }

            if (_isRefreshing)
            {
                Console.WriteLine("  -> Skipped: Already refreshing");
                return;
            }

            // Determine if this is an initialization resize (from 0x0)
            bool isInitialization = (e.PreviousSize.Width == 0 || e.PreviousSize.Height == 0);

            // Check if size actually changed meaningfully
            double widthChange = Math.Abs(e.NewSize.Width - e.PreviousSize.Width);
            double heightChange = Math.Abs(e.NewSize.Height - e.PreviousSize.Height);

            bool shouldRefresh = isInitialization || (widthChange > 2.0 || heightChange > 2.0);

            if (shouldRefresh)
            {
                if (isInitialization)
                {
                    Console.WriteLine($"  -> Initial canvas sizing (0x0 -> real size)");
                }
                else
                {
                    Console.WriteLine($"  -> Canvas resize accepted: {widthChange:F2}px width change, {heightChange:F2}px height change");
                }

                UpdatePlot(_currentData, _currentOptions);
            }
            else
            {
                Console.WriteLine($"  -> Skipped: Change too small ({widthChange:F2}px x {heightChange:F2}px)");
            }
        }
    }
}