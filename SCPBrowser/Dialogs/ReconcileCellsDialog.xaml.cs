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
    /// first clears the run's existing links so rows set back to "(none)" become unlinked.
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
            _dbPath = dbPath;
            _query = new CellenOneQueryService(dbPath);
            _recon = new CellRunReconciliationService(dbPath);
            StrategyCombo.SelectedIndex = 0;

            var runs = await _query.GetRunsWithCellsAsync();
            RunCombo.ItemsSource = runs;
            if (runs.Count > 0)
            {
                int idx = preselectRunId.HasValue ? runs.FindIndex(r => r.CellenOneRunId == preselectRunId.Value) : 0;
                RunCombo.SelectedIndex = idx < 0 ? 0 : idx;
            }
        }

        private async void RunCombo_SelectionChanged(object sender, SelectionChangedEventArgs e) => await LoadRunAsync();

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

            InfoText.Text = $"{_cells.Count} cells · {_raws.Count} raw files on plate \"{run.PlateName}\"";
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

            var links = Rows.Select(row => new CellLinkSuggestion
            {
                CellId = row.CellId,
                RawFileId = row.RawFileName != None && _idByName.TryGetValue(row.RawFileName, out var id) ? id : (int?)null,
                Confidence = double.TryParse(row.ConfidenceText, NumberStyles.Any, CultureInfo.InvariantCulture, out var cf) ? cf : 0,
                Method = string.IsNullOrEmpty(row.Method) ? "manual" : row.Method
            }).Where(l => l.RawFileId != null).ToList();

            // Clear first so rows set back to "(none)" are unlinked, then apply the chosen links.
            await _recon.ClearLinksAsync(run.CellenOneRunId);
            int n = await _recon.ApplyLinksAsync(links);
            await LoadRunAsync();
            InfoText.Text = $"Applied {n} links.";
        }

        private async void ClearAll_Click(object sender, RoutedEventArgs e)
        {
            if (_recon == null || RunCombo.SelectedItem is not CellRunSummary run) return;
            await _recon.ClearLinksAsync(run.CellenOneRunId);
            await LoadRunAsync();
            InfoText.Text = "Cleared all links for this run.";
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
