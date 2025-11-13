using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using SCPBrowser.Models;

namespace SCPBrowser.Services
{
    /// <summary>
    /// Service for managing project-level data including plates, parquet imports, and raw files
    /// </summary>
    public class ProjectDataService
    {
        private readonly string _projectDbPath;

        public ProjectDataService(string projectDbPath)
        {
            _projectDbPath = projectDbPath;
        }

        /// <summary>
        /// Creates a new project database with all necessary tables
        /// </summary>
        public async Task CreateProjectAsync(string projectName, string description)
        {
            var connectionString = $"Data Source={_projectDbPath}";

            using (var connection = new SqliteConnection(connectionString))
            {
                await connection.OpenAsync();
                await CreateProjectSchemaAsync(connection);

                // Insert initial project info
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = @"
                        INSERT INTO project_info (project_name, created_date, last_modified, description)
                        VALUES (@name, @created, @modified, @description)
                    ";
                    command.Parameters.AddWithValue("@name", projectName);
                    command.Parameters.AddWithValue("@created", DateTime.Now.ToString("o"));
                    command.Parameters.AddWithValue("@modified", DateTime.Now.ToString("o"));
                    command.Parameters.AddWithValue("@description", description ?? "");
                    await command.ExecuteNonQueryAsync();
                }
            }
        }

