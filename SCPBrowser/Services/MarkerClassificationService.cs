using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using SCPBrowser.Models;

namespace SCPBrowser.Services
{
    /// <summary>
    /// Marker-only cell classification: the user defines classes as a name + a set of marker genes (no expression
    /// profile required). Each cell is scored against each class and assigned its best match. Fully decoupled from
    /// the profile-based <see cref="CellTypePredictor"/>.
    /// </summary>
    public class MarkerClassificationService : DatabaseServiceBase
    {
        public const string Unassigned = "Unassigned";

        public MarkerClassificationService(string projectDbPath) : base(projectDbPath) { }

        // ------------------------------------------------------------------ CRUD

        public async Task<List<MarkerClass>> GetClassesAsync()
        {
            await new ProjectDatabaseService(_projectDbPath).EnsureMarkerClassesTablesExistAsync();
            var classes = await QueryAsync(
                "SELECT class_name, color, enabled FROM marker_classes ORDER BY class_name",
                r => new MarkerClass { Name = r.GetString(0), Color = r.IsDBNull(1) ? null : r.GetString(1), Enabled = r.GetInt32(2) != 0 });
            var genes = await QueryAsync(
                "SELECT class_name, gene_name, enabled FROM marker_class_genes",
                r => (cls: r.GetString(0), gene: r.GetString(1), enabled: r.GetInt32(2) != 0));
            var byClass = genes.GroupBy(g => g.cls, StringComparer.OrdinalIgnoreCase)
                               .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);
            foreach (var c in classes)
                if (byClass.TryGetValue(c.Name, out var list))
                {
                    c.Genes = list.Select(x => x.gene).ToList();
                    c.DisabledGenes = new HashSet<string>(list.Where(x => !x.enabled).Select(x => x.gene), StringComparer.OrdinalIgnoreCase);
                }
            return classes;
        }

        public async Task SaveClassAsync(MarkerClass cls)
        {
            await new ProjectDatabaseService(_projectDbPath).EnsureMarkerClassesTablesExistAsync();
            await WithConnectionAsync(async conn =>
            {
                using var tx = conn.BeginTransaction();

                // Preserve which markers were disabled, so editing the gene-list text doesn't silently re-enable them.
                var disabled = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                using (var sel = conn.CreateCommand())
                {
                    sel.Transaction = tx;
                    sel.CommandText = "SELECT gene_name FROM marker_class_genes WHERE class_name = @n AND enabled = 0";
                    sel.Parameters.AddWithValue("@n", cls.Name);
                    using var r = await sel.ExecuteReaderAsync();
                    while (await r.ReadAsync()) disabled.Add(r.GetString(0));
                }

                using (var up = conn.CreateCommand())
                {
                    up.Transaction = tx;
                    up.CommandText = "INSERT INTO marker_classes (class_name, color) VALUES (@n, @c) ON CONFLICT(class_name) DO UPDATE SET color = @c";
                    up.Parameters.AddWithValue("@n", cls.Name);
                    up.Parameters.AddWithValue("@c", (object?)cls.Color ?? DBNull.Value);
                    await up.ExecuteNonQueryAsync();
                }
                using (var del = conn.CreateCommand())
                {
                    del.Transaction = tx;
                    del.CommandText = "DELETE FROM marker_class_genes WHERE class_name = @n";
                    del.Parameters.AddWithValue("@n", cls.Name);
                    await del.ExecuteNonQueryAsync();
                }
                using (var ins = conn.CreateCommand())
                {
                    ins.Transaction = tx;
                    ins.CommandText = "INSERT OR IGNORE INTO marker_class_genes (class_name, gene_name, enabled) VALUES (@n, @g, @e)";
                    var pn = ins.Parameters.Add("@n", SqliteType.Text);
                    var pg = ins.Parameters.Add("@g", SqliteType.Text);
                    var pe = ins.Parameters.Add("@e", SqliteType.Integer);
                    foreach (var g in cls.Genes.Select(x => x.Trim()).Where(x => x.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase))
                    {
                        pn.Value = cls.Name; pg.Value = g; pe.Value = disabled.Contains(g) ? 0 : 1;
                        await ins.ExecuteNonQueryAsync();
                    }
                }
                await tx.CommitAsync();
            });
        }

