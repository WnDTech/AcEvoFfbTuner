using AcEvoFfbTuner.Core.DirectInput;
using AcEvoFfbTuner.Core.FfbProviders;

namespace AcEvoFfbTuner.Core.DeviceDetection;

public sealed class LogitechDeviceDetector : IPlatformDeviceDetector
{
    public string PlatformName => "Logitech HID";
    public WheelbaseVendor Vendor => WheelbaseVendor.Logitech;
    public bool CanDetectWheelRim => false;
    public bool CanDetectPedals => false;

    public void SetProvider(IFFBProvider? provider) { }

    public IReadOnlyList<DetectedDevice> Detect()
    {
        return [];
    }

    public DetectedDevice? DetectWheelRim()
    {
        return null;
    }
}
