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
            int runsWithNoAnnotations = 0;
            int runsWithAnnotationsButNoEnrichment = 0;

            foreach (var point in selectedPoints)
            {
                if (goEnrichmentResults.TryGetValue(point.RunName, out var result))
                {
                    if (result.HasSignificantTerms)
                    {
                        runsWithEnrichment++;
                        totalSignificantTerms += result.AllSignificantTerms.Count;

                        foreach (var term in result.AllSignificantTerms)
                        {
                            _allGoTerms.Add(new GoEnrichmentDisplayRow
                            {
                                RunName = point.RunName,
                                GoTermId = term.TermIdFormatted,
                                GoTermName = term.TermName,
                                Namespace = NamespaceToString(term.Namespace),
                                PValue = term.FdrCorrectedPValue,
                                FoldEnrichment = term.FoldEnrichment,
                                OverlapText = $"{term.SampleInTerm}/{term.SampleTotal}"
                            });
                        }
                    }
                    else if (!result.HasAnnotatedGenes)
                    {
                        runsWithNoAnnotations++;
                    }
                    else
                    {
                        runsWithAnnotationsButNoEnrichment++;
                    }
                }
            }

            // Build summary message
            if (runsWithEnrichment > 0)
            {
                SummaryText.Text = $"{runsWithEnrichment} of {selectedPoints.Count} selected run(s) have significant GO term enrichment ({totalSignificantTerms} total terms)";
            }
            else if (runsWithNoAnnotations == selectedPoints.Count)
            {
                SummaryText.Text = $"No GO annotations found for genes in {selectedPoints.Count} selected run(s). Check gene name format or species settings.";
            }
            else if (runsWithNoAnnotations > 0)
            {
                SummaryText.Text = $"No significant enrichment. {runsWithNoAnnotations} run(s) had no annotated genes, {runsWithAnnotationsButNoEnrichment} had annotations but no enrichment.";
            }
            else
            {
                SummaryText.Text = $"No significant GO term enrichment found in {selectedPoints.Count} selected run(s)";
            }

            ApplyNamespaceFilter();
        }

        private string NamespaceToString(BioTessera.GO.GoNamespace ns)
        {
            return ns switch
            {
                BioTessera.GO.GoNamespace.BiologicalProcess => "biological_process",
                BioTessera.GO.GoNamespace.MolecularFunction => "molecular_function",
                BioTessera.GO.GoNamespace.CellularComponent => "cellular_component",
                _ => "unknown"
            };
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