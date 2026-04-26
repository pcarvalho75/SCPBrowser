using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;

namespace SCPBrowser.Services
{
    /// <summary>
    /// Result of a project backup operation.
    /// </summary>
    public class BackupResult
    {
        public bool Success { get; set; }
        public string BackupPath { get; set; } = string.Empty;
        public long BytesCopied { get; set; }
        public string ErrorMessage { get; set; } = string.Empty;
    }

    /// <summary>
    /// Creates full snapshot copies of a project directory before destructive operations.
    /// A project consists of:
    ///   project.db   (SQLite, hot-backed up via SqliteConnection.BackupDatabase)
    ///   imports/     (parquet source files, recursively copied)
    /// Backups are written to a sibling folder next to the project:
    ///   {projectName}_backup_{timestamp}_{reason}/
    /// </summary>
    public class ProjectBackupService
    {
        /// <summary>
        /// Creates a complete copy of the project at the predetermined backup folder path.
        /// Caller builds the path via BuildBackupPath so the path the user saw in any
        /// confirmation UI matches the path actually used here.
        /// On failure, any partially-created backup folder is cleaned up.
        /// </summary>
        public async Task<BackupResult> CreateProjectBackupAsync(string projectDbPath, string backupPath)
        {
            var result = new BackupResult { BackupPath = backupPath ?? string.Empty };

            try
            {
                if (string.IsNullOrWhiteSpace(projectDbPath) || !File.Exists(projectDbPath))
                {
                    result.ErrorMessage = $"Project database not found: {projectDbPath}";
                    return result;
                }

                string projectDir = Path.GetDirectoryName(projectDbPath) ?? string.Empty;
                if (string.IsNullOrEmpty(projectDir) || !Directory.Exists(projectDir))
                {
                    result.ErrorMessage = $"Project directory not found: {projectDir}";
                    return result;
                }

                if (string.IsNullOrWhiteSpace(backupPath))
                {
                    result.ErrorMessage = "Backup destination path is empty.";
                    return result;
                }

                string parentDir = Path.GetDirectoryName(backupPath) ?? string.Empty;
                if (string.IsNullOrEmpty(parentDir) || !Directory.Exists(parentDir))
                {
                    result.ErrorMessage = $"Backup parent directory not found: {parentDir}";
                    return result;
                }

                if (Directory.Exists(backupPath))
                {
                    result.ErrorMessage = $"Backup destination already exists: {backupPath}";
                    return result;
                }

                Directory.CreateDirectory(backupPath);

                // 1. Hot-backup the SQLite database (works while connections are open).
                string backupDbPath = Path.Combine(backupPath, Path.GetFileName(projectDbPath));
                await Task.Run(() => BackupSqliteDatabase(projectDbPath, backupDbPath));
                long dbBytes = new FileInfo(backupDbPath).Length;

                // 2. Recursive copy of the imports folder, if it exists.
                string sourceImports = Path.Combine(projectDir, "imports");
                long importBytes = 0;
                if (Directory.Exists(sourceImports))
                {
                    string targetImports = Path.Combine(backupPath, "imports");
                    importBytes = await Task.Run(() => CopyDirectoryRecursive(sourceImports, targetImports));
                }

                result.Success = true;
                result.BytesCopied = dbBytes + importBytes;
                return result;
            }
            catch (Exception ex)
            {
                result.ErrorMessage = ex.Message;
                TryCleanupPartialBackup(result.BackupPath);
                return result;
            }
        }

