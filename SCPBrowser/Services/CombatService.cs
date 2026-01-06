using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using MathNet.Numerics.LinearAlgebra;
using MathNet.Numerics.LinearAlgebra.Double;

namespace SCPBrowser.Services
{
    /// <summary>
    /// Production-grade ComBat batch effect correction.
    /// Based on Johnson et al. 2007 "Adjusting batch effects in microarray expression data 
    /// using empirical Bayes methods" (Biostatistics 8:118-127).
    /// 
    /// Ported from scanpy's combat.py implementation.
    /// 
    /// REQUIREMENTS:
    /// - NuGet: MathNet.Numerics
    /// - Input data must be IMPUTED (no NaN/Inf values)
    /// </summary>
    public class ComBatService
    {
        private const double ConvergenceThreshold = 0.0001;
        private const int MaxIterations = 1000;

        public class ComBatResult
        {
            public double[,] CorrectedData { get; set; }
            public bool Success { get; set; }
            public string ErrorMessage { get; set; }
            public int NumBatches { get; set; }
            public int NumSamples { get; set; }
            public int NumFeatures { get; set; }
        }

        /// <summary>
        /// Applies ComBat batch correction using empirical Bayes framework.
        /// </summary>
        /// <param name="data">Data matrix [Samples x Features]. MUST be imputed (no NaN/Inf).</param>
        /// <param name="batchLabels">Batch assignment for each sample.</param>
        /// <param name="biologicalCovariates">Optional biological condition labels to preserve.</param>
        /// <returns>ComBatResult with corrected data in [Samples x Features] format.</returns>
        public ComBatResult Apply(double[,] data, List<int> batchLabels, List<int> biologicalCovariates = null)
        {
            // Input validation
            if (data == null)
                return Fail("Data cannot be null.");

            int nSamples = data.GetLength(0);
            int nFeatures = data.GetLength(1);

            if (batchLabels == null || batchLabels.Count != nSamples)
                return Fail("Batch labels must match number of samples.");

            if (biologicalCovariates != null && biologicalCovariates.Count != nSamples)
                return Fail("Biological covariates must match number of samples.");

            // Check for NaN/Inf - ComBat requires complete matrix
            for (int i = 0; i < nSamples; i++)
            {
                for (int j = 0; j < nFeatures; j++)
                {
                    if (double.IsNaN(data[i, j]) || double.IsInfinity(data[i, j]))
                        return Fail("ComBat requires a complete matrix. Please impute missing values first.");
                }
            }

            var uniqueBatches = batchLabels.Distinct().OrderBy(x => x).ToArray();
            int nBatches = uniqueBatches.Length;

            if (nBatches < 2)
            {
                return new ComBatResult
                {
                    Success = true,
                    CorrectedData = (double[,])data.Clone(),
                    ErrorMessage = "Only one batch found, no correction applied.",
                    NumBatches = 1,
                    NumSamples = nSamples,
                    NumFeatures = nFeatures
                };
            }

            // Build batch index lookup
            var batchIndices = new List<int[]>();
            var nPerBatch = new int[nBatches];
            for (int b = 0; b < nBatches; b++)
            {
                int batchLabel = uniqueBatches[b];
                var indices = Enumerable.Range(0, nSamples)
                    .Where(i => batchLabels[i] == batchLabel)
                    .ToArray();
                batchIndices.Add(indices);
                nPerBatch[b] = indices.Length;
            }

            // Convert to MathNet matrices
            var dataMatrix = Matrix<double>.Build.DenseOfArray(data);
            var dataT = dataMatrix.Transpose(); // [Features x Samples]

            // Step 1: Build design matrix
            var design = BuildDesignMatrix(nSamples, batchLabels.ToArray(), uniqueBatches, biologicalCovariates?.ToArray());

            // Step 2: Standardize data
            var (sData, varPooled, standMean) = StandardizeData(
                dataT, design, nBatches, nPerBatch, nSamples, nFeatures);

            // Step 3: Estimate batch parameters
            var (gammaHat, deltaHat) = EstimateBatchParameters(sData, batchIndices, nBatches, nFeatures);

            // Step 4: Compute EB priors and run iterative solver
            var (gammaStar, deltaStar) = RunEmpiricalBayes(
                sData, gammaHat, deltaHat, batchIndices, nBatches, nFeatures);

            // Step 5: Adjust data
            var correctedT = AdjustData(sData, gammaStar, deltaStar, varPooled, standMean,
                batchIndices, nBatches, nFeatures, nSamples);

            // Transpose back to [Samples x Features]
            var corrected = correctedT.Transpose();

            return new ComBatResult
            {
                Success = true,
                CorrectedData = corrected.ToArray(),
                NumBatches = nBatches,
                NumSamples = nSamples,
                NumFeatures = nFeatures
            };
        }

