using System;
using System.Collections.Generic;
using System.Linq;
using MathNet.Numerics.Statistics;

namespace SCPBrowser.Services
{
    /// <summary>
    /// An alternative, quantitative cell-type scorer built to fix a structural failure in the marker-based channels.
    ///
    /// THE PROBLEM (measured on ground truth, not assumed):
    /// The shipped scorer defines gene specificity as IDF over a BINARY presence test —
    /// specificity(g) = log(N_classes / N_classes_where_median>0). That was inherited from single-cell
    /// transcriptomics, where dropout makes presence/absence informative. In deep single-cell proteomics the
    /// well-measured proteins are detected in ~100% of cells of EVERY class, so specificity collapses to 0 for
    /// everything real and the only survivors are dropout artifacts. Consequences we measured:
    ///   - VIM (validated by the source paper as the 67R-vs-H1975 discriminator) sits at 21.06 vs 16.44 log2 —
    ///     a 24.6x difference at 100% detection in all classes — yet scores specificity 0.000 and is a marker for
    ///     NOTHING, i.e. invisible to the enrichment and coverage channels.
    ///   - Marker sets therefore fill with rarely-detected genes, so a held-out cell hits FEWER of its own class's
    ///     markers than chance predicts (k=5 vs expected 16), and the right-tailed hypergeometric returns p=1.000
    ///     for every class — a perfectly inert channel.
    ///
    /// THE FIX — every channel here is quantitative and rank-based:
    ///   1. Continuous specificity via the tau index on LINEAR abundances (tau=0 uniform, tau=1 class-exclusive),
    ///      replacing binary IDF. VIM scores high instead of zero.
    ///   2. Signature weights w(g,c) = tau(g) * (x_c(g)/x_max(g)) — specific AND high in this class.
    ///   3. Channel: weighted signature rank — are class c's signature genes highly ranked IN THIS CELL?
    ///   4. Channel: AUROC (Mann-Whitney) — do class c's signature genes separate from background in the cell's
    ///      ranking? The proper rank-based analogue of the enrichment test.
    ///   5. Channel: detection log-likelihood — a naive-Bayes term over PercentExpressing, which the shipped
    ///      scorer stores but never uses quantitatively. Makes ABSENCE informative, which matters in SCP.
    ///   6. Fusion by LOG-POOLING (product of experts) rather than an arithmetic mean, so an uninformative channel
    ///      contributes a constant that cancels on renormalisation instead of diluting the decision toward 1/N.
    ///   7. Each channel is standardised by its SPREAD across classes before softmax — the shipped code auto-scales
    ///      the specificity temperature to the LEVEL of the scores (max(medianSpec*0.2, 1.0)), which flattens a
    ///      channel whose values are large but tightly clustered.
    /// </summary>
    public sealed class QuantitativeCellTypeScorer
    {
        private readonly List<string> _classes;
        private readonly Dictionary<string, double> _tau = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, Dictionary<string, double>> _weight = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, HashSet<string>> _signature = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, Dictionary<string, double>> _detect = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, Dictionary<string, double>> _median = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<string> _detectionInformative = new();

        private readonly double[] _channelWeight = { 1, 1, 1, 1 };

        public IReadOnlyDictionary<string, double> Tau => _tau;
        public IReadOnlyDictionary<string, HashSet<string>> Signatures => _signature;
        public IReadOnlyList<double> ChannelWeights => _channelWeight;

        /// <summary>True when the source profiles were already log2 (proteomic builders); false = raw linear (TSV parser).</summary>
        public bool SourceIsLog2 { get; }
        /// <summary>True when percent_expressing was stored 0..100 (proteomic) rather than 0..1 (TSV parser).</summary>
        public bool PercentIsHundredScale { get; }

        /// <summary>Overrides the calibrated channel weights (used by the inner-CV calibration in the harness).</summary>
        public void SetChannelWeights(IReadOnlyList<double> w)
        {
            for (int i = 0; i < 4 && i < w.Count; i++) _channelWeight[i] = Math.Max(0, w[i]);
        }

