using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ScottPlot;
using SCPBrowser.Models;
using SCPBrowser.Services;
// Disambiguate the simple names that ScottPlot also defines; ScottPlot.Color is used fully-qualified.
using Color = System.Windows.Media.Color;
using Image = System.Windows.Controls.Image;

namespace SCPBrowser.Controls
{
    /// <summary>
    /// Phase 3 viewer: a grid of isolated-cell thumbnails (one tile per drop, in reading order), a gating scatter
    /// (diameter vs intensity), and a detail panel. Selecting a cell in either the grid or the scatter shows its
    /// full Trans/Blue images, morphology, and DIA-NN link state. Heavy image blobs are loaded lazily on click.
    /// </summary>
    public partial class CellPlateViewerControl : UserControl
    {
        private string? _projectDbPath;
        private CellenOneQueryService? _query;
        private List<IsolatedCell> _cells = new();
        private Dictionary<int, byte[]> _thumbs = new();
        private Dictionary<int, RawFileRef> _rawById = new();
        private readonly Dictionary<int, Border> _tiles = new();
        private CellRunSummary? _currentRun;
        private IsolatedCell? _selected;
        private ScottPlot.Plottables.Scatter? _highlightDot;
        private BitmapSource? _lastTrans, _lastBlue; // raw detail images, kept so zone stripes toggle without a DB reload

        private double _zoom = 1.0; // tile-size multiplier from the Size slider (1.0 = fill width)

        // Columns the grid fills the viewport with at zoom 1.0. The layout can no longer take its column count from
        // the data (see RelayoutGrid) so it needs a fixed baseline; the Size slider scales tiles away from it.
        private const int BaselineColumns = 12;

        private int _cols = 1;      // columns in the current layout — arrow-key Up/Down steps by this
        private int _shown;         // tiles currently drawn (the filter's survivors), for the honest "showing N of M"

        // User-selectable scatter axes.
        private static readonly (string Name, Func<IsolatedCell, double?> Get)[] Metrics = new (string, Func<IsolatedCell, double?>)[]
        {
            ("Diameter (µm)", c => c.Diameter),
            ("Elongation",    c => c.Elongation),
            ("Circularity",   c => c.Circularity),
            ("Intensity",     c => c.Intensity),
        };
        private Func<IsolatedCell, double?> _xGet = c => c.Diameter;
        private Func<IsolatedCell, double?> _yGet = c => c.Intensity;

        public CellPlateViewerControl()
        {
            InitializeComponent();
            GatingChart.MouseLeftButtonDown += GatingChart_MouseLeftButtonDown;
            GridScroll.SizeChanged += (s, e) => RelayoutGrid();
            SizeChanged += (s, e) => RelayoutGrid();
            Focusable = true;
            PreviewKeyDown += OnPreviewKeyDown;

            var names = Metrics.Select(m => m.Name).ToList();
            XAxisCombo.ItemsSource = names;
            YAxisCombo.ItemsSource = names.ToList();
            XAxisCombo.SelectedIndex = 0; // Diameter
            YAxisCombo.SelectedIndex = 3; // Intensity
        }

        /// <summary>Loads the runs-with-cells for a project and selects the first.</summary>
        public async void Initialize(string projectDbPath)
        {
            _projectDbPath = projectDbPath;
            _query = new CellenOneQueryService(projectDbPath);
            try
            {
                // Self-migrate so review_status/review_note (and earlier cellenONE columns) exist even on a DB
                // that hasn't been re-imported since these features landed.
                await new ProjectDatabaseService(projectDbPath).EnsureCellenOneTablesExistAsync();
                var runs = await _query.GetRunsWithCellsAsync();
                RunCombo.ItemsSource = runs;
                if (runs.Count > 0) RunCombo.SelectedIndex = 0;
                else InfoText.Text = "No cellenONE runs with cells in this project. Import one via Import ▸ Plate Metadata.";
            }
            catch (Exception ex) { InfoText.Text = "Error loading runs: " + ex.Message; }
        }

        private async void RunCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (RunCombo.SelectedItem is not CellRunSummary run) return;
            _currentRun = run;
            await LoadRunAsync(run);
        }

        private async Task LoadRunAsync(CellRunSummary run)
        {
            if (_query == null) return;
            try
            {
                _cells = await _query.GetCellsAsync(run.CellenOneRunId);
                _thumbs = await _query.GetThumbnailsAsync(run.CellenOneRunId, "Trans");
                var raws = await _query.GetRawFilesForPlateAsync(run.PlateId);
                _rawById = raws.ToDictionary(r => r.RawFileId, r => r);

                ComputeMorphStats(); // per-run robust baseline used by the morphology-outlier auto-flag

                int linked = _cells.Count(c => c.RawFileId != null);
                EjBoundBox.Value = run.EjectionBound;
                SedBoundBox.Value = run.SedimentationBound;
                int outOfZone = (run.EjectionBound != null || run.SedimentationBound != null) ? _cells.Count(c => !WithinBounds(c)) : 0;
                InfoText.Text = $"{_cells.Count} cells · {linked} linked · plate \"{run.PlateName}\""
                              + (outOfZone > 0 ? $" · {outOfZone} outside ejection zone" : "");

                RenderGrid();
                RenderScatter();
                ClearDetail();
                RefreshFilterAndCounts();
            }
            catch (Exception ex) { InfoText.Text = "Error loading run: " + ex.Message; }
        }

