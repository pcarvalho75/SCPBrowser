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
                    CREATE TABLE gene_expression (
                        gene_name TEXT NOT NULL,
                        cell_id TEXT NOT NULL,
                        count INTEGER NOT NULL,
                        PRIMARY KEY (gene_name, cell_id)
                    );

                    -- Cell metadata
                    CREATE TABLE cell_metadata (
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
                    CREATE TABLE go_terms (
                        go_id TEXT PRIMARY KEY,
                        name TEXT NOT NULL,
                        namespace TEXT NOT NULL
                    );

                    -- Protein to GO term annotations
                    CREATE TABLE protein_go_annotations (
                        protein_id TEXT NOT NULL,
                        go_term_id TEXT NOT NULL,
                        PRIMARY KEY (protein_id, go_term_id)
                    );

                    -- Indexes for fast queries
                    CREATE INDEX idx_gene_expr_gene ON gene_expression(gene_name);
                    CREATE INDEX idx_gene_expr_cell ON gene_expression(cell_id);
                    CREATE INDEX idx_cell_type ON cell_metadata(cell_type);
                    CREATE INDEX idx_protein_go ON protein_go_annotations(protein_id);
                    CREATE INDEX idx_go_protein ON protein_go_annotations(go_term_id);
                ";

                await command.ExecuteNonQueryAsync();
            }
        }

        // ==================== TRANSCRIPTOMIC DATA ====================

        public async Task WriteTranscriptomicDataAsync(
            string databasePath,
            List<GeneExpressionRecord> expressionRecords,
            List<CellMetadata> metadata)
        {
            var connectionString = $"Data Source={databasePath}";

            using (var connection = new SqliteConnection(connectionString))
            {
                await connection.OpenAsync();

                // Insert gene expression
                await InsertGeneExpressionAsync(connection, expressionRecords);

                // Insert cell metadata
                await InsertCellMetadataAsync(connection, metadata);
            }
        }

        private async Task InsertGeneExpressionAsync(SqliteConnection connection, List<GeneExpressionRecord> records)
        {
            using (var transaction = connection.BeginTransaction())
            {
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = @"
                        INSERT INTO gene_expression (gene_name, cell_id, count)
                        VALUES ($gene_name, $cell_id, $count)
                    ";

                    var paramGeneName = command.Parameters.Add("$gene_name", SqliteType.Text);
                    var paramCellId = command.Parameters.Add("$cell_id", SqliteType.Text);
                    var paramCount = command.Parameters.Add("$count", SqliteType.Integer);

                    foreach (var record in records)
                    {
                        paramGeneName.Value = record.GeneName;
                        paramCellId.Value = record.CellID;
                        paramCount.Value = record.Count;

                        await command.ExecuteNonQueryAsync();
                    }
                }

                await transaction.CommitAsync();
            }
        }

        private async Task InsertCellMetadataAsync(SqliteConnection connection, List<CellMetadata> metadata)
        {
            using (var transaction = connection.BeginTransaction())
            {
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = @"
                        INSERT INTO cell_metadata 
                        (cell_id, age, sex, batch, cell_type, genes_detected, total_reads, mapped_reads, mapping_rate)
                        VALUES ($cell_id, $age, $sex, $batch, $cell_type, $genes_detected, $total_reads, $mapped_reads, $mapping_rate)
                    ";

                    var paramCellId = command.Parameters.Add("$cell_id", SqliteType.Text);
                    var paramAge = command.Parameters.Add("$age", SqliteType.Text);
                    var paramSex = command.Parameters.Add("$sex", SqliteType.Text);
                    var paramBatch = command.Parameters.Add("$batch", SqliteType.Text);
                    var paramCellType = command.Parameters.Add("$cell_type", SqliteType.Text);
                    var paramGenesDetected = command.Parameters.Add("$genes_detected", SqliteType.Integer);
                    var paramTotalReads = command.Parameters.Add("$total_reads", SqliteType.Integer);
                    var paramMappedReads = command.Parameters.Add("$mapped_reads", SqliteType.Integer);
                    var paramMappingRate = command.Parameters.Add("$mapping_rate", SqliteType.Real);

                    foreach (var cell in metadata)
                    {
                        paramCellId.Value = cell.CellID;
                        paramAge.Value = (object)cell.Age ?? DBNull.Value;
                        paramSex.Value = (object)cell.Sex ?? DBNull.Value;
                        paramBatch.Value = (object)cell.Batch ?? DBNull.Value;
                        paramCellType.Value = (object)cell.CellType ?? DBNull.Value;
                        paramGenesDetected.Value = cell.GenesDetected.HasValue ? (object)cell.GenesDetected.Value : DBNull.Value;
                        paramTotalReads.Value = cell.TotalReads.HasValue ? (object)cell.TotalReads.Value : DBNull.Value;
                        paramMappedReads.Value = cell.MappedReads.HasValue ? (object)cell.MappedReads.Value : DBNull.Value;
                        paramMappingRate.Value = cell.MappingRate.HasValue ? (object)cell.MappingRate.Value : DBNull.Value;

                        await command.ExecuteNonQueryAsync();
                    }
                }

                await transaction.CommitAsync();
            }
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
            GoAnnotationDatabase annotationDatabase)
        {
            var connectionString = $"Data Source={databasePath}";

            using (var connection = new SqliteConnection(connectionString))
            {
                await connection.OpenAsync();

                // Insert GO terms
                await InsertGoTermsAsync(connection, goSlimDatabase);

                // Insert protein annotations
                await InsertProteinAnnotationsAsync(connection, annotationDatabase);
            }
        }

        private async Task InsertGoTermsAsync(SqliteConnection connection, GoSlimDatabase goSlimDatabase)
        {
            using (var transaction = connection.BeginTransaction())
            {
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = @"
                        INSERT INTO go_terms (go_id, name, namespace)
                        VALUES ($go_id, $name, $namespace)
                    ";

                    var paramGoId = command.Parameters.Add("$go_id", SqliteType.Text);
                    var paramName = command.Parameters.Add("$name", SqliteType.Text);
                    var paramNamespace = command.Parameters.Add("$namespace", SqliteType.Text);

                    foreach (var term in goSlimDatabase.Terms.Values)
                    {
                        paramGoId.Value = term.Id;
                        paramName.Value = term.Name ?? string.Empty;
                        paramNamespace.Value = term.Namespace ?? string.Empty;

                        await command.ExecuteNonQueryAsync();
                    }
                }

                await transaction.CommitAsync();
            }
        }

        private async Task InsertProteinAnnotationsAsync(SqliteConnection connection, GoAnnotationDatabase annotationDatabase)
        {
            using (var transaction = connection.BeginTransaction())
            {
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = @"
                        INSERT INTO protein_go_annotations (protein_id, go_term_id)
                        VALUES ($protein_id, $go_term_id)
                    ";

                    var paramProteinId = command.Parameters.Add("$protein_id", SqliteType.Text);
                    var paramGoTermId = command.Parameters.Add("$go_term_id", SqliteType.Text);

                    foreach (var proteinEntry in annotationDatabase.ProteinToGoTerms)
                    {
                        var proteinId = proteinEntry.Key;

                        foreach (var goTermId in proteinEntry.Value)
                        {
                            paramProteinId.Value = proteinId;
                            paramGoTermId.Value = goTermId;

                            await command.ExecuteNonQueryAsync();
                        }
                    }
                }

                await transaction.CommitAsync();
            }
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