        /// <summary>
        /// Overload accepting MathNet Matrix directly.
        /// </summary>
        public ComBatResult Apply(Matrix<double> data, int[] batchLabels, int[] biologicalCovariates = null)
        {
            return Apply(data.ToArray(), batchLabels.ToList(), biologicalCovariates?.ToList());
        }

        #region Step 1: Design Matrix

        private Matrix<double> BuildDesignMatrix(int nSamples, int[] batches, int[] uniqueBatches, int[] covariates)
        {
            int nBatches = uniqueBatches.Length;
            int nCovCols = 0;
            List<int> uniqueCovs = null;

            if (covariates != null)
            {
                uniqueCovs = covariates.Distinct().OrderBy(c => c).ToList();
                nCovCols = uniqueCovs.Count - 1; // Drop reference level to avoid collinearity
            }

            var design = Matrix<double>.Build.Dense(nSamples, nBatches + nCovCols);

            for (int i = 0; i < nSamples; i++)
            {
                // One-hot encode batch
                int batchIdx = Array.IndexOf(uniqueBatches, batches[i]);
                design[i, batchIdx] = 1.0;

                // One-hot encode covariates (skip first level as reference)
                if (covariates != null && nCovCols > 0)
                {
                    int covIdx = uniqueCovs.IndexOf(covariates[i]);
                    if (covIdx > 0)
                    {
                        design[i, nBatches + (covIdx - 1)] = 1.0;
                    }
                }
            }

            return design;
        }

        #endregion

        #region Step 2: Standardize

        private (Matrix<double> sData, Vector<double> varPooled, Matrix<double> standMean) StandardizeData(
            Matrix<double> dataT,
            Matrix<double> design,
            int nBatches,
            int[] nPerBatch,
            int nSamples,
            int nFeatures)
        {
            // Solve for regression coefficients: B_hat = (X'X)^-1 X' Y
            // Using QR decomposition for numerical stability
            // data is [Samples x Features], design is [Samples x DesignCols]
            // bHat will be [DesignCols x Features]

            var dataSxF = dataT.Transpose(); // Back to [Samples x Features]
            var bHat = design.QR().Solve(dataSxF); // [DesignCols x Features]

            // Grand mean: weighted average of batch coefficients
            var grandMean = Vector<double>.Build.Dense(nFeatures);
            for (int f = 0; f < nFeatures; f++)
            {
                double sum = 0;
                for (int b = 0; b < nBatches; b++)
                {
                    sum += ((double)nPerBatch[b] / nSamples) * bHat[b, f];
                }
                grandMean[f] = sum;
            }

            // Pooled variance from residuals
            var fitted = design * bHat; // [Samples x Features]
            var residuals = dataSxF - fitted;

            var varPooled = Vector<double>.Build.Dense(nFeatures);
            for (int f = 0; f < nFeatures; f++)
            {
                double sumSq = 0;
                for (int s = 0; s < nSamples; s++)
                {
                    sumSq += residuals[s, f] * residuals[s, f];
                }
                varPooled[f] = sumSq / nSamples;
                if (varPooled[f] < 1e-10) varPooled[f] = 1e-10;
            }

            // Compute stand_mean = grand_mean + covariate effects (batch columns zeroed)
            var designNoBatch = design.Clone();
            for (int s = 0; s < nSamples; s++)
            {
                for (int b = 0; b < nBatches; b++)
                {
                    designNoBatch[s, b] = 0;
                }
            }

            var covEffect = designNoBatch * bHat; // [Samples x Features]

            // standMean is [Features x Samples]
            var standMean = Matrix<double>.Build.Dense(nFeatures, nSamples);
            for (int f = 0; f < nFeatures; f++)
            {
                for (int s = 0; s < nSamples; s++)
                {
                    standMean[f, s] = grandMean[f] + covEffect[s, f];
                }
            }

            // Standardize: sData = (data - standMean) / sqrt(varPooled)
            var sData = Matrix<double>.Build.Dense(nFeatures, nSamples);
            for (int f = 0; f < nFeatures; f++)
            {
                double sqrtVar = Math.Sqrt(varPooled[f]);
                for (int s = 0; s < nSamples; s++)
                {
                    if (varPooled[f] < 1e-10)
                        sData[f, s] = 0; // Zero-variance feature
                    else
                        sData[f, s] = (dataT[f, s] - standMean[f, s]) / sqrtVar;
                }
            }

            return (sData, varPooled, standMean);
        }

