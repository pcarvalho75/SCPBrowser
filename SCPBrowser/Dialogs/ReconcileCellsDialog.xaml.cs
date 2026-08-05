using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using SCPBrowser.Models;
using SCPBrowser.Services;

namespace SCPBrowser
{
    /// <summary>
    /// Phase 4: matches isolated cells to DIA-NN raw files. Offers auto-suggestion (by name or by order), lets the
    /// user override any row manually, and persists the chosen links (raw_file_id + method + confidence). Applying
    /// clears the run's existing links in the same transaction, so rows set back to "(none)" become unlinked
    /// without ever leaving the run unlinked if the write fails half-way.
    /// </summary>
    public partial class ReconcileCellsDialog : Window
    {
        private const string None = "(none)";

        private string? _dbPath;
        private CellenOneQueryService? _query;
        private CellRunReconciliationService? _recon;
        private List<IsolatedCell> _cells = new();
        private List<RawFileRef> _raws = new();
        private Dictionary<string, int> _idByName = new();

        public ObservableCollection<ReconRow> Rows { get; } = new();
        public ObservableCollection<string> RawFileNames { get; } = new();

        public ReconcileCellsDialog()
        {
            InitializeComponent();
            DataContext = this;
            MatchGrid.ItemsSource = Rows;
        }

        /// <summary>Loads runs and (optionally) preselects one.</summary>
        public async void Initialize(string dbPath, int? preselectRunId = null)
        {
            // async void on a dialog entry point: an unhandled failure here (locked project.db, missing tables)
            // would otherwise escape to the dispatcher instead of being reported in the dialog that caused it.
            try
            {
                _dbPath = dbPath;
                _query = new CellenOneQueryService(dbPath);
                _recon = new CellRunReconciliationService(dbPath);
                StrategyCombo.SelectedIndex = 0;

                var runs = await _query.GetRunsWithCellsAsync();
                RunCombo.ItemsSource = runs;
                if (runs.Count == 0)
                {
                    // An empty grid with no message reads as a broken dialog; say what is missing and how to get it.
                    InfoText.Text = "No cellenONE runs in this project - use Import -> Import Plate Metadata... first.";
                    SetActionsEnabled(false);
                    return;
                }

                int idx = preselectRunId.HasValue ? runs.FindIndex(r => r.CellenOneRunId == preselectRunId.Value) : 0;
                RunCombo.SelectedIndex = idx < 0 ? 0 : idx;
            }
            catch (Exception ex)
            {
                ReportError("Could not load the cellenONE runs in this project", ex);
                SetActionsEnabled(false);
            }
        }

        private async void RunCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            try
            {
                await LoadRunAsync();
            }
            catch (Exception ex)
            {
                ReportError("Could not load the selected run", ex);
                SetActionsEnabled(false);
            }
        }

        private async Task LoadRunAsync()
        {
            if (RunCombo.SelectedItem is not CellRunSummary run || _query == null) return;

            _cells = await _query.GetCellsAsync(run.CellenOneRunId);
            _raws = await _query.GetRawFilesForPlateAsync(run.PlateId);
            _idByName = _raws.Where(r => r.RawFileName != null)
                             .GroupBy(r => r.RawFileName!)
                             .ToDictionary(g => g.Key, g => g.First().RawFileId);
            var nameById = _raws.ToDictionary(r => r.RawFileId, r => r.RawFileName ?? "");

            RawFileNames.Clear();
            RawFileNames.Add(None);
            foreach (var r in _raws.Where(r => r.RawFileName != null)) RawFileNames.Add(r.RawFileName!);

            Rows.Clear();
            foreach (var c in _cells)
            {
                Rows.Add(new ReconRow
                {
                    CellId = c.CellId,
                    DropNo = c.DropNo,
                    Well = c.TargetWell ?? "",
                    DiameterText = c.Diameter?.ToString("F1", CultureInfo.InvariantCulture) ?? "",
                    RawFileName = c.RawFileId != null && nameById.TryGetValue(c.RawFileId.Value, out var n) ? n : None,
                    Method = c.LinkMethod ?? "",
                    ConfidenceText = c.LinkConfidence?.ToString("F2", CultureInfo.InvariantCulture) ?? ""
                });
            }

            if (_raws.Count == 0)
            {
                // Nothing to link to: the plate exists but its DIA-NN report has not been imported yet.
                InfoText.Text = $"Plate \"{run.PlateName}\" has no DIA-NN runs yet - import its report before reconciling ({_cells.Count} cells waiting).";
                SetActionsEnabled(false);
                return;
            }

            SetActionsEnabled(true);
            InfoText.Text = $"{_cells.Count} cells · {_raws.Count} raw files on plate \"{run.PlateName}\"";
        }

