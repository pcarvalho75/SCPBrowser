using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using SCPBrowser.Services;

namespace SCPBrowser.Controls
{
    public partial class ProteinCoverageControl : UserControl
    {
        private ProteinCoverageService _coverageService;
        private UniProtService _uniProtService;
        private ProteinCoverageResult _currentResult;
        private List<ProteinFeature> _currentFeatures;
        private Dictionary<string, FastaParserService.ProteinAnnotation> _annotations;
        private bool _isLoading;

        // Data sources (set by parent)
        private string _fastaPath;
        private List<string> _parquetPaths;
        private Func<HashSet<string>> _getSelectedRuns;
        private ProteomicsData _proteomicsData;

        // Colors
        private static readonly Color CoveredColor = Color.FromRgb(21, 101, 192);     // #1565C0
        private static readonly Color UncoveredColor = Color.FromRgb(226, 232, 240);   // #E2E8F0
        private static readonly Color IntensityFill = Color.FromRgb(21, 101, 192);
        private static readonly Color IntensityStroke = Color.FromRgb(13, 71, 161);

        // Domain type colors
        private static readonly Dictionary<string, Color> DomainColors = new()
        {
            { "Domain", Color.FromRgb(239, 68, 68) },
            { "Region", Color.FromRgb(249, 115, 22) },
            { "Transmembrane", Color.FromRgb(234, 179, 8) },
            { "Signal peptide", Color.FromRgb(34, 197, 94) },
            { "Transit peptide", Color.FromRgb(16, 185, 129) },
            { "Active site", Color.FromRgb(168, 85, 247) },
            { "Binding site", Color.FromRgb(236, 72, 153) },
            { "Disulfide bond", Color.FromRgb(244, 63, 94) },
            { "Modified residue", Color.FromRgb(99, 102, 241) },
            { "Chain", Color.FromRgb(107, 114, 128) },
            { "Propeptide", Color.FromRgb(156, 163, 175) },
            { "Repeat", Color.FromRgb(245, 158, 11) },
            { "Motif", Color.FromRgb(14, 165, 233) },
            { "Compositional bias", Color.FromRgb(139, 92, 246) },
            { "Topological domain", Color.FromRgb(20, 184, 166) },
            { "Site", Color.FromRgb(251, 146, 60) }
        };

        // Cached brushes for sequence text rendering
        private static readonly SolidColorBrush CoveredFgBrush = new SolidColorBrush(Colors.White);
        private static readonly SolidColorBrush CoveredBgBrush = new SolidColorBrush(CoveredColor);
        private static readonly SolidColorBrush UncoveredFgBrush = new SolidColorBrush(Color.FromRgb(148, 163, 184));
        private static readonly SolidColorBrush LineNumberBrush = new SolidColorBrush(Color.FromRgb(148, 163, 184));

        static ProteinCoverageControl()
        {
            CoveredFgBrush.Freeze();
            CoveredBgBrush.Freeze();
            UncoveredFgBrush.Freeze();
            LineNumberBrush.Freeze();
        }

        public ProteinCoverageControl()
        {
            InitializeComponent();
            _coverageService = new ProteinCoverageService();
            _uniProtService = new UniProtService();

            SizeChanged += (s, e) =>
            {
                if (_currentResult != null && ActualWidth > 0)
                    RedrawAll();
            };
        }

        /// <summary>
        /// Initializes the control with data sources needed for coverage computation.
        /// </summary>
        public void Initialize(
            string fastaPath,
            List<string> parquetPaths,
            ProteomicsData proteomicsData,
            Dictionary<string, FastaParserService.ProteinAnnotation> annotations,
            Func<HashSet<string>> getSelectedRuns)
        {
            _fastaPath = fastaPath;
            _parquetPaths = parquetPaths;
            _proteomicsData = proteomicsData;
            _annotations = annotations;
            _getSelectedRuns = getSelectedRuns;

            PopulateProteinList();
        }

