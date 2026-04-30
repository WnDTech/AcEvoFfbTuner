using System.IO;
using System.Windows;
using System.Windows.Threading;
using AcEvoFfbTuner.Services;
using AcEvoFfbTuner.ViewModels;
using AcEvoFfbTuner.Views;

namespace AcEvoFfbTuner;

public partial class App : Application
{
    private static readonly string CrashLogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "AcEvoFfbTuner", "crash.log");

    public static MainViewModel ViewModel { get; private set; } = null!;
    public static AppSettings Settings { get; private set; } = null!;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;

        Settings = AppSettings.Load();

        if (Settings.SplashScreenEnabled)
        {
            var customSound = Settings.CustomStartupSoundPath;
            var splash = new Views.SplashScreen(customSound);
            splash.LoadingComplete += () =>
            {
                try
                {
                    ShowMainWindow();
                    splash.Close();
                }
                catch (Exception ex)
                {
                    WriteCrashLog("SplashScreen.LoadingComplete", ex);
                    splash.Close();
                    ShowErrorAndShutdown(ex);
                }
            };
            splash.Show();
        }
        else
        {
            try
            {
                ShowMainWindow();
            }
            catch (Exception ex)
            {
                WriteCrashLog("OnStartup", ex);
                ShowErrorAndShutdown(ex);
            }
        }
    }

    private void ShowMainWindow()
    {
        ViewModel = new MainViewModel();
        ViewModel.Initialize();
        ViewModel.LoadAppSettings();

        var mainWindow = new MainWindow();
        MainWindow = mainWindow;
        mainWindow.Show();

        ShowWhatsNewIfNeeded();
    }

    private void ShowWhatsNewIfNeeded()
    {
        var currentVersion = Services.ChangeLogService.CurrentVersion;
        var lastSeen = Settings.LastSeenVersion;

        if (lastSeen == currentVersion)
            return;

        var entries = Services.ChangeLogService.GetEntriesSince(lastSeen);
        if (entries.Count == 0)
        {
            Settings.LastSeenVersion = currentVersion;
            Settings.Save();
            return;
        }

        var dialog = new Views.WhatsNewDialog { Owner = MainWindow };
        dialog.ShowDialog();

        Settings.LastSeenVersion = currentVersion;
        Settings.Save();
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        WriteCrashLog("DispatcherUnhandled", e.Exception);
        e.Handled = true;
        ShowErrorAndShutdown(e.Exception);
    }

    private void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception ex)
            WriteCrashLog("AppDomainUnhandled", ex);
    }

    private static void WriteCrashLog(string source, Exception ex)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(CrashLogPath)!);
            File.AppendAllText(CrashLogPath,
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] CRASH ({source}):\n" +
                $"{ex.GetType().FullName}: {ex.Message}\n" +
                $"{ex.StackTrace}\n" +
                (ex.InnerException != null ? $"--- Inner ---\n{ex.InnerException.GetType().FullName}: {ex.InnerException.Message}\n{ex.InnerException.StackTrace}\n" : "") +
                "\n");
        }
        catch { }
    }

    private static void ShowErrorAndShutdown(Exception ex)
    {
        var msg = $"AcEvoFfbTuner crashed:\n\n{ex.GetType().Name}: {ex.Message}";
        if (ex.InnerException != null)
            msg += $"\n\nInner: {ex.InnerException.GetType().Name}: {ex.InnerException.Message}";
        msg += $"\n\nCrash log: {CrashLogPath}";
        try { MessageBox.Show(msg, "AcEvoFfbTuner — Fatal Error", MessageBoxButton.OK, MessageBoxImage.Error); } catch { }
        Current.Shutdown(1);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        ViewModel?.Dispose();
        base.OnExit(e);
    }
}
