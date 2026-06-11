using System;
using System.Collections.Generic;
using System.Linq;

namespace SCPBrowser.Models
{
    /// <summary>A user-defined cell class identified purely by a set of marker genes (no expression profile).</summary>
    public class MarkerClass
    {
        public string Name { get; set; } = "";
        public string? Color { get; set; }

        /// <summary>Whether the whole class participates in classification. A disabled class is skipped entirely.</summary>
        public bool Enabled { get; set; } = true;

        public List<string> Genes { get; set; } = new();

        /// <summary>Markers the user has switched off for this class (per-class). Disabled markers are excluded from scoring but still shown in diagnostics.</summary>
        public HashSet<string> DisabledGenes { get; set; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>The genes actually used for scoring (Genes minus DisabledGenes).</summary>
        public IEnumerable<string> EnabledGenes => Genes.Where(g => !DisabledGenes.Contains(g));

        public override string ToString() => $"{Name} ({Genes.Count(g => !DisabledGenes.Contains(g))}/{Genes.Count} markers)";
    }

    /// <summary>The marker-based class a cell was assigned to, with its score and how many markers were found.</summary>
    public class MarkerAssignment
    {
        public string ClassName { get; set; } = "Unassigned";
        public double Score { get; set; }
        public int MarkersFound { get; set; }
    }

    /// <summary>Diagnostics for a marker-classification run: per-class cell counts + per-marker statistics.</summary>
    public class MarkerClassDiagnostics
    {
        public int TotalCells { get; set; }
        public List<ClassCount> ClassCounts { get; set; } = new();
        public List<MarkerStat> Markers { get; set; } = new();
    }

    /// <summary>Number of cells assigned to one class.</summary>
    public class ClassCount
    {
        public string ClassName { get; set; } = "";
        public int CellCount { get; set; }
        public double Fraction { get; set; }
        public double Pct => Fraction * 100;                  // for the bar's ProgressBar.Value
        public string CountLabel => $"{CellCount}  ({Fraction * 100:0.0}%)";
    }

    /// <summary>
    /// Per-marker diagnostic row. <see cref="DetectionFraction"/> (prevalence across all cells) is the key to spotting
    /// a marker that's "taking over" — an abundant protein detected nearly everywhere. <see cref="ContributionFraction"/>
    /// shows how much the marker actually drove its class's assignments.
    /// </summary>
    public class MarkerStat
    {
        public string ClassName { get; set; } = "";
        public string Gene { get; set; } = "";
        public bool Enabled { get; set; } = true;

        public int DetectionCount { get; set; }       // cells where this gene was detected (any intensity)
        public double DetectionFraction { get; set; }  // detection prevalence across ALL cells (0..1)
        public int HighCount { get; set; }             // cells where this gene was HIGHLY expressed (drives scoring)
        public double HighFraction { get; set; }       // high-expression prevalence across ALL cells (0..1)
        public double Weight { get; set; }              // specificity weight used in scoring (enabled markers only)
        public int AssignedHits { get; set; }           // cells assigned to ClassName where this gene was detected
        public double AssignedFraction { get; set; }    // AssignedHits / ALL cells (0..1) — "cells this marker drove"
        public double ContributionFraction { get; set; }// AssignedHits / cells-assigned-to-ClassName (0..1)

        // Convenience for binding / display.
        public double DetectionPct => DetectionFraction * 100;  // for the prevalence bar's ProgressBar.Value
        public double AssignedPct => AssignedFraction * 100;    // for the contribution bar's ProgressBar.Value
        public string AssignedLabel => $"{AssignedHits} cells";
        public string DetectionPercent => $"{DetectionFraction * 100:0.0}%";
        public string ContributionPercent => $"{ContributionFraction * 100:0.0}%";
        public string WeightText => Enabled ? $"{Weight:0.00}" : "—";
        public string Label => $"{Gene}  ({ClassName})";
        public bool IsHighPrevalence => DetectionFraction >= 0.80; // probably too abundant to be informative
    }
}
