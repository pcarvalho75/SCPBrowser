using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Parquet;
using Parquet.Data;

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

                        if (i % 5 == 0) // Every 5 row groups
                            await Task.Delay(1);

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

                                        bool isTargetProtein = false;
                                        if (mapping.TargetProteinIdentifiers != null && mapping.TargetProteinIdentifiers.Count > 0)
                                        {
                                            foreach (var targetId in mapping.TargetProteinIdentifiers)
                                            {
                                                if (string.IsNullOrWhiteSpace(targetId))
                                                    continue;

                                                if (!string.IsNullOrEmpty(protein) &&
                                                    protein.IndexOf(targetId.Trim(), StringComparison.OrdinalIgnoreCase) >= 0)
                                                {
                                                    isTargetProtein = true;
                                                    break;
                                                }

                                                if (!isTargetProtein && !string.IsNullOrEmpty(proteinIds) &&
                                                    proteinIds.IndexOf(targetId.Trim(), StringComparison.OrdinalIgnoreCase) >= 0)
                                                {
                                                    isTargetProtein = true;
                                                    break;
                                                }
                                            }
                                        }

                                        if (isTargetProtein)
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

            data.TotalRawFiles = rawFiles.Count;
            data.TotalProteinGroups = proteinGroups.Count;
            data.TotalPeptides = peptides.Count;
            data.ProteinCountPerFile = proteinsByFile
                .OrderByDescending(kvp => kvp.Value.Count)
                .ToDictionary(kvp => kvp.Key, kvp => kvp.Value.Count);
            data.PeptideCountPerFile = peptidesByFile
                .ToDictionary(kvp => kvp.Key, kvp => kvp.Value.Count);
            data.TotalIonCurrentPerFile = ticByFile;
            data.ProteinQuantMatrix = proteinQuantMatrix;
            data.RawFileNames = rawFiles.OrderBy(f => f).ToList();

            foreach (var rawFile in rawFiles)
            {
                double totalTic = ticByFile[rawFile];
                double targetTic = targetProteinTicByFile[rawFile];
                data.TargetProteinRatioPerFile[rawFile] = totalTic > 0 ? targetTic / totalTic : 0;
            }

            return data;
        }
    }
}