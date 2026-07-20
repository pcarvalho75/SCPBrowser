using System;
using System.Collections.Generic;
using System.Linq;

namespace SCPBrowser.Services
{
    /// <summary>
    /// A cell-type classifier the app can select between at runtime. Both implementations return the SAME wire type
    /// (<see cref="CellTypePredictionResult"/>) so every downstream consumer — DataPoint, the scatter builders, the
    /// hover tooltip, RunDetails, PLP export, the confidence-threshold slider — stays untouched.
    /// </summary>
    public interface IClassifier
    {
        /// <summary>"Standard" or "Quantitative" — persisted per prediction and used to gate method-specific display.</summary>
        string MethodName { get; }

        /// <summary>Per-class marker/signature sets, for the diagnostics consumers. May be null.</summary>
        IReadOnlyDictionary<string, HashSet<string>> MarkerSets { get; }

        CellTypePredictionResult PredictCellType(
            Dictionary<string, double> abundances,
            Dictionary<string, HashSet<string>> keyMarkers,
            HashSet<string> excludedCellTypes,
            Dictionary<string, double> priorWeights);
    }

    /// <summary>The classifier SCPBrowser has always shipped — a thin, behaviour-identical wrapper over CellTypePredictor.</summary>
    public sealed class StandardClassifier : IClassifier
    {
        private readonly CellTypePredictor _predictor;
        public StandardClassifier(TranscriptomicDatabase database) => _predictor = new CellTypePredictor(database);

        public string MethodName => "Standard";
        public IReadOnlyDictionary<string, HashSet<string>> MarkerSets => _predictor.CellTypeMarkers;

        public CellTypePredictionResult PredictCellType(
            Dictionary<string, double> abundances,
            Dictionary<string, HashSet<string>> keyMarkers,
            HashSet<string> excludedCellTypes,
            Dictionary<string, double> priorWeights)
            => _predictor.PredictCellType(abundances, keyMarkers, excludedCellTypes, priorWeights);
    }

    /// <summary>
    /// The quantitative redesign, adapted to the shared wire type. It runs at EQUAL channel weights — the live
    /// configuration, since production has no calibration step and no labels for unknown cells (the calibrated
    /// variant is benchmark-only). The adapter materialises an ORDERED, best-first Scores dictionary because the
    /// confidence-threshold slider reads Scores.First() as the winning class, and recomputes key-marker evidence
    /// (found/missing + adjustment) so the tooltip's ✓/✗ lines survive. The three shipped-only per-class fields
    /// (specificity / hypergeometric p / coverage) have no redesign analogue and are left at neutral defaults;
    /// display of them is gated to the Standard method so the redesign never shows fake zeros.
    /// </summary>
    public sealed class QuantitativeClassifier : IClassifier
    {
        private readonly QuantitativeCellTypeScorer _scorer;
        public QuantitativeClassifier(TranscriptomicDatabase database) => _scorer = new QuantitativeCellTypeScorer(database);

        public string MethodName => "Quantitative";
        public IReadOnlyDictionary<string, HashSet<string>> MarkerSets => _scorer.Signatures;

        public CellTypePredictionResult PredictCellType(
            Dictionary<string, double> abundances,
            Dictionary<string, HashSet<string>> keyMarkers,
            HashSet<string> excludedCellTypes,
            Dictionary<string, double> priorWeights)
        {
            var r = _scorer.Score(abundances, keyMarkers, excludedCellTypes, priorWeights);
            var result = new CellTypePredictionResult { ScorerMethod = "Quantitative" };
            if (r.TopClass == null || r.Probabilities.Count == 0) return result;

            double median = abundances.Count > 0
                ? abundances.Values.OrderBy(x => x).ElementAt(abundances.Count / 2) : 0.0;

            // Insert best-first so Scores.First() is the winner (matches CellTypePredictor's OrderByDescending pattern).
            foreach (var kv in r.Probabilities.OrderByDescending(p => p.Value))
            {
                string cls = kv.Key;
                var score = new CellTypeScore
                {
                    CompositeScore = kv.Value,
                    SpearmanCorrelation = r.Channels.TryGetValue(cls, out var ch) ? ch[0] : 0.0,
                    // No redesign analogue — neutral so a stray read is harmless; display gated to Standard anyway.
                    SpecificityScore = 0.0,
                    HypergeometricPValue = 1.0,
                    MarkerCoverage = 0.0
                };

                // Recompute marker evidence so the tooltip's found/missing lines and the adjustment survive.
                if (keyMarkers != null && keyMarkers.TryGetValue(cls, out var markers) && markers.Count > 0)
                {
                    double adj = 0;
                    foreach (var m in markers)
                    {
                        if (abundances.TryGetValue(m, out var ab)) { score.MarkersFound[m] = ab; adj += ab > median ? 0.3 : 0.1; }
                        else { score.MarkersMissing.Add(m); adj -= 0.1; }
                    }
                    score.KeyMarkerAdjustment = adj;
                }

                result.Scores[cls] = score;
            }

            var top = result.Scores.First();
            result.TopCellType = top.Key;
            result.TopScore = top.Value;
            result.Confidence = r.Confidence;
            result.ScorerMethod = "Quantitative";
            return result;
        }
    }
}
