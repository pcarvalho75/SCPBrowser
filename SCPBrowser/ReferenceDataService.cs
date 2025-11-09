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




        // ==================== TRANSCRIPTOMIC DATA ====================

        /// <summary>
        /// Clears all transcriptomic data (gene expression and cell metadata) from the database
        /// </summary>
        /// <summary>
        /// Clears all transcriptomic data (gene expression, cell metadata, and lookup tables) from the database
        /// REPLACE the existing ClearTranscriptomicDataAsync method in ReferenceDataService.cs with this version
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
                DELETE FROM genes;
                DELETE FROM cells;
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

      


      


        private async Task InsertGeneLookupAsync(SqliteConnection connection, GeneLookup geneLookup, IProgressReporter progress = null)
        {
            const int batchSize = 1000;
            var allGenes = geneLookup.GetAllGenes();

            progress?.ReportMessage($"Inserting {allGenes.Count:N0} genes into lookup table...");

            var geneList = new List<(int id, string name)>();
            foreach (var kvp in allGenes)
            {
                geneList.Add((kvp.Key, kvp.Value));
            }

            for (int i = 0; i < geneList.Count; i += batchSize)
            {
                var batch = geneList.Skip(i).Take(batchSize).ToList();

                using (var transaction = connection.BeginTransaction())
                {
                    using (var command = connection.CreateCommand())
                    {
                        var valuesClauses = new List<string>();
                        var parameters = new List<SqliteParameter>();

                        for (int j = 0; j < batch.Count; j++)
                        {
                            var gene = batch[j];
                            var paramPrefix = $"@g{j}_";

                            valuesClauses.Add($"({paramPrefix}id, {paramPrefix}name)");

                            parameters.Add(new SqliteParameter($"{paramPrefix}id", gene.id));
                            parameters.Add(new SqliteParameter($"{paramPrefix}name", gene.name));
                        }

                        command.CommandText = $@"
                    INSERT OR REPLACE INTO genes (gene_id, gene_name)
                    VALUES {string.Join(", ", valuesClauses)}
                ";

                        command.Parameters.AddRange(parameters.ToArray());
                        await command.ExecuteNonQueryAsync();
                    }

                    await transaction.CommitAsync();
                }

                if ((i + batchSize) % 5000 == 0 || i + batchSize >= geneList.Count)
                {
                    progress?.ReportProgress($"Gene lookup: {Math.Min(i + batchSize, geneList.Count):N0} / {geneList.Count:N0}");
                }
            }

            progress?.ReportProgress("Gene lookup table complete");
        }

        private async Task InsertCellLookupAsync(SqliteConnection connection, CellLookup cellLookup, IProgressReporter progress = null)
        {
            const int batchSize = 1000;
            var allCells = cellLookup.GetAllCells();

            progress?.ReportMessage($"Inserting {allCells.Count:N0} cells into lookup table...");

            var cellList = new List<(int id, string name)>();
            foreach (var kvp in allCells)
            {
                cellList.Add((kvp.Key, kvp.Value));
            }

            for (int i = 0; i < cellList.Count; i += batchSize)
            {
                var batch = cellList.Skip(i).Take(batchSize).ToList();

                using (var transaction = connection.BeginTransaction())
                {
                    using (var command = connection.CreateCommand())
                    {
                        var valuesClauses = new List<string>();
                        var parameters = new List<SqliteParameter>();

                        for (int j = 0; j < batch.Count; j++)
                        {
                            var cell = batch[j];
                            var paramPrefix = $"@c{j}_";

                            valuesClauses.Add($"({paramPrefix}id, {paramPrefix}name)");

                            parameters.Add(new SqliteParameter($"{paramPrefix}id", cell.id));
                            parameters.Add(new SqliteParameter($"{paramPrefix}name", cell.name));
                        }

                        command.CommandText = $@"
                    INSERT OR REPLACE INTO cells (cell_id, cell_name)
                    VALUES {string.Join(", ", valuesClauses)}
                ";

                        command.Parameters.AddRange(parameters.ToArray());
                        await command.ExecuteNonQueryAsync();
                    }

                    await transaction.CommitAsync();
                }

                if ((i + batchSize) % 5000 == 0 || i + batchSize >= cellList.Count)
                {
                    progress?.ReportProgress($"Cell lookup: {Math.Min(i + batchSize, cellList.Count):N0} / {cellList.Count:N0}");
                }
            }

            progress?.ReportProgress("Cell lookup table complete");
        }


        // ====================================================================
        // REPLACE THESE METHODS IN ReferenceDataService.cs
        // Key optimizations: 50K batch size + indexes created AFTER inserts
        // ====================================================================

        private async Task CreateSchemaAsync(SqliteConnection connection)
        {
            using (var command = connection.CreateCommand())
            {
                command.CommandText = @"
            -- Gene lookup table (converts gene names to integer IDs)
            CREATE TABLE IF NOT EXISTS genes (
                gene_id INTEGER PRIMARY KEY,
                gene_name TEXT UNIQUE NOT NULL
            );

            -- Cell lookup table (converts cell IDs to integer IDs)
            CREATE TABLE IF NOT EXISTS cells (
                cell_id INTEGER PRIMARY KEY,
                cell_name TEXT UNIQUE NOT NULL
            );

            -- Sparse gene expression matrix using integer foreign keys
            -- NO INDEXES YET - we'll create them after all data is inserted
            CREATE TABLE IF NOT EXISTS gene_expression (
                gene_id INTEGER NOT NULL,
                cell_id INTEGER NOT NULL,
                count INTEGER NOT NULL,
                PRIMARY KEY (gene_id, cell_id),
                FOREIGN KEY (gene_id) REFERENCES genes(gene_id),
                FOREIGN KEY (cell_id) REFERENCES cells(cell_id)
            ) WITHOUT ROWID;

            -- Cell metadata (still uses text cell_name as primary key for backwards compatibility)
            CREATE TABLE IF NOT EXISTS cell_metadata (
                cell_name TEXT PRIMARY KEY,
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

            -- Indexes for non-expression tables (small, so create them now)
            CREATE INDEX IF NOT EXISTS idx_cell_type ON cell_metadata(cell_type);
            CREATE INDEX IF NOT EXISTS idx_protein_go ON protein_go_annotations(protein_id);
            CREATE INDEX IF NOT EXISTS idx_go_protein ON protein_go_annotations(go_term_id);
        ";

                await command.ExecuteNonQueryAsync();
            }
        }

        private async Task CreateGeneExpressionIndexesAsync(SqliteConnection connection, IProgressReporter progress = null)
        {
            progress?.ReportMessage("Creating indexes for fast queries...");
            progress?.ReportProgress("This may take a few minutes but only happens once");

            using (var command = connection.CreateCommand())
            {
                // Create gene index
                progress?.ReportProgress("Creating gene_id index...");
                command.CommandText = "CREATE INDEX IF NOT EXISTS idx_gene_expr_gene ON gene_expression(gene_id);";
                await command.ExecuteNonQueryAsync();

                // Create cell index
                progress?.ReportProgress("Creating cell_id index...");
                command.CommandText = "CREATE INDEX IF NOT EXISTS idx_gene_expr_cell ON gene_expression(cell_id);";
                await command.ExecuteNonQueryAsync();
            }

            progress?.ReportProgress("Indexes created successfully");
        }

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

                // Ensure tables exist
                progress?.ReportMessage("Creating database schema...");
                await CreateSchemaAsync(connection);

                // Clear existing data if requested
                if (clearExistingData)
                {
                    progress?.ReportMessage("Clearing existing transcriptomic data...");
                    using (var command = connection.CreateCommand())
                    {
                        command.CommandText = @"
                    DELETE FROM gene_expression;
                    DELETE FROM cell_metadata;
                    DELETE FROM genes;
                    DELETE FROM cells;
                    DROP INDEX IF EXISTS idx_gene_expr_gene;
                    DROP INDEX IF EXISTS idx_gene_expr_cell;
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

                // Insert lookup tables first
                await InsertGeneLookupAsync(connection, parsedData.GeneLookup, progress);
                await InsertCellLookupAsync(connection, parsedData.CellLookup, progress);

                // Insert gene expression data (WITHOUT indexes for speed)
                await InsertGeneExpressionAsync(connection, parsedData.ExpressionRecords, progress);

                // Insert cell metadata
                await InsertCellMetadataAsync(connection, parsedData.Metadata, progress);

                // NOW create the indexes after all data is inserted
                await CreateGeneExpressionIndexesAsync(connection, progress);

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

        // ====================================================================
        // REPLACE InsertGeneExpressionAsync in ReferenceDataService.cs
        // Uses nested loops: outer = 100K transaction boundary, inner = 10K batches
        // ====================================================================

        private async Task InsertGeneExpressionAsync(SqliteConnection connection, List<GeneExpressionRecord> records, IProgressReporter progress = null)
        {
            const int innerBatchSize = 10000;  // Parameter limit safety (30K params per INSERT)
            const int outerBatchSize = 100000; // Transaction boundary (commit every 100K records)

            progress?.ReportMessage($"Inserting {records.Count:N0} gene expression records...");
            progress?.ReportProgress($"Using {outerBatchSize:N0} records per transaction, {innerBatchSize:N0} per batch");

            // Outer loop: 100K records per transaction
            for (int outerIndex = 0; outerIndex < records.Count; outerIndex += outerBatchSize)
            {
                using (var transaction = connection.BeginTransaction())
                {
                    // Inner loop: 10K records per INSERT statement
                    int endOfOuterBatch = Math.Min(outerIndex + outerBatchSize, records.Count);

                    for (int innerIndex = outerIndex; innerIndex < endOfOuterBatch; innerIndex += innerBatchSize)
                    {
                        var batch = records.Skip(innerIndex).Take(innerBatchSize).ToList();

                        using (var command = connection.CreateCommand())
                        {
                            var valuesClauses = new List<string>();
                            var parameters = new List<SqliteParameter>();

                            for (int j = 0; j < batch.Count; j++)
                            {
                                var record = batch[j];
                                var paramPrefix = $"@p{j}_";

                                valuesClauses.Add($"({paramPrefix}gene_id, {paramPrefix}cell_id, {paramPrefix}count)");

                                parameters.Add(new SqliteParameter($"{paramPrefix}gene_id", record.GeneId));
                                parameters.Add(new SqliteParameter($"{paramPrefix}cell_id", record.CellId));
                                parameters.Add(new SqliteParameter($"{paramPrefix}count", record.Count));
                            }

                            command.CommandText = $@"
                        INSERT OR IGNORE INTO gene_expression (gene_id, cell_id, count)
                        VALUES {string.Join(", ", valuesClauses)}
                    ";

                            command.Parameters.AddRange(parameters.ToArray());
                            await command.ExecuteNonQueryAsync();
                        }
                    }

                    // Commit after 100K records
                    await transaction.CommitAsync();

                    // Progress reporting after each outer batch (100K)
                    var current = Math.Min(outerIndex + outerBatchSize, records.Count);
                    var percentage = (current * 100.0 / records.Count);
                    progress?.ReportProgress($"Expression data: {current:N0} / {records.Count:N0} ({percentage:F1}%)");
                }
            }

            progress?.ReportProgress("Gene expression data insertion complete");
        }


        private async Task InsertCellMetadataAsync(SqliteConnection connection, List<CellMetadata> metadata, IProgressReporter progress = null)
        {
            const int batchSize = 500;

            progress?.ReportMessage($"Inserting {metadata.Count:N0} cell metadata records...");

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
                            var paramPrefix = $"@m{j}_";

                            valuesClauses.Add($"({paramPrefix}cell_name, {paramPrefix}age, {paramPrefix}sex, " +
                                            $"{paramPrefix}batch, {paramPrefix}cell_type, {paramPrefix}genes_detected, " +
                                            $"{paramPrefix}total_reads, {paramPrefix}mapped_reads, {paramPrefix}mapping_rate)");

                            parameters.Add(new SqliteParameter($"{paramPrefix}cell_name", cell.CellID));
                            parameters.Add(new SqliteParameter($"{paramPrefix}age",
                                string.IsNullOrEmpty(cell.Age) ? (object)DBNull.Value : cell.Age));
                            parameters.Add(new SqliteParameter($"{paramPrefix}sex",
                                string.IsNullOrEmpty(cell.Sex) ? (object)DBNull.Value : cell.Sex));
                            parameters.Add(new SqliteParameter($"{paramPrefix}batch",
                                string.IsNullOrEmpty(cell.Batch) ? (object)DBNull.Value : cell.Batch));
                            parameters.Add(new SqliteParameter($"{paramPrefix}cell_type",
                                string.IsNullOrEmpty(cell.CellType) ? (object)DBNull.Value : cell.CellType));
                            parameters.Add(new SqliteParameter($"{paramPrefix}genes_detected",
                                cell.GenesDetected.HasValue ? (object)cell.GenesDetected.Value : DBNull.Value));
                            parameters.Add(new SqliteParameter($"{paramPrefix}total_reads",
                                cell.TotalReads.HasValue ? (object)cell.TotalReads.Value : DBNull.Value));
                            parameters.Add(new SqliteParameter($"{paramPrefix}mapped_reads",
                                cell.MappedReads.HasValue ? (object)cell.MappedReads.Value : DBNull.Value));
                            parameters.Add(new SqliteParameter($"{paramPrefix}mapping_rate",
                                cell.MappingRate.HasValue ? (object)cell.MappingRate.Value : DBNull.Value));
                        }

                        command.CommandText = $@"
                    INSERT OR REPLACE INTO cell_metadata 
                    (cell_name, age, sex, batch, cell_type, genes_detected, total_reads, mapped_reads, mapping_rate)
                    VALUES {string.Join(", ", valuesClauses)}
                ";

                        command.Parameters.AddRange(parameters.ToArray());
                        await command.ExecuteNonQueryAsync();
                    }

                    await transaction.CommitAsync();
                }

                if ((i + batchSize) % 2500 == 0 || i + batchSize >= metadata.Count)
                {
                    progress?.ReportProgress($"Cell metadata: {Math.Min(i + batchSize, metadata.Count):N0} / {metadata.Count:N0}");
                }
            }

            progress?.ReportProgress("Cell metadata complete");
        }

        public async Task<TranscriptomicDatabase> LoadTranscriptomicDataAsync(string databasePath, IProgressReporter progress = null)
        {
            if (!File.Exists(databasePath))
                throw new FileNotFoundException("Reference database not found", databasePath);

            var database = new TranscriptomicDatabase();
            var connectionString = $"Data Source={databasePath};Mode=ReadOnly";

            using (var connection = new SqliteConnection(connectionString))
            {
                await connection.OpenAsync();

                // Load gene lookup table
                progress?.ReportMessage("Loading gene lookup table...");
                var geneLookup = new Dictionary<int, string>();
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = "SELECT gene_id, gene_name FROM genes";
                    using (var reader = await command.ExecuteReaderAsync())
                    {
                        while (await reader.ReadAsync())
                        {
                            geneLookup[reader.GetInt32(0)] = reader.GetString(1);
                        }
                    }
                }
                progress?.ReportProgress($"Loaded {geneLookup.Count:N0} genes");

                // Load cell lookup table
                progress?.ReportMessage("Loading cell lookup table...");
                var cellLookup = new Dictionary<int, string>();
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = "SELECT cell_id, cell_name FROM cells";
                    using (var reader = await command.ExecuteReaderAsync())
                    {
                        while (await reader.ReadAsync())
                        {
                            cellLookup[reader.GetInt32(0)] = reader.GetString(1);
                        }
                    }
                }
                progress?.ReportProgress($"Loaded {cellLookup.Count:N0} cells");

                // Load cell metadata
                progress?.ReportMessage("Loading cell metadata...");
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = @"
                SELECT cell_name, age, sex, batch, cell_type, genes_detected, 
                       total_reads, mapped_reads, mapping_rate
                FROM cell_metadata
            ";

                    using (var reader = await command.ExecuteReaderAsync())
                    {
                        while (await reader.ReadAsync())
                        {
                            var cellName = reader.GetString(0);
                            var metadata = new CellMetadata
                            {
                                CellID = cellName,
                                Age = reader.IsDBNull(1) ? null : reader.GetString(1),
                                Sex = reader.IsDBNull(2) ? null : reader.GetString(2),
                                Batch = reader.IsDBNull(3) ? null : reader.GetString(3),
                                CellType = reader.IsDBNull(4) ? null : reader.GetString(4),
                                GenesDetected = reader.IsDBNull(5) ? null : reader.GetInt32(5),
                                TotalReads = reader.IsDBNull(6) ? null : reader.GetInt64(6),
                                MappedReads = reader.IsDBNull(7) ? null : reader.GetInt64(7),
                                MappingRate = reader.IsDBNull(8) ? null : reader.GetDouble(8)
                            };

                            database.CellMetadata[cellName] = metadata;

                            if (!string.IsNullOrEmpty(metadata.CellType))
                            {
                                if (!database.CellTypeIndex.ContainsKey(metadata.CellType))
                                    database.CellTypeIndex[metadata.CellType] = new List<string>();
                                database.CellTypeIndex[metadata.CellType].Add(cellName);
                            }
                        }
                    }
                }
                progress?.ReportProgress($"Loaded {database.CellMetadata.Count:N0} cell metadata records");

                // Load gene expression using integer IDs, convert back to names
                progress?.ReportMessage("Loading gene expression data...");
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = @"
                SELECT gene_id, cell_id, count
                FROM gene_expression
                ORDER BY gene_id
            ";

                    using (var reader = await command.ExecuteReaderAsync())
                    {
                        int recordCount = 0;
                        while (await reader.ReadAsync())
                        {
                            var geneId = reader.GetInt32(0);
                            var cellId = reader.GetInt32(1);
                            var count = reader.GetInt32(2);

                            // Convert IDs back to names
                            if (geneLookup.TryGetValue(geneId, out string geneName) &&
                                cellLookup.TryGetValue(cellId, out string cellName))
                            {
                                if (!database.GeneExpression.ContainsKey(geneName))
                                    database.GeneExpression[geneName] = new List<(string cellId, int count)>();

                                database.GeneExpression[geneName].Add((cellName, count));
                                recordCount++;

                                if (recordCount % 100000 == 0)
                                {
                                    progress?.ReportProgress($"Loaded {recordCount:N0} expression records...");
                                }
                            }
                        }
                        progress?.ReportProgress($"Loaded {recordCount:N0} total expression records");
                    }
                }
            }

            progress?.ReportMessage("Database loaded successfully");
            return database;
        }








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