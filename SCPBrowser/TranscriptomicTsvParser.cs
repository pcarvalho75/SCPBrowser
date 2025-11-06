using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace SCPBrowser
{
    public class TranscriptomicTsvParser
    {
        public async Task<List<GeneExpressionRecord>> ParseGeneExpressionMatrixAsync(string filePath)
        {
            if (!File.Exists(filePath))
                throw new FileNotFoundException("Gene expression matrix file not found", filePath);

            var records = new List<GeneExpressionRecord>();
            var lines = await File.ReadAllLinesAsync(filePath);

            if (lines.Length < 2)
                throw new InvalidDataException("File must have at least a header row and one data row");

            var headerParts = lines[0].Split('\t');
            var cellIds = new string[headerParts.Length - 1];

            for (int i = 1; i < headerParts.Length; i++)
            {
                cellIds[i - 1] = headerParts[i].Trim('"');
            }

            for (int rowIndex = 1; rowIndex < lines.Length; rowIndex++)
            {
                var parts = lines[rowIndex].Split('\t');

                if (parts.Length < 2)
                    continue;

                var geneName = parts[0].Trim('"');

                for (int colIndex = 1; colIndex < parts.Length && colIndex - 1 < cellIds.Length; colIndex++)
                {
                    if (int.TryParse(parts[colIndex], out int count) && count > 0)
                    {
                        records.Add(new GeneExpressionRecord
                        {
                            GeneName = geneName,
                            CellID = cellIds[colIndex - 1],
                            Count = count
                        });
                    }
                }
            }

            return records;
        }

        public async Task<List<CellMetadata>> ParseCellMetadataAsync(string filePath)
        {
            if (!File.Exists(filePath))
                throw new FileNotFoundException("Cell metadata file not found", filePath);

            var metadata = new List<CellMetadata>();
            var lines = await File.ReadAllLinesAsync(filePath);

            int startIndex = 0;

            if (lines.Length > 0 && lines[0].Contains("CellID", StringComparison.OrdinalIgnoreCase))
                startIndex = 1;

            for (int i = startIndex; i < lines.Length; i++)
            {
                var parts = lines[i].Split('\t');

                if (parts.Length < 5)
                    continue;

                var cell = new CellMetadata
                {
                    CellID = parts[0].Trim('"'),
                    Age = parts.Length > 1 ? parts[1].Trim('"') : null,
                    Sex = parts.Length > 2 ? parts[2].Trim('"') : null,
                    Batch = parts.Length > 3 ? parts[3].Trim('"') : null,
                    CellType = parts.Length > 4 ? parts[4].Trim('"') : null
                };

                if (cell.CellType == "NA") cell.CellType = null;

                if (parts.Length > 5 && parts[5] != "NA")
                {
                    if (int.TryParse(parts[5], out int genesDetected))
                        cell.GenesDetected = genesDetected;
                }

                if (parts.Length > 6 && parts[6] != "NA")
                {
                    if (long.TryParse(parts[6], out long totalReads))
                        cell.TotalReads = totalReads;
                }

                if (parts.Length > 7 && parts[7] != "NA")
                {
                    if (long.TryParse(parts[7], out long mappedReads))
                        cell.MappedReads = mappedReads;
                }

                if (parts.Length > 8 && parts[8] != "NA")
                {
                    if (double.TryParse(parts[8], NumberStyles.Float, CultureInfo.InvariantCulture, out double mappingRate))
                        cell.MappingRate = mappingRate;
                }

                metadata.Add(cell);
            }

            return metadata;
        }
    }
}