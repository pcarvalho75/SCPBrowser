using System;
using System.Collections.Generic;
using System.Linq;
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

        // Data storage
        private ProteomicsData _originalData;
        private ProteomicsData _plateFilteredData;
        private ProteomicsData _filteredData;
        private Dictionary<string, int> _rawFileToPlateId;
        private List<HvpResult> _hvpResults;

        // Filter settings
        private int _proteinCutoff = 800;
        private List<int> _selectedPlateIds = new List<int>();

        // Public read-only access to data
        public ProteomicsData OriginalData => _originalData;
        public ProteomicsData PlateFilteredData => _plateFilteredData;
        public ProteomicsData FilteredData => _filteredData;
        public Dictionary<string, int> RawFileToPlateId => _rawFileToPlateId;
        public List<HvpResult> HvpResults => _hvpResults;

        // Filter settings properties
        public int ProteinCutoff
        {
            get => _proteinCutoff;
            set
            {
                if (_proteinCutoff != value)
                {
                    _proteinCutoff = value;
                    Console.WriteLine($"DataFilterService: Protein cutoff set to {value}");
                }
            }
        }

        public List<int> SelectedPlateIds
        {
            get => _selectedPlateIds;
            set => _selectedPlateIds = value ?? new List<int>();
        }

        /// <summary>
        /// Sets the original unfiltered data
        /// </summary>
        public void SetOriginalData(ProteomicsData data)
        {
            _originalData = data;
            Console.WriteLine($"DataFilterService: Original data set with {data?.TotalRawFiles ?? 0} raw files");
        }

        /// <summary>
        /// Loads the mapping of raw file names to plate IDs
        /// </summary>
        public async Task LoadPlateMappingAsync(ParquetDataService parquetService)
        {
            _rawFileToPlateId = new Dictionary<string, int>();

            if (parquetService == null)
                return;

            var rawFiles = await parquetService.GetRawFilesAsync();
            foreach (var rf in rawFiles)
            {
                if (rf.PlateId.HasValue)
                {
                    _rawFileToPlateId[rf.RawFileName] = rf.PlateId.Value;
                }
            }

            Console.WriteLine($"DataFilterService: Loaded plate mapping for {_rawFileToPlateId.Count} raw files");
        }

        /// <summary>
        /// Applies all filters and raises the FilteredDataChanged event
        /// </summary>
        public async Task ApplyFiltersAsync(ParquetDataService parquetService)
        {
            if (_originalData == null)
            {
                Console.WriteLine("DataFilterService: No original data to filter");
                return;
            }

            // Step 1: Filter by plates
            _plateFilteredData = await FilterByPlatesAsync(parquetService, _selectedPlateIds);

            // Step 2: Filter by protein cutoff
            _filteredData = FilterByProteinCutoff(_plateFilteredData, _proteinCutoff);

            // Step 3: Compute HVP on filtered data
            ComputeHvpResults();

            Console.WriteLine($"DataFilterService: Filters applied - {_filteredData.TotalRawFiles} raw files pass all filters");

            // Notify listeners
            FilteredDataChanged?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>
        /// Computes Highly Variable Proteins for the current filtered data
        /// </summary>
        private void ComputeHvpResults()
        {
            _hvpResults = null;

            if (_filteredData == null || _filteredData.ProteinQuantMatrix.Count == 0)
            {
                Console.WriteLine("DataFilterService: No data for HVP computation");
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
                Console.WriteLine($"DataFilterService: HVP computed - {hvpCount} highly variable proteins identified");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"DataFilterService: HVP computation failed - {ex.Message}");
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
                Console.WriteLine("DataFilterService: No plates selected - returning empty data");
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

            Console.WriteLine($"DataFilterService: Plate filter - {allRawFilesInPlates.Count} raw files in selected plates");

            return FilterDataByRawFiles(_originalData, allRawFilesInPlates);
        }

        /// <summary>
        /// Filters proteomics data to exclude raw files below the protein cutoff
        /// </summary>
        private ProteomicsData FilterByProteinCutoff(ProteomicsData data, int cutoff)
        {
            if (data == null)
                return null;

            // Get raw files that meet the cutoff
            var passingRawFiles = data.ProteinCountPerFile
                .Where(kvp => kvp.Value >= cutoff)
                .Select(kvp => kvp.Key)
                .ToHashSet();

            Console.WriteLine($"DataFilterService: Protein cutoff filter - {passingRawFiles.Count}/{data.RawFileNames.Count} raw files pass cutoff of {cutoff}");

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
                ProteinQuantMatrix = new Dictionary<string, Dictionary<string, double>>()
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
            filtered.TotalPeptides = source.TotalPeptides; // Keep original

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
                TotalPeptides = 0
            };
        }

        /// <summary>
        /// Clears all data and resets filters
        /// </summary>
        public void Clear()
        {
            _originalData = null;
            _plateFilteredData = null;
            _filteredData = null;
            _rawFileToPlateId = null;
            _hvpResults = null;
            _selectedPlateIds = new List<int>();
            _proteinCutoff = 800;

            Console.WriteLine("DataFilterService: Cleared all data and filters");
        }
    }
}