using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Data.Sqlite;

namespace SCPBrowser.Services
{
    /// <summary>
    /// High-content phenotypic profiling of cellenONE micrographs, reimplementing the families CellProfiler is used
    /// for in the scpImaging pipeline (Loi, Holmes et al., bioRxiv 2025, doi:10.64898/2025.12.01.691577, MIT-licensed
    /// package emmottlab/scpImaging) — but natively in C# so no Python/R/CellProfiler is required.
    ///
    /// For each isolated cell it background-subtracts and segments the primary cell in its brightfield (Trans) frame,
    /// then computes shape (AreaShape_*), intensity (Intensity_*), Haralick texture (Texture_*) and radial-distribution
    /// (RadialDistribution_*) features. Results land in cell_features (long format). The paper found shape and texture
    /// to be the strongest drivers of both doublet detection and cell-type classification, so these add a phenotypic
    /// dimension that links cell morphology to the proteomics.
    /// </summary>
    public class CellPhenotypeService : DatabaseServiceBase
    {
        public int DiffThreshold { get; set; } = 22;   // background-subtract threshold (matches CellImageAnalysisService)
        public int CandidateFloor { get; set; } = 120; // min blob area (px) to be a cell
        public int MaxArea { get; set; } = 6000;        // max blob area (px)
        public int TextureLevels { get; set; } = 8;     // GLCM gray levels

        public CellPhenotypeService(string projectDbPath) : base(projectDbPath) { }

        /// <summary>Computes and stores phenotype features for every cell in a run. Returns the number of cells profiled.</summary>
        public async Task<int> AnalyzeRunAsync(int cellenOneRunId, IProgress<string>? progress = null, CancellationToken ct = default)
        {
            byte[]? bgPng = await WithConnectionAsync<byte[]?>(async conn =>
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT background_image FROM cellenone_runs WHERE cellenone_run_id=@r";
                cmd.Parameters.AddWithValue("@r", cellenOneRunId);
                var r = await cmd.ExecuteScalarAsync();
                return r == null || r == DBNull.Value ? null : (byte[])r;
            });
            if (bgPng == null) return 0;
            var (bg, bw, bh) = ToGray(bgPng);

            // Isolation ROI (same as the doublet detector): restrict segmentation to objects co-isolated within the
            // ejection/sedimentation bounds, not the whole frame. Null bounds ⇒ whole frame.
            int? ejBound = null, sedBound = null;
            await WithConnectionAsync(async conn =>
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT ejection_bound, sedimentation_bound FROM cellenone_runs WHERE cellenone_run_id=@r";
                cmd.Parameters.AddWithValue("@r", cellenOneRunId);
                using var rd = await cmd.ExecuteReaderAsync();
                if (await rd.ReadAsync())
                {
                    ejBound = rd.IsDBNull(0) ? (int?)null : rd.GetInt32(0);
                    sedBound = rd.IsDBNull(1) ? (int?)null : rd.GetInt32(1);
                }
            });

