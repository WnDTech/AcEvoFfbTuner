using System.Windows;

namespace AcEvoFfbTuner.Views;

/// <summary>Press-to-detect pedal setup: big live bars plus per-pedal
/// Detect / Undo / Clear. DataContext is the shared MainViewModel.</summary>
public partial class PedalDetectDialog : Window
{
    public PedalDetectDialog()
    {
        InitializeComponent();
    }

    private void OnCloseClick(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }
}
