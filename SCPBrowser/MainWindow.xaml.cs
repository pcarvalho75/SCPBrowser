using Microsoft.Win32;
using SCPBrowser.GOTools;
using SCPBrowser.Models;
using SCPBrowser.Services;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using BioTessera.Core.Models;

namespace SCPBrowser
{
    public partial class MainWindow : Window
    {
        // Routed command for the F5 / menu "Reload Project Data" action.
        public static readonly System.Windows.Input.RoutedCommand ReloadProjectDataCommand =
            new System.Windows.Input.RoutedCommand("ReloadProjectData", typeof(MainWindow));

        // Project management fields
        private string _currentProjectPath;
        private ProjectDatabaseService _projectDatabaseService;
        private ParquetDataService _parquetService;
        private PlateService _plateService;
        private CellTypeClassificationService _cellTypeService;
        private bool _hasOpenProject = false;
        private string _projectReferenceDatabasePath;
        private GoTermResolver _goTermResolver;
        private BioTessera.GO.GoStatusService _goStatusService;
        private bool _bioTesseraNeedsUpdate = false;
        private System.Windows.Threading.DispatcherTimer _proteinCutoffDebounceTimer;
        private const int ProteinCutoffDebounceDelayMs = 300;
        private const string SETTING_PROTEIN_CUTOFF = "ProteinCutoff";
        private const string SETTING_UPPER_PROTEIN_CUTOFF = "UpperProteinCutoff";
        private DataFilterService _dataFilterService;
        private int _pendingProteinCutoff;
        private CancellationTokenSource _projectCts;


        // Public properties for controls to access
        public bool HasOpenProject => _hasOpenProject;
        public string ProjectReferenceDatabasePath => _projectReferenceDatabasePath;

        public MainWindow()
        {
            InitializeComponent();

            // Migrate settings from previous version after ClickOnce update
            if (Settings.Default.UpgradeRequired)
            {
                Settings.Default.Upgrade();
                Settings.Default.UpgradeRequired = false;
                Settings.Default.Save();
            }

            // Subscribe to PeptideTicTab events
            PeptideTicTab.CellTypePredictionsRequested += PeptideTicTab_CellTypePredictionsRequested;
            PeptideTicTab.SelectionChangedForBioTessera += PeptideTicTab_SelectionChangedForBioTessera;
            PeptideTicTab.RunInclusionChanged += PeptideTicTab_RunInclusionChanged;
            PeptideTicTab.ClearAllExclusionsRequested += PeptideTicTab_ClearAllExclusionsRequested;
            PeptideTicTab.ExportDiagnosticsRequested += PeptideTicTab_ExportDiagnosticsRequested;
            PeptideTicTab.ContaminantRatioCutoffChanged += PeptideTicTab_ContaminantRatioCutoffChanged;

            // Subscribe to ProjectBrowser reclassify request
            ProjectBrowserDialog.ReclassifyRequested += ProjectBrowserDialog_ReclassifyRequested;

            // Subscribe to marker-only classification so we can unlock + colour the scatter from the saved results.
            ProjectBrowserDialog.MarkerCellsClassified += ProjectBrowserDialog_MarkerCellsClassified;

            // Subscribe to ProjectBrowser cascade-delete completion so we can reload data
            ProjectBrowserDialog.ConditionDeleted += ProjectBrowserDialog_ConditionDeleted;

            // Subscribe to Settings changes
            SettingsDialog.SettingsSaved += (s, args) => PeptideTicTab.ApplyConfidenceThresholdFromSettings();

            _goTermResolver = new GoTermResolver(9606); // Human default
            UpdateWindowTitle();

            // Build the protein-cutoff debounce timer once so rapid slider drags
            // don't accumulate Tick closures.
            _proteinCutoffDebounceTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(ProteinCutoffDebounceDelayMs)
            };
            _proteinCutoffDebounceTimer.Tick += async (s, e) =>
            {
                _proteinCutoffDebounceTimer.Stop();
                await ApplyProteinCutoffAsync(_pendingProteinCutoff);
            };

            // CRITICAL: Ensure loading overlay is hidden on startup
            LoadingOverlay.Hide();

            // Load recent projects
            _ = LoadRecentProjectsUI();

            // Check GO database status
            CheckGoStatus();

            Console.Clear();
        }

        // ==================== PROJECT MANAGEMENT ====================


        private async void NewProject_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new NewProjectDialog
            {
                Owner = this
            };

            if (dialog.ShowDialog() == true)
            {
                try
                {
                    string projectDbPath = Path.Combine(dialog.ProjectLocation, "project.db");
                    await OpenProjectAsync(projectDbPath);

                    MessageBox.Show(
                        $"Project '{dialog.ProjectName}' created successfully!",
                        "Project Created",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                }
                catch (Exception ex)
                {
                    MessageBox.Show(
                        $"Error opening project:\n\n{ex.Message}",
                        "Error",
                        MessageBoxButton.OK,
                        MessageBoxImage.Error);
                }
            }
        }

        private async void OpenProject_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFileDialog
            {
                Filter = "Project Database (project.db)|project.db|All Database Files (*.db)|*.db",
                Title = "Open SCP Browser Project"
            };

