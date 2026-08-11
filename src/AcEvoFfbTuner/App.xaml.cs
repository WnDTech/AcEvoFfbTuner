using System.IO;
using System.Threading;
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

    private static Mutex? _singleInstanceMutex;
    private static bool _ownsSingleInstance;
    private static EventWaitHandle? _activateSignal;
    private Thread? _signalListenerThread;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Single-instance guard: if another copy is already running, signal it
        // and exit BEFORE any window is created — no splash/main window can ever
        // pop over a live session from a stray second launch (build tools,
        // accidental double-click, etc.).
        _singleInstanceMutex = new Mutex(true, @"Local\AcEvoFfbTuner_SingleInstance", out _ownsSingleInstance);
        if (!_ownsSingleInstance)
        {
            LogSecondInstance("duplicate launch — signaling existing instance and exiting");
            try
            {
                using var signal = EventWaitHandle.OpenExisting(@"Local\AcEvoFfbTuner_ActivateSignal");
                signal.Set();
            }
            catch { }
            Shutdown();
            return;
        }

        StartSignalListener();
        Services.WindowEventMonitor.Start();

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

    private void StartSignalListener()
    {
        _activateSignal = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\AcEvoFfbTuner_ActivateSignal");
        _signalListenerThread = new Thread(() =>
        {
            try
            {
                while (_activateSignal.WaitOne())
                {
                    Application.Current?.Dispatcher.BeginInvoke(() =>
                    {
                        LogSecondInstance("signal received from a duplicate launch");
                        if (Application.Current?.MainWindow is Views.MainWindow mw)
                            mw.ShowToast("Already Running", "AcEvoFfbTuner is already running — the duplicate instance exited.", 5000);
                    });
                }
            }
            catch { }
        })
        {
            IsBackground = true,
            Name = "SingleInstanceSignal"
        };
        _signalListenerThread.Start();
    }

    private static void LogSecondInstance(string message)
    {
        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "AcEvoFfbTuner");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "single_instance.log"),
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {message}\n");
        }
        catch { }
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

            // Version changed — purge log files from the previous version so
            // diagnostic packs only ever contain logs from the current build.
            PurgeLogsFromPreviousVersion(currentVersion, lastSeen);

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

    private static void PurgeLogsFromPreviousVersion(string currentVersion, string? lastSeen)
    {
        try
        {
            var baseDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "AcEvoFfbTuner");
            if (!Directory.Exists(baseDir)) return;

            int purged = 0;
            foreach (var file in Directory.GetFiles(baseDir, "*.log"))
            {
                // crash.log must survive version updates — it is the only record
                // of a crash from a previous build (the 21:17 crash stack was
                // lost when the purge wiped it). Same for its .fail.txt fallback.
                var name = Path.GetFileName(file);
                if (name == "crash.log") continue;
                try { File.Delete(file); purged++; } catch { }
            }
            foreach (var file in Directory.GetFiles(baseDir, "*.txt"))
            {
                if (Path.GetFileName(file) == "last_profile.txt") continue; // profile state, not a log
                if (Path.GetFileName(file) == "crash.log.fail.txt") continue; // crash fallback, see above
                try { File.Delete(file); purged++; } catch { }
            }

            try
            {
                var logPath = Path.Combine(baseDir, "update.log");
                File.AppendAllText(logPath,
                    $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] Version changed {lastSeen ?? "first-run"} -> {currentVersion}: purged {purged} log file(s) from the previous version.\n");
            }
            catch { }
        }
        catch { }
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
        _signalListenerThread = null;
        try
        {
            if (_ownsSingleInstance)
                _singleInstanceMutex?.ReleaseMutex();
        }
        catch { }
        _singleInstanceMutex?.Dispose();
        _singleInstanceMutex = null;
        _activateSignal?.Dispose();
        _activateSignal = null;
        base.OnExit(e);
    }
}
