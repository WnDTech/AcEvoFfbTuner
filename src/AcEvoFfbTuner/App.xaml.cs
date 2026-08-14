using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using AcEvoFfbTuner.Core.FfbProviders;
using AcEvoFfbTuner.Services;
using AcEvoFfbTuner.ViewModels;
using AcEvoFfbTuner.Views;

namespace AcEvoFfbTuner;

public partial class App : Application
{
    private static readonly string CrashLogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "AcEvoFfbTuner", "crash.log");

    private static readonly string DumpPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "AcEvoFfbTuner", "crash.dmp");

    // ── Native crash capture ────────────────────────────────────────────────
    // Managed exception handlers do NOT run for access violations / native
    // crashes — the field "crash and disappeared" reports left NO crash.log at
    // all. A native unhandled-exception filter records the exception code and
    // writes a minidump, so the next diagnostic pack contains the faulting
    // module and stack instead of nothing.

    [StructLayout(LayoutKind.Sequential)]
    private struct MiniDumpExceptionInformation
    {
        public uint ThreadId;
        public IntPtr ExceptionPointers;
        [MarshalAs(UnmanagedType.Bool)] public bool ClientPointers;
    }

    private delegate int UnhandledExceptionFilter(IntPtr exceptionPointers);

    [DllImport("kernel32.dll")]
    private static extern IntPtr SetUnhandledExceptionFilter(IntPtr filter);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentProcessId();

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("dbghelp.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool MiniDumpWriteDump(IntPtr process, uint processId, IntPtr file, uint dumpType,
        IntPtr exceptionParam, IntPtr userStreamParam, IntPtr callbackParam);

    private const uint MiniDumpNormal = 0x00000000;
    private const uint MiniDumpWithDataSegs = 0x00000002;
    private const uint MiniDumpWithThreadInfo = 0x00001000;

    private static readonly UnhandledExceptionFilter _nativeFilter = NativeExceptionFilter;
    private static int _inNativeFilter;

    private static void InstallNativeCrashFilter()
    {
        try
        {
            SetUnhandledExceptionFilter(Marshal.GetFunctionPointerForDelegate(_nativeFilter));
        }
        catch { }
    }

    private static int NativeExceptionFilter(IntPtr exceptionPointers)
    {
        if (Interlocked.Exchange(ref _inNativeFilter, 1) != 0)
            return 0; // EXCEPTION_CONTINUE_SEARCH

        uint code = 0;
        IntPtr address = IntPtr.Zero;
        if (exceptionPointers != IntPtr.Zero)
        {
            IntPtr record = Marshal.ReadIntPtr(exceptionPointers); // EXCEPTION_POINTERS->ExceptionRecord
            if (record != IntPtr.Zero)
            {
                code = (uint)Marshal.ReadInt32(record);            // ExceptionCode
                address = Marshal.ReadIntPtr(record, IntPtr.Size == 8 ? 16 : 12); // ExceptionAddress
            }
        }

        try
        {
            File.AppendAllText(CrashLogPath,
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] CRASH (NativeExceptionFilter): code=0x{code:X8} address=0x{address.ToInt64():X}\n");
        }
        catch { }

        try
        {
            var dir = Path.GetDirectoryName(DumpPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            using var fs = new FileStream(DumpPath, FileMode.Create, FileAccess.Write, FileShare.None);
            var exInfo = new MiniDumpExceptionInformation
            {
                ThreadId = GetCurrentThreadId(),
                ExceptionPointers = exceptionPointers,
                ClientPointers = false
            };
            IntPtr exInfoPtr = Marshal.AllocHGlobal(Marshal.SizeOf<MiniDumpExceptionInformation>());
            try
            {
                Marshal.StructureToPtr(exInfo, exInfoPtr, false);
                MiniDumpWriteDump(GetCurrentProcess(), GetCurrentProcessId(), fs.SafeFileHandle.DangerousGetHandle(),
                    MiniDumpNormal | MiniDumpWithDataSegs | MiniDumpWithThreadInfo, exInfoPtr, IntPtr.Zero, IntPtr.Zero);
            }
            finally
            {
                Marshal.FreeHGlobal(exInfoPtr);
            }
        }
        catch { }

        // Best-effort TrueForce session-end handshake (init packets 67/68)
        // BEFORE WER terminates the process — a crash while the stream is
        // engaged otherwise latches the wheel's TrueForce engine (next
        // session opens but never streams, power cycle required).
        try
        {
            LogitechTrueForceProvider.EmergencyTeardown();
        }
        catch { }

        return 0; // EXCEPTION_CONTINUE_SEARCH — let WER terminate the process as before
    }

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
        InstallNativeCrashFilter();

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

            // The app auto-connects BEFORE this purge runs — only delete files
            // written by the PREVIOUS version. Anything newer than the app's own
            // start time is this session's evidence (connection_debug.log,
            // logitech_trueforce.log, ...) and must survive — otherwise every
            // update wipes the connect logs the user then sends in the pack.
            DateTime appStart = System.Diagnostics.Process.GetCurrentProcess().StartTime;

            int purged = 0;
            foreach (var file in Directory.GetFiles(baseDir, "*.log"))
            {
                // crash.log must survive version updates — it is the only record
                // of a crash from a previous build (the 21:17 crash stack was
                // lost when the purge wiped it). Same for its .fail.txt fallback.
                var name = Path.GetFileName(file);
                if (name == "crash.log") continue;
                if (new FileInfo(file).LastWriteTime > appStart) continue; // current-session evidence
                try { File.Delete(file); purged++; } catch { }
            }
            foreach (var file in Directory.GetFiles(baseDir, "*.txt"))
            {
                if (Path.GetFileName(file) == "last_profile.txt") continue; // profile state, not a log
                if (Path.GetFileName(file) == "crash.log.fail.txt") continue; // crash fallback, see above
                if (new FileInfo(file).LastWriteTime > appStart) continue; // current-session evidence
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
            LogitechTrueForceProvider.EmergencyTeardown();
        }
        catch { }
        e.Handled = true;
        ShowErrorAndShutdown(e.Exception);
    }

    private void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        try
        {
            if (e.ExceptionObject is Exception ex)
                WriteCrashLog("AppDomainUnhandled", ex);
            LogitechTrueForceProvider.EmergencyTeardown();
        }
        catch { }
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
        try { LogitechTrueForceProvider.EmergencyTeardown(); } catch { }
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
