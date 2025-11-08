using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace SCPBrowser
{
    public partial class PeptideTicControl : UserControl
    {
        private ProteomicsData _currentData;
        private bool _isInitialized = false;
        private Dictionary<string, CellTypePredictionResult> _cellTypePredictions;
        private Dictionary<string, Color> _cellTypeColorMap;
        private Dictionary<string, RunGoEnrichmentResult> _goEnrichmentResults;
        private Dictionary<string, Color> _goTermColorMap;
        private List<DataPoint> _currentSelectedPoints = new List<DataPoint>();

        public PeptideTicControl()
        {
            InitializeComponent();

            ScatterPlot.SelectionChanged += ScatterPlot_SelectionChanged;
            ScatterPlot.PointClicked += ScatterPlot_PointClicked;
            SelectedPointsGridPanel.GridSelectionChanged += SelectedPointsGridPanel_GridSelectionChanged;

            _isInitialized = true;
        }

        public void UpdateChart(ProteomicsData data)
        {
            _currentData = data;
            RefreshChart();
        }

        public void SetImageBaseDirectory(string directory)
        {
            RunDetailPanel.SetImageBaseDirectory(directory);
        }

        public void SetCellTypePredictions(Dictionary<string, CellTypePredictionResult> predictions, Dictionary<string, Color> colorMap)
        {
            _cellTypePredictions = predictions;
            _cellTypeColorMap = colorMap;
            ColorByCellTypeRadio.IsEnabled = predictions != null && predictions.Count > 0;

            if (_currentData != null)
            {
                RefreshChart();
            }
        }

        public void SetGoEnrichmentResults(Dictionary<string, RunGoEnrichmentResult> results, Dictionary<string, Color> colorMap)
        {
            _goEnrichmentResults = results;
            _goTermColorMap = colorMap;
            ColorByGoTermRadio.IsEnabled = results != null && results.Count > 0;

            // Update GO tab if we have selected points
            if (_currentSelectedPoints.Count > 0)
            {
                GoEnrichmentTab.UpdateGoEnrichment(_currentSelectedPoints, _goEnrichmentResults);
            }
        }

        private void ColorMode_Changed(object sender, RoutedEventArgs e)
        {
            if (!_isInitialized || ColorByCellTypeRadio == null)
                return;

            if (_currentData != null)
            {
                RefreshChart();
            }
        }

        private void RefreshChart()
        {
            if (_currentData == null || _currentData.PeptideCountPerFile.Count == 0)
            {
                SelectedPointsGridPanel.ClearGrid();
                GoEnrichmentTab.ClearData();
                ClearSelectionButton.IsEnabled = false;
                RunDetailPanel.ClearDetails();
                return;
            }

            var options = new ScatterPlotOptions
            {
                UseLogLog = LogLogCheckBox.IsChecked == true,
                UseCellTypeColoring = ColorByCellTypeRadio.IsChecked == true,
                CellTypePredictions = _cellTypePredictions,
                CellTypeColorMap = _cellTypeColorMap,
                GoEnrichmentResults = _goEnrichmentResults,
                GoTermColorMap = _goTermColorMap
            };

            ScatterPlot.UpdatePlot(_currentData, options);

            UpdatePlotHeader(_currentData.PeptideCountPerFile.Count, options.UseCellTypeColoring);
        }

        private void UpdatePlotHeader(int fileCount, bool useCellTypeColoring)
        {
            if (useCellTypeColoring && _cellTypePredictions != null)
            {
                int predictedCount = _cellTypePredictions.Count(p => p.Value.TopCellType != null);
                PlotGroupBoxHeader.Text = $"Peptides vs Total Ion Current per Raw File ({fileCount} files, {predictedCount} with cell type predictions)";
            }
            else
            {
                PlotGroupBoxHeader.Text = $"Peptides vs Total Ion Current per Raw File ({fileCount} files)";
            }
        }

        private void LogLogCheckBox_Changed(object sender, RoutedEventArgs e)
        {
            if (_currentData != null)
            {
                RefreshChart();
            }
        }

        private void ClearSelectionButton_Click(object sender, RoutedEventArgs e)
        {
            ScatterPlot.ClearSelection();
            SelectedPointsGridPanel.ClearGrid();
            GoEnrichmentTab.ClearData();
            ClearSelectionButton.IsEnabled = false;
            RunDetailPanel.ClearDetails();
            _currentSelectedPoints.Clear();
        }

        private void ScatterPlot_SelectionChanged(object sender, PlotSelectionChangedEventArgs e)
        {
            _currentSelectedPoints = e.SelectedPoints;

            if (e.SelectedPoints.Count > 0)
            {
                var gridData = e.SelectedPoints.Select(p => new SelectedPointData
                {
                    RunName = p.RunName,
                    PeptideCount = p.PeptideCount,
                    TicValue = p.TicValue,
                    ProteinCount = p.ProteinCount,
                    TrypsinRatioPercent = $"{p.TrypsinRatio * 100:F2}%",
                    CellType = p.PredictedCellType ?? "Unknown"
                }).ToList();

                SelectedPointsGridPanel.UpdateGrid(gridData);
                GoEnrichmentTab.UpdateGoEnrichment(e.SelectedPoints, _goEnrichmentResults);
                ClearSelectionButton.IsEnabled = true;

                if (e.SelectedPoints.Count == 1)
                {
                    RunDetailPanel.ShowRunDetails(e.SelectedPoints[0], _goEnrichmentResults);
                }
                else
                {
                    RunDetailPanel.ShowSelectionSummary(e.SelectedPoints, _goEnrichmentResults);
                }
            }
            else
            {
                SelectedPointsGridPanel.ClearGrid();
                GoEnrichmentTab.ClearData();
                ClearSelectionButton.IsEnabled = false;
                RunDetailPanel.ClearDetails();
            }
        }

        private void ScatterPlot_PointClicked(object sender, PointInteractionEventArgs e)
        {
            RunDetailPanel.ShowRunDetails(e.DataPoint, _goEnrichmentResults);

            // Also update GO enrichment tab for the clicked point
            if (e.DataPoint != null)
            {
                GoEnrichmentTab.UpdateGoEnrichment(new List<DataPoint> { e.DataPoint }, _goEnrichmentResults);
            }
        }

        private void SelectedPointsGridPanel_GridSelectionChanged(object sender, SelectedPointData selectedData)
        {
            if (selectedData == null)
                return;

            var dataPoint = _currentSelectedPoints.FirstOrDefault(p => p.RunName == selectedData.RunName);
            if (dataPoint != null)
            {
                RunDetailPanel.ShowRunDetails(dataPoint, _goEnrichmentResults);
                ScatterPlot.HighlightPoints(new List<string> { selectedData.RunName });
            }
        }
    }
}