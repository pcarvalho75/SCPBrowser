using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

namespace SCPBrowser.Services
{
    /// <summary>
    /// Service for fetching protein information from UniProt REST API.
    /// Results are cached to avoid repeated network calls.
    /// </summary>
    public class UniProtService
    {
        private static readonly HttpClient _httpClient = new HttpClient();
        private readonly ConcurrentDictionary<string, UniProtEntry> _cache = new();

        private const string UniProtApiBase = "https://rest.uniprot.org/uniprotkb/";
        private const string UniProtWebBase = "https://www.uniprot.org/uniprotkb/";

        static UniProtService()
        {
            _httpClient.DefaultRequestHeaders.Add("Accept", "application/json");
            _httpClient.DefaultRequestHeaders.Add("User-Agent", "SCPBrowser/1.0 (Proteomics Analysis Tool)");
            _httpClient.Timeout = TimeSpan.FromSeconds(15);
        }

        /// <summary>
        /// Gets protein information from UniProt. Accepts various ID formats:
        /// - UniProt accession (P36578)
        /// - UniProt mnemonic (RPL4_HUMAN)
        /// - Full FASTA header (sp|P36578|RL4_HUMAN)
        /// </summary>
        public async Task<UniProtEntry> GetProteinInfoAsync(string proteinId)
        {
            if (string.IsNullOrWhiteSpace(proteinId))
                return null;

            // Normalize the ID
            string normalizedId = NormalizeProteinId(proteinId);

            // Check cache
            if (_cache.TryGetValue(normalizedId, out var cached))
                return cached;

            try
            {
                var entry = await FetchFromUniProtAsync(normalizedId);
                if (entry != null)
                {
                    _cache[normalizedId] = entry;
                    // Also cache by accession and entry name for faster lookups
                    if (!string.IsNullOrEmpty(entry.AccessionId))
                        _cache[entry.AccessionId] = entry;
                    if (!string.IsNullOrEmpty(entry.EntryName))
                        _cache[entry.EntryName] = entry;
                }
                return entry;
            }
            catch (Exception ex)
            {

                return null;
            }
        }

        /// <summary>
        /// Batch fetch multiple proteins with progress reporting.
        /// </summary>
        public async Task<Dictionary<string, UniProtEntry>> GetProteinInfoBatchAsync(
            IEnumerable<string> proteinIds,
            IProgress<(int Current, int Total, string ProteinId)> progress = null)
        {
            var results = new Dictionary<string, UniProtEntry>();
            var toFetch = new List<string>();

            // Check cache first
            foreach (var id in proteinIds)
            {
                var normalizedId = NormalizeProteinId(id);
                if (_cache.TryGetValue(normalizedId, out var cached))
                {
                    results[id] = cached;
                }
                else
                {
                    toFetch.Add(id);
                }
            }

            // Fetch remaining with throttling (max 5 concurrent)
            int completed = results.Count;
            int total = completed + toFetch.Count;

            using var semaphore = new System.Threading.SemaphoreSlim(5);
            var tasks = toFetch.Select(async id =>
            {
                await semaphore.WaitAsync();
                try
                {
                    var entry = await GetProteinInfoAsync(id);
                    lock (results)
                    {
                        if (entry != null)
                            results[id] = entry;
                        completed++;
                        progress?.Report((completed, total, id));
                    }
                    return (id, entry);
                }
                finally
                {
                    semaphore.Release();
                }
            });

            await Task.WhenAll(tasks);
            return results;
        }

        private async Task<UniProtEntry> FetchFromUniProtAsync(string proteinId)
        {
            string url = $"{UniProtApiBase}{proteinId}";

            var response = await _httpClient.GetAsync(url);

            if (!response.IsSuccessStatusCode)
            {
                // Try search if direct lookup fails (for mnemonics like RPL4_HUMAN)
                if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                {
                    return await SearchUniProtAsync(proteinId);
                }
                return null;
            }

            var json = await response.Content.ReadAsStringAsync();
            return ParseUniProtResponse(json);
        }

