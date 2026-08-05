using SCPBrowser.Services;
using System;
using System.IO;
using System.Threading.Tasks;

namespace SCPBrowser
{
    public class TranscriptomicConverterUtility
    {
        public static async Task ConvertTsvToSqliteAsync(
            string expressionTsvPath,
            string metadataTsvPath,
            string outputDatabasePath,
            IProgressReporter progress = null)
        {
            // Run the heavy work on a background thread to keep UI responsive
            await Task.Run(async () =>
            {
                var parser = new TranscriptomicTsvParser();
                var referenceService = new ReferenceDataService();

                progress?.ReportMessage("Starting transcriptomic data conversion...");

                var parsedData = await parser.ParseTranscriptomicDataAsync(
                    expressionTsvPath,
                    metadataTsvPath,
                    progress);

                // Create the database using ProjectDatabaseService since it has the schema. Schema ONLY: this is a
                // standalone reference file, not a project, and writing a project_info row here made it openable
                // as a project that then appeared to contain no data.
                progress?.ReportMessage("Creating SQLite database...");
                var projectService = new ProjectDatabaseService(outputDatabasePath);
                await projectService.CreateSchemaOnlyAsync();

                progress?.ReportMessage("Writing data to database...");
                await referenceService.WriteTranscriptomicDataAsync(
                    outputDatabasePath,
                    parsedData,
                    clearExistingData: true,
                    progress: progress);

                var fileInfo = new FileInfo(outputDatabasePath);
                var fileSizeMB = fileInfo.Length / (1024.0 * 1024.0);

                progress?.ReportMessage("Conversion complete!");
                progress?.ReportProgress($"Database size: {fileSizeMB:F2} MB");

                // Calculate compression ratio
                var avgRecordSizeOld = 60.0; // Estimated old size with TEXT
                var estimatedOldSize = parsedData.ExpressionRecords.Count * avgRecordSizeOld;
                var compressionRatio = estimatedOldSize / fileInfo.Length;

                progress?.ReportProgress($"Compression ratio: ~{compressionRatio:F1}x smaller than text-based storage");
            });
        }
    }
}