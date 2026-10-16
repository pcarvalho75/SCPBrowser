// IdentificationsPanel.xaml.cs
// Identifications per run as a box plot, grouped by plate or by biological condition.
// Location: SCPBrowser/Controls/IdentificationsPanel.xaml.cs

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using SCPBrowser.Models;
using SCPBrowser.Services;

namespace SCPBrowser
{
    public enum IdentificationGrouping
    {
        Plate,
        Condition
    }

    public partial class IdentificationsPanel : UserControl
    {
        /// <summary>
        /// Colors for conditions the Explorer has no color for (it colors only conditions among the runs it shows).
        /// Starts with the colors of the owner's reference figure.
        /// </summary>
        private static readonly Color[] FallbackConditionPalette =
        {
            Color.FromRgb(0x3B, 0x7D, 0xD8), // blue
            Color.FromRgb(0xE0, 0x66, 0x3A), // orange-red
            Color.FromRgb(0x34, 0xA7, 0x7A), // green
            Color.FromRgb(0x8D, 0x6C, 0xC4), // violet
            Color.FromRgb(0xC9, 0x96, 0x1A), // ochre
            Color.FromRgb(0x2F, 0x9F, 0xB3), // teal
            Color.FromRgb(0xC8, 0x54, 0x7E), // rose
            Color.FromRgb(0x6F, 0x7E, 0x8E)  // slate
        };

        private IdentificationGrouping _grouping = IdentificationGrouping.Plate;
        private IReadOnlyList<RawFileInfo> _rawFiles = Array.Empty<RawFileInfo>();
        private IReadOnlyList<PlateInfo> _plates = Array.Empty<PlateInfo>();
        private ProteomicsData? _data;
        private IReadOnlySet<string> _excludedRuns = new HashSet<string>();
        private List<ResolvedRun> _plottedRuns = new();
        private bool _isGeneMatrix;

        /// <summary>A database run with the value plotted for it.</summary>
        private sealed record ResolvedRun(RawFileInfo File, int Value, bool IsExcluded, bool HasNoIdentifications);

        public IdentificationsPanel()
        {
            InitializeComponent();
            Plot.CategoryClicked += (s, key) => CategoryClicked?.Invoke(this, key);
            Plot.RunClicked += (s, run) => RunClicked?.Invoke(this, run);
            ApplyGrouping();
        }

        /// <summary>Raised with the category key (plate id, or condition name) when a box is clicked.</summary>
        public event EventHandler<string>? CategoryClicked;

        /// <summary>Raised with the run name when a dot is clicked.</summary>
        public event EventHandler<string>? RunClicked;

        public IdentificationGrouping Grouping
        {
            get => _grouping;
            set
            {
                _grouping = value;
                ApplyGrouping();
                Rebuild();
            }
        }

        /// <summary>
        /// Plate colors by plate id, read at every rebuild. They must come from PlateFilterControl, the single source
        /// of plate colors (persisted as PlateColors); a plate missing from it gets that control's default for its
        /// position, which is what PlateFilterControl itself would assign.
        /// </summary>
        public Func<IReadOnlyDictionary<int, Color>?>? PlateColorSource { get; set; }

        /// <summary>Condition colors by name, from the Explorer's condition legend (persisted as BioConditionColors).</summary>
        public Func<IReadOnlyDictionary<string, Color>?>? ConditionColorSource { get; set; }

        /// <summary>Plate id (Plate grouping) or condition name (Condition grouping) to highlight; null shows all alike.</summary>
        public string? HighlightedKey
        {
            get => Plot.HighlightedKey;
            set => Plot.HighlightedKey = value;
        }

        /// <summary>
        /// Plots one dot per database run. Values come from the data as loaded for the Explorer, not from
        /// raw_files.protein_count/peptide_count, which are written once at import and can predate the FDR filter.
        /// </summary>
        /// <param name="rawFiles">Every run in the project database, with its plate and condition.</param>
        /// <param name="plates">The project's plates, in the order the plate list shows them.</param>
        /// <param name="data">The data as loaded for the Explorer; null while it is still loading.</param>
        /// <param name="excludedRuns">Runs the user excluded by hand (the excluded_runs table).</param>
        public void SetData(IReadOnlyList<RawFileInfo> rawFiles, IReadOnlyList<PlateInfo> plates, ProteomicsData? data,
            IReadOnlySet<string> excludedRuns)
        {
            _rawFiles = rawFiles ?? Array.Empty<RawFileInfo>();
            _plates = plates ?? Array.Empty<PlateInfo>();
            _data = data;
            _excludedRuns = excludedRuns ?? new HashSet<string>();

            // A gene matrix has no peptide dimension, so there is nothing to switch to.
            _isGeneMatrix = data?.IsGeneMatrix == true;
            MetricSwitch.Visibility = _isGeneMatrix ? Visibility.Collapsed : Visibility.Visible;
            Rebuild();
        }

