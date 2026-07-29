using AcEvoFfbTuner.Core.DirectInput;
using AcEvoFfbTuner.Core.FfbProviders;

namespace AcEvoFfbTuner.Core.DeviceDetection;

public sealed class FanatecDeviceDetector : IPlatformDeviceDetector
{
    private FanatecProvider? _provider;

    public string PlatformName => "Fanatec SDK";
    public WheelbaseVendor Vendor => WheelbaseVendor.Fanatec;
    public bool CanDetectWheelRim => true;
    public bool CanDetectPedals => true;

    public void SetProvider(IFFBProvider? provider)
    {
        _provider = provider as FanatecProvider;
    }

    public IReadOnlyList<DetectedDevice> Detect()
    {
        var results = new List<DetectedDevice>();
        var rim = DetectWheelRim();
        if (rim != null) results.Add(rim);
        return results;
    }

    public DetectedDevice? DetectWheelRim()
    {
        try
        {
            if (_provider == null) return null;

            string? rimName = _provider.RimProductName;
            if (string.IsNullOrEmpty(rimName) || rimName == "None")
                rimName = "Fanatec wheel";

            return new DetectedDevice
            {
                Category = DeviceCategory.WheelRim,
                DeviceId = $"fanatec_rim_{rimName}",
                ProductName = _provider.RimProductName ?? "Unknown",
                VendorName = "Fanatec",
                Vendor = WheelbaseVendor.Fanatec,
                State = DeviceConnectionState.Connected,
                DisplayName = rimName,
                Capabilities = new DeviceCapabilities
                {
                    HasLeds = _provider.HasRimRevLeds,
                    HasRumbleMotors = _provider.HasRumbleMotors,
                    HasGearDisplay = _provider.HasRimLedDisplay,
                    LedCount = _provider.HasRimRevLeds ? 9 : 0,
                    SupportsFullForce = _provider.IsFullForceAvailable
                }
            };
        }
        catch (Exception ex)
        {
            return new DetectedDevice
            {
                Category = DeviceCategory.WheelRim,
                DeviceId = "fanatec_rim_error",
                ProductName = "Fanatec wheel",
                VendorName = "Fanatec",
                Vendor = WheelbaseVendor.Fanatec,
                State = DeviceConnectionState.Error,
                DisplayName = "Fanatec wheel",
                ErrorMessage = $"Fanatec rim detection failed: {ex.Message}"
            };
        }
    }
}
