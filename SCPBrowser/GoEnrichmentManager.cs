using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace SCPBrowser
{
    public class RunGoEnrichmentResult
    {
        public string TopGoTermId { get; set; }
        public string TopGoTermName { get; set; }
        public string Namespace { get; set; }
        public double PValue { get; set; }
        public double FoldEnrichment { get; set; }
        public int OverlapCount { get; set; }
        public List<GoEnrichmentResult> AllSignificantTerms { get; set; } = new List<GoEnrichmentResult>();
    }

    public class GoEnrichmentManager
    {
        private GoSlimDatabase _goSlimDatabase;
        private GoAnnotationDatabase _annotationDatabase;
        private GoEnrichmentAnalyzer _analyzer;
        private readonly ReferenceDataService _referenceService;

        public bool IsLoaded => _goSlimDatabase != null && _annotationDatabase != null && _analyzer != null;
        public GoSlimDatabase GoSlimDatabase => _goSlimDatabase;
        public GoAnnotationDatabase AnnotationDatabase => _annotationDatabase;

        public GoEnrichmentManager()
        {
            _referenceService = new ReferenceDataService();
        }

        public async Task LoadDatabaseAsync(string databasePath)
        {
            if (!File.Exists(databasePath))
                throw new FileNotFoundException("Reference database not found", databasePath);

            var (goSlimDb, annotationDb) = await _referenceService.LoadGoAnnotationsAsync(databasePath);

            _goSlimDatabase = goSlimDb;
            _annotationDatabase = annotationDb;
            _analyzer = new GoEnrichmentAnalyzer(_goSlimDatabase, _annotationDatabase);
        }

        public Dictionary<string, RunGoEnrichmentResult> EnrichAllRuns(
            ProteomicsData proteomicsData,
            double pValueThreshold = 0.05,
            int minOverlap = 2)
        {
            if (!IsLoaded)
                throw new InvalidOperationException("GO databases not loaded");

            var results = new Dictionary<string, RunGoEnrichmentResult>();

            foreach (var runName in proteomicsData.RawFileNames)
            {
                var enrichmentResult = EnrichRun(proteomicsData, runName, pValueThreshold, minOverlap);
                results[runName] = enrichmentResult;
            }

            return results;
        }

        private RunGoEnrichmentResult EnrichRun(
            ProteomicsData proteomicsData,
            string runName,
            double pValueThreshold,
            int minOverlap)
        {
            var detectedProteinIds = ExtractProteinIdsForRun(proteomicsData, runName);

            if (detectedProteinIds.Count == 0)
            {
                return new RunGoEnrichmentResult();
            }

            var enrichmentResults = _analyzer.AnalyzeEnrichment(
                detectedProteinIds,
                pValueThreshold,
                minOverlap);

            if (enrichmentResults.Count == 0)
            {
                return new RunGoEnrichmentResult();
            }

            var topTerm = enrichmentResults.First();

            return new RunGoEnrichmentResult
            {
                TopGoTermId = topTerm.GoTermId,
                TopGoTermName = topTerm.GoTermName,
                Namespace = topTerm.Namespace,
                PValue = topTerm.PValue,
                FoldEnrichment = topTerm.FoldEnrichment,
                OverlapCount = topTerm.Overlap,
                AllSignificantTerms = enrichmentResults
            };
        }

        private List<string> ExtractProteinIdsForRun(ProteomicsData proteomicsData, string runName)
        {
            var proteinIds = new HashSet<string>();

            foreach (var proteinGroup in proteomicsData.ProteinQuantMatrix.Keys)
            {
                if (proteomicsData.ProteinQuantMatrix[proteinGroup].ContainsKey(runName))
                {
                    double abundance = proteomicsData.ProteinQuantMatrix[proteinGroup][runName];

                    if (abundance > 0)
                    {
                        var extractedIds = ExtractProteinIds(proteinGroup);
                        foreach (var id in extractedIds)
                        {
                            proteinIds.Add(id);
                        }
                    }
                }
            }

            return proteinIds.ToList();
        }

        private List<string> ExtractProteinIds(string proteinGroup)
        {
            var ids = new List<string>();

            var parts = proteinGroup.Split(';', ',');

            foreach (var part in parts)
            {
                var trimmed = part.Trim();
                if (string.IsNullOrEmpty(trimmed))
                    continue;

                string proteinId = null;

                if (trimmed.Contains("|"))
                {
                    var pipeParts = trimmed.Split('|');
                    if (pipeParts.Length >= 2)
                    {
                        proteinId = pipeParts[1];
                    }
                }
                else if (trimmed.Contains("_") || trimmed.Contains("-"))
                {
                    int underscorePos = trimmed.IndexOf('_');
                    int dashPos = trimmed.IndexOf('-');

                    int splitPos = -1;
                    if (underscorePos >= 0 && dashPos >= 0)
                        splitPos = Math.Min(underscorePos, dashPos);
                    else if (underscorePos >= 0)
                        splitPos = underscorePos;
                    else if (dashPos >= 0)
                        splitPos = dashPos;

                    if (splitPos > 0)
                        proteinId = trimmed.Substring(0, splitPos);
                    else
                        proteinId = trimmed;
                }
                else
                {
                    proteinId = trimmed;
                }

                if (!string.IsNullOrEmpty(proteinId))
                {
                    ids.Add(proteinId);
                }
            }

            return ids;
        }

        public Dictionary<string, System.Windows.Media.Color> GenerateGoTermColorMap(
            Dictionary<string, RunGoEnrichmentResult> enrichmentResults)
        {
            if (!IsLoaded)
                return new Dictionary<string, System.Windows.Media.Color>();

            var uniqueGoTerms = enrichmentResults.Values
                .Where(r => !string.IsNullOrEmpty(r.TopGoTermId))
                .Select(r => r.TopGoTermId)
                .Distinct()
                .OrderBy(g => g)
                .ToList();

            var colorMap = new Dictionary<string, System.Windows.Media.Color>();

            for (int i = 0; i < uniqueGoTerms.Count; i++)
            {
                colorMap[uniqueGoTerms[i]] = GetDistinctColor(i, uniqueGoTerms.Count);
            }

            return colorMap;
        }

        private System.Windows.Media.Color GetDistinctColor(int index, int total)
        {
            double hue = (double)index / total * 360.0;
            return HsvToRgb(hue, 0.7, 0.9);
        }

        private System.Windows.Media.Color HsvToRgb(double h, double s, double v)
        {
            double c = v * s;
            double x = c * (1 - Math.Abs((h / 60.0) % 2 - 1));
            double m = v - c;

            double r, g, b;

            if (h < 60)
            {
                r = c; g = x; b = 0;
            }
            else if (h < 120)
            {
                r = x; g = c; b = 0;
            }
            else if (h < 180)
            {
                r = 0; g = c; b = x;
            }
            else if (h < 240)
            {
                r = 0; g = x; b = c;
            }
            else if (h < 300)
            {
                r = x; g = 0; b = c;
            }
            else
            {
                r = c; g = 0; b = x;
            }

            return System.Windows.Media.Color.FromRgb(
                (byte)((r + m) * 255),
                (byte)((g + m) * 255),
                (byte)((b + m) * 255)
            );
        }
    }
}