        #endregion

        #region Step 3: Estimate Batch Parameters

        private (double[,] gammaHat, double[,] deltaHat) EstimateBatchParameters(
            Matrix<double> sData,
            List<int[]> batchIndices,
            int nBatches,
            int nFeatures)
        {
            var gammaHat = new double[nBatches, nFeatures];
            var deltaHat = new double[nBatches, nFeatures];

            for (int b = 0; b < nBatches; b++)
            {
                var indices = batchIndices[b];
                int n = indices.Length;

                for (int f = 0; f < nFeatures; f++)
                {
                    // Compute mean (gamma) and variance (delta) for this batch
                    double sum = 0;
                    for (int i = 0; i < n; i++)
                        sum += sData[f, indices[i]];
                    double mean = sum / n;
                    gammaHat[b, f] = mean;

                    if (n < 2)
                    {
                        deltaHat[b, f] = 1.0;
                        continue;
                    }

                    double sumSq = 0;
                    for (int i = 0; i < n; i++)
                    {
                        double diff = sData[f, indices[i]] - mean;
                        sumSq += diff * diff;
                    }
                    deltaHat[b, f] = sumSq / (n - 1); // Sample variance
                    if (deltaHat[b, f] < 1e-10) deltaHat[b, f] = 1e-10;
                }
            }

            return (gammaHat, deltaHat);
        }

        #endregion

        #region Step 4: Empirical Bayes

        private (double[,] gammaStar, double[,] deltaStar) RunEmpiricalBayes(
            Matrix<double> sData,
            double[,] gammaHat,
            double[,] deltaHat,
            List<int[]> batchIndices,
            int nBatches,
            int nFeatures)
        {
            var gammaStar = new double[nBatches, nFeatures];
            var deltaStar = new double[nBatches, nFeatures];

            // Parallelize across batches
            Parallel.For(0, nBatches, b =>
            {
                var indices = batchIndices[b];
                int n = indices.Length;

                // Extract batch data [Features x BatchSamples]
                var batchData = new double[nFeatures, n];
                for (int f = 0; f < nFeatures; f++)
                {
                    for (int i = 0; i < n; i++)
                    {
                        batchData[f, i] = sData[f, indices[i]];
                    }
                }

                // Compute priors from gammaHat row
                double gammaBar = 0;
                double t2 = 0;
                for (int f = 0; f < nFeatures; f++)
                    gammaBar += gammaHat[b, f];
                gammaBar /= nFeatures;

                double sumSq = 0;
                for (int f = 0; f < nFeatures; f++)
                {
                    double diff = gammaHat[b, f] - gammaBar;
                    sumSq += diff * diff;
                }
                t2 = sumSq / (nFeatures - 1);
                if (t2 < 1e-10) t2 = 1e-10;

                // Method-of-moments for inverse-gamma priors
                double deltaMean = 0;
                for (int f = 0; f < nFeatures; f++)
                    deltaMean += deltaHat[b, f];
                deltaMean /= nFeatures;

                double deltaVar = 0;
                for (int f = 0; f < nFeatures; f++)
                {
                    double diff = deltaHat[b, f] - deltaMean;
                    deltaVar += diff * diff;
                }
                deltaVar /= (nFeatures - 1);
                if (deltaVar < 1e-10) deltaVar = 1e-10;

                double aPrior = (2 * deltaVar + deltaMean * deltaMean) / deltaVar;
                double bPrior = (deltaMean * deltaVar + deltaMean * deltaMean * deltaMean) / deltaVar;

                // Run iterative EB solver (matching scanpy's _it_sol)
                var (gStar, dStar) = IterativeSolution(
                    batchData, gammaHat, deltaHat, b, n, nFeatures,
                    gammaBar, t2, aPrior, bPrior);

                for (int f = 0; f < nFeatures; f++)
                {
                    gammaStar[b, f] = gStar[f];
                    deltaStar[b, f] = dStar[f];
                }
            });

            return (gammaStar, deltaStar);
        }

