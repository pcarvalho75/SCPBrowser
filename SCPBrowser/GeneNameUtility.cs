using System.Collections.Generic;
using BioTessera.Utilities;
using SCPBrowser.Services;

namespace SCPBrowser
{
    /// <summary>
    /// SCPBrowser-specific gene name helpers that build on BioTessera's GeneNameExtractor.
    /// For single protein identifiers, use GeneNameExtractor.Extract directly.
    /// </summary>
    public static class GeneNameUtility
    {
        /// <summary>
        /// Extracts gene names from a semicolon/comma-delimited protein group string
        /// (e.g., "RPL4_HUMAN;ACTB_HUMAN" → ["RPL4", "ACTB"]).
        /// </summary>
        public static List<string> ExtractGeneNamesFromGroup(string proteinGroup)
        {
            var result = new List<string>();

            var parts = proteinGroup.Split(';', ',');
            foreach (var part in parts)
            {
                var geneName = GeneNameExtractor.Extract(part.Trim());
                if (!string.IsNullOrEmpty(geneName))
                    result.Add(geneName);
            }

            return result;
        }

        /// <summary>
        /// Extracts gene names from a protein group using the ProteinToGeneMap when available,
        /// falling back to parsing the protein group ID directly.
        /// </summary>
        public static List<string> ExtractGeneNames(ProteomicsData proteomicsData, string proteinGroup)
        {
            if (proteomicsData.ProteinToGeneMap.TryGetValue(proteinGroup, out string genesString))
                return ExtractGeneNamesFromGroup(genesString);

            return ExtractGeneNamesFromGroup(proteinGroup);
        }
    }
}
