// NipalsAlgorithm.cs
using System;
using System.Threading;
using System.Threading.Tasks;

namespace PCANipals
{
    /// <summary>
    /// NIPALS (Nonlinear Iterative Partial Least Squares) algorithm for PCA.
    /// Handles missing values (NaN) using pairwise complete observations.
    ///
    /// Implementation notes:
    /// - Loadings/scores are computed using pairwise-complete observations (NaNs ignored).
    /// - Each component is extracted from the residual matrix via deflation.
    /// - Variance explained is reported as reduction in residual SSE over observed entries, relative to initial SSE.
    /// - Loadings are normalized to unit L2 norm for numerical stability.
    /// </summary>
    public static class NipalsAlgorithm
    {
        public static PcaResult Compute(
            double[,] data,
            int nComponents = 2,
            int maxIterations = 500,
            double tolerance = 1e-9,
            bool center = true,
            bool scale = false,
            int minSupportForScore = 2,
            int minSupportForLoading = 2,
            CancellationToken cancellationToken = default)
        {
            if (data == null) throw new ArgumentNullException(nameof(data));
            if (nComponents < 1) throw new ArgumentOutOfRangeException(nameof(nComponents));
            if (maxIterations < 1) throw new ArgumentOutOfRangeException(nameof(maxIterations));
            if (tolerance <= 0) throw new ArgumentOutOfRangeException(nameof(tolerance));
            if (minSupportForScore < 1) throw new ArgumentOutOfRangeException(nameof(minSupportForScore));
            if (minSupportForLoading < 1) throw new ArgumentOutOfRangeException(nameof(minSupportForLoading));

            int n = data.GetLength(0);
            int p = data.GetLength(1);
            if (n == 0 || p == 0) throw new ArgumentException("Input matrix must be non-empty.", nameof(data));

            nComponents = Math.Min(nComponents, Math.Min(n, p));

            // Work on a copy to avoid modifying original
            double[,] X = (double[,])data.Clone();

            // Support (observed counts) per row and column + missingness detection
            int[] supportPerSample = new int[n];
            int[] supportPerVariable = new int[p];
            bool hasMissing = CalculateSupportAndMissing(X, supportPerSample, supportPerVariable);

            // Force centering if missing values are present (0 is neutral only after centering)
            if (hasMissing && !center) center = true;

            // Preprocess
            double[] means = new double[p];
            double[] stds = new double[p];
            Preprocess(X, means, stds, center, scale);

            // Initial SSE (over observed entries)
            double residualSse = CalculateTotalSse(X);
            double totalSse0 = residualSse;

            // Outputs (initialized to 0 by default)
            double[,] scores = new double[n, nComponents];
            double[,] loadings = new double[p, nComponents];
            double[] varianceExplained = new double[nComponents];
            int[] iterations = new int[nComponents];
            int[,] supportPerScore = new int[n, nComponents];
            bool allConverged = true;

            // Buffers
            double[] t = new double[n];
            double[] t_old = new double[n];
            double[] p_loading = new double[p];

            // Buffers for optimized loadings accumulation (row-major pass)
            double[] numBuffer = new double[p];
            double[] denomBuffer = new double[p];
            int[] countBuffer = new int[p];

            int extracted = 0;

            // Tolerances for numerical sanity checks
            const double epsEnergy = 1e-15;
            const double epsSseIncreaseRel = 1e-10; // relative tolerance for SSE increase
            const double epsSseIncreaseAbs = 1e-12; // absolute tolerance for SSE increase

            for (int h = 0; h < nComponents; h++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                // If essentially no residual energy remains, stop extracting more components.
                if (residualSse <= epsEnergy || totalSse0 <= epsEnergy)
                {
                    iterations[h] = 0;
                    break;
                }

                bool initOk = InitializeScores(X, t, supportPerVariable, minSupportForLoading);
                if (!initOk)
                {
                    iterations[h] = 0;
                    allConverged = false; // could not initialize a valid component
                    break;
                }

                bool converged = false;
                int iter;

                for (iter = 0; iter < maxIterations; iter++)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    Array.Copy(t, t_old, n);

                    ComputeLoadingsOptimized(
                        X, t, p_loading,
                        minSupportForLoading,
                        numBuffer, denomBuffer, countBuffer);

                    double pNorm = VectorNorm2(p_loading);
                    if (pNorm <= epsEnergy || double.IsNaN(pNorm) || double.IsInfinity(pNorm))
                    {
                        converged = false;
                        break;
                    }

                    ScaleVectorInPlace(p_loading, 1.0 / pNorm);

                    ComputeScores(
                        X, p_loading, t,
                        supportPerScore, h,
                        minSupportForScore);

                    if (CheckConvergence(t, t_old, tolerance))
                    {
                        converged = true;
                        break;
                    }
                }

                iterations[h] = converged ? Math.Min(iter + 1, maxIterations) : Math.Min(iter + 1, maxIterations);
                if (!converged) allConverged = false;

                // Store component (even if not converged, caller can inspect Converged/Iterations)
                for (int i = 0; i < n; i++) scores[i, h] = t[i];
                for (int j = 0; j < p; j++) loadings[j, h] = p_loading[j];

                // Deflate + compute new residual SSE in one pass
                double sseBefore = residualSse;
                double sseAfter = DeflateAndCalculateSse(X, t, p_loading);

                // Sanity: SSE should not increase meaningfully after deflation
                double allowedIncrease = epsSseIncreaseAbs + epsSseIncreaseRel * (Math.Abs(sseBefore) + 1.0);
                if (sseAfter > sseBefore + allowedIncrease)
                {
                    // Clamp and mark as non-converged overall (pathological missingness / degeneracy)
                    sseAfter = sseBefore;
                    allConverged = false;
                }

                residualSse = sseAfter;

                double explained = sseBefore - residualSse;
                if (explained < 0.0) explained = 0.0;

                varianceExplained[h] = (totalSse0 > epsEnergy) ? explained / totalSse0 : 0.0;

                extracted++;
            }

