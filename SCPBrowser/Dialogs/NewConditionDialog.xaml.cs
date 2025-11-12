using System.Windows;
using System.Windows.Controls;

namespace SCPBrowser
{
    public partial class NewConditionDialog : Window
    {
        public string ConditionName { get; private set; }

        public NewConditionDialog()
        {
            InitializeComponent();
        }

        // Event handler for when text changes in the condition name field
        private void Input_Changed(object sender, TextChangedEventArgs e)
        {
            // Enable the Add button only if the name field has content
            if (ConditionNameTextBox != null && AddButton != null)
            {
                AddButton.IsEnabled = !string.IsNullOrWhiteSpace(ConditionNameTextBox.Text);
            }
        }

        // Event handler for the Add button
        private void Add_Click(object sender, RoutedEventArgs e)
        {
            // Validate input
            if (string.IsNullOrWhiteSpace(ConditionNameTextBox.Text))
            {
                MessageBox.Show("Please enter a condition name.", "Validation Error",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // Store the value
            ConditionName = ConditionNameTextBox.Text.Trim();

            // Set dialog result and close
            DialogResult = true;
            Close();
        }

        // Event handler for the Cancel button
        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            // Set dialog result to false and close
            DialogResult = false;
            Close();
        }
    }
}