using System;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Data.Sqlite;
using SCPBrowser.Models;

namespace SCPBrowser.Services
{
    /// <summary>
    /// Persists a parsed <see cref="CellenOneRun"/> into the project database: the run row (with a single deduped
    /// background image), one isolated_cells row per cell, and per-channel cell_images blobs + thumbnails.
    /// All writes happen in a single transaction. Re-importing the same run is idempotent (matched by file hash).
    ///
    /// The DIA-NN link (isolated_cells.raw_file_id) is left NULL here - it is populated later by the reconcile step.
    /// </summary>
    public class CellenOneImportService : DatabaseServiceBase
    {
        private const int ThumbMaxDim = 256;

        public CellenOneImportService(string projectDbPath) : base(projectDbPath)
        {
        }

        /// <summary>
        /// Parses the run folder and imports it against the given plate. Returns the cellenone_run_id
        /// (existing id if this run was already imported for this plate).
        /// </summary>
        public async Task<int> ImportRunAsync(string runDir, int plateId, IProgress<string>? progress = null, CancellationToken ct = default)
        {
            progress?.Report("Parsing cellenONE run...");
            var run = CellenOneParser.ParseRun(runDir);
            run.PlateId = plateId;
            run.FileHash = ComputeCanonicalHash(runDir);
            return await ImportRunAsync(run, plateId, progress, ct);
        }

        /// <summary>
        /// Imports a pre-parsed run against the given plate (use the string overload unless you parsed for a preview first).
        /// </summary>
        public async Task<int> ImportRunAsync(CellenOneRun run, int plateId, IProgress<string>? progress = null, CancellationToken ct = default)
        {
            // Tolerate databases created before this feature.
            await new ProjectDatabaseService(_projectDbPath).EnsureCellenOneTablesExistAsync();

            run.PlateId = plateId;
            run.FileHash ??= run.SourceDir != null ? ComputeCanonicalHash(run.SourceDir) : null;

            // Idempotency: skip if this exact run is already imported for this plate.
            if (!string.IsNullOrEmpty(run.FileHash))
            {
                var existing = await ExecuteScalarAsync<long>(
                    "SELECT cellenone_run_id FROM cellenone_runs WHERE plate_id = @p AND file_hash = @h LIMIT 1",
                    cmd => { cmd.Parameters.AddWithValue("@p", plateId); cmd.Parameters.AddWithValue("@h", run.FileHash!); },
                    defaultValue: 0);
                if (existing > 0)
                {
                    progress?.Report("Run already imported for this plate - skipping.");
                    return (int)existing;
                }
            }

            byte[]? background = ReadFileOrNull(run.BackgroundImagePath);

            int newRunId = await WithConnectionAsync(async connection =>
            {
                using var transaction = connection.BeginTransaction();
                try
                {
                    int runId = await InsertRunAsync(connection, transaction, run, background);

                    int n = run.Cells.Count;
                    for (int i = 0; i < n; i++)
                    {
                        ct.ThrowIfCancellationRequested();
                        var cell = run.Cells[i];

                        int cellId = await InsertCellAsync(connection, transaction, runId, plateId, cell);
                        await InsertImageAsync(connection, transaction, cellId, "Trans", cell.TransImagePath, cell.ImageX, cell.ImageY, cell.Diameter);
                        await InsertImageAsync(connection, transaction, cellId, "Blue", cell.BlueImagePath, cell.ImageX, cell.ImageY, cell.Diameter);

                        if (i % 20 == 0 || i == n - 1)
                            progress?.Report($"Importing cells... {i + 1}/{n}");
                    }

                    await InsertDetectionsAsync(connection, transaction, runId, run.Detections, ct);
                    await transaction.CommitAsync(ct);
                    progress?.Report($"Imported {n} cells.");
                    return runId;
                }
                catch
                {
                    try { await transaction.RollbackAsync(); } catch { /* connection may be unusable */ }
                    throw;
                }
            });

            // Image-based doublet detection (best-effort; never fails the import).
            try
            {
                await new CellImageAnalysisService(_projectDbPath).AnalyzeRunAsync(newRunId, progress, ct);
            }
            catch (OperationCanceledException) { throw; }
            catch { /* non-critical */ }

            // CellProfiler-style phenotype features (best-effort; never fails the import).
            try
            {
                await new CellPhenotypeService(_projectDbPath).AnalyzeRunAsync(newRunId, progress, ct);
            }
            catch (OperationCanceledException) { throw; }
            catch { /* non-critical */ }

            return newRunId;
        }

