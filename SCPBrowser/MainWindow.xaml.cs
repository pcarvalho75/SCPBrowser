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

namespace SCPBrowser
{
    public partial class MainWindow : Window
    {
        public MainWindow()
        {
            InitializeComponent();
        }

        private void MainTabControl_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
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
                }
                else if (MainTabControl.SelectedIndex == 2)
                {
                    ProteinMatrixTab.UpdateMatrix(data);
                }
            }
        }

        private async void ConvertTranscriptomic_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new TranscriptomicConverterDialog();
            if (dialog.ShowDialog() == true)
            {
                try
                {
                    Mouse.OverrideCursor = Cursors.Wait;

                    await TranscriptomicConverterUtility.ConvertTsvToParquetAsync(
                        dialog.ExpressionFilePath,
                        dialog.MetadataFilePath,
                        dialog.OutputDirectory);

                    Mouse.OverrideCursor = null;

                    MessageBox.Show(
                        $"Conversion completed successfully!\n\n" +
                        $"Files created in:\n{dialog.OutputDirectory}\n\n" +
                        $"Files:\n" +
                        $"- transcriptomic_expression.parquet\n" +
                        $"- transcriptomic_metadata.parquet\n\n" +
                        $"Copy these files to the 'ReferenceData' folder in your application directory.",
                        "Conversion Complete",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                }
                catch (Exception ex)
                {
                    Mouse.OverrideCursor = null;
                    MessageBox.Show(
                        $"Error during conversion:\n\n{ex.Message}",
                        "Conversion Error",
                        MessageBoxButton.OK,
                        MessageBoxImage.Error);
                }
            }
        }

        private async void LoadGoa_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFileDialog
            {
                Filter = "GAF files (*.gaf)|*.gaf|All files (*.*)|*.*",
                Title = "Select GOA GAF File"
            };

            if (dialog.ShowDialog() != true)
                return;

            try
            {
                Mouse.OverrideCursor = Cursors.Wait;

                var parser = new GoAnnotationParser();
                var database = await parser.ParseAndBuildDatabaseAsync(dialog.FileName);

                Mouse.OverrideCursor = null;

                MessageBox.Show(
                    $"GOA loaded successfully!\n\n" +
                    $"Total Proteins: {database.TotalProteins:N0}\n" +
                    $"Total Annotations: {database.TotalAnnotations:N0}\n" +
                    $"Unique GO Terms: {database.GoTermToProteins.Count:N0}",
                    "GOA Test",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                Mouse.OverrideCursor = null;
                MessageBox.Show(
                    $"Error loading GOA:\n\n{ex.Message}",
                    "Load Error",
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
                Filter = "Parquet files (*.parquet)|*.parquet|All files (*.*)|*.*",
                Title = "Save Compiled Annotations",
                FileName = "go_annotations_human.parquet"
            };

            if (saveDialog.ShowDialog() != true)
                return;

            try
            {
                Mouse.OverrideCursor = Cursors.Wait;

                var compiler = new GoAnnotationCompiler();
                var compiledDatabase = await compiler.CompileAnnotationsAsync(
                    oboDialog.FileName,
                    gafDialog.FileName);

                // Save to Parquet
                var parquetService = new GoAnnotationParquetService();
                await parquetService.WriteCompiledAnnotationsAsync(
                    compiledDatabase,
                    saveDialog.FileName);

                // Test reading it back
                var loadedDatabase = await parquetService.ReadCompiledAnnotationsAsync(
                    saveDialog.FileName);

                Mouse.OverrideCursor = null;

                var sampleProteins = loadedDatabase.ProteinToGoTerms.Take(5);
                var sampleText = string.Join("\n", sampleProteins.Select(p =>
                    $"  {p.Key}: {p.Value.Count} GO Slim terms"));

                MessageBox.Show(
                    $"GO Annotations compiled and saved!\n\n" +
                    $"File: {System.IO.Path.GetFileName(saveDialog.FileName)}\n\n" +
                    $"Proteins: {loadedDatabase.TotalProteins:N0}\n" +
                    $"Annotations: {loadedDatabase.TotalAnnotations:N0}\n" +
                    $"GO Slim terms: {loadedDatabase.GoTermToProteins.Count:N0}\n\n" +
                    $"Sample proteins:\n{sampleText}",
                    "Compilation Complete",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                Mouse.OverrideCursor = null;
                MessageBox.Show(
                    $"Error:\n\n{ex.Message}",
                    "Error",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }

        private async void LoadGoSlim_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFileDialog
            {
                Filter = "OBO files (*.obo)|*.obo|All files (*.*)|*.*",
                Title = "Select GO Slim OBO File"
            };

            if (dialog.ShowDialog() != true)
                return;

            try
            {
                Mouse.OverrideCursor = Cursors.Wait;

                var parser = new GoSlimParser();
                var database = await parser.ParseOboFileAsync(dialog.FileName);

                Mouse.OverrideCursor = null;

                MessageBox.Show(
                    $"GO Slim loaded successfully!\n\n" +
                    $"Total Terms: {database.TotalTerms:N0}\n" +
                    $"Biological Process: {database.BiologicalProcessCount:N0}\n" +
                    $"Molecular Function: {database.MolecularFunctionCount:N0}\n" +
                    $"Cellular Component: {database.CellularComponentCount:N0}\n" +
                    $"Annotatable Terms: {database.AnnotatableTerms:N0}\n\n" +
                    $"Sample terms:\n" +
                    string.Join("\n", database.Terms.Values.Take(5).Select(t => $"  {t.Id} - {t.Name}")),
                    "GO Slim Test",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                Mouse.OverrideCursor = null;
                MessageBox.Show(
                    $"Error loading GO Slim:\n\n{ex.Message}",
                    "Load Error",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }

        private void About_Click(object sender, RoutedEventArgs e)
        {
            MessageBox.Show(
                "SCP Browser - Single Cell Proteomics Analysis Tool\n\n" +
                "Version 1.0\n\n" +
                "Developed at Fiocruz\n" +
                "Computational Proteomics",
                "About SCP Browser",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }

        private void Exit_Click(object sender, RoutedEventArgs e)
        {
            Application.Current.Shutdown();
        }

        private async void TestEnrichment_Click(object sender, RoutedEventArgs e)
        {
            var oboDialog = new OpenFileDialog
            {
                Filter = "OBO files (*.obo)|*.obo|All files (*.*)|*.*",
                Title = "Select GO Slim OBO File"
            };
            if (oboDialog.ShowDialog() != true) return;

            var parquetDialog = new OpenFileDialog
            {
                Filter = "Parquet files (*.parquet)|*.parquet|All files (*.*)|*.*",
                Title = "Select Compiled GO Annotations Parquet"
            };
            if (parquetDialog.ShowDialog() != true) return;

            try
            {
                Mouse.OverrideCursor = Cursors.Wait;

                var goSlimParser = new GoSlimParser();
                var goSlimDb = await goSlimParser.ParseOboFileAsync(oboDialog.FileName);

                var parquetService = new GoAnnotationParquetService();
                var annotationDb = await parquetService.ReadCompiledAnnotationsAsync(parquetDialog.FileName);

                // Test with a small sample of proteins (first 500)
                var testProteins = annotationDb.ProteinToGoTerms.Keys.Take(500).ToList();

                var analyzer = new GoEnrichmentAnalyzer(goSlimDb, annotationDb);
                var enrichmentResults = analyzer.AnalyzeEnrichment(testProteins, pValueThreshold: 0.05);

                Mouse.OverrideCursor = null;

                var topResults = enrichmentResults.Take(10);
                var resultsText = string.Join("\n", topResults.Select(r =>
                    $"  {r.GoTermName}: p={r.PValue:E2}, {r.Overlap}/{r.ProteinsInSample} proteins"));

                MessageBox.Show(
                    $"Enrichment Analysis Complete!\n\n" +
                    $"Sample size: {testProteins.Count} proteins\n" +
                    $"Significant GO terms: {enrichmentResults.Count}\n\n" +
                    $"Top 10 enriched terms:\n{resultsText}",
                    "Enrichment Test",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                Mouse.OverrideCursor = null;
                MessageBox.Show($"Error:\n\n{ex.Message}", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }
}