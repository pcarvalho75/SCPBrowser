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
                || MissingValues != other.MissingValues;
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
                MissingValues = MissingValues
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
