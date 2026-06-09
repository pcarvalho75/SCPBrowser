using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using SCPBrowser.Models;

namespace SCPBrowser.Services
{
    /// <summary>
    /// Parses a cellenONE ".Run" output folder into in-memory <see cref="CellenOneRun"/> / <see cref="IsolatedCell"/>
    /// models. Intentionally WPF-free and side-effect-free (it only reads text + resolves image file paths; it does
    /// NOT decode images) so it can be exercised from a plain console / test harness against real data.
    ///
    /// Sources it reads inside the run folder:
    ///   *_Logfile.log    - instrument header + end-of-run summary (totals, channels, humidity, start time)
    ///   *.par            - INI-style run config: provenance (RunDirSettings), field geometry, gating ([Cells])
    ///   *_isolated.xls   - TAB-separated (despite the extension); the per-cell -> well/position/morphology table
    ///
    /// isolated.xls layout (reverse-engineered): a header row, then for each cell two (metadata,data) line pairs -
    /// one for the "Transmission" channel and one for "Blue". On a METADATA line the channel name sits in column 1
    /// (the X slot) and columns 7..16 carry Plate/Well/Target/Field/XPos/YPos/Date/Time/ImageFile/Background; the
    /// following DATA line carries DropNo,X,Y,Diameter,Elongation,Circularity,Intensity in columns 0..6.
    /// </summary>
    public static class CellenOneParser
    {
        // cellenONE log/par/xls files are ISO-8859 (Latin1). Read with Latin1 so high bytes never throw and ASCII
        // labels/numbers are preserved exactly.
        private static readonly Encoding FileEncoding = Encoding.Latin1;

        /// <summary>True if the folder is a single-cell isolation run (has an isolated.xls), vs a reagent dispense run.</summary>
        public static bool IsIsolationRun(string runDir)
        {
            return FindIsolatedXls(runDir) != null;
        }

        /// <summary>
        /// Parses a run folder. Always returns a run (RunType "isolation" or "dispense"); for dispense runs
        /// <see cref="CellenOneRun.Cells"/> is empty.
        /// </summary>
        public static CellenOneRun ParseRun(string runDir)
        {
            if (string.IsNullOrWhiteSpace(runDir) || !Directory.Exists(runDir))
                throw new DirectoryNotFoundException($"cellenONE run folder not found: {runDir}");

            var run = new CellenOneRun
            {
                SourceDir = runDir,
                RunType = IsIsolationRun(runDir) ? "isolation" : "dispense"
            };

            var logPath = FindFirst(runDir, "*_Logfile.log");
            if (logPath != null) ParseLogfile(logPath, run);

            var parPath = FindFirst(runDir, "*.par");
            if (parPath != null) ParsePar(parPath, run);

            var xlsPath = FindIsolatedXls(runDir);
            if (xlsPath != null) ParseIsolatedXls(xlsPath, runDir, run);

            var geoPath = FindGeoprops(runDir);
            if (geoPath != null) ParseGeoprops(geoPath, run);
            ComputeObjectCounts(run);

            // Fallback background image if the xls did not name one.
            if (run.BackgroundImagePath == null)
            {
                var bg = Directory.GetFiles(runDir, "*_Background.png")
                    .FirstOrDefault(f => !f.Contains("BackgroundFull", StringComparison.OrdinalIgnoreCase)
                                      && !f.Contains("FluBackground", StringComparison.OrdinalIgnoreCase));
                run.BackgroundImagePath = bg;
            }

            return run;
        }

        // ----------------------------------------------------------------- logfile

