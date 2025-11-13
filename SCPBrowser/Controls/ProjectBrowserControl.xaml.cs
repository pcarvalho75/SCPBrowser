using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using Microsoft.Win32;
using System.Collections.Generic;
using SCPBrowser.GOTools;
using SCPBrowser.Services;

namespace SCPBrowser
{
    public partial class ProjectBrowserControl : UserControl
    {
        private ReferenceDataService _referenceService;
        private GoSlimDatabase _goSlimDatabase;
        private GoAnnotationDatabase _goAnnotations;
        private TranscriptomicDatabase _transcriptomicData;

        public ProjectBrowserControl()
        {
            InitializeComponent();
            _referenceService = new ReferenceDataService();
            Console.WriteLine("ProjectBrowserControl initialized");
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            Console.WriteLine("Closing Project Browser...");

            // Simply hide this control - no need to reference DBBrowserOverlay
            this.Visibility = Visibility.Collapsed;
        }

        private async void BrowseDatabase_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFileDialog
            {
                Filter = "SQLite Database (*.db)|*.db|All files (*.*)|*.*",
                Title = "Select Reference Database"
            };

            if (dialog.ShowDialog() != true)
                return;

            DatabasePathTextBox.Text = dialog.FileName;

            try
            {
                Console.WriteLine($"Loading database: {dialog.FileName}");

                // Show loading overlay
                var mainWindow = Window.GetWindow(this) as MainWindow;
                if (mainWindow != null)
                {
                    mainWindow.LoadingOverlay.SetMessage("Loading Database");
                    mainWindow.LoadingOverlay.SetProgress("Reading GO annotations...");
                    mainWindow.LoadingOverlay.Show();
                }

                // Load GO annotations
                (_goSlimDatabase, _goAnnotations) = await _referenceService.LoadGoAnnotationsAsync(dialog.FileName);

                if (mainWindow != null)
                {
                    mainWindow.LoadingOverlay.SetProgress("Reading transcriptomic data...");
                }

                // Load transcriptomic data
                _transcriptomicData = await _referenceService.LoadTranscriptomicDataAsync(dialog.FileName);

                if (mainWindow != null)
                {
                    mainWindow.LoadingOverlay.Hide();
                }

                // Update UI
                EmptyStatePanel.Visibility = Visibility.Collapsed;
                DataTabControl.Visibility = Visibility.Visible;

                PopulateGoAnnotationsTab();
                PopulateTranscriptomicTab();
                PopulateDatabaseInfoTab(dialog.FileName);

                Console.WriteLine("Database statistics loaded successfully");
            }
            catch (Exception ex)
            {
                var mainWindow = Window.GetWindow(this) as MainWindow;
                if (mainWindow != null)
                {
                    mainWindow.LoadingOverlay.Hide();
                }

                Console.WriteLine($"Error loading database: {ex.Message}");
                MessageBox.Show(
                    $"Error loading database:\n\n{ex.Message}",
                    "Database Error",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);

                EmptyStatePanel.Visibility = Visibility.Visible;
                DataTabControl.Visibility = Visibility.Collapsed;
            }
        }

        private void PopulateGoAnnotationsTab()
        {
            // Update summary statistics
            TotalTermsText.Text = _goSlimDatabase.TotalTerms.ToString("N0");
            TotalProteinsText.Text = _goAnnotations.TotalProteins.ToString("N0");
            TotalAnnotationsText.Text = _goAnnotations.TotalAnnotations.ToString("N0");
            GoSlimTermsText.Text = _goAnnotations.GoTermToProteins.Count.ToString("N0");

            // Build GO tree
            GoTreeView.Items.Clear();

            // Group by namespace
            var namespaces = _goSlimDatabase.Terms.Values
                .GroupBy(t => t.Namespace)
                .OrderBy(g => g.Key);

            foreach (var namespaceGroup in namespaces)
            {
                var namespaceNode = new TreeViewItem
                {
                    Header = CreateNamespaceHeader(namespaceGroup.Key, namespaceGroup.Count()),
                    Tag = namespaceGroup.Key,
                    FontWeight = FontWeights.SemiBold
                };

                // Find root terms (terms with no parents in this namespace)
                var rootTerms = namespaceGroup.Where(t =>
                    t.ParentIds == null ||
                    t.ParentIds.Count == 0 ||
                    !t.ParentIds.Any(p => _goSlimDatabase.Terms.ContainsKey(p) &&
                                       _goSlimDatabase.Terms[p].Namespace == namespaceGroup.Key))
                    .OrderBy(t => t.Name);

                foreach (var term in rootTerms)
                {
                    var termNode = CreateGoTermNode(term);
                    namespaceNode.Items.Add(termNode);
                }

                GoTreeView.Items.Add(namespaceNode);
            }
        }

