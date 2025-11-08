using System.Collections.Generic;
using System.Linq;

namespace SCPBrowser.GOTools
{
    public class GoTerm
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Namespace { get; set; } // biological_process, molecular_function, cellular_component
        public string Definition { get; set; }
        public List<string> ParentIds { get; set; } = new List<string>(); // from is_a and relationship lines
        public List<string> Synonyms { get; set; } = new List<string>();
        public bool DoNotAnnotate { get; set; } // if subset contains gocheck_do_not_annotate

        public bool IsBiologicalProcess => Namespace == "biological_process";
        public bool IsMolecularFunction => Namespace == "molecular_function";
        public bool IsCellularComponent => Namespace == "cellular_component";
    }

    public class GoSlimDatabase
    {
        public Dictionary<string, GoTerm> Terms { get; set; } = new Dictionary<string, GoTerm>();

        public int TotalTerms => Terms.Count;
        public int BiologicalProcessCount => Terms.Values.Count(t => t.IsBiologicalProcess);
        public int MolecularFunctionCount => Terms.Values.Count(t => t.IsMolecularFunction);
        public int CellularComponentCount => Terms.Values.Count(t => t.IsCellularComponent);
        public int AnnotatableTerms => Terms.Values.Count(t => !t.DoNotAnnotate);
    }
}