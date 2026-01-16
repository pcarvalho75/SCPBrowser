using System;
using System.Collections.Generic;
using System.Linq;

namespace TransmutationLearning
{
    /// <summary>
    /// Hybrid kNN-based distillation service.
    /// Uses proteomic neighborhood (kNN) combined with user biological priors
    /// to refine cell type assignments in a data-driven way.
    /// </summary>
    public class KnnDistillationService
    {
        /// <summary>
        /// Run kNN-based distillation on retained cells
        /// </summary>
        /// <param name="retainedCells">Cells that passed confidence filtering</param>
        /// <param name="proteinMatrix">Protein expression matrix: protein -> run -> intensity</param>
        /// <param name="expectedDistribution">User-defined expected cell type distribution (biological prior)</param>
        /// <param name="k">Number of nearest neighbors to consider</param>
        /// <param name="sensitivity">0-1, controls prior influence (0=pure kNN, 1=strong prior)</param>
        /// <param name="minConfidence">Minimum vote confidence to reclassify (e.g., 0.6 = 60%)</param>
        /// <param name="protectionRank">Protect cell types ranked this high or better from removal</param>
        /// <param name="proteinSubset">Optional subset of proteins to use for similarity computation</param>
        /// <returns>DistilledDataset with potentially updated labels</returns>
        public DistilledDataset RunKnnDistillation(
            List<CellClassification> retainedCells,
            Dictionary<string, Dictionary<string, double>> proteinMatrix,
            ExpectedDistribution expectedDistribution,
            int k = 10,
            double sensitivity = 0.5,
            double minConfidence = 0.6,
            int protectionRank = 2,
            HashSet<string> proteinSubset = null)
        {
            var result = new DistilledDataset
            {
                ExpectedDistribution = expectedDistribution,
                Sensitivity = sensitivity,
                DistillationApplied = true
            };

            // Step 1: Build feature matrix (cells x proteins)
            var (featureMatrix, runOrder, proteinOrder) = BuildFeatureMatrix(retainedCells, proteinMatrix, proteinSubset);

            // Step 2: Compute pairwise cosine similarity
            var similarityMatrix = ComputePairwiseCosineSimilarity(featureMatrix);

            // Step 3: Find k nearest neighbors for each cell
            var neighbors = FindKNearestNeighbors(similarityMatrix, k);

            // Step 4: Build run -> current label mapping
            var runToLabel = retainedCells.ToDictionary(c => c.Run, c => c.Labels);

            // Step 5: Compute prior weights from expected distribution
            var priorWeights = ComputePriorWeights(expectedDistribution);

            // Step 6: Process each cell with weighted voting
            for (int i = 0; i < retainedCells.Count; i++)
            {
                var cell = retainedCells[i];
                var cellNeighbors = neighbors[i];

                var record = EvaluateCellWithKnn(
                    cell,
                    cellNeighbors,
                    runOrder,
                    runToLabel,
                    priorWeights,
                    expectedDistribution,
                    sensitivity,
                    minConfidence,
                    protectionRank);

                result.ReclassificationHistory.Add(record);
                result.DistilledCells.Add(cell);
                result.DistilledLabels[cell.Run] = record.DistilledLabel;

                // Update runToLabel for subsequent cells (propagate changes)
                runToLabel[cell.Run] = record.DistilledLabel;
            }

            return result;
        }

        /// <summary>
        /// Build feature matrix: rows = cells, columns = proteins
        /// Values are log2 intensities, NaN for missing
        /// </summary>
        private (double[,] matrix, List<string> runOrder, List<string> proteinOrder) BuildFeatureMatrix(
            List<CellClassification> cells,
            Dictionary<string, Dictionary<string, double>> proteinMatrix,
            HashSet<string> proteinSubset = null)
        {
            // Get ordered lists
            var runOrder = cells.Select(c => c.Run).ToList();

            // Use subset if provided, otherwise all proteins
            var proteinOrder = proteinSubset != null && proteinSubset.Count > 0
                ? proteinMatrix.Keys.Where(p => proteinSubset.Contains(p)).OrderBy(p => p).ToList()
                : proteinMatrix.Keys.OrderBy(p => p).ToList();

            int nCells = runOrder.Count;
            int nProteins = proteinOrder.Count;

            var matrix = new double[nCells, nProteins];

            // Fill matrix
            for (int i = 0; i < nCells; i++)
            {
                string run = runOrder[i];
                for (int j = 0; j < nProteins; j++)
                {
                    string protein = proteinOrder[j];
                    if (proteinMatrix[protein].TryGetValue(run, out double intensity) && intensity > 0)
                    {
                        matrix[i, j] = Math.Log2(intensity);
                    }
                    else
                    {
                        matrix[i, j] = double.NaN; // Missing value
                    }
                }
            }

            return (matrix, runOrder, proteinOrder);
        }

