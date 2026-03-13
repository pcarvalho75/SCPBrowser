using System;
using System.IO;
using System.Windows.Media.Imaging;

namespace SCPBrowser
{
    /// <summary>
    /// Handles loading of images for proteomics runs
    /// </summary>
    public static class ImageLoader
    {
        /// <summary>
        /// Loads an image from the specified path
        /// </summary>
        /// <param name="imagePath">Full path to the image file</param>
        /// <returns>BitmapImage if successful, null otherwise</returns>
        public static BitmapImage LoadImage(string imagePath)
        {
            if (string.IsNullOrEmpty(imagePath) || !File.Exists(imagePath))
                return null;

            try
            {
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.UriSource = new Uri(imagePath);
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.EndInit();
                return bitmap;
            }
            catch (Exception ex)
            {

                return null;
            }
        }

        /// <summary>
        /// Gets the expected image path for a run name
        /// For now, this is a placeholder - you'll need to implement the actual logic
        /// based on how your images are named and stored
        /// </summary>
        /// <param name="baseDirectory">Base directory containing images</param>
        /// <param name="runName">Name of the run</param>
        /// <returns>Full path to the image file, or null if not found</returns>
        public static string GetImagePathForRun(string baseDirectory, string runName)
        {
            if (string.IsNullOrEmpty(baseDirectory) || string.IsNullOrEmpty(runName))
                return null;

            // TODO: Implement actual image path resolution logic based on your naming convention
            // For example, if images are named like: runName.png or runName_preview.png

            // Try common image extensions
            string[] extensions = { ".png", ".jpg", ".jpeg", ".bmp", ".tif", ".tiff" };

            foreach (var ext in extensions)
            {
                string path = Path.Combine(baseDirectory, runName + ext);
                if (File.Exists(path))
                    return path;

                // Also try with _preview suffix
                path = Path.Combine(baseDirectory, runName + "_preview" + ext);
                if (File.Exists(path))
                    return path;
            }

            return null;
        }
    }
}