using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using MathNet.Numerics.Statistics;

namespace SCPBrowser.Services
{
    /// <summary>
    /// Result of highly variable protein analysis using the Seurat v3 VST method.
    /// </summary>
    public class HvpResult
    {
        /// <summary>Protein identifier</summary>
        public string ProteinId { get; set; } = string.Empty;

        /// <summary>Mean abundance (calculated on observed values only)</summary>
        public double Mean { get; set; }

        /// <summary>Observed variance (calculated on observed values only)</summary>
        public double Variance { get; set; }

        /// <summary>Expected variance from LOESS fit (mean-variance relationship)</summary>
        public double VarianceExpected { get; set; }

        /// <summary>Standardized variance after VST (ranking criterion)</summary>
        public double VarianceStandardized { get; set; }

        /// <summary>Rank by standardized variance (1 = most variable)</summary>
        public int Rank { get; set; }

        /// <summary>
        /// True only when VST actually ran and VarianceStandardized is a measured quantity. False when too few
        /// proteins passed the detection filter to fit the mean-variance trend — in that state every protein
        /// carries VarianceStandardized = 0, so any ranking by it is arbitrary (it degenerates to alphabetical)
        /// and callers must NOT present the result as "top N highly variable".
        /// </summary>
        public bool IsScored { get; set; }

        /// <summary>Whether this protein is selected as highly variable</summary>
        public bool IsHighlyVariable { get; set; }

        /// <summary>Number of cells where this protein was detected (non-zero/non-missing)</summary>
        public int DetectionCount { get; set; }

        /// <summary>Detection rate (fraction of cells with observed values)</summary>
        public double DetectionRate { get; set; }
    }

    /// <summary>
    /// Service for identifying highly variable proteins (HVPs) in single-cell proteomics data
    /// using the Variance Stabilizing Transformation (VST) method from Seurat v3.
    /// 
    /// <para><b>Algorithm (Stuart et al., 2019):</b></para>
    /// <list type="number">
    ///   <item>Calculate mean and variance for each protein across observed cells</item>
    ///   <item>Fit LOESS regression: log10(variance) ~ log10(mean) to model mean-variance relationship</item>
    ///   <item>Get expected variance from the fitted curve</item>
    ///   <item>Standardize each value: (x - mean) / sqrt(expected_variance)</item>
    ///   <item>Clip standardized values to [-sqrt(n_cells), sqrt(n_cells)]</item>
    ///   <item>Calculate variance of clipped standardized values</item>
    ///   <item>Rank proteins by this "standardized variance"</item>
    /// </list>
    /// 
    /// <para><b>Adaptations for Proteomics:</b></para>
    /// <list type="bullet">
    ///   <item>Missing values are treated as NULL (ignored), not as ZERO</item>
    ///   <item>Requires raw intensity values (not log-transformed)</item>
    ///   <item>Enforces minimum detection count for statistical reliability</item>
    /// </list>
    /// 
    /// <para><b>Reference:</b></para>
    /// Stuart T, Butler A, Hoffman P, et al. Comprehensive Integration of Single-Cell Data.
    /// Cell. 2019;177(7):1888-1902.e21. doi:10.1016/j.cell.2019.05.031
    /// </summary>
    public class HighlyVariableProteinsService
    {
        #region Events

        /// <summary>
        /// Event raised to report progress during analysis.
        /// Argument is a value between 0 and 100.
        /// </summary>
        public event Action<int> ProgressChanged;

        #endregion

        #region Public Methods

