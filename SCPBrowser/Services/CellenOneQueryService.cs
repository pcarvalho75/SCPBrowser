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
                    CellCount = r.GetInt32(5)
                });
        }

        /// <summary>All isolated cells for a run (no image blobs). raw_file_id/link fields are included.</summary>
        public Task<List<IsolatedCell>> GetCellsAsync(int cellenOneRunId)
        {
            return QueryAsync(@"
                SELECT cell_id, cellenone_run_id, plate_id, drop_no, target_well, target, field, x_pos, y_pos, image_x, image_y,
                       diameter, elongation, circularity, intensity, flu_diameter, flu_intensity,
                       status, isolated_at, raw_file_id, link_method, link_confidence, n_objects, blob_count
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
                    BlobCount = IntN(r, 23)
                },
                cmd => cmd.Parameters.AddWithValue("@r", cellenOneRunId));
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
