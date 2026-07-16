using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Xml.Linq;

namespace SCPBrowser.Services
{
    /// <summary>
    /// Raw, self-contained result of reading a gene × sample .xlsx matrix (e.g. a DIA-NN gene/pg matrix exported to
    /// Excel). Column 0 holds gene/row labels; columns 1..N are samples. Only detected (numeric, &gt; 0) cells are kept
    /// — blank cells mean "not detected", matching the parquet loader's detect-only semantics.
    /// </summary>
    public sealed class GeneMatrix
    {
        /// <summary>Header text of column 0 (e.g. "Genes").</summary>
        public string RowLabelHeader { get; set; }

        /// <summary>Sample column headers (columns 1..N), typically full raw-file paths.</summary>
        public List<string> SampleHeaders { get; } = new();

        /// <summary>Row label (gene symbol) per data row, aligned with <see cref="Rows"/>.</summary>
        public List<string> Genes { get; } = new();

        /// <summary>Per data row: 0-based sample-column index → intensity. Sparse: only detected (&gt; 0) values.</summary>
        public List<Dictionary<int, double>> Rows { get; } = new();
    }

    /// <summary>
    /// Minimal, dependency-free reader for the .xlsx (Office Open XML) gene-matrix format. An .xlsx is a zip of XML;
    /// this parses <c>xl/sharedStrings.xml</c> + the first worksheet with the BCL only (System.IO.Compression +
    /// System.Xml.Linq) — no ClosedXML/EPPlus — matching the codebase's hand-rolled tabular parsers. Suitable for the
    /// single-cell proteomics matrix scale (thousands of genes × hundreds of samples).
    /// </summary>
    public static class XlsxGeneMatrixReader
    {
        private static readonly XNamespace Ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";

        public static GeneMatrix Read(string path)
        {
            if (!File.Exists(path))
                throw new FileNotFoundException("Excel gene matrix not found", path);

            using var zip = ZipFile.OpenRead(path);

            var shared = ReadSharedStrings(zip);

            var wsEntry = zip.GetEntry("xl/worksheets/sheet1.xml")
                ?? zip.Entries.FirstOrDefault(e =>
                        e.FullName.StartsWith("xl/worksheets/sheet", StringComparison.OrdinalIgnoreCase) &&
                        e.FullName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase));
            if (wsEntry == null)
                throw new InvalidDataException("Workbook has no worksheet.");

            var matrix = new GeneMatrix();
            using var ws = wsEntry.Open();
            var sheetData = XDocument.Load(ws).Root?.Element(Ns + "sheetData");
            if (sheetData == null)
                throw new InvalidDataException("Worksheet has no sheetData.");

            bool headerParsed = false;
            foreach (var rowEl in sheetData.Elements(Ns + "row"))
            {
                // Gather this row's cells by column index (sparse-safe).
                var cells = new Dictionary<int, (string text, bool isNumeric, double num)>();
                int maxCol = -1;
                int prevCol = -1;
                foreach (var c in rowEl.Elements(Ns + "c"))
                {
                    // The OOXML `r` (cell reference) is optional; when omitted, cells are positional. Fall back to
                    // previous-column + 1 so a writer that omits `r` doesn't collapse every cell onto column 0.
                    string cref = (string)c.Attribute("r");
                    int col = !string.IsNullOrEmpty(cref) ? ColumnIndex(cref) : prevCol + 1;
                    prevCol = col;
                    string t = (string)c.Attribute("t");
                    var vEl = c.Element(Ns + "v");

                    string text = null;
                    bool isNum = false;
                    double num = 0;

                    if (t == "s") // shared string
                    {
                        if (vEl != null && int.TryParse(vEl.Value, out int idx) && idx >= 0 && idx < shared.Count)
                            text = shared[idx];
                    }
                    else if (t == "inlineStr")
                    {
                        var isEl = c.Element(Ns + "is");
                        text = isEl == null ? null : string.Concat(isEl.Descendants(Ns + "t").Select(x => x.Value));
                    }
                    else if (t == "str") // formula string result
                    {
                        text = vEl?.Value;
                    }
                    else // numeric (t == null or "n")
                    {
                        if (vEl != null &&
                            double.TryParse(vEl.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double d))
                        {
                            isNum = true;
                            num = d;
                            text = vEl.Value;
                        }
                    }

                    // Text-encoded numbers (e.g. a .tsv/.csv opened in Excel and saved as text) still count as
                    // intensities, so an all-text matrix isn't silently read as "nothing detected".
                    if (!isNum && !string.IsNullOrEmpty(text) &&
                        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double td))
                    {
                        isNum = true;
                        num = td;
                    }

                    cells[col] = (text, isNum, num);
                    if (col > maxCol) maxCol = col;
                }

                if (!headerParsed)
                {
                    // Header row: column 0 = row label header, columns 1..maxCol = sample headers.
                    matrix.RowLabelHeader = cells.TryGetValue(0, out var h0) ? h0.text : null;
                    for (int col = 1; col <= maxCol; col++)
                        matrix.SampleHeaders.Add(cells.TryGetValue(col, out var hc) ? (hc.text ?? "") : "");
                    headerParsed = true;
                }
                else
                {
                    // Data row: column 0 = gene label, columns 1..N = intensities.
                    string gene = cells.TryGetValue(0, out var g0) ? g0.text : null;
                    if (string.IsNullOrEmpty(gene))
                        continue; // skip rows without a gene label (e.g. trailing blanks)

                    var values = new Dictionary<int, double>();
                    for (int col = 1; col <= matrix.SampleHeaders.Count; col++)
                    {
                        if (cells.TryGetValue(col, out var cell) && cell.isNumeric && cell.num > 0)
                            values[col - 1] = cell.num; // 0-based sample index; blank / non-positive = not detected
                    }

                    matrix.Genes.Add(gene);
                    matrix.Rows.Add(values);
                }
            }

            return matrix;
        }

        private static List<string> ReadSharedStrings(ZipArchive zip)
        {
            var shared = new List<string>();
            var entry = zip.GetEntry("xl/sharedStrings.xml");
            if (entry == null) return shared;

            using var s = entry.Open();
            var root = XDocument.Load(s).Root;
            if (root == null) return shared;

            foreach (var si in root.Elements(Ns + "si"))
                shared.Add(string.Concat(si.Descendants(Ns + "t").Select(t => t.Value))); // concat rich-text runs
            return shared;
        }

        /// <summary>Converts an A1-style cell reference to a 0-based column index (e.g. "AB12" → 27).</summary>
        private static int ColumnIndex(string cellRef)
        {
            if (string.IsNullOrEmpty(cellRef)) return 0;
            int idx = 0;
            foreach (char ch in cellRef)
            {
                if (ch >= 'A' && ch <= 'Z') idx = idx * 26 + (ch - 'A' + 1);
                else if (ch >= 'a' && ch <= 'z') idx = idx * 26 + (ch - 'a' + 1);
                else break; // reached the row digits
            }
            return idx - 1;
        }
    }
}