        private bool UsePeptides => !_isGeneMatrix && PeptidesRadio.IsChecked == true;

        private void ApplyGrouping()
        {
            if (SplitByPlateCheckBox == null)
                return;
            var conditionOnly = _grouping == IdentificationGrouping.Condition ? Visibility.Visible : Visibility.Collapsed;
            SplitByPlateCheckBox.Visibility = conditionOnly;
            PlateColorsCheckBox.Visibility = conditionOnly;
            Plot.GroupTitle = _grouping == IdentificationGrouping.Plate ? "Plates" : "Biological conditions";
        }

        private void MetricRadio_Checked(object sender, RoutedEventArgs e) => Rebuild();

        private void PlotOption_Changed(object sender, RoutedEventArgs e)
        {
            // Split boxes are plate boxes, so they are always in plate colors.
            if (PlateColorsCheckBox != null && SplitByPlateCheckBox != null)
                PlateColorsCheckBox.IsEnabled = SplitByPlateCheckBox.IsChecked != true;
            Rebuild();
        }

        // ------------------------------------------------------------------------------------------------------------
        // Building the plot
        // ------------------------------------------------------------------------------------------------------------

        private void Rebuild()
        {
            if (Plot == null)
                return;

            Plot.YAxisTitle = _isGeneMatrix ? "Genes per sample" : UsePeptides ? "Peptides per run" : "Protein groups per run";
            Plot.ValueUnit = _isGeneMatrix ? "genes" : UsePeptides ? "peptides" : "protein groups";

            if (_data == null)
            {
                _plottedRuns = new List<ResolvedRun>();
                Plot.EmptyMessage = "Identifications per run appear once the project data has finished loading.";
                Plot.Legend = Array.Empty<BoxPlotLegendEntry>();
                Plot.Categories = Array.Empty<BoxPlotCategory>();
                CaptionText.Text = string.Empty;
                return;
            }

            var (runs, notLoaded) = ResolveRuns(_data, UsePeptides);
            var plateIndex = _plates.Select((p, i) => (p.PlateId, i)).ToDictionary(t => t.PlateId, t => t.i);
            var plateNames = _plates.ToDictionary(p => p.PlateId, p => p.PlateName);
            var plateColors = PlateColorSource?.Invoke();

            Color PlateColor(int plateId)
            {
                if (plateColors != null && plateColors.TryGetValue(plateId, out var chosen))
                    return PlotColors.Ink(chosen);
                int i = plateIndex.TryGetValue(plateId, out var index) ? index : 0;
                return PlotColors.Ink(PlateFilterControl.PlateColorPalette[i % PlateFilterControl.PlateColorPalette.Length]);
            }

            string PlateName(int? plateId) =>
                plateId.HasValue && plateNames.TryGetValue(plateId.Value, out var name) ? name : "No plate";

            BoxPlotRun ToDot(ResolvedRun run, Color color, string detail) => new()
            {
                RunName = run.File.RawFileName,
                Value = run.Value,
                Color = color,
                IsExcluded = run.IsExcluded,
                Detail = detail
            };

            var categories = new List<BoxPlotCategory>();
            var legend = new List<BoxPlotLegendEntry>();
            int notShown;
            string notShownReason;

            if (_grouping == IdentificationGrouping.Plate)
            {
                var placed = runs.Where(r => r.File.PlateId.HasValue && plateIndex.ContainsKey(r.File.PlateId.Value)).ToList();
                notShown = runs.Count - placed.Count;
                notShownReason = "without a plate";
                _plottedRuns = placed;

                foreach (var plate in _plates)
                {
                    var color = PlateColor(plate.PlateId);
                    var plateRuns = placed.Where(r => r.File.PlateId == plate.PlateId)
                        .Select(r => ToDot(r, color, r.File.BiologicalCondition ?? string.Empty))
                        .ToList();
                    categories.Add(new BoxPlotCategory
                    {
                        Key = plate.PlateId.ToString(CultureInfo.InvariantCulture),
                        Label = plate.PlateName,
                        Boxes = new[] { new BoxPlotBox { Label = plate.PlateName, Color = color, Runs = plateRuns } }
                    });
                }
            }
            else
            {
                var labelled = runs.Where(r => !string.IsNullOrEmpty(r.File.BiologicalCondition)).ToList();
                notShown = runs.Count - labelled.Count;
                notShownReason = "without a biological condition";
                _plottedRuns = labelled;

                var conditionColors = ConditionColorSource?.Invoke();
                var conditions = labelled.Select(r => r.File.BiologicalCondition!)
                    .Distinct()
                    .OrderBy(c => c, StringComparer.OrdinalIgnoreCase)
                    .ToList();
                // With every run on one plate, plate colors and a split by plate say nothing, so the options go away
                // and the conditions keep their own colors.
                var platesShown = labelled.Select(r => r.File.PlateId).Distinct().ToList();
                bool severalPlates = platesShown.Count > 1;
                var plateOptions = severalPlates ? Visibility.Visible : Visibility.Collapsed;
                PlateColorsCheckBox.Visibility = plateOptions;
                SplitByPlateCheckBox.Visibility = plateOptions;
                bool split = severalPlates && SplitByPlateCheckBox.IsChecked == true;
                // One color system per view: with runs in plate colors the condition boxes are neutral, because a
                // condition color next to plate-colored dots reads as a plate (a red box of Plate 10 runs when red
                // is Plate 8). Without plate colors, box and dots take the condition's Explorer color.
                bool plateColored = severalPlates && (split || PlateColorsCheckBox.IsChecked == true);

                for (int i = 0; i < conditions.Count; i++)
                {
                    string condition = conditions[i];
                    var conditionRuns = labelled.Where(r => r.File.BiologicalCondition == condition).ToList();

                    // In plate-list order, so split boxes and the legend follow the plate list.
                    var byPlate = conditionRuns
                        .GroupBy(r => r.File.PlateId)
                        .OrderBy(g => g.Key.HasValue && plateIndex.TryGetValue(g.Key.Value, out var pi) ? pi : int.MaxValue)
                        .ToList();

                    IReadOnlyList<BoxPlotBox> boxes;
                    if (split)
                    {
                        boxes = byPlate.Select(g =>
                        {
                            var color = g.Key.HasValue ? PlateColor(g.Key.Value) : PlotColors.NoPlate;
                            return new BoxPlotBox
                            {
                                Label = PlateName(g.Key),
                                Color = color,
                                Runs = g.Select(r => ToDot(r, color, PlateName(g.Key))).ToList()
                            };
                        }).ToList();
                    }
                    else
                    {
                        var conditionColor = conditionColors != null && conditionColors.TryGetValue(condition, out var chosen)
                            ? PlotColors.Ink(chosen)
                            : FallbackConditionPalette[i % FallbackConditionPalette.Length];
                        boxes = new[]
                        {
                            new BoxPlotBox
                            {
                                Label = condition,
                                Color = plateColored ? PlotColors.NeutralBox : conditionColor,
                                Runs = byPlate.SelectMany(g =>
                                {
                                    var color = !plateColored ? conditionColor
                                        : g.Key.HasValue ? PlateColor(g.Key.Value) : PlotColors.NoPlate;
                                    return g.Select(r => ToDot(r, color, PlateName(g.Key)));
                                }).ToList()
                            }
                        };
                    }

                    categories.Add(new BoxPlotCategory { Key = condition, Label = condition, Boxes = boxes });
                }

                // The legend names the plates whose runs are on the plot, in plate-list order.
                if (plateColored)
                {
                    foreach (var plate in _plates.Where(p => platesShown.Contains(p.PlateId)))
                        legend.Add(new BoxPlotLegendEntry { Label = plate.PlateName, Color = PlateColor(plate.PlateId) });
                    if (platesShown.Contains(null))
                        legend.Add(new BoxPlotLegendEntry { Label = "No plate", Color = PlotColors.NoPlate });
                }
            }

            int excluded = _plottedRuns.Count(r => r.IsExcluded);
            if (excluded > 0)
                legend.Add(new BoxPlotLegendEntry { Label = "Excluded", IsRing = true });

            Plot.EmptyMessage = _grouping == IdentificationGrouping.Plate
                ? "No plates with runs in this project yet."
                : "No runs with a biological condition in this project yet.";
            Plot.Legend = legend;
            Plot.Categories = categories.Count > 0 && _plottedRuns.Count > 0 ? categories : Array.Empty<BoxPlotCategory>();
            CaptionText.Text = BuildCaption(notLoaded, notShown, notShownReason);
        }

