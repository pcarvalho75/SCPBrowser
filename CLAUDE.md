# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

A Windows desktop tool suite for **single-cell proteomics (SCP)** analysis. The flagship app, **SCPBrowser**, imports DIA-NN search results (Parquet), organizes runs into plates/conditions inside a per-project SQLite database, and provides interactive QC, dimensionality reduction, batch correction, and cell-type classification. A sister app, **TransmutationLearning**, builds cross-modal (transcriptomics → proteomics) cell-type classifiers. Everything is C#/.NET 10 + WPF.

## Build, run, "test"

Prerequisites — all are hard requirements:
- **.NET 10 SDK** and **Windows** (WPF; the apps target `net10.0-windows7.0`). `bin/` also contains stale `net8.0-windows` output from an earlier target — ignore it; the current target is net10.0.
- **The sibling `Voronoi` repo must be checked out next to this one** at `../Voronoi` (i.e. `repos/pcarvalho75/Voronoi/`). It supplies `BioTessera` and `RevigoCore`, which SCPBrowser references by relative path. Without it, the solution will not restore or build.
- `SCPBrowser/DLLs/PatternTools.dll` is a **vendored binary** committed to the repo (referenced directly, not via NuGet).

```powershell
dotnet build SCPBrowser.sln              # builds all projects + the external Voronoi refs
dotnet run --project SCPBrowser          # launch the main SCP browser GUI
dotnet run --project TransmutationLearning   # launch the classifier-development GUI
dotnet run --project PCANipals           # "tests": runs the NIPALS PCA self-test harness (see below)
```

There is **no unit-test framework** (no xUnit/NUnit/MSTest project). `PCANipals/Program.cs` is a hand-written console harness that runs three PCA validation cases (basic, with missing values, proteomics-like matrix) and prints results — that is the closest thing to a test suite. When changing `NipalsAlgorithm`, run it and eyeball the output.

`.claude/settings.local.json` already allows `dotnet build`.

## Solution layout & dependency graph

Five projects in `SCPBrowser.sln`:

| Project | Kind | Role |
|---|---|---|
| `SCPBrowser` | WPF exe | Main SCP data browser (the bulk of the code) |
| `TransmutationLearning` | WPF exe | Cross-modal classifier development; **references SCPBrowser** and reuses its `ParquetDataService` |
| `PCANipals` | Console exe | NIPALS PCA implementation (handles missing values) + self-test harness |
| `TLearningWebSite` | `.esproj` | Static HTML/CSS marketing/docs site for the Transmutation method (no npm build needed) |
| `BioTessera`, `RevigoCore` | external | Live in the sibling `../Voronoi` repo; GO-term/proteome mapping support |

**The reference chain matters.** SCPBrowser does *not* reference PCANipals directly — it gets `NipalsAlgorithm` **transitively through BioTessera** (`SCPBrowser → BioTessera → PCANipals`). That is why `ScatterPlotControl.xaml.cs` can `using PCANipals` despite no PCANipals entry in `SCPBrowser.csproj`. SCPBrowser's direct refs are BioTessera, RevigoCore, and PatternTools.dll.

## SCPBrowser architecture

Plain WPF code-behind (no MVVM framework, no DI container). `MainWindow` is the orchestrator; UI is composed of `UserControl`s under `Controls/` and modal `Dialogs/`. Stateless-ish algorithms live under `Services/`.

**Project = SQLite file.** A "project" is a `.db` opened/created by `ProjectDatabaseService` (extends `DatabaseServiceBase`, uses `Microsoft.Data.Sqlite`). `App.xaml.cs` calls `SQLitePCL.Batteries.Init()` at startup. Core tables: `project_info`, `plates`, `parquet_imports`, `raw_files`, `protein_quant_summary`, `raw_file_cell_type_classifications`, and a key/value `project_settings`. Hierarchy: a **plate** (technical run batch — instrument/operator/date, no biology) contains **parquet imports**, which contain **raw files** (one LC-MS run = one cell/sample, carrying the **biological condition**).

**Import flow.** `ImportParquetDialog` → `ParquetDataService` (uses `Parquet.Net`) parses DIA-NN columns (Run, Protein.Group, Modified.Sequence, quantities, TIC) into an in-memory `ProteomicsData` (notably `ProteinQuantMatrix: Dict<proteinId, Dict<rawFile, abundance>>`), assigns plate + condition, and persists. `ProteomicsDataConverter` handles format normalization.

**The central view is a hand-rolled scatter plot.** `ScatterPlotControl` + `PlotRender.cs` draw directly onto a WPF `Canvas` (ellipses/lines/text, custom data↔screen transforms) — **not** ScottPlot. A point = one run/cell; default axes are peptide count vs total ion current, colored by contaminant ratio (Viridis, via `ColorMapper`). `SelectionManager` implements lasso/polygon gating (ray-cast point-in-polygon) with coordinates stored in *data* space so selections survive zoom/pan. **ScottPlot is used only** in `ProteinHistogramControl` and `ProteinDistributionControl`.