        private void RenderGrid()
        {
            GridCanvas.Children.Clear();
            _tiles.Clear();
            if (_cells.Count == 0) { GridCanvas.Width = GridCanvas.Height = 0; return; }

            foreach (var cell in _cells)
            {
                var border = new Border
                {
                    Background = Brushes.Black,
                    Tag = cell,
                    Cursor = Cursors.Hand,
                    ToolTip = BuildTileTooltip(cell),
                    // Seed from the active filter so a hidden tile never shows at a stale slot before the first
                    // RefreshFilterAndCounts; the layout only assigns positions to the tiles it draws.
                    Visibility = PassesFilter(cell) ? Visibility.Visible : Visibility.Collapsed
                };

                var content = new Grid();
                if (_thumbs.TryGetValue(cell.CellId, out var tb))
                {
                    var img = new Image { Source = BytesToImage(tb), Stretch = Stretch.UniformToFill };
                    RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
                    content.Children.Add(img);
                }
                border.Child = content;
                StyleTile(cell, border); // border colour + doublet/review badges from current state

                border.MouseLeftButtonUp += (s, _) => { if ((s as Border)?.Tag is IsolatedCell c) SelectCell(c); };
                GridCanvas.Children.Add(border);
                _tiles[cell.CellId] = border;
            }

            RelayoutGrid();
        }

        /// <summary>
        /// The cells as they are drawn: the current filter's survivors in a stable reading order. Layout, the
        /// "showing N of M" count and keyboard navigation all key off this one list, so what is on screen and what
        /// the arrow keys can reach are by construction the same set.
        /// </summary>
        private List<IsolatedCell> DisplayOrder()
            => _cells.Where(PassesFilter)
                     .OrderBy(c => c.Field ?? 0)
                     .ThenBy(c => c.XPos ?? 0)
                     .ThenBy(c => c.YPos ?? 0)
                     .ThenBy(c => c.DropNo)
                     .ToList();

        /// <summary>
        /// Sizes/positions the existing tiles to fill the viewport width (tiles grow as the window/panel widens),
        /// keeping the cell-image aspect ratio. Called on run load, on every resize, and whenever the filter changes.
        /// </summary>
        private void RelayoutGrid()
        {
            if (_cells.Count == 0) { _shown = 0; return; }

            double avail = GridScroll.ViewportWidth > 0 ? GridScroll.ViewportWidth : GridScroll.ActualWidth;
            if (avail <= 0) avail = 600;
            // (XPos,YPos) is the position of the imaging FIELD, not of the cell: many cells share one pair, so using
            // it as the layout key stacked opaque tiles exactly on top of each other and made all but the last of
            // each stack invisible and un-clickable (208 cells -> 14 reachable tiles on a real run). Lay out in
            // reading order over the unique drop instead, and derive the column count from the tile size rather than
            // from the data. Baseline fills the width; the Size slider scales from there, recomputed on every resize.
            double baseTile = (avail - 14) / BaselineColumns;
            double tile = Math.Clamp(baseTile * _zoom, 24, 360);
            _cols = Math.Max(1, (int)((avail - 14) / tile));

            var ordered = DisplayOrder();
            _shown = ordered.Count;
            int rows = Math.Max(1, (int)Math.Ceiling(ordered.Count / (double)_cols));

            GridCanvas.Width = _cols * tile + 6;
            GridCanvas.Height = rows * tile + 6;
            for (int i = 0; i < ordered.Count; i++)
            {
                if (!_tiles.TryGetValue(ordered[i].CellId, out var b)) continue;
                b.Width = tile - 2;
                b.Height = tile - 2;
                Canvas.SetLeft(b, (i % _cols) * tile);
                Canvas.SetTop(b, (i / _cols) * tile);
            }
        }

