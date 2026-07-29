using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace AcEvoFfbTuner.Services;

public sealed class HotPlugMonitor : IDisposable
{
    private const int WmDeviceChange = 0x0219;
    private const int DbtDevicearrival = 0x8000;
    private const int DbtDeviceremovecomplete = 0x8004;
    private const int DbtDevtypDeviceinterface = 0x0005;

    private HwndSource? _hwndSource;
    private Timer? _pollingTimer;
    private Timer? _debounceTimer;
    private bool _disposed;
    private readonly object _debounceLock = new();
    private bool _debouncePending;
    private int _isOutputActive;

    public event EventHandler<string>? DeviceArrived;
    public event EventHandler<string>? DeviceRemoved;

    public HotPlugMonitor()
    {
        _pollingTimer = new Timer(OnPollingTick, null, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10));
    }

    public void Register(HwndSource hwndSource)
    {
        if (_disposed) return;
        _hwndSource = hwndSource;
        _hwndSource.AddHook(WndProc);
    }

    public bool IsOutputActive
    {
        get => Interlocked.CompareExchange(ref _isOutputActive, 0, 0) != 0;
        set => Interlocked.Exchange(ref _isOutputActive, value ? 1 : 0);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WmDeviceChange) return IntPtr.Zero;

        int evt = wParam.ToInt32();
        if (evt != DbtDevicearrival && evt != DbtDeviceremovecomplete)
            return IntPtr.Zero;

        if (lParam == IntPtr.Zero)
            return IntPtr.Zero;

        var devBroadcastHeader = Marshal.PtrToStructure<DevBroadcastDeviceinterface>(lParam);
        if (devBroadcastHeader.dbcc_devicetype != DbtDevtypDeviceinterface)
            return IntPtr.Zero;

        string? devicePath = null;
        try
        {
            int stringOffset = Marshal.OffsetOf<DevBroadcastDeviceinterface>("dbcc_name").ToInt32();
            devicePath = Marshal.PtrToStringUni(lParam + stringOffset);
        }
        catch { }

        if (string.IsNullOrEmpty(devicePath))
            return IntPtr.Zero;

        ScheduleRefresh();

        if (evt == DbtDevicearrival)
            DeviceArrived?.Invoke(this, devicePath);
        else
            DeviceRemoved?.Invoke(this, devicePath);

        return IntPtr.Zero;
    }

    private void OnPollingTick(object? state)
    {
        try
        {
            if (!IsOutputActive)
                ScheduleRefresh();
        }
        catch { }
    }

    private void ScheduleRefresh()
    {
        lock (_debounceLock)
        {
            if (_debouncePending) return;
            _debouncePending = true;
        }

        _debounceTimer?.Change(Timeout.Infinite, Timeout.Infinite);
        _debounceTimer = new Timer(_ =>
        {
            lock (_debounceLock) _debouncePending = false;
        }, null, 500, Timeout.Infinite);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _pollingTimer?.Dispose();
        _debounceTimer?.Dispose();
        if (_hwndSource != null)
        {
            _hwndSource.RemoveHook(WndProc);
            _hwndSource = null;
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct DevBroadcastDeviceinterface
    {
        public int dbcc_size;
        public int dbcc_devicetype;
        public int dbcc_reserved;
        public Guid dbcc_classguid;
        public char dbcc_name;
    }
}
