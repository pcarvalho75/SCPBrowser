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
    }

    public class ColumnMapping
    {
        public string RawFileColumn { get; set; } = string.Empty;
        public string ProteinGroupColumn { get; set; } = string.Empty;
        public string PeptideColumn { get; set; } = string.Empty;
        public string TotalIonCurrentColumn { get; set; } = string.Empty;
        public List<string> TargetProteinIdentifiers { get; set; } = new();
    }

    public class ParquetDataService
    {
        private readonly string _projectDbPath;

        // Constructor for database operations
        public ParquetDataService(string projectDbPath)
        {
            _projectDbPath = projectDbPath;
        }

        // Parameterless constructor for file parsing only (backwards compatibility)
        public ParquetDataService()
        {
            _projectDbPath = null;
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
        /// Gets the most recent import ID
        /// </summary>
        public async Task<int?> GetMostRecentImportIdAsync()
        {
            var connectionString = $"Data Source={_projectDbPath}";

            using (var connection = new SqliteConnection(connectionString))
            {
                await connection.OpenAsync();

                using (var command = connection.CreateCommand())
                {
                    command.CommandText = @"
                SELECT import_id 
                FROM parquet_imports 
                ORDER BY import_timestamp DESC 
                LIMIT 1
            ";

                    var result = await command.ExecuteScalarAsync();

                    if (result == null || result == DBNull.Value)
                        return null;

                    return Convert.ToInt32(result);
                }
            }
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

                                        if (ticValue != null)
                                        {
                                            double tic = Convert.ToDouble(ticValue);
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

                                    if (ticValue != null)
                                    {
                                        double tic = Convert.ToDouble(ticValue);
                                        ticByFile[rawFile] += tic;

                                        if (mapping.TargetProteinIdentifiers != null &&
                                            mapping.TargetProteinIdentifiers.Count > 0 &&
                                            !string.IsNullOrEmpty(proteinIds))
                                        {
                                            if (mapping.TargetProteinIdentifiers.Any(target =>
                                                proteinIds.Contains(target, StringComparison.OrdinalIgnoreCase)))
                                            {
                                                targetProteinTicByFile[rawFile] += tic;
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
                    data.TargetProteinRatioPerFile[rawFile] = totalTic > 0 ? (targetTic / totalTic) * 100.0 : 0;
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
        /// Gets all raw files, optionally filtered by plate or condition
        /// </summary>
        public async Task<List<RawFileInfo>> GetRawFilesAsync(int? plateId = null, string biologicalCondition = null)
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
                    }

                    if (!string.IsNullOrEmpty(biologicalCondition))
                    {
                        whereClauses.Add("rf.biological_condition = @condition");
                    }

                    var whereClause = whereClauses.Count > 0 ? "WHERE " + string.Join(" AND ", whereClauses) : "";

                    command.CommandText = $@"
                        SELECT rf.raw_file_id, rf.import_id, rf.raw_file_name, 
                               rf.biological_condition, rf.plate_id, p.plate_name,
                               rf.protein_count, rf.peptide_count, rf.total_ion_current
                        FROM raw_files rf
                        LEFT JOIN plates p ON rf.plate_id = p.plate_id
                        {whereClause}
                        ORDER BY rf.raw_file_name
                    ";

                    if (plateId.HasValue)
                    {
                        command.Parameters.AddWithValue("@plateId", plateId.Value);
                    }

                    if (!string.IsNullOrEmpty(biologicalCondition))
                    {
                        command.Parameters.AddWithValue("@condition", biologicalCondition);
                    }

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
                            if (result == null) return;
                            importId = Convert.ToInt32(result);
                        }

                        // Delete raw files first (foreign key constraint)
                        using (var command = connection.CreateCommand())
                        {
                            command.CommandText = "DELETE FROM raw_files WHERE import_id = @importId";
                            command.Parameters.AddWithValue("@importId", importId);
                            await command.ExecuteNonQueryAsync();
                        }

                        // Delete the import
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
            var connectionString = $"Data Source={_projectDbPath}";

            using (var connection = new SqliteConnection(connectionString))
            {
                await connection.OpenAsync();

                using (var command = connection.CreateCommand())
                {
                    command.CommandText = @"
                        SELECT file_name 
                        FROM parquet_imports 
                        ORDER BY import_timestamp DESC 
                        LIMIT 1";

                    var result = await command.ExecuteScalarAsync();
                    return result?.ToString();
                }
            }
        }
    }
}