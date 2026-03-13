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
using System.Windows.Input;
using System.Windows.Media;

namespace SCPBrowser
{

    public class PlateSelectionChangedEventArgs : EventArgs
    {
        public List<int> SelectedPlateIds { get; set; }
        public List<PlateFilterItem> AllPlates { get; set; }
    }

    public class PlateColorChangedEventArgs : EventArgs
    {
        public int PlateId { get; set; }
        public string PlateName { get; set; }
        public Color NewColor { get; set; }
        public Dictionary<string, Color> FullColorMap { get; set; }
    }

    public partial class PlateFilterControl : UserControl
    {
        private PlateService _plateService;
        private ParquetDataService _parquetService;
        private ObservableCollection<PlateFilterItem> _plateItems;
        private bool _isInitializing = false;

        public event EventHandler<PlateSelectionChangedEventArgs> PlateSelectionChanged;
        public event EventHandler<PlateColorChangedEventArgs> PlateColorChanged;

        public static readonly Color[] PlateColorPalette = new[]
        {
                Color.FromRgb(147, 197, 253),  // Baby blue
                Color.FromRgb(134, 239, 172),  // Baby green
                Color.FromRgb(253, 186, 116),  // Baby orange / peach
                Color.FromRgb(196, 181, 253),  // Baby purple / lavender
                Color.FromRgb(249, 168, 212),  // Baby pink
                Color.FromRgb(153, 246, 228),  // Baby teal / mint
                Color.FromRgb(253, 224, 71),   // Baby yellow
                Color.FromRgb(165, 180, 252),  // Baby indigo / periwinkle
                Color.FromRgb(167, 243, 208),  // Baby emerald / seafoam
                Color.FromRgb(252, 165, 165)   // Baby red / salmon
        };

        /// <summary>
        /// Returns a color map for plates (plate name -> color) for use in scatter plots
        /// </summary>
        public Dictionary<string, Color> GetPlateColorMap()
        {
            return _plateItems.ToDictionary(
                p => p.PlateName,
                p => ((SolidColorBrush)p.PlateColor).Color
            );
        }

        /// <summary>
        /// Returns a color map keyed by plate ID for use in histogram/distribution controls
        /// </summary>
        public Dictionary<int, Color> GetPlateColorMapById()
        {
            return _plateItems.ToDictionary(
                p => p.PlateId,
                p => ((SolidColorBrush)p.PlateColor).Color
            );
        }

        /// <summary>
        /// Updates a plate's color and raises PlateColorChanged
        /// </summary>
        public void UpdatePlateColor(int plateId, Color newColor)
        {
            var item = _plateItems.FirstOrDefault(p => p.PlateId == plateId);
            if (item == null) return;

            item.PlateColor = new SolidColorBrush(newColor);

            PlateColorChanged?.Invoke(this, new PlateColorChangedEventArgs
            {
                PlateId = plateId,
                PlateName = item.PlateName,
                NewColor = newColor,
                FullColorMap = GetPlateColorMap()
            });
        }

        /// <summary>
        /// Restores plate colors from a saved color map (plate name -> color).
        /// Called on project load to sync with persisted colors.
        /// </summary>
        public void RestorePlateColors(Dictionary<string, Color> colorMap)
        {
            if (colorMap == null || colorMap.Count == 0) return;

            foreach (var item in _plateItems)
            {
                if (colorMap.TryGetValue(item.PlateName, out var color))
                {
                    item.PlateColor = new SolidColorBrush(color);
                }
            }
        }

        public PlateFilterControl()
        {
            InitializeComponent();
            _plateItems = new ObservableCollection<PlateFilterItem>();
            PlateButtonsControl.ItemsSource = _plateItems;

        }

        /// <summary>
        /// Loads plates from the database and initializes the control
        /// </summary>
        public async Task LoadPlatesAsync(string databasePath)
        {
            try
            {
                _isInitializing = true;


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
                int colorIndex = 0;
                foreach (var plate in plates)
                {
                    int fileCount = fileCounts.ContainsKey(plate.PlateId) ? fileCounts[plate.PlateId] : 0;

                    _plateItems.Add(new PlateFilterItem
                    {
                        PlateId = plate.PlateId,
                        PlateName = plate.PlateName,
                        FileCount = fileCount,
                        IsSelected = true,
                        PlateInfo = plate,
                        PlateColor = new SolidColorBrush(PlateColorPalette[colorIndex % PlateColorPalette.Length])
                    });
                    colorIndex++;
                }

                UpdateSummaryText();
                _isInitializing = false;


            }
            catch (Exception ex)
            {

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

            }

            UpdateSummaryText();
            RaisePlateSelectionChanged();
        }

