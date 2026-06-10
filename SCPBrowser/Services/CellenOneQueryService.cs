using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using SCPBrowser.Models;

namespace SCPBrowser.Services
{
    /// <summary>
    /// Read-side access to imported cellenONE data for the plate/cell viewer and the reconcile UI.
    /// Cell lists and thumbnails are loaded eagerly; full-resolution images are fetched one cell at a time
    /// (the heavy image_blob is never selected in list/grid queries).
    /// </summary>
    public class CellenOneQueryService : DatabaseServiceBase
    {
        public CellenOneQueryService(string projectDbPath) : base(projectDbPath)
        {
        }

        /// <summary>Imported runs that actually have isolated cells, newest first.</summary>
        public Task<List<CellRunSummary>> GetRunsWithCellsAsync()
        {
            return QueryAsync(@"
                SELECT r.cellenone_run_id, r.plate_id, p.plate_name, r.run_uid, r.run_date,
                       r.ejection_bound, r.sedimentation_bound,
                       COUNT(ic.cell_id) AS cell_count
                FROM cellenone_runs r
                LEFT JOIN plates p ON p.plate_id = r.plate_id
                LEFT JOIN isolated_cells ic ON ic.cellenone_run_id = r.cellenone_run_id
                GROUP BY r.cellenone_run_id
                HAVING cell_count > 0
                ORDER BY r.run_date DESC, r.cellenone_run_id DESC",
                r => new CellRunSummary
                {
                    CellenOneRunId = r.GetInt32(0),
                    PlateId = r.GetInt32(1),
                    PlateName = StrN(r, 2),
                    RunUid = StrN(r, 3),
                    RunDate = StrN(r, 4),
                    EjectionBound = IntN(r, 5),
                    SedimentationBound = IntN(r, 6),
                    CellCount = r.GetInt32(7)
                });
        }

        /// <summary>All isolated cells for a run (no image blobs). raw_file_id/link fields are included.</summary>
        public Task<List<IsolatedCell>> GetCellsAsync(int cellenOneRunId)
        {
            return QueryAsync(@"
                SELECT cell_id, cellenone_run_id, plate_id, drop_no, target_well, target, field, x_pos, y_pos, image_x, image_y,
                       diameter, elongation, circularity, intensity, flu_diameter, flu_intensity,
                       status, isolated_at, raw_file_id, link_method, link_confidence, n_objects, blob_count,
                       review_status, review_note
                FROM isolated_cells
                WHERE cellenone_run_id = @r
                ORDER BY drop_no",
                r => new IsolatedCell
                {
                    CellId = r.GetInt32(0),
                    CellenOneRunId = r.GetInt32(1),
                    PlateId = r.GetInt32(2),
                    DropNo = r.GetInt32(3),
                    TargetWell = StrN(r, 4),
                    Target = IntN(r, 5),
                    Field = IntN(r, 6),
                    XPos = IntN(r, 7),
                    YPos = IntN(r, 8),
                    ImageX = DblN(r, 9),
                    ImageY = DblN(r, 10),
                    Diameter = DblN(r, 11),
                    Elongation = DblN(r, 12),
                    Circularity = DblN(r, 13),
                    Intensity = DblN(r, 14),
                    FluDiameter = DblN(r, 15),
                    FluIntensity = DblN(r, 16),
                    Status = StrN(r, 17),
                    IsolatedAt = StrN(r, 18),
                    RawFileId = IntN(r, 19),
                    LinkMethod = StrN(r, 20),
                    LinkConfidence = DblN(r, 21),
                    NObjects = IntN(r, 22),
                    BlobCount = IntN(r, 23),
                    ReviewStatus = StrN(r, 24),
                    ReviewNote = StrN(r, 25)
                },
                cmd => cmd.Parameters.AddWithValue("@r", cellenOneRunId));
        }

        /// <summary>Persists user-edited (or re-parsed) ejection/sedimentation zone boundaries for a run.</summary>
        public Task SetRunBoundsAsync(int cellenOneRunId, int? ejection, int? sedimentation)
            => ExecuteNonQueryAsync(
                "UPDATE cellenone_runs SET ejection_bound = @e, sedimentation_bound = @s WHERE cellenone_run_id = @r",
                cmd =>
                {
                    cmd.Parameters.AddWithValue("@e", (object?)ejection ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@s", (object?)sedimentation ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@r", cellenOneRunId);
                });