        private static void ParseLogfile(string path, CellenOneRun run)
        {
            var text = File.ReadAllText(path, FileEncoding);
            run.LogfileText = text;
            var lines = SplitLines(text);

            // Header: line 0 = "cellenONE - 384WP\t<serial>", line 1 = "<sw version>\t<date>"
            if (lines.Length > 0) run.Instrument = FirstField(lines[0]);
            if (lines.Length > 1) run.SoftwareVersion = FirstField(lines[1]);

            foreach (var raw in lines)
            {
                var line = raw.Trim();
                if (line.Length == 0) continue;

                if (StartsWith(line, "Run ID:"))
                    run.RunUid = After(line, "Run ID:");
                else if (StartsWith(line, "Humidity:"))
                {
                    var m = Regex.Match(line, @"(\d+(?:\.\d+)?)\s*%");
                    if (m.Success && double.TryParse(m.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out var h))
                        run.Humidity = h;
                }
                else if (StartsWith(line, "Start Time:"))
                {
                    run.RunDate = ParseDateTime(After(line, "Start Time:")) ?? After(line, "Start Time:");
                }
                else if (StartsWith(line, "Target:") && run.TargetLabware == null)
                {
                    // e.g. "Target: MTP384_WholePlate_B2: 1" -> "MTP384_WholePlate_B2"
                    var v = After(line, "Target:");
                    run.TargetLabware = v.Split(':')[0].Trim();
                }
                else if (line.Contains("Channels", StringComparison.OrdinalIgnoreCase) && line.Contains(':'))
                    run.Channels = line.Substring(line.IndexOf(':') + 1).Trim();
                else if (line.Contains("Total detected particles", StringComparison.OrdinalIgnoreCase))
                    run.TotalDetected = (int?)ExtractLabeledNumber(line);
                else if (line.Contains("Total volume dispensed", StringComparison.OrdinalIgnoreCase))
                    run.VolumeDispensedNl = ExtractLabeledNumber(line);
                else if (line.Contains("Concentration estimated", StringComparison.OrdinalIgnoreCase))
                    run.Concentration = ExtractLabeledNumber(line);
            }
        }

        // ----------------------------------------------------------------- .par

        private static void ParsePar(string path, CellenOneRun run)
        {
            // The .par is UTF-8 (e.g. the ID-template placeholder is the two-byte UTF-8 sequence for '¤'),
            // unlike the Latin1 log/xls. Decode as UTF-8 so that placeholder round-trips; invalid binary
            // values (the BCG* camera blobs) fall back to U+FFFD, which we don't read.
            var text = File.ReadAllText(path, Encoding.UTF8);
            var sections = ParseIni(text);

            if (sections.TryGetValue("RunDirectory", out var runDir) &&
                runDir.TryGetValue("RunDirSettings", out var rds))
            {
                var dir = ExtractJsonString(rds, "Directory");
                run.IdTemplate = ExtractJsonString(rds, "ID");
                if (!string.IsNullOrEmpty(dir))
                    run.OperatorName = OperatorFromDirectory(dir);
            }

            if (sections.TryGetValue("Field Parameter", out var field))
            {
                if (field.TryGetValue("Field SizeXY", out var size))
                {
                    run.FieldSizeX = ExtractJsonInt(size, "x");
                    run.FieldSizeY = ExtractJsonInt(size, "y");
                }
                if (field.TryGetValue("Dot Pitch XY", out var pitch))
                    run.DotPitch = ExtractJsonInt(pitch, "x");
            }

            if (sections.TryGetValue("Cells", out var cells))
            {
                // Capture the gating-relevant values verbatim as a small JSON object for audit / later display.
                var gating = new Dictionary<string, string>();
                foreach (var key in new[] { "ROI", "MaxTrials", "TParameters", "FluParameters", "AdditionalParams" })
                    if (cells.TryGetValue(key, out var val) && !string.IsNullOrEmpty(val))
                        gating[key] = val;
                if (gating.Count > 0)
                    run.GatingParamsJson = JsonSerializer.Serialize(gating);
            }

            // Fallback target labware from the last-used UI settings if the logfile did not provide one.
            if (run.TargetLabware == null &&
                sections.TryGetValue("LastMainWndSettings", out var last) &&
                last.TryGetValue("Settings", out var settingsVal))
            {
                var parts = Regex.Matches(settingsVal, "\"([^\"]+)\"").Select(m => m.Groups[1].Value).ToList();
                run.TargetLabware = parts.LastOrDefault(p => p.Contains("MTP", StringComparison.OrdinalIgnoreCase));
            }
        }

