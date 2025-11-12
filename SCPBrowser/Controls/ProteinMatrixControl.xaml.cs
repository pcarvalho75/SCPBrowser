using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using SCPBrowser.Services;

namespace SCPBrowser
{
    public partial class ProteinMatrixControl : UserControl
    {
        private ProteomicsData _currentData;
        private DataTable _fullDataTable;
        private DataTable _filteredDataTable;

        public ProteinMatrixControl()
        {
            InitializeComponent();
        }

        public void UpdateMatrix(ProteomicsData data)
        {
            _currentData = data;
            RefreshMatrix();
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
            _fullDataTable.Columns.Add("Protein Group", typeof(string));

            foreach (var rawFile in _currentData.RawFileNames)
            {
                _fullDataTable.Columns.Add(rawFile, typeof(double));
            }

            foreach (var protein in _currentData.ProteinQuantMatrix.Keys.OrderBy(p => p))
            {
                var row = _fullDataTable.NewRow();
                row["Protein Group"] = protein;

                foreach (var rawFile in _currentData.RawFileNames)
                {
                    if (_currentData.ProteinQuantMatrix[protein].ContainsKey(rawFile))
                    {
                        row[rawFile] = _currentData.ProteinQuantMatrix[protein][rawFile];
                    }
                    else
                    {
                        row[rawFile] = 0.0;
                    }
                }

                _fullDataTable.Rows.Add(row);
            }

            _filteredDataTable = _fullDataTable.Copy();
            ProteinMatrixGrid.ItemsSource = _filteredDataTable.DefaultView;

            StatusText.Text = $"Displaying {_filteredDataTable.Rows.Count} proteins across {_currentData.RawFileNames.Count} raw files";
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
            else
            {
                var textColumn = e.Column as DataGridTextColumn;
                if (textColumn != null && textColumn.Binding is System.Windows.Data.Binding binding)
                {
                    binding.StringFormat = "N2";
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