using System.Collections.Specialized;
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
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel vm)
        {
            vm.SystemLogEntries.CollectionChanged -= OnLogChanged;
            vm.SystemLogEntries.CollectionChanged += OnLogChanged;
        }
        DataContextChanged += (s, args) =>
        {
            if (args.OldValue is MainViewModel oldVm)
                oldVm.SystemLogEntries.CollectionChanged -= OnLogChanged;
            if (args.NewValue is MainViewModel newVm)
                newVm.SystemLogEntries.CollectionChanged += OnLogChanged;
        };
    }

    private void OnLogChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        Dispatcher.InvokeAsync(() =>
        {
            if (SystemLogList.Items.Count > 0)
                SystemLogList.ScrollIntoView(SystemLogList.Items[SystemLogList.Items.Count - 1]);
        });
    }

    private void OnSystemLogSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is ListBox lb)
            lb.UnselectAll();
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
