# Project Notes

## ProteinCoverageControl Improvements

Improved ProteinCoverageControl with: axis labels on all panels (adaptive intervals), domain color legend (WrapPanel, zoom-aware), intensity Y-axis log scale with grid lines (50px left margin), sequence text batching (limit 10K AA), synchronized zoom/pan across all canvases (scroll=zoom, drag=pan, min 20 residues, hand/scrollAll cursors), copy stats button, export PNG with DPI selector (300/600). Export hides zoom bar, renders at 1200px width, uses _isExporting guard to prevent SizeChanged re-entry. Domain feature filtering uses 1-based UniProt positions compared against 0-based view bounds; the `>` operator on 1-based values is correct (equivalent to `>=` on 0-based). Path ambiguity resolved with System.Windows.Shapes.Path due to System.IO import. DrawPeptideBars and DrawIntensityProfile draw axis labels even on early return (no data in view).

## Concurrency Fixes

- [2026-03-10 10:09] Fixed 3 major concurrency issues: (1) DataFilterService.ApplyFiltersAsync now uses SemaphoreSlim to serialize concurrent filter passes - event fires from FilteredDataChanged raised outside the lock to avoid deadlocks. (2) ProteinMatrixTab_ContaminantsUpdated now builds a NEW dictionary and assigns atomically instead of Clear()+rebuild on the shared TargetProteinRatioPerFile. (3) CloseProject cancels a CancellationTokenSource; all async event handlers guard with _hasOpenProject and null-conditional checks to avoid NullReferenceException from nulled services.
