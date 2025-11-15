using System;
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
        private ProjectDataService _projectService;
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

        // In SCPBrowser/MainWindow.xaml.cs

        private async Task OpenProjectAsync(string projectDbPath)
        {
            try
            {
                LoadingOverlay.SetMessage("Opening Project");
                LoadingOverlay.SetProgress("Loading project information...");
                LoadingOverlay.Show();

                _currentProjectPath = projectDbPath;
                _projectService = new ProjectDataService(projectDbPath);

                // Load project info
                var projectInfo = await _projectService.GetProjectInfoAsync();

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

                UpdateWindowTitle(projectInfo.ProjectName);

                // Find and load the last imported parquet file
                LoadingOverlay.SetProgress("Finding associated data...");

                string lastImportedFile = await _projectService.GetLastImportedParquetFileAsync();
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
                await MainControlTab.LoadDataFromProject(parquetPath);

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
            _projectService = null;
            _hasOpenProject = false;
            _projectReferenceDatabasePath = null;

            // Reset UI
            WelcomeScreen.Visibility = Visibility.Visible;
            MainTabControl.Visibility = Visibility.Collapsed;
            ImportParquetMenuItem.IsEnabled = false;
            ImportOmicProfileMenuItem.IsEnabled = false;
            ImportGoAnnotationsMenuItem.IsEnabled = false;
            CloseProjectMenuItem.IsEnabled = false;

            ProjectBrowserMenuItem.IsEnabled = false;

            // Clear tabs (future implementation)
            // MainControlTab.ClearData();
            // PeptideTicTab.ClearData();
            // ProteinMatrixTab.ClearData();
            // GoEnrichmentTab.ClearData();

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
            if (!_hasOpenProject || _projectService == null)
            {
                MessageBox.Show(
                    "Please open or create a project first.",
                    "No Project Open",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            string projectDirectory = Path.GetDirectoryName(_currentProjectPath);

            var dialog = new ImportParquetDialog(_projectService, projectDirectory)
            {
                Owner = this
            };

            if (dialog.ShowDialog() == true && dialog.ImportSuccessful)
            {
                MessageBox.Show(
                    "Parquet file import completed successfully!",
                    "Import Complete",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);

                // TODO: Refresh project data display
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
            if (!_hasOpenProject || _projectService == null)
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
                InitialDirectory = Path.GetDirectoryName(expressionFilePath)
            };

            if (metadataDialog.ShowDialog() != true)
                return;

            string metadataFilePath = metadataDialog.FileName;

            // Use the project database directly (unified database)
            string referenceDatabasePath = _projectReferenceDatabasePath;

            // Confirm the import
            var result = MessageBox.Show(
                $"Import transcriptomic data to project database?\n\n" +
                $"Expression Matrix: {Path.GetFileName(expressionFilePath)}\n" +
                $"Cell Metadata: {Path.GetFileName(metadataFilePath)}\n\n" +
                $"Target Database: {Path.GetFileName(referenceDatabasePath)}\n" +
                $"Location: {Path.GetDirectoryName(referenceDatabasePath)}",
                "Confirm Import",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (result != MessageBoxResult.Yes)
                return;

            var progressReporter = new UIProgressReporter(LoadingOverlay);

            try
            {
                LoadingOverlay.SetMessage("Importing Transcriptomic Data");
                LoadingOverlay.SetProgress("Parsing TSV files...");
                LoadingOverlay.Show();

                var parser = new TranscriptomicTsvParser();
                var parsedData = await parser.ParseTranscriptomicDataAsync(
                    expressionFilePath,
                    metadataFilePath,
                    progressReporter);

                var referenceService = new ReferenceDataService();

                // Write transcriptomic data to the project database
                // (database already exists from project creation)
                progressReporter.ReportProgress("Writing to project database...");
                await referenceService.WriteTranscriptomicDataAsync(
                    referenceDatabasePath,
                    parsedData,
                    true,  // overwrite existing data
                    progressReporter);

                LoadingOverlay.Hide();

                MessageBox.Show(
                    $"Transcriptomic data imported successfully!\n\n" +
                    $"Cell Types: {parsedData.CellTypeProfiles.Count:N0}\n" +
                    $"Total Cells: {parsedData.CellTypeMetadata.Sum(m => m.CellCount):N0}\n" +
                    $"Total Genes: {parsedData.CellTypeProfiles.FirstOrDefault()?.MedianExpression.Count ?? 0:N0}\n\n" +
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
            if (!_hasOpenProject || _projectService == null)
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
                Title = "Select GOA GAF File"
            };

            if (gafDialog.ShowDialog() != true)
                return;

            // Use the project database directly (unified database)
            string referenceDatabasePath = _projectReferenceDatabasePath;

            // Confirm the import
            var result = MessageBox.Show(
                $"Compile GO annotations to project database?\n\n" +
                $"GO Slim OBO: {Path.GetFileName(oboDialog.FileName)}\n" +
                $"GOA GAF: {Path.GetFileName(gafDialog.FileName)}\n\n" +
                $"Target Database: {Path.GetFileName(referenceDatabasePath)}\n" +
                $"Location: {Path.GetDirectoryName(referenceDatabasePath)}",
                "Confirm Compilation",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (result != MessageBoxResult.Yes)
                return;

            var progressReporter = new UIProgressReporter(LoadingOverlay);

            try
            {
                LoadingOverlay.SetMessage("Compiling GO Annotations");
                LoadingOverlay.SetProgress("Initializing...");
                LoadingOverlay.Show();

                var compiler = new GoAnnotationCompiler();
                var compiledDatabase = await compiler.CompileAnnotationsAsync(
                    oboDialog.FileName,
                    gafDialog.FileName,
                    progressReporter);

                var goSlimParser = new GoSlimParser();
                var goSlimDatabase = await goSlimParser.ParseOboFileAsync(oboDialog.FileName);

                var referenceService = new ReferenceDataService();

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

        // ==================== TAB CONTROL HANDLERS ====================

        private void MainTabControl_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            // Tab switching logic - can be implemented later as needed
        }

        private void MainControlTab_DataLoaded(object sender, EventArgs e)
        {
            Console.WriteLine("MainControlTab_DataLoaded event fired");

            WelcomeScreen.Visibility = Visibility.Collapsed;
            MainTabControl.Visibility = Visibility.Visible;

            // Get the loaded data from MainControlTab
            var data = MainControlTab.GetCurrentData();

            if (data == null)
            {
                Console.WriteLine("No data to populate other tabs");
                return;
            }

            Console.WriteLine($"Populating tabs with data: {data.TotalRawFiles} runs, {data.TotalProteinGroups} proteins");

            // Populate PeptideTicTab
            try
            {
                var fileDirectory = MainControlTab.GetCurrentFileDirectory();
                PeptideTicTab.SetImageBaseDirectory(fileDirectory);
                PeptideTicTab.UpdateChart(data);

                // Subscribe to cell type prediction requests
                PeptideTicTab.CellTypePredictionsRequested -= PeptideTicTab_CellTypePredictionsRequested;
                PeptideTicTab.CellTypePredictionsRequested += PeptideTicTab_CellTypePredictionsRequested;

                // Set predictions if available
                var predictions = MainControlTab.GetCellTypePredictions();
                if (predictions != null && predictions.Count > 0)
                {
                    var colorMap = MainControlTab.GetCellTypeColorMap();
                    PeptideTicTab.SetCellTypePredictions(predictions, colorMap);
                }

                // Set GO enrichment if available
                var goResults = MainControlTab.GetGoEnrichmentResults();
                if (goResults != null && goResults.Count > 0)
                {
                    var goColorMap = MainControlTab.GetGoTermColorMap();
                    PeptideTicTab.SetGoEnrichmentResults(goResults, goColorMap);
                }

                Console.WriteLine("PeptideTicTab populated successfully");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error populating PeptideTicTab: {ex.Message}");
            }

            // Populate ProteinMatrixTab
            try
            {
                ProteinMatrixTab.UpdateMatrix(data);
                Console.WriteLine("ProteinMatrixTab populated successfully");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error populating ProteinMatrixTab: {ex.Message}");
            }

            Console.WriteLine("All tabs populated from loaded data");
        }

        private async void PeptideTicTab_CellTypePredictionsRequested(object sender, EventArgs e)
        {
            try
            {
                // Get current import ID
                int? importId = await _projectService.GetMostRecentImportIdAsync();
                if (!importId.HasValue)
                {
                    MessageBox.Show("No parquet data has been imported.", "No Data", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                // Get current proteomics data
                var currentData = MainControlTab.GetCurrentData();
                if (currentData == null)
                {
                    MessageBox.Show("No proteomics data available.", "No Data", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                // Show loading overlay with progress
                var progressReporter = new UIProgressReporter(LoadingOverlay);
                LoadingOverlay.SetMessage("Cell Type Classification");
                LoadingOverlay.Show();

                // Get or compute predictions
                var predictions = await MainControlTab.GetCellTypePredictionsAsync(
                    currentData,
                    _projectReferenceDatabasePath,
                    importId.Value,
                    progressReporter);

                // Pass predictions to PeptideTicTab
                var colorMap = MainControlTab.GetCellTypeColorMap();
                PeptideTicTab.SetCellTypePredictions(predictions, colorMap);

                LoadingOverlay.Hide();
            }
            catch (Exception ex)
            {
                LoadingOverlay.Hide();
                MessageBox.Show(
                    $"Error computing cell type predictions:\n\n{ex.Message}",
                    "Prediction Error",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }

        // ==================== PROGRESS REPORTER ====================

        private class UIProgressReporter : IProgressReporter
        {
            private readonly LoadingOverlay _loadingOverlay;

            public UIProgressReporter(LoadingOverlay loadingOverlay)
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

        private void ClearRecentProjects()
        {
            if (Settings.Default.RecentProjects != null)
            {
                Settings.Default.RecentProjects.Clear();
                Settings.Default.Save();
            }
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