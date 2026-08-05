using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using SCPBrowser.Models;

namespace SCPBrowser.Services
{
    /// <summary>One proposed (or applied) link between an isolated cell and a DIA-NN raw file.</summary>
    public class CellLinkSuggestion
    {
        public int CellId { get; set; }
        public int DropNo { get; set; }
        public string? TargetWell { get; set; }
        public int? RawFileId { get; set; }
        public string? RawFileName { get; set; }
        public double Confidence { get; set; }
        public string Method { get; set; } = "";
    }

    /// <summary>
    /// Reconciles isolated cells with DIA-NN raw files (the deferred linkage). The matchers are pure and static so
    /// they can be unit-tested headlessly; <see cref="ApplyLinksAsync"/> persists raw_file_id + link_method +
    /// link_confidence on isolated_cells (auditable, and fully overridable by the user from the UI).
    /// </summary>
    public class CellRunReconciliationService : DatabaseServiceBase
    {
        public CellRunReconciliationService(string projectDbPath) : base(projectDbPath)
        {
        }

        // ----------------------------------------------------------------- matchers (pure)

        /// <summary>
        /// Match by name: a raw file whose name contains the cell's drop number as a standalone token (high
        /// confidence), else one containing the normalized target well (lower confidence). Each raw file is used
        /// at most once (best cell wins). Cells with no candidate get a null suggestion.
        /// </summary>
        public static List<CellLinkSuggestion> SuggestByName(
            IReadOnlyList<IsolatedCell> cells, IReadOnlyList<RawFileRef> rawFiles, string? idTemplate = null)
        {
            // Pre-tokenize raw files once.
            var rfTokens = rawFiles.ToDictionary(
                rf => rf,
                rf => new HashSet<string>(Tokenize(rf.RawFileName ?? ""), StringComparer.OrdinalIgnoreCase));

            // Rank every (cell, rawFile) candidate, then greedily assign best-first so each raw file is used once.
            var candidates = new List<(IsolatedCell cell, RawFileRef rf, double conf)>();
            foreach (var cell in cells)
            {
                string dropTok = cell.DropNo.ToString();
                string well = NormalizeWell(cell.TargetWell);
                foreach (var rf in rawFiles)
                {
                    double c = 0;
                    if (rfTokens[rf].Contains(dropTok)) c = 0.9;
                    if (c == 0 && well.Length > 0)
                    {
                        string flat = Flatten(rf.RawFileName ?? "");
                        if (flat.Contains(well, StringComparison.OrdinalIgnoreCase)) c = 0.6;
                    }
                    if (c > 0) candidates.Add((cell, rf, c));
                }
            }

            var usedRaw = new HashSet<int>();
            var usedCell = new HashSet<int>();
            var byCell = new Dictionary<int, CellLinkSuggestion>();
            foreach (var (cell, rf, conf) in candidates.OrderByDescending(x => x.conf))
            {
                if (usedCell.Contains(cell.CellId) || usedRaw.Contains(rf.RawFileId)) continue;
                usedCell.Add(cell.CellId);
                usedRaw.Add(rf.RawFileId);
                byCell[cell.CellId] = Suggest(cell, rf, conf, "name");
            }

            // Emit one row per cell (unmatched cells get a null target).
            return cells.Select(c => byCell.TryGetValue(c.CellId, out var s) ? s : Suggest(c, null, 0, "name")).ToList();
        }

        /// <summary>
        /// Match by order: the i-th cell (by drop number) maps to the i-th raw file (by natural name order).
        /// Low confidence by construction - it assumes acquisition preserved isolation order.
        /// </summary>
        public static List<CellLinkSuggestion> SuggestByOrdinal(
            IReadOnlyList<IsolatedCell> cells, IReadOnlyList<RawFileRef> rawFiles)
        {
            var sortedCells = cells.OrderBy(c => c.DropNo).ToList();
            var sortedRaw = rawFiles.OrderBy(r => r.RawFileName ?? "", NaturalComparer.Instance).ToList();
            var result = new List<CellLinkSuggestion>(sortedCells.Count);
            for (int i = 0; i < sortedCells.Count; i++)
            {
                var rf = i < sortedRaw.Count ? sortedRaw[i] : null;
                result.Add(Suggest(sortedCells[i], rf, rf != null ? 0.5 : 0, "ordinal"));
            }
            return result;
        }

        private static CellLinkSuggestion Suggest(IsolatedCell cell, RawFileRef? rf, double conf, string method)
            => new CellLinkSuggestion
            {
                CellId = cell.CellId,
                DropNo = cell.DropNo,
                TargetWell = cell.TargetWell,
                RawFileId = rf?.RawFileId,
                RawFileName = rf?.RawFileName,
                Confidence = conf,
                Method = method
            };

        // ----------------------------------------------------------------- persistence

