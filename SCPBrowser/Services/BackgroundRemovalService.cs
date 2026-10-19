using System;
using System.Collections.Generic;
using System.Linq;
using SCPBrowser.Models;

namespace SCPBrowser.Services
{
    /// <summary>One protein as seen in the selected blanks of a scope.</summary>
    public sealed class BlankProteinStats
    {
        /// <summary>Selected blanks of the scope that detect the protein.</summary>
        public int Detections { get; internal set; }

        /// <summary>Detections over the number of selected blanks of the scope.</summary>
        public double Fraction { get; internal set; }

        /// <summary>The blank level (max or median intensity over the detecting blanks); NaN when none has an intensity.</summary>
        public double Level { get; internal set; } = double.NaN;
    }

    /// <summary>
    /// A set of runs and the selected blanks that apply to them: one plate, or the whole project.
    /// </summary>
    public sealed class BackgroundScope
    {
        /// <summary>The plate, or null for the whole project.</summary>
        public int? PlateId { get; internal set; }

        public string Name { get; internal set; } = "";

        /// <summary>The selected blanks the background of this scope is estimated from.</summary>
        public List<string> SelectedBlanks { get; internal set; } = new();

        /// <summary>
        /// The runs of this scope use the project's selected blanks: a per-plate scope whose plate has no selected
        /// blank of its own, or runs without a plate.
        /// </summary>
        public bool UsesProjectBlanks { get; internal set; }

        /// <summary>Every protein found in at least one of the selected blanks.</summary>
        public Dictionary<string, BlankProteinStats> BlankProteins { get; internal set; } = new(StringComparer.Ordinal);

        /// <summary>The proteins the frequency rule removes from every run of the scope (empty when the rule is off).</summary>
        public HashSet<string> FrequentProteins { get; internal set; } = new(StringComparer.Ordinal);

        /// <summary>The runs this scope applies to.</summary>
        public List<string> Runs { get; internal set; } = new();
    }

    /// <summary>Which runs are blanks, which are selected, and the background of every run's scope.</summary>
    public sealed class BackgroundRemovalPlan
    {
        public NegativeControlSettings Settings { get; internal set; } = new();

        /// <summary>Every run of a negative-control condition in the data.</summary>
        public HashSet<string> BlankRuns { get; internal set; } = new(StringComparer.Ordinal);

        /// <summary>The blank runs not left out, in data order.</summary>
        public List<string> SelectedBlanks { get; internal set; } = new();

        public List<BackgroundScope> Scopes { get; internal set; } = new();

        /// <summary>The scope of every run of the data (empty when no blank is selected).</summary>
        public Dictionary<string, BackgroundScope> ScopeByRun { get; internal set; } = new(StringComparer.Ordinal);

        /// <summary>Runs whose plate has no selected blank and so use the project's selected blanks (per-plate scope only).</summary>
        public int RunsUsingProjectBlanks { get; internal set; }
    }

    /// <summary>What background removal does to one run.</summary>
    public sealed class RunRemovalSummary
    {
        public string Run { get; internal set; } = "";
        public int RawProteins { get; internal set; }
        public int KeptProteins { get; internal set; }
        public int RemovedByFrequency { get; internal set; }
        public int RemovedByIntensity { get; internal set; }
        public int RawPeptides { get; internal set; }
        public int KeptPeptides { get; internal set; }
        public bool IsBlank { get; internal set; }
    }

    /// <summary>The outcome of background removal over a whole dataset.</summary>
    public sealed class BackgroundRemovalResult
    {
        public BackgroundRemovalPlan Plan { get; internal set; } = new();

        /// <summary>Per run, the raw and kept protein and peptide counts (every run of the data, blanks included).</summary>
        public Dictionary<string, RunRemovalSummary> Runs { get; internal set; } = new(StringComparer.Ordinal);

        /// <summary>Protein measurements (protein in a run) removed by each rule, over the runs kept for analysis.</summary>
        public int MeasurementsRemovedByFrequency { get; internal set; }
        public int MeasurementsRemovedByIntensity { get; internal set; }
        public int MeasurementsTotal { get; internal set; }

