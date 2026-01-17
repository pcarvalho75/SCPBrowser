using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace TransmutationLearning.Services
{
    /// <summary>
    /// Handles persistence of user settings like expected distribution order
    /// </summary>
    public class SettingsService
    {
        private const string OrderFileExtension = ".distillorder.json";

        /// <summary>
        /// Gets the path for the expected order file (same directory as parquet, with .distillorder.json extension)
        /// </summary>
        public string GetExpectedOrderFilePath(string parquetPath)
        {
            if (string.IsNullOrEmpty(parquetPath))
                return null;

            return parquetPath + OrderFileExtension;
        }

        /// <summary>
        /// Saves the expected distribution order to disk
        /// </summary>
        public bool SaveExpectedOrder(string parquetPath, List<string> order)
        {
            try
            {
                string orderFilePath = GetExpectedOrderFilePath(parquetPath);
                if (string.IsNullOrEmpty(orderFilePath))
                    return false;

                if (order == null || order.Count == 0)
                    return false;

                string json = JsonSerializer.Serialize(order, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(orderFilePath, json);
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Loads saved expected order from disk if available
        /// </summary>
        public List<string> LoadExpectedOrder(string parquetPath)
        {
            try
            {
                string orderFilePath = GetExpectedOrderFilePath(parquetPath);
                if (string.IsNullOrEmpty(orderFilePath) || !File.Exists(orderFilePath))
                    return new List<string>();

                string json = File.ReadAllText(orderFilePath);
                return JsonSerializer.Deserialize<List<string>>(json) ?? new List<string>();
            }
            catch
            {
                return new List<string>();
            }
        }
    }
}
