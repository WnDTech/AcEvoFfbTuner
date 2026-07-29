using AcEvoFfbTuner.Core.DirectInput;
using AcEvoFfbTuner.Core.FfbProviders;

namespace AcEvoFfbTuner.Core.DeviceDetection;

public static class PlatformDeviceDetectorFactory
{
    public static IPlatformDeviceDetector Create(WheelbaseVendor vendor, IFFBProvider? provider)
    {
        IPlatformDeviceDetector detector = vendor switch
        {
            WheelbaseVendor.Fanatec => new FanatecDeviceDetector(),
            WheelbaseVendor.Moza => new MozaDeviceDetector(),
            WheelbaseVendor.Simagic => new SimagicDeviceDetector(),
            WheelbaseVendor.Thrustmaster => new ThrustmasterDeviceDetector(),
            WheelbaseVendor.Logitech => new LogitechDeviceDetector(),
            WheelbaseVendor.Simucube => new SimucubeDeviceDetector(),
            WheelbaseVendor.Asetek => new AsetekDeviceDetector(),
            WheelbaseVendor.VNM => new GenericDeviceDetector(WheelbaseVendor.VNM),
            WheelbaseVendor.Cammus => new GenericDeviceDetector(WheelbaseVendor.Cammus),
            _ => new GenericDeviceDetector(WheelbaseVendor.Unknown),
        };

        detector.SetProvider(provider);
        return detector;
    }

    public static IPlatformDeviceDetector CreateFromProductName(string productName, IFFBProvider? provider)
    {
        var vendor = WheelbaseFactory.DetectVendor(productName);
        return Create(vendor, provider);
    }
}