        /// <summary>Proteins with a measurement in the analysed runs before removal and none after.</summary>
        public int ProteinsRemovedEverywhere { get; internal set; }

        /// <summary>Distinct proteins the frequency rule removes, over all scopes.</summary>
        public int FrequentProteins { get; internal set; }

        /// <summary>Blank runs left out of the analyses.</summary>
        public HashSet<string> ExcludedBlankRuns { get; internal set; } = new(StringComparer.Ordinal);

        /// <summary>
        /// The proteins removed from each analysed run (runs that lost none are absent), for views that read per-cell
        /// values from the project database instead of the loaded data. Filled by Apply only.
        /// </summary>
        public Dictionary<string, HashSet<string>> RemovedProteinsPerRun { get; internal set; } = new(StringComparer.Ordinal);

        /// <summary>False when the protein was removed from the run as background, or the run is a blank left out of the analyses.</summary>
        public bool Keeps(string run, string protein) =>
            !ExcludedBlankRuns.Contains(run)
            && !(RemovedProteinsPerRun.TryGetValue(run, out var removed) && removed.Contains(protein));

        /// <summary>The cleaned data; null when only the summary was asked for.</summary>
        public ProteomicsData? Cleaned { get; internal set; }

        /// <summary>One line for status bars and reports.</summary>
        public string Describe()
        {
            var s = Plan.Settings;
            var parts = new List<string>();
            if (s.RemovesBackground)
            {
                if (Plan.SelectedBlanks.Count == 0)
                    parts.Add("background removal on, but no blank run is selected, so nothing is removed");
                else
                {
                    var rules = new List<string>();
                    if (s.UseFrequency) rules.Add($"found in at least {s.MinFraction:P0} of blanks");
                    if (s.UseIntensity) rules.Add($"below {s.IntensityFactor:0.##}x the {(s.BlankLevel == BlankLevelStatistic.Max ? "max" : "median")} blank level");
                    parts.Add($"background removal on ({string.Join(" or ", rules)}; {Plan.SelectedBlanks.Count} blank run(s), " +
                              $"{(s.PerPlate ? "per plate" : "whole project")}): {ProteinsRemovedEverywhere} protein(s) removed from every run, " +
                              $"{MeasurementsRemovedByFrequency + MeasurementsRemovedByIntensity:N0} of {MeasurementsTotal:N0} measurements removed");
                }
            }
            if (ExcludedBlankRuns.Count > 0)
                parts.Add($"{ExcludedBlankRuns.Count} blank run(s) left out of the analyses");
            return string.Join("; ", parts);
        }
    }

    /// <summary>One row of the negative-control proteins table.</summary>
    public sealed class BlankProteinRow
    {
        public string Protein { get; internal set; } = "";
        public string Genes { get; internal set; } = "";
        public string Scope { get; internal set; } = "";
        public int BlankDetections { get; internal set; }
        public int SelectedBlanks { get; internal set; }
        public double BlankFraction { get; internal set; }
        public double BlankLevel { get; internal set; } = double.NaN;
        /// <summary>Non-blank runs of the scope that detect the protein.</summary>
        public int CellDetections { get; internal set; }
        public int CellRuns { get; internal set; }
        /// <summary>Median, over the non-blank runs that detect it, of the run intensity over the blank level.</summary>
        public double MedianCellToBlank { get; internal set; } = double.NaN;
        /// <summary>Non-blank runs where the protein is kept after removal.</summary>
        public int CellKept { get; internal set; }
        /// <summary>Removed (frequency), Partly removed (intensity), Kept, or Not applied.</summary>
        public string Status { get; internal set; } = "";
    }

