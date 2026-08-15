using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
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

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateFileW(string lpFileName, uint dwDesiredAccess, uint dwShareMode,
        IntPtr lpSecurityAttributes, uint dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool WriteFile(IntPtr hFile, byte[] lpBuffer, int nNumberOfBytesToWrite,
        out uint lpNumberOfBytesWritten, IntPtr lpOverlapped);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern bool CreateDirectory(string lpPathName, IntPtr lpSecurityAttributes);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint SetFilePointer(IntPtr hFile, int lDistanceToMove, IntPtr lpDistanceToMoveHigh, uint dwMoveMethod);

    [DllImport("dbghelp.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool MiniDumpWriteDump(IntPtr process, uint processId, IntPtr file, uint dumpType,
        ref MiniDumpExceptionInformation exceptionParam, IntPtr userStreamParam, IntPtr callbackParam);

    private const uint GENERIC_WRITE = 0x40000000;
    private const uint FILE_SHARE_READ = 0x1;
    private const uint OPEN_ALWAYS = 4;
    private const uint CREATE_ALWAYS = 2;
    private const uint FILE_ATTRIBUTE_NORMAL = 0x80;
    private const uint FILE_END = 2;
    private static readonly IntPtr InvalidHandle = new(-1);

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

    /// <summary>
    /// One-time elevated setup of WER LocalDumps (HKLM) so Windows itself
    /// writes a minidump for every crash of this exe — including crashes that
    /// corrupt the process so badly the in-process native filter cannot run
    /// (the field RS50 crashes ship no crash.log/crash.dmp for exactly that
    /// reason). Dumps land in %LOCALAPPDATA%\CrashDumps and the diag pack
    /// builder ships the newest one. The installer performs the same setup,
    /// so this is a self-heal for machines where the install-time prompt was
    /// skipped or declined. The flag is only set once the keys are confirmed
    /// present, so a declined UAC retries on the next launch.
    /// </summary>
    private static void EnsureWerLocalDumps()
    {
        const string subkey = @"SOFTWARE\Microsoft\Windows\Windows Error Reporting\LocalDumps\AcEvoFfbTuner.exe";
        const string fullKey = @"HKLM\" + subkey;
        var log = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "AcEvoFfbTuner", "wer_dumps.log");

        void LogWer(string msg)
        {
            try
            {
                File.AppendAllText(log, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {msg}\n");
            }
            catch { }
        }

        try
        {
            // ALWAYS verify the keys — the flag alone is not proof (early
            // versions set it unconditionally even when the UAC was declined).
            // Missing keys = no crash dumps, so retry the one-time elevated
            // setup until it sticks.
            bool configured = false;
            try
            {
                using var k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(subkey, false);
                configured = k?.GetValue("DumpType") is int dt && dt == 1;
            }
            catch { }

            if (configured)
            {
                Settings.WerLocalDumpsConfigured = true;
                Settings.Save();
                LogWer("confirmed — WER LocalDumps active");
                return;
            }

            LogWer("keys missing — attempting one-time elevated setup");
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo("cmd.exe",
                    $"/c reg add \"{fullKey}\" /v DumpType /t REG_DWORD /d 1 /f & reg add \"{fullKey}\" /v DumpCount /t REG_DWORD /d 5 /f")
                {
                    UseShellExecute = true,
                    Verb = "runas",
                    WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden,
                    CreateNoWindow = true
                };
                using var p = System.Diagnostics.Process.Start(psi);
                p?.WaitForExit(15000);
            }
            catch (Exception ex)
            {
                LogWer($"elevated setup failed/declined — {ex.GetType().Name}: {ex.Message}");
            }

            try
            {
                using var k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(subkey, false);
                configured = k?.GetValue("DumpType") is int dt && dt == 1;
            }
            catch { }

            if (configured)
            {
                Settings.WerLocalDumpsConfigured = true;
                Settings.Save();
                LogWer("confirmed — WER LocalDumps active");
            }
            else
            {
                LogWer("not confirmed — will retry on next launch");
            }
        }
        catch (Exception ex)
        {
            try
            {
                File.AppendAllText(log, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] ERROR: {ex.GetType().Name}: {ex.Message}\n");
            }
            catch { }
        }
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

        // ── Native-only crash capture ──────────────────────────────────────
        // A corrupted heap can kill the managed filter before any managed
        // allocation succeeds (File.AppendAllText, FileStream, etc.) — which is
        // why crash.log/crash.dmp were missing from the field packs. Everything
        // below uses raw P/Invoke with no managed allocations so the dump
        // survives even a badly corrupted process.

        string? dumpDir = null;
        try { dumpDir = Path.GetDirectoryName(DumpPath); } catch { }

        IntPtr logFile = InvalidHandle, dumpFile = InvalidHandle;
        try
        {
            if (dumpDir != null)
            {
                if (dumpDir.Length > 0) CreateDirectory(dumpDir, IntPtr.Zero);
                logFile = CreateFileW(CrashLogPath, GENERIC_WRITE, FILE_SHARE_READ, IntPtr.Zero, OPEN_ALWAYS, FILE_ATTRIBUTE_NORMAL, IntPtr.Zero);
                if (logFile != InvalidHandle)
                {
                    SetFilePointer(logFile, 0, IntPtr.Zero, FILE_END);
                    string line = string.Format(CultureInfo.InvariantCulture,
                        "[{0:yyyy-MM-dd HH:mm:ss.fff}] CRASH (NativeExceptionFilter): code=0x{1:X8} address=0x{2:X}\n",
                        DateTime.Now, code, address.ToInt64());
                    byte[] bytes = Encoding.UTF8.GetBytes(line);
                    WriteFile(logFile, bytes, bytes.Length, out _, IntPtr.Zero);
                    CloseHandle(logFile);
                    logFile = InvalidHandle;
                }

                dumpFile = CreateFileW(DumpPath, GENERIC_WRITE, 0, IntPtr.Zero, CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL, IntPtr.Zero);
                if (dumpFile != InvalidHandle)
                {
                    var exInfo = new MiniDumpExceptionInformation
                    {
                        ThreadId = GetCurrentThreadId(),
                        ExceptionPointers = exceptionPointers,
                        ClientPointers = false
                    };
                    MiniDumpWriteDump(GetCurrentProcess(), GetCurrentProcessId(), dumpFile,
                        MiniDumpNormal | MiniDumpWithDataSegs | MiniDumpWithThreadInfo, ref exInfo, IntPtr.Zero, IntPtr.Zero);
                    CloseHandle(dumpFile);
                    dumpFile = InvalidHandle;
                }
            }
        }
        catch { }
        finally
        {
            if (logFile != InvalidHandle) { try { CloseHandle(logFile); } catch { } }
            if (dumpFile != InvalidHandle) { try { CloseHandle(dumpFile); } catch { } }
        }

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

        // One-time elevated WER LocalDumps setup — fire-and-forget; the UAC
        // prompt (if any) is a single user action per machine, and the flag
        // prevents repeats even if the user declines.
        Task.Run(EnsureWerLocalDumps);

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
