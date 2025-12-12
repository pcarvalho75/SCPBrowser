using BioTessera.Core.Models;
using BioTessera.Utilities;
using System;
using System.Collections.Generic;
using System.Linq;

namespace SCPBrowser.Services
{
    /// <summary>
    /// Converts SCPBrowser ProteomicsData to BioTessera Protein list,
    /// aggregating abundances by biological condition using median.
    /// </summary>
    public static class ProteomicsDataConverter
    {
        /// <summary>
        /// Converts ProteomicsData to BioTessera Protein list.
        /// Aggregates run-level abundances to condition-level using median.
        /// </summary>
        public static List<Protein> Convert(ProteomicsData data)
        {
            if (data == null || data.ProteinQuantMatrix == null || data.ProteinQuantMatrix.Count == 0)
                return new List<Protein>();

            // Group runs by biological condition
            var conditionToRuns = BuildConditionToRunsMap(data);

            var proteins = new List<Protein>();

            foreach (var proteinEntry in data.ProteinQuantMatrix)
            {
                string proteinId = proteinEntry.Key;
                var runAbundances = proteinEntry.Value;

                // Get gene name from map, or extract from protein ID
                string geneName = null;
                if (data.ProteinToGeneMap != null && data.ProteinToGeneMap.TryGetValue(proteinId, out var mappedGene))
                    geneName = mappedGene;

                if (string.IsNullOrEmpty(geneName))
                    geneName = GeneNameExtractor.Extract(proteinId);

                // Aggregate abundances by condition (median)
                var conditionAbundances = new Dictionary<string, double>();

                foreach (var condition in conditionToRuns)
                {
                    var values = condition.Value
                        .Where(run => runAbundances.ContainsKey(run))
                        .Select(run => runAbundances[run])
                        .Where(v => v > 0)
                        .ToList();

                    if (values.Count > 0)
                        conditionAbundances[condition.Key] = Median(values);
                }

                // Skip proteins with no valid abundances
                if (conditionAbundances.Count == 0)
                    continue;

                proteins.Add(new Protein
                {
                    UniProtId = proteinId,
                    GeneName = geneName,
                    Abundances = conditionAbundances,
                    GoTerms = new List<string>(),
                    HierarchyPath = new List<string>()
                });
            }

            return proteins;
        }

        /// <summary>
        /// Builds a mapping from biological condition to list of run names.
        /// If no conditions are assigned, creates a single "All" condition.
        /// </summary>
        private static Dictionary<string, List<string>> BuildConditionToRunsMap(ProteomicsData data)
        {
            var conditionToRuns = new Dictionary<string, List<string>>();

            if (data.BiologicalConditionPerFile != null && data.BiologicalConditionPerFile.Count > 0)
            {
                // Group runs by their assigned biological condition
                foreach (var kvp in data.BiologicalConditionPerFile)
                {
                    string runName = kvp.Key;
                    string condition = kvp.Value;

                    // Skip runs with no condition assigned
                    if (string.IsNullOrWhiteSpace(condition))
                        continue;

                    if (!conditionToRuns.ContainsKey(condition))
                        conditionToRuns[condition] = new List<string>();

                    conditionToRuns[condition].Add(runName);
                }
            }

            // If no conditions were found, treat all runs as single "All" condition
            if (conditionToRuns.Count == 0 && data.RawFileNames != null)
            {
                conditionToRuns["All"] = data.RawFileNames.ToList();
            }

            return conditionToRuns;
        }

        /// <summary>
        /// Calculates the median of a list of values.
        /// </summary>
        private static double Median(List<double> values)
        {
            if (values.Count == 0)
                return 0;

            var sorted = values.OrderBy(v => v).ToList();
            int count = sorted.Count;

            if (count % 2 == 0)
                return (sorted[count / 2 - 1] + sorted[count / 2]) / 2.0;

            return sorted[count / 2];
        }
    }
}