        private StackPanel CreateNamespaceHeader(string namespaceName, int count)
        {
            var panel = new StackPanel { Orientation = Orientation.Horizontal };

            var icon = new TextBlock
            {
                Text = GetNamespaceIcon(namespaceName),
                FontSize = 14,
                Margin = new Thickness(0, 0, 8, 0)
            };

            var name = new TextBlock
            {
                Text = namespaceName,
                FontSize = 13
            };

            var countText = new TextBlock
            {
                Text = $" ({count} terms)",
                FontSize = 11,
                Foreground = new SolidColorBrush(Color.FromRgb(100, 116, 139)),
                Margin = new Thickness(5, 0, 0, 0)
            };

            panel.Children.Add(icon);
            panel.Children.Add(name);
            panel.Children.Add(countText);

            return panel;
        }

        private string GetNamespaceIcon(string namespaceName)
        {
            return namespaceName switch
            {
                "biological_process" => "🔬",
                "molecular_function" => "⚙️",
                "cellular_component" => "🧬",
                _ => "📁"
            };
        }

        private TreeViewItem CreateGoTermNode(GoTerm term)
        {
            var node = new TreeViewItem
            {
                Header = CreateGoTermHeader(term),
                Tag = term
            };

            // Find children
            var children = _goSlimDatabase.Terms.Values
                .Where(t => t.ParentIds != null && t.ParentIds.Contains(term.Id) && t.Namespace == term.Namespace)
                .OrderBy(t => t.Name);

            foreach (var child in children)
            {
                node.Items.Add(CreateGoTermNode(child));
            }

            return node;
        }

        private StackPanel CreateGoTermHeader(GoTerm term)
        {
            var panel = new StackPanel { Orientation = Orientation.Horizontal };

            var idText = new TextBlock
            {
                Text = term.Id,
                FontSize = 11,
                Foreground = new SolidColorBrush(Color.FromRgb(37, 99, 235)),
                Margin = new Thickness(0, 0, 8, 0),
                FontFamily = new FontFamily("Consolas")
            };

            var nameText = new TextBlock
            {
                Text = term.Name,
                FontSize = 12
            };

            // Add protein count if available
            if (_goAnnotations.GoTermToProteins.TryGetValue(term.Id, out var proteins))
            {
                var countText = new TextBlock
                {
                    Text = $" [{proteins.Count} proteins]",
                    FontSize = 10,
                    Foreground = new SolidColorBrush(Color.FromRgb(16, 185, 129)),
                    Margin = new Thickness(5, 0, 0, 0),
                    FontWeight = FontWeights.SemiBold
                };
                panel.Children.Add(idText);
                panel.Children.Add(nameText);
                panel.Children.Add(countText);
            }
            else
            {
                panel.Children.Add(idText);
                panel.Children.Add(nameText);
            }

            return panel;
        }

