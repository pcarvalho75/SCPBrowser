using System;
using System.Globalization;
using System.IO;
using System.Windows.Data;
using System.Windows.Media;

namespace SCPBrowser.Converters
{
    /// <summary>
    /// Converts a full file path to just the file name
    /// </summary>
    public class FileNameConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is string path && !string.IsNullOrEmpty(path))
            {
                try
                {
                    // Get just the file name (e.g., "project.db")
                    return Path.GetFileName(path);
                }
                catch
                {
                    return path;
                }
            }
            return string.Empty;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

    /// <summary>
    /// Converts a full file path to just the directory path
    /// </summary>
    public class DirectoryConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is string path && !string.IsNullOrEmpty(path))
            {
                try
                {
                    // Get the directory path
                    var directory = Path.GetDirectoryName(path);
                    return string.IsNullOrEmpty(directory) ? path : directory;
                }
                catch
                {
                    return path;
                }
            }
            return string.Empty;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

    /// <summary>
    /// Converts a PriorWeight value to a green-tinted background brush.
    /// 0.0 = white, 1.0 = moderate green, higher = deeper green (capped at ~2.0).
    /// </summary>
    public class PriorWeightToGreenConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is double weight && weight > 0.001)
            {
                // Scale: 0 → white, 1.0 → moderate green, ≥2.0 → deep green
                // Use sqrt to spread the range nicely for small values
                double ratio = Math.Min(Math.Sqrt(weight) / Math.Sqrt(2.0), 1.0);

                byte r = (byte)(255 - (int)(ratio * 130)); // 255 → 125
                byte g = (byte)(255 - (int)(ratio * 25));   // 255 → 230
                byte b = (byte)(255 - (int)(ratio * 130)); // 255 → 125
                return new SolidColorBrush(Color.FromRgb(r, g, b));
            }
            return new SolidColorBrush(Colors.White);
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}