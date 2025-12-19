using PCANipals;
using SCPBrowser.GOTools;
using SCPBrowser.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using UMAP;

namespace SCPBrowser
{
    public class ScatterPlotOptions
    {
        public bool UsePcaView { get; set; } = false;
        public bool UseUmapView { get; set; } = false;
        public bool UseLogLog { get; set; } = true;
        public bool UseCellTypeColoring { get; set; } = false;
        public Dictionary<string, CellTypePredictionResult> CellTypePredictions { get; set; }
        public Dictionary<string, Color> CellTypeColorMap { get; set; }
        public Dictionary<string, RunGoEnrichmentResult> GoEnrichmentResults { get; set; }
        public Dictionary<string, Color> GoTermColorMap { get; set; }

        public bool UseBioConditionColoring { get; set; } = false;
        public Dictionary<string, string> BioConditionPerFile { get; set; }
        public Dictionary<string, Color> BioConditionColorMap { get; set; }

        // Checkbox filter sets for persistent selection
        public HashSet<string> CheckedCellTypes { get; set; }
        public HashSet<string> CheckedBioConditions { get; set; }
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
        private PcaResult _pcaResult;
        private double[] _pcaVarianceExplained;
        private List<string> _pcaProteinNames;
        private float[][] _umapResult;
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
            for (int i = 1; i < Math.Min(5, stackTrace.FrameCount); i++)
            {
                var frame = stackTrace.GetFrame(i);
            }

            if (_isRefreshing)
            {
                return;
            }

            _isRefreshing = true;
            _suppressSelectionEvents = true;

            try
            {
                // Clear PCA/UMAP if data changed
                if (_currentData != data)
                {
                    _pcaResult = null;
                    _pcaProteinNames = null;
                    _umapResult = null;
                }

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

                // Compute PCA if needed
                if (options.UsePcaView && _pcaResult == null)
                {
                    ComputePca(data);
                }

                // Compute UMAP if needed
                if (options.UseUmapView && _umapResult == null)
                {
                    ComputeUmap(data);
                }

                DrawChart(data, options);

                if (hadSelection && savedDataCoordinates.Count > 0)
                {
                    _selectionManager.SetPolygonPointsData(savedDataCoordinates);
                    RedrawSelectionFromDataCoordinates();
                }

                // Apply checkbox selections at the very end
                if (options.CheckedCellTypes != null || options.CheckedBioConditions != null)
                {
                    UpdateSelectionWithFilters(
                        options.CheckedCellTypes ?? new HashSet<string>(),
                        options.CheckedBioConditions ?? new HashSet<string>());
                }
            }
            finally
            {
                _suppressSelectionEvents = false;
                _isRefreshing = false;
            }
        }

        /// <summary>
        /// Gets PCA loadings data for display. Returns null if PCA hasn't been computed.
        /// </summary>
        public (List<string> ProteinNames, double[,] Loadings, double[] VarianceExplained)? GetPcaLoadings()
        {
            if (_pcaResult == null || _pcaProteinNames == null)
                return null;

            return (_pcaProteinNames, _pcaResult.Loadings, _pcaResult.VarianceExplained);
        }

        private void ComputeUmap(ProteomicsData data)
        {
            if (data == null || data.ProteinQuantMatrix.Count == 0)
            {
                _umapResult = null;
                return;
            }

            try
            {
                var rawFiles = data.RawFileNames.ToList();
                var proteins = data.ProteinQuantMatrix.Keys.ToList();

                int nSamples = rawFiles.Count;
                int nProteins = proteins.Count;

                if (nSamples < 15 || nProteins < 2)
                {
                    Console.WriteLine($"UMAP requires at least 15 samples, got {nSamples}");
                    _umapResult = null;
                    return;
                }

                // Build matrix: rows = samples (raw files), columns = proteins
                // UMAP expects float[][] (jagged array)
                float[][] matrix = new float[nSamples][];

                for (int i = 0; i < nSamples; i++)
                {
                    matrix[i] = new float[nProteins];
                    string rawFile = rawFiles[i];

                    for (int j = 0; j < nProteins; j++)
                    {
                        string protein = proteins[j];
                        if (data.ProteinQuantMatrix[protein].TryGetValue(rawFile, out double value) && value > 0)
                        {
                            matrix[i][j] = (float)Math.Log2(value + 1);
                        }
                        else
                        {
                            matrix[i][j] = 0f; // UMAP doesn't handle NaN well, use 0
                        }
                    }
                }

                // Run UMAP
                var umap = new Umap(
                    distance: Umap.DistanceFunctions.Euclidean,
                    dimensions: 2,
                    numberOfNeighbors: Math.Min(15, nSamples - 1)
                );

                int epochs = umap.InitializeFit(matrix);
                for (int i = 0; i < epochs; i++)
                {
                    umap.Step();
                }

                _umapResult = umap.GetEmbedding();

            }
            catch (Exception ex)
            {
                Console.WriteLine($"UMAP computation failed: {ex.Message}");
                _umapResult = null;
            }
        }

