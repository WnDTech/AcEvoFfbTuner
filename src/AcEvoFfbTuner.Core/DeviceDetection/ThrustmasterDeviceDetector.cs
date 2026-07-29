using AcEvoFfbTuner.Core.DirectInput;
using AcEvoFfbTuner.Core.FfbProviders;

namespace AcEvoFfbTuner.Core.DeviceDetection;

public sealed class ThrustmasterDeviceDetector : IPlatformDeviceDetector
{
    private const ushort TmVid = 0x044F;

    private static readonly Dictionary<ushort, string> KnownBases = new()
    {
        [0x0200] = "T818",
        [0x0202] = "T598",
        [0x0206] = "T300",
        [0x0204] = "T150",
        [0x020E] = "T248",
        [0x0212] = "T128",
    };

    public string PlatformName => "Thrustmaster HID";
    public WheelbaseVendor Vendor => WheelbaseVendor.Thrustmaster;
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
            var devices = HidDeviceEnumerator.EnumerateByVid(TmVid);
            if (devices.Count == 0) return null;

            var device = devices.FirstOrDefault(d => !string.IsNullOrEmpty(d.ProductString));
            if (device == null) return null;

            string baseName = KnownBases.TryGetValue(device.ProductId, out var known)
                ? known
                : "Thrustmaster";

            string product = device.ProductString.ToUpperInvariant();
            string rimName = IdentifyRim(product);

            string displayName = rimName != null ? $"{baseName} + {rimName}" : $"{baseName} wheel";

            return new DetectedDevice
            {
                Category = DeviceCategory.WheelRim,
                DeviceId = $"tm_rim_{device.ProductId:X4}",
                ProductName = device.ProductString,
                VendorName = "Thrustmaster",
                Vendor = WheelbaseVendor.Thrustmaster,
                State = DeviceConnectionState.Connected,
                DisplayName = displayName,
                Capabilities = new DeviceCapabilities()
            };
        }
        catch (Exception ex)
        {
            return new DetectedDevice
            {
                Category = DeviceCategory.WheelRim,
                DeviceId = "tm_rim_error",
                ProductName = "Thrustmaster wheel",
                VendorName = "Thrustmaster",
                Vendor = WheelbaseVendor.Thrustmaster,
                State = DeviceConnectionState.Error,
                DisplayName = "Thrustmaster wheel",
                ErrorMessage = $"Thrustmaster rim detection failed: {ex.Message}"
            };
        }
    }

    private static string? IdentifyRim(string product)
    {
        if (product.Contains("SF1000")) return "SF1000";
        if (product.Contains("488")) return "488 Challenge";
        if (product.Contains("F1")) return "F1 Wheel";
        if (product.Contains("FERRARI")) return "Ferrari Wheel";
        if (product.Contains("LEATHER") || product.Contains("28 GT")) return "Leather GT";
        if (product.Contains("TM OPEN") || product.Contains("OPEN")) return "Open Wheel";
        return null;
    }
}