        /// <summary>
        /// Learns each channel's voice from TRAINING cells only (leak-free: never call with test cells).
        /// weight = max(0, acc - chance) / (1 - chance) — a channel at chance is silenced, a perfect channel gets 1.
        /// This is what stops a weak channel from vetoing a strong one under the product fusion.
        /// </summary>
        public void Calibrate(IEnumerable<(Dictionary<string, double> abundances, string trueClass)> trainCells)
        {
            var cells = trainCells?.ToList();
            if (cells == null || cells.Count == 0) return;

            for (int i = 0; i < 4; i++) _channelWeight[i] = 1;   // score with equal voice while measuring

            var correct = new int[4];
            int n = 0;
            foreach (var (ab, truth) in cells)
            {
                if (ab == null || ab.Count == 0) continue;
                n++;
                var r = Score(ab);
                for (int m = 0; m < 4; m++)
                {
                    var best = r.Channels.OrderByDescending(kv => kv.Value[m]).First().Key;
                    if (string.Equals(best, truth, StringComparison.OrdinalIgnoreCase)) correct[m]++;
                }
            }
            if (n == 0) return;

            double chance = 1.0 / _classes.Count;
            for (int m = 0; m < 4; m++)
            {
                double acc = (double)correct[m] / n;
                _channelWeight[m] = Math.Max(0.0, (acc - chance) / (1.0 - chance));
            }
        }

        /// <param name="signatureSize">Top-M genes per class by signature weight.</param>
        /// <param name="detectionDelta">A gene informs the detection channel only if its detection rate differs by
        /// at least this much across classes (otherwise it is the same evidence for everyone).</param>
        public QuantitativeCellTypeScorer(TranscriptomicDatabase db, int signatureSize = 150, double detectionDelta = 0.30)
        {
            if (db?.CellTypeProfiles == null || db.CellTypeProfiles.Count < 2)
                throw new ArgumentException("Need at least two cell-type profiles.", nameof(db));

            _classes = db.CellTypeProfiles.Keys.OrderBy(c => c, StringComparer.Ordinal).ToList();

            // ---- 0. Normalise the two conventions the producers DISAGREE on -------------------------------------
            // The profile producers are not consistent, and getting this wrong silently destroys every score:
            //   median_expression  — proteomic builders store log2(intensity+1) (max ~21-25);
            //                        TranscriptomicTsvParser stores RAW LINEAR counts/TPM (observed max 6317).
            //                        Applying 2^m to linear data overflows to infinity.
            //   percent_expressing — proteomic builders store 0..100; the TSV parser stores a 0..1 fraction.
            // So detect both scales and convert into canonical internal spaces (log2 + linear + probability).
            var rawMedians = _classes.SelectMany(c => (db.CellTypeProfiles[c].MedianExpression ?? new Dictionary<string, double>()).Values).ToList();
            var rawPcts = _classes.SelectMany(c => (db.CellTypeProfiles[c].PercentExpressing ?? new Dictionary<string, double>()).Values).ToList();

            // log2 abundances essentially never exceed ~40; anything larger must be a linear scale.
            SourceIsLog2 = rawMedians.Count == 0 || rawMedians.Max() <= 40.0;
            // A 0..1 fraction cannot exceed 1; anything above must be a 0..100 percentage.
            PercentIsHundredScale = rawPcts.Count > 0 && rawPcts.Max() > 1.5;

            var lin = new Dictionary<string, Dictionary<string, double>>(StringComparer.OrdinalIgnoreCase);
            foreach (var c in _classes)
            {
                var med = db.CellTypeProfiles[c].MedianExpression ?? new Dictionary<string, double>();
                var pct = db.CellTypeProfiles[c].PercentExpressing ?? new Dictionary<string, double>();

                // canonical log2 space (used for the margin) and linear space (used for tau, a ratio statistic)
                _median[c] = med.ToDictionary(kv => kv.Key,
                    kv => SourceIsLog2 ? kv.Value : Math.Log(Math.Max(0, kv.Value) + 1, 2),
                    StringComparer.OrdinalIgnoreCase);
                lin[c] = med.ToDictionary(kv => kv.Key,
                    kv => SourceIsLog2 ? Math.Max(0, Math.Pow(2, kv.Value) - 1) : Math.Max(0, kv.Value),
                    StringComparer.OrdinalIgnoreCase);

                double denom = PercentIsHundredScale ? 100.0 : 1.0;
                _detect[c] = pct.ToDictionary(kv => kv.Key, kv => Math.Min(1.0, Math.Max(0.0, kv.Value / denom)),
                                              StringComparer.OrdinalIgnoreCase);
            }

            var allGenes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var c in _classes) foreach (var g in _median[c].Keys) allGenes.Add(g);

