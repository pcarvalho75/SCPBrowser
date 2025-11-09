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
            var parser = new TranscriptomicTsvParser();
            var referenceService = new ReferenceDataService();

            progress?.ReportMessage("Starting transcriptomic data conversion...");

            var parsedData = await parser.ParseTranscriptomicDataAsync(
                expressionTsvPath,
                metadataTsvPath,
                progress);

            progress?.ReportMessage("Creating SQLite database...");
            await referenceService.CreateDatabaseAsync(outputDatabasePath);

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
        }
    }
}