        /// <summary>
        /// Every database run with its value in the loaded data. A run absent from the loaded data while other runs of
        /// its import are present had no identification pass the filters, so it is plotted at 0 and counted in n. A
        /// run whose whole import is absent was not loaded at all (for example its file was not found), so it is left
        /// out rather than shown as an empty run.
        /// </summary>
        private (List<ResolvedRun> Runs, int NotLoaded) ResolveRuns(ProteomicsData data, bool peptides)
        {
            var counts = peptides ? data.PeptideCountPerFile : data.ProteinCountPerFile;
            var loadedImports = _rawFiles
                .Where(rf => data.ProteinCountPerFile.ContainsKey(rf.RawFileName))
                .Select(rf => rf.ImportId)
                .ToHashSet();

            var runs = new List<ResolvedRun>(_rawFiles.Count);
            int notLoaded = 0;
            foreach (var rf in _rawFiles)
            {
                bool excluded = _excludedRuns.Contains(rf.RawFileName);
                if (counts.TryGetValue(rf.RawFileName, out int value))
                    runs.Add(new ResolvedRun(rf, value, excluded, HasNoIdentifications: false));
                else if (loadedImports.Contains(rf.ImportId))
                    runs.Add(new ResolvedRun(rf, 0, excluded, HasNoIdentifications: true));
                else
                    notLoaded++;
            }
            return (runs, notLoaded);
        }

