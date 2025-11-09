using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;

namespace SCPBrowser
{
    public class TranscriptomicTsvParser
    {
        /// <summary>
        /// Parses the gene expression matrix and builds lookup tables for efficient storage
        /// </summary>
        public async Task<ParsedTranscriptomicData> ParseTranscriptomicDataAsync(
            string expressionMatrixPath,
            string metadataPath,
            IProgressReporter progress = null)
        {
            var result = new ParsedTranscriptomicData();

            progress?.ReportMessage("Parsing gene expression matrix...");
            await ParseGeneExpressionMatrixAsync(expressionMatrixPath, result, progress);
            progress?.ReportProgress($"Loaded {result.ExpressionRecords.Count:N0} non-zero expression values");
            progress?.ReportProgress($"Unique genes: {result.GeneLookup.Count:N0}, Unique cells: {result.CellLookup.Count:N0}");

            progress?.ReportMessage("Parsing cell metadata...");
            result.Metadata = await ParseCellMetadataAsync(metadataPath, result.CellLookup, progress);
            progress?.ReportProgress($"Loaded metadata for {result.Metadata.Count:N0} cells");

            return result;
        }

        private async Task ParseGeneExpressionMatrixAsync(
            string filePath,
            ParsedTranscriptomicData result,
            IProgressReporter progress = null)
        {
            if (!File.Exists(filePath))
                throw new FileNotFoundException("Gene expression matrix file not found", filePath);

            var lines = await File.ReadAllLinesAsync(filePath);

            if (lines.Length < 2)
                throw new InvalidDataException("File must have at least a header row and one data row");

            progress?.ReportProgress($"Reading matrix with {lines.Length:N0} rows...");

            // Parse header to get cell names
            var headerParts = lines[0].Split('\t');
            var cellIds = new int[headerParts.Length - 1];

            for (int i = 1; i < headerParts.Length; i++)
            {
                var cellName = headerParts[i].Trim('"');
                cellIds[i - 1] = result.CellLookup.GetOrAddCellId(cellName);
            }

            progress?.ReportProgress($"Found {cellIds.Length:N0} cells in matrix");

            // Parse data rows
            int totalNonZeroValues = 0;
            for (int rowIndex = 1; rowIndex < lines.Length; rowIndex++)
            {
                var parts = lines[rowIndex].Split('\t');

                if (parts.Length < 2)
                    continue;

                var geneName = parts[0].Trim('"');
                var geneId = result.GeneLookup.GetOrAddGeneId(geneName);

                for (int colIndex = 1; colIndex < parts.Length && colIndex - 1 < cellIds.Length; colIndex++)
                {
                    if (int.TryParse(parts[colIndex], out int count) && count > 0)
                    {
                        result.ExpressionRecords.Add(new GeneExpressionRecord
                        {
                            GeneId = geneId,
                            CellId = cellIds[colIndex - 1],
                            Count = count
                        });
                        totalNonZeroValues++;
                    }
                }

                // Progress reporting for large files
                if (rowIndex % 500 == 0)
                {
                    var percentage = (rowIndex * 100.0 / (lines.Length - 1));
                    progress?.ReportProgress($"Processed {rowIndex:N0} / {lines.Length - 1:N0} genes ({percentage:F1}%) - {totalNonZeroValues:N0} non-zero values");
                }
            }

            progress?.ReportProgress($"Matrix parsing complete: {totalNonZeroValues:N0} non-zero values from {result.GeneLookup.Count:N0} genes");
        }

        private async Task<List<CellMetadata>> ParseCellMetadataAsync(
            string filePath,
            CellLookup cellLookup,
            IProgressReporter progress = null)
        {
            if (!File.Exists(filePath))
                throw new FileNotFoundException("Cell metadata file not found", filePath);

            var metadata = new List<CellMetadata>();
            var lines = await File.ReadAllLinesAsync(filePath);

            int startIndex = 0;

            if (lines.Length > 0 && lines[0].Contains("CellID", StringComparison.OrdinalIgnoreCase))
                startIndex = 1;

            progress?.ReportProgress($"Reading metadata for {lines.Length - startIndex:N0} cells...");

            for (int i = startIndex; i < lines.Length; i++)
            {
                var parts = lines[i].Split('\t');

                if (parts.Length < 5)
                    continue;

                var cellName = parts[0].Trim('"');

                // Register cell in lookup table (if not already there from expression matrix)
                cellLookup.GetOrAddCellId(cellName);

                var cell = new CellMetadata
                {
                    CellID = cellName,
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

                if ((i - startIndex) % 1000 == 0)
                {
                    progress?.ReportProgress($"Processed {i - startIndex:N0} cell metadata records...");
                }
            }

            progress?.ReportProgress($"Metadata parsing complete: {metadata.Count:N0} cells");
            return metadata;
        }
    }
}