        public Task DeleteClassAsync(string className) =>
            WithConnectionAsync(async conn =>
            {
                using var tx = conn.BeginTransaction();
                foreach (var sql in new[]
                {
                    "DELETE FROM marker_class_genes WHERE class_name = @n",
                    "DELETE FROM marker_classes WHERE class_name = @n"
                })
                {
                    using var cmd = conn.CreateCommand();
                    cmd.Transaction = tx; cmd.CommandText = sql;
                    cmd.Parameters.AddWithValue("@n", className);
                    await cmd.ExecuteNonQueryAsync();
                }
                await tx.CommitAsync();
            });

        public async Task<int> GetMinMarkersAsync()
        {
            var s = await new ProjectDatabaseService(_projectDbPath).GetSettingAsync("marker_min_markers", "2");
            return int.TryParse(s, out var v) && v > 0 ? v : 2;
        }

        public Task SetMinMarkersAsync(int n) =>
            new ProjectDatabaseService(_projectDbPath).SetSettingAsync("marker_min_markers", Math.Max(1, n).ToString());

        /// <summary>Enables/disables a whole class. A disabled class is skipped entirely during classification.</summary>
        public Task SetClassEnabledAsync(string className, bool enabled) =>
            ExecuteNonQueryAsync(
                "UPDATE marker_classes SET enabled = @e WHERE class_name = @c",
                cmd => { cmd.Parameters.AddWithValue("@e", enabled ? 1 : 0); cmd.Parameters.AddWithValue("@c", className); });

        /// <summary>Enables/disables a single marker within a class (per-class). Disabled markers are excluded from scoring but stay visible in diagnostics.</summary>
        public Task SetMarkerEnabledAsync(string className, string gene, bool enabled) =>
            ExecuteNonQueryAsync(
                "UPDATE marker_class_genes SET enabled = @e WHERE class_name = @c AND gene_name = @g",
                cmd =>
                {
                    cmd.Parameters.AddWithValue("@e", enabled ? 1 : 0);
                    cmd.Parameters.AddWithValue("@c", className);
                    cmd.Parameters.AddWithValue("@g", gene);
                });

        // ------------------------------------------------------------------ scorer (pure)

        /// <summary>
        /// Assigns each cell to its best marker class.
        /// Score(class) = (Σ specificity-weights of the class's markers detected in the cell) × (found / panel-size).
        /// The specificity weight of a gene is log(1 + #classes / #classes-containing-it) — markers unique to one
        /// class count far more than promiscuous ones. The ×found-fraction makes the score super-linear in the
        /// number of markers hit. A class is only eligible if at least <paramref name="minMarkers"/> of its genes
        /// are detected; otherwise the cell is <see cref="Unassigned"/>.
        /// </summary>
        public static Dictionary<string, MarkerAssignment> Classify(
            IReadOnlyDictionary<string, HashSet<string>> cellGenes,
            IReadOnlyList<MarkerClass> classes,
            int minMarkers)
        {
            // Disabled classes are skipped entirely; disabled markers are excluded from the panel and the shared-by
            // count, so disabling either fully removes its influence.
            var enabledClasses = classes.Where(c => c.Enabled).ToList();
            int classCount = Math.Max(1, enabledClasses.Count);
            var panels = enabledClasses
                .Select(c => (Cls: c, Panel: c.EnabledGenes.Distinct(StringComparer.OrdinalIgnoreCase).ToList()))
                .ToList();

            var sharedBy = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var (_, panel) in panels)
                foreach (var g in panel)
                    sharedBy[g] = sharedBy.GetValueOrDefault(g) + 1;
            double Weight(string g) => Math.Log(1.0 + (double)classCount / Math.Max(1, sharedBy.GetValueOrDefault(g, 1)));

