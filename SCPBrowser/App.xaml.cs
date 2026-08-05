using System;
using System.Configuration;
using System.Data;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Threading;

namespace SCPBrowser
{
    /// <summary>
    /// Interaction logic for App.xaml
    /// </summary>
    public partial class App : Application
    {
        /// <summary>Name of the error log written next to the open project (or under LocalAppData if none).</summary>
        private const string ErrorLogFileName = "scpbrowser-errors.log";

        /// <summary>
        /// Path of the currently open project.db. Published statically so a crash report can name the project it
        /// happened in and drop its log beside the data it describes. Null when no project is open.
        /// </summary>
        public static string? CurrentProjectPath { get; set; }

        public App()
        {
            // Initialize SQLite
            SQLitePCL.Batteries.Init();

            // Several handlers on the import/reconcile path are bare async void. Without these, an exception
            // escaping one of them tears the process down with the Windows crash dialog, so a bench scientist
            // is left with nothing to report and no way to recover.
            DispatcherUnhandledException += OnDispatcherUnhandledException;
            AppDomain.CurrentDomain.UnhandledException += OnAppDomainUnhandledException;
        }

        private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            string logPath = TryWriteErrorLog(e.Exception, "UI thread");

            MessageBox.Show(
                BuildErrorMessage(e.Exception, logPath, fatal: false),
                "SCP Browser - Unexpected Error",
                MessageBoxButton.OK,
                MessageBoxImage.Error);

            // Keep the window alive: losing the whole session (open project, loaded data, review state) is worse
            // than living with one failed action, and the message above tells the user what to re-check.
            e.Handled = true;
        }

        private void OnAppDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            // A background-thread escape cannot be swallowed - the runtime is going to terminate either way, so
            // the only job here is to leave a record behind and say where it is.
            if (e.ExceptionObject is not Exception ex) return;

            string logPath = TryWriteErrorLog(ex, "background thread");

            try
            {
                MessageBox.Show(
                    BuildErrorMessage(ex, logPath, fatal: true),
                    "SCP Browser - Fatal Error",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
            catch
            {
                // The UI may already be torn down; the log file is the deliverable in that case.
            }
        }

        private static string BuildErrorMessage(Exception ex, string logPath, bool fatal)
        {
            var sb = new StringBuilder();
            sb.AppendLine("Something went wrong inside SCP Browser.");
            sb.AppendLine();
            sb.AppendLine($"{ex.GetType().Name}: {ex.Message}");
            sb.AppendLine();
            sb.AppendLine(string.IsNullOrWhiteSpace(CurrentProjectPath)
                ? "Project: none open"
                : $"Project: {CurrentProjectPath}");
            sb.AppendLine();
            sb.AppendLine(string.IsNullOrEmpty(logPath)
                ? "The full details could NOT be written to a log file."
                : $"Full details were appended to:{Environment.NewLine}{logPath}");
            sb.AppendLine();
            sb.Append(fatal
                ? "The application has to close. Send the log file above with your bug report."
                : "The action was interrupted part-way. Re-check whatever was running before you continue, and send the log file above with your bug report.");
            return sb.ToString();
        }

        private static string TryWriteErrorLog(Exception ex, string source)
        {
            try
            {
                string logPath = ResolveErrorLogPath();

                var sb = new StringBuilder();
                sb.AppendLine("================================================================");
                sb.AppendLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  unhandled exception on {source}");
                sb.AppendLine($"Project: {(string.IsNullOrWhiteSpace(CurrentProjectPath) ? "(none open)" : CurrentProjectPath)}");
                sb.AppendLine(ex.ToString());
                sb.AppendLine();

                File.AppendAllText(logPath, sb.ToString());
                return logPath;
            }
            catch
            {
                // Logging must never itself take the app down, and an unwritable log is not worth a second dialog.
                return string.Empty;
            }
        }

        private static string ResolveErrorLogPath()
        {
            // Next to the project when one is open, so the log travels with the data it describes.
            string? projectDir = string.IsNullOrWhiteSpace(CurrentProjectPath)
                ? null
                : Path.GetDirectoryName(CurrentProjectPath);

            if (!string.IsNullOrEmpty(projectDir) && Directory.Exists(projectDir))
                return Path.Combine(projectDir, ErrorLogFileName);

            string appDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SCPBrowser");
            Directory.CreateDirectory(appDir);
            return Path.Combine(appDir, ErrorLogFileName);
        }
    }
}
