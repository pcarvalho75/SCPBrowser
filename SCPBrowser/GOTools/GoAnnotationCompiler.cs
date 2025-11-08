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
            string goaGafPath)
        {
            // Load GO Slim database
            var goSlimParser = new GoSlimParser();
            var goSlimDatabase = await goSlimParser.ParseOboFileAsync(goSlimOboPath);

            // Load GOA annotations
            var goaParser = new GoAnnotationParser();
            var rawAnnotations = await goaParser.ParseGafFileAsync(goaGafPath);

            // Build set of GO Slim term IDs for fast lookup
            var goSlimTermIds = new HashSet<string>(goSlimDatabase.Terms.Keys);

            // Filter and compile annotations
            var compiledDatabase = new GoAnnotationDatabase();

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
            }

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