            var items = new List<(int cellId, double? ix, double? iy, double? dia, byte[] png)>();
            await WithConnectionAsync(async conn =>
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"SELECT ic.cell_id, ic.image_x, ic.image_y, ic.diameter, ci.image_blob
                                    FROM isolated_cells ic
                                    JOIN cell_images ci ON ci.cell_id = ic.cell_id AND ci.channel = 'Trans'
                                    WHERE ic.cellenone_run_id = @r AND ci.image_blob IS NOT NULL";
                cmd.Parameters.AddWithValue("@r", cellenOneRunId);
                using var reader = await cmd.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                    items.Add((reader.GetInt32(0),
                               reader.IsDBNull(1) ? null : reader.GetDouble(1),
                               reader.IsDBNull(2) ? null : reader.GetDouble(2),
                               reader.IsDBNull(3) ? null : reader.GetDouble(3),
                               (byte[])reader[4]));
            });

            var rows = new List<(int cellId, string feat, double val)>();
            int profiled = 0;
            for (int i = 0; i < items.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var it = items[i];
                try
                {
                    var (g, w, h) = ToGray(it.png);
                    var feats = ProfileCell(g, w, h, bg, bw, bh, it.ix, it.iy, it.dia,
                                            DiffThreshold, CandidateFloor, MaxArea, TextureLevels, ejBound, sedBound);
                    if (feats != null)
                    {
                        foreach (var kv in feats) rows.Add((it.cellId, kv.Key, kv.Value));
                        profiled++;
                    }
                }
                catch { /* a single bad image must not abort the run */ }
                if (i % 50 == 0) progress?.Report($"Profiling cell phenotypes... {i}/{items.Count}");
            }

            await WithConnectionAsync(async conn =>
            {
                using var tx = conn.BeginTransaction();
                using (var del = conn.CreateCommand())
                {
                    del.Transaction = tx;
                    del.CommandText = "DELETE FROM cell_features WHERE cell_id IN (SELECT cell_id FROM isolated_cells WHERE cellenone_run_id=@r)";
                    del.Parameters.AddWithValue("@r", cellenOneRunId);
                    await del.ExecuteNonQueryAsync();
                }
                using var cmd = conn.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = "INSERT OR REPLACE INTO cell_features (cell_id, feature, value) VALUES (@c, @f, @v)";
                var pC = cmd.Parameters.Add("@c", SqliteType.Integer);
                var pF = cmd.Parameters.Add("@f", SqliteType.Text);
                var pV = cmd.Parameters.Add("@v", SqliteType.Real);
                foreach (var (cellId, feat, val) in rows)
                {
                    pC.Value = cellId; pF.Value = feat;
                    pV.Value = double.IsFinite(val) ? val : (object)DBNull.Value;
                    await cmd.ExecuteNonQueryAsync();
                }
                await tx.CommitAsync();
            });

            progress?.Report($"Phenotype profiling: {profiled}/{items.Count} cells profiled ({rows.Count} feature values).");
            return profiled;
        }

        /// <summary>
        /// Segments the primary cell in a frame (background-subtract → threshold → connected components, picking the
        /// largest cell-sized blob nearest the cellenONE cell coordinates) and returns its feature dictionary, or null
        /// if no cell was found.
        /// </summary>
        public static Dictionary<string, double>? ProfileCell(
            byte[] g, int w, int h, byte[] bg, int bw, int bh,
            double? ix, double? iy, double? dia, int diffThreshold, int floor, int maxArea, int levels,
            int? ejBound = null, int? sedBound = null)
        {
            int W = Math.Min(w, bw), H = Math.Min(h, bh);
            var mask = new bool[w * h];
            for (int y = 0; y < H; y++)
                for (int x = 0; x < W; x++)
                    if (Math.Abs(g[y * w + x] - bg[y * bw + x]) > diffThreshold)
                        mask[y * w + x] = true;

            var blobs = ConnectedComponents(mask, w, h)
                .Where(b => b.Count >= floor && b.Count <= maxArea)
                .Where(b => { var c = Centroid(b, w); return InRoi(c.x, c.y, ejBound, sedBound); })
                .ToList();
            if (blobs.Count == 0) return null;

            // Primary = the cell-sized blob nearest the instrument's cell coordinate (else the largest).
            List<int> primary;
            if (ix.HasValue && iy.HasValue)
            {
                double cx = ix.Value, cy = iy.Value;
                primary = blobs.OrderBy(b =>
                {
                    var (bx, by) = Centroid(b, w);
                    return (bx - cx) * (bx - cx) + (by - cy) * (by - cy);
                }).First();
            }
            else primary = blobs.OrderByDescending(b => b.Count).First();

            var feats = ComputeFeatures(g, w, h, primary, levels);
            feats["Segmentation_NObjects"] = blobs.Count; // local multiplet signal
            return feats;
        }

        /// <summary>
        /// Computes shape, intensity, Haralick texture and radial-distribution features for a segmented blob
        /// (pixel indices into the w×h grayscale frame g). Public + static so the math can be unit-tested directly.
        /// </summary>
        public static Dictionary<string, double> ComputeFeatures(byte[] g, int w, int h, IReadOnlyList<int> pixels, int levels)
        {
            int n = pixels.Count;
            var f = new Dictionary<string, double>();

            // --- geometry: centroid + central moments ---
            double sx = 0, sy = 0;
            int minx = int.MaxValue, maxx = int.MinValue, miny = int.MaxValue, maxy = int.MinValue;
            foreach (int p in pixels)
            {
                int x = p % w, y = p / w;
                sx += x; sy += y;
                if (x < minx) minx = x; if (x > maxx) maxx = x;
                if (y < miny) miny = y; if (y > maxy) maxy = y;
            }
            double cx = sx / n, cy = sy / n;
            double m20 = 0, m02 = 0, m11 = 0;
            foreach (int p in pixels)
            {
                double dx = (p % w) - cx, dy = (p / w) - cy;
                m20 += dx * dx; m02 += dy * dy; m11 += dx * dy;
            }
            m20 /= n; m02 /= n; m11 /= n;

            // --- perimeter (boundary pixels with a 4-neighbor outside the blob) + convex hull ---
            var blobSet = new HashSet<int>(pixels);
            int perim = 0;
            foreach (int p in pixels)
            {
                int x = p % w, y = p / w;
                bool boundary =
                    x == 0 || x == w - 1 || y == 0 || y == h - 1 ||
                    !blobSet.Contains(p - 1) || !blobSet.Contains(p + 1) ||
                    !blobSet.Contains(p - w) || !blobSet.Contains(p + w);
                if (boundary) perim++;
            }
            double perimeter = Math.Max(1, perim);
            double hullArea = ConvexHullArea(pixels, w);

            // Shape features the cellenONE does NOT already report. (It provides Diameter/Elongation/Circularity per
            // cell, so EquivalentDiameter/FormFactor/Eccentricity/AspectRatio/axis-lengths are intentionally omitted
            // as redundant — the instrument's own values stay primary.)
            f["AreaShape_Area"] = n;
            f["AreaShape_Perimeter"] = perimeter;
            f["AreaShape_Orientation"] = 0.5 * Math.Atan2(2 * m11, m20 - m02) * 180.0 / Math.PI;
            f["AreaShape_Extent"] = (double)n / Math.Max(1, (maxx - minx + 1) * (maxy - miny + 1));
            f["AreaShape_Solidity"] = hullArea > 0 ? Math.Min(1.0, n / hullArea) : 1.0; // convex-hull irregularity

            // --- intensity within the mask (rescaled 0..1). The instrument already reports mean Intensity, so we keep
            //     only the spread/total it does NOT provide (Std, Integrated, MAD); Mean/Min/Max are omitted. ---
            double iSum = 0, iSum2 = 0;
            var vals = new double[n];
            for (int k = 0; k < n; k++)
            {
                double v = g[pixels[k]] / 255.0;
                vals[k] = v; iSum += v; iSum2 += v * v;
            }
            double iMean = iSum / n;
            f["Intensity_StdIntensity"] = Math.Sqrt(Math.Max(0, iSum2 / n - iMean * iMean));
            f["Intensity_IntegratedIntensity"] = iSum;
            f["Intensity_MADIntensity"] = MedianAbsDev(vals);

            // --- Haralick texture (GLCM, intra-mask neighbor pairs, symmetric, d=1, 0°+90°) ---
            AddTexture(f, g, w, blobSet, pixels, levels);

            // --- radial intensity distribution: fraction of intensity in 3 concentric rings + radial CV ---
            AddRadial(f, g, w, pixels, cx, cy);

            return f;
        }

        private static void AddTexture(Dictionary<string, double> f, byte[] g, int w, HashSet<int> blob, IReadOnlyList<int> pixels, int levels)
        {
            var glcm = new double[levels, levels];
            double total = 0;
            foreach (int p in pixels)
            {
                int a = g[p] * levels / 256;
                int right = p + 1, down = p + w;
                if (blob.Contains(right)) { int b = g[right] * levels / 256; glcm[a, b]++; glcm[b, a]++; total += 2; }
                if (blob.Contains(down)) { int b = g[down] * levels / 256; glcm[a, b]++; glcm[b, a]++; total += 2; }
            }
            if (total <= 0)
            {
                f["Texture_Contrast"] = 0; f["Texture_AngularSecondMoment"] = 0;
                f["Texture_Correlation"] = 0; f["Texture_Homogeneity"] = 0; f["Texture_Entropy"] = 0;
                return;
            }

            double asm = 0, contrast = 0, homogeneity = 0, entropy = 0;
            double mx = 0, my = 0;
            for (int i = 0; i < levels; i++)
                for (int j = 0; j < levels; j++)
                {
                    double pij = glcm[i, j] / total;
                    if (pij <= 0) continue;
                    asm += pij * pij;
                    contrast += (i - j) * (i - j) * pij;
                    homogeneity += pij / (1.0 + (i - j) * (i - j));
                    entropy -= pij * Math.Log(pij);
                    mx += i * pij; my += j * pij;
                }
            double sx = 0, sy = 0, cov = 0;
            for (int i = 0; i < levels; i++)
                for (int j = 0; j < levels; j++)
                {
                    double pij = glcm[i, j] / total;
                    if (pij <= 0) continue;
                    sx += (i - mx) * (i - mx) * pij;
                    sy += (j - my) * (j - my) * pij;
                    cov += (i - mx) * (j - my) * pij;
                }
            f["Texture_Contrast"] = contrast;
            f["Texture_AngularSecondMoment"] = asm;
            f["Texture_Homogeneity"] = homogeneity;
            f["Texture_Entropy"] = entropy;
            f["Texture_Correlation"] = (sx > 1e-12 && sy > 1e-12) ? cov / Math.Sqrt(sx * sy) : 0;
        }

        private static void AddRadial(Dictionary<string, double> f, byte[] g, int w, IReadOnlyList<int> pixels, double cx, double cy)
        {
            double rmax = 1e-9;
            foreach (int p in pixels)
            {
                double dx = (p % w) - cx, dy = (p / w) - cy;
                double r = Math.Sqrt(dx * dx + dy * dy);
                if (r > rmax) rmax = r;
            }
            var ringSum = new double[3];
            double tot = 0;
            foreach (int p in pixels)
            {
                double dx = (p % w) - cx, dy = (p / w) - cy;
                double rn = Math.Sqrt(dx * dx + dy * dy) / rmax;
                int ring = Math.Min(2, (int)(rn * 3));
                double v = g[p] / 255.0;
                ringSum[ring] += v; tot += v;
            }
            for (int k = 0; k < 3; k++)
                f[$"RadialDistribution_FracAtD_{k + 1}of3"] = tot > 0 ? ringSum[k] / tot : 0;
            double rm = (ringSum[0] + ringSum[1] + ringSum[2]) / 3.0;
            double rv = 0;
            for (int k = 0; k < 3; k++) rv += (ringSum[k] - rm) * (ringSum[k] - rm);
            f["RadialDistribution_RadialCV"] = rm > 1e-12 ? Math.Sqrt(rv / 3.0) / rm : 0;
        }

        // ---- geometry helpers ----

        private static (double x, double y) Centroid(List<int> blob, int w)
        {
            double sx = 0, sy = 0;
            foreach (int p in blob) { sx += p % w; sy += p / w; }
            return (sx / blob.Count, sy / blob.Count);
        }

        /// <summary>True if a blob centroid is inside the isolation corridor — the X band between the two zone boundary lines (order-agnostic). Needs both bounds; missing either ⇒ no restriction.</summary>
        private static bool InRoi(double x, double y, int? ejBound, int? sedBound)
        {
            // Single high-X cutoff at the outer (ejection) boundary; objects past it (nozzle) are excluded, real cells
            // anywhere before it are kept. See CellImageAnalysisService.InIsolationRoi for the rationale.
            int? cutoff = ejBound.HasValue && sedBound.HasValue ? Math.Max(ejBound.Value, sedBound.Value) : (ejBound ?? sedBound);
            return !cutoff.HasValue || x <= cutoff.Value;
        }

        private static double MedianAbsDev(double[] vals)
        {
            double med = Median(vals);
            var dev = new double[vals.Length];
            for (int i = 0; i < vals.Length; i++) dev[i] = Math.Abs(vals[i] - med);
            return Median(dev);
        }

        private static double Median(double[] xs)
        {
            var s = (double[])xs.Clone();
            Array.Sort(s);
            int n = s.Length;
            return n == 0 ? 0 : (n % 2 == 1 ? s[n / 2] : 0.5 * (s[n / 2 - 1] + s[n / 2]));
        }

        /// <summary>Area of the convex hull of the blob's pixels (Andrew's monotone chain + shoelace).</summary>
        private static double ConvexHullArea(IReadOnlyList<int> pixels, int w)
        {
            var pts = new List<(int x, int y)>(pixels.Count);
            foreach (int p in pixels) pts.Add((p % w, p / w));
            pts.Sort((a, b) => a.x != b.x ? a.x.CompareTo(b.x) : a.y.CompareTo(b.y));
            if (pts.Count < 3) return Math.Max(1, pixels.Count);

            int Cross((int x, int y) o, (int x, int y) a, (int x, int y) b)
                => (a.x - o.x) * (b.y - o.y) - (a.y - o.y) * (b.x - o.x);

            var hull = new List<(int x, int y)>();
            foreach (var p in pts) // lower
            {
                while (hull.Count >= 2 && Cross(hull[hull.Count - 2], hull[hull.Count - 1], p) <= 0) hull.RemoveAt(hull.Count - 1);
                hull.Add(p);
            }
            int lower = hull.Count + 1;
            for (int i = pts.Count - 2; i >= 0; i--) // upper
            {
                var p = pts[i];
                while (hull.Count >= lower && Cross(hull[hull.Count - 2], hull[hull.Count - 1], p) <= 0) hull.RemoveAt(hull.Count - 1);
                hull.Add(p);
            }
            hull.RemoveAt(hull.Count - 1);

            double area2 = 0;
            for (int i = 0; i < hull.Count; i++)
            {
                var a = hull[i]; var b = hull[(i + 1) % hull.Count];
                area2 += (double)a.x * b.y - (double)b.x * a.y;
            }
            return Math.Abs(area2) / 2.0;
        }

        private static List<List<int>> ConnectedComponents(bool[] mask, int w, int h)
        {
            var blobs = new List<List<int>>();
            var visited = new bool[w * h];
            var stack = new Stack<int>();
            int[] dx = { -1, 0, 1, -1, 1, -1, 0, 1 }, dy = { -1, -1, -1, 0, 0, 1, 1, 1 };
            for (int i = 0; i < mask.Length; i++)
            {
                if (!mask[i] || visited[i]) continue;
                var blob = new List<int>();
                stack.Push(i); visited[i] = true;
                while (stack.Count > 0)
                {
                    int p = stack.Pop(); blob.Add(p);
                    int px = p % w, py = p / w;
                    for (int k = 0; k < 8; k++)
                    {
                        int nx = px + dx[k], ny = py + dy[k];
                        if (nx < 0 || ny < 0 || nx >= w || ny >= h) continue;
                        int np = ny * w + nx;
                        if (mask[np] && !visited[np]) { visited[np] = true; stack.Push(np); }
                    }
                }
                blobs.Add(blob);
            }
            return blobs;
        }

        /// <summary>Decodes a PNG to an 8-bit grayscale buffer. Safe off the UI thread (operates on a frozen bitmap).</summary>
        public static (byte[] px, int w, int h) ToGray(byte[] png)
        {
            var bi = new BitmapImage();
            bi.BeginInit(); bi.CacheOption = BitmapCacheOption.OnLoad; bi.StreamSource = new MemoryStream(png); bi.EndInit(); bi.Freeze();
            var gray = new FormatConvertedBitmap(bi, PixelFormats.Gray8, null, 0);
            int w = gray.PixelWidth, h = gray.PixelHeight;
            var px = new byte[w * h];
            gray.CopyPixels(px, w, 0);
            return (px, w, h);
        }
    }
}
