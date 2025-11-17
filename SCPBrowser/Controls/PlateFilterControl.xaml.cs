// PlateFilterControl.xaml.cs
// Global plate filter control - manages plate selection for filtering data
// Location: SCPBrowser/Controls/PlateFilterControl.xaml.cs

using SCPBrowser.Models;
using SCPBrowser.Services;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace SCPBrowser
{
    public class PlateSelectionChangedEventArgs : EventArgs
    {
        public List<int> SelectedPlateIds { get; set; }
        public List<PlateFilterItem> AllPlates { get; set; }
    }

    public partial class PlateFilterControl : UserControl
    {
        private PlateService _plateService;
        private ParquetDataService _parquetService;
        private ObservableCollection<PlateFilterItem> _plateItems;
        private bool _isInitializing = false;

        public event EventHandler<PlateSelectionChangedEventArgs> PlateSelectionChanged;

        public PlateFilterControl()
        {
            InitializeComponent();
            _plateItems = new ObservableCollection<PlateFilterItem>();
            PlateButtonsControl.ItemsSource = _plateItems;
            Console.WriteLine("PlateFilterControl initialized");
        }

        /// <summary>
        /// Loads plates from the database and initializes the control
        /// </summary>
        public async Task LoadPlatesAsync(string databasePath)
        {
            try
            {
                _isInitializing = true;
                Console.WriteLine($"PlateFilterControl: Loading plates from {databasePath}");

                _plateService = new PlateService(databasePath);
                _parquetService = new ParquetDataService(databasePath);

                // Get all plates
                var plates = await _plateService.GetPlatesAsync();

                // Get raw file counts per plate
                var allRawFiles = await _parquetService.GetRawFilesAsync();
                var fileCounts = allRawFiles
                    .Where(rf => rf.PlateId.HasValue)
                    .GroupBy(rf => rf.PlateId.Value)
                    .ToDictionary(g => g.Key, g => g.Count());

                // Create plate filter items (all selected by default)
                _plateItems.Clear();
                foreach (var plate in plates)
                {
                    int fileCount = fileCounts.ContainsKey(plate.PlateId) ? fileCounts[plate.PlateId] : 0;

                    _plateItems.Add(new PlateFilterItem
                    {
                        PlateId = plate.PlateId,
                        PlateName = plate.PlateName,
                        FileCount = fileCount,
                        IsSelected = true, // All plates selected by default
                        PlateInfo = plate
                    });
                }

                UpdateSummaryText();
                _isInitializing = false;

                Console.WriteLine($"PlateFilterControl: Loaded {_plateItems.Count} plates");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"PlateFilterControl Error: {ex.Message}");
                _isInitializing = false;
                throw;
            }
        }

        /// <summary>
        /// Called when any plate button is checked or unchecked
        /// </summary>
        private void PlateButton_CheckedChanged(object sender, RoutedEventArgs e)
        {
            if (_isInitializing)
                return;

            // Get the toggle button and update the corresponding item's IsSelected
            if (sender is ToggleButton button && button.DataContext is PlateFilterItem item)
            {
                item.IsSelected = button.IsChecked == true;
                Console.WriteLine($"Plate '{item.PlateName}' IsSelected set to: {item.IsSelected}");
            }

            UpdateSummaryText();
            RaisePlateSelectionChanged();
        }

        /// <summary>
        /// Updates the summary text showing how many plates are selected
        /// </summary>
        private void UpdateSummaryText()
        {
            int selectedCount = _plateItems.Count(p => p.IsSelected);
            int totalCount = _plateItems.Count;

            if (selectedCount == totalCount)
            {
                SummaryText.Text = "All plates selected";
            }
            else if (selectedCount == 0)
            {
                SummaryText.Text = "No plates selected";
                SummaryText.Foreground = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(220, 38, 38)); // Red warning
            }
            else
            {
                SummaryText.Text = $"{selectedCount} of {totalCount} plates selected";
                SummaryText.Foreground = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(100, 116, 139)); // Normal gray
            }
        }

        /// <summary>
        /// Raises the PlateSelectionChanged event with current selection
        /// </summary>
        private void RaisePlateSelectionChanged()
        {
            var selectedIds = _plateItems
                .Where(p => p.IsSelected)
                .Select(p => p.PlateId)
                .ToList();

            Console.WriteLine($"PlateFilterControl: Selection changed - {selectedIds.Count} plates selected");

            PlateSelectionChanged?.Invoke(this, new PlateSelectionChangedEventArgs
            {
                SelectedPlateIds = selectedIds,
                AllPlates = _plateItems.ToList()
            });
        }

        /// <summary>
        /// Gets the currently selected plate IDs
        /// </summary>
        public List<int> GetSelectedPlateIds()
        {
            return _plateItems
                .Where(p => p.IsSelected)
                .Select(p => p.PlateId)
                .ToList();
        }

        /// <summary>
        /// Selects all plates
        /// </summary>
        public void SelectAll()
        {
            _isInitializing = true;
            foreach (var item in _plateItems)
            {
                item.IsSelected = true;
            }
            _isInitializing = false;
            UpdateSummaryText();
            RaisePlateSelectionChanged();
        }

        /// <summary>
        /// Deselects all plates
        /// </summary>
        public void DeselectAll()
        {
            _isInitializing = true;
            foreach (var item in _plateItems)
            {
                item.IsSelected = false;
            }
            _isInitializing = false;
            UpdateSummaryText();
            RaisePlateSelectionChanged();
        }
    }

    /// <summary>
    /// Data item for each plate in the filter control
    /// </summary>
    public class PlateFilterItem : INotifyPropertyChanged
    {
        private bool _isSelected;

        public int PlateId { get; set; }
        public string PlateName { get; set; }
        public int FileCount { get; set; }
        public PlateInfo PlateInfo { get; set; }

        public string FileCountText => $"({FileCount} files)";

        public string ToolTip => $"{PlateName}\n{FileCount} raw files\nClick to toggle visibility";

        public bool IsSelected
        {
            get => _isSelected;
            set
            {
                if (_isSelected != value)
                {
                    _isSelected = value;
                    OnPropertyChanged(nameof(IsSelected));
                }
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;

        protected void OnPropertyChanged(string propertyName)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}