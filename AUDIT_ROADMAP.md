# SCPBrowser — Remediation Roadmap

Generated from two adversarial multi-agent audits run on the codebase at commit `1fa0fc1`.
**Nothing in this document has been fixed.** It is an exploratory map only.

- **Correctness audit**: 6 parallel finders (data integrity, scientific/numerical, async-UI, silent failure,
  resources, file-I/O) → 43 candidates → adversarial verification → **29 CONFIRMED**, 8 refuted.
- **UX/workflow review**: 3 lenses → 44 raw issues → synthesis → **28 ranked actions**, 6 claims dropped.

Every finding below was verified by a second agent whose explicit instruction was to *refute* it. Items that
did not survive are listed in Appendix A so you can see what was checked and dismissed.

---

## How to use this

Each item is self-contained: location, trigger, what actually goes wrong, the evidence that proves it, the
proposed fix, and how to verify. You (or a future session) can pick up any single item cold.

**Suggested order:** Tier 0 → Tier 1 → Tier 2 → UX. Within Tier 0, do R1 first — it is the only *critical* and
several others become easier once the reference/cache lifecycle is correct.

**Before touching code:** the Tier-0 items change numbers. Re-run the GroundTruth2 benchmark before and after
each one and diff the outputs, so you know exactly what moved and can report it honestly.

---

## Executive summary

| Severity | Count |
|---|---|
| CRITICAL | 1 |
| HIGH | 6 |
| MEDIUM | 20 |
| LOW | 2 |

**The five that matter most, in order:**

1. **R1 — cross-project reference bleed (CRITICAL).** Opening a second project keeps the first project's omic
   reference loaded; cells get classified against the wrong reference.
2. **R2 — prediction cache collides across projects.** The cache key is `(importId, method)` and `import_id` is
   a per-database AUTOINCREMENT, so project A's predictions can be served for project B.
3. **U1 — re-importing a reference keeps the OLD labels.** The reference rows are deleted but the
   classifications and the cache are not, and the UI reports success.
4. **U4/R8 — two different quantity columns.** Analysis uses `Ms1.Area`; stored quant, preview and the parquet
   reference builder use `Precursor.Quantity`. Query cells can be scored against a reference on a different
   quantity, and the Plate Browser shows one under the label of the other.
5. **U6 — PCA/UMAP always runs on 2000 HVPs**, even with the HVP checkbox unchecked.

**Manuscript exposure:** items U4, U6, U19, U23, U24 and R3/R4 mean the Methods section may currently describe
the pipeline incorrectly. Read the 'Manuscript implications' section before resubmitting — this is independent
of whether you change any code.

---

## TIER 0 — Manuscript-critical / cross-project corruption

These change numbers or labels that can reach a figure, an export, or the paper. Do these first.

### R1. Opening a second project keeps the FIRST project's omic reference database loaded — cells are classified against the wrong reference

- **Severity:** CRITICAL  |  **Area:** async-ui
- **Location:** `Controls/MainControl.xaml.cs:130`

**What the verifier established**

Every claim in the chain holds. (a) `MainControl` is a single XAML instance (`MainWindow.xaml:162
<local:MainControl x:Name="MainControlTab" DataLoaded="MainControlTab_DataLoaded"/>`) living for the
window lifetime, and `_cellTypeClassificationManager` is `readonly`, created once
(MainControl.xaml.cs:52). (b) `LoadTranscriptomicReferenceAsync` really does short-circuit: `if
(_cellTypeClassificationManager.IsLoaded) { return; }` (:130-134). (c) Grep proves the only
unconditional reload path, `ReloadTranscriptomicReferenceAsync`, has exactly three callers —
MainWindow.xaml.cs:1531, :1579, :1643 — and I read all three: they are the omic-import handlers
(TSV, .pref, parquet reference). Nothing on the project-open path. (d) `OpenProjectAsync`
(MainWindow.xaml.cs:154) never calls `CloseProject()`; `CloseProject()` (:704) is the only reset and
is reached only from `CloseProject_Click` or the catch block at :333. `OpenProject_Click` (:140) and
`NewProject_Click` (:109) go straight to `OpenProjectAsync`. (e)
`OpenProjectMenuItem`/`NewProjectMenuItem` are only ever toggled by GO-database readiness
(`CheckGoStatus`, :2647-2648), never disabled during a load, and `LoadingOverlay` sits inside the
inner grid at `Grid.Row="1"` (MainWindow.xaml:438-440) so it does not cover the `<Menu
Grid.Row="0">` — File ▸ Open Project is reachable at all times. (f) The consequence is real:
`MainControlTab_DataLoaded` reads `bool cellTypeAvailable =
MainControlTab.IsTranscriptomicDatabaseLoaded();` (:1726) — still true from A — and calls
`AutoRunCellTypeClassificationAsync()` (:1742), which passes B's `_projectReferenceDatabasePath` and
B's importId into `GetOrComputePredictionsAsync`. On the compute branch,
`PredictCellTypesForAllRuns` scores against A's `_database.CellTypeProfiles`, then
`DeleteAllCellTypeClassificationsAsync(importId)` — which is `DELETE FROM
raw_file_cell_type_classifications` with importId ignored (CellTypeClassificationService.cs:140-144)
— wipes B's table and `SaveCellTypeClassificationsAsync` writes A-reference-derived labels for every
run name present in both. (g) The 'B has no reference at all' case is real:
`ReferenceDataService.LoadTranscriptomicDataAsync` returns null when `CellTypeProfiles.Count == 0`
(:503-505), so B alone would leave `IsLoaded == false` — but A's `_database` is never replaced, so B
is reported as classifiable and gets a full set of manufactured calls. `GetCellTypeColorMap()` also
derives the legend from A's `_database.CellTypeProfiles.Keys` (:272). No warning anywhere.

**Proposed fix**

Key the loaded reference by its source path: store the path passed to `LoadDatabaseAsync` and, in
`LoadTranscriptomicReferenceAsync`, reload whenever it differs from
`mainWindow.ProjectReferenceDatabasePath` instead of short-circuiting on `IsLoaded`. Additionally
have `OpenProjectAsync` tear down all session-lifetime state (call `CloseProject()` or a dedicated
reset) before opening a new project.

---

### R2. Duplicate-import guard is checked against the pre-rename filename

- **Severity:** HIGH  |  **Area:** data-integrity
- **Location:** `Dialogs/ImportParquetDialog.xaml.cs:265`

**What the verifier established**

The name mismatch is real and verified end to end. `ValidateAndLoadParquetFile` captures `string
fileName = Path.GetFileName(filePath)` at line 182 (the user-selected source basename) and checks
`IsParquetImportedAsync(fileName)` at 265, which queries `SELECT COUNT(*) FROM parquet_imports WHERE
file_name = @fileName` (ParquetDataService.cs:614-620). But `Import_Click` stores `fileName =
Path.GetFileName(_selectedParquetPath)` at line 781, AFTER `File.Move(_selectedParquetPath,
newFilePath)` at 774 where `newFileName = $"{SelectedPlate.PlateName}.parquet"` (754). So the DB
always holds `{PlateName}.parquet` while the guard tests the source basename — the guard can only
fire if the user re-picks the already-renamed file out of imports/. There is NO uniqueness
protection downstream: raw_files is declared `raw_file_name TEXT NOT NULL` with no UNIQUE index
(ProjectDatabaseService.cs:314-325) and `InsertRawFilesAsync` (ParquetDataService.cs:507-553) does a
plain INSERT per run. I found a second, sharper reachability path the auditor missed: if
`ExtractAndStoreProteinQuantAsync` throws, the catch at 853 only shows a MessageBox and the
`finally` at 867-873 re-enables ImportButton, while `_selectedParquetPath` now points at the
already-renamed `{Plate}.parquet`. A second click takes the `originalFileName.Equals(newFileName)`
branch at 759, skips the rename AND the File.Exists guard, and inserts a whole second set of
raw_files rows. Consequences verified: `GetRawFileNameToIdMappingAsync` (176) throws on
ToDictionary, and `LoadMultipleParquetFilesAsync` accumulates
ProteinCountPerFile/PeptideCountPerFile/TotalIonCurrentPerFile with `+=` (213-237). Those doubled
counts reach `DataFilterService.FilterByProteinCutoff` (DataFilterService.cs:325-330) via
`ApplyFiltersAsync` at MainWindow.xaml.cs:1688, which runs BEFORE the throw at 1696 — so the
Explorer is drawn and QC-filtered on doubled numbers, then a generic 'Error loading data' box
appears. Downgraded from critical to high only because the failure is not fully silent (a generic
error dialog does appear on the next open).

**Proposed fix**

Deduplicate on `parquet_imports.file_hash` (already computed at line 780) rather than on the display
name, and additionally reject an import whose run names already exist in raw_files. Add a UNIQUE
index on raw_files(raw_file_name). Separately, do not leave the dialog re-armed after a failed
import: clear `_selectedParquetPath`/disable Import in the catch so a second click cannot re-insert.

---

### R3. Marker-class Confidence is an unbounded score but is thresholded as a 0-1 probability, silently relabelling assigned cells "Undetermined"

- **Severity:** HIGH  |  **Area:** science
- **Location:** `Services/MarkerClassificationService.cs:215`

**What the verifier established**

Mechanism verified end to end. MarkerClassificationService.cs:179 `double score =
detected.Sum(Weight) * ((double)k / panel.Count);` with Weight = log(1 + classCount/sharedBy) at
:164 — unbounded, no normaliser. :215 stores it verbatim as `Confidence = kv.Value.Score`.
CellTypeClassificationService.cs:79 persists it and :126 reloads it verbatim (`Confidence =
reader.GetDouble(6)`). MainWindow.xaml.cs:2031-2037 loads those rows and calls
SetCellTypePredictions(..., selectCellTypeMode:true), which at PeptideTicControl.xaml.cs:618-619
unconditionally runs ApplyConfidenceThreshold(ConfidenceThresholdSlider.Value); :722-725 then does
`if (prediction.Confidence < threshold) prediction.TopCellType = "Undetermined";`. The slider is
Minimum=0/Maximum=1 (PeptideTicControl.xaml:243) and its label renders `{threshold:P0}` (a
percentage). By contrast the profile classifier's Confidence IS a normalised softmax probability
(CellTypePredictor.cs:279, comment "Already a probability (0-1)"), which is what the control was
built for — so the marker path feeds an incompatible scale into a probability gate. TWO ERRORS IN
THE CANDIDATE, neither fatal to it: (a) the default is 0.1, not 0.5 — Settings.Designer.cs:87
DefaultSettingValue("0.1"); the literal "50%" in the XAML is a design-time placeholder overwritten
at PeptideTicControl.xaml.cs:228-229. (b) At 0.1 the worked example (2 classes, 20-gene panel, k=2
-> 0.2197) actually SURVIVES; the relabel needs a larger panel (2 classes, 50-gene panel, k=2 ->
2*ln(3)*0.04 = 0.0879 < 0.1) or the user raising the slider/Settings value, which persists per-
project (PeptideTicControl.xaml.cs:365-369, SettingsControl.xaml.cs:91-99). Reachable and wrong
either way. Extra symptom I traced that the candidate missed: the colour map is built BEFORE the
mutation (MainWindow.xaml.cs:2035 BuildMarkerCellTypeColorMap) and contains no "Undetermined" key,
so relabelled cells get no legend checkbox (PeptideTicControl.xaml.cs:1522-1526 seeds
_checkedCellTypes from colorMap.Keys) and fall back to Brushes.Black at :1417 — they vanish from the
legend/pie while MarkerClassesControl.xaml.cs:157 still reports "Classified N cells: A=x, B=y".

**Proposed fix**

Store a bounded confidence for the marker scorer instead of the raw additive score — e.g. score /
max-attainable-score for that class's panel, or a softmax over the per-class scores Classify already
computes — or stamp the prediction with a scorer method that ApplyConfidenceThreshold skips. Do not
feed an unbounded score into a control whose axis is a probability.

---

### R4. Hypergeometric enrichment is parameterised with n = ALL detected genes, including genes outside the reference universe the population N is defined over

- **Severity:** HIGH  |  **Area:** science
- **Location:** `CellTypePredictor.cs:513`

**What the verifier established**

Verified as written. CellTypePredictor.cs:507 `int N = _geneSpecificity.Count` — the union of genes
across all CellTypeProfiles (built at :57-101 from profile.MedianExpression.Keys). :513 `int n =
detectedProteins.Count` — the full key set of proteinAbundances, which
CellTypeClassificationManager.ExtractProteinAbundances (:301-338) fills from every quantified
protein group with no intersection against _geneSpecificity. :516 k IS intersected. So the sample n
is not drawn from the population N: genes absent from the reference inflate n, inflating E[X] = nK/N
above the value k can attain, pushing the right-tailed p toward 1. Live path confirmed unguarded:
PredictCellType -> CalculateRawMetrics (:409) -> CalculateHypergeometricPValue on every cell,
reached from CellTypeClassificationManager.PredictCellTypesForAllRuns (:236) and from
ClassifierEvaluationService.ScoreCells (:447). The consequence chain is real: :224 pEnrichment =
softmax over -log10(p); when p is 1.0 for every class that term is 0 for all, the softmax is exactly
uniform, and :230 averages that constant in as a full quarter of CompositeScore, which is what
Confidence (:279) and the exported HypergeometricPValue column
(CellTypeClassificationManager.cs:657) are built from. CAVEAT the candidate overstates: p == 1.0
exactly is DATA-DEPENDENT, not universal — its own worked examples use an implausibly small k (k=5
against E[X]=150). Where the reference is built from the same dataset
(ReferenceDataService.BuildProteomicReference, the main workflow), most detected genes ARE in the
universe and the inflation is small. The mis-specification itself is unambiguous and holds
regardless.

**Proposed fix**

Restrict the sample to the population before testing: `int n = detectedProteins.Count(p =>
_geneSpecificity.ContainsKey(p));` (k is already the intersection). This is both the correct
parameterisation and removes the n > N exception by construction. Separately, consider dropping a
channel whose softmax is uniform from the mean at :230 rather than averaging a constant into it.

---

### R5. Prediction cache key (importId, method) collides across projects because import_id is a per-database AUTOINCREMENT

- **Severity:** HIGH  |  **Area:** async-ui
- **Location:** `CellTypeClassificationManager.cs:143`

**What the verifier established**

