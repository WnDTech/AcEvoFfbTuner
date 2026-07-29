using AcEvoFfbTuner.Core.FfbProviders;

namespace AcEvoFfbTuner.Core.DeviceDetection;

public interface IPlatformDeviceDetector
{
    string PlatformName { get; }
    WheelbaseVendor Vendor { get; }
    bool CanDetectWheelRim { get; }
    bool CanDetectPedals { get; }

    void SetProvider(IFFBProvider? provider);

    IReadOnlyList<DetectedDevice> Detect();

    DetectedDevice? DetectWheelRim();
}
