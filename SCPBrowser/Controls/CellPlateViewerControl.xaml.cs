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
    /// Phase 3 viewer: a position grid of isolated-cell thumbnails (laid out by x_pos/y_pos), a gating scatter
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

        private double _zoom = 1.0; // tile-size multiplier from the Size slider (1.0 = fill width)

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

                int linked = _cells.Count(c => c.RawFileId != null);
                InfoText.Text = $"{_cells.Count} cells · {linked} linked to runs · plate \"{run.PlateName}\"";

                RenderGrid();
                RenderScatter();
                ClearDetail();
            }
            catch (Exception ex) { InfoText.Text = "Error loading run: " + ex.Message; }
        }

        private void RenderGrid()
        {
            GridCanvas.Children.Clear();
            _tiles.Clear();
            if (_cells.Count == 0) { GridCanvas.Width = GridCanvas.Height = 0; return; }

            var unlinkedBrush = new SolidColorBrush(Color.FromRgb(0xcb, 0xd5, 0xe1));
            foreach (var cell in _cells)
            {
                var border = new Border
                {
                    BorderBrush = cell.RawFileId != null ? Brushes.SeaGreen : unlinkedBrush,
                    BorderThickness = new Thickness(cell.RawFileId != null ? 2 : 1),
                    Background = Brushes.Black,
                    Tag = cell,
                    Cursor = Cursors.Hand,
                    ToolTip = $"drop {cell.DropNo}  Ø {cell.Diameter:F1}µm"
                };
                if (_thumbs.TryGetValue(cell.CellId, out var tb))
                    border.Child = new Image { Source = BytesToImage(tb), Stretch = Stretch.UniformToFill };

                border.MouseLeftButtonUp += (s, _) => { if ((s as Border)?.Tag is IsolatedCell c) SelectCell(c); };
                GridCanvas.Children.Add(border);
                _tiles[cell.CellId] = border;
            }

            RelayoutGrid();
        }

        /// <summary>
        /// Sizes/positions the existing tiles to fill the viewport width (tiles grow as the window/panel widens),
        /// keeping the cell-image aspect ratio. Called on run load and on every resize.
        /// </summary>
        private void RelayoutGrid()
        {
            if (_cells.Count == 0) return;
            int maxX = _cells.Max(c => c.XPos ?? 1);
            int maxY = _cells.Max(c => c.YPos ?? 1);

            double avail = GridScroll.ViewportWidth > 0 ? GridScroll.ViewportWidth : GridScroll.ActualWidth;
            if (avail <= 0) avail = 600;
            // Square tiles. Baseline fills the width (maxX columns); the Size slider scales from there, and the
            // baseline is recomputed on every resize so the grid grows/shrinks with the window.
            double baseTile = (avail - 14) / maxX;
            double tile = Math.Clamp(baseTile * _zoom, 24, 360);

            GridCanvas.Width = maxX * tile + 6;
            GridCanvas.Height = maxY * tile + 6;
            foreach (var cell in _cells)
            {
                if (!_tiles.TryGetValue(cell.CellId, out var b)) continue;
                b.Width = tile - 2;
                b.Height = tile - 2;
                Canvas.SetLeft(b, ((cell.XPos ?? 1) - 1) * tile);
                Canvas.SetTop(b, ((cell.YPos ?? 1) - 1) * tile);
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
            // Update tile highlight (restore previous first).
            if (_selected != null && _tiles.TryGetValue(_selected.CellId, out var prev))
            {
                prev.BorderBrush = _selected.RawFileId != null ? Brushes.SeaGreen : new SolidColorBrush(Color.FromRgb(0xcb, 0xd5, 0xe1));
                prev.BorderThickness = new Thickness(_selected.RawFileId != null ? 2 : 1);
            }
            _selected = cell;
            if (_tiles.TryGetValue(cell.CellId, out var tile))
            {
                tile.BorderBrush = Brushes.OrangeRed;
                tile.BorderThickness = new Thickness(3);
            }

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
                $"Isolated:    {cell.IsolatedAt}\n" +
                $"DIA-NN run:  {link}";

            // Highlight the selected cell on the scatter (using whichever axes are active).
            HighlightSelectedOnScatter();

            // Lazily load the full-resolution images.
            try
            {
                var t = await _query!.GetCellImageAsync(cell.CellId, "Trans");
                var b = await _query!.GetCellImageAsync(cell.CellId, "Blue");
                TransImage.Source = t != null ? BytesToImage(t) : null;
                BlueImage.Source = b != null ? BytesToImage(b) : null;
            }
            catch { /* image load failure shouldn't break selection */ }
        }

        private void ClearDetail()
        {
            _selected = null;
            DetailHint.Visibility = Visibility.Visible;
            DetailText.Text = "";
            TransImage.Source = null;
            BlueImage.Source = null;
        }

        private void ReconcileButton_Click(object sender, RoutedEventArgs e)
        {
            if (_projectDbPath == null || _currentRun == null) return;
            var dlg = new ReconcileCellsDialog { Owner = Window.GetWindow(this) };
            dlg.Initialize(_projectDbPath, _currentRun.CellenOneRunId);
            dlg.ShowDialog();
            _ = LoadRunAsync(_currentRun); // refresh link state
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
