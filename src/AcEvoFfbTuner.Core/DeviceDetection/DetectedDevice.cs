using System.ComponentModel;
using System.Runtime.CompilerServices;
using AcEvoFfbTuner.Core.FfbProviders;

namespace AcEvoFfbTuner.Core.DeviceDetection;

public sealed class DetectedDevice : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    public DeviceCategory Category { get; init; }
    public string DeviceId { get; init; } = string.Empty;
    public string ProductName { get; init; } = string.Empty;
    public string VendorName { get; init; } = string.Empty;
    public WheelbaseVendor Vendor { get; init; } = WheelbaseVendor.Unknown;

    private DeviceConnectionState _state = DeviceConnectionState.Disconnected;
    public DeviceConnectionState State
    {
        get => _state;
        set { _state = value; OnPropertyChanged(); }
    }

    private string _displayName = string.Empty;
    public string DisplayName
    {
        get => _displayName;
        set { _displayName = value; OnPropertyChanged(); }
    }

    private string? _errorMessage;
    public string? ErrorMessage
    {
        get => _errorMessage;
        set { _errorMessage = value; OnPropertyChanged(); }
    }

    public DeviceCapabilities Capabilities { get; init; } = DeviceCapabilities.Empty;

    private void OnPropertyChanged([CallerMemberName] string? name = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