        private void GoTreeView_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
        {
            var selectedItem = e.NewValue as TreeViewItem;
            if (selectedItem?.Tag is GoTerm term)
            {
                ShowGoTermDetails(term);
            }
            else
            {
                GoTermDetailsPanel.Children.Clear();
                GoTermDetailsPanel.Children.Add(new TextBlock
                {
                    Text = "Select a GO term to view details",
                    FontSize = 13,
                    Foreground = new SolidColorBrush(Color.FromRgb(100, 116, 139)),
                    FontStyle = FontStyles.Italic,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 50, 0, 0)
                });
            }
        }

        private void ShowGoTermDetails(GoTerm term)
        {
            GoTermDetailsPanel.Children.Clear();

            // Term name
            var nameBlock = new TextBlock
            {
                Text = term.Name,
                FontSize = 16,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 0, 0, 5)
            };
            GoTermDetailsPanel.Children.Add(nameBlock);

            // Term ID
            var idBlock = new TextBlock
            {
                Text = term.Id,
                FontSize = 12,
                Foreground = new SolidColorBrush(Color.FromRgb(37, 99, 235)),
                FontFamily = new FontFamily("Consolas"),
                Margin = new Thickness(0, 0, 0, 15)
            };
            GoTermDetailsPanel.Children.Add(idBlock);

            // Definition
            if (!string.IsNullOrEmpty(term.Definition))
            {
                AddDetailSection("Definition", term.Definition);
            }

            // Namespace
            AddDetailSection("Namespace", term.Namespace);

            // Parents
            if (term.ParentIds != null && term.ParentIds.Count > 0)
            {
                var parentsList = new StringBuilder();
                foreach (var parentId in term.ParentIds)
                {
                    if (_goSlimDatabase.Terms.TryGetValue(parentId, out var parent))
                    {
                        parentsList.AppendLine($"• {parent.Id}: {parent.Name}");
                    }
                }
                if (parentsList.Length > 0)
                {
                    AddDetailSection("Parent Terms", parentsList.ToString().TrimEnd());
                }
            }

            // Annotated proteins
            if (_goAnnotations.GoTermToProteins.TryGetValue(term.Id, out var proteins))
            {
                AddDetailSection("Annotated Proteins", $"{proteins.Count:N0} proteins");

                // Show top 20 proteins
                var proteinList = new TextBlock
                {
                    Text = string.Join("\n", proteins.Take(20).Select(p => $"• {p}")),
                    FontSize = 11,
                    FontFamily = new FontFamily("Consolas"),
                    Margin = new Thickness(10, 5, 0, 10),
                    Foreground = new SolidColorBrush(Color.FromRgb(51, 65, 85))
                };
                GoTermDetailsPanel.Children.Add(proteinList);

                if (proteins.Count > 20)
                {
                    var moreText = new TextBlock
                    {
                        Text = $"... and {proteins.Count - 20} more",
                        FontSize = 11,
                        FontStyle = FontStyles.Italic,
                        Foreground = new SolidColorBrush(Color.FromRgb(100, 116, 139)),
                        Margin = new Thickness(10, 0, 0, 0)
                    };
                    GoTermDetailsPanel.Children.Add(moreText);
                }
            }
            else
            {
                AddDetailSection("Annotated Proteins", "No proteins annotated with this term");
            }
        }

        private void AddDetailSection(string title, string content)
        {
            var titleBlock = new TextBlock
            {
                Text = title,
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 10, 0, 5)
            };
            GoTermDetailsPanel.Children.Add(titleBlock);

            var contentBlock = new TextBlock
            {
                Text = content,
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                Foreground = new SolidColorBrush(Color.FromRgb(51, 65, 85)),
                Margin = new Thickness(0, 0, 0, 5)
            };
            GoTermDetailsPanel.Children.Add(contentBlock);
        }

        private void PopulateTranscriptomicTab()
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

            // Get profile
            if (_transcriptomicData.CellTypeProfiles.TryGetValue(metadata.CellType, out var profile))
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

        private void PopulateDatabaseInfoTab(string databasePath)
        {
            DatabaseInfoPanel.Children.Clear();

            var fileInfo = new FileInfo(databasePath);

            var titleBlock = new TextBlock
            {
                Text = "Database Information",
                FontSize = 16,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 0, 0, 15)
            };
            DatabaseInfoPanel.Children.Add(titleBlock);

            AddInfoRow("File Path:", databasePath);
            AddInfoRow("File Name:", fileInfo.Name);
            AddInfoRow("File Size:", $"{fileInfo.Length / (1024.0 * 1024.0):F2} MB");
            AddInfoRow("Created:", fileInfo.CreationTime.ToString("yyyy-MM-dd HH:mm:ss"));
            AddInfoRow("Modified:", fileInfo.LastWriteTime.ToString("yyyy-MM-dd HH:mm:ss"));

            // Separator
            var separator = new Border
            {
                Height = 1,
                Background = new SolidColorBrush(Color.FromRgb(226, 232, 240)),
                Margin = new Thickness(0, 15, 0, 15)
            };
            DatabaseInfoPanel.Children.Add(separator);

            // Summary
            var summaryTitle = new TextBlock
            {
                Text = "Content Summary",
                FontSize = 14,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 0, 0, 10)
            };
            DatabaseInfoPanel.Children.Add(summaryTitle);

            AddInfoRow("GO Terms:", _goSlimDatabase?.TotalTerms.ToString("N0") ?? "0");
            AddInfoRow("Annotated Proteins:", _goAnnotations?.TotalProteins.ToString("N0") ?? "0");
            AddInfoRow("Total Annotations:", _goAnnotations?.TotalAnnotations.ToString("N0") ?? "0");
            AddInfoRow("Cell Types:", _transcriptomicData?.TotalCellTypes.ToString("N0") ?? "0");
            AddInfoRow("Total Cells:", _transcriptomicData?.TotalCells.ToString("N0") ?? "0");
            AddInfoRow("Total Genes:", _transcriptomicData?.TotalGenes.ToString("N0") ?? "0");
        }

        private void AddInfoRow(string label, string value)
        {
            var grid = new Grid { Margin = new Thickness(0, 0, 0, 8) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var labelBlock = new TextBlock
            {
                Text = label,
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Color.FromRgb(100, 116, 139))
            };
            Grid.SetColumn(labelBlock, 0);
            grid.Children.Add(labelBlock);

            var valueBlock = new TextBlock
            {
                Text = value,
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                Foreground = new SolidColorBrush(Color.FromRgb(51, 65, 85))
            };
            Grid.SetColumn(valueBlock, 1);
            grid.Children.Add(valueBlock);

            DatabaseInfoPanel.Children.Add(grid);
        }
    }
}