            // ---- 1. Continuous specificity (tau) on LINEAR abundance --------------------------------------------
            // tau is a ratio statistic, so it must be computed on linear values, never on log2 ones.
            foreach (var g in allGenes)
            {
                var v = _classes.Select(c => lin[c].TryGetValue(g, out var m) ? m : 0.0).ToArray();
                double max = v.Max();
                if (max <= 0) { _tau[g] = 0; continue; }
                double sum = v.Sum(x => 1.0 - x / max);
                _tau[g] = sum / (_classes.Count - 1);   // 0 = uniform, 1 = exclusive to one class
            }

            // ---- 2. Signature weights: MARGIN vs the closest rival, x how reliably this class detects it --------
            // tau is a ONE-VS-ALL statistic, and one-vs-all cannot separate an isogenic pair: R67 and H1975 share
            // near-identical profiles, so tau-based signatures are near-identical too (measured: 49/49 errors were
            // R67->H1975). What discriminates a pair is the margin against the CLOSEST rival, not against the mean.
            // VIM is 24.6x above H1975 but only ~2x above Hela — a one-vs-all weight buries exactly the signal we need.
            //   margin(g,c) = median_c - max_{c' != c} median_c'      (log2 units; >0 only if c is the top expresser)
            //   w(g,c)      = margin(g,c) * detectionRate_c(g)        (dropout artifacts have near-zero detect rate,
            //                                                          so they can no longer masquerade as markers)
            foreach (var c in _classes)
            {
                var w = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
                foreach (var g in allGenes)
                {
                    double mine = _median[c].TryGetValue(g, out var mm) ? mm : 0.0;
                    if (mine <= 0) continue;

                    double bestOther = 0;
                    foreach (var cc in _classes)
                    {
                        if (string.Equals(cc, c, StringComparison.OrdinalIgnoreCase)) continue;
                        if (_median[cc].TryGetValue(g, out var m2) && m2 > bestOther) bestOther = m2;
                    }

                    double margin = mine - bestOther;                       // log2 fold-change vs the closest rival
                    if (margin <= 0) continue;

                    double reliability = _detect[c].TryGetValue(g, out var pr) ? pr : 0.0;
                    double weight = margin * reliability;
                    if (weight > 0) w[g] = weight;
                }
                _weight[c] = w;
                _signature[c] = w.OrderByDescending(kv => kv.Value).Take(signatureSize)
                                 .Select(kv => kv.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
            }

            // ---- 5. Genes whose DETECTABILITY differs by class --------------------------------------------------
            foreach (var g in allGenes)
            {
                double mx = _classes.Max(c => _detect[c].TryGetValue(g, out var p) ? p : 0.0);
                double mn = _classes.Min(c => _detect[c].TryGetValue(g, out var p) ? p : 0.0);
                if (mx - mn >= detectionDelta) _detectionInformative.Add(g);
            }
        }

        public sealed class Result
        {
            public string TopClass { get; set; }
            public double Confidence { get; set; }
            public Dictionary<string, double> Probabilities { get; set; } = new(StringComparer.OrdinalIgnoreCase);
            /// <summary>class -> [spearman, signatureRank, auroc, detectionLL] raw channel values.</summary>
            public Dictionary<string, double[]> Channels { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        }

        public Result Score(Dictionary<string, double> abundances) => Score(abundances, null, null, null);

