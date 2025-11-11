using System;
using System.Windows;
using SCPBrowser.Models;

namespace SCPBrowser
{
    public partial class NewPlateDialog : Window
    {
        public PlateInfo PlateInfo { get; private set; }

        public NewPlateDialog()
        {
            InitializeComponent();
        }

        private void Input_Changed(object sender, System.Windows.Controls.TextChangedEventArgs e)
        {
            ValidateInputs();
        }

        private void ValidateInputs()
        {
            bool isValid = !string.IsNullOrWhiteSpace(PlateNameTextBox.Text);
            CreateButton.IsEnabled = isValid;
        }

        private void Create_Click(object sender, RoutedEventArgs e)
        {
            PlateInfo = new PlateInfo
            {
                PlateName = PlateNameTextBox.Text.Trim(),
                RunDate = RunDatePicker.SelectedDate?.ToString("yyyy-MM-dd") ?? "",
                BiologicalCondition = BiologicalConditionTextBox.Text.Trim(),
                InstrumentName = InstrumentNameTextBox.Text.Trim(),
                OperatorName = OperatorNameTextBox.Text.Trim(),
                BatchNumber = BatchNumberTextBox.Text.Trim(),
                Description = DescriptionTextBox.Text.Trim()
            };

            DialogResult = true;
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
        }
    }
}