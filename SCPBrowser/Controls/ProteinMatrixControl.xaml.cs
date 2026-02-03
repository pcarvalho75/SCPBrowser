using Microsoft.Win32;
using SCPBrowser.Services;
using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace SCPBrowser
{
    public partial class ProteinMatrixControl : UserControl
    {
        private ProteomicsData _currentData;
        private List<HvpResult> _hvpResults;
        private Dictionary<string, double> _hvpLookup;
        private Dictionary<string, Color> _bioConditionHeaderColors;
        private DataTable _fullDataTable;
        private DataTable _filteredDataTable;
        private Dictionary<string, FastaParserService.ProteinAnnotation> _proteinAnnotations;
        private HashSet<string> _contaminantIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private string _projectDbPath;

        /// <summary>
        /// Raised when contaminant IDs are applied and ratios recalculated.
        /// </summary>
        public event EventHandler ContaminantsUpdated;

        /// <summary>
        /// Currently applied contaminant identifiers (protein group strings).
        /// </summary>
        public HashSet<string> ContaminantIds => _contaminantIds;

        public ProteinMatrixControl()
        {
            InitializeComponent();
        }

        /// <summary>
        /// Updates the matrix with proteomics data only (backward compatible)
        /// </summary>
        public void UpdateMatrix(ProteomicsData data)
        {
            UpdateMatrix(data, null);
        }

        /// <summary>
        /// Updates the matrix with proteomics data and HVP results
        /// </summary>
        public void UpdateMatrix(ProteomicsData data, List<HvpResult> hvpResults)
        {
            _currentData = data;
            _hvpResults = hvpResults;

            // Sync DB-loaded contaminants into ProteomicsData
            if (_contaminantIds.Count > 0 && _currentData != null)
            {
                _currentData.ContaminantIds = new HashSet<string>(_contaminantIds, StringComparer.OrdinalIgnoreCase);
            }

            // Build HVP lookup dictionary for fast access
            _hvpLookup = hvpResults?.ToDictionary(h => h.ProteinId, h => h.VarianceStandardized)
                         ?? new Dictionary<string, double>();

            // Generate header colors from biological conditions
            _bioConditionHeaderColors = GenerateBioConditionHeaderColors(data);

            RefreshMatrix();
        }

        /// <summary>
        /// Loads protein annotations from the database for tooltip display.
        /// </summary>
        public async Task LoadProteinAnnotationsAsync(string projectDbPath)
        {
            _projectDbPath = projectDbPath;

            try
            {
                var fastaService = new FastaParserService(projectDbPath);
                _proteinAnnotations = await fastaService.GetAllAnnotationsAsync();
                Console.WriteLine($"ProteinMatrixControl: Loaded {_proteinAnnotations.Count} protein annotations");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"ProteinMatrixControl: Failed to load annotations - {ex.Message}");
                _proteinAnnotations = new Dictionary<string, FastaParserService.ProteinAnnotation>();
            }

            try
            {
                var dbService = new ProjectDatabaseService(projectDbPath);
                await dbService.EnsureProteinContaminantsTableExistsAsync();
                _contaminantIds = await dbService.LoadContaminantsAsync();
                Console.WriteLine($"ProteinMatrixControl: Loaded {_contaminantIds.Count} contaminants from DB");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"ProteinMatrixControl: Failed to load contaminants - {ex.Message}");
            }
        }

        /// <summary>
        /// Generates light pastel colors for biological condition column headers
        /// </summary>
        private Dictionary<string, Color> GenerateBioConditionHeaderColors(ProteomicsData data)
        {
            var colorMap = new Dictionary<string, Color>();

            if (data?.BiologicalConditionPerFile == null || data.BiologicalConditionPerFile.Count == 0)
                return colorMap;

            var uniqueConditions = data.BiologicalConditionPerFile.Values
                .Where(c => !string.IsNullOrEmpty(c))
                .Distinct()
                .OrderBy(c => c)
                .ToList();

            for (int i = 0; i < uniqueConditions.Count; i++)
            {
                // Generate hue-based colors with HIGH lightness (pastel)
                double hue = (double)i / uniqueConditions.Count * 360.0;
                colorMap[uniqueConditions[i]] = HsvToRgb(hue, 0.25, 0.95); // Low saturation, high value = light pastel
            }

            return colorMap;
        }

        private Color HsvToRgb(double h, double s, double v)
        {
            double c = v * s;
            double x = c * (1 - Math.Abs((h / 60.0) % 2 - 1));
            double m = v - c;

            double r, g, b;

            if (h < 60) { r = c; g = x; b = 0; }
            else if (h < 120) { r = x; g = c; b = 0; }
            else if (h < 180) { r = 0; g = c; b = x; }
            else if (h < 240) { r = 0; g = x; b = c; }
            else if (h < 300) { r = x; g = 0; b = c; }
            else { r = c; g = 0; b = x; }

            return Color.FromRgb(
                (byte)((r + m) * 255),
                (byte)((g + m) * 255),
                (byte)((b + m) * 255)
            );
        }

        private FastaParserService.ProteinAnnotation GetBestAnnotation(string proteinGroup)
        {
            if (string.IsNullOrEmpty(proteinGroup) || _proteinAnnotations == null || _proteinAnnotations.Count == 0)
                return null;

            var accessions = proteinGroup.Split(';').Select(a => a.Trim()).Where(a => !string.IsNullOrEmpty(a)).ToArray();

            FastaParserService.ProteinAnnotation bestAnnotation = null;

            foreach (var accession in accessions)
            {
                if (_proteinAnnotations.TryGetValue(accession, out var annotation))
                {
                    if (string.Equals(annotation.Source, "sp", StringComparison.OrdinalIgnoreCase))
                    {
                        bestAnnotation = annotation;
                        break;
                    }

                    if (bestAnnotation == null)
                        bestAnnotation = annotation;
                }
            }

            return bestAnnotation;
        }

        private string GetProteinDescription(string proteinGroup)
        {
            var best = GetBestAnnotation(proteinGroup);
            if (best == null)
                return "";

            var parts = new List<string>();
            if (!string.IsNullOrEmpty(best.ProteinName))
                parts.Add(best.ProteinName);
            if (!string.IsNullOrEmpty(best.GeneName))
                parts.Add($"GN={best.GeneName}");
            return string.Join(" ", parts);
        }

        private string GetProteinGene(string proteinGroup)
        {
            var best = GetBestAnnotation(proteinGroup);
            return best?.GeneName ?? "";
        }

        private void RefreshMatrix()
        {
            if (_currentData == null || _currentData.ProteinQuantMatrix.Count == 0)
            {
                StatusText.Text = "No data to display";
                ExportButton.IsEnabled = false;
                return;
            }

            _fullDataTable = new DataTable();

            // Column 1: Contaminant checkbox
            _fullDataTable.Columns.Add("Contaminant", typeof(bool));

            // Column 2: Protein Group
            _fullDataTable.Columns.Add("Protein Group", typeof(string));

            // Column 3: Description (from FASTA annotations)
            _fullDataTable.Columns.Add("Description", typeof(string));

            // Column 4: Gene (from FASTA annotations, prioritizing SwissProt)
            _fullDataTable.Columns.Add("Gene", typeof(string));

            // Column 3: Variance Standardized (HVP score) - only if we have HVP data
            // FIX: Changed "Var. Std." to "Var_Std" because dots in column names break WPF binding paths
            bool hasHvpData = _hvpLookup != null && _hvpLookup.Count > 0;
            if (hasHvpData)
            {
                _fullDataTable.Columns.Add("Var_Std", typeof(double));
            }

            // Remaining columns: Raw files
            foreach (var rawFile in _currentData.RawFileNames)
            {
                _fullDataTable.Columns.Add(rawFile, typeof(double));
            }

            // Populate rows
            foreach (var protein in _currentData.ProteinQuantMatrix.Keys.OrderBy(p => p))
            {
                var row = _fullDataTable.NewRow();
                row["Contaminant"] = _contaminantIds.Contains(protein);
                row["Protein Group"] = protein;
                row["Description"] = GetProteinDescription(protein);
                row["Gene"] = GetProteinGene(protein);

                // Add HVP score if available
                if (hasHvpData)
                {
                    if (_hvpLookup.TryGetValue(protein, out double varStd))
                    {
                        row["Var_Std"] = varStd;
                    }
                    else
                    {
                        row["Var_Std"] = DBNull.Value;
                    }
                }

                // Add abundances
                foreach (var rawFile in _currentData.RawFileNames)
                {
                    if (_currentData.ProteinQuantMatrix[protein].ContainsKey(rawFile))
                    {
                        row[rawFile] = _currentData.ProteinQuantMatrix[protein][rawFile];
                    }
                    else
                    {
                        row[rawFile] = DBNull.Value; // Use DBNull instead of 0 for missing values
                    }
                }

                _fullDataTable.Rows.Add(row);
            }

            _filteredDataTable = _fullDataTable.Copy();
            ProteinMatrixGrid.Columns.Clear();
            ProteinMatrixGrid.ItemsSource = _filteredDataTable.DefaultView;

            int hvpCount = _hvpResults?.Count(h => h.IsHighlyVariable) ?? 0;
            string hvpInfo = hasHvpData ? $" | {hvpCount} HVPs" : "";
            UpdateContaminantStatusText();
            ExportButton.IsEnabled = true;
        }

        private void ProteinMatrixGrid_AutoGeneratingColumn(object sender, DataGridAutoGeneratingColumnEventArgs e)
        {
            if (e.PropertyName == "Contaminant")
            {
                e.Cancel = true;

                var checkColumn = new DataGridCheckBoxColumn
                {
                    Header = "☣",
                    Binding = new System.Windows.Data.Binding("Contaminant") { UpdateSourceTrigger = System.Windows.Data.UpdateSourceTrigger.PropertyChanged },
                    Width = new DataGridLength(40, DataGridLengthUnitType.Pixel),
                    IsReadOnly = false
                };

                var headerStyle = new Style(typeof(DataGridColumnHeader));
                headerStyle.Setters.Add(new Setter(DataGridColumnHeader.HorizontalContentAlignmentProperty, HorizontalAlignment.Center));
                headerStyle.Setters.Add(new Setter(DataGridColumnHeader.ToolTipProperty, "Contaminant"));
                headerStyle.Setters.Add(new Setter(DataGridColumnHeader.BackgroundProperty,
                    new SolidColorBrush(Color.FromRgb(255, 237, 213))));
                headerStyle.Setters.Add(new Setter(DataGridColumnHeader.FontWeightProperty, FontWeights.SemiBold));
                headerStyle.Setters.Add(new Setter(DataGridColumnHeader.PaddingProperty, new Thickness(8, 5, 8, 5)));
                headerStyle.Setters.Add(new Setter(DataGridColumnHeader.BorderBrushProperty,
                    new SolidColorBrush(Color.FromRgb(203, 213, 225))));
                headerStyle.Setters.Add(new Setter(DataGridColumnHeader.BorderThicknessProperty, new Thickness(0, 0, 1, 1)));
                checkColumn.HeaderStyle = headerStyle;

                ProteinMatrixGrid.Columns.Insert(0, checkColumn);
            }
            else if (e.PropertyName == "Protein Group")
            {
                // Cancel auto-generation and create custom template column
                e.Cancel = true;

                var templateColumn = new DataGridTemplateColumn
                {
                    Header = "Protein Group",
                    Width = new DataGridLength(250, DataGridLengthUnitType.Pixel),
                    SortMemberPath = "Protein Group",
                    IsReadOnly = true
                };

                // Create the cell template - use Loaded event to populate Inlines for bold sp entries
                var template = new DataTemplate();
                var textBlockFactory = new FrameworkElementFactory(typeof(TextBlock));
                textBlockFactory.SetValue(TextBlock.PaddingProperty, new Thickness(8, 5, 8, 5));
                textBlockFactory.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);

                textBlockFactory.AddHandler(TextBlock.LoadedEvent, new RoutedEventHandler(ProteinCell_Loaded));
                textBlockFactory.AddHandler(TextBlock.MouseEnterEvent, new MouseEventHandler(ProteinCell_MouseEnter));

                template.VisualTree = textBlockFactory;
                templateColumn.CellTemplate = template;

                // Insert at beginning
                ProteinMatrixGrid.Columns.Insert(0, templateColumn);
            }
            else if (e.PropertyName == "Description")
            {
                e.Column.IsReadOnly = true;
                e.Column.Width = new DataGridLength(300, DataGridLengthUnitType.Pixel);
            }
            else if (e.PropertyName == "Gene")
            {
                e.Column.IsReadOnly = true;
                e.Column.Width = new DataGridLength(100, DataGridLengthUnitType.Pixel);
            }
            else if (e.PropertyName == "Var_Std") // FIX: Matched new safe column name
            {
                e.Column.IsReadOnly = true;

                // HVP column - special formatting
                var textColumn = e.Column as DataGridTextColumn;
                if (textColumn != null)
                {
                    // Set the visual header back to "Var. Std." for the user
                    textColumn.Header = "Var. Std.";

                    textColumn.Width = new DataGridLength(80, DataGridLengthUnitType.Pixel);
                    if (textColumn.Binding is System.Windows.Data.Binding binding)
                    {
                        binding.StringFormat = "F3";
                    }

                    // Style the header with a distinct color
                    var headerStyle = new Style(typeof(DataGridColumnHeader));
                    headerStyle.Setters.Add(new Setter(DataGridColumnHeader.BackgroundProperty,
                        new SolidColorBrush(Color.FromRgb(254, 243, 199)))); // Amber-100
                    headerStyle.Setters.Add(new Setter(DataGridColumnHeader.FontWeightProperty, FontWeights.SemiBold));
                    headerStyle.Setters.Add(new Setter(DataGridColumnHeader.PaddingProperty, new Thickness(8, 5, 8, 5)));
                    headerStyle.Setters.Add(new Setter(DataGridColumnHeader.BorderBrushProperty,
                        new SolidColorBrush(Color.FromRgb(203, 213, 225))));
                    headerStyle.Setters.Add(new Setter(DataGridColumnHeader.BorderThicknessProperty, new Thickness(0, 0, 1, 1)));
                    e.Column.HeaderStyle = headerStyle;
                }
            }
            else
            {
                e.Column.IsReadOnly = true;

                // Raw file columns
                var textColumn = e.Column as DataGridTextColumn;
                if (textColumn != null)
                {
                    if (textColumn.Binding is System.Windows.Data.Binding binding)
                    {
                        binding.StringFormat = "N2";
                    }

                    // Apply biological condition color to header if available
                    if (_currentData?.BiologicalConditionPerFile != null &&
                        _bioConditionHeaderColors != null &&
                        _currentData.BiologicalConditionPerFile.TryGetValue(e.PropertyName, out string bioCondition) &&
                        !string.IsNullOrEmpty(bioCondition) &&
                        _bioConditionHeaderColors.TryGetValue(bioCondition, out Color headerColor))
                    {
                        var headerStyle = new Style(typeof(DataGridColumnHeader));
                        headerStyle.Setters.Add(new Setter(DataGridColumnHeader.BackgroundProperty,
                            new SolidColorBrush(headerColor)));
                        headerStyle.Setters.Add(new Setter(DataGridColumnHeader.FontWeightProperty, FontWeights.SemiBold));
                        headerStyle.Setters.Add(new Setter(DataGridColumnHeader.PaddingProperty, new Thickness(8, 5, 8, 5)));
                        headerStyle.Setters.Add(new Setter(DataGridColumnHeader.BorderBrushProperty,
                            new SolidColorBrush(Color.FromRgb(203, 213, 225))));
                        headerStyle.Setters.Add(new Setter(DataGridColumnHeader.BorderThicknessProperty, new Thickness(0, 0, 1, 1)));
                        headerStyle.Setters.Add(new Setter(DataGridColumnHeader.ToolTipProperty, bioCondition));
                        e.Column.HeaderStyle = headerStyle;
                    }
                }
            }
        }

        private void ProteinCell_Loaded(object sender, RoutedEventArgs e)
        {
            if (sender is TextBlock textBlock && textBlock.DataContext is System.Data.DataRowView rowView)
            {
                string proteinGroup = rowView["Protein Group"]?.ToString() ?? "";
                textBlock.Inlines.Clear();

                if (string.IsNullOrEmpty(proteinGroup) || _proteinAnnotations == null || _proteinAnnotations.Count == 0)
                {
                    textBlock.Inlines.Add(new System.Windows.Documents.Run(proteinGroup));
                    return;
                }

                var accessions = proteinGroup.Split(';');
                for (int i = 0; i < accessions.Length; i++)
                {
                    var acc = accessions[i].Trim();
                    if (string.IsNullOrEmpty(acc)) continue;

                    bool isSp = _proteinAnnotations.TryGetValue(acc, out var annotation) &&
                                string.Equals(annotation.Source, "sp", StringComparison.OrdinalIgnoreCase);

                    var run = new System.Windows.Documents.Run(acc);
                    if (isSp)
                        run.FontWeight = FontWeights.Bold;

                    textBlock.Inlines.Add(run);

                    if (i < accessions.Length - 1)
                        textBlock.Inlines.Add(new System.Windows.Documents.Run("; "));
                }
            }
        }

        private void ProteinCell_MouseEnter(object sender, MouseEventArgs e)
        {
            if (sender is TextBlock textBlock && _proteinAnnotations != null && _proteinAnnotations.Count > 0)
            {
                string proteinGroup = textBlock.Text;
                string tooltip = BuildProteinTooltip(proteinGroup);

                if (!string.IsNullOrEmpty(tooltip))
                {
                    textBlock.ToolTip = tooltip;
                }
                else
                {
                    textBlock.ToolTip = proteinGroup;
                }
            }
        }

        private string BuildProteinTooltip(string proteinGroup)
        {
            if (string.IsNullOrEmpty(proteinGroup) || _proteinAnnotations == null)
                return null;

            // Handle protein groups (semicolon-separated)
            var accessions = proteinGroup.Split(';').Select(a => a.Trim()).ToArray();
            var lines = new List<string>();

            foreach (var accession in accessions.Take(3)) // Limit to first 3 for long groups
            {
                if (_proteinAnnotations.TryGetValue(accession, out var annotation))
                {
                    var parts = new List<string>();

                    if (!string.IsNullOrEmpty(annotation.ProteinName))
                        parts.Add(annotation.ProteinName);

                    if (!string.IsNullOrEmpty(annotation.GeneName))
                        parts.Add($"Gene: {annotation.GeneName}");

                    if (!string.IsNullOrEmpty(annotation.Organism))
                        parts.Add($"Organism: {annotation.Organism}");

                    if (parts.Count > 0)
                    {
                        lines.Add($"[{accession}] {string.Join(" | ", parts)}");
                    }
                }
            }

            if (accessions.Length > 3)
            {
                lines.Add($"... and {accessions.Length - 3} more");
            }

            return lines.Count > 0 ? string.Join("\n", lines) : null;
        }

        private void SelectAllVisibleButton_Click(object sender, RoutedEventArgs e)
        {
            SetContaminantOnAllVisible(true);
        }

        private void DeselectAllVisibleButton_Click(object sender, RoutedEventArgs e)
        {
            SetContaminantOnAllVisible(false);
        }

        private void SetContaminantOnAllVisible(bool mark)
        {
            if (_filteredDataTable == null) return;

            foreach (DataRow row in _filteredDataTable.Rows)
            {
                string proteinGroup = row["Protein Group"].ToString();
                row["Contaminant"] = mark;

                if (mark)
                    _contaminantIds.Add(proteinGroup);
                else
                    _contaminantIds.Remove(proteinGroup);

                // Sync to full table
                foreach (DataRow fullRow in _fullDataTable.Rows)
                {
                    if (fullRow["Protein Group"].ToString() == proteinGroup)
                    {
                        fullRow["Contaminant"] = mark;
                        break;
                    }
                }
            }

            ProteinMatrixGrid.Items.Refresh();
            UpdateContaminantStatusText();
        }

        private async void ApplyContaminantsButton_Click(object sender, RoutedEventArgs e)
        {
            // Sync _contaminantIds from the current DataTable checkbox state
            _contaminantIds.Clear();
            if (_filteredDataTable != null)
            {
                foreach (DataRow row in _fullDataTable.Rows)
                {
                    if (row["Contaminant"] is bool isContaminant && isContaminant)
                    {
                        _contaminantIds.Add(row["Protein Group"].ToString());
                    }
                }
            }

            // Propagate contaminant IDs to ProteomicsData so DataFilterService can exclude them
            if (_currentData != null)
            {
                _currentData.ContaminantIds = new HashSet<string>(_contaminantIds, StringComparer.OrdinalIgnoreCase);
            }

            // Persist to database
            if (!string.IsNullOrEmpty(_projectDbPath))
            {
                try
                {
                    var dbService = new ProjectDatabaseService(_projectDbPath);
                    await dbService.SaveContaminantsAsync(_contaminantIds);
                    Console.WriteLine($"ProteinMatrixControl: Saved {_contaminantIds.Count} contaminants to DB");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"ProteinMatrixControl: Failed to save contaminants - {ex.Message}");
                }
            }

            RefreshMatrix();
            ContaminantsUpdated?.Invoke(this, EventArgs.Empty);
        }

        private void MarkAsContaminant_Click(object sender, RoutedEventArgs e)
        {
            ToggleContaminantOnSelectedRows(true);
        }

        private void UnmarkAsContaminant_Click(object sender, RoutedEventArgs e)
        {
            ToggleContaminantOnSelectedRows(false);
        }

        private void ToggleContaminantOnSelectedRows(bool mark)
        {
            if (ProteinMatrixGrid.SelectedCells.Count == 0)
                return;

            var affectedRows = ProteinMatrixGrid.SelectedCells
                .Select(c => c.Item as DataRowView)
                .Where(rv => rv != null)
                .Distinct()
                .ToList();

            foreach (var rowView in affectedRows)
            {
                string proteinGroup = rowView["Protein Group"]?.ToString();
                if (string.IsNullOrEmpty(proteinGroup)) continue;

                rowView["Contaminant"] = mark;

                if (mark)
                    _contaminantIds.Add(proteinGroup);
                else
                    _contaminantIds.Remove(proteinGroup);

                // Also update the full table
                foreach (DataRow fullRow in _fullDataTable.Rows)
                {
                    if (fullRow["Protein Group"].ToString() == proteinGroup)
                    {
                        fullRow["Contaminant"] = mark;
                        break;
                    }
                }
            }

            // Force row visual refresh
            ProteinMatrixGrid.Items.Refresh();
            UpdateContaminantStatusText();
        }

        private void ProteinMatrixGrid_CellEditEnding(object sender, DataGridCellEditEndingEventArgs e)
        {
            if (e.Column.Header?.ToString() != "☣" || e.EditAction == DataGridEditAction.Cancel)
                return;

            if (e.Row.Item is DataRowView rowView)
            {
                string proteinGroup = rowView["Protein Group"]?.ToString();
                if (string.IsNullOrEmpty(proteinGroup)) return;

                // The checkbox value hasn't committed yet, so read the editing element
                bool isChecked = false;
                if (e.EditingElement is CheckBox cb)
                    isChecked = cb.IsChecked == true;

                if (isChecked)
                    _contaminantIds.Add(proteinGroup);
                else
                    _contaminantIds.Remove(proteinGroup);

                // Sync to full table
                foreach (DataRow fullRow in _fullDataTable.Rows)
                {
                    if (fullRow["Protein Group"].ToString() == proteinGroup)
                    {
                        fullRow["Contaminant"] = isChecked;
                        break;
                    }
                }

                // Defer visual refresh until after commit
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    ProteinMatrixGrid.CommitEdit(DataGridEditingUnit.Row, true);
                    ProteinMatrixGrid.Items.Refresh();
                    UpdateContaminantStatusText();
                }), System.Windows.Threading.DispatcherPriority.Background);
            }
        }

        private void UpdateContaminantStatusText()
        {
            int contaminantCount = _contaminantIds.Count;
            int hvpCount = _hvpResults?.Count(h => h.IsHighlyVariable) ?? 0;
            bool hasHvpData = _hvpLookup != null && _hvpLookup.Count > 0;
            string hvpInfo = hasHvpData ? $" | {hvpCount} HVPs" : "";
            string contaminantInfo = contaminantCount > 0 ? $" | {contaminantCount} contaminants marked" : "";
            StatusText.Text = $"Displaying {_filteredDataTable.Rows.Count} proteins across {_currentData.RawFileNames.Count} raw files{hvpInfo}{contaminantInfo}";
        }

        private void SearchTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_fullDataTable == null)
                return;

            var searchText = SearchTextBox.Text.Trim();

            if (string.IsNullOrWhiteSpace(searchText))
            {
                _filteredDataTable = _fullDataTable.Copy();
            }
            else
            {
                _filteredDataTable = _fullDataTable.Clone();

                foreach (DataRow row in _fullDataTable.Rows)
                {
                    var proteinName = row["Protein Group"].ToString();
                    var description = row["Description"].ToString();
                    var gene = row["Gene"].ToString();
                    if (proteinName.IndexOf(searchText, StringComparison.OrdinalIgnoreCase) >= 0 ||
                        description.IndexOf(searchText, StringComparison.OrdinalIgnoreCase) >= 0 ||
                        gene.IndexOf(searchText, StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        _filteredDataTable.ImportRow(row);
                    }
                }
            }

            ProteinMatrixGrid.Columns.Clear();
            ProteinMatrixGrid.ItemsSource = _filteredDataTable.DefaultView;
            UpdateContaminantStatusText();
        }

        private void ClearSearchButton_Click(object sender, RoutedEventArgs e)
        {
            SearchTextBox.Text = string.Empty;
        }

        private void ExportButton_Click(object sender, RoutedEventArgs e)
        {
            if (_filteredDataTable == null || _filteredDataTable.Rows.Count == 0)
            {
                MessageBox.Show("No data to export", "Export", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var dialog = new SaveFileDialog
            {
                Filter = "CSV files (*.csv)|*.csv|All files (*.*)|*.*",
                Title = "Export Protein Matrix to CSV",
                FileName = "protein_matrix.csv"
            };

            if (dialog.ShowDialog() != true)
                return;

            try
            {
                using (var writer = new StreamWriter(dialog.FileName))
                {
                    var columnNames = _filteredDataTable.Columns.Cast<DataColumn>()
                        .Select(column => EscapeCsvField(column.ColumnName));
                    writer.WriteLine(string.Join(",", columnNames));

                    foreach (DataRow row in _filteredDataTable.Rows)
                    {
                        var fields = row.ItemArray.Select(field =>
                        {
                            if (field == DBNull.Value)
                            {
                                return ""; // Export missing values as empty
                            }
                            if (field is double doubleValue)
                            {
                                return doubleValue.ToString("G");
                            }
                            return EscapeCsvField(field.ToString());
                        });
                        writer.WriteLine(string.Join(",", fields));
                    }
                }

                MessageBox.Show($"Data exported successfully to:\n{dialog.FileName}", "Export Complete",
                    MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error exporting data: {ex.Message}", "Export Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private string EscapeCsvField(string field)
        {
            if (string.IsNullOrEmpty(field))
                return string.Empty;

            if (field.Contains(",") || field.Contains("\"") || field.Contains("\n"))
            {
                return "\"" + field.Replace("\"", "\"\"") + "\"";
            }

            return field;
        }

        private void ProteinMatrixGrid_LoadingRow(object sender, DataGridRowEventArgs e)
        {
            e.Row.Header = (e.Row.GetIndex() + 1).ToString();

            if (_contaminantIds.Count > 0 && e.Row.Item is DataRowView rowView)
            {
                string proteinGroup = rowView["Protein Group"]?.ToString() ?? "";
                bool isContaminant = _contaminantIds.Contains(proteinGroup);

                if (isContaminant)
                {
                    e.Row.Background = new SolidColorBrush(Color.FromRgb(255, 237, 213));
                    e.Row.BorderBrush = new SolidColorBrush(Color.FromRgb(251, 191, 36));
                    e.Row.BorderThickness = new Thickness(0, 0, 0, 1);
                }
                else
                {
                    e.Row.Background = e.Row.GetIndex() % 2 == 0
                        ? Brushes.White
                        : new SolidColorBrush(Color.FromRgb(249, 250, 251));
                    e.Row.BorderBrush = null;
                    e.Row.BorderThickness = new Thickness(0);
                }
            }
        }
    }
}