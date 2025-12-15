using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using SCPBrowser.Models;

namespace SCPBrowser.Services
{
    /// <summary>
    /// Service for managing cell type classification persistence
    /// </summary>
    public class CellTypeClassificationService
    {
        private readonly string _projectDbPath;

        public CellTypeClassificationService(string projectDbPath)
        {
            _projectDbPath = projectDbPath;
        }

        /// <summary>
        /// Saves cell type classifications to the database
        /// </summary>
        public async Task SaveCellTypeClassificationsAsync(int importId, Dictionary<string, CellTypePredictionResult> predictions)
        {
            using (var connection = new SqliteConnection($"Data Source={_projectDbPath}"))
            {
                await connection.OpenAsync();

                using (var transaction = connection.BeginTransaction())
                {
                    try
                    {
                        // Get raw file ID mapping
                        var rawFileMap = new Dictionary<string, int>();
                        using (var command = connection.CreateCommand())
                        {
                            command.CommandText = @"
                                SELECT raw_file_id, raw_file_name 
                                FROM raw_files 
                                WHERE import_id = @importId
                            ";
                            command.Parameters.AddWithValue("@importId", importId);

                            using (var reader = await command.ExecuteReaderAsync())
                            {
                                while (await reader.ReadAsync())
                                {
                                    int rawFileId = reader.GetInt32(0);
                                    string rawFileName = reader.GetString(1);
                                    rawFileMap[rawFileName] = rawFileId;
                                }
                            }
                        }

                        // Insert classifications
                        using (var insertCmd = connection.CreateCommand())
                        {
                            insertCmd.CommandText = @"
                                INSERT OR REPLACE INTO raw_file_cell_type_classifications 
                                (raw_file_id, predicted_cell_type, composite_score, spearman_correlation, specificity_score, hypergeometric_pvalue, classified_at)
                                VALUES (@rawFileId, @cellType, @compositeScore, @spearman, @specificity, @pvalue, @timestamp)
                            ";

                            foreach (var kvp in predictions)
                            {
                                string runName = kvp.Key;
                                var prediction = kvp.Value;

                                if (!rawFileMap.TryGetValue(runName, out int rawFileId))
                                {
                                    Console.WriteLine($"Warning: Could not find raw_file_id for run '{runName}'");
                                    continue;
                                }

                                // Skip if no prediction data
                                if (prediction.TopScore == null)
                                    continue;

                                // Handle NaN values
                                double compositeScore = double.IsNaN(prediction.TopScore.CompositeScore) ? 0.0 : prediction.TopScore.CompositeScore;
                                double spearman = double.IsNaN(prediction.TopScore.SpearmanCorrelation) ? 0.0 : prediction.TopScore.SpearmanCorrelation;
                                double specificity = double.IsNaN(prediction.TopScore.SpecificityScore) ? 0.0 : prediction.TopScore.SpecificityScore;
                                double pvalue = double.IsNaN(prediction.TopScore.HypergeometricPValue) ? 1.0 : prediction.TopScore.HypergeometricPValue;

                                insertCmd.Parameters.Clear();
                                insertCmd.Parameters.AddWithValue("@rawFileId", rawFileId);
                                insertCmd.Parameters.AddWithValue("@cellType", prediction.TopCellType);
                                insertCmd.Parameters.AddWithValue("@compositeScore", compositeScore);
                                insertCmd.Parameters.AddWithValue("@spearman", spearman);
                                insertCmd.Parameters.AddWithValue("@specificity", specificity);
                                insertCmd.Parameters.AddWithValue("@pvalue", pvalue);
                                insertCmd.Parameters.AddWithValue("@timestamp", DateTime.UtcNow.ToString("o"));

                                await insertCmd.ExecuteNonQueryAsync();
                            }
                        }

                        transaction.Commit();
                    }
                    catch (Exception ex)
                    {
                        transaction.Rollback();
                        throw new Exception($"Failed to save cell type classifications: {ex.Message}", ex);
                    }
                }
            }
        }

        /// <summary>
        /// Loads cell type classifications from the database
        /// </summary>
        public async Task<Dictionary<string, CellTypePredictionResult>> LoadCellTypeClassificationsAsync(int importId)
        {
            var predictions = new Dictionary<string, CellTypePredictionResult>();

            using (var connection = new SqliteConnection($"Data Source={_projectDbPath}"))
            {
                await connection.OpenAsync();

                using (var command = connection.CreateCommand())
                {
                    command.CommandText = @"
                        SELECT rf.raw_file_name, c.predicted_cell_type, 
                               c.composite_score, c.spearman_correlation, 
                               c.specificity_score, c.hypergeometric_pvalue
                        FROM raw_file_cell_type_classifications c
                        JOIN raw_files rf ON c.raw_file_id = rf.raw_file_id
                        WHERE rf.import_id = @importId
                    ";
                    command.Parameters.AddWithValue("@importId", importId);

                    using (var reader = await command.ExecuteReaderAsync())
                    {
                        while (await reader.ReadAsync())
                        {
                            string runName = reader.GetString(0);
                            string cellType = reader.GetString(1);
                            double compositeScore = reader.GetDouble(2);
                            double spearman = reader.GetDouble(3);
                            double specificity = reader.GetDouble(4);
                            double pvalue = reader.GetDouble(5);

                            var topScore = new CellTypeScore
                            {
                                CompositeScore = compositeScore,
                                SpearmanCorrelation = spearman,
                                SpecificityScore = specificity,
                                HypergeometricPValue = pvalue
                            };

                            var result = new CellTypePredictionResult
                            {
                                TopCellType = cellType,
                                TopScore = topScore,
                                Scores = new Dictionary<string, CellTypeScore>
                                {
                                    { cellType, topScore }
                                }
                            };

                            predictions[runName] = result;
                        }
                    }
                }
            }

            return predictions;
        }

        /// <summary>
        /// Deletes all cell type classifications for a given import
        /// </summary>
        public async Task DeleteAllCellTypeClassificationsAsync(int importId)
        {
            using (var connection = new SqliteConnection($"Data Source={_projectDbPath}"))
            {
                await connection.OpenAsync();

                using (var command = connection.CreateCommand())
                {
                    command.CommandText = @"
                        DELETE FROM raw_file_cell_type_classifications 
                        WHERE raw_file_id IN (
                            SELECT raw_file_id 
                            FROM raw_files 
                            WHERE import_id = @importId
                        )
                    ";
                    command.Parameters.AddWithValue("@importId", importId);

                    int deletedCount = await command.ExecuteNonQueryAsync();
                    Console.WriteLine($"Deleted {deletedCount} cell type classifications for import {importId}");
                }
            }
        }
    }
}