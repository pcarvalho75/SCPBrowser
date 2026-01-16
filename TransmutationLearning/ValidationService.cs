using System;
using System.Collections.Generic;
using System.Linq;

namespace TransmutationLearning
{
    /// <summary>
    /// Service for validating the quality of transmutation learning results.
    /// Implements self-consistency check and cross-validation.
    /// </summary>
    public class ValidationService
    {
        private readonly Random _random = new Random(42); // Fixed seed for reproducibility

        /// <summary>
        /// Run k-fold cross-validation on the selected markers.
        /// This provides a true measure of predictive power by testing on held-out data.
        /// </summary>
        /// <param name="retainedCells">Cells to validate</param>
        /// <param name="proteinMatrix">Protein expression matrix</param>
        /// <param name="selectedMarkers">Markers selected for the classifier</param>
        /// <param name="effectiveLabels">Labels to validate against</param>
        /// <param name="k">Number of folds (default 5)</param>
        /// <returns>Cross-validation result with per-fold accuracies</returns>
        public CrossValidationResult RunCrossValidation(
            List<CellClassification> retainedCells,
            Dictionary<string, Dictionary<string, double>> proteinMatrix,
            List<ProteinStatistics> selectedMarkers,
            Dictionary<string, string> effectiveLabels,
            int k = 5)
        {
            var result = new CrossValidationResult { NumFolds = k };

            if (selectedMarkers == null || selectedMarkers.Count == 0)
            {
                result.IsValid = false;
                result.ErrorMessage = "No markers selected";
                return result;
            }

            if (retainedCells.Count < k * 2)
            {
                result.IsValid = false;
                result.ErrorMessage = $"Too few cells ({retainedCells.Count}) for {k}-fold cross-validation";
                return result;
            }

            var markerProteins = selectedMarkers.Select(m => m.ProteinName).ToList();

            // Shuffle cells for random fold assignment
            var shuffledCells = retainedCells.OrderBy(_ => _random.Next()).ToList();

            // Assign cells to folds
            var folds = new List<List<CellClassification>>();
            for (int i = 0; i < k; i++)
                folds.Add(new List<CellClassification>());

            for (int i = 0; i < shuffledCells.Count; i++)
                folds[i % k].Add(shuffledCells[i]);

            // Run k-fold CV
            for (int fold = 0; fold < k; fold++)
            {
                // Test set: current fold
                var testCells = folds[fold];

                // Training set: all other folds
                var trainCells = folds
                    .Where((_, idx) => idx != fold)
                    .SelectMany(f => f)
                    .ToList();

                // Build centroids from training set
                var centroids = BuildCentroids(trainCells, proteinMatrix, markerProteins, effectiveLabels);

                if (centroids.Count < 2)
                {
                    result.FoldAccuracies.Add(0);
                    continue;
                }

                // Classify test cells
                int correct = 0;
                int total = 0;

                foreach (var cell in testCells)
                {
                    if (!effectiveLabels.TryGetValue(cell.Run, out var trueLabel))
                        continue;

                    var cellVector = GetCellVector(cell.Run, proteinMatrix, markerProteins);
                    if (cellVector == null)
                        continue;

                    var (predictedLabel, _) = ClassifyByNearestCentroid(cellVector, centroids);

                    total++;
                    if (predictedLabel == trueLabel)
                        correct++;
                }

                double foldAccuracy = total > 0 ? (double)correct / total : 0;
                result.FoldAccuracies.Add(foldAccuracy);
            }

            // Compute summary statistics
            if (result.FoldAccuracies.Count > 0)
            {
                result.MeanAccuracy = result.FoldAccuracies.Average();
                result.StdAccuracy = Math.Sqrt(result.FoldAccuracies
                    .Select(x => Math.Pow(x - result.MeanAccuracy, 2))
                    .Average());
            }

            result.IsValid = true;
            result.PassesValidation = result.MeanAccuracy >= 0.75;

            if (!result.PassesValidation)
            {
                result.WarningMessage = $"Cross-validation accuracy: {result.MeanAccuracy:P1} ± {result.StdAccuracy:P1}\n\n" +
                    $"This suggests the markers may not generalize well to unseen cells.\n" +
                    $"Consider:\n" +
                    $"• Selecting more robust markers\n" +
                    $"• Checking for batch effects\n" +
                    $"• Reviewing cell type annotations";
            }

            return result;
        }

