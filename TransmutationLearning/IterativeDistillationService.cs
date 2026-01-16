using System;
using System.Collections.Generic;
using System.Linq;
using TransmutationLearning.Services;

namespace TransmutationLearning
{
    #region Data Structures

    /// <summary>
    /// Progress reporting for UI updates during iterative distillation
    /// </summary>
    public class IterationProgress
    {
        public int Current { get; set; }
        public int Total { get; set; }
        public string Status { get; set; }
        public double Percent => Total > 0 ? (Current * 100.0 / Total) : 0;
    }

    /// <summary>
    /// Summary of a single iteration
    /// </summary>
    public class IterationSummary
    {
        public int Iteration { get; set; }
        public int ProteinsUsed { get; set; }
        public int MarkersSelected { get; set; }
        public int LabelsChanged { get; set; }
        public double ChangePercent { get; set; }
        public string Notes { get; set; }  // e.g., "Relaxed p-value to 0.05"
    }

    /// <summary>
    /// Settings for iterative distillation
    /// </summary>
    public class IterativeDistillationSettings
    {
        // Pre-filter (Iteration 0)
        public double MinDetectionRate { get; set; } = 0.30;
        public double VariancePercentile { get; set; } = 0.50;  // Top 50%

        // Auto marker selection
        public double MarkerPValue { get; set; } = 0.01;
        public double MarkerMinDelta { get; set; } = 0.50;
        public double MarkerMinDetection { get; set; } = 0.30;
        public int MinMarkersRequired { get; set; } = 20;
        public int MaxMarkersAllowed { get; set; } = 200;

        // Convergence
        public double ConvergenceThreshold { get; set; } = 0.02;  // <2% change
        public int MaxIterations { get; set; } = 5;

        // kNN parameters (from UI)
        public int K { get; set; } = 10;
        public double MinConfidence { get; set; } = 0.60;
        public double PriorInfluence { get; set; } = 0.50;
        public int ProtectionRank { get; set; } = 2;

        // Dual-mode similarity: "Dark Matter" metric
        // Weight for detection pattern (Jaccard) vs intensity (Cosine)
        // 0 = pure intensity, 1 = pure detection pattern, 0.3 = recommended default
        public double DetectionPatternWeight { get; set; } = 0.30;

        // Biological Gravity: PPI-informed similarity
        // PPIService instance (null = disabled)
        public PPIService PPIService { get; set; } = null;
        // Weight for PPI boost (0 = no effect, higher = more biological gravity)
        public double PPIWeight { get; set; } = 0.0;
    }

    /// <summary>
    /// Complete result of iterative distillation
    /// </summary>
    public class IterativeDistillationResult
    {
        public bool Converged { get; set; }
        public int IterationsRun { get; set; }
        public List<IterationSummary> History { get; set; } = new List<IterationSummary>();
        public DistilledDataset FinalLabels { get; set; }
        public List<ProteinStatistics> AutoSelectedMarkers { get; set; } = new List<ProteinStatistics>();
        public IterativeDistillationSettings SettingsUsed { get; set; }

        // Summary helpers
        public int TotalCells => FinalLabels?.TotalCells ?? 0;
        public int TotalReclassified => FinalLabels?.ReclassifiedCount ?? 0;
        public int FinalMarkerCount => AutoSelectedMarkers?.Count ?? 0;
    }

    #endregion

    /// <summary>
    /// Service for iterative smart distillation.
    /// Automatically selects discriminative markers and refines labels through iteration.
    /// </summary>
    public class IterativeDistillationService
    {
        private readonly KnnDistillationService _knnService = new KnnDistillationService();
        private readonly FeatureSelectionService _featureService = new FeatureSelectionService();

