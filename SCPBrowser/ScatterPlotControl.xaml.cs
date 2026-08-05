using PCANipals;
using SCPBrowser.GOTools;
using SCPBrowser.Models;
using SCPBrowser.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Microsoft.Win32;
using UMAP;

namespace SCPBrowser
{
    /// <summary>
    /// Seeded random generator for UMAP reproducibility.
    /// </summary>
    internal sealed class SeededRandom : IProvideRandomValues
    {
        private readonly Random _rng;
        public SeededRandom(int seed) => _rng = new Random(seed);
        public SeededRandom() => _rng = new Random();
        public bool IsThreadSafe => false;
        public float NextFloat() => (float)_rng.NextDouble();
        public void NextFloats(Span<float> buffer)
        {
            for (int i = 0; i < buffer.Length; i++)
                buffer[i] = (float)_rng.NextDouble();
        }
        public int Next(int minValue, int maxValue) => _rng.Next(minValue, maxValue);
    }

    /// <summary>
    /// Inputs for one plot render. Every collection here is genuinely OPTIONAL — a caller supplies only the maps
    /// for the colouring mode it wants, and the render path null-checks each one — so they are annotated nullable.
    /// That is the accurate contract, not a suppression: declaring them non-nullable while every call site passes
    /// null for most of them is what produced the warnings.
    /// </summary>
    public class ScatterPlotOptions
    {
        // Plate coloring
        public bool UsePlateColoring { get; set; } = false;
        public Dictionary<string, string>? PlatePerFile { get; set; }
        public Dictionary<string, Color>? PlateColorMap { get; set; }
        public bool UsePcaView { get; set; } = false;
        public bool UseUmapView { get; set; } = false;
        public bool UseLogLog { get; set; } = true;
        public bool UseCellTypeColoring { get; set; } = false;
        public Dictionary<string, CellTypePredictionResult>? CellTypePredictions { get; set; }
        public Dictionary<string, Color>? CellTypeColorMap { get; set; }
        public Dictionary<string, RunGoEnrichmentResult>? GoEnrichmentResults { get; set; }
        public Dictionary<string, Color>? GoTermColorMap { get; set; }

        public bool UseBioConditionColoring { get; set; } = false;
        public Dictionary<string, string>? BioConditionPerFile { get; set; }
        public Dictionary<string, Color>? BioConditionColorMap { get; set; }

        // Checkbox filter sets for persistent selection
        public HashSet<string>? CheckedCellTypes { get; set; }
        public HashSet<string>? CheckedBioConditions { get; set; }
        public HashSet<string>? CheckedPlates { get; set; }

        // HVP for dimensionality reduction
        public List<HvpResult>? HvpResults { get; set; }

        // Batch effect correction
        public bool ApplyBatchCorrection { get; set; } = false;

        public Dictionary<string, int>? BatchLabelPerFile { get; set; }

        // Dimensionality reduction settings
        public DimensionReductionSettings? DimRedSettings { get; set; }

        // Runs excluded by contaminant ratio cutoff (greyed in Explorer, excluded from PCA/UMAP)
        public HashSet<string>? ContaminantRatioExcludedRuns { get; set; }
        public double ContaminantRatioCutoff { get; set; } = 1.0;
    }

    public class PlotSelectionChangedEventArgs : EventArgs
    {
        public List<DataPoint> SelectedPoints { get; set; } = new();
    }

    public class PointInteractionEventArgs : EventArgs
    {
        public DataPoint? DataPoint { get; set; }
    }

    public partial class ScatterPlotControl : UserControl
    {
        private ProteomicsData _currentData;
        private ScatterPlotOptions _currentOptions;
        private List<DataPoint> _dataPoints;
        private PlotRenderer _plotRenderer;
        private PcaResult _pcaResult;
        private List<string> _pcaProteinNames;
        private float[][] _umapResult;
        private double? _labelPurity;
        private List<string> _dimRedRawFiles;
        private SelectionManager _selectionManager;
        private bool _isRefreshing = false;
        private DimensionReductionSettings? _previousDimRedSettings;
        private const double HoverTolerance = 12;
        private bool _suppressSelectionEvents = false;

        // Generation counter for deferred SelectionChanged events.
        // Incremented at the start of each UpdatePlot call. Deferred closures capture
        // the current value; if _updateGeneration has changed by the time the deferred
        // call executes, it means a newer UpdatePlot superseded it and the stale call
        // is skipped. This prevents rapid resize from firing outdated events.
        private int _updateGeneration;

        public event EventHandler<PlotSelectionChangedEventArgs> SelectionChanged;
        public event EventHandler<PointInteractionEventArgs> PointHovered;
        public double? LabelPurity => _labelPurity;
        public IReadOnlyList<DataPoint> DataPoints => _dataPoints;
        public bool NeedsPcaCompute => _pcaResult == null;
        public bool NeedsUmapCompute => _umapResult == null;
        public bool PreviousApplyBatchCorrection => _previousApplyBatchCorrection;
        public DimensionReductionSettings? PreviousDimRedSettings => _previousDimRedSettings;
        public ProteomicsData CurrentData => _currentData;

        /// <summary>
        /// Force-clears PCA/UMAP caches so the next UpdatePlot recomputes from scratch.
        /// </summary>
        public void InvalidateCaches()
        {
            _pcaResult = null;
            _pcaProteinNames = null;
            _umapResult = null;
            _labelPurity = null;
            _dimRedRawFiles = null;
        }
        private bool _previousApplyBatchCorrection = false;
        private HashSet<string> _hvpProteinIds;

        /// <summary>
        /// Fraction of the last embedding's matrix that was NOT measured and had to be filled in. Surfaced so the
        /// user can see how much of the picture is imputed rather than observed - at 60% missingness the embedding
        /// is substantially a statement about detection, not abundance.
        /// </summary>
        private double _missingRate;
        public double LastMissingRate => _missingRate;

        /// <summary>Cells left out of the last embedding because their population was unchecked AND grey dots are hidden.</summary>
        private int _excludedByPopulationFilter;
        public int LastExcludedByPopulationFilter => _excludedByPopulationFilter;

        /// <summary>Signature of the cohort the last embedding was computed over; a change forces a recompute.</summary>
        private string _previousCohortKey;

        /// <summary>True when the last attempt had too few cells to embed (fewer than 3).</summary>
        private bool _cohortTooSmall;
        public bool LastCohortTooSmall => _cohortTooSmall;

