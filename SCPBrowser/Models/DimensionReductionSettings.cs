using System;
using System.Threading.Tasks;
using SCPBrowser.Services;

namespace SCPBrowser.Models
{
    /// <summary>Per-cell normalisation applied before batch correction and z-scoring.</summary>
    public enum CellNormalization
    {
        /// <summary>No per-cell normalisation (the behaviour before this option existed).</summary>
        None = 0,
        /// <summary>Subtract each cell's median log2 abundance over its observed proteins.</summary>
        MedianCentre = 1,
        /// <summary>Scale each cell to a common total intensity before the log transform.</summary>
        TotalIntensity = 2
    }

    /// <summary>How proteins not quantified in a cell are filled in for dimensionality reduction.</summary>
    public enum MissingValueMode
    {
        /// <summary>Write 0 (the historical behaviour; treats "not measured" as a measured zero).</summary>
        Zero = 0,
        /// <summary>Use the protein's mean across the cells where it was observed.</summary>
        ProteinMean = 1
    }

    /// <summary>
    /// Settings for the dimensionality reduction pipeline (PCA/UMAP).
    /// Persisted per-project via ProjectDatabaseService key-value store.
    /// </summary>
    public class DimensionReductionSettings
    {
        private const string KeyPrefix = "dimred.";

        // Preprocessing
        public bool ZScoreScale { get; set; } = true;
        public double ClipMaxValue { get; set; } = 10.0;

        /// <summary>
        /// Per-cell normalisation applied to the log2 matrix BEFORE batch correction and z-scoring.
        ///
        /// Single-cell proteomes differ in input amount, cell size and depth, so without this two cells that differ
        /// only in how much material they contained separate in PCA/UMAP as if they were biologically different -
        /// PC1 easily becomes a depth axis that then gets named as a population. "MedianCentre" subtracts each
        /// cell's median log2 abundance (a robust, standard SCP choice); "TotalIntensity" divides by the cell's
        /// summed intensity before the log. "None" is the historical behaviour, kept so earlier analyses can be
        /// reproduced exactly.
        /// </summary>
        public CellNormalization Normalization { get; set; } = CellNormalization.MedianCentre;

        /// <summary>
        /// How proteins not quantified in a given cell are treated.
        ///
        /// The historical behaviour wrote 0, which in log2 space is a REAL abundance, not "unknown" - with 40-70%
        /// missingness that drags means down and inflates variance, and the HVP service's own documentation says
        /// explicitly that missing must not be treated as zero. "ProteinMean" substitutes the protein's mean over
        /// the cells where it WAS observed, which leaves the protein's centre unchanged instead of pulling it
        /// toward zero.
        /// </summary>
        public MissingValueMode MissingValues { get; set; } = MissingValueMode.ProteinMean;

        /// <summary>
        /// Minimum fraction of cells in which a protein must be quantified for it to enter the embedding.
        /// 0 = no floor (the historical behaviour).
        ///
        /// This is the single strongest lever against a "detection map": a protein seen in a small minority of
        /// cells contributes mostly imputed values, so its column encodes WHICH cells detected it - that is,
        /// their depth - rather than how much protein they contained. On a 74%-missing dataset the binary
        /// detection pattern alone can reproduce nearly all of the apparent population structure. Raising this
        /// floor removes those columns, at the cost of a smaller feature set.
        /// </summary>
        public double MinDetectionRate { get; set; } = 0.0;

        /// <summary>
        /// Regress the per-cell number of quantified proteins (sequencing depth) out of every protein before
        /// batch correction. Depth is the dominant nuisance axis in single-cell proteomics and routinely
        /// becomes a leading principal component; removing it linearly is the standard, conservative fix.
        /// </summary>
        public bool RegressDepth { get; set; } = false;

        /// <summary>
        /// k for optional kNN-graph smoothing (0 = off). Each cell is replaced by a distance-weighted average
        /// of itself and its k nearest neighbours, which suppresses per-cell sampling noise. Standard practice
        /// in single-cell analysis, but it makes clusters look more discrete than the data supports and MUST be
        /// disclosed in any figure legend that uses it.
        /// </summary>
        public int SmoothingNeighbors { get; set; } = 0;

        /// <summary>Number of diffusion steps for kNN smoothing (ignored when SmoothingNeighbors is 0).</summary>
        public int SmoothingSteps { get; set; } = 1;

        // PCA
        public int NumPcaComponents { get; set; } = 30;
        public int NumPcsForUmap { get; set; } = 20;

