using BioTessera.Core.Models;
using BioTessera.GO;
using BioTessera.Utilities;
using System;
using System.Collections.Generic;

namespace SCPBrowser.Services
{
    /// <summary>
    /// Resolves GO terms for proteins using the central GO database.
    /// Caches gene annotations for fast repeated lookups.
    /// </summary>
    public class GoTermResolver
    {
        private readonly int _taxonId;
        private Dictionary<string, List<int>> _geneAnnotations;
        private bool _isLoaded;

        public int TaxonId => _taxonId;
        public bool IsReady => _isLoaded && _geneAnnotations != null && _geneAnnotations.Count > 0;
        public int GeneCount => _geneAnnotations?.Count ?? 0;
        public int ResolvedCount { get; private set; }
        public int UnresolvedCount { get; private set; }

        public GoTermResolver(int taxonId = 9606)
        {
            _taxonId = taxonId;
            _isLoaded = false;
        }



        /// <summary>
        /// Loads gene annotations from the central GO database.
        /// Call this before ResolveGoTerms, or it will be called automatically.
        /// </summary>
        public void LoadAnnotations()
        {
            if (_isLoaded)
                return;

            try
            {
                var dbPath = PatternLabPaths.GoDatabasePath;
                if (!System.IO.File.Exists(dbPath))
                {
                    Console.WriteLine($"[GoTermResolver] GO database not found at {dbPath}");
                    _geneAnnotations = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
                    _isLoaded = true;
                    return;
                }

                using var db = new GoDatabase(dbPath);
                _geneAnnotations = db.GetAllGeneAnnotations(_taxonId);
                _isLoaded = true;

                Console.WriteLine($"[GoTermResolver] Loaded {_geneAnnotations.Count} gene annotations for taxon {_taxonId}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[GoTermResolver] Error loading annotations: {ex.Message}");
                _geneAnnotations = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
                _isLoaded = true;
            }
        }

        public List<Protein> ResolveHierarchyPaths(List<Protein> proteins)
        {
            var result = new List<Protein>();

            try
            {
                using var db = new BioTessera.GO.GoDatabase();

                if (db.GetGoTermPathCount() == 0)
                {
                    Console.WriteLine("[BioTessera] No GO term paths. Using GO Terms mode instead.");
                    return proteins;
                }

                foreach (var protein in proteins)
                {
                    if (protein.GoTerms == null || protein.GoTerms.Count == 0)
                        continue;

                    var termIds = protein.GoTerms
                        .Select(t => int.TryParse(t.Replace("GO:", ""), out int id) ? id : 0)
                        .Where(id => id > 0)
                        .ToList();

                    var pathsByNamespace = db.GetBestPathsPerNamespace(termIds);

                    if (pathsByNamespace.Count == 0)
                        continue;

                    bool first = true;
                    foreach (var kvp in pathsByNamespace)
                    {
                        if (first)
                        {
                            protein.HierarchyPath = kvp.Value.GetPathNamesList();
                            result.Add(protein);
                            first = false;
                        }
                        else
                        {
                            var clone = new Protein
                            {
                                UniProtId = protein.UniProtId,
                                GeneName = protein.GeneName,
                                Abundances = protein.Abundances,
                                GoTerms = protein.GoTerms,
                                HierarchyPath = kvp.Value.GetPathNamesList()
                            };
                            result.Add(clone);
                        }
                    }
                }

                Console.WriteLine($"[BioTessera] Resolved hierarchy paths: {result.Count} entries from {proteins.Count} proteins");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[BioTessera] Error resolving hierarchy paths: {ex.Message}");
                return proteins;
            }

            return result;
        }



        public void ResolveGoTerms(List<Protein> proteins)
        {
            if (proteins == null || proteins.Count == 0)
                return;

            // Ensure annotations are loaded
            if (!_isLoaded)
                LoadAnnotations();

            Console.WriteLine($"[GoTermResolver] Starting resolution for {proteins.Count} proteins");
            Console.WriteLine($"[GoTermResolver] Gene annotations loaded: {_geneAnnotations?.Count ?? 0}");

            ResolvedCount = 0;
            UnresolvedCount = 0;
            int noGeneNameCount = 0;

            // Sample some gene names for debugging
            var sampleGeneNames = new List<string>();
            var sampleDbGenes = _geneAnnotations?.Keys.Take(5).ToList() ?? new List<string>();

            foreach (var protein in proteins)
            {
                // Clear existing GO terms
                protein.GoTerms.Clear();

                // Skip if no gene name
                if (string.IsNullOrWhiteSpace(protein.GeneName))
                {
                    noGeneNameCount++;
                    continue;
                }

                // Collect sample gene names for debugging
                if (sampleGeneNames.Count < 10)
                    sampleGeneNames.Add(protein.GeneName);

                // Look up gene in annotations
                if (_geneAnnotations.TryGetValue(protein.GeneName, out var termIds))
                {
                    // Convert term IDs to GO:XXXXXXX format
                    foreach (var termId in termIds)
                    {
                        protein.GoTerms.Add($"GO:{termId:D7}");
                    }
                    ResolvedCount++;
                }
                else
                {
                    UnresolvedCount++;
                }
            }

            Console.WriteLine($"[GoTermResolver] Sample protein gene names: {string.Join(", ", sampleGeneNames)}");
            Console.WriteLine($"[GoTermResolver] Sample DB gene names: {string.Join(", ", sampleDbGenes)}");
            Console.WriteLine($"[GoTermResolver] No gene name: {noGeneNameCount}, Resolved: {ResolvedCount}, Unresolved: {UnresolvedCount}");
        }

        /// <summary>
        /// Clears the cached annotations. Call this if the GO database is updated.
        /// </summary>
        public void ClearCache()
        {
            _geneAnnotations = null;
            _isLoaded = false;
            ResolvedCount = 0;
            UnresolvedCount = 0;
        }

        /// <summary>
        /// Gets GO term IDs for a single gene name.
        /// Returns empty list if gene not found.
        /// </summary>
        public List<string> GetGoTermsForGene(string geneName)
        {
            if (!_isLoaded)
                LoadAnnotations();

            var result = new List<string>();

            if (string.IsNullOrWhiteSpace(geneName))
                return result;

            if (_geneAnnotations.TryGetValue(geneName, out var termIds))
            {
                foreach (var termId in termIds)
                {
                    result.Add($"GO:{termId:D7}");
                }
            }

            return result;
        }
    }
}