        // ----------------------------------------------------------------- isolated.xls

        private static void ParseIsolatedXls(string path, string runDir, CellenOneRun run)
        {
            var lines = SplitLines(File.ReadAllText(path, FileEncoding));
            var byDrop = new Dictionary<int, IsolatedCell>();

            IsolatedCell? pending = null;
            string? pendingChannel = null;

            // lines[0] is the header
            for (int i = 1; i < lines.Length; i++)
            {
                var cols = lines[i].Split('\t');
                if (cols.Length < 2) continue;

                var channel = cols[1].Trim();
                bool isMeta = channel.Equals("Transmission", StringComparison.OrdinalIgnoreCase)
                           || channel.Equals("Blue", StringComparison.OrdinalIgnoreCase);

                if (isMeta)
                {
                    if (!TryParseDropNo(cols[0], out var dropNo)) { pending = null; continue; }

                    if (!byDrop.TryGetValue(dropNo, out var cell))
                    {
                        cell = new IsolatedCell { DropNo = dropNo };
                        byDrop[dropNo] = cell;
                    }

                    // Position / well metadata (identical across both channel rows; harmless to set twice).
                    cell.TargetWell = NullIfEmpty(Get(cols, 8));
                    cell.Target = ParseIntOrNull(Get(cols, 9));
                    cell.Field = ParseIntOrNull(Get(cols, 10));
                    cell.XPos = ParseIntOrNull(Get(cols, 11));
                    cell.YPos = ParseIntOrNull(Get(cols, 12));
                    cell.IsolatedAt = CombineDateTime(Get(cols, 13), Get(cols, 14));

                    var imageFile = ExtractHyperlink(Get(cols, 15));
                    if (!string.IsNullOrEmpty(imageFile))
                    {
                        var resolved = Path.Combine(runDir, imageFile);
                        if (channel.Equals("Blue", StringComparison.OrdinalIgnoreCase))
                            cell.BlueImagePath = resolved;
                        else
                            cell.TransImagePath = resolved;

                        if (cell.Status == null)
                            cell.Status = StatusFromImageName(imageFile);
                    }

                    if (run.BackgroundImagePath == null)
                    {
                        var bg = ExtractHyperlink(Get(cols, 16));
                        if (!string.IsNullOrEmpty(bg)) run.BackgroundImagePath = Path.Combine(runDir, bg);
                    }

                    pending = cell;
                    pendingChannel = channel;
                }
                else if (pending != null && IsNumericToken(cols[0]))
                {
                    // Data row: DropNo, X, Y, Diameter, Elongation, Circularity, Intensity
                    if (pendingChannel != null && pendingChannel.Equals("Blue", StringComparison.OrdinalIgnoreCase))
                    {
                        pending.FluDiameter = ParseDoubleOrNull(Get(cols, 3));
                        pending.FluIntensity = ParseDoubleOrNull(Get(cols, 6));
                    }
                    else
                    {
                        pending.ImageX = ParseDoubleOrNull(Get(cols, 1));
                        pending.ImageY = ParseDoubleOrNull(Get(cols, 2));
                        pending.Diameter = ParseDoubleOrNull(Get(cols, 3));
                        pending.Elongation = ParseDoubleOrNull(Get(cols, 4));
                        pending.Circularity = ParseDoubleOrNull(Get(cols, 5));
                        pending.Intensity = ParseDoubleOrNull(Get(cols, 6));
                    }
                    pending = null;
                    pendingChannel = null;
                }
            }

            run.Cells = byDrop.Values.OrderBy(c => c.DropNo).ToList();
        }

        // ----------------------------------------------------------------- geoprops (superset)

