using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Parquet;
using Parquet.Data;
using SCPBrowser.Models;

namespace SCPBrowser.Services
{
    /*
     * DIA-NN Parquet File Column Structure:
     * 
     * Run Information:
     *   - Run.Index, Run, Channel
     * 
     * Precursor/Peptide Information:
     *   - Precursor.Id, Modified.Sequence, Stripped.Sequence, Precursor.Charge
     *   - Precursor.Lib.Index, Decoy, Proteotypic, Precursor.Mz
     * 
     * Protein Information:
     *   - Protein.Ids, Protein.Group, Protein.Names, Genes
     * 
     * Retention Time & Ion Mobility:
     *   - RT, iRT, Predicted.RT, Predicted.iRT
     *   - IM, iIM, Predicted.IM, Predicted.iIM
     * 
     * Quantification:
     *   - Precursor.Quantity, Precursor.Normalised
     *   - Ms1.Area, Ms1.Normalised, Ms1.Apex.Area, Ms1.Apex.Mz.Delta
     *   - Normalisation.Factor
     *   - PG.TopN, PG.MaxLFQ, Genes.TopN, Genes.MaxLFQ, Genes.MaxLFQ.Unique
     * 
     * Quality Metrics:
     *   - Quantity.Quality, Empirical.Quality, Normalisation.Noise
     *   - Ms1.Profile.Corr, Evidence, Mass.Evidence, Channel.Evidence
     *   - Ms1.Total.Signal.Before, Ms1.Total.Signal.After
     *   - PG.MaxLFQ.Quality, Genes.MaxLFQ.Quality, Genes.MaxLFQ.Unique.Quality
     * 
     * Peak Information:
     *   - RT.Start, RT.Stop, FWHM
     * 
     * Statistical Confidence:
     *   - Q.Value, PEP, Global.Q.Value, Lib.Q.Value
     *   - Peptidoform.Q.Value, Global.Peptidoform.Q.Value, Lib.Peptidoform.Q.Value
     *   - Translated.Q.Value, Channel.Q.Value
     *   - PG.Q.Value, PG.PEP, GG.Q.Value, Protein.Q.Value
     *   - Global.PG.Q.Value, Lib.PG.Q.Value
     * 
     * PTM Information:
     *   - PTM.Site.Confidence, Site.Occupancy.Probabilities, Protein.Sites
     *   - Lib.PTM.Site.Confidence
     * 
     * Fragment Information:
     *   - Best.Fr.Mz, Best.Fr.Mz.Delta
     * 
     * For our analysis:
     *   - Raw File Column: "Run"
     *   - Protein Group Column: "Protein.Group"
     *   - Peptide Column: "Modified.Sequence" or "Stripped.Sequence"
     */

    public class ProteomicsData
    {
        public int TotalRawFiles { get; set; }
        public int TotalProteinGroups { get; set; }
        public int TotalPeptides { get; set; }
        public Dictionary<string, int> ProteinCountPerFile { get; set; } = new();
        public Dictionary<string, int> PeptideCountPerFile { get; set; } = new();
        public Dictionary<string, double> TotalIonCurrentPerFile { get; set; } = new();
        public Dictionary<string, double> TargetProteinRatioPerFile { get; set; } = new();
        public Dictionary<string, Dictionary<string, double>> ProteinQuantMatrix { get; set; } = new();

        public Dictionary<string, string> ProteinToGeneMap { get; set; } = new(); // protein group → gene names
        public List<string> RawFileNames { get; set; } = new();

        public Dictionary<string, string> BiologicalConditionPerFile { get; set; } = new();
        public HashSet<string> AllPeptideSequences { get; set; } = new();

        /// <summary>
        /// Protein groups marked as contaminants. Used to exclude from analyses (PCA, UMAP, classification, etc.)
        /// while still allowing contaminant ratio calculations.
        /// </summary>
        public HashSet<string> ContaminantIds { get; set; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// True when this data came from an Excel gene-matrix import (keyed by gene symbol, no peptide dimension —
        /// PeptideCount is substituted with the detected-gene count). Drives honest axis labels in the Explorer.
        /// </summary>
        public bool IsGeneMatrix { get; set; }
    }



    public class ColumnMapping
    {
        public string RawFileColumn { get; set; } = string.Empty;
        public string ProteinGroupColumn { get; set; } = string.Empty;
        public string PeptideColumn { get; set; } = string.Empty;
        public string TotalIonCurrentColumn { get; set; } = string.Empty;
        public List<string> TargetProteinIdentifiers { get; set; } = new();
    }

    /// <summary>
    /// Blast-radius numbers for a biological condition: how many rows in each
    /// affected table would be removed by a cascade delete of this condition.
    /// </summary>
    public class ConditionStats
    {
        public string Condition { get; set; } = string.Empty;
        public int RawFileCount { get; set; }
        public int PlateCount { get; set; }
        public List<string> PlateNames { get; set; } = new();
        public int ClassificationCount { get; set; }
        public int ProteinQuantRowCount { get; set; }
        public int ExclusionCount { get; set; }
        public List<int> OrphanImportIdsAfterDelete { get; set; } = new();
        public int OrphanImportCount => OrphanImportIdsAfterDelete?.Count ?? 0;
    }

    /// <summary>
    /// Counts of rows actually deleted by DeleteConditionCascadeAsync.
    /// </summary>
    public class DeleteConditionResult
    {
        public string Condition { get; set; } = string.Empty;
        public int DeletedRawFiles { get; set; }
        public int DeletedClassifications { get; set; }
        public int DeletedProteinQuantRows { get; set; }
        public int DeletedExclusions { get; set; }
        public int DeletedOrphanImports { get; set; }
    }

    public class ParquetDataService : DatabaseServiceBase
    {
        // Constructor for database operations
        public ParquetDataService(string projectDbPath) : base(projectDbPath)
        {
        }

        // Parameterless constructor for file parsing only (backwards compatibility)
        public ParquetDataService() : base(null)
        {
        }

        // ==================== FILE PARSING METHODS ====================

