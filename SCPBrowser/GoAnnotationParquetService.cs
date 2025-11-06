using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Parquet;
using Parquet.Data;
using Parquet.Schema;

namespace SCPBrowser
{
    public class GoAnnotationParquetService
    {
        public async Task WriteCompiledAnnotationsAsync(
            GoAnnotationDatabase database,
            string outputPath)
        {
            // Create schema: protein_id (string), go_term_ids (string - comma-separated)
            var schema = new ParquetSchema(
                new DataField<string>("protein_id"),
                new DataField<string>("go_term_ids")
            );

            // Prepare data
            var proteinIds = database.ProteinToGoTerms.Keys.ToList();
            var goTermIdStrings = proteinIds
                .Select(proteinId => string.Join(",", database.ProteinToGoTerms[proteinId]))
                .ToList();

            // Write to Parquet
            using (Stream fileStream = File.Create(outputPath))
            {
                using (var parquetWriter = await ParquetWriter.CreateAsync(schema, fileStream))
                {
                    using (ParquetRowGroupWriter groupWriter = parquetWriter.CreateRowGroup())
                    {
                        await groupWriter.WriteColumnAsync(new DataColumn(
                            schema.DataFields[0],
                            proteinIds.ToArray()));

                        await groupWriter.WriteColumnAsync(new DataColumn(
                            schema.DataFields[1],
                            goTermIdStrings.ToArray()));
                    }
                }
            }
        }

        public async Task<GoAnnotationDatabase> ReadCompiledAnnotationsAsync(string filePath)
        {
            if (!File.Exists(filePath))
                throw new FileNotFoundException("Compiled GO annotations Parquet file not found", filePath);

            var database = new GoAnnotationDatabase();

            using (Stream fileStream = File.OpenRead(filePath))
            {
                using (var parquetReader = await ParquetReader.CreateAsync(fileStream))
                {
                    for (int i = 0; i < parquetReader.RowGroupCount; i++)
                    {
                        using (ParquetRowGroupReader groupReader = parquetReader.OpenRowGroupReader(i))
                        {
                            var proteinIdsColumn = await groupReader.ReadColumnAsync(
                                parquetReader.Schema.DataFields[0]);
                            var goTermIdsColumn = await groupReader.ReadColumnAsync(
                                parquetReader.Schema.DataFields[1]);

                            var proteinIds = (string[])proteinIdsColumn.Data;
                            var goTermIdStrings = (string[])goTermIdsColumn.Data;

                            for (int j = 0; j < proteinIds.Length; j++)
                            {
                                var proteinId = proteinIds[j];
                                var goTermIds = goTermIdStrings[j].Split(',').ToList();

                                // Build protein -> GO terms mapping
                                database.ProteinToGoTerms[proteinId] = goTermIds;

                                // Build GO term -> proteins mapping
                                foreach (var goTermId in goTermIds)
                                {
                                    if (!database.GoTermToProteins.ContainsKey(goTermId))
                                    {
                                        database.GoTermToProteins[goTermId] = new List<string>();
                                    }

                                    if (!database.GoTermToProteins[goTermId].Contains(proteinId))
                                    {
                                        database.GoTermToProteins[goTermId].Add(proteinId);
                                    }
                                }
                            }
                        }
                    }
                }
            }

            return database;
        }
    }
}