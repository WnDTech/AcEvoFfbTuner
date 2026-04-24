using System.Windows;

namespace AcEvoFfbTuner.Views;

public partial class InputDialog : Window
{
    public string? Result { get; private set; }

    public InputDialog(string defaultValue = "")
    {
        InitializeComponent();
        InputTextBox.Text = defaultValue;
        InputTextBox.Focus();
        InputTextBox.SelectAll();
    }

    private void OnKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Enter)
        {
            OnOkClick(sender, e);
        }
    }

    private void OnOkClick(object sender, RoutedEventArgs e)
    {
        Result = InputTextBox.Text?.Trim();
        if (!string.IsNullOrWhiteSpace(Result))
        {
            DialogResult = true;
            Close();
        }
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
