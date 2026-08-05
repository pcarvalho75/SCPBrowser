using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Data.Sqlite;
using Microsoft.Win32;
using SCPBrowser.Services;

namespace SCPBrowser
{
    public partial class NewProjectDialog : Window
    {
        public string ProjectName { get; private set; }
        public string ProjectLocation { get; private set; }
        public string ProjectDescription { get; private set; }

        public NewProjectDialog()
        {
            InitializeComponent();
        }

        private void BrowseLocation_Click(object sender, RoutedEventArgs e)
        {
            // Use SaveFileDialog as a workaround to select/create folder
            // We'll strip the filename and just use the directory
            var dialog = new SaveFileDialog
            {
                Title = "Select Project Folder (filename will be ignored)",
                FileName = "Select Folder", // Default filename
                Filter = "Folder Selection|*.folder", // Dummy filter
                CheckFileExists = false,
                CheckPathExists = true
            };

            if (dialog.ShowDialog() == true)
            {
                // Get just the directory part, ignore the filename
                string selectedPath = Path.GetDirectoryName(dialog.FileName);
                ProjectLocationTextBox.Text = selectedPath;
                ValidateInputs();
            }
        }

        private void ProjectName_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        {
            ValidateInputs();
        }

        private void Description_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        {
            ValidateInputs();
        }

        private void ValidateInputs()
        {
            bool isValid = !string.IsNullOrWhiteSpace(ProjectNameTextBox.Text) &&
                          !string.IsNullOrWhiteSpace(ProjectLocationTextBox.Text) &&
                          !string.IsNullOrWhiteSpace(DescriptionTextBox.Text);

            CreateButton.IsEnabled = isValid;
        }

        private async void Create_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                ProjectName = ProjectNameTextBox.Text.Trim();
                ProjectLocation = ProjectLocationTextBox.Text.Trim();
                ProjectDescription = DescriptionTextBox.Text.Trim();

                // Validate project name doesn't contain invalid characters
                char[] invalidChars = Path.GetInvalidFileNameChars();
                if (ProjectName.IndexOfAny(invalidChars) >= 0)
                {
                    MessageBox.Show(
                        "Project name contains invalid characters. Please use only letters, numbers, and basic punctuation.",
                        "Invalid Project Name",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                    return;
                }

                // Create project directory if it doesn't exist
                try
                {
                    if (!Directory.Exists(ProjectLocation))
                    {
                        Directory.CreateDirectory(ProjectLocation);
                    }
                }
                catch (UnauthorizedAccessException)
                {
                    MessageBox.Show(
                        "Access denied. You don't have permission to create a directory at this location.",
                        "Permission Error",
                        MessageBoxButton.OK,
                        MessageBoxImage.Error);
                    return;
                }
                catch (IOException ex)
                {
                    MessageBox.Show(
                        $"Error creating project directory:\n\n{ex.Message}",
                        "Directory Error",
                        MessageBoxButton.OK,
                        MessageBoxImage.Error);
                    return;
                }

                // Create imports subdirectory
                string importsPath = Path.Combine(ProjectLocation, "imports");
                try
                {
                    if (!Directory.Exists(importsPath))
                    {
                        Directory.CreateDirectory(importsPath);
                    }
                }
                catch (Exception ex)
                {
                    MessageBox.Show(
                        $"Error creating imports directory:\n\n{ex.Message}",
                        "Directory Error",
                        MessageBoxButton.OK,
                        MessageBoxImage.Error);
                    return;
                }

                // Check if project.db already exists
                string projectDbPath = Path.Combine(ProjectLocation, "project.db");
                if (File.Exists(projectDbPath))
                {
                    if (!await ConfirmAndBackupOverwriteAsync(projectDbPath))
                    {
                        return;
                    }

                    try
                    {
                        // Pooled SQLite connections keep the file handle open after Dispose, which would make the
                        // delete fail with a sharing violation the user cannot act on.
                        SqliteConnection.ClearAllPools();
                        File.Delete(projectDbPath);
                    }
                    catch (IOException ex)
                    {
                        MessageBox.Show(
                            $"Error deleting existing project database:\n\n{ex.Message}\n\nThe file may be in use by another application.\n\nThe backup taken a moment ago is still on disk.",
                            "File Error",
                            MessageBoxButton.OK,
                            MessageBoxImage.Error);
                        return;
                    }
                }

                // Create the project database
                var projectService = new ProjectDatabaseService(projectDbPath);
                await projectService.CreateProjectAsync(ProjectName, ProjectDescription);

                // Publish the project a crash report should name and log next to (see App.CurrentProjectPath).
                App.CurrentProjectPath = projectDbPath;

                DialogResult = true;
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    $"Error creating project:\n\n{ex.Message}",
                    "Project Creation Error",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }

