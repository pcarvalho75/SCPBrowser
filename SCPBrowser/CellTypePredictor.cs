using MathNet.Numerics.Distributions;
using MathNet.Numerics.Statistics;
using System;
using System.Collections.Generic;
using System.Linq;

namespace SCPBrowser
{
    /// <summary>
    /// Predicts cell types by comparing proteomics data against aggregated transcriptomic cell type profiles
    /// Uses a sophisticated scoring system combining correlation, specificity, and enrichment statistics
    /// </summary>
    public class CellTypePredictor
    {
        private readonly TranscriptomicDatabase _database;
        private readonly Dictionary<string, double> _geneSpecificity;
        private readonly Dictionary<string, HashSet<string>> _cellTypeMarkers;

        public Dictionary<string, HashSet<string>> CellTypeMarkers => _cellTypeMarkers;

        /// <summary>
        /// Creates a new CellTypePredictor using aggregated cell type profiles
        /// </summary>
        /// <param name="database">Database containing cell type profiles (not individual cells)</param>
        /// <param name="markerSpecificityThreshold">Minimum specificity score for a gene to be considered a marker (default: 0.5)</param>
        public CellTypePredictor(TranscriptomicDatabase database, double markerSpecificityThreshold = 0.2)
        {
            _database = database ?? throw new ArgumentNullException(nameof(database));

            if (_database.CellTypeProfiles == null || _database.CellTypeProfiles.Count == 0)
                throw new ArgumentException("Database must contain cell type profiles", nameof(database));

            // Pre-calculate gene specificity and marker sets for efficient prediction
            _geneSpecificity = CalculateGeneSpecificity();
            _cellTypeMarkers = DefineCellTypeMarkers(markerSpecificityThreshold);
        }

        /// <summary>
        /// Calculates how specific each gene is to certain cell types
        /// Higher specificity = gene is expressed in fewer cell types (more discriminative)
        /// Uses inverse document frequency (IDF) logic: log(total_cell_types / cell_types_expressing_gene)
        /// </summary>
        private Dictionary<string, double> CalculateGeneSpecificity()
        {
            var specificity = new Dictionary<string, double>();
            int totalCellTypes = _database.CellTypeProfiles.Count;

            if (totalCellTypes == 0)
                return specificity;

            // Collect all unique genes across all cell type profiles
            var allGenes = new HashSet<string>();
            foreach (var profile in _database.CellTypeProfiles.Values)
            {
                foreach (var gene in profile.MedianExpression.Keys)
                {
                    allGenes.Add(gene);
                }
            }

            // Calculate specificity for each gene
            foreach (var gene in allGenes)
            {
                int cellTypesExpressingGene = 0;

                // Count how many cell types express this gene (median > 0)
                foreach (var profile in _database.CellTypeProfiles.Values)
                {
                    if (profile.MedianExpression.ContainsKey(gene) && profile.MedianExpression[gene] > 0)
                    {
                        cellTypesExpressingGene++;
                    }
                }

                // IDF-style specificity: genes expressed in fewer cell types are more specific
                if (cellTypesExpressingGene > 0)
                {
                    specificity[gene] = Math.Log((double)totalCellTypes / cellTypesExpressingGene);
                }
                else
                {
                    specificity[gene] = 0;
                }
            }

            return specificity;
        }

        private Dictionary<string, HashSet<string>> DefineCellTypeMarkers(double specificityThreshold)
        {
            var markers = new Dictionary<string, HashSet<string>>();

            //Console.WriteLine($"[DEBUG-MARKERS] Starting marker definition with threshold: {specificityThreshold}");
            //Console.WriteLine($"[DEBUG-MARKERS] Total cell types: {_database.CellTypeProfiles.Count}");
            //Console.WriteLine($"[DEBUG-MARKERS] Total genes in specificity map: {_geneSpecificity.Count}");

            foreach (var (cellType, profile) in _database.CellTypeProfiles)
            {
                markers[cellType] = new HashSet<string>();
                int candidateGenes = 0;
                int passedSpecificity = 0;
                int passedPercent = 0;

                // Consider genes that are both specific and well-expressed in this cell type
                foreach (var (gene, medianExpression) in profile.MedianExpression)
                {
                    if (medianExpression <= 0)
                        continue;

                    candidateGenes++;

                    // Check if gene is specific enough
                    if (_geneSpecificity.TryGetValue(gene, out double specificity) &&
                        specificity >= specificityThreshold)
                    {
                        passedSpecificity++;

                        // Optional: Also check that gene is expressed in a good fraction of cells
                        // This makes markers more robust
                        if (profile.PercentExpressing.TryGetValue(gene, out double percentExpressing) &&
                            percentExpressing >= 0.2) 
                        {
                            passedPercent++;
                            markers[cellType].Add(gene);
                        }
                        else if (!profile.PercentExpressing.ContainsKey(gene))
                        {
                            // If PercentExpressing not available, just use specificity
                            markers[cellType].Add(gene);
                        }
                    }
                }
            }

            return markers;
        }