        /// <summary>
        /// Builds the backup folder path that should be used for a destructive operation
        /// happening NOW. The caller passes this same path to CreateProjectBackupAsync so
        /// any UI showing the destination matches the folder actually written.
        /// Returns empty string if the project paths can't be resolved.
        /// </summary>
        public string BuildBackupPath(string projectDbPath, string reason)
        {
            if (string.IsNullOrWhiteSpace(projectDbPath))
                return string.Empty;

            string projectDir = Path.GetDirectoryName(projectDbPath) ?? string.Empty;
            if (string.IsNullOrEmpty(projectDir))
                return string.Empty;

            string parentDir = Path.GetDirectoryName(projectDir) ?? string.Empty;
            if (string.IsNullOrEmpty(parentDir))
                return string.Empty;

            string projectName = Path.GetFileName(projectDir);
            string timestamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
            string sanitizedReason = SanitizeForFileName(reason);

            string backupFolderName = string.IsNullOrEmpty(sanitizedReason)
                ? $"{projectName}_backup_{timestamp}"
                : $"{projectName}_backup_{timestamp}_{sanitizedReason}";

            return Path.Combine(parentDir, backupFolderName);
        }

        /// <summary>
        /// Estimates the on-disk size of the project (project.db + imports folder).
        /// Returns 0 on any IO error so the UI can fall back to "unknown".
        /// </summary>
        public async Task<long> EstimateProjectSizeAsync(string projectDbPath)
        {
            if (string.IsNullOrWhiteSpace(projectDbPath))
                return 0;

            return await Task.Run(() =>
            {
                try
                {
                    long total = 0;

                    if (File.Exists(projectDbPath))
                        total += new FileInfo(projectDbPath).Length;

                    string projectDir = Path.GetDirectoryName(projectDbPath) ?? string.Empty;
                    if (!string.IsNullOrEmpty(projectDir))
                    {
                        string importsDir = Path.Combine(projectDir, "imports");
                        if (Directory.Exists(importsDir))
                            total += DirectorySize(importsDir);
                    }

                    return total;
                }
                catch
                {
                    return 0L;
                }
            });
        }

        private static long DirectorySize(string dir)
        {
            long total = 0;
            foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            {
                try { total += new FileInfo(f).Length; }
                catch { /* skip files we can't stat */ }
            }
            return total;
        }

        private static void BackupSqliteDatabase(string sourceDbPath, string destDbPath)
        {
            using (var source = new SqliteConnection($"Data Source={sourceDbPath}"))
            using (var dest = new SqliteConnection($"Data Source={destDbPath}"))
            {
                source.Open();
                dest.Open();
                source.BackupDatabase(dest);
            }
        }

        private static long CopyDirectoryRecursive(string sourceDir, string targetDir)
        {
            Directory.CreateDirectory(targetDir);
            long totalBytes = 0;

            foreach (var file in Directory.GetFiles(sourceDir))
            {
                string targetFile = Path.Combine(targetDir, Path.GetFileName(file));
                File.Copy(file, targetFile, overwrite: false);
                totalBytes += new FileInfo(targetFile).Length;
            }

            foreach (var subDir in Directory.GetDirectories(sourceDir))
            {
                string targetSubDir = Path.Combine(targetDir, Path.GetFileName(subDir));
                totalBytes += CopyDirectoryRecursive(subDir, targetSubDir);
            }

            return totalBytes;
        }

        private static void TryCleanupPartialBackup(string backupPath)
        {
            if (string.IsNullOrEmpty(backupPath) || !Directory.Exists(backupPath))
                return;

            try
            {
                Directory.Delete(backupPath, recursive: true);
            }
            catch
            {
                // Best-effort cleanup so the original failure surfaces unmodified.
            }
        }

        private static string SanitizeForFileName(string input)
        {
            if (string.IsNullOrWhiteSpace(input))
                return string.Empty;

            var invalid = Path.GetInvalidFileNameChars();
            var chars = input.Trim().ToCharArray();
            for (int i = 0; i < chars.Length; i++)
            {
                if (Array.IndexOf(invalid, chars[i]) >= 0 || chars[i] == ' ')
                    chars[i] = '_';
            }

            string cleaned = new string(chars);
            const int maxLen = 80;
            if (cleaned.Length > maxLen)
                cleaned = cleaned.Substring(0, maxLen);

            return cleaned;
        }
    }
}