        /// <summary>
        /// Parses the geoprops superset: every detected object in every drop (multiple per drop = doublet/multiplet).
        /// Only Transmission-channel objects are kept (Blue is all-zero for unstained runs).
        /// </summary>
        private static void ParseGeoprops(string path, CellenOneRun run)
        {
            var lines = SplitLines(File.ReadAllText(path, FileEncoding));
            int currentDrop = -1;
            bool inTransmission = false;
            int objIndex = 0;

            for (int i = 1; i < lines.Length; i++)
            {
                var cols = lines[i].Split('\t');
                if (cols.Length < 2) continue;

                var channel = cols[1].Trim();
                bool isMeta = channel.Equals("Transmission", StringComparison.OrdinalIgnoreCase)
                           || channel.Equals("Blue", StringComparison.OrdinalIgnoreCase);
                if (isMeta)
                {
                    if (TryParseDropNo(cols[0], out var d))
                    {
                        currentDrop = d;
                        inTransmission = channel.Equals("Transmission", StringComparison.OrdinalIgnoreCase);
                        objIndex = 0;
                    }
                    else { currentDrop = -1; inTransmission = false; }
                }
                else if (inTransmission && currentDrop >= 0 && IsNumericToken(cols[0]))
                {
                    run.Detections.Add(new CellDetection
                    {
                        DropNo = currentDrop,
                        Channel = "Transmission",
                        ObjectIndex = objIndex++,
                        X = ParseDoubleOrNull(Get(cols, 1)),
                        Y = ParseDoubleOrNull(Get(cols, 2)),
                        Diameter = ParseDoubleOrNull(Get(cols, 3)),
                        Elongation = ParseDoubleOrNull(Get(cols, 4)),
                        Circularity = ParseDoubleOrNull(Get(cols, 5)),
                        Intensity = ParseDoubleOrNull(Get(cols, 6))
                    });
                }
            }
        }

        /// <summary>Sets each isolated cell's NObjects = number of objects detected in its drop (from geoprops).</summary>
        private static void ComputeObjectCounts(CellenOneRun run)
        {
            if (run.Detections.Count == 0) return;
            var perDrop = run.Detections.GroupBy(d => d.DropNo).ToDictionary(g => g.Key, g => g.Count());
            foreach (var cell in run.Cells)
                if (perDrop.TryGetValue(cell.DropNo, out var n)) cell.NObjects = n;
        }

        private static string? FindGeoprops(string dir)
        {
            var matches = Directory.GetFiles(dir, "*_geoprops.xls", SearchOption.TopDirectoryOnly);
            return matches.FirstOrDefault(f => !Path.GetFileName(f).StartsWith("Reordered", StringComparison.OrdinalIgnoreCase))
                   ?? matches.FirstOrDefault();
        }

        // ----------------------------------------------------------------- helpers

        private static string? FindFirst(string dir, string pattern)
            => Directory.GetFiles(dir, pattern, SearchOption.TopDirectoryOnly).FirstOrDefault();

        private static string? FindIsolatedXls(string dir)
        {
            var matches = Directory.GetFiles(dir, "*_isolated.xls", SearchOption.TopDirectoryOnly);
            // Prefer the canonical file over the "Reordered_*" variant (same data, sorted by well).
            return matches.FirstOrDefault(f => !Path.GetFileName(f).StartsWith("Reordered", StringComparison.OrdinalIgnoreCase))
                   ?? matches.FirstOrDefault();
        }

        private static string[] SplitLines(string text)
            => text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

        private static string FirstField(string line)
        {
            var tab = line.IndexOf('\t');
            return (tab >= 0 ? line.Substring(0, tab) : line).Trim();
        }

        private static bool StartsWith(string s, string prefix) => s.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
        private static string After(string s, string marker) => s.Substring(s.IndexOf(marker, StringComparison.OrdinalIgnoreCase) + marker.Length).Trim();

        private static string Get(string[] cols, int i) => i >= 0 && i < cols.Length ? cols[i].Trim() : "";
        private static string? NullIfEmpty(string s) => string.IsNullOrEmpty(s) ? null : s;

        private static bool IsNumericToken(string s) => double.TryParse(s.Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out _);

        private static int? ParseIntOrNull(string s)
            => int.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var v) ? v : (int?)null;

