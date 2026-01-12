using System;
using System.Windows;

namespace TransmutationLearning
{
    public partial class MainWindow : Window
    {
        public MainWindow()
        {
            InitializeComponent();
        }

        private void MainTransmutationControl_DataLoaded(object sender, EventArgs e)
        {
            var dataset = MainTransmutationControl.Dataset;
            if (dataset != null)
            {
                Console.WriteLine($"Data loaded successfully:");
                Console.WriteLine($"  - {dataset.TotalProteins:N0} proteins");
                Console.WriteLine($"  - {dataset.TotalMatchedRuns:N0} matched runs");
                Console.WriteLine($"  - {dataset.CellTypes.Count} cell types: {string.Join(", ", dataset.CellTypes)}");

                // Ready for Phase 3: Feature selection
                // The dataset and filtered data are available via:
                // - MainTransmutationControl.Dataset
                // - MainTransmutationControl.FilteredData
            }
        }
    }
}
