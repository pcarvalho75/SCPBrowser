// GOBrowserControl.xaml.cs
// Child control for browsing GO annotations
// Location: SCPBrowser/Controls/GOBrowserControl.xaml.cs

using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using SCPBrowser.GOTools;
using SCPBrowser.Services;

namespace SCPBrowser
{
    public partial class GOBrowserControl : UserControl
    {
        private ReferenceDataService _referenceService;
        private GoSlimDatabase _goSlimDatabase;
        private GoAnnotationDatabase _goAnnotations;

        public GOBrowserControl()
        {
            InitializeComponent();
            _referenceService = new ReferenceDataService();
            Console.WriteLine("GOBrowserControl initialized");
        }

        /// <summary>
        /// Loads GO annotation data from the database and populates the UI
        /// </summary>
        public async Task LoadDataAsync(string databasePath)
        {
            try
            {
                Console.WriteLine($"GOBrowserControl: Loading GO data from {databasePath}");

                // Load GO annotations
                (_goSlimDatabase, _goAnnotations) = await _referenceService.LoadGoAnnotationsAsync(databasePath);

                // Populate the UI
                PopulateGoAnnotationsUI();

                Console.WriteLine("GOBrowserControl: GO data loaded successfully");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"GOBrowserControl Error: {ex.Message}");
                throw; // Let the parent handle the error display
            }
        }

        private void PopulateGoAnnotationsUI()
        {
            // Update summary statistics
            TotalTermsText.Text = _goSlimDatabase.TotalTerms.ToString("N0");
            TotalProteinsText.Text = _goAnnotations.TotalProteins.ToString("N0");
            TotalAnnotationsText.Text = _goAnnotations.TotalAnnotations.ToString("N0");
            GoSlimTermsText.Text = _goAnnotations.GoTermToProteins.Count.ToString("N0");

            // Build GO tree
            BuildGoTree();
        }

        private void BuildGoTree()
        {
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

            // Find children recursively
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

            panel.Children.Add(idText);
            panel.Children.Add(nameText);

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
                panel.Children.Add(countText);
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
                ShowEmptyDetailsMessage();
            }
        }

        private void ShowEmptyDetailsMessage()
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

        private void ShowGoTermDetails(GoTerm term)
        {
            GoTermDetailsPanel.Children.Clear();

            // Term name
            var nameBlock = new TextBlock
            {
                Text = term.Name,
                FontSize = 16,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 0, 0, 10)
            };
            GoTermDetailsPanel.Children.Add(nameBlock);

            // GO ID
            AddDetailSection("GO ID", term.Id);

            // Namespace
            AddDetailSection("Namespace", term.Namespace);

            // Definition
            if (!string.IsNullOrEmpty(term.Definition))
            {
                AddDetailSection("Definition", term.Definition);
            }

            // Annotated proteins
            if (_goAnnotations.GoTermToProteins.TryGetValue(term.Id, out var proteins))
            {
                var proteinList = proteins.Take(50).ToList();
                var proteinText = string.Join(", ", proteinList);

                if (proteins.Count > 50)
                {
                    proteinText += $"\n\n... and {proteins.Count - 50} more proteins";
                }

                AddDetailSection($"Annotated Proteins ({proteins.Count})", proteinText);

                if (proteins.Count > 50)
                {
                    var moreText = new TextBlock
                    {
                        Text = $"Showing first 50 of {proteins.Count} total proteins",
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
    }
}