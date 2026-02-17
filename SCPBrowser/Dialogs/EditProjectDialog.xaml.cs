using System.Windows;

namespace SCPBrowser
{
    public partial class EditProjectDialog : Window
    {
        public string ProjectName { get; private set; }
        public string ProjectDescription { get; private set; }

        public EditProjectDialog(string currentName, string currentDescription)
        {
            InitializeComponent();
            ProjectNameTextBox.Text = currentName ?? "";
            DescriptionTextBox.Text = currentDescription ?? "";
        }

        private void Input_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        {
            SaveButton.IsEnabled = !string.IsNullOrWhiteSpace(ProjectNameTextBox.Text) &&
                                   !string.IsNullOrWhiteSpace(DescriptionTextBox.Text);
        }

        private void Save_Click(object sender, RoutedEventArgs e)
        {
            ProjectName = ProjectNameTextBox.Text.Trim();
            ProjectDescription = DescriptionTextBox.Text.Trim();
            DialogResult = true;
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
        }
    }
}
