using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using SCPBrowser.Services;

namespace SCPBrowser
{
    public partial class PcaLoadingsDialog : Window
    {
        private readonly List<string> _proteinNames;
        private readonly double[,] _loadings;
        private readonly double[] _varianceExplained;
        private readonly UniProtService _uniProtService;
        private Dictionary<string, UniProtEntry> _proteinInfo = new();

        public PcaLoadingsDialog(List<string> proteinNames, double[,] loadings, double[] varianceExplained)
        {
            InitializeComponent();

            _proteinNames = proteinNames;
            _loadings = loadings;
            _varianceExplained = varianceExplained;
            _uniProtService = new UniProtService();

            // Display variance explained
            VarianceText.Text = $"Variance explained: PC1 = {varianceExplained[0]:P1}, PC2 = {varianceExplained[1]:P1}";
            Pc1Header.Text = $"PC1 Loadings ({varianceExplained[0]:P1})";
            Pc2Header.Text = $"PC2 Loadings ({varianceExplained[1]:P1})";

            UpdateLoadingsDisplay(10);
        }

        private void TopNComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (TopNComboBox.SelectedItem is ComboBoxItem item && int.TryParse(item.Content.ToString(), out int topN))
            {
                UpdateLoadingsDisplay(topN);
            }
        }

        private void UpdateLoadingsDisplay(int topN)
        {
            if (_loadings == null || _proteinNames == null)
                return;

            int nProteins = _loadings.GetLength(0);

            // Build list of (protein, loading) for PC1 and PC2
            var pc1Loadings = new List<(string Protein, double Loading)>();
            var pc2Loadings = new List<(string Protein, double Loading)>();

            for (int i = 0; i < nProteins; i++)
            {
                pc1Loadings.Add((_proteinNames[i], _loadings[i, 0]));
                pc2Loadings.Add((_proteinNames[i], _loadings[i, 1]));
            }

            // Sort by absolute value and take top N
            var topPc1 = pc1Loadings
                .OrderByDescending(x => Math.Abs(x.Loading))
                .Take(topN)
                .ToList();

            var topPc2 = pc2Loadings
                .OrderByDescending(x => Math.Abs(x.Loading))
                .Take(topN)
                .ToList();

            // Find max absolute loading for scaling bars
            double maxPc1 = topPc1.Count > 0 ? topPc1.Max(x => Math.Abs(x.Loading)) : 1;
            double maxPc2 = topPc2.Count > 0 ? topPc2.Max(x => Math.Abs(x.Loading)) : 1;

            // Convert to display items
            var pc1Items = topPc1.Select(x => new LoadingDisplayItem(x.Protein, x.Loading, maxPc1, _proteinInfo)).ToList();
            var pc2Items = topPc2.Select(x => new LoadingDisplayItem(x.Protein, x.Loading, maxPc2, _proteinInfo)).ToList();

            Pc1LoadingsList.ItemsSource = pc1Items;
            Pc2LoadingsList.ItemsSource = pc2Items;

            // Fetch UniProt info for displayed proteins
            var allProteins = topPc1.Select(x => x.Protein).Union(topPc2.Select(x => x.Protein)).Distinct().ToList();
            FetchProteinInfoAsync(allProteins, pc1Items, pc2Items);
        }

