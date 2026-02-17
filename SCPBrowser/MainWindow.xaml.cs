using Microsoft.Win32;
using SCPBrowser.GOTools;
using SCPBrowser.Models;
using SCPBrowser.Services;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using BioTessera.Core.Models;

namespace SCPBrowser
{
    public partial class MainWindow : Window
    {
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
        private DataFilterService _dataFilterService;
        private int _pendingProteinCutoff;


        // Public properties for controls to access
        public bool HasOpenProject => _hasOpenProject;
        public string ProjectReferenceDatabasePath => _projectReferenceDatabasePath;

        public MainWindow()
        {
            InitializeComponent();

            // Subscribe to PeptideTicTab events
            PeptideTicTab.CellTypePredictionsRequested += PeptideTicTab_CellTypePredictionsRequested;
            PeptideTicTab.SelectionChangedForBioTessera += PeptideTicTab_SelectionChangedForBioTessera;
            PeptideTicTab.RunInclusionChanged += PeptideTicTab_RunInclusionChanged;
            PeptideTicTab.ClearAllExclusionsRequested += PeptideTicTab_ClearAllExclusionsRequested;
            PeptideTicTab.ExportDiagnosticsRequested += PeptideTicTab_ExportDiagnosticsRequested;
            PeptideTicTab.ContaminantRatioCutoffChanged += PeptideTicTab_ContaminantRatioCutoffChanged;

            // Subscribe to ProjectBrowser reclassify request
            ProjectBrowserDialog.ReclassifyRequested += ProjectBrowserDialog_ReclassifyRequested;

            _goTermResolver = new GoTermResolver(9606); // Human default
            UpdateWindowTitle();

            // CRITICAL: Ensure loading overlay is hidden on startup
            LoadingOverlay.Hide();

            // Load recent projects
            LoadRecentProjectsUI();

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
                ImportParquetMenuItem.IsEnabled = true;
                ImportFastaMenuItem.IsEnabled = true;
                ImportOmicProfileMenuItem.IsEnabled = true;
                CloseProjectMenuItem.IsEnabled = true;
                ClearCellTypeClassificationsMenuItem.IsEnabled = true;

                // Load and show plate filter control
                LoadingOverlay.SetProgress("Loading plates...");
                await PlateFilterControl.LoadPlatesAsync(projectDbPath);
                PeptideTicTab.SetPlateColorMap(PlateFilterControl.GetPlateColorMap());
                await _dataFilterService.LoadPlateMappingAsync(_parquetService, _plateService);
                PlateFilterControl.Visibility = Visibility.Visible;
                Console.WriteLine("PlateFilterControl loaded and visible");

                // Subscribe to events
                PlateFilterControl.PlateSelectionChanged += PlateFilterControl_PlateSelectionChanged;
                MainControlTab.ProteinCutoffChanged += MainControlTab_ProteinCutoffChanged;
                _dataFilterService.FilteredDataChanged += DataFilterService_FilteredDataChanged;

                // Load saved protein cutoff
                var savedCutoff = await _projectDatabaseService.GetSettingAsync(SETTING_PROTEIN_CUTOFF, "800");
                if (int.TryParse(savedCutoff, out int cutoffValue))
                {
                    MainControlTab.ProteinCutoff = cutoffValue;
                    _dataFilterService.ProteinCutoff = cutoffValue;
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
                        string parquetPath = Path.Combine(projectDirectory, "imports", fileName);
                        bool fileExists = await Task.Run(() => File.Exists(parquetPath));

                        if (fileExists)
                        {
                            parquetPaths.Add(parquetPath);
                        }
                        else
                        {
                            Console.WriteLine($"Warning: Data file '{fileName}' not found in imports folder.");
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

                Console.WriteLine($"Project opened: {projectInfo.ProjectName}");
                Console.WriteLine($"Created: {projectInfo.CreatedDate}");
                Console.WriteLine($"Location: {Path.GetDirectoryName(projectDbPath)}");

                AddToRecentProjects(projectDbPath);

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
                Console.WriteLine($"DataFilterService: FilteredDataChanged event received");
                await RefreshAllTabsWithFilteredDataAsync();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error in FilteredDataChanged handler: {ex.Message}");
            }
        }

        private void MainControlTab_ProteinCutoffChanged(object sender, int newCutoff)
        {
            // Use debouncing to avoid excessive filter operations when dragging slider
            _pendingProteinCutoff = newCutoff;
            _proteinCutoffDebounceTimer?.Stop();
            _proteinCutoffDebounceTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(ProteinCutoffDebounceDelayMs)
            };
            _proteinCutoffDebounceTimer.Tick += async (s, e) =>
            {
                _proteinCutoffDebounceTimer.Stop();
                await ApplyProteinCutoffAsync(_pendingProteinCutoff);
            };
            _proteinCutoffDebounceTimer.Start();
        }

