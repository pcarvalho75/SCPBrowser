using SCPBrowser.GOTools;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace SCPBrowser
{
    public partial class GoEnrichmentTabControl : UserControl
    {
        private List<DataPoint> _currentSelectedPoints;
        private Dictionary<string, RunGoEnrichmentResult> _currentGoEnrichmentResults;
        private List<GoEnrichmentDisplayRow> _allGoTerms;

        public GoEnrichmentTabControl()
        {
            InitializeComponent();
            ClearData();
        }

        public void UpdateGoEnrichment(List<DataPoint> selectedPoints, Dictionary<string, RunGoEnrichmentResult> goEnrichmentResults)
        {
            _currentSelectedPoints = selectedPoints;
            _currentGoEnrichmentResults = goEnrichmentResults;

            if (selectedPoints == null || selectedPoints.Count == 0 || goEnrichmentResults == null)
            {
                ClearData();
                return;
            }

            _allGoTerms = new List<GoEnrichmentDisplayRow>();
            int runsWithEnrichment = 0;
            int totalSignificantTerms = 0;

            foreach (var point in selectedPoints)
            {
                if (goEnrichmentResults.ContainsKey(point.RunName))
                {
                    var result = goEnrichmentResults[point.RunName];

                    if (result.AllSignificantTerms != null && result.AllSignificantTerms.Count > 0)
                    {
                        runsWithEnrichment++;
                        totalSignificantTerms += result.AllSignificantTerms.Count;

                        foreach (var term in result.AllSignificantTerms)
                        {
                            _allGoTerms.Add(new GoEnrichmentDisplayRow
                            {
                                RunName = point.RunName,
                                GoTermId = term.GoTermId,
                                GoTermName = term.GoTermName,
                                Namespace = term.Namespace,
                                PValue = term.PValue,
                                FoldEnrichment = term.FoldEnrichment,
                                OverlapText = $"{term.Overlap}/{term.ProteinsInSample}"
                            });
                        }
                    }
                }
            }

            if (runsWithEnrichment > 0)
            {
                SummaryText.Text = $"{runsWithEnrichment} of {selectedPoints.Count} selected run(s) have significant GO term enrichment ({totalSignificantTerms} total terms)";
            }
            else
            {
                SummaryText.Text = $"No significant GO term enrichment found in {selectedPoints.Count} selected run(s)";
            }

            ApplyNamespaceFilter();
        }

        public void ClearData()
        {
            Console.WriteLine("GO Enrichment Data Cleared");
            _currentSelectedPoints = null;
            _currentGoEnrichmentResults = null;
            _allGoTerms = null;
            SummaryText.Text = "Select points to view GO enrichment analysis";
            StatisticsText.Text = "0 GO terms displayed";
            GoTermsGrid.ItemsSource = null;
        }

        private void NamespaceFilter_Changed(object sender, RoutedEventArgs e)
        {
            ApplyNamespaceFilter();
        }

        private void ApplyNamespaceFilter()
        {
            if (GoTermsGrid == null || BiologicalProcessRadio == null || MolecularFunctionRadio == null || CellularComponentRadio == null)
            {
                return;
            }
            if (_allGoTerms == null || _allGoTerms.Count == 0)
            {

                GoTermsGrid.ItemsSource = null;
                StatisticsText.Text = "0 GO terms displayed";
                return;

            }

            List<GoEnrichmentDisplayRow> filteredTerms;

            if (BiologicalProcessRadio.IsChecked == true)
            {
                filteredTerms = _allGoTerms.Where(t => t.Namespace == "biological_process").ToList();
            }
            else if (MolecularFunctionRadio.IsChecked == true)
            {
                filteredTerms = _allGoTerms.Where(t => t.Namespace == "molecular_function").ToList();
            }
            else if (CellularComponentRadio.IsChecked == true)
            {
                filteredTerms = _allGoTerms.Where(t => t.Namespace == "cellular_component").ToList();
            }
            else
            {
                filteredTerms = _allGoTerms;
            }

            GoTermsGrid.ItemsSource = filteredTerms.OrderBy(t => t.PValue).ToList();

            var bpCount = filteredTerms.Count(t => t.Namespace == "biological_process");
            var mfCount = filteredTerms.Count(t => t.Namespace == "molecular_function");
            var ccCount = filteredTerms.Count(t => t.Namespace == "cellular_component");

            StatisticsText.Text = $"{filteredTerms.Count} GO terms displayed (BP: {bpCount}, MF: {mfCount}, CC: {ccCount})";
        }
    }

    public class GoEnrichmentDisplayRow
    {
        public string RunName { get; set; }
        public string GoTermId { get; set; }
        public string GoTermName { get; set; }
        public string Namespace { get; set; }
        public double PValue { get; set; }
        public double FoldEnrichment { get; set; }
        public string OverlapText { get; set; }
    }
}