Verified end to end. `parquet_imports.import_id INTEGER PRIMARY KEY AUTOINCREMENT`
(Services/ProjectDatabaseService.cs:301) is per-project-file, so a single-import project A and a
single-import project B both yield 1; `GetMostRecentImportIdAsync` is `SELECT import_id FROM
parquet_imports ORDER BY import_timestamp DESC LIMIT 1` (ParquetDataService.cs:289-296). The manager
is a session singleton never reset on project open (see previous finding), and
`_cachedImportId`/`_cachedMethod`/`_cachedPredictions` are only written on a successful compute/DB-
load (:173-175, :202-204) and only cleared by `SetClassificationMethod` on an actual method change
(:61) — which does not happen when both projects resolve to the same method, because `if (method ==
ClassificationMethod && _classifier != null) return;` (:45) fires first. The guard at :143-149
therefore passes for project B and `GetOrComputePredictionsAsync` returns A's dictionary BEFORE step
3 ever reads B's own `raw_file_cell_type_classifications`, so even a project B that has correct
stored classifications is overridden by A's in-memory ones. That dictionary lands in
`MainControl._cellTypePredictions` (MainControl.xaml.cs:484) and is what `ExportPLP_Click` ships:
`var cellTypePredictions = MainControlTab.GetCellTypePredictions();` (MainWindow.xaml.cs:1338). Harm
requires overlapping run names (the re-import/re-analysis workflow), which the finding correctly
scopes. Note the code comment at :21-25 shows the author intended these keys to prevent exactly this
cross-project leak — the fix is present but the key chosen is not globally unique. One sub-claim I
partially discount: 'ignores keyMarkers/excludedCellTypes/priorWeights' is largely mitigated on the
main reclassify route, which passes `forceRecompute: true` (MainWindow.xaml.cs:2326); it still
applies to the non-forced Color-by-Cell-Type and auto-run routes.

**Proposed fix**

Add the project database path to the cache key (`_cachedProjectPath == projectDatabasePath`)
alongside importId and method, and clear `_cachedPredictions`/`_cachedImportId`/`_cachedMethod`
whenever a project is opened or closed. Optionally fold a hash of keyMarkers + excludedCellTypes +
priorWeights into the key so a non-forced request cannot serve results computed under different
classifier inputs.

---

### R6. Cell viewer lays tiles out by (XPos,YPos), which is not a per-cell coordinate — 194 of 208 cells are fully occluded and unreachable in the doublet-QC grid

- **Severity:** HIGH  |  **Area:** resources
- **Location:** `Controls/CellPlateViewerControl.xaml.cs:177`

**What the verifier established**

Reachable: MainWindow.xaml.cs:1229-1249 CellViewer_Click constructs CellPlateViewerControl in its
own window and calls Initialize(_currentProjectPath); RunCombo_SelectionChanged -> LoadRunAsync ->
RenderGrid/RelayoutGrid. The layout key is exactly (XPos,YPos): RelayoutGrid() line 177-178
`Canvas.SetLeft(b, ((cell.XPos ?? 1) - 1) * tile); Canvas.SetTop(b, ((cell.YPos ?? 1) - 1) * tile);`
with every tile given the same Width/Height (lines 175-176) and an opaque Background=Brushes.Black
(line 129), so co-located tiles occlude each other exactly and only the last child added (highest
drop_no, since GetCellsAsync orders `ORDER BY drop_no`, CellenOneQueryService.cs:56) renders or hit-
tests. I verified the degeneracy directly against the user's own data rather than trusting the
claim. Parsing each canonical *_isolated.xls with the parser's own column indices
(CellenOneParser.cs:226-227, XPos=col 11 / YPos=col 12 — I confirmed against the real header row:
DropNo,X,Y,Diameter,Elongation,Circularity,Intensity,Plate,Well,Target,Field,XPos,YPos,...), ALL SIX
runs in C:\Users\paulo\Desktop\Luisa\*.Run give identical results: 208 cells, 14 distinct
(XPos,YPos) pairs, maxY=1, one well (A-1), one target. XPos runs 1..14 with 11 cells each except
XPos=13 which holds 65 cells. So GridCanvas is a single row of 14 stacks and 194 of 208 tiles are
invisible and unclickable. I also checked whether a composite key would rescue it:
(Target,Field,XPos,YPos) yields only 154 distinct values for 208 cells (54 collisions; e.g.
(1,3,13,1) holds 12 cells), confirming the position fields are a per-imaging-field coordinate, not a
cell identity. Only DropNo is unique (Models/CellenOneModels.cs:91-102 says so explicitly, yet line
93 tells consumers to plot by XPos/YPos/Field — the comment itself is the source of the bug). The
misleading part is confirmed too: InfoText reports the full `{_cells.Count} cells` (line 108) and
RefreshFilterAndCounts (lines 614-629) tallies review/keep/discard over all 208, so the operator
sees e.g. "208 cells" and a review count while only 14 tiles exist on the canvas. Keyboard nav
cannot recover the hidden cells: MoveSelection lines 351-353 skip any candidate whose offset along
the movement axis is zero (`if (dx != 0 && Math.Sign(ox) != dx) continue;`), so co-located cells
(ox=0) are never reached, and since every YPos==1 the Up/Down keys find no candidate at all.
Severity re-assessed critical -> high. I traced review_status through the whole codebase (grep: only
CellenOneQueryService.SetReviewStatusAsync/GetCellsAsync, the model, the schema/migration in
ProjectDatabaseService.cs:86/563-576, and this control). It is write-only — no quantitation, export,
or classification path consumes it, so this cannot inject a wrong number into a manuscript figure.
What it does do is silently present a ~7%-complete image QC review as complete, which is squarely
'silently misleads the user' and directly undercuts the image-based doublet/morphology QC that backs
the doublet claims.

**Proposed fix**

Do not use (XPos,YPos) as a layout key — it is a per-field position that repeats, and no combination
of Target/Field/XPos/YPos is unique either (verified: 154 distinct for 208 cells). Lay the grid out
in sequential reading order keyed on the unique DropNo. In RelayoutGrid(): compute `int cols =
Math.Max(1, (int)((avail - 14) / tile));`, iterate `_cells` in a stable order (Field, then XPos,
then DropNo), and place tile i at `Canvas.SetLeft(b, (i % cols) * tile); Canvas.SetTop(b, (i / cols)
* tile);`, setting GridCanvas.Width = cols*tile+6 and GridCanvas.Height = ceil(n/cols)*tile+6. Note
the tile-size baseline at line 167 (`(avail - 14) / maxX`) must also stop deriving columns from
maxX. In the same change, rewrite MoveSelection to navigate that display order over the
PassesFilter-passing list (index +/-1 for Left/Right, index +/-cols for Up/Down) instead of
comparing XPos/YPos deltas, otherwise the hidden cells stay unreachable by keyboard. Also consider
excluding collapsed (filtered-out) tiles from the index so navigation matches what is on screen.

---

### R7. protein_quant_summary is written from a different quantity column than every in-memory analysis uses

- **Severity:** MEDIUM  |  **Area:** data-integrity
- **Location:** `Services/ParquetDataService.cs:735`

**What the verifier established**

Both mappings are exactly as quoted and both are on live paths. ParquetDataService.cs:730-736
(`ExtractAndStoreProteinQuantAsync`, the only writer of protein_quant_summary for parquet imports)
uses `PeptideColumn = "Stripped.Sequence"`, `TotalIonCurrentColumn = "Precursor.Quantity"`; the
identical mapping is used at ImportParquetDialog.xaml.cs:380-386 to populate
`raw_files.peptide_count`/`total_ion_current`. MainControl.xaml.cs:352-359 — the mapping used by
`LoadDataAsync` on every project open — uses `PeptideColumn = "Modified.Sequence"`,
`TotalIonCurrentColumn = "Ms1.Area"`. I confirmed both surface under identical labels:
PlateBrowserControl.xaml columns are literally Header="Peptides" (bound to RawFileInfo.PeptideCount,
i.e. distinct Stripped.Sequence) and Header="Total Ion Current" (bound to
RawFileInfo.TotalIonCurrent, i.e. sum of Precursor.Quantity), while
ScatterPlotControl.xaml.cs:1040-1041/1974 and PeptideTicControl show the Ms1.Area/Modified.Sequence
versions of the same two metrics for the same cell. The scientifically material half is also real:
MarkerClassificationService.BuildCellGeneLevelsAsync (252-255) scores `pqs.median_intensity` =
summed Precursor.Quantity, while CellTypeClassificationManager scores
`proteomicsData.ProteinQuantMatrix` = summed Ms1.Area (MainWindow.xaml.cs:1882/1962 ->
MainControl.GetCurrentData()). Two classifiers whose outputs share one 'Cell Type' UI concept are
scored on different DIA-NN quantities. Severity kept at medium (not high) because neither number is
internally wrong — the defect is that two different quantities carry the same label.

**Proposed fix**

Define the quantity/peptide columns once (a shared ColumnMapping constant, or read back the mapping
JSON already stored in parquet_imports.column_mapping) and use it for both the persisted
raw_files/protein_quant_summary values and the in-memory load. If the difference is deliberate,
rename the persisted fields and the Plate Browser headers so they cannot be read as the Explorer's
metrics.

---

### R8. Marker classification overwrites the transcriptomic classifier's table, tagged scorer_method='Standard' and without clearing first

- **Severity:** MEDIUM  |  **Area:** data-integrity
- **Location:** `Services/MarkerClassificationService.cs:222`

**What the verifier established**

The call at 220-222 is exactly as quoted and `SaveCellTypeClassificationsAsync` has `string
scorerMethod = "Standard"` as its default (CellTypeClassificationService.cs:22-23), so marker
assignments are written into raw_file_cell_type_classifications tagged as the Standard
transcriptomic scorer. Reachable: MarkerClassesControl is unconditionally hosted in
ProjectBrowser2.xaml:66 (no gating on whether a reference profile is loaded) and Classify_Click:148
/ Reclassify_Click:190 both call ClassifyAndStoreAsync. The mislabelling then propagates:
CellTypeClassificationManager.GetOrComputePredictionsAsync reads `GetStoredScorerMethodAsync()` at
line 137/167 and, with no explicit `classification_method` setting, adopts 'Standard' and serves the
marker rows as Standard-classifier output; with the setting present and set to 'Quantitative'
(written at MainWindow.xaml.cs:2313), `methodMatches` is false at line 168 so it falls through to
recompute and executes DeleteAllCellTypeClassificationsAsync + SaveCellTypeClassificationsAsync
(196-197) — silently wiping the marker assignments on the next project open
(AutoRunCellTypeClassificationAsync, MainWindow.xaml.cs:1742). The same wrong resolution feeds the
manuscript-facing Classification Confidence Map at MainWindow.xaml.cs:1102-1110. I partially refute
the auditor's first sub-claim: BuildCellGeneLevelsAsync covers every cell with any
protein_quant_summary row, so leftover stale rows from the other classifier require an already-
incomplete pqs (see the import-rollback finding); on its own that mixing mode is not routinely
reachable. Severity medium, not high: the marker class definitions survive in marker_classes and the
assignments can be recomputed, so the loss is recoverable — the durable harm is wrong provenance.

**Proposed fix**

In ClassifyAndStoreAsync call DeleteAllCellTypeClassificationsAsync(importId) before saving and pass
an explicit distinct tag (e.g. "Marker"), then teach CellTypeClassificationManager and the
confidence-map method resolution to recognise that tag so they neither serve it as a transcriptomic
result nor silently recompute over it.

---

### R9. Marker classifications are persisted as scorer_method='Standard', so they are later served and exported as profile-classifier output

- **Severity:** MEDIUM  |  **Area:** science
- **Location:** `Services/MarkerClassificationService.cs:222`

**What the verifier established**

Verified. MarkerClassificationService.cs:222 calls SaveCellTypeClassificationsAsync(importId,
predictions) with no scorerMethod, and CellTypeClassificationService.cs:22-23 defaults the parameter
to "Standard" (written at :80). It also skips the DeleteAll that the profile path performs
(CellTypeClassificationManager.cs:196), so the table can mix methods when the marker run covers a
different raw-file set than a prior run (raw_file_id is UNIQUE, ProjectDatabaseService.cs:377, so
INSERT OR REPLACE only overwrites overlapping rows). The impersonation is reachable on a project
that never used the reclassify dialog (the ONLY writer of the classification_method setting is
MainWindow.xaml.cs:2311-2313): CellTypeClassificationManager.cs:126-140 finds setting == null, falls
back to GetStoredScorerMethodAsync() -> "Standard", calls SetClassificationMethod("Standard"), and
at :167-177 methodMatches is true so the MARKER rows are returned as CellTypePredictor output and
cached. This fires automatically on the next project open via
MainWindow.AutoRunCellTypeClassificationAsync (:1878-1925, forceRecompute:false) and silently flips
the project's active classifier away from the intended Quantitative default. Downstream:
ExportClassificationDiagnosticsAsync gates the fabricated columns only on "Quantitative"
(CellTypeClassificationManager.cs:647), so marker rows write SpecificityScore 0.0000 /
MarkerCoverage 0.0000 / a p-value as if measured, and MainWindow.xaml.cs:1103-1110 resolves the
Classification-Confidence figure's method from the same poisoned row. ONE FACTUAL CORRECTION to the
candidate: the exported p-value is 0.000E+000, not 1.000E+000 — the marker path sets TopScore = new
CellTypeScore{CompositeScore=...} whose HypergeometricPValue defaults to 0.0, and
CellTypeClassificationService.cs:69 only substitutes 1.0 on NaN. p = 0 is a MORE misleading
fabrication (infinitely significant) than the candidate claimed. Also note the export requires a
loaded reference (CellTypeClassificationManager.cs:575) so it needs a project with BOTH a reference
and marker classes. Severity kept at medium: the harm is mislabelled provenance, stale-serving and
fabricated diagnostic columns, not a corrupted primary measurement.

**Proposed fix**

Pass an explicit distinct method (SaveCellTypeClassificationsAsync(importId.Value, predictions,
"Marker")), delete existing rows first as the profile path does, and change the diagnostics gate at
CellTypeClassificationManager.cs:647 to blank specificity/hypergeometric/coverage for any method
other than "Standard" rather than only for "Quantitative".

---

### R10. Channel-ablation figure's "full set" bar does not reproduce the scorer it is labelled as, for the default quantitative path

- **Severity:** MEDIUM  |  **Area:** science
- **Location:** `Services/ClassifierEvaluationService.cs:906`

**What the verifier established**