        private void DrawUmapChart(ProteomicsData data, ScatterPlotOptions options, List<string> rawFiles, double canvasWidth, double canvasHeight)
        {
            const double MarginLeft = 60;
            const double MarginRight = 20;
            const double MarginTop = 20;
            const double MarginBottom = 50;

            double plotWidth = canvasWidth - MarginLeft - MarginRight;
            double plotHeight = canvasHeight - MarginTop - MarginBottom;

            // Get UMAP coordinates
            var umap1 = new List<double>();
            var umap2 = new List<double>();

            for (int i = 0; i < rawFiles.Count; i++)
            {
                umap1.Add(_umapResult[i][0]);
                umap2.Add(_umapResult[i][1]);
            }

            // Calculate ranges with padding
            double umap1Min = umap1.Min();
            double umap1Max = umap1.Max();
            double umap2Min = umap2.Min();
            double umap2Max = umap2.Max();

            double umap1Range = umap1Max - umap1Min;
            double umap2Range = umap2Max - umap2Min;

            if (umap1Range < 0.001) umap1Range = 1;
            if (umap2Range < 0.001) umap2Range = 1;

            umap1Min -= umap1Range * 0.1;
            umap1Max += umap1Range * 0.1;
            umap2Min -= umap2Range * 0.1;
            umap2Max += umap2Range * 0.1;

            // Draw axes
            var axisColor = new SolidColorBrush(Color.FromRgb(100, 100, 100));

            var xAxis = new Line
            {
                X1 = MarginLeft,
                Y1 = canvasHeight - MarginBottom,
                X2 = canvasWidth - MarginRight,
                Y2 = canvasHeight - MarginBottom,
                Stroke = axisColor,
                StrokeThickness = 1
            };
            PlotCanvas.Children.Add(xAxis);

            var yAxis = new Line
            {
                X1 = MarginLeft,
                Y1 = MarginTop,
                X2 = MarginLeft,
                Y2 = canvasHeight - MarginBottom,
                Stroke = axisColor,
                StrokeThickness = 1
            };
            PlotCanvas.Children.Add(yAxis);

            // Axis labels
            var xLabel = new TextBlock
            {
                Text = "UMAP 1",
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Colors.Black)
            };
            Canvas.SetLeft(xLabel, MarginLeft + plotWidth / 2 - 25);
            Canvas.SetTop(xLabel, canvasHeight - 25);
            PlotCanvas.Children.Add(xLabel);