        /// <summary>
        /// Run iterative distillation with automatic marker selection
        /// </summary>
        /// <param name="retainedCells">Cells that passed confidence filtering</param>
        /// <param name="proteinMatrix">Protein expression matrix</param>
        /// <param name="expectedDistribution">User-defined expected cell type distribution</param>
        /// <param name="settings">Iterative distillation settings</param>
        /// <param name="progress">Optional progress reporter for UI updates</param>
        /// <returns>Complete iterative distillation result</returns>
        public IterativeDistillationResult RunIterativeDistillation(
            List<CellClassification> retainedCells,
            Dictionary<string, Dictionary<string, double>> proteinMatrix,
            ExpectedDistribution expectedDistribution,
            IterativeDistillationSettings settings,
            IProgress<IterationProgress> progress = null)
        {
            var result = new IterativeDistillationResult
            {
                SettingsUsed = settings
            };

            // Validate inputs
            if (retainedCells == null || retainedCells.Count == 0)
            {
                result.History.Add(new IterationSummary
                {
                    Iteration = 0,
                    Notes = "Error: No retained cells provided"
                });
                return result;
            }

            if (proteinMatrix == null || proteinMatrix.Count == 0)
            {
                result.History.Add(new IterationSummary
                {
                    Iteration = 0,
                    Notes = "Error: No protein matrix provided"
                });
                return result;
            }

            // ═══════════════════════════════════════════════════════════════
            // ITERATION 0: Bootstrap with pre-filtered proteins
            // ═══════════════════════════════════════════════════════════════
            progress?.Report(new IterationProgress
            {
                Current = 0,
                Total = settings.MaxIterations + 1,
                Status = "Pre-filtering proteins..."
            });

            var bootstrapProteins = PreFilterProteins(retainedCells, proteinMatrix, settings);

            progress?.Report(new IterationProgress
            {
                Current = 0,
                Total = settings.MaxIterations + 1,
                Status = $"Running bootstrap kNN with {bootstrapProteins.Count} proteins..."
            });

            var currentLabels = _knnService.RunKnnDistillation(
                retainedCells,
                proteinMatrix,
                expectedDistribution,
                settings.K,
                settings.PriorInfluence,
                settings.MinConfidence,
                settings.ProtectionRank,
                bootstrapProteins,
                settings.DetectionPatternWeight,
                settings.PPIService,
                settings.PPIWeight);

            result.History.Add(new IterationSummary
            {
                Iteration = 0,
                ProteinsUsed = bootstrapProteins.Count,
                MarkersSelected = 0,
                LabelsChanged = currentLabels.ReclassifiedCount,
                ChangePercent = currentLabels.ReclassificationPercent,
                Notes = "Bootstrap iteration"
            });

            // ═══════════════════════════════════════════════════════════════
            // ITERATIONS 1-N: Refine with auto-selected markers
            // ═══════════════════════════════════════════════════════════════
            Dictionary<string, string> previousLabelMap = null;
            List<ProteinStatistics> selectedMarkers = null;

            for (int iter = 1; iter <= settings.MaxIterations; iter++)
            {
                progress?.Report(new IterationProgress
                {
                    Current = iter,
                    Total = settings.MaxIterations + 1,
                    Status = $"Iteration {iter}: Selecting markers..."
                });

                // Save previous labels for convergence check
                previousLabelMap = new Dictionary<string, string>(currentLabels.DistilledLabels);

                // Auto-select markers based on current labels
                var (markers, selectionNotes) = AutoSelectMarkers(
                    retainedCells,
                    proteinMatrix,
                    currentLabels,
                    settings);

                selectedMarkers = markers;

                if (selectedMarkers.Count < settings.MinMarkersRequired)
                {
                    result.History.Add(new IterationSummary
                    {
                        Iteration = iter,
                        ProteinsUsed = 0,
                        MarkersSelected = selectedMarkers.Count,
                        Notes = $"Stopped: Only {selectedMarkers.Count} markers (min {settings.MinMarkersRequired})"
                    });
                    break;
                }

                // Build protein subset from selected markers
                var markerProteins = new HashSet<string>(selectedMarkers.Select(m => m.ProteinName));

                progress?.Report(new IterationProgress
                {
                    Current = iter,
                    Total = settings.MaxIterations + 1,
                    Status = $"Iteration {iter}: Running kNN with {markerProteins.Count} markers..."
                });

                // Re-run distillation with marker subset
                currentLabels = _knnService.RunKnnDistillation(
                    retainedCells,
                    proteinMatrix,
                    expectedDistribution,
                    settings.K,
                    settings.PriorInfluence,
                    settings.MinConfidence,
                    settings.ProtectionRank,
                    markerProteins,
                    settings.DetectionPatternWeight,
                    settings.PPIService,
                    settings.PPIWeight);

                // Check convergence
                int changed = CountLabelChanges(previousLabelMap, currentLabels.DistilledLabels);
                double changePercent = (double)changed / retainedCells.Count;

                result.History.Add(new IterationSummary
                {
                    Iteration = iter,
                    ProteinsUsed = markerProteins.Count,
                    MarkersSelected = selectedMarkers.Count,
                    LabelsChanged = changed,
                    ChangePercent = changePercent * 100,
                    Notes = selectionNotes
                });

                if (changePercent < settings.ConvergenceThreshold)
                {
                    result.Converged = true;
                    result.IterationsRun = iter;
                    break;
                }

                result.IterationsRun = iter;
            }

            // ═══════════════════════════════════════════════════════════════
            // FINALIZE
            // ═══════════════════════════════════════════════════════════════
            result.FinalLabels = currentLabels;
            result.AutoSelectedMarkers = selectedMarkers ?? new List<ProteinStatistics>();

            if (!result.Converged && result.IterationsRun >= settings.MaxIterations)
            {
                result.History.Last().Notes += " (max iterations reached)";
            }

            progress?.Report(new IterationProgress
            {
                Current = settings.MaxIterations + 1,
                Total = settings.MaxIterations + 1,
                Status = result.Converged
                    ? $"Converged after {result.IterationsRun} iterations"
                    : $"Completed {result.IterationsRun} iterations (not converged)"
            });

            return result;
        }