Both fusions read as claimed and they differ. ChannelCombinations (:897-922) and
AnalyzeChannelCombinations (:843-860) aggregate with an unweighted arithmetic sum `agg[i] +=
probs[m][i]` over all four channels, where probs come from ChannelProbabilities (:1427-1467) — for
quant, z-standardise then softmax at T=1, no weights, no skipping. The scorer the report is branded
with fuses by weighted log-pooling: QuantitativeCellTypeScorer.cs:260-278 skips channels with
`_channelWeight[m] <= 1e-9` and with `sd < 1e-12`, then accumulates `logAcc[...] +=
_channelWeight[m] * Math.Log(...)`, with weights calibrated per fold
(ClassifierEvaluationService.cs:407-408). Default Options.UseQuantitativeScorer = true (:54), so the
mismatch is the default. Reachability confirmed to the user-facing artefact:
ClassifierEvaluationDialog.xaml.cs:170 -> EvaluationReportBuilder.Build -> :45
ChannelCombinationFigure -> :317 ChannelCombinations, and the caption at
EvaluationReportBuilder.cs:351 invites exactly the invalid inference ("Bars above the full-set
(gold) bar ... evidence that a channel is diluting rather than helping"), while
AnalyzeChannelCombinations prints "(full set = the scorer being evaluated: {ScorerName})" at :868
into both the on-screen text and the exported .txt (dialog :132, :179). Note the label IS correct
when UseQuantitativeScorer is false — the shipped scorer really is an arithmetic mean of softmaxes
(CellTypePredictor.cs:230). The defect is confined to the default quant branch.

**Proposed fix**

Persist the per-fold calibrated weights on Report and apply the same weighted log-pool in
ChannelCombinations/ChannelProbabilities when IsQuantitativeScorer, or relabel the gold bar as
"equal-weight arithmetic fusion of all four channels" and stop equating it with the evaluated
scorer.

---

### R11. Subset-stability "uncertainty quantification" measures an uncalibrated scorer while every other number in the report is calibrated

- **Severity:** MEDIUM  |  **Area:** science
- **Location:** `Services/ClassifierEvaluationService.cs:599`

**What the verifier established**

Verified by direct comparison of the two construction sites. Main CV path: Run (:218) -> ScoreCells,
which at :401-408 does `quant = new QuantitativeCellTypeScorer(db); if
(optionsForCalibration?.CalibrateQuantWeights == true ...)
quant.SetChannelWeights(CalibrateQuantViaInnerCv(...))`. Subset-stability path: RunSubsetStability
(:599) does `var quantScorer = useQuant ? new QuantitativeCellTypeScorer(db) : null;` and never
calls SetChannelWeights anywhere in the method (:563-638), so it runs at the constructor's equal
weights. All three flags default to the mismatch: UseQuantitativeScorer = true (:54),
CalibrateQuantWeights = true (:62), RunSubsetStability = true (:65);
ClassifierEvaluationDialog.xaml.cs:90-100 never overrides CalibrateQuantWeights. The resulting
numbers are printed as the report's uncertainty at FormatSummary :729-733 and charted by
EvaluationReportBuilder.cs:47 StabilityFigure. The gap matters by the code's own measurement —
QuantitativeCellTypeScorer.cs:253-254: "Equal-weight log-pooling lets a weak channel veto a strong
one (measured: AUROC at 59% dragged a 100% Spearman down to 89%)" — which is precisely the
configuration the stability pass runs in. The candidate cited that note at
ClassifierEvaluationService.cs:253-255 (which is the learning-curve loop, not the note); the note is
real but lives in QuantitativeCellTypeScorer.cs.

**Proposed fix**

Inside the fold loop of RunSubsetStability, mirror ScoreCells: `if (useQuant &&
options.CalibrateQuantWeights) quantScorer.SetChannelWeights(CalibrateQuantViaInnerCv(data,
trainMap, options, f));`

---

### R12. "Clear Cell Type Classifications" deletes the DB rows but leaves the in-memory predictions, so the app keeps displaying and exporting classifications that no longer exist

- **Severity:** MEDIUM  |  **Area:** async-ui
- **Location:** `MainWindow.xaml.cs:664`

**What the verifier established**

The outcome is confirmed, though the stated mechanism is only the second of two — the real behaviour
is worse than described. `ClearCellTypeClassifications_Click` calls `await
_cellTypeService.DeleteAllCellTypeClassificationsAsync(importId.Value);` (:664) and touches no in-
memory state; the manager method that would (`DeleteAllClassificationsAsync` → `ClearCache()`,
CellTypeClassificationManager.cs:383-389) is dead — grep over the whole tree shows `ClearCache()` on
this class has no caller other than that unused method. Correction to the candidate's trigger:
`PeptideTicTab_CellTypePredictionsRequested` will not even fire, because
`ColorModeComboBox_SelectionChanged` only raises `CellTypePredictionsRequested` when
`_cellTypePredictions == null || _cellTypePredictions.Count == 0`
(PeptideTicControl.xaml.cs:780-784), and nothing clears `PeptideTicControl._cellTypePredictions`
(set in `SetCellTypePredictions`, :607-609; `UpdateChart` clears only the checked-sets, :581-589).
So selecting Color by ▸ Cell Type simply recolours from the stale in-memory set with no recompute
attempt at all. Independently, if the request did fire, the manager guard at
CellTypeClassificationManager.cs:143 would return the same stale cache (importId and method
unchanged; `GetStoredScorerMethodAsync` now returns null so
`SetClassificationMethod("Quantitative")` is a no-op when that is already the method). Net: project
DB has zero classification rows while the Explorer legend, pie chart and `ExportPLP_Click` (:1338 →
`MainControlTab.GetCellTypePredictions()`) all still carry a full set of labels, and the
confirmation dialog's promise ("Classifications will be recomputed next time you use the 'Color by
Cell Type' feature") is false. Reopening the project later recomputes from scratch and can differ
from the session that produced the figures. Downgraded from 'high' because the displayed values are
this project's own last-computed values, not another project's — the defect is staleness presented
as freshness plus a DB/UI divergence, not a numerically foreign result.

**Proposed fix**

Route the clear through state that owns the cache: add
`MainControlTab.ClearCellTypePredictionCache()` that calls
`_cellTypeClassificationManager.ClearCache()`, resets `_cachedImportId`/`_cachedMethod`, and nulls
`_cellTypePredictions`; also push `PeptideTicTab.SetCellTypePredictions(null, null)` so the Explorer
drops its copy and re-requests. Call all of that from `ClearCellTypeClassifications_Click` after the
delete.

---

## TIER 1 — Correctness & data integrity

Crashes, aborted pipelines, data written wrong or lost. Not directly manuscript-facing, but real.

### R13. Protein-matrix CSV export writes culture-formatted, unescaped doubles (decimal comma shifts every column)

- **Severity:** HIGH  |  **Area:** silent-failure
- **Location:** `Controls/ProteinMatrixControl.xaml.cs:710`

**What the verifier established**

The cited code is exactly as quoted and every quant cell reaches it as a boxed double: RefreshMatrix
adds one `typeof(double)` column per raw file (lines 211-214) plus `Var_Std` (line 207), and fills
them from ProteinQuantMatrix (lines 239-249). `doubleValue.ToString("G")` at line 710 uses
Thread.CurrentCulture and is the ONLY branch that bypasses EscapeCsvField (line 711 escapes
everything else; header at 699-701 is escaped). I checked for a global culture override and found
none: App.xaml.cs only calls SQLitePCL.Batteries.Init(), and SCPBrowser.csproj sets no
InvariantGlobalization / DefaultThreadCurrentCulture. Verified empirically on this box:
`(1234567.89).ToString('G')` under pt-BR yields `1234567,89`, so each numeric cell injects an extra
field separator while the header keeps N columns, and line 717 still reports "Data exported
successfully". Export is reachable from the Protein Matrix tab's Export button (ExportButton enabled
at line 260). Two corrections to the candidate: (a) the secondary 'scientific notation on en-US'
claim is largely wrong — I measured 1e8 -> "100000000", 1.23456789e9 -> "1234567890.12346"; "G" only
switches to exponent form around 1e15, above realistic MS intensities; (b) this machine's culture is
en-US, so exports already produced for the manuscript on this machine are NOT corrupted — the
exposure is other users of the ClickOnce build (and any comma-decimal locale).

**Proposed fix**

Return EscapeCsvField(doubleValue.ToString("R", CultureInfo.InvariantCulture)); mirroring the
codebase's own norm in ClassifierEvaluationService.WritePerCellCsv (lines 655-667, which already
uses "R"/InvariantCulture). This covers Var_Std and every raw-file column, since both are
typeof(double).

---

## TIER 2 — Robustness, silent failure, locale, performance

Wrong-looking output, silently dropped data, locale corruption, UI freezes.

### R14. GetRawFileNameToIdMappingAsync / LoadCellTypeClassificationsAsync throw on duplicate run names, aborting half of the project-load pipeline

- **Severity:** MEDIUM  |  **Area:** data-integrity
- **Location:** `Services/ParquetDataService.cs:176`

**What the verifier established**

Code and blast radius verified, though this is the failure mode of the duplicate-import defect
rather than an independent root cause. ParquetDataService.cs:172-176 ends in a bare
`list.ToDictionary(r => r.Name, r => r.Id)` and CellTypeClassificationService.cs:132 in
`rows.ToDictionary(r => r.RunName, r => r.Result)`; both throw ArgumentException on a duplicate
raw_file_name, which nothing in the schema or InsertRawFilesAsync prevents. The call site is real:
MainWindow.xaml.cs:1696 inside MainControlTab_DataLoaded, which is raised on every project open
(MainControl.xaml.cs:447). I confirmed the skipped work by reading 1696-1747: excluded-run loading
(1703-1704), protein annotations (1708), ProteinMatrixTab.UpdateMatrix (1710), protein coverage
(1713), contaminant-ratio recalculation (1716), AutoRunCellTypeClassificationAsync (1742) and
RestoreCheckedStatesAsync (1746) are all bypassed, while ApplyFiltersAsync (1688) and
PeptideTicTab.UpdateChart (1693) have already run — so the Explorer is fully drawn with un-excluded,
un-annotated data behind a generic 'Error loading data' box (1751).
CellTypeClassificationService.cs:38 (`rawFileMap[name] = id`, last-row-wins) is also as described.
Severity medium rather than high: it only fires once duplicates already exist, and it does at least
surface an error rather than failing silently.

**Proposed fix**

Prevent the duplicates at the source (UNIQUE index on raw_files(raw_file_name) plus a content-hash
import guard). Independently, replace the bare ToDictionary calls with a grouped/TryAdd form that
raises an explicit, blocking error naming the duplicated runs instead of aborting the load pipeline
halfway through.

---

### R15. Parquet import has no rollback: a failure after the import/raw-file rows commit leaves a committed import with zero protein_quant_summary rows

- **Severity:** MEDIUM  |  **Area:** data-integrity
- **Location:** `Dialogs/ImportParquetDialog.xaml.cs:838`

**What the verifier established**

The asymmetry is real. InsertParquetImportAsync (809) and InsertRawFilesAsync (822) each commit
their own transaction (ParquetDataService.cs:482-499 and 507-553); ExtractAndStoreProteinQuantAsync
(838) then re-parses the whole parquet and only commits at the end via BulkInsertProteinQuantAsync
(782). The catch at 853-866 does nothing but show a MessageBox, whereas the sibling
ImportGeneMatrixDialog.xaml.cs:189-208 tracks `committedImportName` and calls
DeleteParquetImportAsync plus deletes the copied file. Consequence verified: raw_files rows exist
with an empty protein_quant_summary, the Explorer still looks right (it reads the parquet from disk
via MainControl.LoadDataAsync), but MarkerClassificationService.BuildCellGeneLevelsAsync (252-255)
returns no rows for those cells, so Classify_Click reports 'Classified N cells'
(MarkerClassesControl.xaml.cs:157) over a silently truncated population. I REFUTE the auditor's
'retrying is blocked' claim: re-selecting the renamed imports/{Plate}.parquet makes filePath ==
expectedPath at line 187, the guard at 265 then DOES match and offers delete-and-reimport, so
recovery exists. But I found something worse than what was reported: the `finally` at 867-873 re-
enables ImportButton with `_selectedParquetPath` now pointing at the renamed file, so a second click
takes the `originalFileName.Equals(newFileName)` branch at 759, skips both the rename and the
File.Exists guard, and inserts a duplicate parquet_imports row plus a duplicate set of raw_files —
directly manufacturing the duplicate-name corruption. That in-place retry raises this from low to
medium.

**Proposed fix**

Mirror the gene-matrix importer: track the committed importId/file name and call
DeleteParquetImportAsync (and undo the File.Move) in the catch. Additionally, in the catch clear
`_selectedParquetPath` and leave ImportButton disabled so an in-place retry cannot double-insert.

---

### R16. LoadMultipleParquetFilesAsync sums per-run counts across files while overwriting the quant matrix

- **Severity:** MEDIUM  |  **Area:** data-integrity
- **Location:** `Services/ParquetDataService.cs:216`

**What the verifier established**

The merge semantics are exactly as quoted and internally inconsistent: ProteinCountPerFile
(215-218), PeptideCountPerFile (223-227) and TotalIonCurrentPerFile (232-236) use `+=` on a key
collision; TargetProteinRatioPerFile is averaged (242-245); BiologicalConditionPerFile (251),
ProteinToGeneMap (271) and ProteinQuantMatrix (264) all overwrite. Reached on every multi-file
project open via MainWindow.xaml.cs:312 -> MainControl.LoadDataAsync:380-386. The downstream harm is
confirmed: DataFilterService.FilterByProteinCutoff (DataFilterService.cs:325-330) gates on
ProteinCountPerFile, so a doubled count can push a cell across the QC cutoff, and
ScatterPlotControl.xaml.cs:1040-1041/1974 renders the doubled peptide/TIC values in the Explorer and
its tooltips. Caveat that keeps this at medium rather than high: I could not find a path that
produces a shared run name across two loaded parquet files WITHOUT also producing duplicate
raw_files rows, so in practice this always co-occurs with the ToDictionary throw at line 176 — but
the `+=` corruption happens first (ApplyFiltersAsync at MainWindow.xaml.cs:1688, UpdateChart at
1693) and is what the user actually sees on screen before the error box appears.

**Proposed fix**

Merge per-run scalars with the same semantics as the quant matrix (overwrite), or better: detect the
key collision and fail loudly — two files claiming the same run name is a data-integrity error, not
something to silently reconcile.

---

### R17. n > N makes MathNet's Hypergeometric constructor throw and aborts the whole classification run; only the benchmark harness guards it

- **Severity:** MEDIUM  |  **Area:** science
- **Location:** `CellTypePredictor.cs:540`

**What the verifier established**

The unguarded path is real: CellTypePredictor.cs:540 `new Hypergeometric(N, K, n)` with no
validation; MathNet's IsValidParameterSet requires draws <= population, so n > N throws
ArgumentException. No try/catch exists between there and the UI — CalculateHypergeometricPValue
(:498) has none, CalculateRawMetrics (:387) has none (its only try/catch is inside
CalculateSpearmanCorrelation at :445), PredictCellType (:167) has none, PredictCellTypesForAllRuns
(CellTypeClassificationManager.cs:211-241) has none, GetOrComputePredictionsAsync (:108) has none;
the first catch is the generic MessageBox at MainWindow.xaml.cs:2349-2354 (and a silent swallow in
AutoRunCellTypeClassificationAsync at :1929). The authors demonstrably hit this:
ClassifierEvaluationService.cs:416-424 pre-empts the identical call, commenting "Guard the known
unguarded hazard ... n > N is mathematically undefined and Math.NET throws (no try/catch upstream)",
and RunSubsetStability repeats the guard at :617. SEVERITY DOWNGRADED from the candidate's "high":
the trigger needs a reference universe smaller than one cell's gene count, which for the normal
workflow (BuildProteomicReference over labelled runs, ReferenceDataService.cs:517-554 — N is the
union over all labelled cells) requires a degenerately small label set, e.g. 1-2 cells per class via
ParquetReferenceLabelDialog (MainWindow.xaml.cs:1596-1615), or a small curated panel imported as the
reference. And it fails LOUD (no cells classified, nothing saved) rather than producing a plausible
wrong number.

