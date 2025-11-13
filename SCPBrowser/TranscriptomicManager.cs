using SCPBrowser.Services;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace SCPBrowser
{
    public class TranscriptomicManager
    {
        private TranscriptomicDatabase _database;
        private CellTypePredictor _predictor;
        private readonly ReferenceDataService _referenceService;

        public bool IsLoaded => _database != null && _predictor != null;
        public TranscriptomicDatabase Database => _database;

        public TranscriptomicManager()
        {
            _referenceService = new ReferenceDataService();
        }

        public async Task LoadDatabaseAsync(string databasePath)
        {
            if (!File.Exists(databasePath))
                throw new FileNotFoundException("Reference database not found", databasePath);

            _database = await _referenceService.LoadTranscriptomicDataAsync(databasePath);
            _predictor = new CellTypePredictor(_database);
        }

        public CellTypePredictionResult PredictCellTypeForRun(ProteomicsData proteomicsData, string runName)
        {
            if (!IsLoaded)
                throw new InvalidOperationException("Transcriptomic database not loaded");

            if (proteomicsData == null || string.IsNullOrEmpty(runName))
                return new CellTypePredictionResult();

            var proteinAbundances = ExtractProteinAbundances(proteomicsData, runName);
            return _predictor.PredictCellType(proteinAbundances);
        }

        // In SCPBrowser/TranscriptomicManager.cs

        public Dictionary<string, CellTypePredictionResult> PredictCellTypesForAllRuns(ProteomicsData proteomicsData)
        {
            if (!IsLoaded)
                throw new InvalidOperationException("Transcriptomic database not loaded");

            var predictions = new Dictionary<string, CellTypePredictionResult>();

            // --- ADD THIS NULL CHECK ---
            // If there's no proteomics data, just return an empty prediction list.
            // This prevents the crash when a project is opened with no data.
            if (proteomicsData == null || proteomicsData.RawFileNames == null)
            {
                return predictions;
            }
            // --- END OF NULL CHECK ---

            foreach (var runName in proteomicsData.RawFileNames) // This line was crashing
            {
                var prediction = PredictCellTypeForRun(proteomicsData, runName);
                predictions[runName] = prediction;
            }

            return predictions;
        }

        private Dictionary<string, double> ExtractProteinAbundances(ProteomicsData proteomicsData, string runName)
        {
            var abundances = new Dictionary<string, double>();

            foreach (var proteinGroup in proteomicsData.ProteinQuantMatrix.Keys)
            {
                if (proteomicsData.ProteinQuantMatrix[proteinGroup].ContainsKey(runName))
                {
                    double abundance = proteomicsData.ProteinQuantMatrix[proteinGroup][runName];

                    if (abundance > 0)
                    {
                        var proteinNames = ExtractProteinNames(proteinGroup);
                        foreach (var proteinName in proteinNames)
                        {
                            if (!abundances.ContainsKey(proteinName))
                            {
                                abundances[proteinName] = abundance;
                            }
                            else
                            {
                                abundances[proteinName] = Math.Max(abundances[proteinName], abundance);
                            }
                        }
                    }
                }
            }

            return abundances;
        }

        private List<string> ExtractProteinNames(string proteinGroup)
        {
            var names = new List<string>();

            var parts = proteinGroup.Split(';', ',');
            foreach (var part in parts)
            {
                var trimmed = part.Trim();
                if (!string.IsNullOrEmpty(trimmed))
                {
                    var geneName = ExtractGeneName(trimmed);
                    if (!string.IsNullOrEmpty(geneName))
                    {
                        names.Add(geneName);
                    }
                }
            }

            return names;
        }

        private string ExtractGeneName(string proteinIdentifier)
        {
            if (string.IsNullOrEmpty(proteinIdentifier))
                return null;

            if (proteinIdentifier.Contains("_"))
            {
                var parts = proteinIdentifier.Split('_');
                if (parts.Length > 0)
                    return parts[0];
            }

            if (proteinIdentifier.Contains("|"))
            {
                var parts = proteinIdentifier.Split('|');
                if (parts.Length >= 2)
                    return parts[1];
            }

            return proteinIdentifier;
        }

        public Dictionary<string, System.Windows.Media.Color> GenerateCellTypeColorMap()
        {
            if (!IsLoaded)
                return new Dictionary<string, System.Windows.Media.Color>();

            // FIX: Use CellTypeProfiles.Keys instead of CellTypeIndex.Keys
            var cellTypes = _database.CellTypeProfiles.Keys.OrderBy(ct => ct).ToList();
            var colorMap = new Dictionary<string, System.Windows.Media.Color>();

            for (int i = 0; i < cellTypes.Count; i++)
            {
                colorMap[cellTypes[i]] = GetDistinctColor(i, cellTypes.Count);
            }

            return colorMap;
        }

        private System.Windows.Media.Color GetDistinctColor(int index, int total)
        {
            double hue = (double)index / total * 360.0;
            return HsvToRgb(hue, 0.7, 0.9);
        }

        private System.Windows.Media.Color HsvToRgb(double h, double s, double v)
        {
            double c = v * s;
            double x = c * (1 - Math.Abs((h / 60.0) % 2 - 1));
            double m = v - c;

            double r, g, b;

            if (h < 60)
            {
                r = c; g = x; b = 0;
            }
            else if (h < 120)
            {
                r = x; g = c; b = 0;
            }
            else if (h < 180)
            {
                r = 0; g = c; b = x;
            }
            else if (h < 240)
            {
                r = 0; g = x; b = c;
            }
            else if (h < 300)
            {
                r = x; g = 0; b = c;
            }
            else
            {
                r = c; g = 0; b = x;
            }

            return System.Windows.Media.Color.FromRgb(
                (byte)((r + m) * 255),
                (byte)((g + m) * 255),
                (byte)((b + m) * 255)
            );
        }
    }
}