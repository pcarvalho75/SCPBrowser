using System;
using System.Collections.Generic;
using System.Linq;

namespace TransmutationLearning
{
    /// <summary>
    /// Service for computing protein statistics and selecting marker proteins
    /// </summary>
    public class FeatureSelectionService
    {
        /// <summary>
        /// Compute statistics for all proteins based on retained cells
        /// </summary>
        /// <param name="filteredData">Filtered dataset from confidence filtering</param>
        /// <param name="dataset">Full transmutation dataset</param>
        /// <param name="criteria">Selection criteria</param>
        /// <param name="distilledData">Optional distilled dataset - if provided and applied, uses distilled labels</param>
        /// <param name="restrictToProteins">Optional set of protein names to restrict analysis to (e.g., from distillation)</param>
        public FeatureSelectionResult ComputeProteinStatistics(
            FilteredDataset filteredData,
            TransmutationDataset dataset,
            FeatureSelectionCriteria criteria,
            DistilledDataset distilledData = null,
            HashSet<string> restrictToProteins = null)
        {
            var result = new FeatureSelectionResult { Criteria = criteria };

            // Get retained runs for lookup
            var retainedRuns = new HashSet<string>(filteredData.RetainedCells.Select(c => c.Run));

            // Build run -> cell type mapping
            // Use distilled labels if distillation was applied, otherwise use original labels
            Dictionary<string, string> runToCellType;
            if (distilledData != null && distilledData.DistillationApplied)
            {
                runToCellType = new Dictionary<string, string>();
                foreach (var cell in filteredData.RetainedCells)
                {
                    // Use distilled label if available, fall back to original
                    if (distilledData.DistilledLabels.TryGetValue(cell.Run, out var distilledLabel))
                        runToCellType[cell.Run] = distilledLabel;
                    else
                        runToCellType[cell.Run] = cell.Labels;
                }
            }
            else
            {
                runToCellType = filteredData.RetainedCells.ToDictionary(c => c.Run, c => c.Labels);
            }

            // Get cell types present in data (using the effective labels)
            var cellTypes = runToCellType.Values
                .Distinct()
                .OrderBy(c => c)
                .ToList();

            // Determine which proteins to process
            var proteinsToProcess = restrictToProteins != null && restrictToProteins.Count > 0
                ? dataset.AllProteins.Where(p => restrictToProteins.Contains(p)).ToList()
                : dataset.AllProteins;

            // Process each protein
            foreach (var protein in proteinsToProcess)
            {
                var stats = ComputeSingleProteinStats(protein, dataset, retainedRuns, runToCellType, cellTypes);

                // Evaluate against criteria
                stats.PassesFilter = EvaluateCriteria(stats, criteria);

                result.AllProteinStats.Add(stats);
            }

            // Sort by weighted specificity score descending (detection-weighted)
            result.AllProteinStats = result.AllProteinStats
                .OrderByDescending(p => p.WeightedSpecificityScore)
                .ToList();

            return result;
        }

        /// <summary>
        /// Compute statistics for a single protein
        /// </summary>
        private ProteinStatistics ComputeSingleProteinStats(
            string protein,
            TransmutationDataset dataset,
            HashSet<string> retainedRuns,
            Dictionary<string, string> runToCellType,
            List<string> cellTypes)
        {
            var stats = new ProteinStatistics { ProteinName = protein };

            // Get expression values for this protein across retained runs
            if (!dataset.ProteinMatrix.TryGetValue(protein, out var proteinData))
            {
                // Protein not in matrix - shouldn't happen but handle gracefully
                return stats;
            }

            // Group expression values by cell type
            var expressionByType = new Dictionary<string, List<double>>();
            foreach (var cellType in cellTypes)
            {
                expressionByType[cellType] = new List<double>();
            }

            int totalCells = 0;
            int detectedCells = 0;

            foreach (var run in retainedRuns)
            {
                if (!runToCellType.TryGetValue(run, out var cellType))
                    continue;

                if (!expressionByType.ContainsKey(cellType))
                    continue;

                totalCells++;

                // Check if protein was detected in this run
                if (proteinData.TryGetValue(run, out var intensity) && intensity > 0)
                {
                    expressionByType[cellType].Add(Math.Log2(intensity));
                    detectedCells++;
                }
            }

            // Compute per-cell-type metrics
            foreach (var cellType in cellTypes)
            {
                var values = expressionByType[cellType];
                var cellCount = retainedRuns.Count(r => runToCellType.TryGetValue(r, out var ct) && ct == cellType);

                var metrics = new ProteinCellTypeMetrics
                {
                    CellType = cellType,
                    CellCount = cellCount,
                    DetectedCount = values.Count,
                    DetectionRate = cellCount > 0 ? (double)values.Count / cellCount : 0,
                    MedianExpression = values.Count > 0 ? StatisticsHelper.Median(values) : 0
                };

                stats.CellTypeMetrics[cellType] = metrics;
            }

            // Overall detection rate
            stats.OverallDetectionRate = totalCells > 0 ? (double)detectedCells / totalCells : 0;

            // Find best and second-best cell types by median expression
            var rankedTypes = stats.CellTypeMetrics.Values
                .Where(m => m.DetectionRate > 0)
                .OrderByDescending(m => m.MedianExpression)
                .ToList();

            if (rankedTypes.Count >= 1)
            {
                var best = rankedTypes[0];
                stats.BestCellType = best.CellType;
                stats.BestCellTypeExpression = best.MedianExpression;
                stats.BestCellTypeDetection = best.DetectionRate;

                if (rankedTypes.Count >= 2)
                {
                    var second = rankedTypes[1];
                    stats.SecondBestCellType = second.CellType;
                    stats.SecondBestCellTypeExpression = second.MedianExpression;
                    stats.SpecificityDelta = best.MedianExpression - second.MedianExpression;
                }
                else
                {
                    stats.SecondBestCellType = "-";
                    stats.SpecificityDelta = best.MedianExpression; // No second type means very specific
                }

                // Weighted score: prioritize proteins with high specificity AND high detection rate
                // A protein detected in 80% of T-Cells with good delta is better than
                // one with same delta but only 20% detection
                stats.WeightedSpecificityScore = stats.SpecificityDelta * stats.BestCellTypeDetection;
            }

            // Kruskal-Wallis test for differential expression across cell types
            var groups = expressionByType.Values
                .Where(v => v.Count > 0)
                .ToList();

            if (groups.Count >= 2)
            {
                var (H, pValue) = StatisticsHelper.KruskalWallisTest(groups);
                stats.KruskalWallisH = H;
                stats.KruskalWallisPValue = pValue;
            }
            else
            {
                stats.KruskalWallisH = 0;
                stats.KruskalWallisPValue = 1;
            }

            // Robustness check - minimum cells per type
            stats.MinCellCount = stats.CellTypeMetrics.Values
                .Where(m => m.DetectionRate > 0)
                .Select(m => m.DetectedCount)
                .DefaultIfEmpty(0)
                .Min();
            stats.IsRobust = stats.MinCellCount >= 3; // At least 3 cells in smallest group

            return stats;
        }

