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
                "SELECT class_name, color FROM marker_classes ORDER BY class_name",
                r => new MarkerClass { Name = r.GetString(0), Color = r.IsDBNull(1) ? null : r.GetString(1) });
            var genes = await QueryAsync(
                "SELECT class_name, gene_name FROM marker_class_genes",
                r => (cls: r.GetString(0), gene: r.GetString(1)));
            var byClass = genes.GroupBy(g => g.cls, StringComparer.OrdinalIgnoreCase)
                               .ToDictionary(g => g.Key, g => g.Select(x => x.gene).ToList(), StringComparer.OrdinalIgnoreCase);
            foreach (var c in classes)
                if (byClass.TryGetValue(c.Name, out var list)) c.Genes = list;
            return classes;
        }

        public async Task SaveClassAsync(MarkerClass cls)
        {
            await new ProjectDatabaseService(_projectDbPath).EnsureMarkerClassesTablesExistAsync();
            await WithConnectionAsync(async conn =>
            {
                using var tx = conn.BeginTransaction();
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
                    ins.CommandText = "INSERT OR IGNORE INTO marker_class_genes (class_name, gene_name) VALUES (@n, @g)";
                    var pn = ins.Parameters.Add("@n", SqliteType.Text);
                    var pg = ins.Parameters.Add("@g", SqliteType.Text);
                    foreach (var g in cls.Genes.Select(x => x.Trim()).Where(x => x.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase))
                    {
                        pn.Value = cls.Name; pg.Value = g;
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
            int classCount = Math.Max(1, classes.Count);
            var sharedBy = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var cls in classes)
                foreach (var g in cls.Genes.Distinct(StringComparer.OrdinalIgnoreCase))
                    sharedBy[g] = sharedBy.GetValueOrDefault(g) + 1;
            double Weight(string g) => Math.Log(1.0 + (double)classCount / Math.Max(1, sharedBy.GetValueOrDefault(g, 1)));

            int floor = Math.Max(1, minMarkers);
            var result = new Dictionary<string, MarkerAssignment>(cellGenes.Count);
            foreach (var (cell, genes) in cellGenes)
            {
                MarkerClass? best = null;
                double bestScore = 0;
                int bestK = 0;
                foreach (var cls in classes)
                {
                    if (cls.Genes.Count == 0) continue;
                    var detected = cls.Genes.Where(genes.Contains).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                    int k = detected.Count;
                    if (k < floor) continue;
                    double score = detected.Sum(Weight) * ((double)k / cls.Genes.Count);
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

            progress?.Report("Loading protein annotations...");
            var protGene = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            await WithConnectionAsync(async conn =>
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT accession, gene_name FROM protein_annotations WHERE gene_name IS NOT NULL AND gene_name <> ''";
                using var r = await cmd.ExecuteReaderAsync();
                while (await r.ReadAsync()) protGene[r.GetString(0)] = r.GetString(1);
            });

            progress?.Report("Reading detected proteins per cell...");
            var cellGenes = new Dictionary<string, HashSet<string>>();
            await WithConnectionAsync(async conn =>
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"SELECT rf.raw_file_name, pqs.protein_id
                                    FROM protein_quant_summary pqs
                                    JOIN raw_files rf ON rf.raw_file_id = pqs.raw_file_id";
                using var r = await cmd.ExecuteReaderAsync();
                while (await r.ReadAsync())
                {
                    string cell = r.GetString(0), group = r.GetString(1);
                    if (!cellGenes.TryGetValue(cell, out var set)) { set = new HashSet<string>(StringComparer.OrdinalIgnoreCase); cellGenes[cell] = set; }
                    foreach (var g in GenesForGroup(group, protGene)) set.Add(g);
                }
            });

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
