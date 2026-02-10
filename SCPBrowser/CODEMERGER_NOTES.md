# Project Notes

## DimRed Pipeline Roadmap

- [2026-02-10 12:32] ## Revised Dimensionality Reduction Pipeline Roadmap

### Step 1 — DimensionReductionSettings model + persistence
- New file: Models\DimensionReductionSettings.cs
- Fields: ZScoreScale(bool,true), ClipMaxValue(double,10.0), NumPcaComponents(int,30), NumPcsForUmap(int,20), UmapNeighbors(int,15), UseGuidedEmbedding(bool,false), GuidedWeight(double,0.3)
- min_dist/spread EXCLUDED (umap-sharp hardcodes 0.1/1.0, throws on other values)
- Persist via ProjectDatabaseService with "dimred." key prefix

### Step 2 — Settings popup UI in PeptideTicControl.xaml
- Gear button next to View ComboBox, enabled only for PCA/UMAP modes
- Popup with sections: Preprocessing, PCA, UMAP, Guided Embedding
- Guided Embedding checkbox only enabled when classifications exist, always shows warning
- Apply saves to DB + invalidates caches; Reset Defaults restores factory
- Dark theme: #1B1B2F bg, #6C63FF accent, rounded corners

### Step 3 — Shared BuildPreprocessedMatrix() method
- Extract duplicated matrix-building from ComputePca and ComputeUmap
- Pipeline: rawFiles → proteins (HVP-filtered) → log2 matrix → ComBat → NEW z-score scaling → clip ±MaxValue
- Z-score: per protein column, center + divide by stddev, skip if stddev==0

### Step 4 — PCA: compute N components
- Change nComponents from hardcoded 2 to settings.NumPcaComponents (default 30)
- Cap at Min(requested, nSamples-1, nProteins-1)
- PCA plot still displays PC1 vs PC2, full scores stored for UMAP
- Try NIPALS first; switch to SVD if slow

### Step 5 — UMAP: feed PCA coordinates
- Ensure PCA computed first, extract top NumPcsForUmap columns from scores
- If guided embedding: append weighted one-hot cell type vectors (weight * one-hot)
- Samples with no prediction get zeros (neutral)
- Pass combined matrix to Umap with settings.UmapNeighbors

### Step 6 — Wire settings through pipeline
- PeptideTicControl loads settings on startup, passes via ScatterPlotOptions
- ScatterPlotOptions gets DimensionReductionSettings property
- Setting changes invalidate _pcaResult/_umapResult, trigger recompute

### Step 7 — Label purity diagnostic
- kNN label purity in unsupervised PCA space
- For each sample, fraction of k nearest neighbors sharing same cell type
- Display percentage in settings popup or plot header
- High purity (>70%) = guidance is cosmetic; Low (<40%) = guidance overrides data

### Step 8 — Review & Cleanup
- Verify cache invalidation, persistence, dark theme
- Remove duplicated matrix code
- Test NIPALS at 30 components
- Check edge cases: few samples, no HVPs, no classifications

### Deferred
- min_dist/spread tuning (umap-sharp limitation)
- Leiden/Louvain clustering (separate scope)
- Library-size normalization (not appropriate for proteomics)
- Advanced missing value handling (zero-imputation is reasonable baseline)
- True graph-intersection supervised UMAP (one-hot approximation sufficient)
