using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;

namespace SCPBrowser.Services
{
    /// <summary>
    /// Service for managing reference data (GO annotations and transcriptomic data)
    /// within the project database. No longer creates separate databases - works with
    /// the unified project database created by ProjectDataService.
    /// </summary>
    public class ReferenceDataService
    {
        // ==================== PROTEOMICS REFERENCE (.pref) ====================

        /// <summary>
        /// Loads a proteomics reference from a .pref file and converts it to TranscriptomicDatabase format
        /// </summary>
        public async Task<TranscriptomicDatabase> LoadProteomicsReferenceAsync(string prefFilePath, IProgressReporter progress = null)
        {
            if (!File.Exists(prefFilePath))
                throw new FileNotFoundException("Proteomics reference file not found", prefFilePath);

            progress?.ReportMessage($"Loading proteomics reference from {Path.GetFileName(prefFilePath)}...");

            var database = new TranscriptomicDatabase();
            var metadata = new Dictionary<string, string>();
            var expressionMatrix = new Dictionary<string, Dictionary<string, double>>();
            var detectionMatrix = new Dictionary<string, Dictionary<string, double>>();
            var cellTypes = new List<string>();

            string currentSection = null;

            var lines = await Task.Run(() => File.ReadAllLines(prefFilePath));

            foreach (var line in lines)
            {
                if (string.IsNullOrWhiteSpace(line))
                    continue;

                // Parse metadata headers
                if (line.StartsWith("##"))
                {
                    if (line.StartsWith("##SECTION:"))
                    {
                        currentSection = line.Substring(10).Trim();
                        continue;
                    }

                    if (line.Contains(":"))
                    {
                        var parts = line.Substring(2).Split(new[] { ':' }, 2);
                        if (parts.Length == 2)
                        {
                            metadata[parts[0].Trim()] = parts[1].Trim();
                        }
                    }
                    continue;
                }

                // Parse data sections
                var columns = line.Split('\t');
                if (columns.Length < 2)
                    continue;

                // Header row
                if (columns[0] == "Protein" && cellTypes.Count == 0)
                {
                    cellTypes.AddRange(columns.Skip(1));
                    continue;
                }

                // Data rows
                var protein = columns[0];

                if (currentSection == "EXPRESSION")
                {
                    var exprRow = new Dictionary<string, double>();
                    for (int i = 1; i < columns.Length && i <= cellTypes.Count; i++)
                    {
                        if (double.TryParse(columns[i], NumberStyles.Any, CultureInfo.InvariantCulture, out var value))
                        {
                            exprRow[cellTypes[i - 1]] = value;
                        }
                    }
                    expressionMatrix[protein] = exprRow;
                }
                else if (currentSection == "DETECTION")
                {
                    var detRow = new Dictionary<string, double>();
                    for (int i = 1; i < columns.Length && i <= cellTypes.Count; i++)
                    {
                        if (double.TryParse(columns[i], NumberStyles.Any, CultureInfo.InvariantCulture, out var value))
                        {
                            detRow[cellTypes[i - 1]] = value;
                        }
                    }
                    detectionMatrix[protein] = detRow;
                }
            }

            progress?.ReportMessage($"Building cell type profiles for {cellTypes.Count} cell types, {expressionMatrix.Count} proteins...");

            // Convert to CellTypeProfile format
            foreach (var cellType in cellTypes)
            {
                var profile = new CellTypeProfile
                {
                    CellType = cellType,
                    MedianExpression = new Dictionary<string, double>(),
                    MeanExpression = new Dictionary<string, double>(),
                    PercentExpressing = new Dictionary<string, double>()
                };

                foreach (var protein in expressionMatrix.Keys)
                {
                    if (expressionMatrix[protein].TryGetValue(cellType, out var expr))
                    {
                        profile.MedianExpression[protein] = expr;
                        profile.MeanExpression[protein] = expr; // Use median as mean for proteomics
                    }

                    if (detectionMatrix.TryGetValue(protein, out var detRow) &&
                        detRow.TryGetValue(cellType, out var detection))
                    {
                        profile.PercentExpressing[protein] = detection * 100; // Convert 0-1 to percentage
                    }
                }

                database.CellTypeProfiles[cellType] = profile;

                // Create metadata
                int cellCount = 0;
                if (metadata.TryGetValue("TotalCellsRetained", out var cellCountStr))
                {
                    int.TryParse(cellCountStr, out cellCount);
                    cellCount = cellCount / cellTypes.Count; // Approximate per-type count
                }

                database.CellTypeMetadata[cellType] = new CellTypeMetadata
                {
                    CellType = cellType,
                    CellCount = cellCount,
                    GenesExpressed = profile.MedianExpression.Count
                };
            }

            progress?.ReportProgress($"Loaded proteomics reference: {database.TotalCellTypes} cell types, {database.TotalGenes} proteins");

            return database;
        }

