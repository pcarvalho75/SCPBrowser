using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;

namespace SCPBrowser.Services
{
    /// <summary>
    /// Parses FASTA files and stores protein annotations in the database.
    /// Handles UniProt, NCBI, and generic FASTA header formats.
    /// </summary>
    public class FastaParserService : DatabaseServiceBase
    {
        public FastaParserService(string projectDbPath) : base(projectDbPath)
        {
        }

        /// <summary>
        /// Parsed protein annotation from FASTA header
        /// </summary>
        public class ProteinAnnotation
        {
            public string Accession { get; set; } = string.Empty;
            public string EntryName { get; set; } = string.Empty;
            public string ProteinName { get; set; } = string.Empty;
            public string GeneName { get; set; } = string.Empty;
            public string Organism { get; set; } = string.Empty;
            public string FullHeader { get; set; } = string.Empty;
            /// <summary>
            /// Source database: "sp" (SwissProt), "tr" (TrEMBL), or "other"
            /// </summary>
            public string Source { get; set; } = string.Empty;
        }

        /// <summary>
        /// Parses a FASTA file and stores annotations in the database.
        /// </summary>
        public async Task<int> ImportFastaAsync(string fastaPath, IProgress<string> progress = null)
        {
            if (!File.Exists(fastaPath))
                throw new FileNotFoundException("FASTA file not found", fastaPath);

            progress?.Report("Parsing FASTA headers...");

            var annotations = new List<ProteinAnnotation>();
            int lineCount = 0;

            using (var reader = new StreamReader(fastaPath))
            {
                string line;
                while ((line = await reader.ReadLineAsync()) != null)
                {
                    lineCount++;

                    if (line.StartsWith(">"))
                    {
                        var annotation = ParseHeader(line);
                        if (annotation != null && !string.IsNullOrEmpty(annotation.Accession))
                        {
                            annotations.Add(annotation);
                        }

                        if (annotations.Count % 10000 == 0)
                        {
                            progress?.Report($"Parsed {annotations.Count:N0} proteins...");
                        }
                    }
                }
            }

            progress?.Report($"Storing {annotations.Count:N0} annotations in database...");
            await StoreAnnotationsAsync(annotations, progress);

            return annotations.Count;
        }

