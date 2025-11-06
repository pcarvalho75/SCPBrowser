using System;
using System.Collections.Generic;
using System.Linq;

namespace SCPBrowser
{
    public class CellTypePredictor
    {
        private readonly TranscriptomicDatabase _database;
        private readonly Dictionary<string, double> _geneSpecificity;
        private readonly Dictionary<string, HashSet<string>> _cellTypeMarkers;

        public CellTypePredictor(TranscriptomicDatabase database, double markerSpecificityThreshold = 0.5)
        {
            _database = database ?? throw new ArgumentNullException(nameof(database));
            _geneSpecificity = CalculateGeneSpecificity();
            _cellTypeMarkers = DefineCellTypeMarkers(markerSpecificityThreshold);
        }

        private Dictionary<string, double> CalculateGeneSpecificity()
        {
            var specificity = new Dictionary<string, double>();
            int totalCellTypes = _database.CellTypeIndex.Count;

            if (totalCellTypes == 0)
                return specificity;

            foreach (var gene in _database.GeneExpression.Keys)
            {
                var cellTypesExpressingGene = new HashSet<string>();

                foreach (var (cellId, count) in _database.GeneExpression[gene])
                {
                    if (_database.CellMetadata.TryGetValue(cellId, out var metadata) &&
                        !string.IsNullOrEmpty(metadata.CellType))
                    {
                        cellTypesExpressingGene.Add(metadata.CellType);
                    }
                }

                int numCellTypes = cellTypesExpressingGene.Count;

                if (numCellTypes > 0)
                {
                    specificity[gene] = Math.Log((double)totalCellTypes / numCellTypes);
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

            foreach (var cellType in _database.CellTypeIndex.Keys)
            {
                markers[cellType] = new HashSet<string>();
                var cellsOfType = _database.CellTypeIndex[cellType].ToHashSet();

                foreach (var gene in _database.GeneExpression.Keys)
                {
                    if (_geneSpecificity.TryGetValue(gene, out double specificity) &&
                        specificity >= specificityThreshold)
                    {
                        var avgExpression = GetAverageExpressionForCellType(gene, cellsOfType);
                        if (avgExpression > 0)
                        {
                            markers[cellType].Add(gene);
                        }
                    }
                }
            }

            return markers;
        }

        public CellTypePredictionResult PredictCellType(Dictionary<string, double> proteinAbundances)
        {
            if (proteinAbundances == null || proteinAbundances.Count == 0)
                return new CellTypePredictionResult();

            var results = new Dictionary<string, CellTypeScore>();

            foreach (var cellType in _database.CellTypeIndex.Keys)
            {
                var score = CalculateComprehensiveScore(proteinAbundances, cellType);
                results[cellType] = score;
            }

            var orderedResults = results.OrderByDescending(kvp => kvp.Value.CompositeScore)
                                       .ToDictionary(kvp => kvp.Key, kvp => kvp.Value);

            return new CellTypePredictionResult
            {
                Scores = orderedResults,
                TopCellType = orderedResults.FirstOrDefault().Key,
                TopScore = orderedResults.FirstOrDefault().Value
            };
        }

        private CellTypeScore CalculateComprehensiveScore(Dictionary<string, double> proteinAbundances, string cellType)
        {
            var cellsOfType = _database.CellTypeIndex[cellType].ToHashSet();

            double spearmanCorr = CalculateSpearmanCorrelation(proteinAbundances, cellsOfType);
            double specificityScore = CalculateSpecificityWeightedScore(proteinAbundances, cellsOfType);
            double hypergeometricPValue = CalculateHypergeometricPValue(proteinAbundances.Keys.ToList(), cellType);

            double compositeScore = (spearmanCorr * 0.4) + (specificityScore * 0.4) + ((1 - hypergeometricPValue) * 0.2);

            return new CellTypeScore
            {
                SpearmanCorrelation = spearmanCorr,
                SpecificityScore = specificityScore,
                HypergeometricPValue = hypergeometricPValue,
                CompositeScore = Math.Max(0, compositeScore)
            };
        }

        private double CalculateSpearmanCorrelation(Dictionary<string, double> proteinAbundances, HashSet<string> cellsOfType)
        {
            var commonProteins = proteinAbundances.Keys
                .Where(p => _database.GeneExpression.ContainsKey(p))
                .ToList();

            if (commonProteins.Count < 3)
                return 0;

            var proteomicsRanks = GetRanks(proteinAbundances
                .Where(kvp => commonProteins.Contains(kvp.Key))
                .Select(kvp => kvp.Value)
                .ToList());

            var transcriptomicsValues = commonProteins
                .Select(p => GetAverageExpressionForCellType(p, cellsOfType))
                .ToList();

            var transcriptomicsRanks = GetRanks(transcriptomicsValues);

            return CalculatePearsonCorrelation(proteomicsRanks, transcriptomicsRanks);
        }

        private double CalculateSpecificityWeightedScore(Dictionary<string, double> proteinAbundances, HashSet<string> cellsOfType)
        {
            double totalScore = 0;
            int matchCount = 0;

            foreach (var protein in proteinAbundances.Keys)
            {
                if (!_geneSpecificity.ContainsKey(protein))
                    continue;

                double specificity = _geneSpecificity[protein];

                if (specificity <= 0)
                    continue;

                double avgExpression = GetAverageExpressionForCellType(protein, cellsOfType);

                if (avgExpression > 0)
                {
                    totalScore += specificity * Math.Log(avgExpression + 1);
                    matchCount++;
                }
            }

            if (matchCount == 0)
                return 0;

            return totalScore / Math.Sqrt(matchCount);
        }

        private double CalculateHypergeometricPValue(List<string> detectedProteins, string cellType)
        {
            if (!_cellTypeMarkers.ContainsKey(cellType))
                return 1.0;

            int N = _database.GeneExpression.Count;
            int K = _cellTypeMarkers[cellType].Count;
            int n = detectedProteins.Count;
            int k = detectedProteins.Count(p => _cellTypeMarkers[cellType].Contains(p));

            if (K == 0 || n == 0)
                return 1.0;

            return CalculateHypergeometricPValueExact(N, K, n, k);
        }

        private double CalculateHypergeometricPValueExact(int N, int K, int n, int k)
        {
            double pValue = 0;

            for (int i = k; i <= Math.Min(n, K); i++)
            {
                double probability = (BinomialCoefficient(K, i) * BinomialCoefficient(N - K, n - i)) /
                                    BinomialCoefficient(N, n);
                pValue += probability;
            }

            return Math.Min(1.0, pValue);
        }

        private double BinomialCoefficient(int n, int k)
        {
            if (k > n || k < 0)
                return 0;
            if (k == 0 || k == n)
                return 1;
            if (k > n - k)
                k = n - k;

            double result = 1;
            for (int i = 1; i <= k; i++)
            {
                result *= (n - k + i);
                result /= i;
            }

            return result;
        }

        private List<double> GetRanks(List<double> values)
        {
            var indexed = values.Select((value, index) => new { value, index }).ToList();
            var sorted = indexed.OrderBy(x => x.value).ToList();

            var ranks = new double[values.Count];
            for (int i = 0; i < sorted.Count; i++)
            {
                ranks[sorted[i].index] = i + 1;
            }

            return ranks.ToList();
        }

        private double CalculatePearsonCorrelation(List<double> x, List<double> y)
        {
            if (x.Count != y.Count || x.Count == 0)
                return 0;

            double meanX = x.Average();
            double meanY = y.Average();

            double numerator = 0;
            double denomX = 0;
            double denomY = 0;

            for (int i = 0; i < x.Count; i++)
            {
                double diffX = x[i] - meanX;
                double diffY = y[i] - meanY;

                numerator += diffX * diffY;
                denomX += diffX * diffX;
                denomY += diffY * diffY;
            }

            if (denomX == 0 || denomY == 0)
                return 0;

            return numerator / Math.Sqrt(denomX * denomY);
        }

        private double GetAverageExpressionForCellType(string geneName, HashSet<string> cellsOfType)
        {
            if (!_database.GeneExpression.ContainsKey(geneName))
                return 0;

            var expressionData = _database.GeneExpression[geneName];
            var relevantCells = expressionData.Where(e => cellsOfType.Contains(e.cellId)).ToList();

            if (relevantCells.Count == 0)
                return 0;

            return relevantCells.Average(e => e.count);
        }
    }

    public class CellTypePredictionResult
    {
        public Dictionary<string, CellTypeScore> Scores { get; set; } = new Dictionary<string, CellTypeScore>();
        public string TopCellType { get; set; }
        public CellTypeScore TopScore { get; set; }
    }

    public class CellTypeScore
    {
        public double SpearmanCorrelation { get; set; }
        public double SpecificityScore { get; set; }
        public double HypergeometricPValue { get; set; }
        public double CompositeScore { get; set; }

        public override string ToString()
        {
            return $"Spearman: {SpearmanCorrelation:F3}, Specificity: {SpecificityScore:F3}, " +
                   $"P-value: {HypergeometricPValue:E2}, Composite: {CompositeScore:F3}";
        }
    }
}