    /// <summary>
    /// Negative-control background removal. From the selected blank runs it estimates, per scope (a plate or the whole
    /// project), which proteins are background, then builds a cleaned copy of the loaded data in which those proteins
    /// (and the peptides seen only under them) are absent from the runs of the scope. The data passed in is never
    /// modified. The preview and the applied result come from the same per-run decision, so they cannot disagree.
    /// </summary>
    public static class BackgroundRemovalService
    {
        /// <summary>
        /// Works out the blank runs, the selected blanks and the background of every scope.
        /// </summary>
        /// <param name="runToPlate">Plate of each run (runs without a plate use the project's blanks).</param>
        /// <param name="plateNames">Plate names for display; optional.</param>
        public static BackgroundRemovalPlan BuildPlan(ProteomicsData data, NegativeControlSettings settings,
            IReadOnlyDictionary<string, int>? runToPlate, IReadOnlyDictionary<int, string>? plateNames)
        {
            var plan = new BackgroundRemovalPlan { Settings = settings.Clone() };
            if (data == null || settings.Conditions.Count == 0)
                return plan;

            var leftOut = new HashSet<string>(settings.LeftOutRuns, StringComparer.Ordinal);
            foreach (var run in data.RawFileNames)
            {
                if (!data.BiologicalConditionPerFile.TryGetValue(run, out var condition) || !settings.IsNegativeControl(condition))
                    continue;
                plan.BlankRuns.Add(run);
                if (!leftOut.Contains(run))
                    plan.SelectedBlanks.Add(run);
            }

            if (!settings.RemovesBackground || plan.SelectedBlanks.Count == 0)
                return plan;

            int? PlateOf(string run) => runToPlate != null && runToPlate.TryGetValue(run, out var id) ? id : null;
            string PlateName(int id) => plateNames != null && plateNames.TryGetValue(id, out var n) && !string.IsNullOrEmpty(n) ? n : $"Plate {id}";

            BackgroundScope? project = null;
            BackgroundScope ProjectScope()
            {
                if (project != null) return project;
                project = new BackgroundScope { Name = "Whole project", SelectedBlanks = new List<string>(plan.SelectedBlanks), UsesProjectBlanks = settings.PerPlate };
                Estimate(project, data, settings);
                plan.Scopes.Add(project);
                return project;
            }

            if (!settings.PerPlate)
            {
                var scope = ProjectScope();
                scope.UsesProjectBlanks = false;
                foreach (var run in data.RawFileNames)
                {
                    plan.ScopeByRun[run] = scope;
                    scope.Runs.Add(run);
                }
                return plan;
            }

            var byPlate = new Dictionary<int, BackgroundScope>();
            foreach (var group in plan.SelectedBlanks.Where(b => PlateOf(b).HasValue).GroupBy(b => PlateOf(b)!.Value).OrderBy(g => g.Key))
            {
                var scope = new BackgroundScope { PlateId = group.Key, Name = PlateName(group.Key), SelectedBlanks = group.ToList() };
                Estimate(scope, data, settings);
                plan.Scopes.Add(scope);
                byPlate[group.Key] = scope;
            }

            foreach (var run in data.RawFileNames)
            {
                var plate = PlateOf(run);
                if (plate.HasValue && byPlate.TryGetValue(plate.Value, out var scope))
                {
                    plan.ScopeByRun[run] = scope;
                    scope.Runs.Add(run);
                }
                else
                {
                    var fallback = ProjectScope();
                    plan.ScopeByRun[run] = fallback;
                    fallback.Runs.Add(run);
                    plan.RunsUsingProjectBlanks++;
                }
            }
            return plan;
        }

        // Detections, fraction and blank level of every protein found in the scope's selected blanks.
        private static void Estimate(BackgroundScope scope, ProteomicsData data, NegativeControlSettings settings)
        {
            int n = scope.SelectedBlanks.Count;
            var intensities = new Dictionary<string, List<double>>(StringComparer.Ordinal);
            foreach (var blank in scope.SelectedBlanks)
            {
                if (!data.ProteinsPerFile.TryGetValue(blank, out var proteins))
                    continue;
                foreach (var protein in proteins)
                {
                    if (!scope.BlankProteins.TryGetValue(protein, out var stats))
                    {
                        scope.BlankProteins[protein] = stats = new BlankProteinStats();
                        intensities[protein] = new List<double>();
                    }
                    stats.Detections++;
                    if (TryIntensity(data, protein, blank, out double value))
                        intensities[protein].Add(value);
                }
            }

            foreach (var (protein, stats) in scope.BlankProteins)
            {
                stats.Fraction = n > 0 ? stats.Detections / (double)n : 0;
                var values = intensities[protein];
                if (values.Count > 0)
                    stats.Level = settings.BlankLevel == BlankLevelStatistic.Max ? values.Max() : Median(values);
                // A small tolerance so "50% of 4 blanks" keeps 2 of 4 whatever the rounding of 0.5 * 4.
                if (settings.UseFrequency && stats.Fraction >= settings.MinFraction - 1e-9)
                    scope.FrequentProteins.Add(protein);
            }
        }