        /// <summary>
        /// Check if protein passes selection criteria
        /// </summary>
        private bool EvaluateCriteria(ProteinStatistics stats, FeatureSelectionCriteria criteria)
        {
            // P-value threshold
            if (stats.KruskalWallisPValue > criteria.MaxPValue)
                return false;

            // Detection rate in best cell type
            if (stats.BestCellTypeDetection < criteria.MinDetectionRate)
                return false;

            // Specificity delta
            if (stats.SpecificityDelta < criteria.MinSpecificityDelta)
                return false;

            // Robustness requirement
            if (criteria.RequireRobustness && !stats.IsRobust)
                return false;

            // Overall detection (not too many missing)
            if (stats.OverallDetectionRate < (1 - criteria.MaxMissingRate))
                return false;

            return true;
        }

        /// <summary>
        /// Select all proteins that pass the filter
        /// </summary>
        public void SelectAllPassing(FeatureSelectionResult result)
        {
            foreach (var protein in result.AllProteinStats)
            {
                protein.IsSelected = protein.PassesFilter;
            }
            UpdateSelectedMarkers(result);
        }

        /// <summary>
        /// Clear all selections
        /// </summary>
        public void ClearSelection(FeatureSelectionResult result)
        {
            foreach (var protein in result.AllProteinStats)
            {
                protein.IsSelected = false;
            }
            UpdateSelectedMarkers(result);
        }

        /// <summary>
        /// Update the selected markers list and groupings
        /// </summary>
        public void UpdateSelectedMarkers(FeatureSelectionResult result)
        {
            result.SelectedMarkers = result.AllProteinStats
                .Where(p => p.IsSelected)
                .OrderByDescending(p => p.WeightedSpecificityScore)
                .ToList();

            // Group by best cell type
            result.MarkersByCellType = result.SelectedMarkers
                .GroupBy(p => p.BestCellType)
                .ToDictionary(
                    g => g.Key,
                    g => g.OrderByDescending(p => p.WeightedSpecificityScore).ToList());
        }

        /// <summary>
        /// Parse criteria from UI combo box values
        /// </summary>
        public static FeatureSelectionCriteria ParseCriteriaFromUI(
            string pValueText,
            string detectionText,
            string specificityText,
            string cellFractionText,
            bool requireRobust)
        {
            var criteria = new FeatureSelectionCriteria { RequireRobustness = requireRobust };

            // Parse p-value (e.g., "0.01", "0.001", "0.05")
            if (!string.IsNullOrEmpty(pValueText))
            {
                var cleaned = pValueText.Replace("p < ", "").Replace("≤ ", "").Trim();
                if (double.TryParse(cleaned, out var pVal))
                    criteria.MaxPValue = pVal;
            }

            // Parse detection rate (e.g., "30%", "50%", "Any")
            if (!string.IsNullOrEmpty(detectionText) && detectionText != "Any")
            {
                var cleaned = detectionText.Replace("%", "").Replace("≥ ", "").Trim();
                if (double.TryParse(cleaned, out var det))
                    criteria.MinDetectionRate = det / 100.0;
            }
            else if (detectionText == "Any")
            {
                criteria.MinDetectionRate = 0;
            }

            // Parse specificity delta (e.g., "0.5", "1.0", "Any")
            if (!string.IsNullOrEmpty(specificityText) && specificityText != "Any")
            {
                var cleaned = specificityText.Replace("Δ ≥ ", "").Replace("≥ ", "").Trim();
                if (double.TryParse(cleaned, out var delta))
                    criteria.MinSpecificityDelta = delta;
            }
            else if (specificityText == "Any")
            {
                criteria.MinSpecificityDelta = 0;
            }

            // Parse cell fraction (e.g., "30%", "50%", "Any")
            if (!string.IsNullOrEmpty(cellFractionText) && cellFractionText != "Any")
            {
                var cleaned = cellFractionText.Replace("%", "").Replace("≥ ", "").Trim();
                if (double.TryParse(cleaned, out var frac))
                    criteria.MinCellFraction = frac / 100.0;
            }
            else if (cellFractionText == "Any")
            {
                criteria.MinCellFraction = 0;
            }

            return criteria;
        }
    }
}