**Proposed fix**

Apply the previous fix (intersect the detected set with the reference universe so n <= N by
construction), and add a defensive guard in CalculateHypergeometricPValueExact that returns 1.0 for
degenerate parameters instead of letting MathNet throw through the classification loop.

---

### R18. OpenProjectAsync is re-entrant and its CancellationToken is never observed — two interleaved project loads can write one project's classifications into the other's database

- **Severity:** MEDIUM  |  **Area:** async-ui
- **Location:** `MainWindow.xaml.cs:161`

**What the verifier established**

Both halves check out. The token is genuinely inert: grep for `_projectCts`/`ct` across
MainWindow.xaml.cs returns only :40 (field), :159-161 (create + capture) and :706-707
(CloseProject), so `_projectCts?.Cancel()` under the comment 'Cancel any previous project's in-
flight operations' does nothing. Re-entrancy is reachable: there are real dispatcher yields during a
load — `await Task.Delay(50)` (:167), `await Task.Run(async () => {...})` (:175, service
construction + table migrations + queries), and inside `MainControlTab.LoadDataFromProject` →
`LoadDataAsync` → `LoadGoEnrichmentAsync`, which does `await Task.Run(() =>
_goEnrichmentManager.LoadDatabase())` and `await Task.Run(() =>
_goEnrichmentManager.EnrichAllRuns(...))` (MainControl.xaml.cs:209, :233) — a multi-second-to-
minutes window. During it the menu bar is live (LoadingOverlay is `Grid.RowSpan="2"` inside the
inner grid at `Grid.Row="1"`, MainWindow.xaml:438-440, and does not cover `<Menu Grid.Row="0">`),
and `OpenProjectMenuItem` is never disabled for a load (only `CheckGoStatus` touches it,
:2647-2648). `OpenProject_Click` is `async void` (:140), so a second `OpenProjectAsync` starts and
overwrites `_projectDatabaseService`, `_parquetService`, `_plateService`, `_cellTypeService`,
`_dataFilterService`, `_currentProjectPath` and `_projectReferenceDatabasePath` (:178-182, :169,
:205) plus MainControl's own `_currentData`/`_allParquetFilePaths`. When A's load then fires
`DataLoaded`, `MainControlTab_DataLoaded` runs against B's services:
`AutoRunCellTypeClassificationAsync` reads B's importId (:1890) and `GetOrComputePredictionsAsync`
executes `DeleteAllCellTypeClassificationsAsync` — `DELETE FROM raw_file_cell_type_classifications`,
importId ignored (CellTypeClassificationService.cs:142) — then `SaveCellTypeClassificationsAsync`.
Project B's classification table is destroyed and partially repopulated from A's data. Severity set
to medium rather than high because it requires the user to deliberately start a second open mid-
load; the damage when it happens is genuine data loss in project B.

**Proposed fix**

Add an in-flight guard at the top of `OpenProjectAsync` (return early or await the pending load),
disable `NewProjectMenuItem`/`OpenProjectMenuItem`/the recent-projects list for the duration in a
`finally`, and either thread `ct` through the awaited calls with an `IsCancellationRequested` re-
check after each await before touching shared fields, or delete the token so the comment stops
claiming protection that does not exist.

---

### R19. The whole filter pipeline (3 matrix deep copies + O(n^2 log n) LOESS) runs synchronously on the UI thread, and the contaminant-ratio slider fires it per tick with no debounce

- **Severity:** MEDIUM  |  **Area:** async-ui
- **Location:** `Controls/PeptideTicControl.xaml.cs:1907`

**What the verifier established**

The core is confirmed and is in fact stronger than stated: the pipeline does not merely 'block the
dispatcher between awaits', it never yields at all. `ContaminantCutoffSlider` is continuous
(`Minimum=0 Maximum=100 IsSnapToTickEnabled="False"`, PeptideTicControl.xaml:362-366) and reachable
— `ContaminantRatioLegendPanel.Visibility = Visibility.Visible` in Contaminant Ratio colour mode
(:1579). `ContaminantCutoffSlider_ValueChanged` has only a 0.005 dead zone (:1903) then raises the
event (:1907) → `MainWindow.PeptideTicTab_ContaminantRatioCutoffChanged` (:2139), which has no
loading overlay and calls `ApplyFiltersAsync`. Inside `DataFilterService.ApplyFiltersAsync`, steps
2-5 are plainly synchronous, and step 1's only await bottoms out in `DatabaseServiceBase.QueryAsync`
→ `await connection.OpenAsync(); ... await command.ExecuteReaderAsync(); ... await
reader.ReadAsync();` — Microsoft.Data.Sqlite implements the ADO.NET async methods synchronously, so
every one of those completes inline and the whole method runs on the dispatcher. Cost is real:
`FilterDataByRawFiles` rebuilds the full `ProteinQuantMatrix` three times per pass
(DataFilterService.cs:203-213, :399-409), and `ComputeHvpResults` → `FitLoess` does, for each of n
proteins, an allocating `indices.Select(...).OrderBy(p => p.Dist)` over all n
(HighlyVariableProteinsService.cs:425-434) — O(n^2 log n) with ~n^2 allocations, seconds for a few
thousand proteins. The same un-overlaid path is used by `MainControlTab_MaxProteinCutoffChanged`
(:413), `RunExcludeRequested` (:434), `RunRestoreRequested` (:450), `ClearExclusionsRequested`
(:466), `PlateFilterControl_PlateSelectionChanged` (:482). REFUTING two sub-claims: (1) '~200
complete passes queued behind the semaphore' is wrong — because the pass is fully synchronous,
WM_MOUSEMOVE coalesces while the thread is blocked, so the pass count is bounded by drag-
duration/pass-duration, and `_filterLock` is never actually contended from the single UI thread; (2)
the 'user kills the process mid-classification → half-deleted classification table' consequence is
not reachable from this path, since no classification write runs concurrently with a slider drag.
What remains is a genuine multi-second-per-tick freeze with no overlay and no progress, i.e. a hang,
not data corruption.

**Proposed fix**

Debounce the slider the way the protein-cutoff path already is (`_proteinCutoffDebounceTimer`,
MainWindow.xaml.cs:84-92) or handle only `Thumb.DragCompleted`. Independently, move the synchronous
body of `ApplyFiltersAsync` (steps 2-5, especially `ComputeHvpResults`) into `await Task.Run(...)`
so the dispatcher stays free, and show the loading overlay on the filter paths that currently have
none.

---

### R20. Opening a second project re-subscribes seven MainWindow handlers without unsubscribing, so every filter action runs N times

- **Severity:** MEDIUM  |  **Area:** async-ui
- **Location:** `MainWindow.xaml.cs:232`

**What the verifier established**

Mechanically confirmed. `OpenProjectAsync` subscribes all seven at :232-238 unconditionally; the
only `-=` for them is in `CloseProject()` at :710-716, and no open path calls `CloseProject()`
(`OpenProject_Click` :140, `NewProject_Click` :109, `RecentProject_Click` :2517 all go straight to
`OpenProjectAsync`; `CloseProject()` is reached only from `CloseProject_Click` :680 or the catch at
:333). `PlateFilterControl` and `MainControlTab` are XAML-declared singletons (MainWindow.xaml:152,
:162) that survive project switches, so the delegates accumulate.
`_dataFilterService.FilteredDataChanged` is indeed exempt (a fresh `DataFilterService` is
constructed at :182). Consequence is N sequential full filter passes per plate click / exclude /
upper-cutoff change — and since each pass is the fully synchronous multi-second matrix-copy + LOESS
pipeline established above, the freeze scales linearly. Two caveats that keep this below the data-
integrity findings: the duplicated calls are idempotent (`ExcludeRun` is a `HashSet.Add`,
`RestoreRun` a `Remove`, `SetSettingAsync` an upsert), so no wrong numbers are produced; and
`RecentProject_Click` is on the WelcomeScreen, which is `Collapsed` while a project is open, so in
practice only File ▸ Open Project and File ▸ New Project drive the accumulation.

**Proposed fix**

Extract an `UnsubscribeProjectEvents()` from `CloseProject()` and call it at the top of
`OpenProjectAsync` before the `+=` block, or use `-=` immediately before each `+=` — the pattern
already applied correctly to `ProteinMatrixTab.ContaminantsUpdated` at :1719-1720.

---

### R21. Missing parquet imports silently dropped on project open — partial dataset presented as the complete project

- **Severity:** MEDIUM  |  **Area:** silent-failure
- **Location:** `MainWindow.xaml.cs:287`

**What the verifier established**

