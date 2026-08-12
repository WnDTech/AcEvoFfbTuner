using System.Windows;

namespace AcEvoFfbTuner.Views;

public partial class ShareProfileDialog : Window
{
    public string TitleText
    {
        get => TitleTextBox.Text;
        set => TitleTextBox.Text = value;
    }

    public string DescriptionText
    {
        get => DescriptionTextBox.Text;
        set => DescriptionTextBox.Text = value;
    }

    public string AuthorText
    {
        get => AuthorTextBox.Text;
        set => AuthorTextBox.Text = value;
    }

    public string PreviewText
    {
        get => PreviewTextBlock.Text;
        set => PreviewTextBlock.Text = value;
    }

    public ShareProfileDialog()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            TitleTextBox.Focus();
            TitleTextBox.SelectAll();
        };
    }

    private void OnOkClick(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(TitleText))
        {
            StatusText(OkButton, "Enter a title");
            return;
        }
        if (TitleText.Length > 128)
        {
            StatusText(OkButton, "Title too long (max 128)");
            return;
        }
        if (string.IsNullOrWhiteSpace(AuthorText))
        {
            StatusText(OkButton, "Enter an author name");
            return;
        }
        if (AuthorText.Length > 64)
        {
            StatusText(OkButton, "Author too long (max 64)");
            return;
        }
        DialogResult = true;
        Close();
    }

    private void StatusText(System.Windows.Controls.Button button, string text)
    {
        var original = button.Content;
        button.Content = text;
        button.IsEnabled = false;
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = System.TimeSpan.FromSeconds(1.5) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            button.IsEnabled = true;
            button.Content = original;
        };
        timer.Start();
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
