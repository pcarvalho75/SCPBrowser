using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using SCPBrowser.Models;

namespace SCPBrowser.Services
{
    /// <summary>
    /// Service for managing cell type classification persistence
    /// </summary>
    public class CellTypeClassificationService : DatabaseServiceBase
    {
        public CellTypeClassificationService(string projectDbPath) : base(projectDbPath)
        {
        }

        /// <summary>
        /// Saves cell type classifications to the database
        /// </summary>
        public async Task SaveCellTypeClassificationsAsync(int importId, Dictionary<string, CellTypePredictionResult> predictions,
            string scorerMethod = "Standard")
        {
            await WithConnectionAsync(async connection =>
            {
                using (var transaction = connection.BeginTransaction())
                {
                    try
                    {
                        var rawFileMap = new Dictionary<string, int>();
                        using (var command = connection.CreateCommand())
                        {
                            command.CommandText = "SELECT raw_file_id, raw_file_name FROM raw_files";
                            using (var reader = await command.ExecuteReaderAsync())
                            {
                                while (await reader.ReadAsync())
                                    rawFileMap[reader.GetString(1)] = reader.GetInt32(0);
                            }
                        }

                        int savedCount = 0;
                        using (var insertCmd = connection.CreateCommand())
                        {
                            insertCmd.CommandText = @"
                                INSERT OR REPLACE INTO raw_file_cell_type_classifications
                                (raw_file_id, predicted_cell_type, composite_score, spearman_correlation, specificity_score, hypergeometric_pvalue, confidence, scorer_method, classified_at)
                                VALUES (@rawFileId, @cellType, @compositeScore, @spearman, @specificity, @pvalue, @confidence, @scorerMethod, @timestamp)";

                            foreach (var kvp in predictions)
                            {
                                string runName = kvp.Key;
                                var prediction = kvp.Value;

                                if (!rawFileMap.TryGetValue(runName, out int rawFileId))
                                {

                                    continue;
                                }

                                string cellType = prediction.TopCellType ?? "Undetermined";
                                double compositeScore = 0.0, spearman = 0.0, specificity = 0.0, pvalue = 1.0;

                                if (prediction.TopScore != null)
                                {
                                    compositeScore = double.IsNaN(prediction.TopScore.CompositeScore) ? 0.0 : prediction.TopScore.CompositeScore;
                                    spearman = double.IsNaN(prediction.TopScore.SpearmanCorrelation) ? 0.0 : prediction.TopScore.SpearmanCorrelation;
                                    specificity = double.IsNaN(prediction.TopScore.SpecificityScore) ? 0.0 : prediction.TopScore.SpecificityScore;
                                    pvalue = double.IsNaN(prediction.TopScore.HypergeometricPValue) ? 1.0 : prediction.TopScore.HypergeometricPValue;
                                }

                                insertCmd.Parameters.Clear();
                                insertCmd.Parameters.AddWithValue("@rawFileId", rawFileId);
                                insertCmd.Parameters.AddWithValue("@cellType", cellType);
                                insertCmd.Parameters.AddWithValue("@compositeScore", compositeScore);
                                insertCmd.Parameters.AddWithValue("@spearman", spearman);
                                insertCmd.Parameters.AddWithValue("@specificity", specificity);
                                insertCmd.Parameters.AddWithValue("@pvalue", pvalue);
                                insertCmd.Parameters.AddWithValue("@confidence", double.IsNaN(prediction.Confidence) ? 0.0 : prediction.Confidence);
                                insertCmd.Parameters.AddWithValue("@scorerMethod", scorerMethod ?? "Standard");
                                insertCmd.Parameters.AddWithValue("@timestamp", DateTime.UtcNow.ToString("o"));

                                await insertCmd.ExecuteNonQueryAsync();
                                savedCount++;
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
            });
        }

        /// <summary>
        /// Loads cell type classifications from the database
        /// </summary>
        public async Task<Dictionary<string, CellTypePredictionResult>> LoadCellTypeClassificationsAsync(int importId)
        {
            var rows = await QueryAsync(@"
                SELECT rf.raw_file_name, c.predicted_cell_type,
                    c.composite_score, c.spearman_correlation,
                    c.specificity_score, c.hypergeometric_pvalue,
                    c.confidence, c.scorer_method
                FROM raw_file_cell_type_classifications c
                JOIN raw_files rf ON c.raw_file_id = rf.raw_file_id",
                reader =>
                {
                    string cellType = reader.GetString(1);
                    var topScore = new CellTypeScore
                    {
                        CompositeScore = reader.GetDouble(2),
                        SpearmanCorrelation = reader.GetDouble(3),
                        SpecificityScore = reader.GetDouble(4),
                        HypergeometricPValue = reader.GetDouble(5)
                    };
                    return (RunName: reader.GetString(0), Result: new CellTypePredictionResult
                    {
                        TopCellType = cellType,
                        TopScore = topScore,
                        Confidence = reader.GetDouble(6),
                        ScorerMethod = reader.IsDBNull(7) ? "Standard" : reader.GetString(7),
                        Scores = new Dictionary<string, CellTypeScore> { { cellType, topScore } }
                    });
                });

            var predictions = rows.ToDictionary(r => r.RunName, r => r.Result);

            return predictions;
        }

        /// <summary>
        /// Deletes all cell type classifications for a given import
        /// </summary>
        public async Task DeleteAllCellTypeClassificationsAsync(int importId)
        {
            int deletedCount = await ExecuteNonQueryAsync("DELETE FROM raw_file_cell_type_classifications");

        }

        /// <summary>
        /// The classifier method that produced the stored rows ("Standard"/"Quantitative"), or null if none exist.
        /// Legacy rows predate the column and read back as "Standard" via the column default.
        /// </summary>
        public async Task<string> GetStoredScorerMethodAsync()
        {
            var rows = await QueryAsync(
                "SELECT scorer_method FROM raw_file_cell_type_classifications LIMIT 1",
                reader => reader.IsDBNull(0) ? null : reader.GetString(0));
            return rows.FirstOrDefault();
        }
    }
}