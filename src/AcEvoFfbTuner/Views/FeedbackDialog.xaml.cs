using System.Windows;

namespace AcEvoFfbTuner.Views;

public partial class FeedbackDialog : Window
{
    public string Feedback { get; private set; } = "";

    public FeedbackDialog()
    {
        InitializeComponent();
        FeedbackBox.Focus();
    }

    private void OnSend(object sender, RoutedEventArgs e)
    {
        var text = FeedbackBox.Text?.Trim();
        if (string.IsNullOrWhiteSpace(text))
        {
            FeedbackBox.Focus();
            return;
        }

        Feedback = text;
        DialogResult = true;
        Close();
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
