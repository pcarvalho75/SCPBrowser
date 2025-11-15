using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using SCPBrowser.GOTools;
using SCPBrowser.Services;

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

        // Public properties for controls to access
        public bool HasOpenProject => _hasOpenProject;
        public string ProjectReferenceDatabasePath => _projectReferenceDatabasePath;

        public MainWindow()
        {
            InitializeComponent();
            UpdateWindowTitle();

            // CRITICAL: Ensure loading overlay is hidden on startup
            LoadingOverlay.Hide();

            // Load recent projects
            LoadRecentProjectsUI();

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

                _currentProjectPath = projectDbPath;

                // Initialize all services
                _projectDatabaseService = new ProjectDatabaseService(projectDbPath);
                _parquetService = new ParquetDataService(projectDbPath);
                _plateService = new PlateService(projectDbPath);
                _cellTypeService = new CellTypeClassificationService(projectDbPath);

                // Ensure new tables exist (migration for existing projects)
                await _projectDatabaseService.EnsureCellTypeClassificationsTableExistsAsync();

                // Load project info
                var projectInfo = await _projectDatabaseService.GetProjectInfoAsync();

                if (projectInfo == null)
                {
                    throw new Exception("Invalid project database. Project information not found.");
                }

                _hasOpenProject = true;

                // The project database IS the reference database (unified)
                _projectReferenceDatabasePath = projectDbPath;

                // Update UI
                WelcomeScreen.Visibility = Visibility.Collapsed;
                MainTabControl.Visibility = Visibility.Visible;
                ImportParquetMenuItem.IsEnabled = true;
                ImportOmicProfileMenuItem.IsEnabled = true;
                ImportGoAnnotationsMenuItem.IsEnabled = true;
                CloseProjectMenuItem.IsEnabled = true;
                ClearCellTypeClassificationsMenuItem.IsEnabled = true;

                UpdateWindowTitle(projectInfo.ProjectName);

                // Find and load the last imported parquet file
                LoadingOverlay.SetProgress("Finding associated data...");

                string lastImportedFile = await _parquetService.GetLastImportedParquetFileAsync();
                string parquetPath = null;

                if (!string.IsNullOrEmpty(lastImportedFile))
                {
                    string projectDirectory = Path.GetDirectoryName(_currentProjectPath);
                    parquetPath = Path.Combine(projectDirectory, "imports", lastImportedFile);

                    if (!File.Exists(parquetPath))
                    {
                        LoadingOverlay.Hide();
                        MessageBox.Show(
                            $"Data file '{lastImportedFile}' not found in imports folder.\n\nPlease re-import your data.",
                            "Data File Missing",
                            MessageBoxButton.OK,
                            MessageBoxImage.Warning);
                        parquetPath = null;
                    }
                    else
                    {
                        LoadingOverlay.SetProgress($"Loading data from {lastImportedFile}...");
                    }
                }
                else
                {
                    LoadingOverlay.SetProgress("No data imported yet. Please import a Parquet file.");
                }

                // Load data into MainControlTab
                // This will trigger the DataLoaded event which populates other tabs
                await MainControlTab.LoadDataFromProject(parquetPath, _projectReferenceDatabasePath);

                LoadingOverlay.Hide();

                Console.WriteLine($"Project opened: {projectInfo.ProjectName}");
                Console.WriteLine($"Created: {projectInfo.CreatedDate}");
                Console.WriteLine($"Location: {Path.GetDirectoryName(projectDbPath)}");

                AddToRecentProjects(projectDbPath);

                ProjectBrowserMenuItem.IsEnabled = true;
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

        private void CloseProject()
        {
            _currentProjectPath = null;
            _projectDatabaseService = null;
            _parquetService = null;
            _plateService = null;
            _cellTypeService = null;
            _hasOpenProject = false;
            _projectReferenceDatabasePath = null;

            // Reset UI
            WelcomeScreen.Visibility = Visibility.Visible;
            MainTabControl.Visibility = Visibility.Collapsed;
            ImportParquetMenuItem.IsEnabled = false;
            ImportOmicProfileMenuItem.IsEnabled = false;
            ImportGoAnnotationsMenuItem.IsEnabled = false;
            CloseProjectMenuItem.IsEnabled = false;
            ClearCellTypeClassificationsMenuItem.IsEnabled = false;

            ProjectBrowserMenuItem.IsEnabled = false;

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

        private void ImportParquet_Click(object sender, RoutedEventArgs e)
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

            // Pass the new specialized services to ImportParquetDialog
            var dialog = new ImportParquetDialog(
                _parquetService,      // For parquet operations
                _plateService,        // For plate operations  
                projectDirectory)
            {
                Owner = this
            };

            if (dialog.ShowDialog() == true)
            {
                MessageBox.Show(
                    "Parquet data imported successfully!",
                    "Import Complete",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);

                // Refresh the UI if needed
                if (_hasOpenProject)
                {
                    // Trigger any necessary UI updates here
                }
            }
        }

        // ==================== EXISTING MENU HANDLERS ====================

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
            if (!_hasOpenProject)
                return;

            await ProjectBrowserDialog.ShowWithDatabaseAsync(_projectReferenceDatabasePath);
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

            // Step 1: Select Gene Expression Matrix file
            var expressionDialog = new OpenFileDialog
            {
                Filter = "TSV files (*.tsv)|*.tsv|Text files (*.txt)|*.txt|All files (*.*)|*.*",
                Title = "Select Gene Expression Matrix TSV File",
                InitialDirectory = projectDirectory
            };

            if (expressionDialog.ShowDialog() != true)
                return;

            string expressionFilePath = expressionDialog.FileName;

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

                // The reference database is the project database itself
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

        private async void CompileGoAnnotations_Click(object sender, RoutedEventArgs e)
        {
            // Project check - menu item is disabled when no project is open
            if (!_hasOpenProject || _projectDatabaseService == null)
            {
                MessageBox.Show(
                    "Please open or create a project first.\n\nGO annotations import requires an active project.",
                    "No Project Open",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            var oboDialog = new OpenFileDialog
            {
                Filter = "OBO files (*.obo)|*.obo|All files (*.*)|*.*",
                Title = "Select GO Slim OBO File"
            };

            if (oboDialog.ShowDialog() != true)
                return;

            var gafDialog = new OpenFileDialog
            {
                Filter = "GAF files (*.gaf)|*.gaf|All files (*.*)|*.*",
                Title = "Select UniProt GAF Annotation File"
            };

            if (gafDialog.ShowDialog() != true)
                return;

            try
            {
                LoadingOverlay.SetMessage("Compiling GO Annotations");
                LoadingOverlay.Show();

                var progressReporter = new LoadingOverlayProgressReporter(LoadingOverlay);

                // Step 1: Compile annotations using GoAnnotationCompiler
                progressReporter.ReportMessage("Compiling GO Annotations");
                var compiler = new GoAnnotationCompiler();
                var compiledDatabase = await compiler.CompileAnnotationsAsync(
                    oboDialog.FileName,
                    gafDialog.FileName,
                    progressReporter);

                // Step 2: Parse GO Slim for term definitions
                progressReporter.ReportMessage("Parsing GO Slim");
                var goSlimParser = new GoSlimParser();
                var goSlimDatabase = await goSlimParser.ParseOboFileAsync(oboDialog.FileName);

                // Step 3: Write to unified project database
                progressReporter.ReportMessage("Writing to Database");
                var referenceService = new ReferenceDataService();
                string referenceDatabasePath = _projectReferenceDatabasePath;

                // Write to project database (already exists, no need to create)
                await referenceService.WriteGoAnnotationsAsync(
                    referenceDatabasePath,
                    goSlimDatabase,
                    compiledDatabase,
                    true,
                    progressReporter);

                progressReporter.ReportMessage("Verifying Database");
                progressReporter.ReportProgress("Loading data back to verify...");

                var (loadedGoSlim, loadedAnnotations) = await referenceService.LoadGoAnnotationsAsync(referenceDatabasePath);

                LoadingOverlay.Hide();

                MessageBox.Show(
                    $"GO annotations compiled successfully!\n\n" +
                    $"GO Terms: {loadedGoSlim.Terms.Count:N0}\n" +
                    $"Annotated Proteins: {loadedAnnotations.TotalProteins:N0}\n" +
                    $"Total Annotations: {loadedAnnotations.TotalAnnotations:N0}\n\n" +
                    $"Database: {Path.GetFileName(referenceDatabasePath)}",
                    "Compilation Complete",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);

                Console.WriteLine($"GO annotations compiled to: {referenceDatabasePath}");
            }
            catch (Exception ex)
            {
                LoadingOverlay.Hide();
                Console.WriteLine($"Error compiling GO annotations: {ex.Message}");
                MessageBox.Show(
                    $"Error compiling GO annotations:\n\n{ex.Message}",
                    "Compilation Error",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }

        // ==================== TAB CONTROL ====================

        private void MainTabControl_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            // Future: Handle tab switching logic if needed
        }

        private void MainControlTab_DataLoaded(object sender, EventArgs e)
        {
            // When MainControlTab finishes loading, populate other tabs with the same data
            PeptideTicTab.UpdateChart(MainControlTab.GetCurrentData());
            ProteinMatrixTab.UpdateMatrix(MainControlTab.GetCurrentData());

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


            Console.WriteLine($"GO enrichment results passed: {goResults?.Count ?? 0} runs");

            // Wire up the event handler for cell type predictions
            PeptideTicTab.CellTypePredictionsRequested -= PeptideTicTab_CellTypePredictionsRequested;
            PeptideTicTab.CellTypePredictionsRequested += PeptideTicTab_CellTypePredictionsRequested;

            Console.WriteLine($"Cell type classification enabled: {cellTypeAvailable}");
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
                var predictions = await MainControlTab.GetCellTypePredictionsAsync(
                    proteomicsData,
                    _projectReferenceDatabasePath,
                    importId.Value,
                    progressReporter);

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

        private List<string> GetRecentProjects()
        {
            var recentProjects = new List<string>();

            if (Settings.Default.RecentProjects != null)
            {
                foreach (string projectPath in Settings.Default.RecentProjects)
                {
                    // Only include projects that still exist
                    if (File.Exists(projectPath))
                    {
                        recentProjects.Add(projectPath);
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

        private void LoadParquet(object sender, RoutedEventArgs e)
        {
            MainControlTab.OpenDiannFile();
        }

        private async void RecentProject_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button button && button.Tag is string projectPath)
            {
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
    }
}