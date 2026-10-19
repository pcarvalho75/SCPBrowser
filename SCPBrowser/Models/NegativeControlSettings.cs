using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using SCPBrowser.Services;

namespace SCPBrowser.Models
{
    /// <summary>How the blank level of a protein is taken from the selected blank runs that detect it.</summary>
    public enum BlankLevelStatistic
    {
        /// <summary>The highest blank intensity: the strictest bar a cell measurement must clear. Default.</summary>
        Max = 0,
        /// <summary>The median blank intensity: less sensitive to one unusually high blank.</summary>
        Median = 1
    }

    /// <summary>
    /// Negative controls (blanks) and the background removal estimated from them, saved in the project. A condition
    /// marked as a negative control holds blank runs; the runs not left out are the selected blanks the background is
    /// estimated from. When removal is on, a protein is removed from every run its blanks apply to (the run's plate, or
    /// the whole project) if it is found in at least <see cref="MinFraction"/> of those blanks, and, with the intensity
    /// rule, a measurement is also removed when it is below <see cref="IntensityFactor"/> times the protein's blank level
    /// (with <see cref="RequireBothRules"/>, only when both rules apply). The data as loaded is never changed; the
    /// cleaned copy is built from it on every load.
    /// </summary>
    public sealed class NegativeControlSettings
    {
        private const string SettingKey = "negctrl.Settings";

        /// <summary>The biological conditions whose runs are blanks.</summary>
        public List<string> Conditions { get; set; } = new();

        /// <summary>Blank runs left out of the background estimate (for example, contaminated blanks).</summary>
        public List<string> LeftOutRuns { get; set; } = new();

        /// <summary>Background removal applied on load. Marking blanks alone changes nothing downstream.</summary>
        public bool RemovalEnabled { get; set; }

        /// <summary>Frequency rule: remove a protein found in at least <see cref="MinFraction"/> of the selected blanks.</summary>
        public bool UseFrequency { get; set; } = true;

        /// <summary>Fraction of the selected blanks (0 to 1) a protein must be found in for the frequency rule.</summary>
        public double MinFraction { get; set; } = 0.5;

        /// <summary>Intensity rule: remove a measurement below <see cref="IntensityFactor"/> times the blank level.</summary>
        public bool UseIntensity { get; set; }

        /// <summary>
        /// How many times its blank level a cell measurement must reach to be kept under the intensity rule. 2 reads as
        /// "at most half of the signal can be the blank's"; on Isabele's biopsies every k from 1 to 3 draws nearly the
        /// same line, because their background proteins sit at about the blank level while lysates sit 16x above it.
        /// </summary>
        public double IntensityFactor { get; set; } = 2;

        /// <summary>
        /// With both rules on, remove a measurement only when both apply: the protein is frequent in the blanks AND the
        /// measurement is below <see cref="IntensityFactor"/> times its blank level. A run with clearly more of a blank
        /// protein than any blank keeps it (Isabele's cell lysates, 16x above the blanks). Off: either rule removes it.
        /// </summary>
        public bool RequireBothRules { get; set; }

        /// <summary>How the blank level is taken from the selected blanks that detect a protein.</summary>
        public BlankLevelStatistic BlankLevel { get; set; } = BlankLevelStatistic.Max;

        /// <summary>
        /// Each plate's blanks apply only to that plate's runs. A plate with fewer than <see cref="MinBlanksPerScope"/>
        /// selected blanks of its own uses all the selected blanks of the project, and the preview says so.
        /// </summary>
        public bool PerPlate { get; set; } = true;

        /// <summary>
        /// The fewest selected blanks a plate needs to be estimated on its own blanks (per-plate scope). With one blank,
        /// any percentage means "found in that blank", so the default is 2.
        /// </summary>
        public int MinBlanksPerScope { get; set; } = 2;