        /// <summary>
        /// Predicts cell type for a single cell based on its protein abundances
        /// Compares against all cell type profiles and returns ranked scores
        /// </summary>
        /// <param name="proteinAbundances">Dictionary of gene/protein name -> abundance value</param>
        /// <returns>Prediction result with scores for all cell types</returns>
        public CellTypePredictionResult PredictCellType(Dictionary<string, double> proteinAbundances)
        {
            if (proteinAbundances == null || proteinAbundances.Count == 0)
                return new CellTypePredictionResult();

            var results = new Dictionary<string, CellTypeScore>();

            // Compare against each cell type profile
            foreach (var cellType in _database.CellTypeProfiles.Keys)
            {
                var score = CalculateComprehensiveScore(proteinAbundances, cellType);
                results[cellType] = score;
            }

            // Order by composite score (highest first)
            var orderedResults = results
                .OrderByDescending(kvp => kvp.Value.CompositeScore)
                .ToDictionary(kvp => kvp.Key, kvp => kvp.Value);

            return new CellTypePredictionResult
            {
                Scores = orderedResults,
                TopCellType = orderedResults.FirstOrDefault().Key,
                TopScore = orderedResults.FirstOrDefault().Value
            };
        }

        /// <summary>
        /// Calculates a comprehensive score combining multiple statistical approaches
        /// </summary>
        private CellTypeScore CalculateComprehensiveScore(Dictionary<string, double> proteinAbundances, string cellType)
        {
            if (!_database.CellTypeProfiles.ContainsKey(cellType))
            {
                return new CellTypeScore
                {
                    SpearmanCorrelation = 0,
                    SpecificityScore = 0,
                    HypergeometricPValue = 1.0,
                    CompositeScore = 0
                };
            }

            var profile = _database.CellTypeProfiles[cellType];

            // 1. Spearman correlation: How well do protein ranks correlate with transcript ranks?
            double spearmanCorr = CalculateSpearmanCorrelation(proteinAbundances, profile);

            // 2. Specificity-weighted score: Prioritize specific marker genes
            double specificityScore = CalculateSpecificityWeightedScore(proteinAbundances, profile);

            // 3. Hypergeometric p-value: Is the overlap with markers statistically significant?
            double hypergeometricPValue = CalculateHypergeometricPValue(proteinAbundances.Keys.ToList(), cellType);

            // Combine scores: correlation (40%) + specificity (40%) + enrichment (20%)
            double compositeScore = (spearmanCorr * 0.4) + (specificityScore * 0.4) + ((1 - hypergeometricPValue) * 0.2);

            return new CellTypeScore
            {
                SpearmanCorrelation = spearmanCorr,
                SpecificityScore = specificityScore,
                HypergeometricPValue = hypergeometricPValue,
                CompositeScore = Math.Max(0, compositeScore)
            };
        }

        /// <summary>
        /// Calculates Spearman rank correlation between proteomics and transcriptomics
        /// Uses cell type profile median expression values (NOT individual cells)
        /// </summary>

        /// <summary>
        /// Calculates Spearman rank correlation between proteomics and transcriptomics
        /// Uses cell type profile median expression values (NOT individual cells)
        /// </summary>
        private double CalculateSpearmanCorrelation(Dictionary<string, double> proteinAbundances, CellTypeProfile profile)
        {
            // Find common genes between proteomics data and this cell type profile
            var commonProteins = proteinAbundances.Keys
                .Where(p => profile.MedianExpression.ContainsKey(p) && profile.MedianExpression[p] > 0)
                .ToList();

            if (commonProteins.Count < 3)
                return 0;

            // Get the raw values (not ranks). The library will handle ranking.
            var proteomicsValues = commonProteins.Select(p => proteinAbundances[p]);
            var transcriptomicsValues = commonProteins.Select(p => profile.MedianExpression[p]);

            try
            {
                // Let the library do all the work: ranking and correlation
                return Correlation.Spearman(proteomicsValues, transcriptomicsValues);
            }
            catch (Exception)
            {
                // This try-catch is a fragility guard. Math.NET can throw errors
                // if the data is invalid (e.g., all identical values),
                // so we return 0 in that case.
                return 0;
            }
        }
        /// <summary>
        /// Calculates a specificity-weighted score that prioritizes cell-type-specific genes
        /// Genes that are more specific to this cell type contribute more to the score
        /// </summary>
        private double CalculateSpecificityWeightedScore(Dictionary<string, double> proteinAbundances, CellTypeProfile profile)
        {
            double totalScore = 0;
            int matchCount = 0;

            foreach (var (protein, abundance) in proteinAbundances)
            {
                // Check if this gene is in the cell type profile
                if (!profile.MedianExpression.ContainsKey(protein))
                    continue;

                double medianExpression = profile.MedianExpression[protein];
                if (medianExpression <= 0)
                    continue;

                // Get gene specificity (how unique is this gene to certain cell types?)
                if (!_geneSpecificity.ContainsKey(protein))
                    continue;

                double specificity = _geneSpecificity[protein];
                if (specificity <= 0)
                    continue;

                // Weight the match by specificity and log-expression
                // Specific genes with high expression contribute more
                totalScore += specificity * Math.Log(medianExpression + 1) * Math.Log(abundance + 1);
                matchCount++;
            }

            if (matchCount == 0)
                return 0;

            // Normalize by sqrt of matches to prevent bias toward cell types with many expressed genes
            return totalScore / Math.Sqrt(matchCount);
        }

