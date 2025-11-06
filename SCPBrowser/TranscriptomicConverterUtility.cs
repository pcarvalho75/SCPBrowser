using System;
using System.IO;
using System.Threading.Tasks;

namespace SCPBrowser
{
    public class TranscriptomicConverterUtility
    {
        public static async Task ConvertTsvToParquetAsync(
            string expressionTsvPath,
            string metadataTsvPath,
            string outputDirectory)
        {
            var parser = new TranscriptomicTsvParser();
            var parquetService = new TranscriptomicParquetService();

            Console.WriteLine("Parsing gene expression matrix...");
            var expressionRecords = await parser.ParseGeneExpressionMatrixAsync(expressionTsvPath);
            Console.WriteLine($"Loaded {expressionRecords.Count:N0} non-zero expression values");

            Console.WriteLine("Parsing cell metadata...");
            var metadata = await parser.ParseCellMetadataAsync(metadataTsvPath);
            Console.WriteLine($"Loaded metadata for {metadata.Count:N0} cells");

            Directory.CreateDirectory(outputDirectory);

            var expressionParquetPath = Path.Combine(outputDirectory, "transcriptomic_expression.parquet");
            var metadataParquetPath = Path.Combine(outputDirectory, "transcriptomic_metadata.parquet");

            Console.WriteLine("Writing gene expression to Parquet...");
            await parquetService.WriteGeneExpressionAsync(expressionRecords, expressionParquetPath);

            Console.WriteLine("Writing cell metadata to Parquet...");
            await parquetService.WriteCellMetadataAsync(metadata, metadataParquetPath);

            Console.WriteLine($"Conversion complete!");
            Console.WriteLine($"Output files:");
            Console.WriteLine($"  {expressionParquetPath}");
            Console.WriteLine($"  {metadataParquetPath}");
        }
    }
}