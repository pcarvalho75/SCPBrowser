using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using SCPBrowser.Models;

namespace SCPBrowser
{
    public partial class ImportParquetDialog : Window, INotifyPropertyChanged
    {
        private readonly ProjectDataService _projectService;
        private readonly string _projectDirectory;
        private string _selectedParquetPath;
        private string _currentFilter = string.Empty;

        private ObservableCollection<RawFileInfo> _rawFiles;
        private ObservableCollection<RawFileInfo> _filteredRawFiles;
        private ObservableCollection<PlateInfo> _plates;
        private ObservableCollection<string> _conditionNames;

        public ObservableCollection<RawFileInfo> RawFiles
        {
            get => _rawFiles;
            set
            {
                _rawFiles = value;
                OnPropertyChanged(nameof(RawFiles));
            }
        }

        public ObservableCollection<RawFileInfo> FilteredRawFiles
        {
            get => _filteredRawFiles;
            set
            {
                _filteredRawFiles = value;
                OnPropertyChanged(nameof(FilteredRawFiles));
            }
        }

        public ObservableCollection<PlateInfo> Plates
        {
            get => _plates;
            set
            {
                _plates = value;
                OnPropertyChanged(nameof(Plates));
            }
        }

        public ObservableCollection<string> ConditionNames
        {
            get => _conditionNames;
            set
            {
                _conditionNames = value;
                OnPropertyChanged(nameof(ConditionNames));
            }
        }

        public PlateInfo SelectedPlate { get; private set; }
        public bool ImportSuccessful { get; private set; }

        public event PropertyChangedEventHandler PropertyChanged;

        public ImportParquetDialog(ProjectDataService projectService, string projectDirectory)
        {
            InitializeComponent();
            DataContext = this;

            _projectService = projectService;
            _projectDirectory = projectDirectory;

            RawFiles = new ObservableCollection<RawFileInfo>();
            FilteredRawFiles = new ObservableCollection<RawFileInfo>();
            Plates = new ObservableCollection<PlateInfo>();
            ConditionNames = new ObservableCollection<string>();

            LoadPlatesAsync();
        }

