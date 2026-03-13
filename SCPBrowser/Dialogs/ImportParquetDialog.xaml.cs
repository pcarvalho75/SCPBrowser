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
using SCPBrowser.Services;

namespace SCPBrowser
{
    public partial class ImportParquetDialog : Window, INotifyPropertyChanged
    {
        private readonly ParquetDataService _parquetService;
        private readonly PlateService _plateService;
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

        public ImportParquetDialog(ParquetDataService parquetService, PlateService plateService, string projectDirectory)
        {
            InitializeComponent();
            DataContext = this;

            _parquetService = parquetService;
            _plateService = plateService;
            _projectDirectory = projectDirectory;

            RawFiles = new ObservableCollection<RawFileInfo>();
            FilteredRawFiles = new ObservableCollection<RawFileInfo>();
            Plates = new ObservableCollection<PlateInfo>();
            ConditionNames = new ObservableCollection<string>();

            // Add some debugging



            // Load plates and existing conditions
            LoadPlatesAndConditionsAsync();
        }

        private async void LoadPlatesAndConditionsAsync()
        {
            try
            {
                // Load plates
                var plates = await _plateService.GetPlatesAsync().ConfigureAwait(true);
                Plates.Clear();
                foreach (var plate in plates)
                {
                    Plates.Add(plate);
                }

                if (Plates.Count > 0)
                {
                    PlateComboBox.SelectedIndex = 0;
                }

                // Load existing biological conditions from database

                var existingConditions = await _parquetService.GetBiologicalConditionsAsync();

                ConditionNames.Clear();
                foreach (var condition in existingConditions)
                {
                    ConditionNames.Add(condition);

                }


            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    $"Error loading data:\n\n{ex.Message}",
                    "Error",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }

        private async void LoadPlatesAsync()
        {
            try
            {
                var plates = await _plateService.GetPlatesAsync().ConfigureAwait(true);
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
                    // Check if a file with the same name already exists in imports folder
                    if (File.Exists(expectedPath))
                    {
                        var overwriteResult = MessageBox.Show(
                            $"A file with the same name already exists in the imports folder:\n\n{fileName}\n\n" +
                            $"The selected file:\n{filePath}\n\n" +
                            $"Would you like to use the existing file in the imports folder instead?",
                            "File Already Exists",
                            MessageBoxButton.YesNoCancel,
                            MessageBoxImage.Question);

                        if (overwriteResult == MessageBoxResult.Yes)
                        {
                            // Use the existing file in imports folder
                            filePath = expectedPath;
                        }
                        else if (overwriteResult == MessageBoxResult.No)
                        {
                            // Offer to replace the existing file
                            var replaceResult = MessageBox.Show(
                                $"Do you want to replace the existing file with the selected one?\n\n" +
                                $"This will overwrite:\n{expectedPath}",
                                "Replace Existing File",
                                MessageBoxButton.YesNo,
                                MessageBoxImage.Warning);

                            if (replaceResult == MessageBoxResult.Yes)
                            {
                                File.Delete(expectedPath);
                                File.Copy(filePath, expectedPath, overwrite: false);
                                filePath = expectedPath;
                            }
                            else
                            {
                                return;
                            }
                        }
                        else
                        {
                            // User cancelled
                            return;
                        }
                    }
                    else
                    {
                        // File doesn't exist in imports, offer to copy
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
                }

                // Check if already imported in database
                bool alreadyImported = await _parquetService.IsParquetImportedAsync(fileName);
                if (alreadyImported)
                {
                    var result = MessageBox.Show(
                        $"This parquet file has already been imported to the database:\n\n{fileName}\n\n" +
                        $"Do you want to delete the existing import and re-import?",
                        "File Already Imported",
                        MessageBoxButton.YesNo,
                        MessageBoxImage.Warning);

                    if (result == MessageBoxResult.Yes)
                    {
                        await _parquetService.DeleteParquetImportAsync(fileName);
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
            catch (IOException ioEx) when (ioEx.Message.Contains("already exists"))
            {
                MessageBox.Show(
                    $"Error: A file with the same name already exists in the imports folder.\n\n{ioEx.Message}",
                    "File Already Exists",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
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

        private void NewCondition_Click(object sender, RoutedEventArgs e)
        {


            var dialog = new NewConditionDialog
            {
                Owner = this
            };

            if (dialog.ShowDialog() == true)
            {
                string newConditionName = dialog.ConditionName;


                // Check if condition already exists (case-insensitive)
                if (ConditionNames.Any(c => c.Equals(newConditionName, StringComparison.OrdinalIgnoreCase)))
                {

                    MessageBox.Show(
                        $"A condition named '{newConditionName}' already exists.",
                        "Duplicate Condition",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                    return;
                }

                // Add the new condition to the list

                ConditionNames.Add(newConditionName);


                // Print all conditions

                foreach (var cond in ConditionNames)
                {

                }

                // Select the newly added condition in the ComboBox
                BatchConditionComboBox.SelectedItem = newConditionName;





                MessageBox.Show(
                    $"Condition '{newConditionName}' has been added successfully.\n\n" +
                    $"You can now assign it to raw files.",
                    "Condition Added",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
            else
            {

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
                int plateId = await _plateService.CreatePlateAsync(plateInfo).ConfigureAwait(true);
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

                int assignmentCount = 0;

                foreach (var rawFile in filteredList)
                {
                    var originalRawFile = RawFiles.FirstOrDefault(rf => rf.RawFileName == rawFile.RawFileName);
                    if (originalRawFile != null)
                    {

                        originalRawFile.BiologicalCondition = selectedCondition;
                        assignmentCount++;
                    }
                }



                RawFilesDataGrid.Items.Refresh();

                // **NEW: Validate Import button after assignment**
                ValidateImportButton();

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
            else
            {

            }


        }

        private void RawFilesDataGrid_CellEditEnding(object sender, DataGridCellEditEndingEventArgs e)
        {
            // Schedule validation for after the edit completes
            Dispatcher.BeginInvoke(new Action(() =>
            {
                ValidateImportButton();
            }), System.Windows.Threading.DispatcherPriority.Background);
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
            bool allConditionsAssigned = RawFiles != null &&
                                         RawFiles.All(rf => !string.IsNullOrEmpty(rf.BiologicalCondition));

            ImportButton.IsEnabled = hasFile && hasPlate && hasRawFiles && allConditionsAssigned;


        }


        private async void Import_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                // Validate all raw files have conditions assigned - BLOCK if not all assigned
                var unassignedFiles = RawFiles.Where(rf => string.IsNullOrEmpty(rf.BiologicalCondition)).ToList();
                if (unassignedFiles.Count > 0)
                {
                    // Build list of unassigned file names
                    var sb = new System.Text.StringBuilder();
                    sb.AppendLine($"{unassignedFiles.Count} raw file(s) do not have biological conditions assigned:");
                    sb.AppendLine();

                    int displayCount = Math.Min(unassignedFiles.Count, 10);
                    for (int i = 0; i < displayCount; i++)
                    {
                        sb.AppendLine($"• {unassignedFiles[i].RawFileName}");
                    }

                    if (unassignedFiles.Count > 10)
                    {
                        sb.AppendLine($"... and {unassignedFiles.Count - 10} more");
                    }

                    sb.AppendLine();
                    sb.AppendLine("Please assign biological conditions to all raw files before importing.");

                    MessageBox.Show(
                        sb.ToString(),
                        "Missing Biological Conditions",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);

                    return; // Block the import
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

                // Disable UI during import
                ImportButton.IsEnabled = false;
                PlateComboBox.IsEnabled = false;
                RawFilesDataGrid.IsEnabled = false;
                this.Cursor = System.Windows.Input.Cursors.Wait;

                // Show progress in status (we don't have LoadingOverlay in dialog, so we'll update window title)
                string originalTitle = this.Title;
                this.Title = "Importing... Please wait";

                // Generate new filename based on plate name
                string originalFileName = Path.GetFileName(_selectedParquetPath);
                string newFileName = $"{SelectedPlate.PlateName}.parquet";
                string importsPath = Path.Combine(_projectDirectory, "imports");
                string newFilePath = Path.Combine(importsPath, newFileName);

                // Rename the file if the new name is different
                if (!originalFileName.Equals(newFileName, StringComparison.OrdinalIgnoreCase))
                {
                    // Check if target filename already exists
                    if (File.Exists(newFilePath))
                    {
                        MessageBox.Show(
                            $"A file named '{newFileName}' already exists in the imports folder.\n\n" +
                            $"This plate may have already been imported.",
                            "File Already Exists",
                            MessageBoxButton.OK,
                            MessageBoxImage.Warning);
                        return;
                    }

                    // Rename the file
                    File.Move(_selectedParquetPath, newFilePath);
                    _selectedParquetPath = newFilePath;

                }

                // Calculate file hash from the (possibly renamed) file
                string fileHash = _parquetService.CalculateFileHash(_selectedParquetPath);
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

                // STEP 1: Insert parquet import record
                this.Title = "Importing... Creating import record";


                var importInfo = new ParquetImportInfo
                {
                    PlateId = SelectedPlate.PlateId,
                    FileName = fileName,
                    FileHash = fileHash,
                    ImportTimestamp = DateTime.Now,
                    RowCount = 0, // Will be updated after reading parquet
                    ProteinCount = RawFiles.Sum(rf => rf.ProteinCount),
                    CellCount = RawFiles.Count,
                    ColumnMappingJson = mappingJson
                };

                int importId = await _parquetService.InsertParquetImportAsync(importInfo);


                // STEP 2: Insert raw file records
                this.Title = "Importing... Saving raw file information";


                // Assign plate_id to each raw file
                foreach (var rawFile in RawFiles)
                {
                    rawFile.PlateId = SelectedPlate.PlateId;
                }

                var insertedRawFiles = await _parquetService.InsertRawFilesAsync(importId, RawFiles.ToList());


                // STEP 3: Extract and store protein quantification data
                this.Title = "Importing... Processing protein quantification";


                var progress = new Progress<string>(message =>
                {
                    this.Dispatcher.Invoke(() =>
                    {
                        this.Title = $"Importing... {message}";

                    });
                });

                await _parquetService.ExtractAndStoreProteinQuantAsync(
                    _selectedParquetPath,
                    importId,
                    insertedRawFiles,
                    progress);



                // Restore UI
                this.Title = originalTitle;
                this.Cursor = System.Windows.Input.Cursors.Arrow;

                ImportSuccessful = true;
                DialogResult = true;
            }
            catch (Exception ex)
            {
                // Restore cursor
                this.Cursor = System.Windows.Input.Cursors.Arrow;




                MessageBox.Show(
                    $"Error during import:\n\n{ex.Message}",
                    "Import Error",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
            finally
            {
                // Re-enable UI
                ImportButton.IsEnabled = true;
                PlateComboBox.IsEnabled = true;
                RawFilesDataGrid.IsEnabled = true;
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