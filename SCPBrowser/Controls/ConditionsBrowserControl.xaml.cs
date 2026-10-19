// ConditionsBrowserControl.xaml.cs
// Child control for browsing biological conditions, marking negative controls (blanks) and their background removal,
// and triggering condition-level cascade deletes.
// Location: SCPBrowser/Controls/ConditionsBrowserControl.xaml.cs

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using SCPBrowser.Models;
using SCPBrowser.Services;

namespace SCPBrowser
{
    /// <summary>
    /// Raised after a successful cascade delete so parent controls and the main
    /// window can refresh dependent data and views.
    /// </summary>
    public class ConditionDeletedEventArgs : EventArgs
    {
        public string Condition { get; init; } = string.Empty;
        public DeleteConditionResult DeleteResult { get; init; } = new();
        public string BackupPath { get; init; } = string.Empty;
    }

    public partial class ConditionsBrowserControl : UserControl
    {
        private string _databasePath = string.Empty;
        private ParquetDataService? _parquetService;
        private List<RawFileInfo> _allRawFiles = new();
        private List<PlateInfo> _plates = new();

        // The plot/blast-radius split while a condition is selected, kept when the user drags the splitter.
        private GridLength _plotShare = new(2, GridUnitType.Star);
        private GridLength _statsShare = new(1, GridUnitType.Star);

        // Negative controls: the settings saved in the project (what the last load applied) and the draft being
        // edited here. The plot, the preview and the Blank Proteins tab always show the draft.
        private NegativeControlSettings _savedSettings = new();
        private NegativeControlSettings _draft = new();
        private BackgroundRemovalPlan? _plan;
        private BackgroundRemovalResult? _preview;
        private List<BlankProteinView> _blankProteins = new();
        private bool _blankProteinsStale = true;
        private bool _syncingNegativeControls;
        private bool _syncingBlankList;
        private string _blankListShown = "";      // the blank runs the checklist was built for
        private bool _numbersValid = true;         // every number box parses; Apply waits for it
        private HashSet<string> _excludedRuns = new(StringComparer.Ordinal);   // excluded by hand in the Explorer

        // The preview is rebuilt once typing in a number box pauses, not on every keystroke (about 0.1 s per rebuild at
        // 470 runs).
        private readonly DispatcherTimer _numberDebounce = new() { Interval = TimeSpan.FromMilliseconds(250) };

        /// <summary>
        /// Lightweight DTO for the left-side conditions list.
        /// </summary>
        private class ConditionListItem
        {
            public string Name { get; set; } = string.Empty;
            public int RawFileCount { get; set; }
            public bool IsNegativeControl { get; set; }
        }

        /// <summary>One row of the Blank Proteins table, with the texts and blanks the grid shows.</summary>
        private sealed class BlankProteinView
        {
            public BlankProteinRow Row { get; init; } = new();
            public string Protein => Row.Protein;
            public string Genes => Row.Genes;
            public string Scope => Row.Scope;
            public string BlanksText => $"{Row.BlankDetections}/{Row.SelectedBlanks}";
            public double BlankFraction => Row.BlankFraction;
            public double? BlankLevel => double.IsNaN(Row.BlankLevel) ? null : Row.BlankLevel;
            public string CellsText => $"{Row.CellDetections}/{Row.CellRuns}";
            public double CellFraction => Row.CellRuns > 0 ? Row.CellDetections / (double)Row.CellRuns : 0;
            public double? MedianRatio => double.IsNaN(Row.MedianCellToBlank) ? null : Row.MedianCellToBlank;
            public int CellKept => Row.CellKept;
            public string Status => Row.Status;
        }

        /// <summary>
        /// Raised after a successful cascade delete. Parents should refresh dependent state.
        /// </summary>
        public event EventHandler<ConditionDeletedEventArgs>? ConditionDeleted;

        /// <summary>
        /// Raised after new negative-control settings were saved; the host rebuilds the analyses from the loaded data.
        /// </summary>
        public event EventHandler? NegativeControlsApplied;

        /// <summary>
        /// The data as loaded for analysis, for the box plot's per-run values (the database's per-run counts can predate
        /// the FDR filter). Set by the host.
        /// </summary>
        public Func<ProteomicsData?>? LoadedDataSource { get; set; }

        /// <summary>What the applied negative controls did on the last load (null when none apply). Set by the host.</summary>
        public Func<BackgroundRemovalResult?>? AppliedRemovalSource { get; set; }

        /// <summary>Plate colors by plate id for the plot's dots (PlateFilterControl's, the single source).</summary>
        public Func<IReadOnlyDictionary<int, Color>?>? PlateColorSource
        {
            get => IdentificationsPlot.PlateColorSource;
            set => IdentificationsPlot.PlateColorSource = value;
        }

        /// <summary>Condition colors by name for the plot's boxes (the Explorer legend's).</summary>
        public Func<IReadOnlyDictionary<string, Color>?>? ConditionColorSource
        {
            get => IdentificationsPlot.ConditionColorSource;
            set => IdentificationsPlot.ConditionColorSource = value;
        }

        public ConditionsBrowserControl()
        {
            InitializeComponent();
            _numberDebounce.Tick += (s, e) =>
            {
                _numberDebounce.Stop();
                RefreshNegativeControls();
            };
            ScrollingPaneLayout.Attach(RightPaneScroller, RightPane, RequiredRightPaneHeight);
            IdentificationsPlot.CategoryClicked += (s, condition) => SelectCondition(condition);
            IdentificationsPlot.RunClicked += (s, run) =>
            {
                string? condition = _allRawFiles.FirstOrDefault(rf => rf.RawFileName == run)?.BiologicalCondition;
                // A blank's dot puts it in or out of the background estimate.
                if (_plan != null && _plan.BlankRuns.Contains(run))
                {
                    if (_plan.ManuallyExcludedBlanks.Contains(run))
                    {
                        NcSuggestText.Text = "That blank is excluded in the Explorer, so it is never used for the estimate. Restore it there to use it.";
                        NcSuggestText.Visibility = Visibility.Visible;
                    }
                    else
                        SetBlankUsed(run, _draft.LeftOutRuns.Contains(run, StringComparer.Ordinal));
                    if (!string.IsNullOrEmpty(condition))
                        SelectCondition(condition);
                    DetailTabs.SelectedItem = NegativeControlsTab;
                    return;
                }
                if (!string.IsNullOrEmpty(condition))
                    SelectCondition(condition);
            };
        }

