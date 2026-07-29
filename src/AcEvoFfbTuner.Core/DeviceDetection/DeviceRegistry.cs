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