        private static bool TryIntensity(ProteomicsData data, string protein, string run, out double value)
        {
            value = 0;
            return data.ProteinQuantMatrix.TryGetValue(protein, out var row) && row.TryGetValue(run, out value);
        }

        private static double Median(List<double> values)
        {
            var sorted = values.OrderBy(v => v).ToList();
            int m = sorted.Count / 2;
            return sorted.Count % 2 == 1 ? sorted[m] : (sorted[m - 1] + sorted[m]) / 2;
        }

        private enum Decision { Kept, Frequency, Intensity }

        // The one place a protein measurement is judged. Frequency first; the intensity rule then applies to the
        // proteins of the scope's blanks that are left: a measurement must reach k times the protein's blank level.
        // A protein with no blank intensity has no level to compare with and is kept by the intensity rule.
        private static Decision Decide(ProteomicsData data, BackgroundScope? scope, NegativeControlSettings settings, string run, string protein)
        {
            if (scope == null)
                return Decision.Kept;
            if (scope.FrequentProteins.Contains(protein))
                return Decision.Frequency;
            if (settings.UseIntensity && scope.BlankProteins.TryGetValue(protein, out var stats) && !double.IsNaN(stats.Level))
            {
                // A measurement without a quantity cannot be shown to clear the bar, so it is removed.
                if (!TryIntensity(data, protein, run, out double value) || value < settings.IntensityFactor * stats.Level)
                    return Decision.Intensity;
            }
            return Decision.Kept;
        }

        /// <summary>Per-run counts and totals of what the plan removes, without building the cleaned data.</summary>
        public static BackgroundRemovalResult Summarize(ProteomicsData data, BackgroundRemovalPlan plan) => Run(data, plan, buildCopy: false);

        /// <summary>The cleaned copy of <paramref name="data"/> and the summary of what was removed.</summary>
        public static BackgroundRemovalResult Apply(ProteomicsData data, BackgroundRemovalPlan plan) => Run(data, plan, buildCopy: true);

