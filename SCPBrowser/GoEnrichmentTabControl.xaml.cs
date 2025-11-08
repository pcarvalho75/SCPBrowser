using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Controls;

namespace SCPBrowser
{
    public partial class GoEnrichmentTabControl : UserControl
    {
        public GoEnrichmentTabControl()
        {
            InitializeComponent();
            ClearData();
        }

        /// <summary>
        /// Updates the GO enrichment display with selected points
        /// </summary>
        public void UpdateGoEnrichment(List<DataPoint> selectedPoints, Dictionary<string, RunGoEnrichmentResult> goEnrichmentResults)
        {
            if (selectedPoints == null || selectedPoints.Count == 0 || goEnrichmentResults == null)
            {
                ClearData();
                return;
            }

            // Build grid data
            var gridData = new List<GoEnrichmentDisplayRow>();
            int runsWithEnrichment = 0;

            foreach (var point in selectedPoints)
            {
                if (goEnrichmentResults.ContainsKey(point.RunName))
                {
                    var result = goEnrichmentResults[point.RunName];

                    if (!string.IsNullOrEmpty(result.TopGoTermId) && result.AllSignificantTerms != null && result.AllSignificantTerms.Count > 0)
                    {
                        runsWithEnrichment++;
                        gridData.Add(new GoEnrichmentDisplayRow
                        {
                            RunName = point.RunName,
                            TopGoTermId = result.TopGoTermId,
                            TopGoTermName = result.TopGoTermName,
                            Namespace = result.Namespace,
                            PValue = result.PValue,
                            FoldEnrichment = result.FoldEnrichment,
                            OverlapText = $"{result.OverlapCount}/{result.AllSignificantTerms.First().ProteinsInSample}"
                        });
                    }
                }
            }

            // Update summary
            if (runsWithEnrichment > 0)
            {
                SummaryText.Text = $"{runsWithEnrichment} of {selectedPoints.Count} selected run(s) have significant GO term enrichment";
            }
            else
            {
                SummaryText.Text = $"No significant GO term enrichment found in {selectedPoints.Count} selected run(s)";
            }

            // Update grid
            GoTermsGrid.ItemsSource = gridData;
        }

        /// <summary>
        /// Clears all GO enrichment data
        /// </summary>
        public void ClearData()
        {
            SummaryText.Text = "Select points to view GO enrichment analysis";
            GoTermsGrid.ItemsSource = null;
        }
    }

    /// <summary>
    /// Display class for GO enrichment grid rows
    /// </summary>
    public class GoEnrichmentDisplayRow
    {
        public string RunName { get; set; }
        public string TopGoTermId { get; set; }
        public string TopGoTermName { get; set; }
        public string Namespace { get; set; }
        public double PValue { get; set; }
        public double FoldEnrichment { get; set; }
        public string OverlapText { get; set; }
    }
}