        public async Task<List<string>> GetColumnNamesAsync(string filePath)
        {
            if (!File.Exists(filePath))
                throw new FileNotFoundException("Parquet file not found", filePath);

            using (Stream fileStream = File.OpenRead(filePath))
            {
                using (var parquetReader = await ParquetReader.CreateAsync(fileStream))
                {
                    return parquetReader.Schema.GetDataFields()
                        .Select(f => f.Name)
                        .ToList();
                }
            }
        }

        /// <summary>
        /// Gets a mapping from raw file names to raw file IDs
        /// </summary>
        public async Task<Dictionary<string, int>> GetRawFileNameToIdMappingAsync()
        {
            var list = await QueryAsync(
                "SELECT raw_file_id, raw_file_name FROM raw_files",
                reader => (Id: reader.GetInt32(0), Name: reader.GetString(1)));

            // A bare ToDictionary here throws "An item with the same key has already been added",
            // which names nothing and aborts the caller's load pipeline halfway through. Nothing in
            // the schema stops the same run being registered twice, so say exactly which runs are
            // ambiguous - silently picking one of them would attribute counts and classifications
            // to an arbitrary row.
            var duplicates = list.GroupBy(r => r.Name, StringComparer.Ordinal)
                                 .Where(g => g.Count() > 1)
                                 .Select(g => g.Key)
                                 .OrderBy(n => n, StringComparer.Ordinal)
                                 .ToList();

            if (duplicates.Count > 0)
            {
                string named = string.Join(", ", duplicates.Take(10));
                if (duplicates.Count > 10)
                    named += $", ... (+{duplicates.Count - 10} more)";

                throw new InvalidOperationException(
                    $"{duplicates.Count} run name(s) are registered more than once in this project: {named}. " +
                    "This happens when the same DIA-NN report was imported twice. Remove the duplicate " +
                    "import before continuing - while it exists, per-run counts and cell-type " +
                    "classifications cannot be attributed to a single run unambiguously.");
            }

            return list.ToDictionary(r => r.Name, r => r.Id);
        }

        /// <summary>
        /// Gets all imported parquet file names
        /// </summary>
        public async Task<List<string>> GetAllImportedParquetFilesAsync()
        {
            return await QueryAsync(
                "SELECT file_name FROM parquet_imports ORDER BY import_timestamp ASC",
                reader => reader.GetString(0));
        }

        /// <summary>
        /// Loads multiple parquet files and merges them into a single ProteomicsData object
        /// </summary>
        public async Task<ProteomicsData> LoadMultipleParquetFilesAsync(List<string> filePaths, ColumnMapping mapping)
        {
            if (filePaths == null || filePaths.Count == 0)
                return new ProteomicsData();

            // The same path listed twice (two parquet_imports rows pointing at one file on disk) is
            // one dataset, not two: loading it twice would double every per-run count merged below.
            var distinctPaths = filePaths
                .Where(p => !string.IsNullOrEmpty(p))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (distinctPaths.Count == 0)
                return new ProteomicsData();

            // Which file each run was first seen in, so a run claimed by two different files can be
            // named in the error below.
            var runSourceFile = new Dictionary<string, string>(StringComparer.Ordinal);

            // Load the first file as the base
            var mergedData = await LoadParquetFileAsync(distinctPaths[0], mapping);

            foreach (var rawFile in mergedData.RawFileNames)
                runSourceFile[rawFile] = distinctPaths[0];

            // Merge additional files
            for (int i = 1; i < distinctPaths.Count; i++)
            {
                var additionalData = await LoadParquetFileAsync(distinctPaths[i], mapping);

                // Per-run protein/peptide counts are distinct-set cardinalities computed within one
                // file, and the quant matrix below is last-write-wins. Accumulating counts for a run
                // that also exists in another file would report roughly double its identifications
                // while the intensities behind them stay single-file - a plausible wrong number that
                // reaches the QC protein cutoff and the Explorer tooltips. Two files claiming the
                // same run is a data-integrity error, so refuse it instead of reconciling it.
                var collisions = additionalData.RawFileNames
                    .Where(rf => runSourceFile.ContainsKey(rf))
                    .ToList();

                if (collisions.Count > 0)
                {
                    string named = string.Join(", ", collisions.Take(10));
                    if (collisions.Count > 10)
                        named += $", ... (+{collisions.Count - 10} more)";

                    throw new InvalidOperationException(
                        $"'{Path.GetFileName(distinctPaths[i])}' and " +
                        $"'{Path.GetFileName(runSourceFile[collisions[0]])}' both contain " +
                        $"{collisions.Count} of the same run(s): {named}. Per-run protein, peptide and " +
                        "TIC counts cannot be merged across files that share a run. Remove one of the " +
                        "two imports from the project.");
                }

                foreach (var rawFile in additionalData.RawFileNames)
                    runSourceFile[rawFile] = distinctPaths[i];

                // Merge RawFileNames
                foreach (var rawFile in additionalData.RawFileNames)
                {
                    if (!mergedData.RawFileNames.Contains(rawFile))
                        mergedData.RawFileNames.Add(rawFile);
                }

                // Merge ProteinCountPerFile (the guard above rules out a key collision)
                foreach (var kvp in additionalData.ProteinCountPerFile)
                {
                    if (mergedData.ProteinCountPerFile.ContainsKey(kvp.Key))
                        mergedData.ProteinCountPerFile[kvp.Key] += kvp.Value;
                    else
                        mergedData.ProteinCountPerFile[kvp.Key] = kvp.Value;
                }

                // Merge PeptideCountPerFile (the guard above rules out a key collision)
                foreach (var kvp in additionalData.PeptideCountPerFile)
                {
                    if (mergedData.PeptideCountPerFile.ContainsKey(kvp.Key))
                        mergedData.PeptideCountPerFile[kvp.Key] += kvp.Value;
                    else
                        mergedData.PeptideCountPerFile[kvp.Key] = kvp.Value;
                }

                // Merge TotalIonCurrentPerFile (the guard above rules out a key collision)
                foreach (var kvp in additionalData.TotalIonCurrentPerFile)
                {
                    if (mergedData.TotalIonCurrentPerFile.ContainsKey(kvp.Key))
                        mergedData.TotalIonCurrentPerFile[kvp.Key] += kvp.Value;
                    else
                        mergedData.TotalIonCurrentPerFile[kvp.Key] = kvp.Value;
                }

                // Merge TargetProteinRatioPerFile (take average if key exists)
                foreach (var kvp in additionalData.TargetProteinRatioPerFile)
                {
                    if (mergedData.TargetProteinRatioPerFile.ContainsKey(kvp.Key))
                        mergedData.TargetProteinRatioPerFile[kvp.Key] = (mergedData.TargetProteinRatioPerFile[kvp.Key] + kvp.Value) / 2.0;
                    else
                        mergedData.TargetProteinRatioPerFile[kvp.Key] = kvp.Value;
                }

                // Merge BiologicalConditionPerFile
                foreach (var kvp in additionalData.BiologicalConditionPerFile)
                {
                    mergedData.BiologicalConditionPerFile[kvp.Key] = kvp.Value;
                }

                // Merge ProteinQuantMatrix
                foreach (var proteinKvp in additionalData.ProteinQuantMatrix)
                {
                    if (!mergedData.ProteinQuantMatrix.ContainsKey(proteinKvp.Key))
                    {
                        mergedData.ProteinQuantMatrix[proteinKvp.Key] = new Dictionary<string, double>();
                    }

                    foreach (var fileKvp in proteinKvp.Value)
                    {
                        mergedData.ProteinQuantMatrix[proteinKvp.Key][fileKvp.Key] = fileKvp.Value;
                    }
                }

                // Merge ProteinToGeneMap
                foreach (var kvp in additionalData.ProteinToGeneMap)
                {
                    mergedData.ProteinToGeneMap[kvp.Key] = kvp.Value;
                }

                // Merge unique peptide sequences
                mergedData.AllPeptideSequences.UnionWith(additionalData.AllPeptideSequences);
            }

            // Recalculate totals
            mergedData.TotalRawFiles = mergedData.RawFileNames.Count;
            mergedData.TotalProteinGroups = mergedData.ProteinQuantMatrix.Count;
            mergedData.TotalPeptides = mergedData.AllPeptideSequences.Count;

            return mergedData;
        }