            // For any components not extracted, ensure iterations=0 so callers can detect early stop
            for (int h = extracted; h < nComponents; h++)
            {
                if (iterations[h] == 0) continue;
                iterations[h] = 0;
                varianceExplained[h] = 0.0;
            }

            return new PcaResult
            {
                Scores = scores,
                Loadings = loadings,
                VarianceExplained = varianceExplained,
                Iterations = iterations,
                Converged = allConverged,

                Means = means,
                StandardDeviations = stds,
                SupportPerSample = supportPerSample,
                SupportPerVariable = supportPerVariable,
                SupportPerScore = supportPerScore,
                MinimumSupport = minSupportForScore,

                WasCentered = center,
                WasScaled = scale,

                ExtractedComponents = extracted,
                InitialSse = totalSse0,
                ResidualSse = residualSse
            };
        }

        public static Task<PcaResult> ComputeAsync(
            double[,] data,
            int nComponents = 2,
            int maxIterations = 500,
            double tolerance = 1e-9,
            bool center = true,
            bool scale = false,
            int minSupportForScore = 2,
            int minSupportForLoading = 2,
            CancellationToken cancellationToken = default)
        {
            return Task.Run(
                () => Compute(data, nComponents, maxIterations, tolerance, center, scale, minSupportForScore, minSupportForLoading, cancellationToken),
                cancellationToken);
        }

        #region Support + Preprocess

        private static bool CalculateSupportAndMissing(double[,] X, int[] supportPerSample, int[] supportPerVariable)
        {
            int n = X.GetLength(0);
            int p = X.GetLength(1);
            bool hasMissing = false;

            for (int i = 0; i < n; i++)
            {
                int rowCount = 0;
                for (int j = 0; j < p; j++)
                {
                    if (!double.IsNaN(X[i, j]))
                    {
                        rowCount++;
                        supportPerVariable[j]++;
                    }
                    else
                    {
                        hasMissing = true;
                    }
                }
                supportPerSample[i] = rowCount;
            }

            return hasMissing;
        }

        private static void Preprocess(double[,] X, double[] means, double[] stds, bool center, bool scale)
        {
            int n = X.GetLength(0);
            int p = X.GetLength(1);

            for (int j = 0; j < p; j++)
            {
                double sum = 0.0;
                int count = 0;

                for (int i = 0; i < n; i++)
                {
                    double v = X[i, j];
                    if (!double.IsNaN(v))
                    {
                        sum += v;
                        count++;
                    }
                }

                means[j] = count > 0 ? sum / count : 0.0;

                double std = 1.0;
                if (scale && count > 1)
                {
                    double sumSq = 0.0;
                    for (int i = 0; i < n; i++)
                    {
                        double v = X[i, j];
                        if (!double.IsNaN(v))
                        {
                            double d = v - means[j];
                            sumSq += d * d;
                        }
                    }

                    std = Math.Sqrt(sumSq / (count - 1));
                    if (std < 1e-12) std = 1.0;
                }

                stds[j] = std;

                for (int i = 0; i < n; i++)
                {
                    double v = X[i, j];
                    if (!double.IsNaN(v))
                    {
                        if (center) v -= means[j];
                        if (scale) v /= std;
                        X[i, j] = v;
                    }
                }
            }
        }

        private static double CalculateTotalSse(double[,] X)
        {
            int n = X.GetLength(0);
            int p = X.GetLength(1);
            double total = 0.0;

            for (int i = 0; i < n; i++)
            {
                for (int j = 0; j < p; j++)
                {
                    double v = X[i, j];
                    if (!double.IsNaN(v)) total += v * v;
                }
            }

            return total;
        }

        #endregion

        #region NIPALS core

