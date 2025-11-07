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
            string outputDatabasePath)
        {
            var parser = new TranscriptomicTsvParser();
            var referenceService = new ReferenceDataService();

            Console.WriteLine("Parsing gene expression matrix...");
            var expressionRecords = await parser.ParseGeneExpressionMatrixAsync(expressionTsvPath);
            Console.WriteLine($"Loaded {expressionRecords.Count:N0} non-zero expression values");

            Console.WriteLine("Parsing cell metadata...");
            var metadata = await parser.ParseCellMetadataAsync(metadataTsvPath);
            Console.WriteLine($"Loaded metadata for {metadata.Count:N0} cells");

            Console.WriteLine("Creating SQLite database...");
            await referenceService.CreateDatabaseAsync(outputDatabasePath);

            Console.WriteLine("Writing transcriptomic data to database...");
            await referenceService.WriteTranscriptomicDataAsync(
                outputDatabasePath,
                expressionRecords,
                metadata);

            Console.WriteLine($"Conversion complete!");
            Console.WriteLine($"Output database: {outputDatabasePath}");

            var fileInfo = new FileInfo(outputDatabasePath);
            Console.WriteLine($"Database size: {fileInfo.Length / (1024.0 * 1024.0):F2} MB");
        }
    }
}