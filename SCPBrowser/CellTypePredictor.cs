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
        private readonly double _minPercentExpressing;

        public Dictionary<string, HashSet<string>> CellTypeMarkers => _cellTypeMarkers;

        // Softmax temperatures for the four scoring channels. They alone determine how peaked the reported
        // relative score is, yet they appear nowhere in the UI - so they are named here and printed into the
        // diagnostics export rather than living as bare literals mid-method. Smaller T = more decisive.
        /// <summary>Spearman channel temperature. Range is -1..1, so 0.1 means a 0.1 difference gives ~2.7x odds.</summary>
        public const double TSpearman = 0.1;
        /// <summary>Enrichment channel temperature over -log10(p): 2.0 means a 100x p-value difference gives ~2.7x odds.</summary>
        public const double TEnrichment = 2.0;
        /// <summary>Marker-coverage channel temperature. Range is 0..1, so 0.15 means a 15% difference gives ~2.7x odds.</summary>
        public const double TCoverage = 0.15;
        /// <summary>Specificity has no fixed range, so its temperature auto-scales to this fraction of the median score.</summary>
        public const double TSpecificityMedianFactor = 0.2;
        /// <summary>Floor for the auto-scaled specificity temperature, so a tiny median cannot make the softmax explode.</summary>
        public const double TSpecificityFloor = 1.0;

        /// <summary>
        /// One-line description of the fixed scoring parameters, for the diagnostics export and reports. These
        /// govern the reported score's sharpness and are otherwise invisible to the user.
        /// </summary>
        public static string TemperatureSummary =>
            $"softmax temperatures: Spearman T={TSpearman}, Enrichment T={TEnrichment}, Coverage T={TCoverage}, " +
            $"Specificity T=max({TSpecificityMedianFactor}*median, {TSpecificityFloor})";

        /// <summary>
        /// Creates a new CellTypePredictor using aggregated cell type profiles
        /// </summary>
        /// <param name="database">Database containing cell type profiles (not individual cells)</param>
        /// <param name="markerSpecificityThreshold">Minimum specificity score for a gene to be considered a marker.
        /// If negative, auto-calculated as log(N/2) where N = number of cell types (recommended). Default: -1 (auto)</param>
        /// <param name="minPercentExpressing">Minimum fraction of a class's cells that must express a gene for it to
        /// qualify as a marker. NOTE the units are whatever the profile producer stored: the TSV parser writes a 0-1
        /// fraction, while the proteomic builders write a 0-100 percentage — so the historical default of 0.2 is a
        /// no-op against percentage-scale profiles (it means "0.2%"). Kept at 0.2 so existing behaviour is unchanged;
        /// pass 20.0 to actually require 20% expression on percentage-scale profiles.</param>
        public CellTypePredictor(TranscriptomicDatabase database, double markerSpecificityThreshold = -1,
            double minPercentExpressing = 0.2)
        {
            _minPercentExpressing = minPercentExpressing;
            _database = database ?? throw new ArgumentNullException(nameof(database));

            if (_database.CellTypeProfiles == null || _database.CellTypeProfiles.Count == 0)
                throw new ArgumentException("No cell type profiles found in the reference database. Import omic reference data first.", nameof(database));

            // Pre-calculate gene specificity and marker sets for efficient prediction
            _geneSpecificity = CalculateGeneSpecificity();

            // Auto-calculate threshold: log(N/2) ensures only genes in ≤ 2 cell types qualify
                if (markerSpecificityThreshold < 0)
                    markerSpecificityThreshold = Math.Log((double)_database.CellTypeProfiles.Count / 2.0);

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
                            percentExpressing >= _minPercentExpressing)
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
        /// Predicts cell type without user-defined key markers
        /// </summary>
        public CellTypePredictionResult PredictCellType(Dictionary<string, double> proteinAbundances)
        {
            return PredictCellType(proteinAbundances, null, null);
        }

        /// <summary>
        /// Predicts cell type with optional user-defined key markers that influence scoring
        /// </summary>
        /// <param name="proteinAbundances">Protein abundances from proteomics data</param>
        /// <param name="userKeyMarkers">User-defined key markers per cell type (can be null)</param>
        /// <param name="excludedCellTypes">Cell types to exclude from classification (can be null)</param>
        /// <param name="priorWeights">User-defined prior weights per cell type (can be null). Auto-normalized to sum=1.</param>
        public CellTypePredictionResult PredictCellType(
            Dictionary<string, double> proteinAbundances, 
            Dictionary<string, HashSet<string>> userKeyMarkers,
            HashSet<string> excludedCellTypes = null,
            Dictionary<string, double> priorWeights = null)
        {
            if (proteinAbundances == null || proteinAbundances.Count == 0)
                return new CellTypePredictionResult();

            // Calculate median abundance for determining "high" vs "low" abundance
            double medianAbundance = proteinAbundances.Values.OrderBy(x => x).ElementAt(proteinAbundances.Count / 2);

            // Get cell types to consider (exclude those in exclusion list)
            var cellTypesToConsider = _database.CellTypeProfiles.Keys
                .Where(ct => excludedCellTypes == null || !excludedCellTypes.Contains(ct))
                .ToList();

            if (cellTypesToConsider.Count == 0)
                return new CellTypePredictionResult();

            // 1. First Pass: Calculate Raw Metrics for considered cell types
            var results = new Dictionary<string, CellTypeScore>();
            var detectedProteinList = proteinAbundances.Keys.ToList();
            foreach (var cellType in cellTypesToConsider)
            {
                var score = CalculateRawMetrics(proteinAbundances, cellType);
                score.MarkerCoverage = CalculateMarkerCoverage(detectedProteinList, cellType);
                results[cellType] = score;
            }

            // 2. Calculate Key Marker Adjustments (populates MarkersFound/Missing on each score)
            foreach (var kvp in results)
            {
                double keyMarkerAdjustment = CalculateKeyMarkerAdjustment(
                    kvp.Key, proteinAbundances, userKeyMarkers, medianAbundance, kvp.Value);
                kvp.Value.KeyMarkerAdjustment = keyMarkerAdjustment;
            }

            // 3. Per-metric softmax probabilities
            // Temperature controls sensitivity: smaller T = more decisive, larger T = more uniform
            // Spearman (-1 to 1): T=0.1 means 0.1 difference → ~2.7x likelihood ratio
            // Specificity (unbounded): auto-scale T = median of values (floor 1.0)
            // Enrichment -log10(p) (0+): T=2.0 means 100x p-value diff → ~2.7x ratio  
            // Coverage (0 to 1): T=0.15 means 15% coverage diff → ~2.7x ratio
            var cellTypes = results.Keys.ToList();

            double tSpearman = TSpearman;
            double tEnrichment = TEnrichment;
            double tCoverage = TCoverage;

            // Auto-scale specificity temperature to median of values (prevents extreme softmax with large scores)
            var specValues = results.Values.Select(s => s.SpecificityScore).OrderBy(v => v).ToList();
            double medianSpec = specValues[specValues.Count / 2];
            double tSpecificity = Math.Max(medianSpec * TSpecificityMedianFactor, TSpecificityFloor);

            var pSpearman = SoftmaxOverMetric(cellTypes, ct => results[ct].SpearmanCorrelation, tSpearman);
            var pSpecificity = SoftmaxOverMetric(cellTypes, ct => results[ct].SpecificityScore, tSpecificity);
            var pEnrichment = SoftmaxOverMetric(cellTypes, ct => -Math.Log10(Math.Max(results[ct].HypergeometricPValue, 1e-300)), tEnrichment);
            var pCoverage = SoftmaxOverMetric(cellTypes, ct => results[ct].MarkerCoverage, tCoverage);

            // 4. Average per-metric probabilities → base CompositeScore.
            //
            // IMPORTANT, and easy to misread: this is a RELATIVE SHARE across the cell types present in the loaded
            // reference, not a probability that the cell IS that type. The values are forced to sum to 1, so the
            // model can never answer "none of these" - a cell whose true type is absent from the reference still
            // receives a high score for whichever listed class it least disagrees with. How peaked the number is
            // depends entirely on the four temperatures above, which are fixed constants. Report it as a relative
            // score, and read the runner-up margin alongside it: a top share of 0.35 with a 0.02 margin is a very
            // different statement from 0.35 with a 0.20 margin.
            foreach (var ct in cellTypes)
            {
                results[ct].CompositeScore = (pSpearman[ct] + pSpecificity[ct] + pEnrichment[ct] + pCoverage[ct]) / 4.0;
            }

            // 5. Apply Key Marker and Prior adjustments as multiplicative boosts, then re-normalize
            // exp(adjustment) scales the probability: +0.3 → 1.35x boost, -0.3 → 0.74x penalty
            if (priorWeights != null && priorWeights.Count > 0)
            {
                double totalWeight = 0;
                var clampedWeights = new Dictionary<string, double>();
                foreach (var ct in cellTypes)
                {
                    double w = priorWeights.TryGetValue(ct, out double val) ? val : 1.0;
                    clampedWeights[ct] = Math.Max(w, 0.01);
                    totalWeight += clampedWeights[ct];
                }
                double uniform = 1.0 / cellTypes.Count;
                foreach (var ct in cellTypes)
                {
                    double normalized = clampedWeights[ct] / totalWeight;
                    results[ct].PriorAdjustment = Math.Log(normalized / uniform);
                }
            }

            double sumAdjusted = 0;
            foreach (var ct in cellTypes)
            {
                double adjustment = results[ct].KeyMarkerAdjustment + results[ct].PriorAdjustment;
                results[ct].CompositeScore *= Math.Exp(adjustment);
                sumAdjusted += results[ct].CompositeScore;
            }

            // Re-normalize so CompositeScores sum to 1 (true probabilities)
            if (sumAdjusted > 1e-15)
            {
                foreach (var ct in cellTypes)
                    results[ct].CompositeScore /= sumAdjusted;
            }

            // 6. Order by probability (highest first)
            var orderedResults = results
                .OrderByDescending(kvp => kvp.Value.CompositeScore)
                .ToDictionary(kvp => kvp.Key, kvp => kvp.Value);

            var topEntry = orderedResults.First();
            return new CellTypePredictionResult
            {
                Scores = orderedResults,
                TopCellType = topEntry.Key,
                TopScore = topEntry.Value,
                Confidence = topEntry.Value.CompositeScore  // Already a probability (0–1)
            };
        }

        /// <summary>
        /// Computes softmax probabilities for a single metric across all cell types.
        /// P(ct) = exp(value_ct / T) / Σ exp(value_i / T)
        /// Max subtraction used for numerical stability.
        /// </summary>
        private Dictionary<string, double> SoftmaxOverMetric(
            List<string> cellTypes, Func<string, double> metricSelector, double temperature)
        {
            var values = cellTypes.Select(ct => metricSelector(ct) / temperature).ToArray();

            // Subtract max for numerical stability
            double maxVal = values.Max();
            double sumExp = 0;
            for (int i = 0; i < values.Length; i++)
                sumExp += Math.Exp(values[i] - maxVal);

            var result = new Dictionary<string, double>();
            for (int i = 0; i < cellTypes.Count; i++)
                result[cellTypes[i]] = Math.Exp(values[i] - maxVal) / sumExp;

            return result;
        }

        /// <summary>
        /// Calculates the score adjustment based on user-defined key markers
        /// </summary>
        /// <param name="cellType">The cell type being scored</param>
        /// <param name="proteinAbundances">Detected proteins and their abundances</param>
        /// <param name="userKeyMarkers">User-defined key markers per cell type</param>
        /// <param name="medianAbundance">Median abundance in the sample (threshold for high/low)</param>
        /// <param name="score">The CellTypeScore to populate with marker info</param>
        /// <returns>Score adjustment to add to composite score</returns>
        private double CalculateKeyMarkerAdjustment(
            string cellType,
            Dictionary<string, double> proteinAbundances,
            Dictionary<string, HashSet<string>> userKeyMarkers,
            double medianAbundance,
            CellTypeScore score = null)
        {
            // Initialize marker collections in the score
            if (score != null)
            {
                score.MarkersFound = new Dictionary<string, double>();
                score.MarkersMissing = new List<string>();
            }

            // No user-defined markers? No adjustment
            if (userKeyMarkers == null || !userKeyMarkers.ContainsKey(cellType))
                return 0.0;

            var markers = userKeyMarkers[cellType];
            if (markers.Count == 0)
                return 0.0;

            double totalAdjustment = 0.0;

            foreach (var marker in markers)
            {
                if (proteinAbundances.TryGetValue(marker, out double abundance))
                {
                    // Marker is PRESENT
                    score?.MarkersFound.Add(marker, abundance);
                    
                    if (abundance > medianAbundance)
                    {
                        // High abundance: +0.3
                        totalAdjustment += 0.3;
                    }
                    else
                    {
                        // Low abundance: +0.1
                        totalAdjustment += 0.1;
                    }
                }
                else
                {
                    // Marker is ABSENT: -0.1 penalty
                    score?.MarkersMissing.Add(marker);
                    totalAdjustment -= 0.1;
                }
            }

            return totalAdjustment;
        }

        /// <summary>
        /// Calculates the fraction of strict markers for a cell type that are detected in the sample.
        /// Returns 0 if no markers defined.
        /// </summary>
        private double CalculateMarkerCoverage(List<string> detectedProteins, string cellType)
        {
            if (!_cellTypeMarkers.ContainsKey(cellType) || _cellTypeMarkers[cellType].Count == 0)
                return 0;

            int totalMarkers = _cellTypeMarkers[cellType].Count;
            int detected = detectedProteins.Count(p => _cellTypeMarkers[cellType].Contains(p));

            return (double)detected / totalMarkers;
        }

        /// <summary>
        /// Calculates the raw statistical metrics without applying weighting or normalization.
        /// Normalization is handled in the batch context of PredictCellType.
        /// </summary>
        private CellTypeScore CalculateRawMetrics(Dictionary<string, double> proteinAbundances, string cellType)
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

            // 1. Spearman correlation
            double spearmanCorr = CalculateSpearmanCorrelation(proteinAbundances, profile);

            // 2. Specificity-weighted score (Unbounded range)
            double specificityScore = CalculateSpecificityWeightedScore(proteinAbundances, profile);

            // 3. Hypergeometric p-value (Probability 0-1)
            double hypergeometricPValue = CalculateHypergeometricPValue(proteinAbundances.Keys.ToList(), cellType);

            // Return raw values only. CompositeScore is left as 0 
            // because it requires the softmax context (all cell types) to be calculated correctly.
            return new CellTypeScore
            {
                SpearmanCorrelation = spearmanCorr,
                SpecificityScore = specificityScore,
                HypergeometricPValue = hypergeometricPValue,
                CompositeScore = 0
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
                if (!profile.MedianExpression.ContainsKey(protein))
                    continue;

                double medianExpression = profile.MedianExpression[protein];
                if (medianExpression <= 0)
                    continue;

                if (!_geneSpecificity.ContainsKey(protein))
                    continue;

                double specificity = _geneSpecificity[protein];
                if (specificity <= 0)
                    continue;

                totalScore += specificity * Math.Log(medianExpression + 1) * Math.Log(abundance + 1);
                matchCount++;
            }

            if (matchCount == 0)
                return 0;

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

                return 1.0;
            }

            // N = total genes in universe (all genes across all cell types)
            int N = _geneSpecificity.Count;

            // K = number of markers for this cell type
            int K = _cellTypeMarkers[cellType].Count;

            // n = detected proteins THAT LIE IN THE UNIVERSE. The sample must be drawn from the population the
            // test is defined over: counting detected proteins absent from the reference inflates n, which
            // inflates E[X] = nK/N above what k can attain and pushes the right-tailed p toward 1 - silently
            // flattening this channel's softmax so it contributes a constant to the composite. Intersecting here
            // also makes n <= N true by construction, which is the degenerate case MathNet throws on.
            int n = detectedProteins.Count(p => _geneSpecificity.ContainsKey(p));

            // k = overlap (how many detected proteins are markers for this cell type); already a subset of the
            // universe, since markers are drawn from the reference profiles.
            int k = detectedProteins.Count(p => _cellTypeMarkers[cellType].Contains(p));

            //Console.WriteLine($"[DEBUG-HYPER] CellType: {cellType}, N={N}, K={K}, n={n}, k={k}");

            if (K == 0 || n == 0)
            { 
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
            // Defensive guard. MathNet's Hypergeometric validates draws <= population and successes <= population
            // and THROWS otherwise, and there is no try/catch between here and the UI - a single degenerate cell
            // would abort the whole classification run and save nothing. Callers now intersect the sample with the
            // universe so n <= N holds by construction, but a malformed or tiny reference can still violate these,
            // and "no evidence of enrichment" (p = 1) is the correct answer for a degenerate test, not a crash.
            if (N <= 0 || K <= 0 || n <= 0 || K > N || n > N || k <= 0)
                return 1.0;

            // k cannot exceed what is drawable; if it does, the inputs are inconsistent.
            if (k > K || k > n)
                return 1.0;

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

        /// <summary>Which classifier produced this result ("Standard" / "Quantitative"). Gates method-specific display
        /// so the redesign — which has no specificity/hypergeometric/coverage — never renders those as fake zeros.</summary>
        public string ScorerMethod { get; set; } = "Standard";

        /// <summary>
        /// Softmax probability of the top cell type (0–1). 
        /// Higher values indicate stronger classification confidence.
        /// </summary>
        public double Confidence { get; set; }
    }

    /// <summary>
    /// Individual score components for a cell type prediction
    /// </summary>
    public class CellTypeScore
    {
        /// <summary>
        /// Spearman rank correlation between proteomics and transcriptomics profiles (-1 to +1)
        /// Higher = better rank agreement, negative = inverse correlation
        /// </summary>
        public double SpearmanCorrelation { get; set; }

        /// <summary>
        /// Specificity-weighted similarity score (0 to unbounded, typically 0-10)
        /// Higher = more specific marker genes match
        /// </summary>
        public double SpecificityScore { get; set; }

        /// <summary>
        /// Hypergeometric p-value for marker enrichment (0-1)
        /// Lower = more significant enrichment
        /// </summary>
        public double HypergeometricPValue { get; set; }

        /// <summary>
        /// Adjustment from user-defined key markers (can be positive or negative)
        /// Positive = key markers present, Negative = key markers absent
        /// </summary>
        public double KeyMarkerAdjustment { get; set; }

        /// <summary>
        /// Adjustment from user-defined prior weights: ln(normalized_weight / uniform)
        /// Positive = cell type expected, Negative = cell type unexpected
        /// </summary>
        public double PriorAdjustment { get; set; }

        /// <summary>
        /// Fraction of strict cell-type markers detected in the sample (0 to 1)
        /// Higher = more expected markers found, stronger evidence for this cell type
        /// </summary>
        public double MarkerCoverage { get; set; }

        /// <summary>
        /// Softmax probability for this cell type (0–1, all cell types sum to 1)
        /// Higher = better overall match. Directly interpretable as classification probability.
        /// </summary>
        public double CompositeScore { get; set; }

        /// <summary>
        /// Key markers that were found in this sample (with their abundance)
        /// </summary>
        public Dictionary<string, double> MarkersFound { get; set; } = new Dictionary<string, double>();

        /// <summary>
        /// Key markers that were NOT found in this sample
        /// </summary>
        public List<string> MarkersMissing { get; set; } = new List<string>();

        public override string ToString()
        {
            return $"Spearman: {SpearmanCorrelation:F3}, Specificity: {SpecificityScore:F3}, " +
                   $"P-value: {HypergeometricPValue:E2}, Coverage: {MarkerCoverage:F3}, " +
                   $"KeyMarker: {KeyMarkerAdjustment:+0.00;-0.00;0}, Prior: {PriorAdjustment:+0.00;-0.00;0}, Prob: {CompositeScore:P1}";
        }

        /// <summary>
        /// Gets a formatted string showing marker detection status
        /// </summary>
        public string GetMarkerSummary()
        {
            if (MarkersFound.Count == 0 && MarkersMissing.Count == 0)
                return "No key markers defined";

            var parts = new List<string>();
            
            if (MarkersFound.Count > 0)
            {
                var foundList = MarkersFound.OrderByDescending(kv => kv.Value)
                    .Select(kv => $"{kv.Key}");
                parts.Add($"✓ Found: {string.Join(", ", foundList)}");
            }
            
            if (MarkersMissing.Count > 0)
            {
                parts.Add($"✗ Missing: {string.Join(", ", MarkersMissing)}");
            }

            return string.Join(" | ", parts);
        }
    }
}