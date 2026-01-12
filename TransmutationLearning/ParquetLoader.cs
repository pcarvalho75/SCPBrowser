using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Parquet;
using Parquet.Data;
using Parquet.Schema;

namespace TransmutationLearning
{
    /// <summary>
    /// Simple parquet loader for DIA-NN output files
    /// Extracts protein quantification matrix
    /// </summary>
    public class ParquetLoader
    {
        /// <summary>
        /// Loads a DIA-NN parquet file and extracts protein quantification data
        /// </summary>
        public async Task<ParquetData> LoadAsync(string filePath, IProgress<string>? progress = null)
        {
            var result = new ParquetData();

            progress?.Report("Opening parquet file...");

            using var fileStream = File.OpenRead(filePath);
            using var parquetReader = await ParquetReader.CreateAsync(fileStream);

            var schema = parquetReader.Schema;

            // Find column indices
            int runColIndex = FindColumnIndex(schema, "Run");
            int proteinGroupColIndex = FindColumnIndex(schema, "Protein.Group");
            int genesColIndex = FindColumnIndex(schema, "Genes");
            int quantColIndex = FindColumnIndex(schema, "Precursor.Quantity");

            if (runColIndex < 0)
                throw new InvalidDataException("Could not find 'Run' column in parquet file");
            if (proteinGroupColIndex < 0)
                throw new InvalidDataException("Could not find 'Protein.Group' column in parquet file");

            progress?.Report("Reading parquet data...");

            // Read all row groups
            for (int rgIndex = 0; rgIndex < parquetReader.RowGroupCount; rgIndex++)
            {
                progress?.Report($"Processing row group {rgIndex + 1}/{parquetReader.RowGroupCount}...");

                using var rowGroupReader = parquetReader.OpenRowGroupReader(rgIndex);

                var runColumn = await rowGroupReader.ReadColumnAsync(schema.DataFields[runColIndex]);
                var proteinColumn = await rowGroupReader.ReadColumnAsync(schema.DataFields[proteinGroupColIndex]);

                DataColumn? genesColumn = null;
                if (genesColIndex >= 0)
                {
                    genesColumn = await rowGroupReader.ReadColumnAsync(schema.DataFields[genesColIndex]);
                }

                DataColumn? quantColumn = null;
                if (quantColIndex >= 0)
                {
                    quantColumn = await rowGroupReader.ReadColumnAsync(schema.DataFields[quantColIndex]);
                }

                var runs = runColumn.Data as string[];
                var proteins = proteinColumn.Data as string[];
                var genes = genesColumn?.Data as string[];
                
                // Handle different numeric types for quantity
                double[]? quantities = null;
                if (quantColumn != null)
                {
                    quantities = ConvertToDoubleArray(quantColumn.Data);
                }

                if (runs == null || proteins == null)
                    continue;

                for (int i = 0; i < runs.Length; i++)
                {
                    var run = runs[i];
                    var protein = proteins[i];

                    if (string.IsNullOrEmpty(run) || string.IsNullOrEmpty(protein))
                        continue;

                    result.AllRuns.Add(run);

                    // Add to protein matrix
                    if (!result.ProteinMatrix.ContainsKey(protein))
                    {
                        result.ProteinMatrix[protein] = new Dictionary<string, double>();
                    }

                    double quantity = quantities != null && i < quantities.Length ? quantities[i] : 0;
                    
                    // Aggregate quantities per protein-run combination (sum)
                    if (result.ProteinMatrix[protein].ContainsKey(run))
                    {
                        result.ProteinMatrix[protein][run] += quantity;
                    }
                    else
                    {
                        result.ProteinMatrix[protein][run] = quantity;
                    }

                    // Store gene mapping
                    if (genes != null && i < genes.Length && !string.IsNullOrEmpty(genes[i]))
                    {
                        if (!result.ProteinToGeneMap.ContainsKey(protein))
                        {
                            result.ProteinToGeneMap[protein] = genes[i];
                        }
                    }
                }
            }

            result.UniqueRuns = result.AllRuns.Distinct().ToList();

            progress?.Report($"Loaded {result.ProteinMatrix.Count} proteins across {result.UniqueRuns.Count} runs");

            return result;
        }

        private int FindColumnIndex(ParquetSchema schema, string columnName)
        {
            for (int i = 0; i < schema.DataFields.Length; i++)
            {
                if (schema.DataFields[i].Name.Equals(columnName, StringComparison.OrdinalIgnoreCase))
                    return i;
            }
            return -1;
        }

        private double[]? ConvertToDoubleArray(Array? data)
        {
            if (data == null) return null;

            if (data is double[] doubleArr) return doubleArr;
            if (data is float[] floatArr) return floatArr.Select(f => (double)f).ToArray();
            if (data is decimal[] decArr) return decArr.Select(d => (double)d).ToArray();
            if (data is int[] intArr) return intArr.Select(i => (double)i).ToArray();
            if (data is long[] longArr) return longArr.Select(l => (double)l).ToArray();

            // Try generic conversion
            var result = new double[data.Length];
            for (int i = 0; i < data.Length; i++)
            {
                var val = data.GetValue(i);
                if (val != null && double.TryParse(val.ToString(), out double d))
                    result[i] = d;
            }
            return result;
        }
    }

    /// <summary>
    /// Result from loading a parquet file
    /// </summary>
    public class ParquetData
    {
        public Dictionary<string, Dictionary<string, double>> ProteinMatrix { get; set; } = new();
        public Dictionary<string, string> ProteinToGeneMap { get; set; } = new();
        public HashSet<string> AllRuns { get; set; } = new();
        public List<string> UniqueRuns { get; set; } = new();

        public int TotalProteins => ProteinMatrix.Count;
        public int TotalRuns => UniqueRuns.Count;
    }
}
