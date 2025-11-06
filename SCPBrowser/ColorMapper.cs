using System;
using System.Windows.Media;

namespace SCPBrowser
{
    /// <summary>
    /// Provides color mapping utilities for data visualization using Viridis-like gradient
    /// </summary>
    public static class ColorMapper
    {
        /// <summary>
        /// Gets a Viridis-inspired color based on a normalized value (0-1)
        /// </summary>
        /// <param name="normalizedValue">Value between 0 and 1</param>
        /// <returns>Color from blue (0) through green to yellow (1), with gray for values near 0</returns>
        public static Color GetViridisColor(double normalizedValue)
        {
            normalizedValue = Math.Max(0, Math.Min(1, normalizedValue));

            if (normalizedValue < 0.001)
            {
                return Color.FromRgb(200, 200, 200);
            }

            byte r, g, b;

            if (normalizedValue < 0.25)
            {
                double t = normalizedValue / 0.25;
                r = (byte)(68 + t * (59 - 68));
                g = (byte)(1 + t * (82 - 1));
                b = (byte)(84 + t * (139 - 84));
            }
            else if (normalizedValue < 0.5)
            {
                double t = (normalizedValue - 0.25) / 0.25;
                r = (byte)(59 + t * (33 - 59));
                g = (byte)(82 + t * (145 - 82));
                b = (byte)(139 + t * (140 - 139));
            }
            else if (normalizedValue < 0.75)
            {
                double t = (normalizedValue - 0.5) / 0.25;
                r = (byte)(33 + t * (94 - 33));
                g = (byte)(145 + t * (201 - 145));
                b = (byte)(140 + t * (97 - 140));
            }
            else
            {
                double t = (normalizedValue - 0.75) / 0.25;
                r = (byte)(94 + t * (253 - 94));
                g = (byte)(201 + t * (231 - 201));
                b = (byte)(97 + t * (37 - 97));
            }

            return Color.FromRgb(r, g, b);
        }
    }
}