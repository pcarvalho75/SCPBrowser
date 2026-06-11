using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using SCPBrowser.Models;
using SCPBrowser.Services;

namespace SCPBrowser.Controls
{
    /// <summary>
    /// Project Browser tab for defining marker-only cell classes (name + marker genes) and classifying cells on
    /// markers alone — no transcriptomic/proteomic reference profile required.
    /// </summary>
    public partial class MarkerClassesControl : UserControl
    {
        private string? _dbPath;
        private MarkerClassificationService? _service;

        /// <summary>Raised after the user classifies cells, so the host (ProjectBrowser2 → MainWindow) can unlock and
        /// colour the main-screen "Cell Type" view from the freshly-saved marker results.</summary>
        public event EventHandler? CellsClassified;

        private MarkerClassDiagnostics? _diag; // null = stale, recompute when the Diagnostics tab is shown
        private MarkerClass? _selectedClass;

        /// <summary>Row VM for the per-marker enable/disable checkbox list in the Classes editor.</summary>
        private sealed class MarkerToggle
        {
            public string Gene { get; set; } = "";
            public bool Enabled { get; set; } = true;
        }

        public MarkerClassesControl()
        {
            InitializeComponent();
        }

        public async Task LoadAsync(string dbPath)
        {
            _dbPath = dbPath;
            _service = new MarkerClassificationService(dbPath);
            await ReloadClassesAsync();
            MinMarkersBox.Value = await _service.GetMinMarkersAsync();
            ExprTopPctBox.Value = await _service.GetExprTopPctAsync();
            ExprGatePctBox.Value = await _service.GetExprGatePctAsync();
        }

        private async Task ReloadClassesAsync()
        {
            if (_service == null) return;
            ClassList.ItemsSource = await _service.GetClassesAsync();
        }

        private void ClassList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (ClassList.SelectedItem is MarkerClass c)
            {
                _selectedClass = c;
                NameBox.Text = c.Name;
                MarkersBox.Text = string.Join(Environment.NewLine, c.Genes);
                MarkerToggles.ItemsSource = c.Genes
                    .Select(g => new MarkerToggle { Gene = g, Enabled = !c.DisabledGenes.Contains(g) })
                    .ToList();
            }
            else
            {
                _selectedClass = null;
                MarkerToggles.ItemsSource = null;
            }
        }

        /// <summary>Toggling the checkbox beside a class name enables/disables the whole class.</summary>
        private async void ClassEnabled_Click(object sender, RoutedEventArgs e)
        {
            if (_service != null && sender is CheckBox cb && cb.DataContext is MarkerClass c)
                await _service.SetClassEnabledAsync(c.Name, cb.IsChecked == true);
        }

        /// <summary>Toggling a marker checkbox enables/disables that marker within the selected class.</summary>
        private async void MarkerEnabled_Click(object sender, RoutedEventArgs e)
        {
            if (_service == null || _selectedClass == null || sender is not CheckBox cb || cb.DataContext is not MarkerToggle mt) return;
            bool on = cb.IsChecked == true;
            await _service.SetMarkerEnabledAsync(_selectedClass.Name, mt.Gene, on);
            // Keep the in-memory class in sync so the toggle survives reselection without a reload.
            if (on) _selectedClass.DisabledGenes.Remove(mt.Gene);
            else _selectedClass.DisabledGenes.Add(mt.Gene);
        }

        private void AddClass_Click(object sender, RoutedEventArgs e)
        {
            ClassList.SelectedItem = null;
            NameBox.Text = "";
            MarkersBox.Text = "";
            NameBox.Focus();
        }

        private async void SaveClass_Click(object sender, RoutedEventArgs e)
        {
            if (_service == null) return;
            string name = NameBox.Text.Trim();
            if (string.IsNullOrEmpty(name)) { Warn("Enter a class name."); return; }
            var genes = ParseGenes(MarkersBox.Text);
            if (genes.Count == 0) { Warn("Enter at least one marker gene."); return; }

            await _service.SaveClassAsync(new MarkerClass { Name = name, Genes = genes });
            await ReloadClassesAsync();
            StatusText.Text = $"Saved \"{name}\" ({genes.Count} markers).";
        }

