using AcEvoFfbTuner.Core.DirectInput;
using AcEvoFfbTuner.Core.FfbProviders;

namespace AcEvoFfbTuner.Core.DeviceDetection;

public sealed class SimagicDeviceDetector : IPlatformDeviceDetector
{
    private static readonly ushort[] SimagicVids = [0x3235, 0x0483];

    public string PlatformName => "Simagic HID";
    public WheelbaseVendor Vendor => WheelbaseVendor.Simagic;
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
            foreach (var vid in SimagicVids)
            {
                var devices = HidDeviceEnumerator.EnumerateByVid(vid);
                if (devices.Count == 0) continue;

                var device = devices.FirstOrDefault(d => !string.IsNullOrEmpty(d.ProductString));
                if (device == null) continue;

                string product = device.ProductString.ToUpperInvariant();
                string displayName = IdentifyRim(product) ?? "Simagic wheel";

                return new DetectedDevice
                {
                    Category = DeviceCategory.WheelRim,
                    DeviceId = $"simagic_rim_{device.ProductId:X4}",
                    ProductName = device.ProductString,
                    VendorName = "Simagic",
                    Vendor = WheelbaseVendor.Simagic,
                    State = DeviceConnectionState.Connected,
                    DisplayName = displayName,
                    Capabilities = new DeviceCapabilities()
                };
            }

            return null;
        }
        catch (Exception ex)
        {
            return new DetectedDevice
            {
                Category = DeviceCategory.WheelRim,
                DeviceId = "simagic_rim_error",
                ProductName = "Simagic wheel",
                VendorName = "Simagic",
                Vendor = WheelbaseVendor.Simagic,
                State = DeviceConnectionState.Error,
                DisplayName = "Simagic wheel",
                ErrorMessage = $"Simagic rim detection failed: {ex.Message}"
            };
        }
    }

    private static string? IdentifyRim(string product)
    {
        if (product.Contains("GT4")) return "Simagic GT4 Wheel";
        if (product.Contains("FX PRO")) return "Simagic FX Pro Wheel";
        if (product.Contains("FX")) return "Simagic FX Wheel";
        if (product.Contains("GTS")) return "Simagic GTS Wheel";
        if (product.Contains("NEO X")) return "Simagic Neo X Hub";
        if (product.Contains("NEO")) return "Simagic Neo Wheel";
        if (product.Contains("ALPHA U")) return "Simagic Alpha Ultimate Wheelbase";
        if (product.Contains("ALPHA MINI")) return "Simagic Alpha Mini Wheelbase";
        if (product.Contains("ALPHA")) return "Simagic Alpha Wheelbase";
        return null;
    }
}
