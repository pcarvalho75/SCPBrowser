// OmicBrowserControl.xaml.cs
// Child control for browsing transcriptomic data (cell type profiles)
// Location: SCPBrowser/Controls/OmicBrowserControl.xaml.cs

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using SCPBrowser.Services;

namespace SCPBrowser
{
    public partial class OmicBrowserControl : UserControl
    {
        private ReferenceDataService _referenceService;
        private TranscriptomicDatabase _transcriptomicData;
        
        // Key markers per cell type (persisted to database in Step 3)
        private Dictionary<string, HashSet<string>> _keyMarkers = new Dictionary<string, HashSet<string>>();
        private string _currentCellType;
        private CellTypeProfile _currentProfile;
        private TextBox _searchBox;
        private StackPanel _markersListPanel;
        private Dictionary<string, double> _geneSpecificity;
        
        // Excluded cell types (not considered in classification)
        private HashSet<string> _excludedCellTypes = new HashSet<string>();

        // Prior weights per cell type
        private Dictionary<string, double> _priorWeights = new Dictionary<string, double>();

        // Event to notify when markers change
        public event EventHandler<KeyMarkersChangedEventArgs> KeyMarkersChanged;
        
        // Event to request reclassification with current key markers
        public event EventHandler<ReclassifyRequestedEventArgs> ReclassifyRequested;
        
        // Event to notify when cell type exclusions change
        public event EventHandler<CellTypeExclusionsChangedEventArgs> ExclusionsChanged;

        // Event to notify when prior weights change
        public event EventHandler<PriorWeightsChangedEventArgs> PriorWeightsChanged;

        public OmicBrowserControl()
        {
            InitializeComponent();
            _referenceService = new ReferenceDataService();

        }

        /// <summary>
        /// Gets the current key markers for all cell types
        /// </summary>
        public Dictionary<string, HashSet<string>> GetKeyMarkers() => _keyMarkers;

        /// <summary>
        /// Sets key markers (called when loading from database)
        /// </summary>
        public void SetKeyMarkers(Dictionary<string, HashSet<string>> markers)
        {
            _keyMarkers = markers ?? new Dictionary<string, HashSet<string>>();
        }

        /// <summary>
        /// Gets the set of excluded cell types
        /// </summary>
        public HashSet<string> GetExcludedCellTypes() => new HashSet<string>(_excludedCellTypes);

        /// <summary>
        /// Sets excluded cell types (called when loading from database)
        /// </summary>
        public void SetExcludedCellTypes(HashSet<string> excluded)
        {
            _excludedCellTypes = excluded ?? new HashSet<string>();
        }

        /// <summary>
        /// Gets the current prior weights for all cell types
        /// </summary>
        public Dictionary<string, double> GetPriorWeights() => new Dictionary<string, double>(_priorWeights);

        /// <summary>
        /// Sets prior weights (called when loading from database)
        /// </summary>
        public void SetPriorWeights(Dictionary<string, double> weights)
        {
            _priorWeights = weights ?? new Dictionary<string, double>();
        }

        /// <summary>
        /// Event handler for cell type enable/disable checkbox
        /// </summary>
        private void CellTypeEnabled_Changed(object sender, RoutedEventArgs e)
        {
            if (sender is CheckBox checkBox && checkBox.Tag is string cellType)
            {
                bool isEnabled = checkBox.IsChecked == true;
                
                if (isEnabled)
                {
                    _excludedCellTypes.Remove(cellType);

                }
                else
                {
                    _excludedCellTypes.Add(cellType);

                }

                // Fire event to notify listeners
                ExclusionsChanged?.Invoke(this, new CellTypeExclusionsChangedEventArgs
                {
                    ExcludedCellTypes = new HashSet<string>(_excludedCellTypes)
                });
            }
        }

        /// <summary>
        /// Event handler for prior weight TextBox losing focus
        /// </summary>
        private void PriorWeight_LostFocus(object sender, RoutedEventArgs e)
        {
            if (sender is TextBox textBox && textBox.Tag is string cellType)
            {
                if (double.TryParse(textBox.Text, out double weight) && weight >= 0)
                {
                    _priorWeights[cellType] = weight;


                    // Update the card's displayed weight and re-sort
                    if (CellTypeListBox.ItemsSource is System.Collections.Generic.List<CellTypeMetadata> metaList)
                    {
                        var match = metaList.FirstOrDefault(m => m.CellType == cellType);
                        if (match != null)
                            match.PriorWeight = weight;

                        // Re-sort by descending prior weight
                        var sorted = metaList.OrderByDescending(m => m.PriorWeight).ThenBy(m => m.CellType).ToList();
                        CellTypeListBox.ItemsSource = sorted;

                        // Re-select the current cell type
                        CellTypeListBox.SelectedItem = sorted.FirstOrDefault(m => m.CellType == cellType);
                    }

                    PriorWeightsChanged?.Invoke(this, new PriorWeightsChangedEventArgs
                    {
                        CellType = cellType,
                        Weight = weight
                    });
                }
                else
                {
                    // Revert to previous value or default
                    double current = _priorWeights.ContainsKey(cellType) ? _priorWeights[cellType] : 1.0;
                    textBox.Text = current.ToString("F2");
                }
            }
        }

