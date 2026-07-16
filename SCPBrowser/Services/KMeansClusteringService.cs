using System;
using System.Collections.Generic;
using System.Linq;

namespace SCPBrowser.Services
{
    /// <summary>
    /// Result of a k-means fit: a cluster index for every input point plus the fitted centroids and inertia.
    /// </summary>
    public sealed class KMeansResult
    {
        /// <summary>Cluster index (0-based) per input point, in input order. -1 = point had non-finite coordinates and
        /// was left unassigned.</summary>
        public int[] Assignments { get; init; } = Array.Empty<int>();

        /// <summary>Final centroids in standardized (z-scored) space, length = K.</summary>
        public double[][] Centroids { get; init; } = Array.Empty<double[]>();

        /// <summary>Within-cluster sum of squared distances (in standardized space) over the fitted points.</summary>
        public double Inertia { get; init; }

        /// <summary>The effective number of clusters actually produced (≤ requested k, clamped to the fit size).</summary>
        public int K { get; init; }
    }

    /// <summary>
    /// Self-contained k-means with k-means++ seeding (Arthur &amp; Vassilvitskii, 2007), Lloyd refinement, multiple
    /// deterministic restarts, and per-dimension standardization. No external solver dependency — the "pedigree" is the
    /// seeding algorithm and the multi-restart, not a library.
    ///
    /// The fit is performed on a subset of points (those flagged in <c>fitMask</c>, e.g. non-contaminant cells) so a few
    /// outliers don't capture whole clusters; <i>every</i> point is then assigned to its nearest final centroid, so all
    /// cells receive a label. Features are z-scored per dimension using the fit subset's mean/σ, making the result
    /// scale-invariant across axes with wildly different units (peptide count vs TIC).
    /// </summary>
    public static class KMeansClusteringService
    {
        /// <param name="points">One feature vector per point; all must share the same dimension.</param>
        /// <param name="fitMask">Per-point flag selecting which points drive the fit. Null ⇒ fit on all finite points.</param>
        /// <param name="k">Requested cluster count (≥1). Clamped to the number of distinct fit points.</param>
        /// <param name="seed">RNG seed for reproducibility.</param>
        /// <param name="restarts">Independent k-means++ restarts; the lowest-inertia solution wins.</param>
        /// <param name="maxIterations">Max Lloyd iterations per restart.</param>
        public static KMeansResult Fit(
            double[][] points,
            bool[] fitMask,
            int k,
            int seed = 42,
            int restarts = 10,
            int maxIterations = 100)
        {
            if (points == null) throw new ArgumentNullException(nameof(points));
            int n = points.Length;
            if (n == 0) return new KMeansResult { K = 0 };

            int dim = points[0]?.Length ?? 0;
            if (dim == 0) throw new ArgumentException("Points must have at least one dimension.", nameof(points));

            // Which points are usable (finite) and which drive the fit.
            bool IsFinite(double[] p) => p != null && p.Length == dim && p.All(v => !double.IsNaN(v) && !double.IsInfinity(v));
            var fitIdx = new List<int>();
            for (int i = 0; i < n; i++)
            {
                if (!IsFinite(points[i])) continue;
                if (fitMask == null || (i < fitMask.Length && fitMask[i]))
                    fitIdx.Add(i);
            }
            // Degenerate: nothing to fit on → fall back to all finite points, else single trivial cluster.
            if (fitIdx.Count == 0)
            {
                for (int i = 0; i < n; i++) if (IsFinite(points[i])) fitIdx.Add(i);
            }
            if (fitIdx.Count == 0)
                return new KMeansResult { Assignments = Enumerable.Repeat(-1, n).ToArray(), K = 0 };

            // --- Standardize per dimension using fit-subset statistics ---
            var mean = new double[dim];
            var std = new double[dim];
            foreach (int i in fitIdx)
                for (int j = 0; j < dim; j++) mean[j] += points[i][j];
            for (int j = 0; j < dim; j++) mean[j] /= fitIdx.Count;
            foreach (int i in fitIdx)
                for (int j = 0; j < dim; j++) { double dv = points[i][j] - mean[j]; std[j] += dv * dv; }
            for (int j = 0; j < dim; j++)
            {
                std[j] = Math.Sqrt(std[j] / fitIdx.Count);
                if (std[j] < 1e-12) std[j] = 1.0; // constant dimension → leave unscaled
            }

            double[] Z(int i)
            {
                var z = new double[dim];
                for (int j = 0; j < dim; j++) z[j] = (points[i][j] - mean[j]) / std[j];
                return z;
            }

            var fitZ = fitIdx.Select(Z).ToArray();

            // Clamp k to [1, #distinct fit points].
            int distinct = fitZ.Select(z => string.Join(",", z)).Distinct().Count();
            int kEff = Math.Max(1, Math.Min(k, distinct));

            // Trivial single cluster.
            if (kEff == 1)
            {
                var centroid = new double[dim];
                foreach (var z in fitZ) for (int j = 0; j < dim; j++) centroid[j] += z[j];
                for (int j = 0; j < dim; j++) centroid[j] /= fitZ.Length;
                double inertia1 = fitZ.Sum(z => SqDist(z, centroid));
                var assign1 = new int[n];
                for (int i = 0; i < n; i++) assign1[i] = IsFinite(points[i]) ? 0 : -1;
                return new KMeansResult { Assignments = assign1, Centroids = new[] { centroid }, Inertia = inertia1, K = 1 };
            }

            double[][] bestCentroids = null;
            double bestInertia = double.PositiveInfinity;

            for (int r = 0; r < restarts; r++)
            {
                var rng = new Random(seed + r);
                var centroids = KMeansPlusPlusInit(fitZ, kEff, rng);
                var labels = new int[fitZ.Length];

                for (int iter = 0; iter < maxIterations; iter++)
                {
                    bool changed = false;

                    // Assignment step.
                    for (int i = 0; i < fitZ.Length; i++)
                    {
                        int best = NearestCentroid(fitZ[i], centroids);
                        if (best != labels[i]) { labels[i] = best; changed = true; }
                    }

                    // Update step.
                    var sums = new double[kEff][];
                    var counts = new int[kEff];
                    for (int c = 0; c < kEff; c++) sums[c] = new double[dim];
                    for (int i = 0; i < fitZ.Length; i++)
                    {
                        counts[labels[i]]++;
                        for (int j = 0; j < dim; j++) sums[labels[i]][j] += fitZ[i][j];
                    }
                    for (int c = 0; c < kEff; c++)
                    {
                        if (counts[c] == 0)
                        {
                            // Empty cluster: reseed to the point farthest from its current centroid.
                            int far = FarthestPoint(fitZ, centroids, labels);
                            centroids[c] = (double[])fitZ[far].Clone();
                            changed = true;
                        }
                        else
                        {
                            for (int j = 0; j < dim; j++) centroids[c][j] = sums[c][j] / counts[c];
                        }
                    }

                    if (!changed) break;
                }

                double inertia = 0;
                for (int i = 0; i < fitZ.Length; i++) inertia += SqDist(fitZ[i], centroids[labels[i]]);
                if (inertia < bestInertia)
                {
                    bestInertia = inertia;
                    bestCentroids = centroids.Select(c => (double[])c.Clone()).ToArray();
                }
            }

            // Assign ALL points (including non-fit and, if finite, previously-excluded) to nearest final centroid.
            var assignments = new int[n];
            for (int i = 0; i < n; i++)
                assignments[i] = IsFinite(points[i]) ? NearestCentroid(Z(i), bestCentroids) : -1;

            return new KMeansResult
            {
                Assignments = assignments,
                Centroids = bestCentroids,
                Inertia = bestInertia,
                K = kEff
            };
        }