        /// <summary>
        /// Calculates hypergeometric p-value for marker enrichment
        /// Tests: "Is the overlap between detected proteins and cell type markers statistically significant?"
        /// </summary>

        private double CalculateHypergeometricPValue(List<string> detectedProteins, string cellType)
        {
            if (!_cellTypeMarkers.ContainsKey(cellType))
            {
                Console.WriteLine($"[DEBUG-HYPER] No markers defined for cell type: {cellType}");
                return 1.0;
            }

            // N = total genes in universe (all genes across all cell types)
            int N = _geneSpecificity.Count;

            // K = number of markers for this cell type
            int K = _cellTypeMarkers[cellType].Count;

            // n = number of detected proteins
            int n = detectedProteins.Count;

            // k = overlap (how many detected proteins are markers for this cell type)
            int k = detectedProteins.Count(p => _cellTypeMarkers[cellType].Contains(p));

            //Console.WriteLine($"[DEBUG-HYPER] CellType: {cellType}, N={N}, K={K}, n={n}, k={k}");

            if (K == 0 || n == 0)
            {
                Console.WriteLine($"[DEBUG-HYPER] Returning 1.0 because K={K} or n={n}");
                return 1.0;
            }

            return CalculateHypergeometricPValueExact(N, K, n, k);
        }

        /// <summary>
        /// Exact calculation of hypergeometric p-value (right-tailed test)
        /// P(X >= k) where X ~ Hypergeometric(N, K, n)
        /// </summary>
        /// <summary>
        /// Exact calculation of hypergeometric p-value (right-tailed test)
        /// P(X >= k) where X ~ Hypergeometric(N, K, n)
        /// </summary>
        private double CalculateHypergeometricPValueExact(int N, int K, int n, int k)
        {
            // Create the distribution object with your parameters
            // N = population, K = successes, n = draws
            var hypergeometric = new Hypergeometric(N, K, n);

            // Calculate the p-value P(X >= k).
            // This is the Complementary Cumulative Distribution (CCDF) at k-1.
            // CCDF(x) = P(X > x), so CCDF(k-1) = P(X > k-1) = P(X >= k)
            return 1.0 - hypergeometric.CumulativeDistribution(k - 1);
        }


    }

    /// <summary>
    /// Result of cell type prediction containing scores for all cell types
    /// </summary>
    public class CellTypePredictionResult
    {
        public Dictionary<string, CellTypeScore> Scores { get; set; } = new Dictionary<string, CellTypeScore>();
        public string TopCellType { get; set; }
        public CellTypeScore TopScore { get; set; }
    }

    /// <summary>
    /// Individual score components for a cell type prediction
    /// </summary>
    public class CellTypeScore
    {
        /// <summary>
        /// Spearman rank correlation between proteomics and transcriptomics profiles (0-1)
        /// Higher = better rank agreement
        /// </summary>
        public double SpearmanCorrelation { get; set; }

        /// <summary>
        /// Specificity-weighted similarity score (0-∞, typically 0-10)
        /// Higher = more specific marker genes match
        /// </summary>
        public double SpecificityScore { get; set; }

        /// <summary>
        /// Hypergeometric p-value for marker enrichment (0-1)
        /// Lower = more significant enrichment
        /// </summary>
        public double HypergeometricPValue { get; set; }

        /// <summary>
        /// Composite score combining all metrics (0-1)
        /// Higher = better overall match
        /// </summary>
        public double CompositeScore { get; set; }

        public override string ToString()
        {
            return $"Spearman: {SpearmanCorrelation:F3}, Specificity: {SpecificityScore:F3}, " +
                   $"P-value: {HypergeometricPValue:E2}, Composite: {CompositeScore:F3}";
        }
    }
}