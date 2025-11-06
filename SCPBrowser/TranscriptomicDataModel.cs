using System.Collections.Generic;

namespace SCPBrowser
{
    public class CellMetadata
    {
        public string CellID { get; set; }
        public string Age { get; set; }
        public string Sex { get; set; }
        public string Batch { get; set; }
        public string CellType { get; set; }
        public int? GenesDetected { get; set; }
        public long? TotalReads { get; set; }
        public long? MappedReads { get; set; }
        public double? MappingRate { get; set; }
    }

    public class GeneExpressionRecord
    {
        public string GeneName { get; set; }
        public string CellID { get; set; }
        public int Count { get; set; }
    }

    public class TranscriptomicDatabase
    {
        public Dictionary<string, List<(string cellId, int count)>> GeneExpression { get; set; }
        public Dictionary<string, CellMetadata> CellMetadata { get; set; }
        public Dictionary<string, List<string>> CellTypeIndex { get; set; }

        public TranscriptomicDatabase()
        {
            GeneExpression = new Dictionary<string, List<(string cellId, int count)>>();
            CellMetadata = new Dictionary<string, CellMetadata>();
            CellTypeIndex = new Dictionary<string, List<string>>();
        }

        public int TotalGenes => GeneExpression.Count;
        public int TotalCells => CellMetadata.Count;
        public int TotalCellTypes => CellTypeIndex.Count;
    }
}