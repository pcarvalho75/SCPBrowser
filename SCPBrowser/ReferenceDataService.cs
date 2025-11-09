using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using SCPBrowser.GOTools;

namespace SCPBrowser
{
    /// <summary>
    /// Unified service for managing all reference data in a single SQLite database
    /// Handles transcriptomic data, GO terms, and GO annotations
    /// </summary>
    public class ReferenceDataService
    {
        public async Task CreateDatabaseAsync(string outputPath)
        {
            if (File.Exists(outputPath))
                File.Delete(outputPath);

            var connectionString = $"Data Source={outputPath}";

            using (var connection = new SqliteConnection(connectionString))
            {
                await connection.OpenAsync();
                await CreateSchemaAsync(connection);
            }
        }

        private async Task CreateSchemaAsync(SqliteConnection connection)
        {
            using (var command = connection.CreateCommand())
            {
                command.CommandText = @"
                    -- Transcriptomic gene expression data
                    CREATE TABLE IF NOT EXISTS gene_expression (
                        gene_name TEXT NOT NULL,
                        cell_id TEXT NOT NULL,
                        count INTEGER NOT NULL,
                        PRIMARY KEY (gene_name, cell_id)
                    );

                    -- Cell metadata
                    CREATE TABLE IF NOT EXISTS cell_metadata (
                        cell_id TEXT PRIMARY KEY,
                        age TEXT,
                        sex TEXT,
                        batch TEXT,
                        cell_type TEXT,
                        genes_detected INTEGER,
                        total_reads INTEGER,
                        mapped_reads INTEGER,
                        mapping_rate REAL
                    );

                    -- GO terms (GO Slim)
                    CREATE TABLE IF NOT EXISTS go_terms (
                        go_id TEXT PRIMARY KEY,
                        name TEXT NOT NULL,
                        namespace TEXT NOT NULL
                    );

                    -- Protein to GO term annotations
                    CREATE TABLE IF NOT EXISTS protein_go_annotations (
                        protein_id TEXT NOT NULL,
                        go_term_id TEXT NOT NULL,
                        PRIMARY KEY (protein_id, go_term_id)
                    );

                    -- Indexes for fast queries
                    CREATE INDEX IF NOT EXISTS idx_gene_expr_gene ON gene_expression(gene_name);
                    CREATE INDEX IF NOT EXISTS idx_gene_expr_cell ON gene_expression(cell_id);
                    CREATE INDEX IF NOT EXISTS idx_cell_type ON cell_metadata(cell_type);
                    CREATE INDEX IF NOT EXISTS idx_protein_go ON protein_go_annotations(protein_id);
                    CREATE INDEX IF NOT EXISTS idx_go_protein ON protein_go_annotations(go_term_id);
                ";

                await command.ExecuteNonQueryAsync();
            }
        }

        // ==================== TRANSCRIPTOMIC DATA ====================

        /// <summary>
        /// Clears all transcriptomic data (gene expression and cell metadata) from the database
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
                        DELETE FROM gene_expression;
                        DELETE FROM cell_metadata;
                    ";
                    await command.ExecuteNonQueryAsync();
                }

