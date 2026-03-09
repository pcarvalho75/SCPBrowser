# Project Notes

## Protein Coverage Feature

Protein Coverage Feature - COMPLETE (reviewed x2)

New files:
- Services\ProteinCoverageService.cs - On-the-fly FASTA reading, parquet peptide extraction, coverage computation
- Controls\ProteinCoverageControl.xaml + .cs - WPF coverage viewer

Modified files:
- Services\UniProtService.cs - Added ProteinFeature, GetFeatures(), GetSequenceString(), SequenceString + Features
- Controls\PeptideTicControl.xaml - Added "Protein Coverage" tab, x:Name + SelectionChanged on BottomTabControl
- Controls\PeptideTicControl.xaml.cs - InitializeProteinCoverage(), RefreshProteinCoverageList(), BottomTabControl_SelectionChanged, UpdateChart syncs data to coverage panel
- MainWindow.xaml.cs - InitializeProteinCoverageAsync(), FASTA path saved on import

Review 2 fixes applied:
- Race condition guard (_isLoading flag + Load button disable during computation)
- Null accession check before ComputeCoverageAsync call
- Domain label Substring crash (maxChars guard against exceeding Description.Length)
- Division by zero guard (SequenceLength == 0 in RedrawAll)
- Stale ProteomicsData reference (UpdateProteomicsData called from UpdateChart)
- Cached frozen brushes for sequence text
- SizeChanged guarded against zero-width
- Coverage tab refresh only when visible + on tab switch

Build: 0 errors
