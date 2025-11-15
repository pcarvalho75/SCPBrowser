using SCPBrowser.Services;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace SCPBrowser
{
    /// <summary>
    /// Manages transcriptomic reference data and cell type classification for proteomics runs
    /// Handles database loading, predictions, caching, and persistence
    /// </summary>
    public class CellTypeClassificationManager
    {
        private readonly ReferenceDataService _referenceService;
        private TranscriptomicDatabase _database;
        private CellTypePredictor _predictor;
        private Dictionary<string, CellTypePredictionResult> _cachedPredictions;

        public bool IsLoaded => _database != null;
        public TranscriptomicDatabase Database => _database;
        public bool HasPredictions => _cachedPredictions != null && _cachedPredictions.Count > 0;
        public bool IsReady => IsLoaded && _predictor != null;

        public CellTypeClassificationManager()
        {
            _referenceService = new ReferenceDataService();
        }

        /// <summary>
        /// Loads transcriptomic database from file
        /// </summary>
        public async Task LoadDatabaseAsync(string databasePath)
        {
            if (!File.Exists(databasePath))
                throw new FileNotFoundException("Reference database not found", databasePath);

            _database = await _referenceService.LoadTranscriptomicDataAsync(databasePath);
            _predictor = new CellTypePredictor(_database);

            Console.WriteLine($"Transcriptomic database loaded: {_database.TotalCellTypes} cell types, {_database.TotalGenes} genes");
        }

        /// <summary>
        /// Gets cell type predictions from cache, database, or computes them if needed
        /// </summary>
        public async Task<Dictionary<string, CellTypePredictionResult>> GetOrComputePredictionsAsync(
     ProteomicsData proteomicsData,
     string projectDatabasePath,
     int importId,
     IProgressReporter progressReporter = null)
        {
            if (proteomicsData == null)
                throw new ArgumentNullException(nameof(proteomicsData));

            // 1. Check if already in memory cache
            if (_cachedPredictions != null && _cachedPredictions.Count > 0)
            {
                Console.WriteLine("Cell type predictions already in memory cache");
                return _cachedPredictions;
            }

            // 2. Check if transcriptomic database is loaded
            if (!IsLoaded)
            {
                throw new InvalidOperationException("Transcriptomic reference database is not loaded. Please import transcriptomic data first.");
            }

            // 3. Try to load from database
            progressReporter?.ReportMessage("Checking for existing cell type classifications...");

            var projectDataService = new ProjectDataService(projectDatabasePath);
            var existingPredictions = await projectDataService.LoadCellTypeClassificationsAsync(
                projectDatabasePath,
                importId);

            if (existingPredictions.Count > 0 && existingPredictions.Count == proteomicsData.TotalRawFiles)
            {
                Console.WriteLine($"Loaded {existingPredictions.Count} cell type classifications from database");
                _cachedPredictions = existingPredictions;
                return _cachedPredictions;
            }

            // 4. Need to compute predictions
            progressReporter?.ReportMessage("Computing cell type predictions...");

            var predictions = PredictCellTypesForAllRuns(proteomicsData, progressReporter);

            // 5. Save to database
            progressReporter?.ReportMessage("Saving classifications to database...");

            await projectDataService.SaveCellTypeClassificationsAsync(
                projectDatabasePath,
                importId,
                predictions);

            Console.WriteLine($"Saved {predictions.Count} cell type classifications to database");

            // 6. Store in cache and return
            _cachedPredictions = predictions;
            return _cachedPredictions;
        }

        /// <summary>
        /// Predicts cell types for all runs in the proteomics data
        /// </summary>
        public Dictionary<string, CellTypePredictionResult> PredictCellTypesForAllRuns(
            ProteomicsData proteomicsData,
            IProgressReporter progressReporter = null)
        {
            if (!IsLoaded)
                throw new InvalidOperationException("Transcriptomic database not loaded");

            var predictions = new Dictionary<string, CellTypePredictionResult>();

            if (proteomicsData == null || proteomicsData.RawFileNames == null)
            {
                return predictions;
            }

            int currentRun = 0;
            int totalRuns = proteomicsData.TotalRawFiles;

            foreach (var runName in proteomicsData.RawFileNames)
            {
                currentRun++;
                progressReporter?.ReportProgress($"Classifying run {currentRun} of {totalRuns}: {runName}");

                var prediction = PredictCellTypeForRun(proteomicsData, runName);
                predictions[runName] = prediction;
            }

            return predictions;
        }

        /// <summary>
        /// Predicts cell type for a single run
        /// </summary>
        public CellTypePredictionResult PredictCellTypeForRun(ProteomicsData proteomicsData, string runName)
        {
            if (!IsLoaded)
                throw new InvalidOperationException("Transcriptomic database not loaded");

            if (proteomicsData == null || string.IsNullOrEmpty(runName))
                return new CellTypePredictionResult();

            var proteinAbundances = ExtractProteinAbundances(proteomicsData, runName);

            ////// ADD THIS DEBUG OUTPUT
            //if (_database.CellTypeProfiles.Count > 0)
            //{
            //    var firstCellType = _database.CellTypeProfiles.First();
            //    Console.WriteLine($"[DEBUG] Transcriptomic DB - First cell type: {firstCellType.Key}");
            //    Console.WriteLine($"[DEBUG] Sample gene names: {string.Join(", ", firstCellType.Value.MedianExpression.Keys.Take(10))}");
            //}

            return _predictor.PredictCellType(proteinAbundances);
        }

        /// <summary>
        /// Generates a color map for all cell types in the database
        /// </summary>
        public Dictionary<string, System.Windows.Media.Color> GenerateCellTypeColorMap()
        {
            if (!IsLoaded)
                return new Dictionary<string, System.Windows.Media.Color>();

            var cellTypes = _database.CellTypeProfiles.Keys.OrderBy(ct => ct).ToList();
            var colorMap = new Dictionary<string, System.Windows.Media.Color>();

            for (int i = 0; i < cellTypes.Count; i++)
            {
                var hue = (i * 360.0 / cellTypes.Count) % 360;
                var color = ColorFromHSV(hue, 0.7, 0.9);
                colorMap[cellTypes[i]] = color;
            }

            return colorMap;
        }


        private Dictionary<string, double> ExtractProteinAbundances(ProteomicsData proteomicsData, string runName)
        {
            var abundances = new Dictionary<string, double>();

            foreach (var proteinGroup in proteomicsData.ProteinQuantMatrix.Keys)
            {
                if (proteomicsData.ProteinQuantMatrix[proteinGroup].ContainsKey(runName))
                {
                    double abundance = proteomicsData.ProteinQuantMatrix[proteinGroup][runName];

                    if (abundance > 0)
                    {
                        // Try to get gene name from the mapping first
                        if (proteomicsData.ProteinToGeneMap.TryGetValue(proteinGroup, out string genesString))
                        {
                            // genesString is like "RPL4_HUMAN" or "RPL4_HUMAN;ACTB_HUMAN"
                            var geneEntries = genesString.Split(';');
                            foreach (var geneEntry in geneEntries)
                            {
                                var trimmed = geneEntry.Trim();
                                if (string.IsNullOrEmpty(trimmed))
                                    continue;

                                // Extract gene name (remove _HUMAN, _MOUSE, etc.)
                                string geneName = trimmed;
                                if (trimmed.Contains('_'))
                                {
                                    geneName = trimmed.Substring(0, trimmed.IndexOf('_'));
                                }

                                if (!string.IsNullOrEmpty(geneName))
                                {
                                    if (!abundances.ContainsKey(geneName))
                                    {
                                        abundances[geneName] = abundance;
                                    }
                                    else
                                    {
                                        abundances[geneName] = Math.Max(abundances[geneName], abundance);
                                    }
                                }
                            }
                        }
                        else
                        {
                            // Fallback: use old method if no gene mapping available
                            var proteinNames = ExtractProteinNames(proteinGroup);
                            foreach (var proteinName in proteinNames)
                            {
                                if (!abundances.ContainsKey(proteinName))
                                {
                                    abundances[proteinName] = abundance;
                                }
                                else
                                {
                                    abundances[proteinName] = Math.Max(abundances[proteinName], abundance);
                                }
                            }
                        }
                    }
                }
            }

            // DEBUG OUTPUT
            Console.WriteLine($"[DEBUG] Run: {runName}");
            Console.WriteLine($"[DEBUG] Extracted {abundances.Count} unique gene names from proteomics");
            if (abundances.Count > 0)
            {
                Console.WriteLine($"[DEBUG] Sample gene names: {string.Join(", ", abundances.Keys.Take(10))}");
            }

            return abundances;
        }

        /// <summary>
        /// Extracts individual protein names from a protein group string
        /// </summary>
        private List<string> ExtractProteinNames(string proteinGroup)
        {
            var names = new List<string>();

            var parts = proteinGroup.Split(';', ',');
            foreach (var part in parts)
            {
                var trimmed = part.Trim();
                if (!string.IsNullOrEmpty(trimmed))
                {
                    var geneName = ExtractGeneName(trimmed);
                    if (!string.IsNullOrEmpty(geneName))
                    {
                        names.Add(geneName);
                    }
                }
            }

            return names;
        }

        /// <summary>
        /// Extracts gene name from a protein identifier (handles various formats)
        /// </summary>
        private string ExtractGeneName(string proteinIdentifier)
        {
            if (string.IsNullOrEmpty(proteinIdentifier))
                return null;

            if (proteinIdentifier.Contains("_"))
            {
                var parts = proteinIdentifier.Split('_');
                if (parts.Length > 0)
                    return parts[0];
            }

            if (proteinIdentifier.Contains("|"))
            {
                var parts = proteinIdentifier.Split('|');
                if (parts.Length >= 2)
                    return parts[1];
            }

            return proteinIdentifier;
        }

        /// <summary>
        /// Converts HSV color space to RGB
        /// </summary>
        private System.Windows.Media.Color ColorFromHSV(double hue, double saturation, double value)
        {
            int hi = Convert.ToInt32(Math.Floor(hue / 60)) % 6;
            double f = hue / 60 - Math.Floor(hue / 60);

            value = value * 255;
            byte v = Convert.ToByte(value);
            byte p = Convert.ToByte(value * (1 - saturation));
            byte q = Convert.ToByte(value * (1 - f * saturation));
            byte t = Convert.ToByte(value * (1 - (1 - f) * saturation));

            if (hi == 0)
                return System.Windows.Media.Color.FromArgb(255, v, t, p);
            else if (hi == 1)
                return System.Windows.Media.Color.FromArgb(255, q, v, p);
            else if (hi == 2)
                return System.Windows.Media.Color.FromArgb(255, p, v, t);
            else if (hi == 3)
                return System.Windows.Media.Color.FromArgb(255, p, q, v);
            else if (hi == 4)
                return System.Windows.Media.Color.FromArgb(255, t, p, v);
            else
                return System.Windows.Media.Color.FromArgb(255, v, p, q);
        }

        /// <summary>
        /// Clears cached predictions (forces recomputation on next request)
        /// </summary>
        public void ClearCache()
        {
            _cachedPredictions = null;
            Console.WriteLine("Cell type prediction cache cleared");
        }

        /// <summary>
        /// Deletes all classifications from database and clears cache
        /// </summary>
        public async Task DeleteAllClassificationsAsync(string projectDatabasePath, int importId)
        {
            var projectDataService = new ProjectDataService(projectDatabasePath);
            await projectDataService.DeleteAllCellTypeClassificationsAsync(projectDatabasePath, importId);
            ClearCache();
            Console.WriteLine("All cell type classifications deleted");
        }

        /// <summary>
        /// Comprehensive diagnostic to analyze protein-to-gene mapping quality
        /// </summary>
        public void DiagnoseProteinGeneMapping(ProteomicsData proteomicsData, string runName)
        {
            Console.WriteLine($"");
            Console.WriteLine($"========================================");
            Console.WriteLine($"PROTEIN-TO-GENE MAPPING DIAGNOSTIC");
            Console.WriteLine($"Run: {runName}");
            Console.WriteLine($"========================================");

            if (!proteomicsData.ProteinQuantMatrix.ContainsKey(runName))
            {
                Console.WriteLine($"ERROR: Run not found in proteomics data");
                return;
            }

            // Step 1: Get all proteins for this run
            var proteinsInRun = proteomicsData.ProteinQuantMatrix.Keys
                .Where(protein => proteomicsData.ProteinQuantMatrix[protein].ContainsKey(runName))
                .ToList();

            Console.WriteLine($"");
            Console.WriteLine($"Step 1: Proteins detected in proteomics");
            Console.WriteLine($"  Total proteins: {proteinsInRun.Count}");

            // Step 2: Check how many have gene mappings
            int proteinsWithMappings = 0;
            int proteinsWithoutMappings = 0;
            List<string> sampleUnmapped = new List<string>();

            foreach (var protein in proteinsInRun)
            {
                if (proteomicsData.ProteinToGeneMap.ContainsKey(protein))
                {
                    proteinsWithMappings++;
                }
                else
                {
                    proteinsWithoutMappings++;
                    if (sampleUnmapped.Count < 5)
                        sampleUnmapped.Add(protein);
                }
            }

            Console.WriteLine($"");
            Console.WriteLine($"Step 2: Gene mapping availability");
            Console.WriteLine($"  Proteins WITH gene mappings: {proteinsWithMappings}");
            Console.WriteLine($"  Proteins WITHOUT mappings: {proteinsWithoutMappings}");
            if (sampleUnmapped.Count > 0)
            {
                Console.WriteLine($"  Sample unmapped proteins: {string.Join(", ", sampleUnmapped)}");
            }

            // Step 3: Extract all gene names
            var extractedGenes = new Dictionary<string, string>(); // gene -> source protein
            foreach (var protein in proteinsInRun)
            {
                if (proteomicsData.ProteinToGeneMap.TryGetValue(protein, out string genesString))
                {
                    var geneEntries = genesString.Split(';');
                    foreach (var geneEntry in geneEntries)
                    {
                        var trimmed = geneEntry.Trim();
                        if (string.IsNullOrEmpty(trimmed))
                            continue;

                        string geneName = trimmed;
                        if (trimmed.Contains('_'))
                        {
                            geneName = trimmed.Substring(0, trimmed.IndexOf('_'));
                        }

                        if (!string.IsNullOrEmpty(geneName))
                        {
                            if (!extractedGenes.ContainsKey(geneName))
                                extractedGenes[geneName] = protein;
                        }
                    }
                }
            }

            Console.WriteLine($"");
            Console.WriteLine($"Step 3: Gene name extraction");
            Console.WriteLine($"  Total unique genes extracted: {extractedGenes.Count}");
            Console.WriteLine($"  Sample genes: {string.Join(", ", extractedGenes.Keys.Take(10))}");

            // Step 4: Check how many exist in transcriptomic database
            if (!IsLoaded)
            {
                Console.WriteLine($"");
                Console.WriteLine($"ERROR: Transcriptomic database not loaded");
                return;
            }

            var allTranscriptomicGenes = new HashSet<string>();
            foreach (var profile in _database.CellTypeProfiles.Values)
            {
                foreach (var gene in profile.MedianExpression.Keys)
                {
                    allTranscriptomicGenes.Add(gene);
                }
            }

            var matchedGenes = new List<string>();
            var unmatchedGenes = new List<string>();

            foreach (var gene in extractedGenes.Keys)
            {
                if (allTranscriptomicGenes.Contains(gene))
                {
                    matchedGenes.Add(gene);
                }
                else
                {
                    if (unmatchedGenes.Count < 20) // Keep more samples of unmatched
                        unmatchedGenes.Add(gene);
                }
            }

            Console.WriteLine($"");
            Console.WriteLine($"Step 4: Transcriptomic database matching");
            Console.WriteLine($"  Total genes in transcriptomic DB: {allTranscriptomicGenes.Count}");
            Console.WriteLine($"  Proteomics genes that MATCH transcriptomics: {matchedGenes.Count}");
            Console.WriteLine($"  Proteomics genes that DON'T MATCH: {extractedGenes.Count - matchedGenes.Count}");
            Console.WriteLine($"  Match rate: {(matchedGenes.Count * 100.0 / extractedGenes.Count):F1}%");

            if (matchedGenes.Count > 0)
            {
                Console.WriteLine($"");
                Console.WriteLine($"  Sample MATCHED genes: {string.Join(", ", matchedGenes.Take(15))}");
            }

            if (unmatchedGenes.Count > 0)
            {
                Console.WriteLine($"");
                Console.WriteLine($"  Sample UNMATCHED genes: {string.Join(", ", unmatchedGenes)}");
            }

            // Step 5: Check marker overlap
            Console.WriteLine($"");
            Console.WriteLine($"Step 5: Marker gene analysis");
            foreach (var cellType in _database.CellTypeProfiles.Keys)
            {
                if (_cellTypeMarkers != null && _cellTypeMarkers.ContainsKey(cellType))
                {
                    var markers = _cellTypeMarkers[cellType];
                    int overlap = matchedGenes.Count(g => markers.Contains(g));
                    Console.WriteLine($"  {cellType}: {markers.Count} markers, {overlap} detected in this run");
                }
            }

            Console.WriteLine($"========================================");
            Console.WriteLine($"");
        }
    }
}