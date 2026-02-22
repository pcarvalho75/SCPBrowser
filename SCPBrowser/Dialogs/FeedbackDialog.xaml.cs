using System;
using System.Windows;
using System.Windows.Controls;
using SCPBrowser.Services;

namespace SCPBrowser
{
    public partial class FeedbackDialog : Window
    {
        public FeedbackDialog()
        {
            InitializeComponent();
        }

        private void Input_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (SubmitButton == null) return;
            SubmitButton.IsEnabled =
                !string.IsNullOrWhiteSpace(NameTextBox.Text) &&
                !string.IsNullOrWhiteSpace(TitleTextBox.Text) &&
                !string.IsNullOrWhiteSpace(DescriptionTextBox.Text);
        }

        private async void Submit_Click(object sender, RoutedEventArgs e)
        {
            SubmitButton.IsEnabled = false;
            StatusText.Text = "Submitting...";
            StatusText.Foreground = new System.Windows.Media.SolidColorBrush(
                (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#64748b"));

            try
            {
                string url = await FeedbackService.SubmitFeedbackAsync(
                    TitleTextBox.Text.Trim(),
                    DescriptionTextBox.Text.Trim(),
                    NameTextBox.Text.Trim(),
                    BugRadio.IsChecked == true);

                MessageBox.Show(
                    "Thank you! Your feedback has been submitted.",
                    "Feedback Sent",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);

                DialogResult = true;
                Close();
            }
            catch (Exception ex)
            {
                StatusText.Text = $"Error: {ex.Message}";
                StatusText.Foreground = new System.Windows.Media.SolidColorBrush(
                    (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#dc2626"));
                SubmitButton.IsEnabled = true;
            }
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}