        /// <summary>
        /// Identifies highly variable proteins using the Seurat v3 VST method.
        /// </summary>
        /// <param name="proteinMatrix">
        /// Dictionary where key=protein ID, value=dictionary of run->abundance.
        /// <b>IMPORTANT:</b> Values must be RAW intensities (not log-transformed).
        /// Missing values should simply be absent from the inner dictionary.
        /// </param>
        /// <param name="nTopProteins">Number of top variable proteins to select (default: 1000)</param>
        /// <param name="loessSpan">
        /// Fraction of data used for LOESS fit (default: 0.3, matching Seurat).
        /// Lower values = more local fit, higher values = smoother fit.
        /// </param>
        /// <param name="minDetectionRate">
        /// Minimum fraction of cells where protein must be detected (default: 0.05 = 5%).
        /// </param>
        /// <param name="minAbsoluteDetections">
        /// Minimum absolute number of cells where protein must be detected (default: 20).
        /// This ensures statistical reliability of variance estimates.
        /// The effective minimum is max(minDetectionRate * nCells, minAbsoluteDetections).
        /// </param>
        /// <param name="clipMax">
        /// Maximum value for clipping standardized values (default: null = sqrt(n_cells)).
        /// Set to double.PositiveInfinity for no clipping.
        /// </param>
        /// <returns>List of HVP results for all proteins, sorted by rank</returns>
        /// <exception cref="ArgumentException">
        /// Thrown if input data contains negative values (indicating log-transformed data).
        /// </exception>
        public List<HvpResult> FindHighlyVariableProteins(
            Dictionary<string, Dictionary<string, double>> proteinMatrix,
            int nTopProteins = 1000,
            double loessSpan = 0.3,
            double minDetectionRate = 0.05,
            int minAbsoluteDetections = 20,
            double? clipMax = null)
        {
            if (proteinMatrix == null || proteinMatrix.Count == 0)
                return new List<HvpResult>();

            int nProteins = proteinMatrix.Count;
            ReportProgress(0);

            // 1. Performance Warning
            if (nProteins > 5000)
            {

            }

            // 2. Robust Input Validation
            // Check ALL proteins for negative values (indicates log-transformed data)
            // Create a snapshot to avoid race condition with concurrent modifications
            var proteinMatrixSnapshot = proteinMatrix.ToList();
            bool hasNegatives = proteinMatrixSnapshot.AsParallel().Any(p => p.Value.Values.Any(v => v < 0));
            if (hasNegatives)
            {
                throw new ArgumentException(
                    "[HVP Error] Input data contains negative values, suggesting log-transformed or centered data. " +
                    "The VST algorithm requires RAW intensities (strictly positive) to model the mean-variance relationship correctly. " +
                    "Please provide un-transformed intensity values.");
            }

            // Get total number of cells (union of all runs)
            var allRuns = proteinMatrix.Values
                .SelectMany(d => d.Keys)
                .Distinct()
                .ToList();

            int nCells = allRuns.Count;
            double clipValue = clipMax ?? Math.Sqrt(nCells);


            ReportProgress(10);

            // Step 1: Calculate Basic Stats (ON OBSERVED DATA ONLY)
            var results = CalculateBasicStats(proteinMatrix, nCells);
            ReportProgress(30);

            // Step 2: Filter by detection rate AND absolute count
            // Take the stricter of the two thresholds
            int rateBasedMin = (int)Math.Ceiling(minDetectionRate * nCells);
            int effectiveMin = Math.Max(rateBasedMin, minAbsoluteDetections);

            var validResults = results
                .Where(r => r.DetectionCount >= effectiveMin && r.Variance > 0 && r.Mean > 0)
                .ToList();



            if (validResults.Count < 10)
            {
                // Too few proteins clear the detection filter to fit the mean-variance trend, so VST never runs and
                // VarianceStandardized stays 0.0 for EVERY protein. Leave IsScored false: ranking by an all-zero key
                // is arbitrary and, because the sort is stable over a ProteinId-ordered input, silently degenerates
                // to "alphabetically first N". Callers must fall back rather than label that selection "top N
                // highly variable".
                foreach (var r in results)
                {
                    r.Rank = int.MaxValue;
                    r.IsHighlyVariable = false;
                    r.IsScored = false;
                }
                ReportProgress(100);
                return results.OrderBy(r => r.ProteinId).ToList();
            }

            // Step 3: Apply VST
            ApplyVstMethod(validResults, proteinMatrix, nTopProteins, loessSpan, clipValue);
            ReportProgress(90);

            // Mark proteins that didn't pass filters
            var validIds = new HashSet<string>(validResults.Select(r => r.ProteinId));
            foreach (var r in results.Where(r => !validIds.Contains(r.ProteinId)))
            {
                r.Rank = int.MaxValue;
                r.IsHighlyVariable = false;
                r.VarianceExpected = 0;
                r.VarianceStandardized = 0;
            }

            var finalResults = results
                .OrderBy(r => r.Rank)
                .ThenByDescending(r => r.VarianceStandardized)
                .ToList();

            int hvpCount = finalResults.Count(r => r.IsHighlyVariable);

            ReportProgress(100);

            return finalResults;
        }