        /// <summary>
        /// Applies links in one transaction. Suggestions with a null RawFileId are skipped.
        /// When <paramref name="clearRunIdFirst"/> is given, that run's existing links are wiped inside the SAME
        /// transaction, so a failure part-way can never leave the run stripped of the links it started with -
        /// clearing and re-linking commit together or not at all.
        /// </summary>
        public async Task<int> ApplyLinksAsync(IEnumerable<CellLinkSuggestion> links, int? clearRunIdFirst = null)
        {
            return await WithConnectionAsync(async conn =>
            {
                using var tx = conn.BeginTransaction();
                int n = 0;
                try
                {
                    if (clearRunIdFirst != null)
                    {
                        using var clearCmd = conn.CreateCommand();
                        clearCmd.Transaction = tx;
                        clearCmd.CommandText = ClearLinksSql;
                        clearCmd.Parameters.AddWithValue("@r", clearRunIdFirst.Value);
                        await clearCmd.ExecuteNonQueryAsync();
                    }

                    foreach (var link in links)
                    {
                        if (link.RawFileId == null) continue;
                        using var cmd = conn.CreateCommand();
                        cmd.Transaction = tx;
                        cmd.CommandText = @"
                            UPDATE isolated_cells
                            SET raw_file_id = @rf, link_method = @m, link_confidence = @c
                            WHERE cell_id = @cell";
                        cmd.Parameters.AddWithValue("@rf", link.RawFileId);
                        cmd.Parameters.AddWithValue("@m", (object?)link.Method ?? DBNull.Value);
                        cmd.Parameters.AddWithValue("@c", link.Confidence);
                        cmd.Parameters.AddWithValue("@cell", link.CellId);
                        n += await cmd.ExecuteNonQueryAsync();
                    }
                    await tx.CommitAsync();
                    return n;
                }
                catch
                {
                    try { await tx.RollbackAsync(); } catch { }
                    throw;
                }
            });
        }

        private const string ClearLinksSql =
            "UPDATE isolated_cells SET raw_file_id = NULL, link_method = NULL, link_confidence = NULL WHERE cellenone_run_id = @r";

        /// <summary>Clears all links for a run (sets raw_file_id/link_method/link_confidence to NULL).</summary>
        public Task ClearLinksAsync(int cellenOneRunId)
        {
            return ExecuteNonQueryAsync(
                ClearLinksSql,
                cmd => cmd.Parameters.AddWithValue("@r", cellenOneRunId));
        }

        /// <summary>How many cells in a run currently carry a raw-file link. Used to state what a clear would destroy.</summary>
        public Task<int> CountLinkedCellsAsync(int cellenOneRunId)
        {
            return ExecuteScalarAsync<int>(
                "SELECT COUNT(*) FROM isolated_cells WHERE cellenone_run_id = @r AND raw_file_id IS NOT NULL",
                cmd => cmd.Parameters.AddWithValue("@r", cellenOneRunId));
        }

        // ----------------------------------------------------------------- helpers

        private static IEnumerable<string> Tokenize(string s)
            => Regex.Split(s, "[^A-Za-z0-9]+").Where(t => t.Length > 0);

        private static string Flatten(string s)
            => Regex.Replace(s, "[^A-Za-z0-9]", "");

        /// <summary>"A-1" / "A_1" / "A 1" -> "A1" for substring matching.</summary>
        private static string NormalizeWell(string? well)
            => string.IsNullOrEmpty(well) ? "" : Regex.Replace(well, "[^A-Za-z0-9]", "");

        /// <summary>Natural (numeric-aware) string comparer so File10 sorts after File2.</summary>
        private sealed class NaturalComparer : IComparer<string>
        {
            public static readonly NaturalComparer Instance = new();
            private static readonly Regex Chunk = new(@"\d+|\D+", RegexOptions.Compiled);

            public int Compare(string? a, string? b)
            {
                if (a == null) return b == null ? 0 : -1;
                if (b == null) return 1;
                var ax = Chunk.Matches(a);
                var bx = Chunk.Matches(b);
                int n = Math.Min(ax.Count, bx.Count);
                for (int i = 0; i < n; i++)
                {
                    string sa = ax[i].Value, sb = bx[i].Value;
                    int cmp;
                    if (char.IsDigit(sa[0]) && char.IsDigit(sb[0]))
                    {
                        // Compare as numbers (trim leading zeros via BigInteger-free length+lexical fallback).
                        sa = sa.TrimStart('0'); sb = sb.TrimStart('0');
                        cmp = sa.Length != sb.Length ? sa.Length - sb.Length : string.CompareOrdinal(sa, sb);
                    }
                    else
                    {
                        cmp = string.Compare(sa, sb, StringComparison.OrdinalIgnoreCase);
                    }
                    if (cmp != 0) return cmp;
                }
                return ax.Count - bx.Count;
            }
        }
    }
}