        /// <summary>
        /// Writes a proteomics reference to the project database
        /// </summary>
        public async Task WriteProteomicsReferenceAsync(
            string databasePath,
            string prefFilePath,
            bool clearExistingData = true,
            IProgressReporter progress = null)
        {
            // Load from .pref file
            var database = await LoadProteomicsReferenceAsync(prefFilePath, progress);

            // Convert to ParsedTranscriptomicData format
            var parsedData = new ParsedTranscriptomicData
            {
                CellTypeProfiles = database.CellTypeProfiles.Values.ToList(),
                CellTypeMetadata = database.CellTypeMetadata.Values.ToList()
            };

            // Write to database using existing method
            await WriteTranscriptomicDataAsync(databasePath, parsedData, clearExistingData, progress);
        }

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

                progress?.ReportMessage($"Database loaded: {database.TotalCellTypes} cell types, {database.TotalCells:N0} cells, {database.TotalGenes:N0} features");
            }

                        // Return null if no reference profiles have been imported yet (normal initial state)
                        if (database.CellTypeProfiles == null || database.CellTypeProfiles.Count == 0)
                            return null;

                        return database;
                    }

                    // ==================== PROTEOMIC REFERENCE FROM PARQUET ====================

                    /// <summary>
                    /// Builds CellTypeProfiles from a loaded parquet dataset and a run→cellType label map.
                    /// Uses Log2(intensity + 1) normalization, consistent with the PCA pipeline.
                    /// Gene names are extracted by stripping _HUMAN/_MOUSE suffixes from ProteinToGeneMap.
                    /// </summary>
                    public ParsedTranscriptomicData BuildProteomicReference(
                        ProteomicsData proteomicsData,
                        Dictionary<string, string> runCellTypeMap)
                    {
                        // Step 1: For each run, extract gene → log2(intensity+1) 
                        var runGeneAbundances = new Dictionary<string, Dictionary<string, double>>();

                        foreach (var runName in runCellTypeMap.Keys)
                        {
                            var abundances = new Dictionary<string, double>();

                            foreach (var proteinGroup in proteomicsData.ProteinQuantMatrix.Keys)
                            {
                                if (!proteomicsData.ProteinQuantMatrix[proteinGroup].TryGetValue(runName, out double intensity))
                                    continue;
                                if (intensity <= 0)
                                    continue;

                                double logValue = Math.Log2(intensity + 1);

                                var geneNames = GeneNameUtility.ExtractGeneNames(proteomicsData, proteinGroup);
                                foreach (var geneName in geneNames)
                                {
                                    if (!abundances.ContainsKey(geneName))
                                        abundances[geneName] = logValue;
                                    else
                                        abundances[geneName] = Math.Max(abundances[geneName], logValue);
                                }
                            }

                            runGeneAbundances[runName] = abundances;
                        }

                        // Step 2: Collect all gene names across all runs
                        var allGenes = runGeneAbundances.Values
                            .SelectMany(d => d.Keys)
                            .Distinct(StringComparer.OrdinalIgnoreCase)
                            .ToList();

                        // Step 3: Group runs by cell type
                        var cellTypeGroups = runCellTypeMap
                            .GroupBy(kv => kv.Value, StringComparer.OrdinalIgnoreCase)
                            .ToDictionary(g => g.Key, g => g.Select(kv => kv.Key).ToList());

                        // Step 4: Build profiles
                        var profiles = new List<CellTypeProfile>();
                        var metadata = new List<CellTypeMetadata>();

                        foreach (var kvp in cellTypeGroups.OrderBy(g => g.Key))
                        {
                            string cellType = kvp.Key;
                            var runs = kvp.Value;

                            var medianDict = new Dictionary<string, double>();
                            var meanDict = new Dictionary<string, double>();
                            var pctDict = new Dictionary<string, double>();

                            foreach (var gene in allGenes)
                            {
                                var detectedValues = new List<double>();
                                int totalRuns = runs.Count;

                                foreach (var run in runs)
                                {
                                    if (runGeneAbundances[run].TryGetValue(gene, out double val))
                                        detectedValues.Add(val);
                                }

                                // Skip genes not detected in any run of this cell type
                                // (matches TSV parser behavior — only store expressed genes)
                                if (detectedValues.Count == 0)
                                    continue;

                                double percentExpressing = (detectedValues.Count / (double)totalRuns) * 100.0;
                                pctDict[gene] = percentExpressing;

                                detectedValues.Sort();
                                double median = detectedValues.Count % 2 == 0
                                    ? (detectedValues[detectedValues.Count / 2 - 1] + detectedValues[detectedValues.Count / 2]) / 2.0
                                    : detectedValues[detectedValues.Count / 2];
                                double mean = detectedValues.Average();

                                medianDict[gene] = median;
                                meanDict[gene] = mean;
                            }

                            profiles.Add(new CellTypeProfile
                            {
                                CellType = cellType,
                                CellCount = runs.Count,
                                MedianExpression = medianDict,
                                MeanExpression = meanDict,
                                PercentExpressing = pctDict
                            });

                            metadata.Add(new CellTypeMetadata
                            {
                                CellType = cellType,
                                CellCount = runs.Count,
                                GenesExpressed = medianDict.Count,
                                BatchInfo = "parquet",
                                AgeRange = "",
                                PriorWeight = 1.0
                            });
                        }

                        return new ParsedTranscriptomicData
                        {
                            CellTypeProfiles = profiles,
                            CellTypeMetadata = metadata
                        };
                    }



                }
            }