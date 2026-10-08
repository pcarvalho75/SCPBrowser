using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using Microsoft.Data.Sqlite;
using SCPBrowser;
using SCPBrowser.Models;
using SCPBrowser.Services;

namespace HeadlessEmbed;

/// <summary>
/// Generates the Explorer's PCA/UMAP embedding through SCPBrowser's own code, without the GUI.
///
/// The pipeline (BuildPreprocessedMatrix, ComputePca, ComputeUmap) lives as private methods on ScatterPlotControl,
/// a WPF UserControl. Copying that maths here would let the harness and the application drift apart - the exact
/// failure this tool exists to rule out - so the control is allocated without running its constructor and the real
/// methods are invoked by reflection. They operate on the data handed to them and never touch the visual tree.
/// Nothing here reads or writes classifications.
///
/// Usage:
///   HeadlessEmbed &lt;projectDir&gt; &lt;out.csv&gt; [options]
///
///   --no-batch-correction        turn ComBat off (otherwise the project setting is used)
///   --engine uwot|legacy         UMAP engine          --min-dist X   --spread X   --nn N
///   --pca N   --pcs N            PCA components / PCs fed to UMAP
///   --min-detect P               detection floor in percent of cells
///   --smooth K   --smooth-steps S   --depth on|off   --guided on|off
///   --keep-markers               protect key-marker proteins from the detection floor
///   --sweep                      fit a grid of UMAP settings from one PCA; out.csv's folder receives one file each
///   --ui-roundtrip               self-test of the dimensionality-reduction dialog wiring (no project needed)
/// Every option overrides the value stored in the project for this run only; the project is never modified.
/// </summary>
internal static class Program
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            if (args.Contains("--ui-roundtrip")) return UiRoundTrip();

            string projectDir = args.Length > 0 ? args[0] : @"C:\Users\paulo\Desktop\SpaceProjectV3";
            string outCsv = args.Length > 1 ? args[1] : Path.Combine(projectDir, "embedding.csv");
            string dbPath = Path.Combine(projectDir, "project.db");
            if (!File.Exists(dbPath)) throw new FileNotFoundException("project.db not found", dbPath);

            // ---- settings, exactly as the application persists them, then per-run overrides -------------------
            var settings = DimensionReductionSettings.LoadAsync(new ProjectDatabaseService(dbPath)).GetAwaiter().GetResult();
            if (args.Contains("--no-batch-correction")) settings.ApplyBatchCorrection = false;
            string engine = OptStr(args, "--engine", null);
            if (engine != null)
                settings.UmapEngine = engine.Equals("legacy", StringComparison.OrdinalIgnoreCase)
                    ? UmapEngineKind.Legacy : UmapEngineKind.Uwot;
            settings.UmapMinDist = OptDouble(args, "--min-dist", settings.UmapMinDist);
            settings.UmapSpread = OptDouble(args, "--spread", settings.UmapSpread);
            settings.UmapNeighbors = OptInt(args, "--nn", settings.UmapNeighbors);
            settings.NumPcaComponents = OptInt(args, "--pca", settings.NumPcaComponents);
            settings.NumPcsForUmap = Math.Min(OptInt(args, "--pcs", settings.NumPcsForUmap), settings.NumPcaComponents);
            settings.MinDetectionRate = OptDouble(args, "--min-detect", settings.MinDetectionRate * 100) / 100.0;
            settings.SmoothingNeighbors = OptInt(args, "--smooth", settings.SmoothingNeighbors);
            settings.SmoothingSteps = OptInt(args, "--smooth-steps", settings.SmoothingSteps);
            string guided = OptStr(args, "--guided", null);
            if (guided != null) settings.UseGuidedEmbedding = guided.Equals("on", StringComparison.OrdinalIgnoreCase);
            string depth = OptStr(args, "--depth", null);
            if (depth != null) settings.RegressDepth = depth.Equals("on", StringComparison.OrdinalIgnoreCase);

            Console.WriteLine($"project        : {projectDir}");
            Console.WriteLine($"settings       : engine={settings.UmapEngine} minDist={settings.UmapMinDist.ToString(Inv)} " +
                              $"spread={settings.UmapSpread.ToString(Inv)} nn={settings.UmapNeighbors} seed={settings.UmapSeed} " +
                              $"pca={settings.NumPcaComponents}/{settings.NumPcsForUmap} floor={settings.MinDetectionRate:P0} " +
                              $"norm={settings.Normalization} missing={settings.MissingValues} depth={settings.RegressDepth} " +
                              $"smooth={settings.SmoothingNeighbors}x{settings.SmoothingSteps} combat={settings.ApplyBatchCorrection} " +
                              $"hvp={settings.UseHvpFilter}");

            // ---- data, through the application's own parquet reader --------------------------------------------
            var parquetFiles = Directory.GetFiles(Path.Combine(projectDir, "imports"), "*.parquet").OrderBy(f => f).ToList();
            if (parquetFiles.Count == 0) throw new InvalidOperationException("no parquet files under imports/");
            var mapping = new ColumnMapping
            {
                RawFileColumn = "Run",
                ProteinGroupColumn = "Protein.Group",
                PeptideColumn = ColumnMapping.DefaultPeptideColumn,
                TotalIonCurrentColumn = ColumnMapping.DefaultQuantityColumn,
                TargetProteinIdentifiers = new List<string>()
            };
            var data = new ParquetDataService(dbPath).LoadMultipleParquetFilesAsync(parquetFiles, mapping).GetAwaiter().GetResult();
            Console.WriteLine($"loaded         : {data.RawFileNames.Count} runs, {data.ProteinQuantMatrix.Count} protein groups");

            // ---- cohort, through the same stages as the Explorer -------------------------------------------------
            // DataFilterService: plate filter, then the protein-count cutoff on the PARQUET-DERIVED count
            // (data.ProteinCountPerFile, which the Explorer displays; raw_files.protein_count disagrees with it).
            // The checked populations and Hide Grey Dots are then applied by ScatterPlotControl itself.
            var meta = LoadRunMetadata(dbPath);
            int cutoff = ReadIntSetting(dbPath, "ProteinCutoff", 0);
            int upper = ReadIntSetting(dbPath, "UpperProteinCutoff", int.MaxValue);
            var plates = ReadCsvSetting(dbPath, "CheckedPlates");
            var conditions = ReadCsvSetting(dbPath, "CheckedBioConditions");
            var cellTypes = ReadCsvSetting(dbPath, "CheckedCellTypes");
            bool hideGrey = bool.TryParse(ReadSetting(dbPath, "HideGreyDots"), out bool hg) && hg;
            double contaminantCutoff = double.TryParse(ReadSetting(dbPath, "ContaminantRatioCutoff"), NumberStyles.Float, Inv,
                                                       out double cc) ? cc : 1.0;

            var stage = data.RawFileNames.Where(rf =>
            {
                if (!meta.TryGetValue(rf, out var m)) return false;
                if (plates.Count > 0 && (m.Plate == null || !plates.Contains(m.Plate))) return false;
                int pc = data.ProteinCountPerFile.TryGetValue(rf, out int v) ? v : 0;
                if (pc < cutoff || pc > upper) return false;
                // Contaminant-ratio stage, as DataFilterService.FilterByContaminantRatio: keep ratio <= cutoff.
                if (contaminantCutoff < 1.0)
                {
                    double ratio = data.TargetProteinRatioPerFile.TryGetValue(rf, out double rr) ? rr : 0;
                    if (ratio > contaminantCutoff) return false;
                }
                return true;
            }).ToList();

            var staged = Trim(data, stage.OrderBy(x => x, StringComparer.Ordinal).ToList());
            foreach (var rf in staged.RawFileNames) staged.BiologicalConditionPerFile[rf] = meta[rf].Condition;
            Console.WriteLine($"filter stages  : {staged.RawFileNames.Count} runs after plate + cutoff {cutoff}-" +
                              $"{(upper == int.MaxValue ? "inf" : upper.ToString(Inv))}; contaminant cutoff = {contaminantCutoff:P0}; " +
                              $"hide grey dots = {hideGrey}");

            var options = new ScatterPlotOptions
            {
                DimRedSettings = settings,
                UseUmapView = true,
                ApplyBatchCorrection = settings.ApplyBatchCorrection,
                BatchLabelPerFile = staged.RawFileNames.ToDictionary(rf => rf, rf => meta[rf].PlateId),
                BioConditionPerFile = staged.RawFileNames.ToDictionary(rf => rf, rf => meta[rf].Condition),
                PlatePerFile = staged.RawFileNames.ToDictionary(rf => rf, rf => meta[rf].Plate ?? ""),
                CheckedBioConditions = conditions,
                CheckedPlates = plates,
                CheckedCellTypes = cellTypes,
                // The stored classifications, as the Explorer holds them. They drive the cell-type population filter,
                // guided embedding when it is on, and the label-purity diagnostic - all inside the application's code.
                CellTypePredictions = staged.RawFileNames
                    .Where(rf => !string.IsNullOrEmpty(meta[rf].CellType))
                    .ToDictionary(rf => rf, rf => new CellTypePredictionResult { TopCellType = meta[rf].CellType }),
                HvpResults = null,
                ContaminantRatioCutoff = 1.0,
                AlwaysKeepProteins = args.Contains("--keep-markers") ? MarkerProteins(dbPath, staged) : null
            };

            // ---- the application's real pipeline -----------------------------------------------------------------
            var control = (ScatterPlotControl)RuntimeHelpers.GetUninitializedObject(typeof(ScatterPlotControl));
            SetField(control, "_currentOptions", options);
            SetField(control, "_hideUnselected", hideGrey);
            Invoke(control, "ComputePca", staged);

            if (args.Contains("--sweep"))
                return Sweep(control, settings, staged, meta, Path.GetDirectoryName(Path.GetFullPath(outCsv)));

            Invoke(control, "ComputeUmap", staged);
            var umap = (float[][])GetField(control, "_umapResult")
                       ?? throw new InvalidOperationException("UMAP returned null: cohort too small or PCA failed.");
            var used = (List<string>)GetField(control, "_dimRedRawFiles");

            Console.WriteLine();
            Console.WriteLine("--- diagnostics reported by the application itself ---");
            Console.WriteLine($"cells in embedding    : {Get<int>(control, "EmbeddingCohortSize")}");
            Console.WriteLine($"proteins in embedding : {Get<int>(control, "EmbeddingProteinCount")}");
            Console.WriteLine($"dropped below floor   : {Get<int>(control, "CoverageFloorDropped")}");
            Console.WriteLine($"markers rescued       : {Get<int>(control, "MarkersRescued")}");
            Console.WriteLine($"missing rate          : {Get<double>(control, "LastMissingRate"):P1}");
            Console.WriteLine($"depth regressed       : {Get<bool>(control, "DepthRegressed")}");
            Console.WriteLine($"kNN smoothing k       : {Get<int>(control, "SmoothingApplied")}");
            Console.WriteLine($"PC vs depth |rho|     : {Get<double>(control, "DepthPcCorrelation"):F3}");
            Console.WriteLine($"UMAP engine used      : {Get<string>(control, "UmapEngineUsed")}");
            Console.WriteLine($"guided embedding      : {settings.UseGuidedEmbedding} (weight {settings.GuidedWeight.ToString(Inv)})");
            Console.WriteLine($"label purity (PCA)    : {Get<double?>(control, "LabelPurity")?.ToString("F3", Inv) ?? "n/a"}");
            foreach (var w in new[] { "CoverageFloorWarning", "BatchCorrectionWarning", "UmapEngineWarning" })
            {
                var msg = Get<string>(control, w);
                if (!string.IsNullOrEmpty(msg)) Console.WriteLine($"{w,-22}: {msg}");
            }

            WriteCsv(outCsv, used, umap, staged, meta);
            Console.WriteLine($"\nwrote {used.Count} rows to {outCsv}");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("FAILED: " + ex);
            return 1;
        }
    }

    /// <summary>
    /// Fits a grid of UMAP settings from ONE preprocessing + PCA, each through the application's ComputeUmap, so
    /// the only thing that varies between output files is the UMAP layout itself.
    /// </summary>
    private static int Sweep(ScatterPlotControl control, DimensionReductionSettings s, ProteomicsData staged,
                             Dictionary<string, RunMeta> meta, string dir)
    {
        Directory.CreateDirectory(dir);
        var used = (List<string>)GetField(control, "_dimRedRawFiles");
        int n = 0;
        void Fit(UmapEngineKind engine, double md, double sp, int nn, string name)
        {
            s.UmapEngine = engine; s.UmapMinDist = md; s.UmapSpread = sp; s.UmapNeighbors = nn;
            SetField(control, "_umapResult", null);
            Invoke(control, "ComputeUmap", staged);
            var e = (float[][])GetField(control, "_umapResult");
            if (e == null) return;
            WriteCsv(Path.Combine(dir, name), used, e, staged, meta);
            n++;
        }
        foreach (var nn in new[] { 10, 15, 30, 50 })
        {
            Fit(UmapEngineKind.Legacy, 0, 1, nn, $"legacy_nn{nn}.csv");
            foreach (var md in new[] { 0.1, 0.25, 0.4, 0.6, 0.8 })
                foreach (var sp in new[] { 1.0, 1.5, 2.5 })
                    if (md < sp)
                        Fit(UmapEngineKind.Uwot, md, sp, nn,
                            string.Format(Inv, "uwot_md{0}_sp{1}_nn{2}.csv", md, sp, nn));
        }
        Console.WriteLine($"sweep          : wrote {n} embeddings to {dir}");
        return 0;
    }

    /// <summary>
    /// Builds the Explorer control off-screen and checks that every dimensionality-reduction setting survives the
    /// trip settings -> dialog -> settings. A field that is not written to the dialog, or not read back from it, is
    /// silently reset on the next Apply; that bug shipped once for the detection floor and the smoothing fields.
    /// </summary>
    private static int UiRoundTrip()
    {
        var app = new App();
        app.InitializeComponent();                         // application resources the control's XAML refers to
        var ctl = new PeptideTicControl();

        var want = new DimensionReductionSettings
        {
            ZScoreScale = false, ClipMaxValue = 7.5, Normalization = CellNormalization.TotalIntensity,
            MissingValues = MissingValueMode.Zero, MinDetectionRate = 0.85, RegressDepth = true,
            SmoothingNeighbors = 12, SmoothingSteps = 3, NumPcaComponents = 17, NumPcsForUmap = 13,
            UmapNeighbors = 33, UmapSeed = 7, UmapEngine = UmapEngineKind.Uwot, UmapMinDist = 0.6, UmapSpread = 2.0,
            UseGuidedEmbedding = true, GuidedWeight = 0.2, ShowPcaView = true, UseHvpFilter = true, HvpCount = 750,
            ApplyBatchCorrection = true
        };
        int fails = 0;
        void Check(string what, bool ok) { Console.WriteLine($"  {(ok ? "PASS" : "FAIL")}  {what}"); if (!ok) fails++; }

        // Round trip, with the control's current settings set to defaults so nothing can leak in from a clone.
        Invoke(ctl, "ApplySettingsToUI", want);
        SetField(ctl, "_dimRedSettings", DimensionReductionSettings.CreateDefaults());
        var got = (DimensionReductionSettings)ctl.GetType().GetMethod("ReadSettingsFromUI", Any).Invoke(ctl, null);
        Console.WriteLine("settings -> dialog -> settings:");
        foreach (var prop in typeof(DimensionReductionSettings).GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            object a = prop.GetValue(want), b = prop.GetValue(got);
            bool same = a is double da && b is double db ? Math.Abs(da - db) < 1e-9 : Equals(a, b);
            Check($"{prop.Name,-22} {a} -> {b}", same);
        }

        // A blank field must keep the value in force, not revert to the factory default.
        SetField(ctl, "_dimRedSettings", want.Clone());
        SetText(ctl, "MinDetectionTextBox", "");
        SetText(ctl, "SmoothingKTextBox", "");
        var kept = (DimensionReductionSettings)ctl.GetType().GetMethod("ReadSettingsFromUI", Any).Invoke(ctl, null);
        Console.WriteLine("blank fields keep the value in force:");
        Check($"MinDetectionRate stays {want.MinDetectionRate}", Math.Abs(kept.MinDetectionRate - want.MinDetectionRate) < 1e-9);
        Check($"SmoothingNeighbors stays {want.SmoothingNeighbors}", kept.SmoothingNeighbors == want.SmoothingNeighbors);

        // min_dist must be clamped below spread.
        Invoke(ctl, "ApplySettingsToUI", want);
        SetText(ctl, "UmapMinDistTextBox", "5");
        SetText(ctl, "UmapSpreadTextBox", "1");
        var clamped = (DimensionReductionSettings)ctl.GetType().GetMethod("ReadSettingsFromUI", Any).Invoke(ctl, null);
        Console.WriteLine("min_dist clamp:");
        Check($"min_dist 5 with spread 1 -> {clamped.UmapMinDist}", clamped.UmapSpread == 1 && clamped.UmapMinDist < 1);

        // min_dist and spread are greyed out for the legacy engine.
        var legacy = want.Clone(); legacy.UmapEngine = UmapEngineKind.Legacy;
        Invoke(ctl, "ApplySettingsToUI", legacy);
        bool minOff = !((System.Windows.Controls.TextBox)GetField(ctl, "UmapMinDistTextBox")).IsEnabled;
        Invoke(ctl, "ApplySettingsToUI", want);
        bool minOn = ((System.Windows.Controls.TextBox)GetField(ctl, "UmapMinDistTextBox")).IsEnabled;
        Console.WriteLine("engine toggle:");
        Check("min_dist disabled for Legacy, enabled for uwot", minOff && minOn);

        Console.WriteLine(fails == 0 ? "\nALL PASS" : $"\n{fails} FAILURE(S)");
        return fails == 0 ? 0 : 2;
    }

    private static void SetText(object ctl, string field, string text) =>
        ((System.Windows.Controls.TextBox)GetField(ctl, field)).Text = text;

    private static void WriteCsv(string path, List<string> runs, float[][] e, ProteomicsData data,
                                 Dictionary<string, RunMeta> meta)
    {
        // Protein and peptide counts are the parquet-derived ones the Explorer filters on and displays.
        using var w = new StreamWriter(path);
        w.WriteLine("run,condition,plate,cell_type,protein_count,peptide_count,umap1,umap2");
        for (int i = 0; i < runs.Count; i++)
        {
            var rf = runs[i];
            var m = meta[rf];
            w.WriteLine(string.Join(",",
                Csv(rf), Csv(m.Condition), Csv(m.Plate), Csv(m.CellType),
                (data.ProteinCountPerFile.TryGetValue(rf, out int pc) ? pc : 0).ToString(Inv),
                (data.PeptideCountPerFile.TryGetValue(rf, out int pe) ? pe : 0).ToString(Inv),
                e[i][0].ToString("R", Inv), e[i][1].ToString("R", Inv)));
        }
    }

    // ---------------- project reads ----------------

    private sealed record RunMeta(string Condition, string Plate, int PlateId, string CellType);

    private static Dictionary<string, RunMeta> LoadRunMetadata(string dbPath)
    {
        var d = new Dictionary<string, RunMeta>(StringComparer.Ordinal);
        using var c = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly");
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText =
            "SELECT rf.raw_file_name, rf.biological_condition, p.plate_name, COALESCE(rf.plate_id,0), " +
            "       COALESCE(cl.predicted_cell_type,'') " +
            "FROM raw_files rf " +
            "LEFT JOIN plates p ON p.plate_id = rf.plate_id " +
            "LEFT JOIN raw_file_cell_type_classifications cl ON cl.raw_file_id = rf.raw_file_id";
        using var r = cmd.ExecuteReader();
        while (r.Read())
            d[r.GetString(0)] = new RunMeta(r.IsDBNull(1) ? "" : r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2),
                                            r.GetInt32(3), r.GetString(4));
        return d;
    }

    private static string ReadSetting(string dbPath, string key)
    {
        using var c = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly");
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT setting_value FROM project_settings WHERE setting_key=$k";
        cmd.Parameters.AddWithValue("$k", key);
        return cmd.ExecuteScalar() as string;
    }

    private static int ReadIntSetting(string dbPath, string key, int fallback) =>
        int.TryParse(ReadSetting(dbPath, key), NumberStyles.Integer, Inv, out int v) ? v : fallback;

    private static HashSet<string> ReadCsvSetting(string dbPath, string key)
    {
        var raw = ReadSetting(dbPath, key);
        return string.IsNullOrWhiteSpace(raw)
            ? new HashSet<string>(StringComparer.Ordinal)
            : raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                 .Select(Uri.UnescapeDataString).ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>Protein groups whose gene symbol is in the project key-marker table.</summary>
    private static HashSet<string> MarkerProteins(string dbPath, ProteomicsData data)
    {
        var genes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (var c = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly"))
        {
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "SELECT DISTINCT UPPER(gene_name) FROM cell_type_key_markers";
            using var r = cmd.ExecuteReader();
            while (r.Read()) if (!r.IsDBNull(0)) genes.Add(r.GetString(0));
        }
        var keep = new HashSet<string>(StringComparer.Ordinal);
        foreach (var pg in data.ProteinQuantMatrix.Keys)
            if (data.ProteinToGeneMap.TryGetValue(pg, out var gs) && !string.IsNullOrEmpty(gs) &&
                gs.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Any(genes.Contains))
                keep.Add(pg);
        return keep;
    }

    /// <summary>Restricts the dataset to a set of runs, as DataFilterService does between stages.</summary>
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
            IsGeneMatrix = src.IsGeneMatrix,
            TotalPeptides = src.TotalPeptides
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
        }
        d.TotalProteinGroups = d.ProteinQuantMatrix.Count;
        return d;
    }

    // ---------------- small helpers ----------------

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

    private static string OptStr(string[] args, string flag, string fallback)
    {
        int i = Array.IndexOf(args, flag);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : fallback;
    }

    private static int OptInt(string[] args, string flag, int fallback) =>
        int.TryParse(OptStr(args, flag, null), NumberStyles.Integer, Inv, out int v) ? v : fallback;

    private static double OptDouble(string[] args, string flag, double fallback) =>
        double.TryParse(OptStr(args, flag, null), NumberStyles.Float, Inv, out double v) ? v : fallback;

    private static string Csv(string s)
    {
        s ??= "";
        return s.Contains(',') || s.Contains('"') ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
    }
}
