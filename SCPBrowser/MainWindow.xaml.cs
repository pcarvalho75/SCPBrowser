using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;
using Microsoft.Win32;
using System.IO;
using Path = System.IO.Path;
using SCPBrowser.GOTools;

namespace SCPBrowser
{
    public partial class MainWindow : Window
    {
        public MainWindow()
        {
            InitializeComponent();
            Console.Clear();

        }

        private void MainControlTab_DataLoaded(object sender, EventArgs e)
        {
            LogoOverlay.Visibility = Visibility.Collapsed;
        }

        private void MainTabControl_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            // Only handle if the event is from MainTabControl itself, not from child TabControls
            if (e.Source != MainTabControl)
            {
                Console.WriteLine($"MainTabControl_SelectionChanged: Ignoring event from {e.Source?.GetType().Name}");
                return;
            }

            Console.WriteLine("MainTabControl_SelectionChanged: Processing main tab change");

            var data = MainControlTab.GetCurrentData();
            var imageDirectory = MainControlTab.GetCurrentFileDirectory();
            var cellTypePredictions = MainControlTab.GetCellTypePredictions();
            var cellTypeColorMap = MainControlTab.GetCellTypeColorMap();

            if (data != null)
            {
                if (MainTabControl.SelectedIndex == 1)
                {
                    PeptideTicTab.UpdateChart(data);

                    if (!string.IsNullOrEmpty(imageDirectory))
                    {
                        PeptideTicTab.SetImageBaseDirectory(imageDirectory);
                    }

                    if (cellTypePredictions != null && cellTypePredictions.Count > 0)
                    {
                        PeptideTicTab.SetCellTypePredictions(cellTypePredictions, cellTypeColorMap);
                    }

                    var goEnrichmentResults = MainControlTab.GetGoEnrichmentResults();
                    var goTermColorMap = MainControlTab.GetGoTermColorMap();

                    if (goEnrichmentResults != null && goEnrichmentResults.Count > 0)
                    {
                        PeptideTicTab.SetGoEnrichmentResults(goEnrichmentResults, goTermColorMap);
                    }
                }
                else if (MainTabControl.SelectedIndex == 2)
                {
                    ProteinMatrixTab.UpdateMatrix(data);
                }
            }
        }

