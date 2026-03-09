using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Parquet;
using Parquet.Data;

namespace SCPBrowser.Services
{
    /// <summary>
    /// Represents a peptide mapped to a position in a protein sequence.
    /// </summary>
    public class MappedPeptide
    {
        public string Sequence { get; set; }
        public int StartPosition { get; set; }
        public int EndPosition { get; set; }
        public double SummedIntensity { get; set; }
        public int CellCount { get; set; }
    }

    /// <summary>
    /// Result of a protein coverage analysis.
    /// </summary>
    public class ProteinCoverageResult
    {
        public string Accession { get; set; }
        public string ProteinSequence { get; set; }
        public int SequenceLength { get; set; }
        public List<MappedPeptide> MappedPeptides { get; set; } = new();
        public int[] CoveragePerResidue { get; set; }
        public double[] IntensityPerResidue { get; set; }
        public double CoveragePercent { get; set; }
        public int UniquePeptideCount { get; set; }
        public int TotalPeptideCount { get; set; }
    }

    /// <summary>
    /// Computes protein sequence coverage on the fly from FASTA and parquet files.
    /// No data is stored; everything is read at query time.
    /// </summary>
    public class ProteinCoverageService
    {
        /// <summary>
        /// Reads the amino acid sequence for a given accession from a FASTA file.
        /// Streams through the file without loading it entirely into memory.
        /// </summary>
        public async Task<string> GetProteinSequenceFromFastaAsync(string fastaPath, string accession)
        {
            if (!File.Exists(fastaPath))
                return null;

            bool found = false;
            var sequenceBuilder = new System.Text.StringBuilder();

            using (var reader = new StreamReader(fastaPath))
            {
                string line;
                while ((line = await reader.ReadLineAsync()) != null)
                {
                    if (line.StartsWith(">"))
                    {
                        // If we were already collecting sequence, we're done
                        if (found)
                            break;

                        // Check if this header contains our accession
                        if (HeaderContainsAccession(line, accession))
                            found = true;
                    }
                    else if (found)
                    {
                        sequenceBuilder.Append(line.Trim());
                    }
                }
            }

            return found ? sequenceBuilder.ToString() : null;
        }