        /// <summary>
        /// Blank runs are left out of the cell analyses (Explorer, classification, protein matrix, exports), with or
        /// without background removal: they are not cells. They stay in the Conditions tab and in the data as loaded.
        /// </summary>
        public bool ExcludeBlankRunsFromAnalysis { get; set; } = true;

        /// <summary>True when removal is on and there are blanks and at least one rule to apply.</summary>
        [JsonIgnore]
        public bool RemovesBackground => RemovalEnabled && Conditions.Count > 0 && (UseFrequency || UseIntensity);

        /// <summary>True when the analyses see different data from the data as loaded (removal, or blank runs left out).</summary>
        [JsonIgnore]
        public bool ChangesAnalysis => RemovesBackground || (Conditions.Count > 0 && ExcludeBlankRunsFromAnalysis);

        public bool IsNegativeControl(string condition) =>
            condition != null && Conditions.Contains(condition, StringComparer.Ordinal);

        public NegativeControlSettings Clone() => new()
        {
            Conditions = new List<string>(Conditions),
            LeftOutRuns = new List<string>(LeftOutRuns),
            RemovalEnabled = RemovalEnabled,
            UseFrequency = UseFrequency,
            MinFraction = MinFraction,
            UseIntensity = UseIntensity,
            IntensityFactor = IntensityFactor,
            RequireBothRules = RequireBothRules,
            BlankLevel = BlankLevel,
            PerPlate = PerPlate,
            MinBlanksPerScope = MinBlanksPerScope,
            ExcludeBlankRunsFromAnalysis = ExcludeBlankRunsFromAnalysis
        };

        /// <summary>Same blanks, rules and options (the order of the lists does not matter).</summary>
        public bool SameAs(NegativeControlSettings other) =>
            other != null
            && Conditions.OrderBy(c => c, StringComparer.Ordinal).SequenceEqual(other.Conditions.OrderBy(c => c, StringComparer.Ordinal))
            && LeftOutRuns.OrderBy(r => r, StringComparer.Ordinal).SequenceEqual(other.LeftOutRuns.OrderBy(r => r, StringComparer.Ordinal))
            && RemovalEnabled == other.RemovalEnabled
            && UseFrequency == other.UseFrequency
            && MinFraction == other.MinFraction
            && UseIntensity == other.UseIntensity
            && IntensityFactor == other.IntensityFactor
            && RequireBothRules == other.RequireBothRules
            && BlankLevel == other.BlankLevel
            && PerPlate == other.PerPlate
            && MinBlanksPerScope == other.MinBlanksPerScope
            && ExcludeBlankRunsFromAnalysis == other.ExcludeBlankRunsFromAnalysis;

        /// <summary>The saved settings, or the defaults (no blanks, removal off) when none are saved or they cannot be read.</summary>
        public static async Task<NegativeControlSettings> LoadAsync(ProjectDatabaseService db)
        {
            if (db == null)
                return new NegativeControlSettings();
            try
            {
                string json = await db.GetSettingAsync(SettingKey);
                if (string.IsNullOrWhiteSpace(json))
                    return new NegativeControlSettings();
                var settings = JsonSerializer.Deserialize<NegativeControlSettings>(json) ?? new NegativeControlSettings();
                settings.Conditions ??= new List<string>();
                settings.LeftOutRuns ??= new List<string>();
                settings.MinFraction = Math.Clamp(settings.MinFraction, 0.0, 1.0);
                if (!(settings.IntensityFactor > 0))
                    settings.IntensityFactor = 2;
                settings.MinBlanksPerScope = Math.Max(1, settings.MinBlanksPerScope);
                return settings;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[NegativeControlSettings.LoadAsync] {ex.Message}");
                return new NegativeControlSettings();
            }
        }

        /// <summary>Saves the settings as one JSON value (numbers in invariant form, whatever the machine's culture).</summary>
        public Task SaveAsync(ProjectDatabaseService db) =>
            db.SetSettingAsync(SettingKey, JsonSerializer.Serialize(this));
    }
}