        /// <summary>
        /// Guards the overwrite of an existing project. Deleting project.db throws away every imported run, plate,
        /// isolated cell and image, reconcile link, classification and marker class it holds, so the user is shown
        /// what is actually in there and a full backup is taken first - the same contract the condition cascade
        /// delete already uses. Returns true only if the caller may now delete the database.
        /// </summary>
        private async Task<bool> ConfirmAndBackupOverwriteAsync(string projectDbPath)
        {
            string contents = await SummarizeExistingProjectAsync(projectDbPath);

            var backupService = new ProjectBackupService();
            // Build the backup path ONCE so the folder named in the prompt is the folder actually written.
            string backupPath = backupService.BuildBackupPath(projectDbPath, "before-overwrite");
            long projectSize = await backupService.EstimateProjectSizeAsync(projectDbPath);

            var result = MessageBox.Show(
                $"A project already exists in this folder:\n\n{projectDbPath}\n\n" +
                $"Creating a new project here DELETES it, including:\n\n{contents}\n\n" +
                $"A full backup (project.db + imports, about {FormatSize(projectSize)}) will be written first to:\n" +
                $"{(string.IsNullOrEmpty(backupPath) ? "(a backup location could not be resolved)" : backupPath)}\n\n" +
                "The imports folder itself is left in place, so its parquet files remain behind as orphans of the deleted project.\n\n" +
                "Choose No to keep the existing project - you can open it from the Recent Projects list instead.\n\n" +
                "Delete the existing project and create a new one here?",
                "Existing Project Will Be Deleted",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning,
                MessageBoxResult.No);

            if (result != MessageBoxResult.Yes)
            {
                return false;
            }

            if (string.IsNullOrEmpty(backupPath))
            {
                MessageBox.Show(
                    "A backup location could not be resolved for this folder, so the existing project was NOT deleted.\n\n" +
                    "Move the project somewhere with a parent folder, or pick a different location for the new project.",
                    "Backup Location Unavailable",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
                return false;
            }

            // Backup first, and abort the whole flow if it fails - an unrecoverable overwrite is never acceptable.
            CreateButton.IsEnabled = false;
            try
            {
                var backupResult = await backupService.CreateProjectBackupAsync(projectDbPath, backupPath);
                if (!backupResult.Success)
                {
                    MessageBox.Show(
                        $"Backup failed; the existing project was NOT deleted.\n\n{backupResult.ErrorMessage}",
                        "Backup Failed",
                        MessageBoxButton.OK,
                        MessageBoxImage.Error);
                    return false;
                }

                MessageBox.Show(
                    $"The existing project was backed up to:\n\n{backupResult.BackupPath}\n\n" +
                    $"({FormatSize(backupResult.BytesCopied)} copied)\n\n" +
                    "It will now be deleted from the project folder.",
                    "Backup Complete",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return true;
            }
            finally
            {
                // Re-derive rather than force-enable: the fields stay editable while the backup runs.
                ValidateInputs();
            }
        }

        /// <summary>
        /// Counts what an existing project.db holds so the overwrite prompt can state the blast radius instead of
        /// asking a bare yes/no. Each table is probed separately: older projects predate some of them, and one
        /// missing table must not reduce the whole summary to "unknown".
        /// </summary>
        private static async Task<string> SummarizeExistingProjectAsync(string projectDbPath)
        {
            var lines = new List<string>();

            try
            {
                // Pooling off: a pooled connection outlives Dispose and would hold the file open against the delete.
                using (var connection = new SqliteConnection($"Data Source={projectDbPath};Mode=ReadOnly;Pooling=False"))
                {
                    await connection.OpenAsync();

                    string existingName = await TryQueryTextAsync(
                        connection, "SELECT project_name FROM project_info ORDER BY project_id LIMIT 1");
                    if (!string.IsNullOrWhiteSpace(existingName))
                    {
                        lines.Add($"  Project name: {existingName}");
                    }

                    AddCount(lines, "DIA-NN raw files", await TryCountAsync(connection, "raw_files"));
                    AddCount(lines, "Plates", await TryCountAsync(connection, "plates"));
                    AddCount(lines, "cellenONE isolated cells", await TryCountAsync(connection, "isolated_cells"));
                    AddCount(lines, "Cell images", await TryCountAsync(connection, "cell_images"));
                    AddCount(lines, "Cell type classifications", await TryCountAsync(connection, "raw_file_cell_type_classifications"));
                    AddCount(lines, "Reference omic profiles", await TryCountAsync(connection, "cell_type_profiles"));
                    AddCount(lines, "Marker classes", await TryCountAsync(connection, "marker_classes"));
                    AddCount(lines, "k-means cluster labels", await TryCountAsync(connection, "kmeans_clusters"));
                    AddCount(lines, "Excluded runs", await TryCountAsync(connection, "excluded_runs"));
                }
            }
            catch (Exception ex)
            {
                // An unreadable database is still a project the user may not want destroyed - say so rather than
                // implying there is nothing in it.
                return $"  (the existing database could not be read to list its contents: {ex.Message})";
            }

            return lines.Count > 0
                ? string.Join(Environment.NewLine, lines)
                : "  (the existing database appears to be empty)";
        }

        private static void AddCount(List<string> lines, string label, long? count)
        {
            if (count is > 0)
            {
                lines.Add($"  {label}: {count.Value:N0}");
            }
        }

        private static async Task<long?> TryCountAsync(SqliteConnection connection, string table)
        {
            try
            {
                using (var command = connection.CreateCommand())
                {
                    // Table names come from the fixed list above, never from user input.
                    command.CommandText = $"SELECT COUNT(*) FROM {table}";
                    var value = await command.ExecuteScalarAsync();
                    return value == null || value == DBNull.Value ? (long?)null : Convert.ToInt64(value);
                }
            }
            catch (SqliteException)
            {
                // Table absent in this project's schema version.
                return null;
            }
        }

        private static async Task<string> TryQueryTextAsync(SqliteConnection connection, string sql)
        {
            try
            {
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = sql;
                    var value = await command.ExecuteScalarAsync();
                    return value == null || value == DBNull.Value ? string.Empty : Convert.ToString(value) ?? string.Empty;
                }
            }
            catch (SqliteException)
            {
                return string.Empty;
            }
        }

        private static string FormatSize(long bytes)
        {
            if (bytes <= 0) return "unknown size";
            if (bytes < 1024L * 1024L) return $"{bytes / 1024.0:F1} KB";
            if (bytes < 1024L * 1024L * 1024L) return $"{bytes / (1024.0 * 1024.0):F1} MB";
            return $"{bytes / (1024.0 * 1024.0 * 1024.0):F2} GB";
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
        }
    }
}