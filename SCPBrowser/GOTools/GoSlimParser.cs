using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace SCPBrowser.GOTools
{
    public class GoSlimParser
    {
        public async Task<GoSlimDatabase> ParseOboFileAsync(string filePath)
        {
            if (!File.Exists(filePath))
                throw new FileNotFoundException("GO Slim OBO file not found", filePath);

            var database = new GoSlimDatabase();
            var lines = await File.ReadAllLinesAsync(filePath);

            GoTerm currentTerm = null;
            bool inTermBlock = false;
            var subsets = new HashSet<string>();

            foreach (var line in lines)
            {
                var trimmedLine = line.Trim();

                // Skip empty lines and comments
                if (string.IsNullOrWhiteSpace(trimmedLine) || trimmedLine.StartsWith("!"))
                    continue;

                // Start of a new term block
                if (trimmedLine == "[Term]")
                {
                    // Save previous term if it was in goslim_generic
                    if (currentTerm != null && subsets.Contains("goslim_generic"))
                    {
                        database.Terms[currentTerm.Id] = currentTerm;
                    }

                    currentTerm = new GoTerm();
                    subsets.Clear();
                    inTermBlock = true;
                    continue;
                }

                // Skip Typedef blocks
                if (trimmedLine == "[Typedef]")
                {
                    currentTerm = null;
                    inTermBlock = false;
                    continue;
                }

                // Parse term properties
                if (inTermBlock && currentTerm != null)
                {
                    if (trimmedLine.StartsWith("id:"))
                    {
                        currentTerm.Id = ExtractValue(trimmedLine, "id:");
                    }
                    else if (trimmedLine.StartsWith("name:"))
                    {
                        currentTerm.Name = ExtractValue(trimmedLine, "name:");
                    }
                    else if (trimmedLine.StartsWith("namespace:"))
                    {
                        currentTerm.Namespace = ExtractValue(trimmedLine, "namespace:");
                    }
                    else if (trimmedLine.StartsWith("def:"))
                    {
                        currentTerm.Definition = ExtractDefinition(trimmedLine);
                    }
                    else if (trimmedLine.StartsWith("subset:"))
                    {
                        var subset = ExtractValue(trimmedLine, "subset:");
                        subsets.Add(subset);

                        if (subset == "gocheck_do_not_annotate")
                        {
                            currentTerm.DoNotAnnotate = true;
                        }
                    }
                    else if (trimmedLine.StartsWith("is_a:"))
                    {
                        var parentId = ExtractGoId(trimmedLine);
                        if (!string.IsNullOrEmpty(parentId))
                        {
                            currentTerm.ParentIds.Add(parentId);
                        }
                    }
                    else if (trimmedLine.StartsWith("relationship:"))
                    {
                        var parentId = ExtractGoIdFromRelationship(trimmedLine);
                        if (!string.IsNullOrEmpty(parentId))
                        {
                            currentTerm.ParentIds.Add(parentId);
                        }
                    }
                    else if (trimmedLine.StartsWith("synonym:"))
                    {
                        var synonym = ExtractSynonym(trimmedLine);
                        if (!string.IsNullOrEmpty(synonym))
                        {
                            currentTerm.Synonyms.Add(synonym);
                        }
                    }
                }
            }

            // Don't forget the last term
            if (currentTerm != null && subsets.Contains("goslim_generic"))
            {
                database.Terms[currentTerm.Id] = currentTerm;
            }

            return database;
        }

        private string ExtractValue(string line, string prefix)
        {
            return line.Substring(prefix.Length).Trim();
        }

        private string ExtractDefinition(string line)
        {
            // def: "definition text" [references]
            // Extract text between first pair of quotes
            var startQuote = line.IndexOf('"');
            if (startQuote == -1)
                return string.Empty;

            var endQuote = line.IndexOf('"', startQuote + 1);
            if (endQuote == -1)
                return string.Empty;

            return line.Substring(startQuote + 1, endQuote - startQuote - 1);
        }

        private string ExtractGoId(string line)
        {
            // is_a: GO:0005694 ! chromosome
            // Extract GO:XXXXXXX
            var parts = line.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2)
            {
                var goId = parts[1];
                if (goId.StartsWith("GO:"))
                    return goId;
            }
            return null;
        }

        private string ExtractGoIdFromRelationship(string line)
        {
            // relationship: part_of GO:0005634 ! nucleus
            // Extract GO:XXXXXXX
            var parts = line.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var part in parts)
            {
                if (part.StartsWith("GO:"))
                    return part;
            }
            return null;
        }

        private string ExtractSynonym(string line)
        {
            // synonym: "text" EXACT []
            // Extract text between first pair of quotes
            var startQuote = line.IndexOf('"');
            if (startQuote == -1)
                return null;

            var endQuote = line.IndexOf('"', startQuote + 1);
            if (endQuote == -1)
                return null;

            return line.Substring(startQuote + 1, endQuote - startQuote - 1);
        }
    }
}