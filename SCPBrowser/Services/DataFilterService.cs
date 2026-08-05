using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace SCPBrowser.Services
{
    /// <summary>
    /// Centralized service for managing data filtering across the application.
    /// Handles plate filtering, protein cutoff filtering, and future filter types.
    /// </summary>
    public class DataFilterService
    {
        // Events
        public event EventHandler FilteredDataChanged;

        // Serialization guard - prevents concurrent filter passes from corrupting state
        private readonly SemaphoreSlim _filterLock = new SemaphoreSlim(1, 1);

        // Data storage
        private ProteomicsData _originalData;
        private ProteomicsData _plateFilteredData;
        private ProteomicsData _proteinCutoffFilteredData;
        private ProteomicsData _filteredData;
        private Dictionary<string, int> _rawFileToPlateId;
        private List<HvpResult> _hvpResults;

        private Dictionary<int, string> _plateIdToName;

        // Caching flags
        private bool _plateMappingLoaded = false;

        public Dictionary<int, string> PlateIdToName => _plateIdToName;

        /// <summary>
        /// Default minimum protein groups a run must identify to enter the analysis. Runs below it are dropped
        /// before PCA, UMAP, classification and export, so this gate decides which cells reach every downstream
        /// result. 800 is a high-sensitivity-platform value: it is exposed in Settings because a less sensitive
        /// instrument needs a lower one, and a more sensitive one needs a higher one to keep empty wells out.
        /// </summary>
        public const int DefaultProteinCutoff = 800;

        /// <summary>
        /// Sentinel for "no upper bound". The Max. Proteins spinner reports this when it sits at its maximum,
        /// and every comparison against the upper cutoff treats the value as filtering disabled.
        /// </summary>
        public const int NoUpperProteinCutoff = 99999;

        // Filter settings
        private int _proteinCutoff = DefaultProteinCutoff;
        private int _upperProteinCutoff = NoUpperProteinCutoff;
        private double _contaminantRatioCutoff = 1.0; // 0.0–1.0 fraction; 1.0 = no filtering
        private List<int> _selectedPlateIds = new List<int>();
        private HashSet<string> _manuallyExcludedRuns = new HashSet<string>();

        // Public read-only access to data
        public ProteomicsData OriginalData => _originalData;
        public ProteomicsData PlateFilteredData => _plateFilteredData;
        public ProteomicsData ProteinCutoffFilteredData => _proteinCutoffFilteredData;
        public ProteomicsData FilteredData => _filteredData;
        public Dictionary<string, int> RawFileToPlateId => _rawFileToPlateId;
        public List<HvpResult> HvpResults => _hvpResults;

        /// <summary>
        /// Runs excluded by the contaminant ratio cutoff.
        /// These runs are removed from PCA/UMAP/classification but still shown as grey dots on the Explorer scatter.
        /// </summary>
        public HashSet<string> ContaminantRatioExcludedRuns { get; private set; } = new HashSet<string>();

        // Filter settings properties
        public int ProteinCutoff
        {
            get => _proteinCutoff;
            set
            {
                if (_proteinCutoff != value)
                {
                    _proteinCutoff = value;

                }
            }
        }

        public int UpperProteinCutoff
        {
            get => _upperProteinCutoff;
            set
            {
                if (_upperProteinCutoff != value)
                {
                    _upperProteinCutoff = value;

                }
            }
        }

        public void ExcludeRun(string rawFileName)
        {
            _manuallyExcludedRuns.Add(rawFileName);
        }

        public void RestoreRun(string rawFileName)
        {
            _manuallyExcludedRuns.Remove(rawFileName);
        }

        public void ClearManualExclusions()
        {
            _manuallyExcludedRuns.Clear();

        }

        public HashSet<string> ManuallyExcludedRuns => _manuallyExcludedRuns;

        public List<int> SelectedPlateIds
        {
            get => _selectedPlateIds;
            set => _selectedPlateIds = value ?? new List<int>();
        }

        /// <summary>
        /// Maximum contaminant ratio (0.0–1.0). Runs above this threshold are excluded
        /// from PCA/UMAP/classification but greyed out on the Explorer scatter.
        /// Default 1.0 = no filtering.
        /// </summary>
        public double ContaminantRatioCutoff
        {
            get => _contaminantRatioCutoff;
            set
            {
                double clamped = Math.Clamp(value, 0.0, 1.0);
                if (Math.Abs(_contaminantRatioCutoff - clamped) > 0.0001)
                {
                    _contaminantRatioCutoff = clamped;

                }
            }
        }

        /// <summary>
        /// Sets the original unfiltered data
        /// </summary>
        public void SetOriginalData(ProteomicsData data)
        {
            _originalData = data;

        }

        /// <summary>
        /// Loads the mapping of raw file names to plate IDs (cached after first load)
        /// </summary>
        public async Task LoadPlateMappingAsync(ParquetDataService parquetService, PlateService plateService = null)
        {
            // Use cache if already loaded
            if (_plateMappingLoaded && _rawFileToPlateId != null && _rawFileToPlateId.Count > 0)
            {

                return;
            }

            _rawFileToPlateId = new Dictionary<string, int>();
            _plateIdToName = new Dictionary<int, string>();

            if (parquetService == null)
                return;

            var rawFiles = await parquetService.GetRawFilesAsync().ConfigureAwait(false);
            foreach (var rf in rawFiles)
            {
                if (rf.PlateId.HasValue)
                {
                    _rawFileToPlateId[rf.RawFileName] = rf.PlateId.Value;
                }
            }

            // Load plate names if plate service is provided
            if (plateService != null)
            {
                var plates = await plateService.GetPlatesAsync().ConfigureAwait(false);
                foreach (var plate in plates)
                {
                    _plateIdToName[plate.PlateId] = plate.PlateName;
                }
            }

            _plateMappingLoaded = true;

        }

        /// <summary>
        /// Invalidates the cached plate mapping (call after data import)
        /// </summary>
        public void InvalidatePlateMappingCache()
        {
            _plateMappingLoaded = false;
            _rawFileToPlateId?.Clear();
            _plateIdToName?.Clear();

        }

        /// <summary>
        /// Applies all filters and raises the FilteredDataChanged event
        /// </summary>
        public async Task ApplyFiltersAsync(ParquetDataService parquetService)
        {
            if (_originalData == null)
            {

                return;
            }

            await _filterLock.WaitAsync();
            try
            {
                // Step 1: Filter by plates
                _plateFilteredData = await FilterByPlatesAsync(parquetService, _selectedPlateIds);

                // Step 2: Filter by protein cutoff
                _proteinCutoffFilteredData = FilterByProteinCutoff(_plateFilteredData, _proteinCutoff);

                // Step 3: Filter by contaminant ratio cutoff
                _filteredData = FilterByContaminantRatio(_proteinCutoffFilteredData);

                // Step 4: Remove contaminant proteins from both datasets
                ExcludeContaminants(_proteinCutoffFilteredData);
                ExcludeContaminants(_filteredData);

                // Step 5: Compute HVP on filtered data
                ComputeHvpResults();


            }
            finally
            {
                _filterLock.Release();
            }

            // Notify listeners outside the lock to avoid deadlocks with UI handlers
            FilteredDataChanged?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>
        /// Removes contaminant proteins from the filtered ProteinQuantMatrix.
        /// Contaminants are still used for ratio calculations but excluded from PCA, UMAP, classification, etc.
        /// </summary>
        private void ExcludeContaminants(ProteomicsData data)
        {
            if (data == null || _originalData.ContaminantIds == null || _originalData.ContaminantIds.Count == 0)
                return;

            int removed = 0;
            foreach (var contaminantId in _originalData.ContaminantIds)
            {
                if (data.ProteinQuantMatrix.Remove(contaminantId))
                    removed++;
            }

            if (removed > 0)
            {
                data.TotalProteinGroups = data.ProteinQuantMatrix.Count;

            }
        }

        /// <summary>
        /// Computes Highly Variable Proteins for the current filtered data
        /// </summary>
        private void ComputeHvpResults()
        {
            _hvpResults = null;

            if (_filteredData == null || _filteredData.ProteinQuantMatrix.Count == 0)
            {

                return;
            }

            try
            {
                var hvpService = new HighlyVariableProteinsService();
                _hvpResults = hvpService.FindHighlyVariableProteins(
                    _filteredData.ProteinQuantMatrix,
                    nTopProteins: 2000,
                    loessSpan: 0.3,
                    minDetectionRate: 0.05,
                    minAbsoluteDetections: 20);

                int hvpCount = _hvpResults?.Count(h => h.IsHighlyVariable) ?? 0;

            }
            catch (Exception)
            {

                _hvpResults = null;
            }
        }

        /// <summary>
        /// Filters proteomics data to only include raw files from selected plates
        /// </summary>
        private async Task<ProteomicsData> FilterByPlatesAsync(ParquetDataService parquetService, List<int> selectedPlateIds)
        {
            if (_originalData == null)
                return null;

            // If no plates selected, return empty data
            if (selectedPlateIds == null || selectedPlateIds.Count == 0)
            {

                return CreateEmptyProteomicsData();
            }

            // Get raw files for selected plates from database
            var allRawFilesInPlates = new HashSet<string>();
            foreach (var plateId in selectedPlateIds)
            {
                var rawFiles = await parquetService.GetRawFilesAsync(plateId: plateId);
                foreach (var rf in rawFiles)
                {
                    allRawFilesInPlates.Add(rf.RawFileName);
                }
            }



            return FilterDataByRawFiles(_originalData, allRawFilesInPlates);
        }

        /// <summary>
        /// Filters proteomics data to exclude raw files below the protein cutoff
        /// </summary>
        private ProteomicsData FilterByProteinCutoff(ProteomicsData data, int cutoff)
        {
            if (data == null)
                return null;

            // Get raw files that meet both lower and upper cutoffs and are not manually excluded
            var passingRawFiles = data.ProteinCountPerFile
                .Where(kvp => kvp.Value >= cutoff
                    && (_upperProteinCutoff >= NoUpperProteinCutoff || kvp.Value <= _upperProteinCutoff)
                    && !_manuallyExcludedRuns.Contains(kvp.Key))
                .Select(kvp => kvp.Key)
                .ToHashSet();



            return FilterDataByRawFiles(data, passingRawFiles);
        }

        /// <summary>
        /// Filters runs whose contaminant ratio exceeds the cutoff.
        /// Excluded runs are tracked in ContaminantRatioExcludedRuns so the UI can grey them out.
        /// Uses TargetProteinRatioPerFile from OriginalData (which stores ratios pre-filtering).
        /// </summary>
        private ProteomicsData FilterByContaminantRatio(ProteomicsData data)
        {
            ContaminantRatioExcludedRuns.Clear();

            if (data == null || _contaminantRatioCutoff >= 1.0)
                return data;

            // Use ratios from OriginalData since they're computed before any filtering
            var ratios = _originalData.TargetProteinRatioPerFile;
            if (ratios == null || ratios.Count == 0)
                return data;

            var passingRawFiles = new HashSet<string>();
            foreach (var rawFile in data.RawFileNames)
            {
                double ratio = ratios.TryGetValue(rawFile, out double r) ? r : 0;
                if (ratio <= _contaminantRatioCutoff)
                    passingRawFiles.Add(rawFile);
                else
                    ContaminantRatioExcludedRuns.Add(rawFile);
            }



            return FilterDataByRawFiles(data, passingRawFiles);
        }

        /// <summary>
        /// Filters proteomics data to only include specific raw files
        /// </summary>
        private ProteomicsData FilterDataByRawFiles(ProteomicsData source, HashSet<string> allowedRawFiles)
        {
            var filtered = new ProteomicsData
            {
                RawFileNames = source.RawFileNames.Where(rf => allowedRawFiles.Contains(rf)).ToList(),
                ProteinCountPerFile = source.ProteinCountPerFile
                    .Where(kvp => allowedRawFiles.Contains(kvp.Key))
                    .ToDictionary(kvp => kvp.Key, kvp => kvp.Value),
                PeptideCountPerFile = source.PeptideCountPerFile
                    .Where(kvp => allowedRawFiles.Contains(kvp.Key))
                    .ToDictionary(kvp => kvp.Key, kvp => kvp.Value),
                TotalIonCurrentPerFile = source.TotalIonCurrentPerFile
                    .Where(kvp => allowedRawFiles.Contains(kvp.Key))
                    .ToDictionary(kvp => kvp.Key, kvp => kvp.Value),
                TargetProteinRatioPerFile = source.TargetProteinRatioPerFile
                    .Where(kvp => allowedRawFiles.Contains(kvp.Key))
                    .ToDictionary(kvp => kvp.Key, kvp => kvp.Value),
                BiologicalConditionPerFile = source.BiologicalConditionPerFile
                    .Where(kvp => allowedRawFiles.Contains(kvp.Key))
                    .ToDictionary(kvp => kvp.Key, kvp => kvp.Value),
                ProteinToGeneMap = new Dictionary<string, string>(source.ProteinToGeneMap),
                ProteinQuantMatrix = new Dictionary<string, Dictionary<string, double>>(),
                ContaminantIds = new HashSet<string>(source.ContaminantIds, StringComparer.OrdinalIgnoreCase),
                IsGeneMatrix = source.IsGeneMatrix // preserve the gene-matrix flag through filtering (drives the axis relabel)
            };

            // Filter protein quant matrix
            foreach (var protein in source.ProteinQuantMatrix)
            {
                var filteredRawFiles = protein.Value
                    .Where(kvp => allowedRawFiles.Contains(kvp.Key))
                    .ToDictionary(kvp => kvp.Key, kvp => kvp.Value);

                if (filteredRawFiles.Count > 0)
                {
                    filtered.ProteinQuantMatrix[protein.Key] = filteredRawFiles;
                }
            }

            // Update totals
            filtered.TotalRawFiles = filtered.RawFileNames.Count;
            filtered.TotalProteinGroups = filtered.ProteinQuantMatrix.Count;
            filtered.TotalPeptides = source.AllPeptideSequences.Count; // Use unique sequences from source

            return filtered;
        }

        /// <summary>
        /// Creates an empty ProteomicsData object
        /// </summary>
        private ProteomicsData CreateEmptyProteomicsData()
        {
            return new ProteomicsData
            {
                RawFileNames = new List<string>(),
                ProteinCountPerFile = new Dictionary<string, int>(),
                PeptideCountPerFile = new Dictionary<string, int>(),
                TotalIonCurrentPerFile = new Dictionary<string, double>(),
                TargetProteinRatioPerFile = new Dictionary<string, double>(),
                BiologicalConditionPerFile = new Dictionary<string, string>(),
                ProteinQuantMatrix = new Dictionary<string, Dictionary<string, double>>(),
                ProteinToGeneMap = new Dictionary<string, string>(),
                TotalRawFiles = 0,
                TotalProteinGroups = 0,
                TotalPeptides = 0,
                ContaminantIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            };
        }

        public void Clear()
        {
            _originalData = null;
            _plateFilteredData = null;
            _proteinCutoffFilteredData = null;
            _filteredData = null;
            _rawFileToPlateId = null;
            _plateIdToName = null;
            _hvpResults = null;
            _selectedPlateIds = new List<int>();
            _proteinCutoff = DefaultProteinCutoff;
            _upperProteinCutoff = NoUpperProteinCutoff;
            _contaminantRatioCutoff = 1.0;
            _manuallyExcludedRuns.Clear();
            ContaminantRatioExcludedRuns.Clear();
        }
    }
}