        /// <summary>
        /// Creates the project database schema
        /// </summary>
        /// <summary>
        /// Creates the project database schema
        /// </summary>
        private async Task CreateProjectSchemaAsync(SqliteConnection connection)
        {
            using (var command = connection.CreateCommand())
            {
                command.CommandText = @"
            -- ==================== PROJECT TABLES ====================
            
            -- Project metadata
            CREATE TABLE IF NOT EXISTS project_info (
                project_id INTEGER PRIMARY KEY AUTOINCREMENT,
                project_name TEXT NOT NULL,
                created_date TEXT NOT NULL,
                last_modified TEXT NOT NULL,
                description TEXT
            );

            -- Plates (technical metadata only - NO biological condition or batch)
            CREATE TABLE IF NOT EXISTS plates (
                plate_id INTEGER PRIMARY KEY AUTOINCREMENT,
                project_id INTEGER NOT NULL,
                plate_name TEXT NOT NULL,
                run_date TEXT,
                instrument_name TEXT,
                operator_name TEXT,
                description TEXT,
                FOREIGN KEY (project_id) REFERENCES project_info(project_id)
            );

            -- Parquet file imports
            CREATE TABLE IF NOT EXISTS parquet_imports (
                import_id INTEGER PRIMARY KEY AUTOINCREMENT,
                plate_id INTEGER NOT NULL,
                file_name TEXT NOT NULL,
                file_hash TEXT NOT NULL,
                import_timestamp TEXT NOT NULL,
                row_count INTEGER NOT NULL,
                protein_count INTEGER NOT NULL,
                cell_count INTEGER NOT NULL,
                column_mapping TEXT NOT NULL,
                FOREIGN KEY (plate_id) REFERENCES plates(plate_id),
                UNIQUE(file_name)
            );

            -- Raw files (biological condition stored HERE at raw file level)
            CREATE TABLE IF NOT EXISTS raw_files (
                raw_file_id INTEGER PRIMARY KEY AUTOINCREMENT,
                import_id INTEGER NOT NULL,
                raw_file_name TEXT NOT NULL,
                biological_condition TEXT,
                plate_id INTEGER,
                protein_count INTEGER,
                peptide_count INTEGER,
                total_ion_current REAL,
                FOREIGN KEY (import_id) REFERENCES parquet_imports(import_id),
                FOREIGN KEY (plate_id) REFERENCES plates(plate_id)
            );

            -- Protein quantification summary
            CREATE TABLE IF NOT EXISTS protein_quant_summary (
                protein_id TEXT NOT NULL,
                raw_file_id INTEGER NOT NULL,
                median_intensity REAL,
                mean_intensity REAL,
                detection_count INTEGER,
                PRIMARY KEY (protein_id, raw_file_id),
                FOREIGN KEY (raw_file_id) REFERENCES raw_files(raw_file_id)
            );

            -- ==================== REFERENCE DATA TABLES ====================
            
            -- Cell type profiles (transcriptomic reference data)
            CREATE TABLE IF NOT EXISTS cell_type_profiles (
                cell_type TEXT NOT NULL,
                gene_name TEXT NOT NULL,
                median_expression REAL NOT NULL,
                mean_expression REAL NOT NULL,
                percent_expressing REAL NOT NULL,
                PRIMARY KEY (cell_type, gene_name)
            ) WITHOUT ROWID;

            -- Cell type metadata
            CREATE TABLE IF NOT EXISTS cell_type_metadata (
                cell_type TEXT PRIMARY KEY,
                cell_count INTEGER NOT NULL,
                genes_expressed INTEGER NOT NULL,
                age_range TEXT,
                batch_info TEXT
            );

            -- GO terms (GO Slim)
            CREATE TABLE IF NOT EXISTS go_terms (
                go_id TEXT PRIMARY KEY,
                name TEXT NOT NULL,
                namespace TEXT NOT NULL,
                definition TEXT
            );

            -- Protein to GO term annotations
            CREATE TABLE IF NOT EXISTS protein_go_annotations (
                protein_id TEXT NOT NULL,
                go_term_id TEXT NOT NULL,
                PRIMARY KEY (protein_id, go_term_id)
            );

            -- ==================== INDICES ====================
            
            -- Project data indices
            CREATE INDEX IF NOT EXISTS idx_parquet_imports_plate ON parquet_imports(plate_id);
            CREATE INDEX IF NOT EXISTS idx_parquet_imports_filename ON parquet_imports(file_name);
            CREATE INDEX IF NOT EXISTS idx_raw_files_import ON raw_files(import_id);
            CREATE INDEX IF NOT EXISTS idx_raw_files_plate ON raw_files(plate_id);
            CREATE INDEX IF NOT EXISTS idx_raw_files_condition ON raw_files(biological_condition);
            CREATE INDEX IF NOT EXISTS idx_protein_quant_protein ON protein_quant_summary(protein_id);
            CREATE INDEX IF NOT EXISTS idx_protein_quant_rawfile ON protein_quant_summary(raw_file_id);
            
            -- Reference data indices
            CREATE INDEX IF NOT EXISTS idx_cell_type_profiles_cell_type ON cell_type_profiles(cell_type);
            CREATE INDEX IF NOT EXISTS idx_cell_type_profiles_gene ON cell_type_profiles(gene_name);
            CREATE INDEX IF NOT EXISTS idx_protein_go ON protein_go_annotations(protein_id);
            CREATE INDEX IF NOT EXISTS idx_go_protein ON protein_go_annotations(go_term_id);
        ";
                await command.ExecuteNonQueryAsync();
            }
        }

        /// <summary>
        /// Loads project information
        /// </summary>
        public async Task<ProjectInfo> GetProjectInfoAsync()
        {
            var connectionString = $"Data Source={_projectDbPath}";

            using (var connection = new SqliteConnection(connectionString))
            {
                await connection.OpenAsync();

                using (var command = connection.CreateCommand())
                {
                    command.CommandText = "SELECT * FROM project_info LIMIT 1";
                    using (var reader = await command.ExecuteReaderAsync())
                    {
                        if (await reader.ReadAsync())
                        {
                            return new ProjectInfo
                            {
                                ProjectId = reader.GetInt32(0),
                                ProjectName = reader.GetString(1),
                                CreatedDate = DateTime.Parse(reader.GetString(2)),
                                LastModified = DateTime.Parse(reader.GetString(3)),
                                Description = reader.IsDBNull(4) ? "" : reader.GetString(4)
                            };
                        }
                    }
                }
            }

            return null;
        }

