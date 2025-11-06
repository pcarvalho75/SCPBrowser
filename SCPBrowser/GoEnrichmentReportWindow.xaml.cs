using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;

namespace SCPBrowser
{
    public partial class GoEnrichmentReportWindow : Window
    {
        public GoEnrichmentReportWindow(string runName, RunGoEnrichmentResult enrichmentResult)
        {
            InitializeComponent();

            RunNameText.Text = $"GO Enrichment Analysis: {runName}";

            if (enrichmentResult == null || enrichmentResult.AllSignificantTerms == null || enrichmentResult.AllSignificantTerms.Count == 0)
            {
                SummaryText.Text = "No significant GO term enrichment found for this run.";
                return;
            }

            SummaryText.Text = $"Total significant GO terms: {enrichmentResult.AllSignificantTerms.Count}";

            PopulateGrids(enrichmentResult.AllSignificantTerms);
        }

        private void PopulateGrids(List<GoEnrichmentResult> allTerms)
        {
            // Filter by namespace and take top 20 for each
            var biologicalProcess = allTerms
                .Where(t => t.Namespace == "biological_process")
                .Take(20)
                .Select(t => new GoEnrichmentDisplayItem(t))
                .ToList();

            var molecularFunction = allTerms
                .Where(t => t.Namespace == "molecular_function")
                .Take(20)
                .Select(t => new GoEnrichmentDisplayItem(t))
                .ToList();

            var cellularComponent = allTerms
                .Where(t => t.Namespace == "cellular_component")
                .Take(20)
                .Select(t => new GoEnrichmentDisplayItem(t))
                .ToList();

            BiologicalProcessGrid.ItemsSource = biologicalProcess;
            MolecularFunctionGrid.ItemsSource = molecularFunction;
            CellularComponentGrid.ItemsSource = cellularComponent;
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            this.Close();
        }
    }

    // Helper class for DataGrid binding
    public class GoEnrichmentDisplayItem
    {
        public string GoTermId { get; set; }
        public string GoTermName { get; set; }
        public double PValue { get; set; }
        public double FoldEnrichment { get; set; }
        public string OverlapText { get; set; }

        public GoEnrichmentDisplayItem(GoEnrichmentResult result)
        {
            GoTermId = result.GoTermId;
            GoTermName = result.GoTermName;
            PValue = result.PValue;
            FoldEnrichment = result.FoldEnrichment;
            OverlapText = $"{result.Overlap}/{result.ProteinsInSample}";
        }
    }
}