        private void ZoomSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            _zoom = e.NewValue;
            RelayoutGrid();
        }

        private void Axis_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (!IsLoaded) return; // combos are seeded in the ctor; first real render happens on run load
            RenderScatter();
        }

        private void RenderScatter()
        {
            int xi = XAxisCombo.SelectedIndex >= 0 ? XAxisCombo.SelectedIndex : 0;
            int yi = YAxisCombo.SelectedIndex >= 0 ? YAxisCombo.SelectedIndex : System.Math.Min(3, Metrics.Length - 1);
            var xm = Metrics[xi];
            var ym = Metrics[yi];
            _xGet = xm.Get;
            _yGet = ym.Get;

            GatingChart.Plot.Clear();
            _highlightDot = null;
            var pts = _cells.Where(c => xm.Get(c).HasValue && ym.Get(c).HasValue).ToList();
            if (pts.Count > 0)
            {
                var xs = pts.Select(c => xm.Get(c)!.Value).ToArray();
                var ys = pts.Select(c => ym.Get(c)!.Value).ToArray();
                var sc = GatingChart.Plot.Add.Scatter(xs, ys);
                sc.LineWidth = 0;
                sc.MarkerSize = 5;
                sc.Color = ScottPlot.Color.FromHex("#2563eb").WithAlpha(0.5);
            }
            GatingChart.Plot.Axes.Bottom.Label.Text = xm.Name;
            GatingChart.Plot.Axes.Left.Label.Text = ym.Name;
            GatingChart.Plot.Axes.AutoScale();
            GatingChart.Refresh();

            // Keep the current selection highlighted on the new axes.
            HighlightSelectedOnScatter();
        }

        private void HighlightSelectedOnScatter()
        {
            if (_highlightDot != null) { GatingChart.Plot.Remove(_highlightDot); _highlightDot = null; }
            if (_selected == null) return;
            var x = _xGet(_selected);
            var y = _yGet(_selected);
            if (x.HasValue && y.HasValue)
            {
                _highlightDot = GatingChart.Plot.Add.Scatter(new[] { x.Value }, new[] { y.Value });
                _highlightDot.LineWidth = 0;
                _highlightDot.MarkerSize = 13;
                _highlightDot.Color = ScottPlot.Color.FromHex("#ea580c");
            }
            GatingChart.Refresh();
        }

        private void GatingChart_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            var pts = _cells.Where(c => _xGet(c).HasValue && _yGet(c).HasValue).ToList();
            if (pts.Count == 0) return;

            var p = e.GetPosition(GatingChart);
            Coordinates coords = GatingChart.Plot.GetCoordinates(new Pixel((float)p.X, (float)p.Y));

            double xr = Math.Max(1e-9, pts.Max(c => _xGet(c)!.Value) - pts.Min(c => _xGet(c)!.Value));
            double yr = Math.Max(1e-9, pts.Max(c => _yGet(c)!.Value) - pts.Min(c => _yGet(c)!.Value));

            IsolatedCell? best = null;
            double bestDist = double.MaxValue;
            foreach (var c in pts)
            {
                double dx = (_xGet(c)!.Value - coords.X) / xr;
                double dy = (_yGet(c)!.Value - coords.Y) / yr;
                double d = dx * dx + dy * dy;
                if (d < bestDist) { bestDist = d; best = c; }
            }
            if (best != null) SelectCell(best);
        }

        private async void SelectCell(IsolatedCell cell)
        {
            // Update tile highlight (restore the previous tile's normal border first).
            if (_selected != null && _tiles.TryGetValue(_selected.CellId, out var prev))
                ApplyNormalBorder(_selected, prev);
            _selected = cell;
            if (_tiles.TryGetValue(cell.CellId, out var tile))
            {
                tile.BorderBrush = Brushes.OrangeRed;
                tile.BorderThickness = new Thickness(3);
                tile.BringIntoView();
            }
            Focus(); // keep keyboard focus so arrow keys navigate after a click

            DetailHint.Visibility = Visibility.Collapsed;
            string link = cell.RawFileId != null && _rawById.TryGetValue(cell.RawFileId.Value, out var rf)
                ? $"{rf.RawFileName}  ({cell.LinkMethod}, conf {cell.LinkConfidence:F2})"
                : "(not linked)";
            DetailText.Text =
                $"Drop #:      {cell.DropNo}\n" +
                $"Well:        {cell.TargetWell}\n" +
                $"Field/Pos:   F{cell.Field}  X{cell.XPos} Y{cell.YPos}\n" +
                $"Diameter:    {cell.Diameter:F2} µm\n" +
                $"Elongation:  {cell.Elongation:F2}\n" +
                $"Circularity: {cell.Circularity:F2}\n" +
                $"Intensity:   {cell.Intensity:F1}\n" +
                $"Objects:     {(cell.NObjects?.ToString() ?? "?")} (instrument)\n" +
                $"Cells (img): {(cell.BlobCount?.ToString() ?? "?")}{(DoubletCount(cell) > 1 ? "  ⚠ DOUBLET" : "")}\n" +
                $"Isolated:    {cell.IsolatedAt}\n" +
                $"DIA-NN run:  {link}";

            ReviewPanel.Visibility = Visibility.Visible;
            UpdateReviewDetail(cell);

            // Highlight the selected cell on the scatter (using whichever axes are active).
            HighlightSelectedOnScatter();

            // Lazily load the full-resolution images.
            try
            {
                var t = await _query!.GetCellImageAsync(cell.CellId, "Trans");
                var b = await _query!.GetCellImageAsync(cell.CellId, "Blue");
                if (!ReferenceEquals(_selected, cell)) return; // superseded by a newer selection (rapid arrow nav)
                _lastTrans = t != null ? BytesToImage(t) : null;
                _lastBlue = b != null ? BytesToImage(b) : null;
                TransImage.Source = _lastTrans != null ? ApplyZoneStripes(_lastTrans) : null;
                BlueImage.Source = _lastBlue != null ? ApplyZoneStripes(_lastBlue) : null;

                var feats = await _query!.GetCellFeaturesAsync(cell.CellId);
                if (!ReferenceEquals(_selected, cell)) return;
                PhenotypeText.Text = feats.Count == 0
                    ? "(none yet — use ↻ Reanalyze to compute phenotype features)"
                    : string.Join("\n", feats.Select(ft => $"{ft.Feature,-34} {ft.Value,9:0.###}"));
            }
            catch { /* image / feature load failure shouldn't break selection */ }
        }

        private void OnPreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (_selected == null) return;
            // Don't hijack keys while the run picker, size slider, or the note box has focus.
            if (e.OriginalSource is System.Windows.Controls.Primitives.Selector || e.OriginalSource is Slider || e.OriginalSource is TextBox) return;
            switch (e.Key)
            {
                case Key.Left: MoveSelection(-1, 0); e.Handled = true; break;
                case Key.Right: MoveSelection(1, 0); e.Handled = true; break;
                case Key.Up: MoveSelection(0, -1); e.Handled = true; break;
                case Key.Down: MoveSelection(0, 1); e.Handled = true; break;
                case Key.F: SetSelectedReview("flag"); e.Handled = true; break;
                case Key.K: SetSelectedReview("keep"); e.Handled = true; break;
                case Key.D: SetSelectedReview("discard"); e.Handled = true; break;
                case Key.Back: SetSelectedReview(null); e.Handled = true; break;
            }
        }

        /// <summary>
        /// Moves the selection through the on-screen order: Left/Right step one tile, Up/Down one row. Navigating the
        /// display order (rather than XPos/YPos deltas) is what makes every cell reachable — with the old position
        /// comparison, co-located cells had a zero offset and were skipped outright, and a run whose YPos is constant
        /// had no Up/Down candidate at all.
        /// </summary>
        private void MoveSelection(int dx, int dy)
        {
            var ordered = DisplayOrder();
            if (ordered.Count == 0) return;
            var sel = _selected;
            if (sel == null) { SelectCell(ordered[0]); return; }

            int idx = ordered.FindIndex(c => c.CellId == sel.CellId);
            if (idx < 0) { SelectCell(ordered[0]); return; } // the filter hid the selection — re-enter at the start

            int next = idx + (dx != 0 ? dx : dy * Math.Max(1, _cols));
            if (next < 0 || next >= ordered.Count) return;
            SelectCell(ordered[next]);
        }

        /// <summary>Doublet count = the larger of the instrument's object count and the image blob count.</summary>
        private static int DoubletCount(IsolatedCell cell) => Math.Max(cell.NObjects ?? 0, cell.BlobCount ?? 0);

        /// <summary>Restores a tile to its non-selected appearance (border + doublet/review badges) from current state.</summary>
        private void ApplyNormalBorder(IsolatedCell cell, Border tile) => StyleTile(cell, tile);

        private void ClearDetail()
        {
            _selected = null;
            DetailHint.Visibility = Visibility.Visible;
            DetailText.Text = "";
            PhenotypeText.Text = "";
            TransImage.Source = null;
            BlueImage.Source = null;
            _lastTrans = null;
            _lastBlue = null;
            ReviewPanel.Visibility = Visibility.Collapsed;
        }

        private void ReconcileButton_Click(object sender, RoutedEventArgs e)
        {
            if (_projectDbPath == null || _currentRun == null) return;
            var dlg = new ReconcileCellsDialog { Owner = Window.GetWindow(this) };
            dlg.Initialize(_projectDbPath, _currentRun.CellenOneRunId);
            dlg.ShowDialog();
            _ = LoadRunAsync(_currentRun); // refresh link state
        }

        /// <summary>Resets this run's automatic doublet flags and re-runs the image-based detector in place (no re-import; manual keep/discard kept).</summary>
        private async void ReanalyzeButton_Click(object sender, RoutedEventArgs e)
        {
            if (_projectDbPath == null || _currentRun == null || _query == null) return;
            if (MessageBox.Show(
                    "Re-run image analysis for this run?\n\nThis clears the automatic doublet flags (image blob counts) and recomputes them — plus the CellProfiler-style phenotype features — from the cell images already in the database. Your manual keep/discard decisions are kept.",
                    "Reanalyze run", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
                return;

            var run = _currentRun;
            run.EjectionBound = EjBoundBox.Value;            // reanalyze with exactly the bounds shown on screen
            run.SedimentationBound = SedBoundBox.Value;
            ReanalyzeButton.IsEnabled = false;
            ReconcileButton.IsEnabled = false;
            try
            {
                await _query.SetRunBoundsAsync(run.CellenOneRunId, run.EjectionBound, run.SedimentationBound);
                var progress = new Progress<string>(m => InfoText.Text = m);
                int flagged = await Task.Run(async () =>
                {
                    await _query.ResetDoubletFlagsAsync(run.CellenOneRunId);          // reset this run's flags
                    int fl = await new CellImageAnalysisService(_projectDbPath)
                        .AnalyzeRunAsync(run.CellenOneRunId, progress);               // recompute blob_count
                    await new CellPhenotypeService(_projectDbPath)
                        .AnalyzeRunAsync(run.CellenOneRunId, progress);               // recompute phenotype features
                    return fl;
                });
                await LoadRunAsync(run); // reload so tiles/counts reflect the new flags
                InfoText.Text = $"Reanalyzed plate \"{run.PlateName}\": {flagged} doublet(s) flagged; phenotype features updated.";
            }
            catch (Exception ex) { InfoText.Text = "Reanalyze failed: " + ex.Message; }
            finally { ReanalyzeButton.IsEnabled = true; ReconcileButton.IsEnabled = true; }
        }

        // ----- QC review: auto-flag (doublet + morphology outlier) + manual keep/discard disposition -----

        private static readonly SolidColorBrush DoubletBrush = new(Color.FromRgb(0xdc, 0x26, 0x26));
        private static readonly SolidColorBrush UnlinkedBrush = new(Color.FromRgb(0xcb, 0xd5, 0xe1));

        // Robust-z threshold (MAD-scaled) for the morphology-outlier auto-flag. Conservative; tune if it over/under-flags.
        private const double ReviewOutlierK = 3.5;

        // Per-run robust baseline (median + 1.4826·MAD) for each morphology metric, computed on run load.
        private double _diMed, _diScale, _elMed, _elScale, _ciMed, _ciScale;

        private enum ReviewState { Normal, NeedsReview, Kept, Discarded }

        /// <summary>Effective review state: an explicit keep/discard/flag wins; otherwise a cell auto-flagged (doublet or morphology outlier) is "needs review".</summary>
        private ReviewState StateOf(IsolatedCell c)
        {
            switch (c.ReviewStatus?.Trim().ToLowerInvariant())
            {
                case "discard": return ReviewState.Discarded;
                case "keep": return ReviewState.Kept;
                case "flag": return ReviewState.NeedsReview;
            }
            return AutoFlagReasons(c).Count > 0 ? ReviewState.NeedsReview : ReviewState.Normal;
        }

        /// <summary>Why the heuristics would flag this cell for review (doublet and/or morphology outliers); empty = nothing automatic.</summary>
        private List<string> AutoFlagReasons(IsolatedCell c)
        {
            var reasons = new List<string>();
            int dc = DoubletCount(c);
            if (dc > 1) reasons.Add($"doublet ({dc} cells)");
            if (c.Diameter is double d && _diScale > 0 && Math.Abs(d - _diMed) > ReviewOutlierK * _diScale)
                reasons.Add(d > _diMed ? "large diameter" : "small diameter");
            if (c.Elongation is double el && _elScale > 0 && (el - _elMed) > ReviewOutlierK * _elScale)
                reasons.Add("high elongation");
            if (c.Circularity is double ci && _ciScale > 0 && (_ciMed - ci) > ReviewOutlierK * _ciScale)
                reasons.Add("low circularity");
            return reasons;
        }

        private void ComputeMorphStats()
        {
            (_diMed, _diScale) = RobustStats(_cells.Where(c => c.Diameter.HasValue).Select(c => c.Diameter!.Value));
            (_elMed, _elScale) = RobustStats(_cells.Where(c => c.Elongation.HasValue).Select(c => c.Elongation!.Value));
            (_ciMed, _ciScale) = RobustStats(_cells.Where(c => c.Circularity.HasValue).Select(c => c.Circularity!.Value));
        }

        /// <summary>Returns (median, 1.4826·MAD). Scale is 0 when undefined (no spread / too few points), which disables that metric's outlier test.</summary>
        private static (double med, double scale) RobustStats(IEnumerable<double> values)
        {
            var a = values.ToArray();
            if (a.Length < 8) return (a.Length > 0 ? Median(a) : double.NaN, 0); // too few to judge outliers robustly
            double med = Median(a);
            var dev = a.Select(v => Math.Abs(v - med)).ToArray();
            return (med, 1.4826 * Median(dev));
        }

        private static double Median(double[] xs)
        {
            var s = (double[])xs.Clone();
            Array.Sort(s);
            int n = s.Length;
            if (n == 0) return double.NaN;
            return n % 2 == 1 ? s[n / 2] : 0.5 * (s[n / 2 - 1] + s[n / 2]);
        }

        /// <summary>Applies the non-selected appearance to a tile: border colour (doublet/link), discard dimming, and the doublet + review badges.</summary>
        private void StyleTile(IsolatedCell cell, Border border)
        {
            var state = StateOf(cell);
            int dcount = DoubletCount(cell);
            bool doublet = dcount > 1;

            border.BorderBrush = doublet ? DoubletBrush : (cell.RawFileId != null ? Brushes.SeaGreen : UnlinkedBrush);
            border.BorderThickness = new Thickness(doublet || cell.RawFileId != null ? 2 : 1);
            border.Opacity = state == ReviewState.Discarded ? 0.5 : 1.0;

            if (border.Child is Grid g)
            {
                for (int i = g.Children.Count - 1; i >= 0; i--)
                    if (g.Children[i] is not Image) g.Children.RemoveAt(i);
                if (doublet) g.Children.Add(MakeDoubletBadge(dcount));
                var rb = MakeReviewBadge(state);
                if (rb != null) g.Children.Add(rb);
            }
        }

        /// <summary>Top-left corner badge encoding the review disposition (amber flag / green keep / grey discard); null when normal.</summary>
        private static Border? MakeReviewBadge(ReviewState s)
        {
            string glyph; Color bg;
            switch (s)
            {
                case ReviewState.NeedsReview: glyph = "⚑"; bg = Color.FromRgb(0xf5, 0x9e, 0x0b); break;
                case ReviewState.Kept:        glyph = "✓"; bg = Color.FromRgb(0x16, 0xa3, 0x4a); break;
                case ReviewState.Discarded:   glyph = "✗"; bg = Color.FromRgb(0x6b, 0x72, 0x80); break;
                default: return null;
            }
            return new Border
            {
                Background = new SolidColorBrush(bg),
                CornerRadius = new CornerRadius(7),
                Padding = new Thickness(3, 0, 3, 0),
                HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
                VerticalAlignment = System.Windows.VerticalAlignment.Top,
                Margin = new Thickness(1, 1, 0, 0),
                Child = new TextBlock { Text = glyph, Foreground = Brushes.White, FontSize = 9, FontWeight = FontWeights.Bold }
            };
        }

        private string BuildTileTooltip(IsolatedCell cell)
        {
            string t = $"drop {cell.DropNo}  Ø {cell.Diameter:F1}µm";
            int dc = DoubletCount(cell);
            if (dc > 1) t += $"  ⚠ {dc} cells";
            switch (StateOf(cell))
            {
                case ReviewState.Kept: t += "  ✓ kept"; break;
                case ReviewState.Discarded: t += "  ✗ discarded"; break;
                case ReviewState.NeedsReview:
                    var r = AutoFlagReasons(cell);
                    t += r.Count > 0 ? "  ⚑ review: " + string.Join(", ", r) : "  ⚑ flagged";
                    break;
            }
            return t;
        }

        private void UpdateReviewDetail(IsolatedCell cell)
        {
            var reasons = AutoFlagReasons(cell);
            string auto = reasons.Count > 0 ? "   (auto-flag: " + string.Join(", ", reasons) + ")" : "";
            ReviewStateText.Text = StateOf(cell) switch
            {
                ReviewState.Discarded => "✗ DISCARDED" + auto,
                ReviewState.Kept => "✓ kept" + auto,
                ReviewState.NeedsReview => (string.Equals(cell.ReviewStatus, "flag", StringComparison.OrdinalIgnoreCase)
                                            ? "⚑ flagged for review" : "⚑ needs review") + auto,
                _ => "— not flagged"
            };
            ReviewNoteBox.Text = cell.ReviewNote ?? "";
        }

        /// <summary>Sets (and persists) the selected cell's disposition, then refreshes its tile, the detail panel, and the counts/filter.</summary>
        private async void SetSelectedReview(string? status)
        {
            if (_selected == null || _query == null) return;
            var cell = _selected;
            cell.ReviewStatus = status;
            string? note = string.IsNullOrWhiteSpace(ReviewNoteBox.Text) ? null : ReviewNoteBox.Text.Trim();
            cell.ReviewNote = note;

            try { await _query.SetReviewStatusAsync(cell.CellId, status, note); }
            catch (Exception ex) { InfoText.Text = "Review save failed: " + ex.Message; }

            if (_tiles.TryGetValue(cell.CellId, out var tile))
            {
                StyleTile(cell, tile);
                tile.ToolTip = BuildTileTooltip(cell);
                if (ReferenceEquals(_selected, cell)) // keep the selection highlight on top of the restyle
                {
                    tile.BorderBrush = Brushes.OrangeRed;
                    tile.BorderThickness = new Thickness(3);
                }
            }
            UpdateReviewDetail(cell);
            RefreshFilterAndCounts();
        }

        private void FlagBtn_Click(object sender, RoutedEventArgs e) => SetSelectedReview("flag");
        private void KeepBtn_Click(object sender, RoutedEventArgs e) => SetSelectedReview("keep");
        private void DiscardBtn_Click(object sender, RoutedEventArgs e) => SetSelectedReview("discard");
        private void ClearReviewBtn_Click(object sender, RoutedEventArgs e) => SetSelectedReview(null);

        private async void ReviewNoteBox_LostFocus(object sender, RoutedEventArgs e)
        {
            if (_selected == null || _query == null) return;
            string? note = string.IsNullOrWhiteSpace(ReviewNoteBox.Text) ? null : ReviewNoteBox.Text.Trim();
            if (note == _selected.ReviewNote) return; // unchanged
            _selected.ReviewNote = note;
            try { await _query.SetReviewStatusAsync(_selected.CellId, _selected.ReviewStatus, note); }
            catch (Exception ex) { InfoText.Text = "Note save failed: " + ex.Message; }
        }

        /// <summary>
        /// Turns this run's discards into run exclusions. review_status on its own is an annotation — no filtering,
        /// classification or export path reads it — so without this step a cell the operator discarded stays in the
        /// UMAP, stays classified and stays in the PLP export. excluded_runs is the mechanism the rest of the app
        /// honours, so that is what a discard has to become. Explicit, confirmed, and undoable from the excluded-runs
        /// grid; nothing here changes a number on its own.
        /// </summary>
        private async void ApplyDiscardsButton_Click(object sender, RoutedEventArgs e)
        {
            if (_projectDbPath == null || _query == null || _currentRun == null) return;

            ApplyDiscardsButton.IsEnabled = false;
            try
            {
                var toExclude = await _query.GetDiscardedRawFileIdsAsync(_currentRun.CellenOneRunId);
                if (toExclude.Count == 0)
                {
                    MessageBox.Show(
                        "No discarded cell in this run is reconciled to a DIA-NN run, so there is nothing to exclude.\n\n" +
                        "A discard can only reach the analysis through the run it is linked to — use \"Reconcile ↔ runs...\" first.",
                        "Apply discards", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                var parquet = new ParquetDataService(_projectDbPath);
                await new ProjectDatabaseService(_projectDbPath).EnsureExcludedRunsTableExistsAsync();
                var already = await parquet.GetExcludedRunIdsAsync();
                int fresh = toExclude.Count(id => !already.Contains(id));

                if (MessageBox.Show(
                        $"Exclude {toExclude.Count} DIA-NN run(s) linked to cells you discarded?\n\n" +
                        $"{fresh} would be newly excluded ({toExclude.Count - fresh} already excluded).\n\n" +
                        "Keep/discard is recorded as an annotation only; excluding the run is what actually removes it " +
                        "from the analysis and the PLP export. You can undo this from the excluded-runs grid, and the " +
                        "change takes effect the next time the project data is reloaded.",
                        "Apply discards as run exclusions", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
                    return;

                foreach (int id in toExclude)
                    await parquet.ExcludeRunAsync(id, "cellenONE QC: discarded");

                InfoText.Text = $"Excluded {fresh} run(s) from discarded cells ({toExclude.Count - fresh} were already excluded). Reload the project data to apply.";
            }
            catch (Exception ex) { InfoText.Text = "Apply discards failed: " + ex.Message; }
            finally { RefreshFilterAndCounts(); } // restores the button's enabled state from the current counts
        }

        private void ReviewFilterCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!IsLoaded) return;
            RefreshFilterAndCounts();
        }

        /// <summary>Updates the review counts and shows/hides tiles per the active "Show:" filter, then re-packs the grid.</summary>
        private void RefreshFilterAndCounts()
        {
            int needs = 0, kept = 0, disc = 0, discLinked = 0;
            foreach (var c in _cells)
            {
                switch (StateOf(c))
                {
                    case ReviewState.NeedsReview: needs++; break;
                    case ReviewState.Kept: kept++; break;
                    case ReviewState.Discarded:
                        disc++;
                        if (c.RawFileId != null) discLinked++;
                        break;
                }
                if (_tiles.TryGetValue(c.CellId, out var t))
                    t.Visibility = PassesFilter(c) ? Visibility.Visible : Visibility.Collapsed;
            }
            RelayoutGrid(); // hidden tiles leave no hole: the grid packs only what it draws (and sets _shown)

            ReviewCountText.Text = $"⚑ {needs} review · ✓ {kept} kept · ✗ {disc} discarded"
                                 + (_shown < _cells.Count ? $" · showing {_shown} of {_cells.Count}" : "");

            // The disposition is an annotation until it is applied, so say how many discards could actually be
            // acted on rather than implying the review already changed the analysis.
            ApplyDiscardsButton.Content = discLinked > 0 ? $"Apply {discLinked} discards ▸ exclusions" : "Apply discards ▸ exclusions";
            ApplyDiscardsButton.IsEnabled = discLinked > 0;
        }

        private bool PassesFilter(IsolatedCell c)
        {
            if (WithinBoundsCheck?.IsChecked == true && !WithinBounds(c)) return false; // ejection-zone filter ANDs with review
            int fi = ReviewFilterCombo?.SelectedIndex ?? 0;
            if (fi <= 0) return true; // All review states
            return fi switch
            {
                1 => StateOf(c) == ReviewState.NeedsReview,
                2 => StateOf(c) == ReviewState.Kept,
                3 => StateOf(c) == ReviewState.Discarded,
                _ => true
            };
        }

        // ----- ejection / sedimentation zone boundaries -----

        /// <summary>A cell is "within bounds" if its detected X (along-channel) position lies between the sedimentation and ejection boundaries. Cells with unknown position, or runs with no bounds, are not excluded.</summary>
        private bool WithinBounds(IsolatedCell c)
        {
            int? ej = _currentRun?.EjectionBound, sed = _currentRun?.SedimentationBound;
            int? cutoff = ej.HasValue && sed.HasValue ? Math.Max(ej.Value, sed.Value) : (ej ?? sed);
            if (cutoff == null || c.ImageX == null) return true; // a cell is valid if it sits at/before the ejection line
            return c.ImageX.Value <= cutoff.Value;
        }

        /// <summary>Returns the image with vertical ejection (red) / sedimentation (green) boundary lines drawn at their camera-pixel X when "Show zones" is on. Pixel-accurate (drawn into the bitmap, so it scales with the Uniform-stretched display).</summary>
        private BitmapSource ApplyZoneStripes(BitmapSource src)
        {
            if (ShowZonesCheck.IsChecked != true) return src;
            int? ej = _currentRun?.EjectionBound, sed = _currentRun?.SedimentationBound;
            if (ej == null && sed == null) return src;

            int w = src.PixelWidth, h = src.PixelHeight;
            var dv = new DrawingVisual();
            using (var dc = dv.RenderOpen())
            {
                dc.DrawImage(src, new System.Windows.Rect(0, 0, w, h));
                double lw = Math.Max(1.0, w / 320.0);
                if (sed is int s && s >= 0 && s <= w)
                    dc.DrawLine(new Pen(Brushes.LimeGreen, lw), new System.Windows.Point(s, 0), new System.Windows.Point(s, h));
                if (ej is int e && e >= 0 && e <= w)
                    dc.DrawLine(new Pen(Brushes.Red, lw), new System.Windows.Point(e, 0), new System.Windows.Point(e, h));
            }
            var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(dv);
            rtb.Freeze();
            return rtb;
        }

        private void RefreshDetailImages()
        {
            if (_lastTrans != null) TransImage.Source = ApplyZoneStripes(_lastTrans);
            if (_lastBlue != null) BlueImage.Source = ApplyZoneStripes(_lastBlue);
        }

        private void ZoneDisplay_Changed(object sender, RoutedEventArgs e)
        {
            if (!IsLoaded) return;
            RefreshDetailImages();
        }

        private void ZoneFilter_Changed(object sender, RoutedEventArgs e)
        {
            if (!IsLoaded) return;
            RefreshFilterAndCounts();
        }

        private async void SaveBounds_Click(object sender, RoutedEventArgs e)
        {
            if (_query == null || _currentRun == null) return;
            int? ej = EjBoundBox.Value, sed = SedBoundBox.Value;
            _currentRun.EjectionBound = ej;
            _currentRun.SedimentationBound = sed;
            try
            {
                await _query.SetRunBoundsAsync(_currentRun.CellenOneRunId, ej, sed);
                InfoText.Text = $"Saved zones for \"{_currentRun.PlateName}\": ejection {ej?.ToString() ?? "—"}, sedimentation {sed?.ToString() ?? "—"} px.";
            }
            catch (Exception ex) { InfoText.Text = "Save zones failed: " + ex.Message; }
            RefreshDetailImages();
            RefreshFilterAndCounts();
        }

        private static Border MakeDoubletBadge(int count)
        {
            return new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(0xdc, 0x26, 0x26)),
                CornerRadius = new CornerRadius(7),
                Padding = new Thickness(3, 0, 3, 0),
                HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
                VerticalAlignment = System.Windows.VerticalAlignment.Top,
                Margin = new Thickness(0, 1, 1, 0),
                Child = new TextBlock { Text = count.ToString(), Foreground = Brushes.White, FontSize = 9, FontWeight = FontWeights.Bold }
            };
        }

        private static BitmapImage BytesToImage(byte[] bytes)
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.StreamSource = new MemoryStream(bytes);
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
    }
}