        /// <summary>
        /// Run self-consistency validation on the selected markers.
        /// Builds a centroid classifier from training data and re-classifies
        /// the same cells to check if labels are self-consistent.
        /// </summary>
        /// <param name="retainedCells">Cells used for training</param>
        /// <param name="proteinMatrix">Protein expression matrix</param>
        /// <param name="selectedMarkers">Markers selected for the classifier</param>
        /// <param name="effectiveLabels">Labels to validate against (distilled or original)</param>
        /// <returns>Validation result with agreement metrics</returns>
        public ValidationResult RunSelfConsistencyCheck(
            List<CellClassification> retainedCells,
            Dictionary<string, Dictionary<string, double>> proteinMatrix,
            List<ProteinStatistics> selectedMarkers,
            Dictionary<string, string> effectiveLabels)
        {
            var result = new ValidationResult();

            if (selectedMarkers == null || selectedMarkers.Count == 0)
            {
                result.IsValid = false;
                result.ErrorMessage = "No markers selected";
                return result;
            }

            // Get marker protein names
            var markerProteins = selectedMarkers.Select(m => m.ProteinName).ToList();

            // Step 1: Build centroids for each cell type using training data
            var centroids = BuildCentroids(retainedCells, proteinMatrix, markerProteins, effectiveLabels);

            if (centroids.Count < 2)
            {
                result.IsValid = false;
                result.ErrorMessage = "Need at least 2 cell types to validate";
                return result;
            }

            // Step 2: Re-classify each cell using centroid similarity
            int totalCells = 0;
            int agreementCount = 0;
            var confusionMatrix = new Dictionary<string, Dictionary<string, int>>();
            var perCellResults = new List<CellValidationResult>();

            foreach (var cell in retainedCells)
            {
                if (!effectiveLabels.TryGetValue(cell.Run, out var originalLabel))
                    continue;

                // Get cell's expression vector for marker proteins
                var cellVector = GetCellVector(cell.Run, proteinMatrix, markerProteins);
                if (cellVector == null)
                    continue;

                // Find most similar centroid
                var (predictedLabel, confidence) = ClassifyByNearestCentroid(cellVector, centroids);

                totalCells++;
                bool isMatch = predictedLabel == originalLabel;
                if (isMatch)
                    agreementCount++;

                // Record per-cell result
                perCellResults.Add(new CellValidationResult
                {
                    Run = cell.Run,
                    OriginalLabel = originalLabel,
                    PredictedLabel = predictedLabel,
                    Confidence = confidence,
                    IsMatch = isMatch
                });

                // Update confusion matrix
                if (!confusionMatrix.ContainsKey(originalLabel))
                    confusionMatrix[originalLabel] = new Dictionary<string, int>();
                if (!confusionMatrix[originalLabel].ContainsKey(predictedLabel))
                    confusionMatrix[originalLabel][predictedLabel] = 0;
                confusionMatrix[originalLabel][predictedLabel]++;
            }

            // Compute metrics
            result.TotalCells = totalCells;
            result.AgreementCount = agreementCount;
            result.AgreementRate = totalCells > 0 ? (double)agreementCount / totalCells : 0;
            result.ConfusionMatrix = confusionMatrix;
            result.PerCellResults = perCellResults;
            result.CellTypes = centroids.Keys.ToList();

            // Determine if validation passed (>= 80% agreement)
            result.PassesValidation = result.AgreementRate >= 0.80;
            result.IsValid = true;

            // Generate warning message if needed
            if (!result.PassesValidation)
            {
                result.WarningMessage = $"Self-consistency check: {result.AgreementRate:P1} agreement " +
                    $"(threshold: 80%).\n\n" +
                    $"This may indicate:\n" +
                    $"• Noisy or poorly separated clusters\n" +
                    $"• Complex non-spherical cluster shapes (e.g., developmental trajectories)\n" +
                    $"• Insufficient marker coverage for some cell types\n\n" +
                    $"Note: Centroid-based validation assumes spherical clusters. " +
                    $"Trajectory-like data (e.g., differentiating cells) may fail validation " +
                    $"even when the classifier is valid.";
            }

            return result;
        }

        /// <summary>
        /// Build centroid (mean expression) for each cell type
        /// </summary>
        private Dictionary<string, double[]> BuildCentroids(
            List<CellClassification> cells,
            Dictionary<string, Dictionary<string, double>> proteinMatrix,
            List<string> markerProteins,
            Dictionary<string, string> effectiveLabels)
        {
            var centroids = new Dictionary<string, double[]>();
            var cellTypeVectors = new Dictionary<string, List<double[]>>();

            // Group cells by type
            foreach (var cell in cells)
            {
                if (!effectiveLabels.TryGetValue(cell.Run, out var cellType))
                    continue;

                var vector = GetCellVector(cell.Run, proteinMatrix, markerProteins);
                if (vector == null)
                    continue;

                if (!cellTypeVectors.ContainsKey(cellType))
                    cellTypeVectors[cellType] = new List<double[]>();
                cellTypeVectors[cellType].Add(vector);
            }

            // Compute mean for each type
            foreach (var kvp in cellTypeVectors)
            {
                if (kvp.Value.Count == 0)
                    continue;

                int nProteins = markerProteins.Count;
                var centroid = new double[nProteins];

                for (int p = 0; p < nProteins; p++)
                {
                    var values = kvp.Value
                        .Select(v => v[p])
                        .Where(x => !double.IsNaN(x))
                        .ToList();

                    centroid[p] = values.Count > 0 ? values.Average() : double.NaN;
                }

                centroids[kvp.Key] = centroid;
            }

            return centroids;
        }