        private async void LoadPlatesAsync()
        {
            try
            {
                var plates = await _projectService.GetPlatesAsync();
                Plates.Clear();
                foreach (var plate in plates)
                {
                    Plates.Add(plate);
                }

                if (Plates.Count > 0)
                {
                    PlateComboBox.SelectedIndex = 0;
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    $"Error loading plates:\n\n{ex.Message}",
                    "Error",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }

        private void BrowseFile_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFileDialog
            {
                Filter = "Parquet Files (*.parquet)|*.parquet|All Files (*.*)|*.*",
                Title = "Select DIA-NN Parquet File"
            };

            if (dialog.ShowDialog() == true)
            {
                ValidateAndLoadParquetFile(dialog.FileName);
            }
        }

        private async void ValidateAndLoadParquetFile(string filePath)
        {
            try
            {
                string fileName = Path.GetFileName(filePath);
                string importsPath = Path.Combine(_projectDirectory, "imports");
                string expectedPath = Path.Combine(importsPath, fileName);

                // Check if file is in imports folder
                if (!filePath.Equals(expectedPath, StringComparison.OrdinalIgnoreCase))
                {
                    var result = MessageBox.Show(
                        $"The selected file is not in the project's imports folder.\n\n" +
                        $"Expected location:\n{importsPath}\n\n" +
                        $"Would you like to copy the file to the imports folder?",
                        "File Not in Imports Folder",
                        MessageBoxButton.YesNo,
                        MessageBoxImage.Question);

                    if (result == MessageBoxResult.Yes)
                    {
                        // Ensure imports directory exists
                        if (!Directory.Exists(importsPath))
                        {
                            Directory.CreateDirectory(importsPath);
                        }

                        // Copy file
                        File.Copy(filePath, expectedPath, overwrite: false);
                        filePath = expectedPath;
                    }
                    else
                    {
                        FileValidationText.Text = "⚠️ File must be in the imports folder to continue.";
                        FileValidationText.Visibility = Visibility.Visible;
                        return;
                    }
                }

                // Check if already imported
                bool alreadyImported = await _projectService.IsParquetImportedAsync(fileName);
                if (alreadyImported)
                {
                    var result = MessageBox.Show(
                        $"This parquet file has already been imported:\n\n{fileName}\n\n" +
                        $"Do you want to delete the existing import and re-import?",
                        "File Already Imported",
                        MessageBoxButton.YesNo,
                        MessageBoxImage.Warning);

                    if (result == MessageBoxResult.Yes)
                    {
                        await _projectService.DeleteParquetImportAsync(fileName);
                    }
                    else
                    {
                        return;
                    }
                }

                _selectedParquetPath = filePath;
                FilePathTextBox.Text = filePath;
                FileValidationText.Visibility = Visibility.Collapsed;

                // Load parquet preview
                await LoadParquetPreviewAsync(filePath);
                ValidateImportButton();
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    $"Error validating file:\n\n{ex.Message}",
                    "Validation Error",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }

        private async System.Threading.Tasks.Task LoadParquetPreviewAsync(string filePath)
        {
            try
            {
                var parquetService = new ParquetDataService();

                // Get column names to determine mapping
                var columns = await parquetService.GetColumnNamesAsync(filePath);

                var mapping = new ColumnMapping
                {
                    RawFileColumn = "Run",
                    ProteinGroupColumn = "Protein.Group",
                    PeptideColumn = "Stripped.Sequence",
                    TotalIonCurrentColumn = "Precursor.Quantity"
                };

                // Load data to extract raw file names
                var data = await parquetService.LoadParquetFileAsync(filePath, mapping);

                // Clear and populate raw files
                RawFiles.Clear();
                foreach (var rawFileName in data.RawFileNames.OrderBy(x => x))
                {
                    RawFiles.Add(new RawFileInfo
                    {
                        RawFileName = rawFileName,
                        BiologicalCondition = "", // User will assign
                        ProteinCount = data.ProteinCountPerFile.ContainsKey(rawFileName)
                            ? data.ProteinCountPerFile[rawFileName]
                            : 0,
                        PeptideCount = data.PeptideCountPerFile.ContainsKey(rawFileName)
                            ? data.PeptideCountPerFile[rawFileName]
                            : 0,
                        TotalIonCurrent = data.TotalIonCurrentPerFile.ContainsKey(rawFileName)
                            ? data.TotalIonCurrentPerFile[rawFileName]
                            : 0.0
                    });
                }

                ApplyFilter();
                ValidateImportButton();
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    $"Error loading parquet file preview:\n\n{ex.Message}",
                    "Preview Error",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }

        private void NewPlate_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new NewPlateDialog
            {
                Owner = this
            };

            if (dialog.ShowDialog() == true)
            {
                CreatePlateAsync(dialog.PlateInfo);
            }
        }

        private async void CreatePlateAsync(PlateInfo plateInfo)
        {
            try
            {
                int plateId = await _projectService.CreatePlateAsync(plateInfo);
                plateInfo.PlateId = plateId;

                Plates.Add(plateInfo);
                PlateComboBox.SelectedItem = plateInfo;

                MessageBox.Show(
                    $"Plate '{plateInfo.PlateName}' created successfully!",
                    "Plate Created",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    $"Error creating plate:\n\n{ex.Message}",
                    "Error",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }

        private void PlateComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            SelectedPlate = PlateComboBox.SelectedItem as PlateInfo;
            ValidateImportButton();
        }

        private void FilterTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            _currentFilter = FilterTextBox.Text?.Trim() ?? string.Empty;

            if (string.IsNullOrEmpty(_currentFilter))
            {
                FilterClearButton.Visibility = Visibility.Collapsed;
            }
            else
            {
                FilterClearButton.Visibility = Visibility.Visible;
            }

            ApplyFilter();
        }

        private void FilterClearButton_Click(object sender, RoutedEventArgs e)
        {
            FilterTextBox.Clear();
        }

        private void ApplyFilter()
        {
            if (RawFiles == null)
            {
                FilteredRawFiles = new ObservableCollection<RawFileInfo>();
                UpdateFilterStatus(0, 0);
                UpdateNoResultsVisibility(false, false);
                return;
            }

            List<RawFileInfo> filtered;

            if (string.IsNullOrEmpty(_currentFilter))
            {
                filtered = RawFiles.ToList();
            }
            else
            {
                filtered = RawFiles.Where(rf => MatchesFilter(rf, _currentFilter)).ToList();
            }

            FilteredRawFiles = new ObservableCollection<RawFileInfo>(filtered);
            RawFilesDataGrid.ItemsSource = FilteredRawFiles;

            UpdateFilterStatus(filtered.Count, RawFiles.Count);

            bool hasFilter = !string.IsNullOrEmpty(_currentFilter);
            bool hasNoResults = filtered.Count == 0;
            UpdateNoResultsVisibility(hasFilter, hasNoResults);
        }

        private bool MatchesFilter(RawFileInfo rawFile, string filter)
        {
            if (string.IsNullOrEmpty(filter))
                return true;

            if (filter.Contains('*'))
            {
                return MatchesWildcard(rawFile.RawFileName, filter) ||
                       (!string.IsNullOrEmpty(rawFile.BiologicalCondition) &&
                        MatchesWildcard(rawFile.BiologicalCondition, filter));
            }
            else
            {
                return rawFile.RawFileName.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                       (!string.IsNullOrEmpty(rawFile.BiologicalCondition) &&
                        rawFile.BiologicalCondition.Contains(filter, StringComparison.OrdinalIgnoreCase));
            }
        }

        private bool MatchesWildcard(string text, string pattern)
        {
            if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(pattern))
                return false;

            string regexPattern = "^" + Regex.Escape(pattern).Replace("\\*", ".*") + "$";
            return Regex.IsMatch(text, regexPattern, RegexOptions.IgnoreCase);
        }

        private void UpdateFilterStatus(int filteredCount, int totalCount)
        {
            if (filteredCount == totalCount)
            {
                FilterStatusText.Text = $"All {totalCount} raw files shown";
                FilterStatusText.Foreground = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(100, 116, 139));
                RawFilesDataGrid.Tag = null;
            }
            else
            {
                FilterStatusText.Text = $"{filteredCount} of {totalCount} raw files shown";
                FilterStatusText.Foreground = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(37, 99, 235));
                RawFilesDataGrid.Tag = "Filtered";
            }
        }

