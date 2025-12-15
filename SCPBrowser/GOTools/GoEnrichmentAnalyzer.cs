using BioTessera.GO;
using System;
using System.Collections.Generic;
using System.Linq;

namespace SCPBrowser.GOTools
{
    public class GoEnrichmentAnalyzer
    {
        private readonly GoSlimDatabase _goSlimDatabase;
        private readonly GoAnnotationDatabase _annotationDatabase;

        public GoEnrichmentAnalyzer(GoSlimDatabase goSlimDatabase, GoAnnotationDatabase annotationDatabase)
        {
            _goSlimDatabase = goSlimDatabase;
            _annotationDatabase = annotationDatabase;
        }

        public List<GoEnrichmentResult> AnalyzeEnrichment(
            List<string> detectedProteins,
            double pValueThreshold = 0.05,
            int minOverlap = 2)
        {
            var results = new List<GoEnrichmentResult>();

            // Total proteins in background (universe)
            int totalProteins = _annotationDatabase.TotalProteins;

            // Proteins in our sample
            int sampleSize = detectedProteins.Count;

            // For each GO term
            foreach (var goTermEntry in _annotationDatabase.GoTermToProteins)
            {
                var goTermId = goTermEntry.Key;
                var proteinsWithTerm = goTermEntry.Value;

                // Find overlap between detected proteins and proteins with this GO term
                var overlap = detectedProteins.Intersect(proteinsWithTerm).ToList();
                int overlapCount = overlap.Count;

                // Skip if overlap is too small
                if (overlapCount < minOverlap)
                    continue;

                // Hypergeometric test
                int k = overlapCount;                    // successes in sample
                int n = sampleSize;                      // sample size
                int K = proteinsWithTerm.Count;          // successes in background
                int N = totalProteins;                   // background size

                double pValue = CalculateHypergeometricPValue(k, n, K, N);

                // Calculate fold enrichment
                double expected = (double)n * K / N;
                double foldEnrichment = expected > 0 ? k / expected : 0;

                // Only keep significant results
                if (pValue < pValueThreshold)
                {
                    var goTerm = _goSlimDatabase.Terms.ContainsKey(goTermId)
                        ? _goSlimDatabase.Terms[goTermId]
                        : null;

                    results.Add(new GoEnrichmentResult
                    {
                        GoTermId = goTermId,
                        GoTermName = goTerm?.Name ?? "Unknown",
                        Namespace = goTerm?.Namespace ?? "Unknown",
                        ProteinsInTerm = K,
                        ProteinsInSample = n,
                        Overlap = k,
                        PValue = pValue,
                        FoldEnrichment = foldEnrichment
                    });
                }
            }

            // Sort by p-value (most significant first)
            return results.OrderBy(r => r.PValue).ToList();
        }

        private double CalculateHypergeometricPValue(int k, int n, int K, int N)
        {
            // P(X >= k) = sum from i=k to min(n,K) of hypergeometric(i; N, K, n)
            // This is the probability of seeing k or more successes

            double pValue = 0.0;
            int maxI = Math.Min(n, K);

            for (int i = k; i <= maxI; i++)
            {
                pValue += HypergeometricProbability(i, n, K, N);
            }

            return pValue;
        }

        private double HypergeometricProbability(int k, int n, int K, int N)
        {
            // P(X = k) = C(K, k) * C(N-K, n-k) / C(N, n)
            // where C(a, b) is binomial coefficient "a choose b"

            if (k > K || k > n || n - k > N - K)
                return 0.0;

            try
            {
                double logProb = LogBinomialCoefficient(K, k)
                               + LogBinomialCoefficient(N - K, n - k)
                               - LogBinomialCoefficient(N, n);

                return Math.Exp(logProb);
            }
            catch
            {
                return 0.0;
            }
        }

        private double LogBinomialCoefficient(int n, int k)
        {
            // log(C(n, k)) = log(n!) - log(k!) - log((n-k)!)
            // Using log to avoid overflow

            if (k > n || k < 0)
                return double.NegativeInfinity;

            if (k == 0 || k == n)
                return 0.0;

            // Optimize: C(n, k) = C(n, n-k), so use the smaller k
            if (k > n - k)
                k = n - k;

            double logResult = 0.0;
            for (int i = 0; i < k; i++)
            {
                logResult += Math.Log(n - i) - Math.Log(i + 1);
            }

            return logResult;
        }
    }
}