        /// <summary>
        /// Scores a cell, optionally applying the user's key markers, prior weights and cell-type exclusions.
        /// The key-marker/prior handling reproduces CellTypePredictor's exactly (present&amp;high +0.3, present&amp;low
        /// +0.1, absent -0.1; prior = ln(normalised/uniform); both applied as a multiplicative exp() on the final
        /// probability, then renormalised) so the manuscript-advertised feature behaves identically under either
        /// classifier. Excluded types are dropped from the candidate set before scoring, as the shipped path does.
        /// </summary>
        public Result Score(
            Dictionary<string, double> abundances,
            Dictionary<string, HashSet<string>> userKeyMarkers,
            HashSet<string> excludedCellTypes,
            Dictionary<string, double> priorWeights)
        {
            var result = new Result();
            if (abundances == null || abundances.Count == 0) return result;

            // Candidate classes = all reference classes minus the user's exclusions. Signatures/specificity stay
            // defined over ALL classes (the biology doesn't change) — only the candidate set is restricted.
            var classes = (excludedCellTypes == null || excludedCellTypes.Count == 0)
                ? _classes
                : _classes.Where(c => !excludedCellTypes.Contains(c)).ToList();
            if (classes.Count == 0) return result;

            // Percentile rank of each detected gene within this cell (1 = most abundant). Rank-based => immune to
            // per-cell depth/intensity scaling, which the shipped specificity score is not.
            var ordered = abundances.OrderBy(kv => kv.Value).Select(kv => kv.Key).ToList();
            var rank = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < ordered.Count; i++) rank[ordered[i]] = (i + 1.0) / ordered.Count;

            foreach (var c in classes)
            {
                double spearman = Spearman(abundances, _median[c]);
                double sigRank = SignatureRank(rank, c);
                double auroc = Auroc(rank, c);
                double detLL = DetectionLogLik(abundances, c);
                result.Channels[c] = new[] { spearman, sigRank, auroc, detLL };
            }

            // ---- 6/7. Standardise each channel by SPREAD, softmax, then WEIGHTED LOG-POOL ------------------------
            // Equal-weight log-pooling lets a weak channel veto a strong one (measured: AUROC at 59% dragged a 100%
            // Spearman down to 89%). Products are unforgiving, so each channel's voice is scaled by its reliability
            // weight — an uninformative channel is silenced rather than allowed to out-vote an accurate one.
            var probs = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            var logAcc = classes.ToDictionary(c => c, _ => 0.0, StringComparer.OrdinalIgnoreCase);
            int channelsUsed = 0;

            for (int m = 0; m < 4; m++)
            {
                if (_channelWeight[m] <= 1e-9) continue;         // calibrated out

                var vals = classes.Select(c => result.Channels[c][m]).ToArray();
                double mean = vals.Average();
                double sd = Math.Sqrt(vals.Sum(v => (v - mean) * (v - mean)) / Math.Max(1, vals.Length - 1));
                if (sd < 1e-12 || double.IsNaN(sd)) continue;    // genuinely uninformative here: skip, don't dilute

                var z = vals.Select(v => (v - mean) / sd).ToArray();
                double mx = z.Max();
                double sum = z.Sum(v => Math.Exp(v - mx));
                for (int i = 0; i < classes.Count; i++)
                {
                    double p = Math.Exp(z[i] - mx) / sum;
                    logAcc[classes[i]] += _channelWeight[m] * Math.Log(Math.Max(p, 1e-12));
                }
                channelsUsed++;
            }

            if (channelsUsed == 0)
            {
                foreach (var c in classes) probs[c] = 1.0 / classes.Count;
            }
            else
            {
                double mx = logAcc.Values.Max();
                double denom = logAcc.Values.Sum(v => Math.Exp(v - mx));
                foreach (var c in classes) probs[c] = Math.Exp(logAcc[c] - mx) / denom;
            }

