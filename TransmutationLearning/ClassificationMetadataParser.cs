using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace TransmutationLearning
{
    /// <summary>
    /// Parses SingleR-style classification metadata files (tab-delimited)
    /// Expected format:
    /// "Run"  "scores.CellType1"  "scores.CellType2"  ...  "labels"  "delta.next"  "pruned.labels"
    /// </summary>
    public class ClassificationMetadataParser
    {
        /// <summary>
        /// Parses a tab-delimited classification file
        /// </summary>
        public async Task<(List<CellClassification> Classifications, List<string> CellTypes)> ParseAsync(string filePath)
        {
            var classifications = new List<CellClassification>();
            var cellTypes = new List<string>();

            var lines = await File.ReadAllLinesAsync(filePath);
            if (lines.Length == 0)
                throw new InvalidDataException("Classification file is empty");

            // Parse header to identify score columns
            var header = ParseTsvLine(lines[0]);
            var scoreColumns = new Dictionary<int, string>(); // columnIndex -> cellTypeName
            int runIndex = -1;
            int labelIndex = -1;
            int deltaIndex = -1;
            int prunedIndex = -1;

            for (int i = 0; i < header.Length; i++)
            {
                var col = header[i].Trim('"').Trim();

                if (col.Equals("Run", StringComparison.OrdinalIgnoreCase))
                {
                    runIndex = i;
                }
                else if (col.StartsWith("scores.", StringComparison.OrdinalIgnoreCase))
                {
                    // Extract cell type name from "scores.CellTypeName"
                    var cellType = col.Substring(7).Replace(".", " ");
                    scoreColumns[i] = cellType;
                    if (!cellTypes.Contains(cellType))
                        cellTypes.Add(cellType);
                }
                else if (col.Equals("labels", StringComparison.OrdinalIgnoreCase))
                {
                    labelIndex = i;
                }
                else if (col.Equals("delta.next", StringComparison.OrdinalIgnoreCase))
                {
                    deltaIndex = i;
                }
                else if (col.Equals("pruned.labels", StringComparison.OrdinalIgnoreCase))
                {
                    prunedIndex = i;
                }
            }

            // Validate required columns
            if (runIndex < 0)
                throw new InvalidDataException("Missing 'Run' column in classification file");
            if (labelIndex < 0)
                throw new InvalidDataException("Missing 'labels' column in classification file");
            if (deltaIndex < 0)
                throw new InvalidDataException("Missing 'delta.next' column in classification file");
            if (scoreColumns.Count == 0)
                throw new InvalidDataException("No score columns (scores.*) found in classification file");

            // Parse data rows
            for (int lineNum = 1; lineNum < lines.Length; lineNum++)
            {
                var line = lines[lineNum];
                if (string.IsNullOrWhiteSpace(line))
                    continue;

                var fields = ParseTsvLine(line);

                var classification = new CellClassification
                {
                    RunName = fields[runIndex].Trim('"').Trim(),
                    Label = fields[labelIndex].Trim('"').Trim(),
                    PrunedLabel = prunedIndex >= 0 && prunedIndex < fields.Length
                        ? fields[prunedIndex].Trim('"').Trim()
                        : fields[labelIndex].Trim('"').Trim()
                };

                // Parse delta
                if (deltaIndex < fields.Length)
                {
                    var deltaStr = fields[deltaIndex].Trim('"').Trim();
                    if (double.TryParse(deltaStr, NumberStyles.Float, CultureInfo.InvariantCulture, out double delta))
                    {
                        classification.DeltaNext = delta;
                    }
                }

                // Parse scores
                foreach (var kvp in scoreColumns)
                {
                    if (kvp.Key < fields.Length)
                    {
                        var scoreStr = fields[kvp.Key].Trim('"').Trim();
                        if (double.TryParse(scoreStr, NumberStyles.Float, CultureInfo.InvariantCulture, out double score))
                        {
                            classification.Scores[kvp.Value] = score;
                        }
                    }
                }

                classifications.Add(classification);
            }

            return (classifications, cellTypes);
        }

        /// <summary>
        /// Parses a TSV line handling quoted fields
        /// </summary>
        private string[] ParseTsvLine(string line)
        {
            return line.Split('\t');
        }
    }
}