        private void SetActionsEnabled(bool enabled)
        {
            SuggestButton.IsEnabled = enabled;
            ApplyButton.IsEnabled = enabled;
            // Clearing stays available whenever the run has cells: it is the only way to undo links left behind
            // by an earlier reconcile, and that is exactly the case when the plate's raw files are gone.
            ClearAllButton.IsEnabled = enabled || _cells.Count > 0;
        }

        /// <summary>Reports a failure in the dialog itself rather than letting it escape an async void handler.</summary>
        private void ReportError(string what, Exception ex)
        {
            InfoText.Text = $"{what}: {ex.Message}";
            MessageBox.Show(
                $"{what}.\n\n{ex.GetType().Name}: {ex.Message}",
                "Reconcile Cells",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }

        private void Suggest_Click(object sender, RoutedEventArgs e)
        {
            if (_cells.Count == 0) return;
            bool byName = StrategyCombo.SelectedIndex == 0;
            var suggestions = byName
                ? CellRunReconciliationService.SuggestByName(_cells, _raws, null)
                : CellRunReconciliationService.SuggestByOrdinal(_cells, _raws);

            var byCell = suggestions.ToDictionary(s => s.CellId);
            foreach (var row in Rows)
            {
                if (!byCell.TryGetValue(row.CellId, out var s)) continue;
                row.RawFileName = s.RawFileName ?? None;
                row.Method = s.Method;
                row.ConfidenceText = s.Confidence > 0 ? s.Confidence.ToString("F2", CultureInfo.InvariantCulture) : "";
            }

            int matched = suggestions.Count(s => s.RawFileId != null);
            InfoText.Text = $"Suggested {matched} matches ({(byName ? "name" : "order")}). Review/edit any row, then Apply.";
        }

        private async void Apply_Click(object sender, RoutedEventArgs e)
        {
            if (_recon == null || RunCombo.SelectedItem is not CellRunSummary run) return;

            try
            {
                var links = Rows.Select(row => new CellLinkSuggestion
                {
                    CellId = row.CellId,
                    RawFileId = row.RawFileName != None && _idByName.TryGetValue(row.RawFileName, out var id) ? id : (int?)null,
                    Confidence = double.TryParse(row.ConfidenceText, NumberStyles.Any, CultureInfo.InvariantCulture, out var cf) ? cf : 0,
                    Method = string.IsNullOrEmpty(row.Method) ? "manual" : row.Method
                }).Where(l => l.RawFileId != null).ToList();

                // Clear and re-link in ONE transaction: rows set back to "(none)" must end up unlinked, but a
                // failure between the two must never leave the run with no links at all.
                int n = await _recon.ApplyLinksAsync(links, run.CellenOneRunId);
                await LoadRunAsync();
                InfoText.Text = $"Applied {n} links.";
            }
            catch (Exception ex)
            {
                ReportError("Could not apply the links", ex);
            }
        }

        private async void ClearAll_Click(object sender, RoutedEventArgs e)
        {
            if (_recon == null || RunCombo.SelectedItem is not CellRunSummary run) return;

            try
            {
                int linked = await _recon.CountLinkedCellsAsync(run.CellenOneRunId);
                if (linked == 0)
                {
                    InfoText.Text = "This run has no links to clear.";
                    return;
                }

                // Clearing is immediate and unrecoverable - name the run and the count before doing it.
                var answer = MessageBox.Show(
                    $"Remove all {linked} cell to raw-file links from run \"{run.RunUid ?? run.CellenOneRunId.ToString(CultureInfo.InvariantCulture)}\" on plate \"{run.PlateName}\"?\n\n" +
                    "This cannot be undone; the run would have to be reconciled again from scratch.",
                    "Clear All Links",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning,
                    MessageBoxResult.No);

                if (answer != MessageBoxResult.Yes) return;

                await _recon.ClearLinksAsync(run.CellenOneRunId);
                await LoadRunAsync();
                InfoText.Text = $"Cleared {linked} links for this run.";
            }
            catch (Exception ex)
            {
                ReportError("Could not clear the links for this run", ex);
            }
        }

        private void Close_Click(object sender, RoutedEventArgs e) => DialogResult = true;
    }

    /// <summary>Editable grid row for the reconcile dialog.</summary>
    public class ReconRow : INotifyPropertyChanged
    {
        public int CellId { get; set; }
        public int DropNo { get; set; }
        public string Well { get; set; } = "";
        public string DiameterText { get; set; } = "";

        private string _rawFileName = "(none)";
        public string RawFileName { get => _rawFileName; set { _rawFileName = value; On(nameof(RawFileName)); } }

        private string _method = "";
        public string Method { get => _method; set { _method = value; On(nameof(Method)); } }

        private string _confidenceText = "";
        public string ConfidenceText { get => _confidenceText; set { _confidenceText = value; On(nameof(ConfidenceText)); } }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void On(string n) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    }
}
