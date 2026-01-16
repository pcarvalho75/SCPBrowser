using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace TransmutationLearning.Services
{
    /// <summary>
    /// Service for loading and querying Protein-Protein Interaction (PPI) data from STRING database.
    ///
    /// The "Biological Gravity" concept: Proteins are not independent variables - they form
    /// physical complexes. If two cells express interacting proteins, they are functionally
    /// similar even if the raw expression values differ. PPIs act as "wormholes" in the
    /// high-dimensional space, pulling functionally related cells closer together.
    /// </summary>
    public class PPIService
    {
        // Adjacency list: ENSP ID -> set of interacting ENSP IDs
        private Dictionary<string, HashSet<string>> _interactions;

        // Gene symbol -> ENSP ID mapping (populated from aliases file)
        private Dictionary<string, string> _geneToEnsp;

        // ENSP ID -> Gene symbol mapping (reverse lookup)
        private Dictionary<string, string> _enspToGene;

        // Data directory for STRING files
        private string _dataDirectory;

        // STRING DB download URLs (v12.0)
        private const string STRING_PHYSICAL_LINKS_URL =
            "https://stringdb-downloads.org/download/protein.physical.links.v12.0/9606.protein.physical.links.v12.0.txt.gz";
        private const string STRING_ALIASES_URL =
            "https://stringdb-downloads.org/download/protein.aliases.v12.0/9606.protein.aliases.v12.0.txt.gz";

        // Minimum combined score to consider (700 = high confidence in STRING)
        public int MinConfidenceScore { get; set; } = 700;

        // Statistics
        public int TotalProteins { get; private set; }
        public int TotalInteractions { get; private set; }
        public bool IsLoaded => _interactions != null && _interactions.Count > 0;
        public bool HasAliases => _geneToEnsp != null && _geneToEnsp.Count > 0;

        public PPIService()
        {
            _interactions = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            _geneToEnsp = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            _enspToGene = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Load PPI data from STRING physical links file.
        /// Format: protein1 protein2 combined_score (tab or space separated)
        /// Example: 9606.ENSP00000000233 9606.ENSP00000263431 900
        /// </summary>
        /// <param name="filePath">Path to .txt or .txt.gz file</param>
        /// <param name="progress">Optional progress reporter (0-100)</param>
        public void LoadPhysicalLinks(string filePath, IProgress<int> progress = null)
        {
            if (!File.Exists(filePath))
                throw new FileNotFoundException($"PPI file not found: {filePath}");

            _interactions.Clear();
            TotalProteins = 0;
            TotalInteractions = 0;

            var allProteins = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // Determine if gzipped
            bool isGzipped = filePath.EndsWith(".gz", StringComparison.OrdinalIgnoreCase);

            using (var fileStream = File.OpenRead(filePath))
            {
                Stream readStream = isGzipped
                    ? new GZipStream(fileStream, CompressionMode.Decompress)
                    : fileStream;

                using (var reader = new StreamReader(readStream))
                {
                    string line;
                    int lineCount = 0;
                    bool isFirstLine = true;

                    while ((line = reader.ReadLine()) != null)
                    {
                        lineCount++;

                        // Report progress every 100k lines
                        if (lineCount % 100000 == 0)
                            progress?.Report(Math.Min(99, lineCount / 10000)); // Approximate

                        // Skip header line
                        if (isFirstLine)
                        {
                            isFirstLine = false;
                            if (line.StartsWith("protein1") || line.StartsWith("#"))
                                continue;
                        }

                        // Parse line: protein1 protein2 combined_score
                        var parts = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                        if (parts.Length < 3)
                            continue;

                        string protein1 = parts[0];
                        string protein2 = parts[1];

                        if (!int.TryParse(parts[2], out int score))
                            continue;

                        // Filter by confidence score
                        if (score < MinConfidenceScore)
                            continue;

                        // Strip species prefix (9606.) if present
                        protein1 = StripSpeciesPrefix(protein1);
                        protein2 = StripSpeciesPrefix(protein2);

                        // Add bidirectional interaction
                        AddInteraction(protein1, protein2);

                        allProteins.Add(protein1);
                        allProteins.Add(protein2);
                        TotalInteractions++;
                    }
                }
            }

            TotalProteins = allProteins.Count;
            progress?.Report(100);
        }

        /// <summary>
        /// Load gene symbol to ENSP ID mapping from STRING aliases file.
        /// Format: #string_protein_id alias source (tab separated)
        /// We look for "BioMart_HUGO" or "Ensembl_HGNC" sources for gene symbols.
        /// </summary>
        /// <param name="filePath">Path to protein.aliases.txt or .gz file</param>
        /// <param name="progress">Optional progress reporter (0-100)</param>
        public void LoadAliases(string filePath, IProgress<int> progress = null)
        {
            if (!File.Exists(filePath))
                throw new FileNotFoundException($"Aliases file not found: {filePath}");

            _geneToEnsp.Clear();
            _enspToGene.Clear();

            bool isGzipped = filePath.EndsWith(".gz", StringComparison.OrdinalIgnoreCase);

            // Priority sources for gene symbols (higher = better)
            var prioritySources = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
            {
                { "BioMart_HUGO", 100 },
                { "Ensembl_HGNC", 90 },
                { "Ensembl_HGNC_symbol", 90 },
                { "BLAST_UniProt_GN", 80 },
                { "Ensembl_gene", 70 },
                { "Ensembl_UniProt_GN", 60 }
            };

            // Track best mapping per gene symbol
            var bestMappings = new Dictionary<string, (string ensp, int priority)>(StringComparer.OrdinalIgnoreCase);

            using (var fileStream = File.OpenRead(filePath))
            {
                Stream readStream = isGzipped
                    ? new GZipStream(fileStream, CompressionMode.Decompress)
                    : fileStream;

                using (var reader = new StreamReader(readStream))
                {
                    string line;
                    int lineCount = 0;

                    while ((line = reader.ReadLine()) != null)
                    {
                        lineCount++;

                        if (lineCount % 100000 == 0)
                            progress?.Report(Math.Min(99, lineCount / 10000));

                        // Skip comments/header
                        if (line.StartsWith("#") || line.StartsWith("string_protein_id"))
                            continue;

                        // Parse: protein_id \t alias \t source
                        var parts = line.Split('\t');
                        if (parts.Length < 3)
                            continue;

                        string enspId = StripSpeciesPrefix(parts[0]);
                        string alias = parts[1].Trim();
                        string source = parts[2].Trim();

                        // Check if this is a gene symbol source
                        if (!prioritySources.TryGetValue(source, out int priority))
                            continue;

                        // Skip if alias looks like an ENSP ID
                        if (alias.StartsWith("ENSP", StringComparison.OrdinalIgnoreCase))
                            continue;

                        // Keep best mapping per gene symbol
                        if (!bestMappings.TryGetValue(alias, out var existing) || priority > existing.priority)
                        {
                            bestMappings[alias] = (enspId, priority);
                        }
                    }
                }
            }

            // Build final mappings
            foreach (var kvp in bestMappings)
            {
                string geneSymbol = kvp.Key;
                string enspId = kvp.Value.ensp;

                _geneToEnsp[geneSymbol] = enspId;

                // For reverse lookup, prefer shorter gene symbols (more likely correct)
                if (!_enspToGene.ContainsKey(enspId) || geneSymbol.Length < _enspToGene[enspId].Length)
                {
                    _enspToGene[enspId] = geneSymbol;
                }
            }

            progress?.Report(100);
        }

        /// <summary>
        /// Check if two proteins interact (using gene symbols).
        /// Returns true if they are known physical interactors.
        /// </summary>
        public bool AreInteracting(string gene1, string gene2)
        {
            if (!IsLoaded || !HasAliases)
                return false;

            // Map gene symbols to ENSP IDs
            if (!_geneToEnsp.TryGetValue(gene1, out string ensp1))
                return false;
            if (!_geneToEnsp.TryGetValue(gene2, out string ensp2))
                return false;

            // Check interaction
            if (_interactions.TryGetValue(ensp1, out var partners))
                return partners.Contains(ensp2);

            return false;
        }

        /// <summary>
        /// Get all known interactors for a protein (using gene symbol).
        /// Returns gene symbols of interacting proteins.
        /// </summary>
        public HashSet<string> GetInteractors(string geneSymbol)
        {
            var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (!IsLoaded || !HasAliases)
                return result;

            if (!_geneToEnsp.TryGetValue(geneSymbol, out string enspId))
                return result;

            if (!_interactions.TryGetValue(enspId, out var enspPartners))
                return result;

            foreach (var partnerEnsp in enspPartners)
            {
                if (_enspToGene.TryGetValue(partnerEnsp, out string partnerGene))
                    result.Add(partnerGene);
            }

            return result;
        }

        /// <summary>
        /// Calculate PPI boost between two cells based on co-expressed interacting protein pairs.
        ///
        /// For each pair of proteins (P1 in cell_i, P2 in cell_j) that are known interactors,
        /// we add to the boost score. This creates "biological gravity" - cells expressing
        /// complementary parts of protein complexes are pulled closer together.
        /// </summary>
        /// <param name="proteinsCell1">Set of proteins detected in cell 1 (gene symbols)</param>
        /// <param name="proteinsCell2">Set of proteins detected in cell 2 (gene symbols)</param>
        /// <returns>Normalized boost score (0 to 1)</returns>
        public double CalculatePPIBoost(HashSet<string> proteinsCell1, HashSet<string> proteinsCell2)
        {
            if (!IsLoaded || !HasAliases)
                return 0;

            int interactingPairs = 0;
            int checkedPairs = 0;

            // For efficiency, iterate over smaller set
            var smaller = proteinsCell1.Count <= proteinsCell2.Count ? proteinsCell1 : proteinsCell2;
            var larger = proteinsCell1.Count <= proteinsCell2.Count ? proteinsCell2 : proteinsCell1;

            foreach (var gene1 in smaller)
            {
                var interactors = GetInteractors(gene1);
                if (interactors.Count == 0)
                    continue;

                foreach (var gene2 in larger)
                {
                    checkedPairs++;
                    if (interactors.Contains(gene2))
                        interactingPairs++;
                }
            }

            if (checkedPairs == 0)
                return 0;

            // Normalize: what fraction of possible pairs are interacting?
            // Use sqrt to compress the range (many interactions shouldn't dominate too much)
            double rawBoost = (double)interactingPairs / Math.Max(1, Math.Sqrt(checkedPairs));

            // Clamp to [0, 1]
            return Math.Min(1.0, rawBoost);
        }

        /// <summary>
        /// Get statistics about loaded data
        /// </summary>
        public string GetStatusSummary()
        {
            if (!IsLoaded)
                return "PPI data not loaded";

            var sb = new System.Text.StringBuilder();
            sb.Append($"{TotalProteins:N0} proteins, {TotalInteractions:N0} interactions");

            if (HasAliases)
                sb.Append($", {_geneToEnsp.Count:N0} gene mappings");
            else
                sb.Append(" (no gene mapping)");

            return sb.ToString();
        }

        /// <summary>
        /// Clear all loaded data
        /// </summary>
        public void Clear()
        {
            _interactions.Clear();
            _geneToEnsp.Clear();
            _enspToGene.Clear();
            TotalProteins = 0;
            TotalInteractions = 0;
        }

        #region Data Directory and File Management

        /// <summary>
        /// Set the directory where STRING data files are stored
        /// </summary>
        public void SetDataDirectory(string directory)
        {
            _dataDirectory = directory;
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);
        }

        /// <summary>
        /// Get the path to the physical links file
        /// </summary>
        public string GetPhysicalLinksPath()
        {
            if (string.IsNullOrEmpty(_dataDirectory))
                throw new InvalidOperationException("Data directory not set. Call SetDataDirectory first.");
            return Path.Combine(_dataDirectory, "9606.protein.physical.links.v12.0.txt");
        }

        /// <summary>
        /// Get the path to the aliases file
        /// </summary>
        public string GetAliasesPath()
        {
            if (string.IsNullOrEmpty(_dataDirectory))
                throw new InvalidOperationException("Data directory not set. Call SetDataDirectory first.");
            return Path.Combine(_dataDirectory, "9606.protein.aliases.v12.0.txt");
        }

        /// <summary>
        /// Check if STRING data files exist in the configured directory
        /// </summary>
        public bool DataFilesExist()
        {
            if (string.IsNullOrEmpty(_dataDirectory))
                return false;
            return File.Exists(GetPhysicalLinksPath()) && File.Exists(GetAliasesPath());
        }

        #endregion

        #region Async Download and Load

        /// <summary>
        /// Download STRING PPI data files with progress reporting
        /// </summary>
        public async Task DownloadDataAsync(IProgress<(string status, double percent)> progress, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrEmpty(_dataDirectory))
                throw new InvalidOperationException("Data directory not set. Call SetDataDirectory first.");

            Directory.CreateDirectory(_dataDirectory);

            using (var httpClient = new HttpClient())
            {
                httpClient.Timeout = TimeSpan.FromMinutes(30);

                // Download physical links (~20MB compressed) - first 50%
                progress?.Report(("Downloading protein interactions...", 0));
                await DownloadAndDecompressAsync(
                    httpClient,
                    STRING_PHYSICAL_LINKS_URL,
                    GetPhysicalLinksPath(),
                    progress, 0, 50,
                    cancellationToken);

                // Download aliases (~15MB compressed) - last 50%
                progress?.Report(("Downloading protein aliases...", 50));
                await DownloadAndDecompressAsync(
                    httpClient,
                    STRING_ALIASES_URL,
                    GetAliasesPath(),
                    progress, 50, 100,
                    cancellationToken);
            }
        }

        /// <summary>
        /// Download a gzipped file and decompress it
        /// </summary>
        private async Task DownloadAndDecompressAsync(
            HttpClient httpClient,
            string url,
            string outputPath,
            IProgress<(string status, double percent)> progress,
            int progressStart,
            int progressEnd,
            CancellationToken cancellationToken)
        {
            var tempGzPath = outputPath + ".gz.tmp";
            var fileName = Path.GetFileName(outputPath);

            try
            {
                using (var response = await httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken))
                {
                    response.EnsureSuccessStatusCode();

                    var totalBytes = response.Content.Headers.ContentLength ?? -1;
                    var downloadedBytes = 0L;

                    using (var contentStream = await response.Content.ReadAsStreamAsync())
                    using (var fileStream = new FileStream(tempGzPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true))
                    {
                        var buffer = new byte[81920];
                        int bytesRead;

                        while ((bytesRead = await contentStream.ReadAsync(buffer, 0, buffer.Length, cancellationToken)) > 0)
                        {
                            await fileStream.WriteAsync(buffer, 0, bytesRead, cancellationToken);
                            downloadedBytes += bytesRead;

                            if (totalBytes > 0)
                            {
                                var downloadProgress = (double)downloadedBytes / totalBytes;
                                var scaledProgress = progressStart + downloadProgress * (progressEnd - progressStart) * 0.8;
                                var mbDownloaded = downloadedBytes / (1024.0 * 1024.0);
                                var mbTotal = totalBytes / (1024.0 * 1024.0);
                                progress?.Report(($"Downloading {fileName} ({mbDownloaded:F1}/{mbTotal:F1} MB)...", scaledProgress));
                            }
                        }
                    }
                }

                // Decompress
                progress?.Report(($"Decompressing {fileName}...", progressStart + (progressEnd - progressStart) * 0.85));

                using (var compressedStream = new FileStream(tempGzPath, FileMode.Open, FileAccess.Read))
                using (var gzipStream = new GZipStream(compressedStream, CompressionMode.Decompress))
                using (var outputStream = new FileStream(outputPath, FileMode.Create, FileAccess.Write))
                {
                    await gzipStream.CopyToAsync(outputStream, 81920, cancellationToken);
                }

                progress?.Report(($"Completed {fileName}", progressEnd));
            }
            finally
            {
                if (File.Exists(tempGzPath))
                {
                    try { File.Delete(tempGzPath); } catch { }
                }
            }
        }

        /// <summary>
        /// Load STRING data asynchronously (physical links + aliases)
        /// </summary>
        public async Task LoadDataAsync(IProgress<(string status, double percent)> progress, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrEmpty(_dataDirectory))
                throw new InvalidOperationException("Data directory not set. Call SetDataDirectory first.");

            var linksPath = GetPhysicalLinksPath();
            var aliasesPath = GetAliasesPath();

            if (!File.Exists(linksPath))
                throw new FileNotFoundException($"Physical links file not found: {linksPath}");
            if (!File.Exists(aliasesPath))
                throw new FileNotFoundException($"Aliases file not found: {aliasesPath}");

            // Load on background thread
            await Task.Run(() =>
            {
                // Load physical links (0-70%)
                progress?.Report(("Loading protein interactions...", 0));
                var linksProgress = progress != null
                    ? new Progress<int>(p => progress.Report(($"Loading interactions ({p}%)...", p * 0.7)))
                    : null;
                LoadPhysicalLinks(linksPath, linksProgress);

                // Load aliases (70-100%)
                progress?.Report(("Loading protein aliases...", 70));
                var aliasesProgress = progress != null
                    ? new Progress<int>(p => progress.Report(($"Loading aliases ({p}%)...", 70 + p * 0.3)))
                    : null;
                LoadAliases(aliasesPath, aliasesProgress);

                progress?.Report(("PPI data loaded", 100));
            }, cancellationToken);
        }

        #endregion

        #region Mapping Statistics

        /// <summary>
        /// Get mapping statistics for a set of protein names
        /// </summary>
        /// <param name="proteinNames">Protein/gene names to check</param>
        /// <returns>Tuple of (mapped count, total count)</returns>
        public (int mapped, int total) GetMappingStats(IEnumerable<string> proteinNames)
        {
            if (!HasAliases)
                return (0, 0);

            int total = 0;
            int mapped = 0;

            foreach (var name in proteinNames)
            {
                total++;
                if (_geneToEnsp.ContainsKey(name))
                    mapped++;
            }

            return (mapped, total);
        }

        #endregion

        #region PPI Boost Alias

        /// <summary>
        /// Compute PPI boost between two cells (alias for CalculatePPIBoost)
        /// </summary>
        public double ComputePPIBoost(HashSet<string> proteinsCell1, HashSet<string> proteinsCell2)
        {
            return CalculatePPIBoost(proteinsCell1, proteinsCell2);
        }

        #endregion

        #region Private Helpers

        private void AddInteraction(string protein1, string protein2)
        {
            // Bidirectional
            if (!_interactions.ContainsKey(protein1))
                _interactions[protein1] = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            _interactions[protein1].Add(protein2);

            if (!_interactions.ContainsKey(protein2))
                _interactions[protein2] = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            _interactions[protein2].Add(protein1);
        }

        private string StripSpeciesPrefix(string proteinId)
        {
            // STRING IDs are like "9606.ENSP00000000233" - strip the "9606." prefix
            if (proteinId.StartsWith("9606."))
                return proteinId.Substring(5);
            return proteinId;
        }

        #endregion
    }
}