        /// <summary>
        /// Populates the protein combo box with proteins present in the selected runs.
        /// </summary>
        public void PopulateProteinList()
        {
            ProteinComboBox.Items.Clear();

            if (_proteomicsData?.ProteinQuantMatrix == null)
                return;

            var selectedRuns = _getSelectedRuns?.Invoke();

            var proteins = _proteomicsData.ProteinQuantMatrix.Keys
                .Where(pg =>
                {
                    if (selectedRuns == null || selectedRuns.Count == 0)
                        return true;

                    var runAbundances = _proteomicsData.ProteinQuantMatrix[pg];
                    return selectedRuns.Any(r => runAbundances.ContainsKey(r) && runAbundances[r] > 0);
                })
                .OrderBy(pg => pg)
                .ToList();

            foreach (var protein in proteins)
            {
                string display = protein;
                if (_annotations != null)
                {
                    var acc = _coverageService.GetBestAccession(protein, _annotations);
                    if (acc != null && _annotations.TryGetValue(acc, out var ann))
                    {
                        string gene = !string.IsNullOrEmpty(ann.GeneName) ? $" [{ann.GeneName}]" : "";
                        string name = !string.IsNullOrEmpty(ann.ProteinName) 
                            ? (ann.ProteinName.Length > 40 ? ann.ProteinName.Substring(0, 37) + "..." : ann.ProteinName) 
                            : "";
                        display = $"{protein}{gene} {name}".Trim();
                    }
                }

                var item = new ComboBoxItem { Content = display, Tag = protein };
                ProteinComboBox.Items.Add(item);
            }

            StatsText.Text = $"{proteins.Count} proteins available";
        }

        /// <summary>
        /// Refreshes the protein list when selection changes in the Explorer.
        /// </summary>
        public void RefreshForSelection()
        {
            PopulateProteinList();
        }

        /// <summary>
        /// Updates the proteomics data reference when filters change.
        /// </summary>
        public void UpdateProteomicsData(ProteomicsData data)
        {
            if (data != null)
                _proteomicsData = data;
        }

        private void ProteinComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            // No auto-load on selection change, user clicks Load
        }