        private static async Task<int> InsertRunAsync(SqliteConnection connection, SqliteTransaction transaction, CellenOneRun run, byte[]? background)
        {
            using var cmd = connection.CreateCommand();
            cmd.Transaction = transaction;
            cmd.CommandText = @"
                INSERT INTO cellenone_runs
                    (plate_id, run_uid, run_type, instrument, sw_version, operator_name, run_date, humidity,
                     total_detected, volume_dispensed_nl, concentration, channels, target_labware,
                     field_size_x, field_size_y, dot_pitch, ejection_bound, sedimentation_bound, id_template, source_dir, gating_params_json,
                     background_image, logfile_text, file_hash, imported_at)
                VALUES
                    (@plate_id, @run_uid, @run_type, @instrument, @sw_version, @operator_name, @run_date, @humidity,
                     @total_detected, @volume_dispensed_nl, @concentration, @channels, @target_labware,
                     @field_size_x, @field_size_y, @dot_pitch, @ejection_bound, @sedimentation_bound, @id_template, @source_dir, @gating_params_json,
                     @background_image, @logfile_text, @file_hash, @imported_at);
                SELECT last_insert_rowid();";

            AddParam(cmd, "@plate_id", run.PlateId);
            AddParam(cmd, "@run_uid", run.RunUid);
            AddParam(cmd, "@run_type", run.RunType);
            AddParam(cmd, "@instrument", run.Instrument);
            AddParam(cmd, "@sw_version", run.SoftwareVersion);
            AddParam(cmd, "@operator_name", run.OperatorName);
            AddParam(cmd, "@run_date", run.RunDate);
            AddParam(cmd, "@humidity", run.Humidity);
            AddParam(cmd, "@total_detected", run.TotalDetected);
            AddParam(cmd, "@volume_dispensed_nl", run.VolumeDispensedNl);
            AddParam(cmd, "@concentration", run.Concentration);
            AddParam(cmd, "@channels", run.Channels);
            AddParam(cmd, "@target_labware", run.TargetLabware);
            AddParam(cmd, "@field_size_x", run.FieldSizeX);
            AddParam(cmd, "@field_size_y", run.FieldSizeY);
            AddParam(cmd, "@dot_pitch", run.DotPitch);
            AddParam(cmd, "@ejection_bound", run.EjectionBound);
            AddParam(cmd, "@sedimentation_bound", run.SedimentationBound);
            AddParam(cmd, "@id_template", run.IdTemplate);
            AddParam(cmd, "@source_dir", run.SourceDir);
            AddParam(cmd, "@gating_params_json", run.GatingParamsJson);
            AddParam(cmd, "@background_image", background);
            AddParam(cmd, "@logfile_text", run.LogfileText);
            AddParam(cmd, "@file_hash", run.FileHash);
            AddParam(cmd, "@imported_at", DateTime.Now.ToString("o"));

            var result = await cmd.ExecuteScalarAsync();
            return Convert.ToInt32(result);
        }

