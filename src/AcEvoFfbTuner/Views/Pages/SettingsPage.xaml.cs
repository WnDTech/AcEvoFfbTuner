using System.Windows;
using System.Windows.Controls;
using AcEvoFfbTuner.ViewModels;

namespace AcEvoFfbTuner.Views.Pages;

public partial class SettingsPage : UserControl
{
    public event EventHandler? TestingGuideRequested;
    public event EventHandler? CalibrationWizardRequested;

    public SettingsPage()
    {
        InitializeComponent();
    }

    private void OnCopyDebugToClipboard(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel vm)
        {
            Clipboard.SetText(vm.DebugSnapshot);
            vm.StatusText = "Debug info copied to clipboard";
        }
    }

    private void OnOpenTestingGuide(object sender, RoutedEventArgs e)
    {
        TestingGuideRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OnOpenCalibrationWizard(object sender, RoutedEventArgs e)
    {
        CalibrationWizardRequested?.Invoke(this, EventArgs.Empty);
    }
}