        // UMAP
        public int UmapNeighbors { get; set; } = 15;
        public int UmapSeed { get; set; } = 42;

        // Guided Embedding (optional, NOT default)
        public bool UseGuidedEmbedding { get; set; } = false;
        public double GuidedWeight { get; set; } = 0.3;

        // UI / display settings (do NOT trigger recomputation)
        public bool ShowPcaView { get; set; } = false;
        public bool UseHvpFilter { get; set; } = false;
        public int HvpCount { get; set; } = 500;
        public bool ApplyBatchCorrection { get; set; } = false;

        /// <summary>
        /// Creates a new instance with factory defaults.
        /// </summary>
        public static DimensionReductionSettings CreateDefaults() => new DimensionReductionSettings();

        /// <summary>
        /// Loads settings from the project database. Missing keys get defaults.
        /// </summary>
        public static async Task<DimensionReductionSettings> LoadAsync(ProjectDatabaseService db)
        {
            var s = new DimensionReductionSettings();
            if (db == null) return s;

            s.ZScoreScale = await ReadBoolAsync(db, "ZScoreScale", s.ZScoreScale);
            s.ClipMaxValue = await ReadDoubleAsync(db, "ClipMaxValue", s.ClipMaxValue);
            s.NumPcaComponents = await ReadIntAsync(db, "NumPcaComponents", s.NumPcaComponents);
            s.NumPcsForUmap = await ReadIntAsync(db, "NumPcsForUmap", s.NumPcsForUmap);
            s.UmapNeighbors = await ReadIntAsync(db, "UmapNeighbors", s.UmapNeighbors);
            s.UmapSeed = await ReadIntAsync(db, "UmapSeed", s.UmapSeed);
            s.UseGuidedEmbedding = await ReadBoolAsync(db, "UseGuidedEmbedding", s.UseGuidedEmbedding);
            s.GuidedWeight = await ReadDoubleAsync(db, "GuidedWeight", s.GuidedWeight);
            s.ShowPcaView = await ReadBoolAsync(db, "ShowPcaView", s.ShowPcaView);
            s.UseHvpFilter = await ReadBoolAsync(db, "UseHvpFilter", s.UseHvpFilter);
            s.HvpCount = await ReadIntAsync(db, "HvpCount", s.HvpCount);
            s.ApplyBatchCorrection = await ReadBoolAsync(db, "ApplyBatchCorrection", s.ApplyBatchCorrection);
            s.Normalization = (CellNormalization)await ReadIntAsync(db, "Normalization", (int)s.Normalization);
            s.MissingValues = (MissingValueMode)await ReadIntAsync(db, "MissingValues", (int)s.MissingValues);
            s.MinDetectionRate = await ReadDoubleAsync(db, "MinDetectionRate", s.MinDetectionRate);
            s.RegressDepth = await ReadBoolAsync(db, "RegressDepth", s.RegressDepth);
            s.SmoothingNeighbors = await ReadIntAsync(db, "SmoothingNeighbors", s.SmoothingNeighbors);
            s.SmoothingSteps = await ReadIntAsync(db, "SmoothingSteps", s.SmoothingSteps);

            return s;
        }

        /// <summary>
        /// Saves all settings to the project database.
        /// </summary>
        public async Task SaveAsync(ProjectDatabaseService db)
        {
            if (db == null) return;

            await db.SetSettingAsync(KeyPrefix + "ZScoreScale", ZScoreScale.ToString());
            // ReadDoubleAsync parses with InvariantCulture, so the writer must use it too. Writing with the
            // current culture stores "3,5" on a comma-decimal machine, which then fails to parse on reload and
            // silently reverts the setting to its default - the saved figure stops being reproducible.
            await db.SetSettingAsync(KeyPrefix + "ClipMaxValue", ClipMaxValue.ToString("R", System.Globalization.CultureInfo.InvariantCulture));
            await db.SetSettingAsync(KeyPrefix + "NumPcaComponents", NumPcaComponents.ToString());
            await db.SetSettingAsync(KeyPrefix + "NumPcsForUmap", NumPcsForUmap.ToString());
            await db.SetSettingAsync(KeyPrefix + "UmapNeighbors", UmapNeighbors.ToString());
            await db.SetSettingAsync(KeyPrefix + "UmapSeed", UmapSeed.ToString());
            await db.SetSettingAsync(KeyPrefix + "UseGuidedEmbedding", UseGuidedEmbedding.ToString());
            await db.SetSettingAsync(KeyPrefix + "GuidedWeight", GuidedWeight.ToString("R", System.Globalization.CultureInfo.InvariantCulture));
            await db.SetSettingAsync(KeyPrefix + "ShowPcaView", ShowPcaView.ToString());
            await db.SetSettingAsync(KeyPrefix + "UseHvpFilter", UseHvpFilter.ToString());
            await db.SetSettingAsync(KeyPrefix + "HvpCount", HvpCount.ToString());
            await db.SetSettingAsync(KeyPrefix + "ApplyBatchCorrection", ApplyBatchCorrection.ToString());
            await db.SetSettingAsync(KeyPrefix + "Normalization", ((int)Normalization).ToString());
            await db.SetSettingAsync(KeyPrefix + "MissingValues", ((int)MissingValues).ToString());
            await db.SetSettingAsync(KeyPrefix + "MinDetectionRate", MinDetectionRate.ToString("R", System.Globalization.CultureInfo.InvariantCulture));
            await db.SetSettingAsync(KeyPrefix + "RegressDepth", RegressDepth.ToString());
            await db.SetSettingAsync(KeyPrefix + "SmoothingNeighbors", SmoothingNeighbors.ToString());
            await db.SetSettingAsync(KeyPrefix + "SmoothingSteps", SmoothingSteps.ToString());
        }