        private async Task<UniProtEntry> SearchUniProtAsync(string query)
        {
            // Search by ID or mnemonic
            string url = $"{UniProtApiBase}search?query=(id:{query} OR mnemonic:{query})&size=1";

            var response = await _httpClient.GetAsync(url);
            if (!response.IsSuccessStatusCode)
                return null;

            var json = await response.Content.ReadAsStringAsync();
            return ParseSearchResponse(json);
        }

        private UniProtEntry ParseUniProtResponse(string json)
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                var entry = new UniProtEntry
                {
                    AccessionId = GetFirstAccession(root),
                    SecondaryAccessions = GetSecondaryAccessions(root),
                    EntryName = GetStringProperty(root, "uniProtkbId"),
                    ProteinName = GetProteinName(root),
                    AlternativeNames = GetAlternativeNames(root),
                    GeneName = GetGeneName(root),
                    GeneNameSynonyms = GetGeneNameSynonyms(root),
                    Organism = GetOrganism(root),
                    OrganismId = GetOrganismId(root),
                    Function = GetFunction(root),
                    SubcellularLocations = GetSubcellularLocations(root),
                    TissueSpecificity = GetTissueSpecificity(root),
                    Involvement = GetInvolvement(root),
                    SequenceLength = GetSequenceLength(root),
                    MolecularWeight = GetMolecularWeight(root),
                    Keywords = GetKeywords(root),
                    GoTerms = GetGoTerms(root),
                    PdbIds = GetPdbIds(root),
                    InterProDomains = GetInterProDomains(root),
                    PathwayIds = GetPathwayIds(root),
                    PubMedIds = GetPubMedIds(root),
                    ProteinExistence = GetProteinExistence(root),
                    LastModified = GetLastModified(root),
                    SequenceString = GetSequenceString(root),
                    Features = GetFeatures(root)
                };