        private static double? ParseDoubleOrNull(string s)
            => double.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var v) ? v : (double?)null;

        private static bool TryParseDropNo(string s, out int dropNo)
            => int.TryParse(s.Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out dropNo);

        /// <summary>Pulls the filename out of an Excel =HYPERLINK("name") cell; returns the raw value if not a hyperlink.</summary>
        private static string ExtractHyperlink(string cell)
        {
            if (string.IsNullOrEmpty(cell)) return "";
            var m = Regex.Match(cell, "\"([^\"]+)\"");
            return m.Success ? m.Groups[1].Value : cell.Trim();
        }

        private static string? StatusFromImageName(string fileName)
        {
            // e.g. "15_Printed_T1_F1_R2_C1_(A-1)_Trans_2V58C18_Run.png" -> "Printed"
            var parts = Path.GetFileName(fileName).Split('_');
            return parts.Length > 1 ? parts[1] : null;
        }

        /// <summary>Extracts a trailing numeric value from a tab/space separated summary line such as "Total detected particles:\t1060".</summary>
        private static double? ExtractLabeledNumber(string line)
        {
            var matches = Regex.Matches(line, @"-?\d+(?:\.\d+)?");
            if (matches.Count == 0) return null;
            return double.TryParse(matches[matches.Count - 1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out var v) ? v : (double?)null;
        }

        private static string? CombineDateTime(string date, string time)
        {
            if (string.IsNullOrEmpty(date) && string.IsNullOrEmpty(time)) return null;
            var combined = $"{date} {time}".Trim();
            return ParseDateTime(combined) ?? combined;
        }

        /// <summary>Parses cellenONE date strings (e.g. "2:57 PM - 2/3/2026" or "2/3/2026 10:48:36 AM") to ISO-8601, or null.</summary>
        private static string? ParseDateTime(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return null;
            var cleaned = s.Replace(" - ", " ").Trim();
            return DateTime.TryParse(cleaned, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt)
                ? dt.ToString("o", CultureInfo.InvariantCulture)
                : null;
        }

        private static string? OperatorFromDirectory(string dir)
        {
            var segments = dir.Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < segments.Length - 1; i++)
                if (segments[i].Equals("Run", StringComparison.OrdinalIgnoreCase))
                    return segments[i + 1];
            return null;
        }

        // Reads `"key":"value"` out of a (possibly not-strictly-valid) JSON-ish blob.
        private static string? ExtractJsonString(string source, string key)
        {
            var m = Regex.Match(source, "\"" + Regex.Escape(key) + "\"\\s*:\\s*\"([^\"]*)\"");
            return m.Success ? m.Groups[1].Value : null;
        }

        // Reads `"key":<int>` out of a JSON-ish blob.
        private static int? ExtractJsonInt(string source, string key)
        {
            var m = Regex.Match(source, "\"" + Regex.Escape(key) + "\"\\s*:\\s*(-?\\d+)");
            return m.Success && int.TryParse(m.Groups[1].Value, out var v) ? v : (int?)null;
        }

        /// <summary>
        /// Minimal INI parser: returns section -> (key -> value). Values have one pair of surrounding double quotes
        /// stripped. Splits each entry on the first '=' only (cellenONE keys contain spaces and values contain '=').
        /// </summary>
        private static Dictionary<string, Dictionary<string, string>> ParseIni(string text)
        {
            var result = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
            var current = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            result[""] = current;

            foreach (var raw in SplitLines(text))
            {
                var line = raw.Trim();
                if (line.Length == 0) continue;

                if (line.StartsWith("[") && line.EndsWith("]"))
                {
                    var name = line.Substring(1, line.Length - 2).Trim();
                    if (!result.TryGetValue(name, out current!))
                    {
                        current = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                        result[name] = current;
                    }
                    continue;
                }

                var eq = line.IndexOf('=');
                if (eq <= 0) continue;
                var key = line.Substring(0, eq).Trim();
                var value = line.Substring(eq + 1).Trim();
                if (value.Length >= 2 && value[0] == '"' && value[value.Length - 1] == '"')
                    value = value.Substring(1, value.Length - 2);
                current[key] = value;
            }

            return result;
        }
    }
}
