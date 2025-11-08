using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace SCPBrowser.GOTools
{
    public class GoEnrichmentResult
    {
        public string GoTermId { get; set; }
        public string GoTermName { get; set; }
        public string Namespace { get; set; }
        public int ProteinsInTerm { get; set; }           // K: proteins annotated to this term in background
        public int ProteinsInSample { get; set; }         // n: total proteins in our sample
        public int Overlap { get; set; }                  // k: proteins in sample that have this term
        public double PValue { get; set; }
        public double FoldEnrichment { get; set; }

        public bool IsSignificant(double threshold = 0.05) => PValue < threshold;
    }
}