        /// <summary>
        /// Returns true if any setting differs from another instance.
        /// Used to detect when recomputation is needed.
        /// </summary>
        public bool DiffersFrom(DimensionReductionSettings other)
        {
            if (other == null) return true;
            return ZScoreScale != other.ZScoreScale
                || Math.Abs(ClipMaxValue - other.ClipMaxValue) > 1e-9
                || NumPcaComponents != other.NumPcaComponents
                || NumPcsForUmap != other.NumPcsForUmap
                || UmapNeighbors != other.UmapNeighbors
                || UmapSeed != other.UmapSeed
                || UseGuidedEmbedding != other.UseGuidedEmbedding
                || Math.Abs(GuidedWeight - other.GuidedWeight) > 1e-9
                || UseHvpFilter != other.UseHvpFilter
                || HvpCount != other.HvpCount
                // Both change the matrix that PCA/UMAP consumes, so the embedding must be recomputed.
                || Normalization != other.Normalization
                || MissingValues != other.MissingValues
                || Math.Abs(MinDetectionRate - other.MinDetectionRate) > 1e-9
                || RegressDepth != other.RegressDepth
                || SmoothingNeighbors != other.SmoothingNeighbors
                || SmoothingSteps != other.SmoothingSteps;
        }

        /// <summary>
        /// Creates a deep copy of these settings.
        /// </summary>
        public DimensionReductionSettings Clone()
        {
            return new DimensionReductionSettings
            {
                ZScoreScale = ZScoreScale,
                ClipMaxValue = ClipMaxValue,
                NumPcaComponents = NumPcaComponents,
                NumPcsForUmap = NumPcsForUmap,
                UmapNeighbors = UmapNeighbors,
                UmapSeed = UmapSeed,
                UseGuidedEmbedding = UseGuidedEmbedding,
                GuidedWeight = GuidedWeight,
                ShowPcaView = ShowPcaView,
                UseHvpFilter = UseHvpFilter,
                HvpCount = HvpCount,
                ApplyBatchCorrection = ApplyBatchCorrection,
                Normalization = Normalization,
                MissingValues = MissingValues,
                MinDetectionRate = MinDetectionRate,
                RegressDepth = RegressDepth,
                SmoothingNeighbors = SmoothingNeighbors,
                SmoothingSteps = SmoothingSteps
            };
        }

        // --- Private helpers ---

        private static async Task<bool> ReadBoolAsync(ProjectDatabaseService db, string name, bool defaultValue)
        {
            var val = await db.GetSettingAsync(KeyPrefix + name);
            return val != null ? bool.TryParse(val, out var b) ? b : defaultValue : defaultValue;
        }

        private static async Task<int> ReadIntAsync(ProjectDatabaseService db, string name, int defaultValue)
        {
            var val = await db.GetSettingAsync(KeyPrefix + name);
            return val != null ? int.TryParse(val, out var i) ? i : defaultValue : defaultValue;
        }

        private static async Task<double> ReadDoubleAsync(ProjectDatabaseService db, string name, double defaultValue)
        {
            var val = await db.GetSettingAsync(KeyPrefix + name);
            return val != null ? double.TryParse(val, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : defaultValue : defaultValue;
        }
    }
}