        private static async Task<int> InsertCellAsync(SqliteConnection connection, SqliteTransaction transaction, int runId, int plateId, IsolatedCell cell)
        {
            using var cmd = connection.CreateCommand();
            cmd.Transaction = transaction;
            cmd.CommandText = @"
                INSERT INTO isolated_cells
                    (cellenone_run_id, plate_id, drop_no, target_well, target, field, x_pos, y_pos, image_x, image_y, n_objects,
                     diameter, elongation, circularity, intensity, flu_diameter, flu_intensity, status, isolated_at)
                VALUES
                    (@run_id, @plate_id, @drop_no, @target_well, @target, @field, @x_pos, @y_pos, @image_x, @image_y, @n_objects,
                     @diameter, @elongation, @circularity, @intensity, @flu_diameter, @flu_intensity, @status, @isolated_at);
                SELECT last_insert_rowid();";

            AddParam(cmd, "@run_id", runId);
            AddParam(cmd, "@plate_id", plateId);
            AddParam(cmd, "@drop_no", cell.DropNo);
            AddParam(cmd, "@target_well", cell.TargetWell);
            AddParam(cmd, "@target", cell.Target);
            AddParam(cmd, "@field", cell.Field);
            AddParam(cmd, "@x_pos", cell.XPos);
            AddParam(cmd, "@y_pos", cell.YPos);
            AddParam(cmd, "@image_x", cell.ImageX);
            AddParam(cmd, "@image_y", cell.ImageY);
            AddParam(cmd, "@n_objects", cell.NObjects);
            AddParam(cmd, "@diameter", cell.Diameter);
            AddParam(cmd, "@elongation", cell.Elongation);
            AddParam(cmd, "@circularity", cell.Circularity);
            AddParam(cmd, "@intensity", cell.Intensity);
            AddParam(cmd, "@flu_diameter", cell.FluDiameter);
            AddParam(cmd, "@flu_intensity", cell.FluIntensity);
            AddParam(cmd, "@status", cell.Status);
            AddParam(cmd, "@isolated_at", cell.IsolatedAt);

            var result = await cmd.ExecuteScalarAsync();
            return Convert.ToInt32(result);
        }

        private static async Task InsertImageAsync(SqliteConnection connection, SqliteTransaction transaction, int cellId, string channel, string? imagePath, double? cx, double? cy, double? diameter)
        {
            var full = ReadFileOrNull(imagePath);
            if (full == null) return; // no image for this channel - skip the row

            byte[]? thumb = null;
            int width = 0, height = 0;
            try
            {
                (thumb, width, height) = MakeThumbnail(full, ThumbMaxDim, cx, cy, diameter);
            }
            catch
            {
                // Decoding failure must not abort the import; store the full blob without a thumbnail.
                thumb = null;
            }

            using var cmd = connection.CreateCommand();
            cmd.Transaction = transaction;
            cmd.CommandText = @"
                INSERT INTO cell_images (cell_id, channel, image_blob, thumb_blob, format, width, height)
                VALUES (@cell_id, @channel, @image_blob, @thumb_blob, @format, @width, @height);";
            AddParam(cmd, "@cell_id", cellId);
            AddParam(cmd, "@channel", channel);
            AddParam(cmd, "@image_blob", full);
            AddParam(cmd, "@thumb_blob", thumb);
            AddParam(cmd, "@format", "png");
            AddParam(cmd, "@width", width > 0 ? width : (int?)null);
            AddParam(cmd, "@height", height > 0 ? height : (int?)null);
            await cmd.ExecuteNonQueryAsync();
        }

        private static async Task InsertDetectionsAsync(SqliteConnection connection, SqliteTransaction transaction, int runId, System.Collections.Generic.List<CellDetection> detections, CancellationToken ct)
        {
            if (detections.Count == 0) return;
            using var cmd = connection.CreateCommand();
            cmd.Transaction = transaction;
            cmd.CommandText = @"
                INSERT INTO cell_detections (cellenone_run_id, drop_no, channel, object_index, x, y, diameter, elongation, circularity, intensity)
                VALUES (@run, @drop, @ch, @idx, @x, @y, @dia, @elong, @circ, @int)";
            var pRun = cmd.Parameters.Add("@run", SqliteType.Integer);
            var pDrop = cmd.Parameters.Add("@drop", SqliteType.Integer);
            var pCh = cmd.Parameters.Add("@ch", SqliteType.Text);
            var pIdx = cmd.Parameters.Add("@idx", SqliteType.Integer);
            var pX = cmd.Parameters.Add("@x", SqliteType.Real);
            var pY = cmd.Parameters.Add("@y", SqliteType.Real);
            var pDia = cmd.Parameters.Add("@dia", SqliteType.Real);
            var pElong = cmd.Parameters.Add("@elong", SqliteType.Real);
            var pCirc = cmd.Parameters.Add("@circ", SqliteType.Real);
            var pInt = cmd.Parameters.Add("@int", SqliteType.Real);

            foreach (var d in detections)
            {
                ct.ThrowIfCancellationRequested();
                pRun.Value = runId;
                pDrop.Value = d.DropNo;
                pCh.Value = (object?)d.Channel ?? DBNull.Value;
                pIdx.Value = d.ObjectIndex;
                pX.Value = (object?)d.X ?? DBNull.Value;
                pY.Value = (object?)d.Y ?? DBNull.Value;
                pDia.Value = (object?)d.Diameter ?? DBNull.Value;
                pElong.Value = (object?)d.Elongation ?? DBNull.Value;
                pCirc.Value = (object?)d.Circularity ?? DBNull.Value;
                pInt.Value = (object?)d.Intensity ?? DBNull.Value;
                await cmd.ExecuteNonQueryAsync();
            }
        }

