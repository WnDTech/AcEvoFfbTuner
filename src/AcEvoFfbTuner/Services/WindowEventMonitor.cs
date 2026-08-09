using System.Diagnostics;
using System.IO;
using System.Management;
using System.Runtime.InteropServices;
using System.Text;

namespace AcEvoFfbTuner.Services;

/// <summary>
/// Hooks Windows events (window create/show/destroy/foreground) and logs every
/// visible window that appears — including windows that never take focus, which
/// the foreground-sampling watcher in MainWindow can miss. Used to identify the
/// mystery popup windows that flash over the game while this app is running.
/// Logs to %APPDATA%\AcEvoFfbTuner\window_events.log.
/// </summary>
public static class WindowEventMonitor
{
    private const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
    private const uint EVENT_OBJECT_CREATE = 0x8000;
    private const uint EVENT_OBJECT_DESTROY = 0x8001;
    private const uint EVENT_OBJECT_SHOW = 0x8002;
    private const uint EVENT_OBJECT_HIDE = 0x8003;
    private const uint WINEVENT_OUTOFCONTEXT = 0x0000;
    private const int OBJID_WINDOW = 0x00000000;

    private static readonly WinEventDelegate _delegate = OnWinEvent;
    private static IntPtr _hook;
    private static Thread? _thread;
    private static ManagementEventWatcher? _processWatcher;
    private static readonly Dictionary<long, long> _recent = new();
    private static readonly object _sync = new();

    private static string LogPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "AcEvoFfbTuner", "window_events.log");

    public static void Start()
    {
        if (_thread != null) return;
        _thread = new Thread(ThreadProc)
        {
            IsBackground = true,
            Name = "WindowEventMonitor"
        };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        StartProcessWatcher();
    }

    /// <summary>
    /// Watches process starts for terminal/console utilities so the parent of the
    /// mystery popup (Windows Terminal running tzutil) can be identified.
    /// </summary>
    private static void StartProcessWatcher()
    {
        try
        {
            var query = new WqlEventQuery(
                "SELECT * FROM Win32_ProcessStartTrace WHERE " +
                "ProcessName = 'tzutil.exe' OR ProcessName = 'WindowsTerminal.exe' OR " +
                "ProcessName = 'wt.exe' OR ProcessName = 'cmd.exe' OR ProcessName = 'powershell.exe'");
            _processWatcher = new ManagementEventWatcher(query);
            _processWatcher.EventArrived += OnProcessStarted;
            _processWatcher.Start();
        }
        catch { }
    }

    private static void OnProcessStarted(object sender, EventArrivedEventArgs e)
    {
        try
        {
            var data = e.NewEvent;
            string name = Convert.ToString(data["ProcessName"]) ?? "";
            string pid = Convert.ToString(data["ProcessID"]) ?? "";
            string parentPid = Convert.ToString(data["ParentProcessID"]) ?? "";
            string cmdLine = Convert.ToString(data["ProcessStartCommandLine"]) ?? "";

            string parentName = "unknown";
            if (int.TryParse(parentPid, out int ppid) && ppid > 0)
            {
                try { using var p = Process.GetProcessById(ppid); parentName = p.ProcessName; } catch { }
            }

            Log($"PROC-START {name} pid={pid} parent={parentPid}({parentName}) cmd=\"{cmdLine}\"");
        }
        catch { }
    }

    private static void ThreadProc()
    {
        try
        {
            _hook = SetWinEventHook(EVENT_SYSTEM_FOREGROUND, EVENT_OBJECT_HIDE, IntPtr.Zero, _delegate, 0, 0, WINEVENT_OUTOFCONTEXT);
            while (GetMessage(out MSG msg, IntPtr.Zero, 0, 0) > 0)
            {
                TranslateMessage(ref msg);
                DispatchMessage(ref msg);
            }
        }
        catch { }
    }

    private static void OnWinEvent(IntPtr hWinEventHook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint dwEventThread, uint dwmsEventTime)
    {
        try
        {
            if (idObject != OBJID_WINDOW || hwnd == IntPtr.Zero) return;
            if (!IsWindowVisible(hwnd)) return;

            GetWindowThreadProcessId(hwnd, out uint pid);
            if (pid == 0 || pid == Environment.ProcessId) return;

            long key = hwnd.ToInt64();
            long now = Environment.TickCount64;
            lock (_sync)
            {
                if (_recent.TryGetValue(key, out long last) && now - last < 1500)
                    return;
                _recent[key] = now;
                if (_recent.Count > 200) _recent.Clear();
            }

            string type = eventType switch
            {
                EVENT_SYSTEM_FOREGROUND => "FOREGROUND",
                EVENT_OBJECT_CREATE => "CREATE",
                EVENT_OBJECT_DESTROY => "DESTROY",
                EVENT_OBJECT_SHOW => "SHOW",
                EVENT_OBJECT_HIDE => "HIDE",
                _ => $"0x{eventType:X4}"
            };

            var title = new StringBuilder(256);
            GetWindowText(hwnd, title, title.Capacity);
            var cls = new StringBuilder(256);
            GetClassName(hwnd, cls, cls.Capacity);
            GetWindowRect(hwnd, out RECT rect);
            int w = rect.Right - rect.Left;
            int h = rect.Bottom - rect.Top;

            string proc = "unknown";
            try { using var p = Process.GetProcessById((int)pid); proc = p.ProcessName; } catch { }

            Log($"{type} {proc} | \"{title}\" | class={cls} | {w}x{h}@{rect.Left},{rect.Top} | pid={pid} | hwnd=0x{hwnd.ToInt64():X}");
        }
        catch { }
    }

    private static void Log(string message)
    {
        try
        {
            var dir = Path.GetDirectoryName(LogPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.AppendAllText(LogPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {message}\n");
        }
        catch { }
    }

    private delegate void WinEventDelegate(IntPtr hWinEventHook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint dwEventThread, uint dwmsEventTime);

    [DllImport("user32.dll")]
    private static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr hmodWinEventProc, WinEventDelegate lpfnWinEventProc, uint idProcess, uint idThread, uint dwFlags);

    [DllImport("user32.dll")]
    private static extern int GetMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref MSG lpMsg);

    [DllImport("user32.dll")]
    private static extern bool DispatchMessage(ref MSG lpMsg);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public int ptX;
        public int ptY;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }
}
