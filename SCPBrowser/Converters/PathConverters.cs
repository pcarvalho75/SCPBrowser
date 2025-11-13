using System;
using System.Globalization;
using System.IO;
using System.Windows.Data;

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
}