        private void UpdateNoResultsVisibility(bool hasFilter, bool hasNoResults)
        {
            if (hasFilter && hasNoResults)
            {
                NoResultsBorder.Visibility = Visibility.Visible;
                RawFilesDataGrid.Visibility = Visibility.Collapsed;
                NoResultsHelpText.Text = $"No runs contain '{_currentFilter}'. Try a different search term or clear the filter.";
            }
            else
            {
                NoResultsBorder.Visibility = Visibility.Collapsed;
                RawFilesDataGrid.Visibility = Visibility.Visible;
            }
        }

        private void ApplyBatchAssignment_Click(object sender, RoutedEventArgs e)
        {
            if (BatchConditionComboBox.SelectedItem == null)
            {
                MessageBox.Show(
                    "Please select a condition to assign.",
                    "No Condition Selected",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            string selectedCondition = BatchConditionComboBox.SelectedItem.ToString();

            var filteredList = FilteredRawFiles.ToList();

            if (filteredList.Count == 0)
            {
                MessageBox.Show(
                    "No runs match the current filter.",
                    "No Runs to Assign",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }

            // Show preview
            if (ShowBatchAssignmentPreview(filteredList, selectedCondition))
            {
                foreach (var rawFile in filteredList)
                {
                    var originalRawFile = RawFiles.FirstOrDefault(rf => rf.RawFileName == rawFile.RawFileName);
                    if (originalRawFile != null)
                    {
                        originalRawFile.BiologicalCondition = selectedCondition;
                    }
                }

                RawFilesDataGrid.Items.Refresh();

                // Optional: Clear filter after assignment
                if (!string.IsNullOrEmpty(_currentFilter))
                {
                    var result = MessageBox.Show(
                        "Clear filter to see all runs?",
                        "Clear Filter",
                        MessageBoxButton.YesNo,
                        MessageBoxImage.Question);

                    if (result == MessageBoxResult.Yes)
                    {
                        FilterTextBox.Clear();
                    }
                }
            }
        }

        private bool ShowBatchAssignmentPreview(List<RawFileInfo> runs, string condition)
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"About to assign condition '{condition}' to {runs.Count} run(s):");
            sb.AppendLine();

            int displayCount = Math.Min(runs.Count, 10);
            for (int i = 0; i < displayCount; i++)
            {
                var run = runs[i];
                string currentCondition = string.IsNullOrEmpty(run.BiologicalCondition)
                    ? "(unassigned)"
                    : run.BiologicalCondition;
                sb.AppendLine($"• {run.RawFileName}");
                sb.AppendLine($"  Current: {currentCondition} → New: {condition}");

                if (i < displayCount - 1)
                    sb.AppendLine();
            }

            if (runs.Count > 10)
            {
                sb.AppendLine();
                sb.AppendLine($"... and {runs.Count - 10} more run(s)");
            }

            sb.AppendLine();
            sb.AppendLine("Continue with batch assignment?");

            var result = MessageBox.Show(
                sb.ToString(),
                "Preview Batch Assignment",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            return result == MessageBoxResult.Yes;
        }

        private void ValidateImportButton()
        {
            bool hasFile = !string.IsNullOrEmpty(_selectedParquetPath);
            bool hasPlate = SelectedPlate != null;
            bool hasRawFiles = RawFiles != null && RawFiles.Count > 0;

            ImportButton.IsEnabled = hasFile && hasPlate && hasRawFiles;
        }

        private async void Import_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                // Validate all raw files have conditions assigned
                var unassignedFiles = RawFiles.Where(rf => string.IsNullOrEmpty(rf.BiologicalCondition)).ToList();
                if (unassignedFiles.Count > 0)
                {
                    var result = MessageBox.Show(
                        $"{unassignedFiles.Count} raw file(s) do not have biological conditions assigned.\n\n" +
                        $"Do you want to continue anyway? Unassigned files will be imported without conditions.",
                        "Unassigned Conditions",
                        MessageBoxButton.YesNo,
                        MessageBoxImage.Warning);

                    if (result == MessageBoxResult.No)
                        return;
                }

                // Get unique conditions
                var uniqueConditions = RawFiles
                    .Where(rf => !string.IsNullOrEmpty(rf.BiologicalCondition))
                    .Select(rf => rf.BiologicalCondition)
                    .Distinct()
                    .ToList();

                // Update condition names for future use
                foreach (var condition in uniqueConditions)
                {
                    if (!ConditionNames.Contains(condition))
                    {
                        ConditionNames.Add(condition);
                    }
                }

                // Calculate file hash
                string fileHash = _projectService.CalculateFileHash(_selectedParquetPath);
                string fileName = Path.GetFileName(_selectedParquetPath);

                // Create column mapping JSON
                var mapping = new ColumnMapping
                {
                    RawFileColumn = "Run",
                    ProteinGroupColumn = "Protein.Group",
                    PeptideColumn = "Stripped.Sequence",
                    TotalIonCurrentColumn = "Precursor.Quantity"
                };
                string mappingJson = System.Text.Json.JsonSerializer.Serialize(mapping);

                // Create import record
                var importInfo = new ParquetImportInfo
                {
                    PlateId = SelectedPlate.PlateId,
                    FileName = fileName,
                    FileHash = fileHash,
                    ImportTimestamp = DateTime.Now,
                    RowCount = 0, // Will be filled by actual import process
                    ProteinCount = RawFiles.Sum(rf => rf.ProteinCount),
                    CellCount = RawFiles.Count,
                    ColumnMappingJson = mappingJson
                };

                // TODO: In next phase, we'll implement the actual import logic
                // For now, we'll just show success
                MessageBox.Show(
                    $"Import prepared successfully!\n\n" +
                    $"Plate: {SelectedPlate.PlateName}\n" +
                    $"Raw Files: {RawFiles.Count}\n" +
                    $"Conditions: {uniqueConditions.Count}\n\n" +
                    $"NOTE: Full import implementation coming in next phase.",
                    "Import Prepared",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);

                ImportSuccessful = true;
                DialogResult = true;
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    $"Error during import:\n\n{ex.Message}",
                    "Import Error",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
        }

        protected void OnPropertyChanged(string propertyName)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}