        /// <summary>
        /// Parses a FASTA header line into a ProteinAnnotation object.
        /// Supports UniProt, NCBI, and generic formats.
        /// </summary>
        public ProteinAnnotation ParseHeader(string headerLine)
        {
            if (string.IsNullOrWhiteSpace(headerLine))
                return null;

            // Remove leading '>'
            string header = headerLine.TrimStart('>').Trim();

            var annotation = new ProteinAnnotation { FullHeader = header };

            // Try UniProt format: >sp|P36578|RL4_HUMAN 60S ribosomal protein L4 OS=Homo sapiens OX=9606 GN=RPL4 PE=1 SV=5
            // Or: >tr|A0A123|A0A123_HUMAN Description OS=...
            var uniprotMatch = Regex.Match(header, @"^(sp|tr)\|([A-Za-z0-9_-]+)\|([A-Za-z0-9_]+)\s+(.*)$");
            if (uniprotMatch.Success)
            {
                annotation.Source = uniprotMatch.Groups[1].Value.ToLower(); // "sp" or "tr"
                annotation.Accession = uniprotMatch.Groups[2].Value;
                annotation.EntryName = uniprotMatch.Groups[3].Value;

                string remainder = uniprotMatch.Groups[4].Value;
                ParseUniProtFields(remainder, annotation);
                return annotation;
            }

            // Try simple UniProt without sp|tr prefix: >P36578|RL4_HUMAN Description
            var simpleUniprotMatch = Regex.Match(header, @"^([A-Za-z0-9_-]+)\|([A-Za-z0-9_]+)\s+(.*)$");
            if (simpleUniprotMatch.Success)
            {
                annotation.Source = "other";
                annotation.Accession = simpleUniprotMatch.Groups[1].Value;
                annotation.EntryName = simpleUniprotMatch.Groups[2].Value;

                string remainder = simpleUniprotMatch.Groups[3].Value;
                ParseUniProtFields(remainder, annotation);
                return annotation;
            }

            // Try NCBI format: >gi|123456|ref|NP_001234.1| protein description [Homo sapiens]
            var ncbiMatch = Regex.Match(header, @"^gi\|[0-9]+\|[a-z]+\|([A-Za-z0-9_.]+)\|?\s*(.*)$");
            if (ncbiMatch.Success)
            {
                annotation.Accession = ncbiMatch.Groups[1].Value;
                string remainder = ncbiMatch.Groups[2].Value;

                // Extract organism from [brackets]
                var orgMatch = Regex.Match(remainder, @"\[([^\]]+)\]\s*$");
                if (orgMatch.Success)
                {
                    annotation.Organism = orgMatch.Groups[1].Value;
                    annotation.ProteinName = remainder.Substring(0, orgMatch.Index).Trim();
                }
                else
                {
                    annotation.ProteinName = remainder.Trim();
                }
                return annotation;
            }

            // Generic format: >accession description
            var genericMatch = Regex.Match(header, @"^([^\s]+)\s*(.*)$");
            if (genericMatch.Success)
            {
                annotation.Accession = genericMatch.Groups[1].Value;
                annotation.ProteinName = genericMatch.Groups[2].Value.Trim();

                // Try to extract organism from [brackets]
                var orgMatch = Regex.Match(annotation.ProteinName, @"\[([^\]]+)\]\s*$");
                if (orgMatch.Success)
                {
                    annotation.Organism = orgMatch.Groups[1].Value;
                    annotation.ProteinName = annotation.ProteinName.Substring(0, orgMatch.Index).Trim();
                }

                return annotation;
            }

            return annotation;
        }

        /// <summary>
        /// Parses UniProt-style fields (OS=, GN=, etc.) from the description portion.
        /// </summary>
        private void ParseUniProtFields(string text, ProteinAnnotation annotation)
        {
            // Extract protein name (everything before first OS=, GN=, PE=, SV=, OX=)
            var nameMatch = Regex.Match(text, @"^(.*?)(?:\s+(?:OS|GN|PE|SV|OX)=|$)");
            if (nameMatch.Success)
            {
                annotation.ProteinName = nameMatch.Groups[1].Value.Trim();
            }

            // Extract OS (Organism)
            var osMatch = Regex.Match(text, @"OS=([^=]+?)(?:\s+(?:OX|GN|PE|SV)=|$)");
            if (osMatch.Success)
            {
                annotation.Organism = osMatch.Groups[1].Value.Trim();
            }

            // Extract GN (Gene Name)
            var gnMatch = Regex.Match(text, @"GN=([^\s]+)");
            if (gnMatch.Success)
            {
                annotation.GeneName = gnMatch.Groups[1].Value.Trim();
            }
        }