        /// <summary>
        /// Get expression vector for a cell (log2 intensities)
        /// </summary>
        private double[] GetCellVector(
            string run,
            Dictionary<string, Dictionary<string, double>> proteinMatrix,
            List<string> markerProteins)
        {
            var vector = new double[markerProteins.Count];
            int detected = 0;

            for (int i = 0; i < markerProteins.Count; i++)
            {
                string protein = markerProteins[i];
                if (proteinMatrix.TryGetValue(protein, out var proteinData) &&
                    proteinData.TryGetValue(run, out var intensity) &&
                    intensity > 0)
                {
                    vector[i] = Math.Log2(intensity);
                    detected++;
                }
                else
                {
                    vector[i] = double.NaN;
                }
            }

            // Require at least 20% of markers detected
            if (detected < markerProteins.Count * 0.2)
                return null;

            return vector;
        }

        /// <summary>
        /// Classify a cell by finding nearest centroid (cosine similarity)
        /// </summary>
        private (string label, double confidence) ClassifyByNearestCentroid(
            double[] cellVector,
            Dictionary<string, double[]> centroids)
        {
            string bestLabel = null;
            double bestSimilarity = double.MinValue;
            double secondBest = double.MinValue;

            foreach (var kvp in centroids)
            {
                double similarity = CosineSimilarity(cellVector, kvp.Value);
                if (similarity > bestSimilarity)
                {
                    secondBest = bestSimilarity;
                    bestSimilarity = similarity;
                    bestLabel = kvp.Key;
                }
                else if (similarity > secondBest)
                {
                    secondBest = similarity;
                }
            }

            // Confidence is the gap between best and second-best
            double confidence = bestSimilarity - secondBest;
            return (bestLabel, confidence);
        }

        /// <summary>
        /// Compute cosine similarity between two vectors (pairwise complete)
        /// </summary>
        private double CosineSimilarity(double[] a, double[] b)
        {
            double dotProduct = 0;
            double normA = 0;
            double normB = 0;
            int shared = 0;

            for (int i = 0; i < a.Length; i++)
            {
                if (!double.IsNaN(a[i]) && !double.IsNaN(b[i]))
                {
                    dotProduct += a[i] * b[i];
                    normA += a[i] * a[i];
                    normB += b[i] * b[i];
                    shared++;
                }
            }

            if (shared < 3 || normA == 0 || normB == 0)
                return 0;

            return dotProduct / (Math.Sqrt(normA) * Math.Sqrt(normB));
        }
    }

    /// <summary>
    /// Result of self-consistency validation
    /// </summary>
    public class ValidationResult
    {
        public bool IsValid { get; set; }
        public string ErrorMessage { get; set; }
        public string WarningMessage { get; set; }

        public int TotalCells { get; set; }
        public int AgreementCount { get; set; }
        public double AgreementRate { get; set; }
        public bool PassesValidation { get; set; }

        public List<string> CellTypes { get; set; } = new List<string>();
        public Dictionary<string, Dictionary<string, int>> ConfusionMatrix { get; set; }
            = new Dictionary<string, Dictionary<string, int>>();
        public List<CellValidationResult> PerCellResults { get; set; }
            = new List<CellValidationResult>();

        /// <summary>
        /// Get per-cell-type accuracy
        /// </summary>
        public Dictionary<string, double> GetPerTypeAccuracy()
        {
            var result = new Dictionary<string, double>();
            foreach (var cellType in CellTypes)
            {
                var cellsOfType = PerCellResults.Where(r => r.OriginalLabel == cellType).ToList();
                if (cellsOfType.Count > 0)
                {
                    result[cellType] = (double)cellsOfType.Count(r => r.IsMatch) / cellsOfType.Count;
                }
            }
            return result;
        }
    }

    /// <summary>
    /// Validation result for a single cell
    /// </summary>
    public class CellValidationResult
    {
        public string Run { get; set; }
        public string OriginalLabel { get; set; }
        public string PredictedLabel { get; set; }
        public double Confidence { get; set; }
        public bool IsMatch { get; set; }
    }

    /// <summary>
    /// Result of k-fold cross-validation
    /// </summary>
    public class CrossValidationResult
    {
        public bool IsValid { get; set; }
        public string ErrorMessage { get; set; }
        public string WarningMessage { get; set; }
        public bool PassesValidation { get; set; }

        public int NumFolds { get; set; }
        public List<double> FoldAccuracies { get; set; } = new List<double>();
        public double MeanAccuracy { get; set; }
        public double StdAccuracy { get; set; }

        public string Summary => IsValid
            ? $"{MeanAccuracy:P1} ± {StdAccuracy:P1} ({NumFolds}-fold CV)"
            : ErrorMessage;
    }
}
