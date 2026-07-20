using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace SCPBrowser.Services
{
    /// <summary>
    /// Renders a <see cref="ClassifierEvaluationService.Report"/> as a self-contained HTML page with inline SVG
    /// figures — no external CSS, JS, fonts or libraries, so it opens anywhere and prints to PDF for a manuscript.
    ///
    /// Figure conventions (deliberate, not cosmetic):
    ///  - Confusion matrix uses ONE sequential hue (light→dark = low→high), never a rainbow, and is row-normalised
    ///    so colour encodes recall while the printed number stays the raw count.
    ///  - Categorical series use a fixed hue order with a legend AND direct labels, so identity is never carried by
    ///    colour alone (three of the light-mode hues sit under 3:1 on the surface, so labels are obligatory).
    ///  - Every axis is a single scale. No dual axes.
    ///  - Grid and axes are recessive; the marks carry the message.
    /// </summary>
    public static class EvaluationReportBuilder
    {
        // Sequential blue ramp, light -> dark (magnitude encoding).
        private static readonly string[] Seq =
        {
            "#cde2fb", "#b7d3f6", "#9ec5f4", "#86b6ef", "#6da7ec", "#5598e7",
            "#3987e5", "#2a78d6", "#256abf", "#1c5cab", "#184f95", "#104281", "#0d366b"
        };

        private const string S1 = "#2a78d6";   // categorical slot 1 — blue
        private const string S2 = "#1baf7a";   // slot 2 — aqua
        private const string S3 = "#eda100";   // slot 3 — yellow
        private const string Critical = "#d03b3b";
        private const string Good = "#0ca30c";

        public static string Build(ClassifierEvaluationService.Report r, string datasetName = null)
        {
            var sb = new StringBuilder();
            sb.Append(Head(r, datasetName));
            sb.Append(HeroStats(r));
            sb.Append(Summary(r));
            sb.Append(ConfusionFigure(r));
            sb.Append(PerClassFigure(r));
            sb.Append(ChannelFigure(r));
            sb.Append(ChannelCombinationFigure(r));
            if (r.LearningCurve != null && r.LearningCurve.Count >= 2) sb.Append(LearningCurveFigure(r));
            if (r.SubsetStability != null && r.SubsetStability.Count > 0) sb.Append(StabilityFigure(r));
            sb.Append(MethodsNote(r));
            sb.Append("</div></body></html>");
            return sb.ToString();
        }

        // ---------------------------------------------------------------------------------------------------------

        private static string Head(ClassifierEvaluationService.Report r, string dataset)
        {
            return $@"<!doctype html>
<html lang=""en""><head><meta charset=""utf-8"">
<meta name=""viewport"" content=""width=device-width, initial-scale=1"">
<title>Classifier cross-validation — {Esc(dataset ?? "SCPBrowser")}</title>
<style>
  /* Fixed white-background light theme — exported for white-page manuscripts (no dark variant). */
  :root {{
    color-scheme: light;
    --surface-1: #ffffff; --plane: #ffffff;
    --text-primary: #111111; --text-secondary: #444444; --text-muted: #6b6b6b;
    --grid: #e6e5e1; --rule: #dcdcdc;
  }}
  * {{ box-sizing: border-box; }}
  body {{ margin:0; background: var(--plane); color: var(--text-primary);
         font: 14px/1.55 -apple-system, 'Segoe UI', Roboto, Helvetica, Arial, sans-serif; }}
  .wrap {{ max-width: 1080px; margin: 0 auto; padding: 32px 24px 64px; }}
  h1 {{ font-size: 22px; margin: 0 0 4px; letter-spacing: -0.01em; }}
  h2 {{ font-size: 15px; margin: 0 0 2px; letter-spacing: -0.005em; }}
  .sub {{ color: var(--text-secondary); font-size: 13px; margin: 0 0 24px; }}
  .fig {{ background: var(--surface-1); border: 1px solid var(--rule); border-radius: 8px;
          padding: 18px 18px 14px; margin: 0 0 20px; }}
  .cap {{ color: var(--text-secondary); font-size: 12.5px; margin: 10px 0 0; }}
  .scroll {{ overflow-x: auto; }}
  .tiles {{ display:flex; gap:12px; flex-wrap:wrap; margin: 0 0 20px; }}
  .tile {{ flex:1 1 200px; background: var(--surface-1); border:1px solid var(--rule);
           border-radius:8px; padding:14px 16px; }}
  .tile .k {{ font-size:11.5px; color: var(--text-secondary); text-transform:uppercase;
              letter-spacing:.06em; margin-bottom:6px; }}
  .tile .v {{ font-size:30px; font-weight:650; letter-spacing:-0.02em; line-height:1.1; }}
  .tile .n {{ font-size:12px; color: var(--text-muted); margin-top:4px; }}
  .note {{ background: var(--surface-1); border:1px solid var(--rule); border-left:3px solid {S1};
           border-radius:6px; padding:12px 14px; color: var(--text-secondary); font-size:12.5px; }}
  .explain {{ background: #f3f7fd; border:1px solid #d7e3f5; border-left:3px solid {S1};
              border-radius:6px; padding:14px 16px; font-size:13.5px; line-height:1.6; color:#2b3a4d; }}
  .reads {{ color: var(--text-muted); font-size:12px; margin:8px 0 0; }}
  .fig h2 {{ display:flex; align-items:baseline; gap:10px; flex-wrap:wrap; }}
  .fig h2 .why {{ font-size:11.5px; font-weight:500; color: var(--text-muted); font-style:italic; letter-spacing:0; }}
  .legend {{ display:flex; gap:16px; flex-wrap:wrap; margin: 2px 0 10px; font-size:12.5px;
             color: var(--text-secondary); }}
  .legend i {{ width:10px; height:10px; border-radius:2px; display:inline-block; margin-right:6px; }}
  table {{ border-collapse: collapse; font-size: 12.5px; }}
  text {{ font-family: inherit; }}
</style></head><body><div class=""wrap"">
<h1>Cell-type classification — cross-validated benchmark</h1>
<p class=""sub"">{Esc(dataset ?? "")}{(dataset == null ? "" : " · ")}{r.TotalCells} cells · {r.Labels.Count} classes ·
scorer: {Esc(r.ScorerName)}</p>";
        }

        private static string HeroStats(ClassifierEvaluationService.Report r)
        {
            string ci = $"95% CI {Pct(r.SinglePassCiLow, 1)}–{Pct(r.SinglePassCiHigh, 1)}";
            var sb = new StringBuilder(@"<div class=""tiles"">");
            sb.Append(Tile("Accuracy", Pct(r.Accuracy, 2), $"{r.PredictionsTotal} held-out predictions"));
            sb.Append(Tile("Balanced accuracy", Pct(r.BalancedAccuracy, 2), "mean per-class recall"));
            sb.Append(Tile("Single CV pass", Pct(r.SinglePassAccuracy, 2), $"{r.SinglePassCorrect}/{r.SinglePassTotal} · {ci}"));
            if (r.LoocvAccuracy.HasValue)
                sb.Append(Tile("Leave-one-out", Pct(r.LoocvAccuracy.Value, 2),
                    $"{r.LoocvCorrect}/{r.LoocvTotal} · 95% CI {Pct(r.LoocvCiLow, 1)}–{Pct(r.LoocvCiHigh, 1)}"));
            sb.Append("</div>");
            return sb.ToString();
        }

        private static string Tile(string k, string v, string n) =>
            $@"<div class=""tile""><div class=""k"">{Esc(k)}</div><div class=""v"">{Esc(v)}</div><div class=""n"">{Esc(n)}</div></div>";

        // ---- Plain-language summary of the headline result ------------------------------------------------------

        private static string Summary(ClassifierEvaluationService.Report r)
        {
            // Where do the errors concentrate? Name the dominant off-diagonal confusion, if any.
            string errStory = "no cells were misclassified";
            if (r.Confusion != null)
            {
                (int i, int j, int v) worst = (-1, -1, 0);
                int totalErr = 0;
                for (int i = 0; i < r.Labels.Count; i++)
                    for (int j = 0; j < r.Labels.Count; j++)
                        if (i != j && r.Confusion[i, j] > 0)
                        {
                            totalErr += r.Confusion[i, j];
                            if (r.Confusion[i, j] > worst.v) worst = (i, j, r.Confusion[i, j]);
                        }
                if (totalErr > 0)
                    errStory = $"the errors are dominated by <strong>{Esc(r.Labels[worst.i])} → {Esc(r.Labels[worst.j])}</strong> " +
                               $"({worst.v} of {totalErr} misclassifications)";
            }

            string headline = r.LoocvAccuracy.HasValue
                ? $"{Pct(r.LoocvAccuracy.Value, 1)} leave-one-out accuracy ({r.LoocvCorrect}/{r.LoocvTotal})"
                : $"{Pct(r.SinglePassAccuracy, 1)} single-pass accuracy ({r.SinglePassCorrect}/{r.SinglePassTotal})";

            return $@"<div class=""explain"">
<strong>In brief.</strong> Across {r.Labels.Count} conditions and {r.TotalCells} cells, the classifier reaches {headline},
with a balanced accuracy of {Pct(r.BalancedAccuracy, 1)}. Every cell is scored against reference profiles built only from
<em>other</em> cells, so these are held-out results, not a fit to the same data. When it does err, {errStory}.
Read on for the per-class breakdown, how much each scoring channel contributes, how much reference data the method needs,
and how stable each call is to the proteins measured.</div>
<div style=""height:16px""></div>";
        }

        // ---- Confusion matrix: one sequential hue, row-normalised; number = raw count ----------------------------

        private static string ConfusionFigure(ClassifierEvaluationService.Report r)
        {
            int n = r.Labels.Count;
            const int cell = 62, padL = 108, padT = 76;
            int w = padL + n * cell + 24, h = padT + n * cell + 46;

            var sb = new StringBuilder();
            sb.Append(@"<div class=""fig""><h2>Confusion matrix <span class=""why"">— which conditions get mistaken for which</span></h2>");
            sb.Append(@"<p class=""cap"" style=""margin:0 0 10px"">Each row is one true condition's cells; each column is the classifier's call. Colour shows the fraction of that row landing in each column (light→dark = low→high, i.e. recall), and the number is the raw prediction count. A clean diagonal means every condition is recovered; off-diagonal colour marks conditions the classifier confuses.</p>");
            sb.Append($@"<div class=""scroll""><svg viewBox=""0 0 {w} {h}"" width=""{w}"" height=""{h}"" role=""img"">");

            sb.Append($@"<text x=""{padL + n * cell / 2}"" y=""22"" text-anchor=""middle"" font-size=""12"" font-weight=""600"" fill=""var(--text-secondary)"">predicted</text>");
            for (int j = 0; j < n; j++)
                sb.Append($@"<text x=""{padL + j * cell + cell / 2}"" y=""{padT - 10}"" text-anchor=""middle"" font-size=""11.5"" fill=""var(--text-secondary)"">{Esc(r.Labels[j])}</text>");

            for (int i = 0; i < n; i++)
            {
                int rowSum = 0;
                for (int j = 0; j < n; j++) rowSum += r.Confusion[i, j];
                sb.Append($@"<text x=""{padL - 12}"" y=""{padT + i * cell + cell / 2 + 4}"" text-anchor=""end"" font-size=""11.5"" fill=""var(--text-secondary)"">{Esc(r.Labels[i])}</text>");

                for (int j = 0; j < n; j++)
                {
                    int v = r.Confusion[i, j];
                    double frac = rowSum > 0 ? (double)v / rowSum : 0;
                    string fill = v == 0 ? "var(--surface-1)" : Seq[Math.Min(Seq.Length - 1, (int)Math.Round(frac * (Seq.Length - 1)))];
                    // 2px surface gap between fills
                    sb.Append($@"<rect x=""{padL + j * cell + 1}"" y=""{padT + i * cell + 1}"" width=""{cell - 2}"" height=""{cell - 2}"" rx=""4"" fill=""{fill}"" stroke=""var(--grid)"" stroke-width=""1""/>");
                    if (v > 0)
                    {
                        string ink = frac > 0.45 ? "#ffffff" : "var(--text-primary)";
                        sb.Append($@"<text x=""{padL + j * cell + cell / 2}"" y=""{padT + i * cell + cell / 2 + 5}"" text-anchor=""middle"" font-size=""13"" font-weight=""600"" fill=""{ink}"">{v}</text>");
                    }
                }
            }
            sb.Append($@"<text transform=""translate(20,{padT + n * cell / 2}) rotate(-90)"" text-anchor=""middle"" font-size=""12"" font-weight=""600"" fill=""var(--text-secondary)"">true</text>");
            sb.Append("</svg></div>");

            var offDiag = new List<string>();
            for (int i = 0; i < n; i++)
                for (int j = 0; j < n; j++)
                    if (i != j && r.Confusion[i, j] > 0)
                        offDiag.Add($"{r.Labels[i]}→{r.Labels[j]} ({r.Confusion[i, j]})");
            sb.Append($@"<p class=""cap"">{(offDiag.Count == 0 ? "No off-diagonal predictions: every cell was assigned to its true condition."
                : "Off-diagonal: " + Esc(string.Join(", ", offDiag)) + ".")}</p>");
            sb.Append("</div>");
            return sb.ToString();
        }

        // ---- Per-class precision / recall / F1: 3 categorical series, legend + direct labels ---------------------

        private static string PerClassFigure(ClassifierEvaluationService.Report r)
        {
            var cls = r.PerClass;
            if (cls == null || cls.Count == 0) return "";

            const int rowH = 26, groupGap = 16, padL = 108, padT = 8, barMax = 620;
            int h = padT + cls.Count * (rowH * 3 + groupGap) + 30;
            int w = padL + barMax + 70;

            var sb = new StringBuilder();
            sb.Append(@"<div class=""fig""><h2>Per-class performance <span class=""why"">— is any single condition weaker than the headline?</span></h2>");
            sb.Append(@"<p class=""cap"" style=""margin:0 0 4px"">Precision = of the cells called this condition, how many truly were it. Recall = of this condition's true cells, how many were found. F1 = their harmonic mean (a single balance of the two).</p>");
            sb.Append($@"<div class=""legend"">
<span><i style=""background:{S1}""></i>Precision</span>
<span><i style=""background:{S2}""></i>Recall</span>
<span><i style=""background:{S3}""></i>F1</span></div>");
            sb.Append($@"<div class=""scroll""><svg viewBox=""0 0 {w} {h}"" width=""{w}"" height=""{h}"" role=""img"">");

            for (int t = 0; t <= 4; t++)
            {
                int x = padL + (int)(barMax * t / 4.0);
                sb.Append($@"<line x1=""{x}"" y1=""{padT}"" x2=""{x}"" y2=""{h - 26}"" stroke=""var(--grid)"" stroke-width=""1""/>");
                sb.Append($@"<text x=""{x}"" y=""{h - 10}"" text-anchor=""middle"" font-size=""11"" fill=""var(--text-muted)"">{t * 25}%</text>");
            }

            int y = padT;
            foreach (var c in cls)
            {
                sb.Append($@"<text x=""{padL - 12}"" y=""{y + rowH * 1.5 + 4}"" text-anchor=""end"" font-size=""12.5"" font-weight=""600"" fill=""var(--text-primary)"">{Esc(c.Label)}</text>");
                sb.Append(Bar(padL, y, barMax, rowH, c.Precision, S1));
                sb.Append(Bar(padL, y + rowH, barMax, rowH, c.Recall, S2));
                sb.Append(Bar(padL, y + rowH * 2, barMax, rowH, c.F1, S3));
                y += rowH * 3 + groupGap;
            }
            sb.Append("</svg></div>");
            sb.Append($@"<p class=""cap"">Support: {string.Join(" · ", cls.Select(c => $"{Esc(c.Label)} {c.Support}"))}.</p>");
            sb.Append("</div>");
            return sb.ToString();
        }

        /// <summary>4px rounded data-end anchored to the baseline, 2px surface gap, direct value label.</summary>
        private static string Bar(int x0, int y, int max, int rowH, double frac, string fill)
        {
            int bw = Math.Max(2, (int)Math.Round(max * Math.Max(0, Math.Min(1, frac))));
            int bh = rowH - 6;
            var sb = new StringBuilder();
            sb.Append($@"<rect x=""{x0}"" y=""{y + 3}"" width=""{bw}"" height=""{bh}"" rx=""4"" fill=""{fill}""/>");
            sb.Append($@"<text x=""{x0 + bw + 8}"" y=""{y + 3 + bh - 4}"" font-size=""11.5"" fill=""var(--text-secondary)"">{Pct(frac, 1)}</text>");
            return sb.ToString();
        }

        // ---- Channel contribution — the "feature strength" figure ------------------------------------------------

        private static string ChannelFigure(ClassifierEvaluationService.Report r)
        {
            var stats = ClassifierEvaluationService.ChannelStats(r);
            if (stats == null || stats.Count == 0) return "";

            const int rowH = 34, padL = 128, padT = 10, barMax = 560;
            int h = padT + stats.Count * rowH + 34;
            int w = padL + barMax + 210;
            double uniform = 1.0 / Math.Max(2, r.Labels.Count);

            var sb = new StringBuilder();
            sb.Append(@"<div class=""fig""><h2>Scoring-channel contribution <span class=""why"">— does every metric earn its place?</span></h2>");
            string fusionNote = r.IsQuantitativeScorer
                ? "the channels are combined by reliability-weighted log-pooling, so a low-spread channel is down-weighted rather than diluting the decision"
                : "the channels are combined by an unweighted mean, so a low-spread channel still takes a full share of the combined score and dilutes the decision";
            sb.Append($@"<p class=""cap"" style=""margin:0 0 10px"">Each metric's accuracy on its own, and its <em>spread</em> — the gap between the highest and lowest class probability it produces. A spread near zero means the channel emits nearly the same value for every class: it cannot discriminate. Here {fusionNote}. Chance = {Pct(uniform, 0)}.</p>");
            sb.Append($@"<div class=""scroll""><svg viewBox=""0 0 {w} {h}"" width=""{w}"" height=""{h}"" role=""img"">");

            for (int t = 0; t <= 4; t++)
            {
                int x = padL + (int)(barMax * t / 4.0);
                sb.Append($@"<line x1=""{x}"" y1=""{padT}"" x2=""{x}"" y2=""{h - 26}"" stroke=""var(--grid)"" stroke-width=""1""/>");
                sb.Append($@"<text x=""{x}"" y=""{h - 10}"" text-anchor=""middle"" font-size=""11"" fill=""var(--text-muted)"">{t * 25}%</text>");
            }
            int cx = padL + (int)(barMax * uniform);
            sb.Append($@"<line x1=""{cx}"" y1=""{padT}"" x2=""{cx}"" y2=""{h - 26}"" stroke=""var(--text-muted)"" stroke-width=""1"" stroke-dasharray=""4 3""/>");
            sb.Append($@"<text x=""{cx + 4}"" y=""{padT + 10}"" font-size=""10.5"" fill=""var(--text-muted)"">chance</text>");

            int y = padT;
            foreach (var s in stats)
            {
                bool dead = s.MeanSpread < 0.01;
                sb.Append($@"<text x=""{padL - 12}"" y=""{y + 21}"" text-anchor=""end"" font-size=""12.5"" font-weight=""600"" fill=""var(--text-primary)"">{Esc(s.Name)}</text>");
                sb.Append(Bar(padL, y, barMax, rowH - 4, s.StandaloneAccuracy, dead ? Critical : S1));
                string tag = dead
                    ? $@"<tspan fill=""{Critical}"" font-weight=""600"">inert</tspan> · spread {s.MeanSpread:F4}"
                    : $"spread {s.MeanSpread:F3}";
                sb.Append($@"<text x=""{padL + barMax + 66}"" y=""{y + 21}"" font-size=""11.5"" fill=""var(--text-secondary)"">{tag}</text>");
                y += rowH;
            }
            sb.Append("</svg></div>");

            var deadOnes = stats.Where(s => s.MeanSpread < 0.01).ToList();
            if (deadOnes.Count > 0)
                sb.Append($@"<p class=""cap"" style=""color:{Critical}"">{Esc(string.Join(", ", deadOnes.Select(d => d.Name)))} is uniform in {deadOnes[0].FlatCells}/{deadOnes[0].TotalCells} cells — it contributes a constant to every class and cannot affect which class wins.</p>");
            sb.Append(@"<p class=""reads"">How to read: a tall standalone-accuracy bar means the metric classifies well on its own; a near-zero spread means it emits the same value for every class, so it carries no information regardless of its accuracy.</p>");
            sb.Append("</div>");
            return sb.ToString();
        }

        // ---- Channel-combination ablation: which subset of metrics carries the decision -------------------------

        private static string ChannelCombinationFigure(ClassifierEvaluationService.Report r)
        {
            var combos = ClassifierEvaluationService.ChannelCombinations(r);
            if (combos.Count == 0) return "";
            var rows = combos.OrderByDescending(c => c.Accuracy).ToList();

            const int rowH = 22, padL = 300, padT = 8, barMax = 460;
            int h = padT + rows.Count * rowH + 34;
            int w = padL + barMax + 80;
            double minAcc = Math.Max(0, rows.Min(c => c.Accuracy) - 0.05);

            var sb = new StringBuilder();
            sb.Append(@"<div class=""fig""><h2>Channel-combination ablation <span class=""why"">— which metrics actually carry the decision</span></h2>");
            sb.Append(@"<p class=""cap"" style=""margin:0 0 10px"">Accuracy of every subset of the four scoring channels, over all predictions. The full set (the shipped scorer) is highlighted. This is diagnostic: the best subset is picked after seeing the answers, so its accuracy is optimistic — but it shows whether any channel is dead weight or actively harmful.</p>");
            sb.Append($@"<div class=""scroll""><svg viewBox=""0 0 {w} {h}"" width=""{w}"" height=""{h}"" role=""img"">");

            for (int t = 0; t <= 4; t++)
            {
                double a = minAcc + (1.0 - minAcc) * t / 4.0;
                int x = padL + (int)(barMax * (a - minAcc) / (1.0 - minAcc));
                sb.Append($@"<line x1=""{x}"" y1=""{padT}"" x2=""{x}"" y2=""{h - 26}"" stroke=""var(--grid)"" stroke-width=""1""/>");
                sb.Append($@"<text x=""{x}"" y=""{h - 10}"" text-anchor=""middle"" font-size=""10.5"" fill=""var(--text-muted)"">{Pct(a, 0)}</text>");
            }

            int y = padT;
            foreach (var c in rows)
            {
                int bw = Math.Max(2, (int)Math.Round(barMax * (c.Accuracy - minAcc) / (1.0 - minAcc)));
                string fill = c.IsFullSet ? S3 : (c.ChannelCount == 1 ? "#9ec5f4" : S1);
                string label = c.IsFullSet ? c.Channels + "  (full set)" : c.Channels;
                sb.Append($@"<text x=""{padL - 10}"" y=""{y + rowH / 2 + 4}"" text-anchor=""end"" font-size=""11"" fill=""var(--text-secondary)"" font-weight=""{(c.IsFullSet ? "700" : "400")}"">{Esc(label)}</text>");
                sb.Append($@"<rect x=""{padL}"" y=""{y + 3}"" width=""{bw}"" height=""{rowH - 6}"" rx=""3"" fill=""{fill}""/>");
                sb.Append($@"<text x=""{padL + bw + 7}"" y=""{y + rowH / 2 + 4}"" font-size=""10.5"" fill=""var(--text-secondary)"">{Pct(c.Accuracy, 1)}</text>");
                y += rowH;
            }
            sb.Append("</svg></div>");
            sb.Append($@"<p class=""reads"">Bars above the full-set (gold) bar are subsets that do at least as well as the whole scorer — evidence that a channel is diluting rather than helping. Bars far below show which metrics can't stand alone.</p>");
            sb.Append("</div>");
            return sb.ToString();
        }

        // ---- Learning curve --------------------------------------------------------------------------------------

        private static string LearningCurveFigure(ClassifierEvaluationService.Report r)
        {
            var pts = r.LearningCurve.OrderBy(p => p.ReferenceCellsPerClass).ToList();
            const int padL = 62, padR = 24, padT = 14, padB = 44, plotW = 620, plotH = 220;
            int w = padL + plotW + padR, h = padT + plotH + padB;

            double minY = Math.Max(0, pts.Min(p => p.Accuracy) - 0.08), maxY = 1.0;
            int kMin = pts.Min(p => p.ReferenceCellsPerClass), kMax = pts.Max(p => p.ReferenceCellsPerClass);
            Func<int, double> X = k => padL + (kMax == kMin ? plotW / 2.0 : plotW * (k - kMin) / (double)(kMax - kMin));
            Func<double, double> Y = a => padT + plotH * (1 - (a - minY) / (maxY - minY));

            var sb = new StringBuilder();
            sb.Append(@"<div class=""fig""><h2>Learning curve <span class=""why"">— how much labelled reference data does it need?</span></h2>");
            sb.Append(@"<p class=""cap"" style=""margin:0 0 10px"">Accuracy as a function of how many reference cells per class the model is built from — i.e. how much labelled data the classifier actually needs.</p>");
            sb.Append($@"<div class=""scroll""><svg viewBox=""0 0 {w} {h}"" width=""{w}"" height=""{h}"" role=""img"">");

            for (int t = 0; t <= 4; t++)
            {
                double a = minY + (maxY - minY) * t / 4.0;
                double yy = Y(a);
                sb.Append($@"<line x1=""{padL}"" y1=""{yy:F1}"" x2=""{padL + plotW}"" y2=""{yy:F1}"" stroke=""var(--grid)"" stroke-width=""1""/>");
                sb.Append($@"<text x=""{padL - 10}"" y=""{yy + 4:F1}"" text-anchor=""end"" font-size=""11"" fill=""var(--text-muted)"">{Pct(a, 0)}</text>");
            }

            var d = string.Join(" ", pts.Select((p, i) => $"{(i == 0 ? "M" : "L")}{X(p.ReferenceCellsPerClass):F1},{Y(p.Accuracy):F1}"));
            sb.Append($@"<path d=""{d}"" fill=""none"" stroke=""{S1}"" stroke-width=""2"" stroke-linejoin=""round"" stroke-linecap=""round""/>");
            foreach (var p in pts)
            {
                sb.Append($@"<circle cx=""{X(p.ReferenceCellsPerClass):F1}"" cy=""{Y(p.Accuracy):F1}"" r=""4.5"" fill=""{S1}"" stroke=""var(--surface-1)"" stroke-width=""2""/>");
                sb.Append($@"<text x=""{X(p.ReferenceCellsPerClass):F1}"" y=""{Y(p.Accuracy) - 12:F1}"" text-anchor=""middle"" font-size=""11"" fill=""var(--text-secondary)"">{Pct(p.Accuracy, 1)}</text>");
                sb.Append($@"<text x=""{X(p.ReferenceCellsPerClass):F1}"" y=""{padT + plotH + 18}"" text-anchor=""middle"" font-size=""11.5"" fill=""var(--text-secondary)"">{p.ReferenceCellsPerClass}</text>");
            }
            sb.Append($@"<text x=""{padL + plotW / 2}"" y=""{h - 8}"" text-anchor=""middle"" font-size=""12"" fill=""var(--text-secondary)"">reference cells per class</text>");
            sb.Append("</svg></div></div>");
            return sb.ToString();
        }

        // ---- Protein-subset stability ----------------------------------------------------------------------------

        private static string StabilityFigure(ClassifierEvaluationService.Report r)
        {
            var vals = r.SubsetStability.Values.OrderBy(v => v).ToList();
            int bins = 10;
            var counts = new int[bins];
            foreach (var v in vals) counts[Math.Min(bins - 1, (int)(v * bins))]++;

            const int padL = 56, padT = 14, plotW = 620, plotH = 180, padB = 46;
            int w = padL + plotW + 24, h = padT + plotH + padB;
            int maxC = Math.Max(1, counts.Max());

            var sb = new StringBuilder();
            sb.Append(@"<div class=""fig""><h2>Uncertainty <span class=""why"">— how stable is each call to the proteins measured?</span></h2>");
            sb.Append($@"<p class=""cap"" style=""margin:0 0 10px"">Each cell was re-classified many times using random halves of its measured proteins. The bar shows how many cells fell in each agreement band — a cell at 100% received the same call from every subset. Mean agreement {Pct(vals.Average(), 1)}; {vals.Count(v => v >= 0.999)} of {vals.Count} cells fully stable.</p>");
            sb.Append($@"<div class=""scroll""><svg viewBox=""0 0 {w} {h}"" width=""{w}"" height=""{h}"" role=""img"">");

            int bw = plotW / bins;
            for (int i = 0; i < bins; i++)
            {
                int bh = (int)Math.Round(plotH * counts[i] / (double)maxC);
                int x = padL + i * bw, yTop = padT + plotH - bh;
                if (counts[i] > 0)
                {
                    sb.Append($@"<rect x=""{x + 2}"" y=""{yTop}"" width=""{bw - 4}"" height=""{bh}"" rx=""4"" fill=""{S1}""/>");
                    sb.Append($@"<text x=""{x + bw / 2}"" y=""{yTop - 5}"" text-anchor=""middle"" font-size=""11"" fill=""var(--text-secondary)"">{counts[i]}</text>");
                }
                sb.Append($@"<text x=""{x + bw / 2}"" y=""{padT + plotH + 16}"" text-anchor=""middle"" font-size=""10.5"" fill=""var(--text-muted)"">{i * 10}–{(i + 1) * 10}</text>");
            }
            sb.Append($@"<line x1=""{padL}"" y1=""{padT + plotH}"" x2=""{padL + plotW}"" y2=""{padT + plotH}"" stroke=""var(--rule)"" stroke-width=""1""/>");
            sb.Append($@"<text x=""{padL + plotW / 2}"" y=""{h - 8}"" text-anchor=""middle"" font-size=""12"" fill=""var(--text-secondary)"">agreement across protein subsets (%)</text>");
            sb.Append("</svg></div></div>");
            return sb.ToString();
        }

        private static string MethodsNote(ClassifierEvaluationService.Report r)
        {
            var sb = new StringBuilder(@"<div class=""note""><strong>Method.</strong> ");
            sb.Append($"Stratified {r.RepeatAccuracies.Count}× repeated cross-validation over {r.TotalCells} cells in {r.Labels.Count} classes. ");
            sb.Append("Each fold rebuilds the reference profiles from its <em>training</em> cells only, so no cell is ever scored against a reference it helped define. ");
            sb.Append("Training labels are the ground-truth conditions, so the label spaces coincide by construction. ");
            sb.Append("Confidence intervals are Wilson score intervals on independent cells from a single pass — not on the repeats, which re-score the same cells and would understate uncertainty. ");
            if (r.RepeatAccuracies.Count > 1)
                sb.Append($"Across-repeat spread (fold-partition variability, not sampling uncertainty) was {Pct(r.FoldPartitionSd, 2)}. ");
            sb.Append($"Scored without key markers, priors or cell-type exclusions, i.e. the unaided classifier. Scorer: {Esc(r.ScorerName)}.");
            if (r.SkippedNGreaterThanN > 0)
                sb.Append($" {r.SkippedNGreaterThanN} prediction(s) were skipped (detected genes exceeded the reference universe).");
            foreach (var wn in r.Warnings) sb.Append($"<br><strong>Warning.</strong> {Esc(wn)}");
            sb.Append("</div>");
            return sb.ToString();
        }

        private static string Pct(double v, int dp) => (v * 100).ToString("F" + dp, CultureInfo.InvariantCulture) + "%";

        private static string Esc(string s) => (s ?? "")
            .Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");
    }
}