        private string BuildCaption(int notLoaded, int notShown, string notShownReason)
        {
            string unit = _isGeneMatrix ? "sample" : "run";
            var parts = new List<string>
            {
                $"Each dot is a {unit}; box: median and interquartile range; whiskers: 1.5 × IQR."
            };

            int empty = _plottedRuns.Count(r => r.HasNoIdentifications);
            if (empty > 0)
                parts.Add($"{Plural(empty, unit)} with no identification left after filtering {(empty == 1 ? "is" : "are")} plotted at 0.");
            int excluded = _plottedRuns.Count(r => r.IsExcluded);
            if (excluded > 0)
                parts.Add($"{Plural(excluded, "manually excluded " + unit)} {(excluded == 1 ? "is" : "are")} drawn as {(excluded == 1 ? "a ring" : "rings")} and left out of the boxes and n.");
            if (notShown > 0)
                parts.Add($"{Plural(notShown, unit)} {notShownReason} {(notShown == 1 ? "is" : "are")} not shown.");
            if (notLoaded > 0)
                parts.Add($"{Plural(notLoaded, unit)} from imports that are not loaded {(notLoaded == 1 ? "is" : "are")} not shown.");
            return string.Join(" ", parts);

            static string Plural(int n, string noun) => n == 1 ? $"1 {noun}" : $"{n:N0} {noun}s";
        }

        // ------------------------------------------------------------------------------------------------------------
        // Copy and export
        // ------------------------------------------------------------------------------------------------------------

        private string MetricSlug => _isGeneMatrix ? "genes_per_sample" : UsePeptides ? "peptides_per_run" : "protein_groups_per_run";