        /// <summary>
        /// Identifies WHICH cells will enter the embedding.
        ///
        /// While grey dots are shown, unchecked populations are still on screen and still legitimately define the
        /// axes, so the cohort is simply "everything" and toggling a legend checkbox must NOT trigger an expensive
        /// recompute. Once grey dots are hidden those cells are gone from the figure, so the checked sets become
        /// part of the cohort's identity and any change to them has to invalidate the embedding.
        /// </summary>
        /// <summary>
        /// Whether these options would change the set of cells entering the embedding, and therefore force a
        /// recompute. The caller needs this BEFORE calling UpdatePlot so it can put the work on a background
        /// thread behind the wait screen - otherwise the recompute runs on the UI thread and the window freezes.
        /// </summary>
        public bool WillCohortChange(ScatterPlotOptions options)
            => !string.Equals(CohortKey(options), _previousCohortKey, StringComparison.Ordinal);

        private string CohortKey(ScatterPlotOptions options)
        {
            if (!_hideUnselected) return "ALL";

            string Join(HashSet<string> s) =>
                s == null || s.Count == 0 ? "-" : string.Join("", s.OrderBy(x => x, StringComparer.Ordinal));

            return "HIDE|" + Join(options?.CheckedCellTypes)
                 + "|" + Join(options?.CheckedBioConditions)
                 + "|" + Join(options?.CheckedPlates);
        }

