using System;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace TransmutationLearning.Services
{
    /// <summary>
    /// Manages downloading STRING database files for PPI integration.
    /// Downloads the physical protein links file for Homo sapiens.
    /// </summary>
    public class PPIDownloadManager
    {
        // STRING DB download URLs (v12.0 - latest stable)
        private const string STRING_PHYSICAL_LINKS_URL = 
            "https://stringdb-downloads.org/download/protein.physical.links.v12.0/9606.protein.physical.links.v12.0.txt.gz";
        
        private const string STRING_ALIASES_URL = 
            "https://stringdb-downloads.org/download/protein.aliases.v12.0/9606.protein.aliases.v12.0.txt.gz";

        private readonly HttpClient _httpClient;

        public PPIDownloadManager()
        {
            _httpClient = new HttpClient();
            _httpClient.Timeout = TimeSpan.FromMinutes(10); // Large file timeout
        }

        /// <summary>
        /// Get the default storage path for PPI data files
        /// </summary>
        public static string GetDefaultPPIDirectory()
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return Path.Combine(appData, "SCPBrowser", "PPI");
        }

        /// <summary>
        /// Get the path to the physical links file
        /// </summary>
        public static string GetPhysicalLinksPath()
        {
            return Path.Combine(GetDefaultPPIDirectory(), "9606.protein.physical.links.v12.0.txt");
        }

        /// <summary>
        /// Get the path to the aliases file
        /// </summary>
        public static string GetAliasesPath()
        {
            return Path.Combine(GetDefaultPPIDirectory(), "9606.protein.aliases.v12.0.txt");
        }

        /// <summary>
        /// Check if PPI data files exist
        /// </summary>
        public static bool ArePPIFilesAvailable()
        {
            return File.Exists(GetPhysicalLinksPath()) && File.Exists(GetAliasesPath());
        }

        /// <summary>
        /// Download STRING PPI data files with progress reporting
        /// </summary>
        /// <param name="progress">Progress reporter (0-100)</param>
        /// <param name="statusCallback">Status message callback</param>
        /// <param name="cancellationToken">Cancellation token</param>
        public async Task DownloadPPIDataAsync(
            IProgress<int> progress,
            Action<string> statusCallback,
            CancellationToken cancellationToken = default)
        {
            var directory = GetDefaultPPIDirectory();
            Directory.CreateDirectory(directory);

            // Download physical links (~20MB compressed)
            statusCallback?.Invoke("Downloading protein interactions (physical links)...");
            await DownloadAndDecompressAsync(
                STRING_PHYSICAL_LINKS_URL,
                GetPhysicalLinksPath(),
                progress,
                0, 50, // First 50% of progress
                cancellationToken);

            // Download aliases (~15MB compressed) - needed for gene symbol mapping
            statusCallback?.Invoke("Downloading protein aliases (name mapping)...");
            await DownloadAndDecompressAsync(
                STRING_ALIASES_URL,
                GetAliasesPath(),
                progress,
                50, 100, // Last 50% of progress
                cancellationToken);

            statusCallback?.Invoke("PPI data download complete!");
        }

        /// <summary>
        /// Download a gzipped file and decompress it
        /// </summary>
        private async Task DownloadAndDecompressAsync(
            string url,
            string outputPath,
            IProgress<int> progress,
            int progressStart,
            int progressEnd,
            CancellationToken cancellationToken)
        {
            var tempGzPath = outputPath + ".gz.tmp";

            try
            {
                // Download with progress
                using (var response = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken))
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
                                var scaledProgress = progressStart + (int)(downloadProgress * (progressEnd - progressStart) * 0.8);
                                progress?.Report(scaledProgress);
                            }
                        }
                    }
                }

                // Decompress
                progress?.Report(progressStart + (int)((progressEnd - progressStart) * 0.85));

                using (var compressedStream = new FileStream(tempGzPath, FileMode.Open, FileAccess.Read))
                using (var gzipStream = new GZipStream(compressedStream, CompressionMode.Decompress))
                using (var outputStream = new FileStream(outputPath, FileMode.Create, FileAccess.Write))
                {
                    await gzipStream.CopyToAsync(outputStream, 81920, cancellationToken);
                }

                progress?.Report(progressEnd);
            }
            finally
            {
                // Clean up temp file
                if (File.Exists(tempGzPath))
                {
                    try { File.Delete(tempGzPath); } catch { }
                }
            }
        }

        /// <summary>
        /// Delete downloaded PPI files
        /// </summary>
        public static void DeletePPIFiles()
        {
            var linksPath = GetPhysicalLinksPath();
            var aliasesPath = GetAliasesPath();

            if (File.Exists(linksPath))
                File.Delete(linksPath);
            if (File.Exists(aliasesPath))
                File.Delete(aliasesPath);
        }

        /// <summary>
        /// Get the total size of downloaded PPI files
        /// </summary>
        public static long GetPPIFilesSize()
        {
            long size = 0;
            var linksPath = GetPhysicalLinksPath();
            var aliasesPath = GetAliasesPath();

            if (File.Exists(linksPath))
                size += new FileInfo(linksPath).Length;
            if (File.Exists(aliasesPath))
                size += new FileInfo(aliasesPath).Length;

            return size;
        }
    }
}
