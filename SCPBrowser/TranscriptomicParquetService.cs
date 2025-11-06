using Parquet;
using Parquet.Data;
using Parquet.Schema;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace SCPBrowser
{
    public class TranscriptomicParquetService
    {
        public async Task WriteGeneExpressionAsync(List<GeneExpressionRecord> records, string outputPath)
        {
            if (records == null || records.Count == 0)
                throw new ArgumentException("No records to write");

            var schema = new ParquetSchema(
                new DataField<string>("GeneName"),
                new DataField<string>("CellID"),
                new DataField<int>("Count")
            );

            using (Stream fileStream = File.Create(outputPath))
            {
                using (var parquetWriter = await ParquetWriter.CreateAsync(schema, fileStream))
                {
                    const int batchSize = 100000;

                    for (int i = 0; i < records.Count; i += batchSize)
                    {
                        var batch = records.Skip(i).Take(batchSize).ToList();

                        using (var groupWriter = parquetWriter.CreateRowGroup())
                        {
                            var geneNames = batch.Select(r => r.GeneName).ToArray();
                            var cellIds = batch.Select(r => r.CellID).ToArray();
                            var counts = batch.Select(r => r.Count).ToArray();

                            await groupWriter.WriteColumnAsync(new DataColumn(schema.DataFields[0], geneNames));
                            await groupWriter.WriteColumnAsync(new DataColumn(schema.DataFields[1], cellIds));
                            await groupWriter.WriteColumnAsync(new DataColumn(schema.DataFields[2], counts));
                        }
                    }
                }
            }
        }

        public async Task WriteCellMetadataAsync(List<CellMetadata> metadata, string outputPath)
        {
            if (metadata == null || metadata.Count == 0)
                throw new ArgumentException("No metadata to write");

            var schema = new ParquetSchema(
                new DataField<string>("CellID"),
                new DataField<string>("Age"),
                new DataField<string>("Sex"),
                new DataField<string>("Batch"),
                new DataField<string>("CellType"),
                new DataField<int?>("GenesDetected"),
                new DataField<long?>("TotalReads"),
                new DataField<long?>("MappedReads"),
                new DataField<double?>("MappingRate")
            );

            using (Stream fileStream = File.Create(outputPath))
            {
                using (var parquetWriter = await ParquetWriter.CreateAsync(schema, fileStream))
                {
                    using (var groupWriter = parquetWriter.CreateRowGroup())
                    {
                        await groupWriter.WriteColumnAsync(new DataColumn(schema.DataFields[0],
                            metadata.Select(m => m.CellID).ToArray()));
                        await groupWriter.WriteColumnAsync(new DataColumn(schema.DataFields[1],
                            metadata.Select(m => m.Age).ToArray()));
                        await groupWriter.WriteColumnAsync(new DataColumn(schema.DataFields[2],
                            metadata.Select(m => m.Sex).ToArray()));
                        await groupWriter.WriteColumnAsync(new DataColumn(schema.DataFields[3],
                            metadata.Select(m => m.Batch).ToArray()));
                        await groupWriter.WriteColumnAsync(new DataColumn(schema.DataFields[4],
                            metadata.Select(m => m.CellType).ToArray()));
                        await groupWriter.WriteColumnAsync(new DataColumn(schema.DataFields[5],
                            metadata.Select(m => m.GenesDetected).ToArray()));
                        await groupWriter.WriteColumnAsync(new DataColumn(schema.DataFields[6],
                            metadata.Select(m => m.TotalReads).ToArray()));
                        await groupWriter.WriteColumnAsync(new DataColumn(schema.DataFields[7],
                            metadata.Select(m => m.MappedReads).ToArray()));
                        await groupWriter.WriteColumnAsync(new DataColumn(schema.DataFields[8],
                            metadata.Select(m => m.MappingRate).ToArray()));
                    }
                }
            }
        }

        public async Task<TranscriptomicDatabase> LoadDatabaseAsync(string expressionPath, string metadataPath)
        {
            var database = new TranscriptomicDatabase();

            await LoadMetadataAsync(metadataPath, database);
            await LoadGeneExpressionAsync(expressionPath, database);

            return database;
        }

        private async Task LoadMetadataAsync(string metadataPath, TranscriptomicDatabase database)
        {
            if (!File.Exists(metadataPath))
                throw new FileNotFoundException("Metadata parquet file not found", metadataPath);

            using (Stream fileStream = File.OpenRead(metadataPath))
            {
                using (var parquetReader = await ParquetReader.CreateAsync(fileStream))
                {
                    for (int i = 0; i < parquetReader.RowGroupCount; i++)
                    {
                        using (var groupReader = parquetReader.OpenRowGroupReader(i))
                        {
                            var dataFields = parquetReader.Schema.GetDataFields();

                            var cellIdColumn = await groupReader.ReadColumnAsync(dataFields[0]);
                            var ageColumn = await groupReader.ReadColumnAsync(dataFields[1]);
                            var sexColumn = await groupReader.ReadColumnAsync(dataFields[2]);
                            var batchColumn = await groupReader.ReadColumnAsync(dataFields[3]);
                            var cellTypeColumn = await groupReader.ReadColumnAsync(dataFields[4]);
                            var genesDetectedColumn = await groupReader.ReadColumnAsync(dataFields[5]);
                            var totalReadsColumn = await groupReader.ReadColumnAsync(dataFields[6]);
                            var mappedReadsColumn = await groupReader.ReadColumnAsync(dataFields[7]);
                            var mappingRateColumn = await groupReader.ReadColumnAsync(dataFields[8]);

                            var cellIds = cellIdColumn.Data as Array;
                            var ages = ageColumn.Data as Array;
                            var sexes = sexColumn.Data as Array;
                            var batches = batchColumn.Data as Array;
                            var cellTypes = cellTypeColumn.Data as Array;
                            var genesDetected = genesDetectedColumn.Data as Array;
                            var totalReads = totalReadsColumn.Data as Array;
                            var mappedReads = mappedReadsColumn.Data as Array;
                            var mappingRates = mappingRateColumn.Data as Array;

                            for (int row = 0; row < cellIds.Length; row++)
                            {
                                var cellId = cellIds.GetValue(row)?.ToString();
                                if (string.IsNullOrEmpty(cellId))
                                    continue;

                                var metadata = new CellMetadata
                                {
                                    CellID = cellId,
                                    Age = ages?.GetValue(row)?.ToString(),
                                    Sex = sexes?.GetValue(row)?.ToString(),
                                    Batch = batches?.GetValue(row)?.ToString(),
                                    CellType = cellTypes?.GetValue(row)?.ToString(),
                                    GenesDetected = genesDetected?.GetValue(row) as int?,
                                    TotalReads = totalReads?.GetValue(row) as long?,
                                    MappedReads = mappedReads?.GetValue(row) as long?,
                                    MappingRate = mappingRates?.GetValue(row) as double?
                                };

                                database.CellMetadata[cellId] = metadata;

                                if (!string.IsNullOrEmpty(metadata.CellType))
                                {
                                    if (!database.CellTypeIndex.ContainsKey(metadata.CellType))
                                        database.CellTypeIndex[metadata.CellType] = new List<string>();

                                    database.CellTypeIndex[metadata.CellType].Add(cellId);
                                }
                            }
                        }
                    }
                }
            }
        }

        private async Task LoadGeneExpressionAsync(string expressionPath, TranscriptomicDatabase database)
        {
            if (!File.Exists(expressionPath))
                throw new FileNotFoundException("Gene expression parquet file not found", expressionPath);

            using (Stream fileStream = File.OpenRead(expressionPath))
            {
                using (var parquetReader = await ParquetReader.CreateAsync(fileStream))
                {
                    for (int i = 0; i < parquetReader.RowGroupCount; i++)
                    {
                        using (var groupReader = parquetReader.OpenRowGroupReader(i))
                        {
                            var dataFields = parquetReader.Schema.GetDataFields();

                            var geneNameColumn = await groupReader.ReadColumnAsync(dataFields[0]);
                            var cellIdColumn = await groupReader.ReadColumnAsync(dataFields[1]);
                            var countColumn = await groupReader.ReadColumnAsync(dataFields[2]);

                            var geneNames = geneNameColumn.Data as Array;
                            var cellIds = cellIdColumn.Data as Array;
                            var counts = countColumn.Data as Array;

                            for (int row = 0; row < geneNames.Length; row++)
                            {
                                var geneName = geneNames.GetValue(row)?.ToString();
                                var cellId = cellIds.GetValue(row)?.ToString();
                                var countObj = counts.GetValue(row);

                                if (string.IsNullOrEmpty(geneName) || string.IsNullOrEmpty(cellId) || countObj == null)
                                    continue;

                                int count = Convert.ToInt32(countObj);

                                if (!database.GeneExpression.ContainsKey(geneName))
                                    database.GeneExpression[geneName] = new List<(string cellId, int count)>();

                                database.GeneExpression[geneName].Add((cellId, count));
                            }
                        }
                    }
                }
            }
        }
    }
}