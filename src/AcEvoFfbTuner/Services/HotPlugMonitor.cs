using System.Runtime.InteropServices;
using System.Windows.Interop;
using AcEvoFfbTuner.Core.DeviceDetection;

namespace AcEvoFfbTuner.Services;

/// <summary>
/// Detects USB devices being plugged in / unplugged / powered on while the app
/// runs. Three layers, all collapsed into one debounced <see cref="DevicesChanged"/>:
///  1. WM_DEVICECHANGE interface arrival/removal — requires
///     RegisterDeviceNotification for the HID interface class (previously
///     missing, so these never arrived).
///  2. DBT_DEVNODES_CHANGED — broadcast to every top-level window on any
///     device-tree change, no registration needed.
///  3. A 3 s poll of a cheap HID fingerprint (no device I/O) as a fallback.
/// </summary>
public sealed class HotPlugMonitor : IDisposable
{
    private const int WmDeviceChange = 0x0219;
    private const int DbtDevicearrival = 0x8000;
    private const int DbtDeviceremovecomplete = 0x8004;
    private const int DbtDevnodesChanged = 0x0007;
    private const int DbtDevtypDeviceinterface = 0x0005;
    private const int DeviceNotifyWindowHandle = 0x0;
    private const int DebounceMs = 750;

    private static readonly Guid HidInterfaceGuid = new(0x4D1E55B2, 0xF16F, 0x11CF, 0x88, 0xCB, 0x00, 0x11, 0x11, 0x00, 0x00, 0x30);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr RegisterDeviceNotification(IntPtr hRecipient, ref DevBroadcastDeviceinterfaceFilter filter, int flags);

    [DllImport("user32.dll")]
    private static extern bool UnregisterDeviceNotification(IntPtr handle);

    private HwndSource? _hwndSource;
    private IntPtr _notifyHandle;
    private readonly Timer _pollingTimer;
    private readonly Timer _debounceTimer;
    private bool _disposed;
    private int _isOutputActive;
    private int _lastFingerprint;
    private int _pollBusy;
    private int _pendingArrival;

    /// <summary>Raw interface arrival (device path). Fires once per interface.</summary>
    public event EventHandler<string>? DeviceArrived;
    /// <summary>Raw interface removal (device path).</summary>
    public event EventHandler<string>? DeviceRemoved;
    /// <summary>Debounced: the set of connected devices changed. The bool is
    /// true when at least one arrival was seen (or could not be ruled out).
    /// Raised on a thread-pool thread.</summary>
    public event EventHandler<bool>? DevicesChanged;

    public HotPlugMonitor()
    {
        _debounceTimer = new Timer(OnDebounceElapsed, null, Timeout.Infinite, Timeout.Infinite);
        _lastFingerprint = RaceHardwareScanner.PresentDeviceFingerprint();
        _pollingTimer = new Timer(OnPollingTick, null, TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(3));
    }

    public void Register(HwndSource hwndSource)
    {
        if (_disposed) return;
        _hwndSource = hwndSource;
        _hwndSource.AddHook(WndProc);

        var filter = new DevBroadcastDeviceinterfaceFilter
        {
            dbcc_size = Marshal.SizeOf<DevBroadcastDeviceinterfaceFilter>(),
            dbcc_devicetype = DbtDevtypDeviceinterface,
            dbcc_classguid = HidInterfaceGuid,
        };
        _notifyHandle = RegisterDeviceNotification(hwndSource.Handle, ref filter, DeviceNotifyWindowHandle);
    }

    /// <summary>Kept for compatibility; the poll no longer pauses while FFB
    /// output runs — it only lists device paths and never opens a device.</summary>
    public bool IsOutputActive
    {
        get => Interlocked.CompareExchange(ref _isOutputActive, 0, 0) != 0;
        set => Interlocked.Exchange(ref _isOutputActive, value ? 1 : 0);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WmDeviceChange) return IntPtr.Zero;

        int evt = wParam.ToInt32();
        if (evt == DbtDevnodesChanged)
        {
            // Direction unknown — treat as possible arrival.
            ScheduleChanged(arrival: true);
            return IntPtr.Zero;
        }

        if ((evt != DbtDevicearrival && evt != DbtDeviceremovecomplete) || lParam == IntPtr.Zero)
            return IntPtr.Zero;

        var header = Marshal.PtrToStructure<DevBroadcastHeader>(lParam);
        if (header.dbch_devicetype != DbtDevtypDeviceinterface)
            return IntPtr.Zero;

        string? devicePath = null;
        try
        {
            int nameOffset = Marshal.OffsetOf<DevBroadcastDeviceinterfaceFilter>("dbcc_name").ToInt32();
            devicePath = Marshal.PtrToStringUni(lParam + nameOffset);
        }
        catch { }

        bool arrival = evt == DbtDevicearrival;
        ScheduleChanged(arrival);

        if (!string.IsNullOrEmpty(devicePath))
        {
            if (arrival) DeviceArrived?.Invoke(this, devicePath);
            else DeviceRemoved?.Invoke(this, devicePath);
        }
        return IntPtr.Zero;
    }

    private void OnPollingTick(object? state)
    {
        if (_disposed || Interlocked.Exchange(ref _pollBusy, 1) == 1) return;
        try
        {
            int fp = RaceHardwareScanner.PresentDeviceFingerprint();
            if (fp != 0 && fp != _lastFingerprint)
            {
                _lastFingerprint = fp;
                ScheduleChanged(arrival: true);
            }
        }
        catch { }
        finally { Interlocked.Exchange(ref _pollBusy, 0); }
    }

    /// <summary>One plug-in produces several interface events; collapse them.</summary>
    private void ScheduleChanged(bool arrival)
    {
        if (_disposed) return;
        if (arrival) Interlocked.Exchange(ref _pendingArrival, 1);
        try { _debounceTimer.Change(DebounceMs, Timeout.Infinite); } catch (ObjectDisposedException) { }
    }

    private void OnDebounceElapsed(object? state)
    {
        if (_disposed) return;
        bool arrival = Interlocked.Exchange(ref _pendingArrival, 0) == 1;
        // Sync the poll baseline so the poller doesn't re-fire for the same change.
        try { _lastFingerprint = RaceHardwareScanner.PresentDeviceFingerprint(); } catch { }
        try { DevicesChanged?.Invoke(this, arrival); } catch { }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _pollingTimer.Dispose();
        _debounceTimer.Dispose();
        if (_notifyHandle != IntPtr.Zero)
        {
            UnregisterDeviceNotification(_notifyHandle);
            _notifyHandle = IntPtr.Zero;
        }
        if (_hwndSource != null)
        {
            _hwndSource.RemoveHook(WndProc);
            _hwndSource = null;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DevBroadcastHeader
    {
        public int dbch_size;
        public int dbch_devicetype;
        public int dbch_reserved;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DevBroadcastDeviceinterfaceFilter
    {
        public int dbcc_size;
        public int dbcc_devicetype;
        public int dbcc_reserved;
        public Guid dbcc_classguid;
        public char dbcc_name;
    }
}
