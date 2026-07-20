using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace SCPBrowser.Services
{
    /// <summary>
    /// Builds the "Classification confidence" figure (Mode B, full-reference display): every cell is scored against
    /// per-class reference profiles built from ALL cells of each class (including itself), using the app's SELECTED
    /// classifier. This visualises the confidence STRUCTURE of the loaded dataset — a strong diagonal block per class,
    /// with secondary shading where classes are proteomically similar (e.g. an isogenic pair).
    ///
    /// HONESTY: this is a DISPLAY of the classification on its own reference, NOT a held-out performance estimate.
    /// Because each cell contributes to its own class's reference, confidence reads cleaner than the cross-validated
    /// number. The scores are relative, softmax-normalised assignment scores, not calibrated probabilities. Held-out
    /// accuracy comes from the separate cross-validated benchmark, never from this figure.
    /// </summary>
    public static class ConfidenceMapBuilder
    {
        public sealed class CellRow
        {
            public string Run { get; set; }
            public string TrueLabel { get; set; }
            public string Predicted { get; set; }          // Mode B (full-reference) call
            public double[] Scores { get; set; }           // Mode B assignment scores, aligned with Classes
            public double Margin { get; set; }             // Mode B: top minus runner-up

            /// <summary>Held-out (cross-validated) call, computed against a reference built WITHOUT this cell's fold.
            /// This is what the correct/wrong colour reflects — Mode B alone classifies almost every cell correctly.</summary>
            public string HeldOutPredicted { get; set; }
            public bool HeldOutCorrect { get; set; } = true;
            /// <summary>False if the held-out pass never actually classified this cell (so it must not be shown as
            /// "correct" — a silent held-out failure would otherwise render an all-blue, zero-error figure).</summary>
            public bool HeldOutEvaluated { get; set; }

            public bool Correct => string.Equals(Predicted, TrueLabel, StringComparison.OrdinalIgnoreCase);
        }

        public sealed class Result
        {
            public List<string> Classes { get; set; } = new();
            public List<CellRow> Rows { get; set; } = new();   // grouped by TrueLabel, then run
            public string Method { get; set; }
            public string ScorerLabel { get; set; }
        }

        public static Result Compute(ProteomicsData data, string method,
            HashSet<string> includeConditions = null,
            Dictionary<string, HashSet<string>> keyMarkers = null,
            HashSet<string> excludedCellTypes = null,
            Dictionary<string, double> priorWeights = null)
        {
            var labelMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in data.BiologicalConditionPerFile)
            {
                if (string.IsNullOrWhiteSpace(kv.Value)) continue;
                if (includeConditions != null && includeConditions.Count > 0 && !includeConditions.Contains(kv.Value)) continue;
                labelMap[kv.Key] = kv.Value.Trim();
            }
            if (labelMap.Count == 0) throw new InvalidOperationException("No labelled cells to display.");

            var parsed = new ReferenceDataService().BuildProteomicReference(data, labelMap);
            if (parsed?.CellTypeProfiles == null || parsed.CellTypeProfiles.Count < 2)
                throw new InvalidOperationException("Need at least two conditions to build a confidence map.");

            var db = new TranscriptomicDatabase
            {
                CellTypeProfiles = parsed.CellTypeProfiles
                    .GroupBy(p => p.CellType, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase),
                CellTypeMetadata = (parsed.CellTypeMetadata ?? new List<CellTypeMetadata>())
                    .GroupBy(m => m.CellType, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase)
            };

            IClassifier clf = string.Equals(method, "Quantitative", StringComparison.OrdinalIgnoreCase)
                ? new QuantitativeClassifier(db) : new StandardClassifier(db);

            var classes = db.CellTypeProfiles.Keys.OrderBy(c => c, StringComparer.Ordinal).ToList();
            int idxOf(string c) => classes.IndexOf(c);

            var rows = new List<CellRow>();
            foreach (var kv in labelMap.OrderBy(k => k.Value, StringComparer.Ordinal).ThenBy(k => k.Key, StringComparer.Ordinal))
            {
                var ab = CellTypeClassificationManager.ExtractProteinAbundances(data, kv.Key);
                if (ab.Count == 0) continue;

                var pred = clf.PredictCellType(ab, keyMarkers, excludedCellTypes, priorWeights);
                var scores = new double[classes.Count];
                foreach (var s in pred.Scores)
                {
                    int i = idxOf(s.Key);
                    if (i >= 0) scores[i] = s.Value.CompositeScore;
                }
                var top2 = scores.OrderByDescending(x => x).Take(2).ToArray();
                rows.Add(new CellRow
                {
                    Run = kv.Key,
                    TrueLabel = kv.Value,
                    Predicted = pred.TopCellType,
                    Scores = scores,
                    Margin = top2.Length >= 2 ? top2[0] - top2[1] : (top2.Length == 1 ? top2[0] : 0)
                });
            }

            // Held-out (cross-validated) correctness: Mode B classifies every cell correctly by construction (each
            // cell helped build its own reference), so the correct/wrong marks would be meaningless. A stratified
            // 5-fold pass classifies each cell against a reference built WITHOUT its fold — the honest error signal.
            try
            {
                ComputeHeldOut(data, labelMap, method, keyMarkers, excludedCellTypes, priorWeights,
                    rows.ToDictionary(r => r.Run, r => r, StringComparer.OrdinalIgnoreCase));
            }
            catch { /* leave HeldOutCorrect at the Mode-B value */ }

            return new Result
            {
                Classes = classes,
                Rows = rows,
                Method = clf.MethodName,
                ScorerLabel = clf.MethodName == "Quantitative"
                    ? "QScore classifier (margin specificity + rank enrichment + detection likelihood)"
                    : "Standard four-metric scorer"
            };
        }

        /// <summary>
        /// Held-out classification filling HeldOutCorrect/HeldOutPredicted per cell. Uses leave-one-out for modest
        /// cell counts (deterministic, matches the cross-validated benchmark) and stratified 5-fold above a size
        /// cutoff so the compute stays bounded on large datasets.
        /// </summary>
        private static void ComputeHeldOut(ProteomicsData data, Dictionary<string, string> labelMap, string method,
            Dictionary<string, HashSet<string>> keyMarkers, HashSet<string> excluded, Dictionary<string, double> priors,
            Dictionary<string, CellRow> rowByRun)
        {
            // Build the fold assignment: LOOCV (each cell its own fold) when small; stratified 5-fold otherwise.
            const int loocvMax = 150, kFolds = 5;
            var byClass = labelMap.GroupBy(kv => kv.Value, StringComparer.OrdinalIgnoreCase)
                                  .ToDictionary(g => g.Key, g => g.Select(kv => kv.Key).ToList(), StringComparer.OrdinalIgnoreCase);

            var foldOf = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            int folds;
            if (labelMap.Count <= loocvMax)
            {
                folds = labelMap.Count;
                int idx = 0;
                foreach (var run in labelMap.Keys.OrderBy(r => r, StringComparer.Ordinal)) foldOf[run] = idx++;
            }
            else
            {
                folds = kFolds;
                var rng = new Random(1);   // app code (not a workflow script): a fixed seed is deterministic
                foreach (var c in byClass.OrderBy(c => c.Key, StringComparer.Ordinal))
                {
                    var shuffled = c.Value.OrderBy(_ => rng.Next()).ToList();
                    for (int i = 0; i < shuffled.Count; i++) foldOf[shuffled[i]] = i % kFolds;
                }
            }

            for (int f = 0; f < folds; f++)
            {
                var testRuns = new HashSet<string>(foldOf.Where(kv => kv.Value == f).Select(kv => kv.Key), StringComparer.OrdinalIgnoreCase);
                if (testRuns.Count == 0) continue;
                var trainMap = labelMap.Where(kv => !testRuns.Contains(kv.Key)).ToDictionary(kv => kv.Key, kv => kv.Value);

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
                IClassifier clf = string.Equals(method, "Quantitative", StringComparison.OrdinalIgnoreCase)
                    ? new QuantitativeClassifier(db) : new StandardClassifier(db);

                foreach (var run in testRuns)
                {
                    if (!rowByRun.TryGetValue(run, out var row)) continue;
                    var ab = CellTypeClassificationManager.ExtractProteinAbundances(data, run);
                    if (ab.Count == 0) continue;
                    var pred = clf.PredictCellType(ab, keyMarkers, excluded, priors);
                    if (pred?.TopCellType == null) continue;
                    row.HeldOutPredicted = pred.TopCellType;
                    row.HeldOutCorrect = string.Equals(pred.TopCellType, row.TrueLabel, StringComparison.OrdinalIgnoreCase);
                    row.HeldOutEvaluated = true;
                }
            }
        }

        // ---- Rendering (self-contained HTML with inline SVG; the heatmap is also extractable as a standalone SVG) --

        private static readonly string[] Seq =
        {
            "#f4f7fb", "#cde2fb", "#b7d3f6", "#9ec5f4", "#86b6ef", "#6da7ec", "#5598e7",
            "#3987e5", "#2a78d6", "#256abf", "#1c5cab", "#184f95", "#104281", "#0d366b"
        };

        public static string BuildHtml(Result r, string datasetName = null)
        {
            string svg = HeatmapSvg(r, out int svgW, out int svgH);
            string margin = MarginSvg(r);
            bool heldOutRan = r.Rows.Any(x => x.HeldOutEvaluated);
            string heldOutNote = heldOutRan
                ? "The held-out marks use the classifier's live (un-calibrated) configuration, so they may differ slightly from the calibrated cross-validated benchmark."
                : "<strong>Held-out validation could not be computed for this dataset</strong>, so no correct/wrong marks are shown — the colours reflect on-reference confidence only.";

            var sb = new StringBuilder();
            sb.Append($@"<!doctype html><html lang=""en""><head><meta charset=""utf-8"">
<meta name=""viewport"" content=""width=device-width, initial-scale=1"">
<title>Classification confidence — {Esc(datasetName ?? "SCPBrowser")}</title>
<style>
 :root {{ color-scheme: light; --surface:#fcfcfb; --plane:#f9f9f7; --ink:#0b0b0b; --ink2:#52514e; --muted:#86857f; --rule:#d9d8d3; }}
 @media (prefers-color-scheme: dark) {{ :root:where(:not([data-theme=light])) {{ color-scheme:dark; --surface:#1a1a19; --plane:#0d0d0d; --ink:#fff; --ink2:#c3c2b7; --muted:#8f8e86; --rule:#3a3a37; }} }}
 :root[data-theme=dark] {{ color-scheme:dark; --surface:#1a1a19; --plane:#0d0d0d; --ink:#fff; --ink2:#c3c2b7; --muted:#8f8e86; --rule:#3a3a37; }}
 body {{ margin:0; background:var(--plane); color:var(--ink); font:14px/1.55 -apple-system,'Segoe UI',Roboto,Arial,sans-serif; }}
 .wrap {{ max-width:1000px; margin:0 auto; padding:28px 22px 60px; }}
 h1 {{ font-size:21px; margin:0 0 4px; letter-spacing:-.01em; }}
 .sub {{ color:var(--ink2); font-size:13px; margin:0 0 20px; }}
 .fig {{ background:var(--surface); border:1px solid var(--rule); border-radius:8px; padding:16px 16px 12px; margin:0 0 18px; }}
 .cap {{ color:var(--ink2); font-size:12.5px; margin:8px 0 0; }}
 .scroll {{ overflow-x:auto; }}
 .note {{ background:var(--surface); border:1px solid var(--rule); border-left:3px solid #eda100; border-radius:6px; padding:12px 14px; color:var(--ink2); font-size:12.5px; }}
 text {{ font-family:inherit; }}
</style></head><body><div class=""wrap"">
<h1>Classification confidence</h1>
<p class=""sub"">{Esc(datasetName ?? "")}{(datasetName == null ? "" : " · ")}{r.Rows.Count} cells · {r.Classes.Count} classes · {Esc(r.ScorerLabel)}</p>

<div class=""note""><strong>What this shows.</strong> Relative assignment scores (softmax-normalised across classes), <em>not</em>
calibrated probabilities. Each cell is scored against per-class references built from the loaded dataset — a display of the
classification on its own reference, <strong>distinct from held-out accuracy</strong> (which comes from the cross-validated benchmark).
Because each cell contributes to its own class's reference, confidence reads cleaner here than in cross-validation.</div>
<div style=""height:14px""></div>

<div class=""fig""><h2 style=""font-size:15px;margin:0 0 2px"">Per-cell confidence across candidate classes</h2>
<p class=""cap"" style=""margin:0 0 10px"">Rows = cells grouped by assigned condition; columns = candidate classes. Colour intensity = assignment score against the full-dataset reference (Mode B display). A red tick on the right of a row flags a cell the classifier <strong>misclassifies out-of-sample</strong> (held-out cross-validation: leave-one-out for ≤150 cells, otherwise 5-fold) — the honest error signal, since Mode B scores almost every cell confidently on its own reference. {heldOutNote}</p>
<div class=""scroll"">{svg}</div></div>

<div class=""fig""><h2 style=""font-size:15px;margin:0 0 2px"">Assignment margin by condition</h2>
<p class=""cap"" style=""margin:0 0 10px"">Top score minus runner-up, per cell (Mode B). Large margins = confident calls. Each point is coloured by its <strong>held-out</strong> (cross-validated) outcome — blue if classified correctly out-of-sample, red if not — so a confident-looking cell that is nonetheless red is one the classifier confuses with a proteomically similar class.</p>
<div class=""scroll"">{margin}</div></div>

</div></body></html>");
            return sb.ToString();
        }

        /// <summary>The heatmap alone, as a standalone SVG document (publication-ready vector export).</summary>
        public static string HeatmapStandaloneSvg(Result r)
        {
            string inner = HeatmapSvg(r, out int w, out int h);

            // The honesty context must travel WITH the vector file (it leaves the HTML behind), so embed a title,
            // the scorer label, and the caveat + a legend as <text>/<rect> above and below the heatmap.
            int extraTop = 46, extraBottom = 30;
            string title =
                $@"<text x=""14"" y=""20"" font-size=""14"" font-weight=""700"" fill=""#0b0b0b"">Classification confidence — {Esc(r.ScorerLabel)}</text>" +
                $@"<text x=""14"" y=""37"" font-size=""11"" fill=""#52514e"">Colour = assignment score on the full-dataset reference (a display, NOT held-out accuracy). Red ticks = cells misclassified out-of-sample (held-out cross-validation).</text>";
            string legend =
                $@"<rect x=""14"" y=""{h + extraTop + 6}"" width=""9"" height=""9"" fill=""#d03b3b""/>" +
                $@"<text x=""28"" y=""{h + extraTop + 14}"" font-size=""11"" fill=""#52514e"">misclassified out-of-sample (held-out cross-validation)</text>";

            // Shift the heatmap group down to make room for the title band.
            inner = inner
                .Replace("var(--ink)", "#0b0b0b").Replace("var(--ink2)", "#52514e")
                .Replace("var(--muted)", "#86857f").Replace("var(--surface)", "#fcfcfb").Replace("var(--rule)", "#d9d8d3");
            // Replace the opening <svg ...> so the viewBox/height include the extra bands, then wrap the body in a <g>.
            inner = System.Text.RegularExpressions.Regex.Replace(inner, @"^<svg viewBox=""0 0 (\d+) (\d+)"" width=""\d+"" height=""\d+""",
                m => $@"<svg xmlns=""http://www.w3.org/2000/svg"" viewBox=""0 0 {m.Groups[1].Value} {int.Parse(m.Groups[2].Value) + extraTop + extraBottom}"" width=""{m.Groups[1].Value}"" height=""{int.Parse(m.Groups[2].Value) + extraTop + extraBottom}""");
            int tagEnd = inner.IndexOf('>');   // end of the opening <svg ...> tag
            inner = inner.Substring(0, tagEnd + 1) + title + $@"<g transform=""translate(0,{extraTop})"">" + inner.Substring(tagEnd + 1);
            inner = inner.Replace("</svg>", "</g>" + legend + "</svg>");

            return "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n" + inner;
        }

        private static string HeatmapSvg(Result r, out int w, out int h)
        {
            int n = r.Classes.Count;
            int cellRows = r.Rows.Count;
            int colW = Math.Max(64, 520 / Math.Max(1, n));
            int rowH = cellRows <= 120 ? 6 : Math.Max(2, 720 / cellRows);
            const int padL = 96, padT = 64, padR = 150;
            int gridH = cellRows * rowH;
            w = padL + n * colW + padR;
            h = padT + gridH + 30;

            var sb = new StringBuilder();
            sb.Append($@"<svg viewBox=""0 0 {w} {h}"" width=""{w}"" height=""{h}"" role=""img"">");

            // Column headers
            sb.Append($@"<text x=""{padL + n * colW / 2}"" y=""20"" text-anchor=""middle"" font-size=""12"" font-weight=""600"" fill=""var(--ink2)"">candidate class</text>");
            for (int j = 0; j < n; j++)
                sb.Append($@"<text x=""{padL + j * colW + colW / 2}"" y=""{padT - 8}"" text-anchor=""middle"" font-size=""11.5"" fill=""var(--ink2)"">{Esc(r.Classes[j])}</text>");

            // Rows, drawn per group so we can label + separate groups.
            int y = padT, gStart = 0;
            string curGroup = null;
            void FlushGroupLabel(int groupStart, int groupEnd, string label)
            {
                int midY = padT + (groupStart + groupEnd) / 2 * rowH;
                sb.Append($@"<text x=""{padL - 10}"" y=""{midY + 4}"" text-anchor=""end"" font-size=""11.5"" font-weight=""600"" fill=""var(--ink)"">{Esc(label)}</text>");
            }

            for (int i = 0; i < cellRows; i++)
            {
                var row = r.Rows[i];
                if (row.TrueLabel != curGroup)
                {
                    if (curGroup != null) FlushGroupLabel(gStart, i, curGroup);
                    // separator line above the new group (except first)
                    if (i > 0) sb.Append($@"<line x1=""{padL}"" y1=""{padT + i * rowH}"" x2=""{padL + n * colW}"" y2=""{padT + i * rowH}"" stroke=""var(--surface)"" stroke-width=""2""/>");
                    curGroup = row.TrueLabel; gStart = i;
                }
                for (int j = 0; j < n; j++)
                {
                    double v = row.Scores[j];
                    // Scores are class probabilities (0..1); clamp defensively so a figure can never index out of range.
                    int ci = (int)Math.Round(Math.Max(0.0, Math.Min(1.0, v)) * (Seq.Length - 1));
                    string fill = Seq[Math.Max(0, Math.Min(Seq.Length - 1, ci))];
                    sb.Append($@"<rect x=""{padL + j * colW}"" y=""{padT + i * rowH}"" width=""{colW}"" height=""{rowH}"" fill=""{fill}""/>");
                }
                // Flag cells misclassified OUT-OF-SAMPLE (held-out) with a red tick — the honest error signal, since
                // Mode B classifies almost every cell correctly by construction. Only cells actually evaluated
                // out-of-sample can be flagged; an un-evaluated cell gets no tick (never shown as "correct").
                if (row.HeldOutEvaluated && !row.HeldOutCorrect)
                    sb.Append($@"<rect x=""{padL + n * colW}"" y=""{padT + i * rowH}"" width=""5"" height=""{Math.Max(2, rowH)}"" fill=""#d03b3b""/>");
            }
            if (curGroup != null) FlushGroupLabel(gStart, cellRows, curGroup);

            // Column separators
            for (int j = 0; j <= n; j++)
                sb.Append($@"<line x1=""{padL + j * colW}"" y1=""{padT}"" x2=""{padL + j * colW}"" y2=""{padT + gridH}"" stroke=""var(--surface)"" stroke-width=""1.5""/>");
            sb.Append($@"<rect x=""{padL}"" y=""{padT}"" width=""{n * colW}"" height=""{gridH}"" fill=""none"" stroke=""var(--rule)"" stroke-width=""1""/>");

            // Colorbar (right)
            int cbX = padL + n * colW + 34, cbY = padT, cbW = 14, cbH = Math.Min(gridH, 220);
            for (int s = 0; s < cbH; s++)
            {
                double t = 1.0 - (double)s / cbH;
                string fill = Seq[Math.Min(Seq.Length - 1, (int)Math.Round(t * (Seq.Length - 1)))];
                sb.Append($@"<rect x=""{cbX}"" y=""{cbY + s}"" width=""{cbW}"" height=""1"" fill=""{fill}""/>");
            }
            sb.Append($@"<rect x=""{cbX}"" y=""{cbY}"" width=""{cbW}"" height=""{cbH}"" fill=""none"" stroke=""var(--rule)"" stroke-width=""1""/>");
            sb.Append($@"<text x=""{cbX + cbW + 6}"" y=""{cbY + 9}"" font-size=""10.5"" fill=""var(--muted)"">1.0</text>");
            sb.Append($@"<text x=""{cbX + cbW + 6}"" y=""{cbY + cbH}"" font-size=""10.5"" fill=""var(--muted)"">0.0</text>");
            sb.Append($@"<text transform=""translate({cbX + cbW + 40},{cbY + cbH / 2}) rotate(-90)"" text-anchor=""middle"" font-size=""11"" fill=""var(--ink2)"">assignment score (relative)</text>");
            sb.Append($@"<text transform=""translate(18,{padT + gridH / 2}) rotate(-90)"" text-anchor=""middle"" font-size=""12"" font-weight=""600"" fill=""var(--ink2)"">cells (grouped by assigned condition)</text>");

            sb.Append("</svg>");
            return sb.ToString();
        }

        private static string MarginSvg(Result r)
        {
            var byClass = r.Rows.GroupBy(x => x.TrueLabel).OrderBy(g => g.Key, StringComparer.Ordinal).ToList();
            const int padL = 96, padT = 12, colGap = 8, plotH = 190, padB = 40;
            int colW = 120;
            int w = padL + byClass.Count * (colW + colGap) + 20, h = padT + plotH + padB;

            var sb = new StringBuilder();
            sb.Append($@"<svg viewBox=""0 0 {w} {h}"" width=""{w}"" height=""{h}"" role=""img"">");
            for (int t = 0; t <= 4; t++)
            {
                int yy = padT + plotH - (int)(plotH * t / 4.0);
                sb.Append($@"<line x1=""{padL}"" y1=""{yy}"" x2=""{w - 20}"" y2=""{yy}"" stroke=""var(--rule)"" stroke-width=""1""/>");
                sb.Append($@"<text x=""{padL - 8}"" y=""{yy + 4}"" text-anchor=""end"" font-size=""10.5"" fill=""var(--muted)"">{t * 25}%</text>");
            }
            var rng = new Random(11);
            for (int gi = 0; gi < byClass.Count; gi++)
            {
                var g = byClass[gi];
                int cx = padL + gi * (colW + colGap) + colW / 2;
                foreach (var row in g)
                {
                    double jitter = (rng.NextDouble() - 0.5) * (colW - 24);
                    int px = (int)(cx + jitter);
                    int py = padT + plotH - (int)(plotH * Math.Max(0, Math.Min(1, row.Margin)));
                    // Colour by HELD-OUT correctness (out-of-sample). Cells the held-out pass didn't evaluate are
                    // drawn neutral grey rather than implied correct.
                    string fill = !row.HeldOutEvaluated ? "#9ca3af" : (row.HeldOutCorrect ? "#2a78d6" : "#d03b3b");
                    sb.Append($@"<circle cx=""{px}"" cy=""{py}"" r=""2.8"" fill=""{fill}"" fill-opacity=""0.8""/>");
                }
                sb.Append($@"<text x=""{cx}"" y=""{padT + plotH + 16}"" text-anchor=""middle"" font-size=""11"" fill=""var(--ink2)"">{Esc(g.Key)}</text>");
            }
            sb.Append($@"<text transform=""translate(16,{padT + plotH / 2}) rotate(-90)"" text-anchor=""middle"" font-size=""11"" fill=""var(--ink2)"">margin (top − runner-up)</text>");
            // Legend
            int lx = w - 200, ly = padT + 4;
            sb.Append($@"<circle cx=""{lx}"" cy=""{ly}"" r=""4"" fill=""#2a78d6""/><text x=""{lx + 9}"" y=""{ly + 4}"" font-size=""11"" fill=""var(--ink2)"">correct (held-out)</text>");
            sb.Append($@"<circle cx=""{lx}"" cy=""{ly + 17}"" r=""4"" fill=""#d03b3b""/><text x=""{lx + 9}"" y=""{ly + 21}"" font-size=""11"" fill=""var(--ink2)"">misclassified (held-out)</text>");
            sb.Append("</svg>");
            return sb.ToString();
        }

        private static string Esc(string s) => (s ?? "")
            .Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");
    }
}
