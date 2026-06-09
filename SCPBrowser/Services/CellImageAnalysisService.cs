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
    /// Image-based doublet detection. For each isolated cell's brightfield (Trans) frame it background-subtracts
    /// the per-run background, thresholds the difference, finds connected components, and counts cell-sized blobs.
    /// The cell-size threshold is SELF-CALIBRATED per run (a fraction of the median single-cell blob area) so it
    /// adapts to instrument/magnification. blob_count &gt;= 2 ⇒ likely doublet.
    ///
    /// This catches the doublets the instrument's own gating misses: it frequently detects a single object and
    /// isolates the drop while a second cell rides along unmeasured (validated: Male_neutrophils run reports
    /// n_objects=1 for every isolated cell, yet ~40% of frames clearly contain two cells).
    ///
    /// Runnable standalone (re-analyze with a different sensitivity without re-importing).
    /// </summary>
    public class CellImageAnalysisService : DatabaseServiceBase
    {
        // Tunable (validated defaults on the Male_neutrophils run).
        public int DiffThreshold { get; set; } = 22;     // background-subtract threshold, 0-255
        public int CandidateFloor { get; set; } = 120;   // min blob area to consider (px)
        public int MaxArea { get; set; } = 6000;         // max blob area (px)
        public double MinFill { get; set; } = 0.40;      // area / bbox-area: rejects thin edge/line artifacts
        public double CoCellFraction { get; set; } = 0.6;// a second object must be >= this x the median cell to count
        public int MinThreshold { get; set; } = 200;     // floor for the calibrated co-cell threshold (px)

        public CellImageAnalysisService(string projectDbPath) : base(projectDbPath) { }

        /// <summary>Computes and stores blob_count for every cell in a run. Returns the number flagged as doublets.</summary>
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
            if (bgPng == null) return 0; // no background → can't analyze

            var (bg, bw, bh) = ToGray(bgPng);

            var items = new List<(int cellId, byte[] png)>();
            await WithConnectionAsync(async conn =>
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"SELECT ic.cell_id, ci.image_blob
                                    FROM isolated_cells ic
                                    JOIN cell_images ci ON ci.cell_id = ic.cell_id AND ci.channel = 'Trans'
                                    WHERE ic.cellenone_run_id = @r AND ci.image_blob IS NOT NULL";
                cmd.Parameters.AddWithValue("@r", cellenOneRunId);
                using var reader = await cmd.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                    items.Add((reader.GetInt32(0), (byte[])reader[1]));
            });

            // Pass 1: candidate blobs per cell.
            var perCell = new List<(int cellId, List<int> areas)>(items.Count);
            for (int i = 0; i < items.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var (g, w, h) = ToGray(items[i].png);
                perCell.Add((items[i].cellId, CandidateAreas(g, w, h, bg, bw, bh)));
                if (i % 50 == 0) progress?.Report($"Analyzing images for doublets... {i}/{items.Count}");
            }

            // Self-calibrate the typical cell size, then the co-cell threshold.
            var tops = perCell.Where(p => p.areas.Count > 0).Select(p => p.areas[0]).OrderBy(a => a).ToList();
            int median = tops.Count > 0 ? tops[tops.Count / 2] : 400;
            int eff = Math.Max(MinThreshold, (int)(CoCellFraction * median));

            // Pass 2: persist blob_count.
            int flagged = 0;
            await WithConnectionAsync(async conn =>
            {
                using var tx = conn.BeginTransaction();
                using var cmd = conn.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = "UPDATE isolated_cells SET blob_count = @n WHERE cell_id = @c";
                var pN = cmd.Parameters.Add("@n", SqliteType.Integer);
                var pC = cmd.Parameters.Add("@c", SqliteType.Integer);
                foreach (var (cellId, areas) in perCell)
                {
                    int count = areas.Count(a => a >= eff);
                    if (count >= 2) flagged++;
                    pN.Value = count; pC.Value = cellId;
                    await cmd.ExecuteNonQueryAsync();
                }
                await tx.CommitAsync();
            });

            progress?.Report($"Doublet analysis: {flagged}/{perCell.Count} flagged (cell≈{median}px, threshold {eff}px).");
            return flagged;
        }

        private List<int> CandidateAreas(byte[] fr, int w, int h, byte[] bg, int bw, int bh)
        {
            int W = Math.Min(w, bw), H = Math.Min(h, bh);
            var mask = new bool[w * h];
            for (int y = 0; y < H; y++)
                for (int x = 0; x < W; x++)
                    if (Math.Abs(fr[y * w + x] - bg[y * bw + x]) > DiffThreshold)
                        mask[y * w + x] = true;
            return ConnectedComponents(mask, w, h)
                .Where(b => b.Area >= CandidateFloor && b.Area <= MaxArea && b.Fill >= MinFill)
                .Select(b => b.Area)
                .OrderByDescending(a => a)
                .ToList();
        }

        private class Blob
        {
            public int Area, MinX, MinY, MaxX, MaxY;
            public double Fill => (double)Area / Math.Max(1, (MaxX - MinX + 1) * (MaxY - MinY + 1));
        }

        private static List<Blob> ConnectedComponents(bool[] mask, int w, int h)
        {
            var blobs = new List<Blob>();
            var visited = new bool[w * h];
            var stack = new Stack<int>();
            int[] dx = { -1, 0, 1, -1, 1, -1, 0, 1 }, dy = { -1, -1, -1, 0, 0, 1, 1, 1 };
            for (int i = 0; i < mask.Length; i++)
            {
                if (!mask[i] || visited[i]) continue;
                var b = new Blob { MinX = int.MaxValue, MinY = int.MaxValue, MaxX = int.MinValue, MaxY = int.MinValue };
                stack.Push(i); visited[i] = true;
                while (stack.Count > 0)
                {
                    int p = stack.Pop(); int px = p % w, py = p / w;
                    b.Area++;
                    if (px < b.MinX) b.MinX = px; if (px > b.MaxX) b.MaxX = px;
                    if (py < b.MinY) b.MinY = py; if (py > b.MaxY) b.MaxY = py;
                    for (int k = 0; k < 8; k++)
                    {
                        int nx = px + dx[k], ny = py + dy[k];
                        if (nx < 0 || ny < 0 || nx >= w || ny >= h) continue;
                        int np = ny * w + nx;
                        if (mask[np] && !visited[np]) { visited[np] = true; stack.Push(np); }
                    }
                }
                blobs.Add(b);
            }
            return blobs;
        }

        private static (byte[] px, int w, int h) ToGray(byte[] png)
        {
            var bi = new BitmapImage();
            bi.BeginInit(); bi.CacheOption = BitmapCacheOption.OnLoad; bi.StreamSource = new MemoryStream(png); bi.EndInit(); bi.Freeze();
            var g = new FormatConvertedBitmap(bi, PixelFormats.Gray8, null, 0);
            int w = g.PixelWidth, h = g.PixelHeight;
            var px = new byte[w * h];
            g.CopyPixels(px, w, 0);
            return (px, w, h);
        }
    }
}
