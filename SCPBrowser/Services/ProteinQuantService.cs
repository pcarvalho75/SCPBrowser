using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using SCPBrowser.Models;

namespace SCPBrowser.Services
{
    /// <summary>
    /// Service for managing protein quantification statistics
    /// </summary>
    public class ProteinQuantService
    {
        private readonly string _projectDbPath;

        public ProteinQuantService(string projectDbPath)
        {
            _projectDbPath = projectDbPath;
        }

        /// <summary>
        /// Saves protein quantification summary statistics to the database
        /// </summary>
        public async Task SaveProteinQuantSummaryAsync(ProteomicsData data, List<RawFileInfo> rawFiles)
        {
            var connectionString = $"Data Source={_projectDbPath}";

            // Create lookup: RawFileName -> raw_file_id
            var rawFileIdMap = rawFiles.ToDictionary(rf => rf.RawFileName, rf => rf.RawFileId);

            // Prepare protein statistics
            var proteinStats = new List<ProteinQuantSummary>();

            foreach (var protein in data.ProteinQuantMatrix.Keys)
            {
                foreach (var rawFileName in data.RawFileNames)
                {
                    if (!rawFileIdMap.ContainsKey(rawFileName))
                        continue; // Skip if raw file not in our import

                    int rawFileId = rawFileIdMap[rawFileName];

                    // Get the summed intensity for this protein in this raw file
                    double intensity = 0;
                    if (data.ProteinQuantMatrix[protein].ContainsKey(rawFileName))
                    {
                        intensity = data.ProteinQuantMatrix[protein][rawFileName];
                    }

                    // For now, we'll use the summed intensity as both median and mean
                    // In a more sophisticated version, we'd store all peptide intensities
                    // and calculate proper statistics
                    if (intensity > 0)
                    {
                        proteinStats.Add(new ProteinQuantSummary
                        {
                            ProteinId = protein,
                            RawFileId = rawFileId,
                            MedianIntensity = intensity,
                            MeanIntensity = intensity,
                            DetectionCount = 1 // Detected in this file
                        });
                    }
                }
            }

            // Bulk insert protein statistics
            using (var connection = new SqliteConnection(connectionString))
            {
                await connection.OpenAsync();

                using (var transaction = connection.BeginTransaction())
                {
                    try
                    {
                        foreach (var stat in proteinStats)
                        {
                            using (var command = connection.CreateCommand())
                            {
                                command.CommandText = @"
                            INSERT INTO protein_quant_summary 
                            (protein_id, raw_file_id, median_intensity, mean_intensity, detection_count)
                            VALUES 
                            (@proteinId, @rawFileId, @median, @mean, @count)
                        ";

                                command.Parameters.AddWithValue("@proteinId", stat.ProteinId);
                                command.Parameters.AddWithValue("@rawFileId", stat.RawFileId);
                                command.Parameters.AddWithValue("@median", stat.MedianIntensity);
                                command.Parameters.AddWithValue("@mean", stat.MeanIntensity);
                                command.Parameters.AddWithValue("@count", stat.DetectionCount);

                                await command.ExecuteNonQueryAsync();
                            }
                        }

                        await transaction.CommitAsync();
                    }
                    catch
                    {
                        await transaction.RollbackAsync();
                        throw;
                    }
                }
            }
        }

        // Helper class for protein statistics
        private class ProteinQuantSummary
        {
            public string ProteinId { get; set; }
            public int RawFileId { get; set; }
            public double MedianIntensity { get; set; }
            public double MeanIntensity { get; set; }
            public int DetectionCount { get; set; }
        }
    }
}