        private void SelectCondition(string condition)
        {
            var item = (ConditionsListBox.ItemsSource as IEnumerable<ConditionListItem>)?.FirstOrDefault(c => c.Name == condition);
            if (item == null)
                return;
            ConditionsListBox.SelectedItem = item;
            ConditionsListBox.ScrollIntoView(item);
        }

        /// <summary>
        /// Loads the list of biological conditions for the given project database.
        /// </summary>
        public async Task LoadDataAsync(string databasePath)
        {
            // Reopening the browser on the same project keeps negative-control changes not yet applied; closing it asks
            // about them first (ProjectBrowser2), so only a reload reaches here with a draft.
            bool keepDraft = string.Equals(_databasePath, databasePath, StringComparison.OrdinalIgnoreCase)
                             && HasPendingNegativeControlChanges;
            _databasePath = databasePath;
            _parquetService = new ParquetDataService(databasePath);
            _savedSettings = await NegativeControlSettings.LoadAsync(new ProjectDatabaseService(databasePath));
            if (!keepDraft)
            {
                _draft = _savedSettings.Clone();
                NcSuggestText.Visibility = Visibility.Collapsed;
            }
            await PopulateConditionsListAsync();
            ResetDetailsPane();
        }

        /// <summary>True when the negative controls were changed here and not applied.</summary>
        public bool HasPendingNegativeControlChanges => !_draft.SameAs(_savedSettings);

        private async Task PopulateConditionsListAsync()
        {
            if (_parquetService == null)
                return;

            var allRawFiles = await _parquetService.GetRawFilesAsync();
            var items = allRawFiles
                .Where(rf => !string.IsNullOrEmpty(rf.BiologicalCondition))
                .GroupBy(rf => rf.BiologicalCondition!)
                .Select(g => new ConditionListItem
                {
                    Name = g.Key,
                    RawFileCount = g.Count(),
                    IsNegativeControl = _draft.IsNegativeControl(g.Key)
                })
                .OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            ConditionsListBox.ItemsSource = items;

            // Identifications per run of every condition, from the data as loaded for the Explorer
            _allRawFiles = allRawFiles;
            _plates = await new PlateService(_databasePath).GetPlatesAsync();
            HashSet<string> excludedRuns;
            try
            {
                excludedRuns = await _parquetService.GetExcludedRunNamesAsync();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ConditionsBrowserControl] Could not read excluded runs: {ex.Message}");
                excludedRuns = new HashSet<string>();
            }
            _excludedRuns = new HashSet<string>(excludedRuns, StringComparer.Ordinal);
            RefreshNegativeControls(rebuildPlot: false);   // SetData below draws the plot once, with the overlay
            IdentificationsPlot.SetData(allRawFiles, _plates, LoadedDataSource?.Invoke(), excludedRuns);
        }