        private static bool InitializeScores(double[,] X, double[] t, int[] supportPerVariable, int minSupportForLoading)
        {
            int n = X.GetLength(0);
            int p = X.GetLength(1);

            int maxCol = -1;
            double maxEnergy = double.NegativeInfinity;

            int required = Math.Max(2, minSupportForLoading);

            for (int j = 0; j < p; j++)
            {
                if (supportPerVariable[j] < required) continue;

                double sumSq = 0.0;
                int count = 0;

                for (int i = 0; i < n; i++)
                {
                    double v = X[i, j];
                    if (!double.IsNaN(v))
                    {
                        sumSq += v * v;
                        count++;
                    }
                }

                if (count <= 0) continue;

                double energy = sumSq / count;
                if (energy > maxEnergy)
                {
                    maxEnergy = energy;
                    maxCol = j;
                }
            }

            if (maxCol < 0 || maxEnergy <= 0.0)
            {
                Array.Clear(t, 0, t.Length);
                return false;
            }

            for (int i = 0; i < n; i++)
            {
                double v = X[i, maxCol];
                t[i] = double.IsNaN(v) ? 0.0 : v;
            }

            return true;
        }

        /// <summary>
        /// Cache-friendly loadings computation. Iterates rows first (row-major) and accumulates per-column sums.
        /// Support count is based on observed X entries.
        /// </summary>
        private static void ComputeLoadingsOptimized(
            double[,] X,
            double[] t,
            double[] loadings,
            int minSupportForLoading,
            double[] numBuffer,
            double[] denomBuffer,
            int[] countBuffer)
        {
            int n = X.GetLength(0);
            int p = X.GetLength(1);

            Array.Clear(numBuffer, 0, p);
            Array.Clear(denomBuffer, 0, p);
            Array.Clear(countBuffer, 0, p);

            for (int i = 0; i < n; i++)
            {
                double ti = t[i];
                double tiSq = ti * ti;

                for (int j = 0; j < p; j++)
                {
                    double x = X[i, j];
                    if (!double.IsNaN(x))
                    {
                        numBuffer[j] += x * ti;
                        denomBuffer[j] += tiSq;
                        countBuffer[j]++;
                    }
                }
            }

            for (int j = 0; j < p; j++)
            {
                if (countBuffer[j] >= minSupportForLoading && denomBuffer[j] > 1e-15)
                    loadings[j] = numBuffer[j] / denomBuffer[j];
                else
                    loadings[j] = 0.0;
            }
        }

        private static void ComputeScores(
            double[,] X,
            double[] p_loading,
            double[] scores,
            int[,] supportPerScore,
            int componentIdx,
            int minSupportForScore)
        {
            int n = X.GetLength(0);
            int p = X.GetLength(1);

            for (int i = 0; i < n; i++)
            {
                double num = 0.0;
                double denom = 0.0;
                int support = 0;

                for (int j = 0; j < p; j++)
                {
                    double x = X[i, j];
                    if (!double.IsNaN(x))
                    {
                        double pj = p_loading[j];
                        num += x * pj;
                        denom += pj * pj;
                        support++;
                    }
                }

                supportPerScore[i, componentIdx] = support;

                if (support < minSupportForScore || denom <= 1e-15)
                    scores[i] = 0.0;
                else
                    scores[i] = num / denom;
            }
        }

        /// <summary>
        /// Deflates X in-place and returns the new residual SSE (over observed entries) computed in the same pass.
        /// </summary>
        private static double DeflateAndCalculateSse(double[,] X, double[] t, double[] p_loading)
        {
            int n = X.GetLength(0);
            int p = X.GetLength(1);

            double totalSse = 0.0;

            for (int i = 0; i < n; i++)
            {
                double ti = t[i];
                bool hasScore = Math.Abs(ti) > 1e-15;

                for (int j = 0; j < p; j++)
                {
                    double x = X[i, j];
                    if (double.IsNaN(x)) continue;

                    if (hasScore)
                    {
                        double newVal = x - ti * p_loading[j];
                        X[i, j] = newVal;
                        totalSse += newVal * newVal;
                    }
                    else
                    {
                        totalSse += x * x;
                    }
                }
            }

            return totalSse;
        }

        #endregion

        #region Vector utils

        private static double VectorNorm2(double[] v)
        {
            double sum = 0.0;
            for (int i = 0; i < v.Length; i++) sum += v[i] * v[i];
            return Math.Sqrt(sum);
        }

        private static void ScaleVectorInPlace(double[] v, double scale)
        {
            for (int i = 0; i < v.Length; i++) v[i] *= scale;
        }

        private static bool CheckConvergence(double[] t, double[] t_old, double tolerance)
        {
            double normOld2 = 0.0;
            double diff2 = 0.0;
            double sum2 = 0.0;

            for (int i = 0; i < t.Length; i++)
            {
                double oldv = t_old[i];
                double v = t[i];

                normOld2 += oldv * oldv;

                double d = v - oldv;
                diff2 += d * d;

                double s = v + oldv; // sign-flip check
                sum2 += s * s;
            }

            double denom = Math.Sqrt(normOld2) + 1e-15;
            double rel = Math.Sqrt(diff2) / denom;
            double relFlip = Math.Sqrt(sum2) / denom;

            return Math.Min(rel, relFlip) < tolerance;
        }

        #endregion
    }
}