            // ---- Key markers + priors — identical math to CellTypePredictor, applied multiplicatively -------------
            bool hasMarkers = userKeyMarkers != null && userKeyMarkers.Count > 0;
            bool hasPriors = priorWeights != null && priorWeights.Count > 0;
            if (hasMarkers || hasPriors)
            {
                double medianAbundance = abundances.Values.OrderBy(x => x).ElementAt(abundances.Count / 2);

                var priorAdj = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
                if (hasPriors)
                {
                    double total = 0;
                    var clamped = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
                    foreach (var c in classes)
                    {
                        double wv = priorWeights.TryGetValue(c, out var val) ? val : 1.0;
                        clamped[c] = Math.Max(wv, 0.01);
                        total += clamped[c];
                    }
                    double uniform = 1.0 / classes.Count;
                    foreach (var c in classes) priorAdj[c] = Math.Log(clamped[c] / total / uniform);
                }

                double sumAdjusted = 0;
                foreach (var c in classes)
                {
                    double adj = priorAdj.TryGetValue(c, out var pa) ? pa : 0.0;
                    if (hasMarkers && userKeyMarkers.TryGetValue(c, out var markers) && markers.Count > 0)
                        foreach (var marker in markers)
                            adj += abundances.TryGetValue(marker, out var ab)
                                ? (ab > medianAbundance ? 0.3 : 0.1)   // present: high vs low
                                : -0.1;                                // absent
                    probs[c] *= Math.Exp(adj);
                    sumAdjusted += probs[c];
                }
                if (sumAdjusted > 1e-15)
                    foreach (var c in classes) probs[c] /= sumAdjusted;
            }

            result.Probabilities = probs;
            var top = probs.OrderByDescending(kv => kv.Value).First();
            result.TopClass = top.Key;
            result.Confidence = top.Value;
            return result;
        }

        // ---- Channel implementations ---------------------------------------------------------------------------

        private static double Spearman(Dictionary<string, double> abundances, Dictionary<string, double> profile)
        {
            var common = abundances.Keys.Where(g => profile.TryGetValue(g, out var m) && m > 0).ToList();
            if (common.Count < 3) return 0;
            try { return Correlation.Spearman(common.Select(g => abundances[g]), common.Select(g => profile[g])); }
            catch { return 0; }
        }

        /// <summary>
        /// Weighted mean rank of class c's signature IN THIS CELL. Denominator uses the FULL signature weight, so a
        /// missing signature gene costs the class — absence is evidence, unlike the shipped detected-only scoring.
        /// </summary>
        private double SignatureRank(Dictionary<string, double> rank, string c)
        {
            double num = 0, den = 0;
            foreach (var g in _signature[c])
            {
                double w = _weight[c][g];
                den += w;
                if (rank.TryGetValue(g, out var r)) num += w * r;
            }
            return den > 0 ? num / den : 0;
        }

        /// <summary>
        /// Mann-Whitney AUROC: how well class c's signature genes separate from the rest of the cell's ranking.
        /// The rank-based analogue of enrichment — and unlike the hypergeometric it is quantitative, so it cannot
        /// be defeated by every gene being detected.
        /// </summary>
        private double Auroc(Dictionary<string, double> rank, string c)
        {
            var sig = new List<double>();
            var bg = new List<double>();
            foreach (var kv in rank)
            {
                if (_signature[c].Contains(kv.Key)) sig.Add(kv.Value); else bg.Add(kv.Value);
            }
            if (sig.Count == 0 || bg.Count == 0) return 0.5;

            // U = sum of signature ranks - n1(n1+1)/2, over the pooled ranking.
            var pooled = sig.Select(v => (v, isSig: true)).Concat(bg.Select(v => (v, isSig: false)))
                            .OrderBy(x => x.v).ToList();
            double sumRanks = 0;
            for (int i = 0; i < pooled.Count; i++) if (pooled[i].isSig) sumRanks += i + 1;
            double n1 = sig.Count, n2 = bg.Count;
            double u = sumRanks - n1 * (n1 + 1) / 2.0;
            return u / (n1 * n2);
        }

        /// <summary>
        /// Naive-Bayes detection likelihood over genes whose detectability differs by class. Uses PercentExpressing
        /// as an actual probability — the field the shipped scorer computes, stores, and then never really uses.
        /// </summary>
        private double DetectionLogLik(Dictionary<string, double> abundances, string c)
        {
            double ll = 0;
            var det = _detect[c];
            foreach (var g in _detectionInformative)
            {
                double p = det.TryGetValue(g, out var pp) ? pp : 0.0;
                p = Math.Min(0.99, Math.Max(0.01, p));            // clamp so one gene cannot dominate
                ll += abundances.ContainsKey(g) ? Math.Log(p) : Math.Log(1 - p);
            }
            return ll;
        }
    }
}
