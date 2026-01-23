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
        // Plate coloring
        public bool UsePlateColoring { get; set; } = false;
        public Dictionary<string, string> PlatePerFile { get; set; }
        public Dictionary<string, Color> PlateColorMap { get; set; }
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
        public HashSet<string> CheckedPlates { get; set; }

        // Add field near other private fields:
        private bool _previousApplyBatchCorrection = false;

        // HVP for dimensionality reduction
        public List<HvpResult> HvpResults { get; set; }

        // Batch effect correction
        public bool ApplyBatchCorrection { get; set; } = false;

        public Dictionary<string, int> BatchLabelPerFile { get; set; }
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
        private bool _previousApplyBatchCorrection = false;
        private HashSet<string> _hvpProteinIds;
        private Dictionary<string, int> _plateMappingPerFile;

        // Selection styling constants
        private static readonly Color UnselectedGray = Color.FromRgb(210, 210, 210);
        private const double UnselectedOpacity = 0.5;

        public ScatterPlotControl()
        {
            InitializeComponent();

            _dataPoints = new List<DataPoint>();
            _plotRenderer = new PlotRenderer();
            _selectionManager = new SelectionManager();

            PlotCanvas.SizeChanged += PlotCanvas_SizeChanged;
        }

        /// <summary>
        /// Sets the plate mapping for batch effect correction (plate = batch)
        /// </summary>
        public void SetPlateMapping(Dictionary<string, int> plateMapping)
        {
            _plateMappingPerFile = plateMapping ?? new Dictionary<string, int>();
        }

        /// <summary>
        /// Applies selection styling to a data point.
        /// Selected points show their base color, unselected points are grayed out.
        /// </summary>
        private void ApplySelectionStyling(DataPoint point, bool isSelected)
        {
            if (point?.Visual == null) return;

            if (isSelected)
            {
                point.Visual.Fill = new SolidColorBrush(point.BaseColor);
                point.Visual.Stroke = new SolidColorBrush(Color.FromRgb(50, 50, 50));
                point.Visual.StrokeThickness = 1;
                point.Visual.Opacity = 1.0;
            }
            else
            {
                point.Visual.Fill = new SolidColorBrush(UnselectedGray);
                point.Visual.Stroke = new SolidColorBrush(UnselectedGray);
                point.Visual.StrokeThickness = 1;
                point.Visual.Opacity = UnselectedOpacity;
            }
        }

        public void UpdatePlot(ProteomicsData data, ScatterPlotOptions options)
        {
            if (_isRefreshing)
            {
                return;
            }

            _isRefreshing = true;
            _suppressSelectionEvents = true;

            try
            {
                // Extract HVP protein IDs from options
                HashSet<string> newHvpIds = null;
                if (options?.HvpResults != null && options.HvpResults.Count > 0)
                {
                    newHvpIds = new HashSet<string>(
                        options.HvpResults
                            .Where(h => h.IsHighlyVariable)
                            .Select(h => h.ProteinId));
                }

                // Clear PCA/UMAP if data changed OR if HVP set changed OR if batch correction toggled
                bool hvpChanged = !HvpSetsEqual(_hvpProteinIds, newHvpIds);
                bool batchCorrectionChanged = (options?.ApplyBatchCorrection ?? false) != _previousApplyBatchCorrection;

                if (_currentData != data || hvpChanged || batchCorrectionChanged)
                {
                    _pcaResult = null;
                    _pcaProteinNames = null;
                    _umapResult = null;

                    if (hvpChanged)
                    {
                        Console.WriteLine($"HVP set changed: {_hvpProteinIds?.Count ?? 0} -> {newHvpIds?.Count ?? 0}");
                    }
                    if (batchCorrectionChanged)
                    {
                        Console.WriteLine($"Batch correction changed: {_previousApplyBatchCorrection} -> {options?.ApplyBatchCorrection ?? false}");
                    }
                }

                _hvpProteinIds = newHvpIds;
                _previousApplyBatchCorrection = options?.ApplyBatchCorrection ?? false;

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
                if (options.CheckedCellTypes != null || options.CheckedBioConditions != null || options.CheckedPlates != null)
                {
                    UpdateSelectionWithFilters(
                        options.CheckedCellTypes ?? new HashSet<string>(),
                        options.CheckedBioConditions ?? new HashSet<string>(),
                        options.CheckedPlates ?? new HashSet<string>());
                }
            }
            finally
            {
                _suppressSelectionEvents = false;
                _isRefreshing = false;
            }
        }

        private bool HvpSetsEqual(HashSet<string> a, HashSet<string> b)
        {
            if (a == null && b == null) return true;
            if (a == null || b == null) return false;
            if (a.Count != b.Count) return false;
            return a.SetEquals(b);
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

                // Determine which proteins to use - HVPs if available, otherwise all
                List<string> proteins;
                if (_hvpProteinIds != null && _hvpProteinIds.Count > 0)
                {
                    proteins = data.ProteinQuantMatrix.Keys
                        .Where(p => _hvpProteinIds.Contains(p))
                        .ToList();
                    Console.WriteLine($"UMAP: Using {proteins.Count} highly variable proteins");
                }
                else
                {
                    proteins = data.ProteinQuantMatrix.Keys.ToList();
                    Console.WriteLine($"UMAP: Using all {proteins.Count} proteins (no HVP filter)");
                }

                int nSamples = rawFiles.Count;
                int nProteins = proteins.Count;

                if (nSamples < 15 || nProteins < 2)
                {
                    Console.WriteLine($"UMAP requires at least 15 samples, got {nSamples}");
                    _umapResult = null;
                    return;
                }

                // Build matrix as double[,] first (for potential ComBat correction)
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
                            matrix[i, j] = 0; // Impute missing as 0 for ComBat compatibility
                        }
                    }
                }

                // Apply batch correction if enabled
                if (_currentOptions?.ApplyBatchCorrection == true && _currentOptions.BatchLabelPerFile != null)
                {
                    var batchLabels = rawFiles
                        .Select(rf => _currentOptions.BatchLabelPerFile.TryGetValue(rf, out int plateId) ? plateId : 0)
                        .ToList();

                    var uniqueBatches = batchLabels.Distinct().Count();
                    if (uniqueBatches >= 2)
                    {
                        Console.WriteLine($"UMAP: Applying ComBat batch correction ({uniqueBatches} batches)");
                        var combatService = new ComBatService();
                        var result = combatService.Apply(matrix, batchLabels);

                        if (result.Success)
                        {
                            matrix = result.CorrectedData;
                            Console.WriteLine($"UMAP: ComBat correction applied successfully");
                        }
                        else
                        {
                            Console.WriteLine($"UMAP: ComBat correction failed - {result.ErrorMessage}");
                        }
                    }
                    else
                    {
                        Console.WriteLine($"UMAP: Skipping batch correction (only {uniqueBatches} batch)");
                    }
                }

                // Convert to float[][] for UMAP
                float[][] umapMatrix = new float[nSamples][];
                for (int i = 0; i < nSamples; i++)
                {
                    umapMatrix[i] = new float[nProteins];
                    for (int j = 0; j < nProteins; j++)
                    {
                        umapMatrix[i][j] = (float)matrix[i, j];
                    }
                }

                // Run UMAP
                var umap = new Umap(
                    distance: Umap.DistanceFunctions.Euclidean,
                    dimensions: 2,
                    numberOfNeighbors: Math.Min(15, nSamples - 1)
                );

                int epochs = umap.InitializeFit(umapMatrix);
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
            const double MarkerSize = 10;

            for (int i = 0; i < rawFiles.Count; i++)
            {
                double xNorm = (umap1[i] - umap1Min) / (umap1Max - umap1Min);
                double yNorm = (umap2[i] - umap2Min) / (umap2Max - umap2Min);

                double screenX = MarginLeft + xNorm * plotWidth;
                double screenY = canvasHeight - MarginBottom - yNorm * plotHeight;

                // Determine color
                Color markerColor = Color.FromRgb(100, 149, 237); // Default: cornflower blue
                string cellType = null;
                string bioCondition = null;
                string plateName = null;
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
                else if (options.UsePlateColoring && options.PlatePerFile != null)
                {
                    if (options.PlatePerFile.TryGetValue(rawFiles[i], out var plate) && !string.IsNullOrEmpty(plate))
                    {
                        plateName = plate;
                        if (options.PlateColorMap != null && options.PlateColorMap.TryGetValue(plate, out var color))
                        {
                            markerColor = color;
                        }
                    }
                }

                var ellipse = new Ellipse
                {
                    Width = MarkerSize,
                    Height = MarkerSize,
                    Fill = new SolidColorBrush(UnselectedGray),
                    Stroke = new SolidColorBrush(UnselectedGray),
                    StrokeThickness = 1,
                    Cursor = Cursors.Hand,
                    Opacity = UnselectedOpacity
                };

                Canvas.SetLeft(ellipse, screenX - MarkerSize / 2);
                Canvas.SetTop(ellipse, screenY - MarkerSize / 2);
                PlotCanvas.Children.Add(ellipse);

                // Get additional data for DataPoint
                data.PeptideCountPerFile.TryGetValue(rawFiles[i], out int peptideCount);
                data.TotalIonCurrentPerFile.TryGetValue(rawFiles[i], out double ticValue);
                data.ProteinCountPerFile.TryGetValue(rawFiles[i], out int proteinCount);
                data.TargetProteinRatioPerFile.TryGetValue(rawFiles[i], out double trypsinRatio);

                // Get bio condition for tooltip if not coloring by it
                if (bioCondition == null && options.BioConditionPerFile != null)
                {
                    options.BioConditionPerFile.TryGetValue(rawFiles[i], out bioCondition);
                }

                // Get cell type for tooltip if not coloring by it
                if (cellType == null && options.CellTypePredictions != null)
                {
                    if (options.CellTypePredictions.TryGetValue(rawFiles[i], out var prediction))
                    {
                        cellType = prediction.TopCellType;
                        predictionScore = prediction.TopScore;
                    }
                }

                // Get plate name for tooltip if not coloring by it
                if (plateName == null && options.PlatePerFile != null)
                {
                    options.PlatePerFile.TryGetValue(rawFiles[i], out plateName);
                }

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
                    BiologicalCondition = bioCondition,
                    PlateName = plateName
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

                // Determine which proteins to use - HVPs if available, otherwise all
                List<string> proteins;
                if (_hvpProteinIds != null && _hvpProteinIds.Count > 0)
                {
                    proteins = data.ProteinQuantMatrix.Keys
                        .Where(p => _hvpProteinIds.Contains(p))
                        .ToList();
                    Console.WriteLine($"PCA: Using {proteins.Count} highly variable proteins");
                }
                else
                {
                    proteins = data.ProteinQuantMatrix.Keys.ToList();
                    Console.WriteLine($"PCA: Using all {proteins.Count} proteins (no HVP filter)");
                }

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
                            matrix[i, j] = 0; // Impute missing as 0 for ComBat compatibility
                        }
                    }
                }

                // Apply batch correction if enabled
                if (_currentOptions?.ApplyBatchCorrection == true && _currentOptions.BatchLabelPerFile != null)
                {
                    var batchLabels = rawFiles
                        .Select(rf => _currentOptions.BatchLabelPerFile.TryGetValue(rf, out int plateId) ? plateId : 0)
                        .ToList();

                    var uniqueBatches = batchLabels.Distinct().Count();
                    if (uniqueBatches >= 2)
                    {
                        Console.WriteLine($"PCA: Applying ComBat batch correction ({uniqueBatches} batches)");
                        var combatService = new ComBatService();
                        var result = combatService.Apply(matrix, batchLabels);

                        if (result.Success)
                        {
                            matrix = result.CorrectedData;
                            Console.WriteLine($"PCA: ComBat correction applied successfully");
                        }
                        else
                        {
                            Console.WriteLine($"PCA: ComBat correction failed - {result.ErrorMessage}");
                        }
                    }
                    else
                    {
                        Console.WriteLine($"PCA: Skipping batch correction (only {uniqueBatches} batch)");
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
        /// <param name="userInteraction">If true, always apply filters even if all are empty</param>
        public void UpdateSelectionWithFilters(HashSet<string> checkedCellTypes, HashSet<string> checkedBioConditions, HashSet<string> checkedPlates = null, bool userInteraction = false)
        {
            if (_dataPoints == null || _dataPoints.Count == 0)
                return;

            // If all filter sets are empty and this isn't a user interaction, keep all points selected
            bool hasNoFilters = (checkedCellTypes == null || checkedCellTypes.Count == 0) &&
                               (checkedBioConditions == null || checkedBioConditions.Count == 0) &&
                               (checkedPlates == null || checkedPlates.Count == 0) &&
                               _selectionManager.PolygonPointsScreen.Count < 3;

            if (hasNoFilters && !userInteraction)
                return;

            // Debug: log first point's PlateName
            if (_dataPoints.Count > 0)
            {
                var firstPoint = _dataPoints[0];
                Console.WriteLine($"[DEBUG] UpdateSelectionWithFilters: First point PlateName='{firstPoint.PlateName}', BioCond='{firstPoint.BiologicalCondition}'");
            }

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

                bool matchesPlate = checkedPlates != null &&
                                    checkedPlates.Count > 0 &&
                                    !string.IsNullOrEmpty(point.PlateName) &&
                                    checkedPlates.Contains(point.PlateName);

                bool shouldBeSelected = isInPolygon || matchesCellType || matchesBioCondition || matchesPlate;

                point.IsSelected = shouldBeSelected;

                if (shouldBeSelected)
                {
                    selectedPoints.Add(point);
                }

                ApplySelectionStyling(point, shouldBeSelected);
            }

            SelectionChanged?.Invoke(this, new PlotSelectionChangedEventArgs { SelectedPoints = selectedPoints });
        }

        public void ClearSelection()
        {
            _selectionManager.ClearSelection();

            foreach (var point in _dataPoints)
            {
                point.IsSelected = false;
                ApplySelectionStyling(point, false);
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
                bool isHighlighted = runNames.Contains(point.RunName);
                ApplySelectionStyling(point, isHighlighted);
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
            bool showInternalLegend = !options.UseCellTypeColoring && !options.UseBioConditionColoring && !options.UsePlateColoring;
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
            else if (options.UsePlateColoring && options.PlatePerFile != null)
            {
                _dataPoints = DrawDataPointsWithPlates(PlotCanvas, rawFiles, peptideCounts, ticValues,
                    proteinCounts, trypsinRatios, canvasWidth, canvasHeight, options.PlatePerFile,
                    options.PlateColorMap, options.CellTypePredictions, options.BioConditionPerFile);
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

            // Y axis
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
            const double MarkerSize = 10;

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
                string plateName = null;
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
                else if (options.UsePlateColoring && options.PlatePerFile != null)
                {
                    if (options.PlatePerFile.TryGetValue(rawFiles[i], out var plate) && !string.IsNullOrEmpty(plate))
                    {
                        plateName = plate;
                        if (options.PlateColorMap != null && options.PlateColorMap.TryGetValue(plate, out var color))
                        {
                            markerColor = color;
                        }
                    }
                }

                var ellipse = new Ellipse
                {
                    Width = MarkerSize,
                    Height = MarkerSize,
                    Fill = new SolidColorBrush(UnselectedGray),
                    Stroke = new SolidColorBrush(UnselectedGray),
                    StrokeThickness = 1,
                    Cursor = Cursors.Hand,
                    Opacity = UnselectedOpacity
                };

                Canvas.SetLeft(ellipse, screenX - MarkerSize / 2);
                Canvas.SetTop(ellipse, screenY - MarkerSize / 2);
                PlotCanvas.Children.Add(ellipse);

                // Get additional data for DataPoint
                data.PeptideCountPerFile.TryGetValue(rawFiles[i], out int peptideCount);
                data.TotalIonCurrentPerFile.TryGetValue(rawFiles[i], out double ticValue);
                data.ProteinCountPerFile.TryGetValue(rawFiles[i], out int proteinCount);
                data.TargetProteinRatioPerFile.TryGetValue(rawFiles[i], out double trypsinRatio);

                // Get bio condition for tooltip if not coloring by it
                if (bioCondition == null && options.BioConditionPerFile != null)
                {
                    options.BioConditionPerFile.TryGetValue(rawFiles[i], out bioCondition);
                }

                // Get cell type for tooltip if not coloring by it
                if (cellType == null && options.CellTypePredictions != null)
                {
                    if (options.CellTypePredictions.TryGetValue(rawFiles[i], out var prediction))
                    {
                        cellType = prediction.TopCellType;
                        predictionScore = prediction.TopScore;
                    }
                }

                // Get plate name for tooltip if not coloring by it
                if (plateName == null && options.PlatePerFile != null)
                {
                    options.PlatePerFile.TryGetValue(rawFiles[i], out plateName);
                }

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
                    BiologicalCondition = bioCondition,
                    PlateName = plateName
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
            const double MarkerSize = 10;

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
                    StrokeThickness = 1,
                    Opacity = 1.0
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
                    IsSelected = true,
                    PredictedCellType = cellType,
                    PredictionScore = predictionScore,
                    BiologicalCondition = condition
                });
            }

            return dataPoints;
        }

        private List<DataPoint> DrawDataPointsWithPlates(Canvas canvas, List<string> rawFiles, List<int> peptideCounts,
    List<double> ticValues, List<int> proteinCounts, List<double> trypsinRatios,
    double canvasWidth, double canvasHeight, Dictionary<string, string> platePerFile,
    Dictionary<string, Color> colorMap, Dictionary<string, CellTypePredictionResult> predictions,
    Dictionary<string, string> bioConditions)
        {
            var dataPoints = new List<DataPoint>();
            const double MarkerSize = 10;
            var unassignedColor = Color.FromRgb(200, 200, 200);

            for (int i = 0; i < rawFiles.Count; i++)
            {
                if (ticValues[i] <= 0)
                    continue;

                Point screenPos = _plotRenderer.DataToScreen(peptideCounts[i], ticValues[i], canvasWidth, canvasHeight);

                Color markerColor = unassignedColor;
                string plateName = null;

                if (platePerFile != null && platePerFile.TryGetValue(rawFiles[i], out plateName) && !string.IsNullOrEmpty(plateName))
                {
                    if (colorMap != null && colorMap.TryGetValue(plateName, out var color))
                    {
                        markerColor = color;
                    }
                }

                // Get cell type and bio condition for tooltip
                string cellType = null;
                CellTypeScore predictionScore = null;
                if (predictions != null && predictions.TryGetValue(rawFiles[i], out var prediction))
                {
                    cellType = prediction.TopCellType;
                    predictionScore = prediction.TopScore;
                }

                string bioCondition = null;
                if (bioConditions != null && bioConditions.TryGetValue(rawFiles[i], out var condition))
                {
                    bioCondition = condition;
                }

                var ellipse = new Ellipse
                {
                    Width = MarkerSize,
                    Height = MarkerSize,
                    Fill = new SolidColorBrush(markerColor),
                    Stroke = new SolidColorBrush(Color.FromRgb(50, 50, 50)),
                    StrokeThickness = 1,
                    Opacity = 1.0
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
                    IsSelected = true,
                    PredictedCellType = cellType,
                    PredictionScore = predictionScore,
                    BiologicalCondition = bioCondition,
                    PlateName = plateName
                });
            }

            return dataPoints;
        }
    

        private List<DataPoint> DrawDataPointsWithBioConditions(Canvas canvas, List<string> rawFiles, List<int> peptideCounts,
            List<double> ticValues, List<int> proteinCounts, List<double> trypsinRatios,
            double canvasWidth, double canvasHeight, Dictionary<string, string> bioConditions,
            Dictionary<string, Color> colorMap, Dictionary<string, CellTypePredictionResult> predictions)
        {
            var dataPoints = new List<DataPoint>();
            const double MarkerSize = 10;
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
                    StrokeThickness = 1,
                    Opacity = 1.0
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
                    IsSelected = true,
                    PredictedCellType = cellType,
                    PredictionScore = predictionScore,
                    BiologicalCondition = condition
                });
            }

            return dataPoints;
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
                bool isSelected = _selectionManager.IsPointInSelection(testPoint);

                point.IsSelected = isSelected;
                if (isSelected)
                {
                    selectedPoints.Add(point);
                }

                ApplySelectionStyling(point, isSelected);
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
            // Reset all points to normal size (don't change fills - preserve selection styling)
            foreach (var p in _dataPoints)
            {
                p.Visual.Width = 8;
                p.Visual.Height = 8;
                Canvas.SetLeft(p.Visual, p.XScreen - 4);
                Canvas.SetTop(p.Visual, p.YScreen - 4);
            }

            // Enlarge hovered point (don't change fill - preserve selection styling)
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

            if (!string.IsNullOrEmpty(point.PlateName))
            {
                tooltipText += $"\nPlate: {point.PlateName}";
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
            // Reset sizes only (don't change fills - preserve selection styling)
            foreach (var point in _dataPoints)
            {
                point.Visual.Width = 10;
                point.Visual.Height = 10;
                Canvas.SetLeft(point.Visual, point.XScreen - 5);
                Canvas.SetTop(point.Visual, point.YScreen - 5);
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

        /// <summary>
        /// Returns true if there is an active polygon (lasso) selection
        /// </summary>
        public bool HasPolygonSelection()
        {
            return _selectionManager.PolygonPointsScreen.Count >= 3;
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