        // Expanded color swatch palette for the color picker (6 columns x 5 rows)
        private static readonly Color[] ColorSwatchPalette = new[]
        {
            // Row 1: Reds / Pinks
            Color.FromRgb(239, 68, 68),    // Red
            Color.FromRgb(252, 165, 165),   // Light red
            Color.FromRgb(236, 72, 153),    // Pink
            Color.FromRgb(249, 168, 212),   // Light pink
            Color.FromRgb(192, 38, 211),    // Fuchsia
            Color.FromRgb(232, 170, 247),   // Light fuchsia

            // Row 2: Purples / Blues
            Color.FromRgb(139, 92, 246),    // Violet
            Color.FromRgb(196, 181, 253),   // Light violet
            Color.FromRgb(59, 130, 246),    // Blue
            Color.FromRgb(147, 197, 253),   // Light blue
            Color.FromRgb(6, 182, 212),     // Cyan
            Color.FromRgb(153, 246, 228),   // Light cyan

            // Row 3: Greens
            Color.FromRgb(34, 197, 94),     // Green
            Color.FromRgb(134, 239, 172),   // Light green
            Color.FromRgb(16, 185, 129),    // Emerald
            Color.FromRgb(167, 243, 208),   // Light emerald
            Color.FromRgb(132, 204, 22),    // Lime
            Color.FromRgb(190, 242, 100),   // Light lime

            // Row 4: Yellows / Oranges
            Color.FromRgb(234, 179, 8),     // Yellow
            Color.FromRgb(253, 224, 71),    // Light yellow
            Color.FromRgb(249, 115, 22),    // Orange
            Color.FromRgb(253, 186, 116),   // Light orange
            Color.FromRgb(245, 158, 11),    // Amber
            Color.FromRgb(252, 211, 77),    // Light amber

            // Row 5: Neutrals
            Color.FromRgb(107, 114, 128),   // Gray
            Color.FromRgb(209, 213, 219),   // Light gray
            Color.FromRgb(120, 113, 108),   // Stone
            Color.FromRgb(214, 211, 209),   // Light stone
            Color.FromRgb(82, 82, 91),      // Zinc
            Color.FromRgb(30, 41, 59),      // Dark slate
        };

        /// <summary>
        /// Right-click on a plate button to open a color swatch picker
        /// </summary>
        private void PlateButton_RightClick(object sender, MouseButtonEventArgs e)
        {
            if (sender is not ToggleButton button || button.DataContext is not PlateFilterItem item)
                return;

            e.Handled = true;

            var contextMenu = new ContextMenu
            {
                Background = new SolidColorBrush(Color.FromRgb(255, 255, 255)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(203, 213, 225)),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(6),
                HasDropShadow = true
            };

            // Header
            var header = new MenuItem
            {
                Header = $"Color for: {item.PlateName}",
                IsEnabled = false,
                FontWeight = FontWeights.SemiBold,
                FontSize = 12
            };
            contextMenu.Items.Add(header);
            contextMenu.Items.Add(new Separator());

            // Color swatch grid inside a single MenuItem
            var swatchItem = new MenuItem { StaysOpenOnClick = true };
            var wrapPanel = new WrapPanel { Width = 6 * 28 };

            foreach (var color in ColorSwatchPalette)
            {
                var border = new Border
                {
                    Width = 24,
                    Height = 24,
                    Margin = new Thickness(2),
                    CornerRadius = new CornerRadius(4),
                    Background = new SolidColorBrush(color),
                    BorderBrush = new SolidColorBrush(Color.FromRgb(180, 180, 180)),
                    BorderThickness = new Thickness(1),
                    Cursor = Cursors.Hand,
                    ToolTip = $"#{color.R:X2}{color.G:X2}{color.B:X2}"
                };

                // Highlight if this is the current color
                var currentColor = ((SolidColorBrush)item.PlateColor).Color;
                if (color == currentColor)
                {
                    border.BorderBrush = new SolidColorBrush(Colors.Black);
                    border.BorderThickness = new Thickness(2);
                }

                var capturedColor = color;
                border.MouseLeftButtonUp += (s, args) =>
                {
                    contextMenu.IsOpen = false;
                    UpdatePlateColor(item.PlateId, capturedColor);
                };

                // Hover effect
                border.MouseEnter += (s, args) =>
                {
                    if (s is Border b) b.Opacity = 0.7;
                };
                border.MouseLeave += (s, args) =>
                {
                    if (s is Border b) b.Opacity = 1.0;
                };

                wrapPanel.Children.Add(border);
            }

            swatchItem.Header = wrapPanel;
            contextMenu.Items.Add(swatchItem);

            button.ContextMenu = contextMenu;
            contextMenu.IsOpen = true;
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
            SetAll(true);
        }

        /// <summary>
        /// Deselects all plates
        /// </summary>
        public void DeselectAll()
        {
            SetAll(false);
        }

        private void SetAll(bool selected)
        {
            _isInitializing = true;
            foreach (var item in _plateItems)
            {
                item.IsSelected = selected;
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
        private SolidColorBrush _plateColor;

        public int PlateId { get; set; }
        public string PlateName { get; set; }
        public int FileCount { get; set; }
        public PlateInfo PlateInfo { get; set; }

        public System.Windows.Media.SolidColorBrush PlateColor
        {
            get => _plateColor;
            set
            {
                if (_plateColor != value)
                {
                    _plateColor = value;
                    OnPropertyChanged(nameof(PlateColor));
                }
            }
        }

        public string FileCountText => $"({FileCount} files)";

        public string ToolTip
        {
            get
            {
                var lines = new List<string> { PlateName, $"{FileCount} raw files" };

                if (PlateInfo != null)
                {
                    if (!string.IsNullOrEmpty(PlateInfo.RunDate))
                        lines.Add($"Run Date: {PlateInfo.RunDate}");
                    if (!string.IsNullOrEmpty(PlateInfo.InstrumentName))
                        lines.Add($"Instrument: {PlateInfo.InstrumentName}");
                    if (!string.IsNullOrEmpty(PlateInfo.OperatorName))
                        lines.Add($"Operator: {PlateInfo.OperatorName}");
                    if (!string.IsNullOrEmpty(PlateInfo.Description))
                        lines.Add($"Description: {PlateInfo.Description}");
                }

                lines.Add("Click to toggle visibility");
                lines.Add("Right-click to change color");
                return string.Join("\n", lines);
            }
        }

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