Code is as quoted: the loop keeps only File.Exists survivors and warns only when parquetPaths.Count
== 0 (lines 291-303); the expected count (allImportedFiles.Count) is in hand at line 264 and
discarded. The subset then flows into MainControlTab.LoadDataFromProject (line 312) ->
LoadDataAsync, which sets TotalRunsText/TotalProteinsText/TotalPeptidesText from the reduced data
(MainControl.xaml.cs:400-402, 419-421) and StatusText "Loaded successfully: N runs" (line 438), then
raises DataLoaded, so filtering/HVP/PCA/ComBat/classification all run on the subset with no 'N of M'
anywhere. The same pattern repeats at 1024-1042 and 2229-2250 (the latter prints "Reloading data
from {parquetPaths.Count} file(s)" with no expected total). Two caveats that lower severity from the
candidate's 'critical': (1) I found NO in-app path that creates a DB row without its file —
ImportGeneMatrixDialog rolls back (lines 189-203), ImportParquetDialog moves/copies the file into
imports/ BEFORE inserting rows, ProjectBackupService copies db+imports together, and there is no
plate-rename caller for PlateService.UpdatePlateAsync — so the trigger is purely external (folder
moved/renamed, file deleted). (2) The candidate's OneDrive 'unhydrated placeholder' trigger is
wrong: File.Exists returns true for dehydrated placeholders. A corrupt/unreadable parquet is also
NOT silently dropped (LoadMultipleParquetFilesAsync lets the exception propagate to MainControl's
catch).

**Proposed fix**

Collect the missing filenames; when missing.Count > 0 && parquetPaths.Count > 0, show a modal
listing them and stating that all counts, figures and exports will reflect only N of M imports,
requiring explicit confirmation, and carry a persistent "PARTIAL DATA (N of M imports)" marker in
the window title/status bar. Apply the same at lines 1024-1042 and 2229-2250.

---

### R22. HVP filter degrades to alphabetically-first N proteins when <10 proteins are scorable, still labelled "top N highly variable"

- **Severity:** MEDIUM  |  **Area:** silent-failure
- **Location:** `Controls/PeptideTicControl.xaml.cs:1754`

**What the verifier established**

Full chain verified. (1) Early exit: HighlyVariableProteinsService.cs:172-182 returns every protein
with IsHighlyVariable=false, Rank=int.MaxValue, ordered by ProteinId — and VarianceStandardized is
left at its default 0.0, because it is assigned ONLY in ApplyVstMethod (line 380), never in
CalculateBasicStats. (2) Reachable: DataFilterService.ComputeHvpResults (lines 268-273) passes
minAbsoluteDetections:20, and effectiveMin = max(ceil(0.05*nCells), 20) (lines 163-164), so any
filtered view with <20 cells makes DetectionCount >= 20 impossible for every protein -> validResults
empty. A <20-cell view is producible from first-class UI controls: selecting one small plate in the
plate filter, or raising the protein-count cutoff (MainWindow SETTING_PROTEIN_CUTOFF slider ->
DataFilterService.FilterByProteinCutoff). (3) The stable-sort claim holds: LINQ OrderByDescending is
stable and its input is already OrderBy(ProteinId), so all-zero keys preserve alphabetical order;
Take(_hvpCount) then force-sets IsHighlyVariable=true (line 1754). (4) Consumed for real:
options.HvpResults -> Where(IsHighlyVariable) -> _hvpProteinIds (ScatterPlotControl.xaml.cs:277-283,
340-349) -> protein selection in BuildPreprocessedMatrix (lines 542-546). No warning is emitted; the
header still says "PCA - Principal Component Analysis". Severity is medium rather than high because
it requires the opt-in "Top HVPs only" box (DimensionReductionSettings.UseHvpFilter defaults false)
AND a <20-cell filtered view; with >=20 cells the normal VST path is correct. Weak mitigation only:
the Protein Matrix tab's Var_Std column would show 0.0 for every protein if the user happens to
look.

**Proposed fix**

In HighlyVariableProteinsService set VarianceStandardized = double.NaN (or add an explicit IsScored
flag set only in ApplyVstMethod) on the <10-valid early-exit path, and have GetFilteredHvpResults
fall back to all proteins plus a visible header warning when nothing is scored; surface the
<10-valid condition from DataFilterService.ComputeHvpResults to the UI.

---

### R23. Protein-matrix CSV export writes culture-dependent decimals unquoted into a comma-separated file

- **Severity:** MEDIUM  |  **Area:** io-robustness
- **Location:** `Controls/ProteinMatrixControl.xaml.cs:710`

**What the verifier established**

The mechanism holds exactly as described and is reachable: XAML line 59-61 wires `ExportButton` ->
`ExportButton_Click`; the button is enabled at line 260 whenever the matrix has data; every quant
column is `typeof(double)` (line 213) and `Var_Std` (line 207) too. The writer path is `if (field is
double doubleValue) return doubleValue.ToString("G");` (710) -> `writer.WriteLine(string.Join(",",
fields))` (713) — doubles bypass `EscapeCsvField` entirely, and `ToString("G")` is culture-
sensitive. No `CultureInfo.DefaultThreadCurrentCulture` assignment and no `InvariantGlobalization`
exist anywhere in the repo (grep: zero hits), so the process culture is the OS culture. On a pt-
BR/de-DE machine every numeric cell emits an unquoted `1234,56`, splitting each quant column into
two fields and shifting the row. HOWEVER the candidate's premise 'the author's own likely locale' is
REFUTED: `Get-Culture` on this machine returns `en-US` with decimal separator `.`, so the
supplementary matrix as generated here is intact. This is a portability defect for the ClickOnce-
distributed build, not a corruption of the manuscript's own export — hence medium, not critical.

**Proposed fix**

`doubleValue.ToString("R", CultureInfo.InvariantCulture)` at line 710 (and pass it through
EscapeCsvField for belt-and-braces). Nothing about the current en-US output changes.

---

### R24. Classifier-evaluation HTML report builds SVG coordinates with the current culture

- **Severity:** MEDIUM  |  **Area:** io-robustness
- **Location:** `Services/EvaluationReportBuilder.cs:382`

**What the verifier established**

Reachable and mechanically correct. `ClassifierEvaluationDialog.SaveReport_Click` (line 170) calls
`EvaluationReportBuilder.Build`, which emits `LearningCurveFigure` whenever `r.LearningCurve.Count
>= 2` (Build, line 46). Defaults make that the normal case: `LearningCurveSizes = {2,3,5,10,17}`
(ClassifierEvaluationService.cs:39), each k kept if `k < minClass` (line 255) — with ~18 cells/class
that leaves 4 points. Line 382 interpolates `{X(...):F1},{Y(...):F1}` with no `IFormatProvider`, so
on a decimal-comma locale `d` becomes `M96,0,240,5 L...`: SVG path data treats the comma as a
coordinate separator, so it renders a different-but-plausible curve rather than failing. Same defect
at 378/379 (`y1`/`y2`), 386 (`cx`/`cy`), 387-388 — and the candidate missed one more instance at
line 238 (`y + rowH * 1.5 + 4` is a double, so `y="47,5"` in PerClassFigure). `Pct()` at 448
correctly uses InvariantCulture, which is why the omission is easy to miss. Severity reduced from
high because the author's machine is en-US (`Get-Culture`), so the manuscript figure as produced
here is correct; the risk is a collaborator/reviewer regenerating the report on a comma locale.

**Proposed fix**

Add `private static string N(double v) => v.ToString("F1", CultureInfo.InvariantCulture);` and use
it at 238, 378, 379, 382, 386, 387, 388. The `:F4`/`:F3` spread labels at 298-299 are text-only,
cosmetic.

---

### R25. Transcriptomic TSV matrix parser assumes header length == data length; an R-style header shifts every expression value one cell over

- **Severity:** MEDIUM  |  **Area:** io-robustness
- **Location:** `TranscriptomicTsvParser.cs:180`

**What the verifier established**

The call path is real: MainWindow `ImportOmicProfile_Click` (1415) routes `.tsv`/`.txt` to
`ImportOmicProfile_FromTsv` (1462) -> `TranscriptomicConverterUtility.ConvertTsvToSqliteAsync`
(1499) -> this parser, and nothing in the repo produces the expression matrix, so its shape is
entirely externally determined. The arithmetic checks out: with header of N fields and data rows of
N+1, `cellIds = new int[N-1]` (180) is filled from `headerParts[1..N-1]` (182-186), so `cellIds[0]`
is the SECOND cell name; the data loop `colIndex - 1 < cellIds.Length` (202) then binds `parts[1]`
(cell 1's counts) to cell 2 and silently drops the final column. There is no length check anywhere
and no warning — the import reports a plausible cell/gene count. R's `write.table(mat, sep="\t")`
with defaults does emit exactly this header, so the trigger is realistic. Downgraded from high on
one honest caveat the candidate overstates: if the matrix columns are grouped by cell type (the
common case for a reference matrix), most of the one-column shift stays inside the same cell-type
group, so the aggregated profiles are damaged mainly at type boundaries plus the dropped last cell —
full-scale corruption only if the columns are in arbitrary order.

**Proposed fix**

After reading the first data row, compare `parts.Length` to `headerParts.Length`: if `parts.Length
== headerParts.Length + 1` treat the header as headerless-first-column (cellIds[i] =
headerParts[i]); on any other mismatch throw `InvalidDataException` naming both counts rather than
truncating.

---

### R26. .pref import wipes the existing reference database, then throws KeyNotFoundException if any protein lacks a DETECTION row

- **Severity:** MEDIUM  |  **Area:** io-robustness
- **Location:** `Services/ReferenceDataService.cs:313`

**What the verifier established**

Traced end to end. MainWindow `ImportOmicProfile_FromPref` (1546) calls
`WriteProteomicsReferenceAsync(..., clearExistingData: true)` (1557) ->
`WriteTranscriptomicDataAsync`, which executes `DELETE FROM cell_type_profiles; DELETE FROM
cell_type_metadata;` at 233-238 with no enclosing transaction, and only afterwards calls
`WriteCellTypeProfilesAsync`. There, `var genes = profile.MedianExpression.Keys.ToList()` (289)
drives `profile.PercentExpressing[gene]` (313). In the .pref loader those two dictionaries are
filled under independent conditions — Median/Mean at 121-125 (from EXPRESSION) and PercentExpressing
at 127-131 (only if `detectionMatrix` has the protein AND that cell type) — so a .pref with no
`##SECTION:DETECTION` block, or one protein missing from it, throws KeyNotFoundException on an
already-emptied database. Grep confirms nothing in the repo writes .pref, so the format is
externally authored and unvalidated. The candidate's suggestion to also guard MeanExpression is
unnecessary: Mean is always set alongside Median at 124. Medium rather than high because the loss is
a re-importable derived reference, and the user does get an error dialog (MainWindow:1586).

**Proposed fix**

Use `profile.PercentExpressing.TryGetValue(gene, out var pct) ? pct : 0.0` at line 313, and move the
DELETE at 233-238 plus all inserts inside one transaction so a failed import cannot leave the
reference wiped.

---

### R27. Multi-parquet merge sums per-run protein/peptide counts while overwriting the quant matrix, inflating counts for any run present in two files

- **Severity:** MEDIUM  |  **Area:** io-robustness
- **Location:** `Services/ParquetDataService.cs:216`

**What the verifier established**

The merge branch is not exotic — every project open loads ALL imported parquets (MainWindow:312,
1040, 1209, 2250 -> MainControl.LoadDataFromProject:293 -> LoadDataAsync:380-386 takes
LoadMultipleParquetFilesAsync whenever Count > 1). Within a single file the counts are per-run
distinct-set cardinalities (`proteinsByFile[rawFile].Count`, line 462), so summing them across files
(216, 225) is only correct when the run sets are disjoint. Nothing enforces that:
`IsParquetImportedAsync` (614) is called with `fileName` only (ImportParquetDialog:265) and the
`raw_files` table has no unique constraint on `raw_file_name` (ProjectDatabaseService.cs:314-325).
Import a DIA-NN re-search under a new filename into the same project and every shared run reports
roughly double its identified-protein/peptide count and double TIC, while `ProteinQuantMatrix` for
the same (protein, run) is last-write-wins (264) — so the headline per-cell QC numbers and the
intensities silently disagree. One sub-claim is REFUTED: TargetProteinRatioPerFile is never
populated in this flow — `MainControl.xaml.cs:358` sets `TargetProteinIdentifiers = new
List<string>()` and line 466 only fills the dictionary when that list is non-empty, so the
`(a+b)/2.0` at 243 is dead in practice.

**Proposed fix**

Carry per-run HashSet<string> of proteins/peptides across files and recompute counts after merging
(and decide explicitly whether overlapping (protein, run) quant sums or last-wins). Cheapest guard:
detect an overlapping run name during merge and refuse/warn instead of accumulating.

---

### R28. Reference import disables durability on the live project database (synchronous=OFF + journal_mode=MEMORY) around a long bulk insert and a VACUUM

- **Severity:** LOW  |  **Area:** data-integrity
- **Location:** `Services/ReferenceDataService.cs:247`

**What the verifier established**

Every factual element checks out. ReferenceDataService.cs:244-253 issues `PRAGMA synchronous = OFF;
PRAGMA journal_mode = MEMORY;` and the restore block at 263-270 runs only after
WriteCellTypeProfilesAsync + WriteCellTypeMetadataAsync + VACUUM, and restores only `synchronous` —
journal_mode stays MEMORY through the VACUUM, which rewrites the whole file. The target really is
the live project DB: MainWindow.xaml.cs:205 sets `_projectReferenceDatabasePath = projectDbPath`
with the comment 'The project database IS the reference database (unified)', and it is passed
straight through at 1554 (.pref flow) and 1610/1619-1623 (labelled-parquet flow), so the same file
holds every parquet import, classification and cellenONE blob. SQLite documents MEMORY journalling
as very likely to corrupt the database if the application crashes mid-transaction, and no
ProjectBackupService snapshot is taken first. Severity downgraded from medium to low: a managed
exception during the write propagates out of the `using(connection)` and is caught at
MainWindow.xaml.cs:1534/1582 with the process still alive, so SQLite can roll back from the in-
memory journal — corruption requires an actual process kill or power loss, which is an external
event rather than a code path the app can take on its own.

**Proposed fix**

Drop `PRAGMA journal_mode = MEMORY` and `PRAGMA synchronous = OFF` on the project database (the
batched transactions already give the speed win), or switch the project DB to WAL once at creation.
If the pragmas are kept, take a ProjectBackupService snapshot first, as the delete-condition flow
does, and restore journal_mode explicitly alongside synchronous.

---

### R29. Classification diagnostics TSV export formats every number with the current culture

- **Severity:** LOW  |  **Area:** io-robustness
- **Location:** `CellTypeClassificationManager.cs:653`

**What the verifier established**

The code is as quoted (culture-sensitive `ToString("F4")`/`("E3")` at 653-662, 670 and 796/806) and
reachable via MainWindow:2109/2120 -> `ExportClassificationDiagnosticsAsync` /
`ExportGenePresenceByCellTypeAsync`, and the sister export really is correct
(`ClassifierEvaluationService.WritePerCellCsv` 658-667 uses `"R", CultureInfo.InvariantCulture`).
But the impact claim is inflated: the file is TAB-delimited (`string.Join("\t", ...)` at
625/684/781/809), so unlike the CSV case a decimal comma causes no column shift and no data loss —
the columns stay aligned and a comma-decimal value read by pandas/R surfaces as text or NaN, i.e. a
visible failure rather than a plausible wrong number. Combined with this machine being en-US (`Get-
Culture`), the exported diagnostics behind the manuscript are unaffected. Real but low.

**Proposed fix**

Add `CultureInfo.InvariantCulture` to the ToString calls at 653-662, 670, 796 and 806 to match
WritePerCellCsv.

---

## TIER 3 — Workflow / UX / adoption

From the UX review. Ranked by (user harm x likelihood) / effort. Several overlap with the correctness
findings above — where they do, the correctness item is authoritative for the mechanism and this one for the
user-facing symptom.

### U1. Re-importing a reference profile keeps the OLD cell-type labels

- **Effort:** small
- **Where:** `MainWindow.xaml.cs:1476-1544 (Tsv), :1546-1592 (Pref), :1594-1656 (Parquet); CellTypeClassificationManager.cs:83-101, :143-177, :371`

**Why it matters**

The single most likely way this tool produces a published wrong result. All three
.tsv/.pref/.parquet routes write with clearExistingData:true (DELETE FROM cell_type_profiles /
cell_type_metadata, ReferenceDataService.cs:228-240) but never touch
raw_file_cell_type_classifications or the manager cache. ReloadTranscriptomicReferenceAsync ->
LoadDatabaseAsync (CellTypeClassificationManager.cs:83-101) replaces _database but leaves
_cachedPredictions/_cachedImportId intact, and the cache key is (importId, method) only — reference
identity is not part of it (:143-145, :168-177). The user sees 'Reference data imported
successfully! Cell Types: 12' and every cell keeps the label from the reference that was just
deleted; those labels flow into UMAP colouring, the confidence map and the PLP export. Two reviewers
found this independently.

**Proposed change**

In all three handlers, after ReloadTranscriptomicReferenceAsync(): call new
CellTypeClassificationService(projectDb).DeleteAllCellTypeClassificationsAsync(importId) + expose
and call the manager's existing ClearCache() (CellTypeClassificationManager.cs:371), then either re-
run AutoRunCellTypeClassificationAsync (MainWindow.xaml.cs:1878) or prompt 'The reference changed.
Reclassify N cells now?'. Also add a pre-write confirmation naming what is destroyed (profile count
+ classification count) and route it through the existing ProjectBackupService, exactly as
ConditionsBrowserControl.xaml.cs:165-191 already does for condition deletes.

---

### U2. A brand-new project dead-ends on the app's own step 1

- **Effort:** small
- **Where:** `Controls/MainControl.xaml.cs:264-285; MainWindow.xaml.cs:975-1010; Dialogs/ImportParquetDialog.xaml.cs:676-687; Dialogs/ImportParquetDialog.xaml:226-233`

**Why it matters**

This is the very first thing every new user does. On open with no data, MainControl pops a modal
telling them '1. Go to Import -> Parquet File...' (MainControl.xaml.cs:264-285). But
ValidateImportButton requires SelectedPlate != null (ImportParquetDialog.xaml.cs:676-687),
NewProjectDialog.Create_Click only calls CreateProjectAsync — no plate is ever created
(NewProjectDialog.xaml.cs:157-161; the only CreatePlateAsync call sites are MainWindow.xaml.cs:800
and ImportGeneMatrixDialog.xaml.cs:146), and ImportParquet_Click does no plate check at all, unlike
ImportCellenOneRun_Click which blocks with a clear message (MainWindow.xaml.cs:902-914). The user
assigns every condition by hand and the Import button never enables. The only counter-signal is a
10px red italic hint (ImportParquetDialog.xaml:226-233). The xlsx route silently auto-creates a
plate, so the two import paths disagree.

**Proposed change**

Three small edits: (a) make the no-data MessageBox list 'Import -> Import Plate Metadata...' as step
1; (b) copy the hasPlates guard from ImportCellenOneRun_Click into ImportParquet_Click; (c) in
ValidateImportButton, write the disabled reason next to the Import button from the branches you
already compute ('no file' / 'no plate registered' / 'N conditions unassigned') instead of relying
on the tiny hint.

---

### U3. A missing GO database disables Open Project, New Project and the recent list

- **Effort:** small
- **Where:** `MainWindow.xaml.cs:2618-2652`

**Why it matters**

CheckGoStatus sets OpenProjectButton, NewProjectButton, NewProjectMenuItem, OpenProjectMenuItem and
RecentProjectsList .IsEnabled = _goStatusService.IsReady. A user who installs SCPBrowser and
launches it finds every button on the welcome screen dead, and must download an ontology plus
species annotations before they can look at their own DIA-NN file. GO is one optional colouring mode
plus the BioTessera tab; MainControl.LoadGoEnrichmentAsync (MainControl.xaml.cs:183-202) already
degrades gracefully without it. This is the first ten minutes of the tool deciding whether it gets
adopted, and it is exactly the adoption criticism in the review.

**Proposed change**

Delete the five IsEnabled assignments at MainWindow.xaml.cs:2643-2651. Keep the amber GoStatusPanel
as an advisory with its one-click Configure button. Disable only the GO-dependent surfaces
(BioTessera tab header, GO colouring modes) with a tooltip pointing at Utils -> GO Database.

---

### U4. Two different quantity columns: the analysis uses Ms1.Area, everything else uses Precursor.Quantity

- **Effort:** medium
- **Where:** `Controls/MainControl.xaml.cs:352-359; Services/ParquetDataService.cs:730-736; Dialogs/ImportParquetDialog.xaml.cs:385, :786-791; Dialogs/ParquetReferenceLabelDialog.xaml.cs:108-109`

**Why it matters**

MainControl.LoadDataAsync builds the ColumnMapping the user actually sees and analyses
(PCA/UMAP/HVP/classification/PLP export) with TotalIonCurrentColumn = 'Ms1.Area'
(MainControl.xaml.cs:357). The import-time extraction that fills protein_quant_summary
(ParquetDataService.cs:735), the import preview (ImportParquetDialog.xaml.cs:385, :789) and the
builder that creates a proteomic REFERENCE from parquet (ParquetReferenceLabelDialog.xaml.cs:109)
all use 'Precursor.Quantity'. So the classifier scores MS1-area query cells against an MS2-quantity
reference, and the marker classifier reads protein_quant_summary — a third footing again
(MarkerClassificationService.cs:249-253). Neither column name appears anywhere in the UI, and
Ms1.Area is missing/zero far more often in single-cell runs, which also feeds the HVP detection
counts (HighlyVariableProteinsService.cs:316-345 treats every stored entry as an observation, and
ParquetDataService.cs:414-422 stores an entry whenever the value parses, including 0).

**Proposed change**

Minimal version, do this first: hoist one static ColumnMapping constant and use it from all four
sites so query cells, stored quant, preview and reference are on the same column. Then surface it: a
'Quantity column' dropdown in ImportParquetDialog (Precursor.Quantity / PG.MaxLFQ / Genes.MaxLFQ /
Ms1.Area), persisted in the parquet_imports.column_mapping_json that is already written
(ImportParquetDialog.xaml.cs:789-791), with the active column shown in the Protein Matrix header.

---

### U5. Closing the Project Browser with X silently switches the project's classifier

- **Effort:** small
- **Where:** `Controls/ProjectBrowser2.xaml.cs:264-283; MainWindow.xaml.cs:2309-2313`

**Why it matters**

CloseButton_Click builds ReclassifyRequestedEventArgs with ApplyKeyMarkers and PriorWeights but
never sets ClassificationMethod, whose declared default is 'Quantitative'
(OmicBrowserControl.xaml.cs:1312-1314). MainWindow then persists it verbatim —
SetSettingAsync('classification_method', e.ClassificationMethod ?? 'Quantitative') — and
reclassifies with forceRecompute. A user who deliberately chose 'Standard', adds one key marker and
closes with X has the whole dataset reclassified by a different algorithm, and the completion dialog
only mentions marker counts. The explicit Reclassify button does it correctly
(OmicBrowserControl.xaml.cs:941-946).

**Proposed change**

One line: in CloseButton_Click add ClassificationMethod = OmicBrowser.SelectedClassificationMethod
to the event args. Optionally name the active method in the reclassify-complete dialog.

---

### U6. PCA/UMAP always runs on only 2000 proteins, even with the HVP filter unchecked

- **Effort:** small
- **Where:** `Services/DataFilterService.cs:266-273; Controls/PeptideTicControl.xaml.cs:1727-1760; ScatterPlotControl.xaml.cs:339-346, :542-553`

**Why it matters**

ComputeHvpResults calls FindHighlyVariableProteins with a hardcoded nTopProteins:2000, and the
service stamps IsHighlyVariable = i < nTopProteins (HighlyVariableProteinsService.cs:393).
GetFilteredHvpResults returns that list unchanged when UseHvpFilter is false
(PeptideTicControl.xaml.cs:1735-1738, :1759), and BuildPreprocessedMatrix keeps only proteins
carrying the flag (ScatterPlotControl.xaml.cs:340-346, :547-553). So the checkbox switches between
top-2000 and top-N, never 'all proteins' — while its own tooltip says the filter is 'usually
unnecessary'. On a 4000-protein Astral dataset half the quantified proteome is dropped from every
embedding without the user asking, and they would describe the method incorrectly in the paper.

**Proposed change**

When UseHvpFilter is false, return null from GetFilteredHvpResults so BuildPreprocessedMatrix falls
through to the existing all-proteins else-branch (ScatterPlotControl.xaml.cs:549-553). Move the 2000
into DimensionReductionSettings as the visible HVP ranking pool, and print the realised protein
count in the plot header next to 'PCA'/'UMAP'.

---

### U7. Utils -> Clear Cell Type Classifications is a no-op and tells the user the opposite

- **Effort:** small
- **Where:** `MainWindow.xaml.cs:642-677; Controls/PeptideTicControl.xaml.cs:780-786; CellTypeClassificationManager.cs:143-149, :371`

**Why it matters**

The handler deletes the DB rows and nothing else, then promises 'Click Color by Cell Type to
recompute.' But PeptideTicControl only requests a recompute when _cellTypePredictions is null/empty
(PeptideTicControl.xaml.cs:780-786) — it isn't — so it recolours with the labels just 'cleared'; and
even the request path would hit the manager's surviving _cachedPredictions for the same (importId,
method). The user clears precisely because they distrust the labels, is told it worked, and sees
identical results. Their only real remedy is closing and reopening the project.

