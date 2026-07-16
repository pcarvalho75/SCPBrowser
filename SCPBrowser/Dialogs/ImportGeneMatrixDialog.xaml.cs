using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using Microsoft.Win32;
using SCPBrowser.Models;
using SCPBrowser.Services;

namespace SCPBrowser
{
    /// <summary>
    /// Imports a gene × sample .xlsx matrix (DIA-NN gene/pg matrix exported to Excel). Parses it into ProteomicsData,
    /// auto-derives the biological condition (cell type) from each sample's folder path, and persists it through the
    /// same tables the parquet importer uses so the reopen/analysis path works unchanged.
    /// </summary>
    public partial class ImportGeneMatrixDialog : Window
    {
        private readonly ParquetDataService _parquetService;
        private readonly PlateService _plateService;
        private readonly string _projectDirectory;

        private string _selectedFilePath;
        private ProteomicsData _parsedData;

        private readonly ObservableCollection<RawFileInfo> _rawFiles = new();
        private readonly ObservableCollection<PlateInfo> _plates = new();

        public bool ImportSuccessful { get; private set; }

        public ImportGeneMatrixDialog(ParquetDataService parquetService, PlateService plateService, string projectDirectory)
        {
            InitializeComponent();
            _parquetService = parquetService;
            _plateService = plateService;
            _projectDirectory = projectDirectory;

            RawFilesDataGrid.ItemsSource = _rawFiles;
            PlateComboBox.ItemsSource = _plates;

            Loaded += async (_, __) => await LoadPlatesAsync();
        }

        private async System.Threading.Tasks.Task LoadPlatesAsync()
        {
            try
            {
                var plates = await _plateService.GetPlatesAsync();
                _plates.Clear();
                foreach (var p in plates) _plates.Add(p);
                if (_plates.Count > 0) PlateComboBox.SelectedIndex = 0;
            }
            catch { /* plate list is optional — a plate is auto-created on import if none is chosen */ }
        }

        private void BrowseFile_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog
            {
                Filter = "Excel gene matrix (*.xlsx)|*.xlsx",
                Title = "Select a DIA-NN gene matrix (.xlsx)"
            };
            if (dlg.ShowDialog() != true) return;

            try
            {
                Mouse.OverrideCursor = Cursors.Wait;
                _selectedFilePath = dlg.FileName;
                FilePathTextBox.Text = _selectedFilePath;

                _parsedData = XlsxGeneMatrixParser.Parse(_selectedFilePath);
                PopulateGrid(_parsedData);
                UpdateSummary();
                ImportButton.IsEnabled = _rawFiles.Count > 0;

                if (_rawFiles.Count == 0)
                    MessageBox.Show(
                        "No samples with detected genes were found. Make sure this is a DIA-NN gene matrix " +
                        "(gene symbols as rows, samples as columns, intensities in the cells).",
                        "Import", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Could not read the Excel matrix:\n\n{ex.Message}", "Import",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                ImportButton.IsEnabled = false;
            }
            finally { Mouse.OverrideCursor = null; }
        }

        private void PopulateGrid(ProteomicsData data)
        {
            _rawFiles.Clear();
            foreach (var run in data.RawFileNames)
            {
                data.BiologicalConditionPerFile.TryGetValue(run, out var cond);
                _rawFiles.Add(new RawFileInfo
                {
                    RawFileName = run,
                    BiologicalCondition = cond ?? "",
                    ProteinCount = data.ProteinCountPerFile.TryGetValue(run, out var pc) ? pc : 0,
                    PeptideCount = data.PeptideCountPerFile.TryGetValue(run, out var pep) ? pep : 0,
                    TotalIonCurrent = data.TotalIonCurrentPerFile.TryGetValue(run, out var tic) ? tic : 0
                });
            }
        }

        private void UpdateSummary()
        {
            int conds = _rawFiles.Select(r => r.BiologicalCondition)
                                 .Where(c => !string.IsNullOrEmpty(c))
                                 .Distinct(StringComparer.OrdinalIgnoreCase).Count();
            long detected = _rawFiles.Sum(r => (long)r.ProteinCount);
            SummaryText.Text = $"{_rawFiles.Count} samples · {_parsedData.TotalProteinGroups:N0} genes · " +
                               $"{conds} condition(s) auto-detected from file paths · {detected:N0} detected values.";
        }

