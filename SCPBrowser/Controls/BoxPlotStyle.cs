// BoxPlotStyle.cs
// The look of the identification box plots, chosen in the Customize popover, and its per-user persistence.
// Location: SCPBrowser/Controls/BoxPlotStyle.cs

using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Threading;

namespace SCPBrowser
{
    public enum PlotBackground
    {
        White,
        Warm,

        /// <summary>No background in saved images; on screen it looks white, the panel behind the plot.</summary>
        Transparent
    }

    public enum PlotPoints
    {
        All,
        OutliersOnly,
        None
    }

    /// <summary>
    /// What the user can change about the identification box plots. The same style draws the screen and every saved
    /// PNG or SVG, in both Project Browser tabs.
    /// </summary>
    public sealed class BoxPlotStyle
    {
        public PlotBackground Background { get; set; } = PlotBackground.White;
        public PlotPoints Points { get; set; } = PlotPoints.All;

        /// <summary>Dot diameter in px; 0 = automatic (smaller dots for crowded boxes).</summary>
        public double PointSize { get; set; }

        /// <summary>Dot opacity from 0.1 to 1; 0 = automatic (more transparent for crowded boxes).</summary>
        public double PointOpacity { get; set; }

        /// <summary>Opacity of the box's IQR fill in its own color.</summary>
        public double BoxFillOpacity { get; set; } = 0.18;

        public bool ShowCounts { get; set; } = true;
        public bool ShowGridlines { get; set; } = true;

        /// <summary>Start the y axis at zero instead of fitting it to the data.</summary>
        public bool YFromZero { get; set; }

        /// <summary>
        /// Text the user typed over the automatic header and y-axis title, keyed "title.plate", "title.condition",
        /// "y.proteins", "y.peptides" and "y.genes"; a missing or empty entry keeps the automatic text.
        /// </summary>
        public Dictionary<string, string> Texts { get; set; } = new();

        public string? Text(string key) => Texts.TryGetValue(key, out var text) && !string.IsNullOrWhiteSpace(text) ? text.Trim() : null;

        /// <summary>The text as typed, for the edit box (Text trims it for drawing).</summary>
        public string RawText(string key) => Texts.TryGetValue(key, out var text) ? text : string.Empty;

        /// <summary>Brings values read from an older or hand-edited setting into range.</summary>
        internal void Clamp()
        {
            PointSize = PointSize <= 0 ? 0 : Math.Clamp(PointSize, 2, 14);
            PointOpacity = PointOpacity <= 0 ? 0 : Math.Clamp(PointOpacity, 0.1, 1);
            BoxFillOpacity = Math.Clamp(BoxFillOpacity, 0, 0.8);
            Texts ??= new Dictionary<string, string>();
        }
    }

    /// <summary>
    /// The one style both plots use, persisted per user in Settings.IdentificationPlotStyle (JSON) so it survives
    /// restarts and applies to every project. Changes take effect at once and are written to disk shortly after, so
    /// dragging a slider does not rewrite user.config on every tick.
    /// </summary>
    public static class BoxPlotStyleStore
    {
        private static readonly JsonSerializerOptions Json = new() { Converters = { new JsonStringEnumConverter() } };
        private static BoxPlotStyle? _current;
        private static DispatcherTimer? _saveTimer;

        /// <summary>Raised on the UI thread after every change, so both tabs redraw.</summary>
        public static event EventHandler? Changed;

        public static BoxPlotStyle Current => _current ??= Load();

        public static void Update(Action<BoxPlotStyle> change)
        {
            change(Current);
            Current.Clamp();
            Changed?.Invoke(null, EventArgs.Empty);
            ScheduleSave();
        }

        public static void Reset()
        {
            _current = new BoxPlotStyle();
            Changed?.Invoke(null, EventArgs.Empty);
            ScheduleSave();
        }

        /// <summary>Writes a pending change now (the popover calls this when it closes).</summary>
        public static void Flush()
        {
            if (_saveTimer?.IsEnabled != true)
                return;
            _saveTimer.Stop();
            Save();
        }

        private static BoxPlotStyle Load()
        {
            try
            {
                string json = Settings.Default.IdentificationPlotStyle;
                if (!string.IsNullOrWhiteSpace(json) && JsonSerializer.Deserialize<BoxPlotStyle>(json, Json) is { } style)
                {
                    style.Clamp();
                    return style;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[BoxPlotStyleStore] Ignoring unreadable plot style: {ex.Message}");
            }
            return new BoxPlotStyle();
        }

        private static void ScheduleSave()
        {
            if (_saveTimer == null)
            {
                _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
                _saveTimer.Tick += (s, e) => { _saveTimer.Stop(); Save(); };
            }
            _saveTimer.Stop();
            _saveTimer.Start();
        }

        private static void Save()
        {
            try
            {
                Settings.Default.IdentificationPlotStyle = JsonSerializer.Serialize(Current, Json);
                Settings.Default.Save();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[BoxPlotStyleStore] Could not save the plot style: {ex.Message}");
            }
        }
    }
}
