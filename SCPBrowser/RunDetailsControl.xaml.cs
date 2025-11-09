using SCPBrowser.GOTools;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace SCPBrowser
{
    public partial class RunDetailControl : UserControl
    {
        private List<DataPoint> _currentSelectedPoints = new List<DataPoint>();
        private int _currentDetailIndex = 0;
        private string _selectedRunName;
        private Dictionary<string, RunGoEnrichmentResult> _goEnrichmentResults;
        private string _imageBaseDirectory;

        public RunDetailControl()
        {
            InitializeComponent();
            ClearDetails();
        }

        public void SetImageBaseDirectory(string directory)
        {
            _imageBaseDirectory = directory;
        }

        /// <summary>
        /// Displays details for a single run
        /// </summary>
        public void ShowRunDetails(DataPoint dataPoint, Dictionary<string, RunGoEnrichmentResult> goResults)
        {
            _goEnrichmentResults = goResults;
            _currentSelectedPoints.Clear();
            _currentSelectedPoints.Add(dataPoint);
            _currentDetailIndex = 0;

            SummarySection.Visibility = Visibility.Collapsed;
            NavigationPanel.Visibility = Visibility.Collapsed;

            DisplayRunDetails(dataPoint);
        }

        /// <summary>
        /// Displays a summary and navigation for multiple runs
        /// </summary>
        public void ShowSelectionSummary(List<DataPoint> selectedPoints, Dictionary<string, RunGoEnrichmentResult> goResults)
        {
            _goEnrichmentResults = goResults;
            _currentSelectedPoints = selectedPoints;
            _currentDetailIndex = 0;

            SummarySection.Visibility = Visibility.Visible;
            NavigationPanel.Visibility = Visibility.Visible;

            DisplaySelectionSummary(selectedPoints);
            UpdateNavigationDisplay();

            if (selectedPoints.Count > 0)
            {
                DisplayRunDetails(selectedPoints[0]);
            }
        }

        /// <summary>
        /// Clears all details from the panel
        /// </summary>
        public void ClearDetails()
        {
            Console.WriteLine("Run details cleared");
            _currentSelectedPoints.Clear();
            _goEnrichmentResults = null;
            _currentDetailIndex = 0;

            SummarySection.Visibility = Visibility.Collapsed;
            NavigationPanel.Visibility = Visibility.Collapsed;

            RunImage.Source = null;
            DetailsText.Text = "Select a run to view details";
            _selectedRunName = null;
        }

        private void DisplaySelectionSummary(List<DataPoint> selectedPoints)
        {
            int totalRuns = selectedPoints.Count;
            int avgPeptides = (int)selectedPoints.Average(p => p.PeptideCount);
            int avgProteins = (int)selectedPoints.Average(p => p.ProteinCount);
            double avgTic = selectedPoints.Average(p => p.TicValue);
            double avgTrypsinRatio = selectedPoints.Average(p => p.TrypsinRatio);

            string summaryText = $"Runs: {totalRuns}\n" +
                                $"Avg Peptides: {avgPeptides:N0}\n" +
                                $"Avg Proteins: {avgProteins:N0}\n" +
                                $"Avg TIC: {avgTic:E2}\n" +
                                $"Avg Target Ratio: {avgTrypsinRatio * 100:F2}%";

            // Add cell type distribution if available
            var cellTypeCounts = selectedPoints
                .Where(p => !string.IsNullOrEmpty(p.PredictedCellType))
                .GroupBy(p => p.PredictedCellType)
                .OrderByDescending(g => g.Count())
                .ToList();

            if (cellTypeCounts.Any())
            {
                summaryText += "\n\nCell Types:";
                foreach (var group in cellTypeCounts)
                {
                    summaryText += $"\n  {group.Key}: {group.Count()}";
                }
            }

            SummaryText.Text = summaryText;
        }

        private void UpdateNavigationDisplay()
        {
            if (_currentSelectedPoints.Count == 0)
                return;

            NavigationText.Text = $"Run {_currentDetailIndex + 1} of {_currentSelectedPoints.Count}";
            PrevButton.IsEnabled = _currentDetailIndex > 0;
            NextButton.IsEnabled = _currentDetailIndex < _currentSelectedPoints.Count - 1;
        }

        private void PrevButton_Click(object sender, RoutedEventArgs e)
        {
            if (_currentDetailIndex > 0)
            {
                _currentDetailIndex--;
                DisplayRunDetails(_currentSelectedPoints[_currentDetailIndex]);
                UpdateNavigationDisplay();
            }
        }

        private void NextButton_Click(object sender, RoutedEventArgs e)
        {
            if (_currentDetailIndex < _currentSelectedPoints.Count - 1)
            {
                _currentDetailIndex++;
                DisplayRunDetails(_currentSelectedPoints[_currentDetailIndex]);
                UpdateNavigationDisplay();
            }
        }

        private void DisplayRunDetails(DataPoint dataPoint)
        {
            if (dataPoint == null)
            {
                ClearDetails();
                return;
            }

            string detailsText = $"Run: {dataPoint.RunName}\n\n" +
                                $"Peptides: {dataPoint.PeptideCount:N0}\n" +
                                $"Protein Groups: {dataPoint.ProteinCount:N0}\n" +
                                $"Total Ion Current: {dataPoint.TicValue:E2}\n" +
                                $"Trypsin Ratio: {dataPoint.TrypsinRatio * 100:F2}%";

            if (!string.IsNullOrEmpty(dataPoint.PredictedCellType))
            {
                detailsText += $"\n\nPredicted Cell Type: {dataPoint.PredictedCellType}";

                if (dataPoint.PredictionScore != null)
                {
                    detailsText += $"\n\nPrediction Details:";
                    detailsText += $"\n  Composite Score: {dataPoint.PredictionScore.CompositeScore:F3}";
                    detailsText += $"\n  Spearman Corr: {dataPoint.PredictionScore.SpearmanCorrelation:F3}";
                    detailsText += $"\n  Specificity Score: {dataPoint.PredictionScore.SpecificityScore:F3}";
                    detailsText += $"\n  P-value: {dataPoint.PredictionScore.HypergeometricPValue:E2}";
                }
            }

            DetailsText.Text = detailsText;

            if (!string.IsNullOrEmpty(_imageBaseDirectory))
            {
                string imagePath = ImageLoader.GetImagePathForRun(_imageBaseDirectory, dataPoint.RunName);
                if (!string.IsNullOrEmpty(imagePath))
                {
                    var bitmap = ImageLoader.LoadImage(imagePath);
                    if (bitmap != null)
                    {
                        RunImage.Source = bitmap;
                    }
                    else
                    {
                        RunImage.Source = null;
                    }
                }
                else
                {
                    RunImage.Source = null;
                }
            }
            else
            {
                RunImage.Source = null;
            }

            // Enable GO enrichment button if we have results for this run
            if (_goEnrichmentResults != null &&
                _goEnrichmentResults.ContainsKey(dataPoint.RunName) &&
                _goEnrichmentResults[dataPoint.RunName].AllSignificantTerms != null &&
                _goEnrichmentResults[dataPoint.RunName].AllSignificantTerms.Count > 0)
            {
                _selectedRunName = dataPoint.RunName;
            }
            else
            {
                _selectedRunName = null;
            }
        }

    }
}