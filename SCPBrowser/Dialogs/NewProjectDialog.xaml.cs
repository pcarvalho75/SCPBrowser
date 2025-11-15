using System;
using System.IO;
using System.Windows;
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

        private void ValidateInputs()
        {
            bool isValid = !string.IsNullOrWhiteSpace(ProjectNameTextBox.Text) &&
                          !string.IsNullOrWhiteSpace(ProjectLocationTextBox.Text);

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
                if (!Directory.Exists(ProjectLocation))
                {
                    Directory.CreateDirectory(ProjectLocation);
                }

                // Create imports subdirectory
                string importsPath = Path.Combine(ProjectLocation, "imports");
                if (!Directory.Exists(importsPath))
                {
                    Directory.CreateDirectory(importsPath);
                }

                // Check if project.db already exists
                string projectDbPath = Path.Combine(ProjectLocation, "project.db");
                if (File.Exists(projectDbPath))
                {
                    var result = MessageBox.Show(
                        $"A project database already exists in this location:\n\n{projectDbPath}\n\nDo you want to overwrite it?",
                        "Project Already Exists",
                        MessageBoxButton.YesNo,
                        MessageBoxImage.Question);

                    if (result == MessageBoxResult.No)
                    {
                        return;
                    }

                    File.Delete(projectDbPath);
                }

                // Create the project database
                var projectService = new ProjectDatabaseService(projectDbPath);
                await projectService.CreateProjectAsync(ProjectName, ProjectDescription);

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

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
        }
    }
}