        /// <summary>
        /// Gets the list of highly variable protein IDs, sorted by rank (most variable first).
        /// </summary>
        /// <param name="results">Results from FindHighlyVariableProteins</param>
        /// <returns>List of protein IDs marked as highly variable</returns>
        public List<string> GetHighlyVariableProteinIds(List<HvpResult> results)
        {
            if (results == null)
                return new List<string>();

            return results
                .Where(r => r.IsHighlyVariable)
                .OrderBy(r => r.Rank)
                .Select(r => r.ProteinId)
                .ToList();
        }

        /// <summary>
        /// Filters a protein matrix to include only highly variable proteins.
        /// </summary>
        /// <param name="proteinMatrix">Original protein matrix</param>
        /// <param name="hvpResults">Results from FindHighlyVariableProteins</param>
        /// <returns>Filtered matrix containing only HVPs</returns>
        public Dictionary<string, Dictionary<string, double>> FilterToHvp(
            Dictionary<string, Dictionary<string, double>> proteinMatrix,
            List<HvpResult> hvpResults)
        {
            if (proteinMatrix == null || hvpResults == null)
                return new Dictionary<string, Dictionary<string, double>>();

            var hvpIds = new HashSet<string>(GetHighlyVariableProteinIds(hvpResults));

            return proteinMatrix
                .Where(kvp => hvpIds.Contains(kvp.Key))
                .ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
        }

        /// <summary>
        /// Exports HVP results to a CSV file for visualization (e.g., mean-variance plot).
        /// Useful for debugging and verifying the LOESS fit.
        /// </summary>
        /// <param name="results">Results from FindHighlyVariableProteins</param>
        /// <param name="filePath">Output CSV file path</param>
        /// <param name="includeAllProteins">If true, exports all proteins; if false, only HVPs</param>
        public void ExportToCsv(List<HvpResult> results, string filePath, bool includeAllProteins = true)
        {
            if (results == null || results.Count == 0)
                return;

            var toExport = includeAllProteins ? results : results.Where(r => r.IsHighlyVariable);

            var sb = new StringBuilder();
            sb.AppendLine("ProteinId,Mean,Variance,VarianceExpected,VarianceStandardized,Rank,IsHighlyVariable,DetectionCount,DetectionRate,Log10Mean,Log10Variance,Log10VarianceExpected");

            foreach (var r in toExport.OrderBy(r => r.Rank))
            {
                double log10Mean = r.Mean > 0 ? Math.Log10(r.Mean) : double.NaN;
                double log10Var = r.Variance > 0 ? Math.Log10(r.Variance) : double.NaN;
                double log10VarExp = r.VarianceExpected > 0 ? Math.Log10(r.VarianceExpected) : double.NaN;

                sb.AppendLine($"{r.ProteinId},{r.Mean:G6},{r.Variance:G6},{r.VarianceExpected:G6},{r.VarianceStandardized:G6}," +
                              $"{(r.Rank == int.MaxValue ? "NA" : r.Rank.ToString())},{r.IsHighlyVariable},{r.DetectionCount},{r.DetectionRate:F4}," +
                              $"{log10Mean:G6},{log10Var:G6},{log10VarExp:G6}");
            }

            File.WriteAllText(filePath, sb.ToString());

        }

        /// <summary>
        /// Gets a summary of the HVP analysis results.
        /// </summary>
        public string GetSummary(List<HvpResult> results)
        {
            if (results == null || results.Count == 0)
                return "No results available.";

            int total = results.Count;
            int hvpCount = results.Count(r => r.IsHighlyVariable);
            int passedFilter = results.Count(r => r.Rank != int.MaxValue);

            var hvps = results.Where(r => r.IsHighlyVariable).ToList();
            double avgDetectionRate = hvps.Any() ? hvps.Average(r => r.DetectionRate) : 0;
            double avgVarianceStd = hvps.Any() ? hvps.Average(r => r.VarianceStandardized) : 0;

            var topProteins = hvps.Take(10).Select(r => r.ProteinId);

            return $"=== HVP Analysis Summary ===\n" +
                   $"Total proteins: {total}\n" +
                   $"Passed detection filter: {passedFilter}\n" +
                   $"Selected as HVP: {hvpCount}\n" +
                   $"Avg detection rate (HVPs): {avgDetectionRate:P1}\n" +
                   $"Avg standardized variance (HVPs): {avgVarianceStd:F3}\n" +
                   $"Top 10 HVPs: {string.Join(", ", topProteins)}";
        }

        #endregion

        #region Private Methods - Statistics