        /// <summary>
        /// Gets the most recent import ID
        /// </summary>
        public async Task<int?> GetMostRecentImportIdAsync()
        {
            var result = await ExecuteScalarAsync<int>(
                "SELECT import_id FROM parquet_imports ORDER BY import_timestamp DESC LIMIT 1",
                defaultValue: -1);

            return result == -1 ? null : result;
        }

        public async Task<ProteomicsData> LoadParquetFileAsync(string filePath, ColumnMapping mapping)
        {
            if (!File.Exists(filePath))
                throw new FileNotFoundException("Parquet file not found", filePath);

            if (string.IsNullOrEmpty(mapping.RawFileColumn))
                throw new ArgumentException("Raw file column must be specified");
            if (string.IsNullOrEmpty(mapping.ProteinGroupColumn))
                throw new ArgumentException("Protein group column must be specified");
            if (string.IsNullOrEmpty(mapping.PeptideColumn))
                throw new ArgumentException("Peptide column must be specified");
            if (string.IsNullOrEmpty(mapping.TotalIonCurrentColumn))
                throw new ArgumentException("Total ion current column must be specified");

            var data = new ProteomicsData();
            var rawFiles = new HashSet<string>();
            var proteinGroups = new HashSet<string>();
            var peptides = new HashSet<string>();
            var proteinsByFile = new Dictionary<string, HashSet<string>>();
            var peptidesByFile = new Dictionary<string, HashSet<string>>();
            var ticByFile = new Dictionary<string, double>();
            var targetProteinTicByFile = new Dictionary<string, double>();
            var proteinQuantMatrix = new Dictionary<string, Dictionary<string, double>>();

            using (Stream fileStream = File.OpenRead(filePath))
            {
                using (var parquetReader = await ParquetReader.CreateAsync(fileStream))
                {
                    var dataFields = parquetReader.Schema.GetDataFields();

                    var rawFileField = dataFields.FirstOrDefault(f =>
                        f.Name.Equals(mapping.RawFileColumn, StringComparison.Ordinal));
                    var proteinField = dataFields.FirstOrDefault(f =>
                        f.Name.Equals(mapping.ProteinGroupColumn, StringComparison.Ordinal));
                    var peptideField = dataFields.FirstOrDefault(f =>
                        f.Name.Equals(mapping.PeptideColumn, StringComparison.Ordinal));
                    var ticField = dataFields.FirstOrDefault(f =>
                        f.Name.Equals(mapping.TotalIonCurrentColumn, StringComparison.Ordinal));
                    var proteinIdsField = dataFields.FirstOrDefault(f =>
                        f.Name.Equals("Protein.Ids", StringComparison.Ordinal));
                    var genesField = dataFields.FirstOrDefault(f =>
                        f.Name.Equals("Genes", StringComparison.Ordinal));

                    if (rawFileField == null)
                        throw new InvalidOperationException($"Column '{mapping.RawFileColumn}' not found");
                    if (proteinField == null)
                        throw new InvalidOperationException($"Column '{mapping.ProteinGroupColumn}' not found");
                    if (peptideField == null)
                        throw new InvalidOperationException($"Column '{mapping.PeptideColumn}' not found");
                    if (ticField == null)
                        throw new InvalidOperationException($"Column '{mapping.TotalIonCurrentColumn}' not found");

                    for (int i = 0; i < parquetReader.RowGroupCount; i++)
                    {
                        using (var groupReader = parquetReader.OpenRowGroupReader(i))
                        {
                            var rawFileColumn = await groupReader.ReadColumnAsync(rawFileField);
                            var proteinColumn = await groupReader.ReadColumnAsync(proteinField);
                            var peptideColumn = await groupReader.ReadColumnAsync(peptideField);
                            var ticColumn = await groupReader.ReadColumnAsync(ticField);

                            Array proteinIdsData = null;
                            if (proteinIdsField != null)
                            {
                                var proteinIdsColumn = await groupReader.ReadColumnAsync(proteinIdsField);
                                proteinIdsData = proteinIdsColumn.Data as Array;
                            }

                            Array genesData = null;
                            if (genesField != null)
                            {
                                var genesColumn = await groupReader.ReadColumnAsync(genesField);
                                genesData = genesColumn.Data as Array;
                            }

                            var rawFileData = rawFileColumn.Data as Array;
                            var proteinData = proteinColumn.Data as Array;
                            var peptideData = peptideColumn.Data as Array;
                            var ticData = ticColumn.Data as Array;

                            for (int row = 0; row < rawFileData.Length; row++)
                            {
                                var rawFile = rawFileData.GetValue(row)?.ToString();
                                var protein = proteinData.GetValue(row)?.ToString();
                                var peptide = peptideData.GetValue(row)?.ToString();
                                var ticValue = ticData.GetValue(row);
                                var proteinIds = proteinIdsData?.GetValue(row)?.ToString();
                                var genes = genesData?.GetValue(row)?.ToString();

                                if (!string.IsNullOrEmpty(rawFile))
                                {
                                    rawFiles.Add(rawFile);

                                    if (!proteinsByFile.ContainsKey(rawFile))
                                        proteinsByFile[rawFile] = new HashSet<string>();

                                    if (!peptidesByFile.ContainsKey(rawFile))
                                        peptidesByFile[rawFile] = new HashSet<string>();

                                    if (!ticByFile.ContainsKey(rawFile))
                                        ticByFile[rawFile] = 0;

                                    if (!targetProteinTicByFile.ContainsKey(rawFile))
                                        targetProteinTicByFile[rawFile] = 0;

                                    if (!string.IsNullOrEmpty(protein))
                                    {
                                        proteinGroups.Add(protein);
                                        proteinsByFile[rawFile].Add(protein);

                                        // Store protein-to-gene mapping
                                        if (!string.IsNullOrEmpty(genes) && !data.ProteinToGeneMap.ContainsKey(protein))
                                        {
                                            data.ProteinToGeneMap[protein] = genes;
                                        }

                                        if (!proteinQuantMatrix.ContainsKey(protein))
                                            proteinQuantMatrix[protein] = new Dictionary<string, double>();

                                        if (ticValue != null && double.TryParse(ticValue.ToString(), out double tic))
                                        {
                                            if (!proteinQuantMatrix[protein].ContainsKey(rawFile))
                                                proteinQuantMatrix[protein][rawFile] = 0;
                                            proteinQuantMatrix[protein][rawFile] += tic;
                                        }
                                    }

                                    if (!string.IsNullOrEmpty(peptide))
                                    {
                                        peptides.Add(peptide);
                                        peptidesByFile[rawFile].Add(peptide);
                                    }

                                    if (ticValue != null && double.TryParse(ticValue.ToString(), out double ticVal))
                                    {
                                        ticByFile[rawFile] += ticVal;

                                        if (mapping.TargetProteinIdentifiers != null &&
                                            mapping.TargetProteinIdentifiers.Count > 0 &&
                                            !string.IsNullOrEmpty(proteinIds))
                                        {
                                            if (mapping.TargetProteinIdentifiers.Any(target =>
                                                proteinIds.Contains(target, StringComparison.OrdinalIgnoreCase)))
                                            {
                                                targetProteinTicByFile[rawFile] += ticVal;
                                            }
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
            }

            data.TotalRawFiles = rawFiles.Count;
            data.TotalProteinGroups = proteinGroups.Count;
            data.AllPeptideSequences = peptides;
            data.TotalPeptides = peptides.Count;
            data.RawFileNames = rawFiles.OrderBy(rf => rf).ToList();
            data.ProteinQuantMatrix = proteinQuantMatrix;

            foreach (var rawFile in rawFiles)
            {
                data.ProteinCountPerFile[rawFile] = proteinsByFile[rawFile].Count;
                data.PeptideCountPerFile[rawFile] = peptidesByFile[rawFile].Count;
                data.TotalIonCurrentPerFile[rawFile] = ticByFile[rawFile];

                if (mapping.TargetProteinIdentifiers != null && mapping.TargetProteinIdentifiers.Count > 0)
                {
                    double targetTic = targetProteinTicByFile.ContainsKey(rawFile) ? targetProteinTicByFile[rawFile] : 0;
                    double totalTic = ticByFile[rawFile];
                    data.TargetProteinRatioPerFile[rawFile] = totalTic > 0 ? targetTic / totalTic : 0;
                }
            }

            return data;
        }

        // ==================== DATABASE OPERATIONS ====================

        /// <summary>
        /// Inserts a parquet import record and returns the new import_id
        /// </summary>
        public async Task<int> InsertParquetImportAsync(ParquetImportInfo importInfo)
        {
            return await ExecuteScalarAsync<int>(@"
                INSERT INTO parquet_imports 
                (plate_id, file_name, file_hash, import_timestamp, row_count, protein_count, cell_count, column_mapping)
                VALUES 
                (@plateId, @fileName, @fileHash, @timestamp, @rowCount, @proteinCount, @cellCount, @mapping);
                SELECT last_insert_rowid();",
                cmd =>
                {
                    cmd.Parameters.AddWithValue("@plateId", importInfo.PlateId);
                    cmd.Parameters.AddWithValue("@fileName", importInfo.FileName);
                    cmd.Parameters.AddWithValue("@fileHash", importInfo.FileHash);
                    cmd.Parameters.AddWithValue("@timestamp", importInfo.ImportTimestamp.ToString("o"));
                    cmd.Parameters.AddWithValue("@rowCount", importInfo.RowCount);
                    cmd.Parameters.AddWithValue("@proteinCount", importInfo.ProteinCount);
                    cmd.Parameters.AddWithValue("@cellCount", importInfo.CellCount);
                    cmd.Parameters.AddWithValue("@mapping", importInfo.ColumnMappingJson);
                });
        }

        /// <summary>
        /// Bulk inserts raw file records associated with an import
        /// Returns the raw files with their assigned raw_file_id values
        /// </summary>
        public async Task<List<RawFileInfo>> InsertRawFilesAsync(int importId, List<RawFileInfo> rawFiles)
        {
            var insertedFiles = new List<RawFileInfo>();

            await WithConnectionAsync(async connection =>
            {
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
                                    SELECT last_insert_rowid();";

                                command.Parameters.AddWithValue("@importId", importId);
                                command.Parameters.AddWithValue("@rawFileName", rawFile.RawFileName);
                                command.Parameters.AddWithValue("@condition", rawFile.BiologicalCondition ?? (object)DBNull.Value);
                                command.Parameters.AddWithValue("@plateId", rawFile.PlateId.HasValue ? rawFile.PlateId.Value : DBNull.Value);
                                command.Parameters.AddWithValue("@proteinCount", rawFile.ProteinCount);
                                command.Parameters.AddWithValue("@peptideCount", rawFile.PeptideCount);
                                command.Parameters.AddWithValue("@tic", rawFile.TotalIonCurrent);

                                var result = await command.ExecuteScalarAsync();
                                rawFile.RawFileId = result != null && int.TryParse(result.ToString(), out int parsedId) ? parsedId : 0;
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
            });

            return insertedFiles;
        }

        /// <summary>
        /// Gets all raw files, optionally filtered by plate or condition
        /// </summary>
        public async Task<List<RawFileInfo>> GetRawFilesAsync(int? plateId = null, string biologicalCondition = null)
        {
            var whereClauses = new List<string>();
            if (plateId.HasValue)
                whereClauses.Add("rf.plate_id = @plateId");
            if (!string.IsNullOrEmpty(biologicalCondition))
                whereClauses.Add("rf.biological_condition = @condition");

            var whereClause = whereClauses.Count > 0 ? "WHERE " + string.Join(" AND ", whereClauses) : "";

            return await QueryAsync($@"
                SELECT rf.raw_file_id, rf.import_id, rf.raw_file_name, 
                       rf.biological_condition, rf.plate_id, p.plate_name,
                       rf.protein_count, rf.peptide_count, rf.total_ion_current
                FROM raw_files rf
                LEFT JOIN plates p ON rf.plate_id = p.plate_id
                {whereClause}
                ORDER BY rf.raw_file_name",
                reader => new RawFileInfo
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
                },
                cmd =>
                {
                    if (plateId.HasValue)
                        cmd.Parameters.AddWithValue("@plateId", plateId.Value);
                    if (!string.IsNullOrEmpty(biologicalCondition))
                        cmd.Parameters.AddWithValue("@condition", biologicalCondition);
                });
        }

        /// <summary>
        /// Gets all distinct biological conditions from the database
        /// </summary>
        public async Task<List<string>> GetBiologicalConditionsAsync()
        {
            return await QueryAsync(@"
                SELECT DISTINCT biological_condition 
                FROM raw_files 
                WHERE biological_condition IS NOT NULL 
                  AND biological_condition != ''
                ORDER BY biological_condition",
                reader => reader.GetString(0));
        }

        /// <summary>
        /// Checks if a parquet file has already been imported
        /// </summary>
        public async Task<bool> IsParquetImportedAsync(string fileName)
        {
            var count = await ExecuteScalarAsync<int>(
                "SELECT COUNT(*) FROM parquet_imports WHERE file_name = @fileName",
                cmd => cmd.Parameters.AddWithValue("@fileName", fileName));
            return count > 0;
        }

        /// <summary>
        /// Returns the file name a previous import registered for this content hash, or null when the
        /// content is not in the project yet. Content is the only stable identity for an import: the
        /// display name can differ between the file the user picks and the row that was written, so a
        /// name-only check lets the same report in twice.
        /// </summary>
        public async Task<string> GetImportedFileNameByHashAsync(string fileHash)
        {
            if (string.IsNullOrEmpty(fileHash))
                return null;

            return await ExecuteScalarAsync<string>(
                "SELECT file_name FROM parquet_imports WHERE file_hash = @fileHash LIMIT 1",
                cmd => cmd.Parameters.AddWithValue("@fileHash", fileHash));
        }

        /// <summary>
        /// Every run name currently registered in raw_files. The table has no uniqueness constraint on
        /// raw_file_name, so an importer has to check for a clash before it writes.
        /// </summary>
        public async Task<HashSet<string>> GetAllRawFileNamesAsync()
        {
            var list = await QueryAsync(
                "SELECT raw_file_name FROM raw_files",
                reader => reader.GetString(0));

            return new HashSet<string>(list, StringComparer.Ordinal);
        }

        /// <summary>
        /// Deletes an existing parquet import and all associated raw files
        /// </summary>
        public async Task DeleteParquetImportAsync(string fileName)
        {
            await DeleteParquetImportCoreAsync(fileName, null);
        }

        /// <summary>
        /// Deletes one specific import by its id. An importer rolling back its own failed write must
        /// target the row it just created: file_name is not unique in parquet_imports, so undoing by
        /// name could take out an unrelated import that happens to share the name.
        /// </summary>
        public async Task DeleteParquetImportByIdAsync(int importId)
        {
            await DeleteParquetImportCoreAsync(null, importId);
        }

        private async Task DeleteParquetImportCoreAsync(string fileName, int? explicitImportId)
        {
            await WithConnectionAsync(async connection =>
            {
                using (var transaction = connection.BeginTransaction())
                {
                    try
                    {
                        int importId;
                        if (explicitImportId.HasValue)
                        {
                            importId = explicitImportId.Value;
                        }
                        else
                        {
                            using (var command = connection.CreateCommand())
                            {
                                command.CommandText = "SELECT import_id FROM parquet_imports WHERE file_name = @fileName";
                                command.Parameters.AddWithValue("@fileName", fileName);
                                var result = await command.ExecuteScalarAsync();
                                if (result == null || !int.TryParse(result.ToString(), out importId)) return;
                            }
                        }

                        // Clean child rows that reference raw_files of this import.
                        // Without this, deletes leave orphans behind in three tables since
                        // SQLite foreign keys are not enforced (no PRAGMA foreign_keys = ON).
                        const string doomedRawFilesSubquery =
                            "SELECT raw_file_id FROM raw_files WHERE import_id = @importId";

                        using (var command = connection.CreateCommand())
                        {
                            command.CommandText = $"DELETE FROM protein_quant_summary WHERE raw_file_id IN ({doomedRawFilesSubquery})";
                            command.Parameters.AddWithValue("@importId", importId);
                            await command.ExecuteNonQueryAsync();
                        }

                        using (var command = connection.CreateCommand())
                        {
                            command.CommandText = $"DELETE FROM raw_file_cell_type_classifications WHERE raw_file_id IN ({doomedRawFilesSubquery})";
                            command.Parameters.AddWithValue("@importId", importId);
                            await command.ExecuteNonQueryAsync();
                        }

                        using (var command = connection.CreateCommand())
                        {
                            command.CommandText = $"DELETE FROM excluded_runs WHERE raw_file_id IN ({doomedRawFilesSubquery})";
                            command.Parameters.AddWithValue("@importId", importId);
                            await command.ExecuteNonQueryAsync();
                        }

                        using (var command = connection.CreateCommand())
                        {
                            command.CommandText = "DELETE FROM raw_files WHERE import_id = @importId";
                            command.Parameters.AddWithValue("@importId", importId);
                            await command.ExecuteNonQueryAsync();
                        }

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
            });
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
        /// Gets the filename of the most recently imported parquet file
        /// </summary>
        public async Task<string> GetLastImportedParquetFileAsync()
        {
            return await ExecuteScalarAsync<string>(
                "SELECT file_name FROM parquet_imports ORDER BY import_timestamp DESC LIMIT 1");
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

            // Load parquet data
            var mapping = new ColumnMapping
            {
                RawFileColumn = "Run",
                ProteinGroupColumn = "Protein.Group",
                PeptideColumn = "Stripped.Sequence",
                TotalIonCurrentColumn = "Precursor.Quantity"
            };

            var data = await LoadParquetFileAsync(parquetPath, mapping);

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

                    // Use the summed intensity as both median and mean
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
        /// Stores protein_quant_summary rows straight from an already-parsed ProteomicsData (no re-parse). Used by the
        /// Excel gene-matrix importer, where the matrix is keyed by gene symbol. Mirrors ExtractAndStoreProteinQuantAsync.
        /// </summary>
        public async Task StoreProteinQuantMatrixAsync(
            ProteomicsData data,
            List<RawFileInfo> rawFiles,
            IProgress<string> progress = null)
        {
            var rawFileIdMap = rawFiles.ToDictionary(rf => rf.RawFileName, rf => rf.RawFileId);
            var proteinStats = new List<ProteinQuantSummary>();

            foreach (var protein in data.ProteinQuantMatrix.Keys)
            {
                foreach (var kv in data.ProteinQuantMatrix[protein])
                {
                    if (!rawFileIdMap.TryGetValue(kv.Key, out int rawFileId)) continue;
                    if (kv.Value > 0)
                    {
                        proteinStats.Add(new ProteinQuantSummary
                        {
                            ProteinId = protein,
                            RawFileId = rawFileId,
                            MedianIntensity = kv.Value,
                            MeanIntensity = kv.Value,
                            DetectionCount = 1
                        });
                    }
                }
            }

            progress?.Report($"Storing {proteinStats.Count} protein quantification records...");
            await BulkInsertProteinQuantAsync(proteinStats);
        }

        /// <summary>
        /// Loads biological conditions from the database and populates them into ProteomicsData
        /// </summary>
        public async Task PopulateBiologicalConditionsAsync(ProteomicsData data)
        {
            if (string.IsNullOrEmpty(_projectDbPath))
            {

                return;
            }

            var rows = await QueryAsync(
                "SELECT raw_file_name, biological_condition FROM raw_files",
                reader => (Name: reader.GetString(0), Condition: reader.IsDBNull(1) ? null : reader.GetString(1)));

            foreach (var (name, condition) in rows)
            {
                if (data.RawFileNames.Contains(name))
                    data.BiologicalConditionPerFile[name] = condition;
            }


        }

        /// <summary>
        /// Bulk inserts protein quantification summary records
        /// </summary>
        private async Task BulkInsertProteinQuantAsync(List<ProteinQuantSummary> proteinStats)
        {
            if (proteinStats == null || proteinStats.Count == 0)
                return;

            await WithConnectionAsync(async connection =>
            {
                using (var transaction = connection.BeginTransaction())
                {
                    try
                    {
                        using (var command = connection.CreateCommand())
                        {
                            command.CommandText = @"
                                INSERT INTO protein_quant_summary
                                (protein_id, raw_file_id, median_intensity, mean_intensity, detection_count)
                                VALUES
                                (@proteinId, @rawFileId, @median, @mean, @count)";

                            var proteinIdParam = command.Parameters.Add("@proteinId", SqliteType.Integer);
                            var rawFileIdParam = command.Parameters.Add("@rawFileId", SqliteType.Integer);
                            var medianParam = command.Parameters.Add("@median", SqliteType.Real);
                            var meanParam = command.Parameters.Add("@mean", SqliteType.Real);
                            var countParam = command.Parameters.Add("@count", SqliteType.Integer);

                            command.Prepare();

                            foreach (var stat in proteinStats)
                            {
                                proteinIdParam.Value = stat.ProteinId;
                                rawFileIdParam.Value = stat.RawFileId;
                                medianParam.Value = stat.MedianIntensity;
                                meanParam.Value = stat.MeanIntensity;
                                countParam.Value = stat.DetectionCount;

                                await command.ExecuteNonQueryAsync().ConfigureAwait(false);
                            }
                        }

                        await transaction.CommitAsync().ConfigureAwait(false);
                    }
                    catch
                    {
                        await transaction.RollbackAsync().ConfigureAwait(false);
                        throw;
                    }
                }
            });
        }

        /// <summary>
        /// Updates biological condition for a raw file
        /// </summary>
        public async Task UpdateRawFileConditionAsync(int rawFileId, string biologicalCondition)
        {
            await ExecuteNonQueryAsync(@"
                UPDATE raw_files SET biological_condition = @condition WHERE raw_file_id = @rawFileId",
                cmd =>
                {
                    cmd.Parameters.AddWithValue("@rawFileId", rawFileId);
                    cmd.Parameters.AddWithValue("@condition", biologicalCondition ?? "");
                });
        }

        /// <summary>
        /// Updates plate assignment for a raw file
        /// </summary>
        public async Task UpdateRawFilePlateAsync(int rawFileId, int? plateId)
        {
            await ExecuteNonQueryAsync(@"
                UPDATE raw_files SET plate_id = @plateId WHERE raw_file_id = @rawFileId",
                cmd =>
                {
                    cmd.Parameters.AddWithValue("@rawFileId", rawFileId);
                    cmd.Parameters.AddWithValue("@plateId", plateId.HasValue ? plateId.Value : DBNull.Value);
                });
        }

        // ==================== RUN EXCLUSION METHODS ====================

        /// <summary>
        /// Gets all excluded run IDs for the current import
        /// </summary>
        public async Task<HashSet<int>> GetExcludedRunIdsAsync()
        {
            var list = await QueryAsync(
                "SELECT raw_file_id FROM excluded_runs",
                reader => reader.GetInt32(0));
            return new HashSet<int>(list);
        }

        /// <summary>
        /// Gets excluded run IDs as raw file names for easier filtering
        /// </summary>
        public async Task<HashSet<string>> GetExcludedRunNamesAsync()
        {
            var list = await QueryAsync(@"
                SELECT rf.raw_file_name 
                FROM excluded_runs er
                INNER JOIN raw_files rf ON er.raw_file_id = rf.raw_file_id",
                reader => reader.GetString(0));
            return new HashSet<string>(list);
        }

        /// <summary>
        /// Excludes a run from BioTessera selection
        /// </summary>
        public async Task ExcludeRunAsync(int rawFileId)
        {
            await ExecuteNonQueryAsync(@"
                INSERT OR IGNORE INTO excluded_runs (raw_file_id, excluded_at)
                VALUES (@rawFileId, @excludedAt)",
                cmd =>
                {
                    cmd.Parameters.AddWithValue("@rawFileId", rawFileId);
                    cmd.Parameters.AddWithValue("@excludedAt", DateTime.Now.ToString("o"));
                });
        }

        /// <summary>
        /// Includes a previously excluded run (removes exclusion)
        /// </summary>
        public async Task IncludeRunAsync(int rawFileId)
        {
            await ExecuteNonQueryAsync(
                "DELETE FROM excluded_runs WHERE raw_file_id = @rawFileId",
                cmd => cmd.Parameters.AddWithValue("@rawFileId", rawFileId));
        }

        /// <summary>
        /// Clears all run exclusions
        /// </summary>
        public async Task ClearAllExclusionsAsync()
        {
            await ExecuteNonQueryAsync("DELETE FROM excluded_runs");
        }

        /// <summary>
        /// Checks if a specific run is excluded
        /// </summary>
        public async Task<bool> IsRunExcludedAsync(int rawFileId)
        {
            var count = await ExecuteScalarAsync<int>(
                "SELECT COUNT(*) FROM excluded_runs WHERE raw_file_id = @rawFileId",
                cmd => cmd.Parameters.AddWithValue("@rawFileId", rawFileId));
            return count > 0;
        }

        /// <summary>
        /// Returns the blast-radius numbers for a biological condition: how many
        /// raw files, plates, classifications, protein quant rows, exclusions, and
        /// parquet imports would be removed by a cascade delete. Read-only.
        /// </summary>
        public async Task<ConditionStats> GetConditionStatsAsync(string condition)
        {
            var stats = new ConditionStats { Condition = condition ?? string.Empty };

            if (string.IsNullOrWhiteSpace(condition))
                return stats;

            return await WithConnectionAsync(async connection =>
            {
                // 1. Raw file count
                using (var cmd = connection.CreateCommand())
                {
                    cmd.CommandText = "SELECT COUNT(*) FROM raw_files WHERE biological_condition = @condition";
                    cmd.Parameters.AddWithValue("@condition", condition);
                    stats.RawFileCount = Convert.ToInt32(await cmd.ExecuteScalarAsync() ?? 0);
                }

                if (stats.RawFileCount == 0)
                    return stats;

                // 2. Distinct plate names touched
                using (var cmd = connection.CreateCommand())
                {
                    cmd.CommandText = @"
                        SELECT DISTINCT p.plate_name
                        FROM raw_files rf
                        INNER JOIN plates p ON rf.plate_id = p.plate_id
                        WHERE rf.biological_condition = @condition
                        ORDER BY p.plate_name";
                    cmd.Parameters.AddWithValue("@condition", condition);
                    using (var reader = await cmd.ExecuteReaderAsync())
                    {
                        while (await reader.ReadAsync())
                            stats.PlateNames.Add(reader.GetString(0));
                    }
                }
                stats.PlateCount = stats.PlateNames.Count;

                // 3. Protein quant rows
                using (var cmd = connection.CreateCommand())
                {
                    cmd.CommandText = @"
                        SELECT COUNT(*) FROM protein_quant_summary
                        WHERE raw_file_id IN (SELECT raw_file_id FROM raw_files WHERE biological_condition = @condition)";
                    cmd.Parameters.AddWithValue("@condition", condition);
                    stats.ProteinQuantRowCount = Convert.ToInt32(await cmd.ExecuteScalarAsync() ?? 0);
                }

                // 4. Cell type classifications
                using (var cmd = connection.CreateCommand())
                {
                    cmd.CommandText = @"
                        SELECT COUNT(*) FROM raw_file_cell_type_classifications
                        WHERE raw_file_id IN (SELECT raw_file_id FROM raw_files WHERE biological_condition = @condition)";
                    cmd.Parameters.AddWithValue("@condition", condition);
                    stats.ClassificationCount = Convert.ToInt32(await cmd.ExecuteScalarAsync() ?? 0);
                }

                // 5. Run exclusions
                using (var cmd = connection.CreateCommand())
                {
                    cmd.CommandText = @"
                        SELECT COUNT(*) FROM excluded_runs
                        WHERE raw_file_id IN (SELECT raw_file_id FROM raw_files WHERE biological_condition = @condition)";
                    cmd.Parameters.AddWithValue("@condition", condition);
                    stats.ExclusionCount = Convert.ToInt32(await cmd.ExecuteScalarAsync() ?? 0);
                }

                // 6. parquet_imports rows that would become orphan after the delete
                //    (an import is orphaned only when EVERY raw_file under it carries the doomed condition)
                using (var cmd = connection.CreateCommand())
                {
                    cmd.CommandText = @"
                        SELECT import_id FROM (
                            SELECT import_id,
                                   SUM(CASE WHEN biological_condition = @condition THEN 1 ELSE 0 END) AS doomed,
                                   COUNT(*) AS total
                            FROM raw_files
                            GROUP BY import_id
                        ) WHERE doomed = total AND doomed > 0";
                    cmd.Parameters.AddWithValue("@condition", condition);
                    using (var reader = await cmd.ExecuteReaderAsync())
                    {
                        while (await reader.ReadAsync())
                            stats.OrphanImportIdsAfterDelete.Add(reader.GetInt32(0));
                    }
                }

                return stats;
            });
        }

        /// <summary>
        /// Cascade-deletes a biological condition and all data attached to its raw files.
        /// Order: protein_quant_summary, raw_file_cell_type_classifications, excluded_runs,
        /// raw_files, then any parquet_imports that became orphan as a result.
        /// All deletes run inside a single transaction; on any failure everything rolls back.
        /// Disk parquet files in the imports folder are NOT touched.
        /// </summary>
        public async Task<DeleteConditionResult> DeleteConditionCascadeAsync(string condition)
        {
            var result = new DeleteConditionResult { Condition = condition ?? string.Empty };

            if (string.IsNullOrWhiteSpace(condition))
                return result;

            await WithConnectionAsync(async connection =>
            {
                using (var transaction = connection.BeginTransaction())
                {
                    try
                    {
                        // 1. Capture orphan import IDs BEFORE any raw_files are deleted.
                        var orphanImportIds = new List<int>();
                        using (var cmd = connection.CreateCommand())
                        {
                            cmd.Transaction = transaction;
                            cmd.CommandText = @"
                                SELECT import_id FROM (
                                    SELECT import_id,
                                           SUM(CASE WHEN biological_condition = @condition THEN 1 ELSE 0 END) AS doomed,
                                           COUNT(*) AS total
                                    FROM raw_files
                                    GROUP BY import_id
                                ) WHERE doomed = total AND doomed > 0";
                            cmd.Parameters.AddWithValue("@condition", condition);
                            using (var reader = await cmd.ExecuteReaderAsync())
                            {
                                while (await reader.ReadAsync())
                                    orphanImportIds.Add(reader.GetInt32(0));
                            }
                        }

                        // 2. Delete child rows that reference doomed raw_files.
                        const string doomedIdsSubquery =
                            "SELECT raw_file_id FROM raw_files WHERE biological_condition = @condition";

                        result.DeletedProteinQuantRows = await ExecuteInTxAsync(connection, transaction,
                            $"DELETE FROM protein_quant_summary WHERE raw_file_id IN ({doomedIdsSubquery})",
                            cmd => cmd.Parameters.AddWithValue("@condition", condition));

                        result.DeletedClassifications = await ExecuteInTxAsync(connection, transaction,
                            $"DELETE FROM raw_file_cell_type_classifications WHERE raw_file_id IN ({doomedIdsSubquery})",
                            cmd => cmd.Parameters.AddWithValue("@condition", condition));

                        result.DeletedExclusions = await ExecuteInTxAsync(connection, transaction,
                            $"DELETE FROM excluded_runs WHERE raw_file_id IN ({doomedIdsSubquery})",
                            cmd => cmd.Parameters.AddWithValue("@condition", condition));

                        // 3. Delete the raw_files themselves.
                        result.DeletedRawFiles = await ExecuteInTxAsync(connection, transaction,
                            "DELETE FROM raw_files WHERE biological_condition = @condition",
                            cmd => cmd.Parameters.AddWithValue("@condition", condition));

                        // 4. Delete the now-orphan parquet_imports rows.
                        if (orphanImportIds.Count > 0)
                        {
                            var paramNames = new List<string>(orphanImportIds.Count);
                            for (int i = 0; i < orphanImportIds.Count; i++)
                                paramNames.Add("@p" + i);

                            string sql = "DELETE FROM parquet_imports WHERE import_id IN ("
                                + string.Join(",", paramNames) + ")";

                            result.DeletedOrphanImports = await ExecuteInTxAsync(connection, transaction, sql,
                                cmd =>
                                {
                                    for (int i = 0; i < orphanImportIds.Count; i++)
                                        cmd.Parameters.AddWithValue(paramNames[i], orphanImportIds[i]);
                                });
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

            return result;
        }

        /// <summary>
        /// Helper: run a non-query command on a specific connection and transaction.
        /// </summary>
        private static async Task<int> ExecuteInTxAsync(SqliteConnection connection, SqliteTransaction transaction,
            string sql, Action<SqliteCommand> paramSetup)
        {
            using (var cmd = connection.CreateCommand())
            {
                cmd.Transaction = transaction;
                cmd.CommandText = sql;
                paramSetup(cmd);
                return await cmd.ExecuteNonQueryAsync();
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
    }
}