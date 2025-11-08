using System.Collections.Generic;

namespace SCPBrowser.GOTools
{
    public class ProteinGoAnnotation
    {
        public string ProteinId { get; set; }
        public string GeneName { get; set; }
        public List<string> GoTermIds { get; set; } = new List<string>();
    }

    public class GoAnnotationDatabase
    {
        // Protein ID -> GO terms
        public Dictionary<string, List<string>> ProteinToGoTerms { get; set; } = new Dictionary<string, List<string>>();

        // GO term -> Proteins
        public Dictionary<string, List<string>> GoTermToProteins { get; set; } = new Dictionary<string, List<string>>();

        public int TotalProteins => ProteinToGoTerms.Count;
        public int TotalAnnotations => ProteinToGoTerms.Values.Sum(list => list.Count);
    }
}