        private static BackgroundRemovalResult Run(ProteomicsData data, BackgroundRemovalPlan plan, bool buildCopy)
        {
            var settings = plan.Settings;
            var result = new BackgroundRemovalResult { Plan = plan };
            bool removing = settings.RemovesBackground && plan.SelectedBlanks.Count > 0;
            bool excludeBlanks = settings.Conditions.Count > 0 && settings.ExcludeBlankRunsFromAnalysis;

            var keptRuns = new List<string>(data.RawFileNames.Count);
            foreach (var run in data.RawFileNames)
            {
                if (excludeBlanks && plan.BlankRuns.Contains(run))
                    result.ExcludedBlankRuns.Add(run);
                else
                    keptRuns.Add(run);
            }

            var cleaned = buildCopy ? new ProteomicsData
            {
                IsGeneMatrix = data.IsGeneMatrix,
                ContaminantIds = new HashSet<string>(data.ContaminantIds, StringComparer.OrdinalIgnoreCase),
                RawFileNames = keptRuns
            } : null;

            // Proteins kept in each analysed run; also the basis of the per-protein matrix copy below.
            var keptByRun = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
            var analysedBefore = new HashSet<string>(StringComparer.Ordinal);
            var analysedAfter = new HashSet<string>(StringComparer.Ordinal);
            var analysedRuns = new HashSet<string>(keptRuns, StringComparer.Ordinal);

            foreach (var run in data.RawFileNames)
            {
                var proteins = data.ProteinsPerFile.TryGetValue(run, out var p) ? p : Array.Empty<string>();
                var peptides = data.PeptidesPerFile.TryGetValue(run, out var q) ? q : Array.Empty<string>();
                plan.ScopeByRun.TryGetValue(run, out var scope);
                if (!removing) scope = null;

                var summary = new RunRemovalSummary
                {
                    Run = run,
                    IsBlank = plan.BlankRuns.Contains(run),
                    RawProteins = data.ProteinCountPerFile.TryGetValue(run, out var pc) ? pc : proteins.Length,
                    RawPeptides = data.PeptideCountPerFile.TryGetValue(run, out var qc) ? qc : peptides.Length
                };

                var kept = new List<string>(proteins.Length);
                double removedQuant = 0;
                foreach (var protein in proteins)
                {
                    switch (Decide(data, scope, settings, run, protein))
                    {
                        case Decision.Frequency:
                            summary.RemovedByFrequency++;
                            if (TryIntensity(data, protein, run, out var f)) removedQuant += f;
                            break;
                        case Decision.Intensity:
                            summary.RemovedByIntensity++;
                            if (TryIntensity(data, protein, run, out var i)) removedQuant += i;
                            break;
                        default:
                            kept.Add(protein);
                            break;
                    }
                }
                summary.KeptProteins = kept.Count;

                bool anyRemoved = kept.Count != proteins.Length;
                string[] keptPeptides = peptides;
                HashSet<string>? keptSet = null;
                if (anyRemoved || analysedRuns.Contains(run))
                    keptSet = new HashSet<string>(kept, StringComparer.Ordinal);
                if (anyRemoved && peptides.Length > 0)
                {
                    // A peptide stays while at least one protein group it was identified under is kept in this run.
                    // A peptide with no group on record (a row without a protein group) is not anyone's background.
                    data.PeptideProteinGroupsPerFile.TryGetValue(run, out var groupsOf);
                    var list = new List<string>(peptides.Length);
                    foreach (var peptide in peptides)
                    {
                        if (groupsOf == null || !groupsOf.TryGetValue(peptide, out var groups))
                        {
                            list.Add(peptide);
                            continue;
                        }
                        foreach (var g in groups)
                        {
                            if (keptSet!.Contains(g)) { list.Add(peptide); break; }
                        }
                    }
                    keptPeptides = list.ToArray();
                }
                // A gene matrix has no peptides: its "peptide" count is the detected-gene count.
                summary.KeptPeptides = data.IsGeneMatrix ? kept.Count
                    : anyRemoved ? keptPeptides.Length : summary.RawPeptides;
                result.Runs[run] = summary;

                if (!analysedRuns.Contains(run))
                    continue;

                result.MeasurementsTotal += proteins.Length;
                result.MeasurementsRemovedByFrequency += summary.RemovedByFrequency;
                result.MeasurementsRemovedByIntensity += summary.RemovedByIntensity;
                analysedBefore.UnionWith(proteins);
                analysedAfter.UnionWith(kept);
                keptByRun[run] = keptSet!;

                if (cleaned == null)
                    continue;
                if (anyRemoved)
                    result.RemovedProteinsPerRun[run] = new HashSet<string>(proteins.Where(p => !keptSet!.Contains(p)), StringComparer.Ordinal);
                cleaned.ProteinsPerFile[run] = anyRemoved ? kept.ToArray() : proteins;
                cleaned.PeptidesPerFile[run] = keptPeptides;
                cleaned.ProteinCountPerFile[run] = anyRemoved ? kept.Count : summary.RawProteins;
                cleaned.PeptideCountPerFile[run] = summary.KeptPeptides;
                if (data.TotalIonCurrentPerFile.TryGetValue(run, out var tic))
                    cleaned.TotalIonCurrentPerFile[run] = anyRemoved ? Math.Max(0, tic - removedQuant) : tic;
                if (data.TargetProteinRatioPerFile.TryGetValue(run, out var ratio))
                    cleaned.TargetProteinRatioPerFile[run] = ratio;
                if (data.BiologicalConditionPerFile.TryGetValue(run, out var condition))
                    cleaned.BiologicalConditionPerFile[run] = condition;
                if (data.PeptideProteinGroupsPerFile.TryGetValue(run, out var map))
                    cleaned.PeptideProteinGroupsPerFile[run] = map;
                foreach (var peptide in keptPeptides)
                    cleaned.AllPeptideSequences.Add(peptide);
            }

            result.ProteinsRemovedEverywhere = analysedBefore.Count(p => !analysedAfter.Contains(p));
            result.FrequentProteins = plan.Scopes.SelectMany(s => s.FrequentProteins).Distinct(StringComparer.Ordinal).Count();

            if (cleaned != null)
            {
                // The matrix keeps the loader's protein order; a row holds the analysed runs where the protein is kept.
                // A protein kept in some run gets a row even if it has no quantity there, as the loader does.
                foreach (var (protein, row) in data.ProteinQuantMatrix)
                {
                    if (!analysedAfter.Contains(protein))
                        continue;
                    var newRow = new Dictionary<string, double>();
                    foreach (var (run, value) in row)
                    {
                        if (keptByRun.TryGetValue(run, out var keptSet) && keptSet.Contains(protein))
                            newRow[run] = value;
                    }
                    cleaned.ProteinQuantMatrix[protein] = newRow;
                }
                foreach (var (protein, genes) in data.ProteinToGeneMap)
                {
                    if (analysedAfter.Contains(protein))
                        cleaned.ProteinToGeneMap[protein] = genes;
                }
                cleaned.TotalRawFiles = cleaned.RawFileNames.Count;
                cleaned.TotalProteinGroups = cleaned.ProteinQuantMatrix.Count;
                cleaned.TotalPeptides = data.IsGeneMatrix ? cleaned.TotalProteinGroups : cleaned.AllPeptideSequences.Count;
                result.Cleaned = cleaned;
            }
            return result;
        }