        /// <summary>
        /// Gets all distinct biological conditions from the database
        /// </summary>
        public async Task<List<string>> GetBiologicalConditionsAsync()
        {
            var conditions = new List<string>();
            var connectionString = $"Data Source={_projectDbPath}";

            using (var connection = new SqliteConnection(connectionString))
            {
                await connection.OpenAsync();

                using (var command = connection.CreateCommand())
                {
                    command.CommandText = @"
                SELECT DISTINCT biological_condition 
                FROM raw_files 
                WHERE biological_condition IS NOT NULL 
                  AND biological_condition != ''
                ORDER BY biological_condition
            ";

                    using (var reader = await command.ExecuteReaderAsync())
                    {
                        while (await reader.ReadAsync())
                        {
                            conditions.Add(reader.GetString(0));
                        }
                    }
                }
            }

            return conditions;
        }

        /// <summary>
        /// Creates a new plate
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

                // Insert plate (NO biological_condition or batch_number)
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
        /// Updates an existing plate
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

        /// <summary>
        /// Checks if a parquet file has already been imported
        /// </summary>
        public async Task<bool> IsParquetImportedAsync(string fileName)
        {
            var connectionString = $"Data Source={_projectDbPath}";

            using (var connection = new SqliteConnection(connectionString))
            {
                await connection.OpenAsync();

                using (var command = connection.CreateCommand())
                {
                    command.CommandText = "SELECT COUNT(*) FROM parquet_imports WHERE file_name = @fileName";
                    command.Parameters.AddWithValue("@fileName", fileName);

                    var result = await command.ExecuteScalarAsync();
                    return Convert.ToInt32(result) > 0;
                }
            }
        }

        /// <summary>
        /// Deletes an existing parquet import and all associated raw files
        /// </summary>
        public async Task DeleteParquetImportAsync(string fileName)
        {
            var connectionString = $"Data Source={_projectDbPath}";

            using (var connection = new SqliteConnection(connectionString))
            {
                await connection.OpenAsync();

                using (var transaction = connection.BeginTransaction())
                {
                    try
                    {
                        // Get import_id
                        int importId;
                        using (var command = connection.CreateCommand())
                        {
                            command.CommandText = "SELECT import_id FROM parquet_imports WHERE file_name = @fileName";
                            command.Parameters.AddWithValue("@fileName", fileName);
                            var result = await command.ExecuteScalarAsync();
                            if (result == null)
                                return; // Already deleted

                            importId = Convert.ToInt32(result);
                        }

                        // Delete protein quant summary for raw files from this import
                        using (var command = connection.CreateCommand())
                        {
                            command.CommandText = @"
                                DELETE FROM protein_quant_summary 
                                WHERE raw_file_id IN (SELECT raw_file_id FROM raw_files WHERE import_id = @importId)
                            ";
                            command.Parameters.AddWithValue("@importId", importId);
                            await command.ExecuteNonQueryAsync();
                        }

                        // Delete raw files
                        using (var command = connection.CreateCommand())
                        {
                            command.CommandText = "DELETE FROM raw_files WHERE import_id = @importId";
                            command.Parameters.AddWithValue("@importId", importId);
                            await command.ExecuteNonQueryAsync();
                        }

                        // Delete parquet import
                        using (var command = connection.CreateCommand())
                        {
                            command.CommandText = "DELETE FROM parquet_imports WHERE import_id = @importId";
                            command.Parameters.AddWithValue("@importId", importId);
                            await command.ExecuteNonQueryAsync();
                        }

                        await transaction.CommitAsync();
                    }
                    catch
                    {
                        await transaction.RollbackAsync();
                        throw;
                    }
                }
            }
        }