**Filtering is a staged pipeline.** `DataFilterService` snapshots data through stages (plate → protein-count cutoff → contaminant-ratio → manual exclusion) and raises `FilteredDataChanged`. It serializes passes with a `SemaphoreSlim` and fires the event *outside* the lock (see Gotchas).

**Analysis services** (one line each):
- `NipalsAlgorithm` (PCANipals) — NIPALS PCA that tolerates missing values (NaN) via pairwise-complete support tracking; the app uses this for PCA, and the `UMAP` NuGet package for UMAP (seeded for reproducibility; configured via `Models/DimensionReductionSettings`).
- `CombatService` — ComBat empirical-Bayes batch-effect correction (Johnson 2007), built on `MathNet.Numerics`; requires imputed (NaN-free) input.
- `HighlyVariableProteinsService` — Seurat-v3 VST highly-variable-protein ranking (LOESS variance~mean).
- `CellTypePredictor` / `CellTypeClassificationManager` / `CellTypeClassificationService` — classify each run against a transcriptomic reference by combining Spearman correlation + IDF-style specificity + hypergeometric marker enrichment; results cached to the DB.
- `ProteinCoverageService` — on-the-fly FASTA + Parquet-peptide coverage for the `ProteinCoverageControl` viewer.
- `UniProtService`, `GoTools/GoEnrichmentManager` + `GoTermResolver` (with RevigoCore), `FastaParserService`, `PLPExportService`, transcriptomic import (`TranscriptomicTsvParser`, `TranscriptomicConverterUtility`).

## TransmutationLearning architecture

Bootstraps SCP cell-type classifiers from transcriptomic labels. End-to-end: load DIA-NN Parquet + cell-type metadata → filter by classification confidence → select discriminative protein markers → refine ("distill") assignments → validate → export a `.pref` proteomics-reference file. `TransmutationControl.xaml.cs` (~2k lines) is the UI orchestrator; `TransmutationDataModel.cs` holds the models.

Pipeline: `FeatureSelectionService` (Kruskal-Wallis per protein + Benjamini-Hochberg FDR + specificity ranking) → `KnnDistillationService` (k-NN over cosine-intensity + Jaccard-detection similarity, optionally warped by STRING PPI scores — "biological gravity") → `IterativeDistillationService` (relax thresholds and re-distill until CV accuracy plateaus) → `ValidationService` (self-consistency + 5-fold CV). `Services/PPIService` + `PPIDownloadManager` fetch STRING v12 physical-interaction data for *H. sapiens*.

## Domain glossary

Helpful when reading code/UI: **DIA-NN** = the upstream DIA mass-spec search engine whose Parquet output is the input format. **Run / raw file** = one LC-MS injection = one cell/sample. **Plate** = a technical batch of runs (no biology attached). **Biological condition** = the experimental group label on a run. **TIC** = total ion current. **Contaminant ratio** = fraction of signal from contaminant protein groups (a QC axis). **Protein group / Protein.Group** = inferred protein identity. **NIPALS** = PCA variant robust to missing values. **ComBat** = batch-correction method. **HVP** = highly variable proteins. **PLP / `.pref`** = export formats for downstream pattern/classifier tooling.

## Cross-cutting conventions & gotchas

These are hard-won lessons recorded in `SCPBrowser/CODEMERGER_NOTES.md` and `PCANipals/CODEMERGER_NOTES.md` — keep both files updated when you make non-obvious fixes.

- **Never replace a shared `HashSet`/`Dictionary` field reference; mutate it in place (Clear+Add).** Selection/filter state (`_checkedCellTypes`, `_checkedBioConditions`, `_checkedPlates`) is shared by reference into `ScatterPlotControl`'s options. Reassigning the field leaves consumers pointing at the stale instance (this caused the resize-breaks-filtering bug). Same rule applied to `TargetProteinRatioPerFile`: build a new dict and swap it atomically rather than `Clear()`+rebuild on the live one.
- **Concurrency:** async filter passes are serialized with `SemaphoreSlim`; raise change events *outside* the lock to avoid deadlocks. On `CloseProject`, a `CancellationTokenSource` is cancelled and services are nulled — every async handler must guard with `_hasOpenProject` and null-conditional calls or it will `NullReferenceException` mid-teardown.
- **Plate colors have a single source of truth: `PlateFilterControl`.** Right-click a plate to recolor; changes propagate via `PlateColorChanged` / `SetPlateColorMap(...)` to the scatter, histogram, and distribution controls. Colors persist in `project_settings` under `PlateColors` (serialized `EscapedKey=RRGGBB`) and are restored on load. Do not reintroduce hardcoded per-control palettes.
- **Coordinate bases:** protein-coverage domain features use **1-based** UniProt positions compared against **0-based** view bounds — the off-by-one is intentional and correct; don't "fix" it. In `ProteinCoverageControl`, `System.Windows.Shapes.Path` must be fully qualified (it collides with `System.IO.Path`).
- **WPF resize/export re-entrancy:** guard `SizeChanged` handlers against zero-width and against re-entry during PNG export (`_isExporting` flag pattern).