            if (dialog.ShowDialog() == true)
            {
                await OpenProjectAsync(dialog.FileName);
            }
        }

        private async Task OpenProjectAsync(string projectDbPath)
        {
            try
            {
                // Cancel any previous project's in-flight operations
                _projectCts?.Cancel();
                _projectCts = new CancellationTokenSource();
                var ct = _projectCts.Token;
                LoadingOverlay.SetMessage("Opening Project");
                LoadingOverlay.SetProgress("Loading project information...");
                LoadingOverlay.Show();

                // Allow UI to render the overlay
                await Task.Delay(50);

                _currentProjectPath = projectDbPath;

                // Run service initialization and database work on background thread
                ProjectInfo projectInfo = null;
                List<string> allImportedFiles = null;

                await Task.Run(async () =>
                {
                    // Initialize all services
                    _projectDatabaseService = new ProjectDatabaseService(projectDbPath);
                    _parquetService = new ParquetDataService(projectDbPath);
                    _plateService = new PlateService(projectDbPath);
                    _cellTypeService = new CellTypeClassificationService(projectDbPath);
                    _dataFilterService = new DataFilterService();

                    // Ensure new tables exist (migration for existing projects)
                    await _projectDatabaseService.EnsureCellTypeClassificationsTableExistsAsync();

                    await _projectDatabaseService.EnsureExcludedRunsTableExistsAsync();
                    await _projectDatabaseService.EnsureCellenOneTablesExistAsync();

                    // Load project info
                    projectInfo = await _projectDatabaseService.GetProjectInfoAsync();

                    // Get ALL imported files
                    allImportedFiles = await _parquetService.GetAllImportedParquetFilesAsync();
                });

                if (projectInfo == null)
                {
                    throw new Exception("Invalid project database. Project information not found.");
                }

                _hasOpenProject = true;

                // The project database IS the reference database (unified)
                _projectReferenceDatabasePath = projectDbPath;

                // Update UI (must be on UI thread)
                WelcomeScreen.Visibility = Visibility.Collapsed;
                MainTabControl.Visibility = Visibility.Visible;
                ImportPlateMetadataMenuItem.IsEnabled = true;
                ImportCellenOneRunMenuItem.IsEnabled = true;
                ImportParquetMenuItem.IsEnabled = true;
                ImportGeneMatrixMenuItem.IsEnabled = true;
                EvaluateClassifierMenuItem.IsEnabled = true;
                ConfidenceMapMenuItem.IsEnabled = true;
                ImportFastaMenuItem.IsEnabled = true;
                ImportOmicProfileMenuItem.IsEnabled = true;
                CloseProjectMenuItem.IsEnabled = true;
                ClearCellTypeClassificationsMenuItem.IsEnabled = true;
                ReloadProjectDataMenuItem.IsEnabled = true;

                // Load and show plate filter control
                LoadingOverlay.SetProgress("Loading plates...");
                await PlateFilterControl.LoadPlatesAsync(projectDbPath);
                PeptideTicTab.SetPlateColorMap(PlateFilterControl.GetPlateColorMap());
                MainControlTab.SetPlateColorMap(PlateFilterControl.GetPlateColorMapById());
                await _dataFilterService.LoadPlateMappingAsync(_parquetService, _plateService);
                PlateFilterControl.Visibility = Visibility.Visible;


                // Subscribe to events
                PlateFilterControl.PlateSelectionChanged += PlateFilterControl_PlateSelectionChanged;
                PlateFilterControl.PlateColorChanged += PlateFilterControl_PlateColorChanged;
                MainControlTab.ProteinCutoffChanged += MainControlTab_ProteinCutoffChanged;
                MainControlTab.MaxProteinCutoffChanged += MainControlTab_MaxProteinCutoffChanged;
                MainControlTab.RunExcludeRequested += MainControlTab_RunExcludeRequested;
                MainControlTab.RunRestoreRequested += MainControlTab_RunRestoreRequested;
                MainControlTab.ClearExclusionsRequested += MainControlTab_ClearExclusionsRequested;
                _dataFilterService.FilteredDataChanged += DataFilterService_FilteredDataChanged;

                // Load saved protein cutoff
                var savedCutoff = await _projectDatabaseService.GetSettingAsync(SETTING_PROTEIN_CUTOFF, "800");
                if (int.TryParse(savedCutoff, out int cutoffValue))
                {
                    MainControlTab.ProteinCutoff = cutoffValue;
                    _dataFilterService.ProteinCutoff = cutoffValue;
                }

                // Load saved upper protein cutoff
                var savedUpperCutoff = await _projectDatabaseService.GetSettingAsync(SETTING_UPPER_PROTEIN_CUTOFF, "99999");
                if (int.TryParse(savedUpperCutoff, out int upperCutoffValue))
                {
                    MainControlTab.MaxProteinCutoff = upperCutoffValue;
                    _dataFilterService.UpperProteinCutoff = upperCutoffValue;
                }

                UpdateWindowTitle(projectInfo.ProjectName);

                // Find and load ALL imported parquet files
                LoadingOverlay.SetProgress("Finding associated data...");

                List<string> parquetPaths = new List<string>();

                if (allImportedFiles != null && allImportedFiles.Count > 0)
                {
                    string projectDirectory = Path.GetDirectoryName(_currentProjectPath);

                    foreach (var fileName in allImportedFiles)
                    {
                        // Only use the pure filename component so a stored absolute
                        // path or ".." segment cannot escape the imports folder.
                        string safeName = Path.GetFileName(fileName ?? string.Empty);
                        if (string.IsNullOrEmpty(safeName))
                        {
                            System.Diagnostics.Debug.WriteLine($"[OpenProject] Skipping invalid imported filename: '{fileName}'");
                            continue;
                        }
                        string parquetPath = Path.Combine(projectDirectory, "imports", safeName);
                        bool fileExists = await Task.Run(() => File.Exists(parquetPath));

                        if (fileExists)
                        {
                            parquetPaths.Add(parquetPath);
                        }
                        else
                        {
                            System.Diagnostics.Debug.WriteLine($"[OpenProject] Imported parquet missing on disk: {parquetPath}");
                        }
                    }

                    if (parquetPaths.Count > 0)
                    {
                        LoadingOverlay.SetProgress($"Loading data from {parquetPaths.Count} file(s)...");
                    }
                    else
                    {
                        LoadingOverlay.Hide();
                        MessageBox.Show(
                            "No data files found in imports folder.\n\nPlease re-import your data.",
                            "Data Files Missing",
                            MessageBoxButton.OK,
                            MessageBoxImage.Warning);
                    }
                }
                else
                {
                    LoadingOverlay.SetProgress("No data imported yet. Please import a Parquet file.");
                }

                // Load data into MainControlTab
                // This will trigger the DataLoaded event which populates other tabs
                await MainControlTab.LoadDataFromProject(parquetPaths, _projectReferenceDatabasePath);

                LoadingOverlay.Hide();





                AddToRecentProjects(projectDbPath);

                // Seed per-project settings from global defaults if not yet present
                await SeedProjectSettingsAsync(_projectDatabaseService);

                SettingsDialog.SetDatabaseService(_projectDatabaseService);

                ProjectBrowserMenuItem.IsEnabled = true;
                ExportPLPMenuItem.IsEnabled = true;
            }
            catch (Exception ex)
            {
                LoadingOverlay.Hide();
                CloseProject();
                MessageBox.Show(
                    $"Error opening project:\n\n{ex.Message}",
                    "Error",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }

        private async void DataFilterService_FilteredDataChanged(object sender, EventArgs e)
        {
            try
            {
                if (!_hasOpenProject) return;
                // Capture services up-front so a mid-flight CloseProject that nulls
                // them doesn't NPE us further down.
                var filterService = _dataFilterService;
                var parquetService = _parquetService;
                if (filterService == null || parquetService == null) return;

                await RefreshAllTabsWithFilteredDataAsync();

                // Re-check after the await: project may have been closed while we
                // were suspended.
                if (!_hasOpenProject) return;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[DataFilterService_FilteredDataChanged] {ex}");
            }
        }

        private void MainControlTab_ProteinCutoffChanged(object sender, int newCutoff)
        {
            // Use debouncing to avoid excessive filter operations when dragging slider.
            // Timer and Tick handler are wired once in the constructor; here we just
            // update the pending value and restart the timer.
            _pendingProteinCutoff = newCutoff;
            _proteinCutoffDebounceTimer?.Stop();
            _proteinCutoffDebounceTimer?.Start();
        }

        private async Task ApplyProteinCutoffAsync(int newCutoff)
        {
            if (!_hasOpenProject) return;
            try
            {


                LoadingOverlay.SetMessage("Applying Filter");
                LoadingOverlay.SetProgress("Filtering by protein count...");
                LoadingOverlay.Show();

                // Allow UI to render the overlay
                await Task.Delay(50);

                // Save to database
                if (_projectDatabaseService != null)
                {
                    await _projectDatabaseService.SetSettingAsync(SETTING_PROTEIN_CUTOFF, newCutoff.ToString());
                }

                // Apply filters via service
                if (_dataFilterService.OriginalData != null)
                {
                    _dataFilterService.ProteinCutoff = newCutoff;
                    await _dataFilterService.ApplyFiltersAsync(_parquetService);
                }

                LoadingOverlay.Hide();
            }
            catch (Exception ex)
            {
                LoadingOverlay.Hide();
                System.Diagnostics.Debug.WriteLine($"[ApplyProteinCutoffAsync] {ex}");
                MessageBox.Show($"Error applying protein cutoff:\n\n{ex.Message}", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async void MainControlTab_MaxProteinCutoffChanged(object sender, int newUpperCutoff)
        {
            if (!_hasOpenProject) return;
            try
            {
                if (_projectDatabaseService != null)
                {
                    await _projectDatabaseService.SetSettingAsync(SETTING_UPPER_PROTEIN_CUTOFF, newUpperCutoff.ToString());
                }

                _dataFilterService.UpperProteinCutoff = newUpperCutoff;
                await _dataFilterService.ApplyFiltersAsync(_parquetService);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[MainControlTab_MaxProteinCutoffChanged] {ex}");
                MessageBox.Show($"Error applying upper protein cutoff:\n\n{ex.Message}", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async void MainControlTab_RunExcludeRequested(object sender, string rawFileName)
        {
            if (!_hasOpenProject) return;
            try
            {
                _dataFilterService.ExcludeRun(rawFileName);
                await _dataFilterService.ApplyFiltersAsync(_parquetService);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[MainControlTab_RunExcludeRequested] {ex}");
                MessageBox.Show($"Error excluding run:\n\n{ex.Message}", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async void MainControlTab_RunRestoreRequested(object sender, string rawFileName)
        {
            if (!_hasOpenProject) return;
            try
            {
                _dataFilterService.RestoreRun(rawFileName);
                await _dataFilterService.ApplyFiltersAsync(_parquetService);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[MainControlTab_RunRestoreRequested] {ex}");
                MessageBox.Show($"Error restoring run:\n\n{ex.Message}", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async void MainControlTab_ClearExclusionsRequested(object sender, EventArgs e)
        {
            if (!_hasOpenProject) return;
            try
            {
                _dataFilterService.ClearManualExclusions();
                await _dataFilterService.ApplyFiltersAsync(_parquetService);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[MainControlTab_ClearExclusionsRequested] {ex}");
                MessageBox.Show($"Error clearing exclusions:\n\n{ex.Message}", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async void PlateFilterControl_PlateSelectionChanged(object sender, PlateSelectionChangedEventArgs e)
        {
            try
            {
                if (!_hasOpenProject) return;


                if (_dataFilterService?.OriginalData == null)
                {

                    return;
                }

                _dataFilterService.SelectedPlateIds = e.SelectedPlateIds;
                await _dataFilterService.ApplyFiltersAsync(_parquetService);
            }
            catch (Exception ex)
            {

                await Dispatcher.InvokeAsync(() =>
                {
                    MessageBox.Show($"Error filtering data:\n\n{ex.Message}", "Error",
                        MessageBoxButton.OK, MessageBoxImage.Error);
                });
            }
        }

        private void PlateFilterControl_PlateColorChanged(object sender, PlateColorChangedEventArgs e)
        {
            if (!_hasOpenProject) return;



            // Propagate updated color map to PeptideTicControl and refresh its chart
            PeptideTicTab.UpdatePlateColors(e.FullColorMap);

            // Propagate updated color map to histogram/distribution via MainControl
            MainControlTab.SetPlateColorMap(PlateFilterControl.GetPlateColorMapById());

            // Redraw histogram and distribution with the new colors
            if (_dataFilterService?.FilteredData != null)
            {
                var dataForBarChart = _dataFilterService.PlateFilteredData ?? _dataFilterService.FilteredData;
                MainControlTab.UpdateChart(dataForBarChart, _dataFilterService.RawFileToPlateId, _dataFilterService.PlateIdToName);
            }
        }

        private async Task RefreshAllTabsWithFilteredDataAsync()
        {
            if (!_hasOpenProject || _dataFilterService?.FilteredData == null)
                return;



            // Bar chart shows plate-filtered data (all bars) with visual cutoff
            var dataForBarChart = _dataFilterService.PlateFilteredData ?? _dataFilterService.FilteredData;
            MainControlTab.SetExcludedRuns(_dataFilterService.ManuallyExcludedRuns);
            MainControlTab.UpdateChart(dataForBarChart, _dataFilterService.RawFileToPlateId, _dataFilterService.PlateIdToName);

            // Pass HVP results to PeptideTicTab before updating chart
            PeptideTicTab.SetHvpResults(_dataFilterService.HvpResults);

            // Pass plate mapping for batch effect correction (plate = batch)
            PeptideTicTab.SetPlateMapping(_dataFilterService.RawFileToPlateId);

            // PeptideTicTab gets protein-cutoff-filtered data (broader) so Explorer scatter
            // can show contaminant-excluded runs as grey dots.
            // PCA/UMAP will skip excluded runs via ContaminantRatioExcludedRuns in ScatterPlotOptions.
            var dataForExplorer = _dataFilterService.ProteinCutoffFilteredData ?? _dataFilterService.FilteredData;
            PeptideTicTab.SetContaminantRatioExcludedRuns(_dataFilterService.ContaminantRatioExcludedRuns);
            PeptideTicTab.UpdateChart(dataForExplorer, clearSelections: false);

            _bioTesseraNeedsUpdate = true;
        }

        private async Task UpdateBioTesseraTabAsync()
        {
            var data = _dataFilterService.FilteredData ?? _dataFilterService.OriginalData;
            
            if (data == null)
                return;

            try
            {
                // Get selected runs from PeptideTicTab (null means all runs)
                var selectedRuns = PeptideTicTab.GetSelectedRunNames();

                // Convert SCPBrowser data to BioTessera proteins
                var proteins = ProteomicsDataConverter.Convert(data, selectedRuns);

                if (proteins.Count == 0)
                {

                    return;
                }

                // Resolve GO terms from central database
                _goTermResolver.ResolveGoTerms(proteins);

                // Load into BioTessera and generate
                BioTesseraTab.LoadProteins(proteins);
                await BioTesseraTab.GenerateAsync();

                var runInfo = selectedRuns != null ? $" (filtered to {selectedRuns.Count} runs)" : "";

            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[BioTessera load/generate] {ex}");
            }
        }

        /// <summary>
        /// Initializes the protein coverage panel with FASTA path, parquet paths, and annotations.
        /// </summary>
        private async Task InitializeProteinCoverageAsync()
        {
            try
            {
                // Get FASTA path from project settings
                string fastaPath = await _projectDatabaseService.GetSettingAsync("fasta_path", "");

                if (string.IsNullOrEmpty(fastaPath) || !System.IO.File.Exists(fastaPath))
                {

                    return;
                }

                // Build parquet paths
                var allImportedFiles = await _parquetService.GetAllImportedParquetFilesAsync();
                var parquetPaths = new List<string>();
                string projectDirectory = Path.GetDirectoryName(_currentProjectPath);

                if (allImportedFiles != null)
                {
                    foreach (var fileName in allImportedFiles)
                    {
                        string safeName = Path.GetFileName(fileName ?? string.Empty);
                        if (string.IsNullOrEmpty(safeName)) continue;
                        string path = Path.Combine(projectDirectory, "imports", safeName);
                        if (System.IO.File.Exists(path))
                            parquetPaths.Add(path);
                    }
                }

                // Get annotations from ProteinMatrixTab
                var fastaService = new FastaParserService(_currentProjectPath);
                var annotations = await fastaService.GetAllAnnotationsAsync();

                // Initialize the coverage panel
                PeptideTicTab.InitializeProteinCoverage(fastaPath, parquetPaths, annotations);


            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[InitializeProteinCoverageAsync] {ex}");
            }
        }

        private async void ClearCellTypeClassifications_Click(object sender, RoutedEventArgs e)
        {
            var result = MessageBox.Show(
                "This will delete all saved cell type classifications.\n\n" +
                "Classifications will be recomputed next time you use the 'Color by Cell Type' feature.\n\n" +
                "Continue?",
                "Clear Classifications",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (result != MessageBoxResult.Yes)
                return;

            try
            {
                int? importId = await _parquetService.GetMostRecentImportIdAsync();
                if (!importId.HasValue)
                {
                    MessageBox.Show("No data imported.", "Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                await _cellTypeService.DeleteAllCellTypeClassificationsAsync(importId.Value);

                MessageBox.Show(
                    "Cell type classifications cleared successfully.\n\n" +
                    "Click 'Color by Cell Type' to recompute.",
                    "Success",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
            catch (Exception ex)
            {

                MessageBox.Show($"Error clearing classifications:\n\n{ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void CloseProject_Click(object sender, RoutedEventArgs e)
        {
            var result = MessageBox.Show(
                "Are you sure you want to close the current project?",
                "Close Project",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (result == MessageBoxResult.Yes)
            {
                CloseProject();
            }
        }

        private async Task SeedProjectSettingsAsync(ProjectDatabaseService db)
        {
            if (await db.GetSettingAsync("GOPValueCutoff") == null)
                await db.SetSettingAsync("GOPValueCutoff", Settings.Default.GOPValueCutoff.ToString());
            if (await db.GetSettingAsync("GOMinimumOverlap") == null)
                await db.SetSettingAsync("GOMinimumOverlap", Settings.Default.GOMinimumOverlap.ToString());
            if (await db.GetSettingAsync("ClassificationConfidenceThreshold") == null)
                await db.SetSettingAsync("ClassificationConfidenceThreshold", Settings.Default.ClassificationConfidenceThreshold.ToString());
        }

        private void CloseProject()        {
            // Cancel all in-flight async operations for this project
            _projectCts?.Cancel();
            _projectCts = null;

            // Unsubscribe from events to avoid double-subscription on next project open
            PlateFilterControl.PlateSelectionChanged -= PlateFilterControl_PlateSelectionChanged;
            PlateFilterControl.PlateColorChanged -= PlateFilterControl_PlateColorChanged;
            MainControlTab.ProteinCutoffChanged -= MainControlTab_ProteinCutoffChanged;
            MainControlTab.MaxProteinCutoffChanged -= MainControlTab_MaxProteinCutoffChanged;
            MainControlTab.RunExcludeRequested -= MainControlTab_RunExcludeRequested;
            MainControlTab.RunRestoreRequested -= MainControlTab_RunRestoreRequested;
            MainControlTab.ClearExclusionsRequested -= MainControlTab_ClearExclusionsRequested;
            if (_dataFilterService != null)
            {
                _dataFilterService.FilteredDataChanged -= DataFilterService_FilteredDataChanged;
                _dataFilterService.Clear();
            }

            _currentProjectPath = null;
            SettingsDialog.SetDatabaseService(null);
            _projectDatabaseService = null;
            _parquetService = null;
            _plateService = null;
            _cellTypeService = null;
            _dataFilterService = null;
            _hasOpenProject = false;
            _projectReferenceDatabasePath = null;

            // Reset UI
            WelcomeScreen.Visibility = Visibility.Visible;
            MainTabControl.Visibility = Visibility.Collapsed;
            ImportPlateMetadataMenuItem.IsEnabled = false;
            ImportCellenOneRunMenuItem.IsEnabled = false;
            ImportParquetMenuItem.IsEnabled = false;
            ImportGeneMatrixMenuItem.IsEnabled = false;
            EvaluateClassifierMenuItem.IsEnabled = false;
            ConfidenceMapMenuItem.IsEnabled = false;
            ImportOmicProfileMenuItem.IsEnabled = false;
            CloseProjectMenuItem.IsEnabled = false;
            ClearCellTypeClassificationsMenuItem.IsEnabled = false;
            ReloadProjectDataMenuItem.IsEnabled = false;

            ProjectBrowserMenuItem.IsEnabled = false;
            ExportPLPMenuItem.IsEnabled = false;
            ExportPLPControl.Visibility = Visibility.Collapsed;

            PlateFilterControl.Visibility = Visibility.Collapsed;

            UpdateWindowTitle();


        }

        private void UpdateWindowTitle(string projectName = null)
        {
            string version = Environment.GetEnvironmentVariable("ClickOnce_CurrentVersion")
                ?? System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString()
                ?? "?";

            if (string.IsNullOrEmpty(projectName))
            {
                Title = $"SCP Browser v{version}";
            }
            else
            {
                Title = $"SCP Browser v{version} - {projectName}";
            }
        }

        private async void ImportPlateMetadata_Click(object sender, RoutedEventArgs e)
        {
            if (!_hasOpenProject || _plateService == null)
            {
                MessageBox.Show(
                    "Please open or create a project first.",
                    "No Project Open",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            if (_cellenOneImportRunning)
            {
                MessageBox.Show("A cellenONE import is already running. Please wait for it to finish.",
                    "Import in progress", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var dialog = new NewPlateDialog { Owner = this };
            if (dialog.ShowDialog() != true)
                return;

            try
            {
                // Persist the plate first so we have a plate_id to attach cellenONE cells to.
                int plateId = await _plateService.CreatePlateAsync(dialog.PlateInfo);

                // Optionally import the attached cellenONE isolation run (cells + images) against this plate.
                // A fresh plate can never have a pre-existing run, so the returned id is always a new import.
                if (!string.IsNullOrEmpty(dialog.CellenOneRunDir))
                    await RunCellenOneImportAsync(dialog.CellenOneRunDir, plateId, _currentProjectPath);

                // Refresh the plate filter so the new plate is available immediately.
                await PlateFilterControl.LoadPlatesAsync(_currentProjectPath);

                string msg = string.IsNullOrEmpty(dialog.CellenOneRunDir)
                    ? $"Plate '{dialog.PlateInfo.PlateName}' registered."
                    : $"Plate '{dialog.PlateInfo.PlateName}' registered with {dialog.CellenOneCellCount} isolated cells.";
                MessageBox.Show(msg, "Plate Registered", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                LoadingOverlay.Hide();
                MessageBox.Show(
                    $"Error registering plate:\n\n{ex.Message}",
                    "Error",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }

        /// <summary>True while a cellenONE import is writing; blocks a second one from starting.</summary>
        private bool _cellenOneImportRunning;

        /// <summary>
        /// Imports a cellenONE isolation run against an existing plate id, behind the shared wait screen, and
        /// returns the cellenone_run_id. Both entry points (new-plate attach and the "import into existing plate"
        /// route) funnel through here so the import behaves identically either way.
        ///
        /// The returned id is an EXISTING id when the importer deduplicated the run instead of writing it, so
        /// callers that report an outcome must compare it against the ids known before the import.
        ///
        /// The wait screen sits inside the content grid and does NOT cover the menu bar, and the import runs on a
        /// background thread, so the Import menu items are disabled explicitly for the duration - otherwise a
        /// second import can start mid-write and the two race on the same SQLite file (and the first to finish
        /// hides the overlay out from under the other).
        /// </summary>
        private async Task<int> RunCellenOneImportAsync(string runDir, int plateId, string projectPath)
        {
            LoadingOverlay.SetMessage("Importing cellenONE data");
            LoadingOverlay.SetProgress("Reading run...");
            LoadingOverlay.Show();
            // Let the overlay paint before the heavy work starts.
            await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Render);

            _cellenOneImportRunning = true;
            ImportPlateMetadataMenuItem.IsEnabled = false;
            ImportCellenOneRunMenuItem.IsEnabled = false;
            try
            {
                var importer = new CellenOneImportService(projectPath);
                var progress = new Progress<string>(msg => LoadingOverlay.SetProgress(msg));
                // Run the import (file parse + 800 image decodes/thumbnails + ~65 MB write) on a background
                // thread so the UI thread stays free to render and animate the overlay. The import's WPF
                // imaging works on frozen bitmaps, which is safe off the UI thread.
                return await Task.Run(() => importer.ImportRunAsync(runDir, plateId, progress));
            }
            finally
            {
                _cellenOneImportRunning = false;
                ImportPlateMetadataMenuItem.IsEnabled = _hasOpenProject;
                ImportCellenOneRunMenuItem.IsEnabled = _hasOpenProject;
                LoadingOverlay.Hide();
            }
        }

        /// <summary>
        /// Attaches a cellenONE isolation run to a plate that already exists — the route for when the isolation
        /// data arrives after the plate was registered (NewPlateDialog can only attach a run at creation time).
        /// </summary>
        private async void ImportCellenOneRun_Click(object sender, RoutedEventArgs e)
        {
            if (!_hasOpenProject || _currentProjectPath == null)
            {
                MessageBox.Show("Please open or create a project first.", "No Project Open",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (_cellenOneImportRunning)
            {
                MessageBox.Show("A cellenONE import is already running. Please wait for it to finish.",
                    "Import in progress", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var dialog = new ImportCellenOneRunDialog { Owner = this };

            bool hasPlates;
            try
            {
                hasPlates = await dialog.InitializeAsync(_currentProjectPath);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Could not load the project's plates:\n\n{ex.Message}", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            if (!hasPlates)
            {
                MessageBox.Show(
                    "This project has no plates yet, so there is nothing to attach a run to.\n\n" +
                    "Use Import ▸ Import Plate Metadata... to register a plate first — that dialog can also " +
                    "attach the cellenONE run at the same time.",
                    "No plates",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            if (dialog.ShowDialog() != true || string.IsNullOrEmpty(dialog.RunDir))
                return;

            // Pin the project this import belongs to: the user can close or switch projects while it runs, and the
            // follow-up UI must not be applied to a different project than the one written to.
            string projectPath = _currentProjectPath;
            var knownRunIds = dialog.KnownRunIds;

            try
            {
                int runId = await RunCellenOneImportAsync(dialog.RunDir, dialog.SelectedPlateId, projectPath);

                // The importer returns an EXISTING run id when it deduplicated instead of writing, so report what
                // actually happened rather than assuming the cells were added.
                bool wasSkipped = knownRunIds.Contains(runId);

                // Project closed or switched while the import ran (null _currentProjectPath compares unequal here).
                if (!string.Equals(projectPath, _currentProjectPath, StringComparison.OrdinalIgnoreCase))
                {
                    MessageBox.Show(
                        wasSkipped
                            ? "That run was already imported, so nothing was added. (The project has since been closed or changed.)"
                            : $"Imported {dialog.CellCount} isolated cells into '{dialog.SelectedPlateName}'. " +
                              "(The project has since been closed or changed, so nothing was refreshed here.)",
                        "cellenONE Import Complete", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                if (wasSkipped)
                {
                    MessageBox.Show(
                        $"This run is already imported for '{dialog.SelectedPlateName}' — it was deduplicated and " +
                        "nothing was added. The existing cells were left untouched.",
                        "Already Imported", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                // The importer leaves isolated_cells.raw_file_id NULL on purpose - the reconcile step links cells
                // to their MS runs. On this route the MS data is usually already imported, so reconciling is
                // immediately actionable and the import is only half-useful without it.
                var answer = MessageBox.Show(
                    $"Imported {dialog.CellCount} isolated cells into '{dialog.SelectedPlateName}'.\n\n" +
                    "These cells are not yet linked to their MS runs. Open Reconcile Cells ↔ Runs now?",
                    "cellenONE Import Complete",
                    MessageBoxButton.YesNo, MessageBoxImage.Information);

                // Preselect the run just imported. Without this the reconcile dialog opens on whichever run has the
                // newest acquisition date - typically NOT the late-arriving run imported here - and its Apply
                // clears that other run's links before writing, discarding existing (possibly hand-made) matches.
                if (answer == MessageBoxResult.Yes)
                    OpenReconcileDialog(runId);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error importing cellenONE run:\n\n{ex.Message}", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async void ImportParquet_Click(object sender, RoutedEventArgs e)
        {
            if (!_hasOpenProject || _parquetService == null)
            {
                MessageBox.Show(
                    "Please open or create a project first.",
                    "No Project Open",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            // Keep formats separate: a project mixing parquet + xlsx would load only one of them. Block the mix.
            var existingXlsxCheck = await _parquetService.GetAllImportedParquetFilesAsync();
            if (existingXlsxCheck != null && existingXlsxCheck.Any(f => f.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase)))
            {
                MessageBox.Show(
                    "This project already contains an Excel gene-matrix import, which can't be combined with DIA-NN parquet data.\n\nPlease create a new project for parquet imports.",
                    "Cannot mix import formats",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            string projectDirectory = Path.GetDirectoryName(_currentProjectPath);

            var dialog = new ImportParquetDialog(
                _parquetService,
                _plateService,
                projectDirectory)
            {
                Owner = this
            };

            if (dialog.ShowDialog() == true)
            {
                // Refresh UI after successful import
                try
                {
                    LoadingOverlay.SetMessage("Refreshing Data");
                    LoadingOverlay.SetProgress("Reloading plates...");
                    LoadingOverlay.Show();

                    // Refresh plate filter
                    await PlateFilterControl.LoadPlatesAsync(_currentProjectPath);
                    PeptideTicTab.SetPlateColorMap(PlateFilterControl.GetPlateColorMap());
                    MainControlTab.SetPlateColorMap(PlateFilterControl.GetPlateColorMapById());

                    // Get ALL imported parquet files and reload data
                    var allImportedFiles = await _parquetService.GetAllImportedParquetFilesAsync();
                    if (allImportedFiles != null && allImportedFiles.Count > 0)
                    {
                        List<string> parquetPaths = new List<string>();
                        foreach (var fileName in allImportedFiles)
                        {
                            string parquetPath = Path.Combine(projectDirectory, "imports", fileName);
                            if (File.Exists(parquetPath))
                            {
                                parquetPaths.Add(parquetPath);
                            }
                        }

                        if (parquetPaths.Count > 0)
                        {
                            LoadingOverlay.SetProgress($"Loading {parquetPaths.Count} parquet file(s)...");
                            await MainControlTab.LoadDataFromProject(parquetPaths, _projectReferenceDatabasePath);
                        }
                    }

                    LoadingOverlay.Hide();
                }
                catch (Exception ex)
                {
                    LoadingOverlay.Hide();
                    MessageBox.Show(
                        $"Data imported but error refreshing display:\n\n{ex.Message}\n\nTry closing and reopening the project.",
                        "Refresh Error",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                }
            }
        }

        /// <summary>
        /// Cross-validated benchmark of the classifier against the ground-truth biological conditions.
        /// Read-only with respect to the project: the dialog builds every fold's reference in memory and never
        /// writes the reference DB or the classifications table.
        /// </summary>
        private void EvaluateClassifier_Click(object sender, RoutedEventArgs e)
        {
            var data = MainControlTab?.GetCurrentData();
            if (data == null || data.RawFileNames == null || data.RawFileNames.Count == 0)
            {
                MessageBox.Show(
                    "Load a project with imported data first — the benchmark runs on the data currently loaded.",
                    "Evaluate Classifier", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var dialog = new ClassifierEvaluationDialog(data) { Owner = this };
            dialog.ShowDialog();
        }

        /// <summary>
        /// Generates the Classification Confidence figure (Mode B) for the loaded dataset using the project's
        /// selected classifier, writes a self-contained HTML report + a standalone SVG, and opens the report.
        /// Read-only: builds references in memory, persists nothing.
        /// </summary>
        private async void ConfidenceMap_Click(object sender, RoutedEventArgs e)
        {
            var data = MainControlTab?.GetCurrentData();
            if (data == null || data.RawFileNames == null || data.RawFileNames.Count == 0)
            {
                MessageBox.Show("Load a project with imported data first.", "Classification Confidence Map",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            try
            {
                System.Windows.Input.Mouse.OverrideCursor = System.Windows.Input.Cursors.Wait;
                LoadingOverlay.SetMessage("Building confidence map…");
                LoadingOverlay.SetProgress("Scoring cells and running held-out validation…");
                LoadingOverlay.Show();

                // Resolve the method exactly as the classifier manager does: explicit setting → stored method →
                // Quantitative, so the figure matches what the app actually classifies with.
                string method = "Quantitative";
                if (!string.IsNullOrEmpty(_projectReferenceDatabasePath))
                {
                    method = await new ProjectDatabaseService(_projectReferenceDatabasePath)
                        .GetSettingAsync("classification_method", null);
                    if (string.IsNullOrEmpty(method))
                        method = await new CellTypeClassificationService(_projectReferenceDatabasePath)
                            .GetStoredScorerMethodAsync() ?? "Quantitative";
                }

                // Score with the same key markers / excluded types / priors the live classifier uses, so the map's
                // predicted labels and held-out ticks agree with the app rather than showing an unaided classifier.
                var keyMarkers = ProjectBrowserDialog?.GetKeyMarkers();
                var excludedCellTypes = ProjectBrowserDialog?.GetExcludedCellTypes();
                var priorWeights = ProjectBrowserDialog?.GetPriorWeights();

                var result = await System.Threading.Tasks.Task.Run(() =>
                    Services.ConfidenceMapBuilder.Compute(data, method, null, keyMarkers, excludedCellTypes, priorWeights));

                LoadingOverlay.Hide();
                System.Windows.Input.Mouse.OverrideCursor = null;

                var dlg = new Microsoft.Win32.SaveFileDialog
                {
                    Filter = "HTML figure (*.html)|*.html",
                    FileName = "classification_confidence.html",
                    Title = "Save classification confidence figure"
                };
                if (dlg.ShowDialog() != true) return;

                string html = Services.ConfidenceMapBuilder.BuildHtml(result,
                    System.IO.Path.GetFileNameWithoutExtension(_currentProjectPath));
                System.IO.File.WriteAllText(dlg.FileName, html, System.Text.Encoding.UTF8);

                // Sibling standalone SVG of the heatmap for vector editing / PNG conversion.
                string svgPath = System.IO.Path.ChangeExtension(dlg.FileName, ".svg");
                System.IO.File.WriteAllText(svgPath, Services.ConfidenceMapBuilder.HeatmapStandaloneSvg(result), System.Text.Encoding.UTF8);

                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(dlg.FileName) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                System.Windows.Input.Mouse.OverrideCursor = null;
                MessageBox.Show("Could not build the confidence map:" + Environment.NewLine + Environment.NewLine + ex.Message,
                    "Classification Confidence Map", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally { LoadingOverlay.Hide(); System.Windows.Input.Mouse.OverrideCursor = null; }
        }

        private async void ImportGeneMatrix_Click(object sender, RoutedEventArgs e)
        {
            if (!_hasOpenProject || _parquetService == null)
            {
                MessageBox.Show(
                    "Please open or create a project first.",
                    "No Project Open",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            // Keep formats separate: a project mixing parquet + xlsx would load only one of them. Block the mix.
            var existingImports = await _parquetService.GetAllImportedParquetFilesAsync();
            if (existingImports != null && existingImports.Any(f => !f.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase)))
            {
                MessageBox.Show(
                    "This project already contains DIA-NN parquet imports. Excel gene-matrix intensities aren't comparable to parquet data, so the two can't be combined in one project.\n\nPlease create a new project for the Excel gene matrix.",
                    "Cannot mix import formats",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            string projectDirectory = Path.GetDirectoryName(_currentProjectPath);

            var dialog = new ImportGeneMatrixDialog(_parquetService, _plateService, projectDirectory)
            {
                Owner = this
            };

            if (dialog.ShowDialog() == true)
            {
                // Reuse the exact parquet post-import refresh path (the reload glob is format-agnostic).
                try
                {
                    LoadingOverlay.SetMessage("Refreshing Data");
                    LoadingOverlay.SetProgress("Reloading plates...");
                    LoadingOverlay.Show();

                    await PlateFilterControl.LoadPlatesAsync(_currentProjectPath);
                    PeptideTicTab.SetPlateColorMap(PlateFilterControl.GetPlateColorMap());
                    MainControlTab.SetPlateColorMap(PlateFilterControl.GetPlateColorMapById());

                    var allImportedFiles = await _parquetService.GetAllImportedParquetFilesAsync();
                    if (allImportedFiles != null && allImportedFiles.Count > 0)
                    {
                        var importPaths = new List<string>();
                        foreach (var fileName in allImportedFiles)
                        {
                            string path = Path.Combine(projectDirectory, "imports", fileName);
                            if (File.Exists(path))
                                importPaths.Add(path);
                        }

                        if (importPaths.Count > 0)
                        {
                            LoadingOverlay.SetProgress($"Loading {importPaths.Count} import(s)...");
                            await MainControlTab.LoadDataFromProject(importPaths, _projectReferenceDatabasePath);
                        }
                    }

                    LoadingOverlay.Hide();
                }
                catch (Exception ex)
                {
                    LoadingOverlay.Hide();
                    MessageBox.Show(
                        $"Data imported but error refreshing display:\n\n{ex.Message}\n\nTry closing and reopening the project.",
                        "Refresh Error",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                }
            }
        }

        // ==================== EXISTING MENU HANDLERS ====================

        private void CellViewer_Click(object sender, RoutedEventArgs e)
        {
            if (!_hasOpenProject || _currentProjectPath == null)
            {
                MessageBox.Show("Please open or create a project first.", "No Project Open", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var viewer = new Controls.CellPlateViewerControl();
            var window = new Window
            {
                Title = "Cell Plate Viewer",
                Content = viewer,
                Width = 1150,
                Height = 740,
                Owner = this,
                WindowStartupLocation = WindowStartupLocation.CenterOwner
            };
            viewer.Initialize(_currentProjectPath);
            window.Show();
        }

        private void ReconcileCells_Click(object sender, RoutedEventArgs e)
        {
            if (!_hasOpenProject || _currentProjectPath == null)
            {
                MessageBox.Show("Please open or create a project first.", "No Project Open", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            OpenReconcileDialog();
        }

        /// <summary>
        /// Opens the cell/run reconcile dialog, optionally preselecting a run. Preselection matters after a
        /// cellenONE import: the dialog otherwise defaults to the newest-acquired run, and its Apply clears the
        /// selected run's links before writing.
        /// </summary>
        private void OpenReconcileDialog(int? preselectRunId = null)
        {
            var dlg = new ReconcileCellsDialog { Owner = this };
            dlg.Initialize(_currentProjectPath, preselectRunId);
            dlg.ShowDialog();
        }

        private void GoDatabase_Click(object sender, RoutedEventArgs e)
        {
            var manager = new BioTessera.GoAnnotationManager();
            var window = new Window
            {
                Title = "GO Database Manager",
                Content = manager,
                Width = 700,
                Height = 550,
                Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x1E, 0x1E, 0x2E)),
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = this
            };
            window.ShowDialog();

            // Refresh status after dialog closes
            CheckGoStatus();
        }

        private void Exit_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        private void Settings_Click(object sender, RoutedEventArgs e)
        {
            SettingsDialog.Visibility = Visibility.Visible;
        }

        private async void ProjectBrowser_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (!_hasOpenProject)
                    return;

                await ProjectBrowserDialog.ShowWithDatabaseAsync(_projectReferenceDatabasePath);
            }
            catch (Exception ex)
            {

                MessageBox.Show($"Error opening project browser:\n\n{ex.Message}", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async void ExportPLP_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (!_hasOpenProject)
                    return;

                // Use filtered data (respects plate filter + protein cutoff)
                var data = _dataFilterService?.FilteredData;
                if (data == null)
                {
                    MessageBox.Show("No data loaded. Import omic data first.", "Export",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                // Gather all data needed by the export control
                var excludedRuns = await _parquetService.GetExcludedRunNamesAsync();
                var cellTypePredictions = MainControlTab.GetCellTypePredictions();
                var rawFileToPlateId = _dataFilterService?.RawFileToPlateId;
                var plateIdToName = _dataFilterService?.PlateIdToName;

                // Get checked conditions from PeptideTicControl
                var checkedBioConditions = PeptideTicTab.CheckedBioConditions;
                var checkedCellTypes = PeptideTicTab.CheckedCellTypes;
                var checkedPlates = PeptideTicTab.CheckedPlates;

                // Single source of truth: the runs currently in the Explorer selection (filters + lasso, minus manual
                // exclusions). Supersedes the checkbox sets above so the lasso constrains the export. Null = Explorer
                // not rendered yet → the export falls back to re-deriving from the checkboxes.
                var selectedRunNames = PeptideTicTab.GetSelectedRunNames()?.ToList();

                // Load FASTA annotations
                Dictionary<string, FastaParserService.ProteinAnnotation> fastaAnnotations = null;
                try
                {
                    var fastaService = new FastaParserService(_currentProjectPath);
                    fastaAnnotations = await fastaService.GetAllAnnotationsAsync();
                }
                catch
                {
                    fastaAnnotations = new Dictionary<string, FastaParserService.ProteinAnnotation>();
                }

                // Persisted k-means cluster labels (run → "Cluster N") so "K-means cluster" can be chosen as an
                // export label. Empty/absent if the user has never run k-means.
                Dictionary<string, string> kmeansLabels = null;
                try { kmeansLabels = _projectDatabaseService != null ? await _projectDatabaseService.LoadKMeansLabelsAsync() : null; }
                catch { kmeansLabels = null; }

                ExportPLPControl.Initialize(data, excludedRuns, cellTypePredictions,
                    rawFileToPlateId, plateIdToName, fastaAnnotations,
                    checkedBioConditions, checkedCellTypes, checkedPlates, selectedRunNames, kmeansLabels);
                ExportPLPControl.Show();
            }
            catch (Exception ex)
            {

                MessageBox.Show($"Error opening PLP export:\n\n{ex.Message}", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void SubmitFeedback_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var dialog = new FeedbackDialog { Owner = this };
                dialog.ShowDialog();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Could not open feedback dialog: {ex.Message}",
                    "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void About_Click(object sender, RoutedEventArgs e)
        {
            MessageBox.Show(
                "SCP Browser - Single Cell Proteomics Analysis Platform\n\n" +
                "Version 1.0\n" +
                "A tool for analyzing DIA-NN parquet files from single-cell proteomics experiments.",
                "About SCP Browser",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }

        private void ConfigureGoDatabase_Click(object sender, RoutedEventArgs e)
        {
            GoDatabase_Click(sender, e);
        }

        // ==================== IMPORT MENU ====================

        private async void ImportOmicProfile_Click(object sender, RoutedEventArgs e)
        {
            // Project check - menu item is disabled when no project is open
            if (!_hasOpenProject || _projectDatabaseService == null)
            {
                MessageBox.Show(
                    "Please open or create a project first.\n\nOmic profile import requires an active project.",
                    "No Project Open",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            // Get the project directory
            string projectDirectory = Path.GetDirectoryName(_currentProjectPath);

            // Unified file dialog accepting all supported reference formats
            var fileDialog = new OpenFileDialog
            {
                Filter = "All supported formats|*.tsv;*.txt;*.parquet;*.pref|" +
                        "TSV files (*.tsv)|*.tsv|" +
                        "Parquet files (*.parquet)|*.parquet|" +
                        "Proteomics Reference (*.pref)|*.pref|" +
                        "Text files (*.txt)|*.txt|" +
                        "All files (*.*)|*.*",
                Title = "Select Omic Reference File",
                InitialDirectory = projectDirectory
            };

            if (fileDialog.ShowDialog() != true)
                return;

            string selectedFilePath = fileDialog.FileName;
            string extension = Path.GetExtension(selectedFilePath).ToLowerInvariant();

            switch (extension)
            {
                case ".parquet":
                    await ImportOmicProfile_FromParquet(selectedFilePath);
                    break;

                case ".pref":
                    await ImportOmicProfile_FromPref(selectedFilePath);
                    break;

                case ".tsv":
                case ".txt":
                    await ImportOmicProfile_FromTsv(selectedFilePath, projectDirectory);
                    break;

                default:
                    MessageBox.Show(
                        $"Unrecognized file extension '{extension}'.\n\n" +
                        "Supported formats: .tsv, .txt (transcriptomic/metabolomic), .parquet (proteomic), .pref (proteomics reference).",
                        "Unsupported File",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                    break;
            }
        }

        private async Task ImportOmicProfile_FromTsv(string expressionFilePath, string projectDirectory)
        {
            // Step 2: Select Cell Metadata file
            var metadataDialog = new OpenFileDialog
            {
                Filter = "TSV files (*.tsv)|*.tsv|Text files (*.txt)|*.txt|All files (*.*)|*.*",
                Title = "Select Cell Metadata TSV File",
                InitialDirectory = projectDirectory
            };

            if (metadataDialog.ShowDialog() != true)
                return;

            string metadataFilePath = metadataDialog.FileName;

            try
            {
                LoadingOverlay.SetMessage("Importing Omic Reference Data");
                LoadingOverlay.Show();

                var progressReporter = new LoadingOverlayProgressReporter(LoadingOverlay);
                string referenceDatabasePath = _projectReferenceDatabasePath;

                await TranscriptomicConverterUtility.ConvertTsvToSqliteAsync(
                    expressionFilePath,
                    metadataFilePath,
                    referenceDatabasePath,
                    progressReporter);

                LoadingOverlay.Hide();

                var referenceService = new ReferenceDataService();
                var loadedDatabase = await referenceService.LoadTranscriptomicDataAsync(referenceDatabasePath);

                if (loadedDatabase == null)
                {
                    MessageBox.Show("Import completed but no cell type profiles were found in the data.",
                        "Import Warning", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
                else
                {
                    MessageBox.Show(
                        $"Reference data imported successfully!\n\n" +
                        $"Cell Types: {loadedDatabase.TotalCellTypes}\n" +
                        $"Total Cells: {loadedDatabase.TotalCells:N0}\n" +
                        $"Unique Features: {loadedDatabase.TotalGenes:N0}\n\n" +
                        $"Database: {Path.GetFileName(referenceDatabasePath)}",
                        "Import Complete",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                }



                // Hot reload: Update MainControl's transcriptomic reference
                await MainControlTab.ReloadTranscriptomicReferenceAsync();
                PeptideTicTab.EnableCellTypeClassification(MainControlTab.IsTranscriptomicDatabaseLoaded());
            }
            catch (Exception ex)
            {
                LoadingOverlay.Hide();

                MessageBox.Show(
                    $"Error importing reference data:\n\n{ex.Message}",
                    "Import Error",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }

        private async Task ImportOmicProfile_FromPref(string prefFilePath)
        {
            try
            {
                LoadingOverlay.SetMessage("Importing Proteomics Reference (.pref)");
                LoadingOverlay.Show();

                var progressReporter = new LoadingOverlayProgressReporter(LoadingOverlay);
                string referenceDatabasePath = _projectReferenceDatabasePath;

                var referenceService = new ReferenceDataService();
                await referenceService.WriteProteomicsReferenceAsync(
                    referenceDatabasePath,
                    prefFilePath,
                    clearExistingData: true,
                    progress: progressReporter);

                LoadingOverlay.Hide();

                var loadedDatabase = await referenceService.LoadTranscriptomicDataAsync(referenceDatabasePath);

                MessageBox.Show(
                    $"Proteomics reference imported successfully!\n\n" +
                    $"Cell Types: {loadedDatabase.TotalCellTypes}\n" +
                    $"Total Cells: {loadedDatabase.TotalCells:N0}\n" +
                    $"Proteins: {loadedDatabase.TotalGenes:N0}\n\n" +
                    $"Database: {Path.GetFileName(referenceDatabasePath)}",
                    "Import Complete",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);



                await MainControlTab.ReloadTranscriptomicReferenceAsync();
                PeptideTicTab.EnableCellTypeClassification(MainControlTab.IsTranscriptomicDatabaseLoaded());
            }
            catch (Exception ex)
            {
                LoadingOverlay.Hide();

                MessageBox.Show(
                    $"Error importing proteomics reference:\n\n{ex.Message}",
                    "Import Error",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }

        private async Task ImportOmicProfile_FromParquet(string parquetFilePath)
        {
            var dialog = new ParquetReferenceLabelDialog(parquetFilePath)
            {
                Owner = this
            };

            if (dialog.ShowDialog() != true || !dialog.BuildSuccessful)
                return;

            try
            {
                LoadingOverlay.SetMessage("Building Proteomic Reference");
                LoadingOverlay.Show();

                var progressReporter = new LoadingOverlayProgressReporter(LoadingOverlay);
                string referenceDatabasePath = _projectReferenceDatabasePath;

                // Build CellTypeProfiles from labeled parquet data
                progressReporter.ReportMessage("Computing expression profiles...");
                var referenceService = new ReferenceDataService();
                var parsedData = referenceService.BuildProteomicReference(dialog.LoadedData, dialog.RunCellTypeMap);

                // Write to project DB (same path as TSV flow)
                progressReporter.ReportMessage("Writing reference to database...");
                await referenceService.WriteTranscriptomicDataAsync(
                    referenceDatabasePath,
                    parsedData,
                    clearExistingData: true,
                    progress: progressReporter);

                LoadingOverlay.Hide();

                int totalProteins = parsedData.CellTypeProfiles.Count > 0
                    ? parsedData.CellTypeProfiles[0].MedianExpression.Count : 0;

                MessageBox.Show(
                    $"Proteomic reference built successfully!\n\n" +
                    $"Cell Types: {parsedData.CellTypeProfiles.Count}\n" +
                    $"Total Runs: {dialog.RunCellTypeMap.Count}\n" +
                    $"Proteins: {totalProteins:N0}\n\n" +
                    $"Database: {Path.GetFileName(referenceDatabasePath)}",
                    "Import Complete",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);



                // Hot reload
                await MainControlTab.ReloadTranscriptomicReferenceAsync();
                PeptideTicTab.EnableCellTypeClassification(MainControlTab.IsTranscriptomicDatabaseLoaded());
            }
            catch (Exception ex)
            {
                LoadingOverlay.Hide();

                MessageBox.Show(
                    $"Error building proteomic reference:\n\n{ex.Message}",
                    "Build Error",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }

    

        // ==================== TAB CONTROL ====================

        private async void MainTabControl_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            // Only process if this is the MainTabControl (not nested tab controls)
            if (e.Source != MainTabControl)
                return;

            // BioTessera is at index 3
            if (MainTabControl.SelectedIndex == 3 && _bioTesseraNeedsUpdate)
            {
                _bioTesseraNeedsUpdate = false;
                await UpdateBioTesseraTabAsync();
            }
        }

        private async void MainControlTab_DataLoaded(object sender, EventArgs e)
        {
            try
            {
                // Store original data in filter service
                var originalData = MainControlTab.GetCurrentData();
                _dataFilterService.SetOriginalData(originalData);
                _dataFilterService.SelectedPlateIds = PlateFilterControl.GetSelectedPlateIds();



                // Apply initial filters
                await _dataFilterService.ApplyFiltersAsync(_parquetService);

                // When MainControlTab finishes loading, populate other tabs with the same data
                MainControlTab.SetDatabaseService(_projectDatabaseService);
                PeptideTicTab.SetDatabaseService(_projectDatabaseService);
                PeptideTicTab.UpdateChart(_dataFilterService.FilteredData);

                // Load raw file ID mapping for exclusion tracking
                var rawFileIdMapping = await _parquetService.GetRawFileNameToIdMappingAsync();
                PeptideTicTab.SetRawFileIdMapping(rawFileIdMapping);

                // Pass plate mapping for batch effect correction
                PeptideTicTab.SetPlateMapping(_dataFilterService.RawFileToPlateId);

                // Load existing exclusions from database
                var excludedRunNames = await _parquetService.GetExcludedRunNamesAsync();
                PeptideTicTab.SetExcludedRuns(excludedRunNames);


                // Load protein annotations BEFORE updating matrix so descriptions are available
                await ProteinMatrixTab.LoadProteinAnnotationsAsync(_currentProjectPath);

                ProteinMatrixTab.UpdateMatrix(_dataFilterService.FilteredData, _dataFilterService.HvpResults);

                // Initialize protein coverage panel
                await InitializeProteinCoverageAsync();

            // Recalculate contaminant ratios on OriginalData (handles pre-loaded contaminants from DB)
            ProteinMatrixTab_ContaminantsUpdated(this, EventArgs.Empty);

            // Subscribe to contaminant changes (unsubscribe first to avoid duplicates)
                ProteinMatrixTab.ContaminantsUpdated -= ProteinMatrixTab_ContaminantsUpdated;
                ProteinMatrixTab.ContaminantsUpdated += ProteinMatrixTab_ContaminantsUpdated;

                // Set image base directory
                PeptideTicTab.SetImageBaseDirectory(MainControlTab.GetCurrentFileDirectory());

                // Enable cell type classification if transcriptomic database is loaded
                bool cellTypeAvailable = MainControlTab.IsTranscriptomicDatabaseLoaded();
                PeptideTicTab.EnableCellTypeClassification(cellTypeAvailable);

                // Pass GO enrichment results to PeptideTicTab
                var goResults = MainControlTab.GetGoEnrichmentResults();
                var goColorMap = MainControlTab.GetGoTermColorMap();
                var currentData = MainControlTab.GetCurrentData();
                PeptideTicTab.EnableBioConditionClassification(currentData != null && currentData.BiologicalConditionPerFile.Count > 0);

                PeptideTicTab.SetGoEnrichmentResults(goResults, goColorMap);



                // Auto-run cell type classification if reference database is loaded
                if (cellTypeAvailable)
                {
                    await AutoRunCellTypeClassificationAsync();
                }

                // Restore checkbox states from database
                await RestoreCheckedStatesAsync();
            }
            catch (Exception ex)
            {

                MessageBox.Show($"Error loading data:\n\n{ex.Message}", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }


        private void PeptideTicTab_SelectionChangedForBioTessera(object sender, EventArgs e)
        {
            _bioTesseraNeedsUpdate = true;
        }

        private async void ProteinMatrixTab_ContaminantsUpdated(object sender, EventArgs e)
        {
            var contaminantIds = ProteinMatrixTab.ContaminantIds;
            var originalData = _dataFilterService?.OriginalData;
            if (originalData == null || !_hasOpenProject) return;

            originalData.ContaminantIds = new HashSet<string>(contaminantIds, StringComparer.OrdinalIgnoreCase);

            // Build a NEW dictionary to avoid mutating one that FilterByContaminantRatio may be iterating
            var newRatios = new Dictionary<string, double>();
            if (contaminantIds.Count > 0)
            {
                foreach (var rawFile in originalData.RawFileNames)
                {
                    double contaminantAbundance = 0;

                    foreach (var kvp in originalData.ProteinQuantMatrix)
                    {
                        if (contaminantIds.Contains(kvp.Key) && kvp.Value.TryGetValue(rawFile, out double abundance))
                        {
                            contaminantAbundance += abundance;
                        }
                    }

                    double totalTic = originalData.TotalIonCurrentPerFile.TryGetValue(rawFile, out double tic) ? tic : 0;
                    newRatios[rawFile] = totalTic > 0
                        ? contaminantAbundance / totalTic
                        : 0;
                }
            }

            // Atomic swap - safe even if ApplyFiltersAsync is reading the old reference
            originalData.TargetProteinRatioPerFile = newRatios;

            // Re-apply filters so PCA/UMAP/classification/HVP exclude contaminants
            await _dataFilterService.ApplyFiltersAsync(_parquetService);

            // Refresh the Explorer chart with updated ratios
            PeptideTicTab.UpdateChart(_dataFilterService.FilteredData);
        }

        private async Task RestoreCheckedStatesAsync()
        {
            if (_projectDatabaseService == null) return;

            var cellTypes = await _projectDatabaseService.GetSettingAsync("CheckedCellTypes");
            var bioConditions = await _projectDatabaseService.GetSettingAsync("CheckedBioConditions");
            var plates = await _projectDatabaseService.GetSettingAsync("CheckedPlates");

            var cellTypeColors = await _projectDatabaseService.GetSettingAsync("CellTypeColors");
            var bioConditionColors = await _projectDatabaseService.GetSettingAsync("BioConditionColors");
            var plateColors = await _projectDatabaseService.GetSettingAsync("PlateColors");

            // Restore checkbox states
            if (cellTypes != null || bioConditions != null || plates != null)
            {
                var checkedCellTypes = cellTypes != null
                    ? new HashSet<string>(cellTypes.Split(',').Where(s => !string.IsNullOrEmpty(s)))
                    : null;
                var checkedBioConditions = bioConditions != null
                    ? new HashSet<string>(bioConditions.Split(',').Where(s => !string.IsNullOrEmpty(s)))
                    : null;
                var checkedPlates = plates != null
                    ? new HashSet<string>(plates.Split(',').Where(s => !string.IsNullOrEmpty(s)))
                    : null;

                PeptideTicTab.RestoreCheckedStates(checkedCellTypes, checkedBioConditions, checkedPlates);
            }

            // Restore color maps
            if (cellTypeColors != null || bioConditionColors != null || plateColors != null)
            {
                PeptideTicTab.RestoreColorMaps(cellTypeColors, bioConditionColors, plateColors);

                // Also restore plate colors to PlateFilterControl (button rectangles)
                if (!string.IsNullOrEmpty(plateColors))
                {
                    var plateColorMap = new Dictionary<string, System.Windows.Media.Color>();
                    foreach (var entry in plateColors.Split(','))
                    {
                        var eq = entry.LastIndexOf('=');
                        if (eq < 0) continue;
                        string key = Uri.UnescapeDataString(entry.Substring(0, eq));
                        string hex = entry.Substring(eq + 1);
                        if (hex.Length != 6) continue;
                        if (!byte.TryParse(hex.Substring(0, 2), System.Globalization.NumberStyles.HexNumber,
                                System.Globalization.CultureInfo.InvariantCulture, out var r) ||
                            !byte.TryParse(hex.Substring(2, 2), System.Globalization.NumberStyles.HexNumber,
                                System.Globalization.CultureInfo.InvariantCulture, out var g) ||
                            !byte.TryParse(hex.Substring(4, 2), System.Globalization.NumberStyles.HexNumber,
                                System.Globalization.CultureInfo.InvariantCulture, out var b))
                        {
                            System.Diagnostics.Debug.WriteLine($"[Restore plate colors] Skipping malformed hex for '{key}': '{hex}'");
                            continue;
                        }
                        plateColorMap[key] = System.Windows.Media.Color.FromRgb(r, g, b);
                    }

                    if (plateColorMap.Count > 0)
                    {
                        PlateFilterControl.RestorePlateColors(plateColorMap);

                        // Re-push updated colors to all consumers
                        PeptideTicTab.SetPlateColorMap(PlateFilterControl.GetPlateColorMap());
                        MainControlTab.SetPlateColorMap(PlateFilterControl.GetPlateColorMapById());
                    }
                }
            }

            // Refresh checkbox/legend UI to reflect restored states and colors
            PeptideTicTab.RefreshCheckboxUI();
        }

        /// <summary>
        /// Automatically runs cell type classification when project loads
        /// </summary>
        private async Task AutoRunCellTypeClassificationAsync()
        {
            try
            {
                var proteomicsData = MainControlTab.GetCurrentData();
                if (proteomicsData == null)
                {

                    return;
                }

                // Get the most recent import ID
                int? importId = await _parquetService.GetMostRecentImportIdAsync();
                if (!importId.HasValue)
                {

                    return;
                }

                // Create a simple progress reporter that doesn't show the overlay
                var progressReporter = new SilentProgressReporter();

                // Get predictions from MainControl (which uses CellTypeClassificationManager)
                // Pass key markers from ProjectBrowser for classification
                var keyMarkers = ProjectBrowserDialog.GetKeyMarkers();
                var excludedCellTypes = ProjectBrowserDialog.GetExcludedCellTypes();
                var priorWeights = ProjectBrowserDialog.GetPriorWeights();
                var predictions = await MainControlTab.GetCellTypePredictionsAsync(
                    proteomicsData,
                    _projectReferenceDatabasePath,
                    importId.Value,
                    progressReporter,
                    keyMarkers,
                    forceRecompute: false,
                    excludedCellTypes,
                    priorWeights);

                if (predictions == null || predictions.Count == 0)
                {

                    return;
                }

                // Get color map
                var colorMap = MainControlTab.GetCellTypeColorMap();

                // Pass predictions to PeptideTicTab (this will also select Cell Type mode)
                PeptideTicTab.SetCellTypePredictions(predictions, colorMap, selectCellTypeMode: true);


            }
            catch (Exception ex)
            {

                // Don't show error to user - this is a background operation
            }
        }

        /// <summary>
        /// Silent progress reporter that doesn't update UI
        /// </summary>
        private class SilentProgressReporter : IProgressReporter
        {
            public void ReportMessage(string message)
            {

            }

            public void ReportProgress(string progress)
            {
                // Silent
            }
        }

        private async void PeptideTicTab_CellTypePredictionsRequested(object sender, EventArgs e)
        {
            try
            {


                LoadingOverlay.SetMessage("Computing Cell Type Classifications");
                LoadingOverlay.SetProgress("Analyzing protein expression patterns...");
                LoadingOverlay.Show();

                var proteomicsData = MainControlTab.GetCurrentData();
                if (proteomicsData == null)
                {
                    LoadingOverlay.Hide();
                    MessageBox.Show("No data loaded.", "Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                // Get the most recent import ID
                int? importId = await _parquetService.GetMostRecentImportIdAsync();
                if (!importId.HasValue)
                {
                    LoadingOverlay.Hide();
                    MessageBox.Show("No import data found.", "Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                // Create progress reporter
                var progressReporter = new LoadingOverlayProgressReporter(LoadingOverlay);

                // Get predictions from MainControl (which uses CellTypeClassificationManager)
                // Pass key markers from ProjectBrowser for classification
                var keyMarkers = ProjectBrowserDialog.GetKeyMarkers();
                var excludedCellTypes = ProjectBrowserDialog.GetExcludedCellTypes();
                var priorWeights = ProjectBrowserDialog.GetPriorWeights();
                var predictions = await MainControlTab.GetCellTypePredictionsAsync(
                    proteomicsData,
                    _projectReferenceDatabasePath,
                    importId.Value,
                    progressReporter,
                    keyMarkers,
                    forceRecompute: false,
                    excludedCellTypes,
                    priorWeights);

                // Get color map
                var colorMap = MainControlTab.GetCellTypeColorMap();

                // Pass predictions to PeptideTicTab
                PeptideTicTab.SetCellTypePredictions(predictions, colorMap);

                LoadingOverlay.Hide();


            }
            catch (Exception ex)
            {
                LoadingOverlay.Hide();
                MessageBox.Show($"Error computing cell type predictions:\n\n{ex.Message}",
                    "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>
        /// After a marker-only classification (no reference profile loaded), the results are already saved to
        /// raw_file_cell_type_classifications. Load them and push them straight into the scatter: this unlocks the
        /// main-screen "Cell Type" colour option, caches the predictions (so re-selecting it works without a profile),
        /// auto-selects Cell Type mode, and colours — fixing the "dropdown stays locked after Classify" issue.
        /// </summary>
        private async void ProjectBrowserDialog_MarkerCellsClassified(object sender, EventArgs e)
        {
            try
            {
                if (string.IsNullOrEmpty(_projectReferenceDatabasePath) || _parquetService == null) return;
                if (MainControlTab.GetCurrentData() == null) return;

                int? importId = await _parquetService.GetMostRecentImportIdAsync();
                if (!importId.HasValue) return;

                var predictions = await new CellTypeClassificationService(_projectReferenceDatabasePath)
                    .LoadCellTypeClassificationsAsync(importId.Value);
                if (predictions == null || predictions.Count == 0) return;

                var colorMap = BuildMarkerCellTypeColorMap(predictions);
                PeptideTicTab.EnableCellTypeClassification(true);
                PeptideTicTab.SetCellTypePredictions(predictions, colorMap, selectCellTypeMode: true);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[MarkerCellsClassified] {ex}");
            }
        }

        /// <summary>Assigns a distinct palette colour to each marker class name (grey for "Unassigned").</summary>
        private static Dictionary<string, System.Windows.Media.Color> BuildMarkerCellTypeColorMap(
            Dictionary<string, CellTypePredictionResult> predictions)
        {
            var palette = new[]
            {
                System.Windows.Media.Color.FromRgb(0x1f,0x77,0xb4), System.Windows.Media.Color.FromRgb(0xff,0x7f,0x0e),
                System.Windows.Media.Color.FromRgb(0x2c,0xa0,0x2c), System.Windows.Media.Color.FromRgb(0xd6,0x27,0x28),
                System.Windows.Media.Color.FromRgb(0x94,0x67,0xbd), System.Windows.Media.Color.FromRgb(0x8c,0x56,0x4b),
                System.Windows.Media.Color.FromRgb(0xe3,0x77,0xc2), System.Windows.Media.Color.FromRgb(0x17,0xbe,0xcf),
                System.Windows.Media.Color.FromRgb(0xbc,0xbd,0x22), System.Windows.Media.Color.FromRgb(0x39,0x9b,0x9b),
            };
            var map = new Dictionary<string, System.Windows.Media.Color>(StringComparer.OrdinalIgnoreCase);
            int i = 0;
            foreach (var t in predictions.Values.Select(p => p.TopCellType)
                         .Where(s => !string.IsNullOrEmpty(s))
                         .Distinct(StringComparer.OrdinalIgnoreCase)
                         .OrderBy(s => s, StringComparer.OrdinalIgnoreCase))
            {
                map[t] = string.Equals(t, "Unassigned", StringComparison.OrdinalIgnoreCase)
                    ? System.Windows.Media.Color.FromRgb(0xc8, 0xc8, 0xc8)
                    : palette[i++ % palette.Length];
            }
            return map;
        }

        private async void PeptideTicTab_ExportDiagnosticsRequested(object sender, EventArgs e)
        {
            try
            {
                var predictions = PeptideTicTab.GetCellTypePredictions();
                var proteomicsData = PeptideTicTab.GetCurrentData();

                if (predictions == null || predictions.Count == 0)
                {
                    MessageBox.Show("No cell type predictions available to export.", 
                        "Export Diagnostics", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                if (proteomicsData == null)
                {
                    MessageBox.Show("No proteomics data loaded.", 
                        "Export Diagnostics", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                // Show save dialog
                var saveDialog = new Microsoft.Win32.SaveFileDialog
                {
                    Filter = "Tab-separated values (*.tsv)|*.tsv|All files (*.*)|*.*",
                    DefaultExt = ".tsv",
                    FileName = "CellType_Diagnostics"
                };

                if (saveDialog.ShowDialog() != true)
                    return;

                LoadingOverlay.SetMessage("Exporting Classification Diagnostics");
                LoadingOverlay.SetProgress("Preparing data...");
                LoadingOverlay.Show();

                var progressReporter = new LoadingOverlayProgressReporter(LoadingOverlay);

                await MainControlTab.ExportClassificationDiagnosticsAsync(
                    saveDialog.FileName,
                    predictions,
                    proteomicsData,
                    progressReporter);

                // Also export gene presence by cell type
                var genePresencePath = System.IO.Path.Combine(
                    System.IO.Path.GetDirectoryName(saveDialog.FileName),
                    System.IO.Path.GetFileNameWithoutExtension(saveDialog.FileName) + "_GenePresence.tsv");

                await MainControlTab.ExportGenePresenceByCellTypeAsync(
                    genePresencePath,
                    predictions,
                    proteomicsData,
                    progressReporter);

                LoadingOverlay.Hide();

                MessageBox.Show($"Diagnostics exported successfully to:\n{saveDialog.FileName}\n\nGene presence matrix exported to:\n{genePresencePath}",
                    "Export Complete", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                LoadingOverlay.Hide();
                MessageBox.Show($"Error exporting diagnostics:\n\n{ex.Message}",
                    "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async void PeptideTicTab_ContaminantRatioCutoffChanged(object sender, double cutoff)
        {
            try
            {
                if (!_hasOpenProject || _dataFilterService == null) return;
                _dataFilterService.ContaminantRatioCutoff = cutoff;
                await _dataFilterService.ApplyFiltersAsync(_parquetService);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[PeptideTicTab_ContaminantRatioCutoffChanged] {ex}");
            }
        }

        private async void ProjectBrowserDialog_ConditionDeleted(object? sender, ConditionDeletedEventArgs e)
        {
            try
            {
                if (!_hasOpenProject) return;
                await ReloadProjectDataAsync();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ProjectBrowserDialog_ConditionDeleted] {ex}");
                MessageBox.Show(
                    $"Condition was deleted but reloading the project data failed:\n\n{ex.Message}\n\nPlease close and re-open the project.",
                    "Reload Failed",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
        }

        /// <summary>
        /// Click handler for the File &gt; Reload Project Data menu item AND the F5 KeyBinding.
        /// Both routes funnel through here. Signature accepts either RoutedEventArgs (menu)
        /// or ExecutedRoutedEventArgs (KeyBinding) since both inherit from RoutedEventArgs.
        /// </summary>
        private async void ReloadProjectData_Click(object sender, RoutedEventArgs e)
        {
            if (!_hasOpenProject) return;

            try
            {
                await ReloadProjectDataAsync();
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    $"Reloading project data failed:\n\n{ex.Message}",
                    "Reload Failed",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }

        /// <summary>
        /// CanExecute for the F5 KeyBinding. Mirrors the menu item's IsEnabled state.
        /// </summary>
        private void ReloadProjectData_CanExecute(object sender, System.Windows.Input.CanExecuteRoutedEventArgs e)
        {
            e.CanExecute = _hasOpenProject;
        }

        /// <summary>
        /// Re-runs the project data load chain after an in-place destructive change
        /// (e.g. cascade delete of a biological condition). Re-pulls the imported
        /// parquet list, reloads plate filter and plate mapping, and pushes the new
        /// data through MainControlTab which propagates to all dependent tabs.
        /// </summary>
        public async Task ReloadProjectDataAsync()
        {
            if (!_hasOpenProject || _parquetService == null) return;

            try
            {
                LoadingOverlay.SetMessage("Reloading Project Data");
                LoadingOverlay.SetProgress("Refreshing imports...");
                LoadingOverlay.Show();
                await Task.Delay(50);

                // 1. Refresh plate filter and plate mapping in the data filter pipeline.
                await PlateFilterControl.LoadPlatesAsync(_currentProjectPath);
                PeptideTicTab.SetPlateColorMap(PlateFilterControl.GetPlateColorMap());
                MainControlTab.SetPlateColorMap(PlateFilterControl.GetPlateColorMapById());
                if (_dataFilterService != null)
                {
                    await _dataFilterService.LoadPlateMappingAsync(_parquetService, _plateService);
                }

                // 2. Resolve the surviving parquet imports back to disk paths.
                var allImportedFiles = await _parquetService.GetAllImportedParquetFilesAsync();
                var parquetPaths = new List<string>();
                if (allImportedFiles != null && allImportedFiles.Count > 0)
                {
                    string projectDirectory = Path.GetDirectoryName(_currentProjectPath);

                    foreach (var fileName in allImportedFiles)
                    {
                        string safeName = Path.GetFileName(fileName ?? string.Empty);
                        if (string.IsNullOrEmpty(safeName)) continue;

                        string parquetPath = Path.Combine(projectDirectory, "imports", safeName);
                        bool fileExists = await Task.Run(() => File.Exists(parquetPath));
                        if (fileExists)
                            parquetPaths.Add(parquetPath);
                    }
                }

                // 3. Push refreshed data through MainControlTab; its existing DataLoaded
                //    event chain refreshes ScatterPlot, PeptideTic, ProteinMatrix, etc.
                LoadingOverlay.SetProgress($"Reloading data from {parquetPaths.Count} file(s)...");
                await MainControlTab.LoadDataFromProject(parquetPaths, _projectReferenceDatabasePath);

                // 4. If the user has the Project Browser dialog open, refresh its inner
                //    controls (omic / plate / conditions) too. When it's collapsed there's
                //    nothing to do; ShowWithDatabaseAsync will reload it next time it opens.
                if (ProjectBrowserDialog.Visibility == Visibility.Visible)
                {
                    LoadingOverlay.SetProgress("Refreshing project browser...");
                    await ProjectBrowserDialog.RefreshAsync();
                }

                LoadingOverlay.Hide();
            }
            catch
            {
                LoadingOverlay.Hide();
                throw;
            }
        }

        private async void ProjectBrowserDialog_ReclassifyRequested(object sender, ReclassifyRequestedEventArgs e)
        {
            try
            {
                bool applyMarkers = e.ApplyKeyMarkers;


                if (applyMarkers)
                {
                    LoadingOverlay.SetMessage("Reclassifying Cells with Key Markers");
                    LoadingOverlay.SetProgress("Applying key marker adjustments...");
                }
                else
                {
                    LoadingOverlay.SetMessage("Reclassifying Cells (Baseline)");
                    LoadingOverlay.SetProgress("Computing baseline scores without marker adjustments...");
                }
                LoadingOverlay.Show();

                var proteomicsData = MainControlTab.GetCurrentData();
                if (proteomicsData == null)
                {
                    LoadingOverlay.Hide();
                    MessageBox.Show("No data loaded.", "Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                // Get the most recent import ID
                int? importId = await _parquetService.GetMostRecentImportIdAsync();
                if (!importId.HasValue)
                {
                    LoadingOverlay.Hide();
                    MessageBox.Show("No import data found.", "Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                // Create progress reporter
                var progressReporter = new LoadingOverlayProgressReporter(LoadingOverlay);

                // Persist the selected classifier method to the SAME database the manager reads it from, so its
                // recompute below uses the chosen method.
                if (!string.IsNullOrEmpty(_projectReferenceDatabasePath))
                    await new ProjectDatabaseService(_projectReferenceDatabasePath)
                        .SetSettingAsync("classification_method", e.ClassificationMethod ?? "Quantitative");

                // Get key markers only if applying them, and get excluded cell types
                var keyMarkers = applyMarkers ? ProjectBrowserDialog.GetKeyMarkers() : null;
                var excludedCellTypes = ProjectBrowserDialog.GetExcludedCellTypes();
                var priorWeights = e.PriorWeights;

                var predictions = await MainControlTab.GetCellTypePredictionsAsync(
                    proteomicsData,
                    _projectReferenceDatabasePath,
                    importId.Value,
                    progressReporter,
                    keyMarkers,
                    forceRecompute: true,
                    excludedCellTypes,
                    priorWeights); // Pass prior weights

                // Get color map
                var colorMap = MainControlTab.GetCellTypeColorMap();

                // Pass predictions to PeptideTicTab
                PeptideTicTab.SetCellTypePredictions(predictions, colorMap);

                LoadingOverlay.Hide();

                // Show summary
                int totalMarkers = keyMarkers?.Values.Sum(m => m.Count) ?? 0;
                int excludedCount = excludedCellTypes?.Count ?? 0;
                string excludedNote = excludedCount > 0 ? $"\n{excludedCount} cell type(s) excluded." : "";
                string message = applyMarkers
                    ? $"Reclassification complete!\n\n{predictions.Count} cells reclassified using {totalMarkers} key marker(s).{excludedNote}"
                    : $"Baseline reclassification complete!\n\n{predictions.Count} cells reclassified without key marker adjustments.{excludedNote}";
                

                MessageBox.Show(message, "Reclassification Complete", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                LoadingOverlay.Hide();
                MessageBox.Show($"Error during reclassification:\n\n{ex.Message}",
                    "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // ==================== LOADING OVERLAY PROGRESS REPORTER ====================

        private class LoadingOverlayProgressReporter : IProgressReporter
        {
            private readonly LoadingOverlay _loadingOverlay;

            public LoadingOverlayProgressReporter(LoadingOverlay loadingOverlay)
            {
                _loadingOverlay = loadingOverlay;
            }

            public void ReportMessage(string message)
            {
                _loadingOverlay.Dispatcher.InvokeAsync(() =>
                {
                    _loadingOverlay.SetMessage(message);
                }, System.Windows.Threading.DispatcherPriority.Render);
            }

            public void ReportProgress(string progressDetail)
            {
                _loadingOverlay.Dispatcher.InvokeAsync(() =>
                {
                    _loadingOverlay.SetProgress(progressDetail);
                }, System.Windows.Threading.DispatcherPriority.Render);
            }
        }

        private async void PeptideTicTab_RunInclusionChanged(object sender, RunInclusionChangedEventArgs e)
        {
            try
            {
                if (e.IsIncluded)
                {
                    await _parquetService.IncludeRunAsync(e.RawFileId);

                }
                else
                {
                    await _parquetService.ExcludeRunAsync(e.RawFileId);

                }
            }
            catch (Exception ex)
            {

                await Dispatcher.InvokeAsync(() =>
                {
                    MessageBox.Show($"Error updating run exclusion:\n\n{ex.Message}", "Error",
                        MessageBoxButton.OK, MessageBoxImage.Error);
                });
            }
        }

        private async void PeptideTicTab_ClearAllExclusionsRequested(object sender, EventArgs e)
        {
            try
            {
                await _parquetService.ClearAllExclusionsAsync();

            }
            catch (Exception ex)
            {

                await Dispatcher.InvokeAsync(() =>
                {
                    MessageBox.Show($"Error clearing exclusions:\n\n{ex.Message}", "Error",
                        MessageBoxButton.OK, MessageBoxImage.Error);
                });
            }
        }

        // ==================== RECENT PROJECTS MANAGEMENT ====================

        private void AddToRecentProjects(string projectPath)
        {
            // Initialize the collection if it's null
            if (Settings.Default.RecentProjects == null)
            {
                Settings.Default.RecentProjects = new System.Collections.Specialized.StringCollection();
            }

            // Remove the project if it already exists (we'll add it to the front)
            if (Settings.Default.RecentProjects.Contains(projectPath))
            {
                Settings.Default.RecentProjects.Remove(projectPath);
            }

            // Add to the beginning
            Settings.Default.RecentProjects.Insert(0, projectPath);

            // Keep only the last 5 projects
            while (Settings.Default.RecentProjects.Count > 5)
            {
                Settings.Default.RecentProjects.RemoveAt(Settings.Default.RecentProjects.Count - 1);
            }

            // Save settings
            Settings.Default.Save();


        }

        private async Task<List<RecentProjectItem>> GetRecentProjectsAsync()
        {
            var recentProjects = new List<RecentProjectItem>();

            if (Settings.Default.RecentProjects != null)
            {
                foreach (string projectPath in Settings.Default.RecentProjects)
                {
                    if (File.Exists(projectPath))
                    {
                        string description = "";
                        string name = "";
                        try
                        {
                            var service = new ProjectDatabaseService(projectPath);
                            var info = await service.GetProjectInfoAsync();
                            if (info != null)
                            {
                                if (!string.IsNullOrEmpty(info.Description))
                                    description = info.Description;
                                if (!string.IsNullOrEmpty(info.ProjectName))
                                    name = info.ProjectName;
                            }
                        }
                        catch { }

                        recentProjects.Add(new RecentProjectItem
                        {
                            Path = projectPath,
                            Name = string.IsNullOrEmpty(name) ? System.IO.Path.GetFileName(projectPath) : name,
                            Description = string.IsNullOrEmpty(description) ? null : description
                        });
                    }
                }
            }

            return recentProjects;
        }

        private async Task LoadRecentProjectsUI()
        {
            var recentProjects = await GetRecentProjectsAsync();

            if (recentProjects.Count > 0)
            {
                RecentProjectsList.ItemsSource = recentProjects;
                NoRecentProjectsText.Visibility = Visibility.Collapsed;
            }
            else
            {
                RecentProjectsList.ItemsSource = null;
                NoRecentProjectsText.Visibility = Visibility.Visible;
            }


        }

        private async void RecentProject_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (sender is Button button && button.Tag is RecentProjectItem item)
                {
                    string projectPath = item.Path;


                    // Check if the file still exists
                    if (!File.Exists(projectPath))
                    {
                        var result = MessageBox.Show(
                            $"This project file no longer exists:\n\n{projectPath}\n\nRemove it from recent projects?",
                            "Project Not Found",
                            MessageBoxButton.YesNo,
                            MessageBoxImage.Warning);

                        if (result == MessageBoxResult.Yes)
                        {
                            // Remove from settings
                            if (Settings.Default.RecentProjects != null && Settings.Default.RecentProjects.Contains(projectPath))
                            {
                                Settings.Default.RecentProjects.Remove(projectPath);
                                Settings.Default.Save();
                            }

                            // Refresh the UI
                            await LoadRecentProjectsUI();
                        }

                        return;
                    }

                    // Open the project
                    await OpenProjectAsync(projectPath);
                }
            }
            catch (Exception ex)
            {

                MessageBox.Show($"Error opening project:\n\n{ex.Message}", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async void RemoveRecentProject_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button button && button.Tag is RecentProjectItem item)
            {
                string projectPath = item.Path;
                // Remove from settings
                if (Settings.Default.RecentProjects != null && Settings.Default.RecentProjects.Contains(projectPath))
                {
                    Settings.Default.RecentProjects.Remove(projectPath);
                    Settings.Default.Save();
                }

                // Refresh the UI
                await LoadRecentProjectsUI();
            }
        }

        private async void EditRecentProject_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                RecentProjectItem item = null;

                if (sender is MenuItem menuItem)
                {
                    // Try Tag first, then walk up to the ContextMenu's PlacementTarget
                    if (menuItem.Tag is RecentProjectItem tagItem)
                        item = tagItem;
                    else if (menuItem.Parent is ContextMenu ctx && ctx.PlacementTarget is Button btn && btn.Tag is RecentProjectItem btnItem)
                        item = btnItem;
                }

                if (item == null) return;

                var service = new ProjectDatabaseService(item.Path);
                var info = await service.GetProjectInfoAsync();

                var dialog = new EditProjectDialog(info?.ProjectName ?? item.Name, info?.Description ?? "");
                dialog.Owner = this;

                if (dialog.ShowDialog() == true)
                {
                    await service.UpdateProjectInfoAsync(dialog.ProjectName, dialog.ProjectDescription);
                    await LoadRecentProjectsUI();
                }
            }
            catch (Exception ex)
            {

                MessageBox.Show($"Error editing project:\n\n{ex.Message}", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // ==================== GENE ONTOLOGY STATUS CHECK ====================
        private void CheckGoStatus()
        {
            _goStatusService = new BioTessera.GO.GoStatusService();

            bool isReady = _goStatusService.IsReady;

            // Update GO status panel visibility and content
            if (!_goStatusService.DatabaseExists || !_goStatusService.HasOntology)
            {
                GoStatusPanel.Visibility = Visibility.Visible;
                GoStatusTitle.Text = "⚠️ Gene Ontology database required";
                GoStatusMessage.Text = "Set up GO database to enable analysis";
            }
            else if (_goStatusService.InstalledSpecies == null || _goStatusService.InstalledSpecies.Count == 0)
            {
                GoStatusPanel.Visibility = Visibility.Visible;
                GoStatusTitle.Text = "⚠️ No species annotations";
                GoStatusMessage.Text = "Add at least one species to enable GO enrichment";
            }
            else
            {
                GoStatusPanel.Visibility = Visibility.Collapsed;
            }

            // Enable/disable project buttons on welcome screen
            OpenProjectButton.IsEnabled = isReady;
            NewProjectButton.IsEnabled = isReady;

            // Enable/disable File menu items
            NewProjectMenuItem.IsEnabled = isReady;
            OpenProjectMenuItem.IsEnabled = isReady;

            // Enable/disable recent projects list
            RecentProjectsList.IsEnabled = isReady;
        }

        private async void ImportFasta_Click(object sender, RoutedEventArgs e)
        {
            if (!_hasOpenProject || _projectDatabaseService == null)
            {
                MessageBox.Show(
                    "Please open or create a project first.",
                    "No Project Open",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            var dialog = new OpenFileDialog
            {
                Filter = "FASTA files (*.fasta;*.fa;*.faa)|*.fasta;*.fa;*.faa|All files (*.*)|*.*",
                Title = "Select Search Database FASTA File"
            };

            if (dialog.ShowDialog() != true)
                return;

            try
            {
                LoadingOverlay.SetMessage("Importing FASTA Database...");
                LoadingOverlay.SetProgress("Parsing protein headers...");
                LoadingOverlay.Show();

                await _projectDatabaseService.EnsureProteinAnnotationsTableExistsAsync();
                await _projectDatabaseService.MigrateProteinAnnotationsAddSourceColumnAsync();

                var fastaService = new FastaParserService(_currentProjectPath);

                var progress = new Progress<string>(msg =>
                {
                    Dispatcher.Invoke(() => LoadingOverlay.SetProgress(msg));
                });

                int count = await fastaService.ImportFastaAsync(dialog.FileName, progress);

                // Save FASTA file path for protein coverage viewer
                await _projectDatabaseService.SetSettingAsync("fasta_path", dialog.FileName);

                // Reload annotations into memory and refresh the matrix
                await ProteinMatrixTab.LoadProteinAnnotationsAsync(_currentProjectPath);
                if (_dataFilterService?.FilteredData != null)
                {
                    ProteinMatrixTab.UpdateMatrix(_dataFilterService.FilteredData, _dataFilterService.HvpResults);
                }

                // Re-initialize protein coverage with new FASTA
                await InitializeProteinCoverageAsync();

                LoadingOverlay.Hide();

                MessageBox.Show(
                    $"Successfully imported {count:N0} protein annotations from:\n\n{Path.GetFileName(dialog.FileName)}",
                    "FASTA Import Complete",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                LoadingOverlay.Hide();
                MessageBox.Show(
                    $"Error importing FASTA file:\n\n{ex.Message}",
                    "Import Error",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }
    }
}