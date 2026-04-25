using System.Windows;
using AcEvoFfbTuner.Services;
using AcEvoFfbTuner.ViewModels;
using AcEvoFfbTuner.Views;

namespace AcEvoFfbTuner;

public partial class App : Application
{
    public static MainViewModel ViewModel { get; private set; } = null!;
    public static AppSettings Settings { get; private set; } = null!;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        Settings = AppSettings.Load();

        if (Settings.SplashScreenEnabled)
        {
            var customSound = Settings.CustomStartupSoundPath;
            var splash = new Views.SplashScreen(customSound);
            splash.LoadingComplete += () =>
            {
                ViewModel = new MainViewModel();
                ViewModel.Initialize();
                ViewModel.LoadAppSettings();

                var mainWindow = new MainWindow();
                MainWindow = mainWindow;
                mainWindow.Show();
                splash.Close();
            };
            splash.Show();
        }
        else
        {
            ViewModel = new MainViewModel();
            ViewModel.Initialize();
            ViewModel.LoadAppSettings();

            var mainWindow = new MainWindow();
            MainWindow = mainWindow;
            mainWindow.Show();
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        ViewModel.Dispose();
        base.OnExit(e);
    }
}