        /// <summary>
        /// The negative-control proteins table: every protein found in a selected blank, per scope, with how often the
        /// blanks and the cells of the scope detect it, how strong the cells are against the blank level, and what the
        /// rules do to it.
        /// </summary>
        public static List<BlankProteinRow> DescribeBlankProteins(ProteomicsData data, BackgroundRemovalPlan plan)
        {
            var rows = new List<BlankProteinRow>();
            var settings = plan.Settings;
            foreach (var scope in plan.Scopes)
            {
                var cellRuns = scope.Runs.Where(r => !plan.BlankRuns.Contains(r)).ToList();
                var cellSets = cellRuns.Select(r => data.ProteinsPerFile.TryGetValue(r, out var p)
                    ? new HashSet<string>(p, StringComparer.Ordinal) : new HashSet<string>(StringComparer.Ordinal)).ToList();

                foreach (var (protein, stats) in scope.BlankProteins)
                {
                    var row = new BlankProteinRow
                    {
                        Protein = protein,
                        Genes = data.ProteinToGeneMap.TryGetValue(protein, out var g) ? g : "",
                        Scope = scope.Name + (scope.UsesProjectBlanks ? " (project blanks)" : ""),
                        BlankDetections = stats.Detections,
                        SelectedBlanks = scope.SelectedBlanks.Count,
                        BlankFraction = stats.Fraction,
                        BlankLevel = stats.Level,
                        CellRuns = cellRuns.Count
                    };
                    var ratios = new List<double>();
                    for (int i = 0; i < cellRuns.Count; i++)
                    {
                        if (!cellSets[i].Contains(protein))
                            continue;
                        row.CellDetections++;
                        if (!double.IsNaN(stats.Level) && stats.Level > 0 && TryIntensity(data, protein, cellRuns[i], out var v))
                            ratios.Add(v / stats.Level);
                        if (!settings.RemovesBackground || Decide(data, scope, settings, cellRuns[i], protein) == Decision.Kept)
                            row.CellKept++;
                    }
                    if (ratios.Count > 0)
                        row.MedianCellToBlank = Median(ratios);
                    row.Status = !settings.RemovesBackground ? "Not applied"
                        : scope.FrequentProteins.Contains(protein) ? "Removed"
                        : row.CellKept < row.CellDetections ? "Partly removed"
                        : "Kept";
                    rows.Add(row);
                }
            }
            return rows;
        }
    }
}
