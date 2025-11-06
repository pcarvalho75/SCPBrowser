using System.IO;
using System.Windows;
using Microsoft.Win32;

namespace SCPBrowser
{
    public partial class TranscriptomicConverterDialog : Window
    {
        public string ExpressionFilePath { get; private set; }
        public string MetadataFilePath { get; private set; }
        public string OutputDirectory { get; private set; }

        public TranscriptomicConverterDialog()
        {
            InitializeComponent();

            // Default to ReferenceData folder in application directory
            var appDir = AppDomain.CurrentDomain.BaseDirectory;
            var defaultOutput = Path.Combine(appDir, "ReferenceData");
            OutputDirectoryTextBox.Text = defaultOutput;
            OutputDirectory = defaultOutput;
        }

        private void BrowseExpression_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFileDialog
            {
                Filter = "TSV files (*.tsv)|*.tsv|Text files (*.txt)|*.txt|All files (*.*)|*.*",
                Title = "Select Gene Expression Matrix TSV File"
            };

            if (dialog.ShowDialog() == true)
            {
                ExpressionFileTextBox.Text = dialog.FileName;
                ExpressionFilePath = dialog.FileName;

                // Auto-suggest output directory based on input file location
                if (string.IsNullOrEmpty(MetadataFilePath))
                {
                    var inputDir = Path.GetDirectoryName(dialog.FileName);
                    OutputDirectoryTextBox.Text = inputDir;
                    OutputDirectory = inputDir;
                }
            }
        }

        private void BrowseMetadata_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFileDialog
            {
                Filter = "TSV files (*.tsv)|*.tsv|Text files (*.txt)|*.txt|All files (*.*)|*.*",
                Title = "Select Cell Metadata TSV File"
            };

            if (dialog.ShowDialog() == true)
            {
                MetadataFileTextBox.Text = dialog.FileName;
                MetadataFilePath = dialog.FileName;
            }
        }

        private void BrowseOutput_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFileDialog
            {
                Filter = "All files (*.*)|*.*",
                Title = "Select any file in the output directory (we'll use its folder)",
                CheckFileExists = false,
                FileName = "Select Folder"
            };

            if (dialog.ShowDialog() == true)
            {
                var directory = Path.GetDirectoryName(dialog.FileName);
                OutputDirectoryTextBox.Text = directory;
                OutputDirectory = directory;
            }
        }

        private void OutputDirectory_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        {
            OutputDirectory = OutputDirectoryTextBox.Text;
        }

        private void Convert_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(ExpressionFilePath))
            {
                MessageBox.Show("Please select a gene expression matrix file.", "Validation Error",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (string.IsNullOrEmpty(MetadataFilePath))
            {
                MessageBox.Show("Please select a cell metadata file.", "Validation Error",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (string.IsNullOrEmpty(OutputDirectory))
            {
                MessageBox.Show("Please select an output directory.", "Validation Error",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            DialogResult = true;
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
        }
    }
}