        /// <summary>
        /// Iterative solver for conditional posterior means (matches scanpy's _it_sol).
        /// Updates all features together and uses relative convergence.
        /// </summary>
        private (double[] gammaStar, double[] deltaStar) IterativeSolution(
            double[,] batchData,
            double[,] gammaHat,
            double[,] deltaHat,
            int batchIndex,
            int n,
            int nFeatures,
            double gBar,
            double t2,
            double a,
            double b)
        {
            var gOld = new double[nFeatures];
            var dOld = new double[nFeatures];
            var gNew = new double[nFeatures];
            var dNew = new double[nFeatures];

            for (int f = 0; f < nFeatures; f++)
            {
                gOld[f] = gammaHat[batchIndex, f];
                dOld[f] = deltaHat[batchIndex, f];
            }

            for (int iter = 0; iter < MaxIterations; iter++)
            {
                // Update gamma (posterior mean under normal prior)
                for (int f = 0; f < nFeatures; f++)
                {
                    gNew[f] = (t2 * n * gammaHat[batchIndex, f] + dOld[f] * gBar) / (t2 * n + dOld[f]);
                }

                // Update delta (posterior mean under inverse-gamma prior)
                for (int f = 0; f < nFeatures; f++)
                {
                    double sumSq = 0;
                    for (int i = 0; i < n; i++)
                    {
                        double diff = batchData[f, i] - gNew[f];
                        sumSq += diff * diff;
                    }
                    dNew[f] = (0.5 * sumSq + b) / (n / 2.0 + a - 1.0);
                    if (dNew[f] < 1e-10) dNew[f] = 1e-10;
                }

                // Check relative convergence (matching scanpy)
                double maxGammaChange = 0;
                double maxDeltaChange = 0;
                for (int f = 0; f < nFeatures; f++)
                {
                    if (Math.Abs(gOld[f]) > 1e-10)
                    {
                        double rel = Math.Abs(gNew[f] - gOld[f]) / Math.Abs(gOld[f]);
                        if (rel > maxGammaChange) maxGammaChange = rel;
                    }
                    if (Math.Abs(dOld[f]) > 1e-10)
                    {
                        double rel = Math.Abs(dNew[f] - dOld[f]) / Math.Abs(dOld[f]);
                        if (rel > maxDeltaChange) maxDeltaChange = rel;
                    }
                }

                double change = Math.Max(maxGammaChange, maxDeltaChange);
                if (change < ConvergenceThreshold)
                    break;

                Array.Copy(gNew, gOld, nFeatures);
                Array.Copy(dNew, dOld, nFeatures);
            }

            return (gNew, dNew);
        }

        #endregion

        #region Step 5: Adjust Data

        private Matrix<double> AdjustData(
            Matrix<double> sData,
            double[,] gammaStar,
            double[,] deltaStar,
            Vector<double> varPooled,
            Matrix<double> standMean,
            List<int[]> batchIndices,
            int nBatches,
            int nFeatures,
            int nSamples)
        {
            var result = Matrix<double>.Build.Dense(nFeatures, nSamples);

            for (int b = 0; b < nBatches; b++)
            {
                var indices = batchIndices[b];

                for (int f = 0; f < nFeatures; f++)
                {
                    double gamma = gammaStar[b, f];
                    double sqrtDelta = Math.Sqrt(deltaStar[b, f]);
                    if (sqrtDelta < 1e-10) sqrtDelta = 1e-10;

                    double sqrtVarPooled = Math.Sqrt(varPooled[f]);

                    foreach (int s in indices)
                    {
                        // Adjust standardized data
                        double adjusted = (sData[f, s] - gamma) / sqrtDelta;
                        // Transform back to original scale
                        result[f, s] = adjusted * sqrtVarPooled + standMean[f, s];
                    }
                }
            }

            return result;
        }

        #endregion

        private ComBatResult Fail(string message)
        {
            return new ComBatResult { Success = false, ErrorMessage = message };
        }
    }
}