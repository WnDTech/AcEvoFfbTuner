using System.Management;
using AcEvoFfbTuner.Core.DirectInput;

namespace AcEvoFfbTuner.Core.FfbProviders;

public enum WheelbaseVendor
{
    Unknown,
    Simucube,
    Logitech,
    Moza,
    Fanatec,
    Simagic,
    Thrustmaster,
    GenericDirectInput
}

public sealed class WheelbaseFactory
{
    private static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "AcEvoFfbTuner", "wheelbase_factory.log");

    private const string SimucubeVendorId = "16D0";
    private const string LogitechVendorId = "046D";
    private const string MozaVendorId = "346E";
    private const string FanatecVendorId = "0EB7";
    private const string SimagicVendorId = "3235";
    private const string ThrustmasterVendorId = "044F";

    private static readonly Dictionary<string, WheelbaseVendor> VendorIdMap = new(StringComparer.OrdinalIgnoreCase)
    {
        [SimucubeVendorId] = WheelbaseVendor.Simucube,
        [LogitechVendorId] = WheelbaseVendor.Logitech,
        [MozaVendorId] = WheelbaseVendor.Moza,
        [FanatecVendorId] = WheelbaseVendor.Fanatec,
        [SimagicVendorId] = WheelbaseVendor.Simagic,
        [ThrustmasterVendorId] = WheelbaseVendor.Thrustmaster,
    };

    public static WheelbaseVendor DetectVendorFromProductName(string productName)
    {
        if (string.IsNullOrEmpty(productName)) return WheelbaseVendor.Unknown;
        var n = productName.ToUpperInvariant();

        if (n.Contains("SIMUCUBE")) return WheelbaseVendor.Simucube;
        if (n.Contains("LOGITECH") || n.Contains("G29") || n.Contains("G920") || n.Contains("G923")) return WheelbaseVendor.Logitech;
        if (n.Contains("MOZA")) return WheelbaseVendor.Moza;
        if (n.Contains("FANATEC") || n.Contains("CLUBSPORT") || n.Contains("CSL ")) return WheelbaseVendor.Fanatec;
        if (n.Contains("SIMAGIC")) return WheelbaseVendor.Simagic;
        if (n.Contains("THRUSTMASTER") || n.Contains("T300") || n.Contains("T150") || n.Contains("TX ")) return WheelbaseVendor.Thrustmaster;

        return WheelbaseVendor.Unknown;
    }

    public static WheelbaseVendor DetectVendorFromUsb()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT DeviceID, HardwareID FROM Win32_PnPEntity WHERE HardwareID IS NOT NULL");

            foreach (var obj in searcher.Get())
            {
                try
                {
                    string? hwId = obj["HardwareID"] switch
                    {
                        string s => s,
                        string[] arr => arr.FirstOrDefault(),
                        _ => null
                    };
                    if (string.IsNullOrEmpty(hwId)) continue;

                    string vendorId = ExtractVendorId(hwId);
                    if (!string.IsNullOrEmpty(vendorId) && VendorIdMap.TryGetValue(vendorId, out var vendor))
                        return vendor;
                }
                catch { }
            }
        }
        catch (Exception ex)
        {
            Log($"USB scan failed: {ex.Message}");
        }

        return WheelbaseVendor.Unknown;
    }

    private static string ExtractVendorId(string hardwareId)
    {
        int vidIdx = hardwareId.IndexOf("VID_", StringComparison.OrdinalIgnoreCase);
        if (vidIdx < 0) return "";
        int start = vidIdx + 4;
        if (start + 4 > hardwareId.Length) return "";
        return hardwareId.Substring(start, 4);
    }

    public static WheelbaseVendor DetectVendor(string productName)
    {
        var fromName = DetectVendorFromProductName(productName);
        if (fromName != WheelbaseVendor.Unknown)
        {
            Log($"Vendor detected from product name '{productName}': {fromName}");
            return fromName;
        }

        var fromUsb = DetectVendorFromUsb();
        if (fromUsb != WheelbaseVendor.Unknown)
        {
            Log($"Vendor detected from USB scan: {fromUsb}");
            return fromUsb;
        }

        Log($"Vendor unknown for '{productName}', defaulting to GenericDirectInput");
        return WheelbaseVendor.GenericDirectInput;
    }

    public static IFFBProvider CreateProvider(WheelbaseVendor vendor, FfbDeviceManager deviceManager)
    {
        Log($"Vendor: {vendor} — using DirectInput (vendor SDKs pending)");
        return new GenericDirectInputProvider(deviceManager);
    }

    public static IFFBProvider CreateFromDeviceName(string productName, FfbDeviceManager deviceManager)
    {
        var vendor = DetectVendor(productName);
        return CreateProvider(vendor, deviceManager);
    }

    private static void Log(string msg)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
            File.AppendAllText(LogPath, $"[{DateTime.Now:HH:mm:ss.fff}] {msg}\n");
        }
        catch { }
    }
}
