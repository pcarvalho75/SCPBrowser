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
        public int GeneId { get; set; }
        public int CellId { get; set; }
        public int Count { get; set; }
    }

    /// <summary>
    /// Lookup table for genes - converts gene names to integer IDs
    /// </summary>
    public class GeneLookup
    {
        private Dictionary<string, int> _nameToId = new Dictionary<string, int>();
        private Dictionary<int, string> _idToName = new Dictionary<int, string>();
        private int _nextId = 1;

        public int GetOrAddGeneId(string geneName)
        {
            if (_nameToId.TryGetValue(geneName, out int id))
                return id;

            id = _nextId++;
            _nameToId[geneName] = id;
            _idToName[id] = geneName;
            return id;
        }

        public int GetGeneId(string geneName)
        {
            return _nameToId.TryGetValue(geneName, out int id) ? id : 0;
        }

        public string GetGeneName(int geneId)
        {
            return _idToName.TryGetValue(geneId, out string name) ? name : null;
        }

        public Dictionary<int, string> GetAllGenes() => _idToName;
        public int Count => _nameToId.Count;
    }

    /// <summary>
    /// Lookup table for cells - converts cell IDs to integer IDs
    /// </summary>
    public class CellLookup
    {
        private Dictionary<string, int> _nameToId = new Dictionary<string, int>();
        private Dictionary<int, string> _idToName = new Dictionary<int, string>();
        private int _nextId = 1;

        public int GetOrAddCellId(string cellName)
        {
            if (_nameToId.TryGetValue(cellName, out int id))
                return id;

            id = _nextId++;
            _nameToId[cellName] = id;
            _idToName[id] = cellName;
            return id;
        }

        public int GetCellId(string cellName)
        {
            return _nameToId.TryGetValue(cellName, out int id) ? id : 0;
        }

        public string GetCellName(int cellId)
        {
            return _idToName.TryGetValue(cellId, out string name) ? name : null;
        }

        public Dictionary<int, string> GetAllCells() => _idToName;
        public int Count => _nameToId.Count;
    }

    /// <summary>
    /// Result from parsing transcriptomic data - includes lookup tables
    /// </summary>
    public class ParsedTranscriptomicData
    {
        public List<GeneExpressionRecord> ExpressionRecords { get; set; }
        public List<CellMetadata> Metadata { get; set; }
        public GeneLookup GeneLookup { get; set; }
        public CellLookup CellLookup { get; set; }

        public ParsedTranscriptomicData()
        {
            ExpressionRecords = new List<GeneExpressionRecord>();
            Metadata = new List<CellMetadata>();
            GeneLookup = new GeneLookup();
            CellLookup = new CellLookup();
        }
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