using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace TransmutationLearning
{
    /// <summary>
    /// Exports proteomics reference to .pref file format
    /// </summary>
    public class ProteomicsReferenceExporter
    {
        /// <summary>
        /// Build a proteomics reference from feature selection results
        /// </summary>
        public ProteomicsReference BuildReference(
            FeatureSelectionResult featureResult,
            FilteredDataset filteredData,
            TransmutationDataset dataset,
            string sourceDatasetName,
            string classificationSourceName,
            DistilledDataset distilledData = null)
        {
            var reference = new ProteomicsReference();

            // Get ordered cell types - use distilled labels if available
            List<string> cellTypes;
            if (distilledData != null && distilledData.DistillationApplied)
            {
                cellTypes = distilledData.DistilledLabels.Values
                    .Distinct()
                    .OrderBy(c => c)
                    .ToList();
            }
            else
            {
                cellTypes = filteredData.RetainedCells
                    .Select(c => c.Labels)
                    .Distinct()
                    .OrderBy(c => c)
                    .ToList();
            }

            // Build metadata
            reference.Metadata = new ProteomicsReferenceMetadata
            {
                Version = "1.0",
                GeneratedBy = "TransmutationLearning",
                GeneratedDate = DateTime.Now,
                SourceDataset = sourceDatasetName,
                ClassificationSource = classificationSourceName,
                DeltaThreshold = filteredData.DeltaThreshold,
                TotalCellsRetained = filteredData.TotalRetained,
                FeatureCount = featureResult.TotalMarkersSelected,
                CellTypes = cellTypes,
                SelectionCriteria = FormatCriteria(featureResult.Criteria),
                // Distillation info
                DistillationApplied = distilledData?.DistillationApplied ?? false,
                DistillationSensitivity = distilledData?.Sensitivity ?? 0,
                CellsReclassified = distilledData?.ReclassifiedCount ?? 0
            };

            // Copy marker stats
            reference.MarkerStats = featureResult.SelectedMarkers.ToList();

            // Build expression and detection matrices
            foreach (var marker in featureResult.SelectedMarkers)
            {
                var exprRow = new Dictionary<string, double>();
                var detRow = new Dictionary<string, double>();

                foreach (var cellType in cellTypes)
                {
                    if (marker.CellTypeMetrics.TryGetValue(cellType, out var metrics))
                    {
                        exprRow[cellType] = metrics.MedianExpression;
                        detRow[cellType] = metrics.DetectionRate;
                    }
                    else
                    {
                        exprRow[cellType] = 0;
                        detRow[cellType] = 0;
                    }
                }

                reference.ExpressionMatrix[marker.ProteinName] = exprRow;
                reference.DetectionMatrix[marker.ProteinName] = detRow;
            }

            return reference;
        }

        /// <summary>
        /// Export reference to .pref file
        /// </summary>
        public void Export(ProteomicsReference reference, string filePath)
        {
            var sb = new StringBuilder();

            // Write header/metadata
            sb.AppendLine("##PROTEOMICS_REFERENCE_FORMAT");
            sb.AppendLine($"##Version:{reference.Metadata.Version}");
            sb.AppendLine($"##GeneratedBy:{reference.Metadata.GeneratedBy}");
            sb.AppendLine($"##GeneratedDate:{reference.Metadata.GeneratedDate:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine($"##SourceDataset:{reference.Metadata.SourceDataset}");
            sb.AppendLine($"##ClassificationSource:{reference.Metadata.ClassificationSource}");
            sb.AppendLine($"##DeltaThreshold:{reference.Metadata.DeltaThreshold:F4}");
            sb.AppendLine($"##TotalCellsRetained:{reference.Metadata.TotalCellsRetained}");
            sb.AppendLine($"##FeatureCount:{reference.Metadata.FeatureCount}");
            sb.AppendLine($"##CellTypes:{string.Join(",", reference.Metadata.CellTypes)}");
            sb.AppendLine($"##SelectionCriteria:{reference.Metadata.SelectionCriteria}");
            sb.AppendLine($"##DistillationApplied:{reference.Metadata.DistillationApplied}");
            if (reference.Metadata.DistillationApplied)
            {
                sb.AppendLine($"##DistillationSensitivity:{reference.Metadata.DistillationSensitivity:F2}");
                sb.AppendLine($"##CellsReclassified:{reference.Metadata.CellsReclassified}");
            }
            sb.AppendLine();

            var cellTypes = reference.GetOrderedCellTypes();
            var proteins = reference.GetOrderedProteins();

            // Write expression matrix section
            sb.AppendLine("##SECTION:EXPRESSION");
            sb.AppendLine($"Protein\t{string.Join("\t", cellTypes)}");

            foreach (var protein in proteins)
            {
                var values = cellTypes.Select(ct =>
                    reference.ExpressionMatrix[protein].TryGetValue(ct, out var v)
                        ? v.ToString("F4", CultureInfo.InvariantCulture)
                        : "0.0000");
                sb.AppendLine($"{protein}\t{string.Join("\t", values)}");
            }

            sb.AppendLine();

            // Write detection matrix section
            sb.AppendLine("##SECTION:DETECTION");
            sb.AppendLine($"Protein\t{string.Join("\t", cellTypes)}");

            foreach (var protein in proteins)
            {
                var values = cellTypes.Select(ct =>
                    reference.DetectionMatrix[protein].TryGetValue(ct, out var v)
                        ? v.ToString("F4", CultureInfo.InvariantCulture)
                        : "0.0000");
                sb.AppendLine($"{protein}\t{string.Join("\t", values)}");
            }

            sb.AppendLine();

            // Write marker statistics section
            sb.AppendLine("##SECTION:MARKER_STATS");
            sb.AppendLine("Protein\tBestCellType\tSpecificityDelta\tWeightedScore\tKW_H\tKW_pValue\tBestDetection\tIsRobust");

            foreach (var protein in proteins)
            {
                var stat = reference.MarkerStats.FirstOrDefault(m => m.ProteinName == protein);
                if (stat != null)
                {
                    sb.AppendLine(string.Join("\t",
                        stat.ProteinName,
                        stat.BestCellType,
                        stat.SpecificityDelta.ToString("F4", CultureInfo.InvariantCulture),
                        stat.WeightedSpecificityScore.ToString("F4", CultureInfo.InvariantCulture),
                        stat.KruskalWallisH.ToString("F4", CultureInfo.InvariantCulture),
                        stat.KruskalWallisPValue.ToString("E4", CultureInfo.InvariantCulture),
                        stat.BestCellTypeDetection.ToString("F4", CultureInfo.InvariantCulture),
                        stat.IsRobust ? "TRUE" : "FALSE"));
                }
            }

            File.WriteAllText(filePath, sb.ToString());
        }

        /// <summary>
        /// Format criteria for metadata
        /// </summary>
        private string FormatCriteria(FeatureSelectionCriteria criteria)
        {
            if (criteria == null)
                return "default";

            var parts = new List<string>();

            if (criteria.MaxPValue < 1)
                parts.Add($"p<{criteria.MaxPValue}");

            if (criteria.MinDetectionRate > 0)
                parts.Add($"det>={criteria.MinDetectionRate:P0}");

            if (criteria.MinSpecificityDelta > 0)
                parts.Add($"delta>={criteria.MinSpecificityDelta:F2}");

            if (criteria.RequireRobustness)
                parts.Add("robust");

            return parts.Count > 0 ? string.Join(";", parts) : "none";
        }
    }
}