            int floor = Math.Max(1, minMarkers);
            var result = new Dictionary<string, MarkerAssignment>(cellGenes.Count);
            foreach (var (cell, genes) in cellGenes)
            {
                MarkerClass? best = null;
                double bestScore = 0;
                int bestK = 0;
                foreach (var (cls, panel) in panels)
                {
                    if (panel.Count == 0) continue;
                    var detected = panel.Where(genes.Contains).ToList();
                    int k = detected.Count;
                    if (k < floor) continue;
                    double score = detected.Sum(Weight) * ((double)k / panel.Count);
                    if (score > bestScore) { bestScore = score; best = cls; bestK = k; }
                }
                result[cell] = new MarkerAssignment { ClassName = best?.Name ?? Unassigned, Score = bestScore, MarkersFound = bestK };
            }
            return result;
        }

        // ------------------------------------------------------------------ DB-driven classify + store

        /// <summary>
        /// Builds each cell's detected genes from protein_quant_summary + protein_annotations, runs the scorer, and
        /// stores the assignments via the existing classification table so the scatter can colour by them. Returns a
        /// class → cell-count summary.
        /// </summary>
        public async Task<Dictionary<string, int>> ClassifyAndStoreAsync(IProgress<string>? progress = null)
        {
            progress?.Report("Loading marker classes...");
            var classes = await GetClassesAsync();
            if (classes.Count == 0) return new Dictionary<string, int>();
            int minMarkers = await GetMinMarkersAsync();

            var levels = await BuildCellGeneLevelsAsync(progress);
            var cellGenes = HighExpressionGenes(levels, await GetExprTopPctAsync() / 100.0, await GetExprGatePctAsync() / 100.0);

            progress?.Report($"Classifying {cellGenes.Count} cells...");
            var assignments = Classify(cellGenes, classes, minMarkers);

            var predictions = new Dictionary<string, CellTypePredictionResult>();
            foreach (var kv in assignments)
            {
                var score = new CellTypeScore { CompositeScore = kv.Value.Score };
                predictions[kv.Key] = new CellTypePredictionResult
                {
                    TopCellType = kv.Value.ClassName,
                    TopScore = score,
                    Confidence = kv.Value.Score,
                    Scores = new Dictionary<string, CellTypeScore> { { kv.Value.ClassName, score } }
                };
            }

            int? importId = await new ParquetDataService(_projectDbPath).GetMostRecentImportIdAsync();
            if (importId.HasValue && predictions.Count > 0)
                await new CellTypeClassificationService(_projectDbPath).SaveCellTypeClassificationsAsync(importId.Value, predictions);

            progress?.Report("Done.");
            return assignments.Values.GroupBy(a => a.ClassName).ToDictionary(g => g.Key, g => g.Count());
        }

        /// <summary>Builds each cell's gene → max(median_intensity) map from protein_quant_summary + protein_annotations.</summary>
        private async Task<Dictionary<string, Dictionary<string, double>>> BuildCellGeneLevelsAsync(IProgress<string>? progress)
        {
            progress?.Report("Loading protein annotations...");
            var protGene = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            await WithConnectionAsync(async conn =>
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT accession, gene_name FROM protein_annotations WHERE gene_name IS NOT NULL AND gene_name <> ''";
                using var r = await cmd.ExecuteReaderAsync();
                while (await r.ReadAsync()) protGene[r.GetString(0)] = r.GetString(1);
            });

            // Honor user-flagged contaminants (the same protein_contaminants table the PCA/UMAP/profile analyses and
            // the PLP export use) so a flagged protein group is excluded from marker classification too.
            var pdb = new ProjectDatabaseService(_projectDbPath);
            await pdb.EnsureProteinContaminantsTableExistsAsync();
            var contaminants = new HashSet<string>(await pdb.LoadContaminantsAsync(), StringComparer.OrdinalIgnoreCase);

            progress?.Report("Reading protein intensities per cell...");
            var levels = new Dictionary<string, Dictionary<string, double>>(StringComparer.OrdinalIgnoreCase);
            await WithConnectionAsync(async conn =>
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"SELECT rf.raw_file_name, pqs.protein_id, pqs.median_intensity
                                    FROM protein_quant_summary pqs
                                    JOIN raw_files rf ON rf.raw_file_id = pqs.raw_file_id
                                    WHERE pqs.median_intensity IS NOT NULL";
                using var r = await cmd.ExecuteReaderAsync();
                while (await r.ReadAsync())
                {
                    string cell = r.GetString(0), group = r.GetString(1);
                    if (contaminants.Count > 0 && contaminants.Contains(group)) continue; // excluded: user-flagged contaminant
                    double v = r.GetDouble(2);
                    if (!levels.TryGetValue(cell, out var gl)) { gl = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase); levels[cell] = gl; }
                    foreach (var g in GenesForGroup(group, protGene))
                        if (!gl.TryGetValue(g, out var cur) || v > cur) gl[g] = v;
                }
            });
            return levels;
        }

        /// <summary>
        /// Per cell, the set of genes the cell expresses HIGH. Only markers detected in MORE than
        /// <paramref name="gatePrevalence"/> of cells (broadly-detected / "abundant") get the intensity cut — a cell
        /// must be in their top <paramref name="topFraction"/> of expressers to count them. Sparse / specific markers
        /// (detection ≤ gate) are trusted on detection alone, so a genuine low-abundance marker is never half-discarded
        /// by ranking. This is the expression-aware replacement for plain detection that tames ubiquitous proteins
        /// (e.g. C3) without harming low-abundance specific markers.
        /// </summary>
        public static Dictionary<string, HashSet<string>> HighExpressionGenes(
            Dictionary<string, Dictionary<string, double>> levels, double topFraction, double gatePrevalence)
        {
            topFraction = Math.Clamp(topFraction, 0.01, 1.0);
            int total = Math.Max(1, levels.Count);
            var byGene = new Dictionary<string, List<double>>(StringComparer.OrdinalIgnoreCase);
            foreach (var gl in levels.Values)
                foreach (var kv in gl)
                {
                    if (!byGene.TryGetValue(kv.Key, out var list)) { list = new List<double>(); byGene[kv.Key] = list; }
                    list.Add(kv.Value);
                }
            var thr = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in byGene)
            {
                if ((double)kv.Value.Count / total <= gatePrevalence)
                {
                    thr[kv.Key] = double.NegativeInfinity; // sparse / specific marker → trust detection, no rank-cut
                    continue;
                }
                var s = kv.Value; s.Sort();
                int idx = (int)Math.Floor((1.0 - topFraction) * (s.Count - 1));
                thr[kv.Key] = s[Math.Clamp(idx, 0, s.Count - 1)];
            }
            var result = new Dictionary<string, HashSet<string>>(levels.Count, StringComparer.OrdinalIgnoreCase);
            foreach (var (cell, gl) in levels)
            {
                var hi = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var kv in gl)
                    if (kv.Value >= thr[kv.Key]) hi.Add(kv.Key);
                result[cell] = hi;
            }
            return result;
        }

        /// <summary>Expression cutoff: a cell counts as "high" for a marker if it's in this top % of expressers. Default 50.</summary>
        public async Task<int> GetExprTopPctAsync()
        {
            var s = await new ProjectDatabaseService(_projectDbPath).GetSettingAsync("marker_expr_top_pct", "50");
            return int.TryParse(s, out var v) && v >= 1 && v <= 100 ? v : 50;
        }

        public Task SetExprTopPctAsync(int pct) =>
            new ProjectDatabaseService(_projectDbPath).SetSettingAsync("marker_expr_top_pct", Math.Clamp(pct, 1, 100).ToString());

        /// <summary>Prevalence gate: only markers detected in MORE than this % of cells get the intensity cut; sparser markers are trusted on detection. Default 50.</summary>
        public async Task<int> GetExprGatePctAsync()
        {
            var s = await new ProjectDatabaseService(_projectDbPath).GetSettingAsync("marker_expr_gate_pct", "50");
            return int.TryParse(s, out var v) && v >= 0 && v <= 100 ? v : 50;
        }

        public Task SetExprGatePctAsync(int pct) =>
            new ProjectDatabaseService(_projectDbPath).SetSettingAsync("marker_expr_gate_pct", Math.Clamp(pct, 0, 100).ToString());

        /// <summary>
        /// Computes diagnostics for the current classes + markers: per-class cell counts and, per marker, its
        /// prevalence across all cells, specificity weight, and how much it contributed to its class's assignments —
        /// so the user can see which marker is "taking over" and disable it. Does NOT save assignments.
        /// </summary>
        public async Task<MarkerClassDiagnostics> ComputeDiagnosticsAsync(IProgress<string>? progress = null)
        {
            var classes = await GetClassesAsync();
            var diag = new MarkerClassDiagnostics();
            if (classes.Count == 0) return diag;
            int minMarkers = await GetMinMarkersAsync();

            var levels = await BuildCellGeneLevelsAsync(progress);
            int total = levels.Count;
            diag.TotalCells = total;
            if (total == 0) return diag;

            var cellGenes = HighExpressionGenes(levels, await GetExprTopPctAsync() / 100.0, await GetExprGatePctAsync() / 100.0);
            progress?.Report("Classifying for diagnostics...");
            var assignments = Classify(cellGenes, classes, minMarkers);

            var counts = assignments.Values.GroupBy(a => a.ClassName).ToDictionary(g => g.Key, g => g.Count());
            diag.ClassCounts = counts.OrderByDescending(kv => kv.Value)
                .Select(kv => new ClassCount { ClassName = kv.Key, CellCount = kv.Value, Fraction = (double)kv.Value / total })
                .ToList();

            // Specificity weights over the enabled panels (same formula the scorer uses).
            int classCount = Math.Max(1, classes.Count(c => c.Enabled));
            var sharedBy = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var c in classes.Where(c => c.Enabled))
                foreach (var g in c.EnabledGenes.Distinct(StringComparer.OrdinalIgnoreCase))
                    sharedBy[g] = sharedBy.GetValueOrDefault(g) + 1;
            double Weight(string g) => Math.Log(1.0 + (double)classCount / Math.Max(1, sharedBy.GetValueOrDefault(g, 1)));

            // Detection (any intensity) from the levels; high-expression (what now drives scoring) + per-(assigned-class, gene) hits from cellGenes.
            var geneDet = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var gl in levels.Values)
                foreach (var g in gl.Keys)
                    geneDet[g] = geneDet.GetValueOrDefault(g) + 1;

            var geneHigh = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var assignedHit = new Dictionary<(string cls, string gene), int>();
            foreach (var (cell, genes) in cellGenes)
            {
                string asg = assignments[cell].ClassName;
                foreach (var g in genes)
                {
                    geneHigh[g] = geneHigh.GetValueOrDefault(g) + 1;
                    var key = (asg, g);
                    assignedHit[key] = assignedHit.GetValueOrDefault(key) + 1;
                }
            }

            foreach (var c in classes)
            {
                int classN = counts.GetValueOrDefault(c.Name, 0);
                foreach (var gene in c.Genes.Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    bool enabled = !c.DisabledGenes.Contains(gene);
                    int det = geneDet.GetValueOrDefault(gene, 0);
                    int high = geneHigh.GetValueOrDefault(gene, 0);
                    int hits = assignedHit.GetValueOrDefault((c.Name, gene), 0);
                    diag.Markers.Add(new MarkerStat
                    {
                        ClassName = c.Name,
                        Gene = gene,
                        Enabled = enabled,
                        DetectionCount = det,
                        DetectionFraction = (double)det / total,
                        HighCount = high,
                        HighFraction = (double)high / total,
                        Weight = enabled ? Weight(gene) : 0,
                        AssignedHits = hits,
                        AssignedFraction = (double)hits / total,
                        ContributionFraction = classN > 0 ? (double)hits / classN : 0
                    });
                }
            }
            diag.Markers = diag.Markers.OrderByDescending(m => m.DetectionFraction)
                                       .ThenBy(m => m.ClassName, StringComparer.OrdinalIgnoreCase).ToList();
            progress?.Report("Done.");
            return diag;
        }

        /// <summary>Maps a protein group ("P1;P2" or "RPL4_HUMAN") to gene symbols via annotations, falling back to id parsing.</summary>
        private static IEnumerable<string> GenesForGroup(string proteinGroup, Dictionary<string, string> protGene)
        {
            foreach (var part in proteinGroup.Split(';', ','))
            {
                var p = part.Trim();
                if (p.Length == 0) continue;
                if (protGene.TryGetValue(p, out var g)) yield return g;
                else foreach (var gg in GeneNameUtility.ExtractGeneNamesFromGroup(p)) yield return gg;
            }
        }
    }
}
