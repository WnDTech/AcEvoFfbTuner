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

        ThemeManager.ApplyTheme(Settings.ThemeName);

        if (Settings.SplashScreenEnabled)
        {
            var customSound = Settings.CustomStartupSoundPath;
            var splash = new Views.SplashScreen(customSound);
            splash.LoadingComplete += () =>
            {
                try
                {
                    ShowMainWindow(showWhatsNew: false);
                    splash.Close();
                    ShowWhatsNewIfNeeded();
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
                ShowMainWindow(showWhatsNew: true);
            }
            catch (Exception ex)
            {
                WriteCrashLog("OnStartup", ex);
                ShowErrorAndShutdown(ex);
            }
        }
    }

    private void ShowMainWindow(bool showWhatsNew = true)
    {
        ViewModel = new MainViewModel();
        ViewModel.Initialize();
        ViewModel.LoadAppSettings();

        var mainWindow = new MainWindow();

        if (Settings.StartMinimised)
        {
            mainWindow.WindowState = WindowState.Minimized;
        }

        MainWindow = mainWindow;
        mainWindow.Show();

        if (Settings.AutoConnect || Settings.AutoStart)
        {
            ViewModel.ApplyStartupActions();
        }

        if (showWhatsNew)
            ShowWhatsNewIfNeeded();
    }

    private void ShowWhatsNewIfNeeded()
    {
        _ = ShowWhatsNewIfNeededAsync();
    }

    private async Task ShowWhatsNewIfNeededAsync()
    {
        try
        {
            await Services.ChangeLogService.InitializeAsync();

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
        catch
        {
        }
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        try
        {
            WriteCrashLog("DispatcherUnhandled", e.Exception);
        }
        catch { }
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
            var dir = Path.GetDirectoryName(CrashLogPath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            var typeName = ex?.GetType().FullName ?? "null";
            var message = ex?.Message ?? "null";
            var stackTrace = ex?.StackTrace ?? "null";
            string inner = "";
            if (ex?.InnerException != null)
            {
                inner = $"--- Inner ---\n{ex.InnerException.GetType().FullName}: {ex.InnerException.Message}\n{ex.InnerException.StackTrace ?? "null"}\n";
            }
            File.AppendAllText(CrashLogPath,
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] CRASH ({source}):\n" +
                $"{typeName}: {message}\n" +
                $"{stackTrace}\n" +
                inner +
                "\n");
        }
        catch (Exception logEx)
        {
            try
            {
                File.WriteAllText(CrashLogPath + ".fail.txt",
                    $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] WriteCrashLog failed:\n{logEx}\n" +
                    $"Original exception type: {ex?.GetType().FullName}\n" +
                    $"Original message: {ex?.Message}\n");
            }
            catch { }
        }
    }

    private static void ShowErrorAndShutdown(Exception ex)
    {
        try
        {
            var msg = $"AcEvoFfbTuner crashed:\n\n{ex?.GetType().Name ?? "unknown"}: {ex?.Message ?? "no message"}";
            if (ex?.InnerException != null)
                msg += $"\n\nInner: {ex.InnerException.GetType().Name}: {ex.InnerException.Message}";
            msg += $"\n\nCrash log: {CrashLogPath}";
            try { MessageBox.Show(msg, "AcEvoFfbTuner — Fatal Error", MessageBoxButton.OK, MessageBoxImage.Error); } catch { }
        }
        catch { }
        try { Application.Current?.Shutdown(1); } catch { }
        try { Environment.Exit(1); } catch { }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        ViewModel?.Dispose();
        base.OnExit(e);
    }
}