        /// <summary>
        /// Inserts a parquet import record and returns the new import_id
        /// </summary>
        public async Task<int> InsertParquetImportAsync(ParquetImportInfo importInfo)
        {
            var connectionString = $"Data Source={_projectDbPath}";

            using (var connection = new SqliteConnection(connectionString))
            {
                await connection.OpenAsync();

                using (var command = connection.CreateCommand())
                {
                    command.CommandText = @"
                INSERT INTO parquet_imports 
                (plate_id, file_name, file_hash, import_timestamp, row_count, protein_count, cell_count, column_mapping)
                VALUES 
                (@plateId, @fileName, @fileHash, @timestamp, @rowCount, @proteinCount, @cellCount, @mapping);
                
                SELECT last_insert_rowid();
            ";

                    command.Parameters.AddWithValue("@plateId", importInfo.PlateId);
                    command.Parameters.AddWithValue("@fileName", importInfo.FileName);
                    command.Parameters.AddWithValue("@fileHash", importInfo.FileHash);
                    command.Parameters.AddWithValue("@timestamp", importInfo.ImportTimestamp.ToString("o"));
                    command.Parameters.AddWithValue("@rowCount", importInfo.RowCount);
                    command.Parameters.AddWithValue("@proteinCount", importInfo.ProteinCount);
                    command.Parameters.AddWithValue("@cellCount", importInfo.CellCount);
                    command.Parameters.AddWithValue("@mapping", importInfo.ColumnMappingJson);

                    var result = await command.ExecuteScalarAsync();
                    return Convert.ToInt32(result);
                }
            }
        }

        /// <summary>
        /// Bulk inserts raw file records associated with an import
        /// </summary>
        /// <summary>
        /// Bulk inserts raw file records associated with an import
        /// Returns the raw files with their assigned raw_file_id values
        /// </summary>
        public async Task<List<RawFileInfo>> InsertRawFilesAsync(int importId, List<RawFileInfo> rawFiles)
        {
            var connectionString = $"Data Source={_projectDbPath}";
            var insertedFiles = new List<RawFileInfo>();

            using (var connection = new SqliteConnection(connectionString))
            {
                await connection.OpenAsync();

                using (var transaction = connection.BeginTransaction())
                {
                    try
                    {
                        foreach (var rawFile in rawFiles)
                        {
                            using (var command = connection.CreateCommand())
                            {
                                command.CommandText = @"
                            INSERT INTO raw_files 
                            (import_id, raw_file_name, biological_condition, plate_id, protein_count, peptide_count, total_ion_current)
                            VALUES 
                            (@importId, @rawFileName, @condition, @plateId, @proteinCount, @peptideCount, @tic);
                            
                            SELECT last_insert_rowid();
                        ";

                                command.Parameters.AddWithValue("@importId", importId);
                                command.Parameters.AddWithValue("@rawFileName", rawFile.RawFileName);
                                command.Parameters.AddWithValue("@condition", rawFile.BiologicalCondition ?? (object)DBNull.Value);
                                command.Parameters.AddWithValue("@plateId", rawFile.PlateId.HasValue ? rawFile.PlateId.Value : DBNull.Value);
                                command.Parameters.AddWithValue("@proteinCount", rawFile.ProteinCount);
                                command.Parameters.AddWithValue("@peptideCount", rawFile.PeptideCount);
                                command.Parameters.AddWithValue("@tic", rawFile.TotalIonCurrent);

                                var result = await command.ExecuteScalarAsync();
                                int rawFileId = Convert.ToInt32(result);

                                // Update the raw file object with its database ID
                                rawFile.RawFileId = rawFileId;
                                insertedFiles.Add(rawFile);
                            }
                        }

                        await transaction.CommitAsync();
                    }
                    catch
                    {
                        await transaction.RollbackAsync();
                        throw;
                    }
                }
            }

            return insertedFiles;
        }

