using BioTessera.GO;
using BioTessera.Utilities;
using SCPBrowser.Services;
using System;
using System.Collections.Generic;
using System.Linq;

namespace SCPBrowser.GOTools
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

        // Diagnostic info
        public int TotalGenesInRun { get; set; }
        public int AnnotatedGenesInRun { get; set; }

        public bool HasAnnotatedGenes => AnnotatedGenesInRun > 0;
        public bool HasSignificantTerms => AllSignificantTerms != null && AllSignificantTerms.Count > 0;
    }

    public class GoEnrichmentManager
    {
        private GoEnrichmentService _enrichmentService;
        private int _taxonId = 9606; // Default to human

        public bool IsLoaded => _enrichmentService != null && _enrichmentService.IsReady;
        public int GeneCount => _enrichmentService?.GeneCount ?? 0;
        public int TermCount => _enrichmentService?.TermCount ?? 0;

        public GoEnrichmentManager(int taxonId = 9606)
        {
            _taxonId = taxonId;
        }

        public void LoadDatabase()
        {
            _enrichmentService = new GoEnrichmentService(_taxonId);
            _enrichmentService.LoadBackground();
        }

        public void SetTaxonId(int taxonId)
        {
            if (_taxonId != taxonId)
            {
                _taxonId = taxonId;
                _enrichmentService?.ClearCache();
                _enrichmentService = null;
            }
        }

        public Dictionary<string, RunGoEnrichmentResult> EnrichAllRuns(
            ProteomicsData proteomicsData,
            double pValueThreshold = 0.05,
            int minOverlap = 2)
        {
            var results = new Dictionary<string, RunGoEnrichmentResult>();

            if (proteomicsData == null || proteomicsData.RawFileNames == null)
                return results;

            if (!IsLoaded)
                LoadDatabase();

            if (!IsLoaded)
                return results;

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
            var geneNames = ExtractGeneNamesForRun(proteomicsData, runName);

            if (geneNames.Count == 0)
                return new RunGoEnrichmentResult
                {
                    TotalGenesInRun = 0,
                    AnnotatedGenesInRun = 0
                };

            var enrichmentResults = _enrichmentService.Analyze(
                geneNames,
                pValueThreshold,
                minOverlap,
                applyFdrCorrection: true);

            // Get annotated count from the first result, or calculate if no results
            int annotatedCount = enrichmentResults.Count > 0
                ? enrichmentResults.First().SampleTotal
                : _enrichmentService.CountAnnotatedGenes(geneNames);

            if (enrichmentResults.Count == 0)
                return new RunGoEnrichmentResult
                {
                    TotalGenesInRun = geneNames.Count,
                    AnnotatedGenesInRun = annotatedCount
                };

            var topTerm = enrichmentResults.First();

            return new RunGoEnrichmentResult
            {
                TopGoTermId = topTerm.TermIdFormatted,
                TopGoTermName = topTerm.TermName,
                Namespace = NamespaceToString(topTerm.Namespace),
                PValue = topTerm.FdrCorrectedPValue,
                FoldEnrichment = topTerm.FoldEnrichment,
                OverlapCount = topTerm.SampleInTerm,
                AllSignificantTerms = enrichmentResults,
                TotalGenesInRun = geneNames.Count,
                AnnotatedGenesInRun = annotatedCount
            };
        }

        private List<string> ExtractGeneNamesForRun(ProteomicsData proteomicsData, string runName)
        {
            var geneNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var proteinEntry in proteomicsData.ProteinQuantMatrix)
            {
                string proteinId = proteinEntry.Key;
                var runAbundances = proteinEntry.Value;

                // Check if protein is detected in this run
                if (!runAbundances.TryGetValue(runName, out var abundance) || abundance <= 0)
                    continue;

                // Extract gene name(s) from protein group
                var proteinParts = proteinId.Split(';', ',');
                foreach (var part in proteinParts)
                {
                    var geneName = GeneNameExtractor.Extract(part.Trim());
                    if (!string.IsNullOrEmpty(geneName))
                        geneNames.Add(geneName);
                }
            }

            return geneNames.ToList();
        }

        private string NamespaceToString(GoNamespace ns)
        {
            return ns switch
            {
                GoNamespace.BiologicalProcess => "biological_process",
                GoNamespace.MolecularFunction => "molecular_function",
                GoNamespace.CellularComponent => "cellular_component",
                _ => "unknown"
            };
        }

        public Dictionary<string, System.Windows.Media.Color> GenerateGoTermColorMap(
            Dictionary<string, RunGoEnrichmentResult> enrichmentResults)
        {
            var uniqueGoTerms = enrichmentResults.Values
                .Where(r => !string.IsNullOrEmpty(r.TopGoTermId))
                .Select(r => r.TopGoTermId)
                .Distinct()
                .ToList();

            var colorMap = new Dictionary<string, System.Windows.Media.Color>();
            for (int i = 0; i < uniqueGoTerms.Count; i++)
            {
                double hue = (double)i / uniqueGoTerms.Count * 360.0;
                colorMap[uniqueGoTerms[i]] = HsvToRgb(hue, 0.7, 0.9);
            }

            return colorMap;
        }

        private System.Windows.Media.Color HsvToRgb(double h, double s, double v)
        {
            int hi = (int)(h / 60) % 6;
            double f = h / 60 - (int)(h / 60);
            double p = v * (1 - s);
            double q = v * (1 - f * s);
            double t = v * (1 - (1 - f) * s);

            double r, g, b;
            switch (hi)
            {
                case 0: r = v; g = t; b = p; break;
                case 1: r = q; g = v; b = p; break;
                case 2: r = p; g = v; b = t; break;
                case 3: r = p; g = q; b = v; break;
                case 4: r = t; g = p; b = v; break;
                default: r = v; g = p; b = q; break;
            }

            return System.Windows.Media.Color.FromRgb(
                (byte)(r * 255),
                (byte)(g * 255),
                (byte)(b * 255));
        }
    }
}