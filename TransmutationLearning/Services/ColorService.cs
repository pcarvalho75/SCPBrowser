using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Media;

namespace TransmutationLearning.Services
{
    /// <summary>
    /// Manages consistent color assignment for cell types across all charts
    /// </summary>
    public class ColorService
    {
        private readonly Dictionary<string, Color> _cellTypeColors = new Dictionary<string, Color>();

        // Soft pastel colors for pie chart and histogram
        private static readonly Color[] PastelColors = new[]
        {
            Color.FromRgb(255, 179, 186), // soft pink
            Color.FromRgb(255, 223, 186), // soft peach
            Color.FromRgb(255, 255, 186), // soft yellow
            Color.FromRgb(186, 255, 201), // soft mint
            Color.FromRgb(186, 225, 255), // soft sky
            Color.FromRgb(219, 186, 255), // soft lavender
            Color.FromRgb(255, 186, 255), // soft magenta
            Color.FromRgb(186, 255, 255), // soft cyan
            Color.FromRgb(255, 209, 186), // soft coral
            Color.FromRgb(209, 255, 186), // soft lime
            Color.FromRgb(186, 209, 255), // soft periwinkle
            Color.FromRgb(255, 186, 209), // soft rose
        };

        /// <summary>
        /// Assigns fixed colors to cell types (alphabetically sorted for consistency)
        /// </summary>
        public void AssignColors(IEnumerable<string> cellTypes)
        {
            _cellTypeColors.Clear();
            var sortedCellTypes = cellTypes.OrderBy(ct => ct).ToList();
            for (int i = 0; i < sortedCellTypes.Count; i++)
            {
                _cellTypeColors[sortedCellTypes[i]] = PastelColors[i % PastelColors.Length];
            }
        }

        /// <summary>
        /// Gets a consistent color for a cell type - uses dictionary if available, 
        /// otherwise generates deterministic color from name
        /// </summary>
        public Color GetColor(string cellType)
        {
            if (_cellTypeColors.TryGetValue(cellType, out var color))
                return color;

            // Deterministic fallback based on cell type name (not loop index)
            int hash = Math.Abs(cellType.GetHashCode());
            return PastelColors[hash % PastelColors.Length];
        }

        /// <summary>
        /// Clears all assigned colors
        /// </summary>
        public void Clear()
        {
            _cellTypeColors.Clear();
        }
    }
}
