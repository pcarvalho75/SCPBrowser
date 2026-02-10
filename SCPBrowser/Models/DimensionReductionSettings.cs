using System;
using System.Threading.Tasks;
using SCPBrowser.Services;

namespace SCPBrowser.Models
{
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

        // PCA
        public int NumPcaComponents { get; set; } = 30;
        public int NumPcsForUmap { get; set; } = 20;

        // UMAP
        public int UmapNeighbors { get; set; } = 15;
        public bool UmapFixedSeed { get; set; } = true;

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
            s.UmapFixedSeed = await ReadBoolAsync(db, "UmapFixedSeed", s.UmapFixedSeed);
            s.UseGuidedEmbedding = await ReadBoolAsync(db, "UseGuidedEmbedding", s.UseGuidedEmbedding);
            s.GuidedWeight = await ReadDoubleAsync(db, "GuidedWeight", s.GuidedWeight);
            s.ShowPcaView = await ReadBoolAsync(db, "ShowPcaView", s.ShowPcaView);
            s.UseHvpFilter = await ReadBoolAsync(db, "UseHvpFilter", s.UseHvpFilter);
            s.HvpCount = await ReadIntAsync(db, "HvpCount", s.HvpCount);
            s.ApplyBatchCorrection = await ReadBoolAsync(db, "ApplyBatchCorrection", s.ApplyBatchCorrection);

            return s;
        }

        /// <summary>
        /// Saves all settings to the project database.
        /// </summary>
        public async Task SaveAsync(ProjectDatabaseService db)
        {
            if (db == null) return;

            await db.SetSettingAsync(KeyPrefix + "ZScoreScale", ZScoreScale.ToString());
            await db.SetSettingAsync(KeyPrefix + "ClipMaxValue", ClipMaxValue.ToString("R"));
            await db.SetSettingAsync(KeyPrefix + "NumPcaComponents", NumPcaComponents.ToString());
            await db.SetSettingAsync(KeyPrefix + "NumPcsForUmap", NumPcsForUmap.ToString());
            await db.SetSettingAsync(KeyPrefix + "UmapNeighbors", UmapNeighbors.ToString());
            await db.SetSettingAsync(KeyPrefix + "UmapFixedSeed", UmapFixedSeed.ToString());
            await db.SetSettingAsync(KeyPrefix + "UseGuidedEmbedding", UseGuidedEmbedding.ToString());
            await db.SetSettingAsync(KeyPrefix + "GuidedWeight", GuidedWeight.ToString("R"));
            await db.SetSettingAsync(KeyPrefix + "ShowPcaView", ShowPcaView.ToString());
            await db.SetSettingAsync(KeyPrefix + "UseHvpFilter", UseHvpFilter.ToString());
            await db.SetSettingAsync(KeyPrefix + "HvpCount", HvpCount.ToString());
            await db.SetSettingAsync(KeyPrefix + "ApplyBatchCorrection", ApplyBatchCorrection.ToString());
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
                || UseGuidedEmbedding != other.UseGuidedEmbedding
                || Math.Abs(GuidedWeight - other.GuidedWeight) > 1e-9
                || UseHvpFilter != other.UseHvpFilter
                || HvpCount != other.HvpCount;
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
                UmapFixedSeed = UmapFixedSeed,
                UseGuidedEmbedding = UseGuidedEmbedding,
                GuidedWeight = GuidedWeight,
                ShowPcaView = ShowPcaView,
                UseHvpFilter = UseHvpFilter,
                HvpCount = HvpCount,
                ApplyBatchCorrection = ApplyBatchCorrection
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