        private async void ConvertTranscriptomicData_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new TranscriptomicConverterDialog();
            if (dialog.ShowDialog() == true)
            {
                // Create UI progress reporter
                var progressReporter = new UIProgressReporter(LoadingOverlay);

                try
                {
                    LoadingOverlay.SetMessage("Converting Transcriptomic Data");
                    LoadingOverlay.SetProgress("Initializing...");
                    LoadingOverlay.Show();

                    await TranscriptomicConverterUtility.ConvertTsvToSqliteAsync(
                        dialog.ExpressionFilePath,
                        dialog.MetadataFilePath,
                        dialog.OutputDatabasePath,
                        progressReporter);

                    LoadingOverlay.Hide();

                    var fileInfo = new FileInfo(dialog.OutputDatabasePath);
                    var fileSizeMB = fileInfo.Length / (1024.0 * 1024.0);

                    MessageBox.Show(
                        $"Conversion completed successfully!\n\n" +
                        $"Database created:\n{dialog.OutputDatabasePath}\n\n" +
                        $"Size: {fileSizeMB:F2} MB\n\n" +
                        $"This database now contains the transcriptomic reference data.\n" +
                        $"You can now add GO annotations to this same database using\n" +
                        $"Tools > Compile GO Annotations.",
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
        }

        private class UIProgressReporter : IProgressReporter
        {
            private readonly LoadingOverlay _loadingOverlay;

            public UIProgressReporter(LoadingOverlay loadingOverlay)
            {
                _loadingOverlay = loadingOverlay;
            }

            public void ReportMessage(string message)
            {
                // Use InvokeAsync for better UI responsiveness
                _loadingOverlay.Dispatcher.InvokeAsync(async () =>
                {
                    _loadingOverlay.SetMessage(message);
                    // Small delay to ensure UI updates are rendered
                    await Task.Delay(10);
                }, System.Windows.Threading.DispatcherPriority.Render);
            }

            public void ReportProgress(string progressDetail)
            {
                // Use InvokeAsync for better UI responsiveness
                _loadingOverlay.Dispatcher.InvokeAsync(async () =>
                {
                    _loadingOverlay.SetProgress(progressDetail);
                    // Small delay to ensure UI updates are rendered
                    await Task.Delay(10);
                }, System.Windows.Threading.DispatcherPriority.Render);
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

            // Create UI progress reporter
            var progressReporter = new UIProgressReporter(LoadingOverlay);

            try
            {
                LoadingOverlay.SetMessage("Compiling GO Annotations");
                LoadingOverlay.SetProgress("Initializing...");
                LoadingOverlay.Show();

                // Compile annotations from GAF with progress reporting
                var compiler = new GoAnnotationCompiler();
                var compiledDatabase = await compiler.CompileAnnotationsAsync(
                    oboDialog.FileName,
                    gafDialog.FileName,
                    progressReporter);

                // Parse GO Slim OBO
                var goSlimParser = new GoSlimParser();
                var goSlimDatabase = await goSlimParser.ParseOboFileAsync(oboDialog.FileName);

                // Create or append to database
                var referenceService = new ReferenceDataService();

                if (!File.Exists(saveDialog.FileName))
                {
                    progressReporter.ReportProgress("Creating new database...");
                    await referenceService.CreateDatabaseAsync(saveDialog.FileName);
                }

                // Write GO annotations with progress reporting
                await referenceService.WriteGoAnnotationsAsync(
                    saveDialog.FileName,
                    goSlimDatabase,
                    compiledDatabase,
                    true,
                    progressReporter);

                // Test reading it back
                progressReporter.ReportMessage("Verifying Database");
                progressReporter.ReportProgress("Reading data back...");

                var (loadedGoSlim, loadedAnnotations) = await referenceService.LoadGoAnnotationsAsync(
                    saveDialog.FileName);

                // ✅ NEW: Force cleanup after verification
                GC.Collect();
                GC.WaitForPendingFinalizers();
                await Task.Delay(500); // Give Windows time to release the file lock

                LoadingOverlay.Hide();

                var fileInfo = new FileInfo(saveDialog.FileName);
                var fileSizeMB = fileInfo.Length / (1024.0 * 1024.0);

                var sampleProteins = loadedAnnotations.ProteinToGoTerms.Take(5);
                var sampleText = string.Join("\n", sampleProteins.Select(p =>
                    $"  {p.Key}: {p.Value.Count} GO Slim terms"));

                MessageBox.Show(
                    $"GO Annotations compiled and saved!\n\n" +
                    $"File: {Path.GetFileName(saveDialog.FileName)}\n" +
                    $"Size: {fileSizeMB:F2} MB\n\n" +
                    $"GO Terms stored: {loadedGoSlim.TotalTerms:N0}\n" +
                    $"Proteins: {loadedAnnotations.TotalProteins:N0}\n" +
                    $"Annotations: {loadedAnnotations.TotalAnnotations:N0}\n" +
                    $"GO Slim terms used: {loadedAnnotations.GoTermToProteins.Count:N0}\n\n" +
                    $"Sample proteins:\n{sampleText}\n\n" +
                    $"The database is now ready for transcriptomic data or use in analysis.",
                    "Compilation Complete",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                LoadingOverlay.Hide();
                MessageBox.Show(
                    $"Error during GO annotation compilation:\n\n{ex.Message}",
                    "Compilation Error",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }
        private void OpenDiannFile_Click(object sender, RoutedEventArgs e)
        {
            MainControlTab.OpenDiannFile();
        }

        private void Settings_Click(object sender, RoutedEventArgs e)
        {
            Console.WriteLine("Opening Settings...");
            // Hide DB Browser if it's open
            DBBrowserOverlay.Visibility = Visibility.Collapsed;

            // Find the Settings tab by its content type
            foreach (TabItem tab in MainTabControl.Items)
            {
                if (tab.Content is SettingsControl)
                {
                    tab.IsSelected = true;
                    break;
                }
            }
        }

        private void DBBrowser_Click(object sender, RoutedEventArgs e)
        {
            Console.WriteLine("Opening DB Browser...");
            DBBrowserOverlay.Visibility = Visibility.Visible;
        }

        private void About_Click(object sender, RoutedEventArgs e)
        {
            MessageBox.Show(
                "SCP Browser - Single Cell Proteomics Analysis Tool\n\n" +
                "Version 1.0\n\n" +
                "Developed at Fiocruz Paraná and ISSCOR / UCSD\n" +
                "Computational Proteomics",
                "About SCP Browser",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }

        private void Exit_Click(object sender, RoutedEventArgs e)
        {
            Application.Current.Shutdown();
        }
    }
}