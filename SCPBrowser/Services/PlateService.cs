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
    public class PlateService
    {
        private readonly string _projectDbPath;

        public PlateService(string projectDbPath)
        {
            _projectDbPath = projectDbPath;
        }

        /// <summary>
        /// Creates a new plate and returns the plate_id
        /// </summary>
        public async Task<int> CreatePlateAsync(PlateInfo plate)
        {
            var connectionString = $"Data Source={_projectDbPath}";

            using (var connection = new SqliteConnection(connectionString))
            {
                await connection.OpenAsync();

                // Get project_id (assuming single project per database)
                int projectId = 1;
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = "SELECT project_id FROM project_info LIMIT 1";
                    var result = await command.ExecuteScalarAsync();
                    if (result != null)
                    {
                        projectId = Convert.ToInt32(result);
                    }
                }

                // Insert plate (NO biological_condition or batch_number - those are at raw file level)
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = @"
                INSERT INTO plates (project_id, plate_name, run_date, 
                                  description, instrument_name, operator_name)
                VALUES (@projectId, @plateName, @runDate, 
                        @description, @instrument, @operator);
                SELECT last_insert_rowid();
            ";
                    command.Parameters.AddWithValue("@projectId", projectId);
                    command.Parameters.AddWithValue("@plateName", plate.PlateName);
                    command.Parameters.AddWithValue("@runDate", plate.RunDate ?? "");
                    command.Parameters.AddWithValue("@description", plate.Description ?? "");
                    command.Parameters.AddWithValue("@instrument", plate.InstrumentName ?? "");
                    command.Parameters.AddWithValue("@operator", plate.OperatorName ?? "");

                    var result = await command.ExecuteScalarAsync();
                    return Convert.ToInt32(result);
                }
            }
        }

        /// <summary>
        /// Gets all plates in the project
        /// </summary>
        public async Task<List<PlateInfo>> GetPlatesAsync()
        {
            var plates = new List<PlateInfo>();
            var connectionString = $"Data Source={_projectDbPath}";

            using (var connection = new SqliteConnection(connectionString))
            {
                await connection.OpenAsync();

                using (var command = connection.CreateCommand())
                {
                    command.CommandText = @"
                SELECT p.plate_id, p.plate_name, p.run_date, 
                       p.description, p.instrument_name, p.operator_name,
                       COUNT(pi.import_id) as file_count
                FROM plates p
                LEFT JOIN parquet_imports pi ON p.plate_id = pi.plate_id
                GROUP BY p.plate_id
                ORDER BY p.plate_name
            ";

                    using (var reader = await command.ExecuteReaderAsync())
                    {
                        while (await reader.ReadAsync())
                        {
                            plates.Add(new PlateInfo
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
                    }
                }
            }

            return plates;
        }

        /// <summary>
        /// Updates an existing plate's metadata
        /// </summary>
        public async Task UpdatePlateAsync(PlateInfo plate)
        {
            var connectionString = $"Data Source={_projectDbPath}";

            using (var connection = new SqliteConnection(connectionString))
            {
                await connection.OpenAsync();

                using (var command = connection.CreateCommand())
                {
                    command.CommandText = @"
                UPDATE plates 
                SET plate_name = @plateName,
                    run_date = @runDate,
                    description = @description,
                    instrument_name = @instrument,
                    operator_name = @operator
                WHERE plate_id = @plateId
            ";
                    command.Parameters.AddWithValue("@plateId", plate.PlateId);
                    command.Parameters.AddWithValue("@plateName", plate.PlateName);
                    command.Parameters.AddWithValue("@runDate", plate.RunDate ?? "");
                    command.Parameters.AddWithValue("@description", plate.Description ?? "");
                    command.Parameters.AddWithValue("@instrument", plate.InstrumentName ?? "");
                    command.Parameters.AddWithValue("@operator", plate.OperatorName ?? "");

                    await command.ExecuteNonQueryAsync();
                }
            }
        }
    }
}