        private async void ConditionsListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (ConditionsListBox.SelectedItem is ConditionListItem selected)
            {
                IdentificationsPlot.HighlightedKey = selected.Name;
                await LoadConditionStatsAsync(selected.Name);
            }
            else
            {
                ResetDetailsPane();
            }
        }

        private async Task LoadConditionStatsAsync(string condition)
        {
            if (_parquetService == null)
                return;

            try
            {
                var stats = await _parquetService.GetConditionStatsAsync(condition);

                // A protein identified in several runs is one protein, so the condition's total is a distinct count
                // across its runs, from the data as loaded (the database's per-run rows can predate the FDR filter).
                // The tile showed the number of protein-quant rows, one per protein per run: 95,988 for 2,744
                // distinct proteins in KOLF_GR_6mo of SpaceProjectV3. That row count stays in the delete dialog.
                var loadedData = LoadedDataSource?.Invoke();
                var runNames = _allRawFiles
                    .Where(rf => string.Equals(rf.BiologicalCondition, condition, StringComparison.Ordinal))
                    .Select(rf => rf.RawFileName)
                    .ToList();
                int? distinctProteins = loadedData == null
                    ? null
                    : (await Task.Run(() => loadedData.CountDistinctAcrossRuns(runNames))).Proteins;

                // Another condition may have been selected while counting; its panel must not be overwritten.
                if ((ConditionsListBox.SelectedItem as ConditionListItem)?.Name != condition)
                    return;

                ConditionHeaderText.Text = $"Condition: {condition}";
                ConditionSubText.Text =
                    $"Removing this condition will purge {stats.RawFileCount:N0} raw file(s) and all dependent rows.";
                ConditionSubText.Visibility = Visibility.Visible;

                _syncingNegativeControls = true;
                NegativeControlCheckBox.IsChecked = _draft.IsNegativeControl(condition);
                _syncingNegativeControls = false;
                NegativeControlCheckBox.Visibility = Visibility.Visible;

                StatRawFilesText.Text = stats.RawFileCount.ToString("N0");
                StatPlatesText.Text = stats.PlateCount.ToString("N0");
                StatProteinQuantText.Text = distinctProteins?.ToString("N0") ?? "n/a";
                StatClassificationsText.Text = stats.ClassificationCount.ToString("N0");
                StatExclusionsText.Text = stats.ExclusionCount.ToString("N0");
                StatOrphanImportsText.Text = stats.OrphanImportCount.ToString("N0");

                PlateNamesList.ItemsSource = stats.PlateNames;

                StatsPanel.Visibility = Visibility.Visible;
                BlastRadiusTab.IsEnabled = true;
                DetailTabs.Visibility = Visibility.Visible;
                ShowStatsArea(true);
                DangerZonePanel.Visibility = Visibility.Visible;
                DeleteConditionButton.IsEnabled = stats.RawFileCount > 0;
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    $"Error loading condition statistics:\n\n{ex.Message}",
                    "Stats Error",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }

        /// <summary>
        /// The blast radius takes room under the plot only while a condition is selected; otherwise the plot has the
        /// whole area. A split the user dragged is restored the next time a condition is selected.
        /// </summary>
        private void ShowStatsArea(bool show)
        {
            bool shown = StatsSplitter.Visibility == Visibility.Visible;
            if (show == shown)
                return;
            if (show)
            {
                PlotRow.Height = _plotShare;
                StatsRow.Height = _statsShare;
                StatsRow.MinHeight = 80; // the blast radius scrolls, so a small window keeps more of the plot
                StatsSplitter.Visibility = Visibility.Visible;
            }
            else
            {
                if (PlotRow.Height.IsStar && StatsRow.Height.IsStar && StatsRow.Height.Value > 0)
                    (_plotShare, _statsShare) = (PlotRow.Height, StatsRow.Height);
                StatsRow.MinHeight = 0;
                StatsRow.Height = new GridLength(0);
                PlotRow.Height = new GridLength(1, GridUnitType.Star);
                StatsSplitter.Visibility = Visibility.Collapsed;
            }
        }

        /// <summary>
        /// Height the right pane needs: the condition header, the plot at its minimum plot area, and, while a condition
        /// is selected, the blast radius at its minimum (it scrolls inside) and the Danger Zone at full size. Also keeps
        /// the plot row's minimum at the plot's, so the blast radius gives up its room first.
        /// </summary>
        private double RequiredRightPaneHeight()
        {
            double plot = IdentificationsPlot.MinimumHeight() + IdentificationsPlot.Margin.Top + IdentificationsPlot.Margin.Bottom;
            if (Math.Abs(PlotRow.MinHeight - plot) > 0.5)
                PlotRow.MinHeight = plot;
            double required = ScrollingPaneLayout.OuterHeight(ConditionHeaderBorder) + plot
                + ScrollingPaneLayout.OuterHeight(DangerZonePanel);
            if (StatsSplitter.Visibility == Visibility.Visible)
                required += ScrollingPaneLayout.OuterHeight(StatsSplitter) + StatsRow.MinHeight;
            return required;
        }

        private void ResetDetailsPane()
        {
            IdentificationsPlot.HighlightedKey = null;
            ConditionHeaderText.Text = "Select a condition to view its impact";
            ConditionSubText.Text = string.Empty;
            ConditionSubText.Visibility = Visibility.Collapsed;
            NegativeControlCheckBox.Visibility = Visibility.Collapsed;
            StatsPanel.Visibility = Visibility.Collapsed;
            DangerZonePanel.Visibility = Visibility.Collapsed;
            PlateNamesList.ItemsSource = null;
            BlastRadiusTab.IsEnabled = false;

            // With negative controls marked, their tabs stay open without a selection, so a project with blanks shows
            // its settings and blank proteins without first clicking a condition. The blast radius needs one.
            bool showNegativeControls = _draft.Conditions.Count > 0;
            DetailTabs.Visibility = showNegativeControls ? Visibility.Visible : Visibility.Collapsed;
            if (showNegativeControls && (DetailTabs.SelectedItem == null || DetailTabs.SelectedItem == BlastRadiusTab))
                DetailTabs.SelectedItem = NegativeControlsTab;
            ShowStatsArea(showNegativeControls);
        }

        /// <summary>
        /// Reloads the conditions and the saved negative-control settings, keeping the selected condition and tab.
        /// Called after the host applied new negative controls.
        /// </summary>
        public async Task ReloadKeepingSelectionAsync()
        {
            if (string.IsNullOrEmpty(_databasePath))
                return;
            string? selected = (ConditionsListBox.SelectedItem as ConditionListItem)?.Name;
            var tab = DetailTabs.SelectedItem;
            _savedSettings = await NegativeControlSettings.LoadAsync(new ProjectDatabaseService(_databasePath));
            _draft = _savedSettings.Clone();
            NcSuggestText.Visibility = Visibility.Collapsed;
            await PopulateConditionsListAsync();
            if (selected != null)
            {
                SelectCondition(selected);
                DetailTabs.SelectedItem = tab;
            }
            else
                ResetDetailsPane();
        }

        // ------------------------------------------------------------------------------------------------------------
        // Negative controls
        // ------------------------------------------------------------------------------------------------------------

        private Dictionary<string, int> RunToPlate() => _allRawFiles
            .Where(rf => rf.PlateId.HasValue)
            .GroupBy(rf => rf.RawFileName, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First().PlateId!.Value, StringComparer.Ordinal);

        private Dictionary<int, string> PlateNames() => _plates
            .GroupBy(p => p.PlateId)
            .ToDictionary(g => g.Key, g => g.First().PlateName);

        private string PlateNameOf(string run)
        {
            var plateId = _allRawFiles.FirstOrDefault(rf => rf.RawFileName == run)?.PlateId;
            return plateId.HasValue ? _plates.FirstOrDefault(p => p.PlateId == plateId.Value)?.PlateName ?? $"Plate {plateId}" : "No plate";
        }

        /// <summary>
        /// Rebuilds everything that shows the draft: the plot's blank markings and after-removal counts, the rule
        /// controls, the blank-run checklist, the preview, and (when shown) the Blank Proteins table.
        /// </summary>
        private void RefreshNegativeControls(bool rebuildPlot = true)
        {
            _numberDebounce.Stop();
            var data = LoadedDataSource?.Invoke();
            _plan = null;
            _preview = null;
            _blankProteinsStale = true;

            SyncNegativeControlControls();

            if (data != null && _draft.Conditions.Count > 0)
            {
                // Removal is previewed even while it is switched off, so the effect can be judged before turning it on.
                var previewSettings = _draft.Clone();
                previewSettings.RemovalEnabled = true;
                _plan = BackgroundRemovalService.BuildPlan(data, previewSettings, RunToPlate(), PlateNames(), _excludedRuns);
                _preview = previewSettings.RemovesBackground && _plan.SelectedBlanks.Count > 0
                    ? BackgroundRemovalService.Summarize(data, _plan)
                    : null;
            }

            IdentificationsPlot.SetNegativeControls(_plan == null ? null : new NegativeControlOverlay
            {
                BlankRuns = _plan.BlankRuns,
                LeftOutBlanks = new HashSet<string>(_plan.BlankRuns.Where(r => !_plan.SelectedBlanks.Contains(r)), StringComparer.Ordinal),
                Removal = _preview?.Runs
            }, rebuildPlot);

            BuildBlankRunsList(data);
            BuildPreview(data);
            UpdateNegativeControlStatus(data);
            if (DetailTabs.SelectedItem == BlankProteinsTab)
                RefreshBlankProteins();
        }

        private void SyncNegativeControlControls()
        {
            _syncingNegativeControls = true;
            try
            {
                RemovalEnabledCheckBox.IsChecked = _draft.RemovalEnabled;
                FrequencyCheckBox.IsChecked = _draft.UseFrequency;
                IntensityCheckBox.IsChecked = _draft.UseIntensity;
                string fraction = (_draft.MinFraction * 100).ToString("0.##", CultureInfo.CurrentCulture);
                if (!TryParseNumber(MinFractionBox.Text, out var shown) || Math.Abs(shown - _draft.MinFraction * 100) > 1e-9)
                    MinFractionBox.Text = fraction;
                if (!TryParseNumber(IntensityFactorBox.Text, out var factor) || Math.Abs(factor - _draft.IntensityFactor) > 1e-9)
                    IntensityFactorBox.Text = _draft.IntensityFactor.ToString("0.##", CultureInfo.CurrentCulture);
                if (!int.TryParse(MinBlanksBox.Text, NumberStyles.Integer, CultureInfo.CurrentCulture, out var minBlanks) || minBlanks != _draft.MinBlanksPerScope)
                    MinBlanksBox.Text = _draft.MinBlanksPerScope.ToString(CultureInfo.CurrentCulture);
                BlankLevelCombo.SelectedIndex = _draft.BlankLevel == BlankLevelStatistic.Max ? 0 : 1;
                EitherRuleRadio.IsChecked = !_draft.RequireBothRules;
                BothRulesRadio.IsChecked = _draft.RequireBothRules;
                PerPlateRadio.IsChecked = _draft.PerPlate;
                WholeProjectRadio.IsChecked = !_draft.PerPlate;
                ExcludeBlanksCheckBox.IsChecked = _draft.ExcludeBlankRunsFromAnalysis;

                MinFractionBox.IsEnabled = _draft.UseFrequency;
                IntensityFactorBox.IsEnabled = _draft.UseIntensity;
                BlankLevelCombo.IsEnabled = _draft.UseIntensity;
                CombineRow.IsEnabled = _draft.UseFrequency && _draft.UseIntensity;
                MinBlanksRow.IsEnabled = _draft.PerPlate;

                if (ConditionsListBox.SelectedItem is ConditionListItem selected)
                    NegativeControlCheckBox.IsChecked = _draft.IsNegativeControl(selected.Name);
            }
            finally
            {
                _syncingNegativeControls = false;
            }
        }

        private static bool TryParseNumber(string text, out double value) =>
            double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out value)
            || double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);

        private void BuildBlankRunsList(ProteomicsData? data)
        {
            bool any = _plan != null && _plan.BlankRuns.Count > 0;
            NcBody.Visibility = _draft.Conditions.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            SuggestOutliersButton.IsEnabled = any;
            UseAllBlanksButton.IsEnabled = any && _plan!.SelectedBlanks.Count < _plan.BlankRuns.Count;

            if (data == null)
            {
                NcIntroText.Text = "Negative controls are available once the project data has finished loading.";
                return;
            }
            if (_draft.Conditions.Count == 0)
            {
                NcIntroText.Text = "No condition is marked as a negative control. Select the condition that holds your blanks " +
                                   "(runs with no cell) and tick \"Negative control (blanks)\" above. Several conditions can be marked.";
                return;
            }
            NcIntroText.Text = $"Negative controls: {string.Join(", ", _draft.Conditions.OrderBy(c => c, StringComparer.OrdinalIgnoreCase))}. " +
                               $"{_plan?.SelectedBlanks.Count ?? 0} of {_plan?.BlankRuns.Count ?? 0} blank runs are used for the estimate.";
            if (!any)
            {
                BlankRunsPanel.Children.Clear();
                _blankListShown = "";
                BlankRunsPanel.Children.Add(new TextBlock { Text = "These conditions have no loaded run.", Style = (Style)FindResource("NcText") });
                return;
            }

            // Same blanks as on screen: only the ticks change, so the list keeps its scroll position and focus.
            string shown = string.Join("|", _plan!.BlankRuns.OrderBy(r => r, StringComparer.Ordinal)) + "#" +
                           string.Join("|", _plan.ManuallyExcludedBlanks.OrderBy(r => r, StringComparer.Ordinal));
            if (shown == _blankListShown)
            {
                _syncingBlankList = true;
                foreach (var box in BlankRunsPanel.Children.OfType<CheckBox>())
                    box.IsChecked = _plan.SelectedBlanks.Contains((string)box.Tag);
                _syncingBlankList = false;
                return;
            }
            _blankListShown = shown;
            BlankRunsPanel.Children.Clear();

            foreach (var plateGroup in _plan!.BlankRuns
                         .OrderBy(r => PlateNameOf(r), StringComparer.OrdinalIgnoreCase)
                         .ThenBy(r => r, StringComparer.Ordinal)
                         .GroupBy(PlateNameOf))
            {
                BlankRunsPanel.Children.Add(new TextBlock
                {
                    Text = plateGroup.Key,
                    FontSize = 11,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = new SolidColorBrush(Color.FromRgb(0x64, 0x74, 0x8b)),
                    Margin = new Thickness(0, 4, 0, 1)
                });
                foreach (var run in plateGroup)
                {
                    int proteins = data.ProteinCountPerFile.TryGetValue(run, out var n) ? n : 0;
                    bool excluded = _plan.ManuallyExcludedBlanks.Contains(run);
                    var box = new CheckBox
                    {
                        IsChecked = _plan.SelectedBlanks.Contains(run),
                        IsEnabled = !excluded,
                        Tag = run,
                        Style = (Style)FindResource("NcCheckBox"),
                        Margin = new Thickness(6, 1, 0, 1),
                        Content = new TextBlock
                        {
                            Inlines =
                            {
                                new System.Windows.Documents.Run(run),
                                new System.Windows.Documents.Run($"   {proteins:N0} protein groups" + (excluded ? " (excluded in the Explorer)" : ""))
                                    { Foreground = new SolidColorBrush(Color.FromRgb(0x94, 0xa3, 0xb8)) }
                            },
                            TextTrimming = TextTrimming.CharacterEllipsis
                        },
                        ToolTip = excluded ? run + "\nExcluded in the Explorer: never used for the estimate. Restore it there to use it." : run
                    };
                    box.Checked += (s, e) => { if (!_syncingBlankList) SetBlankUsed((string)((CheckBox)s!).Tag, true); };
                    box.Unchecked += (s, e) => { if (!_syncingBlankList) SetBlankUsed((string)((CheckBox)s!).Tag, false); };
                    BlankRunsPanel.Children.Add(box);
                }
            }
        }

        private void SetBlankUsed(string run, bool used)
        {
            bool isLeftOut = _draft.LeftOutRuns.Contains(run, StringComparer.Ordinal);
            if (used == !isLeftOut)
                return;
            if (used)
                _draft.LeftOutRuns.RemoveAll(r => string.Equals(r, run, StringComparison.Ordinal));
            else
                _draft.LeftOutRuns.Add(run);
            NcSuggestText.Visibility = Visibility.Collapsed;
            RefreshNegativeControls();
        }

        private void BuildPreview(ProteomicsData? data)
        {
            PreviewPanel.Children.Clear();
            void Line(string text, Brush? color = null, bool bold = false) => PreviewPanel.Children.Add(new TextBlock
            {
                Text = text,
                Style = (Style)FindResource("NcText"),
                Margin = new Thickness(0, 1, 0, 1),
                Foreground = color ?? new SolidColorBrush(Color.FromRgb(0x33, 0x41, 0x55)),
                FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal
            });
            var warn = new SolidColorBrush(Color.FromRgb(0xb4, 0x53, 0x09));

            if (data == null || _plan == null)
            {
                Line("Nothing to preview yet.");
                return;
            }
            if (!_draft.UseFrequency && !_draft.UseIntensity)
            {
                Line("No rule is ticked, so no background is removed.", warn);
            }
            else if (_plan.SelectedBlanks.Count == 0)
            {
                Line("No blank run is selected, so no background can be estimated.", warn);
            }
            else if (_preview != null)
            {
                Line(_draft.RemovalEnabled ? "With these settings:" : "If background removal were switched on:", bold: true);

                foreach (var scope in _plan.Scopes)
                {
                    string blanks = scope.SelectedBlanks.Count == 1 ? "1 blank" : $"{scope.SelectedBlanks.Count} blanks";
                    var parts = new List<string> { $"{scope.BlankProteins.Count:N0} proteins seen in {blanks}" };
                    if (_draft.UseFrequency)
                        parts.Add($"{scope.FrequentProteins.Count:N0} frequent");
                    // How strong the blanks are against the cells: strong blanks put more cell proteins below the blank level.
                    double blankSignal = MedianSignal(data, scope.SelectedBlanks);
                    double cellSignal = MedianSignal(data, scope.Runs.Where(r => !_plan.BlankRuns.Contains(r)));
                    if (blankSignal > 0 && cellSignal > 0)
                        parts.Add($"blank signal {blankSignal / cellSignal:0.#}x that of the cells");
                    Line($"{scope.Name}: {string.Join(", ", parts)}.");
                    if (_draft.UseFrequency && scope.SelectedBlanks.Count == 1)
                        Line($"   With a single blank, every protein in it is frequent, whatever the percentage.", warn);
                }
                if (_draft.PerPlate && _plan.PlatesUsingProjectBlanks.Count > 0)
                    Line($"{string.Join(", ", _plan.PlatesUsingProjectBlanks)}: fewer than {_draft.MinBlanksPerScope} selected blanks of its own, " +
                         $"so its runs use all {_plan.SelectedBlanks.Count} selected blanks.");

                int removed = _preview.MeasurementsRemovedByFrequency + _preview.MeasurementsRemovedByIntensity;
                double share = _preview.MeasurementsTotal > 0 ? removed / (double)_preview.MeasurementsTotal : 0;
                string byRule = _draft.UseFrequency && _draft.UseIntensity && !_draft.RequireBothRules
                    ? $" ({_preview.MeasurementsRemovedByFrequency:N0} by frequency, {_preview.MeasurementsRemovedByIntensity:N0} by intensity)"
                    : "";
                Line($"Protein measurements removed from the analysed runs: {removed:N0} of {_preview.MeasurementsTotal:N0} ({share:P0}){byRule}. " +
                     $"{_preview.ProteinsRemovedEverywhere:N0} proteins are left with no measurement.");

                // How much each condition loses: the share of a run's protein groups removed, over the condition's runs.
                var byCondition = _allRawFiles
                    .Where(rf => !string.IsNullOrEmpty(rf.BiologicalCondition) && !_draft.IsNegativeControl(rf.BiologicalCondition)
                                 && _preview.Runs.ContainsKey(rf.RawFileName))
                    .GroupBy(rf => rf.BiologicalCondition!)
                    .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase);
                foreach (var g in byCondition)
                {
                    var lost = g.Select(rf => _preview.Runs[rf.RawFileName])
                        .Where(s => s.RawProteins > 0)
                        .Select(s => 1.0 - s.KeptProteins / (double)s.RawProteins)
                        .OrderBy(x => x)
                        .ToList();
                    if (lost.Count == 0)
                        continue;
                    double median = lost.Count % 2 == 1 ? lost[lost.Count / 2] : (lost[lost.Count / 2 - 1] + lost[lost.Count / 2]) / 2;
                    Line($"   {g.Key}: runs lose a median {median:P0} of their protein groups (range {lost[0]:P0} to {lost[^1]:P0}).",
                        median >= 0.5 ? warn : null);
                }
            }

            if (_draft.ExcludeBlankRunsFromAnalysis && _plan.BlankRuns.Count > 0)
                Line($"{_plan.BlankRuns.Count} blank run(s) are left out of the cell analyses.");
            if (_plan.ManuallyExcludedBlanks.Count > 0)
                Line($"{_plan.ManuallyExcludedBlanks.Count} blank run(s) excluded in the Explorer are not used for the estimate.");
        }

        private static double MedianSignal(ProteomicsData data, IEnumerable<string> runs)
        {
            var values = runs.Select(r => data.TotalIonCurrentPerFile.TryGetValue(r, out var v) ? v : double.NaN)
                .Where(v => v > 0).OrderBy(v => v).ToList();
            if (values.Count == 0)
                return 0;
            int m = values.Count / 2;
            return values.Count % 2 == 1 ? values[m] : (values[m - 1] + values[m]) / 2;
        }

        private void UpdateNegativeControlStatus(ProteomicsData? data)
        {
            bool pending = !_draft.SameAs(_savedSettings);
            ApplyNegativeControlsButton.IsEnabled = pending && data != null && _numbersValid;
            DiscardNegativeControlsButton.IsEnabled = pending;
            var applied = AppliedRemovalSource?.Invoke();
            string appliedText = applied != null && !string.IsNullOrEmpty(applied.Describe())
                ? $"Applied now: {applied.Describe()}."
                : _savedSettings.Conditions.Count > 0 ? "Applied now: no change to the analyses." : "";
            NcStatusText.Text = pending
                ? "These changes are a preview only. Apply and reprocess to use them in every analysis." + (appliedText.Length > 0 ? " " + appliedText : "")
                : appliedText;
            NcStatusText.Foreground = new SolidColorBrush(pending ? Color.FromRgb(0xb4, 0x53, 0x09) : Color.FromRgb(0x47, 0x55, 0x69));
        }

        private void NegativeControlCheckBox_Changed(object sender, RoutedEventArgs e)
        {
            if (_syncingNegativeControls || ConditionsListBox.SelectedItem is not ConditionListItem selected)
                return;
            bool mark = NegativeControlCheckBox.IsChecked == true;
            if (mark && !_draft.IsNegativeControl(selected.Name))
                _draft.Conditions.Add(selected.Name);
            else if (!mark)
                _draft.Conditions.RemoveAll(c => string.Equals(c, selected.Name, StringComparison.Ordinal));
            selected.IsNegativeControl = mark;
            ConditionsListBox.Items.Refresh();
            ConditionsListBox.SelectedItem = selected;
            RefreshNegativeControls();
            if (mark)
                DetailTabs.SelectedItem = NegativeControlsTab;
        }

        private void NcOption_Changed(object sender, RoutedEventArgs e)
        {
            if (_syncingNegativeControls || RemovalEnabledCheckBox == null || BlankLevelCombo == null || BothRulesRadio == null
                || ExcludeBlanksCheckBox == null)
                return;
            _draft.RemovalEnabled = RemovalEnabledCheckBox.IsChecked == true;
            _draft.UseFrequency = FrequencyCheckBox.IsChecked == true;
            _draft.UseIntensity = IntensityCheckBox.IsChecked == true;
            _draft.BlankLevel = BlankLevelCombo.SelectedIndex == 1 ? BlankLevelStatistic.Median : BlankLevelStatistic.Max;
            _draft.RequireBothRules = BothRulesRadio.IsChecked == true;
            _draft.PerPlate = PerPlateRadio.IsChecked == true;
            _draft.ExcludeBlankRunsFromAnalysis = ExcludeBlanksCheckBox.IsChecked == true;
            RefreshNegativeControls();
        }

        private void NcNumber_Changed(object sender, TextChangedEventArgs e)
        {
            if (_syncingNegativeControls || MinFractionBox == null || IntensityFactorBox == null || MinBlanksBox == null)
                return;
            var normal = new SolidColorBrush(Color.FromRgb(0xab, 0xad, 0xb3));
            var bad = new SolidColorBrush(Color.FromRgb(0xdc, 0x26, 0x26));
            bool changed = false;
            bool valid = true;

            if (TryParseNumber(MinFractionBox.Text, out var percent) && percent > 0 && percent <= 100)
            {
                MinFractionBox.BorderBrush = normal;
                if (Math.Abs(_draft.MinFraction - percent / 100) > 1e-12) { _draft.MinFraction = percent / 100; changed = true; }
            }
            else
            {
                MinFractionBox.BorderBrush = bad;
                valid = false;
            }

            if (TryParseNumber(IntensityFactorBox.Text, out var factor) && factor > 0)
            {
                IntensityFactorBox.BorderBrush = normal;
                if (Math.Abs(_draft.IntensityFactor - factor) > 1e-12) { _draft.IntensityFactor = factor; changed = true; }
            }
            else
            {
                IntensityFactorBox.BorderBrush = bad;
                valid = false;
            }

            if (int.TryParse(MinBlanksBox.Text, NumberStyles.Integer, CultureInfo.CurrentCulture, out var minBlanks) && minBlanks >= 1)
            {
                MinBlanksBox.BorderBrush = normal;
                if (_draft.MinBlanksPerScope != minBlanks) { _draft.MinBlanksPerScope = minBlanks; changed = true; }
            }
            else
            {
                MinBlanksBox.BorderBrush = bad;
                valid = false;
            }

            _numbersValid = valid;
            if (changed)
            {
                _numberDebounce.Stop();
                _numberDebounce.Start();
            }
            UpdateNegativeControlStatus(LoadedDataSource?.Invoke());
        }

        /// <summary>
        /// Leaves out the blanks with far more protein groups than the others: above the median plus 3 robust standard
        /// deviations (1.4826 x MAD) and above 1.5 times the median. On Isabele's project this picks the three blanks at
        /// 466 to 667 protein groups against a median of 166.
        /// </summary>
        private void SuggestOutliersButton_Click(object sender, RoutedEventArgs e)
        {
            var data = LoadedDataSource?.Invoke();
            if (data == null || _plan == null || _plan.BlankRuns.Count < 3)
            {
                NcSuggestText.Text = "Suggestions need at least 3 blank runs.";
                NcSuggestText.Visibility = Visibility.Visible;
                return;
            }
            var counts = _plan.BlankRuns.ToDictionary(r => r, r => data.ProteinCountPerFile.TryGetValue(r, out var n) ? n : 0);
            double median = MedianOf(counts.Values.Select(v => (double)v));
            double mad = MedianOf(counts.Values.Select(v => Math.Abs(v - median)));
            double bar = Math.Max(median + 3 * 1.4826 * mad, 1.5 * median);
            var outliers = counts.Where(kv => kv.Value > bar).OrderBy(kv => kv.Key, StringComparer.Ordinal).ToList();

            _draft.LeftOutRuns = outliers.Select(kv => kv.Key).ToList();
            NcSuggestText.Text = outliers.Count == 0
                ? $"No outlier: every blank has at most {bar:N0} protein groups (median {median:N0}). All blanks are used."
                : $"Left out {outliers.Count} blank(s) with {outliers.Min(kv => kv.Value):N0} to {outliers.Max(kv => kv.Value):N0} protein groups " +
                  $"(the other blanks have a median of {median:N0}). Check them in the plot before applying.";
            NcSuggestText.Visibility = Visibility.Visible;
            RefreshNegativeControls();

            static double MedianOf(IEnumerable<double> values)
            {
                var v = values.OrderBy(x => x).ToList();
                return v.Count == 0 ? 0 : v.Count % 2 == 1 ? v[v.Count / 2] : (v[v.Count / 2 - 1] + v[v.Count / 2]) / 2;
            }
        }

        private void UseAllBlanksButton_Click(object sender, RoutedEventArgs e)
        {
            _draft.LeftOutRuns.Clear();
            NcSuggestText.Visibility = Visibility.Collapsed;
            RefreshNegativeControls();
        }

        private async void ApplyNegativeControlsButton_Click(object sender, RoutedEventArgs e) =>
            await ApplyNegativeControlsAsync();

        /// <summary>
        /// Saves the negative controls being edited and asks the host to rebuild the analyses. False when there was
        /// nothing valid to apply or the save failed.
        /// </summary>
        public async Task<bool> ApplyNegativeControlsAsync()
        {
            if (string.IsNullOrEmpty(_databasePath) || !_numbersValid)
                return false;
            try
            {
                ApplyNegativeControlsButton.IsEnabled = false;
                // Blanks no longer in the data are not worth keeping; the conditions are kept as marked.
                var known = new HashSet<string>(_allRawFiles.Select(rf => rf.RawFileName), StringComparer.Ordinal);
                _draft.LeftOutRuns = _draft.LeftOutRuns.Where(known.Contains).Distinct(StringComparer.Ordinal).ToList();
                await _draft.SaveAsync(new ProjectDatabaseService(_databasePath));
                _savedSettings = _draft.Clone();
                // The host reprocesses and then reloads this tab, which shows what was applied.
                DiscardNegativeControlsButton.IsEnabled = false;
                NcStatusText.Text = "Saved. Reprocessing the analyses...";
                NcStatusText.Foreground = new SolidColorBrush(Color.FromRgb(0x47, 0x55, 0x69));
                NegativeControlsApplied?.Invoke(this, EventArgs.Empty);
                return true;
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Could not save the negative controls:\n\n{ex.Message}", "Negative Controls",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                UpdateNegativeControlStatus(LoadedDataSource?.Invoke());
                return false;
            }
        }

        private void DiscardNegativeControlsButton_Click(object sender, RoutedEventArgs e) => DiscardNegativeControlChanges();

        /// <summary>Drops the changes not applied: the tab shows the saved negative controls again.</summary>
        public void DiscardNegativeControlChanges()
        {
            _draft = _savedSettings.Clone();
            NcSuggestText.Visibility = Visibility.Collapsed;
            if (ConditionsListBox.ItemsSource is IEnumerable<ConditionListItem> items)
            {
                foreach (var item in items)
                    item.IsNegativeControl = _draft.IsNegativeControl(item.Name);
                var selected = ConditionsListBox.SelectedItem;
                ConditionsListBox.Items.Refresh();
                ConditionsListBox.SelectedItem = selected;
            }
            RefreshNegativeControls();
        }

        private void DetailTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            // The event bubbles from the combo box and the grid inside the tabs; only a tab change counts.
            if (!ReferenceEquals(e.OriginalSource, DetailTabs))
                return;
            // The negative-control tabs hold more than the blast radius: give them at least half of the area.
            if (DetailTabs.SelectedItem != BlastRadiusTab && StatsSplitter.Visibility == Visibility.Visible
                && PlotRow.Height.IsStar && StatsRow.Height.IsStar && StatsRow.Height.Value < PlotRow.Height.Value)
            {
                PlotRow.Height = new GridLength(1, GridUnitType.Star);
                StatsRow.Height = new GridLength(1, GridUnitType.Star);
            }
            if (DetailTabs.SelectedItem == BlankProteinsTab && _blankProteinsStale)
                RefreshBlankProteins();
        }

        private void RefreshBlankProteins()
        {
            var data = LoadedDataSource?.Invoke();
            _blankProteinsStale = false;
            if (data == null || _plan == null || _plan.Scopes.Count == 0)
            {
                _blankProteins = new List<BlankProteinView>();
                BlankProteinsGrid.ItemsSource = _blankProteins;
                BlankProteinsSummaryText.Text = _draft.Conditions.Count == 0
                    ? "Mark a condition as a negative control to list the proteins of its blanks."
                    : "No blank run is selected.";
                return;
            }
            _blankProteins = BackgroundRemovalService.DescribeBlankProteins(data, _plan)
                .OrderByDescending(r => r.BlankFraction)
                .ThenByDescending(r => r.BlankLevel)
                .Select(r => new BlankProteinView { Row = r })
                .ToList();
            ApplyBlankProteinFilter();
        }

        private void ApplyBlankProteinFilter()
        {
            string filter = BlankProteinFilterBox.Text.Trim();
            var shown = filter.Length == 0
                ? _blankProteins
                : _blankProteins.Where(v => v.Protein.Contains(filter, StringComparison.OrdinalIgnoreCase)
                                            || v.Genes.Contains(filter, StringComparison.OrdinalIgnoreCase)).ToList();
            BlankProteinsGrid.ItemsSource = shown;
            int removed = _blankProteins.Count(v => v.Status == "Removed");
            int partly = _blankProteins.Count(v => v.Status == "Partly removed");
            BlankProteinsSummaryText.Text =
                $"{_blankProteins.Count:N0} protein rows in the selected blanks ({_plan?.Scopes.Count ?? 0} plate scope(s)); " +
                $"{removed:N0} removed, {partly:N0} partly removed with these settings" + (_draft.RemovalEnabled ? "" : " (if switched on)") +
                (filter.Length > 0 ? $"; {shown.Count:N0} shown" : "") + ".";
        }

        private void BlankProteinFilterBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (BlankProteinFilterWatermark != null)
                BlankProteinFilterWatermark.Visibility = BlankProteinFilterBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            if (!_blankProteinsStale)
                ApplyBlankProteinFilter();
        }

        private void ExportBlankProteinsButton_Click(object sender, RoutedEventArgs e)
        {
            if (_blankProteinsStale)
                RefreshBlankProteins();
            if (_blankProteins.Count == 0)
            {
                MessageBox.Show("There are no blank proteins to export.", "Export", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            var dialog = new SaveFileDialog
            {
                Filter = "CSV file|*.csv",
                FileName = "negative_control_proteins.csv"
            };
            if (dialog.ShowDialog(Window.GetWindow(this)) != true)
                return;
            try
            {
                var inv = CultureInfo.InvariantCulture;
                string Q(string s) => s.IndexOfAny(new[] { ',', '"', '\n', '\r' }) >= 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
                string N(double v) => double.IsNaN(v) ? "" : v.ToString("R", inv);
                var sb = new StringBuilder();
                sb.AppendLine("protein,genes,plate,blank_detections,selected_blanks,blank_fraction,blank_level,cell_detections,cell_runs,median_cell_to_blank,cell_kept,status");
                foreach (var v in _blankProteins)
                {
                    var r = v.Row;
                    sb.AppendLine(string.Join(",", Q(r.Protein), Q(r.Genes), Q(r.Scope), r.BlankDetections.ToString(inv), r.SelectedBlanks.ToString(inv),
                        N(r.BlankFraction), N(r.BlankLevel), r.CellDetections.ToString(inv), r.CellRuns.ToString(inv),
                        N(r.MedianCellToBlank), r.CellKept.ToString(inv), Q(r.Status)));
                }
                File.WriteAllText(dialog.FileName, sb.ToString(), new UTF8Encoding(false));
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Could not write the file:\n\n{ex.Message}", "Export", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // ------------------------------------------------------------------------------------------------------------
        // Cascade delete
        // ------------------------------------------------------------------------------------------------------------

        private async void DeleteConditionButton_Click(object sender, RoutedEventArgs e)
        {
            if (_parquetService == null || string.IsNullOrEmpty(_databasePath))
                return;

            if (ConditionsListBox.SelectedItem is not ConditionListItem selected)
                return;

            string condition = selected.Name;

            try
            {
                // Re-fetch stats fresh so the dialog never shows stale numbers.
                var stats = await _parquetService.GetConditionStatsAsync(condition);
                if (stats.RawFileCount == 0)
                {
                    MessageBox.Show(
                        $"No raw files are currently labeled '{condition}'. Nothing to delete.",
                        "Nothing To Delete",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                    await PopulateConditionsListAsync();
                    return;
                }

                var backupService = new ProjectBackupService();
                string reason = $"before-delete-{condition}";
                // Build the backup path ONCE so the dialog and the actual backup folder match exactly.
                string backupPath = backupService.BuildBackupPath(_databasePath, reason);
                long projectSize = await backupService.EstimateProjectSizeAsync(_databasePath);

                var dialog = new DeleteConditionConfirmationDialog(condition, stats, backupPath, projectSize)
                {
                    Owner = Window.GetWindow(this)
                };

                if (dialog.ShowDialog() != true || !dialog.Confirmed)
                    return;

                // 1. Backup first. Abort the whole flow if backup fails.
                DeleteConditionButton.IsEnabled = false;
                var backupResult = await backupService.CreateProjectBackupAsync(_databasePath, backupPath);
                if (!backupResult.Success)
                {
                    MessageBox.Show(
                        $"Backup failed; the delete was NOT performed.\n\n{backupResult.ErrorMessage}",
                        "Backup Failed",
                        MessageBoxButton.OK,
                        MessageBoxImage.Error);
                    DeleteConditionButton.IsEnabled = true;
                    return;
                }

                // 2. Cascade delete in a transaction.
                DeleteConditionResult deleteResult;
                try
                {
                    deleteResult = await _parquetService.DeleteConditionCascadeAsync(condition);
                }
                catch (Exception delEx)
                {
                    MessageBox.Show(
                        $"Backup succeeded at:\n{backupResult.BackupPath}\n\n" +
                        $"But the cascade delete failed and was rolled back:\n{delEx.Message}",
                        "Delete Failed (Backup OK)",
                        MessageBoxButton.OK,
                        MessageBoxImage.Error);
                    DeleteConditionButton.IsEnabled = true;
                    return;
                }

                // 3. A deleted negative control is no longer one: the reload below must not look for its blanks.
                if (_savedSettings.IsNegativeControl(condition) || _draft.IsNegativeControl(condition))
                {
                    _savedSettings.Conditions.RemoveAll(c => string.Equals(c, condition, StringComparison.Ordinal));
                    await _savedSettings.SaveAsync(new ProjectDatabaseService(_databasePath));
                    _draft.Conditions.RemoveAll(c => string.Equals(c, condition, StringComparison.Ordinal));
                }

                // 4. Refresh local UI.
                await PopulateConditionsListAsync();
                ResetDetailsPane();

                // 5. Show summary BEFORE bubbling the event so the user sees confirmation
                //    before the parent's reload spinner takes over the screen.
                MessageBox.Show(
                    $"Condition '{condition}' was deleted.\n\n" +
                    $"Removed:\n" +
                    $"  Raw files: {deleteResult.DeletedRawFiles:N0}\n" +
                    $"  Protein quant rows: {deleteResult.DeletedProteinQuantRows:N0}\n" +
                    $"  Cell type classifications: {deleteResult.DeletedClassifications:N0}\n" +
                    $"  Run exclusions: {deleteResult.DeletedExclusions:N0}\n" +
                    $"  Orphan parquet imports: {deleteResult.DeletedOrphanImports:N0}\n\n" +
                    $"Backup saved to:\n{backupResult.BackupPath}",
                    "Delete Complete",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);

                // 6. Tell parents to refresh their own dependent state (plate browser, analysis tabs).
                ConditionDeleted?.Invoke(this, new ConditionDeletedEventArgs
                {
                    Condition = condition,
                    DeleteResult = deleteResult,
                    BackupPath = backupResult.BackupPath
                });
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    $"Unexpected error while deleting condition:\n\n{ex.Message}",
                    "Delete Error",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
                DeleteConditionButton.IsEnabled = true;
            }
        }
    }
}
