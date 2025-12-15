// ProjectBrowser2.xaml.cs
// Mother control that coordinates three child browser controls
// Location: SCPBrowser/Controls/ProjectBrowser2.xaml.cs

using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace SCPBrowser
{
    public partial class ProjectBrowser2 : UserControl
    {
        public ProjectBrowser2()
        {
            InitializeComponent();
            Console.WriteLine("ProjectBrowser2 (mother control) initialized");
        }

        public async Task ShowWithDatabaseAsync(string databasePath)
        {
            Console.WriteLine($"ProjectBrowser2: Loading database from {databasePath}");

            this.Visibility = Visibility.Visible;

            var mainWindow = Window.GetWindow(this) as MainWindow;

            try
            {
                if (mainWindow != null)
                {
                    mainWindow.LoadingOverlay.SetMessage("Loading Project Browser");
                    mainWindow.LoadingOverlay.Show();
                }

                if (mainWindow != null)
                {
                    mainWindow.LoadingOverlay.SetProgress("Loading transcriptomic data...");
                }
                await OmicBrowser.LoadDataAsync(databasePath);
                Console.WriteLine("  ✓ Omic Browser loaded");

                if (mainWindow != null)
                {
                    mainWindow.LoadingOverlay.SetProgress("Loading plate data...");
                }
                await PlateBrowser.LoadDataAsync(databasePath);
                Console.WriteLine("  ✓ Plate Browser loaded");

                if (mainWindow != null)
                {
                    mainWindow.LoadingOverlay.Hide();
                }

                Console.WriteLine("ProjectBrowser2: All data loaded successfully");
            }
            catch (Exception ex)
            {
                if (mainWindow != null)
                {
                    mainWindow.LoadingOverlay.Hide();
                }

                Console.WriteLine($"ProjectBrowser2 Error: {ex.Message}");
                MessageBox.Show(
                    $"Error loading project browser:\n\n{ex.Message}",
                    "Browser Error",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }

        /// <summary>
        /// Close button handler - simply hides the control
        /// </summary>
        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            Console.WriteLine("Closing ProjectBrowser2...");
            this.Visibility = Visibility.Collapsed;
        }
    }
}