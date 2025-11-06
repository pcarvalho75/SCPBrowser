using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace SCPBrowser
{
    public class GoAnnotationParser
    {
        public async Task<List<ProteinGoAnnotation>> ParseGafFileAsync(string filePath)
        {
            if (!File.Exists(filePath))
                throw new FileNotFoundException("GAF file not found", filePath);

            var annotations = new Dictionary<string, ProteinGoAnnotation>();
            var lines = await File.ReadAllLinesAsync(filePath);

            foreach (var line in lines)
            {
                // Skip empty lines and comments
                if (string.IsNullOrWhiteSpace(line) || line.StartsWith("!"))
                    continue;

                var parts = line.Split('\t');

                // GAF 2.2 format should have at least 15 columns
                if (parts.Length < 15)
                    continue;

                // Extract key fields
                var uniprotId = parts[1].Trim();  // Column 2: DB Object ID
                var geneName = parts[2].Trim();    // Column 3: DB Object Symbol
                var goTermId = parts[4].Trim();    // Column 5: GO ID

                // Validate GO term format
                if (!goTermId.StartsWith("GO:"))
                    continue;

                // Add or update annotation
                if (!annotations.ContainsKey(uniprotId))
                {
                    annotations[uniprotId] = new ProteinGoAnnotation
                    {
                        ProteinId = uniprotId,
                        GeneName = geneName,
                        GoTermIds = new List<string>()
                    };
                }

                // Add GO term if not already present
                if (!annotations[uniprotId].GoTermIds.Contains(goTermId))
                {
                    annotations[uniprotId].GoTermIds.Add(goTermId);
                }
            }

            return annotations.Values.ToList();
        }

        public async Task<GoAnnotationDatabase> ParseAndBuildDatabaseAsync(string filePath)
        {
            var annotationsList = await ParseGafFileAsync(filePath);
            var database = new GoAnnotationDatabase();

            foreach (var annotation in annotationsList)
            {
                // Build protein -> GO terms mapping
                database.ProteinToGoTerms[annotation.ProteinId] = annotation.GoTermIds;

                // Build GO term -> proteins mapping
                foreach (var goTermId in annotation.GoTermIds)
                {
                    if (!database.GoTermToProteins.ContainsKey(goTermId))
                    {
                        database.GoTermToProteins[goTermId] = new List<string>();
                    }

                    if (!database.GoTermToProteins[goTermId].Contains(annotation.ProteinId))
                    {
                        database.GoTermToProteins[goTermId].Add(annotation.ProteinId);
                    }
                }
            }

            return database;
        }
    }
}