        private List<HvpResult> CalculateBasicStats(
            Dictionary<string, Dictionary<string, double>> proteinMatrix,
            int nCells)
        {
            var results = new List<HvpResult>(proteinMatrix.Count);

            foreach (var kvp in proteinMatrix)
            {
                // CRITICAL: Only use observed/detected values
                // Missing data in proteomics = "not detected", NOT "zero expression"
                var observedValues = kvp.Value.Values.ToList();
                int detectionCount = observedValues.Count;

                double mean = 0;
                double variance = 0;

                if (detectionCount > 0)
                {
                    mean = observedValues.Average();
                    if (detectionCount > 1)
                    {
                        variance = Statistics.Variance(observedValues);
                    }
                }

                results.Add(new HvpResult
                {
                    ProteinId = kvp.Key,
                    Mean = mean,
                    Variance = variance,
                    DetectionCount = detectionCount,
                    DetectionRate = (double)detectionCount / nCells
                });
            }

            return results;
        }

        private void ApplyVstMethod(
            List<HvpResult> validResults,
            Dictionary<string, Dictionary<string, double>> proteinMatrix,
            int nTopProteins,
            double loessSpan,
            double clipMax)
        {
            // Prepare data for LOESS: log10(mean) vs log10(variance)
            var logMeans = validResults.Select(r => Math.Log10(r.Mean)).ToArray();
            var logVariances = validResults.Select(r => Math.Log10(r.Variance)).ToArray();

            // Fit LOESS to model the mean-variance relationship
            var fittedLogVariance = FitLoess(logMeans, logVariances, loessSpan);

            // Calculate standardized variance for each protein
            for (int i = 0; i < validResults.Count; i++)
            {
                var result = validResults[i];
                var abundances = proteinMatrix[result.ProteinId].Values.ToList(); // Observed only

                // VST is running for this protein, so its VarianceStandardized below is a measured quantity.
                result.IsScored = true;

                // Expected variance from LOESS fit
                result.VarianceExpected = Math.Pow(10, fittedLogVariance[i]);
                double expectedStd = Math.Sqrt(Math.Max(result.VarianceExpected, 1e-10));

                // Standardize observed values: (x - mean) / expected_std
                // Then clip to prevent outliers from dominating
                var standardized = abundances
                    .Select(v => (v - result.Mean) / expectedStd)
                    .Select(v => Math.Max(-clipMax, Math.Min(clipMax, v)))
                    .ToList();

                // Calculate variance of clipped standardized values
                result.VarianceStandardized = standardized.Count > 1
                    ? Statistics.Variance(standardized)
                    : 0;
            }

            // Rank by standardized variance (descending - higher = more variable)
            var ranked = validResults
                .OrderByDescending(r => r.VarianceStandardized)
                .ToList();

            for (int i = 0; i < ranked.Count; i++)
            {
                ranked[i].Rank = i + 1;
                ranked[i].IsHighlyVariable = i < nTopProteins;
            }
        }

        #endregion

        #region Private Methods - LOESS Regression

        /// <summary>
        /// LOESS (Locally Estimated Scatterplot Smoothing) implementation.
        /// Fits a local quadratic polynomial at each point using tricube weighting.
        /// </summary>
        /// <param name="x">Independent variable (log10 mean)</param>
        /// <param name="y">Dependent variable (log10 variance)</param>
        /// <param name="span">Fraction of data to use for each local fit</param>
        /// <returns>Fitted values for each x</returns>
        private double[] FitLoess(double[] x, double[] y, double span)
        {
            int n = x.Length;

            // Need minimum points for quadratic fit
            if (n < 5)
                return (double[])y.Clone();

            // Calculate window size, ensuring it's within valid bounds
            int windowSize = (int)(span * n);
            windowSize = Math.Max(3, windowSize);   // At least 3 points for quadratic fit
            windowSize = Math.Min(windowSize, n);   // At most n points (prevents IndexOutOfRange)

            var fitted = new double[n];
            var indices = Enumerable.Range(0, n).ToArray();

            for (int i = 0; i < n; i++)
            {
                double xi = x[i];

                // Find k nearest neighbors by x distance
                var nearestIndices = indices
                    .Select(j => new { Index = j, Dist = Math.Abs(x[j] - xi) })
                    .OrderBy(p => p.Dist)
                    .Take(windowSize)
                    .ToList();

                int actualK = nearestIndices.Count;

                // Safety: need at least 3 points for quadratic fit
                if (actualK < 3)
                {
                    fitted[i] = y[i];
                    continue;
                }

                // Maximum distance for weight calculation
                double maxDist = nearestIndices.Last().Dist;
                if (maxDist < 1e-10) maxDist = 1.0;

                // Extract local data and weights
                var x_local = new double[actualK];
                var y_local = new double[actualK];
                var w_local = new double[actualK];

                for (int k = 0; k < actualK; k++)
                {
                    int idx = nearestIndices[k].Index;
                    x_local[k] = x[idx];
                    y_local[k] = y[idx];
                    w_local[k] = TricubeWeight(nearestIndices[k].Dist / maxDist);
                }

                fitted[i] = WeightedLocalRegression(xi, x_local, y_local, w_local);

                // Report progress during LOESS (most expensive step)
                if (i % 100 == 0)
                {
                    int progress = 30 + (int)(60.0 * i / n);
                    ReportProgress(progress);
                }
            }

            return fitted;
        }