        /// <summary>
        /// Extracts protein quantification data from parquet and stores summary statistics
        /// </summary>
        public async Task ExtractAndStoreProteinQuantAsync(
            string parquetPath,
            int importId,
            List<RawFileInfo> rawFiles,
            IProgress<string> progress = null)
        {
            progress?.Report("Loading parquet file...");

            // Load parquet data using existing service
            var parquetService = new ParquetDataService();
            var mapping = new ColumnMapping
            {
                RawFileColumn = "Run",
                ProteinGroupColumn = "Protein.Group",
                PeptideColumn = "Stripped.Sequence",
                TotalIonCurrentColumn = "Precursor.Quantity"
            };

            var data = await parquetService.LoadParquetFileAsync(parquetPath, mapping);

            progress?.Report($"Processing {data.ProteinQuantMatrix.Count} proteins across {data.RawFileNames.Count} files...");

            // Create lookup: RawFileName -> raw_file_id
            var rawFileIdMap = rawFiles.ToDictionary(rf => rf.RawFileName, rf => rf.RawFileId);

            // Prepare protein statistics
            var proteinStats = new List<ProteinQuantSummary>();

            foreach (var protein in data.ProteinQuantMatrix.Keys)
            {
                foreach (var rawFileName in data.RawFileNames)
                {
                    if (!rawFileIdMap.ContainsKey(rawFileName))
                        continue; // Skip if raw file not in our import

                    int rawFileId = rawFileIdMap[rawFileName];

                    // Get the summed intensity for this protein in this raw file
                    double intensity = 0;
                    if (data.ProteinQuantMatrix[protein].ContainsKey(rawFileName))
                    {
                        intensity = data.ProteinQuantMatrix[protein][rawFileName];
                    }

                    // For now, we'll use the summed intensity as both median and mean
                    // In a more sophisticated version, we'd store all peptide intensities
                    // and calculate proper statistics
                    if (intensity > 0)
                    {
                        proteinStats.Add(new ProteinQuantSummary
                        {
                            ProteinId = protein,
                            RawFileId = rawFileId,
                            MedianIntensity = intensity,
                            MeanIntensity = intensity,
                            DetectionCount = 1 // Detected in this file
                        });
                    }
                }
            }

            progress?.Report($"Storing {proteinStats.Count} protein quantification records...");

            // Bulk insert protein statistics
            await BulkInsertProteinQuantAsync(proteinStats);

            progress?.Report("Protein quantification complete!");
        }

        /// <summary>
        /// Bulk inserts protein quantification summary records
        /// </summary>
        private async Task BulkInsertProteinQuantAsync(List<ProteinQuantSummary> proteinStats)
        {
            var connectionString = $"Data Source={_projectDbPath}";

            using (var connection = new SqliteConnection(connectionString))
            {
                await connection.OpenAsync();

                using (var transaction = connection.BeginTransaction())
                {
                    try
                    {
                        foreach (var stat in proteinStats)
                        {
                            using (var command = connection.CreateCommand())
                            {
                                command.CommandText = @"
                            INSERT INTO protein_quant_summary 
                            (protein_id, raw_file_id, median_intensity, mean_intensity, detection_count)
                            VALUES 
                            (@proteinId, @rawFileId, @median, @mean, @count)
                        ";

                                command.Parameters.AddWithValue("@proteinId", stat.ProteinId);
                                command.Parameters.AddWithValue("@rawFileId", stat.RawFileId);
                                command.Parameters.AddWithValue("@median", stat.MedianIntensity);
                                command.Parameters.AddWithValue("@mean", stat.MeanIntensity);
                                command.Parameters.AddWithValue("@count", stat.DetectionCount);

                                await command.ExecuteNonQueryAsync();
                            }
                        }

                        await transaction.CommitAsync();
                    }
                    catch
                    {
                        await transaction.RollbackAsync();
                        throw;
                    }
                }
            }
        }

        // Helper class for protein statistics
        private class ProteinQuantSummary
        {
            public string ProteinId { get; set; }
            public int RawFileId { get; set; }
            public double MedianIntensity { get; set; }
            public double MeanIntensity { get; set; }
            public int DetectionCount { get; set; }
        }

        /// <summary>
        /// Calculates SHA256 hash of a file
        /// </summary>
        public string CalculateFileHash(string filePath)
        {
            using (var sha256 = SHA256.Create())
            {
                using (var stream = File.OpenRead(filePath))
                {
                    var hash = sha256.ComputeHash(stream);
                    return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
                }
            }
        }