        /// <summary>
        /// Checks whether a FASTA header line contains the given accession.
        /// Handles UniProt format (sp|ACCESSION|...) and generic format (ACCESSION ...).
        /// </summary>
        private bool HeaderContainsAccession(string headerLine, string accession)
        {
            string header = headerLine.TrimStart('>').Trim();

            // UniProt: sp|P12345|ENTRY_NAME or tr|P12345|ENTRY_NAME
            var parts = header.Split('|');
            if (parts.Length >= 2)
            {
                if (string.Equals(parts[1].Trim(), accession, StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            // Generic: first token is the accession
            var firstToken = header.Split(new[] { ' ', '\t' }, 2)[0];
            if (string.Equals(firstToken, accession, StringComparison.OrdinalIgnoreCase))
                return true;

            return false;
        }

        /// <summary>
        /// Reads peptides and their intensities from parquet files for a given protein group,
        /// filtered to specific runs. Returns a dictionary: peptide sequence -> (summed intensity, cell count).
        /// </summary>
        public async Task<Dictionary<string, (double Intensity, int CellCount)>> GetPeptidesFromParquetAsync(
            List<string> parquetPaths,
            string proteinGroup,
            HashSet<string> selectedRuns)
        {
            var peptideData = new Dictionary<string, (double Intensity, int CellCount)>();

            foreach (var parquetPath in parquetPaths)
            {
                if (!File.Exists(parquetPath))
                    continue;

                await ReadPeptidesFromSingleParquet(parquetPath, proteinGroup, selectedRuns, peptideData);
            }

            return peptideData;
        }

        private async Task ReadPeptidesFromSingleParquet(
            string parquetPath,
            string proteinGroup,
            HashSet<string> selectedRuns,
            Dictionary<string, (double Intensity, int CellCount)> peptideData)
        {
            using (Stream fileStream = File.OpenRead(parquetPath))
            {
                using (var parquetReader = await ParquetReader.CreateAsync(fileStream))
                {
                    var dataFields = parquetReader.Schema.GetDataFields();

                    var runField = dataFields.FirstOrDefault(f => f.Name == "Run");
                    var proteinGroupField = dataFields.FirstOrDefault(f => f.Name == "Protein.Group");
                    var strippedSeqField = dataFields.FirstOrDefault(f => f.Name == "Stripped.Sequence");
                    var quantField = dataFields.FirstOrDefault(f => f.Name == "Precursor.Quantity");

                    if (runField == null || proteinGroupField == null || strippedSeqField == null)
                        return;

                    for (int i = 0; i < parquetReader.RowGroupCount; i++)
                    {
                        using (var groupReader = parquetReader.OpenRowGroupReader(i))
                        {
                            var runColumn = await groupReader.ReadColumnAsync(runField);
                            var proteinColumn = await groupReader.ReadColumnAsync(proteinGroupField);
                            var peptideColumn = await groupReader.ReadColumnAsync(strippedSeqField);

                            Array quantData = null;
                            if (quantField != null)
                            {
                                var quantColumn = await groupReader.ReadColumnAsync(quantField);
                                quantData = quantColumn.Data as Array;
                            }

                            var runData = runColumn.Data as Array;
                            var proteinData = proteinColumn.Data as Array;
                            var seqData = peptideColumn.Data as Array;

                            for (int row = 0; row < runData.Length; row++)
                            {
                                var run = runData.GetValue(row)?.ToString();
                                var protein = proteinData.GetValue(row)?.ToString();
                                var peptide = seqData.GetValue(row)?.ToString();

                                if (string.IsNullOrEmpty(run) || string.IsNullOrEmpty(protein) || string.IsNullOrEmpty(peptide))
                                    continue;

                                // Filter by selected runs (null means all runs)
                                if (selectedRuns != null && !selectedRuns.Contains(run))
                                    continue;

                                // Filter by protein group
                                if (!string.Equals(protein, proteinGroup, StringComparison.Ordinal))
                                    continue;

                                double intensity = 0;
                                if (quantData != null)
                                {
                                    var quantValue = quantData.GetValue(row);
                                    if (quantValue != null)
                                        double.TryParse(quantValue.ToString(), out intensity);
                                }

                                if (peptideData.TryGetValue(peptide, out var existing))
                                {
                                    peptideData[peptide] = (existing.Intensity + intensity, existing.CellCount + 1);
                                }
                                else
                                {
                                    peptideData[peptide] = (intensity, 1);
                                }
                            }
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Maps peptide sequences onto a protein sequence, finding all occurrence positions.
        /// </summary>
        public List<MappedPeptide> MapPeptidesToProtein(
            string proteinSequence,
            Dictionary<string, (double Intensity, int CellCount)> peptideData)
        {
            var mapped = new List<MappedPeptide>();

            foreach (var kvp in peptideData)
            {
                string peptide = kvp.Key;
                double intensity = kvp.Value.Intensity;
                int cellCount = kvp.Value.CellCount;

                // Find all occurrences of this peptide in the protein sequence
                int searchStart = 0;
                while (searchStart < proteinSequence.Length)
                {
                    int pos = proteinSequence.IndexOf(peptide, searchStart, StringComparison.OrdinalIgnoreCase);
                    if (pos < 0)
                        break;

                    mapped.Add(new MappedPeptide
                    {
                        Sequence = peptide,
                        StartPosition = pos,
                        EndPosition = pos + peptide.Length - 1,
                        SummedIntensity = intensity,
                        CellCount = cellCount
                    });

                    searchStart = pos + 1;
                }
            }

            return mapped.OrderBy(p => p.StartPosition).ThenBy(p => p.EndPosition).ToList();
        }

        /// <summary>
        /// Computes the full coverage result for a protein from parquet + FASTA data.
        /// </summary>
        public async Task<ProteinCoverageResult> ComputeCoverageAsync(
            string fastaPath,
            List<string> parquetPaths,
            string proteinGroup,
            string accession,
            HashSet<string> selectedRuns)
        {
            // Get protein sequence from FASTA
            string proteinSequence = await GetProteinSequenceFromFastaAsync(fastaPath, accession);
            if (string.IsNullOrEmpty(proteinSequence))
                return null;

            // Get peptides from parquet
            var peptideData = await GetPeptidesFromParquetAsync(parquetPaths, proteinGroup, selectedRuns);
            if (peptideData.Count == 0)
            {
                return new ProteinCoverageResult
                {
                    Accession = accession,
                    ProteinSequence = proteinSequence,
                    SequenceLength = proteinSequence.Length,
                    CoveragePerResidue = new int[proteinSequence.Length],
                    IntensityPerResidue = new double[proteinSequence.Length],
                    CoveragePercent = 0,
                    UniquePeptideCount = 0,
                    TotalPeptideCount = 0
                };
            }

            // Map peptides to protein positions
            var mappedPeptides = MapPeptidesToProtein(proteinSequence, peptideData);

            // Build per-residue arrays
            int length = proteinSequence.Length;
            var coverageArray = new int[length];
            var intensityArray = new double[length];

            foreach (var mp in mappedPeptides)
            {
                for (int i = mp.StartPosition; i <= mp.EndPosition; i++)
                {
                    coverageArray[i]++;
                    intensityArray[i] += mp.SummedIntensity;
                }
            }

            int coveredResidues = coverageArray.Count(c => c > 0);

            return new ProteinCoverageResult
            {
                Accession = accession,
                ProteinSequence = proteinSequence,
                SequenceLength = length,
                MappedPeptides = mappedPeptides,
                CoveragePerResidue = coverageArray,
                IntensityPerResidue = intensityArray,
                CoveragePercent = length > 0 ? (double)coveredResidues / length * 100.0 : 0,
                UniquePeptideCount = peptideData.Count,
                TotalPeptideCount = peptideData.Values.Sum(v => v.CellCount)
            };
        }

        /// <summary>
        /// Extracts the first accession from a protein group string (semicolon-separated).
        /// Prioritizes SwissProt entries if annotations are available.
        /// </summary>
        public string GetBestAccession(string proteinGroup, Dictionary<string, FastaParserService.ProteinAnnotation> annotations = null)
        {
            if (string.IsNullOrEmpty(proteinGroup))
                return null;

            var accessions = proteinGroup.Split(';').Select(a => a.Trim()).Where(a => !string.IsNullOrEmpty(a)).ToArray();

            if (annotations != null)
            {
                // Prefer SwissProt
                foreach (var acc in accessions)
                {
                    if (annotations.TryGetValue(acc, out var ann) &&
                        string.Equals(ann.Source, "sp", StringComparison.OrdinalIgnoreCase))
                        return acc;
                }
            }

            return accessions.FirstOrDefault();
        }
    }
}
