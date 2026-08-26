using System.Data;
using System.IO;
using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using Microsoft.Data.Sqlite;
using SCPBrowser;
using SCPBrowser.Models;
using SCPBrowser.Services;

namespace HeadlessEmbed;

/// <summary>
/// Generates the embedding through SCPBrowser's own code path, headlessly.
///
/// The pipeline (BuildPreprocessedMatrix, ComputePca, ComputeUmap) lives as private methods on
/// ScatterPlotControl, a WPF UserControl. Rather than copy that maths into the harness - which would let the
/// harness and the application silently diverge, exactly the failure this is meant to rule out - the control is
/// allocated without running its constructor and the real methods are invoked by reflection. They operate on
/// the passed-in data and a handful of fields, and never touch the visual tree.
///
/// Usage: HeadlessEmbed &lt;projectDir&gt; &lt;outCsv&gt; [--no-batch-correction]
/// </summary>
internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            string projectDir = args.Length > 0 ? args[0] : @"C:\Users\paulo\Desktop\SpaceProjectV3";
            string outCsv = args.Length > 1 ? args[1] : Path.Combine(projectDir, "embedding.csv");
            bool batchCorrect = !args.Contains("--no-batch-correction");

            string dbPath = Path.Combine(projectDir, "project.db");
            if (!File.Exists(dbPath)) throw new FileNotFoundException("project.db not found", dbPath);

            Console.WriteLine($"project        : {projectDir}");
            Console.WriteLine($"batch correct  : {batchCorrect}");

            // ---- settings, exactly as the application persists them -------------------------------------
            var settings = LoadSettings(dbPath);
            settings.ApplyBatchCorrection = batchCorrect;

            // Command-line overrides, so parameters can be swept without disturbing the saved project.
            settings.SmoothingNeighbors = OptInt(args, "--smooth", settings.SmoothingNeighbors);
            settings.SmoothingSteps = OptInt(args, "--smooth-steps", settings.SmoothingSteps);
            settings.UmapNeighbors = OptInt(args, "--nn", settings.UmapNeighbors);
            settings.NumPcaComponents = OptInt(args, "--pca", settings.NumPcaComponents);
            settings.NumPcsForUmap = Math.Min(OptInt(args, "--pcs", settings.NumPcsForUmap), settings.NumPcaComponents);
            settings.MinDetectionRate = OptInt(args, "--min-detect", (int)Math.Round(settings.MinDetectionRate * 100)) / 100.0;

            if (args.Contains("--probe-umap")) { ProbeUmap(); return 0; }
            Console.WriteLine($"settings       : minDetect={settings.MinDetectionRate:P0} norm={settings.Normalization} " +
                              $"missing={settings.MissingValues} regressDepth={settings.RegressDepth} " +
                              $"smooth={settings.SmoothingNeighbors}x{settings.SmoothingSteps} " +
                              $"pca={settings.NumPcaComponents} pcs4umap={settings.NumPcsForUmap} " +
                              $"nn={settings.UmapNeighbors} seed={settings.UmapSeed} clip={settings.ClipMaxValue} " +
                              $"hvp={settings.UseHvpFilter}");

            // ---- data, through the application's own parquet reader --------------------------------------
            var parquetFiles = Directory.GetFiles(Path.Combine(projectDir, "imports"), "*.parquet")
                                        .OrderBy(f => f).ToList();
            if (parquetFiles.Count == 0) throw new InvalidOperationException("no parquet files under imports/");
            Console.WriteLine($"parquet        : {string.Join(", ", parquetFiles.Select(Path.GetFileName))}");

            var mapping = new ColumnMapping
            {
                RawFileColumn = "Run",
                ProteinGroupColumn = "Protein.Group",
                PeptideColumn = ColumnMapping.DefaultPeptideColumn,
                TotalIonCurrentColumn = ColumnMapping.DefaultQuantityColumn,
                TargetProteinIdentifiers = new List<string>()
            };

            var svc = new ParquetDataService(dbPath);
            var data = svc.LoadMultipleParquetFilesAsync(parquetFiles, mapping).GetAwaiter().GetResult();
            Console.WriteLine($"loaded         : {data.RawFileNames.Count} runs, {data.ProteinQuantMatrix.Count} protein groups");

            // ---- cohort: the same filters the Explorer applies ------------------------------------------
            var meta = LoadRunMetadata(dbPath);
            int cutoff = ReadIntSetting(dbPath, "ProteinCutoff", 0);
            int upper = ReadIntSetting(dbPath, "UpperProteinCutoff", int.MaxValue);
            var conditions = ReadCsvSetting(dbPath, "CheckedBioConditions");
            var plates = ReadCsvSetting(dbPath, "CheckedPlates");

            // The cutoff must be applied to the SAME protein count the application uses. DataFilterService
            // filters on data.ProteinCountPerFile, the count derived from the parquet during load, NOT on
            // raw_files.protein_count in the database. The two disagree (the parquet-derived count is lower),
            // and using the database column here silently produced a 222-cell cohort against the application's
            // 220 - a different cohort, hence a different embedding.
            var cohort = data.RawFileNames.Where(rf =>
            {
                if (!meta.TryGetValue(rf, out var m)) return false;
                int pc = data.ProteinCountPerFile.TryGetValue(rf, out int v) ? v : 0;
                if (pc < cutoff || pc > upper) return false;
                if (conditions.Count > 0 && !conditions.Contains(m.Condition)) return false;
                if (plates.Count > 0 && (m.Plate == null || !plates.Contains(m.Plate))) return false;
                return true;
            }).OrderBy(x => x, StringComparer.Ordinal).ToList();
            Console.WriteLine($"cohort         : {cohort.Count} cells " +
                              $"(cutoff {cutoff}-{(upper == int.MaxValue ? "inf" : upper.ToString())}, " +
                              $"conditions [{string.Join("|", conditions)}], plates [{string.Join("|", plates)}])");

            var trimmed = Trim(data, cohort);

            // ---- options, mirroring what the Explorer hands the plot -------------------------------------
            var options = new ScatterPlotOptions
            {
                DimRedSettings = settings,
                UseUmapView = true,
                ApplyBatchCorrection = batchCorrect,
                BatchLabelPerFile = cohort.ToDictionary(rf => rf, rf => meta[rf].PlateId),
                BioConditionPerFile = cohort.ToDictionary(rf => rf, rf => meta[rf].Condition),
                HvpResults = null,
                ContaminantRatioCutoff = 1.0,
                AlwaysKeepProteins = args.Contains("--keep-markers") ? MarkerProteins(dbPath, data) : null
            };
            if (args.Contains("--keep-markers"))
                Console.WriteLine($"marker rescue  : {options.AlwaysKeepProteins.Count} marker protein groups protected");

            // ---- run the application's real pipeline -----------------------------------------------------
            var control = (ScatterPlotControl)RuntimeHelpers.GetUninitializedObject(typeof(ScatterPlotControl));
            SetField(control, "_currentOptions", options);
            SetField(control, "_hideUnselected", false);

            Invoke(control, "ComputePca", trimmed);

            // Sweep mode: the parquet load and the PCA are the expensive parts and do not depend on the UMAP
            // parameters, so do them once and fit many embeddings from the same scores.
            if (args.Contains("--uwot-sweep"))
            {
                var pcaS = GetField(control, "_pcaResult");
                var scoresS = (double[,])pcaS.GetType().GetProperty("Scores").GetValue(pcaS);
                int nS = scoresS.GetLength(0);
                int dimS = Math.Min(settings.NumPcsForUmap, scoresS.GetLength(1));
                var inputS = new float[nS, dimS];
                for (int i = 0; i < nS; i++)
                    for (int j = 0; j < dimS; j++) inputS[i, j] = (float)scoresS[i, j];

                string dir = Path.GetDirectoryName(outCsv);
                Directory.CreateDirectory(dir);
                float[] mds = { 0.1f, 0.25f, 0.4f, 0.6f, 0.8f };
                float[] spreads = { 1.0f, 1.5f, 2.5f };
                int[] nns = { 10, 15, 30, 50 };
                int done = 0;
                foreach (var md in mds)
                    foreach (var sp in spreads)
                        foreach (var nn in nns)
                        {
                            if (md >= sp) continue;   // uwot requires min_dist < spread
                            using var mdl = new UMAPuwotSharp.UMapModel();
                            var e2 = mdl.Fit(inputS, embeddingDimension: 2, nNeighbors: nn,
                                             minDist: md, spread: sp, nEpochs: 500,
                                             metric: UMAPuwotSharp.DistanceMetric.Euclidean,
                                             randomSeed: settings.UmapSeed);
                            string fn = Path.Combine(dir, $"sw_md{md}_sp{sp}_nn{nn}.csv"
                                .Replace(",", "."));
                            using var sw = new StreamWriter(fn);
                            sw.WriteLine("run,condition,plate,cell_type,protein_count,peptide_count,umap1,umap2");
                            for (int i = 0; i < cohort.Count; i++)
                            {
                                var rfn = cohort[i]; var mm = meta[rfn];
                                sw.WriteLine(string.Join(",", Csv(rfn), Csv(mm.Condition), Csv(mm.Plate),
                                    Csv(mm.CellType),
                                    (data.ProteinCountPerFile.TryGetValue(rfn, out int a) ? a : 0).ToString(CultureInfo.InvariantCulture),
                                    (data.PeptideCountPerFile.TryGetValue(rfn, out int b) ? b : 0).ToString(CultureInfo.InvariantCulture),
                                    e2[i, 0].ToString("R", CultureInfo.InvariantCulture),
                                    e2[i, 1].ToString("R", CultureInfo.InvariantCulture)));
                            }
                            done++;
                        }
                Console.WriteLine($"sweep          : wrote {done} embeddings to {dir}");
                return 0;
            }

            float[][] umap;
            if (args.Contains("--uwot"))
            {
                // Same input as the built-in path (SCPBrowser's own preprocessing and NIPALS scores), but the
                // embedding is produced by UMAPuwotSharp, which exposes minDist/spread. The bundled UMAP package
                // hardcodes those, which is why its layout cannot be tuned.
                var pca = GetField(control, "_pcaResult");
                var scores = (double[,])pca.GetType().GetProperty("Scores").GetValue(pca);
                int n = scores.GetLength(0);
                int dims = Math.Min(settings.NumPcsForUmap, scores.GetLength(1));
                var input = new float[n, dims];
                for (int i = 0; i < n; i++)
                    for (int j = 0; j < dims; j++) input[i, j] = (float)scores[i, j];

                float minDist = OptFloat(args, "--min-dist", 0.1f);
                float spread = OptFloat(args, "--spread", 1.0f);
                int epochs = OptInt(args, "--epochs", 500);
                Console.WriteLine($"uwot embed     : {n}x{dims} PCs, nNeighbors={settings.UmapNeighbors}, " +
                                  $"minDist={minDist}, spread={spread}, epochs={epochs}, seed={settings.UmapSeed}");

                using var model = new UMAPuwotSharp.UMapModel();
                var emb = model.Fit(input, embeddingDimension: 2, nNeighbors: settings.UmapNeighbors,
                                    minDist: minDist, spread: spread, nEpochs: epochs,
                                    metric: UMAPuwotSharp.DistanceMetric.Euclidean,
                                    randomSeed: settings.UmapSeed);
                umap = new float[n][];
                for (int i = 0; i < n; i++) umap[i] = new[] { emb[i, 0], emb[i, 1] };
            }
            else
            {
                Invoke(control, "ComputeUmap", trimmed);
                umap = (float[][])GetField(control, "_umapResult");
            }

            var usedRuns = (List<string>)GetField(control, "_dimRedRawFiles");
            var proteinNames = (List<string>)GetField(control, "_pcaProteinNames");

            if (umap == null)
                throw new InvalidOperationException("UMAP returned null - cohort too small or PCA failed.");

            Console.WriteLine();
            Console.WriteLine("--- diagnostics reported by the application itself ---");
            Console.WriteLine($"proteins used         : {proteinNames?.Count ?? 0}");
            Console.WriteLine($"missing rate          : {Get<double>(control, "LastMissingRate"):P1}");
            Console.WriteLine($"dropped below floor   : {Get<int>(control, "CoverageFloorDropped")}");
            Console.WriteLine($"markers rescued       : {Get<int>(control, "MarkersRescued")}");
            Console.WriteLine($"depth regressed       : {Get<bool>(control, "DepthRegressed")}");
            Console.WriteLine($"kNN smoothing k       : {Get<int>(control, "SmoothingApplied")}");
            Console.WriteLine($"PC vs depth |rho|     : {Get<double>(control, "DepthPcCorrelation"):F3}");
            var floorWarn = Get<string>(control, "CoverageFloorWarning");
            if (!string.IsNullOrEmpty(floorWarn)) Console.WriteLine($"floor warning         : {floorWarn}");
            var bcWarn = Get<string>(control, "BatchCorrectionWarning");
            if (!string.IsNullOrEmpty(bcWarn)) Console.WriteLine($"batch warning         : {bcWarn}");

            // ---- write coordinates -----------------------------------------------------------------------
            using var w = new StreamWriter(outCsv);
            // Protein and peptide counts are the parquet-derived ones the application filters and displays,
            // NOT raw_files.protein_count, so any table built from this file matches the Explorer grid.
            w.WriteLine("run,condition,plate,cell_type,protein_count,peptide_count,umap1,umap2");
            for (int i = 0; i < usedRuns.Count; i++)
            {
                var rf = usedRuns[i];
                var m = meta[rf];
                w.WriteLine(string.Join(",",
                    Csv(rf), Csv(m.Condition), Csv(m.Plate), Csv(m.CellType),
                    (data.ProteinCountPerFile.TryGetValue(rf, out int pcOut) ? pcOut : 0).ToString(CultureInfo.InvariantCulture),
                    (data.PeptideCountPerFile.TryGetValue(rf, out int peOut) ? peOut : 0).ToString(CultureInfo.InvariantCulture),
                    umap[i][0].ToString("R", CultureInfo.InvariantCulture),
                    umap[i][1].ToString("R", CultureInfo.InvariantCulture)));
            }
            Console.WriteLine($"\nwrote {usedRuns.Count} rows to {outCsv}");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("FAILED: " + ex);
            return 1;
        }
    }

    // ---------------- helpers ----------------

    /// <summary>Reports what the UMAP package actually exposes, to settle which knobs exist at all.</summary>
    private static void ProbeUmap()
    {
        if (Environment.GetCommandLineArgs().Contains("--uwot"))
        {
            var ua = System.Reflection.Assembly.Load("UMAPuwotSharp");
            Console.WriteLine("UMAPuwotSharp: " + ua.FullName);
            foreach (var ty in ua.GetExportedTypes())
            {
                Console.WriteLine("TYPE " + ty.FullName);
                foreach (var m in ty.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
                    Console.WriteLine("   " + m.ReturnType.Name + " " + m.Name + "(" + string.Join(", ",
                        m.GetParameters().Select(pp => pp.ParameterType.Name + " " + pp.Name +
                            (pp.HasDefaultValue ? "=" + (pp.DefaultValue ?? "null") : ""))) + ")");
                foreach (var pr in ty.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                    Console.WriteLine("   prop " + pr.PropertyType.Name + " " + pr.Name);
            }
            return;
        }
        var asm = typeof(UMAP.Umap).Assembly;
        Console.WriteLine("UMAP assembly: " + asm.FullName);
        foreach (var ty in asm.GetExportedTypes())
        {
            Console.WriteLine("TYPE " + ty.FullName);
            foreach (var c in ty.GetConstructors())
                Console.WriteLine("   ctor(" + string.Join(", ",
                    c.GetParameters().Select(pp => pp.ParameterType.Name + " " + pp.Name +
                        (pp.HasDefaultValue ? " = " + (pp.DefaultValue ?? "null") : ""))) + ")");
            foreach (var m in ty.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                Console.WriteLine("   " + m.ReturnType.Name + " " + m.Name + "(" + string.Join(", ",
                    m.GetParameters().Select(pp => pp.ParameterType.Name + " " + pp.Name)) + ")");
        }
    }

    /// <summary>
    /// Protein groups whose gene symbol appears in the project key-marker table. These are the proteins a
    /// completeness floor is most likely to delete and least able to spare.
    /// </summary>
    private static HashSet<string> MarkerProteins(string dbPath, ProteomicsData data)
    {
        var genes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (var c = new SqliteConnection($"Data Source={dbPath}"))
        {
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "SELECT DISTINCT UPPER(gene_name) FROM cell_type_key_markers";
            using var r = cmd.ExecuteReader();
            while (r.Read()) if (!r.IsDBNull(0)) genes.Add(r.GetString(0));
        }
        var keep = new HashSet<string>(StringComparer.Ordinal);
        foreach (var pg in data.ProteinQuantMatrix.Keys)
        {
            if (!data.ProteinToGeneMap.TryGetValue(pg, out var gs) || string.IsNullOrEmpty(gs)) continue;
            foreach (var g in gs.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                if (genes.Contains(g)) { keep.Add(pg); break; }
        }
        return keep;
    }

    private static float OptFloat(string[] args, string flag, float fallback)
    {
        int i = Array.IndexOf(args, flag);
        return (i >= 0 && i + 1 < args.Length &&
                float.TryParse(args[i + 1], NumberStyles.Float, CultureInfo.InvariantCulture, out float v))
            ? v : fallback;
    }

    private static int OptInt(string[] args, string flag, int fallback)
    {
        int i = Array.IndexOf(args, flag);
        return (i >= 0 && i + 1 < args.Length && int.TryParse(args[i + 1], out int v)) ? v : fallback;
    }

    private sealed record RunMeta(string Condition, string Plate, int PlateId, string CellType, int ProteinCount);

    private static Dictionary<string, RunMeta> LoadRunMetadata(string dbPath)
    {
        var d = new Dictionary<string, RunMeta>(StringComparer.Ordinal);
        using var c = new SqliteConnection($"Data Source={dbPath}");
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText =
            "SELECT rf.raw_file_name, rf.biological_condition, p.plate_name, COALESCE(rf.plate_id,0), " +
            "       COALESCE(cl.predicted_cell_type,''), COALESCE(rf.protein_count,0) " +
            "FROM raw_files rf " +
            "LEFT JOIN plates p ON p.plate_id = rf.plate_id " +
            "LEFT JOIN raw_file_cell_type_classifications cl ON cl.raw_file_id = rf.raw_file_id";
        using var r = cmd.ExecuteReader();
        while (r.Read())
            d[r.GetString(0)] = new RunMeta(
                r.IsDBNull(1) ? "" : r.GetString(1),
                r.IsDBNull(2) ? null : r.GetString(2),
                r.GetInt32(3), r.GetString(4), r.GetInt32(5));
        return d;
    }

    private static DimensionReductionSettings LoadSettings(string dbPath)
    {
        var db = new ProjectDatabaseService(dbPath);
        return DimensionReductionSettings.LoadAsync(db).GetAwaiter().GetResult();
    }

    private static string ReadSetting(string dbPath, string key)
    {
        using var c = new SqliteConnection($"Data Source={dbPath}");
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT setting_value FROM project_settings WHERE setting_key=$k";
        cmd.Parameters.AddWithValue("$k", key);
        return cmd.ExecuteScalar() as string;
    }

    private static int ReadIntSetting(string dbPath, string key, int fallback) =>
        int.TryParse(ReadSetting(dbPath, key), NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) ? v : fallback;

    private static HashSet<string> ReadCsvSetting(string dbPath, string key)
    {
        var raw = ReadSetting(dbPath, key);
        return string.IsNullOrWhiteSpace(raw)
            ? new HashSet<string>(StringComparer.Ordinal)
            : raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                 .Select(Uri.UnescapeDataString).ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>Restricts the dataset to one cohort without disturbing anything else about it.</summary>
    private static ProteomicsData Trim(ProteomicsData src, List<string> keep)
    {
        var set = keep.ToHashSet(StringComparer.Ordinal);
        var d = new ProteomicsData
        {
            RawFileNames = keep.ToList(),
            TotalRawFiles = keep.Count,
            ProteinToGeneMap = src.ProteinToGeneMap,
            ContaminantIds = src.ContaminantIds,
            AllPeptideSequences = src.AllPeptideSequences,
            IsGeneMatrix = src.IsGeneMatrix
        };
        foreach (var kv in src.ProteinQuantMatrix)
        {
            var inner = kv.Value.Where(x => set.Contains(x.Key)).ToDictionary(x => x.Key, x => x.Value);
            if (inner.Count > 0) d.ProteinQuantMatrix[kv.Key] = inner;
        }
        foreach (var rf in keep)
        {
            if (src.ProteinCountPerFile.TryGetValue(rf, out int pc)) d.ProteinCountPerFile[rf] = pc;
            if (src.PeptideCountPerFile.TryGetValue(rf, out int pe)) d.PeptideCountPerFile[rf] = pe;
            if (src.TotalIonCurrentPerFile.TryGetValue(rf, out double tic)) d.TotalIonCurrentPerFile[rf] = tic;
            if (src.TargetProteinRatioPerFile.TryGetValue(rf, out double tr)) d.TargetProteinRatioPerFile[rf] = tr;
            if (src.BiologicalConditionPerFile.TryGetValue(rf, out string bc)) d.BiologicalConditionPerFile[rf] = bc;
        }
        d.TotalProteinGroups = d.ProteinQuantMatrix.Count;
        d.TotalPeptides = src.TotalPeptides;
        return d;
    }

    private const BindingFlags Any = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;

    private static void SetField(object o, string name, object v) =>
        (o.GetType().GetField(name, Any) ?? throw new MissingFieldException(name)).SetValue(o, v);

    private static object GetField(object o, string name) =>
        (o.GetType().GetField(name, Any) ?? throw new MissingFieldException(name)).GetValue(o);

    private static void Invoke(object o, string name, params object[] a) =>
        (o.GetType().GetMethod(name, Any) ?? throw new MissingMethodException(name)).Invoke(o, a);

    private static T Get<T>(object o, string prop)
    {
        var p = o.GetType().GetProperty(prop, Any);
        return p == null ? default : (T)p.GetValue(o);
    }

    private static string Csv(string s)
    {
        s ??= "";
        return s.Contains(',') || s.Contains('"') ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
    }
}
