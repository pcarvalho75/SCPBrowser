using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace TransmutationLearning
{
    /// <summary>
    /// Parses SingleR-style classification metadata files
    /// Expected format: tab-delimited with columns for Run, scores per cell type, labels, delta.next, pruned.labels
    /// </summary>
    public static class ClassificationMetadataParser
    {
        public static List<CellClassification> Parse(string filePath)
        {
            var results = new List<CellClassification>();
            var lines = File.ReadAllLines(filePath);

            if (lines.Length < 2)
                throw new Exception("File must have header and at least one data row");

            // Parse header to identify columns (strip quotes if present)
            var header = lines[0].Split('\t')
                .Select(h => h.Trim().Trim('"'))
                .ToArray();

            // Find special columns
            int runIndex = -1;
            int labelsIndex = -1;
            int deltaIndex = -1;
            int prunedIndex = -1;
            var scoreColumns = new List<(int index, string cellType)>();

            for (int i = 0; i < header.Length; i++)
            {
                var col = header[i].Trim();

                // Handle different possible column names
                if (col.Equals("Run", StringComparison.OrdinalIgnoreCase) ||
                    col.Equals("cell", StringComparison.OrdinalIgnoreCase) ||
                    col.Equals("", StringComparison.OrdinalIgnoreCase) && i == 0)
                {
                    runIndex = i;
                }
                else if (col.Equals("labels", StringComparison.OrdinalIgnoreCase))
                {
                    labelsIndex = i;
                }
                else if (col.Equals("delta.next", StringComparison.OrdinalIgnoreCase) ||
                         col.Equals("delta_next", StringComparison.OrdinalIgnoreCase))
                {
                    deltaIndex = i;
                }
                else if (col.Equals("pruned.labels", StringComparison.OrdinalIgnoreCase) ||
                         col.Equals("pruned_labels", StringComparison.OrdinalIgnoreCase))
                {
                    prunedIndex = i;
                }
                else if (!col.StartsWith("scores.") && !col.Equals("labels") &&
                         !col.Equals("delta.next") && !col.Equals("pruned.labels"))
                {
                    // Assume it's a score column (cell type name)
                    // Skip if it looks like a special column
                    if (!string.IsNullOrEmpty(col) && i != runIndex)
                    {
                        // Check if column contains numeric data (peek at first data row)
                        if (lines.Length > 1)
                        {
                            var firstDataRow = lines[1].Split('\t');
                            if (i < firstDataRow.Length && double.TryParse(firstDataRow[i], out _))
                            {
                                scoreColumns.Add((i, col));
                            }
                        }
                    }
                }

                // Handle scores.CellType format
                if (col.StartsWith("scores."))
                {
                    var cellType = col.Substring(7); // Remove "scores." prefix
                    scoreColumns.Add((i, cellType));
                }
            }

            // Validate required columns
            if (runIndex < 0)
                throw new Exception("Could not find Run column (or first column as run identifier)");
            if (labelsIndex < 0)
                throw new Exception("Could not find 'labels' column");
            if (deltaIndex < 0)
                throw new Exception("Could not find 'delta.next' column");

            // Parse data rows
            for (int i = 1; i < lines.Length; i++)
            {
                var parts = lines[i].Split('\t');
                if (parts.Length < header.Length)
                    continue; // Skip incomplete rows

                var classification = new CellClassification
                {
                    Run = parts[runIndex].Trim().Trim('"'),
                    Labels = labelsIndex >= 0 && labelsIndex < parts.Length
                        ? parts[labelsIndex].Trim().Trim('"')
                        : "",
                    DeltaNext = deltaIndex >= 0 && deltaIndex < parts.Length &&
                                double.TryParse(parts[deltaIndex].Trim().Trim('"'), out double delta)
                        ? delta
                        : 0,
                    PrunedLabels = prunedIndex >= 0 && prunedIndex < parts.Length
                        ? parts[prunedIndex].Trim().Trim('"')
                        : ""
                };

                // Parse scores
                foreach (var (index, cellType) in scoreColumns)
                {
                    if (index < parts.Length && double.TryParse(parts[index], out double score))
                    {
                        classification.Scores[cellType] = score;
                    }
                }

                // Compute rankings for Distillation feature
                classification.ComputeRankings();

                results.Add(classification);
            }

            return results;
        }

        /// <summary>
        /// Get unique cell types from parsed classifications
        /// </summary>
        public static List<string> GetCellTypes(List<CellClassification> classifications)
        {
            return classifications
                .Select(c => c.Labels)
                .Where(l => !string.IsNullOrEmpty(l))
                .Distinct()
                .OrderBy(s => s)
                .ToList();
        }
    }
}