        private async void DeleteClass_Click(object sender, RoutedEventArgs e)
        {
            if (_service == null || ClassList.SelectedItem is not MarkerClass c) return;
            if (MessageBox.Show($"Delete class \"{c.Name}\"?", "Delete", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            await _service.DeleteClassAsync(c.Name);
            NameBox.Text = ""; MarkersBox.Text = "";
            await ReloadClassesAsync();
        }

        private async void MinMarkers_ValueChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
        {
            if (_service != null && MinMarkersBox.Value is int v)
                await _service.SetMinMarkersAsync(v);
        }

        private async void ExprTopPct_ValueChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
        {
            if (_service != null && ExprTopPctBox.Value is int v)
                await _service.SetExprTopPctAsync(v);
        }

        private async void ExprGatePct_ValueChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
        {
            if (_service != null && ExprGatePctBox.Value is int v)
                await _service.SetExprGatePctAsync(v);
        }

        private async void Classify_Click(object sender, RoutedEventArgs e)
        {
            if (_service == null) return;
            ClassifyButton.IsEnabled = false;
            StatusText.Text = "Classifying...";
            try
            {
                var progress = new Progress<string>(m => StatusText.Text = m);
                var summary = await _service.ClassifyAndStoreAsync(progress);
                if (summary.Count == 0)
                {
                    SummaryText.Text = "No classes defined, or no cells/proteins to classify.";
                    StatusText.Text = "";
                    return;
                }
                int total = summary.Values.Sum();
                var parts = summary.OrderByDescending(kv => kv.Value).Select(kv => $"{kv.Key}={kv.Value}");
                SummaryText.Text = $"Classified {total} cells:  {string.Join(",  ", parts)}\n" +
                                   "Saved. The main-screen \"Cell Type\" colour mode is now unlocked and showing these assignments.";
                StatusText.Text = "Done.";
                _diag = null; // diagnostics are now stale
                CellsClassified?.Invoke(this, EventArgs.Empty); // unlock + colour the main-screen scatter
            }
            catch (Exception ex)
            {
                StatusText.Text = "Error: " + ex.Message;
            }
            finally { ClassifyButton.IsEnabled = true; }
        }

        // ---------------------------------------------------------------- diagnostics tab

        private async void InnerTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!ReferenceEquals(e.OriginalSource, InnerTabs)) return; // ignore bubbled inner-control selection changes
            if (ReferenceEquals(InnerTabs.SelectedItem, DiagnosticsTab) && _service != null && _diag == null)
                await RefreshDiagnosticsAsync();
        }

        private async void RefreshDiag_Click(object sender, RoutedEventArgs e) => await RefreshDiagnosticsAsync();

        private async void Reclassify_Click(object sender, RoutedEventArgs e)
        {
            if (_service == null) return;
            ReclassifyButton.IsEnabled = false;
            DiagStatusText.Text = "Re-classifying...";
            try
            {
                // Class/marker enable state is persisted live on the Classes tab; just re-score with the current set.
                var progress = new Progress<string>(s => DiagStatusText.Text = s);
                await _service.ClassifyAndStoreAsync(progress);
                await RefreshDiagnosticsAsync();
                CellsClassified?.Invoke(this, EventArgs.Empty); // recolour the main-screen scatter
            }
            catch (Exception ex) { DiagStatusText.Text = "Error: " + ex.Message; }
            finally { ReclassifyButton.IsEnabled = true; }
        }

        private async Task RefreshDiagnosticsAsync()
        {
            if (_service == null) return;
            DiagStatusText.Text = "Computing diagnostics...";
            try
            {
                var progress = new Progress<string>(s => DiagStatusText.Text = s);
                _diag = await _service.ComputeDiagnosticsAsync(progress);
                ClassBars.ItemsSource = _diag.ClassCounts;
                PrevalenceBars.ItemsSource = _diag.Markers.Take(12).ToList();
                ContributionBars.ItemsSource = _diag.Markers.OrderByDescending(m => m.AssignedHits).Take(12).ToList();
                MarkerGrid.ItemsSource = _diag.Markers;
                DiagStatusText.Text = _diag.TotalCells == 0
                    ? "No cells or classes to analyze yet — define classes and Classify first."
                    : $"{_diag.TotalCells} cells · {_diag.ClassCounts.Count} classes · {_diag.Markers.Count} markers. " +
                      "Untick abundant markers/classes on the Classes tab, then Re-classify.";
            }
            catch (Exception ex) { DiagStatusText.Text = "Error: " + ex.Message; }
        }

        private static void Warn(string msg) => MessageBox.Show(msg, "Marker class", MessageBoxButton.OK, MessageBoxImage.Warning);

        private static List<string> ParseGenes(string text) =>
            text.Split(new[] { '\n', '\r', ',', ';', ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(s => s.Trim())
                .Where(s => s.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
    }
}