        private void ProteinComboBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
                LoadCoverage();
        }

        private void LoadButton_Click(object sender, RoutedEventArgs e)
        {
            LoadCoverage();
        }

        private async void LoadCoverage()
        {
            if (_isLoading)
                return;

            string proteinGroup = GetSelectedProteinGroup();
            if (string.IsNullOrEmpty(proteinGroup))
                return;

            if (string.IsNullOrEmpty(_fastaPath) || _parquetPaths == null || _parquetPaths.Count == 0)
            {
                ProteinInfoText.Text = "FASTA or parquet files not available.";
                return;
            }

            string accession = _coverageService.GetBestAccession(proteinGroup, _annotations);
            if (string.IsNullOrEmpty(accession))
            {
                ProteinInfoText.Text = "Could not resolve accession for this protein group.";
                return;
            }

            _isLoading = true;
            LoadButton.IsEnabled = false;
            ShowLoading("Computing coverage...");

            try
            {
                var selectedRuns = _getSelectedRuns?.Invoke();

                var result = await Task.Run(() =>
                    _coverageService.ComputeCoverageAsync(_fastaPath, _parquetPaths, proteinGroup, accession, selectedRuns));

                if (result == null)
                {
                    ProteinInfoText.Text = "Protein sequence not found in FASTA.";
                    return;
                }

                _currentResult = result;
                _currentFeatures = null;

                // Update info text
                string annInfo = "";
                if (_annotations != null && _annotations.TryGetValue(accession, out var ann))
                {
                    var parts = new List<string>();
                    if (!string.IsNullOrEmpty(ann.ProteinName)) parts.Add(ann.ProteinName);
                    if (!string.IsNullOrEmpty(ann.GeneName)) parts.Add($"GN={ann.GeneName}");
                    if (!string.IsNullOrEmpty(ann.Organism)) parts.Add(ann.Organism);
                    annInfo = string.Join(" | ", parts);
                }
                ProteinInfoText.Text = annInfo;

                string runInfo = selectedRuns != null ? $" from {selectedRuns.Count} cells" : " from all cells";
                StatsText.Text = $"{result.CoveragePercent:F1}% coverage | {result.UniquePeptideCount} peptides | {result.SequenceLength} aa{runInfo}";

                // Draw everything
                PlaceholderText.Visibility = Visibility.Collapsed;
                CoveragePanel.Visibility = Visibility.Visible;
                RedrawAll();

                // Fetch UniProt features in background
                _ = FetchDomainFeaturesAsync(accession);
            }
            catch (Exception ex)
            {
                ProteinInfoText.Text = $"Error: {ex.Message}";
            }
            finally
            {
                HideLoading();
                LoadButton.IsEnabled = true;
                _isLoading = false;
            }
        }

        private string GetSelectedProteinGroup()
        {
            if (ProteinComboBox.SelectedItem is ComboBoxItem item)
                return item.Tag?.ToString();

            // If user typed text, try to match
            string text = ProteinComboBox.Text?.Trim();
            if (string.IsNullOrEmpty(text))
                return null;

            // Look for exact match in items
            foreach (ComboBoxItem ci in ProteinComboBox.Items)
            {
                string pg = ci.Tag?.ToString();
                if (string.Equals(pg, text, StringComparison.OrdinalIgnoreCase))
                    return pg;
                if (ci.Content?.ToString()?.StartsWith(text, StringComparison.OrdinalIgnoreCase) == true)
                    return pg;
            }

            // Try the text directly as a protein group
            if (_proteomicsData?.ProteinQuantMatrix?.ContainsKey(text) == true)
                return text;

            return null;
        }

        private async Task FetchDomainFeaturesAsync(string accession)
        {
            try
            {
                Dispatcher.Invoke(() =>
                {
                    DomainPanel.Visibility = Visibility.Visible;
                    DomainStatusText.Text = "(loading from UniProt...)";
                });

                var entry = await _uniProtService.GetProteinInfoAsync(accession);

                if (entry?.Features != null && entry.Features.Count > 0)
                {
                    _currentFeatures = entry.Features;
                    Dispatcher.Invoke(() =>
                    {
                        DomainStatusText.Text = $"({_currentFeatures.Count} features)";
                        DrawDomains();
                    });
                }
                else
                {
                    Dispatcher.Invoke(() =>
                    {
                        DomainStatusText.Text = "(no features found)";
                        DomainCanvas.Children.Clear();
                    });
                }
            }
            catch
            {
                Dispatcher.Invoke(() =>
                {
                    DomainStatusText.Text = "(fetch failed)";
                });
            }
        }

        private void RedrawAll()
        {
            if (_currentResult == null || _currentResult.SequenceLength == 0)
                return;

            DrawCoverageBar();
            DrawPeptideBars();
            DrawIntensityProfile();
            DrawSequenceText();

            if (_currentFeatures != null)
                DrawDomains();
        }

        // ==================== DRAWING METHODS ====================

        private void DrawCoverageBar()
        {
            CoverageBarCanvas.Children.Clear();
            var result = _currentResult;
            if (result == null) return;

            double canvasWidth = CoverageBarCanvas.ActualWidth;
            double canvasHeight = CoverageBarCanvas.ActualHeight;
            if (canvasWidth <= 0) return;

            double pixelsPerResidue = canvasWidth / result.SequenceLength;

            // Draw uncovered background
            var bgRect = new Rectangle
            {
                Width = canvasWidth,
                Height = canvasHeight - 4,
                Fill = new SolidColorBrush(UncoveredColor),
                RadiusX = 2,
                RadiusY = 2
            };
            Canvas.SetTop(bgRect, 2);
            CoverageBarCanvas.Children.Add(bgRect);

            // Draw covered regions as merged segments
            int i = 0;
            while (i < result.SequenceLength)
            {
                if (result.CoveragePerResidue[i] > 0)
                {
                    int start = i;
                    int maxDepth = 0;
                    while (i < result.SequenceLength && result.CoveragePerResidue[i] > 0)
                    {
                        if (result.CoveragePerResidue[i] > maxDepth)
                            maxDepth = result.CoveragePerResidue[i];
                        i++;
                    }

                    double x = start * pixelsPerResidue;
                    double w = Math.Max(1, (i - start) * pixelsPerResidue);

                    // Vary opacity by depth
                    double opacity = Math.Min(1.0, 0.4 + maxDepth * 0.15);

                    var rect = new Rectangle
                    {
                        Width = w,
                        Height = canvasHeight - 4,
                        Fill = new SolidColorBrush(CoveredColor) { Opacity = opacity }
                    };
                    Canvas.SetLeft(rect, x);
                    Canvas.SetTop(rect, 2);
                    CoverageBarCanvas.Children.Add(rect);
                }
                else
                {
                    i++;
                }
            }

            // Draw tick marks every 100 residues
            for (int t = 100; t < result.SequenceLength; t += 100)
            {
                double x = t * pixelsPerResidue;
                var tick = new Line
                {
                    X1 = x, Y1 = 0,
                    X2 = x, Y2 = 4,
                    Stroke = Brushes.Gray,
                    StrokeThickness = 0.5
                };
                CoverageBarCanvas.Children.Add(tick);
            }
        }

        private void DrawPeptideBars()
        {
            PeptideBarCanvas.Children.Clear();
            var result = _currentResult;
            if (result == null || result.MappedPeptides.Count == 0) return;

            double canvasWidth = PeptideBarCanvas.ActualWidth;
            if (canvasWidth <= 0) return;

            double pixelsPerResidue = canvasWidth / result.SequenceLength;

            // Compute max intensity for color scaling
            double maxIntensity = result.MappedPeptides.Max(p => p.SummedIntensity);
            if (maxIntensity <= 0) maxIntensity = 1;
            double logMax = Math.Log10(maxIntensity + 1);

            // Layout peptides in rows to avoid overlap
            var rows = LayoutPeptideRows(result.MappedPeptides);

            double barHeight = 10;
            double rowSpacing = 2;
            double totalHeight = rows.Count * (barHeight + rowSpacing);

            PeptideBarCanvas.Height = Math.Max(60, Math.Min(300, totalHeight + 10));

            for (int rowIdx = 0; rowIdx < rows.Count; rowIdx++)
            {
                double y = rowIdx * (barHeight + rowSpacing) + 2;

                foreach (var peptide in rows[rowIdx])
                {
                    double x = peptide.StartPosition * pixelsPerResidue;
                    double w = Math.Max(2, (peptide.EndPosition - peptide.StartPosition + 1) * pixelsPerResidue);

                    // Color by intensity (log scale)
                    double logInt = Math.Log10(peptide.SummedIntensity + 1);
                    double fraction = logMax > 0 ? logInt / logMax : 0.5;
                    byte alpha = (byte)(100 + (int)(155 * fraction));

                    var rect = new Rectangle
                    {
                        Width = w,
                        Height = barHeight,
                        Fill = new SolidColorBrush(Color.FromArgb(alpha, CoveredColor.R, CoveredColor.G, CoveredColor.B)),
                        Stroke = new SolidColorBrush(IntensityStroke) { Opacity = 0.3 },
                        StrokeThickness = 0.5,
                        RadiusX = 1,
                        RadiusY = 1,
                        ToolTip = $"{peptide.Sequence}\nPos: {peptide.StartPosition + 1}-{peptide.EndPosition + 1}\nIntensity: {peptide.SummedIntensity:E2}\nCells: {peptide.CellCount}"
                    };
                    Canvas.SetLeft(rect, x);
                    Canvas.SetTop(rect, y);
                    PeptideBarCanvas.Children.Add(rect);
                }
            }
        }

        private List<List<MappedPeptide>> LayoutPeptideRows(List<MappedPeptide> peptides)
        {
            var rows = new List<List<MappedPeptide>>();
            var rowEnds = new List<int>(); // Track rightmost end position per row

            foreach (var pep in peptides.OrderBy(p => p.StartPosition))
            {
                bool placed = false;
                for (int r = 0; r < rows.Count; r++)
                {
                    if (pep.StartPosition > rowEnds[r] + 1)
                    {
                        rows[r].Add(pep);
                        rowEnds[r] = pep.EndPosition;
                        placed = true;
                        break;
                    }
                }

                if (!placed)
                {
                    rows.Add(new List<MappedPeptide> { pep });
                    rowEnds.Add(pep.EndPosition);
                }
            }

            return rows;
        }

        private void DrawIntensityProfile()
        {
            IntensityCanvas.Children.Clear();
            var result = _currentResult;
            if (result == null) return;

            double canvasWidth = IntensityCanvas.ActualWidth;
            double canvasHeight = IntensityCanvas.ActualHeight;
            if (canvasWidth <= 0 || canvasHeight <= 0) return;

            double pixelsPerResidue = canvasWidth / result.SequenceLength;
            double maxIntensity = result.IntensityPerResidue.Max();
            if (maxIntensity <= 0) return;

            // Use log scale for intensity
            double logMax = Math.Log10(maxIntensity + 1);
            double margin = 4;
            double plotHeight = canvasHeight - margin * 2;

            // Build path geometry for filled area
            var geometry = new StreamGeometry();
            using (var ctx = geometry.Open())
            {
                ctx.BeginFigure(new Point(0, canvasHeight - margin), true, true);

                for (int i = 0; i < result.SequenceLength; i++)
                {
                    double x = i * pixelsPerResidue;
                    double intensity = result.IntensityPerResidue[i];
                    double logVal = intensity > 0 ? Math.Log10(intensity + 1) : 0;
                    double y = canvasHeight - margin - (logVal / logMax * plotHeight);

                    ctx.LineTo(new Point(x, y), true, true);
                }

                // Close back to baseline
                ctx.LineTo(new Point(canvasWidth, canvasHeight - margin), true, true);
            }
            geometry.Freeze();

            var path = new Path
            {
                Data = geometry,
                Fill = new SolidColorBrush(IntensityFill) { Opacity = 0.3 },
                Stroke = new SolidColorBrush(IntensityStroke),
                StrokeThickness = 1
            };
            IntensityCanvas.Children.Add(path);

            // Y-axis label
            var label = new TextBlock
            {
                Text = $"Max: {maxIntensity:E1}",
                FontSize = 9,
                Foreground = UncoveredFgBrush
            };
            Canvas.SetLeft(label, canvasWidth - 90);
            Canvas.SetTop(label, 2);
            IntensityCanvas.Children.Add(label);
        }

        private void DrawDomains()
        {
            DomainCanvas.Children.Clear();
            if (_currentResult == null || _currentFeatures == null || _currentFeatures.Count == 0)
                return;

            double canvasWidth = DomainCanvas.ActualWidth;
            if (canvasWidth <= 0) return;

            double pixelsPerResidue = canvasWidth / _currentResult.SequenceLength;

            // Separate range features from point features
            var rangeFeatures = _currentFeatures.Where(f => f.IsRange).ToList();
            var pointFeatures = _currentFeatures.Where(f => !f.IsRange).ToList();

            // Layout range features in rows
            double barHeight = 14;
            double rowSpacing = 2;
            var rows = LayoutDomainRows(rangeFeatures);

            double totalHeight = rows.Count * (barHeight + rowSpacing) + (pointFeatures.Count > 0 ? 16 : 0);
            DomainCanvas.Height = Math.Max(30, totalHeight + 4);

            for (int rowIdx = 0; rowIdx < rows.Count; rowIdx++)
            {
                double y = rowIdx * (barHeight + rowSpacing) + 2;

                foreach (var feature in rows[rowIdx])
                {
                    // Positions are 1-based from UniProt
                    int start = (feature.Start ?? 1) - 1;
                    int end = (feature.End ?? 1) - 1;

                    double x = start * pixelsPerResidue;
                    double w = Math.Max(4, (end - start + 1) * pixelsPerResidue);

                    Color color = DomainColors.TryGetValue(feature.Type, out var c) ? c : Color.FromRgb(156, 163, 175);

                    var rect = new Rectangle
                    {
                        Width = w,
                        Height = barHeight,
                        Fill = new SolidColorBrush(color) { Opacity = 0.6 },
                        Stroke = new SolidColorBrush(color),
                        StrokeThickness = 1,
                        RadiusX = 2,
                        RadiusY = 2,
                        ToolTip = $"{feature.Type}: {feature.Description}\n{feature.Start}-{feature.End} ({feature.Length} aa)"
                    };
                    Canvas.SetLeft(rect, x);
                    Canvas.SetTop(rect, y);
                    DomainCanvas.Children.Add(rect);

                    // Label if wide enough
                    if (w > 30)
                    {
                        int maxChars = Math.Max(3, (int)(w / 6) - 2);
                        string label = !string.IsNullOrEmpty(feature.Description)
                            ? (feature.Description.Length > maxChars ? feature.Description.Substring(0, maxChars) + ".." : feature.Description)
                            : feature.Type;

                        var text = new TextBlock
                        {
                            Text = label,
                            FontSize = 8,
                            Foreground = Brushes.White,
                            FontWeight = FontWeights.SemiBold,
                            IsHitTestVisible = false
                        };
                        Canvas.SetLeft(text, x + 3);
                        Canvas.SetTop(text, y + 1);
                        DomainCanvas.Children.Add(text);
                    }
                }
            }

            // Draw point features as diamonds/markers
            double pointY = rows.Count * (barHeight + rowSpacing) + 4;
            foreach (var feature in pointFeatures)
            {
                int pos = (feature.Start ?? 1) - 1;
                double x = pos * pixelsPerResidue;
                Color color = DomainColors.TryGetValue(feature.Type, out var c) ? c : Color.FromRgb(156, 163, 175);

                var marker = new Ellipse
                {
                    Width = 6,
                    Height = 6,
                    Fill = new SolidColorBrush(color),
                    ToolTip = $"{feature.Type}: {feature.Description}\nPosition: {feature.Start}"
                };
                Canvas.SetLeft(marker, x - 3);
                Canvas.SetTop(marker, pointY);
                DomainCanvas.Children.Add(marker);
            }
        }

        private List<List<ProteinFeature>> LayoutDomainRows(List<ProteinFeature> features)
        {
            var rows = new List<List<ProteinFeature>>();
            var rowEnds = new List<int>();

            foreach (var f in features.OrderBy(f => f.Start))
            {
                bool placed = false;
                for (int r = 0; r < rows.Count; r++)
                {
                    if ((f.Start ?? 0) > rowEnds[r] + 2)
                    {
                        rows[r].Add(f);
                        rowEnds[r] = f.End ?? 0;
                        placed = true;
                        break;
                    }
                }

                if (!placed)
                {
                    rows.Add(new List<ProteinFeature> { f });
                    rowEnds.Add(f.End ?? 0);
                }
            }

            return rows;
        }

        private void DrawSequenceText()
        {
            SequenceTextBlock.Inlines.Clear();
            var result = _currentResult;
            if (result == null) return;

            // Only render text for proteins under 2000 AA (otherwise too slow)
            if (result.SequenceLength > 2000)
            {
                SequenceTextBlock.Inlines.Add(new Run($"Sequence too long to display ({result.SequenceLength} aa). See coverage bar above.")
                {
                    Foreground = UncoveredFgBrush
                });
                return;
            }

            string seq = result.ProteinSequence;
            int charsPerBlock = 10;

            for (int i = 0; i < seq.Length; i++)
            {
                bool covered = result.CoveragePerResidue[i] > 0;
                var run = new Run(seq[i].ToString())
                {
                    Foreground = covered ? CoveredFgBrush : UncoveredFgBrush,
                    Background = covered ? CoveredBgBrush : Brushes.Transparent
                };
                SequenceTextBlock.Inlines.Add(run);

                // Add space every 10 residues for readability
                if ((i + 1) % charsPerBlock == 0 && i < seq.Length - 1)
                {
                    SequenceTextBlock.Inlines.Add(new Run(" "));

                    // Add line number every 50 residues
                    if ((i + 1) % 50 == 0)
                    {
                        SequenceTextBlock.Inlines.Add(new Run($" {i + 1}\n")
                        {
                            Foreground = LineNumberBrush,
                            FontSize = 9
                        });
                    }
                }
            }

            // Final position number
            SequenceTextBlock.Inlines.Add(new Run($"  [{seq.Length}]")
            {
                Foreground = LineNumberBrush,
                FontSize = 9
            });
        }

        private void ShowLoading(string message)
        {
            LoadingText.Text = message;
            LoadingBorder.Visibility = Visibility.Visible;
        }

        private void HideLoading()
        {
            LoadingBorder.Visibility = Visibility.Collapsed;
        }
    }
}