        private async void FetchProteinInfoAsync(List<string> proteinIds, List<LoadingDisplayItem> pc1Items, List<LoadingDisplayItem> pc2Items)
        {
            FetchStatusText.Text = "Fetching protein information from UniProt...";
            FetchStatusText.Visibility = Visibility.Visible;

            try
            {
                var progress = new Progress<(int Current, int Total, string ProteinId)>(p =>
                {
                    FetchStatusText.Text = $"Fetching protein info: {p.Current}/{p.Total}...";
                });

                var results = await _uniProtService.GetProteinInfoBatchAsync(proteinIds, progress);

                // Update cache
                foreach (var kvp in results)
                {
                    _proteinInfo[kvp.Key] = kvp.Value;
                }

                // Update display items with fetched info
                foreach (var item in pc1Items.Concat(pc2Items))
                {
                    if (_proteinInfo.TryGetValue(item.ProteinId, out var entry))
                    {
                        item.UpdateFromUniProt(entry);
                    }
                }

                // Refresh the ItemsSource to update tooltips
                Pc1LoadingsList.Items.Refresh();
                Pc2LoadingsList.Items.Refresh();

                FetchStatusText.Text = $"Loaded info for {results.Count} proteins";

                // Hide status after 2 seconds
                await Task.Delay(2000);
                FetchStatusText.Visibility = Visibility.Collapsed;
            }
            catch (Exception ex)
            {
                FetchStatusText.Text = $"Error fetching protein info: {ex.Message}";
            }
        }