            var yLabel = new TextBlock
            {
                Text = "UMAP 2",
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Colors.Black),
                RenderTransform = new RotateTransform(-90)
            };
            Canvas.SetLeft(yLabel, 15);
            Canvas.SetTop(yLabel, MarginTop + plotHeight / 2 + 25);
            PlotCanvas.Children.Add(yLabel);

            // Draw data points
            _dataPoints = new List<DataPoint>();
            const double MarkerSize = 8;

            for (int i = 0; i < rawFiles.Count; i++)
            {
                double xNorm = (umap1[i] - umap1Min) / (umap1Max - umap1Min);
                double yNorm = (umap2[i] - umap2Min) / (umap2Max - umap2Min);

                double screenX = MarginLeft + xNorm * plotWidth;
                double screenY = canvasHeight - MarginBottom - yNorm * plotHeight;

                // Determine color
                Color markerColor = Color.FromRgb(100, 149, 237);
                string cellType = null;
                string bioCondition = null;
                CellTypeScore predictionScore = null;

                if (options.UseCellTypeColoring && options.CellTypePredictions != null)
                {
                    if (options.CellTypePredictions.TryGetValue(rawFiles[i], out var prediction))
                    {
                        cellType = prediction.TopCellType;
                        predictionScore = prediction.TopScore;
                        if (!string.IsNullOrEmpty(cellType) && options.CellTypeColorMap != null &&
                            options.CellTypeColorMap.TryGetValue(cellType, out var color))
                        {
                            markerColor = color;
                        }
                    }
                }
                else if (options.UseBioConditionColoring && options.BioConditionPerFile != null)
                {
                    if (options.BioConditionPerFile.TryGetValue(rawFiles[i], out bioCondition) &&
                        !string.IsNullOrEmpty(bioCondition) && options.BioConditionColorMap != null &&
                        options.BioConditionColorMap.TryGetValue(bioCondition, out var color))
                    {
                        markerColor = color;
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

                Canvas.SetLeft(ellipse, screenX - MarkerSize / 2);
                Canvas.SetTop(ellipse, screenY - MarkerSize / 2);
                PlotCanvas.Children.Add(ellipse);

                int peptideCount = data.PeptideCountPerFile.ContainsKey(rawFiles[i]) ? data.PeptideCountPerFile[rawFiles[i]] : 0;
                double ticValue = data.TotalIonCurrentPerFile.ContainsKey(rawFiles[i]) ? data.TotalIonCurrentPerFile[rawFiles[i]] : 0;
                int proteinCount = data.ProteinCountPerFile.ContainsKey(rawFiles[i]) ? data.ProteinCountPerFile[rawFiles[i]] : 0;
                double trypsinRatio = data.TargetProteinRatioPerFile.ContainsKey(rawFiles[i]) ? data.TargetProteinRatioPerFile[rawFiles[i]] : 0;

                var dataPoint = new DataPoint
                {
                    RunName = rawFiles[i],
                    XScreen = screenX,
                    YScreen = screenY,
                    XData = umap1[i],
                    YData = umap2[i],
                    PeptideCount = peptideCount,
                    TicValue = ticValue,
                    ProteinCount = proteinCount,
                    TrypsinRatio = trypsinRatio,
                    Visual = ellipse,
                    BaseColor = markerColor,
                    PredictedCellType = cellType,
                    PredictionScore = predictionScore,
                    BiologicalCondition = bioCondition
                };

                _dataPoints.Add(dataPoint);
            }
        }

        private void ComputePca(ProteomicsData data)
        {
            if (data == null || data.ProteinQuantMatrix.Count == 0)
            {
                _pcaResult = null;
                _pcaProteinNames = null;
                return;
            }

            try
            {
                var rawFiles = data.RawFileNames.ToList();
                var proteins = data.ProteinQuantMatrix.Keys.ToList();

                int nSamples = rawFiles.Count;
                int nProteins = proteins.Count;

                if (nSamples < 3 || nProteins < 2)
                {
                    _pcaResult = null;
                    _pcaProteinNames = null;
                    return;
                }

                // Build matrix: rows = samples (raw files), columns = proteins
                double[,] matrix = new double[nSamples, nProteins];

                for (int i = 0; i < nSamples; i++)
                {
                    string rawFile = rawFiles[i];
                    for (int j = 0; j < nProteins; j++)
                    {
                        string protein = proteins[j];
                        if (data.ProteinQuantMatrix[protein].TryGetValue(rawFile, out double value) && value > 0)
                        {
                            matrix[i, j] = Math.Log2(value + 1);
                        }
                        else
                        {
                            matrix[i, j] = double.NaN;
                        }
                    }
                }

                // Run NIPALS PCA
                _pcaResult = NipalsAlgorithm.Compute(
                    matrix,
                    nComponents: 2,
                    maxIterations: 500,
                    tolerance: 1e-9,
                    center: true,
                    scale: false);

                _pcaProteinNames = proteins;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"PCA computation failed: {ex.Message}");
                _pcaResult = null;
                _pcaProteinNames = null;
            }
        }

        /// <summary>
        /// Updates point selection based on checkbox filters combined with existing polygon selection.
        /// </summary>
        /// <param name="checkedCellTypes">Set of checked cell types</param>
        /// <param name="checkedBioConditions">Set of checked biological conditions</param>
        public void UpdateSelectionWithFilters(HashSet<string> checkedCellTypes, HashSet<string> checkedBioConditions)
        {
            if (_dataPoints == null || _dataPoints.Count == 0)
                return;

            var selectedPoints = new List<DataPoint>();
            bool hasPolygonSelection = _selectionManager.PolygonPointsScreen.Count >= 3;

            foreach (var point in _dataPoints)
            {
                bool isInPolygon = false;
                if (hasPolygonSelection)
                {
                    Point testPoint = new Point(point.XScreen, point.YScreen);
                    isInPolygon = _selectionManager.IsPointInSelection(testPoint);
                }

                bool matchesCellType = checkedCellTypes != null &&
                                       checkedCellTypes.Count > 0 &&
                                       !string.IsNullOrEmpty(point.PredictedCellType) &&
                                       checkedCellTypes.Contains(point.PredictedCellType);

                bool matchesBioCondition = checkedBioConditions != null &&
                                           checkedBioConditions.Count > 0 &&
                                           !string.IsNullOrEmpty(point.BiologicalCondition) &&
                                           checkedBioConditions.Contains(point.BiologicalCondition);

                bool shouldBeSelected = isInPolygon || matchesCellType || matchesBioCondition;

                point.IsSelected = shouldBeSelected;

                if (shouldBeSelected)
                {
                    selectedPoints.Add(point);
                    point.Visual.Stroke = new SolidColorBrush(Color.FromRgb(220, 38, 38));
                    point.Visual.StrokeThickness = 2;
                }
                else
                {
                    point.Visual.Stroke = new SolidColorBrush(Color.FromRgb(50, 50, 50));
                    point.Visual.StrokeThickness = 1;
                }
            }

            SelectionChanged?.Invoke(this, new PlotSelectionChangedEventArgs { SelectedPoints = selectedPoints });
        }

        public void ClearSelection()
        {
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

            // PCA mode
            if (options.UsePcaView && _pcaResult != null)
            {
                DrawPcaChart(data, options, rawFiles, canvasWidth, canvasHeight);
                return;
            }

            // UMAP mode
            if (options.UseUmapView && _umapResult != null)
            {
                DrawUmapChart(data, options, rawFiles, canvasWidth, canvasHeight);
                return;
            }

            var peptideCounts = rawFiles.Select(rf => data.PeptideCountPerFile[rf]).ToList();
            var ticValues = rawFiles.Select(rf => data.TotalIonCurrentPerFile.ContainsKey(rf) ?
                data.TotalIonCurrentPerFile[rf] : 0).ToList();
            var proteinCounts = rawFiles.Select(rf => data.ProteinCountPerFile.ContainsKey(rf) ?
                data.ProteinCountPerFile[rf] : 0).ToList();
            var trypsinRatios = rawFiles.Select(rf => data.TargetProteinRatioPerFile.ContainsKey(rf) ?
                data.TargetProteinRatioPerFile[rf] : 0).ToList();

            // Determine if we need space for internal legend (only for Target Protein Ratio mode)
            bool showInternalLegend = !options.UseCellTypeColoring && !options.UseBioConditionColoring;
            _plotRenderer.SetShowInternalLegend(showInternalLegend);

            _plotRenderer.CalculateAxisRanges(peptideCounts, ticValues, options.UseLogLog);
            _plotRenderer.DrawAxesAndGrid(PlotCanvas, canvasWidth, canvasHeight);

            double maxRatio = trypsinRatios.Any() ? trypsinRatios.Max() : 0.05;
            if (maxRatio < 0.01) maxRatio = 0.05;

            if (options.UseCellTypeColoring && options.CellTypePredictions != null)
            {
                _dataPoints = DrawDataPointsWithCellTypes(PlotCanvas, rawFiles, peptideCounts, ticValues,
                    proteinCounts, trypsinRatios, canvasWidth, canvasHeight, options.CellTypePredictions,
                    options.CellTypeColorMap, options.BioConditionPerFile);
            }
            else if (options.UseBioConditionColoring && options.BioConditionPerFile != null)
            {
                _dataPoints = DrawDataPointsWithBioConditions(PlotCanvas, rawFiles, peptideCounts, ticValues,
                    proteinCounts, trypsinRatios, canvasWidth, canvasHeight, options.BioConditionPerFile,
                    options.BioConditionColorMap, options.CellTypePredictions);
            }
            else // Default: Color by Target Protein Ratio
            {
                _dataPoints = _plotRenderer.DrawDataPoints(PlotCanvas, rawFiles, peptideCounts, ticValues,
                    proteinCounts, trypsinRatios, canvasWidth, canvasHeight);
                _plotRenderer.DrawColorLegend(PlotCanvas, canvasWidth, canvasHeight, maxRatio);
            }

            if (_selectionManager.PolygonPointsData.Count > 0)
            {
                RedrawSelectionFromDataCoordinates();
            }
        }


        private void DrawPcaChart(ProteomicsData data, ScatterPlotOptions options, List<string> rawFiles, double canvasWidth, double canvasHeight)
        {
            const double MarginLeft = 60;
            const double MarginRight = 20;
            const double MarginTop = 20;
            const double MarginBottom = 50;

            double plotWidth = canvasWidth - MarginLeft - MarginRight;
            double plotHeight = canvasHeight - MarginTop - MarginBottom;

            // Get PC scores
            var pc1Scores = new List<double>();
            var pc2Scores = new List<double>();

            for (int i = 0; i < rawFiles.Count; i++)
            {
                pc1Scores.Add(_pcaResult.Scores[i, 0]);
                pc2Scores.Add(_pcaResult.Scores[i, 1]);
            }

            // Calculate ranges with padding
            double pc1Min = pc1Scores.Min();
            double pc1Max = pc1Scores.Max();
            double pc2Min = pc2Scores.Min();
            double pc2Max = pc2Scores.Max();

            double pc1Range = pc1Max - pc1Min;
            double pc2Range = pc2Max - pc2Min;

            pc1Min -= pc1Range * 0.1;
            pc1Max += pc1Range * 0.1;
            pc2Min -= pc2Range * 0.1;
            pc2Max += pc2Range * 0.1;

            // Draw axes
            var axisColor = new SolidColorBrush(Color.FromRgb(100, 100, 100));

            // X axis
            var xAxis = new System.Windows.Shapes.Line
            {
                X1 = MarginLeft,
                Y1 = canvasHeight - MarginBottom,
                X2 = canvasWidth - MarginRight,
                Y2 = canvasHeight - MarginBottom,
                Stroke = axisColor,
                StrokeThickness = 1
            };
            PlotCanvas.Children.Add(xAxis);

            // Y axis
            var yAxis = new System.Windows.Shapes.Line
            {
                X1 = MarginLeft,
                Y1 = MarginTop,
                X2 = MarginLeft,
                Y2 = canvasHeight - MarginBottom,
                Stroke = axisColor,
                StrokeThickness = 1
            };
            PlotCanvas.Children.Add(yAxis);

            // Axis labels with variance explained
            string pc1Label = $"PC1 ({_pcaResult.VarianceExplained[0]:P1})";
            string pc2Label = $"PC2 ({_pcaResult.VarianceExplained[1]:P1})";

            var xLabel = new TextBlock
            {
                Text = pc1Label,
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Colors.Black)
            };
            Canvas.SetLeft(xLabel, MarginLeft + plotWidth / 2 - 40);
            Canvas.SetTop(xLabel, canvasHeight - 25);
            PlotCanvas.Children.Add(xLabel);

            var yLabel = new TextBlock
            {
                Text = pc2Label,
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Colors.Black),
                RenderTransform = new RotateTransform(-90)
            };
            Canvas.SetLeft(yLabel, 15);
            Canvas.SetTop(yLabel, MarginTop + plotHeight / 2 + 40);
            PlotCanvas.Children.Add(yLabel);

            // Draw data points
            _dataPoints = new List<DataPoint>();
            const double MarkerSize = 8;

            for (int i = 0; i < rawFiles.Count; i++)
            {
                double xNorm = (pc1Scores[i] - pc1Min) / (pc1Max - pc1Min);
                double yNorm = (pc2Scores[i] - pc2Min) / (pc2Max - pc2Min);

                double screenX = MarginLeft + xNorm * plotWidth;
                double screenY = canvasHeight - MarginBottom - yNorm * plotHeight;

                // Determine color
                Color markerColor = Color.FromRgb(100, 149, 237); // Default: cornflower blue
                string cellType = null;
                string bioCondition = null;
                CellTypeScore predictionScore = null;

                if (options.UseCellTypeColoring && options.CellTypePredictions != null)
                {
                    if (options.CellTypePredictions.TryGetValue(rawFiles[i], out var prediction) && prediction.TopCellType != null)
                    {
                        cellType = prediction.TopCellType;
                        predictionScore = prediction.TopScore;
                        if (options.CellTypeColorMap != null && options.CellTypeColorMap.TryGetValue(cellType, out var color))
                        {
                            markerColor = color;
                        }
                    }
                }
                else if (options.UseBioConditionColoring && options.BioConditionPerFile != null)
                {
                    if (options.BioConditionPerFile.TryGetValue(rawFiles[i], out var condition) && !string.IsNullOrEmpty(condition))
                    {
                        bioCondition = condition;
                        if (options.BioConditionColorMap != null && options.BioConditionColorMap.TryGetValue(condition, out var color))
                        {
                            markerColor = color;
                        }
                    }
                }

                var ellipse = new Ellipse
                {
                    Width = MarkerSize,
                    Height = MarkerSize,
                    Fill = new SolidColorBrush(markerColor),
                    Stroke = new SolidColorBrush(Color.FromRgb(50, 50, 50)),
                    StrokeThickness = 1,
                    Cursor = Cursors.Hand
                };

                Canvas.SetLeft(ellipse, screenX - MarkerSize / 2);
                Canvas.SetTop(ellipse, screenY - MarkerSize / 2);
                PlotCanvas.Children.Add(ellipse);

                // Get additional data for DataPoint
                int peptideCount = data.PeptideCountPerFile.ContainsKey(rawFiles[i]) ? data.PeptideCountPerFile[rawFiles[i]] : 0;
                double ticValue = data.TotalIonCurrentPerFile.ContainsKey(rawFiles[i]) ? data.TotalIonCurrentPerFile[rawFiles[i]] : 0;
                int proteinCount = data.ProteinCountPerFile.ContainsKey(rawFiles[i]) ? data.ProteinCountPerFile[rawFiles[i]] : 0;
                double trypsinRatio = data.TargetProteinRatioPerFile.ContainsKey(rawFiles[i]) ? data.TargetProteinRatioPerFile[rawFiles[i]] : 0;

                var dataPoint = new DataPoint
                {
                    RunName = rawFiles[i],
                    XScreen = screenX,
                    YScreen = screenY,
                    XData = pc1Scores[i],
                    YData = pc2Scores[i],
                    PeptideCount = peptideCount,
                    TicValue = ticValue,
                    ProteinCount = proteinCount,
                    TrypsinRatio = trypsinRatio,
                    Visual = ellipse,
                    BaseColor = markerColor,
                    PredictedCellType = cellType,
                    PredictionScore = predictionScore,
                    BiologicalCondition = bioCondition
                };

                _dataPoints.Add(dataPoint);
            }
        }



        private List<DataPoint> DrawDataPointsWithCellTypes(Canvas canvas, List<string> rawFiles, List<int> peptideCounts,
      List<double> ticValues, List<int> proteinCounts, List<double> trypsinRatios,
      double canvasWidth, double canvasHeight, Dictionary<string, CellTypePredictionResult> predictions,
      Dictionary<string, Color> colorMap, Dictionary<string, string> bioConditions)
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

                string condition = null;
                if (bioConditions != null && bioConditions.TryGetValue(rawFiles[i], out var cond))
                {
                    condition = cond;
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
                    PredictionScore = predictionScore,
                    BiologicalCondition = condition
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
            Dictionary<string, Color> colorMap, Dictionary<string, CellTypePredictionResult> predictions)
        {
            var dataPoints = new List<DataPoint>();
            const double MarkerSize = 8;
            var unassignedColor = Color.FromRgb(200, 200, 200);

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

                string cellType = null;
                CellTypeScore predictionScore = null;
                if (predictions != null && predictions.ContainsKey(rawFiles[i]))
                {
                    var prediction = predictions[rawFiles[i]];
                    cellType = prediction.TopCellType;
                    predictionScore = prediction.TopScore;
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
                    PredictionScore = predictionScore,
                    BiologicalCondition = condition
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

            // Only fire SelectionChanged event if not suppressed
            if (!_suppressSelectionEvents)
            {
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

                UpdatePlot(_currentData, _currentOptions);
            }
            else
            {
                Console.WriteLine($"  -> Skipped: Change too small ({widthChange:F2}px x {heightChange:F2}px)");
            }
        }
    }
}