                return entry;
            }
            catch (Exception ex)
            {

                return null;
            }
        }

        private UniProtEntry ParseSearchResponse(string json)
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                if (root.TryGetProperty("results", out var results) && results.GetArrayLength() > 0)
                {
                    var firstResult = results[0];
                    // Re-parse the first result using the main parser
                    return ParseUniProtResponse(firstResult.GetRawText());
                }

                return null;
            }
            catch
            {
                return null;
            }
        }

        #region JSON Parsing Helpers

        private string GetFirstAccession(JsonElement root)
        {
            if (root.TryGetProperty("primaryAccession", out var acc))
                return acc.GetString();
            return null;
        }

        private List<string> GetSecondaryAccessions(JsonElement root)
        {
            var result = new List<string>();
            try
            {
                if (root.TryGetProperty("secondaryAccessions", out var accs))
                {
                    foreach (var acc in accs.EnumerateArray())
                    {
                        result.Add(acc.GetString());
                    }
                }
            }
            catch { }
            return result;
        }

        private string GetStringProperty(JsonElement root, string propertyName)
        {
            if (root.TryGetProperty(propertyName, out var prop))
                return prop.GetString();
            return null;
        }

        private string GetProteinName(JsonElement root)
        {
            try
            {
                if (root.TryGetProperty("proteinDescription", out var desc))
                {
                    // Try recommendedName first
                    if (desc.TryGetProperty("recommendedName", out var recName))
                    {
                        if (recName.TryGetProperty("fullName", out var fullName))
                        {
                            if (fullName.TryGetProperty("value", out var value))
                                return value.GetString();
                        }
                    }

                    // Fall back to submittedName
                    if (desc.TryGetProperty("submissionNames", out var subNames) && subNames.GetArrayLength() > 0)
                    {
                        var first = subNames[0];
                        if (first.TryGetProperty("fullName", out var fullName))
                        {
                            if (fullName.TryGetProperty("value", out var value))
                                return value.GetString();
                        }
                    }
                }
            }
            catch { }

            return null;
        }

        private List<string> GetAlternativeNames(JsonElement root)
        {
            var result = new List<string>();
            try
            {
                if (root.TryGetProperty("proteinDescription", out var desc))
                {
                    if (desc.TryGetProperty("alternativeNames", out var altNames))
                    {
                        foreach (var altName in altNames.EnumerateArray())
                        {
                            if (altName.TryGetProperty("fullName", out var fullName))
                            {
                                if (fullName.TryGetProperty("value", out var value))
                                    result.Add(value.GetString());
                            }
                        }
                    }
                }
            }
            catch { }
            return result;
        }

        private string GetGeneName(JsonElement root)
        {
            try
            {
                if (root.TryGetProperty("genes", out var genes) && genes.GetArrayLength() > 0)
                {
                    var firstGene = genes[0];
                    if (firstGene.TryGetProperty("geneName", out var geneName))
                    {
                        if (geneName.TryGetProperty("value", out var value))
                            return value.GetString();
                    }
                }
            }
            catch { }

            return null;
        }

        private List<string> GetGeneNameSynonyms(JsonElement root)
        {
            var result = new List<string>();
            try
            {
                if (root.TryGetProperty("genes", out var genes) && genes.GetArrayLength() > 0)
                {
                    var firstGene = genes[0];
                    if (firstGene.TryGetProperty("synonyms", out var synonyms))
                    {
                        foreach (var syn in synonyms.EnumerateArray())
                        {
                            if (syn.TryGetProperty("value", out var value))
                                result.Add(value.GetString());
                        }
                    }
                }
            }
            catch { }
            return result;
        }

        private string GetOrganism(JsonElement root)
        {
            try
            {
                if (root.TryGetProperty("organism", out var org))
                {
                    if (org.TryGetProperty("scientificName", out var name))
                        return name.GetString();
                }
            }
            catch { }

            return null;
        }

        private int? GetOrganismId(JsonElement root)
        {
            try
            {
                if (root.TryGetProperty("organism", out var org))
                {
                    if (org.TryGetProperty("taxonId", out var taxId))
                        return taxId.GetInt32();
                }
            }
            catch { }
            return null;
        }

        private string GetFunction(JsonElement root)
        {
            try
            {
                if (root.TryGetProperty("comments", out var comments))
                {
                    foreach (var comment in comments.EnumerateArray())
                    {
                        if (comment.TryGetProperty("commentType", out var type) &&
                            type.GetString() == "FUNCTION")
                        {
                            if (comment.TryGetProperty("texts", out var texts) && texts.GetArrayLength() > 0)
                            {
                                if (texts[0].TryGetProperty("value", out var value))
                                    return value.GetString();
                            }
                        }
                    }
                }
            }
            catch { }
            return null;
        }

        private List<string> GetSubcellularLocations(JsonElement root)
        {
            var result = new List<string>();
            try
            {
                if (root.TryGetProperty("comments", out var comments))
                {
                    foreach (var comment in comments.EnumerateArray())
                    {
                        if (comment.TryGetProperty("commentType", out var type) &&
                            type.GetString() == "SUBCELLULAR LOCATION")
                        {
                            if (comment.TryGetProperty("subcellularLocations", out var locs))
                            {
                                foreach (var loc in locs.EnumerateArray())
                                {
                                    if (loc.TryGetProperty("location", out var location))
                                    {
                                        if (location.TryGetProperty("value", out var value))
                                            result.Add(value.GetString());
                                    }
                                }
                            }
                        }
                    }
                }
            }
            catch { }
            return result;
        }

        private string GetTissueSpecificity(JsonElement root)
        {
            try
            {
                if (root.TryGetProperty("comments", out var comments))
                {
                    foreach (var comment in comments.EnumerateArray())
                    {
                        if (comment.TryGetProperty("commentType", out var type) &&
                            type.GetString() == "TISSUE SPECIFICITY")
                        {
                            if (comment.TryGetProperty("texts", out var texts) && texts.GetArrayLength() > 0)
                            {
                                if (texts[0].TryGetProperty("value", out var value))
                                    return value.GetString();
                            }
                        }
                    }
                }
            }
            catch { }
            return null;
        }

        private List<string> GetInvolvement(JsonElement root)
        {
            var result = new List<string>();
            try
            {
                if (root.TryGetProperty("comments", out var comments))
                {
                    foreach (var comment in comments.EnumerateArray())
                    {
                        if (comment.TryGetProperty("commentType", out var type) &&
                            type.GetString() == "DISEASE")
                        {
                            if (comment.TryGetProperty("disease", out var disease))
                            {
                                if (disease.TryGetProperty("diseaseId", out var diseaseId))
                                    result.Add(diseaseId.GetString());
                            }
                        }
                    }
                }
            }
            catch { }
            return result;
        }

        private int? GetSequenceLength(JsonElement root)
        {
            try
            {
                if (root.TryGetProperty("sequence", out var seq))
                {
                    if (seq.TryGetProperty("length", out var length))
                        return length.GetInt32();
                }
            }
            catch { }
            return null;
        }

        private double? GetMolecularWeight(JsonElement root)
        {
            try
            {
                if (root.TryGetProperty("sequence", out var seq))
                {
                    if (seq.TryGetProperty("molWeight", out var weight))
                        return weight.GetDouble();
                }
            }
            catch { }
            return null;
        }

        private string GetSequenceString(JsonElement root)
        {
            try
            {
                if (root.TryGetProperty("sequence", out var seq))
                {
                    if (seq.TryGetProperty("value", out var value))
                        return value.GetString();
                }
            }
            catch { }
            return null;
        }

        private List<ProteinFeature> GetFeatures(JsonElement root)
        {
            var result = new List<ProteinFeature>();
            try
            {
                if (root.TryGetProperty("features", out var features))
                {
                    foreach (var feature in features.EnumerateArray())
                    {
                        string type = null;
                        if (feature.TryGetProperty("type", out var typeProp))
                            type = typeProp.GetString();

                        if (string.IsNullOrEmpty(type))
                            continue;

                        // Only keep structurally relevant features
                        if (!IsRelevantFeatureType(type))
                            continue;

                        var pf = new ProteinFeature { Type = type };

                        if (feature.TryGetProperty("description", out var desc))
                            pf.Description = desc.GetString();

                        if (feature.TryGetProperty("location", out var location))
                        {
                            if (location.TryGetProperty("start", out var start) &&
                                start.TryGetProperty("value", out var startVal))
                                pf.Start = startVal.GetInt32();

                            if (location.TryGetProperty("end", out var end) &&
                                end.TryGetProperty("value", out var endVal))
                                pf.End = endVal.GetInt32();
                        }

                        if (pf.Start.HasValue && pf.End.HasValue)
                            result.Add(pf);
                    }
                }
            }
            catch { }
            return result;
        }

        private static bool IsRelevantFeatureType(string type)
        {
            return type switch
            {
                "Domain" => true,
                "Region" => true,
                "Repeat" => true,
                "Motif" => true,
                "Compositional bias" => true,
                "Signal peptide" => true,
                "Transit peptide" => true,
                "Transmembrane" => true,
                "Topological domain" => true,
                "Active site" => true,
                "Binding site" => true,
                "Site" => true,
                "Disulfide bond" => true,
                "Modified residue" => true,
                "Chain" => true,
                "Propeptide" => true,
                _ => false
            };
        }

        private List<string> GetKeywords(JsonElement root)
        {
            var result = new List<string>();
            try
            {
                if (root.TryGetProperty("keywords", out var keywords))
                {
                    foreach (var kw in keywords.EnumerateArray())
                    {
                        if (kw.TryGetProperty("name", out var name))
                            result.Add(name.GetString());
                    }
                }
            }
            catch { }
            return result;
        }

        private List<GoTermReference> GetGoTerms(JsonElement root)
        {
            var result = new List<GoTermReference>();
            try
            {
                if (root.TryGetProperty("uniProtKBCrossReferences", out var refs))
                {
                    foreach (var reference in refs.EnumerateArray())
                    {
                        if (reference.TryGetProperty("database", out var db) && db.GetString() == "GO")
                        {
                            var goRef = new GoTermReference();

                            if (reference.TryGetProperty("id", out var id))
                                goRef.Id = id.GetString();

                            if (reference.TryGetProperty("properties", out var props))
                            {
                                foreach (var prop in props.EnumerateArray())
                                {
                                    if (prop.TryGetProperty("key", out var key) &&
                                        prop.TryGetProperty("value", out var value))
                                    {
                                        if (key.GetString() == "GoTerm")
                                            goRef.Term = value.GetString();
                                        else if (key.GetString() == "GoEvidenceType")
                                            goRef.EvidenceType = value.GetString();
                                    }
                                }
                            }

                            if (!string.IsNullOrEmpty(goRef.Id))
                                result.Add(goRef);
                        }
                    }
                }
            }
            catch { }
            return result;
        }

        private List<string> GetPdbIds(JsonElement root)
        {
            var result = new List<string>();
            try
            {
                if (root.TryGetProperty("uniProtKBCrossReferences", out var refs))
                {
                    foreach (var reference in refs.EnumerateArray())
                    {
                        if (reference.TryGetProperty("database", out var db) && db.GetString() == "PDB")
                        {
                            if (reference.TryGetProperty("id", out var id))
                                result.Add(id.GetString());
                        }
                    }
                }
            }
            catch { }
            return result;
        }

        private List<DomainReference> GetInterProDomains(JsonElement root)
        {
            var result = new List<DomainReference>();
            try
            {
                if (root.TryGetProperty("uniProtKBCrossReferences", out var refs))
                {
                    foreach (var reference in refs.EnumerateArray())
                    {
                        if (reference.TryGetProperty("database", out var db) && db.GetString() == "InterPro")
                        {
                            var domain = new DomainReference();

                            if (reference.TryGetProperty("id", out var id))
                                domain.Id = id.GetString();

                            if (reference.TryGetProperty("properties", out var props))
                            {
                                foreach (var prop in props.EnumerateArray())
                                {
                                    if (prop.TryGetProperty("key", out var key) &&
                                        prop.TryGetProperty("value", out var value) &&
                                        key.GetString() == "EntryName")
                                    {
                                        domain.Name = value.GetString();
                                    }
                                }
                            }

                            if (!string.IsNullOrEmpty(domain.Id))
                                result.Add(domain);
                        }
                    }
                }
            }
            catch { }
            return result;
        }

        private List<PathwayReference> GetPathwayIds(JsonElement root)
        {
            var result = new List<PathwayReference>();
            try
            {
                if (root.TryGetProperty("uniProtKBCrossReferences", out var refs))
                {
                    foreach (var reference in refs.EnumerateArray())
                    {
                        if (reference.TryGetProperty("database", out var db))
                        {
                            var dbName = db.GetString();
                            if (dbName == "Reactome" || dbName == "KEGG")
                            {
                                var pathway = new PathwayReference { Database = dbName };

                                if (reference.TryGetProperty("id", out var id))
                                    pathway.Id = id.GetString();

                                if (reference.TryGetProperty("properties", out var props))
                                {
                                    foreach (var prop in props.EnumerateArray())
                                    {
                                        if (prop.TryGetProperty("key", out var key) &&
                                            prop.TryGetProperty("value", out var value) &&
                                            key.GetString() == "PathwayName")
                                        {
                                            pathway.Name = value.GetString();
                                        }
                                    }
                                }

                                if (!string.IsNullOrEmpty(pathway.Id))
                                    result.Add(pathway);
                            }
                        }
                    }
                }
            }
            catch { }
            return result;
        }

        private List<string> GetPubMedIds(JsonElement root)
        {
            var result = new HashSet<string>();
            try
            {
                if (root.TryGetProperty("references", out var refs))
                {
                    foreach (var reference in refs.EnumerateArray())
                    {
                        if (reference.TryGetProperty("citation", out var citation))
                        {
                            if (citation.TryGetProperty("citationCrossReferences", out var crossRefs))
                            {
                                foreach (var crossRef in crossRefs.EnumerateArray())
                                {
                                    if (crossRef.TryGetProperty("database", out var db) &&
                                        db.GetString() == "PubMed")
                                    {
                                        if (crossRef.TryGetProperty("id", out var id))
                                            result.Add(id.GetString());
                                    }
                                }
                            }
                        }
                    }
                }
            }
            catch { }
            return result.ToList();
        }

        private string GetProteinExistence(JsonElement root)
        {
            try
            {
                if (root.TryGetProperty("proteinExistence", out var pe))
                    return pe.GetString();
            }
            catch { }
            return null;
        }

        private DateTime? GetLastModified(JsonElement root)
        {
            try
            {
                if (root.TryGetProperty("entryAudit", out var audit))
                {
                    if (audit.TryGetProperty("lastSequenceUpdateDate", out var date))
                        return DateTime.Parse(date.GetString());
                }
            }
            catch { }
            return null;
        }

        #endregion

        private string NormalizeProteinId(string proteinId)
        {
            if (string.IsNullOrWhiteSpace(proteinId))
                return proteinId;

            // Handle sp|P36578|RL4_HUMAN format
            if (proteinId.Contains("|"))
            {
                var parts = proteinId.Split('|');
                if (parts.Length >= 2)
                    return parts[1]; // Return the accession
            }

            // Handle RPL4_HUMAN or P36578 format - use directly
            return proteinId.Trim();
        }

        /// <summary>
        /// Clears the cache
        /// </summary>
        public void ClearCache()
        {
            _cache.Clear();
        }

        /// <summary>
        /// Gets number of cached entries
        /// </summary>
        public int CacheCount => _cache.Count;
    }

    #region Data Models

    /// <summary>
    /// Comprehensive protein information from UniProt
    /// </summary>
    public class UniProtEntry
    {
        // Identifiers
        public string AccessionId { get; set; }
        public List<string> SecondaryAccessions { get; set; } = new();
        public string EntryName { get; set; }

        // Names
        public string ProteinName { get; set; }
        public List<string> AlternativeNames { get; set; } = new();
        public string GeneName { get; set; }
        public List<string> GeneNameSynonyms { get; set; } = new();

        // Organism
        public string Organism { get; set; }
        public int? OrganismId { get; set; }

        // Function & Location
        public string Function { get; set; }
        public List<string> SubcellularLocations { get; set; } = new();
        public string TissueSpecificity { get; set; }
        public List<string> Involvement { get; set; } = new(); // Disease associations

        // Sequence info
        public int? SequenceLength { get; set; }
        public double? MolecularWeight { get; set; }
        public string SequenceString { get; set; }

        // Structural features (domains, regions, sites from UniProt)
        public List<ProteinFeature> Features { get; set; } = new();

        // Annotations
        public List<string> Keywords { get; set; } = new();
        public List<GoTermReference> GoTerms { get; set; } = new();

        // Cross-references
        public List<string> PdbIds { get; set; } = new();
        public List<DomainReference> InterProDomains { get; set; } = new();
        public List<PathwayReference> PathwayIds { get; set; } = new();
        public List<string> PubMedIds { get; set; } = new();

        // Metadata
        public string ProteinExistence { get; set; }
        public DateTime? LastModified { get; set; }

        // Computed properties
        public string DisplayName => !string.IsNullOrEmpty(ProteinName)
            ? ProteinName
            : (!string.IsNullOrEmpty(GeneName) ? GeneName : EntryName);

        public string MolecularWeightFormatted => MolecularWeight.HasValue
            ? $"{MolecularWeight.Value / 1000:F1} kDa"
            : null;

        // URLs
        public string UniProtUrl => !string.IsNullOrEmpty(AccessionId)
            ? $"https://www.uniprot.org/uniprotkb/{AccessionId}"
            : null;

        public string AlphaFoldUrl => !string.IsNullOrEmpty(AccessionId)
            ? $"https://alphafold.ebi.ac.uk/entry/{AccessionId}"
            : null;

        public string StringDbUrl => !string.IsNullOrEmpty(AccessionId) && OrganismId.HasValue
            ? $"https://string-db.org/network/{OrganismId}.{AccessionId}"
            : null;

        public string NcbiGeneUrl => !string.IsNullOrEmpty(GeneName)
            ? $"https://www.ncbi.nlm.nih.gov/gene/?term={GeneName}"
            : null;

        public string GetPdbUrl(string pdbId) => $"https://www.rcsb.org/structure/{pdbId}";

        public string GetPubMedUrl(string pmid) => $"https://pubmed.ncbi.nlm.nih.gov/{pmid}";

        public string GetReactomeUrl(string reactomeId) => $"https://reactome.org/content/detail/{reactomeId}";

        // Summary for tooltips
        public string TooltipSummary
        {
            get
            {
                var lines = new List<string>();

                if (!string.IsNullOrEmpty(ProteinName))
                    lines.Add(ProteinName);

                if (!string.IsNullOrEmpty(GeneName))
                    lines.Add($"Gene: {GeneName}");

                if (!string.IsNullOrEmpty(Organism))
                    lines.Add($"Organism: {Organism}");

                if (SequenceLength.HasValue)
                    lines.Add($"Length: {SequenceLength} aa");

                if (MolecularWeight.HasValue)
                    lines.Add($"Mass: {MolecularWeightFormatted}");

                if (SubcellularLocations.Count > 0)
                    lines.Add($"Location: {string.Join(", ", SubcellularLocations.Take(3))}");

                if (!string.IsNullOrEmpty(Function))
                {
                    var funcShort = Function.Length > 150 ? Function.Substring(0, 147) + "..." : Function;
                    lines.Add($"Function: {funcShort}");
                }

                return string.Join("\n", lines);
            }
        }

        public override string ToString()
        {
            return $"{GeneName ?? EntryName}: {ProteinName ?? "Unknown"}";
        }
    }

    public class GoTermReference
    {
        public string Id { get; set; }
        public string Term { get; set; }
        public string EvidenceType { get; set; }

        public string Url => $"https://www.ebi.ac.uk/QuickGO/term/{Id}";
    }

    public class DomainReference
    {
        public string Id { get; set; }
        public string Name { get; set; }

        public string Url => $"https://www.ebi.ac.uk/interpro/entry/InterPro/{Id}";
    }

    public class PathwayReference
    {
        public string Database { get; set; }
        public string Id { get; set; }
        public string Name { get; set; }

        public string Url => Database switch
        {
            "Reactome" => $"https://reactome.org/content/detail/{Id}",
            "KEGG" => $"https://www.kegg.jp/entry/{Id}",
            _ => null
        };
    }

    /// <summary>
    /// A structural or functional feature from UniProt (domain, region, binding site, etc.)
    /// with residue-level positions for overlay on coverage maps.
    /// </summary>
    public class ProteinFeature
    {
        /// <summary>
        /// Feature type (Domain, Region, Transmembrane, Active site, Signal peptide, etc.)
        /// </summary>
        public string Type { get; set; }

        /// <summary>
        /// Human-readable description (e.g. "Kinase domain", "ATP binding")
        /// </summary>
        public string Description { get; set; }

        /// <summary>
        /// 1-based start position in the protein sequence
        /// </summary>
        public int? Start { get; set; }

        /// <summary>
        /// 1-based end position in the protein sequence
        /// </summary>
        public int? End { get; set; }

        /// <summary>
        /// Length of this feature in residues
        /// </summary>
        public int Length => (Start.HasValue && End.HasValue) ? End.Value - Start.Value + 1 : 0;

        /// <summary>
        /// Whether this is a range feature (domain, region) or a point feature (active site, modified residue)
        /// </summary>
        public bool IsRange => Start.HasValue && End.HasValue && End.Value > Start.Value;

        public override string ToString()
        {
            string pos = Start == End ? $"pos {Start}" : $"{Start}-{End}";
            return $"{Type}: {Description} ({pos})";
        }
    }

    #endregion
}