        private void ProteinName_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (sender is TextBlock textBlock && textBlock.DataContext is LoadingDisplayItem item)
            {
                if (!string.IsNullOrEmpty(item.UniProtUrl))
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = item.UniProtUrl,
                        UseShellExecute = true
                    });
                }
            }
        }

        private void CopyButton_Click(object sender, RoutedEventArgs e)
        {
            if (TopNComboBox.SelectedItem is ComboBoxItem item && int.TryParse(item.Content.ToString(), out int topN))
            {
                var lines = new List<string>
                {
                    $"PCA Loadings Analysis - Top {topN} proteins",
                    $"Variance explained: PC1 = {_varianceExplained[0]:P1}, PC2 = {_varianceExplained[1]:P1}",
                    "",
                    "PC1 Loadings:",
                    "Protein\tGene\tLoading\tDescription"
                };

                int nProteins = _loadings.GetLength(0);
                var pc1Loadings = new List<(string Protein, double Loading)>();
                var pc2Loadings = new List<(string Protein, double Loading)>();

                for (int i = 0; i < nProteins; i++)
                {
                    pc1Loadings.Add((_proteinNames[i], _loadings[i, 0]));
                    pc2Loadings.Add((_proteinNames[i], _loadings[i, 1]));
                }

                var topPc1 = pc1Loadings.OrderByDescending(x => Math.Abs(x.Loading)).Take(topN);
                var topPc2 = pc2Loadings.OrderByDescending(x => Math.Abs(x.Loading)).Take(topN);

                foreach (var p in topPc1)
                {
                    _proteinInfo.TryGetValue(p.Protein, out var info);
                    lines.Add($"{p.Protein}\t{info?.GeneName ?? ""}\t{p.Loading:F4}\t{info?.ProteinName ?? ""}");
                }

                lines.Add("");
                lines.Add("PC2 Loadings:");
                lines.Add("Protein\tGene\tLoading\tDescription");

                foreach (var p in topPc2)
                {
                    _proteinInfo.TryGetValue(p.Protein, out var info);
                    lines.Add($"{p.Protein}\t{info?.GeneName ?? ""}\t{p.Loading:F4}\t{info?.ProteinName ?? ""}");
                }

                Clipboard.SetText(string.Join("\n", lines));

                MessageBox.Show("Loadings copied to clipboard!", "Copied", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }
    }

    public class LoadingDisplayItem
    {
        public string ProteinId { get; set; }
        public string ProteinName { get; set; }
        public string GeneName { get; set; }
        public double Loading { get; set; }
        public string LoadingText { get; set; }
        public double BarWidth { get; set; }
        public Brush BarColor { get; set; }
        public HorizontalAlignment BarAlignment { get; set; }
        public string Tooltip { get; set; }
        public string UniProtUrl { get; set; }

        public LoadingDisplayItem(string protein, double loading, double maxAbsLoading, Dictionary<string, UniProtEntry> proteinInfo)
        {
            ProteinId = protein;
            Loading = loading;
            LoadingText = loading.ToString("F3");

            // Try to get cached info
            if (proteinInfo.TryGetValue(protein, out var entry))
            {
                UpdateFromUniProt(entry);
            }
            else
            {
                // Default display until UniProt info is fetched
                ProteinName = ExtractGeneName(protein);
                GeneName = null;
                Tooltip = $"{protein}\nLoading: {loading:F4}\n\nClick to open in UniProt\n(Fetching details...)";
                UniProtUrl = null;
            }

            // Scale bar width (max 150 pixels)
            double normalizedWidth = Math.Abs(loading) / maxAbsLoading * 150;
            BarWidth = Math.Max(2, normalizedWidth);

            // Color: positive = blue, negative = red
            if (loading >= 0)
            {
                BarColor = new SolidColorBrush(Color.FromRgb(59, 130, 246)); // Blue
                BarAlignment = HorizontalAlignment.Left;
            }
            else
            {
                BarColor = new SolidColorBrush(Color.FromRgb(239, 68, 68)); // Red
                BarAlignment = HorizontalAlignment.Right;
            }
        }

        public void UpdateFromUniProt(UniProtEntry entry)
        {
            if (entry == null) return;

            GeneName = entry.GeneName;
            ProteinName = entry.GeneName ?? ExtractGeneName(ProteinId);
            UniProtUrl = entry.UniProtUrl;

            // Build rich tooltip
            var tooltipLines = new List<string>
            {
                $"═══ {entry.GeneName ?? ProteinId} ═══",
                ""
            };

            if (!string.IsNullOrEmpty(entry.ProteinName))
                tooltipLines.Add($"📌 {entry.ProteinName}");

            tooltipLines.Add($"🔢 Loading: {Loading:F4}");
            tooltipLines.Add("");

            if (!string.IsNullOrEmpty(entry.AccessionId))
                tooltipLines.Add($"🔑 UniProt: {entry.AccessionId}");

            if (!string.IsNullOrEmpty(entry.Organism))
                tooltipLines.Add($"🧬 Organism: {entry.Organism}");

            if (entry.SequenceLength.HasValue)
                tooltipLines.Add($"📏 Length: {entry.SequenceLength} aa");

            if (entry.MolecularWeight.HasValue)
                tooltipLines.Add($"⚖️ Mass: {entry.MolecularWeightFormatted}");

            if (entry.SubcellularLocations.Count > 0)
            {
                tooltipLines.Add("");
                tooltipLines.Add($"📍 Location: {string.Join(", ", entry.SubcellularLocations.Take(3))}");
            }

            if (!string.IsNullOrEmpty(entry.Function))
            {
                tooltipLines.Add("");
                var funcShort = entry.Function.Length > 200 ? entry.Function.Substring(0, 197) + "..." : entry.Function;
                tooltipLines.Add($"⚙️ Function: {funcShort}");
            }

            if (entry.PdbIds.Count > 0)
            {
                tooltipLines.Add("");
                tooltipLines.Add($"🔬 Structures: {string.Join(", ", entry.PdbIds.Take(5))}");
            }

            tooltipLines.Add("");
            tooltipLines.Add("─────────────────────────");
            tooltipLines.Add("Click protein name to open UniProt page");

            Tooltip = string.Join("\n", tooltipLines);
        }

        private string ExtractGeneName(string proteinId)
        {
            if (string.IsNullOrEmpty(proteinId))
                return proteinId;

            // Handle sp|P36578|RL4_HUMAN format
            if (proteinId.Contains("|"))
            {
                var parts = proteinId.Split('|');
                if (parts.Length >= 3)
                    return parts[2].Split('_')[0];
                if (parts.Length >= 2)
                    return parts[1];
            }

            // Handle RPL4_HUMAN format
            if (proteinId.Contains("_"))
                return proteinId.Split('_')[0];

            return proteinId;
        }
    }
}