                Console.WriteLine("Cleared existing transcriptomic data from database.");
            }
        }

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

        public async Task WriteTranscriptomicDataAsync(
            string databasePath,
            List<GeneExpressionRecord> expressionRecords,
            List<CellMetadata> metadata,
            bool clearExistingData = true)
        {
            var connectionString = $"Data Source={databasePath}";

            using (var connection = new SqliteConnection(connectionString))
            {
                await connection.OpenAsync();

                // Ensure tables exist
                await CreateSchemaAsync(connection);

                // Clear existing data if requested
                if (clearExistingData)
                {
                    using (var command = connection.CreateCommand())
                    {
                        command.CommandText = @"
                            DELETE FROM gene_expression;
                            DELETE FROM cell_metadata;
                        ";
                        await command.ExecuteNonQueryAsync();
                    }
                    Console.WriteLine("Cleared existing transcriptomic data from database.");
                }

                // Performance optimizations for bulk insert
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = @"
                        PRAGMA synchronous = OFF;
                        PRAGMA journal_mode = MEMORY;
                        PRAGMA temp_store = MEMORY;
                    ";
                    await command.ExecuteNonQueryAsync();
                }

                // Insert gene expression
                await InsertGeneExpressionAsync(connection, expressionRecords);

                // Insert cell metadata
                await InsertCellMetadataAsync(connection, metadata);

                // Restore normal settings
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = @"
                        PRAGMA synchronous = FULL;
                    ";
                    await command.ExecuteNonQueryAsync();
                }
            }
        }

        private async Task InsertGeneExpressionAsync(SqliteConnection connection, List<GeneExpressionRecord> records)
        {
            const int batchSize = 5000;

            Console.WriteLine($"Inserting {records.Count:N0} gene expression records in batches of {batchSize:N0}...");

            for (int i = 0; i < records.Count; i += batchSize)
            {
                var batch = records.Skip(i).Take(batchSize).ToList();

                using (var transaction = connection.BeginTransaction())
                {
                    using (var command = connection.CreateCommand())
                    {
                        var valuesClauses = new List<string>();
                        var parameters = new List<SqliteParameter>();

                        for (int j = 0; j < batch.Count; j++)
                        {
                            var record = batch[j];
                            var paramPrefix = $"@p{j}_";

                            valuesClauses.Add($"({paramPrefix}gene_name, {paramPrefix}cell_id, {paramPrefix}count)");

                            parameters.Add(new SqliteParameter($"{paramPrefix}gene_name", record.GeneName));
                            parameters.Add(new SqliteParameter($"{paramPrefix}cell_id", record.CellID));
                            parameters.Add(new SqliteParameter($"{paramPrefix}count", record.Count));
                        }

                        // Use INSERT OR IGNORE to skip duplicates (safety net)
                        command.CommandText = $@"
                            INSERT OR IGNORE INTO gene_expression (gene_name, cell_id, count)
                            VALUES {string.Join(", ", valuesClauses)}
                        ";

                        command.Parameters.AddRange(parameters.ToArray());
                        await command.ExecuteNonQueryAsync();
                    }

                    await transaction.CommitAsync();
                }

                if ((i + batchSize) % 50000 == 0 || i + batchSize >= records.Count)
                {
                    Console.WriteLine($"  Progress: {Math.Min(i + batchSize, records.Count):N0} / {records.Count:N0} records inserted");
                }
            }

            Console.WriteLine("Gene expression data insertion complete.");
        }

        private async Task InsertCellMetadataAsync(SqliteConnection connection, List<CellMetadata> metadata)
        {
            const int batchSize = 1000;

            Console.WriteLine($"Inserting {metadata.Count:N0} cell metadata records...");

            for (int i = 0; i < metadata.Count; i += batchSize)
            {
                var batch = metadata.Skip(i).Take(batchSize).ToList();

                using (var transaction = connection.BeginTransaction())
                {
                    using (var command = connection.CreateCommand())
                    {
                        var valuesClauses = new List<string>();
                        var parameters = new List<SqliteParameter>();

                        for (int j = 0; j < batch.Count; j++)
                        {
                            var cell = batch[j];
                            var paramPrefix = $"@p{j}_";

                            valuesClauses.Add($"({paramPrefix}cell_id, {paramPrefix}age, {paramPrefix}sex, " +
                                            $"{paramPrefix}batch, {paramPrefix}cell_type, {paramPrefix}genes_detected, " +
                                            $"{paramPrefix}total_reads, {paramPrefix}mapped_reads, {paramPrefix}mapping_rate)");

                            parameters.Add(new SqliteParameter($"{paramPrefix}cell_id", cell.CellID));
                            parameters.Add(new SqliteParameter($"{paramPrefix}age", (object)cell.Age ?? DBNull.Value));
                            parameters.Add(new SqliteParameter($"{paramPrefix}sex", (object)cell.Sex ?? DBNull.Value));
                            parameters.Add(new SqliteParameter($"{paramPrefix}batch", (object)cell.Batch ?? DBNull.Value));
                            parameters.Add(new SqliteParameter($"{paramPrefix}cell_type", (object)cell.CellType ?? DBNull.Value));
                            parameters.Add(new SqliteParameter($"{paramPrefix}genes_detected",
                                cell.GenesDetected.HasValue ? (object)cell.GenesDetected.Value : DBNull.Value));
                            parameters.Add(new SqliteParameter($"{paramPrefix}total_reads",
                                cell.TotalReads.HasValue ? (object)cell.TotalReads.Value : DBNull.Value));
                            parameters.Add(new SqliteParameter($"{paramPrefix}mapped_reads",
                                cell.MappedReads.HasValue ? (object)cell.MappedReads.Value : DBNull.Value));
                            parameters.Add(new SqliteParameter($"{paramPrefix}mapping_rate",
                                cell.MappingRate.HasValue ? (object)cell.MappingRate.Value : DBNull.Value));
                        }

                        // Use INSERT OR REPLACE to update existing records (safety net)
                        command.CommandText = $@"
                            INSERT OR REPLACE INTO cell_metadata 
                            (cell_id, age, sex, batch, cell_type, genes_detected, total_reads, mapped_reads, mapping_rate)
                            VALUES {string.Join(", ", valuesClauses)}
                        ";

                        command.Parameters.AddRange(parameters.ToArray());
                        await command.ExecuteNonQueryAsync();
                    }

                    await transaction.CommitAsync();
                }
            }

            Console.WriteLine("Cell metadata insertion complete.");
        }

        public async Task<TranscriptomicDatabase> LoadTranscriptomicDataAsync(string databasePath)
        {
            if (!File.Exists(databasePath))
                throw new FileNotFoundException("Reference database not found", databasePath);

            var database = new TranscriptomicDatabase();
            var connectionString = $"Data Source={databasePath};Mode=ReadOnly";

            using (var connection = new SqliteConnection(connectionString))
            {
                await connection.OpenAsync();

                // Load cell metadata
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = @"
                        SELECT cell_id, age, sex, batch, cell_type, genes_detected, 
                               total_reads, mapped_reads, mapping_rate
                        FROM cell_metadata
                    ";

                    using (var reader = await command.ExecuteReaderAsync())
                    {
                        while (await reader.ReadAsync())
                        {
                            var cellId = reader.GetString(0);
                            var metadata = new CellMetadata
                            {
                                CellID = cellId,
                                Age = reader.IsDBNull(1) ? null : reader.GetString(1),
                                Sex = reader.IsDBNull(2) ? null : reader.GetString(2),
                                Batch = reader.IsDBNull(3) ? null : reader.GetString(3),
                                CellType = reader.IsDBNull(4) ? null : reader.GetString(4),
                                GenesDetected = reader.IsDBNull(5) ? null : reader.GetInt32(5),
                                TotalReads = reader.IsDBNull(6) ? null : reader.GetInt64(6),
                                MappedReads = reader.IsDBNull(7) ? null : reader.GetInt64(7),
                                MappingRate = reader.IsDBNull(8) ? null : reader.GetDouble(8)
                            };

                            database.CellMetadata[cellId] = metadata;

                            if (!string.IsNullOrEmpty(metadata.CellType))
                            {
                                if (!database.CellTypeIndex.ContainsKey(metadata.CellType))
                                    database.CellTypeIndex[metadata.CellType] = new List<string>();
                                database.CellTypeIndex[metadata.CellType].Add(cellId);
                            }
                        }
                    }
                }

                // Load gene expression
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = @"
                        SELECT gene_name, cell_id, count
                        FROM gene_expression
                        ORDER BY gene_name
                    ";

                    using (var reader = await command.ExecuteReaderAsync())
                    {
                        while (await reader.ReadAsync())
                        {
                            var geneName = reader.GetString(0);
                            var cellId = reader.GetString(1);
                            var count = reader.GetInt32(2);

                            if (!database.GeneExpression.ContainsKey(geneName))
                                database.GeneExpression[geneName] = new List<(string cellId, int count)>();

                            database.GeneExpression[geneName].Add((cellId, count));
                        }
                    }
                }
            }

            return database;
        }

        // ==================== GO ANNOTATIONS ====================

        public async Task WriteGoAnnotationsAsync(
            string databasePath,
            GoSlimDatabase goSlimDatabase,
            GoAnnotationDatabase annotationDatabase,
            bool clearExistingData = true)
        {
            var connectionString = $"Data Source={databasePath}";

            using (var connection = new SqliteConnection(connectionString))
            {
                await connection.OpenAsync();

                // Ensure tables exist
                await CreateSchemaAsync(connection);

                // Clear existing GO data if requested
                if (clearExistingData)
                {
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

                // Performance optimizations for bulk insert
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = @"
                        PRAGMA synchronous = OFF;
                        PRAGMA journal_mode = MEMORY;
                        PRAGMA temp_store = MEMORY;
                    ";
                    await command.ExecuteNonQueryAsync();
                }

                // Insert GO terms
                await InsertGoTermsAsync(connection, goSlimDatabase);

                // Insert protein annotations
                await InsertProteinAnnotationsAsync(connection, annotationDatabase);

                // Restore normal settings
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = @"
                        PRAGMA synchronous = FULL;
                    ";
                    await command.ExecuteNonQueryAsync();
                }
            }
        }

        private async Task InsertGoTermsAsync(SqliteConnection connection, GoSlimDatabase goSlimDatabase)
        {
            const int batchSize = 500;
            var terms = goSlimDatabase.Terms.Values.ToList();

            Console.WriteLine($"Inserting {terms.Count:N0} GO terms...");

            for (int i = 0; i < terms.Count; i += batchSize)
            {
                var batch = terms.Skip(i).Take(batchSize).ToList();

                using (var transaction = connection.BeginTransaction())
                {
                    using (var command = connection.CreateCommand())
                    {
                        var valuesClauses = new List<string>();
                        var parameters = new List<SqliteParameter>();

                        for (int j = 0; j < batch.Count; j++)
                        {
                            var term = batch[j];
                            var paramPrefix = $"@p{j}_";

                            valuesClauses.Add($"({paramPrefix}go_id, {paramPrefix}name, {paramPrefix}namespace)");

                            parameters.Add(new SqliteParameter($"{paramPrefix}go_id", term.Id));
                            parameters.Add(new SqliteParameter($"{paramPrefix}name", term.Name ?? string.Empty));
                            parameters.Add(new SqliteParameter($"{paramPrefix}namespace", term.Namespace ?? string.Empty));
                        }

                        // Use INSERT OR IGNORE to skip duplicates (safety net)
                        command.CommandText = $@"
                            INSERT OR IGNORE INTO go_terms (go_id, name, namespace)
                            VALUES {string.Join(", ", valuesClauses)}
                        ";

                        command.Parameters.AddRange(parameters.ToArray());
                        await command.ExecuteNonQueryAsync();
                    }

                    await transaction.CommitAsync();
                }
            }

            Console.WriteLine("GO terms insertion complete.");
        }

        private async Task InsertProteinAnnotationsAsync(SqliteConnection connection, GoAnnotationDatabase annotationDatabase)
        {
            const int batchSize = 5000;

            // Flatten the protein-to-GO annotations into a list
            var annotations = new List<(string proteinId, string goTermId)>();
            foreach (var proteinEntry in annotationDatabase.ProteinToGoTerms)
            {
                var proteinId = proteinEntry.Key;
                foreach (var goTermId in proteinEntry.Value)
                {
                    annotations.Add((proteinId, goTermId));
                }
            }

            Console.WriteLine($"Inserting {annotations.Count:N0} protein-GO annotations...");

            for (int i = 0; i < annotations.Count; i += batchSize)
            {
                var batch = annotations.Skip(i).Take(batchSize).ToList();

                using (var transaction = connection.BeginTransaction())
                {
                    using (var command = connection.CreateCommand())
                    {
                        var valuesClauses = new List<string>();
                        var parameters = new List<SqliteParameter>();

                        for (int j = 0; j < batch.Count; j++)
                        {
                            var (proteinId, goTermId) = batch[j];
                            var paramPrefix = $"@p{j}_";

                            valuesClauses.Add($"({paramPrefix}protein_id, {paramPrefix}go_term_id)");

                            parameters.Add(new SqliteParameter($"{paramPrefix}protein_id", proteinId));
                            parameters.Add(new SqliteParameter($"{paramPrefix}go_term_id", goTermId));
                        }

                        // Use INSERT OR IGNORE to skip duplicates (safety net)
                        command.CommandText = $@"
                            INSERT OR IGNORE INTO protein_go_annotations (protein_id, go_term_id)
                            VALUES {string.Join(", ", valuesClauses)}
                        ";

                        command.Parameters.AddRange(parameters.ToArray());
                        await command.ExecuteNonQueryAsync();
                    }

                    await transaction.CommitAsync();
                }

                if ((i + batchSize) % 50000 == 0 || i + batchSize >= annotations.Count)
                {
                    Console.WriteLine($"  Progress: {Math.Min(i + batchSize, annotations.Count):N0} / {annotations.Count:N0} annotations inserted");
                }
            }

            Console.WriteLine("Protein-GO annotations insertion complete.");
        }

        public async Task<(GoSlimDatabase goSlimDatabase, GoAnnotationDatabase annotationDatabase)> LoadGoAnnotationsAsync(string databasePath)
        {
            if (!File.Exists(databasePath))
                throw new FileNotFoundException("Reference database not found", databasePath);

            var goSlimDatabase = new GoSlimDatabase();
            var annotationDatabase = new GoAnnotationDatabase();
            var connectionString = $"Data Source={databasePath};Mode=ReadOnly";

            using (var connection = new SqliteConnection(connectionString))
            {
                await connection.OpenAsync();

                // Load GO terms
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = "SELECT go_id, name, namespace FROM go_terms";

                    using (var reader = await command.ExecuteReaderAsync())
                    {
                        while (await reader.ReadAsync())
                        {
                            var goId = reader.GetString(0);
                            var name = reader.GetString(1);
                            var namespace_ = reader.GetString(2);

                            goSlimDatabase.Terms[goId] = new GoTerm
                            {
                                Id = goId,
                                Name = name,
                                Namespace = namespace_
                            };
                        }
                    }
                }

                // Load protein annotations
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = "SELECT protein_id, go_term_id FROM protein_go_annotations ORDER BY protein_id";

                    using (var reader = await command.ExecuteReaderAsync())
                    {
                        while (await reader.ReadAsync())
                        {
                            var proteinId = reader.GetString(0);
                            var goTermId = reader.GetString(1);

                            if (!annotationDatabase.ProteinToGoTerms.ContainsKey(proteinId))
                                annotationDatabase.ProteinToGoTerms[proteinId] = new List<string>();
                            annotationDatabase.ProteinToGoTerms[proteinId].Add(goTermId);

                            if (!annotationDatabase.GoTermToProteins.ContainsKey(goTermId))
                                annotationDatabase.GoTermToProteins[goTermId] = new List<string>();
                            annotationDatabase.GoTermToProteins[goTermId].Add(proteinId);
                        }
                    }
                }
            }

            return (goSlimDatabase, annotationDatabase);
        }
    }
}