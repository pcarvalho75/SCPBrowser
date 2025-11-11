using System.Collections.Generic;
using System.Linq;

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
    /// Result from parsing transcriptomic data - NOW INCLUDES CELL TYPE PROFILES
    /// </summary>
    public class ParsedTranscriptomicData
    {
        // NEW: Cell type profiles (aggregated data)
        public List<CellTypeProfile> CellTypeProfiles { get; set; }

        // NEW: Metadata about each cell type
        public List<CellTypeMetadata> CellTypeMetadata { get; set; }

        // Keep these for backwards compatibility during transition
        public List<GeneExpressionRecord> ExpressionRecords { get; set; }
        public List<CellMetadata> Metadata { get; set; }
        public GeneLookup GeneLookup { get; set; }
        public CellLookup CellLookup { get; set; }

        public ParsedTranscriptomicData()
        {
            CellTypeProfiles = new List<CellTypeProfile>();
            CellTypeMetadata = new List<CellTypeMetadata>();
            ExpressionRecords = new List<GeneExpressionRecord>();
            Metadata = new List<CellMetadata>();
            GeneLookup = new GeneLookup();
            CellLookup = new CellLookup();
        }
    }

    /// <summary>
    /// NEW VERSION: Stores aggregated cell type profiles instead of individual cells
    /// This is MUCH faster to load and use for predictions
    /// </summary>
    public class TranscriptomicDatabase
    {
        /// <summary>
        /// Cell type profiles: cell type name -> aggregated gene expression profile
        /// This replaces the old GeneExpression dictionary
        /// </summary>
        public Dictionary<string, CellTypeProfile> CellTypeProfiles { get; set; }

        /// <summary>
        /// Metadata about each cell type (count, age range, etc.)
        /// This replaces the old CellMetadata and CellTypeIndex
        /// </summary>
        public Dictionary<string, CellTypeMetadata> CellTypeMetadata { get; set; }

        public TranscriptomicDatabase()
        {
            CellTypeProfiles = new Dictionary<string, CellTypeProfile>();
            CellTypeMetadata = new Dictionary<string, CellTypeMetadata>();
        }

        /// <summary>
        /// Total number of unique genes across all cell type profiles
        /// </summary>
        public int TotalGenes
        {
            get
            {
                var allGenes = new HashSet<string>();
                foreach (var profile in CellTypeProfiles.Values)
                {
                    foreach (var gene in profile.MedianExpression.Keys)
                    {
                        allGenes.Add(gene);
                    }
                }
                return allGenes.Count;
            }
        }

        /// <summary>
        /// Total number of cells represented across all cell types
        /// </summary>
        public int TotalCells
        {
            get
            {
                return CellTypeMetadata.Values.Sum(m => m.CellCount);
            }
        }

        /// <summary>
        /// Total number of distinct cell types
        /// </summary>
        public int TotalCellTypes => CellTypeProfiles.Count;
    }
}