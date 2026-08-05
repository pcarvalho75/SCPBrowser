using SCPBrowser.Services;
using System;
using System.Diagnostics;
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
        private IClassifier _classifier;
        private Dictionary<string, CellTypePredictionResult> _cachedPredictions;
        // The in-memory cache is only valid for the (import, method) it was computed for. This manager is a
        // session-lifetime singleton reused across project opens, so without these keys a cache built for project A
        // (or a different method) would be served for project B — showing the wrong cell-type calls.
        private int _cachedImportId = -1;
        private string _cachedMethod;

        // import_id is a per-database AUTOINCREMENT, so project A and project B both have import 1. Keying the
        // cache on (importId, method) alone therefore COLLIDES across projects and can serve A's predictions for
        // B. The reference source path disambiguates them.
        private string _cachedProjectPath;

        /// <summary>
        /// Path of the reference database currently loaded into <see cref="_database"/>, or null if none. This
        /// manager is a session-lifetime singleton reused across project opens, so the loaded reference must be
        /// identified by WHERE IT CAME FROM - otherwise opening a second project keeps the first project's
        /// reference and classifies the new project's cells against it.
        /// </summary>
        public string LoadedReferencePath { get; private set; }

        public bool IsLoaded => _database != null;
        public TranscriptomicDatabase Database => _database;
        public bool HasPredictions => _cachedPredictions != null && _cachedPredictions.Count > 0;
        public bool IsReady => IsLoaded && _classifier != null;

        /// <summary>"Standard" (shipped CellTypePredictor) or "Quantitative" (redesign). Set from the project setting
        /// before classifying; changing it rebuilds the active classifier over the loaded reference.</summary>
        public string ClassificationMethod { get; private set; } = "Standard";

        public CellTypeClassificationManager()
        {
            _referenceService = new ReferenceDataService();
        }

        /// <summary>Selects the classifier method and rebuilds it over the loaded reference (no-op if unchanged).</summary>
        public void SetClassificationMethod(string method)
        {
            method = string.Equals(method, "Quantitative", StringComparison.OrdinalIgnoreCase) ? "Quantitative" : "Standard";
            if (method == ClassificationMethod && _classifier != null) return;

            if (_database != null)
            {
                // Build into a local first so a failed build can never leave ClassificationMethod and _classifier
                // out of sync (label "Quantitative" while a Standard classifier is actually running). Reflect the
                // ACTUAL method built — a <2-class reference falls the redesign back to Standard.
                var built = BuildClassifier(method);
                if (built == null) return;               // keep the current, working classifier on failure
                _classifier = built;
                ClassificationMethod = built.MethodName;
            }
            else
            {
                ClassificationMethod = method;            // no reference yet — build when it loads
            }
            _cachedPredictions = null;                    // cached calls were produced by the previous method
        }

        private IClassifier BuildClassifier(string method)
        {
            try
            {
                // The quantitative scorer requires at least two reference classes; a degenerate single-class
                // reference transparently falls back to Standard (which tolerates it) rather than throwing.
                if (method == "Quantitative" && (_database?.CellTypeProfiles?.Count ?? 0) >= 2)
                    return new QuantitativeClassifier(_database);
                return new StandardClassifier(_database);
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Loads transcriptomic database from file
        /// </summary>
        public async Task LoadDatabaseAsync(string databasePath)
        {
            if (!File.Exists(databasePath))
                throw new FileNotFoundException("Reference database not found", databasePath);

            _database = await _referenceService.LoadTranscriptomicDataAsync(databasePath);

            // Record WHICH reference is loaded, and drop any cache built against the previous one. Both matter for
            // the same reason: this manager outlives a single project.
            _cachedPredictions = null;
            _cachedImportId = -1;
            _cachedMethod = null;
            _cachedProjectPath = null;

            // Null means the database exists but has no reference profiles imported yet — normal initial state
            if (_database == null)
            {
                _classifier = null;
                LoadedReferencePath = null;
                return;
            }

            LoadedReferencePath = databasePath;
            _classifier = BuildClassifier(ClassificationMethod);


        }

        /// <summary>
        /// Gets cell type predictions from cache, database, or computes them if needed
        /// </summary>
        /// <param name="forceRecompute">If true, ignores cached/DB results and recomputes with current key markers</param>
        /// <param name="excludedCellTypes">Cell types to exclude from classification</param>
        public async Task<Dictionary<string, CellTypePredictionResult>> GetOrComputePredictionsAsync(
            ProteomicsData proteomicsData,
            string projectDatabasePath,
            int importId,
            IProgressReporter progressReporter = null,
            Dictionary<string, HashSet<string>> keyMarkers = null,
            bool forceRecompute = false,
            HashSet<string> excludedCellTypes = null,
            Dictionary<string, double> priorWeights = null)
        {
            if (proteomicsData == null)
                throw new ArgumentNullException(nameof(proteomicsData));

            // 0. Resolve the active classification method from the project. An explicit setting always wins; with no
            //    setting, a project that ALREADY has classifications keeps the method that produced them (opening it
            //    never silently re-runs the classifier) while a project with none defaults to the new Quantitative
            //    method. Each read is guarded independently so a transient failure of one doesn't lose the other, and
            //    a confirmed-empty project still gets the intended Quantitative default rather than falling to Standard.
            string setting = null;
            try { setting = await new Services.ProjectDatabaseService(projectDatabasePath).GetSettingAsync("classification_method", null); }
            catch { /* setting unreadable — try the stored method below */ }

            if (!string.IsNullOrEmpty(setting))
            {
                SetClassificationMethod(setting);
            }
            else
            {
                string stored = null; bool storedKnown = false;
                try { stored = await new CellTypeClassificationService(projectDatabasePath).GetStoredScorerMethodAsync(); storedKnown = true; }
                catch { /* stored method unreadable — leave the current method unchanged (conservative) */ }

                // Marker-based assignments are NOT a profile-classifier result: they are produced by
                // MarkerClassificationService from user-defined gene panels and carry none of the metrics this
                // classifier computes. Adopting that tag here would make methodMatches true below and serve the
                // marker rows as CellTypePredictor output (and export fabricated metric columns for them), so it
                // is treated as "no profile method stored" and the intended default applies instead.
                bool storedIsProfileMethod = !string.Equals(stored, Services.MarkerClassificationService.MarkerScorerMethod,
                                                            StringComparison.OrdinalIgnoreCase);

                if (storedKnown) SetClassificationMethod(storedIsProfileMethod ? (stored ?? "Quantitative") : "Quantitative");
            }

            // 1. Check if already in memory cache — but ONLY if it was computed for THIS import and the active method.
            if (!forceRecompute && _cachedPredictions != null && _cachedPredictions.Count > 0
                && _cachedImportId == importId
                && string.Equals(_cachedMethod, ClassificationMethod, StringComparison.OrdinalIgnoreCase)
                // import_id is per-database, so it repeats across projects; without the project path a cache built
                // for project A would be served for project B's import of the same number.
                && string.Equals(_cachedProjectPath, projectDatabasePath, StringComparison.OrdinalIgnoreCase))
            {

                return _cachedPredictions;
            }

            // 2. Check if transcriptomic database is loaded
            if (!IsLoaded)
            {
                throw new InvalidOperationException("Reference database is not loaded. Please import omic reference data first.");
            }

            // 3. Try to load from database (skip if forcing recompute)
            if (!forceRecompute)
            {
                progressReporter?.ReportMessage("Checking for existing cell type classifications...");

                var cellTypeService = new CellTypeClassificationService(projectDatabasePath);
                var existingPredictions = await cellTypeService.LoadCellTypeClassificationsAsync(importId);

                // Only serve stored rows if they were produced by the ACTIVE method — otherwise a method switch would
                // silently display the other classifier's results. On a method mismatch, fall through and recompute.
                string storedMethod = await cellTypeService.GetStoredScorerMethodAsync() ?? "Standard";
                bool methodMatches = string.Equals(storedMethod, ClassificationMethod, StringComparison.OrdinalIgnoreCase);

                if (methodMatches && existingPredictions.Count > 0 && existingPredictions.Count == proteomicsData.TotalRawFiles)
                {

                    _cachedPredictions = existingPredictions;
                    _cachedImportId = importId;
                    _cachedMethod = ClassificationMethod;
                    _cachedProjectPath = projectDatabasePath;
                    return _cachedPredictions;
                }
            }
            else
            {

            }

            // 4. Need to compute predictions

            progressReporter?.ReportMessage("Computing cell type predictions...");

            var predictions = PredictCellTypesForAllRuns(proteomicsData, progressReporter, keyMarkers, excludedCellTypes, priorWeights);

            // 5. Save to database (delete old ones first if forcing recompute)
            progressReporter?.ReportMessage("Saving classifications to database...");

            var saveCellTypeService = new CellTypeClassificationService(projectDatabasePath);
            // Always clear before saving: a method switch replaces the other classifier's rows wholesale so the
            // table never mixes methods (the table is single-method-at-a-time by design).
            await saveCellTypeService.DeleteAllCellTypeClassificationsAsync(importId);
            await saveCellTypeService.SaveCellTypeClassificationsAsync(importId, predictions, ClassificationMethod);



            // 6. Store in cache (keyed to this project + import + method) and return
            _cachedPredictions = predictions;
            _cachedImportId = importId;
            _cachedMethod = ClassificationMethod;
            _cachedProjectPath = projectDatabasePath;
            return _cachedPredictions;
        }

        /// <summary>
        /// Predicts cell types for all runs in the proteomics data
        /// </summary>
        public Dictionary<string, CellTypePredictionResult> PredictCellTypesForAllRuns(
            ProteomicsData proteomicsData,
            IProgressReporter progressReporter = null,
            Dictionary<string, HashSet<string>> keyMarkers = null,
            HashSet<string> excludedCellTypes = null,
            Dictionary<string, double> priorWeights = null)
        {
            if (!IsLoaded)
                throw new InvalidOperationException("Reference database not loaded");

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

                var prediction = PredictCellTypeForRun(proteomicsData, runName, keyMarkers, excludedCellTypes, priorWeights);
                predictions[runName] = prediction;
            }

            return predictions;
        }

        /// <summary>
        /// Predicts cell type for a single run
        /// </summary>
        public CellTypePredictionResult PredictCellTypeForRun(
            ProteomicsData proteomicsData, 
            string runName,
            Dictionary<string, HashSet<string>> keyMarkers = null,
            HashSet<string> excludedCellTypes = null,
            Dictionary<string, double> priorWeights = null)
        {
            if (!IsLoaded || _classifier == null)
                return new CellTypePredictionResult();

            if (proteomicsData == null || string.IsNullOrEmpty(runName))
                return new CellTypePredictionResult();

            var proteinAbundances = ExtractProteinAbundances(proteomicsData, runName);

            return _classifier.PredictCellType(proteinAbundances, keyMarkers, excludedCellTypes, priorWeights);
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

            // Curated hues that avoid yellow (poor contrast on white/light backgrounds).
            // Hue 60° (yellow) is replaced by 25° (orange).
            for (int i = 0; i < cellTypes.Count; i++)
            {
                var hue = (i * 360.0 / cellTypes.Count) % 360;

                // Shift yellow range (45–75°) to dark purple (~280°)
                if (hue >= 45 && hue <= 75)
                    hue = 280;

                var color = ColorFromHSV(hue, 0.7, 0.9);
                colorMap[cellTypes[i]] = color;
            }

            // Add Undetermined as grey
            colorMap["Undetermined"] = System.Windows.Media.Color.FromRgb(180, 180, 180);

            return colorMap;
        }


        /// <summary>
        /// Builds the per-cell gene → abundance dictionary fed to the predictor.
        /// Visibility widened (logic unchanged) so <see cref="Services.ClassifierEvaluationService"/> can score cells
        /// through the exact same extraction the app uses, rather than a copy that could silently drift from it.
        /// </summary>
        public static Dictionary<string, double> ExtractProteinAbundances(ProteomicsData proteomicsData, string runName)
        {
            var abundances = new Dictionary<string, double>();

            foreach (var proteinGroup in proteomicsData.ProteinQuantMatrix.Keys)
            {
                if (proteomicsData.ProteinQuantMatrix[proteinGroup].ContainsKey(runName))
                {
                    double abundance = proteomicsData.ProteinQuantMatrix[proteinGroup][runName];

                    if (abundance > 0)
                    {
                        var geneNames = GeneNameUtility.ExtractGeneNames(proteomicsData, proteinGroup);
                        foreach (var geneName in geneNames)
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
            }

            // DEBUG OUTPUT


            if (abundances.Count > 0)
            {

            }

            return abundances;
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

        }

        /// <summary>
        /// Deletes all classifications from database and clears cache
        /// </summary>
        /// <summary>
        /// Deletes all classifications from database and clears cache
        /// </summary>
        public async Task DeleteAllClassificationsAsync(string projectDatabasePath, int importId)
        {
            var cellTypeService = new CellTypeClassificationService(projectDatabasePath);
            await cellTypeService.DeleteAllCellTypeClassificationsAsync(importId);
            ClearCache();

        }

        // ================================================================
        // === FULLY CORRECTED DIAGNOSTIC METHOD IS BELOW ===
        // ================================================================

        /// <summary>
        /// Comprehensive diagnostic to analyze protein-to-gene mapping quality
        /// </summary>
        public void DiagnoseProteinGeneMapping(ProteomicsData proteomicsData, string runName)
        {






            // --- INCORRECT 'if' BLOCK REMOVED ---
            // The check 'proteomicsData.ProteinQuantMatrix.ContainsKey(runName)'
            // was incorrect because the dictionary keys are protein names, not run names.

            // Step 1: Get all proteins for this run
            var proteinsInRun = proteomicsData.ProteinQuantMatrix.Keys
                .Where(protein => proteomicsData.ProteinQuantMatrix[protein].ContainsKey(runName))
                .ToList();





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





            if (sampleUnmapped.Count > 0)
            {

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






            // Step 4: Check how many exist in transcriptomic database
            if (!IsLoaded)
            {


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







            // Added safety check for divide-by-zero
            if (extractedGenes.Count > 0)
            {

            }
            else
            {

            }


            if (matchedGenes.Count > 0)
            {


            }

            if (unmatchedGenes.Count > 0)
            {


            }

            // Step 5: Check marker overlap


            foreach (var cellType in _database.CellTypeProfiles.Keys)
            {
                // Marker/signature sets of the active classifier (null for methods that don't expose them).
                var markerSets = _classifier?.MarkerSets;
                if (markerSets != null && markerSets.ContainsKey(cellType))
                {
                    var markers = markerSets[cellType];
                    int overlap = matchedGenes.Count(g => markers.Contains(g));

                }
            }



        }

        /// <summary>
        /// Exports detailed classification diagnostics to a TSV file for analysis
        /// </summary>
        public async Task ExportClassificationDiagnosticsAsync(
            string outputPath,
            Dictionary<string, CellTypePredictionResult> predictions,
            ProteomicsData proteomicsData,
            IProgressReporter progress = null)
        {
            if (predictions == null || predictions.Count == 0)
            {
                throw new InvalidOperationException("No predictions available to export");
            }

            if (!IsLoaded || _classifier == null)
            {
                throw new InvalidOperationException("Reference database not loaded");
            }

            progress?.ReportMessage("Preparing classification diagnostics export...");

            // Collect all unique markers/signatures across all cell types (empty if the method exposes none).
            var allMarkers = new HashSet<string>();
            foreach (var markers in (_classifier.MarkerSets?.Values ?? Enumerable.Empty<HashSet<string>>()))
            {
                foreach (var marker in markers)
                {
                    allMarkers.Add(marker);
                }
            }
            var sortedMarkers = allMarkers.OrderBy(m => m).ToList();

            // Get all cell types
            var cellTypes = _database.CellTypeProfiles.Keys.OrderBy(ct => ct).ToList();

            // Build header
            var headerParts = new List<string>
            {
                "RawFile",
                "PredictedCellType",
                "Confidence",
                "CompositeScore",
                "SpearmanCorr",
                "SpecificityScore",
                "HypergeometricPValue",
                "MarkerCoverage",
                "KeyMarkerAdjustment",
                "PriorAdjustment",
                "ProteinsDetected",
                "GenesMatched"
            };

            // Add score columns for each cell type
            foreach (var ct in cellTypes)
            {
                headerParts.Add($"Score_{ct}");
            }

            // Add marker columns
            foreach (var marker in sortedMarkers)
            {
                headerParts.Add($"Marker_{marker}");
            }

            var lines = new List<string> { string.Join("\t", headerParts) };

            int processed = 0;
            int total = predictions.Count;

            foreach (var kvp in predictions.OrderBy(p => p.Key))
            {
                var runName = kvp.Key;
                var result = kvp.Value;

                processed++;
                if (processed % 50 == 0)
                {
                    progress?.ReportProgress($"Processing {processed}/{total}...");
                }

                // Extract protein abundances for this run to check marker presence
                var proteinAbundances = ExtractProteinAbundances(proteomicsData, runName);

                // Specificity / hypergeometric p / coverage exist ONLY in the Standard scorer. Every other method
                // (Quantitative, Marker, or anything added later) leaves them at their neutral defaults, which must
                // be written blank rather than as 0.0000 / 0.000E+000 - otherwise the TSV records a never-measured
                // quantity as if it had been measured. Gate on "is Standard" rather than listing the exceptions, so
                // a new scorer cannot silently start exporting fabricated metrics.
                bool standard = string.Equals(result.ScorerMethod, "Standard", StringComparison.OrdinalIgnoreCase);
                bool quant = !standard;

                var rowParts = new List<string>
                {
                    runName,
                    result.TopCellType ?? "Unknown",
                    result.Confidence.ToString("F4"),
                    result.TopScore?.CompositeScore.ToString("F4") ?? "",
                    result.TopScore?.SpearmanCorrelation.ToString("F4") ?? "",
                    quant ? "" : (result.TopScore?.SpecificityScore.ToString("F4") ?? ""),
                    quant ? "" : (result.TopScore?.HypergeometricPValue.ToString("E3") ?? ""),
                    quant ? "" : (result.TopScore?.MarkerCoverage.ToString("F4") ?? ""),
                    result.TopScore?.KeyMarkerAdjustment.ToString("F3") ?? "",
                    result.TopScore?.PriorAdjustment.ToString("F3") ?? "",
                    proteinAbundances.Count.ToString(),
                    CountGenesMatchingDatabase(proteinAbundances).ToString()
                };

                // Add scores for each cell type
                foreach (var ct in cellTypes)
                {
                    if (result.Scores != null && result.Scores.TryGetValue(ct, out var score))
                    {
                        rowParts.Add(score.CompositeScore.ToString("F4"));
                    }
                    else
                    {
                        rowParts.Add("");
                    }
                }

                // Add marker detection (X if found, empty if not)
                foreach (var marker in sortedMarkers)
                {
                    rowParts.Add(proteinAbundances.ContainsKey(marker) ? "X" : "");
                }

                lines.Add(string.Join("\t", rowParts));
            }

            progress?.ReportMessage($"Writing {lines.Count} rows to file...");

            await Task.Run(() => File.WriteAllLines(outputPath, lines));

            progress?.ReportProgress($"Exported diagnostics to {outputPath}");
        }

        private int CountGenesMatchingDatabase(Dictionary<string, double> proteinAbundances)
        {
            if (!IsLoaded) return 0;

            var allDbGenes = new HashSet<string>();
            foreach (var profile in _database.CellTypeProfiles.Values)
            {
                foreach (var gene in profile.MedianExpression.Keys)
                {
                    allDbGenes.Add(gene);
                }
            }

            return proteinAbundances.Keys.Count(g => allDbGenes.Contains(g));
        }

        /// <summary>
        /// Exports a gene-presence-by-cell-type summary for marker discovery.
        /// For each gene found in proteomics, shows:
        /// - What fraction of cells of each classified type detect it
        /// - The transcriptomic median expression per cell type (from reference DB)
        /// This cross-references proteomics detectability with transcriptomic specificity.
        /// </summary>
        public async Task ExportGenePresenceByCellTypeAsync(
            string outputPath,
            Dictionary<string, CellTypePredictionResult> predictions,
            ProteomicsData proteomicsData,
            IProgressReporter progress = null)
        {
            if (predictions == null || predictions.Count == 0)
                throw new InvalidOperationException("No predictions available");
            if (!IsLoaded)
                throw new InvalidOperationException("Reference database not loaded");

            progress?.ReportMessage("Building gene presence matrix...");

            // Group runs by predicted cell type (exclude Undetermined)
            var runsByCellType = new Dictionary<string, List<string>>();
            foreach (var kvp in predictions)
            {
                var ct = kvp.Value.TopCellType;
                if (string.IsNullOrEmpty(ct) || ct == "Undetermined") continue;
                if (!runsByCellType.ContainsKey(ct))
                    runsByCellType[ct] = new List<string>();
                runsByCellType[ct].Add(kvp.Key);
            }

            var cellTypes = runsByCellType.Keys.OrderBy(ct => ct).ToList();

            // Collect all genes detected across all runs
            var allGenes = new HashSet<string>();
            // gene -> cellType -> count of runs detecting it
            var geneDetectionCounts = new Dictionary<string, Dictionary<string, int>>();

            int processed = 0;
            int total = predictions.Count;

            foreach (var kvp in predictions)
            {
                processed++;
                if (processed % 50 == 0)
                    progress?.ReportProgress($"Scanning run {processed}/{total}...");

                var ct = kvp.Value.TopCellType;
                if (string.IsNullOrEmpty(ct) || ct == "Undetermined") continue;

                var abundances = ExtractProteinAbundances(proteomicsData, kvp.Key);
                foreach (var gene in abundances.Keys)
                {
                    allGenes.Add(gene);
                    if (!geneDetectionCounts.ContainsKey(gene))
                        geneDetectionCounts[gene] = new Dictionary<string, int>();
                    if (!geneDetectionCounts[gene].ContainsKey(ct))
                        geneDetectionCounts[gene][ct] = 0;
                    geneDetectionCounts[gene][ct]++;
                }
            }

            progress?.ReportMessage($"Writing {allGenes.Count} genes to file...");

            // Build header
            var headerParts = new List<string> { "Gene" };
            foreach (var ct in cellTypes)
                headerParts.Add($"Prot_{ct}");    // fraction detected in proteomics
            foreach (var ct in cellTypes)
                headerParts.Add($"Transcr_{ct}"); // median expression from reference DB

            var lines = new List<string> { string.Join("\t", headerParts) };

            var sortedGenes = allGenes.OrderBy(g => g).ToList();
            foreach (var gene in sortedGenes)
            {
                var parts = new List<string> { gene };

                // Proteomics detection fractions
                foreach (var ct in cellTypes)
                {
                    int detected = 0;
                    if (geneDetectionCounts.ContainsKey(gene) && geneDetectionCounts[gene].ContainsKey(ct))
                        detected = geneDetectionCounts[gene][ct];
                    int totalRuns = runsByCellType[ct].Count;
                    double fraction = totalRuns > 0 ? (double)detected / totalRuns : 0;
                    parts.Add(fraction.ToString("F4"));
                }

                // Transcriptomic median expression
                foreach (var ct in cellTypes)
                {
                    double expr = 0;
                    if (_database.CellTypeProfiles.TryGetValue(ct, out var profile) &&
                        profile.MedianExpression.TryGetValue(gene, out double val))
                        expr = val;
                    parts.Add(expr.ToString("F4"));
                }

                lines.Add(string.Join("\t", parts));
            }

            await Task.Run(() => File.WriteAllLines(outputPath, lines));

            progress?.ReportProgress($"Exported gene presence for {allGenes.Count} genes across {cellTypes.Count} cell types");
        }
    }
}