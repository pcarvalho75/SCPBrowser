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
            GoSlimDatabase goSlimDatabase,
            GoAnnotationDatabase annotationDatabase,
            string outputPath)
        {
            // Schema for GO terms (row group 1)
            var goTermSchema = new ParquetSchema(
                new DataField<string>("go_id"),
                new DataField<string>("name"),
                new DataField<string>("namespace")
            );

            // Schema for protein annotations (row group 2)
            var annotationSchema = new ParquetSchema(
                new DataField<string>("protein_id"),
                new DataField<string>("go_term_ids")
            );

            using (Stream fileStream = File.Create(outputPath))
            {
                // Write Row Group 1: GO Terms
                using (var parquetWriter = await ParquetWriter.CreateAsync(goTermSchema, fileStream))
                {
                    var goTermsList = goSlimDatabase.Terms.Values.OrderBy(t => t.Id).ToList();

                    var goIds = goTermsList.Select(t => t.Id).ToArray();
                    var names = goTermsList.Select(t => t.Name ?? string.Empty).ToArray();
                    var namespaces = goTermsList.Select(t => t.Namespace ?? string.Empty).ToArray();

                    using (ParquetRowGroupWriter groupWriter = parquetWriter.CreateRowGroup())
                    {
                        await groupWriter.WriteColumnAsync(new DataColumn(
                            goTermSchema.DataFields[0],
                            goIds));

                        await groupWriter.WriteColumnAsync(new DataColumn(
                            goTermSchema.DataFields[1],
                            names));

                        await groupWriter.WriteColumnAsync(new DataColumn(
                            goTermSchema.DataFields[2],
                            namespaces));
                    }
                }

                // Write Row Group 2: Protein Annotations
                using (var parquetWriter = await ParquetWriter.CreateAsync(annotationSchema, fileStream, append: true))
                {
                    var proteinIds = annotationDatabase.ProteinToGoTerms.Keys.OrderBy(p => p).ToList();
                    var goTermIdStrings = proteinIds
                        .Select(proteinId => string.Join(",", annotationDatabase.ProteinToGoTerms[proteinId]))
                        .ToArray();

                    using (ParquetRowGroupWriter groupWriter = parquetWriter.CreateRowGroup())
                    {
                        await groupWriter.WriteColumnAsync(new DataColumn(
                            annotationSchema.DataFields[0],
                            proteinIds.ToArray()));

                        await groupWriter.WriteColumnAsync(new DataColumn(
                            annotationSchema.DataFields[1],
                            goTermIdStrings));
                    }
                }
            }
        }

        public async Task<(GoSlimDatabase goSlimDatabase, GoAnnotationDatabase annotationDatabase)> ReadCompiledAnnotationsAsync(string filePath)
        {
            if (!File.Exists(filePath))
                throw new FileNotFoundException("Compiled GO annotations Parquet file not found", filePath);

            var goSlimDatabase = new GoSlimDatabase();
            var annotationDatabase = new GoAnnotationDatabase();

            using (Stream fileStream = File.OpenRead(filePath))
            {
                using (var parquetReader = await ParquetReader.CreateAsync(fileStream))
                {
                    if (parquetReader.RowGroupCount < 2)
                        throw new InvalidDataException("Expected at least 2 row groups (GO terms + annotations)");

                    // Read Row Group 0: GO Terms
                    using (ParquetRowGroupReader groupReader = parquetReader.OpenRowGroupReader(0))
                    {
                        var goIdsColumn = await groupReader.ReadColumnAsync(
                            parquetReader.Schema.DataFields[0]);
                        var namesColumn = await groupReader.ReadColumnAsync(
                            parquetReader.Schema.DataFields[1]);
                        var namespacesColumn = await groupReader.ReadColumnAsync(
                            parquetReader.Schema.DataFields[2]);

                        var goIds = (string[])goIdsColumn.Data;
                        var names = (string[])namesColumn.Data;
                        var namespaces = (string[])namespacesColumn.Data;

                        for (int i = 0; i < goIds.Length; i++)
                        {
                            goSlimDatabase.Terms[goIds[i]] = new GoTerm
                            {
                                Id = goIds[i],
                                Name = names[i],
                                Namespace = namespaces[i]
                            };
                        }
                    }

                    // Read Row Group 1: Protein Annotations
                    using (ParquetRowGroupReader groupReader = parquetReader.OpenRowGroupReader(1))
                    {
                        var proteinIdsColumn = await groupReader.ReadColumnAsync(
                            parquetReader.Schema.DataFields[0]);
                        var goTermIdsColumn = await groupReader.ReadColumnAsync(
                            parquetReader.Schema.DataFields[1]);

                        var proteinIds = (string[])proteinIdsColumn.Data;
                        var goTermIdStrings = (string[])goTermIdsColumn.Data;

                        for (int i = 0; i < proteinIds.Length; i++)
                        {
                            var proteinId = proteinIds[i];
                            var goTermIds = goTermIdStrings[i].Split(',').ToList();

                            // Build protein -> GO terms mapping
                            annotationDatabase.ProteinToGoTerms[proteinId] = goTermIds;

                            // Build GO term -> proteins mapping
                            foreach (var goTermId in goTermIds)
                            {
                                if (!annotationDatabase.GoTermToProteins.ContainsKey(goTermId))
                                {
                                    annotationDatabase.GoTermToProteins[goTermId] = new List<string>();
                                }

                                if (!annotationDatabase.GoTermToProteins[goTermId].Contains(proteinId))
                                {
                                    annotationDatabase.GoTermToProteins[goTermId].Add(proteinId);
                                }
                            }
                        }
                    }
                }
            }

            return (goSlimDatabase, annotationDatabase);
        }
    }
}