        /// <summary>CellProfiler-style phenotype features (feature name → value) for one cell, computed by CellPhenotypeService. Empty until a run is profiled.</summary>
        public Task<List<(string Feature, double Value)>> GetCellFeaturesAsync(int cellId)
            => QueryAsync(
                "SELECT feature, value FROM cell_features WHERE cell_id = @c ORDER BY feature",
                r => (r.GetString(0), r.IsDBNull(1) ? double.NaN : r.GetDouble(1)),
                cmd => cmd.Parameters.AddWithValue("@c", cellId));

        /// <summary>Clears the image-analysis doublet flags (blob_count) for a whole run, so a reanalysis starts from a clean slate. Instrument n_objects and manual review decisions are left untouched.</summary>
        public Task ResetDoubletFlagsAsync(int cellenOneRunId)
            => ExecuteNonQueryAsync(
                "UPDATE isolated_cells SET blob_count = NULL WHERE cellenone_run_id = @r",
                cmd => cmd.Parameters.AddWithValue("@r", cellenOneRunId));

        /// <summary>Persists a cell's QC review disposition (null = unreviewed / "flag" / "keep" / "discard") and an optional note. Non-destructive — records the decision only.</summary>
        public Task SetReviewStatusAsync(int cellId, string? status, string? note)
        {
            return ExecuteNonQueryAsync(
                "UPDATE isolated_cells SET review_status = @s, review_note = @n WHERE cell_id = @c",
                cmd =>
                {
                    cmd.Parameters.AddWithValue("@s", (object?)status ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@n", (object?)note ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@c", cellId);
                });
        }

        /// <summary>cell_id → thumbnail bytes for one channel, for fast grid rendering.</summary>
        public Task<Dictionary<int, byte[]>> GetThumbnailsAsync(int cellenOneRunId, string channel)
        {
            return WithConnectionAsync(async conn =>
            {
                var map = new Dictionary<int, byte[]>();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"
                    SELECT ic.cell_id, ci.thumb_blob
                    FROM isolated_cells ic
                    JOIN cell_images ci ON ci.cell_id = ic.cell_id AND ci.channel = @ch
                    WHERE ic.cellenone_run_id = @r AND ci.thumb_blob IS NOT NULL";
                cmd.Parameters.AddWithValue("@r", cellenOneRunId);
                cmd.Parameters.AddWithValue("@ch", channel);
                using var reader = await cmd.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    if (!reader.IsDBNull(1))
                        map[reader.GetInt32(0)] = (byte[])reader[1];
                }
                return map;
            });
        }

        /// <summary>Full-resolution image bytes for one cell+channel ('Trans' | 'Blue'), or null.</summary>
        public Task<byte[]?> GetCellImageAsync(int cellId, string channel)
        {
            return WithConnectionAsync<byte[]?>(async conn =>
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT image_blob FROM cell_images WHERE cell_id = @c AND channel = @ch LIMIT 1";
                cmd.Parameters.AddWithValue("@c", cellId);
                cmd.Parameters.AddWithValue("@ch", channel);
                var result = await cmd.ExecuteScalarAsync();
                return result == null || result == DBNull.Value ? null : (byte[])result;
            });
        }

        /// <summary>DIA-NN raw files belonging to a plate (reconcile targets / link display).</summary>
        public Task<List<RawFileRef>> GetRawFilesForPlateAsync(int plateId)
        {
            return QueryAsync(
                "SELECT raw_file_id, raw_file_name FROM raw_files WHERE plate_id = @p ORDER BY raw_file_name",
                r => new RawFileRef { RawFileId = r.GetInt32(0), RawFileName = StrN(r, 1) },
                cmd => cmd.Parameters.AddWithValue("@p", plateId));
        }

        // ---- nullable readers ----
        private static int? IntN(SqliteDataReader r, int i) => r.IsDBNull(i) ? (int?)null : r.GetInt32(i);
        private static double? DblN(SqliteDataReader r, int i) => r.IsDBNull(i) ? (double?)null : r.GetDouble(i);
        private static string? StrN(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);
    }
}
