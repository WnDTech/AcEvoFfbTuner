using AcEvoFfbTuner.Core.DirectInput;
using AcEvoFfbTuner.Core.FfbProviders;

namespace AcEvoFfbTuner.Core.DeviceDetection;

public sealed class GenericDeviceDetector : IPlatformDeviceDetector
{
    public string PlatformName => "Generic DirectInput";
    public WheelbaseVendor Vendor { get; }
    public bool CanDetectWheelRim => false;
    public bool CanDetectPedals => false;

    public GenericDeviceDetector(WheelbaseVendor vendor)
    {
        Vendor = vendor;
    }

    public void SetProvider(IFFBProvider? provider) { }

    public IReadOnlyList<DetectedDevice> Detect() => [];
    public DetectedDevice? DetectWheelRim() => null;
}