        private async Task ApplyProteinCutoffAsync(int newCutoff)
        {
            try
            {
                Console.WriteLine($"Protein cutoff changed: {newCutoff}");

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
                Console.WriteLine($"Error applying protein cutoff: {ex.Message}");
            }
        }

        private async void PlateFilterControl_PlateSelectionChanged(object sender, PlateSelectionChangedEventArgs e)
        {
            try
            {
                Console.WriteLine($"Plate selection changed: {e.SelectedPlateIds.Count} plates selected");

                if (_dataFilterService.OriginalData == null)
                {
                    Console.WriteLine("No original data available for filtering");
                    return;
                }

                _dataFilterService.SelectedPlateIds = e.SelectedPlateIds;
                await _dataFilterService.ApplyFiltersAsync(_parquetService);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error filtering data: {ex.Message}");
                await Dispatcher.InvokeAsync(() =>
                {
                    MessageBox.Show($"Error filtering data:\n\n{ex.Message}", "Error",
                        MessageBoxButton.OK, MessageBoxImage.Error);
                });
            }
        }

        private async Task RefreshAllTabsWithFilteredDataAsync()
        {
            if (_dataFilterService.FilteredData == null)
                return;

            Console.WriteLine($"Refreshing all tabs with filtered data: {_dataFilterService.FilteredData.TotalRawFiles} runs");

            // Bar chart shows plate-filtered data (all bars) with visual cutoff
            var dataForBarChart = _dataFilterService.PlateFilteredData ?? _dataFilterService.FilteredData;
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
                    Console.WriteLine("[BioTessera] No proteins after conversion");
                    return;
                }

                // Resolve GO terms from central database
                _goTermResolver.ResolveGoTerms(proteins);

                // Load into BioTessera and generate
                BioTesseraTab.LoadProteins(proteins);
                await BioTesseraTab.GenerateAsync();

                var runInfo = selectedRuns != null ? $" (filtered to {selectedRuns.Count} runs)" : "";
                Console.WriteLine($"[BioTessera] Updated with {proteins.Count} proteins{runInfo}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[BioTessera] Error updating tab: {ex.Message}");
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
                Console.WriteLine($"Error clearing classifications: {ex.Message}");
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

        private void CloseProject()
        {
            // Unsubscribe from events to avoid double-subscription on next project open
            PlateFilterControl.PlateSelectionChanged -= PlateFilterControl_PlateSelectionChanged;
            MainControlTab.ProteinCutoffChanged -= MainControlTab_ProteinCutoffChanged;
            if (_dataFilterService != null)
            {
                _dataFilterService.FilteredDataChanged -= DataFilterService_FilteredDataChanged;
                _dataFilterService.Clear();
            }

            _currentProjectPath = null;
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
            ImportParquetMenuItem.IsEnabled = false;
            ImportOmicProfileMenuItem.IsEnabled = false;
            CloseProjectMenuItem.IsEnabled = false;
            ClearCellTypeClassificationsMenuItem.IsEnabled = false;

            ProjectBrowserMenuItem.IsEnabled = false;
            ExportPLPMenuItem.IsEnabled = false;
            ExportPLPControl.Visibility = Visibility.Collapsed;

            PlateFilterControl.Visibility = Visibility.Collapsed;

            UpdateWindowTitle();

            Console.WriteLine("Project closed");
        }

