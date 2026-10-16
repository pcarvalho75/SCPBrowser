// IdentificationBoxPlot.cs
// Box plot of identifications per run (one dot per run), drawn directly with a DrawingContext so it stays crisp on
// screen and exports at any DPI. The data model and styling only; IdentificationsPanel decides what is plotted.
// Location: SCPBrowser/Controls/IdentificationBoxPlot.cs

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace SCPBrowser
{
    /// <summary>One run, drawn as one dot.</summary>
    public sealed class BoxPlotRun
    {
        public string RunName { get; init; } = string.Empty;
        public double Value { get; init; }
        public Color Color { get; init; }

        /// <summary>Manually excluded runs are drawn as rings and left out of the box and of n.</summary>
        public bool IsExcluded { get; init; }

        /// <summary>Context for the hover label, such as the run's plate or condition.</summary>
        public string Detail { get; init; } = string.Empty;
    }

    /// <summary>One box: the IQR, median and whiskers of its included runs, in one color.</summary>
    public sealed class BoxPlotBox
    {
        public string Label { get; init; } = string.Empty;
        public Color Color { get; init; }

        /// <summary>Opacity of the IQR fill. Lower for a neutral box whose dots carry the colors, so it recedes.</summary>
        public double FillOpacity { get; init; } = 0.18;
        public IReadOnlyList<BoxPlotRun> Runs { get; init; } = Array.Empty<BoxPlotRun>();
    }

    /// <summary>One labelled position on the x axis; several boxes side by side when split by plate.</summary>
    public sealed class BoxPlotCategory
    {
        public string Key { get; init; } = string.Empty;
        public string Label { get; init; } = string.Empty;
        public IReadOnlyList<BoxPlotBox> Boxes { get; init; } = Array.Empty<BoxPlotBox>();
    }

    public sealed class BoxPlotLegendEntry
    {
        public string Label { get; init; } = string.Empty;
        public Color Color { get; init; }

        /// <summary>Drawn as a ring, the mark used for excluded runs.</summary>
        public bool IsRing { get; init; }
    }

    public class IdentificationBoxPlot : FrameworkElement
    {
        // Styling. The plot is a warm off-white card with faint horizontal gridlines only; every box has one color.
        private static readonly Color CardColor = Color.FromRgb(0xF7, 0xF6, 0xF3);
        private static readonly Color GridColor = Color.FromRgb(0xE6, 0xE3, 0xDC);
        private static readonly Color TickTextColor = Color.FromRgb(0x6E, 0x6E, 0x6E);
        private static readonly Color TitleTextColor = Color.FromRgb(0x33, 0x33, 0x33);
        private static readonly Color HeaderTextColor = Color.FromRgb(0x5C, 0x5C, 0x5C);
        private static readonly Color CountTextColor = Color.FromRgb(0x80, 0x80, 0x80);
        private static readonly Color LabelTextColor = Color.FromRgb(0x3F, 0x3F, 0x3F);
        private static readonly Color ExcludedColor = Color.FromRgb(0x9A, 0x9A, 0x9A);
        private static readonly Typeface UiFace = new(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        private static readonly Typeface UiFaceSemiBold = new(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);

        private const double BoxOutlineThickness = 1.5;
        private const double MedianThickness = 3.0;
        private const double WhiskerThickness = 1.2;
        private const double MutedOpacity = 0.28;
        private const double IdealSlotWidth = 150;
        private const double SplitBoxSlotWidth = 46;
        private const double MinSlotWidth = 30;

        private IReadOnlyList<BoxPlotCategory> _categories = Array.Empty<BoxPlotCategory>();
        private List<BoxGeometry> _boxes = new();
        private double _dataMin, _dataMax;
        private int _maxRunsPerBox;

        // Hit areas recorded by the last on-screen render, for hover and click.
        private readonly List<(Point Center, double Radius, BoxGeometry Box, BoxPlotRun Run)> _dotHits = new();
        private readonly List<(Rect Area, BoxGeometry Box)> _boxHits = new();
        private readonly List<(Rect Area, BoxPlotCategory Category)> _slotHits = new();
        private readonly List<(Rect Column, BoxGeometry Box)> _boxColumns = new();
        private Rect _plotArea;
        private BoxPlotRun? _hoverRun;
        private BoxGeometry? _hoverBox;

        public IdentificationBoxPlot()
        {
            ClipToBounds = true;
            SnapsToDevicePixels = true;
        }

        /// <summary>Raised with the category key when a box or its slot is clicked.</summary>
        public event EventHandler<string>? CategoryClicked;

        /// <summary>Raised with the run name when a dot is clicked.</summary>
        public event EventHandler<string>? RunClicked;

        public IReadOnlyList<BoxPlotCategory> Categories
        {
            get => _categories;
            set
            {
                _categories = value ?? Array.Empty<BoxPlotCategory>();
                _hoverRun = null;
                _hoverBox = null;
                PrepareGeometry();
                InvalidateMeasure();
                InvalidateVisual();
            }
        }

        private string _groupTitle = string.Empty;
        /// <summary>Small grey header centred above the boxes, naming what the categories are.</summary>
        public string GroupTitle { get => _groupTitle; set { _groupTitle = value ?? string.Empty; InvalidateVisual(); } }

        private string _yAxisTitle = string.Empty;
        public string YAxisTitle { get => _yAxisTitle; set { _yAxisTitle = value ?? string.Empty; InvalidateVisual(); } }

        private string _valueUnit = string.Empty;
        /// <summary>Unit after a value in the hover label, such as "protein groups".</summary>
        public string ValueUnit { get => _valueUnit; set { _valueUnit = value ?? string.Empty; InvalidateVisual(); } }

        private IReadOnlyList<BoxPlotLegendEntry> _legend = Array.Empty<BoxPlotLegendEntry>();
        public IReadOnlyList<BoxPlotLegendEntry> Legend { get => _legend; set { _legend = value ?? Array.Empty<BoxPlotLegendEntry>(); InvalidateVisual(); } }

        private string? _highlightedKey;
        /// <summary>When set, every other category is muted so this one stands out.</summary>
        public string? HighlightedKey { get => _highlightedKey; set { _highlightedKey = value; InvalidateVisual(); } }

        private string _emptyMessage = "No runs to plot.";
        public string EmptyMessage { get => _emptyMessage; set { _emptyMessage = value ?? string.Empty; InvalidateVisual(); } }

        // ------------------------------------------------------------------------------------------------------------
        // Geometry that depends on the data only (statistics and jitter), computed once per data set
        // ------------------------------------------------------------------------------------------------------------

        private sealed class BoxStats
        {
            public int N;
            public double Q1, Median, Q3, LowWhisker, HighWhisker;
        }

        private sealed class BoxGeometry
        {
            public required BoxPlotCategory Category;
            public required BoxPlotBox Box;
            public int CategoryIndex;
            public int BoxIndex;
            public BoxStats? Stats;

            /// <summary>Horizontal offset of each run, in [-1, 1] of the jitter half-width (same order as Box.Runs).</summary>
            public double[] Jitter = Array.Empty<double>();
        }

        private void PrepareGeometry()
        {
            _boxes = new List<BoxGeometry>();
            _dataMin = double.PositiveInfinity;
            _dataMax = double.NegativeInfinity;
            _maxRunsPerBox = 0;

            for (int c = 0; c < _categories.Count; c++)
            {
                var category = _categories[c];
                for (int b = 0; b < category.Boxes.Count; b++)
                {
                    var box = category.Boxes[b];
                    var included = box.Runs.Where(r => !r.IsExcluded).Select(r => r.Value).ToArray();
                    _boxes.Add(new BoxGeometry
                    {
                        Category = category,
                        Box = box,
                        CategoryIndex = c,
                        BoxIndex = b,
                        Stats = ComputeStats(included),
                        Jitter = ComputeJitter(box.Runs)
                    });
                    foreach (var run in box.Runs)
                    {
                        _dataMin = Math.Min(_dataMin, run.Value);
                        _dataMax = Math.Max(_dataMax, run.Value);
                    }
                    _maxRunsPerBox = Math.Max(_maxRunsPerBox, box.Runs.Count);
                }
            }
        }

        private static BoxStats? ComputeStats(double[] values)
        {
            if (values.Length == 0)
                return null;

            Array.Sort(values);
            double q1 = Quantile(values, 0.25), median = Quantile(values, 0.5), q3 = Quantile(values, 0.75);
            double iqr = q3 - q1;
            double lowFence = q1 - 1.5 * iqr, highFence = q3 + 1.5 * iqr;

            // Whiskers reach the furthest run inside the 1.5 x IQR fences; runs beyond them are simply dots.
            double low = values.First(v => v >= lowFence);
            double high = values.Last(v => v <= highFence);
            return new BoxStats { N = values.Length, Q1 = q1, Median = median, Q3 = q3, LowWhisker = low, HighWhisker = high };
        }

        /// <summary>Linear-interpolation quantile of sorted values (R's default type 7, as in ggplot2).</summary>
        private static double Quantile(double[] sorted, double p)
        {
            double h = (sorted.Length - 1) * p;
            int lo = (int)Math.Floor(h);
            int hi = Math.Min(lo + 1, sorted.Length - 1);
            return sorted[lo] + (h - lo) * (sorted[hi] - sorted[lo]);
        }

        /// <summary>
        /// Deterministic jitter, so dots stay put on every redraw. Runs are ranked by value and spread with the golden
        /// ratio sequence, which sends runs of similar value to opposite sides instead of piling them up. For larger
        /// boxes the spread follows the density of values (a sina plot), so a dense band fans out and a sparse tail
        /// stays near the centre line.
        /// </summary>
        private static double[] ComputeJitter(IReadOnlyList<BoxPlotRun> runs)
        {
            int n = runs.Count;
            var jitter = new double[n];
            if (n <= 1)
                return jitter;

            var order = Enumerable.Range(0, n)
                .OrderBy(i => runs[i].Value)
                .ThenBy(i => runs[i].RunName, StringComparer.Ordinal)
                .ToArray();

            const double Golden = 0.6180339887498949;
            for (int rank = 0; rank < n; rank++)
            {
                double u = (0.5 + rank * Golden) % 1.0;
                jitter[order[rank]] = u * 2.0 - 1.0;
            }

            if (n >= 15)
            {
                var values = runs.Select(r => r.Value).ToArray();
                double mean = values.Average();
                double sd = Math.Sqrt(values.Sum(v => (v - mean) * (v - mean)) / (n - 1));
                var sorted = values.OrderBy(v => v).ToArray();
                double iqr = Quantile(sorted, 0.75) - Quantile(sorted, 0.25);
                double spread = iqr > 0 ? Math.Min(sd, iqr / 1.34) : sd;
                double bandwidth = 0.9 * spread * Math.Pow(n, -0.2);
                if (bandwidth > 0)
                {
                    var density = new double[n];
                    for (int i = 0; i < n; i++)
                    {
                        double sum = 0;
                        for (int j = 0; j < n; j++)
                        {
                            double z = (values[i] - values[j]) / bandwidth;
                            sum += Math.Exp(-0.5 * z * z);
                        }
                        density[i] = sum;
                    }
                    double maxDensity = density.Max();
                    for (int i = 0; i < n; i++)
                        jitter[i] *= 0.25 + 0.75 * density[i] / maxDensity;
                }
            }
            return jitter;
        }

        // ------------------------------------------------------------------------------------------------------------
        // Layout
        // ------------------------------------------------------------------------------------------------------------

        private sealed class Layout
        {
            public double Width, Height;
            public Rect Plot;
            public double SlotWidth;
            public double BoxWidth;
            public double ClusterStep;
            public double YMin, YMax;
            public List<double> Ticks = new();
            public double HeaderBaseline;
            public double CountTop;
            public double LabelTop;
            public bool RotateLabels;
            public double DotRadius;
            public double DotOpacity;
            public double JitterHalfWidth;
            public double TickTextWidth;
            public bool GroupThousands;
            public double PixelsPerDip;
        }

        private int BoxesPerCategory => _categories.Count == 0 ? 1 : Math.Max(1, _categories.Max(c => c.Boxes.Count));

        private double IdealSlot => Math.Max(IdealSlotWidth, BoxesPerCategory * SplitBoxSlotWidth + 44);

        private const double PadLeft = 12, PadRight = 18, PadTop = 9, PadBottom = 8;
        private const double YTitleBand = 22;

        protected override Size MeasureOverride(Size availableSize)
        {
            double ideal = PadLeft + YTitleBand + 44 + Math.Max(1, _categories.Count) * IdealSlot + PadRight;
            double width = double.IsInfinity(availableSize.Width) ? ideal : Math.Min(availableSize.Width, ideal);
            double height = double.IsInfinity(availableSize.Height) ? 340 : availableSize.Height;
            return new Size(Math.Max(0, width), Math.Max(0, height));
        }

        private Layout ComputeLayout(Size size, double pixelsPerDip)
        {
            var layout = new Layout { Width = size.Width, Height = size.Height, PixelsPerDip = pixelsPerDip };
            int categoryCount = Math.Max(1, _categories.Count);
            int perCategory = BoxesPerCategory;

            double headerHeight = (_groupTitle.Length > 0 || _legend.Count > 0) ? 18 : 0;
            const double countHeight = 16;
            double plotTop = PadTop + headerHeight + countHeight + 4;

            // Tick labels decide how much room the axis needs, and the ticks depend on the plot height; one estimate
            // of the label band is close enough because the label width barely changes the height.
            double provisionalLeft = PadLeft + YTitleBand + 44;
            double provisionalSlot = (size.Width - provisionalLeft - PadRight) / categoryCount;
            layout.RotateLabels = provisionalSlot < 64 && _categories.Count > 1;
            double labelBand = layout.RotateLabels ? RotatedLabelBand() : UprightLabelBand(provisionalSlot, pixelsPerDip);
            double plotBottom = size.Height - PadBottom - labelBand;

            ComputeYScale(layout, plotBottom - plotTop);
            layout.TickTextWidth = layout.Ticks.Count == 0 ? 0
                : layout.Ticks.Max(t => MakeText(FormatTick(layout, t), 11, TickTextColor, pixelsPerDip).Width);

            double plotLeft = PadLeft + YTitleBand + layout.TickTextWidth + 8;
            double plotRight = size.Width - PadRight;
            layout.Plot = new Rect(plotLeft, plotTop, Math.Max(10, plotRight - plotLeft), Math.Max(10, plotBottom - plotTop));
            layout.SlotWidth = layout.Plot.Width / categoryCount;
            layout.HeaderBaseline = PadTop;
            layout.CountTop = PadTop + headerHeight;
            layout.LabelTop = layout.Plot.Bottom + 8;

            if (perCategory == 1)
            {
                layout.BoxWidth = Math.Min(layout.SlotWidth * 0.46, 58);
                layout.ClusterStep = 0;
            }
            else
            {
                double cluster = layout.SlotWidth * 0.86;
                layout.ClusterStep = cluster / perCategory;
                layout.BoxWidth = Math.Min(layout.ClusterStep * 0.74, 40);
            }

            // Dots shrink and fade a little as boxes fill up, so a plate of 250 runs still reads as a distribution.
            layout.DotRadius = _maxRunsPerBox <= 30 ? 3.75 : _maxRunsPerBox <= 80 ? 3.25 : _maxRunsPerBox <= 200 ? 2.75 : 2.4;
            layout.DotRadius = Math.Max(1.6, Math.Min(layout.DotRadius, layout.BoxWidth / 6.5));
            layout.DotOpacity = _maxRunsPerBox <= 40 ? 1.0 : _maxRunsPerBox <= 120 ? 0.8 : 0.7;
            layout.JitterHalfWidth = layout.BoxWidth * (_maxRunsPerBox >= 15 ? 0.46 : 0.36);
            return layout;
        }

        /// <summary>Height of the upright category labels: one line, or two when a label has to wrap.</summary>
        private double UprightLabelBand(double slotWidth, double pixelsPerDip)
        {
            double tallest = 0;
            foreach (var category in _categories)
            {
                var text = MakeText(category.Label.Replace("_", "_\u200B"), 11.5, LabelTextColor, pixelsPerDip,
                    maxWidth: Math.Max(20, slotWidth - 8), maxLines: 2, semiBold: true);
                tallest = Math.Max(tallest, text.Height);
            }
            return tallest + 10;
        }

        private double RotatedLabelBand()
        {
            // Long labels are trimmed to this width before rotation, so the band height is bounded.
            const double maxLabel = 110, angle = 35 * Math.PI / 180;
            return maxLabel * Math.Sin(angle) + 16 * Math.Cos(angle) + 8;
        }

        private void ComputeYScale(Layout layout, double plotHeight)
        {
            double min = double.IsInfinity(_dataMin) ? 0 : _dataMin;
            double max = double.IsInfinity(_dataMax) ? 1 : _dataMax;
            if (max <= min)
            {
                max = min + Math.Max(1, Math.Abs(min) * 0.1);
                min = Math.Max(0, min - (max - min));
            }

            // ggplot2-style 5% expansion on both sides so no dot sits on the edge; counts never go below zero, so the
            // lower edge only dips under zero far enough to show dots at zero whole.
            double range = max - min;
            layout.YMin = min - range * 0.05;
            layout.YMax = max + range * 0.05;

            // Gridlines about 50 px apart (the reference figure is sparse: 500, 1000): the nice step nearest (on a log
            // scale) to 50 px of data, between roughly 2.5 and 8 steps over the axis.
            double target = Math.Clamp(plotHeight / 50, 2.5, 8);
            double step = NiceStep((layout.YMax - layout.YMin) / target);
            layout.Ticks.Clear();
            for (double t = Math.Ceiling(layout.YMin / step) * step; t <= layout.YMax + step * 1e-9; t += step)
            {
                if (t < -1e-9) continue;
                layout.Ticks.Add(Math.Abs(t) < 1e-9 ? 0 : t);
            }
            // One format per axis: 500 ... 2500 stay plain like the reference figure, but once a tick reaches five
            // digits every tick is grouped (7,500 next to 10,000, not 7500).
            layout.GroupThousands = layout.Ticks.Count > 0 && layout.Ticks.Max() >= 10000;
        }

        /// <summary>The 1, 2, 2.5 or 5 x 10^k step closest (on a log scale) to the rough step, never below 1.</summary>
        private static double NiceStep(double rough)
        {
            if (rough <= 0 || double.IsNaN(rough)) return 1;
            double magnitude = Math.Pow(10, Math.Floor(Math.Log10(rough)));
            double best = magnitude;
            foreach (double nice in new[] { 1.0, 2.0, 2.5, 5.0, 10.0 })
                if (Math.Abs(Math.Log(nice * magnitude / rough)) < Math.Abs(Math.Log(best / rough)))
                    best = nice * magnitude;
            return Math.Max(1, best);
        }

        private static string FormatTick(Layout layout, double value) =>
            value.ToString(layout.GroupThousands ? "N0" : "0", CultureInfo.CurrentCulture);

        private static double YToScreen(Layout layout, double value) =>
            layout.Plot.Bottom - (value - layout.YMin) / (layout.YMax - layout.YMin) * layout.Plot.Height;

        private static double BoxCenterX(Layout layout, BoxGeometry box)
        {
            double slotCenter = layout.Plot.Left + (box.CategoryIndex + 0.5) * layout.SlotWidth;
            int count = box.Category.Boxes.Count;
            return count <= 1 ? slotCenter : slotCenter + (box.BoxIndex - (count - 1) / 2.0) * layout.ClusterStep;
        }

        // ------------------------------------------------------------------------------------------------------------
        // Drawing
        // ------------------------------------------------------------------------------------------------------------

        protected override void OnRender(DrawingContext dc)
        {
            if (ActualWidth <= 0 || ActualHeight <= 0)
                return;
            Draw(dc, new Size(ActualWidth, ActualHeight), VisualTreeHelper.GetDpi(this).PixelsPerDip, forExport: false);
        }

        /// <summary>
        /// Size of a saved figure: the plot's on-screen width, and at least 420 (up to 560) units tall so a plot squeezed
        /// by the splitter still saves as a proper figure. The layout is recomputed for that size, not stretched.
        /// </summary>
        public Size ExportSize
        {
            get
            {
                double width = ActualWidth > 0 ? ActualWidth : 520;
                double height = Math.Max(ActualHeight, Math.Min(560, Math.Max(420, width * 1.1)));
                return new Size(width, height);
            }
        }

        /// <summary>
        /// Renders the plot at ExportSize into a bitmap of the given DPI. The export is the figure itself: no hover
        /// label and no muting of the categories that are not selected.
        /// </summary>
        public BitmapSource RenderToBitmap(double dpi)
        {
            var size = ExportSize;
            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
                Draw(dc, size, dpi / 96.0, forExport: true);

            var bitmap = new RenderTargetBitmap(
                (int)Math.Ceiling(size.Width * dpi / 96.0), (int)Math.Ceiling(size.Height * dpi / 96.0),
                dpi, dpi, PixelFormats.Pbgra32);
            bitmap.Render(visual);
            bitmap.Freeze();
            return bitmap;
        }

        /// <summary>The same figure as RenderToBitmap, as SVG (vector, text as outlines; 1 unit = 1/96 inch).</summary>
        public string RenderToSvg()
        {
            var size = ExportSize;
            var drawing = new DrawingGroup();
            using (var dc = drawing.Open())
                Draw(dc, size, 1.0, forExport: true);
            return DrawingSvgWriter.Write(drawing, size);
        }

        private void Draw(DrawingContext dc, Size size, double pixelsPerDip, bool forExport)
        {
            if (!forExport)
            {
                _dotHits.Clear();
                _boxHits.Clear();
                _slotHits.Clear();
                _boxColumns.Clear();
            }

            var card = new Rect(0, 0, size.Width, size.Height);
            var cardBrush = Frozen(new SolidColorBrush(CardColor));
            if (forExport)
                dc.DrawRectangle(cardBrush, null, card);
            else
                dc.DrawRoundedRectangle(cardBrush, null, card, 6, 6);

            if (_categories.Count == 0)
            {
                var message = MakeText(_emptyMessage, 12, CountTextColor, pixelsPerDip, maxWidth: Math.Max(40, size.Width - 40));
                message.TextAlignment = TextAlignment.Center;
                dc.DrawText(message, new Point(20, (size.Height - message.Height) / 2));
                return;
            }

            var layout = ComputeLayout(size, pixelsPerDip);
            bool muting = !forExport && _highlightedKey != null && _categories.Any(c => c.Key == _highlightedKey);

            if (!forExport)
            {
                _plotArea = layout.Plot;
                // Above and below the plot (the n= row and the labels) a box's whole column shows its summary,
                // which a box crowded with dots would otherwise only show between them.
                foreach (var box in _boxes)
                {
                    double width = box.Category.Boxes.Count > 1 ? layout.ClusterStep : layout.SlotWidth;
                    _boxColumns.Add((new Rect(BoxCenterX(layout, box) - width / 2, 0, width, size.Height), box));
                }
            }

            DrawGridAndTicks(dc, layout);
            DrawYAxisTitle(dc, layout);
            DrawHeaderAndLegend(dc, layout);

            foreach (var box in _boxes)
            {
                bool muted = muting && box.Category.Key != _highlightedKey;
                if (muted) dc.PushOpacity(MutedOpacity);
                DrawBox(dc, layout, box, recordHits: !forExport);
                DrawCount(dc, layout, box);
                if (muted) dc.Pop();
            }

            for (int c = 0; c < _categories.Count; c++)
            {
                var category = _categories[c];
                bool muted = muting && category.Key != _highlightedKey;
                bool highlighted = muting && category.Key == _highlightedKey;
                if (muted) dc.PushOpacity(0.5);
                DrawCategoryLabel(dc, layout, c, category, highlighted);
                if (muted) dc.Pop();

                if (!forExport)
                    _slotHits.Add((new Rect(layout.Plot.Left + c * layout.SlotWidth, layout.Plot.Top,
                        layout.SlotWidth, size.Height - layout.Plot.Top), category));
            }

            if (!forExport)
                DrawHover(dc, layout);
        }

        private void DrawGridAndTicks(DrawingContext dc, Layout layout)
        {
            var gridPen = Frozen(new Pen(Frozen(new SolidColorBrush(GridColor)), 1));
            foreach (var tick in layout.Ticks)
            {
                double y = Math.Round(YToScreen(layout, tick)) + 0.5;
                dc.DrawLine(gridPen, new Point(layout.Plot.Left, y), new Point(layout.Plot.Right, y));

                var text = MakeText(FormatTick(layout, tick), 11, TickTextColor, layout.PixelsPerDip);
                dc.DrawText(text, new Point(layout.Plot.Left - 8 - text.Width, y - text.Height / 2));
            }
        }

        private void DrawYAxisTitle(DrawingContext dc, Layout layout)
        {
            if (_yAxisTitle.Length == 0)
                return;
            var text = MakeText(_yAxisTitle, 12.5, TitleTextColor, layout.PixelsPerDip, maxWidth: layout.Plot.Height + 30);
            double cx = PadLeft + YTitleBand / 2 - 2;
            double cy = layout.Plot.Top + layout.Plot.Height / 2;
            dc.PushTransform(new RotateTransform(-90, cx, cy));
            dc.DrawText(text, new Point(cx - text.Width / 2, cy - text.Height / 2));
            dc.Pop();
        }

        private void DrawHeaderAndLegend(DrawingContext dc, Layout layout)
        {
            double legendWidth = 0;
            var legendItems = new List<(FormattedText Text, BoxPlotLegendEntry Entry)>();
            foreach (var entry in _legend)
            {
                var text = MakeText(entry.Label, 11, HeaderTextColor, layout.PixelsPerDip, maxWidth: 140);
                legendItems.Add((text, entry));
                legendWidth += 12 + text.Width + 12;
            }

            double right = layout.Width - PadRight;
            if (legendItems.Count > 0)
            {
                double x = right - legendWidth + 12;
                double y = layout.HeaderBaseline + 9;
                foreach (var (text, entry) in legendItems)
                {
                    var color = entry.IsRing ? ExcludedColor : entry.Color;
                    if (entry.IsRing)
                        dc.DrawEllipse(null, Frozen(new Pen(Frozen(new SolidColorBrush(color)), 1.3)), new Point(x, y), 3.6, 3.6);
                    else
                        dc.DrawEllipse(Frozen(new SolidColorBrush(color)), null, new Point(x, y), 4.2, 4.2);
                    dc.DrawText(text, new Point(x + 8, y - text.Height / 2));
                    x += 12 + text.Width + 12;
                }
            }

            if (_groupTitle.Length > 0)
            {
                var title = MakeText(_groupTitle, 12, HeaderTextColor, layout.PixelsPerDip, semiBold: true);
                double center = layout.Plot.Left + layout.Plot.Width / 2;
                double legendLeft = right - legendWidth;
                // Keep the header centred over the boxes unless the legend needs that space.
                if (legendItems.Count > 0 && center + title.Width / 2 > legendLeft - 12)
                    center = Math.Max(layout.Plot.Left + title.Width / 2, (layout.Plot.Left + legendLeft - 12) / 2);
                dc.DrawText(title, new Point(center - title.Width / 2, layout.HeaderBaseline));
            }
        }

        private void DrawBox(DrawingContext dc, Layout layout, BoxGeometry box, bool recordHits)
        {
            double cx = BoxCenterX(layout, box);
            double half = layout.BoxWidth / 2;
            var color = box.Box.Color;

            // A few runs sit on top of the box, as in the reference figure. A box of many runs would disappear under
            // its dots, so there the outline, whiskers and median go on top, the thin lines over a halo of the card
            // color (the outline needs none, and with one it reads as a doubled edge).
            bool structureOnTop = box.Box.Runs.Count > 40;

            if (box.Stats is { } stats)
            {
                double yQ1 = YToScreen(layout, stats.Q1), yQ3 = YToScreen(layout, stats.Q3);
                var fill = Frozen(new SolidColorBrush(Color.FromArgb((byte)(Math.Clamp(box.Box.FillOpacity, 0, 1) * 255), color.R, color.G, color.B)));
                dc.DrawRectangle(fill, null, new Rect(cx - half, yQ3, layout.BoxWidth, Math.Max(0.5, yQ1 - yQ3)));
                if (!structureOnTop)
                    DrawBoxStructure(dc, layout, cx, half, color, stats, halo: false);

                if (recordHits)
                {
                    double top = YToScreen(layout, stats.HighWhisker), bottom = YToScreen(layout, stats.LowWhisker);
                    _boxHits.Add((new Rect(cx - half - 4, top - 4, layout.BoxWidth + 8, Math.Max(8, bottom - top + 8)), box));
                }
            }

            // Included runs are solid dots; excluded runs are grey rings, drawn last so a dense box does not hide them.
            var ringPen = Frozen(new Pen(Frozen(new SolidColorBrush(ExcludedColor)), 1.3));
            bool fade = layout.DotOpacity < 1.0;
            if (fade) dc.PushOpacity(layout.DotOpacity);
            foreach (bool excludedPass in new[] { false, true })
            {
                for (int i = 0; i < box.Box.Runs.Count; i++)
                {
                    var run = box.Box.Runs[i];
                    if (run.IsExcluded != excludedPass)
                        continue;
                    var center = new Point(cx + box.Jitter[i] * layout.JitterHalfWidth, YToScreen(layout, run.Value));
                    if (run.IsExcluded)
                        dc.DrawEllipse(null, ringPen, center, layout.DotRadius - 0.4, layout.DotRadius - 0.4);
                    else
                        dc.DrawEllipse(Brush(run.Color), null, center, layout.DotRadius, layout.DotRadius);
                    if (recordHits)
                        _dotHits.Add((center, layout.DotRadius, box, run));
                }
            }
            if (fade) dc.Pop();

            if (structureOnTop && box.Stats is { } topStats)
                DrawBoxStructure(dc, layout, cx, half, color, topStats, halo: true);
        }

        /// <summary>Whiskers, IQR outline and median of one box.</summary>
        private static void DrawBoxStructure(DrawingContext dc, Layout layout, double cx, double half, Color color, BoxStats stats, bool halo)
        {
            double yQ1 = YToScreen(layout, stats.Q1), yQ3 = YToScreen(layout, stats.Q3);
            double yMedian = YToScreen(layout, stats.Median);
            double yHigh = YToScreen(layout, stats.HighWhisker), yLow = YToScreen(layout, stats.LowWhisker);
            var boxRect = new Rect(cx - half, yQ3, half * 2, Math.Max(0.5, yQ1 - yQ3));

            if (halo)
            {
                var haloBrush = Frozen(new SolidColorBrush(Color.FromArgb(0xE6, CardColor.R, CardColor.G, CardColor.B)));
                dc.DrawLine(FlatPen(haloBrush, WhiskerThickness + 2), new Point(cx, yQ3), new Point(cx, yHigh));
                dc.DrawLine(FlatPen(haloBrush, WhiskerThickness + 2), new Point(cx, yQ1), new Point(cx, yLow));
                dc.DrawLine(FlatPen(haloBrush, MedianThickness + 2), new Point(cx - half - 1, yMedian), new Point(cx + half + 1, yMedian));
            }

            var stroke = Frozen(new SolidColorBrush(color));
            dc.DrawLine(FlatPen(stroke, WhiskerThickness), new Point(cx, yQ3), new Point(cx, yHigh));
            dc.DrawLine(FlatPen(stroke, WhiskerThickness), new Point(cx, yQ1), new Point(cx, yLow));
            dc.DrawRectangle(null, Frozen(new Pen(stroke, BoxOutlineThickness)), boxRect);
            dc.DrawLine(FlatPen(stroke, MedianThickness), new Point(cx - half, yMedian), new Point(cx + half, yMedian));
        }

        private static Pen FlatPen(Brush brush, double thickness) =>
            Frozen(new Pen(brush, thickness) { StartLineCap = PenLineCap.Flat, EndLineCap = PenLineCap.Flat });

        private void DrawCount(DrawingContext dc, Layout layout, BoxGeometry box)
        {
            int n = box.Stats?.N ?? 0;
            double room = box.Category.Boxes.Count > 1 ? layout.ClusterStep : layout.SlotWidth;
            bool narrow = room < 44;
            var text = MakeText(narrow ? n.ToString(CultureInfo.CurrentCulture) : $"n={n}", narrow ? 10 : 11, CountTextColor, layout.PixelsPerDip);
            double cx = BoxCenterX(layout, box);
            dc.DrawText(text, new Point(cx - text.Width / 2, layout.CountTop));
        }

        private void DrawCategoryLabel(DrawingContext dc, Layout layout, int index, BoxPlotCategory category, bool highlighted)
        {
            double cx = layout.Plot.Left + (index + 0.5) * layout.SlotWidth;
            // A zero-width space after separators lets names like Plate_08_HeLa wrap onto two lines.
            string label = category.Label.Replace("_", "_​");
            var color = highlighted ? Color.FromRgb(0x11, 0x11, 0x11) : LabelTextColor;

            if (!layout.RotateLabels)
            {
                double maxWidth = Math.Max(20, layout.SlotWidth - 8);
                var text = MakeText(label, 11.5, color, layout.PixelsPerDip, maxWidth: maxWidth, maxLines: 2, semiBold: highlighted);
                text.TextAlignment = TextAlignment.Center;
                // With a MaxTextWidth the lines are centred inside that width, which starts at the origin.
                dc.DrawText(text, new Point(cx - maxWidth / 2, layout.LabelTop));
            }
            else
            {
                var text = MakeText(category.Label, 11, color, layout.PixelsPerDip, maxWidth: 110, maxLines: 1, semiBold: highlighted);
                double angle = -35;
                dc.PushTransform(new RotateTransform(angle, cx, layout.LabelTop + 2));
                dc.DrawText(text, new Point(cx - text.Width, layout.LabelTop + 2 - text.Height / 2));
                dc.Pop();
            }
        }

        private void DrawHover(DrawingContext dc, Layout layout)
        {
            if (_hoverRun != null)
            {
                var hit = _dotHits.FirstOrDefault(h => ReferenceEquals(h.Run, _hoverRun));
                if (hit.Run == null)
                    return;
                var outline = Frozen(new Pen(Frozen(new SolidColorBrush(Color.FromRgb(0x1F, 0x29, 0x37))), 1.4));
                dc.DrawEllipse(Brushes.White, null, hit.Center, hit.Radius + 2.6, hit.Radius + 2.6);
                if (_hoverRun.IsExcluded)
                    dc.DrawEllipse(null, outline, hit.Center, hit.Radius + 0.6, hit.Radius + 0.6);
                else
                    dc.DrawEllipse(Brush(_hoverRun.Color), outline, hit.Center, hit.Radius + 0.6, hit.Radius + 0.6);

                string value = $"{_hoverRun.Value.ToString("N0", CultureInfo.CurrentCulture)} {_valueUnit}".Trim();
                var parts = new List<string> { value };
                if (_hoverRun.Detail.Length > 0) parts.Add(_hoverRun.Detail);
                if (_hoverRun.IsExcluded) parts.Add("excluded");
                DrawCallout(dc, layout, hit.Center, hit.Radius, Shorten(_hoverRun.RunName, 64), string.Join("  ·  ", parts));
            }
            else if (_hoverBox?.Stats is { } stats)
            {
                double cx = BoxCenterX(layout, _hoverBox);
                var anchor = new Point(cx + layout.BoxWidth / 2, YToScreen(layout, stats.Median));
                string title = _hoverBox.Category.Boxes.Count > 1
                    ? $"{_hoverBox.Category.Label} · {_hoverBox.Box.Label}"
                    : _hoverBox.Category.Label;
                string body = $"n={stats.N}  ·  median {stats.Median:N0}  ·  IQR {stats.Q1:N0}–{stats.Q3:N0}";
                DrawCallout(dc, layout, anchor, 0, Shorten(title, 64), body);
            }
        }

        private void DrawCallout(DrawingContext dc, Layout layout, Point anchor, double radius, string title, string body)
        {
            var titleText = MakeText(title, 11.5, Colors.White, layout.PixelsPerDip, semiBold: true);
            var bodyText = MakeText(body, 11, Color.FromRgb(0xD9, 0xDE, 0xE5), layout.PixelsPerDip);
            double width = Math.Max(titleText.Width, bodyText.Width) + 18;
            double height = titleText.Height + bodyText.Height + 12;

            double x = anchor.X + radius + 10;
            if (x + width > layout.Width - 4)
                x = anchor.X - radius - 10 - width;
            x = Math.Max(4, x);
            double y = Math.Clamp(anchor.Y - height / 2, 4, Math.Max(4, layout.Height - height - 4));

            var rect = new Rect(x, y, width, height);
            dc.DrawRoundedRectangle(Frozen(new SolidColorBrush(Color.FromArgb(0xF0, 0x1F, 0x29, 0x37))), null, rect, 5, 5);
            dc.DrawText(titleText, new Point(x + 9, y + 6));
            dc.DrawText(bodyText, new Point(x + 9, y + 6 + titleText.Height));
        }

        private static string Shorten(string text, int max) =>
            text.Length <= max ? text : text.Substring(0, max / 2 - 1) + "…" + text.Substring(text.Length - (max / 2 - 1));

        // ------------------------------------------------------------------------------------------------------------
        // Mouse
        // ------------------------------------------------------------------------------------------------------------

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            var p = e.GetPosition(this);

            BoxPlotRun? run = null;
            BoxGeometry? box = null;
            double best = double.MaxValue;
            foreach (var hit in _dotHits)
            {
                double d = (hit.Center - p).Length;
                if (d <= hit.Radius + 3 && d < best)
                {
                    best = d;
                    run = hit.Run;
                }
            }
            if (run == null)
                box = _boxHits.FirstOrDefault(h => h.Area.Contains(p)).Box;
            if (run == null && box == null && (p.Y < _plotArea.Top || p.Y > _plotArea.Bottom))
                box = _boxColumns.FirstOrDefault(h => h.Column.Contains(p)).Box;

            Cursor = run != null || box != null || _slotHits.Any(h => h.Area.Contains(p)) ? Cursors.Hand : null;
            if (!ReferenceEquals(run, _hoverRun) || !ReferenceEquals(box, _hoverBox))
            {
                _hoverRun = run;
                _hoverBox = box;
                InvalidateVisual();
            }
        }

        protected override void OnMouseLeave(MouseEventArgs e)
        {
            base.OnMouseLeave(e);
            if (_hoverRun != null || _hoverBox != null)
            {
                _hoverRun = null;
                _hoverBox = null;
                InvalidateVisual();
            }
        }

        protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
        {
            base.OnMouseLeftButtonUp(e);
            if (_hoverRun != null)
            {
                RunClicked?.Invoke(this, _hoverRun.RunName);
                return;
            }
            var p = e.GetPosition(this);
            var slot = _slotHits.FirstOrDefault(h => h.Area.Contains(p)).Category;
            if (slot != null)
                CategoryClicked?.Invoke(this, slot.Key);
        }

        // ------------------------------------------------------------------------------------------------------------
        // Helpers
        // ------------------------------------------------------------------------------------------------------------

        private readonly Dictionary<Color, SolidColorBrush> _brushCache = new();

        private SolidColorBrush Brush(Color color)
        {
            if (!_brushCache.TryGetValue(color, out var brush))
            {
                brush = Frozen(new SolidColorBrush(color));
                _brushCache[color] = brush;
            }
            return brush;
        }

        private static T Frozen<T>(T freezable) where T : Freezable
        {
            freezable.Freeze();
            return freezable;
        }

        private static FormattedText MakeText(string text, double size, Color color, double pixelsPerDip,
            double maxWidth = 0, int maxLines = 1, bool semiBold = false)
        {
            var formatted = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                semiBold ? UiFaceSemiBold : UiFace, size, Frozen(new SolidColorBrush(color)), pixelsPerDip);
            if (maxWidth > 0)
            {
                formatted.MaxTextWidth = maxWidth;
                formatted.MaxLineCount = maxLines;
                formatted.Trimming = TextTrimming.CharacterEllipsis;
            }
            return formatted;
        }
    }
}
