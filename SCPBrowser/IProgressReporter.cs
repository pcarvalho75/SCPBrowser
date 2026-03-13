using System;

namespace SCPBrowser
{
    /// <summary>
    /// Interface for reporting progress during long-running operations
    /// </summary>
    public interface IProgressReporter
    {
        void ReportMessage(string message);
        void ReportProgress(string progressDetail);
    }

    /// <summary>
    /// Simple console-based progress reporter for command-line usage
    /// </summary>
    public class ConsoleProgressReporter : IProgressReporter
    {
        public void ReportMessage(string message)
        {

        }

        public void ReportProgress(string progressDetail)
        {

        }
    }
}