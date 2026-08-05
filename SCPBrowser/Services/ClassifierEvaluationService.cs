using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace SCPBrowser.Services
{
    /// <summary>
    /// Cross-validated evaluation of the cell-type classifier against ground-truth biological conditions.
    ///
    /// Design constraints (deliberate — do not "optimise" these away):
    ///  - The SCORING ENGINE IS NOT TOUCHED. Every prediction goes through the shipped
    ///    <see cref="ReferenceDataService.BuildProteomicReference"/> + <see cref="CellTypePredictor.PredictCellType"/>.
    ///    If the benchmark measured a modified scorer the number would be meaningless.
    ///  - NOTHING IS PERSISTED. The normal reference-build path calls WriteTranscriptomicDataAsync(clearExistingData:true),
    ///    which WIPES the project's reference; and raw_file_cell_type_classifications is global (not import-scoped).
    ///    So every fold builds its reference purely in memory and results leave only via the CSV the caller writes.
    ///  - NO LEAKAGE. A fold's reference is built strictly from its training runs (BuildProteomicReference iterates
    ///    runCellTypeMap.Keys, so passing only train runs yields a train-only reference), and test cells are disjoint.
    ///  - Training labels ARE the ground-truth conditions, so the label namespaces are identical by construction
    ///    (the app's normal path uses user-typed labels, which need reconciliation; here that problem cannot arise).
    /// </summary>
    public static class ClassifierEvaluationService
    {
        public sealed class Options
        {
            /// <summary>Conditions to benchmark. Null/empty = every condition present in the data.</summary>
            public HashSet<string> IncludeConditions { get; set; } = new();

            public int Folds { get; set; } = 5;
            public int Repeats { get; set; } = 10;
            public int Seed { get; set; } = 42;

            public bool RunLoocv { get; set; } = true;

            /// <summary>Reference cells per class for the learning curve. 1 is unsafe (see ReferenceUniverseSize).</summary>
            public List<int> LearningCurveSizes { get; set; } = new List<int> { 2, 3, 5, 10, 17 };
            public int LearningCurveRepeats { get; set; } = 5;

            /// <summary>
            /// Marker robustness threshold passed to CellTypePredictor. 0.2 = the shipped default, which is a no-op
            /// against percentage-scale proteomic profiles. 20.0 = actually require 20% of the class's cells to
            /// express the gene. Exposed so the fix can be A/B measured rather than assumed.
            /// </summary>
            public double MinPercentExpressing { get; set; } = 0.2;

            /// <summary>
            /// Use the quantitative redesign (QuantitativeCellTypeScorer) instead of the shipped CellTypePredictor.
            /// Its channel weights are calibrated by an inner CV on each fold's TRAINING cells, so it is only
            /// meaningful where ground-truth labels exist — i.e. here, not in live classification.
            /// </summary>
            public bool UseQuantitativeScorer { get; set; } = true;

            /// <summary>
            /// Calibrate the redesign's channel weights by inner CV on each fold's training cells. TRUE reflects the
            /// benchmark (labels available); FALSE (equal weights) reflects what LIVE classification would actually
            /// run, since the production path has no calibration step and no labels for unknown cells. This flag is
            /// the difference between "how good can it be" and "how good would it be if we shipped it".
            /// </summary>
            public bool CalibrateQuantWeights { get; set; } = true;

            /// <summary>Protein-subset resampling (reviewer-requested uncertainty quantification).</summary>
            public bool RunSubsetStability { get; set; } = true;
            public int SubsetRepeats { get; set; } = 50;
            public double SubsetFraction { get; set; } = 0.5;
        }

        public sealed class CellPrediction
        {
            public string RunName { get; set; } = string.Empty;
            public string TrueLabel { get; set; } = string.Empty;
            public string PredictedLabel { get; set; } = string.Empty;
            public int Repeat { get; set; }
            public int Fold { get; set; }
            public double TopProbability { get; set; }
            public int DetectedGenes { get; set; }
            public Dictionary<string, double> ClassScores { get; set; } = new Dictionary<string, double>();
            /// <summary>class -> [spearman, specificity, hypergeometricP, markerCoverage] as returned by the engine.</summary>
            public Dictionary<string, double[]> RawMetrics { get; set; } = new Dictionary<string, double[]>();
            public bool Correct =>
                PredictedLabel != null && string.Equals(PredictedLabel, TrueLabel, StringComparison.OrdinalIgnoreCase);
        }

        public sealed class ClassMetrics
        {
            public string Label { get; set; } = string.Empty;
            public int Support { get; set; }
            public double Precision { get; set; }
            public double Recall { get; set; }
            public double F1 { get; set; }
        }

        public sealed class LearningCurvePoint
        {
            public int ReferenceCellsPerClass { get; set; }
            public double Accuracy { get; set; }
            public int Evaluated { get; set; }
            public int Skipped { get; set; }
        }

        public sealed class Report
        {
            /// <summary>Which scorer produced this report.</summary>
            public string ScorerName { get; set; } = "shipped 4-metric";
            /// <summary>Channel names matching CellPrediction.RawMetrics, in order.</summary>
            public string[] ChannelNames { get; set; } = { "Spearman", "Specificity", "Enrichment", "Coverage" };
            /// <summary>True when RawMetrics came from the redesign (z-standardised fusion, not engine temperatures).</summary>
            public bool IsQuantitativeScorer { get; set; }

            public List<string> Labels { get; set; } = new List<string>();

            /// <summary>Cells with an in-scope label (the intended denominator).</summary>
            public int TotalCells { get; set; }
            /// <summary>Distinct cells that actually produced at least one prediction.</summary>
            public int EvaluatedCells { get; set; }
            /// <summary>Total predictions made (cells x repeats).</summary>
            public int PredictionsTotal { get; set; }

            /// <summary>Confusion[trueIdx, predIdx], POOLED over every repeat. Counts PREDICTIONS, not cells.</summary>
            public int[,] Confusion { get; set; }

            /// <summary>Accuracy pooled over all predictions (cells x repeats).</summary>
            public double Accuracy { get; set; }
            public double BalancedAccuracy { get; set; }
            public List<ClassMetrics> PerClass { get; set; } = new List<ClassMetrics>();

            // --- Honest uncertainty -------------------------------------------------------------------------
            // A CI must be built on INDEPENDENT units. The 90 cells are independent; the 10 repeats are NOT —
            // they re-score the same 90 cells under different fold shuffles. Taking sd across repeats and
            // dividing by sqrt(repeats) yields an interval that measures fold-shuffle jitter and that narrows
            // without bound as Repeats is raised — a fabricated precision. So the CI below is a Wilson score
            // interval on a SINGLE pass (one vote per cell, n = independent cells), and the across-repeat
            // spread is reported separately as what it actually is: partition variability, not a CI.
            public int SinglePassCorrect { get; set; }
            public int SinglePassTotal { get; set; }
            public double SinglePassAccuracy { get; set; }
            public double SinglePassCiLow { get; set; }
            public double SinglePassCiHigh { get; set; }

            /// <summary>SD of accuracy across repeats = fold-partition variability. NOT a CI on accuracy.</summary>
            public double FoldPartitionSd { get; set; }
            public List<double> RepeatAccuracies { get; set; } = new List<double>();

            public double? LoocvAccuracy { get; set; }
            public int LoocvCorrect { get; set; }
            public int LoocvTotal { get; set; }
            public double LoocvCiLow { get; set; }
            public double LoocvCiHigh { get; set; }
            public List<LearningCurvePoint> LearningCurve { get; set; } = new List<LearningCurvePoint>();

            /// <summary>run → fraction of protein-subsets agreeing with that cell's modal call.</summary>
            public Dictionary<string, double> SubsetStability { get; set; } = new Dictionary<string, double>();
            /// <summary>run → fraction of protein-subsets whose call equals the TRUE label.</summary>
            public Dictionary<string, double> SubsetAccuracy { get; set; } = new Dictionary<string, double>();

            public List<CellPrediction> PerCell { get; set; } = new List<CellPrediction>();

            /// <summary>Cells skipped because detected genes exceeded the reference universe (unguarded throw upstream).</summary>
            public int SkippedNGreaterThanN { get; set; }
            public List<string> Warnings { get; set; } = new List<string>();
        }

        // ---------------------------------------------------------------------------------------------------------

        public static Report Run(ProteomicsData data, Options options, IProgress<string> progress = null)
        {
            if (data == null) throw new ArgumentNullException(nameof(data));
            options ??= new Options();

            var labelMap = BuildLabelMap(data, options.IncludeConditions);
            if (labelMap.Count == 0)
                throw new InvalidOperationException("No cells with a biological condition — nothing to evaluate.");

            var labels = labelMap.Values.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(l => l, StringComparer.Ordinal).ToList();
            if (labels.Count < 2)
                throw new InvalidOperationException($"Need at least 2 conditions to benchmark; found {labels.Count}.");

            var report = new Report
            {
                Labels = labels,
                TotalCells = labelMap.Count,
                IsQuantitativeScorer = options.UseQuantitativeScorer,
                ScorerName = options.UseQuantitativeScorer
                    ? "QScore classifier (margin specificity + AUROC + detection likelihood, product-of-experts fusion)"
                    : "Standard 4-metric (Spearman + specificity + enrichment + coverage, arithmetic mean)",
                ChannelNames = options.UseQuantitativeScorer
                    ? new[] { "Spearman", "SignatureRank", "AUROC", "DetectionLL" }
                    : new[] { "Spearman", "Specificity", "Enrichment", "Coverage" }
            };

            var byClass = labelMap.GroupBy(kv => kv.Value, StringComparer.OrdinalIgnoreCase)
                                  .ToDictionary(g => g.Key, g => g.Select(kv => kv.Key).ToList(), StringComparer.OrdinalIgnoreCase);
            foreach (var c in byClass.OrderBy(c => c.Key))
                progress?.Report($"  {c.Key}: {c.Value.Count} cells");

            int minClass = byClass.Values.Min(v => v.Count);
            if (minClass < options.Folds)
                report.Warnings.Add($"Smallest class has {minClass} cells but {options.Folds} folds requested — some folds will have no test cell for it.");

            // ---- Primary: stratified repeated k-fold CV -------------------------------------------------------
            var allPredictions = new List<CellPrediction>();
            for (int rep = 0; rep < Math.Max(1, options.Repeats); rep++)
            {
                progress?.Report($"Cross-validation repeat {rep + 1}/{options.Repeats}...");
                var rng = new Random(options.Seed + rep);
                var folds = StratifiedFolds(byClass, options.Folds, rng);

                for (int f = 0; f < options.Folds; f++)
                {
                    var testRuns = folds[f];
                    if (testRuns.Count == 0) continue;

                    var trainMap = labelMap.Where(kv => !testRuns.Contains(kv.Key))
                                           .ToDictionary(kv => kv.Key, kv => kv.Value);

                    var preds = ScoreCells(data, trainMap, testRuns, labelMap, rep, f, report, progress, options.MinPercentExpressing, data, options);
                    allPredictions.AddRange(preds);
                }
            }

            report.PerCell = allPredictions;
            ComputePrimaryMetrics(report, allPredictions, labels, options.Repeats);

            // ---- LOOCV (deterministic cross-check) ------------------------------------------------------------
            if (options.RunLoocv)
            {
                progress?.Report("Leave-one-out cross-validation...");
                int correct = 0, n = 0;
                foreach (var run in labelMap.Keys.OrderBy(r => r, StringComparer.Ordinal))
                {
                    var testRuns = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { run };
                    var trainMap = labelMap.Where(kv => !string.Equals(kv.Key, run, StringComparison.OrdinalIgnoreCase))
                                           .ToDictionary(kv => kv.Key, kv => kv.Value);
                    var preds = ScoreCells(data, trainMap, testRuns, labelMap, -1, -1, report, null, options.MinPercentExpressing, data, options);
                    foreach (var p in preds) { n++; if (p.Correct) correct++; }
                }
                report.LoocvCorrect = correct;
                report.LoocvTotal = n;
                report.LoocvAccuracy = n > 0 ? (double)correct / n : (double?)null;
                if (n > 0)
                {
                    var (lo, hi) = WilsonInterval(correct, n);
                    report.LoocvCiLow = lo;
                    report.LoocvCiHigh = hi;
                }
                progress?.Report($"  LOOCV accuracy: {report.LoocvAccuracy:P1} ({correct}/{n})");
            }

            // ---- Learning curve ------------------------------------------------------------------------------
            foreach (int k in (options.LearningCurveSizes ?? new List<int>()).OrderBy(x => x))
            {
                if (k < 1) continue;
                if (k >= minClass)
                {
                    report.Warnings.Add($"Learning-curve k={k} skipped: smallest class has only {minClass} cells (no test cells left).");
                    continue;
                }
                progress?.Report($"Learning curve: {k} reference cells/class...");
                int correct = 0, total = 0, skipped = 0;
                for (int r = 0; r < Math.Max(1, options.LearningCurveRepeats); r++)
                {
                    var rng = new Random(options.Seed * 31 + k * 7 + r);
                    var trainMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    var testRuns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var c in byClass)
                    {
                        var shuffled = c.Value.OrderBy(_ => rng.Next()).ToList();
                        foreach (var run in shuffled.Take(k)) trainMap[run] = c.Key;
                        foreach (var run in shuffled.Skip(k)) testRuns.Add(run);
                    }
                    var before = report.SkippedNGreaterThanN;
                    var preds = ScoreCells(data, trainMap, testRuns, labelMap, -2, k, report, null, options.MinPercentExpressing, data, options);
                    skipped += report.SkippedNGreaterThanN - before;
                    foreach (var p in preds) { total++; if (p.Correct) correct++; }
                }
                report.LearningCurve.Add(new LearningCurvePoint
                {
                    ReferenceCellsPerClass = k,
                    Accuracy = total > 0 ? (double)correct / total : 0,
                    Evaluated = total,
                    Skipped = skipped
                });
            }

            // ---- Protein-subset stability (uncertainty quantification) ----------------------------------------
            if (options.RunSubsetStability)
            {
                progress?.Report($"Protein-subset resampling ({options.SubsetRepeats} x {options.SubsetFraction:P0})...");
                RunSubsetStability(data, labelMap, byClass, options, report, progress);
            }

            return report;
        }

        // ---------------------------------------------------------------------------------------------------------

        private static Dictionary<string, string> BuildLabelMap(ProteomicsData data, HashSet<string> include)
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in data.BiologicalConditionPerFile)
            {
                if (string.IsNullOrWhiteSpace(kv.Value)) continue;
                if (include != null && include.Count > 0 && !include.Contains(kv.Value)) continue;
                map[kv.Key] = kv.Value.Trim();
            }
            return map;
        }

        private static List<HashSet<string>> StratifiedFolds(
            Dictionary<string, List<string>> byClass, int folds, Random rng)
        {
            var result = new List<HashSet<string>>();
            for (int i = 0; i < folds; i++) result.Add(new HashSet<string>(StringComparer.OrdinalIgnoreCase));

            // Round-robin each class's shuffled cells across folds → near-equal class balance in every fold.
            foreach (var c in byClass.OrderBy(c => c.Key, StringComparer.Ordinal))
            {
                var shuffled = c.Value.OrderBy(_ => rng.Next()).ToList();
                for (int i = 0; i < shuffled.Count; i++)
                    result[i % folds].Add(shuffled[i]);
            }
            return result;
        }

        /// <summary>Builds an in-memory predictor from the training runs only. Nothing is written to disk.</summary>
        private static CellTypePredictor BuildPredictor(ProteomicsData data, Dictionary<string, string> trainMap,
            double minPercentExpressing = 0.2)
        {
            var refService = new ReferenceDataService();
            var parsed = refService.BuildProteomicReference(data, trainMap);
            if (parsed?.CellTypeProfiles == null || parsed.CellTypeProfiles.Count == 0)
                return null;

            var db = new TranscriptomicDatabase
            {
                CellTypeProfiles = parsed.CellTypeProfiles
                    .GroupBy(p => p.CellType, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase),
                CellTypeMetadata = (parsed.CellTypeMetadata ?? new List<CellTypeMetadata>())
                    .GroupBy(m => m.CellType, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase)
            };
            return new CellTypePredictor(db, -1, minPercentExpressing);
        }

        /// <summary>
        /// The reference gene universe — mirrors CellTypePredictor.CalculateGeneSpecificity, which counts distinct
        /// genes over profile.MedianExpression. Used to pre-empt the unguarded n>N hypergeometric throw.
        /// </summary>
        private static HashSet<string> UniverseFromDb(TranscriptomicDatabase db)
        {
            var universe = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in db.CellTypeProfiles.Values)
                foreach (var g in p.MedianExpression.Keys)
                    universe.Add(g);
            return universe;
        }

        private static List<CellPrediction> ScoreCells(
            ProteomicsData data,
            Dictionary<string, string> trainMap,
            HashSet<string> testRuns,
            Dictionary<string, string> labelMap,
            int repeat,
            int fold,
            Report report,
            IProgress<string> progress,
            double minPercentExpressing = 0.2,
            ProteomicsData dataForCalibration = null,
            Options optionsForCalibration = null)
        {
            bool useQuant = optionsForCalibration?.UseQuantitativeScorer == true;
            var results = new List<CellPrediction>();

            var refService = new ReferenceDataService();
            var parsed = refService.BuildProteomicReference(data, trainMap);
            if (parsed?.CellTypeProfiles == null || parsed.CellTypeProfiles.Count == 0)
            {
                report.Warnings.Add($"Fold {fold} (repeat {repeat}) produced an empty reference — skipped.");
                return results;
            }

            var db = new TranscriptomicDatabase
            {
                CellTypeProfiles = parsed.CellTypeProfiles
                    .GroupBy(p => p.CellType, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase),
                CellTypeMetadata = (parsed.CellTypeMetadata ?? new List<CellTypeMetadata>())
                    .GroupBy(m => m.CellType, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase)
            };

            int universeSize = UniverseFromDb(db).Count;
            var predictor = new CellTypePredictor(db, -1, minPercentExpressing);

            // The redesign's channel weights are learned by an INNER CV over this fold's TRAINING cells only, so the
            // held-out cells never influence them. Without labels this calibration is impossible — which is exactly
            // why the redesign is offered here and not in the live classification path.
            QuantitativeCellTypeScorer quant = null;
            if (useQuant)
            {
                quant = new QuantitativeCellTypeScorer(db);
                // Equal weights (CalibrateQuantWeights == false) == the live-production configuration: no labels to
                // learn from. Only calibrate when explicitly asked (the benchmark condition).
                if (optionsForCalibration?.CalibrateQuantWeights == true && dataForCalibration != null)
                    quant.SetChannelWeights(CalibrateQuantViaInnerCv(dataForCalibration, trainMap, optionsForCalibration, fold));
            }

            foreach (var run in testRuns)
            {
                var abundances = CellTypeClassificationManager.ExtractProteinAbundances(data, run);
                if (abundances.Count == 0) continue;

                // Guard the known unguarded hazard rather than letting it throw: CellTypePredictor's hypergeometric
                // calls Hypergeometric(N, K, n) with n = ALL detected genes and N = reference universe. n > N is
                // mathematically undefined and Math.NET throws (no try/catch upstream). The quantitative scorer has
                // no such call and is safe at any gene count, so only skip on the Standard path.
                if (!useQuant && abundances.Count > universeSize)
                {
                    report.SkippedNGreaterThanN++;
                    continue;
                }

                CellPrediction cp;

                if (useQuant)
                {
                    var qr = quant.Score(abundances);
                    if (qr?.TopClass == null) continue;
                    cp = new CellPrediction
                    {
                        RunName = run,
                        TrueLabel = labelMap.TryGetValue(run, out var tq) ? tq : "",
                        PredictedLabel = qr.TopClass,
                        Repeat = repeat,
                        Fold = fold,
                        TopProbability = qr.Confidence,
                        DetectedGenes = abundances.Count
                    };
                    foreach (var kv in qr.Probabilities) cp.ClassScores[kv.Key] = kv.Value;
                    foreach (var kv in qr.Channels) cp.RawMetrics[kv.Key] = kv.Value;
                }
                else
                {
                    var prediction = predictor.PredictCellType(abundances);
                    if (prediction?.TopCellType == null) continue;

                    cp = new CellPrediction
                    {
                        RunName = run,
                        TrueLabel = labelMap.TryGetValue(run, out var t) ? t : "",
                        PredictedLabel = prediction.TopCellType,
                        Repeat = repeat,
                        Fold = fold,
                        TopProbability = prediction.Confidence,
                        DetectedGenes = abundances.Count
                    };
                    foreach (var kv in prediction.Scores)
                    {
                        cp.ClassScores[kv.Key] = kv.Value.CompositeScore;
                        cp.RawMetrics[kv.Key] = new[]
                        {
                            kv.Value.SpearmanCorrelation,
                            kv.Value.SpecificityScore,
                            kv.Value.HypergeometricPValue,
                            kv.Value.MarkerCoverage
                        };
                    }
                }
                results.Add(cp);
            }

            return results;
        }

        private static void ComputePrimaryMetrics(Report report, List<CellPrediction> all, List<string> labels, int repeats)
        {
            var idx = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < labels.Count; i++) idx[labels[i]] = i;

            report.PredictionsTotal = all.Count;
            report.EvaluatedCells = all.Select(p => p.RunName).Distinct(StringComparer.OrdinalIgnoreCase).Count();

            // Confusion POOLED over every repeat. Using repeat 0 alone would rest claims about the error structure
            // on a handful of errors; pooling uses all of them. It counts predictions (cells x repeats), so the
            // matrix is labelled as such and the binomial CI is taken from a single pass instead (see below).
            var cm = new int[labels.Count, labels.Count];
            foreach (var p in all)
            {
                if (!idx.TryGetValue(p.TrueLabel ?? "", out int ti)) continue;
                if (!idx.TryGetValue(p.PredictedLabel ?? "", out int pi)) continue;
                cm[ti, pi]++;
            }
            report.Confusion = cm;
            report.Accuracy = all.Count > 0 ? (double)all.Count(p => p.Correct) / all.Count : 0;

            // Wilson score interval on ONE pass = one vote per cell, so n is the number of independent cells.
            var single = all.Where(p => p.Repeat == 0).ToList();
            report.SinglePassTotal = single.Count;
            report.SinglePassCorrect = single.Count(p => p.Correct);
            if (report.SinglePassTotal > 0)
            {
                report.SinglePassAccuracy = (double)report.SinglePassCorrect / report.SinglePassTotal;
                var (lo, hi) = WilsonInterval(report.SinglePassCorrect, report.SinglePassTotal);
                report.SinglePassCiLow = lo;
                report.SinglePassCiHigh = hi;
            }

            var perClass = new List<ClassMetrics>();
            double recallSum = 0;
            for (int i = 0; i < labels.Count; i++)
            {
                int tp = cm[i, i];
                int support = 0, predicted = 0;
                for (int j = 0; j < labels.Count; j++) { support += cm[i, j]; predicted += cm[j, i]; }
                double precision = predicted > 0 ? (double)tp / predicted : 0;
                double recall = support > 0 ? (double)tp / support : 0;
                double f1 = (precision + recall) > 0 ? 2 * precision * recall / (precision + recall) : 0;
                recallSum += recall;
                perClass.Add(new ClassMetrics { Label = labels[i], Support = support, Precision = precision, Recall = recall, F1 = f1 });
            }
            report.PerClass = perClass;
            report.BalancedAccuracy = labels.Count > 0 ? recallSum / labels.Count : 0;

            // Across-repeat spread = how much the score moves when the fold partition is reshuffled. This is NOT
            // sampling uncertainty and must never be divided by sqrt(repeats) and called a CI.
            var accs = new List<double>();
            for (int r = 0; r < repeats; r++)
            {
                var rep = all.Where(p => p.Repeat == r).ToList();
                if (rep.Count > 0) accs.Add((double)rep.Count(p => p.Correct) / rep.Count);
            }
            report.RepeatAccuracies = accs;
            if (accs.Count > 1)
            {
                double mean = accs.Average();
                report.FoldPartitionSd = Math.Sqrt(accs.Sum(a => (a - mean) * (a - mean)) / (accs.Count - 1));
            }
        }

        /// <summary>
        /// Wilson score interval for a binomial proportion — the honest 95% CI for k successes in n independent
        /// trials. Preferred over the normal approximation, which misbehaves badly near p=1 (as here).
        /// </summary>
        private static (double lo, double hi) WilsonInterval(int successes, int n, double z = 1.96)
        {
            if (n <= 0) return (0, 0);
            double p = (double)successes / n;
            double z2 = z * z;
            double denom = 1.0 + z2 / n;
            double centre = (p + z2 / (2.0 * n)) / denom;
            double half = (z / denom) * Math.Sqrt(p * (1 - p) / n + z2 / (4.0 * (double)n * n));
            return (Math.Max(0, centre - half), Math.Min(1, centre + half));
        }

        /// <summary>
        /// Reviewer-requested uncertainty quantification: re-classify each held-out cell using many random subsets
        /// of the measured proteins and report how often the calls agree. Unlike the softmax confidence (which is a
        /// RELATIVE quantity that shrinks toward 1/N as classes are added), this is an empirical per-cell stability.
        /// </summary>
        private static void RunSubsetStability(
            ProteomicsData data,
            Dictionary<string, string> labelMap,
            Dictionary<string, List<string>> byClass,
            Options options,
            Report report,
            IProgress<string> progress)
        {
            var rng = new Random(options.Seed + 977);

            // One leak-free stratified split (fold 0 of a fresh partition) so the reference never sees the test cells.
            var folds = StratifiedFolds(byClass, options.Folds, new Random(options.Seed));
            var perRunCalls = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

            for (int f = 0; f < folds.Count; f++)
            {
                var testRuns = folds[f];
                if (testRuns.Count == 0) continue;
                var trainMap = labelMap.Where(kv => !testRuns.Contains(kv.Key)).ToDictionary(kv => kv.Key, kv => kv.Value);

                // Subset stability must use the SAME scorer the report is branded with, or the "uncertainty" figure
                // silently measures a different classifier than everything else in the report.
                bool useQuant = options.UseQuantitativeScorer;
                var parsed = new ReferenceDataService().BuildProteomicReference(data, trainMap);
                if (parsed?.CellTypeProfiles == null || parsed.CellTypeProfiles.Count < 2) continue;
                var db = new TranscriptomicDatabase
                {
                    CellTypeProfiles = parsed.CellTypeProfiles.GroupBy(p => p.CellType, StringComparer.OrdinalIgnoreCase)
                        .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase),
                    CellTypeMetadata = (parsed.CellTypeMetadata ?? new List<CellTypeMetadata>())
                        .GroupBy(m => m.CellType, StringComparer.OrdinalIgnoreCase)
                        .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase)
                };
                int universe = UniverseFromDb(db).Count;

                var stdPredictor = useQuant ? null : new CellTypePredictor(db, -1, options.MinPercentExpressing);
                var quantScorer = useQuant ? new QuantitativeCellTypeScorer(db) : null;

                // Calibrate exactly as ScoreCells does, from this fold's TRAINING cells only. Without this the
                // stability pass ran the scorer at equal channel weights while every other number in the report was
                // calibrated - so the figure presented as the report's uncertainty measured a different classifier.
                // The gap is not academic: equal-weight log-pooling lets a weak channel veto a strong one (see the
                // note in QuantitativeCellTypeScorer), which is precisely this configuration.
                if (useQuant && options.CalibrateQuantWeights)
                    quantScorer.SetChannelWeights(CalibrateQuantViaInnerCv(data, trainMap, options, f));

                Func<Dictionary<string, double>, string> topClass = subset =>
                    useQuant ? quantScorer.Score(subset)?.TopClass : stdPredictor.PredictCellType(subset)?.TopCellType;

                foreach (var run in testRuns)
                {
                    var full = CellTypeClassificationManager.ExtractProteinAbundances(data, run);
                    if (full.Count == 0) continue;
                    var calls = new List<string>();

                    for (int s = 0; s < options.SubsetRepeats; s++)
                    {
                        // Random subset of the measured proteins — simulates "what if we'd measured only these?"
                        var subset = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
                        foreach (var kv in full)
                            if (rng.NextDouble() < options.SubsetFraction) subset[kv.Key] = kv.Value;

                        if (subset.Count < 10) continue;
                        if (!useQuant && subset.Count > universe) continue; // n>N guard is only for the shipped hypergeometric

                        var top = topClass(subset);
                        if (top != null) calls.Add(top);
                    }

                    if (calls.Count > 0) perRunCalls[run] = calls;
                }
                progress?.Report($"  subset fold {f + 1}/{folds.Count}");
            }

            foreach (var kv in perRunCalls)
            {
                var calls = kv.Value;
                var modal = calls.GroupBy(c => c, StringComparer.OrdinalIgnoreCase).OrderByDescending(g => g.Count()).First();
                report.SubsetStability[kv.Key] = (double)modal.Count() / calls.Count;

                string trueLabel = labelMap.TryGetValue(kv.Key, out var t) ? t : "";
                int hits = calls.Count(c => string.Equals(c, trueLabel, StringComparison.OrdinalIgnoreCase));
                report.SubsetAccuracy[kv.Key] = (double)hits / calls.Count;
            }
        }

        // ---------------------------------------------------------------------------------------------------------

        /// <summary>Per-cell CSV — the durable artefact every figure derives from (confusion, curve, ROC, calibration).</summary>
        public static void WritePerCellCsv(Report report, string path)
        {
            var sb = new StringBuilder();
            var labels = report.Labels;

            sb.Append("run,true_label,predicted_label,correct,repeat,fold,top_probability,detected_genes,subset_stability,subset_accuracy");
            foreach (var l in labels) sb.Append(",score_").Append(Sanitize(l));
            sb.AppendLine();

            foreach (var p in report.PerCell)
            {
                sb.Append(Csv(p.RunName)).Append(',')
                  .Append(Csv(p.TrueLabel)).Append(',')
                  .Append(Csv(p.PredictedLabel)).Append(',')
                  .Append(p.Correct ? "1" : "0").Append(',')
                  .Append(p.Repeat.ToString(CultureInfo.InvariantCulture)).Append(',')
                  .Append(p.Fold.ToString(CultureInfo.InvariantCulture)).Append(',')
                  .Append(p.TopProbability.ToString("R", CultureInfo.InvariantCulture)).Append(',')
                  .Append(p.DetectedGenes.ToString(CultureInfo.InvariantCulture)).Append(',')
                  .Append(report.SubsetStability.TryGetValue(p.RunName ?? "", out var st) ? st.ToString("R", CultureInfo.InvariantCulture) : "")
                  .Append(',')
                  .Append(report.SubsetAccuracy.TryGetValue(p.RunName ?? "", out var sa) ? sa.ToString("R", CultureInfo.InvariantCulture) : "");

                foreach (var l in labels)
                    sb.Append(',').Append(p.ClassScores.TryGetValue(l, out var v) ? v.ToString("R", CultureInfo.InvariantCulture) : "");
                sb.AppendLine();
            }

            File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
        }

        /// <summary>Human-readable summary: confusion matrix, per-class metrics, learning curve, stability.</summary>
        public static string FormatSummary(Report r)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"Scorer          : {r.ScorerName}");
            sb.AppendLine("                  (scored WITHOUT key markers, priors or cell-type exclusions — the live app");
            sb.AppendLine("                   applies those if configured, so this measures the unaided classifier)");
            sb.AppendLine($"Cells in scope  : {r.TotalCells}   evaluated: {r.EvaluatedCells}   predictions: {r.PredictionsTotal}");
            sb.AppendLine($"Classes         : {r.Labels.Count} ({string.Join(", ", r.Labels)})");
            if (r.EvaluatedCells < r.TotalCells)
                sb.AppendLine($"  !! {r.TotalCells - r.EvaluatedCells} cell(s) produced no prediction and are EXCLUDED from every metric below.");
            sb.AppendLine();

            sb.AppendLine($"Accuracy (pooled over {r.RepeatAccuracies.Count} repeats) : {r.Accuracy:P2}   [{r.PredictionsTotal} predictions]");
            sb.AppendLine($"Balanced accuracy                    : {r.BalancedAccuracy:P2}");
            sb.AppendLine();
            sb.AppendLine("Uncertainty (binomial on independent cells — NOT on repeats):");
            sb.AppendLine($"  single CV pass : {r.SinglePassAccuracy:P2}  ({r.SinglePassCorrect}/{r.SinglePassTotal})  Wilson 95% CI [{r.SinglePassCiLow:P1}, {r.SinglePassCiHigh:P1}]");
            if (r.LoocvAccuracy.HasValue)
                sb.AppendLine($"  LOOCV          : {r.LoocvAccuracy.Value:P2}  ({r.LoocvCorrect}/{r.LoocvTotal})  Wilson 95% CI [{r.LoocvCiLow:P1}, {r.LoocvCiHigh:P1}]");
            if (r.RepeatAccuracies.Count > 1)
                sb.AppendLine($"  fold-partition SD across {r.RepeatAccuracies.Count} repeats: {r.FoldPartitionSd:P2}  (reshuffle stability — NOT a CI on accuracy)");
            sb.AppendLine();

            sb.AppendLine("Confusion matrix (rows = true, cols = predicted; counts PREDICTIONS pooled over repeats):");
            int w = Math.Max(8, r.Labels.Max(l => l.Length) + 1);
            sb.Append("".PadRight(w));
            foreach (var l in r.Labels) sb.Append(l.PadLeft(w));
            sb.AppendLine();
            for (int i = 0; i < r.Labels.Count; i++)
            {
                sb.Append(r.Labels[i].PadRight(w));
                for (int j = 0; j < r.Labels.Count; j++) sb.Append(r.Confusion[i, j].ToString().PadLeft(w));
                sb.AppendLine();
            }
            sb.AppendLine();

            sb.AppendLine("Per-class metrics:");
            sb.AppendLine($"  {"class".PadRight(w)}{"support",9}{"precision",11}{"recall",9}{"F1",9}");
            foreach (var c in r.PerClass)
                sb.AppendLine($"  {c.Label.PadRight(w)}{c.Support,9}{c.Precision,11:P1}{c.Recall,9:P1}{c.F1,9:P1}");
            sb.AppendLine();

            if (r.LearningCurve.Count > 0)
            {
                sb.AppendLine("Learning curve (reference cells/class -> accuracy):");
                foreach (var p in r.LearningCurve)
                    sb.AppendLine($"  k={p.ReferenceCellsPerClass,-3} {p.Accuracy,7:P1}   ({p.Evaluated} predictions{(p.Skipped > 0 ? $", {p.Skipped} skipped" : "")})");
                sb.AppendLine();
            }

            if (r.SubsetStability.Count > 0)
            {
                var vals = r.SubsetStability.Values.ToList();
                var accs = r.SubsetAccuracy.Values.ToList();
                sb.AppendLine("Protein-subset resampling (uncertainty quantification):");
                sb.AppendLine($"  cells assessed        : {vals.Count}");
                sb.AppendLine($"  mean modal agreement  : {vals.Average():P1}   (min {vals.Min():P1})");
                sb.AppendLine($"  mean subset accuracy  : {accs.Average():P1}");
                sb.AppendLine($"  fully stable cells    : {vals.Count(v => v >= 0.999)}/{vals.Count}");
                sb.AppendLine();
            }

            if (r.SkippedNGreaterThanN > 0)
                sb.AppendLine($"NOTE: {r.SkippedNGreaterThanN} prediction(s) skipped (detected genes > reference universe).");
            foreach (var warn in r.Warnings) sb.AppendLine($"WARNING: {warn}");

            return sb.ToString();
        }

        /// <summary>
        /// Diagnostic: how much does each of the four metrics ACTUALLY contribute to the decision?
        ///
        /// The engine converts each raw metric to a softmax probability across classes and then averages the four
        /// equally. A channel whose softmax is (near-)uniform contributes a constant ~1/N to every class — it cannot
        /// discriminate at all, yet it still absorbs 25% of the composite and dilutes the channels that do work.
        /// This replicates the engine's exact temperatures (CellTypePredictor.cs:205-217) over the raw metrics the
        /// engine already exposes on CellTypeScore — no engine change, no re-scoring.
        /// </summary>
        public sealed class ChannelStat
        {
            public string Name { get; set; } = string.Empty;
            public double MeanSpread { get; set; }
            public double MaxSpread { get; set; }
            public int FlatCells { get; set; }
            public int TotalCells { get; set; }
            public double StandaloneAccuracy { get; set; }
            /// <summary>Uniform in essentially every cell → contributes a constant and cannot move the argmax.</summary>
            public bool IsInert => MeanSpread < 0.01;
        }

        /// <summary>
        /// Structured per-channel contribution — the data behind the "feature strength" figure. A channel whose
        /// softmax is uniform emits ~1/N to every class: it cannot discriminate, yet still takes a full share of
        /// the composite (under an arithmetic mean it actively dilutes the channels that do work).
        /// </summary>
        public static List<ChannelStat> ChannelStats(Report report)
        {
            var result = new List<ChannelStat>();
            var single = report.PerCell.Where(p => p.Repeat == 0 && p.RawMetrics.Count > 0).ToList();
            if (single.Count == 0) return result;

            var spread = new List<double>[4];
            var correct = new int[4];
            for (int i = 0; i < 4; i++) spread[i] = new List<double>();

            foreach (var p in single)
            {
                var classes = p.RawMetrics.Keys.ToArray();
                var probs = ChannelProbabilities(p, classes, report.IsQuantitativeScorer);
                for (int m = 0; m < 4; m++)
                {
                    spread[m].Add(probs[m].Max() - probs[m].Min());
                    int argmax = 0;
                    for (int i = 1; i < probs[m].Length; i++) if (probs[m][i] > probs[m][argmax]) argmax = i;
                    if (string.Equals(classes[argmax], p.TrueLabel, StringComparison.OrdinalIgnoreCase)) correct[m]++;
                }
            }

            for (int m = 0; m < 4; m++)
                result.Add(new ChannelStat
                {
                    Name = report.ChannelNames[m],
                    MeanSpread = spread[m].Average(),
                    MaxSpread = spread[m].Max(),
                    FlatCells = spread[m].Count(x => x < 0.01),
                    TotalCells = spread[m].Count,
                    StandaloneAccuracy = (double)correct[m] / single.Count
                });
            return result;
        }

        public static string AnalyzeChannels(Report report)
        {
            var stats = ChannelStats(report);
            if (stats.Count == 0) return "(no per-cell raw metrics captured)";

            double uniform = 1.0 / report.Labels.Count;
            var sb = new StringBuilder();
            sb.AppendLine($"Per-channel contribution ({stats[0].TotalCells} cells, {report.Labels.Count} classes, uniform = {uniform:F3}):");
            sb.AppendLine($"  {"channel",-14}{"mean spread",13}{"max spread",12}{"flat cells",12}{"standalone acc",16}");
            foreach (var s in stats)
                sb.AppendLine($"  {s.Name,-14}{s.MeanSpread,13:F4}{s.MaxSpread,12:F4}" +
                              $"{$"{s.FlatCells}/{s.TotalCells}",12}{s.StandaloneAccuracy,16:P1}");
            sb.AppendLine();
            sb.AppendLine("  spread = max(prob) - min(prob) across classes. ~0 means the channel is uniform:");
            sb.AppendLine("  it adds a constant to every class and cannot discriminate, while still taking a full share.");
            return sb.ToString();
        }

        /// <summary>
        /// Sweeps every non-empty subset of the four channels over ALL predictions, re-deriving the engine's
        /// composite from the raw metrics it already exposed. Answers "which channels actually carry the decision?"
        /// NOTE: picking the best subset post-hoc on the same cells is a selection effect — treat as DIAGNOSTIC,
        /// not as a validated accuracy for that subset.
        /// </summary>
        public static string AnalyzeChannelCombinations(Report report)
        {
            var preds = report.PerCell.Where(p => p.RawMetrics.Count > 0).ToList();
            if (preds.Count == 0) return "(no raw metrics captured)";

            string[] names = report.ChannelNames;
            var cache = preds.Select(p =>
            {
                var classes = p.RawMetrics.Keys.ToArray();
                return (classes, probs: ChannelProbabilities(p, classes, report.IsQuantitativeScorer), p.TrueLabel);
            }).ToList();

            var rows = new List<(string combo, double acc, int n)>();
            for (int mask = 1; mask < 16; mask++)
            {
                int correct = 0;
                foreach (var (classes, probs, trueLabel) in cache)
                {
                    int argmax = FuseArgmax(probs, mask, classes.Length, report.IsQuantitativeScorer);
                    if (string.Equals(classes[argmax], trueLabel, StringComparison.OrdinalIgnoreCase)) correct++;
                }
                rows.Add((string.Join("+", Enumerable.Range(0, 4).Where(m => (mask & (1 << m)) != 0).Select(m => names[m])),
                          (double)correct / cache.Count, correct));
            }

            var sb = new StringBuilder();
            sb.AppendLine($"Channel-combination sweep over ALL {cache.Count} predictions (DIAGNOSTIC — post-hoc selection):");
            sb.AppendLine($"  {"channels",-46}{"accuracy",10}{"correct",10}");
            foreach (var r in rows.OrderByDescending(r => r.acc))
                sb.AppendLine($"  {r.combo,-46}{r.acc,10:P2}{r.n,10}");
            sb.AppendLine();
            sb.AppendLine(report.IsQuantitativeScorer
                ? $"  (full set = all four channels fused as {report.ScorerName} does — log-pooled — at EQUAL channel"
                  + " weights. The headline accuracy above may use per-fold calibrated weights, so the two need not"
                  + " match exactly.)"
                : $"  (full set = the scorer being evaluated: {report.ScorerName})");
            return sb.ToString();
        }

        public sealed class ComboStat
        {
            public string Channels { get; set; } = string.Empty;
            public int ChannelCount { get; set; }
            public double Accuracy { get; set; }
            public int Correct { get; set; }
            public int Total { get; set; }
            public bool IsFullSet { get; set; }
        }

        /// <summary>Structured channel-combination sweep — the data behind the ablation figure. Diagnostic only:
        /// the best subset is chosen post-hoc, so it is not a validated accuracy for that subset.</summary>
        public static List<ComboStat> ChannelCombinations(Report report)
        {
            var result = new List<ComboStat>();
            var preds = report.PerCell.Where(p => p.RawMetrics.Count > 0).ToList();
            if (preds.Count == 0) return result;

            string[] names = report.ChannelNames;
            var cache = preds.Select(p =>
            {
                var classes = p.RawMetrics.Keys.ToArray();
                return (classes, probs: ChannelProbabilities(p, classes, report.IsQuantitativeScorer), p.TrueLabel);
            }).ToList();

            for (int mask = 1; mask < 16; mask++)
            {
                int correct = 0;
                foreach (var (classes, probs, trueLabel) in cache)
                {
                    int argmax = FuseArgmax(probs, mask, classes.Length, report.IsQuantitativeScorer);
                    if (string.Equals(classes[argmax], trueLabel, StringComparison.OrdinalIgnoreCase)) correct++;
                }
                int bits = Enumerable.Range(0, 4).Count(m => (mask & (1 << m)) != 0);
                result.Add(new ComboStat
                {
                    Channels = string.Join(" + ", Enumerable.Range(0, 4).Where(m => (mask & (1 << m)) != 0).Select(m => names[m])),
                    ChannelCount = bits,
                    Accuracy = (double)correct / cache.Count,
                    Correct = correct,
                    Total = cache.Count,
                    IsFullSet = mask == 15
                });
            }
            return result;
        }

        // ---- Nested cross-validation ------------------------------------------------------------------------------
        // Picking the best channel subset after seeing the test set is a selection effect — the classic way to
        // report an optimistic number. Nested CV removes it: the subset is chosen by an INNER CV on the outer
        // training cells only, then evaluated once on outer-test cells that played no part in choosing it.

        private static readonly string[] ChannelNames = { "Spearman", "Specificity", "Enrichment", "Coverage" };

        /// <summary>Re-derives the engine's per-channel softmaxes from raw metrics and scores one channel subset.</summary>
        private static int CountCorrectForMask(List<CellPrediction> preds, int mask)
        {
            int correct = 0;
            foreach (var p in preds)
            {
                var classes = p.RawMetrics.Keys.ToArray();
                if (classes.Length == 0) continue;

                var specVals = classes.Select(c => p.RawMetrics[c][1]).OrderBy(v => v).ToList();
                double medianSpec = specVals[specVals.Count / 2];
                double[] temps = { 0.1, Math.Max(medianSpec * 0.2, 1.0), 2.0, 0.15 };

                var agg = new double[classes.Length];
                for (int m = 0; m < 4; m++)
                {
                    if ((mask & (1 << m)) == 0) continue;
                    Func<string, double> sel = m switch
                    {
                        0 => c => p.RawMetrics[c][0],
                        1 => c => p.RawMetrics[c][1],
                        2 => c => -Math.Log10(Math.Max(p.RawMetrics[c][2], 1e-300)),
                        _ => c => p.RawMetrics[c][3],
                    };
                    var vals = classes.Select(c => sel(c) / temps[m]).ToArray();
                    double max = vals.Max();
                    double sum = vals.Sum(v => Math.Exp(v - max));
                    for (int i = 0; i < classes.Length; i++) agg[i] += Math.Exp(vals[i] - max) / sum;
                }

                int argmax = 0;
                for (int i = 1; i < agg.Length; i++) if (agg[i] > agg[argmax]) argmax = i;
                if (string.Equals(classes[argmax], p.TrueLabel, StringComparison.OrdinalIgnoreCase)) correct++;
            }
            return correct;
        }

        private static string MaskName(int mask) =>
            string.Join("+", Enumerable.Range(0, 4).Where(m => (mask & (1 << m)) != 0).Select(m => ChannelNames[m]));

        /// <summary>
        /// Nested CV: inner folds choose the channel subset, outer folds measure it. The reported accuracy is
        /// therefore an honest estimate of "select channels by CV, then classify" as a whole procedure.
        /// </summary>
        public static string RunNestedCv(ProteomicsData data, Options options, IProgress<string> progress = null)
        {
            var labelMap = BuildLabelMap(data, options.IncludeConditions);
            var byClass = labelMap.GroupBy(kv => kv.Value, StringComparer.OrdinalIgnoreCase)
                                  .ToDictionary(g => g.Key, g => g.Select(kv => kv.Key).ToList(), StringComparer.OrdinalIgnoreCase);

            var sb = new StringBuilder();
            var throwaway = new Report { Labels = new List<string>() };

            int totalCorrect = 0, totalCells = 0;
            int shippedCorrect = 0, spearmanOnlyCorrect = 0;
            var picks = new List<string>();

            int outerRepeats = Math.Max(1, Math.Min(options.Repeats, 5));
            for (int rep = 0; rep < outerRepeats; rep++)
            {
                var outerFolds = StratifiedFolds(byClass, options.Folds, new Random(options.Seed + rep));

                for (int o = 0; o < outerFolds.Count; o++)
                {
                    var outerTest = outerFolds[o];
                    if (outerTest.Count == 0) continue;
                    var outerTrainMap = labelMap.Where(kv => !outerTest.Contains(kv.Key))
                                                .ToDictionary(kv => kv.Key, kv => kv.Value);

                    // ---- INNER: choose the channel subset using ONLY outer-training cells ----
                    var innerByClass = outerTrainMap.GroupBy(kv => kv.Value, StringComparer.OrdinalIgnoreCase)
                        .ToDictionary(g => g.Key, g => g.Select(kv => kv.Key).ToList(), StringComparer.OrdinalIgnoreCase);
                    var innerFolds = StratifiedFolds(innerByClass, options.Folds, new Random(options.Seed * 17 + o));

                    var innerPreds = new List<CellPrediction>();
                    for (int i = 0; i < innerFolds.Count; i++)
                    {
                        var innerTest = innerFolds[i];
                        if (innerTest.Count == 0) continue;
                        var innerTrainMap = outerTrainMap.Where(kv => !innerTest.Contains(kv.Key))
                                                         .ToDictionary(kv => kv.Key, kv => kv.Value);
                        innerPreds.AddRange(ScoreCells(data, innerTrainMap, innerTest, labelMap, -3, i,
                            throwaway, null, options.MinPercentExpressing));
                    }

                    int bestMask = 15; double bestAcc = -1;
                    for (int mask = 1; mask < 16; mask++)
                    {
                        double acc = innerPreds.Count > 0 ? (double)CountCorrectForMask(innerPreds, mask) / innerPreds.Count : 0;
                        // Tie-break toward FEWER channels: simpler model wins when inner evidence cannot separate.
                        int bits = Enumerable.Range(0, 4).Count(m => (mask & (1 << m)) != 0);
                        int bestBits = Enumerable.Range(0, 4).Count(m => (bestMask & (1 << m)) != 0);
                        if (acc > bestAcc + 1e-12 || (Math.Abs(acc - bestAcc) <= 1e-12 && bits < bestBits))
                        {
                            bestAcc = acc; bestMask = mask;
                        }
                    }
                    picks.Add(MaskName(bestMask));

                    // ---- OUTER: evaluate the chosen subset on cells that never influenced the choice ----
                    var outerPreds = ScoreCells(data, outerTrainMap, outerTest, labelMap, -4, o,
                        throwaway, null, options.MinPercentExpressing);
                    totalCorrect += CountCorrectForMask(outerPreds, bestMask);
                    totalCells += outerPreds.Count;
                    shippedCorrect += CountCorrectForMask(outerPreds, 15);
                    spearmanOnlyCorrect += CountCorrectForMask(outerPreds, 1);
                }
                progress?.Report($"  nested repeat {rep + 1}/{outerRepeats}");
            }

            double nested = totalCells > 0 ? (double)totalCorrect / totalCells : 0;
            var (nlo, nhi) = WilsonInterval(totalCorrect, totalCells);
            var (slo, shi) = WilsonInterval(shippedCorrect, totalCells);
            var (plo, phi) = WilsonInterval(spearmanOnlyCorrect, totalCells);

            sb.AppendLine($"NESTED CV ({outerRepeats} repeats x {options.Folds} outer folds; channel subset chosen by inner CV):");
            sb.AppendLine($"  outer predictions            : {totalCells}");
            sb.AppendLine($"  NESTED accuracy (honest)     : {nested:P2}  ({totalCorrect}/{totalCells})  Wilson 95% CI [{nlo:P1}, {nhi:P1}]");
            sb.AppendLine($"  shipped 4-metric, same folds : {(double)shippedCorrect / Math.Max(1, totalCells):P2}  ({shippedCorrect}/{totalCells})  CI [{slo:P1}, {shi:P1}]");
            sb.AppendLine($"  Spearman-only, same folds    : {(double)spearmanOnlyCorrect / Math.Max(1, totalCells):P2}  ({spearmanOnlyCorrect}/{totalCells})  CI [{plo:P1}, {phi:P1}]");
            sb.AppendLine();
            sb.AppendLine("  Subset chosen by the inner CV, per outer fold:");
            foreach (var g in picks.GroupBy(p => p).OrderByDescending(g => g.Count()))
                sb.AppendLine($"    {g.Key,-46} chosen {g.Count()}/{picks.Count} folds");
            return sb.ToString();
        }

        /// <summary>
        /// Is the marker machinery structurally blind to quantitative differences? Reports, for probe genes, the
        /// per-class median (log2), how many classes "express" it (median>0 — the binary test the IDF uses), its
        /// resulting specificity, and whether it survives into any marker set.
        /// </summary>
        public static string DiagnoseGeneVisibility(ProteomicsData data, Options options, params string[] probes)
        {
            var labelMap = BuildLabelMap(data, options.IncludeConditions);
            var byClass = labelMap.GroupBy(kv => kv.Value, StringComparer.OrdinalIgnoreCase)
                                  .ToDictionary(g => g.Key, g => g.Select(kv => kv.Key).ToList(), StringComparer.OrdinalIgnoreCase);
            var folds = StratifiedFolds(byClass, options.Folds, new Random(options.Seed));
            var trainMap = labelMap.Where(kv => !folds[0].Contains(kv.Key)).ToDictionary(kv => kv.Key, kv => kv.Value);

            var parsed = new ReferenceDataService().BuildProteomicReference(data, trainMap);
            var db = new TranscriptomicDatabase
            {
                CellTypeProfiles = parsed.CellTypeProfiles.ToDictionary(p => p.CellType, p => p, StringComparer.OrdinalIgnoreCase),
                CellTypeMetadata = parsed.CellTypeMetadata.ToDictionary(m => m.CellType, m => m, StringComparer.OrdinalIgnoreCase)
            };
            var predictor = new CellTypePredictor(db, -1, options.MinPercentExpressing);
            var classes = db.CellTypeProfiles.Keys.OrderBy(c => c).ToList();

            var sb = new StringBuilder();
            sb.AppendLine($"Gene visibility to the marker machinery (threshold = log(N/2) = {Math.Log(classes.Count / 2.0):F3}):");
            sb.AppendLine();
            foreach (var gene in probes)
            {
                int expressing = classes.Count(c => db.CellTypeProfiles[c].MedianExpression.TryGetValue(gene, out var m) && m > 0);
                double spec = expressing > 0 ? Math.Log((double)classes.Count / expressing) : 0;
                var isMarkerFor = classes.Where(c => predictor.CellTypeMarkers.TryGetValue(c, out var mk) && mk.Contains(gene)).ToList();

                sb.AppendLine($"  {gene}:");
                foreach (var c in classes)
                {
                    bool has = db.CellTypeProfiles[c].MedianExpression.TryGetValue(gene, out var med);
                    db.CellTypeProfiles[c].PercentExpressing.TryGetValue(gene, out var pct);
                    sb.AppendLine($"      {c,-8} median(log2) = {(has ? med.ToString("F2") : "  --"),8}   detected in {pct,5:F1}% of cells");
                }
                sb.AppendLine($"      -> expressed in {expressing}/{classes.Count} classes  =>  specificity = {spec:F3}" +
                              $"  =>  marker for: {(isMarkerFor.Count == 0 ? "NOTHING (invisible to enrichment + coverage)" : string.Join(",", isMarkerFor))}");

                // The quantitative signal the binary test throws away.
                var meds = classes.Where(c => db.CellTypeProfiles[c].MedianExpression.ContainsKey(gene))
                                  .Select(c => (c, m: db.CellTypeProfiles[c].MedianExpression[gene])).ToList();
                if (meds.Count >= 2)
                {
                    var hi = meds.OrderByDescending(x => x.m).First();
                    var lo = meds.OrderBy(x => x.m).First();
                    sb.AppendLine($"      -> quantitative range: {hi.c} {hi.m:F2} vs {lo.c} {lo.m:F2}  =  {hi.m - lo.m:F2} log2 units" +
                                  $" ({Math.Pow(2, hi.m - lo.m):F1}x) — signal the BINARY specificity test discards");
                }
                sb.AppendLine();
            }
            return sb.ToString();
        }

        /// <summary>
        /// Compares how the four channels are FUSED. The engine takes an arithmetic mean of the four softmaxes.
        /// A mixture (mean) lets a uniform channel dilute the decision toward 1/N. A product of experts
        /// (geometric mean / log-pooling) makes a uniform channel a constant factor that cancels on renormalisation
        /// — i.e. inert channels become genuinely harmless instead of harmful, with no channel dropped.
        /// </summary>
        public static string ComparePoolingRules(Report report)
        {
            var preds = report.PerCell.Where(p => p.RawMetrics.Count > 0).ToList();
            if (preds.Count == 0) return "(no raw metrics)";

            int arithCorrect = 0, geoCorrect = 0;
            foreach (var p in preds)
            {
                var classes = p.RawMetrics.Keys.ToArray();
                var specVals = classes.Select(c => p.RawMetrics[c][1]).OrderBy(v => v).ToList();
                double medianSpec = specVals[specVals.Count / 2];
                double[] temps = { 0.1, Math.Max(medianSpec * 0.2, 1.0), 2.0, 0.15 };

                var arith = new double[classes.Length];
                var logsum = new double[classes.Length];
                for (int m = 0; m < 4; m++)
                {
                    Func<string, double> sel = m switch
                    {
                        0 => c => p.RawMetrics[c][0],
                        1 => c => p.RawMetrics[c][1],
                        2 => c => -Math.Log10(Math.Max(p.RawMetrics[c][2], 1e-300)),
                        _ => c => p.RawMetrics[c][3],
                    };
                    var vals = classes.Select(c => sel(c) / temps[m]).ToArray();
                    double max = vals.Max();
                    double sum = vals.Sum(v => Math.Exp(v - max));
                    for (int i = 0; i < classes.Length; i++)
                    {
                        double prob = Math.Exp(vals[i] - max) / sum;
                        arith[i] += prob / 4.0;
                        logsum[i] += Math.Log(Math.Max(prob, 1e-12)) / 4.0;   // geometric mean in log space
                    }
                }

                int ai = 0, gi = 0;
                for (int i = 1; i < classes.Length; i++)
                {
                    if (arith[i] > arith[ai]) ai = i;
                    if (logsum[i] > logsum[gi]) gi = i;
                }
                if (string.Equals(classes[ai], p.TrueLabel, StringComparison.OrdinalIgnoreCase)) arithCorrect++;
                if (string.Equals(classes[gi], p.TrueLabel, StringComparison.OrdinalIgnoreCase)) geoCorrect++;
            }

            var sb = new StringBuilder();
            sb.AppendLine($"Fusion rule, all four channels kept ({preds.Count} predictions):");
            sb.AppendLine($"  arithmetic mean (SHIPPED, mixture)      : {(double)arithCorrect / preds.Count:P2}  ({arithCorrect}/{preds.Count})");
            sb.AppendLine($"  geometric mean  (log-pool, product)     : {(double)geoCorrect / preds.Count:P2}  ({geoCorrect}/{preds.Count})");
            sb.AppendLine("  (a uniform channel cancels under a product but dilutes under a mean — same channels, different algebra)");
            return sb.ToString();
        }

        /// <summary>
        /// Head-to-head, same leak-free folds: the shipped 4-metric scorer vs the quantitative redesign.
        /// Also reports each redesigned channel's standalone accuracy, so we can see whether the marker-derived
        /// channels finally earn their place instead of injecting dropout noise.
        /// </summary>
        public static string CompareScorers(ProteomicsData data, Options options, IProgress<string> progress = null)
        {
            var labelMap = BuildLabelMap(data, options.IncludeConditions);
            var byClass = labelMap.GroupBy(kv => kv.Value, StringComparer.OrdinalIgnoreCase)
                                  .ToDictionary(g => g.Key, g => g.Select(kv => kv.Key).ToList(), StringComparer.OrdinalIgnoreCase);

            int shipped = 0, quant = 0, spearmanOnly = 0, shippedCalibrated = 0, total = 0;
            var shippedWeights = new double[4];
            var chanCorrect = new int[4];
            var learnedWeights = new double[4];
            int calibrations = 0;
            string[] chanNames = { "Spearman", "SignatureRank", "AUROC", "DetectionLL" };
            var confusion = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            int repeats = Math.Max(1, Math.Min(options.Repeats, 5));
            for (int rep = 0; rep < repeats; rep++)
            {
                var folds = StratifiedFolds(byClass, options.Folds, new Random(options.Seed + rep));
                for (int f = 0; f < folds.Count; f++)
                {
                    var testRuns = folds[f];
                    if (testRuns.Count == 0) continue;
                    var trainMap = labelMap.Where(kv => !testRuns.Contains(kv.Key)).ToDictionary(kv => kv.Key, kv => kv.Value);

                    var parsed = new ReferenceDataService().BuildProteomicReference(data, trainMap);
                    if (parsed?.CellTypeProfiles == null || parsed.CellTypeProfiles.Count < 2) continue;
                    var db = new TranscriptomicDatabase
                    {
                        CellTypeProfiles = parsed.CellTypeProfiles
                            .GroupBy(p => p.CellType, StringComparer.OrdinalIgnoreCase)
                            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase),
                        CellTypeMetadata = (parsed.CellTypeMetadata ?? new List<CellTypeMetadata>())
                            .GroupBy(m => m.CellType, StringComparer.OrdinalIgnoreCase)
                            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase)
                    };

                    var shippedPredictor = new CellTypePredictor(db, -1, options.MinPercentExpressing);
                    var quantScorer = new QuantitativeCellTypeScorer(db);
                    int universe = UniverseFromDb(db).Count;

                    // Learn channel weights by INNER CV inside the training set (no memorisation bias, no test leakage).
                    var wQuant = CalibrateQuantViaInnerCv(data, trainMap, options, f);
                    quantScorer.SetChannelWeights(wQuant);
                    for (int m = 0; m < 4; m++) learnedWeights[m] += wQuant[m];
                    calibrations++;

                    // ABLATION: shipped channels, redesign's fusion rule. Weights from TRAIN cells only.
                    var wShipped = CalibrateShippedChannels(data, trainMap, shippedPredictor, universe, db.CellTypeProfiles.Count);
                    for (int m = 0; m < 4; m++) shippedWeights[m] += wShipped[m];

                    foreach (var run in testRuns)
                    {
                        var ab = CellTypeClassificationManager.ExtractProteinAbundances(data, run);
                        if (ab.Count == 0 || ab.Count > universe) continue;
                        string truth = labelMap[run];
                        total++;

                        var sp = shippedPredictor.PredictCellType(ab);
                        if (string.Equals(sp?.TopCellType, truth, StringComparison.OrdinalIgnoreCase)) shipped++;
                        if (sp?.Scores != null)
                        {
                            var best = sp.Scores.OrderByDescending(kv => kv.Value.SpearmanCorrelation).First().Key;
                            if (string.Equals(best, truth, StringComparison.OrdinalIgnoreCase)) spearmanOnly++;

                            var fused = CalibratedLogPoolPredict(sp, wShipped);
                            if (string.Equals(fused, truth, StringComparison.OrdinalIgnoreCase)) shippedCalibrated++;
                        }

                        var q = quantScorer.Score(ab);
                        if (string.Equals(q.TopClass, truth, StringComparison.OrdinalIgnoreCase)) quant++;
                        else confusion[$"{truth}->{q.TopClass}"] = confusion.GetValueOrDefault($"{truth}->{q.TopClass}") + 1;

                        // Standalone accuracy of each redesigned channel (argmax of the raw channel value).
                        for (int m = 0; m < 4; m++)
                        {
                            var bestC = q.Channels.OrderByDescending(kv => kv.Value[m]).First().Key;
                            if (string.Equals(bestC, truth, StringComparison.OrdinalIgnoreCase)) chanCorrect[m]++;
                        }
                    }
                }
                progress?.Report($"  scorer comparison repeat {rep + 1}/{repeats}");
            }

            var sb = new StringBuilder();
            sb.AppendLine($"SCORER COMPARISON — identical leak-free folds, {total} held-out predictions:");
            var (a1, b1) = WilsonInterval(shipped, total);
            var (a2, b2) = WilsonInterval(quant, total);
            var (a3, b3) = WilsonInterval(spearmanOnly, total);
            sb.AppendLine($"  shipped 4-metric (arith mean of softmaxes) : {(double)shipped / total,7:P2}  ({shipped}/{total})  CI [{a1:P1}, {b1:P1}]");
            sb.AppendLine($"  Spearman channel alone                     : {(double)spearmanOnly / total,7:P2}  ({spearmanOnly}/{total})  CI [{a3:P1}, {b3:P1}]");
            var (a4, b4) = WilsonInterval(shippedCalibrated, total);
            sb.AppendLine($"  ABLATION: shipped channels + calibrated fusion : {(double)shippedCalibrated / total,7:P2}  ({shippedCalibrated}/{total})  CI [{a4:P1}, {b4:P1}]");
            sb.AppendLine($"  QUANTITATIVE redesign (log-pooled)         : {(double)quant / total,7:P2}  ({quant}/{total})  CI [{a2:P1}, {b2:P1}]");
            sb.AppendLine();
            sb.AppendLine("  Weights learned for the SHIPPED channels (Spearman/Specificity/Enrichment/Coverage):");
            sb.AppendLine($"    {string.Join("  ", shippedWeights.Select(x => (calibrations > 0 ? x / calibrations : 0).ToString("F3")))}");
            sb.AppendLine();
            sb.AppendLine("  Redesigned channels — standalone accuracy and the weight learned from TRAIN cells:");
            for (int m = 0; m < 4; m++)
                sb.AppendLine($"    {chanNames[m],-16}{(double)chanCorrect[m] / total,8:P1}   learned weight {(calibrations > 0 ? learnedWeights[m] / calibrations : 0),6:F3}");
            if (confusion.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("  Remaining errors of the redesign:");
                foreach (var kv in confusion.OrderByDescending(k => k.Value))
                    sb.AppendLine($"    {kv.Key,-22} {kv.Value}");
            }
            return sb.ToString();
        }

        // ---- Ablation: is the defect the CHANNELS or the FUSION? --------------------------------------------------
        // The shipped scorer averages four softmaxes with equal weight. This applies the redesign's fusion rule
        // (reliability-calibrated product of experts) to the SHIPPED channels, unchanged. If that alone recovers
        // full accuracy, the channel redesign is unnecessary and the fix is purely the fusion.

        /// <summary>Extracts the shipped scorer's four raw channel values for one class.</summary>
        private static double ShippedChannel(CellTypeScore s, int m) => m switch
        {
            0 => s.SpearmanCorrelation,
            1 => s.SpecificityScore,
            2 => -Math.Log10(Math.Max(s.HypergeometricPValue, 1e-300)),
            _ => s.MarkerCoverage,
        };

        /// <summary>Learns per-channel reliability weights for the SHIPPED channels from TRAINING cells only.</summary>
        private static double[] CalibrateShippedChannels(ProteomicsData data, Dictionary<string, string> trainMap,
            CellTypePredictor predictor, int universe, int classCount)
        {
            var correct = new int[4];
            int n = 0;
            foreach (var kv in trainMap)
            {
                var ab = CellTypeClassificationManager.ExtractProteinAbundances(data, kv.Key);
                if (ab.Count == 0 || ab.Count > universe) continue;
                var pr = predictor.PredictCellType(ab);
                if (pr?.Scores == null || pr.Scores.Count == 0) continue;
                n++;
                for (int m = 0; m < 4; m++)
                {
                    var best = pr.Scores.OrderByDescending(x => ShippedChannel(x.Value, m)).First().Key;
                    if (string.Equals(best, kv.Value, StringComparison.OrdinalIgnoreCase)) correct[m]++;
                }
            }

            var w = new double[4];
            if (n == 0) { for (int m = 0; m < 4; m++) w[m] = 1; return w; }
            double chance = 1.0 / classCount;
            for (int m = 0; m < 4; m++)
                w[m] = Math.Max(0.0, ((double)correct[m] / n - chance) / (1.0 - chance));
            return w;
        }

        /// <summary>Fuses the SHIPPED channels by weighted log-pooling (product of experts) instead of a mean.</summary>
        private static string CalibratedLogPoolPredict(CellTypePredictionResult pr, double[] w)
        {
            var classes = pr.Scores.Keys.ToArray();
            var logAcc = new double[classes.Length];
            int used = 0;

            for (int m = 0; m < 4; m++)
            {
                if (w[m] <= 1e-9) continue;
                var vals = classes.Select(c => ShippedChannel(pr.Scores[c], m)).ToArray();
                double mean = vals.Average();
                double sd = Math.Sqrt(vals.Sum(v => (v - mean) * (v - mean)) / Math.Max(1, vals.Length - 1));
                if (sd < 1e-12 || double.IsNaN(sd)) continue;   // inert here — contributes nothing rather than diluting

                var z = vals.Select(v => (v - mean) / sd).ToArray();
                double mx = z.Max();
                double sum = z.Sum(v => Math.Exp(v - mx));
                for (int i = 0; i < classes.Length; i++)
                    logAcc[i] += w[m] * Math.Log(Math.Max(Math.Exp(z[i] - mx) / sum, 1e-12));
                used++;
            }
            if (used == 0) return classes.FirstOrDefault();

            int argmax = 0;
            for (int i = 1; i < classes.Length; i++) if (logAcc[i] > logAcc[argmax]) argmax = i;
            return classes[argmax];
        }

        /// <summary>
        /// Learns the redesign's channel weights by an INNER CV inside the training set.
        ///
        /// Calibrating on the training cells directly is optimistically biased: those cells BUILT the reference, so
        /// a channel that memorises them looks perfect (measured: the shipped Specificity channel scores 100% on
        /// train vs 93.3% on held-out cells, earning an undeserved full-strength veto). An inner CV scores each
        /// training cell against a reference built WITHOUT it, so the weights reflect generalisation.
        /// Leak-free: the outer test fold plays no part.
        /// </summary>
        private static double[] CalibrateQuantViaInnerCv(ProteomicsData data, Dictionary<string, string> trainMap,
            Options options, int seedSalt)
        {
            var innerByClass = trainMap.GroupBy(kv => kv.Value, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.Select(kv => kv.Key).ToList(), StringComparer.OrdinalIgnoreCase);
            var innerFolds = StratifiedFolds(innerByClass, options.Folds, new Random(options.Seed * 101 + seedSalt));

            var correct = new int[4];
            int n = 0, classCount = innerByClass.Count;

            foreach (var innerTest in innerFolds)
            {
                if (innerTest.Count == 0) continue;
                var innerTrain = trainMap.Where(kv => !innerTest.Contains(kv.Key))
                                         .ToDictionary(kv => kv.Key, kv => kv.Value);

                var parsed = new ReferenceDataService().BuildProteomicReference(data, innerTrain);
                if (parsed?.CellTypeProfiles == null || parsed.CellTypeProfiles.Count < 2) continue;
                var db = new TranscriptomicDatabase
                {
                    CellTypeProfiles = parsed.CellTypeProfiles.GroupBy(p => p.CellType, StringComparer.OrdinalIgnoreCase)
                        .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase),
                    CellTypeMetadata = (parsed.CellTypeMetadata ?? new List<CellTypeMetadata>())
                        .GroupBy(m => m.CellType, StringComparer.OrdinalIgnoreCase)
                        .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase)
                };
                var scorer = new QuantitativeCellTypeScorer(db);   // equal voice while measuring

                foreach (var run in innerTest)
                {
                    var ab = CellTypeClassificationManager.ExtractProteinAbundances(data, run);
                    if (ab.Count == 0) continue;
                    var r = scorer.Score(ab);
                    if (r.Channels.Count == 0) continue;
                    n++;
                    for (int m = 0; m < 4; m++)
                    {
                        var best = r.Channels.OrderByDescending(kv => kv.Value[m]).First().Key;
                        if (string.Equals(best, trainMap[run], StringComparison.OrdinalIgnoreCase)) correct[m]++;
                    }
                }
            }

            var w = new double[4];
            if (n == 0) { for (int m = 0; m < 4; m++) w[m] = 1; return w; }
            double chance = 1.0 / Math.Max(2, classCount);
            for (int m = 0; m < 4; m++)
                w[m] = Math.Max(0.0, ((double)correct[m] / n - chance) / (1.0 - chance));
            return w;
        }

        /// <summary>
        /// Per-channel probability vectors for one prediction, matching how the SELECTED scorer actually fuses.
        /// The shipped engine softmaxes each raw metric with fixed temperatures (CellTypePredictor.cs:205-217) and
        /// uses -log10(p) for enrichment; the redesign standardises each channel by its SPREAD and softmaxes at T=1.
        /// Diagnosing one with the other's algebra would misreport every channel.
        /// </summary>
        /// <summary>
        /// Fuses the selected channels the way the SCORER BEING EVALUATED fuses them, and returns the winning class
        /// index. The shipped engine averages the per-channel softmaxes (an arithmetic mean, so a uniform channel
        /// dilutes the decision toward 1/N); the quantitative engine LOG-POOLS them (a product of experts, so a
        /// uniform channel contributes a constant that cancels on renormalisation).
        ///
        /// Aggregating both with an arithmetic sum made the "full set" bar of the ablation figure a different
        /// classifier from the one the report is branded with - and the caption invited the reader to conclude a
        /// channel was "diluting rather than helping" from that mismatch.
        ///
        /// Note on weights: the quantitative scorer can additionally weight each channel by a per-fold calibration.
        /// Those weights are not carried on the report, so this reproduces the EQUAL-WEIGHT fusion. That is stated
        /// in the caption rather than papered over.
        /// </summary>
        private static int FuseArgmax(double[][] probs, int mask, int classCount, bool quant)
        {
            var agg = new double[classCount];
            for (int m = 0; m < 4; m++)
            {
                if ((mask & (1 << m)) == 0) continue;
                for (int i = 0; i < classCount; i++)
                    agg[i] += quant ? Math.Log(Math.Max(probs[m][i], 1e-12)) : probs[m][i];
            }
            int argmax = 0;
            for (int i = 1; i < agg.Length; i++) if (agg[i] > agg[argmax]) argmax = i;
            return argmax;
        }

        private static double[][] ChannelProbabilities(CellPrediction p, string[] classes, bool quant)
        {
            var probs = new double[4][];
            double[] temps;
            if (quant)
            {
                temps = new double[] { 1, 1, 1, 1 };
            }
            else
            {
                var specVals = classes.Select(c => p.RawMetrics[c][1]).OrderBy(v => v).ToList();
                double medianSpec = specVals[specVals.Count / 2];
                temps = new double[] { 0.1, Math.Max(medianSpec * 0.2, 1.0), 2.0, 0.15 };
            }

            for (int m = 0; m < 4; m++)
            {
                double[] raw;
                if (quant)
                    raw = classes.Select(c => p.RawMetrics[c][m]).ToArray();
                else
                    raw = classes.Select(c => m == 2 ? -Math.Log10(Math.Max(p.RawMetrics[c][2], 1e-300)) : p.RawMetrics[c][m]).ToArray();

                double[] vals;
                if (quant)
                {
                    double mean = raw.Average();
                    double sd = Math.Sqrt(raw.Sum(v => (v - mean) * (v - mean)) / Math.Max(1, raw.Length - 1));
                    vals = sd < 1e-12 ? raw.Select(_ => 0.0).ToArray() : raw.Select(v => (v - mean) / sd).ToArray();
                }
                else
                {
                    vals = raw.Select(v => v / temps[m]).ToArray();
                }

                double mx = vals.Max();
                double sum = vals.Sum(v => Math.Exp(v - mx));
                probs[m] = vals.Select(v => Math.Exp(v - mx) / sum).ToArray();
            }
            return probs;
        }

        private static string Sanitize(string s) => (s ?? "").Replace(",", "_").Replace("\"", "");
        private static string Csv(string s)
        {
            s ??= "";
            return s.Contains(",") || s.Contains("\"") ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
        }
    }
}