        // ----------------------------------------------------------------- helpers

        private static void AddParam(SqliteCommand cmd, string name, object? value)
            => cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);

        private static byte[]? ReadFileOrNull(string? path)
            => !string.IsNullOrEmpty(path) && File.Exists(path) ? File.ReadAllBytes(path) : null;

        /// <summary>SHA256 of the canonical run file (isolated.xls for isolation runs, logfile otherwise), as lowercase hex.</summary>
        private static string? ComputeCanonicalHash(string runDir)
        {
            var path = FindCanonicalFile(runDir);
            if (path == null) return null;
            using var sha = SHA256.Create();
            using var stream = File.OpenRead(path);
            return Convert.ToHexString(sha.ComputeHash(stream)).ToLowerInvariant();
        }

        private static string? FindCanonicalFile(string runDir)
        {
            foreach (var f in Directory.GetFiles(runDir, "*_isolated.xls", SearchOption.TopDirectoryOnly))
                if (!Path.GetFileName(f).StartsWith("Reordered", StringComparison.OrdinalIgnoreCase))
                    return f;
            var any = Directory.GetFiles(runDir, "*_isolated.xls", SearchOption.TopDirectoryOnly);
            if (any.Length > 0) return any[0];
            var log = Directory.GetFiles(runDir, "*_Logfile.log", SearchOption.TopDirectoryOnly);
            return log.Length > 0 ? log[0] : null;
        }

        /// <summary>Decodes a PNG and returns a downscaled PNG thumbnail (or the original bytes if already small) plus original dimensions. Safe to call off the UI thread (result is frozen).</summary>
        /// <summary>
        /// Builds a thumbnail. If the detected cell position (cx, cy) is known, crops a square centered on the cell
        /// (~6x its diameter) so the grid frames the cell rather than the whole nozzle frame; otherwise thumbnails
        /// the full frame. Safe off the UI thread (operates on frozen bitmaps).
        /// </summary>
        private static (byte[] thumb, int width, int height) MakeThumbnail(byte[] src, int maxDim, double? cx = null, double? cy = null, double? diameter = null)
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.StreamSource = new MemoryStream(src);
            bmp.EndInit();
            bmp.Freeze();

            int w = bmp.PixelWidth, h = bmp.PixelHeight;
            BitmapSource source = bmp;
            bool cropped = false;

            if (cx.HasValue && cy.HasValue && w > 0 && h > 0)
            {
                double dia = diameter.GetValueOrDefault(12);
                int side = (int)Math.Clamp(dia * 8.0, 80, Math.Min(w, h));
                int x0 = (int)Math.Clamp(cx.Value - side / 2.0, 0, Math.Max(0, w - side));
                int y0 = (int)Math.Clamp(cy.Value - side / 2.0, 0, Math.Max(0, h - side));
                if (side >= 8 && x0 + side <= w && y0 + side <= h)
                {
                    var crop = new CroppedBitmap(bmp, new System.Windows.Int32Rect(x0, y0, side, side));
                    crop.Freeze();
                    source = crop;
                    w = side; h = side;
                    cropped = true;
                }
            }

            double scale = (double)maxDim / Math.Max(w, h);
            if (!cropped && scale >= 1.0)
                return (src, w, h); // uncropped and already small enough - keep original bytes

            BitmapSource output = source;
            if (scale < 1.0)
            {
                var t = new TransformedBitmap(source, new ScaleTransform(scale, scale));
                t.Freeze();
                output = t;
            }

            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(output));
            using var ms = new MemoryStream();
            encoder.Save(ms);
            return (ms.ToArray(), w, h);
        }
    }
}
