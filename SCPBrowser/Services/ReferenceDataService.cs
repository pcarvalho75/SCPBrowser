using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using SCPBrowser.GOTools;

namespace SCPBrowser.Services
{
    /// <summary>
    /// Service for managing reference data (GO annotations and transcriptomic data)
    /// within the project database. No longer creates separate databases - works with
    /// the unified project database created by ProjectDataService.
    /// </summary>
    public class ReferenceDataService
    {
        // ==================== TRANSCRIPTOMIC DATA (CELL TYPE PROFILES) ====================

        /// <summary>
        /// Clears all transcriptomic data (cell type profiles and metadata) from the database
        /// </summary>
        public async Task ClearTranscriptomicDataAsync(string databasePath)
        {
            if (!File.Exists(databasePath))
                return;

            var connectionString = $"Data Source={databasePath}";

            using (var connection = new SqliteConnection(connectionString))
            {
                await connection.OpenAsync();

                using (var command = connection.CreateCommand())
                {
                    command.CommandText = @"
                        DELETE FROM cell_type_profiles;
                        DELETE FROM cell_type_metadata;
                    ";
                    await command.ExecuteNonQueryAsync();
                }

                Console.WriteLine("Cleared existing transcriptomic data from database.");
            }
        }

        /// <summary>
        /// Writes cell type profiles to the database
        /// </summary>
        public async Task WriteTranscriptomicDataAsync(
            string databasePath,
            ParsedTranscriptomicData parsedData,
            bool clearExistingData = true,
            IProgressReporter progress = null)
        {
            var connectionString = $"Data Source={databasePath}";

            using (var connection = new SqliteConnection(connectionString))
            {
                await connection.OpenAsync();

                // Tables are created by ProjectDataService, no need to ensure here

                // Clear existing data if requested
                if (clearExistingData)
                {
                    progress?.ReportMessage("Clearing existing transcriptomic data...");
                    using (var command = connection.CreateCommand())
                    {
                        command.CommandText = @"
                            DELETE FROM cell_type_profiles;
                            DELETE FROM cell_type_metadata;
                        ";
                        await command.ExecuteNonQueryAsync();
                    }
                    progress?.ReportProgress("Database cleared");
                }

                // Performance optimizations for bulk insert
                progress?.ReportMessage("Optimizing database for bulk insert...");
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = @"
                        PRAGMA synchronous = OFF;
                        PRAGMA journal_mode = MEMORY;
                        PRAGMA temp_store = MEMORY;
                        PRAGMA cache_size = -64000;
                    ";
                    await command.ExecuteNonQueryAsync();
                }

                // Write cell type profiles
                await WriteCellTypeProfilesAsync(connection, parsedData.CellTypeProfiles, progress);

                // Write cell type metadata
                await WriteCellTypeMetadataAsync(connection, parsedData.CellTypeMetadata, progress);

                // Restore normal settings
                progress?.ReportMessage("Finalizing database...");
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = @"
                        PRAGMA synchronous = FULL;
                        VACUUM;
                    ";
                    await command.ExecuteNonQueryAsync();
                }
                progress?.ReportProgress("Database write complete");
            }
        }

        private async Task WriteCellTypeProfilesAsync(
            SqliteConnection connection,
            List<CellTypeProfile> profiles,
            IProgressReporter progress = null)
        {
            const int batchSize = 1000;
            int totalRecords = profiles.Sum(p => p.MedianExpression.Count);

            progress?.ReportMessage($"Writing {profiles.Count} cell type profiles ({totalRecords:N0} gene-celltype pairs)...");

            int recordsWritten = 0;

            foreach (var profile in profiles)
            {
                var genes = profile.MedianExpression.Keys.ToList();

                for (int i = 0; i < genes.Count; i += batchSize)
                {
                    var batch = genes.Skip(i).Take(batchSize).ToList();

                    using (var transaction = connection.BeginTransaction())
                    {
                        using (var command = connection.CreateCommand())
                        {
                            var valuesClauses = new List<string>();
                            var parameters = new List<SqliteParameter>();

                            for (int j = 0; j < batch.Count; j++)
                            {
                                var gene = batch[j];
                                var paramPrefix = $"@p{j}_";

                                valuesClauses.Add($"({paramPrefix}cell_type, {paramPrefix}gene_name, {paramPrefix}median, {paramPrefix}mean, {paramPrefix}percent)");

                                parameters.Add(new SqliteParameter($"{paramPrefix}cell_type", profile.CellType));
                                parameters.Add(new SqliteParameter($"{paramPrefix}gene_name", gene));
                                parameters.Add(new SqliteParameter($"{paramPrefix}median", profile.MedianExpression[gene]));
                                parameters.Add(new SqliteParameter($"{paramPrefix}mean", profile.MeanExpression[gene]));
                                parameters.Add(new SqliteParameter($"{paramPrefix}percent", profile.PercentExpressing[gene]));
                            }

                            command.CommandText = $@"
                                INSERT OR REPLACE INTO cell_type_profiles 
                                (cell_type, gene_name, median_expression, mean_expression, percent_expressing)
                                VALUES {string.Join(", ", valuesClauses)}
                            ";

                            command.Parameters.AddRange(parameters.ToArray());
                            await command.ExecuteNonQueryAsync();
                        }

                        await transaction.CommitAsync();
                    }

                    recordsWritten += batch.Count;

                    if (recordsWritten % 10000 == 0 || recordsWritten == totalRecords || i + batchSize >= genes.Count)
                    {
                        progress?.ReportProgress($"  {profile.CellType}: {recordsWritten} / {totalRecords} total genes written");
                    }
                }
            }

            progress?.ReportProgress($"Cell type profiles complete: {recordsWritten:N0} total records");
        }

        private async Task WriteCellTypeMetadataAsync(
            SqliteConnection connection,
            List<CellTypeMetadata> metadataList,
            IProgressReporter progress = null)
        {
            progress?.ReportMessage($"Writing metadata for {metadataList.Count} cell types...");

            using (var transaction = connection.BeginTransaction())
            {
                using (var command = connection.CreateCommand())
                {
                    var valuesClauses = new List<string>();
                    var parameters = new List<SqliteParameter>();

                    for (int i = 0; i < metadataList.Count; i++)
                    {
                        var metadata = metadataList[i];
                        var paramPrefix = $"@m{i}_";

                        valuesClauses.Add($"({paramPrefix}cell_type, {paramPrefix}cell_count, {paramPrefix}genes_expressed, {paramPrefix}age_range, {paramPrefix}batch_info)");

                        parameters.Add(new SqliteParameter($"{paramPrefix}cell_type", metadata.CellType));
                        parameters.Add(new SqliteParameter($"{paramPrefix}cell_count", metadata.CellCount));
                        parameters.Add(new SqliteParameter($"{paramPrefix}genes_expressed", metadata.GenesExpressed));
                        parameters.Add(new SqliteParameter($"{paramPrefix}age_range",
                            string.IsNullOrEmpty(metadata.AgeRange) ? DBNull.Value : metadata.AgeRange));
                        parameters.Add(new SqliteParameter($"{paramPrefix}batch_info",
                            string.IsNullOrEmpty(metadata.BatchInfo) ? DBNull.Value : metadata.BatchInfo));
                    }

                    command.CommandText = $@"
                        INSERT OR REPLACE INTO cell_type_metadata 
                        (cell_type, cell_count, genes_expressed, age_range, batch_info)
                        VALUES {string.Join(", ", valuesClauses)}
                    ";

                    command.Parameters.AddRange(parameters.ToArray());
                    await command.ExecuteNonQueryAsync();
                }

                await transaction.CommitAsync();
            }

            progress?.ReportProgress("Cell type metadata complete");
        }


        /// <summary>
        /// Loads cell type profiles from the database
        /// </summary>
        public async Task<TranscriptomicDatabase> LoadTranscriptomicDataAsync(string databasePath, IProgressReporter progress = null)
        {
            if (!File.Exists(databasePath))
                throw new FileNotFoundException("Database not found", databasePath);

            var database = new TranscriptomicDatabase();
            var connectionString = $"Data Source={databasePath};Mode=ReadOnly";

            using (var connection = new SqliteConnection(connectionString))
            {
                await connection.OpenAsync();

                // Load cell type metadata first
                progress?.ReportMessage("Loading cell type metadata...");
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = @"
                SELECT cell_type, cell_count, genes_expressed, age_range, batch_info
                FROM cell_type_metadata
            ";

                    using (var reader = await command.ExecuteReaderAsync())
                    {
                        while (await reader.ReadAsync())
                        {
                            var cellType = reader.GetString(0);
                            var metadata = new CellTypeMetadata
                            {
                                CellType = cellType,
                                CellCount = reader.GetInt32(1),
                                GenesExpressed = reader.GetInt32(2),
                                AgeRange = reader.IsDBNull(3) ? null : reader.GetString(3),
                                BatchInfo = reader.IsDBNull(4) ? null : reader.GetString(4)
                            };

                            database.CellTypeMetadata[cellType] = metadata;
                        }
                    }
                }

                progress?.ReportMessage($"Loading cell type profiles for {database.CellTypeMetadata.Count} cell types...");

                // Load cell type profiles
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = @"
                SELECT cell_type, gene_name, median_expression, mean_expression, percent_expressing
                FROM cell_type_profiles
                ORDER BY cell_type, gene_name
            ";

                    using (var reader = await command.ExecuteReaderAsync())
                    {
                        string currentCellType = null;
                        CellTypeProfile currentProfile = null;
                        int recordCount = 0;

                        while (await reader.ReadAsync())
                        {
                            var cellType = reader.GetString(0);
                            var geneName = reader.GetString(1);
                            var medianExpression = reader.GetDouble(2);
                            var meanExpression = reader.GetDouble(3);
                            var percentExpressing = reader.GetDouble(4);

                            // Start a new profile if cell type changed
                            if (cellType != currentCellType)
                            {
                                if (currentProfile != null)
                                {
                                    database.CellTypeProfiles[currentCellType] = currentProfile;
                                }

                                currentCellType = cellType;
                                currentProfile = new CellTypeProfile
                                {
                                    CellType = cellType,
                                    MedianExpression = new Dictionary<string, double>(),
                                    MeanExpression = new Dictionary<string, double>(),
                                    PercentExpressing = new Dictionary<string, double>(),
                                    CellCount = database.CellTypeMetadata.ContainsKey(cellType)
                                        ? database.CellTypeMetadata[cellType].CellCount
                                        : 0
                                };

                                if (recordCount % 5000 == 0 && recordCount > 0)
                                {
                                    progress?.ReportProgress($"Loaded {recordCount:N0} gene expression records...");
                                }
                            }

                            // Add gene expression data to current profile
                            currentProfile.MedianExpression[geneName] = medianExpression;
                            currentProfile.MeanExpression[geneName] = meanExpression;
                            currentProfile.PercentExpressing[geneName] = percentExpressing;

                            recordCount++;
                        }

                        // Don't forget to save the last profile
                        if (currentProfile != null)
                        {
                            database.CellTypeProfiles[currentCellType] = currentProfile;
                        }

                        progress?.ReportProgress($"Loaded {recordCount:N0} total gene expression records");
                    }
                }

                progress?.ReportMessage($"Database loaded: {database.TotalCellTypes} cell types, {database.TotalCells:N0} cells, {database.TotalGenes:N0} genes");
            }

            return database;
        }
        // ==================== GO ANNOTATIONS ====================

        /// <summary>
        /// Clears all GO annotation data from the database
        /// </summary>
        public async Task ClearGoAnnotationsAsync(string databasePath)
        {
            if (!File.Exists(databasePath))
                return;

            var connectionString = $"Data Source={databasePath}";

            using (var connection = new SqliteConnection(connectionString))
            {
                await connection.OpenAsync();

                using (var command = connection.CreateCommand())
                {
                    command.CommandText = @"
                        DELETE FROM protein_go_annotations;
                        DELETE FROM go_terms;
                    ";
                    await command.ExecuteNonQueryAsync();
                }

                Console.WriteLine("Cleared existing GO annotation data from database.");
            }
        }

    }
}