using System.Collections.Generic;

namespace SCPBrowser.Models
{
    /// <summary>A user-defined cell class identified purely by a set of marker genes (no expression profile).</summary>
    public class MarkerClass
    {
        public string Name { get; set; } = "";
        public string? Color { get; set; }
        public List<string> Genes { get; set; } = new();

        public override string ToString() => $"{Name} ({Genes.Count} markers)";
    }

    /// <summary>The marker-based class a cell was assigned to, with its score and how many markers were found.</summary>
    public class MarkerAssignment
    {
        public string ClassName { get; set; } = "Unassigned";
        public double Score { get; set; }
        public int MarkersFound { get; set; }
    }
}