        /// <summary>
        /// Stores annotations in the database using batch insert.
        /// </summary>
        private async Task StoreAnnotationsAsync(List<ProteinAnnotation> annotations, IProgress<string> progress = null)
        {
            await WithConnectionAsync(async connection =>
            {
                using (var transaction = connection.BeginTransaction())
                {
                    try
                    {
                        using (var clearCmd = connection.CreateCommand())
                        {
                            clearCmd.CommandText = "DELETE FROM protein_annotations";
                            await clearCmd.ExecuteNonQueryAsync();
                        }

                        using (var insertCmd = connection.CreateCommand())
                        {
                            insertCmd.CommandText = @"
                                INSERT OR REPLACE INTO protein_annotations 
                                (accession, entry_name, protein_name, gene_name, organism, full_header, source)
                                VALUES (@acc, @entry, @protein, @gene, @org, @header, @source)";

                            var accParam = insertCmd.Parameters.Add("@acc", SqliteType.Text);
                            var entryParam = insertCmd.Parameters.Add("@entry", SqliteType.Text);
                            var proteinParam = insertCmd.Parameters.Add("@protein", SqliteType.Text);
                            var geneParam = insertCmd.Parameters.Add("@gene", SqliteType.Text);
                            var orgParam = insertCmd.Parameters.Add("@org", SqliteType.Text);
                            var headerParam = insertCmd.Parameters.Add("@header", SqliteType.Text);
                            var sourceParam = insertCmd.Parameters.Add("@source", SqliteType.Text);

                            int count = 0;
                            foreach (var ann in annotations)
                            {
                                accParam.Value = ann.Accession ?? (object)DBNull.Value;
                                entryParam.Value = ann.EntryName ?? (object)DBNull.Value;
                                proteinParam.Value = ann.ProteinName ?? (object)DBNull.Value;
                                geneParam.Value = ann.GeneName ?? (object)DBNull.Value;
                                orgParam.Value = ann.Organism ?? (object)DBNull.Value;
                                headerParam.Value = ann.FullHeader ?? (object)DBNull.Value;
                                sourceParam.Value = ann.Source ?? (object)DBNull.Value;

                                await insertCmd.ExecuteNonQueryAsync();

                                count++;
                                if (count % 5000 == 0)
                                    progress?.Report($"Stored {count:N0} / {annotations.Count:N0} annotations...");
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
            });
        }

        /// <summary>
        /// Gets annotation for a protein accession.
        /// </summary>
        public async Task<ProteinAnnotation> GetAnnotationAsync(string accession)
        {
            if (string.IsNullOrWhiteSpace(accession))
                return null;

            string firstAccession = accession.Split(';')[0].Trim();

            var results = await QueryAsync(@"
                SELECT accession, entry_name, protein_name, gene_name, organism, full_header, source
                FROM protein_annotations WHERE accession = @acc",
                reader => new ProteinAnnotation
                {
                    Accession = reader.IsDBNull(0) ? null : reader.GetString(0),
                    EntryName = reader.IsDBNull(1) ? null : reader.GetString(1),
                    ProteinName = reader.IsDBNull(2) ? null : reader.GetString(2),
                    GeneName = reader.IsDBNull(3) ? null : reader.GetString(3),
                    Organism = reader.IsDBNull(4) ? null : reader.GetString(4),
                    FullHeader = reader.IsDBNull(5) ? null : reader.GetString(5),
                    Source = reader.IsDBNull(6) ? null : reader.GetString(6)
                },
                cmd => cmd.Parameters.AddWithValue("@acc", firstAccession));

            return results.FirstOrDefault();
        }

        /// <summary>
        /// Gets the count of stored annotations.
        /// </summary>
        public async Task<int> GetAnnotationCountAsync()
        {
            return await ExecuteScalarAsync<int>("SELECT COUNT(*) FROM protein_annotations");
        }

        /// <summary>
        /// Loads all annotations into a dictionary for fast lookup.
        /// </summary>
        public async Task<Dictionary<string, ProteinAnnotation>> GetAllAnnotationsAsync()
        {
            var list = await QueryAsync(@"
                SELECT accession, entry_name, protein_name, gene_name, organism, full_header, source
                FROM protein_annotations",
                reader => new ProteinAnnotation
                {
                    Accession = reader.IsDBNull(0) ? null : reader.GetString(0),
                    EntryName = reader.IsDBNull(1) ? null : reader.GetString(1),
                    ProteinName = reader.IsDBNull(2) ? null : reader.GetString(2),
                    GeneName = reader.IsDBNull(3) ? null : reader.GetString(3),
                    Organism = reader.IsDBNull(4) ? null : reader.GetString(4),
                    FullHeader = reader.IsDBNull(5) ? null : reader.GetString(5),
                    Source = reader.IsDBNull(6) ? null : reader.GetString(6)
                });

            var annotations = new Dictionary<string, ProteinAnnotation>(StringComparer.OrdinalIgnoreCase);
            foreach (var ann in list)
            {
                if (!string.IsNullOrEmpty(ann.Accession))
                    annotations[ann.Accession] = ann;
            }
            return annotations;
        }
    }
}