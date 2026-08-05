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

        // True only while Import_Click is writing to the database. Cancel/Esc/the close box must not
        // fire during that window: they would set DialogResult on a window that is closing under a
        // running import and report failure for an import that actually succeeded.
        private bool _importing;

        // Runs in the selected file that are already registered in raw_files. Importing them again
        // duplicates the run under a second raw_file_id, which nothing downstream can disambiguate.
        private readonly List<string> _conflictingRunNames = new List<string>();

        // Set when a failed import could not be rolled back. Sticky: the dialog stays blocked until
        // it is closed, because retrying on top of a half-written import would double-insert.
        private string _blockedReason;

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
                PlateEmptyHint.Visibility = Plates.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

                // With no plates the combo raises no SelectionChanged, so state the blocking reason
                // here too - otherwise the Import button sits dead with nothing said about it.
                ValidateImportButton();

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

                // Check if already imported in database. Identity is the file's CONTENT, not its
                // display name: the same report copied or renamed slips straight past a name-only
                // guard and lands a second time, which nothing downstream can disambiguate. The
                // name check is kept as a fallback for rows written before hashes were recorded.
                // Off the UI thread: a DIA-NN report is large enough that hashing it inline would
                // freeze the dialog while the user is still just picking a file.
                string fileHash = await System.Threading.Tasks.Task.Run(
                    () => _parquetService.CalculateFileHash(filePath));
                string existingImportName = await _parquetService.GetImportedFileNameByHashAsync(fileHash);
                if (existingImportName == null && await _parquetService.IsParquetImportedAsync(fileName))
                {
                    existingImportName = fileName;
                }

                if (existingImportName != null)
                {
                    string sameName = existingImportName.Equals(fileName, StringComparison.OrdinalIgnoreCase)
                        ? existingImportName
                        : $"{existingImportName}  (imported under a different name; identical contents)";

                    var result = MessageBox.Show(
                        $"This parquet file has already been imported to the database:\n\n{sameName}\n\n" +
                        $"Do you want to delete the existing import and re-import?",
                        "File Already Imported",
                        MessageBoxButton.YesNo,
                        MessageBoxImage.Warning);

                    if (result == MessageBoxResult.Yes)
                    {
                        await _parquetService.DeleteParquetImportAsync(existingImportName);
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
                await RefreshRunNameConflictsAsync();
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
                        PlateId = SelectedPlate?.PlateId, // default; each row can be re-pointed in the grid
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

        // Plate creation moved to the dedicated "Import Plate Metadata" entry on the Import menu;
        // this dialog now only selects among already-registered plates.

        private void PlateComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            SelectedPlate = PlateComboBox.SelectedItem as PlateInfo;

            // This combo is the default for the run grid, not the plate of the whole import: a single
            // DIA-NN report covers every plate in the study. Only fill rows the user has not pointed
            // at a plate themselves, so changing the default never silently undoes per-row work.
            if (SelectedPlate != null && RawFiles != null)
            {
                foreach (var rawFile in RawFiles)
                {
                    if (!rawFile.PlateId.HasValue)
                        rawFile.PlateId = SelectedPlate.PlateId;
                }
            }

            ValidateImportButton();
        }

        /// <summary>
        /// Refreshes the list of runs in the selected file that raw_files already holds. Re-importing
        /// such a run creates a second row with the same name, which the project-load pipeline cannot
        /// resolve to one cell.
        /// </summary>
        private async System.Threading.Tasks.Task RefreshRunNameConflictsAsync()
        {
            _conflictingRunNames.Clear();

            if (RawFiles == null || RawFiles.Count == 0)
                return;

            var existingNames = await _parquetService.GetAllRawFileNamesAsync();

            foreach (var rawFile in RawFiles)
            {
                if (existingNames.Contains(rawFile.RawFileName))
                    _conflictingRunNames.Add(rawFile.RawFileName);
            }
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

        private void ApplyBatchPlate_Click(object sender, RoutedEventArgs e)
        {
            if (BatchPlateComboBox.SelectedItem is not PlateInfo selectedPlate)
            {
                MessageBox.Show(
                    "Please select a plate to assign.",
                    "No Plate Selected",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

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

            var confirm = MessageBox.Show(
                $"Assign plate '{selectedPlate.PlateName}' to the {filteredList.Count} run(s) currently shown?",
                "Preview Batch Assignment",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (confirm != MessageBoxResult.Yes)
                return;

            foreach (var rawFile in filteredList)
            {
                var originalRawFile = RawFiles.FirstOrDefault(rf => rf.RawFileName == rawFile.RawFileName);
                if (originalRawFile != null)
                    originalRawFile.PlateId = selectedPlate.PlateId;
            }

            RawFilesDataGrid.Items.Refresh();
            ValidateImportButton();
        }

        private void RowPlateComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            // The Plate column is IsReadOnly - its ComboBox lives in the cell template so the grid
            // never opens an edit transaction, which means CellEditEnding never fires for it.
            ValidateImportButton();
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
            // Grid virtualisation can raise SelectionChanged from a recycled row while the import is
            // running. Re-enabling the button there would allow a second click mid-write, which is
            // exactly the double-insert this dialog now guards against everywhere else.
            if (_importing)
            {
                ImportButton.IsEnabled = false;
                return;
            }

            bool hasFile = !string.IsNullOrEmpty(_selectedParquetPath);
            bool hasPlate = SelectedPlate != null;
            bool hasRawFiles = RawFiles != null && RawFiles.Count > 0;
            int unassignedConditions = RawFiles == null
                ? 0
                : RawFiles.Count(rf => string.IsNullOrEmpty(rf.BiologicalCondition));
            int unassignedPlates = RawFiles == null
                ? 0
                : RawFiles.Count(rf => !rf.PlateId.HasValue);

            // Say WHY the button is dead. A new project has no plate, so the old silent-disable left
            // the app's own "step 1" with no way forward and no explanation. The reason is derived
            // from the same branches that gate IsEnabled below, so the two cannot drift apart.
            string reason = null;
            if (!string.IsNullOrEmpty(_blockedReason))
                reason = _blockedReason;
            else if (Plates == null || Plates.Count == 0)
                reason = "No plate registered yet - use Import ▸ Import Plate Metadata... to register one first.";
            else if (!hasFile)
                reason = "Choose a DIA-NN parquet file with Browse...";
            else if (!hasRawFiles)
                reason = "No runs were found in this file.";
            else if (!hasPlate)
                reason = "Select the plate these runs belong to.";
            else if (unassignedPlates > 0)
                reason = $"{unassignedPlates} run(s) have no plate assigned.";
            else if (unassignedConditions > 0)
                reason = $"{unassignedConditions} run(s) have no biological condition assigned.";
            else if (_conflictingRunNames.Count > 0)
                reason = $"{_conflictingRunNames.Count} run(s) in this file are already in this project " +
                         $"(e.g. {_conflictingRunNames[0]}). Delete that import first.";

            ImportButton.IsEnabled = reason == null;
            ImportBlockedText.Text = reason ?? string.Empty;
            ImportBlockedText.Visibility = reason == null ? Visibility.Collapsed : Visibility.Visible;
        }


        private async void Import_Click(object sender, RoutedEventArgs e)
        {
            // Hoisted above the try: several branches leave the method early from inside it, and the
            // finally has to be able to put the title and cursor back on every one of those paths.
            // Previously an early return left the window reading "Importing... Please wait" forever.
            string originalTitle = this.Title;

            // What this import has already committed, so a failure part-way through can be undone
            // (mirrors ImportGeneMatrixDialog). Without it a throw in step 3 leaves an import row and
            // a full set of raw_files behind with an empty protein_quant_summary: the Explorer still
            // draws, because it re-reads the parquet from disk, while marker classification silently
            // finds no genes for those cells and reports a truncated population as if it were whole.
            int? committedImportId = null;
            bool rolledBack = false;

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

                // Every raw_files row needs a plate: the column is declared NOT NULL, and the plate is
                // the batch variable ComBat corrects on, so a run with no plate is not something to
                // guess at.
                var unplatedFiles = RawFiles.Where(rf => !rf.PlateId.HasValue).ToList();
                if (unplatedFiles.Count > 0)
                {
                    MessageBox.Show(
                        $"{unplatedFiles.Count} raw file(s) have no plate assigned, starting with " +
                        $"'{unplatedFiles[0].RawFileName}'.\n\n" +
                        "Set the Plate column for every run (the Batch Plate control assigns all " +
                        "currently filtered runs at once) before importing.",
                        "Missing Plate Assignment",
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
                _importing = true;
                ImportButton.IsEnabled = false;
                PlateComboBox.IsEnabled = false;
                RawFilesDataGrid.IsEnabled = false;
                this.Cursor = System.Windows.Input.Cursors.Wait;

                // Show progress in status (we don't have LoadingOverlay in dialog, so we'll update window title)
                this.Title = "Importing... Please wait";

                // The file keeps the name it was copied into imports/ under. It used to be renamed to
                // {PlateName}.parquet, which asserted one parquet belongs to exactly one plate - untrue
                // for a single DIA-NN report covering several plates, and it also moved the file out
                // from under the duplicate check. The plate now lives per run, in raw_files.plate_id.
                string fileName = Path.GetFileName(_selectedParquetPath);
                string fileHash = await System.Threading.Tasks.Task.Run(
                    () => _parquetService.CalculateFileHash(_selectedParquetPath));

                // Re-check identity against the database immediately before writing. The checks in
                // ValidateAndLoadParquetFile ran when the file was picked, and nothing in the schema
                // stops a second insert of the same content or the same run names.
                this.Title = "Importing... Checking for an existing import";

                string alreadyImportedAs = await _parquetService.GetImportedFileNameByHashAsync(fileHash);
                if (alreadyImportedAs != null)
                {
                    MessageBox.Show(
                        $"This file's contents are already in the project, imported as " +
                        $"'{alreadyImportedAs}'.\n\n" +
                        "Delete that import first if you want to import this file again.",
                        "File Already Imported",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);

                    return;
                }

                await RefreshRunNameConflictsAsync();
                if (_conflictingRunNames.Count > 0)
                {
                    MessageBox.Show(
                        $"{_conflictingRunNames.Count} run(s) in this file are already registered in " +
                        $"this project, starting with '{_conflictingRunNames[0]}'.\n\n" +
                        "Importing them again would store the same run twice, and per-run counts and " +
                        "cell-type classifications could no longer be attributed to one cell. Delete " +
                        "the earlier import first.",
                        "Duplicate Run Names",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);

                    return;
                }

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
                committedImportId = importId; // durable from here on - must be undone if a later step fails


                // STEP 2: Insert raw file records
                this.Title = "Importing... Saving raw file information";

                // plate_id is whatever the grid holds per run - see the Plate column. Stamping every
                // row with the dialog's plate would collapse a multi-plate study into one batch.
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



                ImportSuccessful = true;
                _importing = false; // the write is done; closing is allowed again
                DialogResult = true;
            }
            catch (Exception ex)
            {
                // The import commits in three separate units (import row, raw files, quant matrix).
                // Undo the import row if it is already durable - that cascade takes the raw files and
                // any quant rows with it - so a failed import leaves the project exactly as it was.
                try
                {
                    if (committedImportId.HasValue)
                    {
                        await _parquetService.DeleteParquetImportByIdAsync(committedImportId.Value);
                        rolledBack = true;
                    }
                }
                catch { /* best-effort rollback; the original failure is reported below */ }

                string outcome;
                if (!committedImportId.HasValue)
                {
                    outcome = "Nothing was written to the project.";
                }
                else if (rolledBack)
                {
                    outcome = "The partially written import was rolled back - the project is unchanged.";
                }
                else
                {
                    // Importing again on top of a half-written import would insert the same runs a
                    // second time, so keep the dialog blocked rather than re-arming it.
                    _selectedParquetPath = null;
                    _blockedReason = "A failed import could not be undone automatically. Close this " +
                                     "dialog and remove the partial import before importing again.";
                    outcome = "The partially written import could NOT be removed automatically. Close " +
                              "this dialog and delete that import before trying again.";
                }

                MessageBox.Show(
                    $"Error during import:\n\n{ex.Message}\n\n{outcome}",
                    "Import Error",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
            finally
            {
                _importing = false;

                // Restore UI. Title and cursor are restored here rather than on the success path only,
                // so no exit route can leave the dialog stuck showing "Importing... Please wait".
                this.Title = originalTitle;
                this.Cursor = System.Windows.Input.Cursors.Arrow;
                PlateComboBox.IsEnabled = true;
                RawFilesDataGrid.IsEnabled = true;
                ValidateImportButton();
            }
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            // IsCancel="True" means Esc lands here too. Closing mid-write would set DialogResult on a
            // window disappearing under a running import and report failure for an import that in
            // fact succeeded, so the button is inert until the write finishes.
            if (_importing)
                return;

            DialogResult = false;
        }

        protected override void OnClosing(CancelEventArgs e)
        {
            // Same reason as Cancel_Click, for the title-bar close box and Alt+F4.
            if (_importing)
            {
                e.Cancel = true;
                return;
            }

            base.OnClosing(e);
        }

        protected void OnPropertyChanged(string propertyName)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}