        /// <summary>
        /// Pre-filter proteins for bootstrap iteration (detection + variance)
        /// </summary>
        private HashSet<string> PreFilterProteins(
            List<CellClassification> cells,
            Dictionary<string, Dictionary<string, double>> proteinMatrix,
            IterativeDistillationSettings settings)
        {
            var retainedRuns = new HashSet<string>(cells.Select(c => c.Run));
            int totalCells = retainedRuns.Count;

            // Compute detection rate and variance for each protein
            var proteinStats = new List<(string protein, double detectionRate, double variance)>();

            foreach (var kvp in proteinMatrix)
            {
                string protein = kvp.Key;
                var runIntensities = kvp.Value;

                // Get intensities for retained cells only
                var values = new List<double>();
                int detected = 0;

                foreach (var run in retainedRuns)
                {
                    if (runIntensities.TryGetValue(run, out double intensity) && intensity > 0)
                    {
                        values.Add(Math.Log2(intensity));
                        detected++;
                    }
                }

                double detectionRate = (double)detected / totalCells;

                // Compute variance if we have enough values
                double variance = 0;
                if (values.Count >= 3)
                {
                    double mean = values.Average();
                    variance = values.Sum(v => (v - mean) * (v - mean)) / (values.Count - 1);
                }

                proteinStats.Add((protein, detectionRate, variance));
            }

            // Step 1: Filter by detection rate
            double minDetection = settings.MinDetectionRate;
            var detectionFiltered = proteinStats
                .Where(p => p.detectionRate >= minDetection)
                .ToList();

            // If too few pass, relax threshold
            if (detectionFiltered.Count < 100 && minDetection > 0.15)
            {
                minDetection = 0.20;
                detectionFiltered = proteinStats
                    .Where(p => p.detectionRate >= minDetection)
                    .ToList();

                if (detectionFiltered.Count < 100)
                {
                    minDetection = 0.10;
                    detectionFiltered = proteinStats
                        .Where(p => p.detectionRate >= minDetection)
                        .ToList();
                }
            }

            // Step 2: Filter by variance - keep top N% most variable
            if (detectionFiltered.Count == 0)
            {
                return new HashSet<string>(proteinMatrix.Keys);
            }

            var sortedByVariance = detectionFiltered
                .OrderByDescending(p => p.variance)
                .ToList();

            int keepCount = (int)(sortedByVariance.Count * (1.0 - settings.VariancePercentile));
            keepCount = Math.Max(keepCount, 50);  // Keep at least 50
            keepCount = Math.Min(keepCount, sortedByVariance.Count);

            var result = new HashSet<string>(
                sortedByVariance
                    .Take(keepCount)
                    .Select(p => p.protein));

            return result;
        }