        /// <summary>
        /// Weighted local polynomial regression (degree 2 = quadratic).
        /// Solves the weighted least squares problem using Cramer's rule.
        /// </summary>
        private double WeightedLocalRegression(double xi, double[] x, double[] y, double[] w, int count = -1)
        {
            // Build weighted normal equations for quadratic fit: y = a + b*x + c*x²
            // Matrix form: (X'WX)β = X'Wy

            int n = count > 0 ? count : x.Length;
            double sumW = 0, sumWX = 0, sumWX2 = 0, sumWX3 = 0, sumWX4 = 0;
            double sumWY = 0, sumWXY = 0, sumWX2Y = 0;

            for (int i = 0; i < n; i++)
            {
                double wi = w[i];
                double xi_val = x[i];
                double yi = y[i];
                double xi2 = xi_val * xi_val;

                sumW += wi;
                sumWX += wi * xi_val;
                sumWX2 += wi * xi2;
                sumWX3 += wi * xi2 * xi_val;
                sumWX4 += wi * xi2 * xi2;
                sumWY += wi * yi;
                sumWXY += wi * xi_val * yi;
                sumWX2Y += wi * xi2 * yi;
            }

            // Normal equations matrix
            double[,] A = {
                { sumW,   sumWX,  sumWX2 },
                { sumWX,  sumWX2, sumWX3 },
                { sumWX2, sumWX3, sumWX4 }
            };
            double[] B = { sumWY, sumWXY, sumWX2Y };

            double det = Determinant3x3(A);

            // If matrix is singular (collinear points), fall back to weighted mean
            if (Math.Abs(det) < 1e-10)
            {
                return sumW > 0 ? sumWY / sumW : 0;
            }

            // Solve using Cramer's rule
            double a = Determinant3x3(ReplaceColumn(A, B, 0)) / det;
            double b = Determinant3x3(ReplaceColumn(A, B, 1)) / det;
            double c = Determinant3x3(ReplaceColumn(A, B, 2)) / det;

            return a + b * xi + c * xi * xi;
        }

        /// <summary>
        /// Tricube weight function: w(u) = (1 - |u|³)³ for |u| &lt; 1, else 0
        /// </summary>
        private double TricubeWeight(double u)
        {
            if (u >= 1.0) return 0.0;
            double t = 1.0 - u * u * u;
            return t * t * t;
        }

        #endregion

        #region Private Methods - Linear Algebra Helpers

        /// <summary>
        /// Calculates the determinant of a 3x3 matrix using the rule of Sarrus.
        /// </summary>
        private double Determinant3x3(double[,] m)
        {
            return m[0, 0] * (m[1, 1] * m[2, 2] - m[1, 2] * m[2, 1])
                 - m[0, 1] * (m[1, 0] * m[2, 2] - m[1, 2] * m[2, 0])
                 + m[0, 2] * (m[1, 0] * m[2, 1] - m[1, 1] * m[2, 0]);
        }

        /// <summary>
        /// Creates a copy of matrix with one column replaced (for Cramer's rule).
        /// </summary>
        private double[,] ReplaceColumn(double[,] m, double[] col, int colIndex)
        {
            var result = (double[,])m.Clone();
            result[0, colIndex] = col[0];
            result[1, colIndex] = col[1];
            result[2, colIndex] = col[2];
            return result;
        }

        private void ReportProgress(int percent)
        {
            ProgressChanged?.Invoke(Math.Min(100, Math.Max(0, percent)));
        }

        #endregion
    }
}