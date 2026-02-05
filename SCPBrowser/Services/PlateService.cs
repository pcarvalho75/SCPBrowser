using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using SCPBrowser.Models;

namespace SCPBrowser.Services
{
    /// <summary>
    /// Service for managing plate metadata and operations
    /// </summary>
    public class PlateService : DatabaseServiceBase
    {
        public PlateService(string projectDbPath) : base(projectDbPath)
        {
        }

        /// <summary>
        /// Creates a new plate and returns the plate_id
        /// </summary>
        public async Task<int> CreatePlateAsync(PlateInfo plate)
        {
            return await WithConnectionAsync(async connection =>
            {
                int projectId = 1;
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = "SELECT project_id FROM project_info LIMIT 1";
                    var result = await command.ExecuteScalarAsync();
                    if (result != null)
                        projectId = Convert.ToInt32(result);
                }

                using (var command = connection.CreateCommand())
                {
                    command.CommandText = @"
                INSERT INTO plates (project_id, plate_name, run_date, 
                                  description, instrument_name, operator_name)
                VALUES (@projectId, @plateName, @runDate, 
                        @description, @instrument, @operator);
                SELECT last_insert_rowid();";
                    command.Parameters.AddWithValue("@projectId", projectId);
                    command.Parameters.AddWithValue("@plateName", plate.PlateName);
                    command.Parameters.AddWithValue("@runDate", plate.RunDate ?? "");
                    command.Parameters.AddWithValue("@description", plate.Description ?? "");
                    command.Parameters.AddWithValue("@instrument", plate.InstrumentName ?? "");
                    command.Parameters.AddWithValue("@operator", plate.OperatorName ?? "");

                    var result = await command.ExecuteScalarAsync();
                    return Convert.ToInt32(result);
                }
            });
        }

        /// <summary>
        /// Gets all plates in the project
        /// </summary>
        public async Task<List<PlateInfo>> GetPlatesAsync()
        {
            return await QueryAsync(@"
                SELECT p.plate_id, p.plate_name, p.run_date, 
                       p.description, p.instrument_name, p.operator_name,
                       COUNT(pi.import_id) as file_count
                FROM plates p
                LEFT JOIN parquet_imports pi ON p.plate_id = pi.plate_id
                GROUP BY p.plate_id
                ORDER BY p.plate_name",
                reader => new PlateInfo
                {
                    PlateId = reader.GetInt32(0),
                    PlateName = reader.GetString(1),
                    RunDate = reader.IsDBNull(2) ? "" : reader.GetString(2),
                    Description = reader.IsDBNull(3) ? "" : reader.GetString(3),
                    InstrumentName = reader.IsDBNull(4) ? "" : reader.GetString(4),
                    OperatorName = reader.IsDBNull(5) ? "" : reader.GetString(5),
                    FileCount = reader.GetInt32(6)
                });
        }

        /// <summary>
        /// Updates an existing plate's metadata
        /// </summary>
        public async Task UpdatePlateAsync(PlateInfo plate)
        {
            await ExecuteNonQueryAsync(@"
                UPDATE plates 
                SET plate_name = @plateName, run_date = @runDate, description = @description,
                    instrument_name = @instrument, operator_name = @operator
                WHERE plate_id = @plateId",
                cmd =>
                {
                    cmd.Parameters.AddWithValue("@plateId", plate.PlateId);
                    cmd.Parameters.AddWithValue("@plateName", plate.PlateName);
                    cmd.Parameters.AddWithValue("@runDate", plate.RunDate ?? "");
                    cmd.Parameters.AddWithValue("@description", plate.Description ?? "");
                    cmd.Parameters.AddWithValue("@instrument", plate.InstrumentName ?? "");
                    cmd.Parameters.AddWithValue("@operator", plate.OperatorName ?? "");
                });
        }
    }
}