using AcEvoFfbTuner.Core.DirectInput;
using AcEvoFfbTuner.Core.FfbProviders;

namespace AcEvoFfbTuner.Core.DeviceDetection;

public sealed class SimucubeDeviceDetector : IPlatformDeviceDetector
{
    public string PlatformName => "Simucube (stub)";
    public WheelbaseVendor Vendor => WheelbaseVendor.Simucube;
    public bool CanDetectWheelRim => false;
    public bool CanDetectPedals => false;

    public void SetProvider(IFFBProvider? provider) { }

    public IReadOnlyList<DetectedDevice> Detect() => [];
    public DetectedDevice? DetectWheelRim() => null;
}