        private async void Import_Click(object sender, RoutedEventArgs e)
        {
            if (_parsedData == null || string.IsNullOrEmpty(_selectedFilePath) || _rawFiles.Count == 0)
            {
                MessageBox.Show("Select an Excel gene matrix first.", "Import",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // Track what this import has durably created, so a mid-way failure can be fully rolled back (see catch).
            string copiedFilePath = null;
            string committedImportName = null;

            try
            {
                ImportButton.IsEnabled = false;
                Mouse.OverrideCursor = Cursors.Wait;

                // Reflect any edited conditions back into the parsed data (keeps the DB overlay consistent on reopen).
                foreach (var rf in _rawFiles)
                    if (!string.IsNullOrWhiteSpace(rf.BiologicalCondition))
                        _parsedData.BiologicalConditionPerFile[rf.RawFileName] = rf.BiologicalCondition.Trim();

                // Determine (or create) the plate.
                int plateId = PlateComboBox.SelectedItem is PlateInfo sel
                    ? sel.PlateId
                    : await _plateService.CreatePlateAsync(new PlateInfo
                    {
                        PlateName = Path.GetFileNameWithoutExtension(_selectedFilePath),
                        Description = "Excel gene-matrix import"
                    });

                // Copy the .xlsx into the project's imports/ folder (unique name).
                string importsDir = Path.Combine(_projectDirectory, "imports");
                Directory.CreateDirectory(importsDir);
                string baseName = Path.GetFileNameWithoutExtension(_selectedFilePath);
                string destName = baseName + ".xlsx";
                string destPath = Path.Combine(importsDir, destName);
                int dedupe = 1;
                while (File.Exists(destPath))
                {
                    destName = $"{baseName}_{dedupe++}.xlsx";
                    destPath = Path.Combine(importsDir, destName);
                }
                File.Copy(_selectedFilePath, destPath);
                copiedFilePath = destPath;

                // Persist: import record → raw files → protein_quant_summary (reusing the parquet tables).
                var importInfo = new ParquetImportInfo
                {
                    PlateId = plateId,
                    FileName = destName,
                    FileHash = string.Empty,
                    ImportTimestamp = DateTime.Now,
                    RowCount = _parsedData.ProteinQuantMatrix.Count,
                    ProteinCount = _parsedData.TotalProteinGroups,
                    CellCount = _parsedData.TotalRawFiles,
                    ColumnMappingJson = "{\"source\":\"xlsx-gene-matrix\"}"
                };
                int importId = await _parquetService.InsertParquetImportAsync(importInfo);
                committedImportName = destName; // the parquet_imports row is now durable — must be undone on later failure

                foreach (var rf in _rawFiles) rf.PlateId = plateId;
                var inserted = await _parquetService.InsertRawFilesAsync(importId, _rawFiles.ToList());
                await _parquetService.StoreProteinQuantMatrixAsync(_parsedData, inserted);

                ImportSuccessful = true;
                DialogResult = true;
            }
            catch (Exception ex)
            {
                // The import commits in separate units (import row → raw files → quant matrix). If a later unit
                // failed, undo the already-committed import row (this cascades raw_files/quant) and delete the
                // copied file, so a failed import leaves the project exactly as it was. Otherwise an orphan .xlsx
                // row would permanently block parquet imports via the mixed-format guard AND be unreachable by the
                // condition-based delete (it has no raw_files to group on).
                try
                {
                    if (committedImportName != null)
                        await _parquetService.DeleteParquetImportAsync(committedImportName);
                    if (copiedFilePath != null && File.Exists(copiedFilePath))
                        File.Delete(copiedFilePath);
                }
                catch { /* best-effort rollback; the original failure is reported below */ }

                MessageBox.Show($"Import failed:\n\n{ex.Message}", "Import",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                ImportButton.IsEnabled = true;
            }
            finally { Mouse.OverrideCursor = null; }
        }

        private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
    }
}
