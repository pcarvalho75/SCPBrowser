using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using SCPBrowser.Models;
using SCPBrowser.Services;

namespace SCPBrowser
{
    /// <summary>
    /// Attaches a cellenONE isolation run to an EXISTING plate.
    ///
    /// This is the "I got the isolation data later" counterpart to <see cref="NewPlateDialog"/>, which can only
    /// attach a run at the moment the plate is created. The dialog itself imports nothing: it validates the run
    /// folder and hands the caller a (plate_id, run folder) pair, exactly as NewPlateDialog does, so both routes
    /// end up in the same <see cref="CellenOneImportService.ImportRunAsync(string,int,IProgress{string},System.Threading.CancellationToken)"/>.
    /// </summary>
    public partial class ImportCellenOneRunDialog : Window
    {
        /// <summary>A plate the run can be attached to, plus whatever cellenONE data it already carries.</summary>
        public sealed class PlateChoice
        {
            public int PlateId { get; init; }
            public string PlateName { get; init; } = "";

            /// <summary>Most recent run attached to this plate (null if none).</summary>
            public string? ExistingRunUid { get; init; }

            /// <summary>Total isolated cells across ALL runs on this plate, not just the newest.</summary>
            public int ExistingCellCount { get; init; }

            /// <summary>How many distinct cellenONE runs are already attached.</summary>
            public int ExistingRunCount { get; init; }

            public bool HasRun => ExistingRunCount > 0;

            /// <summary>False when the existing-runs lookup failed, so nothing can be asserted about this plate.</summary>
            public bool DataKnown { get; init; } = true;

            /// <summary>What the combo box shows: the plate, and whether it already has isolation data.</summary>
            public string Display
            {
                get
                {
                    if (!DataKnown) return PlateName;
                    if (!HasRun) return $"{PlateName}  —  no cellenONE data";
                    string source = ExistingRunCount > 1
                        ? $"{ExistingRunCount} runs"
                        : ExistingRunUid ?? "run";
                    return $"{PlateName}  —  already has {ExistingCellCount} cells ({source})";
                }
            }
        }

        private readonly ObservableCollection<PlateChoice> _plates = new();
        private readonly List<CellRunSummary> _existingRuns = new();

        /// <summary>
        /// False when the existing-runs query failed, which means the duplicate guards below are blind. In that
        /// state the UI must not claim a plate has "no cellenONE data" - it simply does not know.
        /// </summary>
        private bool _existingRunsKnown = true;

        private CellenOneRun? _parsedRun;

        /// <summary>
        /// cellenone_run_ids that existed BEFORE this import. The importer returns the existing id when it
        /// deduplicates, so the caller compares against this set to tell "imported" from "skipped".
        /// </summary>
        public IReadOnlyCollection<int> KnownRunIds { get; private set; } = Array.Empty<int>();

        /// <summary>Plate the user chose. Valid only when DialogResult is true.</summary>
        public int SelectedPlateId { get; private set; }

        /// <summary>Name of the chosen plate, for the caller's confirmation message.</summary>
        public string SelectedPlateName { get; private set; } = "";

        /// <summary>The validated cellenONE .Run folder. Valid only when DialogResult is true.</summary>
        public string? RunDir { get; private set; }

        /// <summary>Number of isolated cells found in the selected run.</summary>
        public int CellCount { get; private set; }

        public ImportCellenOneRunDialog()
        {
            InitializeComponent();
            PlateComboBox.ItemsSource = _plates;
        }