        /// <summary>
        /// Handles PreviewMouseDown on prior weight TextBox to steal focus from ListBox
        /// </summary>
        private void PriorWeight_PreviewMouseDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is TextBox textBox && !textBox.IsFocused)
            {
                textBox.Focus();
                textBox.SelectAll();
                e.Handled = true;
            }
            // If already focused, let the click through normally for caret positioning
        }

        /// <summary>
        /// Loads reference data from the database and populates the UI
        /// </summary>
        public async Task LoadDataAsync(string databasePath)
        {
            try
            {


                // Load reference data (returns null if no profiles imported yet)
                _transcriptomicData = await _referenceService.LoadTranscriptomicDataAsync(databasePath);

                if (_transcriptomicData == null)
                {

                    PopulateTranscriptomicUI();
                    return;
                }

                // Calculate gene specificity for all genes
                CalculateGeneSpecificity();

                // Populate the UI
                PopulateTranscriptomicUI();


            }
            catch (Exception ex)
            {

                throw; // Let the parent handle the error display
            }
        }

        private void CalculateGeneSpecificity()
        {
            _geneSpecificity = new Dictionary<string, double>();
            
            if (_transcriptomicData == null || _transcriptomicData.CellTypeProfiles.Count == 0)
                return;

            int totalCellTypes = _transcriptomicData.CellTypeProfiles.Count;

            // Collect all genes
            var allGenes = new HashSet<string>();
            foreach (var profile in _transcriptomicData.CellTypeProfiles.Values)
            {
                foreach (var gene in profile.MedianExpression.Keys)
                    allGenes.Add(gene);
            }

            // Calculate IDF-style specificity
            foreach (var gene in allGenes)
            {
                int cellTypesExpressing = 0;
                foreach (var profile in _transcriptomicData.CellTypeProfiles.Values)
                {
                    if (profile.MedianExpression.TryGetValue(gene, out double expr) && expr > 0)
                        cellTypesExpressing++;
                }

                if (cellTypesExpressing > 0)
                    _geneSpecificity[gene] = Math.Log((double)totalCellTypes / cellTypesExpressing);
            }
        }

        private void PopulateTranscriptomicUI()
        {
            if (_transcriptomicData == null || _transcriptomicData.TotalCellTypes == 0)
            {
                TotalCellTypesText.Text = "0";
                TotalCellsText.Text = "0";
                TotalGenesText.Text = "0";
                return;
            }

            // Update summary statistics
            TotalCellTypesText.Text = _transcriptomicData.TotalCellTypes.ToString("N0");
            TotalCellsText.Text = _transcriptomicData.TotalCells.ToString("N0");
            TotalGenesText.Text = _transcriptomicData.TotalGenes.ToString("N0");

            // Populate cell type list
            var cellTypeList = _transcriptomicData.CellTypeMetadata.Values.ToList();

            // Apply saved prior weights
            foreach (var meta in cellTypeList)
            {
                if (_priorWeights.TryGetValue(meta.CellType, out double w))
                    meta.PriorWeight = w;
            }

            // Sort by descending prior weight
            cellTypeList = cellTypeList.OrderByDescending(m => m.PriorWeight).ThenBy(m => m.CellType).ToList();

            CellTypeListBox.ItemsSource = cellTypeList;
        }

        private void CellTypeListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (CellTypeListBox.SelectedItem is CellTypeMetadata metadata)
            {
                ShowCellTypeDetails(metadata);
            }
        }

        private void ShowCellTypeDetails(CellTypeMetadata metadata)
        {
            _currentCellType = metadata.CellType;
            CellTypeDetailsPanel.Children.Clear();

            // Ensure markers set exists for this cell type
            if (!_keyMarkers.ContainsKey(_currentCellType))
                _keyMarkers[_currentCellType] = new HashSet<string>();

            // Cell type name
            var nameBlock = new TextBlock
            {
                Text = metadata.CellType,
                FontSize = 18,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 0, 0, 15)
            };
            CellTypeDetailsPanel.Children.Add(nameBlock);

            // Statistics grid
            var statsGrid = new Grid { Margin = new Thickness(0, 0, 0, 20) };
            statsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            statsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            statsGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            statsGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            // Cell count stat
            var cellCountBorder = CreateStatBox("Number of Cells", metadata.CellCount.ToString("N0"), "#2563eb");
            Grid.SetColumn(cellCountBorder, 0);
            Grid.SetRow(cellCountBorder, 0);
            statsGrid.Children.Add(cellCountBorder);

            // Genes expressed stat
            var genesExpressedBorder = CreateStatBox("Genes Expressed", metadata.GenesExpressed.ToString("N0"), "#10b981");
            Grid.SetColumn(genesExpressedBorder, 1);
            Grid.SetRow(genesExpressedBorder, 0);
            statsGrid.Children.Add(genesExpressedBorder);

            // Age range stat (if available)
            if (!string.IsNullOrEmpty(metadata.AgeRange))
            {
                var ageRangeBorder = CreateStatBox("Age Range", metadata.AgeRange, "#f59e0b");
                Grid.SetColumn(ageRangeBorder, 0);
                Grid.SetRow(ageRangeBorder, 1);
                statsGrid.Children.Add(ageRangeBorder);
            }

            CellTypeDetailsPanel.Children.Add(statsGrid);

            // Prior weight editor
            var priorPanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 15) };
            priorPanel.Children.Add(new TextBlock
            {
                Text = "Prior Weight:",
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0)
            });

            double currentWeight = _priorWeights.ContainsKey(_currentCellType) ? _priorWeights[_currentCellType] : 1.0;
            var priorTextBox = new TextBox
            {
                Text = currentWeight.ToString("F2"),
                Width = 60,
                Height = 28,
                FontSize = 13,
                Padding = new Thickness(2, 1, 2, 1),
                HorizontalContentAlignment = HorizontalAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center,
                BorderBrush = new SolidColorBrush(Color.FromRgb(148, 163, 184)),
                BorderThickness = new Thickness(1),
                Tag = _currentCellType,
                ToolTip = "Prior weight for this cell type (auto-normalized when reclassifying)"
            };
            priorTextBox.LostFocus += PriorWeight_LostFocus;
            priorPanel.Children.Add(priorTextBox);

            priorPanel.Children.Add(new TextBlock
            {
                Text = "(auto-normalized)",
                FontSize = 10,
                Foreground = new SolidColorBrush(Color.FromRgb(148, 163, 184)),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(8, 0, 0, 0)
            });
            CellTypeDetailsPanel.Children.Add(priorPanel);

            // Get profile and show key markers section + top expressed genes
            if (_transcriptomicData.CellTypeProfiles.TryGetValue(metadata.CellType, out var profile))
            {
                _currentProfile = profile;
                ShowKeyMarkersSection(profile);
                ShowTopExpressedGenes(profile);
            }
        }

        private void ShowKeyMarkersSection(CellTypeProfile profile)
        {
            // Main container with gradient background
            var mainBorder = new Border
            {
                Background = new LinearGradientBrush(
                    Color.FromRgb(254, 243, 199),  // amber-100
                    Color.FromRgb(253, 230, 138),  // amber-200
                    45),
                BorderBrush = new SolidColorBrush(Color.FromRgb(251, 191, 36)), // amber-400
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(15),
                Margin = new Thickness(0, 0, 0, 20)
            };

            var mainStack = new StackPanel();

            // Header with icon and title
            var headerStack = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 10) };
            
            // Star icon
            var starIcon = new TextBlock
            {
                Text = "⭐",
                FontSize = 16,
                Margin = new Thickness(0, 0, 8, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            headerStack.Children.Add(starIcon);

            var headerText = new TextBlock
            {
                Text = "Key Markers for Classification",
                FontSize = 14,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Color.FromRgb(146, 64, 14)), // amber-800
                VerticalAlignment = VerticalAlignment.Center
            };
            headerStack.Children.Add(headerText);

            // Info tooltip
            var infoText = new TextBlock
            {
                Text = " (?)",
                FontSize = 12,
                Foreground = new SolidColorBrush(Color.FromRgb(180, 83, 9)), // amber-700
                VerticalAlignment = VerticalAlignment.Center,
                Cursor = Cursors.Help,
                ToolTip = "Click on genes below to mark them as key markers.\nKey markers boost classification when present with high abundance,\nand penalize classification when absent or low."
            };
            headerStack.Children.Add(infoText);

            mainStack.Children.Add(headerStack);

            // Selected markers count
            var selectedMarkers = _keyMarkers[_currentCellType];
            var countText = new TextBlock
            {
                Text = selectedMarkers.Count == 0 
                    ? "No markers selected - click genes below to add"
                    : $"{selectedMarkers.Count} marker(s) selected",
                FontSize = 11,
                Foreground = new SolidColorBrush(Color.FromRgb(180, 83, 9)),
                Margin = new Thickness(0, 0, 0, 10),
                FontStyle = selectedMarkers.Count == 0 ? FontStyles.Italic : FontStyles.Normal
            };
            mainStack.Children.Add(countText);

            // Search box
            var searchBorder = new Border
            {
                Background = new SolidColorBrush(Colors.White),
                BorderBrush = new SolidColorBrush(Color.FromRgb(251, 191, 36)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
                Margin = new Thickness(0, 0, 0, 10)
            };

            var searchGrid = new Grid();
            searchGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            searchGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var searchIcon = new TextBlock
            {
                Text = "🔍",
                FontSize = 12,
                Margin = new Thickness(8, 0, 5, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Opacity = 0.6
            };
            Grid.SetColumn(searchIcon, 0);
            searchGrid.Children.Add(searchIcon);

            _searchBox = new TextBox
            {
                BorderThickness = new Thickness(0),
                Background = Brushes.Transparent,
                Padding = new Thickness(5),
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center
            };
            _searchBox.TextChanged += SearchBox_TextChanged;
            
            // Placeholder text behavior
            var placeholderText = "Search genes (e.g., GFAP, MAP2, IBA1...)";
            _searchBox.Text = placeholderText;
            _searchBox.Foreground = Brushes.Gray;
            _searchBox.GotFocus += (s, e) =>
            {
                if (_searchBox.Text == placeholderText)
                {
                    _searchBox.Text = "";
                    _searchBox.Foreground = Brushes.Black;
                }
            };
            _searchBox.LostFocus += (s, e) =>
            {
                if (string.IsNullOrWhiteSpace(_searchBox.Text))
                {
                    _searchBox.Text = placeholderText;
                    _searchBox.Foreground = Brushes.Gray;
                }
            };

            Grid.SetColumn(_searchBox, 1);
            searchGrid.Children.Add(_searchBox);

            searchBorder.Child = searchGrid;
            mainStack.Children.Add(searchBorder);

            // Paste List section (collapsible)
            var pasteSection = CreatePasteListSection();
            mainStack.Children.Add(pasteSection);

            // Markers list (scrollable)
            var listBorder = new Border
            {
                Background = new SolidColorBrush(Colors.White),
                BorderBrush = new SolidColorBrush(Color.FromRgb(226, 232, 240)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
                MaxHeight = 200
            };

            var scrollViewer = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
            };

            _markersListPanel = new StackPanel();
            PopulateMarkersList("");

            scrollViewer.Content = _markersListPanel;
            listBorder.Child = scrollViewer;
            mainStack.Children.Add(listBorder);

            // Checkbox for applying key markers
            _applyKeyMarkersCheckbox = new CheckBox
            {
                Content = "Apply key marker adjustments to scoring",
                IsChecked = true,
                Margin = new Thickness(0, 10, 0, 5),
                FontSize = 11,
                Foreground = new SolidColorBrush(Color.FromRgb(100, 116, 139)) // slate-500
            };
            mainStack.Children.Add(_applyKeyMarkersCheckbox);

            // Reclassify button (always visible, prominent green)
            var reclassifyButton = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(22, 163, 74)), // green-600
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(12, 8, 12, 8),
                Cursor = Cursors.Hand,
                Margin = new Thickness(0, 5, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Center
            };
            var reclassifyButtonText = new TextBlock
            {
                Text = "🔄 Reclassify All Cells",
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                Foreground = Brushes.White
            };
            reclassifyButton.Child = reclassifyButtonText;
            reclassifyButton.MouseLeftButtonUp += ReclassifyButton_Click;
            reclassifyButton.MouseEnter += (s, e) => reclassifyButton.Background = new SolidColorBrush(Color.FromRgb(21, 128, 61)); // green-700
            reclassifyButton.MouseLeave += (s, e) => reclassifyButton.Background = new SolidColorBrush(Color.FromRgb(22, 163, 74));
            mainStack.Children.Add(reclassifyButton);

            // Status text for reclassify feedback
            _reclassifyStatusText = new TextBlock
            {
                FontSize = 11,
                Margin = new Thickness(0, 5, 0, 0),
                TextWrapping = TextWrapping.Wrap,
                HorizontalAlignment = HorizontalAlignment.Center,
                Visibility = Visibility.Collapsed
            };
            mainStack.Children.Add(_reclassifyStatusText);

            mainBorder.Child = mainStack;
            CellTypeDetailsPanel.Children.Add(mainBorder);
        }

        private CheckBox _applyKeyMarkersCheckbox;

        private TextBlock _reclassifyStatusText;

        private TextBox _pasteTextBox;
        private TextBlock _pasteResultText;
        private Border _pasteExpanderContent;

        private UIElement CreatePasteListSection()
        {
            var container = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };

            // Toggle button
            var toggleButton = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(254, 249, 195)), // amber-100
                BorderBrush = new SolidColorBrush(Color.FromRgb(251, 191, 36)), // amber-400
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(8, 5, 8, 5),
                Cursor = Cursors.Hand
            };

            var toggleContent = new StackPanel { Orientation = Orientation.Horizontal };
            var toggleIcon = new TextBlock
            {
                Text = "📋",
                FontSize = 12,
                Margin = new Thickness(0, 0, 5, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            toggleContent.Children.Add(toggleIcon);

            var toggleText = new TextBlock
            {
                Text = "Paste gene list (comma-separated)",
                FontSize = 11,
                Foreground = new SolidColorBrush(Color.FromRgb(146, 64, 14)), // amber-800
                VerticalAlignment = VerticalAlignment.Center
            };
            toggleContent.Children.Add(toggleText);

            var toggleArrow = new TextBlock
            {
                Text = " ▼",
                FontSize = 9,
                Foreground = new SolidColorBrush(Color.FromRgb(180, 83, 9)),
                VerticalAlignment = VerticalAlignment.Center
            };
            toggleContent.Children.Add(toggleArrow);

            toggleButton.Child = toggleContent;

            // Expandable content (hidden by default)
            _pasteExpanderContent = new Border
            {
                Background = new SolidColorBrush(Colors.White),
                BorderBrush = new SolidColorBrush(Color.FromRgb(251, 191, 36)),
                BorderThickness = new Thickness(1, 0, 1, 1),
                CornerRadius = new CornerRadius(0, 0, 4, 4),
                Padding = new Thickness(10),
                Visibility = Visibility.Collapsed
            };

            var expanderStack = new StackPanel();

            // TextBox for pasting
            _pasteTextBox = new TextBox
            {
                Height = 60,
                TextWrapping = TextWrapping.Wrap,
                AcceptsReturn = true,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                FontSize = 11,
                FontFamily = new FontFamily("Consolas"),
                BorderBrush = new SolidColorBrush(Color.FromRgb(226, 232, 240)),
                Padding = new Thickness(5)
            };

            // Placeholder behavior
            var pastePlaceholder = "Paste genes here: GFAP, S100B, AQP4, ALDH1L1, SLC1A3";
            _pasteTextBox.Text = pastePlaceholder;
            _pasteTextBox.Foreground = Brushes.Gray;
            _pasteTextBox.GotFocus += (s, e) =>
            {
                if (_pasteTextBox.Text == pastePlaceholder)
                {
                    _pasteTextBox.Text = "";
                    _pasteTextBox.Foreground = Brushes.Black;
                }
            };
            _pasteTextBox.LostFocus += (s, e) =>
            {
                if (string.IsNullOrWhiteSpace(_pasteTextBox.Text))
                {
                    _pasteTextBox.Text = pastePlaceholder;
                    _pasteTextBox.Foreground = Brushes.Gray;
                }
            };

            expanderStack.Children.Add(_pasteTextBox);

            // Button row
            var buttonRow = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(0, 8, 0, 0)
            };

            var addButton = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(245, 158, 11)), // amber-500
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(12, 6, 12, 6),
                Cursor = Cursors.Hand
            };
            var addButtonText = new TextBlock
            {
                Text = "Add Markers",
                FontSize = 11,
                FontWeight = FontWeights.SemiBold,
                Foreground = Brushes.White
            };
            addButton.Child = addButtonText;
            addButton.MouseLeftButtonUp += PasteAddButton_Click;
            addButton.MouseEnter += (s, e) => addButton.Background = new SolidColorBrush(Color.FromRgb(217, 119, 6)); // amber-600
            addButton.MouseLeave += (s, e) => addButton.Background = new SolidColorBrush(Color.FromRgb(245, 158, 11));

            buttonRow.Children.Add(addButton);

            // Clear button
            var clearButton = new Border
            {
                Background = Brushes.Transparent,
                Padding = new Thickness(12, 6, 12, 6),
                Cursor = Cursors.Hand,
                Margin = new Thickness(10, 0, 0, 0)
            };
            var clearButtonText = new TextBlock
            {
                Text = "Clear All Markers",
                FontSize = 11,
                Foreground = new SolidColorBrush(Color.FromRgb(180, 83, 9)),
                TextDecorations = TextDecorations.Underline
            };
            clearButton.Child = clearButtonText;
            clearButton.MouseLeftButtonUp += ClearAllMarkers_Click;

            buttonRow.Children.Add(clearButton);
            expanderStack.Children.Add(buttonRow);

            // Result text
            _pasteResultText = new TextBlock
            {
                FontSize = 11,
                Margin = new Thickness(0, 8, 0, 0),
                TextWrapping = TextWrapping.Wrap,
                Visibility = Visibility.Collapsed
            };
            expanderStack.Children.Add(_pasteResultText);

            _pasteExpanderContent.Child = expanderStack;

            // Toggle click behavior
            toggleButton.MouseLeftButtonUp += (s, e) =>
            {
                if (_pasteExpanderContent.Visibility == Visibility.Collapsed)
                {
                    _pasteExpanderContent.Visibility = Visibility.Visible;
                    toggleArrow.Text = " ▲";
                    toggleButton.CornerRadius = new CornerRadius(4, 4, 0, 0);
                }
                else
                {
                    _pasteExpanderContent.Visibility = Visibility.Collapsed;
                    toggleArrow.Text = " ▼";
                    toggleButton.CornerRadius = new CornerRadius(4);
                }
            };

            container.Children.Add(toggleButton);
            container.Children.Add(_pasteExpanderContent);

            return container;
        }

        private void PasteAddButton_Click(object sender, MouseButtonEventArgs e)
        {
            var placeholder = "Paste genes here: GFAP, S100B, AQP4, ALDH1L1, SLC1A3";
            var text = _pasteTextBox.Text;
            
            if (string.IsNullOrWhiteSpace(text) || text == placeholder)
            {
                _pasteResultText.Text = "Please paste a list of genes first.";
                _pasteResultText.Foreground = new SolidColorBrush(Color.FromRgb(220, 38, 38)); // red
                _pasteResultText.Visibility = Visibility.Visible;
                return;
            }

            // Parse genes (comma, semicolon, newline, space separated)
            var inputGenes = text
                .Split(new[] { ',', ';', '\n', '\r', '\t' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(g => g.Trim().ToUpperInvariant())
                .Where(g => !string.IsNullOrEmpty(g))
                .Distinct()
                .ToList();

            if (inputGenes.Count == 0)
            {
                _pasteResultText.Text = "No valid genes found in input.";
                _pasteResultText.Foreground = new SolidColorBrush(Color.FromRgb(220, 38, 38));
                _pasteResultText.Visibility = Visibility.Visible;
                return;
            }

            // Check which genes exist in the current cell type's profile
            var availableGenes = _currentProfile.MedianExpression.Keys
                .Select(g => g.ToUpperInvariant())
                .ToHashSet();

            var foundGenes = new List<string>();
            var notFoundGenes = new List<string>();

            foreach (var gene in inputGenes)
            {
                // Find the actual case-sensitive gene name from the profile
                var actualGeneName = _currentProfile.MedianExpression.Keys
                    .FirstOrDefault(g => g.Equals(gene, StringComparison.OrdinalIgnoreCase));

                if (actualGeneName != null)
                {
                    foundGenes.Add(actualGeneName);
                    _keyMarkers[_currentCellType].Add(actualGeneName);
                }
                else
                {
                    notFoundGenes.Add(gene);
                }
            }

            // Build result message
            var resultParts = new List<string>();
            if (foundGenes.Count > 0)
            {
                resultParts.Add($"✓ Added {foundGenes.Count}: {string.Join(", ", foundGenes)}");
            }
            if (notFoundGenes.Count > 0)
            {
                resultParts.Add($"✗ Not found in DB: {string.Join(", ", notFoundGenes)}");
            }

            _pasteResultText.Text = string.Join("\n", resultParts);
            _pasteResultText.Foreground = notFoundGenes.Count > 0 
                ? new SolidColorBrush(Color.FromRgb(180, 83, 9)) // amber warning
                : new SolidColorBrush(Color.FromRgb(22, 163, 74)); // green success
            _pasteResultText.Visibility = Visibility.Visible;

            // Refresh the markers list
            var searchText = _searchBox?.Text ?? "";
            if (searchText == "Search genes (e.g., GFAP, MAP2, IBA1...)")
                searchText = "";
            PopulateMarkersList(searchText);

            // Update count text and fire event
            RefreshMarkersCountText();

            // Fire event for each added marker
            if (foundGenes.Count > 0)
            {
                KeyMarkersChanged?.Invoke(this, new KeyMarkersChangedEventArgs
                {
                    CellType = _currentCellType,
                    Markers = new HashSet<string>(_keyMarkers[_currentCellType])
                });
            }

            // Clear the textbox
            _pasteTextBox.Text = placeholder;
            _pasteTextBox.Foreground = Brushes.Gray;
        }

        private void ClearAllMarkers_Click(object sender, MouseButtonEventArgs e)
        {
            if (_keyMarkers.ContainsKey(_currentCellType))
            {
                int count = _keyMarkers[_currentCellType].Count;
                _keyMarkers[_currentCellType].Clear();

                _pasteResultText.Text = $"Cleared {count} marker(s).";
                _pasteResultText.Foreground = new SolidColorBrush(Color.FromRgb(100, 116, 139)); // slate
                _pasteResultText.Visibility = Visibility.Visible;

                // Refresh UI
                var searchText = _searchBox?.Text ?? "";
                if (searchText == "Search genes (e.g., GFAP, MAP2, IBA1...)")
                    searchText = "";
                PopulateMarkersList(searchText);
                RefreshMarkersCountText();

                // Fire event
                KeyMarkersChanged?.Invoke(this, new KeyMarkersChangedEventArgs
                {
                    CellType = _currentCellType,
                    Markers = new HashSet<string>()
                });
            }
        }

        private void ReclassifyButton_Click(object sender, MouseButtonEventArgs e)
        {
            bool applyMarkers = _applyKeyMarkersCheckbox?.IsChecked ?? false;
            
            // Count total markers across all cell types
            int totalMarkers = _keyMarkers.Values.Sum(m => m.Count);
            
            // If checkbox is on but no markers defined, classify without markers
            if (applyMarkers && totalMarkers == 0)
                applyMarkers = false;

            if (applyMarkers)
            {
                _reclassifyStatusText.Text = $"Reclassifying with {totalMarkers} key marker(s)...";
            }
            else
            {
                _reclassifyStatusText.Text = "Reclassifying (baseline - no marker adjustments)...";
            }
            _reclassifyStatusText.Foreground = new SolidColorBrush(Color.FromRgb(22, 163, 74)); // green
            _reclassifyStatusText.Visibility = Visibility.Visible;

            // Fire event to trigger reclassification
            ReclassifyRequested?.Invoke(this, new ReclassifyRequestedEventArgs
            {
                ApplyKeyMarkers = applyMarkers,
                PriorWeights = GetPriorWeights()
            });
        }

        private void RefreshMarkersCountText()
        {
            // Find and update the count text in the panel
            foreach (var child in CellTypeDetailsPanel.Children)
            {
                if (child is Border b && b.Background is LinearGradientBrush)
                {
                    if (b.Child is StackPanel sp && sp.Children.Count > 1 && sp.Children[1] is TextBlock countText)
                    {
                        var selectedMarkers = _keyMarkers[_currentCellType];
                        countText.Text = selectedMarkers.Count == 0
                            ? "No markers selected - click genes below to add"
                            : $"{selectedMarkers.Count} marker(s) selected";
                        countText.FontStyle = selectedMarkers.Count == 0 ? FontStyles.Italic : FontStyles.Normal;
                    }
                    break;
                }
            }
        }

        private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            var searchText = _searchBox.Text;
            if (searchText == "Search genes (e.g., GFAP, MAP2, IBA1...)")
                searchText = "";
            
            PopulateMarkersList(searchText);
        }

        private void PopulateMarkersList(string searchFilter)
        {
            if (_markersListPanel == null || _currentProfile == null)
                return;

            _markersListPanel.Children.Clear();

            var selectedMarkers = _keyMarkers[_currentCellType];
            var allGenes = _currentProfile.MedianExpression
                .Where(g => g.Value > 0)
                .OrderByDescending(g => g.Value)
                .ToList();

            // Filter by search
            if (!string.IsNullOrWhiteSpace(searchFilter))
            {
                allGenes = allGenes
                    .Where(g => g.Key.IndexOf(searchFilter, StringComparison.OrdinalIgnoreCase) >= 0)
                    .ToList();
            }

            // Sort: selected markers first, then by expression
            var sortedGenes = allGenes
                .OrderByDescending(g => selectedMarkers.Contains(g.Key) ? 1 : 0)
                .ThenByDescending(g => g.Value)
                .Take(50) // Limit for performance
                .ToList();

            foreach (var gene in sortedGenes)
            {
                var isSelected = selectedMarkers.Contains(gene.Key);
                var geneRow = CreateGeneRow(gene.Key, gene.Value, isSelected);
                _markersListPanel.Children.Add(geneRow);
            }

            if (sortedGenes.Count == 0)
            {
                var noResultsText = new TextBlock
                {
                    Text = "No genes found matching your search",
                    FontStyle = FontStyles.Italic,
                    Foreground = Brushes.Gray,
                    Margin = new Thickness(10),
                    HorizontalAlignment = HorizontalAlignment.Center
                };
                _markersListPanel.Children.Add(noResultsText);
            }
        }

        private Border CreateGeneRow(string geneName, double medianExpression, bool isSelected)
        {
            var border = new Border
            {
                Background = isSelected 
                    ? new SolidColorBrush(Color.FromRgb(254, 249, 195)) // amber-100
                    : new SolidColorBrush(Colors.White),
                BorderBrush = isSelected
                    ? new SolidColorBrush(Color.FromRgb(251, 191, 36)) // amber-400
                    : new SolidColorBrush(Color.FromRgb(241, 245, 249)), // slate-100
                BorderThickness = new Thickness(0, 0, 0, 1),
                Padding = new Thickness(10, 8, 10, 8),
                Cursor = Cursors.Hand,
                Tag = geneName
            };

            // Hover effect
            border.MouseEnter += (s, e) =>
            {
                if (!isSelected)
                    border.Background = new SolidColorBrush(Color.FromRgb(248, 250, 252));
            };
            border.MouseLeave += (s, e) =>
            {
                if (!isSelected)
                    border.Background = new SolidColorBrush(Colors.White);
            };

            // Click to toggle
            border.MouseLeftButtonUp += GeneRow_Click;

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); // Star icon
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); // Gene name
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); // Specificity
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); // Expression

            // Star icon (filled if selected)
            var starText = new TextBlock
            {
                Text = isSelected ? "★" : "☆",
                FontSize = 14,
                Foreground = isSelected 
                    ? new SolidColorBrush(Color.FromRgb(245, 158, 11)) // amber-500
                    : new SolidColorBrush(Color.FromRgb(203, 213, 225)), // slate-300
                Margin = new Thickness(0, 0, 8, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(starText, 0);
            grid.Children.Add(starText);

            // Gene name
            var nameText = new TextBlock
            {
                Text = geneName,
                FontSize = 12,
                FontFamily = new FontFamily("Consolas"),
                FontWeight = isSelected ? FontWeights.SemiBold : FontWeights.Normal,
                Foreground = isSelected
                    ? new SolidColorBrush(Color.FromRgb(146, 64, 14)) // amber-800
                    : new SolidColorBrush(Colors.Black),
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(nameText, 1);
            grid.Children.Add(nameText);

            // Specificity indicator
            double specificity = _geneSpecificity.TryGetValue(geneName, out double spec) ? spec : 0;
            string specLabel = specificity > 1.5 ? "High" : (specificity > 0.5 ? "Med" : "Low");
            Color specColor = specificity > 1.5 
                ? Color.FromRgb(34, 197, 94)   // green-500
                : (specificity > 0.5 
                    ? Color.FromRgb(234, 179, 8)   // yellow-500
                    : Color.FromRgb(156, 163, 175)); // gray-400

            var specBorder = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(30, specColor.R, specColor.G, specColor.B)),
                BorderBrush = new SolidColorBrush(specColor),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(3),
                Padding = new Thickness(4, 1, 4, 1),
                Margin = new Thickness(0, 0, 10, 0),
                VerticalAlignment = VerticalAlignment.Center,
                ToolTip = $"Specificity: {specificity:F2}\nHigher = more unique to fewer cell types"
            };
            var specText = new TextBlock
            {
                Text = specLabel,
                FontSize = 9,
                Foreground = new SolidColorBrush(specColor),
                FontWeight = FontWeights.SemiBold
            };
            specBorder.Child = specText;
            Grid.SetColumn(specBorder, 2);
            grid.Children.Add(specBorder);

            // Expression value
            var exprText = new TextBlock
            {
                Text = medianExpression.ToString("F1"),
                FontSize = 11,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Color.FromRgb(16, 185, 129)), // emerald-500
                VerticalAlignment = VerticalAlignment.Center,
                MinWidth = 50,
                TextAlignment = TextAlignment.Right,
                ToolTip = $"Median expression: {medianExpression:F2}"
            };
            Grid.SetColumn(exprText, 3);
            grid.Children.Add(exprText);

            border.Child = grid;
            return border;
        }

        private void GeneRow_Click(object sender, MouseButtonEventArgs e)
        {
            if (sender is Border border && border.Tag is string geneName)
            {
                var selectedMarkers = _keyMarkers[_currentCellType];
                
                if (selectedMarkers.Contains(geneName))
                    selectedMarkers.Remove(geneName);
                else
                    selectedMarkers.Add(geneName);

                // Refresh the list
                var searchText = _searchBox?.Text ?? "";
                if (searchText == "Search genes (e.g., GFAP, MAP2, IBA1...)")
                    searchText = "";
                PopulateMarkersList(searchText);

                // Refresh the details panel to update the count
                if (_transcriptomicData.CellTypeMetadata.TryGetValue(_currentCellType, out var metadata))
                {
                    // Find and update the count text
                    foreach (var child in CellTypeDetailsPanel.Children)
                    {
                        if (child is Border b && b.Background is LinearGradientBrush)
                        {
                            // Found the markers section, update count text
                            if (b.Child is StackPanel sp && sp.Children.Count > 1 && sp.Children[1] is TextBlock countText)
                            {
                                countText.Text = selectedMarkers.Count == 0
                                    ? "No markers selected - click genes below to add"
                                    : $"{selectedMarkers.Count} marker(s) selected";
                                countText.FontStyle = selectedMarkers.Count == 0 ? FontStyles.Italic : FontStyles.Normal;
                            }
                            break;
                        }
                    }
                }

                // Raise event to notify listeners
                KeyMarkersChanged?.Invoke(this, new KeyMarkersChangedEventArgs
                {
                    CellType = _currentCellType,
                    Markers = new HashSet<string>(selectedMarkers)
                });
            }
        }

        private void ShowTopExpressedGenes(CellTypeProfile profile)
        {
            // Top expressed genes
            var topGenes = profile.MedianExpression
                .OrderByDescending(g => g.Value)
                .Take(30);

            var topGenesTitle = new TextBlock
            {
                Text = "Top 30 Expressed Genes",
                FontSize = 14,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 10, 0, 10)
            };
            CellTypeDetailsPanel.Children.Add(topGenesTitle);

            var genesStackPanel = new StackPanel();
            foreach (var gene in topGenes)
            {
                var geneBorder = new Border
                {
                    Background = new SolidColorBrush(Color.FromRgb(248, 250, 252)),
                    BorderBrush = new SolidColorBrush(Color.FromRgb(226, 232, 240)),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(4),
                    Padding = new Thickness(10, 8, 10, 8),
                    Margin = new Thickness(0, 2, 0, 2)
                };

                var geneGrid = new Grid();
                geneGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                geneGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                var geneNameText = new TextBlock
                {
                    Text = gene.Key,
                    FontSize = 12,
                    FontFamily = new FontFamily("Consolas"),
                    VerticalAlignment = VerticalAlignment.Center
                };
                Grid.SetColumn(geneNameText, 0);
                geneGrid.Children.Add(geneNameText);

                var expressionText = new TextBlock
                {
                    Text = gene.Value.ToString("F2"),
                    FontSize = 11,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = new SolidColorBrush(Color.FromRgb(16, 185, 129)),
                    VerticalAlignment = VerticalAlignment.Center
                };
                Grid.SetColumn(expressionText, 1);
                geneGrid.Children.Add(expressionText);

                geneBorder.Child = geneGrid;
                genesStackPanel.Children.Add(geneBorder);
            }

            CellTypeDetailsPanel.Children.Add(genesStackPanel);
        }

        private Border CreateStatBox(string label, string value, string colorHex)
        {
            var color = (Color)ColorConverter.ConvertFromString(colorHex);

            var border = new Border
            {
                Background = new SolidColorBrush(Colors.White),
                BorderBrush = new SolidColorBrush(Color.FromRgb(226, 232, 240)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(15),
                Margin = new Thickness(0, 0, 10, 10)
            };

            var stack = new StackPanel();

            var labelBlock = new TextBlock
            {
                Text = label,
                FontSize = 11,
                Foreground = new SolidColorBrush(Color.FromRgb(100, 116, 139))
            };
            stack.Children.Add(labelBlock);

            var valueBlock = new TextBlock
            {
                Text = value,
                FontSize = 20,
                FontWeight = FontWeights.Bold,
                Foreground = new SolidColorBrush(color)
            };
            stack.Children.Add(valueBlock);

            border.Child = stack;
            return border;
        }
    }

    public class KeyMarkersChangedEventArgs : EventArgs
    {
        public string CellType { get; set; }
        public HashSet<string> Markers { get; set; }
    }

    public class ReclassifyRequestedEventArgs : EventArgs
    {
        public bool ApplyKeyMarkers { get; set; }
        public Dictionary<string, double> PriorWeights { get; set; }
    }

    public class CellTypeExclusionsChangedEventArgs : EventArgs
    {
        public HashSet<string> ExcludedCellTypes { get; set; }
    }

    public class PriorWeightsChangedEventArgs : EventArgs
    {
        public string CellType { get; set; }
        public double Weight { get; set; }
    }
}