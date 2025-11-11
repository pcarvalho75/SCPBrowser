using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace SCPBrowser.GOTools
{
    public class GoAnnotationCompiler
    {
        public async Task<GoAnnotationDatabase> CompileAnnotationsAsync(
     string goSlimOboPath,
     string goaGafPath,
     IProgressReporter progress = null)
        {
            progress?.ReportMessage("Loading GO Slim Database");
            progress?.ReportProgress("Parsing OBO file...");

            // Load GO Slim database
            var goSlimParser = new GoSlimParser();
            var goSlimDatabase = await goSlimParser.ParseOboFileAsync(goSlimOboPath);

            progress?.ReportProgress($"Loaded {goSlimDatabase.TotalTerms:N0} GO Slim terms");

            progress?.ReportMessage("Loading GOA Annotations");
            progress?.ReportProgress("Parsing GAF file...");

            // Load GOA annotations
            var goaParser = new GoAnnotationParser();
            var rawAnnotations = await goaParser.ParseGafFileAsync(goaGafPath);

            progress?.ReportProgress($"Loaded {rawAnnotations.Count:N0} protein annotations");

            // Build set of GO Slim term IDs for fast lookup
            var goSlimTermIds = new HashSet<string>(goSlimDatabase.Terms.Keys);

            progress?.ReportMessage("Compiling GO Annotations");
            progress?.ReportProgress("Filtering and mapping to GO Slim terms...");

            // Filter and compile annotations
            var compiledDatabase = new GoAnnotationDatabase();
            int processedCount = 0;
            int totalAnnotations = rawAnnotations.Count;

            foreach (var annotation in rawAnnotations)
            {
                var filteredGoTerms = new HashSet<string>();

                // For each GO term annotated to this protein
                foreach (var goTermId in annotation.GoTermIds)
                {
                    // If it's a GO Slim term, add it
                    if (goSlimTermIds.Contains(goTermId))
                    {
                        filteredGoTerms.Add(goTermId);
                    }
                    else
                    {
                        // If not, propagate to parent GO Slim terms
                        var parentSlimTerms = FindParentGoSlimTerms(goTermId, goSlimDatabase, goSlimTermIds);
                        foreach (var parentTerm in parentSlimTerms)
                        {
                            filteredGoTerms.Add(parentTerm);
                        }
                    }
                }

                // Only include proteins that have at least one GO Slim annotation
                if (filteredGoTerms.Count > 0)
                {
                    var proteinId = annotation.ProteinId;
                    compiledDatabase.ProteinToGoTerms[proteinId] = filteredGoTerms.ToList();

                    // Build reverse mapping (GO term -> proteins)
                    foreach (var goTermId in filteredGoTerms)
                    {
                        if (!compiledDatabase.GoTermToProteins.ContainsKey(goTermId))
                        {
                            compiledDatabase.GoTermToProteins[goTermId] = new List<string>();
                        }

                        if (!compiledDatabase.GoTermToProteins[goTermId].Contains(proteinId))
                        {
                            compiledDatabase.GoTermToProteins[goTermId].Add(proteinId);
                        }
                    }
                }

                processedCount++;

                // Update progress every 1000 proteins
                if (processedCount % 1000 == 0)
                {
                    var percentage = (processedCount * 100.0 / totalAnnotations);
                    progress?.ReportProgress($"Processed {processedCount:N0} / {totalAnnotations:N0} proteins ({percentage:F1}%)");
                }
            }

            progress?.ReportProgress($"Compilation complete: {compiledDatabase.TotalProteins:N0} proteins with GO annotations");

            return compiledDatabase;
        }

        private HashSet<string> FindParentGoSlimTerms(
            string goTermId,
            GoSlimDatabase goSlimDatabase,
            HashSet<string> goSlimTermIds)
        {
            var parentSlimTerms = new HashSet<string>();
            var visited = new HashSet<string>();
            var queue = new Queue<string>();

            queue.Enqueue(goTermId);

            while (queue.Count > 0)
            {
                var currentTermId = queue.Dequeue();

                if (visited.Contains(currentTermId))
                    continue;

                visited.Add(currentTermId);

                // If this term is in GO Slim, add it
                if (goSlimTermIds.Contains(currentTermId))
                {
                    parentSlimTerms.Add(currentTermId);
                    // Continue searching up the hierarchy for more GO Slim parents
                }

                // Check if we have this term in our GO Slim database
                if (goSlimDatabase.Terms.TryGetValue(currentTermId, out var goTerm))
                {
                    // Add all parent IDs to the queue
                    foreach (var parentId in goTerm.ParentIds)
                    {
                        if (!visited.Contains(parentId))
                        {
                            queue.Enqueue(parentId);
                        }
                    }
                }
            }

            return parentSlimTerms;
        }
    }
}