        /// <summary>
        /// Loads the project's plates and the cellenONE runs already attached to them. Returns false when the
        /// project has no plates, in which case the caller should not show the dialog at all.
        /// </summary>
        public async Task<bool> InitializeAsync(string projectDbPath)
        {
            var plates = await new PlateService(projectDbPath).GetPlatesAsync();
            if (plates.Count == 0)
                return false;

            // Existing runs, so the picker can show which plates already carry isolation data and the duplicate
            // guards below have something to compare against. A missing/legacy cellenONE schema must not block the
            // import, so a failure here degrades to "unknown" rather than throwing.
            var runsByPlate = new Dictionary<int, CellRunSummary>();
            try
            {
                _existingRuns.Clear();
                foreach (var run in await new CellenOneQueryService(projectDbPath).GetRunsWithCellsAsync())
                {
                    _existingRuns.Add(run);
                    // Newest first from the query; keep the first (most recent) per plate.
                    if (!runsByPlate.ContainsKey(run.PlateId))
                        runsByPlate[run.PlateId] = run;
                }
                _existingRunsKnown = true;
            }
            catch
            {
                // Either the cellenONE tables do not exist yet (fresh project - genuinely no data) or the query
                // failed. We cannot tell the two apart here, so treat the guards as blind rather than asserting
                // that every plate is empty.
                _existingRuns.Clear();
                _existingRunsKnown = false;
            }
            KnownRunIds = _existingRuns.Select(r => r.CellenOneRunId).ToHashSet();

            _plates.Clear();
            foreach (var p in plates)
            {
                // Newest run supplies the label; the cell count must span ALL runs on the plate or a plate with two
                // runs would under-report what is already there.
                runsByPlate.TryGetValue(p.PlateId, out var newest);
                var onThisPlate = _existingRuns.Where(r => r.PlateId == p.PlateId).ToList();

                _plates.Add(new PlateChoice
                {
                    PlateId = p.PlateId,
                    PlateName = p.PlateName ?? $"Plate {p.PlateId}",
                    ExistingRunUid = newest?.RunUid,
                    ExistingCellCount = onThisPlate.Sum(r => r.CellCount),
                    ExistingRunCount = onThisPlate.Count,
                    DataKnown = _existingRunsKnown
                });
            }

            // Prefer a plate with no isolation data - that is almost always the one being filled in.
            var firstEmpty = _plates.FirstOrDefault(p => !p.HasRun);
            PlateComboBox.SelectedItem = firstEmpty ?? _plates[0];
            return true;
        }

        private void Browse_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFolderDialog
            {
                Title = "Select a cellenONE isolation .Run folder"
            };
            if (dlg.ShowDialog() != true)
                return;

