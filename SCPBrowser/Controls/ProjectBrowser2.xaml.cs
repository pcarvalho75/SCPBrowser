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

        // Tracks whether the user actually changed something inside OmicBrowser
        // (key markers, prior weights, or excluded cell types) during this session.
        // Reset whenever ShowWithDatabaseAsync (re)loads from the DB; consulted by
        // CloseButton_Click so we don't fire a needless reclassify on a "look only" close.
        private bool _omicChanged;

        // Event to bubble reclassify request to MainWindow
        public event EventHandler<ReclassifyRequestedEventArgs> ReclassifyRequested;

        // Event to bubble post-delete refresh up to MainWindow
        public event EventHandler<ConditionDeletedEventArgs>? ConditionDeleted;

        public ProjectBrowser2()
        {
            InitializeComponent();
            
            // Subscribe to key markers change event
            OmicBrowser.KeyMarkersChanged += OmicBrowser_KeyMarkersChanged;
            
            // Subscribe to prior weights change event
            OmicBrowser.PriorWeightsChanged += OmicBrowser_PriorWeightsChanged;

            // Subscribe to excluded cell types change so we know to reclassify on close
            OmicBrowser.ExclusionsChanged += OmicBrowser_ExclusionsChanged;
            
            // Subscribe to reclassify request
            OmicBrowser.ReclassifyRequested += (s, e) => ReclassifyRequested?.Invoke(this, e);

            // Subscribe to condition delete so we can refresh sibling controls and bubble up
            ConditionsBrowser.ConditionDeleted += ConditionsBrowser_ConditionDeleted;
            

        }

        private async void ConditionsBrowser_ConditionDeleted(object? sender, ConditionDeletedEventArgs e)
        {
            // Refresh the plate browser so its "Biological Conditions" count and
            // raw_file totals reflect the cascade delete.
            try
            {
                if (!string.IsNullOrEmpty(_databasePath))
                    await PlateBrowser.LoadDataAsync(_databasePath);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ConditionsBrowser_ConditionDeleted] Plate refresh failed: {ex}");
            }

            // Bubble up so MainWindow can reload the proteomics data driving the analysis tabs.
            ConditionDeleted?.Invoke(this, e);
        }

        private async void OmicBrowser_KeyMarkersChanged(object sender, KeyMarkersChangedEventArgs e)
        {
            _omicChanged = true;

            if (_projectDbService == null || string.IsNullOrEmpty(e.CellType))
                return;

            try
            {
                await _projectDbService.SaveKeyMarkersAsync(e.CellType, e.Markers);

            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[OmicBrowser_KeyMarkersChanged] {ex}");
            }
        }

        private async void OmicBrowser_PriorWeightsChanged(object sender, PriorWeightsChangedEventArgs e)
        {
            _omicChanged = true;

            if (_projectDbService == null || string.IsNullOrEmpty(e.CellType))
                return;

            try
            {
                await _projectDbService.SavePriorWeightAsync(e.CellType, e.Weight);

            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[OmicBrowser_PriorWeightsChanged] {ex}");
            }
        }

        private void OmicBrowser_ExclusionsChanged(object sender, CellTypeExclusionsChangedEventArgs e)
        {
            // Excluded cell types are read fresh from OmicBrowser at reclassify time,
            // so there is nothing to persist here. We just record that something changed
            // so the close handler knows to reclassify.
            _omicChanged = true;
        }

        public async Task ShowWithDatabaseAsync(string databasePath)
        {


            _databasePath = databasePath;
            _projectDbService = new ProjectDatabaseService(databasePath);

            this.Visibility = Visibility.Visible;

            await LoadAllChildControlsAsync(databasePath);
        }

        /// <summary>
        /// Reloads every child control from the current database without flipping the
        /// Visibility of this dialog. Called by MainWindow.ReloadProjectDataAsync when
        /// this control is already visible to the user.
        /// </summary>
        public async Task RefreshAsync()
        {
            if (string.IsNullOrEmpty(_databasePath))
                return;

            // Re-create the service so any DB schema migrations or in-process state
            // is fresh, mirroring what ShowWithDatabaseAsync does.
            _projectDbService = new ProjectDatabaseService(_databasePath);
            await LoadAllChildControlsAsync(_databasePath);
        }

        private async Task LoadAllChildControlsAsync(string databasePath)
        {
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
                    mainWindow.LoadingOverlay.SetProgress("Loading biological conditions...");
                }
                await ConditionsBrowser.LoadDataAsync(databasePath);


                if (mainWindow != null)
                {
                    mainWindow.LoadingOverlay.Hide();
                }

                // Reset dirty flag AFTER loading completes. Hydrating OmicBrowser via
                // SetKeyMarkers / SetPriorWeights fires its change events, which would
                // otherwise leave _omicChanged = true and force a needless reclassify
                // on close.
                _omicChanged = false;


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
        /// only if the user actually changed something inside OmicBrowser.
        /// </summary>
        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            this.Visibility = Visibility.Collapsed;

            if (!_omicChanged)
                return;

            // User changed key markers, prior weights, or excluded cell types.
            // Trigger reclassification so the analysis tabs reflect those edits.
            var priorWeights = OmicBrowser.GetPriorWeights();
            ReclassifyRequested?.Invoke(this, new ReclassifyRequestedEventArgs
            {
                ApplyKeyMarkers = true,
                PriorWeights = priorWeights
            });

            // Edits have been flushed via the reclassify; clear the flag so
            // a subsequent close without further edits doesn't reclassify again.
            _omicChanged = false;
        }
    }
}