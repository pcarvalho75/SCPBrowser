using Microsoft.Win32;
using SCPBrowser.Services;
using System;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
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

        // cRAP-style accessions bundled with the app so the contaminant-ratio QC can actually fire without a
        // user hand-ticking rows in a 3000-protein matrix. Deliberately a conservative subset of cRAP: skin
        // keratins but NOT the epithelial cytokeratins K7/K8/K18/K19, which are real biology in cell lines;
        // digestion enzymes; affinity reagents; and the serum/milk proteins that dominate FBS culture.
        // Applying it is opt-in, every match is listed for confirmation, and nothing is excluded from the
        // analysis until Apply Contaminants is pressed.
        private static readonly string[] BundledContaminantAccessions =
        {
            // Human skin/hair keratins (dust, handling)
            "P04264", "P35908", "P19013", "P13647", "P02538", "P04259",
            "P35527", "P13645", "P13646", "P02533", "P08779", "Q04695",
            // Digestion enzymes
            "P00761", "P00760", "P00766", "Q7M135",
            // Serum albumin (bovine, human)
            "P02769", "P02768",
            // Other abundant human serum/plasma carry-over
            "P02787", "P01857", "P01834",
            // Bovine milk proteins (fetal bovine serum)
            "P02662", "P02663", "P02666", "P02668", "P00711", "P02754",
            // Other bovine carry-over
            "P01966", "P02070", "P00921",
            // Affinity reagents
            "P22629", "P02701"
        };

        public ProteinMatrixControl()
        {
            InitializeComponent();
            AddBulkContaminantMenuItems();
        }

        /// <summary>
        /// Appends the list-driven contaminant actions to the grid's context menu. Wired in code so the
        /// shipped accession list lives next to the matching logic it drives.
        /// </summary>
        private void AddBulkContaminantMenuItems()
        {
            var menu = ProteinMatrixGrid?.ContextMenu;
            if (menu == null)
                return;

            // These actions now have toolbar buttons, which is where users actually look. Keep them on the context
            // menu too - it is the natural place after right-clicking a row - but guard against adding them twice
            // if this ever runs more than once.
            const string bundledHeader = "Mark bundled contaminants (cRAP subset)...";
            foreach (var existing in menu.Items)
                if (existing is MenuItem mi && string.Equals(mi.Header as string, bundledHeader, StringComparison.Ordinal))
                    return;

            menu.Items.Add(new Separator());

            var bundledItem = new MenuItem { Header = bundledHeader };
            bundledItem.Click += MarkBundledContaminants_Click;
            menu.Items.Add(bundledItem);

            var fileItem = new MenuItem { Header = "Load contaminant list from file..." };
            fileItem.Click += LoadContaminantList_Click;
            menu.Items.Add(fileItem);
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

            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ProteinMatrixControl load annotations] {ex}");
                _proteinAnnotations = new Dictionary<string, FastaParserService.ProteinAnnotation>();
            }

            try
            {
                var dbService = new ProjectDatabaseService(projectDbPath);
                await dbService.EnsureProteinContaminantsTableExistsAsync();
                _contaminantIds = await dbService.LoadContaminantsAsync();

            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ProteinMatrixControl load contaminants] {ex}");
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
                colorMap[uniqueConditions[i]] = ColorMapper.HsvToRgb(hue, 0.25, 0.95); // Low saturation, high value = light pastel
            }

            return colorMap;
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

            ProteinMatrixGrid.Columns.Clear();
            ProteinMatrixGrid.ItemsSource = _fullDataTable.DefaultView;

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
            if (sender is TextBlock textBlock && textBlock.DataContext is System.Data.DataRowView rowView)
            {
                string proteinGroup = rowView["Protein Group"]?.ToString() ?? "";
                string tooltip = _proteinAnnotations != null && _proteinAnnotations.Count > 0
                    ? BuildProteinTooltip(proteinGroup)
                    : null;

                textBlock.ToolTip = !string.IsNullOrEmpty(tooltip) ? tooltip : proteinGroup;
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
            if (_fullDataTable == null) return;

            foreach (DataRowView rowView in _fullDataTable.DefaultView)
            {
                string proteinGroup = rowView["Protein Group"].ToString();
                rowView["Contaminant"] = mark;

                if (mark)
                    _contaminantIds.Add(proteinGroup);
                else
                    _contaminantIds.Remove(proteinGroup);
            }

            ProteinMatrixGrid.Items.Refresh();
            UpdateContaminantStatusText();
        }

        private async void ApplyContaminantsButton_Click(object sender, RoutedEventArgs e)
        {
            // Sync _contaminantIds from the current DataTable checkbox state
            _contaminantIds.Clear();
            if (_fullDataTable != null)
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

                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[ProteinMatrixControl save contaminants] {ex}");
                }
            }

            SearchTextBox.Text = string.Empty;
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

        private void MarkBundledContaminants_Click(object sender, RoutedEventArgs e)
        {
            MarkContaminantsByAccession(
                new HashSet<string>(BundledContaminantAccessions, StringComparer.OrdinalIgnoreCase),
                "bundled cRAP subset");
        }

        private void LoadContaminantList_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFileDialog
            {
                Filter = "Accession lists (*.txt;*.csv;*.tsv;*.fasta;*.fa)|*.txt;*.csv;*.tsv;*.fasta;*.fa|All files (*.*)|*.*",
                Title = "Load Contaminant Accession List"
            };

            if (dialog.ShowDialog() != true)
                return;

            HashSet<string> accessions;
            try
            {
                accessions = ParseAccessionList(File.ReadAllLines(dialog.FileName));
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Could not read the contaminant list: {ex.Message}", "Contaminant List",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            if (accessions.Count == 0)
            {
                MessageBox.Show("No accessions were found in that file.", "Contaminant List",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            string source = $"{accessions.Count} accessions from {Path.GetFileName(dialog.FileName)}";
            MarkContaminantsByAccession(accessions, source);
        }

        /// <summary>
        /// Reads accessions from a plain one-per-line list or from FASTA headers ('>sp|P02768|ALBU_HUMAN'),
        /// so a user can feed the official cRAP FASTA straight in.
        /// </summary>
        private static HashSet<string> ParseAccessionList(IEnumerable<string> fileLines)
        {
            var accessions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var rawLine in fileLines)
            {
                var line = rawLine?.Trim();
                if (string.IsNullOrEmpty(line) || line.StartsWith('#'))
                    continue;

                if (line.StartsWith('>'))
                {
                    var header = line.Substring(1)
                        .Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries)
                        .FirstOrDefault();
                    if (string.IsNullOrEmpty(header))
                        continue;

                    var pipeParts = header.Split('|');
                    accessions.Add(pipeParts.Length >= 2 ? pipeParts[1] : pipeParts[0]);
                    continue;
                }

                var first = line.Split(new[] { ',', '\t', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries)
                    .FirstOrDefault();
                if (!string.IsNullOrEmpty(first))
                    accessions.Add(first);
            }

            return accessions;
        }

        /// <summary>
        /// Marks every protein group containing one of <paramref name="accessions"/>, after showing the user
        /// exactly what will be marked. Matching runs over the whole table, not just the filtered view.
        /// </summary>
        private void MarkContaminantsByAccession(HashSet<string> accessions, string sourceDescription)
        {
            if (_fullDataTable == null || accessions == null || accessions.Count == 0)
            {
                MessageBox.Show("Load a protein matrix first.", "Mark Contaminants",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var matched = new List<DataRow>();
            foreach (DataRow row in _fullDataTable.Rows)
            {
                if (row["Contaminant"] is bool alreadyMarked && alreadyMarked)
                    continue;

                string proteinGroup = row["Protein Group"]?.ToString();
                if (string.IsNullOrEmpty(proteinGroup))
                    continue;

                if (AccessionTokens(proteinGroup).Any(accessions.Contains))
                    matched.Add(row);
            }

            if (matched.Count == 0)
            {
                MessageBox.Show($"No unmarked protein group in this matrix matches the {sourceDescription}.",
                    "Mark Contaminants", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            // Name what will be marked: a contaminant list that quietly removes a real protein is worse than
            // one that removes nothing.
            var previewLines = matched.Take(15).Select(matchedRow =>
            {
                string group = matchedRow["Protein Group"]?.ToString();
                string gene = matchedRow["Gene"]?.ToString();
                return string.IsNullOrEmpty(gene) ? "  " + group : "  " + group + "  (" + gene + ")";
            }).ToList();
            if (matched.Count > 15)
                previewLines.Add("  ... and " + (matched.Count - 15) + " more");

            var answer = MessageBox.Show(
                $"Mark {matched.Count} protein group(s) as contaminants, from the {sourceDescription}?\n\n" +
                string.Join("\n", previewLines) +
                "\n\nNothing is excluded from the analysis until you press Apply Contaminants.",
                "Mark Contaminants", MessageBoxButton.YesNo, MessageBoxImage.Question);

            if (answer != MessageBoxResult.Yes)
                return;

            foreach (var row in matched)
            {
                row["Contaminant"] = true;
                _contaminantIds.Add(row["Protein Group"].ToString());
            }

            ProteinMatrixGrid.Items.Refresh();
            UpdateContaminantStatusText();
        }

        /// <summary>
        /// Yields the accession-like tokens of a protein group, plus the base form of any isoform suffix, so
        /// that 'sp|P02768|ALBU_HUMAN' and 'P02768-2' both match a bare 'P02768'.
        /// </summary>
        private static IEnumerable<string> AccessionTokens(string proteinGroup)
        {
            foreach (var part in proteinGroup.Split(new[] { ';', '|', ',', ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var token = part.Trim();
                if (token.Length == 0)
                    continue;

                yield return token;

                int dash = token.IndexOf('-');
                if (dash > 0)
                    yield return token.Substring(0, dash);
            }
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
            // Zero marked contaminants is not a clean result: the contaminant ratio is then 0 for every run and
            // the ratio cutoff can never exclude anything. Say so, rather than letting an inactive QC step look
            // like a passed one.
            string contaminantInfo = contaminantCount > 0
                ? $" | {contaminantCount} contaminants marked"
                : " | no contaminants marked - contaminant-ratio QC inactive (right-click a row to mark a list)";
            StatusText.Text = $"Displaying {_fullDataTable?.DefaultView.Count ?? 0} proteins across {_currentData?.RawFileNames.Count ?? 0} raw files{hvpInfo}{contaminantInfo}";
        }

        private void SearchTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_fullDataTable == null)
                return;

            var searchText = SearchTextBox.Text.Trim();

            if (string.IsNullOrWhiteSpace(searchText))
            {
                _fullDataTable.DefaultView.RowFilter = "";
            }
            else
            {
                // Escape single quotes to prevent RowFilter injection
                var escaped = searchText.Replace("'", "''");
                _fullDataTable.DefaultView.RowFilter =
                    $"[Protein Group] LIKE '%{escaped}%' OR [Description] LIKE '%{escaped}%' OR [Gene] LIKE '%{escaped}%'";
            }

            // Force all rows to re-render so LoadingRow fires and contaminant highlighting is correct
            ProteinMatrixGrid.Items.Refresh();
            UpdateContaminantStatusText();
        }

        private void ClearSearchButton_Click(object sender, RoutedEventArgs e)
        {
            SearchTextBox.Text = string.Empty;
        }

        private void ExportButton_Click(object sender, RoutedEventArgs e)
        {
            if (_fullDataTable == null || _fullDataTable.DefaultView.Count == 0)
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
                    var columnNames = _fullDataTable.Columns.Cast<DataColumn>()
                        .Select(column => EscapeCsvField(column.ColumnName));
                    writer.WriteLine(string.Join(",", columnNames));

                    foreach (DataRowView rowView in _fullDataTable.DefaultView)
                    {
                        var fields = rowView.Row.ItemArray.Select(field =>
                        {
                            if (field == DBNull.Value)
                                return "";
                            // The quant columns and Var_Std are typeof(double), and this is a COMMA-separated
                            // file: on a comma-decimal locale a culture-formatted "1234,56" silently splits one
                            // cell into two fields and shifts every column right of it. "R" round-trips the
                            // stored value exactly, matching ClassifierEvaluationService.WritePerCellCsv.
                            if (field is double doubleValue)
                                return EscapeCsvField(doubleValue.ToString("R", CultureInfo.InvariantCulture));
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