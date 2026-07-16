using System;
using System.Collections.Generic;
using System.Linq;

namespace SCPBrowser.Services
{
    /// <summary>
    /// Builds a <see cref="ProteomicsData"/> from a gene × sample .xlsx matrix (e.g. a DIA-NN gene/pg matrix exported
    /// to Excel), so it can flow through the same analysis surface as a parquet import.
    ///
    /// A gene matrix has no peptide/precursor dimension, so:
    ///  - the quant matrix is keyed by GENE symbol (safe end-to-end: k-means/PCA/UMAP/HVP/contaminants are key-agnostic,
    ///    and marker classification matches gene keys directly);
    ///  - <c>PeptideCountPerFile</c> is SUBSTITUTED with the per-sample detected-gene count (= <c>ProteinCountPerFile</c>),
    ///    because that field is load-bearing for the Explorer (render gate + run set + X-axis);
    ///  - <c>TotalIonCurrentPerFile</c> is the per-sample summed intensity (a proxy — not comparable to a parquet TIC);
    ///  - the biological condition (cell type) is auto-derived from each sample column's parent folder in its path.
    /// </summary>
    public static class XlsxGeneMatrixParser
    {
        public static ProteomicsData Parse(string filePath)
        {
            var m = XlsxGeneMatrixReader.Read(filePath);
            int n = m.SampleHeaders.Count;

            // Derive a canonical run name + biological condition (cell type) per sample column, guaranteeing unique runs.
            var runNames = new string[n];
            var conditions = new string[n];
            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < n; i++)
            {
                var (run, cond) = DeriveRunAndCondition(m.SampleHeaders[i]);
                if (string.IsNullOrEmpty(run)) run = $"sample_{i + 1}";
                if (!used.Add(run))
                {
                    // Collision: qualify with the condition, then a counter, until unique.
                    string baseName = string.IsNullOrEmpty(cond) ? run : $"{cond}/{run}";
                    string candidate = baseName;
                    int k = 1;
                    while (!used.Add(candidate)) candidate = $"{baseName}#{k++}";
                    run = candidate;
                }
                runNames[i] = run;
                conditions[i] = cond;
            }

            var data = new ProteomicsData { IsGeneMatrix = true };

            // Initialize per-run aggregates for EVERY sample (even empty columns) so no run is missing downstream —
            // PeptideCountPerFile in particular must contain every run or the Explorer X-axis throws.
            for (int i = 0; i < n; i++)
            {
                string run = runNames[i];
                data.ProteinCountPerFile[run] = 0;
                data.TotalIonCurrentPerFile[run] = 0.0;
                if (!string.IsNullOrEmpty(conditions[i]))
                    data.BiologicalConditionPerFile[run] = conditions[i];
            }

            // Fill the quant matrix (keyed by gene) and per-run detected-count / summed-intensity.
            for (int g = 0; g < m.Genes.Count; g++)
            {
                var row = m.Rows[g];
                if (row.Count == 0) continue;
                string gene = m.Genes[g];

                Dictionary<string, double> perRun = null;
                foreach (var kv in row)
                {
                    int sampleIdx = kv.Key;
                    if (sampleIdx < 0 || sampleIdx >= n) continue;
                    double val = kv.Value;
                    string run = runNames[sampleIdx];

                    if (perRun == null && !data.ProteinQuantMatrix.TryGetValue(gene, out perRun))
                    {
                        perRun = new Dictionary<string, double>();
                        data.ProteinQuantMatrix[gene] = perRun;
                    }

                    // Count/sum only the first time a (gene, run) pair is seen, so a duplicate gene row can't inflate
                    // the per-run detected-gene count or TIC (the matrix keeps the last value either way).
                    if (perRun.TryAdd(run, val))
                    {
                        data.ProteinCountPerFile[run] += 1;
                        data.TotalIonCurrentPerFile[run] += val;
                    }
                    else
                    {
                        perRun[run] = val;
                    }
                }

                if (perRun != null)
                    data.ProteinToGeneMap[gene] = gene; // identity map keeps classification / converters happy
            }

            // Drop sample columns with zero detected genes — either leading annotation columns of a pg-style matrix
            // (their cells are text, not intensities) or genuinely empty samples; neither is a real run.
            var emptyRuns = runNames.Where(r => data.ProteinCountPerFile[r] == 0).ToHashSet();
            if (emptyRuns.Count > 0)
            {
                foreach (var r in emptyRuns)
                {
                    data.ProteinCountPerFile.Remove(r);
                    data.TotalIonCurrentPerFile.Remove(r);
                    data.BiologicalConditionPerFile.Remove(r);
                }
                runNames = runNames.Where(r => !emptyRuns.Contains(r)).ToArray();
            }

            // The substitution: "peptides" = detected genes (per run).
            foreach (var run in runNames)
                data.PeptideCountPerFile[run] = data.ProteinCountPerFile[run];

            data.RawFileNames = runNames.OrderBy(r => r, StringComparer.Ordinal).ToList();
            data.TotalRawFiles = runNames.Length;
            data.TotalProteinGroups = data.ProteinQuantMatrix.Count;
            data.TotalPeptides = data.TotalProteinGroups; // consistent with the peptide→protein substitution

            return data;
        }

        /// <summary>
        /// Splits a sample header into (run name, biological condition). For a path like
        /// <c>H:\YZC-SingleCell\A549\20230929_yzc_A549_1.raw.dia</c> the run is the leaf file name and the condition is
        /// the immediate parent folder ("A549"). A bare (non-path) header becomes the run with an empty condition.
        /// </summary>
        private static (string run, string condition) DeriveRunAndCondition(string header)
        {
            if (string.IsNullOrWhiteSpace(header)) return ("", "");
            var parts = header.Replace('/', '\\').Split('\\', StringSplitOptions.RemoveEmptyEntries);
            string run = parts.Length > 0 ? parts[^1].Trim() : header.Trim();
            string cond = parts.Length >= 2 ? parts[^2].Trim() : "";
            return (run, cond);
        }
    }
}
