using System.Windows;
using AcEvoFfbTuner.ViewModels;

namespace AcEvoFfbTuner;

public partial class App : Application
{
    public static MainViewModel ViewModel { get; private set; } = null!;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ViewModel = new MainViewModel();
        ViewModel.Initialize();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        ViewModel.Dispose();
        base.OnExit(e);
    }
}
