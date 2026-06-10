using System.Collections.Generic;

namespace SCPBrowser.Models
{
    /// <summary>
    /// One imported cellenONE ".Run" (a single-cell isolation run, or a reagent-dispense provenance run).
    /// Populated by <see cref="SCPBrowser.Services.CellenOneParser"/> from the run folder, then persisted by
    /// <see cref="SCPBrowser.Services.CellenOneImportService"/>. This type is deliberately WPF-free so the parser
    /// can be exercised from a plain console / test harness.
    /// </summary>
    public class CellenOneRun
    {
        public int CellenOneRunId { get; set; }
        public int PlateId { get; set; }

        /// <summary>cellenONE "Run ID" from the logfile (e.g. 2V58C18_20260203_104727_315).</summary>
        public string? RunUid { get; set; }

        /// <summary>"isolation" (has isolated cells + images) or "dispense" (FA/trypsin reagent run).</summary>
        public string RunType { get; set; } = "isolation";

        public string? Instrument { get; set; }
        public string? SoftwareVersion { get; set; }

        /// <summary>Best-effort operator, parsed from the run directory path in the .par RunDirSettings.</summary>
        public string? OperatorName { get; set; }

        /// <summary>Run start time as an ISO-8601 string (matches the project's date storage convention).</summary>
        public string? RunDate { get; set; }

        public double? Humidity { get; set; }
        public int? TotalDetected { get; set; }
        public double? VolumeDispensedNl { get; set; }
        public double? Concentration { get; set; }

        /// <summary>e.g. "Transmission-Positive/Blue-Negative" from the logfile summary.</summary>
        public string? Channels { get; set; }

        /// <summary>Active target labware (e.g. MTP384_WholePlate_B2).</summary>
        public string? TargetLabware { get; set; }

        public int? FieldSizeX { get; set; }
        public int? FieldSizeY { get; set; }
        public int? DotPitch { get; set; }

        /// <summary>Well-ID template from the .par RunDirSettings ID (e.g. 2V58C18M-BM-$). Used later for run linkage.</summary>
        public string? IdTemplate { get; set; }

        /// <summary>Original source directory of the .Run (for provenance / re-import).</summary>
        public string? SourceDir { get; set; }

        /// <summary>Selected gating parameters captured from the .par [Cells] section, serialized as JSON for audit/display.</summary>
        public string? GatingParamsJson { get; set; }

        /// <summary>Resolved absolute path to the per-run background image (deduped - stored once on the run, not per cell).</summary>
        public string? BackgroundImagePath { get; set; }

        /// <summary>Raw logfile text, retained for audit.</summary>
        public string? LogfileText { get; set; }

        /// <summary>SHA256 of the canonical run file (isolated.xls for isolation runs, logfile otherwise) - drives idempotent re-import.</summary>
        public string? FileHash { get; set; }

        public List<IsolatedCell> Cells { get; set; } = new();

        /// <summary>All detected objects across all drops (the geoprops superset). Used for doublet/QC analysis.</summary>
        public List<CellDetection> Detections { get; set; } = new();
    }

    /// <summary>One detected object within a drop's image (a row of the geoprops superset).</summary>
    public class CellDetection
    {
        public int DropNo { get; set; }
        public string? Channel { get; set; }
        public int ObjectIndex { get; set; }
        public double? X { get; set; }
        public double? Y { get; set; }
        public double? Diameter { get; set; }
        public double? Elongation { get; set; }
        public double? Circularity { get; set; }
        public double? Intensity { get; set; }
    }

    /// <summary>
    /// A single isolated (printed) cell within a <see cref="CellenOneRun"/>. The stable per-cell key is
    /// <see cref="DropNo"/> (it also appears in the image filenames). <see cref="TargetWell"/> is NOT unique in
    /// grid/slide layouts, so consumers should plot by <see cref="XPos"/>/<see cref="YPos"/>/<see cref="Field"/>.
    /// </summary>
    public class IsolatedCell
    {
        public int CellId { get; set; }
        public int CellenOneRunId { get; set; }
        public int PlateId { get; set; }

        /// <summary>cellenONE sequential isolation index (the leading number in the image filenames).</summary>
        public int DropNo { get; set; }

        public string? TargetWell { get; set; }
        public int? Target { get; set; }
        public int? Field { get; set; }
        public int? XPos { get; set; }
        public int? YPos { get; set; }

        /// <summary>In-frame pixel position of the detected cell within its image (enables cell-centered crops).</summary>
        public double? ImageX { get; set; }
        public double? ImageY { get; set; }

        // Transmission (brightfield) morphology
        public double? Diameter { get; set; }
        public double? Elongation { get; set; }
        public double? Circularity { get; set; }
        public double? Intensity { get; set; }

        // Blue (fluorescence) channel
        public double? FluDiameter { get; set; }
        public double? FluIntensity { get; set; }

        /// <summary>Objects the instrument detected in this cell's drop (from geoprops); &gt;1 ⇒ doublet/multiplet risk.</summary>
        public int? NObjects { get; set; }

        /// <summary>Cell-sized blobs found in the brightfield frame by image analysis; &gt;1 ⇒ likely doublet (catches ones the instrument missed).</summary>
        public int? BlobCount { get; set; }

        /// <summary>User QC disposition set in the Cell Plate Viewer: null = unreviewed, "flag" = manually flagged for review, "keep", or "discard". Non-destructive — recorded only, does not alter the analysis.</summary>
        public string? ReviewStatus { get; set; }

        /// <summary>Optional free-text note explaining the review decision.</summary>
        public string? ReviewNote { get; set; }

        public string? Status { get; set; }
        public string? IsolatedAt { get; set; }

        /// <summary>Deferred link to the DIA-NN run (raw_files). NULL until reconciliation.</summary>
        public int? RawFileId { get; set; }
        public string? LinkMethod { get; set; }
        public double? LinkConfidence { get; set; }

        /// <summary>Resolved absolute path to the brightfield PNG (loaded to a blob at import time).</summary>
        public string? TransImagePath { get; set; }

        /// <summary>Resolved absolute path to the fluorescence PNG (loaded to a blob at import time).</summary>
        public string? BlueImagePath { get; set; }
    }

    /// <summary>Summary row for picking an imported cellenONE run in the viewer / reconcile UI.</summary>
    public class CellRunSummary
    {
        public int CellenOneRunId { get; set; }
        public int PlateId { get; set; }
        public string? PlateName { get; set; }
        public string? RunUid { get; set; }
        public string? RunDate { get; set; }
        public int CellCount { get; set; }

        public override string ToString()
            => $"{PlateName ?? "(plate?)"} — {RunUid ?? "run"} ({CellCount} cells)";
    }

    /// <summary>Lightweight reference to a DIA-NN raw file (a candidate target for cell↔run reconciliation).</summary>
    public class RawFileRef
    {
        public int RawFileId { get; set; }
        public string? RawFileName { get; set; }

        public override string ToString() => RawFileName ?? $"raw_file {RawFileId}";
    }
}
