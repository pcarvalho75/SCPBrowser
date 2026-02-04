using PatternTools;
using PatternTools.PLP;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace SCPBrowser.Services
{
    /// <summary>
    /// Label mode for PLP export — determines how runs are classified.
    /// </summary>
    public enum PLPLabelMode
    {
        BioCondition,
        CellType,
        Plate
    }

    /// <summary>
    /// Export configuration for PLP generation.
    /// </summary>
    public class PLPExportOptions
    {
        public PLPLabelMode PrimaryLabelMode { get; set; } = PLPLabelMode.BioCondition;
        public PLPLabelMode SecondaryLabelMode { get; set; } = PLPLabelMode.CellType;
        public string Description { get; set; } = "SCPBrowser Export";
    }

    /// <summary>
    /// Summary information about the PLP export before saving.
    /// </summary>
    public class PLPExportSummary
    {
        public int TotalRuns { get; set; }
        public int TotalProteins { get; set; }
        public Dictionary<string, int> ClassBreakdown { get; set; } = new();
        public int SkippedRuns { get; set; }
        public string SkipReason { get; set; }
    }

    /// <summary>
    /// Service for exporting SCPBrowser data to PatternLab for Proteomics (PLP) format.
    /// Builds a PatternLabProject from selected runs, quantitative protein data,
    /// and classification labels (biological condition, cell type, or plate).
    /// </summary>
    public class PLPExportService
    {
        /// <summary>
        /// Resolves the label string for a given run based on the label mode.
        /// Returns null if the run has no label in that mode.
        /// </summary>
        private static string GetRunLabel(
            string runName,
            PLPLabelMode mode,
            ProteomicsData data,
            Dictionary<string, CellTypePredictionResult> cellTypePredictions,
            Dictionary<string, int> rawFileToPlateId,
            Dictionary<int, string> plateIdToName)
        {
            switch (mode)
            {
                case PLPLabelMode.BioCondition:
                    if (data.BiologicalConditionPerFile != null &&
                        data.BiologicalConditionPerFile.TryGetValue(runName, out var condition) &&
                        !string.IsNullOrEmpty(condition))
                        return condition;
                    return null;

                case PLPLabelMode.CellType:
                    if (cellTypePredictions != null &&
                        cellTypePredictions.TryGetValue(runName, out var prediction) &&
                        !string.IsNullOrEmpty(prediction.TopCellType))
                        return prediction.TopCellType;
                    return null;

                case PLPLabelMode.Plate:
                    if (rawFileToPlateId != null &&
                        rawFileToPlateId.TryGetValue(runName, out var plateId) &&
                        plateIdToName != null &&
                        plateIdToName.TryGetValue(plateId, out var plateName) &&
                        !string.IsNullOrEmpty(plateName))
                        return plateName;
                    return null;

                default:
                    return null;
            }
        }

        /// <summary>
        /// Generates a preview summary of what the export will contain.
        /// </summary>
        public static PLPExportSummary GetExportSummary(
            List<string> selectedRuns,
            ProteomicsData data,
            PLPExportOptions options,
            Dictionary<string, CellTypePredictionResult> cellTypePredictions,
            Dictionary<string, int> rawFileToPlateId,
            Dictionary<int, string> plateIdToName)
        {
            var summary = new PLPExportSummary();
            var classBreakdown = new Dictionary<string, int>();
            int skipped = 0;

            foreach (var run in selectedRuns)
            {
                string primary = GetRunLabel(run, options.PrimaryLabelMode, data, cellTypePredictions, rawFileToPlateId, plateIdToName);
                string secondary = GetRunLabel(run, options.SecondaryLabelMode, data, cellTypePredictions, rawFileToPlateId, plateIdToName);

                if (primary == null || secondary == null)
                {
                    skipped++;
                    continue;
                }

                string label = primary + "_" + secondary;

                if (label == null)
                {
                    skipped++;
                    continue;
                }

                if (!classBreakdown.ContainsKey(label))
                    classBreakdown[label] = 0;
                classBreakdown[label]++;
            }

            // Count proteins across selected runs
            var proteinIds = new HashSet<string>();
            if (data.ProteinQuantMatrix != null)
            {
                foreach (var kvp in data.ProteinQuantMatrix)
                {
                    foreach (var run in selectedRuns)
                    {
                        if (kvp.Value.ContainsKey(run))
                        {
                            proteinIds.Add(kvp.Key);
                            break;
                        }
                    }
                }
            }

            summary.TotalRuns = selectedRuns.Count - skipped;
            summary.TotalProteins = proteinIds.Count;
            summary.ClassBreakdown = classBreakdown;
            summary.SkippedRuns = skipped;
            summary.SkipReason = skipped > 0 ? "No label available for the selected mode" : null;

            return summary;
        }

        /// <summary>
        /// Builds and saves a PatternLabProject from the given data and options.
        /// </summary>
        public static PatternLabProject Export(
            List<string> selectedRuns,
            ProteomicsData data,
            PLPExportOptions options,
            Dictionary<string, CellTypePredictionResult> cellTypePredictions,
            Dictionary<string, int> rawFileToPlateId,
            Dictionary<int, string> plateIdToName,
            Dictionary<string, FastaParserService.ProteinAnnotation> fastaAnnotations,
            string outputPath)
        {
            var plp = Build(selectedRuns, data, options, cellTypePredictions, rawFileToPlateId, plateIdToName, fastaAnnotations);
            plp.Save(outputPath);
            return plp;
        }

        /// <summary>
        /// Builds a PatternLabProject without saving to disk.
        /// </summary>
        public static PatternLabProject Build(
            List<string> selectedRuns,
            ProteomicsData data,
            PLPExportOptions options,
            Dictionary<string, CellTypePredictionResult> cellTypePredictions,
            Dictionary<string, int> rawFileToPlateId,
            Dictionary<int, string> plateIdToName,
            Dictionary<string, FastaParserService.ProteinAnnotation> fastaAnnotations)
        {
            if (data?.ProteinQuantMatrix == null || selectedRuns == null || selectedRuns.Count == 0)
                throw new ArgumentException("No data or selected runs to export.");

            // --- Step 1: Build protein Index ---
            var selectedRunSet = new HashSet<string>(selectedRuns);
            var proteinList = new List<string>();

            foreach (var kvp in data.ProteinQuantMatrix)
            {
                bool hasQuantInSelection = kvp.Value.Keys.Any(r => selectedRunSet.Contains(r));
                if (hasQuantInSelection)
                    proteinList.Add(kvp.Key);
            }

            proteinList.Sort(StringComparer.OrdinalIgnoreCase);

            var index = new SparseMatrixIndexParserV2();
            var proteinToId = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            for (int i = 0; i < proteinList.Count; i++)
            {
                int id = i + 1;
                string proteinAccession = proteinList[i];
                string description = "";

                if (fastaAnnotations != null && fastaAnnotations.TryGetValue(proteinAccession, out var annotation))
                    description = annotation.ProteinName ?? "";

                index.Add(new SparseMatrixIndexParserV2.Index
                {
                    ID = id,
                    Name = proteinAccession,
                    Description = description
                });

                proteinToId[proteinAccession] = id;
            }

            // --- Step 2: Build label mappings ---
            var runLabels = new Dictionary<string, string>();
            foreach (var run in selectedRuns)
            {
                string primary = GetRunLabel(run, options.PrimaryLabelMode, data, cellTypePredictions, rawFileToPlateId, plateIdToName);
                string secondary = GetRunLabel(run, options.SecondaryLabelMode, data, cellTypePredictions, rawFileToPlateId, plateIdToName);

                if (primary != null && secondary != null)
                    runLabels[run] = primary + "_" + secondary;
            }

            var distinctLabels = runLabels.Values.Distinct().OrderBy(l => l, StringComparer.OrdinalIgnoreCase).ToList();
            var labelToInt = new Dictionary<string, int>();
            var classDescriptions = new Dictionary<int, string>();

            for (int i = 0; i < distinctLabels.Count; i++)
            {
                int classId = i + 1;
                labelToInt[distinctLabels[i]] = classId;
                classDescriptions[classId] = distinctLabels[i];
            }

            // --- Step 3: Build SparseMatrix rows ---
            var sparseMatrix = new SparseMatrix { ClassDescriptionDictionary = classDescriptions };

            foreach (var run in selectedRuns)
            {
                if (!runLabels.TryGetValue(run, out var label))
                    continue;

                int classLabel = labelToInt[label];
                var rowData = new List<(int Dim, double Value)>();

                foreach (var kvp in data.ProteinQuantMatrix)
                {
                    if (proteinToId.TryGetValue(kvp.Key, out int proteinId) &&
                        kvp.Value.TryGetValue(run, out double quantValue) &&
                        quantValue > 0)
                    {
                        rowData.Add((proteinId, quantValue));
                    }
                }

                rowData.Sort((a, b) => a.Dim.CompareTo(b.Dim));
                sparseMatrix.theMatrixInRows.Add(new SparseMatrixRow(classLabel, rowData, run));
            }

            // --- Step 4: Assemble PatternLabProject ---
            var plp = new PatternLabProject(sparseMatrix, index, options.Description ?? "SCPBrowser Export");

            return plp;
        }
    }
}
