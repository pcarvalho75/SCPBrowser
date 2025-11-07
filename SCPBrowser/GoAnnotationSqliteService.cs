using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;

namespace SCPBrowser
{
    public class GoAnnotationSqliteService
    {
        public async Task WriteCompiledAnnotationsAsync(
            GoSlimDatabase goSlimDatabase,
            GoAnnotationDatabase annotationDatabase,
            string outputPath)
        {
            // Delete existing file if it exists
            if (File.Exists(outputPath))
                File.Delete(outputPath);

            var connectionString = $"Data Source={outputPath}";

            using (var connection = new SqliteConnection(connectionString))
            {
                await connection.OpenAsync();

                // Create tables
                await CreateSchemaAsync(connection);

                // Insert GO terms
                await InsertGoTermsAsync(connection, goSlimDatabase);

                // Insert protein annotations
                await InsertProteinAnnotationsAsync(connection, annotationDatabase);
            }
        }

        private async Task CreateSchemaAsync(SqliteConnection connection)
        {
            using (var command = connection.CreateCommand())
            {
                command.CommandText = @"
                    CREATE TABLE go_terms (
                        go_id TEXT PRIMARY KEY,
                        name TEXT NOT NULL,
                        namespace TEXT NOT NULL
                    );

                    CREATE TABLE protein_go_annotations (
                        protein_id TEXT NOT NULL,
                        go_term_id TEXT NOT NULL,
                        PRIMARY KEY (protein_id, go_term_id)
                    );

                    CREATE INDEX idx_protein_id ON protein_go_annotations(protein_id);
                    CREATE INDEX idx_go_term_id ON protein_go_annotations(go_term_id);
                ";

                await command.ExecuteNonQueryAsync();
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

        public async Task<(GoSlimDatabase goSlimDatabase, GoAnnotationDatabase annotationDatabase)> ReadCompiledAnnotationsAsync(string filePath)
        {
            if (!File.Exists(filePath))
                throw new FileNotFoundException("Compiled GO annotations SQLite file not found", filePath);

            var goSlimDatabase = new GoSlimDatabase();
            var annotationDatabase = new GoAnnotationDatabase();

            var connectionString = $"Data Source={filePath};Mode=ReadOnly";

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

                            // Build protein -> GO terms mapping
                            if (!annotationDatabase.ProteinToGoTerms.ContainsKey(proteinId))
                            {
                                annotationDatabase.ProteinToGoTerms[proteinId] = new List<string>();
                            }
                            annotationDatabase.ProteinToGoTerms[proteinId].Add(goTermId);

                            // Build GO term -> proteins mapping
                            if (!annotationDatabase.GoTermToProteins.ContainsKey(goTermId))
                            {
                                annotationDatabase.GoTermToProteins[goTermId] = new List<string>();
                            }
                            annotationDatabase.GoTermToProteins[goTermId].Add(proteinId);
                        }
                    }
                }
            }

            return (goSlimDatabase, annotationDatabase);
        }
    }
}