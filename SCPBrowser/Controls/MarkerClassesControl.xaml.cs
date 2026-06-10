using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using SCPBrowser.Models;
using SCPBrowser.Services;

namespace SCPBrowser.Controls
{
    /// <summary>
    /// Project Browser tab for defining marker-only cell classes (name + marker genes) and classifying cells on
    /// markers alone — no transcriptomic/proteomic reference profile required.
    /// </summary>
    public partial class MarkerClassesControl : UserControl
    {
        private string? _dbPath;
        private MarkerClassificationService? _service;

        public MarkerClassesControl()
        {
            InitializeComponent();
        }

        public async Task LoadAsync(string dbPath)
        {
            _dbPath = dbPath;
            _service = new MarkerClassificationService(dbPath);
            await ReloadClassesAsync();
            MinMarkersBox.Value = await _service.GetMinMarkersAsync();
        }

        private async Task ReloadClassesAsync()
        {
            if (_service == null) return;
            ClassList.ItemsSource = await _service.GetClassesAsync();
        }

        private void ClassList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (ClassList.SelectedItem is MarkerClass c)
            {
                NameBox.Text = c.Name;
                MarkersBox.Text = string.Join(Environment.NewLine, c.Genes);
            }
        }

        private void AddClass_Click(object sender, RoutedEventArgs e)
        {
            ClassList.SelectedItem = null;
            NameBox.Text = "";
            MarkersBox.Text = "";
            NameBox.Focus();
        }

        private async void SaveClass_Click(object sender, RoutedEventArgs e)
        {
            if (_service == null) return;
            string name = NameBox.Text.Trim();
            if (string.IsNullOrEmpty(name)) { Warn("Enter a class name."); return; }
            var genes = ParseGenes(MarkersBox.Text);
            if (genes.Count == 0) { Warn("Enter at least one marker gene."); return; }

            await _service.SaveClassAsync(new MarkerClass { Name = name, Genes = genes });
            await ReloadClassesAsync();
            StatusText.Text = $"Saved \"{name}\" ({genes.Count} markers).";
        }

        private async void DeleteClass_Click(object sender, RoutedEventArgs e)
        {
            if (_service == null || ClassList.SelectedItem is not MarkerClass c) return;
            if (MessageBox.Show($"Delete class \"{c.Name}\"?", "Delete", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            await _service.DeleteClassAsync(c.Name);
            NameBox.Text = ""; MarkersBox.Text = "";
            await ReloadClassesAsync();
        }

        private async void MinMarkers_ValueChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
        {
            if (_service != null && MinMarkersBox.Value is int v)
                await _service.SetMinMarkersAsync(v);
        }

        private async void Classify_Click(object sender, RoutedEventArgs e)
        {
            if (_service == null) return;
            ClassifyButton.IsEnabled = false;
            StatusText.Text = "Classifying...";
            try
            {
                var progress = new Progress<string>(m => StatusText.Text = m);
                var summary = await _service.ClassifyAndStoreAsync(progress);
                if (summary.Count == 0)
                {
                    SummaryText.Text = "No classes defined, or no cells/proteins to classify.";
                    StatusText.Text = "";
                    return;
                }
                int total = summary.Values.Sum();
                var parts = summary.OrderByDescending(kv => kv.Value).Select(kv => $"{kv.Key}={kv.Value}");
                SummaryText.Text = $"Classified {total} cells:  {string.Join(",  ", parts)}\n" +
                                   "Saved. Switch the scatter's colour mode to \"Cell Type\" (reload the project if needed) to view the assignments.";
                StatusText.Text = "Done.";
            }
            catch (Exception ex)
            {
                StatusText.Text = "Error: " + ex.Message;
            }
            finally { ClassifyButton.IsEnabled = true; }
        }

        private static void Warn(string msg) => MessageBox.Show(msg, "Marker class", MessageBoxButton.OK, MessageBoxImage.Warning);

        private static List<string> ParseGenes(string text) =>
            text.Split(new[] { '\n', '\r', ',', ';', ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(s => s.Trim())
                .Where(s => s.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
    }
}