        private void CopyImage_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Clipboard.SetImage(Plot.RenderToBitmap(300));
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Could not copy the image:\n\n{ex.Message}", "Copy Image", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void ExportPng_Click(object sender, RoutedEventArgs e)
        {
            int dpi = sender is MenuItem { Tag: string tag } && int.TryParse(tag, out int parsed) ? parsed : 300;
            string grouping = _grouping == IdentificationGrouping.Plate ? "plate" : "condition";
            var dialog = new SaveFileDialog
            {
                FileName = $"{MetricSlug}_by_{grouping}_{dpi}dpi.png",
                Filter = "PNG Image|*.png",
                Title = "Export Identifications Plot"
            };
            if (dialog.ShowDialog() != true)
                return;

            try
            {
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(Plot.RenderToBitmap(dpi)));
                using var stream = new FileStream(dialog.FileName, FileMode.Create);
                encoder.Save(stream);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Export failed: {ex.Message}", "Export Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void CopyValues_Click(object sender, RoutedEventArgs e)
        {
            if (_plottedRuns.Count == 0)
                return;

            var plateNames = _plates.ToDictionary(p => p.PlateId, p => p.PlateName);
            string metric = _isGeneMatrix ? "Genes" : UsePeptides ? "Peptides" : "Protein groups";
            var text = new StringBuilder();
            text.AppendLine($"Run\tPlate\tBiological condition\t{metric}\tExcluded");
            foreach (var run in _plottedRuns.OrderBy(r => r.File.RawFileName, StringComparer.Ordinal))
            {
                string plate = run.File.PlateId.HasValue && plateNames.TryGetValue(run.File.PlateId.Value, out var name) ? name : string.Empty;
                text.AppendLine(string.Join("\t", run.File.RawFileName, plate, run.File.BiologicalCondition ?? string.Empty,
                    run.Value.ToString(CultureInfo.InvariantCulture), run.IsExcluded ? "yes" : "no"));
            }

            try
            {
                Clipboard.SetText(text.ToString());
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Could not copy the values:\n\n{ex.Message}", "Copy Values", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }

    /// <summary>Colors as drawn on the plot's off-white card.</summary>
    internal static class PlotColors
    {
        public static readonly Color NoPlate = Color.FromRgb(0x8A, 0x8F, 0x98);

        /// <summary>Box color when the runs inside carry the colors (plate-colored runs of a condition).</summary>
        public static readonly Color NeutralBox = Color.FromRgb(0x5F, 0x66, 0x72);

        /// <summary>
        /// The same hue, deepened when the color is too light to read on the off-white card. The default plate colors
        /// are pastels chosen for buttons; drawn as 7 px dots or a 1.5 px outline on #F7F6F3 they nearly vanish, so a
        /// color lighter than the reference figure's (relative luminance above 0.30) has its lightness lowered until it
        /// matches, and its saturation capped so the result is not neon. Darker colors are returned unchanged.
        /// </summary>
        public static Color Ink(Color color)
        {
            const double MaxLuminance = 0.30, TargetLuminance = 0.27, MaxSaturation = 0.78;
            if (RelativeLuminance(color) <= MaxLuminance)
                return color;

            RgbToHsl(color, out double h, out double s, out double l);
            s = Math.Min(s, MaxSaturation);
            double lo = 0.12, hi = l;
            for (int i = 0; i < 24; i++)
            {
                double mid = (lo + hi) / 2;
                if (RelativeLuminance(HslToRgb(h, s, mid)) > TargetLuminance) hi = mid; else lo = mid;
            }
            return HslToRgb(h, s, lo);
        }

        private static double RelativeLuminance(Color c)
        {
            static double Linear(byte v)
            {
                double x = v / 255.0;
                return x <= 0.04045 ? x / 12.92 : Math.Pow((x + 0.055) / 1.055, 2.4);
            }
            return 0.2126 * Linear(c.R) + 0.7152 * Linear(c.G) + 0.0722 * Linear(c.B);
        }

        private static void RgbToHsl(Color c, out double h, out double s, out double l)
        {
            double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
            double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
            l = (max + min) / 2;
            double d = max - min;
            if (d < 1e-9)
            {
                h = 0;
                s = 0;
                return;
            }
            s = d / (1 - Math.Abs(2 * l - 1));
            if (max == r) h = 60 * (((g - b) / d) % 6);
            else if (max == g) h = 60 * ((b - r) / d + 2);
            else h = 60 * ((r - g) / d + 4);
            if (h < 0) h += 360;
        }

        private static Color HslToRgb(double h, double s, double l)
        {
            double c = (1 - Math.Abs(2 * l - 1)) * s;
            double x = c * (1 - Math.Abs((h / 60) % 2 - 1));
            double m = l - c / 2;
            (double r, double g, double b) = h switch
            {
                < 60 => (c, x, 0.0),
                < 120 => (x, c, 0.0),
                < 180 => (0.0, c, x),
                < 240 => (0.0, x, c),
                < 300 => (x, 0.0, c),
                _ => (c, 0.0, x)
            };
            static byte ToByte(double v) => (byte)Math.Round(Math.Clamp(v, 0, 1) * 255);
            return Color.FromRgb(ToByte(r + m), ToByte(g + m), ToByte(b + m));
        }
    }
}