        // k-means++ seeding: first centroid uniform at random, each subsequent chosen with probability ∝ D(x)².
        private static double[][] KMeansPlusPlusInit(double[][] data, int k, Random rng)
        {
            int dim = data[0].Length;
            var centroids = new double[k][];
            centroids[0] = (double[])data[rng.Next(data.Length)].Clone();

            var d2 = new double[data.Length];
            for (int c = 1; c < k; c++)
            {
                double total = 0;
                for (int i = 0; i < data.Length; i++)
                {
                    double nearest = double.PositiveInfinity;
                    for (int cc = 0; cc < c; cc++)
                    {
                        double dist = SqDist(data[i], centroids[cc]);
                        if (dist < nearest) nearest = dist;
                    }
                    d2[i] = nearest;
                    total += nearest;
                }

                if (total <= 0)
                {
                    // All remaining points coincide with chosen centroids — pad with a random point.
                    centroids[c] = (double[])data[rng.Next(data.Length)].Clone();
                    continue;
                }

                double target = rng.NextDouble() * total;
                double cum = 0;
                int pick = data.Length - 1;
                for (int i = 0; i < data.Length; i++)
                {
                    cum += d2[i];
                    if (cum >= target) { pick = i; break; }
                }
                centroids[c] = (double[])data[pick].Clone();
            }
            return centroids;
        }

        private static int NearestCentroid(double[] point, double[][] centroids)
        {
            int best = 0;
            double bestDist = double.PositiveInfinity;
            for (int c = 0; c < centroids.Length; c++)
            {
                double dist = SqDist(point, centroids[c]);
                if (dist < bestDist) { bestDist = dist; best = c; }
            }
            return best;
        }

        private static int FarthestPoint(double[][] data, double[][] centroids, int[] labels)
        {
            int far = 0;
            double worst = -1;
            for (int i = 0; i < data.Length; i++)
            {
                double dist = SqDist(data[i], centroids[labels[i]]);
                if (dist > worst) { worst = dist; far = i; }
            }
            return far;
        }

        private static double SqDist(double[] a, double[] b)
        {
            double s = 0;
            for (int j = 0; j < a.Length; j++) { double d = a[j] - b[j]; s += d * d; }
            return s;
        }
    }
}