        /// <summary>
        /// Gets all raw files, optionally filtered by plate or condition
        /// </summary>
        public async Task<List<RawFileInfo>> GetRawFilesAsync(int? plateId = null, string condition = null)
        {
            var rawFiles = new List<RawFileInfo>();
            var connectionString = $"Data Source={_projectDbPath}";

            using (var connection = new SqliteConnection(connectionString))
            {
                await connection.OpenAsync();

                using (var command = connection.CreateCommand())
                {
                    var whereClauses = new List<string>();
                    if (plateId.HasValue)
                    {
                        whereClauses.Add("rf.plate_id = @plateId");
                        command.Parameters.AddWithValue("@plateId", plateId.Value);
                    }
                    if (!string.IsNullOrEmpty(condition))
                    {
                        whereClauses.Add("rf.biological_condition = @condition");
                        command.Parameters.AddWithValue("@condition", condition);
                    }

                    var whereClause = whereClauses.Count > 0 ? "WHERE " + string.Join(" AND ", whereClauses) : "";

                    command.CommandText = $@"
                        SELECT rf.raw_file_id, rf.import_id, rf.raw_file_name, rf.biological_condition,
                               rf.plate_id, p.plate_name, rf.protein_count, rf.peptide_count, rf.total_ion_current
                        FROM raw_files rf
                        LEFT JOIN plates p ON rf.plate_id = p.plate_id
                        {whereClause}
                        ORDER BY rf.raw_file_name
                    ";

                    using (var reader = await command.ExecuteReaderAsync())
                    {
                        while (await reader.ReadAsync())
                        {
                            rawFiles.Add(new RawFileInfo
                            {
                                RawFileId = reader.GetInt32(0),
                                ImportId = reader.GetInt32(1),
                                RawFileName = reader.GetString(2),
                                BiologicalCondition = reader.IsDBNull(3) ? "" : reader.GetString(3),
                                PlateId = reader.IsDBNull(4) ? null : reader.GetInt32(4),
                                PlateName = reader.IsDBNull(5) ? "" : reader.GetString(5),
                                ProteinCount = reader.IsDBNull(6) ? 0 : reader.GetInt32(6),
                                PeptideCount = reader.IsDBNull(7) ? 0 : reader.GetInt32(7),
                                TotalIonCurrent = reader.IsDBNull(8) ? 0.0 : reader.GetDouble(8)
                            });
                        }
                    }
                }
            }

            return rawFiles;
        }

        /// <summary>
        /// Updates biological condition for a raw file
        /// </summary>
        public async Task UpdateRawFileConditionAsync(int rawFileId, string biologicalCondition)
        {
            var connectionString = $"Data Source={_projectDbPath}";

            using (var connection = new SqliteConnection(connectionString))
            {
                await connection.OpenAsync();

                using (var command = connection.CreateCommand())
                {
                    command.CommandText = @"
                        UPDATE raw_files 
                        SET biological_condition = @condition
                        WHERE raw_file_id = @rawFileId
                    ";
                    command.Parameters.AddWithValue("@rawFileId", rawFileId);
                    command.Parameters.AddWithValue("@condition", biologicalCondition ?? "");
                    await command.ExecuteNonQueryAsync();
                }
            }
        }

        /// <summary>
        /// Updates plate assignment for a raw file
        /// </summary>
        public async Task UpdateRawFilePlateAsync(int rawFileId, int? plateId)
        {
            var connectionString = $"Data Source={_projectDbPath}";

            using (var connection = new SqliteConnection(connectionString))
            {
                await connection.OpenAsync();

                using (var command = connection.CreateCommand())
                {
                    command.CommandText = @"
                        UPDATE raw_files 
                        SET plate_id = @plateId
                        WHERE raw_file_id = @rawFileId
                    ";
                    command.Parameters.AddWithValue("@rawFileId", rawFileId);
                    command.Parameters.AddWithValue("@plateId", plateId.HasValue ? plateId.Value : DBNull.Value);
                    await command.ExecuteNonQueryAsync();
                }
            }
        }
    }
}