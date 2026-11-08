using System.ComponentModel;
using System.Runtime.CompilerServices;
using AcEvoFfbTuner.Core.FfbProviders;

namespace AcEvoFfbTuner.Core.DeviceDetection;

public sealed class DeviceRegistry : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    private readonly List<DetectedDevice> _devices = [];
    private int _refreshGuard;
    private static readonly object _dispatcherLock = new();

    public IReadOnlyList<DetectedDevice> Devices
    {
        get { lock (_dispatcherLock) return [.. _devices]; }
    }

    public event EventHandler<DetectedDevice>? DeviceAdded;
    public event EventHandler<DetectedDevice>? DeviceRemoved;
    public event EventHandler<DetectedDevice>? DeviceStateChanged;
    public event EventHandler? RegistryRefreshed;

    public DetectedDevice? WheelBase => Devices.FirstOrDefault(d => d.Category == DeviceCategory.WheelBase);
    public DetectedDevice? WheelRim  => Devices.FirstOrDefault(d => d.Category == DeviceCategory.WheelRim);
    public DetectedDevice? Pedals    => Devices.FirstOrDefault(d => d.Category == DeviceCategory.Pedals);
    public DetectedDevice? Haptics   => Devices.FirstOrDefault(d => d.Category == DeviceCategory.HapticPad);
    public DetectedDevice? Game      => Devices.FirstOrDefault(d => d.Category == DeviceCategory.Game);

    public void AddDefault(DeviceCategory category, string displayName)
    {
        var device = new DetectedDevice
        {
            Category = category,
            DeviceId = $"default_{category}",
            ProductName = displayName,
            VendorName = "",
            Vendor = WheelbaseVendor.Unknown,
            State = DeviceConnectionState.Disconnected,
            DisplayName = displayName,
            Capabilities = DeviceCapabilities.Empty
        };
        AddOrUpdate(device);
    }

    public void AddOrUpdate(DetectedDevice device)
    {
        lock (_dispatcherLock)
        {
            var existing = _devices.FirstOrDefault(d => d.Category == device.Category);
            if (existing != null)
            {
                var idx = _devices.IndexOf(existing);
                _devices[idx] = device;
                OnPropertyChanged(nameof(Devices));
                DeviceStateChanged?.Invoke(this, device);
            }
            else
            {
                _devices.Add(device);
                OnPropertyChanged(nameof(Devices));
                DeviceAdded?.Invoke(this, device);
            }
        }
    }

    public void RemoveDevice(DeviceCategory category)
    {
        lock (_dispatcherLock)
        {
            var existing = _devices.FirstOrDefault(d => d.Category == category);
            if (existing != null)
            {
                _devices.Remove(existing);
                OnPropertyChanged(nameof(Devices));
                DeviceRemoved?.Invoke(this, existing);
            }
        }
    }

    public void SetDisconnected(DeviceCategory category)
    {
        lock (_dispatcherLock)
        {
            var existing = _devices.FirstOrDefault(d => d.Category == category);
            if (existing != null)
            {
                existing.State = DeviceConnectionState.Disconnected;
                OnPropertyChanged(nameof(Devices));
                DeviceStateChanged?.Invoke(this, existing);
            }
        }
    }

    // ── System-wide hardware scan ────────────────────────────────────────
    private List<RaceHardwareItem> _hardware = [];
    private int _scanGuard;

    /// <summary>Every sim device found by the last <see cref="ScanHardware"/>.</summary>
    public IReadOnlyList<RaceHardwareItem> Hardware
    {
        get { lock (_dispatcherLock) return [.. _hardware]; }
    }

    public event EventHandler? HardwareScanned;

    /// <summary>
    /// Runs a full HID scan and updates <see cref="Hardware"/>. Fills the
    /// Pedals / HapticPad category slots from the scan only when nothing else
    /// (DirectInput pedal source, HF8 controller) owns them — the connected
    /// wheelbase slot is never touched here (it reflects the live connection).
    /// Safe to call from any thread; overlapping calls are dropped.
    /// </summary>
    public bool ScanHardware(IReadOnlySet<uint>? ffbVidPids = null)
    {
        if (Interlocked.CompareExchange(ref _scanGuard, 1, 0) != 0)
            return false;
        try
        {
            var found = RaceHardwareScanner.Scan(ffbVidPids);
            lock (_dispatcherLock) _hardware = found;

            ApplyScanSlot(DeviceCategory.Pedals, found);
            ApplyScanSlot(DeviceCategory.HapticPad, found);
            ApplyScanSlot(DeviceCategory.Shifter, found);
            ApplyScanSlot(DeviceCategory.Handbrake, found);
            ApplyScanSlot(DeviceCategory.ButtonBox, found);

            OnPropertyChanged(nameof(Hardware));
            HardwareScanned?.Invoke(this, EventArgs.Empty);
            return true;
        }
        finally
        {
            Interlocked.Exchange(ref _scanGuard, 0);
        }
    }

    private void ApplyScanSlot(DeviceCategory cat, List<RaceHardwareItem> found)
    {
        var items = found.Where(h => h.Category == cat).ToList();
        DetectedDevice? existing;
        lock (_dispatcherLock) existing = _devices.FirstOrDefault(d => d.Category == cat);

        // A slot filled by a live source (not a scan / not a placeholder) wins.
        bool ownedByLiveSource = existing != null
            && existing.State == DeviceConnectionState.Connected
            && !existing.DeviceId.StartsWith("scan_", StringComparison.Ordinal);
        if (ownedByLiveSource) return;

        if (items.Count == 0)
        {
            if (existing != null && existing.DeviceId.StartsWith("scan_", StringComparison.Ordinal))
                SetDisconnected(cat);
            return;
        }

        var first = items[0];
        string display = items.Count == 1 ? first.ProductName : $"{first.ProductName} (+{items.Count - 1})";
        AddOrUpdate(new DetectedDevice
        {
            Category = cat,
            DeviceId = $"scan_{cat}_{first.Key}",
            ProductName = first.ProductName,
            VendorName = first.VendorName,
            Vendor = first.WheelVendor,
            State = DeviceConnectionState.Connected,
            DisplayName = display,
            Capabilities = DeviceCapabilities.Empty
        });
    }

    private IPlatformDeviceDetector? _currentDetector;
    private IFFBProvider? _currentProvider;

    public void UpdateFromProvider(IFFBProvider? provider)
    {
        _currentProvider = provider;

        if (provider == null)
        {
            _currentDetector = null;
            SetDisconnected(DeviceCategory.WheelRim);
            return;
        }

        var vendor = WheelbaseVendor.Unknown;
        try
        {
            var productName = Devices.FirstOrDefault(d => d.Category == DeviceCategory.WheelBase)?.ProductName;
            if (!string.IsNullOrEmpty(productName))
                vendor = WheelbaseFactory.DetectVendor(productName);
        }
        catch { }

        _currentDetector = PlatformDeviceDetectorFactory.Create(vendor, provider);
        _currentDetector.SetProvider(provider);

        var rim = _currentDetector.DetectWheelRim();
        if (rim != null)
            AddOrUpdate(rim);
    }

    public bool TryRefresh()
    {
        if (Interlocked.CompareExchange(ref _refreshGuard, 1, 0) != 0)
            return false;

        try
        {
            var rim = _currentDetector?.DetectWheelRim();
            if (rim != null)
                AddOrUpdate(rim);

            RegistryRefreshed?.Invoke(this, EventArgs.Empty);
            return true;
        }
        finally
        {
            Interlocked.Exchange(ref _refreshGuard, 0);
        }
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