        /// <summary>
        /// Compute pairwise cosine similarity using pairwise complete observations.
        /// Only uses proteins detected in BOTH cells for each pair.
        /// </summary>
        private double[,] ComputePairwiseCosineSimilarity(double[,] featureMatrix)
        {
            int nCells = featureMatrix.GetLength(0);
            int nProteins = featureMatrix.GetLength(1);

            var similarity = new double[nCells, nCells];

            for (int i = 0; i < nCells; i++)
            {
                similarity[i, i] = 1.0; // Self-similarity

                for (int j = i + 1; j < nCells; j++)
                {
                    double dotProduct = 0;
                    double normI = 0;
                    double normJ = 0;
                    int sharedCount = 0;

                    for (int p = 0; p < nProteins; p++)
                    {
                        double vi = featureMatrix[i, p];
                        double vj = featureMatrix[j, p];

                        // Only use proteins detected in BOTH cells (pairwise complete)
                        if (!double.IsNaN(vi) && !double.IsNaN(vj))
                        {
                            dotProduct += vi * vj;
                            normI += vi * vi;
                            normJ += vj * vj;
                            sharedCount++;
                        }
                    }

                    double sim;
                    if (sharedCount < 15 || normI == 0 || normJ == 0)
                    {
                        // Too few shared proteins or zero vector - treat as dissimilar
                        // Note: sharedCount >= 15 ensures statistical quorum for similarity assertion
                        sim = 0;
                    }
                    else
                    {
                        sim = dotProduct / (Math.Sqrt(normI) * Math.Sqrt(normJ));
                        // Clamp to [0, 1] (cosine can be negative, but we want similarity)
                        sim = Math.Max(0, sim);
                    }

                    similarity[i, j] = sim;
                    similarity[j, i] = sim;
                }
            }

            return similarity;
        }

        /// <summary>
        /// Find k nearest neighbors for each cell based on similarity matrix
        /// </summary>
        private List<int>[] FindKNearestNeighbors(double[,] similarityMatrix, int k)
        {
            int nCells = similarityMatrix.GetLength(0);
            var neighbors = new List<int>[nCells];

            for (int i = 0; i < nCells; i++)
            {
                // Get similarities to all other cells
                var similarities = new List<(int index, double sim)>();
                for (int j = 0; j < nCells; j++)
                {
                    if (i != j)
                    {
                        similarities.Add((j, similarityMatrix[i, j]));
                    }
                }

                // Sort by similarity descending and take top k
                neighbors[i] = similarities
                    .OrderByDescending(x => x.sim)
                    .Take(k)
                    .Select(x => x.index)
                    .ToList();
            }

            return neighbors;
        }

        /// <summary>
        /// Compute prior weights from expected distribution using exponential decay
        /// </summary>
        private Dictionary<string, double> ComputePriorWeights(ExpectedDistribution expectedDistribution)
        {
            return expectedDistribution.ToProportions(decayFactor: 0.6);
        }

