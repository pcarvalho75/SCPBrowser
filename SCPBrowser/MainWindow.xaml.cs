using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using SCPBrowser.GOTools;

namespace SCPBrowser
{
    public partial class MainWindow : Window
    {
        // NEW: Project management fields
        private string _currentProjectPath;
        private ProjectDataService _projectService;
        private bool _hasOpenProject = false;

        public MainWindow()
        {
            InitializeComponent();
            UpdateWindowTitle();

            // CRITICAL: Ensure loading overlay is hidden on startup
            LoadingOverlay.Hide();
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
                _projectService = new ProjectDataService(projectDbPath);

                // Load project info
                var projectInfo = await _projectService.GetProjectInfoAsync();

                if (projectInfo == null)
                {
                    throw new Exception("Invalid project database. Project information not found.");
                }

                _hasOpenProject = true;

                // Update UI
                WelcomeScreen.Visibility = Visibility.Collapsed;
                MainTabControl.Visibility = Visibility.Visible;
                ImportParquetMenuItem.IsEnabled = true;
                CloseProjectMenuItem.IsEnabled = true;

                UpdateWindowTitle(projectInfo.ProjectName);

                LoadingOverlay.Hide();

                Console.WriteLine($"Project opened: {projectInfo.ProjectName}");
                Console.WriteLine($"Created: {projectInfo.CreatedDate}");
                Console.WriteLine($"Location: {Path.GetDirectoryName(projectDbPath)}");
            }
            catch (Exception ex)
            {
                LoadingOverlay.Hide();
                throw;
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
            _projectService = null;
            _hasOpenProject = false;

            // Reset UI
            WelcomeScreen.Visibility = Visibility.Visible;
            MainTabControl.Visibility = Visibility.Collapsed;
            ImportParquetMenuItem.IsEnabled = false;
            CloseProjectMenuItem.IsEnabled = false;

            // Clear tabs (future implementation)
            // MainControlTab.ClearData();
            // PeptideTicTab.ClearData();
            // ProteinMatrixTab.ClearData();
            // GoEnrichmentTab.ClearData();
            // CellTypeTab.ClearData();

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

        private void DBBrowser_Click(object sender, RoutedEventArgs e)
        {
            DBBrowserDialog.Visibility = Visibility.Visible;
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

        // ==================== TOOLS MENU ====================

        private async void ConvertTranscriptomicData_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new TranscriptomicConverterDialog  // CHANGED: was TranscriptomicConverter
            {
                Owner = this
            };

            if (dialog.ShowDialog() != true)
                return;

            var progressReporter = new UIProgressReporter(LoadingOverlay);

            try
            {
                LoadingOverlay.SetMessage("Converting Transcriptomic Data");
                LoadingOverlay.SetProgress("Initializing...");
                LoadingOverlay.Show();

                var parser = new TranscriptomicTsvParser();  // CHANGED: was TranscriptomicParser
                var parsedData = await parser.ParseTranscriptomicDataAsync(
                    dialog.ExpressionFilePath,
                    dialog.MetadataFilePath,
                    progressReporter);

                var referenceService = new ReferenceDataService();

                if (!File.Exists(dialog.OutputDatabasePath))
                {
                    progressReporter.ReportProgress("Creating new database...");
                    await referenceService.CreateDatabaseAsync(dialog.OutputDatabasePath);
                }

                await referenceService.WriteTranscriptomicDataAsync(
                    dialog.OutputDatabasePath,
                    parsedData,
                    true,
                    progressReporter);

                LoadingOverlay.Hide();

                MessageBox.Show(
                    $"Transcriptomic data converted successfully!\n\n" +
                    $"Cell Types: {parsedData.CellTypeProfiles.Count:N0}\n" +
                    $"Total Cells: {parsedData.CellTypeMetadata.Sum(m => m.CellCount):N0}\n" +
                    $"Output: {dialog.OutputDatabasePath}",
                    "Conversion Complete",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                LoadingOverlay.Hide();
                MessageBox.Show(
                    $"Error during conversion:\n\n{ex.Message}",
                    "Conversion Error",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }

        private async void CompileGoAnnotations_Click(object sender, RoutedEventArgs e)
        {
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

            var saveDialog = new SaveFileDialog
            {
                Filter = "SQLite Database (*.db)|*.db|All files (*.*)|*.*",
                Title = "Select Reference Database (will be created or appended to)",
                FileName = "reference_data.db"
            };

            if (saveDialog.ShowDialog() != true)
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

                if (!File.Exists(saveDialog.FileName))
                {
                    progressReporter.ReportProgress("Creating new database...");
                    await referenceService.CreateDatabaseAsync(saveDialog.FileName);
                }

                await referenceService.WriteGoAnnotationsAsync(
                    saveDialog.FileName,
                    goSlimDatabase,
                    compiledDatabase,
                    true,
                    progressReporter);

                progressReporter.ReportMessage("Verifying Database");
                progressReporter.ReportProgress("Reading data back...");

                var (loadedGoSlim, loadedAnnotations) = await referenceService.LoadGoAnnotationsAsync(
                    saveDialog.FileName);

                GC.Collect();
                GC.WaitForPendingFinalizers();
                await Task.Delay(500);

                LoadingOverlay.Hide();

                MessageBox.Show(
                    $"GO annotations compiled successfully!\n\n" +
                    $"GO Terms: {loadedGoSlim.TotalTerms:N0}\n" +
                    $"Annotated Proteins: {loadedAnnotations.TotalProteins:N0}\n" +
                    $"Total Annotations: {loadedAnnotations.TotalAnnotations:N0}\n\n" +
                    $"Database: {saveDialog.FileName}",
                    "Compilation Complete",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                LoadingOverlay.Hide();
                MessageBox.Show(
                    $"Error during compilation:\n\n{ex.Message}",
                    "Compilation Error",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }

        // ==================== TAB CONTROL HANDLERS ====================

        private void MainTabControl_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            // Tab switching logic - can be implemented later as needed
            if (MainTabControl.SelectedItem == GoEnrichmentTabItem)
            {
                // TODO: Implement when integrating with project data
                // GoEnrichmentTab.RefreshFromMainControl(MainControlTab);
            }
        }

        private void MainControlTab_DataLoaded(object sender, EventArgs e)
        {
            // Data loaded event - can be used to update other tabs if needed
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
    }
}