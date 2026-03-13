// ProjectBrowser2.xaml.cs
// Mother control that coordinates three child browser controls
// Location: SCPBrowser/Controls/ProjectBrowser2.xaml.cs

using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using SCPBrowser.Services;

namespace SCPBrowser
{
    public partial class ProjectBrowser2 : UserControl
    {
        private string _databasePath;
        private ProjectDatabaseService _projectDbService;

        // Event to bubble reclassify request to MainWindow
        public event EventHandler<ReclassifyRequestedEventArgs> ReclassifyRequested;

        public ProjectBrowser2()
        {
            InitializeComponent();
            
            // Subscribe to key markers change event
            OmicBrowser.KeyMarkersChanged += OmicBrowser_KeyMarkersChanged;
            
            // Subscribe to prior weights change event
            OmicBrowser.PriorWeightsChanged += OmicBrowser_PriorWeightsChanged;
            
            // Subscribe to reclassify request
            OmicBrowser.ReclassifyRequested += (s, e) => ReclassifyRequested?.Invoke(this, e);
            

        }

        private async void OmicBrowser_KeyMarkersChanged(object sender, KeyMarkersChangedEventArgs e)
        {
            if (_projectDbService == null || string.IsNullOrEmpty(e.CellType))
                return;

            try
            {
                await _projectDbService.SaveKeyMarkersAsync(e.CellType, e.Markers);

            }
            catch (Exception ex)
            {

            }
        }

        private async void OmicBrowser_PriorWeightsChanged(object sender, PriorWeightsChangedEventArgs e)
        {
            if (_projectDbService == null || string.IsNullOrEmpty(e.CellType))
                return;

            try
            {
                await _projectDbService.SavePriorWeightAsync(e.CellType, e.Weight);

            }
            catch (Exception ex)
            {

            }
        }

        public async Task ShowWithDatabaseAsync(string databasePath)
        {


            _databasePath = databasePath;
            _projectDbService = new ProjectDatabaseService(databasePath);

            this.Visibility = Visibility.Visible;

            var mainWindow = Window.GetWindow(this) as MainWindow;

            try
            {
                if (mainWindow != null)
                {
                    mainWindow.LoadingOverlay.SetMessage("Loading Project Browser");
                    mainWindow.LoadingOverlay.Show();
                }

                // Load key markers from database first
                if (mainWindow != null)
                {
                    mainWindow.LoadingOverlay.SetProgress("Loading key markers...");
                }
                var keyMarkers = await _projectDbService.LoadAllKeyMarkersAsync();
                OmicBrowser.SetKeyMarkers(keyMarkers);


                // Load prior weights from database
                var priorWeights = await _projectDbService.LoadAllPriorWeightsAsync();
                OmicBrowser.SetPriorWeights(priorWeights);


                if (mainWindow != null)
                {
                    mainWindow.LoadingOverlay.SetProgress("Loading reference data...");
                }
                await OmicBrowser.LoadDataAsync(databasePath);


                if (mainWindow != null)
                {
                    mainWindow.LoadingOverlay.SetProgress("Loading plate data...");
                }
                await PlateBrowser.LoadDataAsync(databasePath);


                if (mainWindow != null)
                {
                    mainWindow.LoadingOverlay.Hide();
                }


            }
            catch (Exception ex)
            {
                if (mainWindow != null)
                {
                    mainWindow.LoadingOverlay.Hide();
                }


                MessageBox.Show(
                    $"Error loading project browser:\n\n{ex.Message}",
                    "Browser Error",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }

        /// <summary>
        /// Gets the current key markers (for use by cell type predictor)
        /// </summary>
        public System.Collections.Generic.Dictionary<string, System.Collections.Generic.HashSet<string>> GetKeyMarkers()
        {
            return OmicBrowser.GetKeyMarkers();
        }

        /// <summary>
        /// Gets the excluded cell types (not considered in classification)
        /// </summary>
        public System.Collections.Generic.HashSet<string> GetExcludedCellTypes()
        {
            return OmicBrowser.GetExcludedCellTypes();
        }

        /// <summary>
        /// Gets the current prior weights (for use by cell type predictor)
        /// </summary>
        public System.Collections.Generic.Dictionary<string, double> GetPriorWeights()
        {
            return OmicBrowser.GetPriorWeights();
        }

        /// <summary>
        /// Close button handler - hides the control and triggers reclassification
        /// </summary>
        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {

            this.Visibility = Visibility.Collapsed;

            // Trigger reclassification with current priors and markers
            var priorWeights = OmicBrowser.GetPriorWeights();
            ReclassifyRequested?.Invoke(this, new ReclassifyRequestedEventArgs
            {
                ApplyKeyMarkers = true,
                PriorWeights = priorWeights
            });
        }
    }
}