        /// <summary>
        /// Evaluate a single cell using kNN weighted voting
        /// </summary>
        private ReclassificationRecord EvaluateCellWithKnn(
            CellClassification cell,
            List<int> neighborIndices,
            List<string> runOrder,
            Dictionary<string, string> runToLabel,
            Dictionary<string, double> priorWeights,
            ExpectedDistribution expectedDistribution,
            double sensitivity,
            double minConfidence,
            int protectionRank)
        {
            var record = new ReclassificationRecord
            {
                Run = cell.Run,
                OriginalLabel = cell.Labels,
                DistilledLabel = cell.Labels, // Default: keep original
                OriginalScore = cell.MaxScore,
                DistilledScore = cell.MaxScore,
                OriginalDelta = cell.DeltaNext,
                Reason = "Kept original"
            };

            // Protection check: if original label is in top-N expected, don't remove it
            int originalRank = expectedDistribution.GetRank(cell.Labels);
            bool isProtected = protectionRank > 0 && originalRank <= protectionRank;

            // Count neighbor votes
            var votes = new Dictionary<string, double>();
            foreach (int neighborIdx in neighborIndices)
            {
                string neighborRun = runOrder[neighborIdx];
                string neighborLabel = runToLabel[neighborRun];

                if (!votes.ContainsKey(neighborLabel))
                    votes[neighborLabel] = 0;
                votes[neighborLabel] += 1;
            }

            if (votes.Count == 0)
            {
                record.Reason = "No neighbor votes";
                return record;
            }

            // Apply prior weighting with virtual votes
            // At high sensitivity, expected types get "virtual votes" even if no neighbors voted for them
            // This ensures the prior can actually dominate when sensitivity = 1
            var weightedVotes = new Dictionary<string, double>();
            double maxPrior = priorWeights.Values.Max();
            int totalNeighbors = neighborIndices.Count;

            // Add virtual votes for ALL expected types based on sensitivity
            // At sensitivity=1: top type gets k virtual votes (same as if all neighbors voted for it)
            // At sensitivity=0: no virtual votes (pure kNN)
            foreach (var kvp in priorWeights)
            {
                string label = kvp.Key;
                double priorScore = kvp.Value / maxPrior; // 0 to 1

                // Virtual votes: at sensitivity=1, top type gets k votes, lower types get proportionally fewer
                double virtualVotes = sensitivity * priorScore * totalNeighbors;
                weightedVotes[label] = virtualVotes;
            }

            // Add actual neighbor votes (these are added on top of virtual votes)
            foreach (var kvp in votes)
            {
                string label = kvp.Key;
                double actualVotes = kvp.Value;

                if (!weightedVotes.ContainsKey(label))
                    weightedVotes[label] = 0;

                // Actual votes are weighted by (1 - sensitivity) so they matter less at high sensitivity
                // At sensitivity=0: only actual votes count
                // At sensitivity=1: actual votes are halved (still matter, but prior dominates)
                double actualWeight = 1.0 - (0.5 * sensitivity);
                weightedVotes[label] += actualVotes * actualWeight;
            }

            // Find winning label and runner-up
            var sortedVotes = weightedVotes.OrderByDescending(kvp => kvp.Value).ToList();
            string winnerLabel = sortedVotes[0].Key;
            double winnerVotes = sortedVotes[0].Value;
            double runnerUpVotes = sortedVotes.Count > 1 ? sortedVotes[1].Value : 0;

            // Calculate confidence as winner's margin over runner-up
            // This ensures high prior influence can actually cause reclassification
            // confidence = winner / (winner + runner-up), ranges from 0.5 (tie) to 1.0 (dominant)
            double confidence = (winnerVotes + runnerUpVotes) > 0
                ? winnerVotes / (winnerVotes + runnerUpVotes)
                : 0.5;

            // Record vote details
            int rawWinnerVotes = votes.TryGetValue(winnerLabel, out var rv) ? (int)rv : 0;
            record.ScoreGap = confidence;

            // Decision logic
            if (winnerLabel == cell.Labels)
            {
                // Neighbors agree with original - keep it
                record.Reason = $"Neighbors confirm ({rawWinnerVotes}/{neighborIndices.Count}, conf={confidence:F2})";
                return record;
            }

            // Neighbors suggest a different label
            if (isProtected)
            {
                // Original is protected - don't remove it
                record.Reason = $"Protected (rank {originalRank} ≤ {protectionRank}), neighbors suggest {winnerLabel}";
                return record;
            }

            if (confidence < minConfidence)
            {
                // Not confident enough to reclassify
                record.Reason = $"Low confidence ({confidence:F2} < {minConfidence:F2}), neighbors suggest {winnerLabel}";
                return record;
            }

            // Reclassify
            record.DistilledLabel = winnerLabel;
            record.DistilledScore = confidence;
            record.Reason = $"kNN vote: {winnerLabel} ({rawWinnerVotes}/{neighborIndices.Count}, conf={confidence:F2})";

            return record;
        }

        /// <summary>
        /// Preview kNN distillation without applying (for UI feedback)
        /// </summary>
        public (int wouldReclassify, Dictionary<string, int> newDistribution) PreviewKnnDistillation(
            List<CellClassification> retainedCells,
            Dictionary<string, Dictionary<string, double>> proteinMatrix,
            ExpectedDistribution expectedDistribution,
            int k = 10,
            double sensitivity = 0.5,
            double minConfidence = 0.6,
            int protectionRank = 2,
            HashSet<string> proteinSubset = null)
        {
            var result = RunKnnDistillation(
                retainedCells,
                proteinMatrix,
                expectedDistribution,
                k,
                sensitivity,
                minConfidence,
                protectionRank,
                proteinSubset);

            return (result.ReclassifiedCount, result.GetDistilledDistribution());
        }
    }
}