**Proposed change**

After the DB delete, call the manager's ClearCache() (expose it from MainControlTab) and push
PeptideTicTab.SetCellTypePredictions(null, null) so the colour mode relocks and the next selection
genuinely recomputes.

---

### U8. Decimal dimensionality-reduction settings silently revert on comma-decimal locales

- **Effort:** small
- **Where:** `Models/DimensionReductionSettings.cs:74, :80 vs :142-147`

**Why it matters**

SaveAsync writes ClipMaxValue and GuidedWeight with ToString('R') — current culture — while
ReadDoubleAsync parses with CultureInfo.InvariantCulture. On a pt-BR machine (i.e. the author's own)
3.5 is stored as '3,5', fails TryParse on reload and silently falls back to the hardcoded default.
The user tunes clip and guided weight, gets the figure they want, reopens the project and the
settings are quietly gone — so the saved figure is not reproducible from the project. Two-word fix,
and it is a reproducibility claim the manuscript depends on.

**Proposed change**

Use ToString('R', CultureInfo.InvariantCulture) in SaveAsync so writer and reader agree. Audit the
sibling double->SetSettingAsync calls at PeptideTicControl.xaml.cs:683 and
SettingsControl.xaml.cs:97-99 for the same asymmetry.

---

### U9. Clear Exclusions issues a global DELETE with no confirmation and flips the UI before the DB call

- **Effort:** small
- **Where:** `Controls/SelectedPointsGridControl.xaml.cs:147-160; Controls/ProteinHistogramControl.xaml.cs:144-147; MainWindow.xaml.cs:2411-2427; Services/ParquetDataService.cs:981-984`

**Why it matters**

Excluding bad cells one by one from the histogram/scatter IS the QC step and can take an hour. Both
Clear Exclusions buttons fire straight through: ClearExclusionsButton_Click sets IsIncluded = true
on every row first and only then raises the event, and the handler calls ClearAllExclusionsAsync,
which is a bare 'DELETE FROM excluded_runs' — global, not scoped to the current selection or plate,
despite sitting on a per-selection grid. No confirmation, no undo, and a failed delete leaves the
screen showing everything included while the DB still has exclusions.

**Proposed change**

Confirm with the count you already compute: 'Restore all N manually excluded runs? This cannot be
undone.' Raise the event first and update the checkboxes only after the await succeeds so the UI
cannot diverge from the DB.

---

### U10. PLP export defaults to a label pair that yields zero exportable runs

- **Effort:** small
- **Where:** `Controls/ExportPLPControl.xaml:91-105; Controls/ExportPLPControl.xaml.cs:200-257; Services/PLPExportService.cs:100-105, :130-137`

**Why it matters**

Primary defaults to Biological Condition and Secondary to Cell Type (both IsSelected='True',
ExportPLPControl.xaml:91-105). GetRunLabel returns null for CellType with no predictions and for
KMeans with no clustering, and GetExportSummary skips a run if either label is null
(PLPExportService.cs:130-137). With no reference imported — a completely valid workflow — every run
is skipped, TotalRuns is 0, Export stays disabled and the only feedback is 'N run(s) skipped: No
label available for the selected mode'. This is the last step of the pipeline, and nothing points at
the Secondary dropdown.

**Proposed change**

Default Secondary to Plate when _cellTypePredictions is empty; annotate the CellType and KMeans
ComboBoxItems '— requires classification' / '— requires k-means' and disable them when the backing
dictionary is empty; make the skip message name the culprit ('N run(s) have no Cell Type label —
change Secondary Label, or classify cells first').

---

### U11. Parquet import is not transactional, its Cancel button stays live, and one path hangs the dialog forever

- **Effort:** medium
- **Where:** `Dialogs/ImportParquetDialog.xaml.cs:735-885; Dialogs/ImportGeneMatrixDialog.xaml.cs:189-208`

**Why it matters**

Import_Click commits three uncoordinated units — InsertParquetImportAsync (:809),
InsertRawFilesAsync (:822), ExtractAndStoreProteinQuantAsync (:838). If the third throws, the import
and raw_files rows stay committed with an empty protein_quant_summary: the Explorer still renders
(it re-reads the parquet directly) but marker classification silently sees no genes and the mixed-
format guard now blocks the xlsx importer. Only ImportButton/PlateComboBox/RawFilesDataGrid are
disabled during the write (:742-745) — Cancel (IsCancel, so Esc too) is not, so closing mid-write
makes line :851 set DialogResult on a closed window and surface 'Error during import' for an import
that succeeded. And the duplicate-filename branch returns at :770 inside the try, while the finally
(:867-873) never restores this.Title/this.Cursor — the user is left staring at 'Importing... Please
wait' with a wait cursor while nothing runs. ImportGeneMatrixDialog.xaml.cs:189-208 already does
full rollback correctly.

**Proposed change**

Copy the gene-matrix pattern verbatim: track committedImportName and call DeleteParquetImportAsync
in the catch. Disable Cancel (or set an _importing flag checked in Cancel_Click and an OnClosing
override) for the duration. Hoist originalTitle above the try and restore Title/Cursor in the
finally.

---

### U12. Cell Plate Viewer Keep/Discard never reaches the analysis

- **Effort:** medium
- **Where:** `Controls/CellPlateViewerControl.xaml:137-148; Controls/CellPlateViewerControl.xaml.cs:566-595; Models/CellenOneModels.cs:130; Services/CellenOneQueryService.cs:113-124`

**Why it matters**

Flag/Keep/Discard write isolated_cells.review_status and nothing else — a repo-wide grep shows the
only consumers are CellPlateViewerControl itself, CellenOneQueryService.cs:53/117 and the
schema/migration in ProjectDatabaseService.cs:86/570-576. DataFilterService, ScatterPlotControl,
CellTypeClassificationManager and PLPExportService never consult it; run exclusion is a separate
mechanism (excluded_runs). Meanwhile the viewer shows a 'Quality review' box, F/K/D shortcuts, a
live kept/discarded counter and a Show: filter that hides discarded tiles. A researcher works
through 300 brightfield images discarding doublets, then finds every one of them still in the UMAP,
still classified, still in the PLP export. The only statement of the truth is a code comment
(CellenOneModels.cs:130), invisible to the user.

**Proposed change**

Wire it: on 'discard', if the cell has a reconciled raw_file_id, call
ParquetDataService.ExcludeRunAsync with reason 'cellenONE QC: discarded' — the excluded-runs grid
already has an Exclusion Reason column. If annotation-only is intentional, add a persistent line in
the Quality review panel saying so plus an 'Apply N discards as run exclusions' button.

---

### U13. The app's own guidance names menus that do not exist, and is overwritten one line later

- **Effort:** small
- **Where:** `Controls/MainControl.xaml.cs:162, :190, :199, :435-438; Controls/PeptideTicControl.xaml:234; Controls/PeptideTicControl.xaml.cs:666`

**Why it matters**

MainControl sets 'No reference profiles imported — use Reference menu to import omic data.' (:162)
and 'GO database not configured. Use Tools -> GO Database...' (:190, :199) — there is no Reference
menu and no Tools menu; the real paths are Import -> Omic Profile (MainWindow.xaml:75-78) and Utils
-> GO Database (:84). Worse, LoadDataAsync calls both and then unconditionally overwrites with
'Loaded successfully: N runs' (:435-438), so neither warning is ever visible. The same wrong menu
name is baked into the disabled Cell Type tooltip the user does read (PeptideTicControl.xaml:234).
This is the gate on the entire classification half of the pipeline.

**Proposed change**

Fix the strings in all four places to 'Import -> Omic Profile...' and 'Utils -> GO Database...', and
make LoadDataAsync append rather than overwrite — e.g. 'Loaded N runs — no reference profile
imported yet (Import -> Omic Profile)' or a separate persistent warning TextBlock beside StatusText.

---

### U14. Reconcile Cells <-> Runs: silent empty state, unguarded handlers, non-atomic clear-then-apply, no confirm on Clear All

- **Effort:** medium
- **Where:** `MainWindow.xaml:86-95; MainWindow.xaml.cs:210-220; Dialogs/ReconcileCellsDialog.xaml.cs:41-55, :59-90, :113-138; Services/CellRunReconciliationService.cs:115-155`

**Why it matters**

This is the link the whole cellenONE half of the pipeline depends on, and it is the least defended
dialog in the app. (a) ReconcileCellsMenuItem and CellViewerMenuItem carry no IsEnabled='False' and
are absent from the enable block at MainWindow.xaml.cs:210-220, so they look live on the welcome
screen. (b) With no cellenONE run imported, Initialize gets runs.Count == 0, LoadRunAsync early-
returns and InfoText — which has no default text — stays blank: a grid with no message at all. (c)
Initialize (async void, :41), Apply_Click (:113) and ClearAll_Click (:132) have no try/catch, and
there is no global handler, so a locked project.db takes the whole app down. (d) Apply_Click calls
ClearLinksAsync then ApplyLinksAsync as two separate commits
(CellRunReconciliationService.cs:117-155) — a failure between them leaves the run fully unlinked.
(e) ClearAll wipes every link with no prompt. The Cell Plate Viewer already gets the empty state
right (CellPlateViewerControl.xaml.cs:80).

**Proposed change**

Add IsEnabled='False' to both menu items and set them true/false alongside the others. In
Initialize, when runs.Count == 0 set InfoText to 'No cellenONE runs in this project — use Import ->
Import Plate Metadata...' and disable Suggest/Apply; when _raws.Count == 0 say the plate has no DIA-
NN runs yet. Wrap the three handlers in try/catch. Move the clear into the transaction
ApplyLinksAsync already opens. Confirm ClearAll with the run name and link count.

---

### U15. No global exception handler — any unguarded async void handler kills the app

- **Effort:** small
- **Where:** `App.xaml.cs:12-16; MainWindow.xaml.cs:1293-1296`

**Why it matters**

App.xaml.cs contains only SQLitePCL.Batteries.Init() — no DispatcherUnhandledException, no
AppDomain.UnhandledException. Any exception escaping a handler terminates the process with the
Windows crash dialog, and the codebase has several bare async void handlers on the critical path
(Reconcile above; CellPlateViewerControl.SetSelectedReview catches, but Initialize paths elsewhere
do not). There is also no OnClosing anywhere, so File -> Exit closes during a cellenONE import or a
classifier evaluation with no check on _cellenOneImportRunning (MainWindow.xaml.cs:1293-1296). A
bench scientist has nothing to report and no way to recover.

**Proposed change**

Add DispatcherUnhandledException in App.xaml.cs: one friendly dialog naming the project path, append
the full exception to a log file next to the project, set e.Handled = true. Add an OnClosing
override on MainWindow that confirms while _cellenOneImportRunning or an import flag is set.

---

### U16. New Project 'overwrite' silently destroys an entire existing project

- **Effort:** small
- **Where:** `Dialogs/NewProjectDialog.xaml.cs:129-155; Controls/ConditionsBrowserControl.xaml.cs:165-209 (the pattern to copy)`

**Why it matters**

If project.db exists the user gets a MessageBoxImage.Question asking 'Do you want to overwrite it?'
and Yes runs File.Delete — taking imported runs, plate registrations, cellenONE cells and images,
reconcile links, the omic reference, classifications, k-means labels, marker classes and exclusions
with it. No backup, no undo, no statement of contents, and the Question icon signals routine.
imports/ is deliberately left behind, so the parquet files survive as orphans. The folder picker is
a SaveFileDialog workaround (:24-31), so picking the wrong folder is easy. The codebase already
contains the right pattern — the condition cascade delete shows the blast radius, requires typing
the name, and takes a mandatory backup that aborts the delete if it fails.

**Proposed change**

Query the existing DB for counts (raw files, plates, isolated cells, classifications), show them,
switch to MessageBoxImage.Warning, and call ProjectBackupService.CreateProjectBackupAsync before
File.Delete, telling the user where the backup went. Cheapest acceptable version: make 'Open the
existing project instead' the default action.

---

### U17. The 800-protein QC cutoff is hardcoded in three places, unexplained, and absent from Settings

- **Effort:** small
- **Where:** `MainWindow.xaml.cs:242; Services/DataFilterService.cs:37-39, :327, :451; Controls/MainControl.xaml:108-114; Controls/SettingsControl.xaml`

**Why it matters**

800 is the study's main QC gate and it is a magic constant (GetSettingAsync fallback at
MainWindow.xaml.cs:242, field initialiser and Clear() at DataFilterService.cs:37, :451; upper
sentinel 99999 at :38, :327). The spinner in MainControl.xaml:108-114 has no tooltip explaining what
it filters or how to choose it, and SettingsControl exposes only GO p-value, GO minimum overlap and
the confidence threshold. A user on a lower-sensitivity platform silently loses most of their cells
the moment the project opens, sees a near-empty UMAP and concludes the tool is broken; a user on a
high-sensitivity platform keeps empty wells.

**Proposed change**

Add a tooltip on the ProteinCutoff spinner stating what it removes and that it applies before
PCA/UMAP/classification/export, and show live 'n pass / n excluded' next to it (the histogram
already has the distribution). Move the default into SettingsControl beside the other analysis
parameters.

---

### U18. Evaluate Classifier benchmarks whichever classifier the checkbox defaults to, not the project's

- **Effort:** small
- **Where:** `Dialogs/ClassifierEvaluationDialog.xaml:60-62; Dialogs/ClassifierEvaluationDialog.xaml.cs:90-100; MainWindow.xaml.cs:1100-1110 (correct resolution to copy)`

**Why it matters**

QuantScorerCheck is hardcoded IsChecked='True' in XAML and read straight into
Options.UseQuantitativeScorer (ClassifierEvaluationDialog.xaml.cs:97). It never consults the
project's classification_method setting or GetStoredScorerMethodAsync — which the Confidence Map
path does resolve correctly. A project running the Standard classifier is still benchmarked as
QScore by default, and neither the results text nor the exported HTML/CSV records which method was
measured. The accuracy number quoted in the paper can therefore describe a different classifier than
the one that produced the figures.

**Proposed change**

Preselect QuantScorerCheck from the project's resolved method using the same chain as
ConfidenceMap_Click, show the resolved method as text next to the checkbox ('project currently uses:
QScore'), and stamp the method name into FormatSummary and the exported report.

---

### U19. 'Confidence' is a forced relative share, not a probability — and the Settings help text states the wrong default

- **Effort:** medium
- **Where:** `CellTypePredictor.cs:205-231, :253-281; Controls/SettingsControl.xaml:100; Settings.settings:20-22`

**Why it matters**

CompositeScore is the mean of four per-metric softmaxes, then multiplied by marker/prior boosts and
renormalised so the scores sum to 1 across the reference's cell types; Confidence is just the top
value (CellTypePredictor.cs:205-231, :253-281 — the code comment even calls them 'true
probabilities'). The four temperatures (0.1 / 2.0 / 0.15 / auto-scaled 0.2x median) are literals
that appear nowhere in the UI yet alone determine how peaked the number is. Because the scores are
forced to sum to 1 the model can never say 'none of these': a cell of a type absent from the
reference still gets a high confidence for whichever class it least dislikes. On top of that,
SettingsControl.xaml:100 tells the user 'default 50%' while the actual default is 0.1
(Settings.settings:20-22).

**Proposed change**

Relabel the column in the UI and exports as 'relative score (top class share)', and add the runner-
up margin (top minus second — already available from the ordered Scores dictionary) as a second
column. Print the four temperatures into the diagnostics export. Fix the Settings help text to say
10%.

---

### U20. GO enrichment is force-run per cell against a genome background on every project load

- **Effort:** medium
- **Where:** `Controls/MainControl.xaml.cs:183-240, :436; GOTools/GoEnrichmentManager.cs:57-78, :132-155; MainWindow.xaml.cs:77, :1662-1674`

**Why it matters**

LoadDataAsync awaits LoadGoEnrichmentAsync before raising DataLoaded (MainControl.xaml.cs:436), and
EnrichAllRuns loops every run calling EnrichRun individually (GoEnrichmentManager.cs:57-78), each
testing that one cell's detected gene list against the loaded species background. Results are
memory-only and recomputed on every open, every import and every F5, with no opt-out and no cancel —
and they run on unfiltered _currentData, including cells about to be filtered away. The result is
also biased: one cell's ~1500 proteins against a 20000-gene background makes housekeeping terms
'enriched' in essentially every cell, so the per-cell top term is close to uninformative.
Separately, changing the GO p-value in Settings never re-runs anything: the only SettingsSaved
subscriber is ApplyConfidenceThresholdFromSettings (MainWindow.xaml.cs:77), so the user tightens
0.05 to 0.01, is told it saved, and keeps looking at old results.

**Proposed change**

Make enrichment lazy — compute on first GO colouring selection or first BioTessera tab activation,
reusing the tab-switch hook at MainWindow.xaml.cs:1662-1674 — and cache against the filtered
dataset. Use the union of proteins detected across the dataset as the background and state which
background is used in the GO panel. In the SettingsSaved handler, compare the GO values to the ones
last used and invalidate the cache (or show 'GO settings changed — press F5 to re-run').

---

### U21. Nothing records or exports the settings that produced a figure

- **Effort:** medium
- **Where:** `Controls/ExportPLPControl.xaml.cs:387; ScatterPlotControl.xaml.cs:2088-2134; Services/EvaluationReportBuilder.cs (reusable)`

**Why it matters**

Parameters live in three disconnected stores — per-project DB keys
(DimensionReductionSettings.cs:73-84, ProteinCutoff, classification_method, GO cutoffs), global user
settings (Settings.Designer.cs), and pure session state (contaminant-ratio cutoff, lasso selection,
checkbox filters). No screen shows them together and no export includes them: the UMAP PNG export
writes pixels only (ScatterPlotControl.xaml.cs:2088-2134) — no seed, neighbours, PC count, HVP
state, batch-correction state, protein cutoff, or excluded runs. A reviewer asking 'what settings
produced Figure 3?' cannot be answered, and because the contaminant cutoff and lasso selection are
never persisted, repeating the steps after reopening can give a different set of cells. For a
methods paper this is the reproducibility gap the reviewers were pointing at.

**Proposed change**

Add Utils -> 'Export analysis report...' writing one self-contained HTML/TSV: project name, imported
files with hashes and quant column, per-plate and per-condition counts, every active filter with the
number of cells it removed, the dimensionality-reduction block, batch-correction state and any
warning raised, classifier method plus markers/priors/exclusions, and app version.
EvaluationReportBuilder is already an HTML generator in the codebase and can be reused. Write the
same block as a sidecar .txt whenever a plot PNG is saved.

---

### U22. Add a pipeline status strip so the user can see where they are

- **Effort:** medium
- **Where:** `MainWindow.xaml:157-173, :178-280; MainWindow.xaml.cs:202-220; Controls/MainControl.xaml.cs:264-285`

**Why it matters**

The pipeline has eight ordered stages but the UI exposes none of the ordering: the welcome screen
offers only Open/New, the four tabs are named after artifacts, UpdateWindowTitle shows version +
name, and OpenProjectAsync enables ten menu items at once regardless of what the project contains
(MainWindow.xaml.cs:210-220). Almost every item above is a symptom of the same absence. All the
state needed is already queried during open — plates, GetAllImportedParquetFilesAsync,
IsTranscriptomicDatabaseLoaded, classifications, fasta_path. This is the most direct answer to the
'basic functionality / will it be adopted' criticism, because the pipeline exists in the code and is
simply invisible in the product.

**Proposed change**

Add a thin status strip above the TabControl next to PlateFilterControl: Plates N · Cells N (M
reconciled) · MS runs N · Reference check/dash · Classified N · FASTA check/dash, each unmet item a
clickable link to the menu action that satisfies it. On a project with zero plates and zero imports,
render the same strip as a 'first steps' panel in the Protein Groups tab instead of the current
modal MessageBox (which currently fires on every open of a plates-but-no-MS-data project).

---

### U23. No q-value/FDR filtering at import — protein counts are not comparable to published SCP numbers

- **Effort:** medium
- **Where:** `Services/ParquetDataService.cs:46-51, :98-105, :378-447; Dialogs/ImportParquetDialog.xaml`

**Why it matters**

ColumnMapping carries only Run / Protein.Group / peptide / quantity (ParquetDataService.cs:98-105);
the row loop counts a protein group as present if any row mentions it, never reading Q.Value,
PG.Q.Value, Global.PG.Q.Value or Lib.PG.Q.Value — all listed as available in the file's own header
comment at :46-51 and then never used. So 'Protein Groups' per cell, the histogram, and the
800-protein cutoff are computed over unfiltered DIA-NN output including MBR propagations that the
field routinely filters at 1% FDR. A user comparing depth to a published paper gets an inflated
number and sets their QC threshold against the wrong scale.

**Proposed change**

Add q-value fields to ColumnMapping plus a 'Confidence filtering' box in ImportParquetDialog
(precursor Q.Value <= 0.01, PG.Q.Value <= 0.01, both on by default, editable), apply them in the
LoadParquetFileAsync row loop, and record the applied thresholds in column_mapping_json so the
filter travels with the import and can be reported in the methods section.

---

### U24. No per-cell normalisation, and missing values are imputed as zero before z-scoring

- **Effort:** medium
- **Where:** `ScatterPlotControl.xaml.cs:520-571; Models/DimensionReductionSettings.cs; Controls/PeptideTicControl.xaml:60-170`

**Why it matters**

BuildPreprocessedMatrix is the entire preprocessing pipeline: select proteins -> log2(x+1) ->
optional ComBat -> optional per-protein z-score -> clip. There is no per-cell/column normalisation
step anywhere in the repo (a normaliz* grep returns only colour mapping and prior weights), and line
:566-569 writes 0 whenever the protein is absent or non-positive for that cell. With 40-70%
missingness, cells differing only in input amount or cell size separate in PCA/UMAP as if they were
biologically different, and PC1 easily becomes a depth/missingness axis that the user then names as
a cell population. Neither decision is stated in the UI, and the HVP service's own documentation
(HighlyVariableProteinsService.cs:58-63) says explicitly that missing must not be treated as zero.

**Proposed change**

Add a 'Normalisation' row to the existing dimensionality-reduction popup (None / median-centre per
cell / total-intensity) applied to the log2 matrix before ComBat, and a 'Missing values' row (zero /
protein-mean / excluded from z-score), both persisted in DimensionReductionSettings beside
ZScoreScale. Show the realised missingness rate of the selected matrix in the plot header.

---

### U25. One parquet file can only be assigned to one plate

- **Effort:** medium
- **Where:** `Dialogs/ImportParquetDialog.xaml.cs:752-777, :816-820; Dialogs/ImportParquetDialog.xaml:272-306, :496`

**Why it matters**

Import_Click stamps rawFile.PlateId = SelectedPlate.PlateId on every raw file and renames the file
to {PlateName}.parquet, which then blocks a second import onto the same plate. The grid has a per-
row Biological Condition editor but no per-row plate. Meanwhile DIA-NN emits one report for the
whole study, so the realistic case — 300 cells across four plates searched together — cannot be
imported correctly. Plate is the batch variable ComBat corrects on
(ScatterPlotControl.xaml.cs:576-582), so collapsing four plates into one destroys the batch
structure, makes Batch Correction a no-op and makes the plate filter meaningless. The alternative is
splitting the parquet outside the tool, which this user base cannot do.

**Proposed change**

Add a Plate column to the raw-file grid reusing the wildcard-filter + 'Apply to Filtered' mechanism
already built for Biological Condition (ImportParquetDialog.xaml:272-306), defaulting every row to
the selected plate. Stop renaming the imported file after the plate and key the duplicate check on
the file hash already computed at :780.

---

### U26. Marker-class results are stored untagged and can be destroyed on the next project open

- **Effort:** medium
- **Where:** `Services/MarkerClassificationService.cs:220-222; Services/CellTypeClassificationService.cs:22-23; CellTypeClassificationManager.cs:158-197; MainWindow.xaml.cs:1742, :1878`

**Why it matters**

MarkerClassificationService.ClassifyAndStoreAsync calls SaveCellTypeClassificationsAsync without a
scorerMethod, so rows land tagged 'Standard' — the same tag the reference-based StandardClassifier
uses (CellTypeClassificationService.cs:22-23). There is no marker-vs-reference provenance. On the
next open, if a reference is loaded AutoRunCellTypeClassificationAsync runs
(MainWindow.xaml.cs:1742): with classification_method = 'Quantitative' the method check fails, the
manager recomputes and line :194-196 DELETEs every marker row; with 'Standard' the marker rows are
instead served back as though the reference classifier produced them. Hand-curated marker
classification is the route that needs no reference at all, and it is the least protected.

**Proposed change**

Pass an explicit scorerMethod ('Markers') from MarkerClassificationService, add it to the method-
resolution ladder in GetOrComputePredictionsAsync, and skip the auto-reclassify when the stored
method is 'Markers' unless the user asks. Minimum viable: prompt before the delete when the stored
rows came from a different route.

---

### U27. Four silent dead ends: Protein Coverage, BioTessera, the missing-imports message, and the TSV converter's stray project row

- **Effort:** small
- **Where:** `MainWindow.xaml.cs:287, :298-302, :557-592, :597-608, :1662-1674; TranscriptomicConverterUtility.cs:31-34; Services/ProjectDatabaseService.cs:162-185, :613, :645-647`

**Why it matters**

Each is small on its own but each makes the tool look broken. (a) InitializeProteinCoverageAsync
returns silently when fasta_path is unset or missing, so the panel is never initialised, the protein
dropdown stays empty and the placeholder still says 'Select a protein and click Load' — the user has
no way to learn that Import -> Search Database (FASTA) is the prerequisite. (b) BioTessera renders
only if _bioTesseraNeedsUpdate was set by an Explorer selection change, and both proteins.Count == 0
and the catch return silently (Debug.WriteLine only) — clicking straight to the headline GO tab can
give a permanently blank panel indistinguishable from a failure. (c) When the DB lists imports but
none resolve on disk (project folder copied/moved, OneDrive not synced) the exact missing path goes
to Debug.WriteLine and the user gets 'No data files found in imports folder. Please re-import your
data.' — naming no file, no folder, and prescribing the wrong fix; re-importing then renames and de-
duplicates into a worse mess. (d) TranscriptomicConverterUtility calls CreateProjectAsync on the
live project DB, which does an unconditional INSERT into project_info
(ProjectDatabaseService.cs:172-182), giving the user's project two rows; GetProjectInfoAsync reads
'LIMIT 1' with no ORDER BY (:613) and UpdateProjectInfoAsync has no WHERE (:645-647), so Edit
Project renames both.

**Proposed change**

(a) Add ProteinCoveragePanel.ShowNoFastaState() on the early-return path. (b) Set
_bioTesseraNeedsUpdate = true once when MainControlTab_DataLoaded completes and replace the two
silent returns with a visible message. (c) Build the missing list in the loop and show it with the
full imports path plus a 'Open folder' Process.Start. (d) Split schema creation from the project-row
insert (EnsureSchemaAsync) and have the converter call that; add ORDER BY project_id to the read and
a WHERE to the update.

---

### U28. Ship a cRAP contaminant list so the contaminant-ratio QC can actually fire

- **Effort:** small
- **Where:** `Controls/ProteinMatrixControl.xaml:16-28, :95-96; Controls/ProteinMatrixControl.xaml.cs:549-596; Services/DataFilterService.cs:39, :342-367`

**Why it matters**

Marking contaminants requires per-row checkboxes or selecting rows and using the context menu
(multi-select does work — ToggleContaminantOnSelectedRows iterates all selected rows). What is
missing is a bundled cRAP/keratin/trypsin/albumin list, pattern-based marking, and accession-list
import. Faced with a 3000-row matrix, the realistic user marks nothing — so the contaminant ratio is
0 for every cell, the Contaminant Ratio colouring is flat, and the ContaminantRatioCutoff filter
(default 1.0, i.e. inert, DataFilterService.cs:39) can never remove anything. A QC feature described
in the paper's workflow silently never runs, and keratin-dominated wells stay in the analysis.

**Proposed change**

Ship a default cRAP accession list and add 'Mark all matching...' driven by the existing
SearchTextBox expression (ProteinMatrixControl.xaml:16-28), plus 'Load contaminant list from file'.
Show a one-line banner in the Protein Matrix when zero contaminants are marked, stating that
contaminant-ratio QC is inactive until some are.

---

## Manuscript implications

Flagged by the UX synthesizer as things that may make the current Methods text inaccurate, or that answer a
reviewer's criticism directly. **These need your judgement, not a code change.**

- Direct answer to 'uncertainty quantification': items 19 and 18. The reported 'Confidence' is a
- renormalised relative share across the supplied reference classes, governed by four undocumented
- softmax temperatures (CellTypePredictor.cs:212-221), with no 'none of the above' outcome — a cell
- of a type absent from the reference still gets a high number. Relabelling it 'relative score (top
- class share)' and adding the runner-up margin is a small change that converts an overclaim into a
- defensible, reportable quantity. Pair it with item 18 so the Evaluate Classifier accuracy you
- quote provably describes the classifier that produced your figures.

- Strongest existing asset against the 'basic functionality' charge, and it is currently undersold:
- ClassifierEvaluationDialog is a genuine benchmark — stratified k-fold with repeats, LOOCV,
- protein-subset stability, a learning curve capped below the smallest class, per-channel
- contribution analysis, an HTML figure report and a per-cell CSV — with two invariants documented
- at the class level: every fold rebuilds its reference from training cells only, and nothing is
- persisted to the project (ClassifierEvaluationDialog.xaml:17-27; .xaml.cs:77-100, :156-191). Most
- desktop SCP tools have nothing equivalent. Name it in the response and reproduce one of its
- figures.

- Answer to 'method documentation' and reproducibility: item 21 (Utils -> Export analysis report).
- Today no export records the seed, neighbours, PC count, HVP state, batch-correction state, protein
- cutoff or excluded runs, and the contaminant cutoff and lasso selection are not persisted at all —
- so a figure cannot be regenerated from the project. One HTML/TSV provenance export
- (EvaluationReportBuilder is already in the codebase) turns 'what settings produced Figure 3?' into
- an artefact you can attach as Supplementary Data.

- Methods-section corrections you should make BEFORE resubmitting, because the manuscript may
- currently describe the pipeline incorrectly: (a) PCA/UMAP run on the top 2000 HVPs even when the
- HVP box is unchecked (item 6); (b) the quantity column is Ms1.Area for the analysis but
- Precursor.Quantity for stored quant and any parquet-built reference (item 4); (c) there is no per-
- cell normalisation and missing values are imputed as zero before z-scoring (item 24); (d) protein
- counts and the 800-protein cutoff are computed with no q-value filter (item 23). Each of these is
- a sentence in the Methods that is presently wrong or unstated.

- Adoption answer: item 22 (pipeline status strip) plus items 2, 3 and 13. The reviewers' 'will it
- be adopted' concern is empirically supported by the first-run path — a new user cannot open a
- project without a GO database, then cannot complete the import the app itself told them to start,
- and the only guidance strings name two menus that do not exist. Fixing those three and adding a
- visible stage indicator lets you claim, honestly, that the workflow is guided end-to-end rather
- than an assembly of tools.

- Honest strengths worth citing verbatim in the rebuttal: batch correction passes biological
- condition as a preserved covariate, falls back to plate-only on a singular design, and surfaces
- each failure mode in the plot header rather than silently changing the embedding
- (ScatterPlotControl.xaml.cs:497-518, :573-616). The HVP implementation is a real Seurat v3 VST
- port with tricube-weighted local quadratic LOESS, clipping, a citation, and a guard that throws on
- log-transformed input (HighlyVariableProteinsService.cs:43-68, :409-527). The condition cascade
- delete shows its blast radius, requires type-to-confirm, and takes a mandatory backup that aborts
- the delete if it fails (ConditionsBrowserControl.xaml.cs:100-209). These are not 'basic'.

---

## What is already good (do not break these)

Grounded in code by the reviewers. Several are worth citing in the rebuttal.

- The destructive-action flow for biological conditions is exemplary and should be the template the
- rest of the app copies: blast radius computed and displayed first (raw files, plates, quant rows,
- classifications, exclusions, orphan imports), type-the-name-to-confirm, a mandatory backup that
- ABORTS the delete if it fails, a transactional cascade with a rollback report, and a closing
- message saying what was removed and where the backup lives
- (ConditionsBrowserControl.xaml.cs:100-228; DeleteConditionConfirmationDialog.xaml.cs:41-57).

- ProjectBackupService is solid infrastructure — SQLite hot backup via BackupDatabase so it works
- with connections open, recursive imports/ copy, timestamped reason-tagged paths built once so the
- path shown is the path written, size estimation, and cleanup of a partial backup on failure. It is
- already there; items 1 and 16 just need to call it.

- The mixed-format guard is a real correctness protection with an actionable message in both
- directions, explaining why parquet and xlsx intensities are not comparable rather than silently
- loading one (MainWindow.xaml.cs:987-997, :1163-1173).

- The late-cellenONE-import path is genuinely well thought out: it deduplicates re-imports and
- reports which happened, tells the user the cells are not yet linked, offers to open Reconcile
- immediately, and preselects the just-imported run — with a comment explaining that without the
- preselection Apply would wipe another run's hand-made links (MainWindow.xaml.cs:926-966).

- The Excel gene-matrix importer does full rollback on partial failure (tracks committedImportName
- and copiedFilePath, undoes both), with a comment explaining exactly why an orphan row would be
- unrecoverable. This is the pattern item 11 should copy onto the parquet path.

- Parquet import refuses to proceed while any raw file lacks a biological condition and enumerates
- the offenders (up to 10, then '...and N more') — while keeping that tractable at 300 files via
- wildcard filtering plus 'Apply to Filtered' batch assignment (ImportParquetDialog.xaml.cs:690-724;
- .xaml:272-306).

- The menu and control tooltips refuse to oversell: the Confidence Map is described as 'a display,
- not a held-out estimate', Evaluate Classifier states 'nothing is saved to the project'
- (MainWindow.xaml:96-105), and the guided-embedding control carries a visible warning that it
- nudges the embedding with labels and is 'Not for discovery' (PeptideTicControl.xaml:169-170).
- Tools rarely warn users away from their own features.

- Every parameter in the dimensionality-reduction popup has a real explanatory tooltip with a stated
- default, a sensible range and the direction of the effect (PeptideTicControl.xaml:60-170). For a
- non-programmer audience this is the right level of hand-holding and is the model the protein
- cutoff, quantity column and classifier temperatures should follow.

- The Cell Plate Viewer's empty state names the exact menu path that fixes it
- (CellPlateViewerControl.xaml.cs:80) — the pattern the Reconcile dialog and Protein Coverage tab
- should copy.

- The classification cache is keyed by (importId, method) with the reasoning written down, stored
- rows are refused on a method mismatch so a method switch cannot display the other classifier's
- results, and SetClassificationMethod builds into a local first so a failed build cannot
- desynchronise the label from the active classifier (CellTypeClassificationManager.cs:20-25,
- :49-56, :165-177). The gaps in items 1 and 26 are additions to this design, not a rewrite of it.

- K-means does record provenance: the basis string ('UMAP axes, k=5') is computed, shown next to the
- control, and persisted alongside the assignments via SaveKMeansAssignmentsAsync(runToCluster, K,
- basis) (PeptideTicControl.xaml.cs:1030-1040).

- Concurrency and lifecycle care is above average for a WPF science app: per-project
- CancellationTokenSource, event unsubscription on close, a semaphore serialising filter passes, a
- generation counter guarding stale deferred selection events, a re-entrancy guard on k-means,
- debounced sliders, a _cellenOneImportRunning flag, and the target project path pinned before long
- awaits so a project switch mid-import cannot apply results to the wrong project. The CODEMERGER
- single-source-of-truth notes are a good regression defence.

- F5 / File -> Reload Project Data is a first-class escape hatch that re-pulls imports, plates and
- plate mapping through the whole dependent-tab chain, including refreshing an open Project Browser
- (MainWindow.xaml.cs:2208-2268).

---

## Appendix A — Checked and dismissed

The correctness audit refuted **8** candidate findings; the UX synthesis dropped
**6**. Recorded so nobody re-investigates them.

**UX claims that did not hold up:**

- 'Contaminants can only be marked one protein at a time' (reviewer 2) — overstated.
- ToggleContaminantOnSelectedRows iterates every selected row, so multi-select + context menu
- already bulk-marks (ProteinMatrixControl.xaml.cs:559-596), and a live search/filter box exists
- (ProteinMatrixControl.xaml:16-28). The real, narrower gap — no bundled cRAP list, no pattern-based
- 'mark all matching', no accession-list import — is retained as item 28 at low priority.

- 'K-means labels persist looking authoritative with nothing recording what they were computed on'
- (reviewer 1) — does not hold as stated. The basis string is computed from CurrentViewBasis(),
- displayed in KMeansStatusLabel, and persisted with the assignments
- (PeptideTicControl.xaml.cs:1030-1040) — reviewer 2 correctly listed this as a strength. The
- residual real defect is only that the PLP export writes a bare 'Cluster N' with no basis, and that
- clustering on the QC (Peptide vs TIC) view is offered with no inline caution. Not worth a ranked
- slot on its own; fold the basis string into the export label when touching item 10.

- 'HVP selection counts zero-intensity entries as detections' (reviewer 2) — mechanically correct
- (ParquetDataService.cs:414-422 creates an entry whenever the value parses, including 0;
- HighlyVariableProteinsService.cs:316-345 treats every stored entry as an observation) but its
- magnitude is conditional on how often the chosen column writes literal zeros, which DIA-NN does
- far more for Ms1.Area than for Precursor.Quantity. Downgraded from a standalone major finding and
- folded into item 4, where fixing the column choice is the precondition anyway. The one-line guard
- (only store when tic > 0) is worth doing at the same time.

- 'The classification cache guard means a method switch can never silently display the other
- classifier's results' listed as a strength by two reviewers, while a third reports marker rows
- being served as reference output — not a contradiction. The guard is correct; it is defeated only
- because MarkerClassificationService omits the scorerMethod argument, which is item 26. Both
- statements retained in their respective sections.

- Reviewer 3's framing of the parquet-import Cancel bug as 'the user re-imports, producing
- duplicates' — the duplicate half is unlikely to reproduce, because a second attempt hits the
- {PlateName}.parquet existence check and is refused (ImportParquetDialog.xaml.cs:762-771). The
- confirmed harms are the phantom half-committed import and the misleading 'Error during import' on
- a successful run; item 11 is scoped to those.

- General 'errors are raw exception text' (reviewer 3, ~50 sites) — the observation holds, including
- ClassifierEvaluationDialog.xaml.cs:145 dumping a full stack trace into the results box the user is
- meant to read. But a repo-wide ErrorDialog helper is a medium-effort refactor with diffuse payoff,
- and it is out of proportion to the owner's remaining time before resubmission. Not ranked; do the
- stack-trace one-liner (change 'ex' to 'ex.Message') and leave the rest until after the revision.

---

## Verification protocol

For any item you fix:

1. **Build to a temp dir** so the running app is not locked:
   `dotnet build <csproj> -o <scratch>/bin_verify`
2. **Re-run the GroundTruth2 benchmark** (90 cells, 5 classes) and diff against the current numbers
   (LOOCV 100%, 5-fold 99.6%, margins 0.87-0.99 / 0.52, stability 91.6%). Any movement must be explainable.
3. **Locale check** for the I/O items: set the OS to a comma-decimal locale (pt-BR / de-DE) and re-export.
4. **Two-project check** for R1/R2/R-cache items: open project A, classify, open project B, confirm B is not
   showing A's reference, labels or cached predictions.
5. Confirm the build has **0 errors** and no new warnings in the files you touched.