        /// <summary>
        /// Auto-select markers based on current labels with self-adjusting thresholds
        /// </summary>
        private (List<ProteinStatistics> markers, string notes) AutoSelectMarkers(
            List<CellClassification> cells,
            Dictionary<string, Dictionary<string, double>> proteinMatrix,
            DistilledDataset currentLabels,
            IterativeDistillationSettings settings)
        {
            var notes = new List<string>();

            // Build a temporary dataset structure for FeatureSelectionService
            var tempDataset = new TransmutationDataset
            {
                ProteinMatrix = proteinMatrix,
                AllProteins = proteinMatrix.Keys.ToList()
            };

            // Build filtered dataset with current distilled labels
            var tempFiltered = new FilteredDataset
            {
                RetainedCells = cells
            };

            // Start with strict criteria
            double pValue = settings.MarkerPValue;
            double minDelta = settings.MarkerMinDelta;
            double minDetection = settings.MarkerMinDetection;

            List<ProteinStatistics> selectedMarkers = null;
            int attempts = 0;
            const int maxAttempts = 4;

            while (attempts < maxAttempts)
            {
                attempts++;

                var criteria = new FeatureSelectionCriteria
                {
                    MaxPValue = pValue,
                    MinDetectionRate = minDetection,
                    MinSpecificityDelta = minDelta,
                    RequireRobustness = false,  // Don't require robustness for auto-selection
                    MaxMissingRate = 0.80
                };

                // Compute protein statistics using current (distilled) labels
                var featureResult = _featureService.ComputeProteinStatistics(
                    tempFiltered, tempDataset, criteria, currentLabels);

                // Get proteins that pass the filter
                selectedMarkers = featureResult.AllProteinStats
                    .Where(p => p.PassesFilter)
                    .OrderByDescending(p => p.WeightedSpecificityScore)
                    .ToList();

                // Check if we have enough markers
                if (selectedMarkers.Count >= settings.MinMarkersRequired)
                {
                    // No cap - let all passing markers through for user refinement in Feature Selection
                    break;
                }

                // Not enough markers - relax thresholds
                if (attempts == 1)
                {
                    // First relaxation: loosen p-value
                    pValue = 0.05;
                    notes.Add("Relaxed p≤0.05");
                }
                else if (attempts == 2)
                {
                    // Second relaxation: loosen delta
                    minDelta = 0.25;
                    notes.Add("Relaxed Δ≥0.25");
                }
                else if (attempts == 3)
                {
                    // Third relaxation: loosen detection
                    minDetection = 0.15;
                    notes.Add("Relaxed det≥15%");
                }
            }

            string notesStr = notes.Count > 0 ? string.Join(", ", notes) : "Standard criteria";
            return (selectedMarkers ?? new List<ProteinStatistics>(), notesStr);
        }

        /// <summary>
        /// Count how many labels changed between iterations
        /// </summary>
        private int CountLabelChanges(
            Dictionary<string, string> previousLabels,
            Dictionary<string, string> currentLabels)
        {
            int changed = 0;
            foreach (var kvp in currentLabels)
            {
                if (previousLabels.TryGetValue(kvp.Key, out var prevLabel))
                {
                    if (prevLabel != kvp.Value)
                        changed++;
                }
            }
            return changed;
        }
    }
}
