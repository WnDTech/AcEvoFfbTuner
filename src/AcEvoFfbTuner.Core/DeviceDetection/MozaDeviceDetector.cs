using AcEvoFfbTuner.Core.DirectInput;
using AcEvoFfbTuner.Core.FfbProviders;

namespace AcEvoFfbTuner.Core.DeviceDetection;

public sealed class MozaDeviceDetector : IPlatformDeviceDetector
{
    private const ushort MozaVid = 0x346E;

    private static readonly Dictionary<ushort, (string name, DeviceCapabilities caps)> KnownWheels = new()
    {
        [0x3509] = ("Moza KS Steering Wheel", new DeviceCapabilities
        {
            HasLeds = true, LedCount = 10, SupportsRgb = true,
            SupportsBrightnessControl = true, SupportsFlagIndicators = true
        }),
        [0x3502] = ("Moza ES Steering Wheel", new DeviceCapabilities
        {
            HasLeds = true, LedCount = 10, SupportsBrightnessControl = true
        }),
        [0x3506] = ("Moza FSR Steering Wheel", new DeviceCapabilities
        {
            HasLeds = true, LedCount = 10, SupportsRgb = true,
            SupportsBrightnessControl = true, SupportsFlagIndicators = true, HasScreen = true
        }),
        [0x3505] = ("Moza GS Steering Wheel", new DeviceCapabilities
        {
            HasLeds = true, LedCount = 10, SupportsRgb = true,
            SupportsBrightnessControl = true, SupportsFlagIndicators = true
        }),
        [0x3504] = ("Moza CS Steering Wheel", new DeviceCapabilities
        {
            HasLeds = true, LedCount = 10, SupportsBrightnessControl = true
        }),
    };

    public string PlatformName => "Moza HID";
    public WheelbaseVendor Vendor => WheelbaseVendor.Moza;
    public bool CanDetectWheelRim => true;
    public bool CanDetectPedals => false;

    public void SetProvider(IFFBProvider? provider) { }

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
            var devices = HidDeviceEnumerator.EnumerateByVid(MozaVid);
            if (devices.Count == 0)
                return null;

            var wheelDevices = devices.Where(d => !string.IsNullOrEmpty(d.ProductString)).ToList();
            if (wheelDevices.Count == 0)
                return null;

            var device = wheelDevices[0];

            if (KnownWheels.TryGetValue(device.ProductId, out var known))
            {
                return new DetectedDevice
                {
                    Category = DeviceCategory.WheelRim,
                    DeviceId = $"moza_rim_{device.ProductId:X4}",
                    ProductName = known.name,
                    VendorName = "Moza",
                    Vendor = WheelbaseVendor.Moza,
                    State = DeviceConnectionState.Connected,
                    DisplayName = known.name,
                    Capabilities = known.caps
                };
            }

            string displayName = !string.IsNullOrEmpty(device.ProductString)
                ? device.ProductString
                : "Moza wheel";

            return new DetectedDevice
            {
                Category = DeviceCategory.WheelRim,
                DeviceId = $"moza_rim_{device.ProductId:X4}",
                ProductName = device.ProductString,
                VendorName = "Moza",
                Vendor = WheelbaseVendor.Moza,
                State = DeviceConnectionState.Connected,
                DisplayName = displayName,
                Capabilities = new DeviceCapabilities { HasLeds = true, LedCount = 10 }
            };
        }
        catch (Exception ex)
        {
            return new DetectedDevice
            {
                Category = DeviceCategory.WheelRim,
                DeviceId = "moza_rim_error",
                ProductName = "Moza wheel",
                VendorName = "Moza",
                Vendor = WheelbaseVendor.Moza,
                State = DeviceConnectionState.Error,
                DisplayName = "Moza wheel",
                ErrorMessage = $"Moza rim detection failed: {ex.Message}"
            };
        }
    }
}