        /// <summary>
        /// Whether a run survives the legend's checked-population filters, using EXACTLY the rule
        /// UpdateSelectionWithFilters applies when it decides what to grey: a run with no value for a category
        /// always passes that category; a run WITH a value passes only if that value is checked. Sharing the rule
        /// is the point - if the embedding dropped a different set than the display greys, the two would disagree.
        /// </summary>
        private bool PassesCheckedPopulationFilters(string rawFile, ProteomicsData data)
        {
            var o = _currentOptions;
            if (o == null) return true;

            // Cell type
            string cellType = null;
            if (o.CellTypePredictions != null && o.CellTypePredictions.TryGetValue(rawFile, out var pred))
                cellType = pred?.TopCellType;
            if (!string.IsNullOrEmpty(cellType))
            {
                if (o.CheckedCellTypes == null || o.CheckedCellTypes.Count == 0 || !o.CheckedCellTypes.Contains(cellType))
                    return false;
            }

            // Biological condition
            string condition = null;
            if (data?.BiologicalConditionPerFile != null)
                data.BiologicalConditionPerFile.TryGetValue(rawFile, out condition);
            if (!string.IsNullOrEmpty(condition))
            {
                if (o.CheckedBioConditions == null || o.CheckedBioConditions.Count == 0 || !o.CheckedBioConditions.Contains(condition))
                    return false;
            }

            // Plate
            string plate = null;
            if (o.PlatePerFile != null)
                o.PlatePerFile.TryGetValue(rawFile, out plate);
            if (!string.IsNullOrEmpty(plate))
            {
                if (o.CheckedPlates == null || o.CheckedPlates.Count == 0 || !o.CheckedPlates.Contains(plate))
                    return false;
            }

            return true;
        }
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
                point.Visual.Visibility = Visibility.Visible;
            }
            else
            {
                point.Visual.Fill = new SolidColorBrush(UnselectedGray);
                point.Visual.Stroke = new SolidColorBrush(UnselectedGray);
                point.Visual.StrokeThickness = 1;
                point.Visual.Opacity = UnselectedOpacity;
                point.Visual.Visibility = _hideUnselected ? Visibility.Collapsed : Visibility.Visible;
            }
        }

        /// <summary>
        /// Recolors the already-rendered points by k-means cluster. Because clustering is computed on the *displayed*
        /// coordinates, this runs AFTER UpdatePlot (when XData/YData and the per-point selection state already exist):
        /// it stamps each point's ClusterId/ClusterLabel and BaseColor, then re-applies the existing selection styling
        /// so excluded/greyed points stay grey while included points show their cluster colour.
        /// </summary>
        public void ApplyClusterColors(
            IReadOnlyDictionary<string, int> runToCluster,
            IReadOnlyDictionary<int, Color> clusterColors)
        {
            if (_dataPoints == null) return;

            foreach (var point in _dataPoints)
            {
                if (point.RunName != null && runToCluster != null &&
                    runToCluster.TryGetValue(point.RunName, out int cid) && cid >= 0)
                {
                    point.ClusterId = cid;
                    point.ClusterLabel = "Cluster " + (cid + 1);
                    if (clusterColors != null && clusterColors.TryGetValue(cid, out var color))
                        point.BaseColor = color;
                }
                else
                {
                    point.ClusterId = -1;
                    point.ClusterLabel = null;
                }

                // Repaint with the new base colour, preserving the current include/grey state.
                ApplySelectionStyling(point, point.IsSelected);
            }
        }

        /// <summary>
        /// Centralized gate for firing the SelectionChanged event.
        /// All code paths that need to notify listeners about selection changes MUST
        /// go through this method instead of invoking SelectionChanged directly.
        /// This ensures that events are suppressed during UpdatePlot (when
        /// _suppressSelectionEvents is true), preventing the PeptideTicControl handler
        /// from receiving partial/unstable state mid-update.
        ///
        /// CODEMERGER NOTE: If you add a new code path that fires SelectionChanged,
        /// use RaiseSelectionChanged instead of SelectionChanged?.Invoke directly.
        /// </summary>
        private void RaiseSelectionChanged(List<DataPoint> selectedPoints)
        {
            if (_suppressSelectionEvents)
            {

                return;
            }


            SelectionChanged?.Invoke(this, new PlotSelectionChangedEventArgs { SelectedPoints = selectedPoints });
        }

        private bool _hideUnselected = false;

        /// <summary>
        /// Shows or hides unselected (grey) data points.
        /// </summary>
        public void SetHideUnselected(bool hide)
        {
            _hideUnselected = hide;
            foreach (var point in _dataPoints)
            {
                if (point?.Visual == null) continue;
                bool isSelected = point.IsSelected;
                if (!isSelected)
                {
                    point.Visual.Visibility = hide ? Visibility.Collapsed : Visibility.Visible;
                }
            }
        }

        /// <summary>
        /// Runs PCA/UMAP computation on a background thread so the UI stays responsive.
        /// Call this before UpdatePlot when heavy compute is expected.
        /// </summary>
        public async Task PrecomputeIfNeededAsync(ProteomicsData data, ScatterPlotOptions options)
        {
            if (data == null || data.PeptideCountPerFile.Count == 0)
                return;

            // Mirror the cache invalidation logic from UpdatePlot
            HashSet<string> newHvpIds = null;
            if (options?.HvpResults != null && options.HvpResults.Count > 0)
            {
                newHvpIds = new HashSet<string>(
                    options.HvpResults
                        .Where(h => h.IsHighlyVariable)
                        .Select(h => h.ProteinId));
            }

            bool hvpChanged = !HvpSetsEqual(_hvpProteinIds, newHvpIds);
            bool batchCorrectionChanged = (options?.ApplyBatchCorrection ?? false) != _previousApplyBatchCorrection;
            var currentDimRedSettings = options?.DimRedSettings;
            bool dimRedSettingsChanged = currentDimRedSettings != null &&
                (currentDimRedSettings.DiffersFrom(_previousDimRedSettings));

            // Cohort change = the SET OF CELLS entering the embedding changed (grey dots hidden with a different
            // set of populations checked). That invalidates the embedding just as surely as a settings change.
            string cohortKey = CohortKey(options);
            bool cohortChanged = !string.Equals(cohortKey, _previousCohortKey, StringComparison.Ordinal);

            if (_currentData != data || hvpChanged || batchCorrectionChanged || dimRedSettingsChanged || cohortChanged)
            {
                _pcaResult = null;
                _pcaProteinNames = null;
                _umapResult = null;
                _labelPurity = null;
                _dimRedRawFiles = null;
            }

            // Update tracking fields so UpdatePlot won't re-invalidate
            _hvpProteinIds = newHvpIds;
            _previousApplyBatchCorrection = options?.ApplyBatchCorrection ?? false;
            _previousDimRedSettings = currentDimRedSettings?.Clone();
            _previousCohortKey = cohortKey;
            _currentData = data;
            _currentOptions = options;

            bool needsPca = (options.UsePcaView || options.UseUmapView) && _pcaResult == null;
            bool needsUmap = options.UseUmapView && _umapResult == null;

            if (needsPca || needsUmap)
            {
                await Task.Run(() =>
                {
                    if (needsPca)
                        ComputePca(data);
                    if (needsUmap)
                        ComputeUmap(data);
                });
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

            // Capture generation so deferred closures can detect staleness.
            // See _updateGeneration field comments for full explanation.
            int gen = ++_updateGeneration;

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

                // Clear PCA/UMAP if data changed OR if HVP set changed OR if batch correction toggled OR if dim red settings changed
                bool hvpChanged = !HvpSetsEqual(_hvpProteinIds, newHvpIds);
                bool batchCorrectionChanged = (options?.ApplyBatchCorrection ?? false) != _previousApplyBatchCorrection;
                var currentDimRedSettings = options?.DimRedSettings;
                bool dimRedSettingsChanged = currentDimRedSettings != null && 
                    (currentDimRedSettings.DiffersFrom(_previousDimRedSettings));

                string cohortKey = CohortKey(options);
                bool cohortChanged = !string.Equals(cohortKey, _previousCohortKey, StringComparison.Ordinal);

                if (_currentData != data || hvpChanged || batchCorrectionChanged || dimRedSettingsChanged || cohortChanged)
                {
                    _pcaResult = null;
                    _pcaProteinNames = null;
                    _umapResult = null;
                    _labelPurity = null;
                    _dimRedRawFiles = null;

                    if (hvpChanged)
                    {

                    }
                    if (batchCorrectionChanged)
                    {

                    }
                    if (dimRedSettingsChanged)
                    {

                    }
                }

                _hvpProteinIds = newHvpIds;
                _previousApplyBatchCorrection = options?.ApplyBatchCorrection ?? false;
                _previousDimRedSettings = currentDimRedSettings?.Clone();
                _previousCohortKey = cohortKey;

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

                // Apply checkbox filter styling synchronously (no flicker).
                // RaiseSelectionChanged is suppressed here (_suppressSelectionEvents = true),
                // so the PeptideTicControl handler won't receive partial state mid-update.

                if (options.CheckedCellTypes != null || options.CheckedBioConditions != null || options.CheckedPlates != null)
                {

                    UpdateSelectionWithFilters(
                        options.CheckedCellTypes ?? new HashSet<string>(),
                        options.CheckedBioConditions ?? new HashSet<string>(),
                        options.CheckedPlates ?? new HashSet<string>());
                    int greyCount = _dataPoints.Count(p => !p.IsSelected);

                }
                else
                {

                }

                // Schedule a deferred SelectionChanged event AFTER the finally block
                // has cleared _suppressSelectionEvents. This ensures every caller of
                // UpdatePlot (RefreshChart, SizeChanged, etc.) gets a single clean event
                // notification once WPF layout has settled.
                //
                // CODEMERGER NOTE: This deferred call is the ONLY place where
                // SelectionChanged fires after UpdatePlot. Do not add another
                // SelectionChanged trigger inside or after UpdatePlot; if you need
                // to change what fires, modify this block.
                if (_dataPoints.Count > 0)
                {
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        // Stale guard: if another UpdatePlot started since we scheduled
                        // this, our snapshot is outdated; skip.
                        if (gen != _updateGeneration)
                        {

                            return;
                        }

                        var selected = _dataPoints.Where(p => p.IsSelected).ToList();

                        RaiseSelectionChanged(selected);
                    }), System.Windows.Threading.DispatcherPriority.Loaded);
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

            private string _batchCorrectionWarning;
            /// <summary>Non-null after the last preprocessing when batch correction was skipped, fell back to
            /// plate-only, or failed — surfaced to the user by the host control. Null = clean correction (or none).</summary>
            public string BatchCorrectionWarning => _batchCorrectionWarning;

            /// <summary>Maps each run's biological condition to an integer covariate for ComBat to preserve, or null if
            /// conditions are unavailable, incomplete, or single-valued (nothing to protect → fall back to plate-only).</summary>
            private List<int> BuildConditionCovariate(List<string> rawFiles)
            {
                var perFile = _currentOptions?.BioConditionPerFile;
                if (perFile == null) return null;

                var conds = new List<string>(rawFiles.Count);
                foreach (var rf in rawFiles)
                {
                    if (!perFile.TryGetValue(rf, out var c) || string.IsNullOrEmpty(c))
                        return null; // incomplete labels → don't risk a partial covariate
                    conds.Add(c);
                }

                var distinct = conds.Distinct().ToList();
                if (distinct.Count < 2) return null; // only one condition → nothing to protect

                var idx = new Dictionary<string, int>();
                for (int i = 0; i < distinct.Count; i++) idx[distinct[i]] = i;
                return conds.Select(c => idx[c]).ToList();
            }

            /// <summary>
            /// Builds the preprocessed matrix for dimensionality reduction.
            /// Pipeline: select proteins (HVP or all) → log2 transform → ComBat → z-score scaling → clip.
            /// </summary>
            private double[,] BuildPreprocessedMatrix(
                ProteomicsData data,
                DimensionReductionSettings settings,
                out List<string> proteins,
                out List<string> rawFiles)
            {
                    _batchCorrectionWarning = null; // cleared each run; set below if correction is skipped/falls back/fails
                    rawFiles = data.RawFileNames.ToList();

                    // Exclude contaminant-ratio-cutoff runs from dimensionality reduction
                    var excluded = _currentOptions?.ContaminantRatioExcludedRuns;
                    if (excluded != null && excluded.Count > 0)
                    {
                        rawFiles = rawFiles.Where(rf => !excluded.Contains(rf)).ToList();

                    }

                    // "Hide Grey Dots" means the unchecked populations are GONE, not merely dimmed - so they must
                    // not shape the embedding either. While they are only greyed they remain part of the dataset
                    // being displayed, and leaving them in the matrix is the honest choice: the axes are defined
                    // by everything on screen. Once the user hides them, continuing to let them define the
                    // variance structure, the HVP ranking, the per-protein z-scores and the UMAP neighbour graph
                    // would place the surviving cells in a space partly built from data the user has removed.
                    _excludedByPopulationFilter = 0;
                    if (_hideUnselected)
                    {
                        int beforeChecked = rawFiles.Count;
                        rawFiles = rawFiles.Where(rf => PassesCheckedPopulationFilters(rf, data)).ToList();
                        _excludedByPopulationFilter = beforeChecked - rawFiles.Count;
                    }

                    // Determine which proteins to use - HVPs if available, otherwise all
                if (_hvpProteinIds != null && _hvpProteinIds.Count > 0)
                {
                    proteins = data.ProteinQuantMatrix.Keys
                        .Where(p => _hvpProteinIds.Contains(p))
                        .ToList();

                }
                else
                {
                    proteins = data.ProteinQuantMatrix.Keys.ToList();

                }

                int nSamples = rawFiles.Count;
                int nProteins = proteins.Count;

                // Build matrix: rows = samples, columns = proteins (log2 transformed).
                // `observed` tracks which entries are real measurements, because in log2 space a written 0 is
                // indistinguishable from a genuine abundance and must not be treated as one.
                double[,] matrix = new double[nSamples, nProteins];
                bool[,] observed = new bool[nSamples, nProteins];

                // Optional per-cell scaling BEFORE the log: equalises how much material each cell contributed, so
                // that input amount / cell size / depth does not masquerade as biology in the embedding.
                var cellScale = new double[nSamples];
                for (int i = 0; i < nSamples; i++) cellScale[i] = 1.0;
                if (settings?.Normalization == Models.CellNormalization.TotalIntensity)
                {
                    var totals = new double[nSamples];
                    for (int i = 0; i < nSamples; i++)
                        for (int j = 0; j < nProteins; j++)
                            if (data.ProteinQuantMatrix[proteins[j]].TryGetValue(rawFiles[i], out double v) && v > 0)
                                totals[i] += v;
                    var positive = totals.Where(t => t > 0).ToList();
                    double reference = positive.Count > 0 ? positive.Average() : 0;
                    for (int i = 0; i < nSamples; i++)
                        cellScale[i] = (totals[i] > 0 && reference > 0) ? reference / totals[i] : 1.0;
                }

                for (int i = 0; i < nSamples; i++)
                {
                    string rawFile = rawFiles[i];
                    for (int j = 0; j < nProteins; j++)
                    {
                        string protein = proteins[j];
                        if (data.ProteinQuantMatrix[protein].TryGetValue(rawFile, out double value) && value > 0)
                        {
                            matrix[i, j] = Math.Log2(value * cellScale[i] + 1);
                            observed[i, j] = true;
                        }
                        else
                        {
                            matrix[i, j] = 0;      // provisional; replaced below unless MissingValues == Zero
                        }
                    }
                }

                // Median-centre each cell over its OBSERVED proteins. Robust to missingness, and the standard
                // single-cell choice: it removes the per-cell offset without letting undetected proteins vote.
                if (settings?.Normalization == Models.CellNormalization.MedianCentre)
                {
                    for (int i = 0; i < nSamples; i++)
                    {
                        var vals = new List<double>(nProteins);
                        for (int j = 0; j < nProteins; j++) if (observed[i, j]) vals.Add(matrix[i, j]);
                        if (vals.Count == 0) continue;
                        vals.Sort();
                        double median = vals.Count % 2 == 1
                            ? vals[vals.Count / 2]
                            : (vals[vals.Count / 2 - 1] + vals[vals.Count / 2]) / 2.0;
                        for (int j = 0; j < nProteins; j++) if (observed[i, j]) matrix[i, j] -= median;
                    }
                }

                // Fill unobserved entries. Writing 0 makes "not measured" look like a measured low abundance, which
                // with 40-70% missingness drags each protein's centre down and inflates its variance - so the
                // default substitutes the protein's mean over the cells where it WAS seen, leaving that centre
                // unchanged. Zero remains available to reproduce analyses made before this option existed.
                _missingRate = 0;
                int missingCount = 0;
                for (int i = 0; i < nSamples; i++)
                    for (int j = 0; j < nProteins; j++)
                        if (!observed[i, j]) missingCount++;
                if (nSamples > 0 && nProteins > 0)
                    _missingRate = (double)missingCount / (nSamples * nProteins);

                if (settings?.MissingValues == Models.MissingValueMode.ProteinMean)
                {
                    for (int j = 0; j < nProteins; j++)
                    {
                        double sum = 0; int n = 0;
                        for (int i = 0; i < nSamples; i++) if (observed[i, j]) { sum += matrix[i, j]; n++; }
                        if (n == 0) continue;                    // never observed: leave at 0 for every cell
                        double mean = sum / n;
                        for (int i = 0; i < nSamples; i++) if (!observed[i, j]) matrix[i, j] = mean;
                    }
                }

                // Apply batch correction if enabled. ComBat removes plate (batch) effects; we pass the biological
                // condition as a covariate so real condition differences are preserved, and fall back to plate-only
                // (with a warning) when condition can't be separated from plate.
                if (_currentOptions?.ApplyBatchCorrection == true && _currentOptions.BatchLabelPerFile != null)
                {
                    var batchLabels = rawFiles
                        .Select(rf => _currentOptions.BatchLabelPerFile.TryGetValue(rf, out int plateId) ? plateId : 0)
                        .ToList();

                    if (batchLabels.Distinct().Count() >= 2)
                    {
                        var combat = new ComBatService();
                        var covariate = BuildConditionCovariate(rawFiles); // biological condition, or null if unusable

                        var result = covariate != null
                            ? combat.Apply(matrix, batchLabels, covariate)
                            : combat.Apply(matrix, batchLabels);

                        if (result.Success)
                        {
                            matrix = result.CorrectedData;
                            if (covariate == null)
                                _batchCorrectionWarning = "Batch correction: no biological condition set — corrected for plate only (biology not protected).";
                        }
                        else if (covariate != null)
                        {
                            // Condition is confounded with plate (singular design) → still remove plate effects, but warn.
                            var batchOnly = combat.Apply(matrix, batchLabels);
                            if (batchOnly.Success)
                            {
                                matrix = batchOnly.CorrectedData;
                                _batchCorrectionWarning = "Batch correction: biological condition is confounded with plate — corrected for plate only; condition differences that align with plates may be removed.";
                            }
                            else
                            {
                                _batchCorrectionWarning = "Batch correction failed (" + batchOnly.ErrorMessage + ") — showing uncorrected data.";
                            }
                        }
                        else
                        {
                            _batchCorrectionWarning = "Batch correction failed (" + result.ErrorMessage + ") — showing uncorrected data.";
                        }
                    }
                }

                // Z-score scaling per protein column (if enabled)
                if (settings != null && settings.ZScoreScale)
                {
                    double clipMax = settings.ClipMaxValue;
                    for (int j = 0; j < nProteins; j++)
                    {
                        // Compute mean
                        double sum = 0;
                        for (int i = 0; i < nSamples; i++)
                            sum += matrix[i, j];
                        double mean = sum / nSamples;

                        // Compute std dev
                        double sumSq = 0;
                        for (int i = 0; i < nSamples; i++)
                        {
                            double diff = matrix[i, j] - mean;
                            sumSq += diff * diff;
                        }
                        double std = Math.Sqrt(sumSq / nSamples);

                        // Scale and clip
                        if (std > 1e-12)
                        {
                            for (int i = 0; i < nSamples; i++)
                            {
                                double scaled = (matrix[i, j] - mean) / std;
                                matrix[i, j] = Math.Clamp(scaled, -clipMax, clipMax);
                            }
                        }
                        else
                        {
                            // Zero variance — set to 0
                            for (int i = 0; i < nSamples; i++)
                                matrix[i, j] = 0;
                        }
                    }

                }

                return matrix;
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
                    var settings = _currentOptions?.DimRedSettings ?? DimensionReductionSettings.CreateDefaults();

                    // Ensure PCA is computed first — UMAP runs on PCA coordinates
                    if (_pcaResult == null)
                    {

                        ComputePca(data);
                    }

                    if (_pcaResult == null)
                    {

                        _umapResult = null;
                        return;
                    }

                    int nSamples = _pcaResult.Scores.GetLength(0);
                    int availablePCs = _pcaResult.Scores.GetLength(1);
                    int nPcsToUse = Math.Min(settings.NumPcsForUmap, availablePCs);

                    if (nSamples < 15)
                    {

                        _umapResult = null;
                        return;
                    }



                    // Build UMAP input from PCA scores
                    int nDimensions = nPcsToUse;

                    // Guided embedding: append weighted one-hot cell type columns
                    List<string> cellTypeLabels = null;
                    string[] uniqueTypes = null;
                    if (settings.UseGuidedEmbedding &&
                        _currentOptions?.CellTypePredictions != null &&
                        _currentOptions.CellTypePredictions.Count > 0)
                    {
                        var rawFiles = _dimRedRawFiles ?? data.RawFileNames.ToList();
                        cellTypeLabels = new List<string>(nSamples);
                        for (int i = 0; i < nSamples; i++)
                        {
                            string label = null;
                            if (_currentOptions.CellTypePredictions.TryGetValue(rawFiles[i], out var pred))
                                label = pred.TopCellType;
                            cellTypeLabels.Add(label);
                        }

                        uniqueTypes = cellTypeLabels
                            .Where(l => !string.IsNullOrEmpty(l))
                            .Distinct()
                            .OrderBy(t => t)
                            .ToArray();

                        if (uniqueTypes.Length >= 2)
                        {
                            nDimensions += uniqueTypes.Length;

                        }
                        else
                        {
                            cellTypeLabels = null;

                        }
                    }

                    // Build float[][] matrix: PCA scores + optional one-hot
                    float[][] umapMatrix = new float[nSamples][];
                    for (int i = 0; i < nSamples; i++)
                    {
                        umapMatrix[i] = new float[nDimensions];

                        // PCA scores
                        for (int j = 0; j < nPcsToUse; j++)
                            umapMatrix[i][j] = (float)_pcaResult.Scores[i, j];

                        // Weighted one-hot cell type columns
                        if (cellTypeLabels != null && uniqueTypes != null)
                        {
                            string label = cellTypeLabels[i];
                            if (!string.IsNullOrEmpty(label))
                            {
                                int typeIdx = Array.IndexOf(uniqueTypes, label);
                                if (typeIdx >= 0)
                                    umapMatrix[i][nPcsToUse + typeIdx] = (float)settings.GuidedWeight;
                            }
                            // Unlabeled samples get zeros (neutral)
                        }
                    }

                    // Run UMAP
                    int neighbors = Math.Min(settings.UmapNeighbors, nSamples - 1);
                    var umap = new Umap(
                        distance: Umap.DistanceFunctions.Euclidean,
                        dimensions: 2,
                        numberOfNeighbors: neighbors,
                        random: settings.UmapSeed > 0 ? new SeededRandom(settings.UmapSeed) : new SeededRandom()
                    );

                    int epochs = umap.InitializeFit(umapMatrix);
                    for (int i = 0; i < epochs; i++)
                        umap.Step();

                        _umapResult = umap.GetEmbedding();

                            // Compute label purity in unsupervised PCA space
                            _labelPurity = null;
                            if (_currentOptions?.CellTypePredictions != null && _currentOptions.CellTypePredictions.Count > 0)
                            {
                                var rawFiles = _dimRedRawFiles ?? data.RawFileNames.ToList();
                                _labelPurity = ComputeLabelPurity(rawFiles, nPcsToUse, neighbors);
                            }
                        }
                        catch (Exception)
                        {

                            _umapResult = null;
                            _labelPurity = null;
                        }
                        }

                    /// <summary>
                    /// Computes kNN label purity in unsupervised PCA space.
                    /// For each labeled sample, checks how many of its k nearest neighbors (in PCA space)
                    /// share the same cell type label. Returns average purity [0..1].
                    /// This measures whether labels are consistent with data structure, independent of UMAP.
                    /// </summary>
                    private double? ComputeLabelPurity(List<string> rawFiles, int nPcs, int k)
                    {
                        try
                        {
                            if (_pcaResult == null || _currentOptions?.CellTypePredictions == null)
                                return null;

                            int nSamples = _pcaResult.Scores.GetLength(0);
                            int availablePCs = _pcaResult.Scores.GetLength(1);
                            int nPcsToUse = Math.Min(nPcs, availablePCs);

                            // Build label array
                            string[] labels = new string[nSamples];
                            int labeledCount = 0;
                            for (int i = 0; i < nSamples; i++)
                            {
                                if (_currentOptions.CellTypePredictions.TryGetValue(rawFiles[i], out var pred)
                                    && !string.IsNullOrEmpty(pred.TopCellType))
                                {
                                    labels[i] = pred.TopCellType;
                                    labeledCount++;
                                }
                            }

                            if (labeledCount < k + 1)
                            {

                                return null;
                            }

                            // Compute pairwise squared Euclidean distances in PCA space
                            int kActual = Math.Min(k, labeledCount - 1);
                            double totalPurity = 0;
                            int evaluated = 0;

                            for (int i = 0; i < nSamples; i++)
                            {
                                if (labels[i] == null) continue;

                                // Compute distances from i to all other labeled samples
                                var distances = new List<(int idx, double dist)>();
                                for (int j = 0; j < nSamples; j++)
                                {
                                    if (i == j || labels[j] == null) continue;
                                    double dist = 0;
                                    for (int p = 0; p < nPcsToUse; p++)
                                    {
                                        double d = _pcaResult.Scores[i, p] - _pcaResult.Scores[j, p];
                                        dist += d * d;
                                    }
                                    distances.Add((j, dist));
                                }

                                // Get k nearest neighbors
                                var neighbors = distances.OrderBy(d => d.dist).Take(kActual).ToList();
                                int sameLabel = neighbors.Count(n => labels[n.idx] == labels[i]);
                                totalPurity += (double)sameLabel / kActual;
                                evaluated++;
                            }

                            double purity = evaluated > 0 ? totalPurity / evaluated : 0;

                            return purity;
                        }
                        catch (Exception)
                        {

                            return null;
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

            // Pre-compute max contaminant ratio for Viridis gradient (default coloring)
            double maxContaminantRatio = rawFiles
                .Select(rf => data.TargetProteinRatioPerFile.TryGetValue(rf, out double r) ? r : 0)
                .DefaultIfEmpty(0)
                .Max();
            if (maxContaminantRatio < 0.01) maxContaminantRatio = 0.05;

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
                else
                {
                    // Default: Color by Contaminant Ratio (Viridis gradient)
                    double ratio = data.TargetProteinRatioPerFile.TryGetValue(rawFiles[i], out double r) ? r : 0;
                    double normalizedRatio = ratio / maxContaminantRatio;
                    markerColor = ColorMapper.GetViridisColor(normalizedRatio);
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
                data.TargetProteinRatioPerFile.TryGetValue(rawFiles[i], out double contaminantRatio);

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
                    ContaminantRatio = contaminantRatio,
                    Visual = ellipse,
                    BaseColor = markerColor,
                    PredictedCellType = cellType,
                    PredictionScore = predictionScore,
                    BiologicalCondition = bioCondition,
                    PlateName = plateName,
                    FullPrediction = options.CellTypePredictions?.GetValueOrDefault(rawFiles[i])
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
                var settings = _currentOptions?.DimRedSettings ?? DimensionReductionSettings.CreateDefaults();
                var matrix = BuildPreprocessedMatrix(data, settings, out var proteins, out var rawFiles);

                _dimRedRawFiles = rawFiles;
                int nSamples = rawFiles.Count;
                int nProteins = proteins.Count;

                if (nSamples < 3 || nProteins < 2)
                {
                    // Too little left to embed. Record why so the header can say so - returning silently here
                    // leaves the previous picture on screen and the user cannot tell the difference between
                    // "nothing changed" and "this could not be computed".
                    _cohortTooSmall = nSamples < 3;
                    _pcaResult = null;
                    _pcaProteinNames = null;
                    return;
                }
                _cohortTooSmall = false;

                // Run NIPALS PCA - compute N components (capped by matrix dimensions)
                int maxComponents = Math.Min(settings.NumPcaComponents, Math.Min(nSamples - 1, nProteins - 1));


                _pcaResult = NipalsAlgorithm.Compute(
                    matrix,
                    nComponents: maxComponents,
                    maxIterations: 500,
                    tolerance: 1e-9,
                    center: true,
                    scale: false);

                _pcaProteinNames = proteins;
            }
            catch (Exception)
            {

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
            bool hasContaminantExclusions = _currentOptions?.ContaminantRatioExcludedRuns != null &&
                _currentOptions.ContaminantRatioExcludedRuns.Count > 0;

            bool hasNoFilters = (checkedCellTypes == null || checkedCellTypes.Count == 0) &&
                               (checkedBioConditions == null || checkedBioConditions.Count == 0) &&
                               (checkedPlates == null || checkedPlates.Count == 0) &&
                               _selectionManager.PolygonPointsScreen.Count < 3 &&
                               !hasContaminantExclusions;



            if (hasNoFilters && !userInteraction)
            {

                return;
            }

            var selectedPoints = new List<DataPoint>();
            bool hasPolygonSelection = _selectionManager.PolygonPointsScreen.Count >= 3;
            double cutoffPercent = (_currentOptions?.ContaminantRatioCutoff ?? 1.0) * 100;

            foreach (var point in _dataPoints)
            {
                // Reset exclusion state
                point.ExclusionReasons = ExclusionReason.None;
                var details = new List<string>();

                // Contaminant ratio check
                bool isContaminantExcluded = _currentOptions?.ContaminantRatioExcludedRuns != null &&
                    _currentOptions.ContaminantRatioExcludedRuns.Contains(point.RunName);
                if (isContaminantExcluded)
                {
                    point.ExclusionReasons |= ExclusionReason.ContaminantRatio;
                    details.Add($"Contaminant ratio {point.ContaminantRatio * 100:F1}% > {cutoffPercent:F0}% cutoff");
                }

                // Lasso check
                bool isInPolygon = false;
                if (hasPolygonSelection)
                {
                    Point testPoint = new Point(point.XScreen, point.YScreen);
                    isInPolygon = _selectionManager.IsPointInSelection(testPoint);
                    if (!isInPolygon)
                    {
                        point.ExclusionReasons |= ExclusionReason.LassoExcluded;
                        details.Add("Outside lasso selection");
                    }
                }

                // Cell type check
                bool hasCellTypeData = !string.IsNullOrEmpty(point.PredictedCellType);
                bool matchesCellType = checkedCellTypes != null && checkedCellTypes.Count > 0 &&
                    hasCellTypeData && checkedCellTypes.Contains(point.PredictedCellType);
                bool passesCellTypeFilter = !hasCellTypeData || matchesCellType;
                if (!passesCellTypeFilter)
                {
                    point.ExclusionReasons |= ExclusionReason.UncheckedCellType;
                    details.Add($"Unchecked cell type: {point.PredictedCellType}");
                }

                // Bio condition check
                bool hasBioConditionData = !string.IsNullOrEmpty(point.BiologicalCondition);
                bool matchesBioCondition = checkedBioConditions != null && checkedBioConditions.Count > 0 &&
                    hasBioConditionData && checkedBioConditions.Contains(point.BiologicalCondition);
                bool passesBioConditionFilter = !hasBioConditionData || matchesBioCondition;
                if (!passesBioConditionFilter)
                {
                    point.ExclusionReasons |= ExclusionReason.UncheckedBioCondition;
                    details.Add($"Unchecked condition: {point.BiologicalCondition}");
                }

                // Plate check
                bool hasPlateData = !string.IsNullOrEmpty(point.PlateName);
                bool matchesPlate = checkedPlates != null && checkedPlates.Count > 0 &&
                    hasPlateData && checkedPlates.Contains(point.PlateName);
                bool passesPlateFilter = !hasPlateData || matchesPlate;
                if (!passesPlateFilter)
                {
                    point.ExclusionReasons |= ExclusionReason.UncheckedPlate;
                    details.Add($"Unchecked plate: {point.PlateName}");
                }

                // Build detail string
                point.ExclusionDetail = details.Count > 0 ? string.Join("; ", details) : null;

                // Determine selection: contaminant-excluded always unselected; otherwise AND all filters
                bool passesAllFilters = passesCellTypeFilter && passesBioConditionFilter && passesPlateFilter;
                bool shouldBeSelected = isContaminantExcluded ? false :
                    (hasPolygonSelection ? (isInPolygon && passesAllFilters) : passesAllFilters);

                point.IsSelected = shouldBeSelected;

                if (shouldBeSelected)
                {
                    selectedPoints.Add(point);
                }

                ApplySelectionStyling(point, shouldBeSelected);
            }

            // Use centralized gate so events are suppressed during UpdatePlot
            RaiseSelectionChanged(selectedPoints);
        }

        public void ClearSelection()
        {
            _selectionManager.ClearSelection();

            foreach (var point in _dataPoints)
            {
                point.IsSelected = false;
                ApplySelectionStyling(point, false);
            }

            RaiseSelectionChanged(new List<DataPoint>());
        }

        public void SelectAll()
        {
            _selectionManager.ClearSelection();

            foreach (var point in _dataPoints)
            {
                point.IsSelected = true;
                point.ExclusionReasons = ExclusionReason.None;
                point.ExclusionDetail = null;
                ApplySelectionStyling(point, true);
            }

            RaiseSelectionChanged(_dataPoints.ToList());
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
                var pcaRawFiles = _dimRedRawFiles ?? rawFiles;
                DrawPcaChart(data, options, pcaRawFiles, canvasWidth, canvasHeight);
                return;
            }

            // UMAP mode
            if (options.UseUmapView && _umapResult != null)
            {
                var umapRawFiles = _dimRedRawFiles ?? rawFiles;
                DrawUmapChart(data, options, umapRawFiles, canvasWidth, canvasHeight);
                return;
            }

            var peptideCounts = rawFiles.Select(rf => data.PeptideCountPerFile[rf]).ToList();
            var ticValues = rawFiles.Select(rf => data.TotalIonCurrentPerFile.ContainsKey(rf) ?
                data.TotalIonCurrentPerFile[rf] : 0).ToList();
            var proteinCounts = rawFiles.Select(rf => data.ProteinCountPerFile.ContainsKey(rf) ?
                data.ProteinCountPerFile[rf] : 0).ToList();
            var contaminantRatios = rawFiles.Select(rf => data.TargetProteinRatioPerFile.ContainsKey(rf) ?
                data.TargetProteinRatioPerFile[rf] : 0).ToList();

            _plotRenderer.IsGeneMatrix = data.IsGeneMatrix;
            _plotRenderer.CalculateAxisRanges(peptideCounts, ticValues, options.UseLogLog);
            _plotRenderer.DrawAxesAndGrid(PlotCanvas, canvasWidth, canvasHeight);

            if (options.UseCellTypeColoring && options.CellTypePredictions != null)
            {
                _dataPoints = DrawDataPointsWithCellTypes(PlotCanvas, rawFiles, peptideCounts, ticValues,
                    proteinCounts, contaminantRatios, canvasWidth, canvasHeight, options.CellTypePredictions,
                    options.CellTypeColorMap, options.BioConditionPerFile);
            }
            else if (options.UseBioConditionColoring && options.BioConditionPerFile != null)
            {
                _dataPoints = DrawDataPointsWithBioConditions(PlotCanvas, rawFiles, peptideCounts, ticValues,
                    proteinCounts, contaminantRatios, canvasWidth, canvasHeight, options.BioConditionPerFile,
                    options.BioConditionColorMap, options.CellTypePredictions);
            }
            else if (options.UsePlateColoring && options.PlatePerFile != null)
            {
                _dataPoints = DrawDataPointsWithPlates(PlotCanvas, rawFiles, peptideCounts, ticValues,
                    proteinCounts, contaminantRatios, canvasWidth, canvasHeight, options.PlatePerFile,
                    options.PlateColorMap, options.CellTypePredictions, options.BioConditionPerFile);
            }
            else // Default: Color by Contaminant Ratio
            {
                _dataPoints = _plotRenderer.DrawDataPoints(PlotCanvas, rawFiles, peptideCounts, ticValues,
                    proteinCounts, contaminantRatios, canvasWidth, canvasHeight);
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

            // Pre-compute max contaminant ratio for Viridis gradient (default coloring)
            double maxContaminantRatio = rawFiles
                .Select(rf => data.TargetProteinRatioPerFile.TryGetValue(rf, out double r) ? r : 0)
                .DefaultIfEmpty(0)
                .Max();
            if (maxContaminantRatio < 0.01) maxContaminantRatio = 0.05;

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
                else
                {
                    // Default: Color by Contaminant Ratio (Viridis gradient)
                    double ratio = data.TargetProteinRatioPerFile.TryGetValue(rawFiles[i], out double r) ? r : 0;
                    double normalizedRatio = ratio / maxContaminantRatio;
                    markerColor = ColorMapper.GetViridisColor(normalizedRatio);
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
                data.TargetProteinRatioPerFile.TryGetValue(rawFiles[i], out double contaminantRatio);

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
                    ContaminantRatio = contaminantRatio,
                    Visual = ellipse,
                    BaseColor = markerColor,
                    PredictedCellType = cellType,
                    PredictionScore = predictionScore,
                    BiologicalCondition = bioCondition,
                    PlateName = plateName,
                    FullPrediction = options.CellTypePredictions?.GetValueOrDefault(rawFiles[i])
                };

                _dataPoints.Add(dataPoint);
            }
        }



        private List<DataPoint> DrawDataPointsWithCellTypes(Canvas canvas, List<string> rawFiles, List<int> peptideCounts,
      List<double> ticValues, List<int> proteinCounts, List<double> contaminantRatios,
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
                    ContaminantRatio = contaminantRatios[i],
                    XScreen = screenPos.X,
                    YScreen = screenPos.Y,
                    Visual = ellipse,
                    BaseColor = markerColor,
                    IsSelected = true,
                    PredictedCellType = cellType,
                    PredictionScore = predictionScore,
                    BiologicalCondition = condition,
                    FullPrediction = predictions?.GetValueOrDefault(rawFiles[i])
                });
            }

            return dataPoints;
        }

        private List<DataPoint> DrawDataPointsWithPlates(Canvas canvas, List<string> rawFiles, List<int> peptideCounts,
    List<double> ticValues, List<int> proteinCounts, List<double> contaminantRatios,
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
                    ContaminantRatio = contaminantRatios[i],
                    XScreen = screenPos.X,
                    YScreen = screenPos.Y,
                    Visual = ellipse,
                    BaseColor = markerColor,
                    IsSelected = true,
                    PredictedCellType = cellType,
                    PredictionScore = predictionScore,
                    BiologicalCondition = bioCondition,
                    PlateName = plateName,
                    FullPrediction = predictions?.GetValueOrDefault(rawFiles[i])
                });
            }

            return dataPoints;
        }
    

        private List<DataPoint> DrawDataPointsWithBioConditions(Canvas canvas, List<string> rawFiles, List<int> peptideCounts,
            List<double> ticValues, List<int> proteinCounts, List<double> contaminantRatios,
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
                    ContaminantRatio = contaminantRatios[i],
                    XScreen = screenPos.X,
                    YScreen = screenPos.Y,
                    Visual = ellipse,
                    BaseColor = markerColor,
                    IsSelected = true,
                    PredictedCellType = cellType,
                    PredictionScore = predictionScore,
                    BiologicalCondition = condition,
                    FullPrediction = predictions?.GetValueOrDefault(rawFiles[i])
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

                return;
            }

            double canvasWidth = PlotCanvas.ActualWidth;
            double canvasHeight = PlotCanvas.ActualHeight;

            _selectionManager.RedrawSelectionFromDataCoordinates(
                dataPoint => _plotRenderer.DataToScreen(dataPoint.X, dataPoint.Y, canvasWidth, canvasHeight));


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

            // Centralized gate handles suppression check internally
            RaiseSelectionChanged(selectedPoints);
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

            // A gene matrix has no peptide/precursor dimension: PeptideCount is a substitute for the detected-gene
            // count and TIC is a summed-intensity proxy, so label them honestly (and drop the redundant duplicate).
            string tooltipText = (_currentData?.IsGeneMatrix == true)
                ? $"{point.RunName}\n" +
                  $"Proteins: {point.ProteinCount:N0}\n" +
                  $"Total Intensity: {point.TicValue:E2}\n" +
                  $"Contaminant Ratio: {point.ContaminantRatio * 100:F2}%"
                : $"{point.RunName}\n" +
                  $"Peptides: {point.PeptideCount:N0}\n" +
                  $"TIC: {point.TicValue:E2}\n" +
                  $"Protein Groups: {point.ProteinCount:N0}\n" +
                  $"Contaminant Ratio: {point.ContaminantRatio * 100:F2}%";

            if (!string.IsNullOrEmpty(point.BiologicalCondition))
            {
                tooltipText += $"\nCondition: {point.BiologicalCondition}";
            }

            if (!string.IsNullOrEmpty(point.PredictedCellType))
            {
                string confidenceStr = point.FullPrediction != null 
                    ? $" — {point.FullPrediction.Confidence:P1} confidence" 
                    : "";
                tooltipText += $"\nCell Type: {point.PredictedCellType}{confidenceStr}";
                
                // Show scores and marker details for all cell types if available
                if (point.FullPrediction?.Scores != null)
                {
                    tooltipText += "\n\n─── Classification Scores ───";
                    foreach (var kvp in point.FullPrediction.Scores.OrderByDescending(s => s.Value.CompositeScore))
                    {
                        var score = kvp.Value;
                        string indicator = kvp.Key == point.PredictedCellType ? "★" : "○";
                        
                        // Show composite score with marker and prior adjustments
                        string markerAdj = score.KeyMarkerAdjustment != 0 
                            ? $" (marker: {score.KeyMarkerAdjustment:+0.0;-0.0})" 
                            : "";
                        string priorAdj = score.PriorAdjustment != 0
                            ? $" (prior: {score.PriorAdjustment:+0.0;-0.0})"
                            : "";
                        tooltipText += $"\n{indicator} {kvp.Key}: {score.CompositeScore:F2}{markerAdj}{priorAdj}";

                        // Per-metric components are specific to the Standard 4-metric scorer. The Quantitative
                        // redesign has no specificity/hypergeometric/coverage analogue, so show only its Spearman
                        // rather than fake 0.00 / -0.0 for the others.
                        if (point.FullPrediction.ScorerMethod == "Quantitative")
                            tooltipText += $"\n   Spearman: {score.SpearmanCorrelation:F3}";
                        else
                            tooltipText += $"\n   Spearman: {score.SpearmanCorrelation:F3}, Spec: {score.SpecificityScore:F2}, -log(p): {-Math.Log10(Math.Max(score.HypergeometricPValue, 1e-300)):F1}, Cov: {score.MarkerCoverage:F3}";
                        
                        // Show markers if defined for this cell type
                        if (score.MarkersFound.Count > 0 || score.MarkersMissing.Count > 0)
                        {
                            tooltipText += "\n   ";
                            if (score.MarkersFound.Count > 0)
                            {
                                var foundList = string.Join(", ", score.MarkersFound.Keys);
                                tooltipText += $"✓{foundList} ";
                            }
                            if (score.MarkersMissing.Count > 0)
                            {
                                var missingList = string.Join(", ", score.MarkersMissing);
                                tooltipText += $"✗{missingList}";
                            }
                        }
                    }
                }
            }

            if (!string.IsNullOrEmpty(point.PlateName))
            {
                tooltipText += $"\nPlate: {point.PlateName}";
            }

            if (!string.IsNullOrEmpty(point.ExclusionDetail))
            {
                tooltipText += $"\n\n⚠ Excluded: {point.ExclusionDetail}";
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

        private void MenuSavePlotImage_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new SaveFileDialog
            {
                Filter = "PNG Image|*.png",
                DefaultExt = ".png",
                FileName = "plot"
            };

            if (dlg.ShowDialog() != true)
                return;

            const int scale = 3;
            int width = (int)PlotCanvas.ActualWidth;
            int height = (int)PlotCanvas.ActualHeight;

            if (width <= 0 || height <= 0)
                return;

            // Hide tooltip so it doesn't render in the saved image
            var previousVisibility = TooltipBorder.Visibility;
            TooltipBorder.Visibility = Visibility.Collapsed;

            try
            {
                var rtb = new RenderTargetBitmap(
                    width * scale, height * scale, 96 * scale, 96 * scale, PixelFormats.Pbgra32);
                rtb.Render(PlotCanvas);

                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(rtb));

                using (var stream = File.Create(dlg.FileName))
                {
                    encoder.Save(stream);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to save image: {ex.Message}", "Save Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                TooltipBorder.Visibility = previousVisibility;
            }
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

                return;
            }

            if (_isRefreshing)
            {

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

            }
        }
    }
}