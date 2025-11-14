// OmicBrowserControl.xaml.cs
// Child control for browsing transcriptomic data (cell type profiles)
// Location: SCPBrowser/Controls/OmicBrowserControl.xaml.cs

using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using SCPBrowser.Services;

namespace SCPBrowser
{
    public partial class OmicBrowserControl : UserControl
    {
        private ReferenceDataService _referenceService;
        private TranscriptomicDatabase _transcriptomicData;

        public OmicBrowserControl()
        {
            InitializeComponent();
            _referenceService = new ReferenceDataService();
            Console.WriteLine("OmicBrowserControl initialized");
        }

        /// <summary>
        /// Loads transcriptomic data from the database and populates the UI
        /// </summary>
        public async Task LoadDataAsync(string databasePath)
        {
            try
            {
                Console.WriteLine($"OmicBrowserControl: Loading transcriptomic data from {databasePath}");

                // Load transcriptomic data
                _transcriptomicData = await _referenceService.LoadTranscriptomicDataAsync(databasePath);

                // Populate the UI
                PopulateTranscriptomicUI();

                Console.WriteLine("OmicBrowserControl: Transcriptomic data loaded successfully");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"OmicBrowserControl Error: {ex.Message}");
                throw; // Let the parent handle the error display
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
            var cellTypeList = _transcriptomicData.CellTypeMetadata.Values
                .OrderBy(m => m.CellType)
                .ToList();

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
            CellTypeDetailsPanel.Children.Clear();

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

            // Get profile and show top expressed genes
            if (_transcriptomicData.CellTypeProfiles.TryGetValue(metadata.CellType, out var profile))
            {
                ShowTopExpressedGenes(profile);
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
}