        private void UpdateWindowTitle(string projectName = null)
        {
            if (string.IsNullOrEmpty(projectName))
            {
                Title = "SCP Browser";
            }
            else
            {
                Title = $"SCP Browser - {projectName}";
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

        // ==================== EXISTING MENU HANDLERS ====================

        private void GoDatabase_Click(object sender, RoutedEventArgs e)
        {
            var manager = new BioTessera.GoAnnotationManager();
            var window = new Window
            {
                Title = "GO Database Manager",
                Content = manager,
                Width = 700,
                Height = 550,
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
                Console.WriteLine($"Error opening project browser: {ex.Message}");
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

                ExportPLPControl.Initialize(data, excludedRuns, cellTypePredictions,
                    rawFileToPlateId, plateIdToName, fastaAnnotations,
                    checkedBioConditions, checkedCellTypes, checkedPlates);
                ExportPLPControl.Show();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error opening PLP export: {ex.Message}");
                MessageBox.Show($"Error opening PLP export:\n\n{ex.Message}", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
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
                        "Supported formats: .tsv, .txt (transcriptomic), .parquet (proteomic), .pref (proteomics reference).",
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
                LoadingOverlay.SetMessage("Importing Transcriptomic Data");
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

                MessageBox.Show(
                                $"Transcriptomic data imported successfully!\n\n" +
                                $"Cell Types: {loadedDatabase.TotalCellTypes}\n" +
                                $"Total Cells: {loadedDatabase.TotalCells:N0}\n" +
                                $"Unique Genes: {loadedDatabase.TotalGenes:N0}\n\n" +
                                $"Database: {Path.GetFileName(referenceDatabasePath)}",
                                "Import Complete",
                                MessageBoxButton.OK,
                                MessageBoxImage.Information);

                Console.WriteLine($"Transcriptomic data imported to: {referenceDatabasePath}");

                // Hot reload: Update MainControl's transcriptomic reference
                await MainControlTab.ReloadTranscriptomicReferenceAsync();
            }
            catch (Exception ex)
            {
                LoadingOverlay.Hide();
                Console.WriteLine($"Error importing transcriptomic data: {ex.Message}");
                MessageBox.Show(
                    $"Error importing transcriptomic data:\n\n{ex.Message}",
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

                Console.WriteLine($"Proteomics reference imported from: {prefFilePath}");

                await MainControlTab.ReloadTranscriptomicReferenceAsync();
            }
            catch (Exception ex)
            {
                LoadingOverlay.Hide();
                Console.WriteLine($"Error importing proteomics reference: {ex.Message}");
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

                Console.WriteLine($"Proteomic reference built from parquet: {parquetFilePath}");

                // Hot reload
                await MainControlTab.ReloadTranscriptomicReferenceAsync();
            }
            catch (Exception ex)
            {
                LoadingOverlay.Hide();
                Console.WriteLine($"Error building proteomic reference: {ex.Message}");
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

                Console.WriteLine($"Stored original data: {originalData?.TotalRawFiles ?? 0} runs");

                // Apply initial filters
                await _dataFilterService.ApplyFiltersAsync(_parquetService);

                // When MainControlTab finishes loading, populate other tabs with the same data
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
                Console.WriteLine($"Loaded {excludedRunNames.Count} excluded runs from database");

                // Load protein annotations BEFORE updating matrix so descriptions are available
                await ProteinMatrixTab.LoadProteinAnnotationsAsync(_currentProjectPath);

                ProteinMatrixTab.UpdateMatrix(_dataFilterService.FilteredData, _dataFilterService.HvpResults);

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

                Console.WriteLine($"GO enrichment results passed: {goResults?.Count ?? 0} runs");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error in DataLoaded handler: {ex.Message}");
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
            var originalData = _dataFilterService.OriginalData;
            if (originalData == null) return;

            originalData.ContaminantIds = new HashSet<string>(contaminantIds, StringComparer.OrdinalIgnoreCase);

            // Recalculate TargetProteinRatioPerFile on OriginalData so it survives re-filtering
            // Stored as fraction (0.0–1.0); display code multiplies by 100
            originalData.TargetProteinRatioPerFile.Clear();
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
                    originalData.TargetProteinRatioPerFile[rawFile] = totalTic > 0
                        ? contaminantAbundance / totalTic
                        : 0;
                }
            }

            // Re-apply filters so PCA/UMAP/classification/HVP exclude contaminants
            await _dataFilterService.ApplyFiltersAsync(_parquetService);

            // Refresh the Explorer chart with updated ratios
            PeptideTicTab.UpdateChart(_dataFilterService.FilteredData);
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
                    Console.WriteLine("No proteomics data available for auto-classification");
                    return;
                }

                // Get the most recent import ID
                int? importId = await _parquetService.GetMostRecentImportIdAsync();
                if (!importId.HasValue)
                {
                    Console.WriteLine("No import ID found for auto-classification");
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
                    Console.WriteLine("No cell type predictions generated");
                    return;
                }

                // Get color map
                var colorMap = MainControlTab.GetCellTypeColorMap();

                // Pass predictions to PeptideTicTab (this will also select Cell Type mode)
                PeptideTicTab.SetCellTypePredictions(predictions, colorMap, selectCellTypeMode: true);

                Console.WriteLine($"Auto cell type classification complete: {predictions.Count} runs classified");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error during auto cell type classification: {ex.Message}");
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
                Console.WriteLine($"[AutoClassify] {message}");
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
                Console.WriteLine("Cell type predictions requested");

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

                Console.WriteLine($"Cell type predictions computed: {predictions.Count} runs classified");
            }
            catch (Exception ex)
            {
                LoadingOverlay.Hide();
                MessageBox.Show($"Error computing cell type predictions:\n\n{ex.Message}",
                    "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
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
                _dataFilterService.ContaminantRatioCutoff = cutoff;
                await _dataFilterService.ApplyFiltersAsync(_parquetService);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error applying contaminant ratio cutoff: {ex.Message}");
            }
        }

        private async void ProjectBrowserDialog_ReclassifyRequested(object sender, ReclassifyRequestedEventArgs e)
        {
            try
            {
                bool applyMarkers = e.ApplyKeyMarkers;
                Console.WriteLine($"Reclassification requested (apply key markers: {applyMarkers})");

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
                
                Console.WriteLine($"Reclassification complete: {predictions.Count} runs, markers applied: {applyMarkers}, excluded: {excludedCount}");
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
                _loadingOverlay.Dispatcher.InvokeAsync(async () =>
                {
                    _loadingOverlay.SetMessage(message);
                    await Task.Delay(10);
                }, System.Windows.Threading.DispatcherPriority.Render);
            }

            public void ReportProgress(string progressDetail)
            {
                _loadingOverlay.Dispatcher.InvokeAsync(async () =>
                {
                    _loadingOverlay.SetProgress(progressDetail);
                    await Task.Delay(10);
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
                    Console.WriteLine($"[Exclusion] Included run: {e.RunName} (ID: {e.RawFileId})");
                }
                else
                {
                    await _parquetService.ExcludeRunAsync(e.RawFileId);
                    Console.WriteLine($"[Exclusion] Excluded run: {e.RunName} (ID: {e.RawFileId})");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Exclusion] Error updating exclusion: {ex.Message}");
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
                Console.WriteLine("[Exclusion] Cleared all exclusions");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Exclusion] Error clearing exclusions: {ex.Message}");
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

            Console.WriteLine($"Added to recent projects: {projectPath}");
        }

        private List<RecentProjectItem> GetRecentProjects()
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
                            var info = service.GetProjectInfoAsync().Result;
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

        private void LoadRecentProjectsUI()
        {
            var recentProjects = GetRecentProjects();

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

            Console.WriteLine($"Loaded {recentProjects.Count} recent projects for display");
        }

        private async void RecentProject_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (sender is Button button && button.Tag is RecentProjectItem item)
                {
                    string projectPath = item.Path;
                    Console.WriteLine($"Recent project clicked: {projectPath}");

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
                            LoadRecentProjectsUI();
                        }

                        return;
                    }

                    // Open the project
                    await OpenProjectAsync(projectPath);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error opening recent project: {ex.Message}");
                MessageBox.Show($"Error opening project:\n\n{ex.Message}", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void RemoveRecentProject_Click(object sender, RoutedEventArgs e)
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
                LoadRecentProjectsUI();
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
                    LoadRecentProjectsUI();
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error editing project: {ex.Message}");
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

                // Reload annotations into memory and refresh the matrix
                await ProteinMatrixTab.LoadProteinAnnotationsAsync(_currentProjectPath);
                if (_dataFilterService?.FilteredData != null)
                {
                    ProteinMatrixTab.UpdateMatrix(_dataFilterService.FilteredData, _dataFilterService.HvpResults);
                }

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