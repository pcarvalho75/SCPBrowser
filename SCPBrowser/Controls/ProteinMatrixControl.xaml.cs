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

            // Build HVP lookup dictionary for fast access
            _hvpLookup = hvpResults?.ToDictionary(h => h.ProteinId, h => h.VarianceStandardized)
                         ?? new Dictionary<string, double>();

            // Generate header colors from biological conditions
            _bioConditionHeaderColors = GenerateBioConditionHeaderColors(data);

            RefreshMatrix();
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

        private void RefreshMatrix()
        {
            if (_currentData == null || _currentData.ProteinQuantMatrix.Count == 0)
            {
                StatusText.Text = "No data to display";
                ExportButton.IsEnabled = false;
                return;
            }

            _fullDataTable = new DataTable();

            // Column 1: Protein Group
            _fullDataTable.Columns.Add("Protein Group", typeof(string));

            // Column 2: Variance Standardized (HVP score) - only if we have HVP data
            bool hasHvpData = _hvpLookup != null && _hvpLookup.Count > 0;
            if (hasHvpData)
            {
                _fullDataTable.Columns.Add("Var. Std.", typeof(double));
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
                row["Protein Group"] = protein;

                // Add HVP score if available
                if (hasHvpData)
                {
                    if (_hvpLookup.TryGetValue(protein, out double varStd))
                    {
                        row["Var. Std."] = varStd;
                    }
                    else
                    {
                        row["Var. Std."] = DBNull.Value;
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
            ProteinMatrixGrid.ItemsSource = _filteredDataTable.DefaultView;

            int hvpCount = _hvpResults?.Count(h => h.IsHighlyVariable) ?? 0;
            string hvpInfo = hasHvpData ? $" | {hvpCount} HVPs" : "";
            StatusText.Text = $"Displaying {_filteredDataTable.Rows.Count} proteins across {_currentData.RawFileNames.Count} raw files{hvpInfo}";
            ExportButton.IsEnabled = true;
        }

        private void ProteinMatrixGrid_AutoGeneratingColumn(object sender, DataGridAutoGeneratingColumnEventArgs e)
        {
            if (e.PropertyName == "Protein Group")
            {
                var textColumn = e.Column as DataGridTextColumn;
                if (textColumn != null)
                {
                    textColumn.Width = new DataGridLength(250, DataGridLengthUnitType.Pixel);
                }
            }
            else if (e.PropertyName == "Var. Std.")
            {
                // HVP column - special formatting
                var textColumn = e.Column as DataGridTextColumn;
                if (textColumn != null)
                {
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
                    if (proteinName.IndexOf(searchText, StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        _filteredDataTable.ImportRow(row);
                    }
                }
            }

            ProteinMatrixGrid.ItemsSource = _filteredDataTable.DefaultView;
            StatusText.Text = $"Displaying {_filteredDataTable.Rows.Count} proteins across {_currentData.RawFileNames.Count} raw files";
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
    }
}