            string folder = dlg.FolderName;
            try
            {
                Mouse.OverrideCursor = Cursors.Wait;

                if (!CellenOneParser.IsIsolationRun(folder))
                {
                    ClearRun();
                    MessageBox.Show(
                        "That folder doesn't look like a cellenONE isolation run (no *_isolated.xls found).\n\n" +
                        "Pick the '.Run' folder that contains the isolated cells.",
                        "No isolated cells",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                var run = CellenOneParser.ParseRun(folder);
                if (run.Cells.Count == 0)
                {
                    ClearRun();
                    MessageBox.Show("No isolated cells were found in that run.", "Nothing to import",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                _parsedRun = run;
                RunFolderTextBox.Text = folder;

                var bits = new List<string>();
                if (!string.IsNullOrWhiteSpace(run.RunUid)) bits.Add($"Run {run.RunUid}");
                if (!string.IsNullOrWhiteSpace(run.RunDate)) bits.Add(run.RunDate!);
                if (!string.IsNullOrWhiteSpace(run.Instrument)) bits.Add(run.Instrument!);
                if (!string.IsNullOrWhiteSpace(run.OperatorName)) bits.Add(run.OperatorName!);

                PreviewTitleText.Text = $"✓ {run.Cells.Count} isolated cells found";
                PreviewDetailText.Text = bits.Count > 0
                    ? string.Join("   ·   ", bits)
                    : Path.GetFileName(folder.TrimEnd('\\', '/'));
                PreviewBorder.Visibility = Visibility.Visible;
            }
            catch (Exception ex)
            {
                ClearRun();
                MessageBox.Show($"Failed to read cellenONE run:\n\n{ex.Message}", "Import error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                Mouse.OverrideCursor = null;
                UpdateWarning();
                Validate();
            }
        }

        private void ClearRun()
        {
            _parsedRun = null;
            RunFolderTextBox.Text = "";
            PreviewBorder.Visibility = Visibility.Collapsed;
        }

        private void PlateComboBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            UpdateWarning();
            Validate();
        }

        /// <summary>
        /// Flags the two cases worth a heads-up: re-importing the same run (harmless, the import is idempotent by
        /// file hash) and attaching a SECOND, different run to a plate that already has one (allowed, but usually
        /// means the wrong plate was picked).
        /// </summary>
        private void UpdateWarning()
        {
            var plate = PlateComboBox.SelectedItem as PlateChoice;
            if (plate == null || _parsedRun == null)
            {
                WarningBorder.Visibility = Visibility.Collapsed;
                return;
            }

            // Highest-value guard: this run is already attached to a DIFFERENT plate. The importer's idempotency is
            // keyed on (plate_id, file_hash), so it would not catch this - the same cells would be imported twice
            // under two plates. Almost always it means the wrong plate is selected.
            if (!string.IsNullOrWhiteSpace(_parsedRun.RunUid))
            {
                var elsewhere = _existingRuns.FirstOrDefault(r =>
                    r.PlateId != plate.PlateId &&
                    string.Equals(r.RunUid, _parsedRun.RunUid, StringComparison.OrdinalIgnoreCase));

                if (elsewhere != null)
                {
                    WarningTitleText.Text = "This run is already imported into another plate";
                    WarningDetailText.Text =
                        $"Run {_parsedRun.RunUid} is already attached to '{elsewhere.PlateName ?? $"plate {elsewhere.PlateId}"}' " +
                        $"({elsewhere.CellCount} cells). Importing it into '{plate.PlateName}' as well will create a second " +
                        "copy of the same cells under a different plate. Check that the target plate is correct.";
                    WarningBorder.Visibility = Visibility.Visible;
                    return;
                }
            }

            if (!plate.HasRun)
            {
                WarningBorder.Visibility = Visibility.Collapsed;
                return;
            }

            // Match against EVERY run on this plate, not just the newest, or re-picking an older run would be
            // mislabelled as a new one.
            bool sameRun = !string.IsNullOrWhiteSpace(_parsedRun.RunUid)
                           && _existingRuns.Any(r => r.PlateId == plate.PlateId
                                                     && string.Equals(r.RunUid, _parsedRun.RunUid, StringComparison.OrdinalIgnoreCase));

            string existingDesc = plate.ExistingRunCount > 1
                ? $"{plate.ExistingCellCount} cells from {plate.ExistingRunCount} runs"
                : $"{plate.ExistingCellCount} cells from run {plate.ExistingRunUid ?? "(unnamed)"}";

            if (sameRun)
            {
                // Deliberately does NOT promise a no-op. The importer deduplicates on a hash of the run files,
                // not on this Run ID, so a re-exported or re-saved copy of the same run has a different hash and
                // WOULD be imported again. The post-import message reports which of the two actually happened.
                WarningTitleText.Text = "This run appears to be already imported for this plate";
                WarningDetailText.Text =
                    $"'{plate.PlateName}' already carries run {_parsedRun.RunUid}. If the run files are unchanged " +
                    "the import is deduplicated and nothing is added; if the folder was re-exported or modified " +
                    "since, a second copy of these cells WILL be added. You will be told which happened.";
            }
            else if (string.IsNullOrWhiteSpace(_parsedRun.RunUid))
            {
                // No Run ID means neither duplicate guard can say anything - do not imply this run is new.
                WarningTitleText.Text = "This plate already has cellenONE data";
                WarningDetailText.Text =
                    $"'{plate.PlateName}' already carries {existingDesc}. This run folder has no Run ID, so it " +
                    "cannot be checked against what is already imported. If it is not the same run, its cells will " +
                    "be ADDED alongside the existing ones.";
            }
            else
            {
                WarningTitleText.Text = "This plate already has cellenONE data";
                WarningDetailText.Text =
                    $"'{plate.PlateName}' already carries {existingDesc}. Importing this different run will ADD its " +
                    "cells alongside them, and there is no undo — check the target plate before continuing.";
            }
            WarningBorder.Visibility = Visibility.Visible;
        }

        private void Validate()
        {
            ImportButton.IsEnabled = PlateComboBox.SelectedItem is PlateChoice && _parsedRun != null;
        }

        private void Import_Click(object sender, RoutedEventArgs e)
        {
            if (PlateComboBox.SelectedItem is not PlateChoice plate || _parsedRun == null)
                return;

            SelectedPlateId = plate.PlateId;
            SelectedPlateName = plate.PlateName;
            RunDir = RunFolderTextBox.Text;
            CellCount = _parsedRun.Cells.Count;
            DialogResult = true;
        }

        private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
    }
}
