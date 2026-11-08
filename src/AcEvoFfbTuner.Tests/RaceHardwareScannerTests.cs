using AcEvoFfbTuner.Core.DeviceDetection;
using AcEvoFfbTuner.Core.FfbProviders;
using Xunit;

namespace AcEvoFfbTuner.Tests;

public class RaceHardwareScannerTests
{
    private static HidDeviceInfo Hid(ushort vid, ushort pid, string product, ushort usagePage = 1, ushort usage = 4, string mfr = "") => new()
    {
        VendorId = vid, ProductId = pid, ProductString = product, ManufacturerString = mfr,
        UsagePage = usagePage, Usage = usage,
    };

    private static uint Key(ushort vid, ushort pid) => ((uint)pid << 16) | vid;

    [Fact]
    public void FfbDevice_IsWheelbase_AndCollectionsAreGrouped()
    {
        var r = RaceHardwareScanner.Classify(
            [Hid(0x346E, 0x0004, "MOZA R5 Base"), Hid(0x346E, 0x0004, "MOZA R5 Base", 0xFF00, 1)],
            new HashSet<uint> { Key(0x346E, 0x0004) });

        var item = Assert.Single(r);
        Assert.Equal(DeviceCategory.WheelBase, item.Category);
        Assert.Equal(WheelbaseVendor.Moza, item.WheelVendor);
        Assert.True(item.IsFfbCapable);
    }

    [Theory]
    [InlineData("Heusinkveld Sim Pedals Sprint", DeviceCategory.Pedals)]
    [InlineData("SIMAGIC Handbrake", DeviceCategory.Handbrake)]
    [InlineData("Sequential Shifter", DeviceCategory.Shifter)]
    [InlineData("Button Box 32", DeviceCategory.ButtonBox)]
    [InlineData("Pedal Haptic Unit", DeviceCategory.HapticPad)]
    public void NonFfbJoystick_ClassifiedByName(string product, DeviceCategory expected)
    {
        var item = Assert.Single(RaceHardwareScanner.Classify([Hid(0x1234, 0x0001, product)]));
        Assert.Equal(expected, item.Category);
    }

    [Fact]
    public void MozaPhf_IsHaptics_EvenWithoutGameControllerUsage()
    {
        var item = Assert.Single(RaceHardwareScanner.Classify([Hid(0x346E, 0x002F, "", 0xFF00, 1)]));
        Assert.Equal(DeviceCategory.HapticPad, item.Category);
    }

    [Fact]
    public void KeyboardsMiceAndUnbrandedGamepads_AreIgnored()
    {
        var r = RaceHardwareScanner.Classify(
        [
            Hid(0x046D, 0xC52B, "USB Receiver", 1, 6),        // Logitech keyboard
            Hid(0x046D, 0xC077, "USB Optical Mouse", 1, 2),   // Logitech mouse
            Hid(0x045E, 0x02FF, "Controller", 1, 5),          // Xbox pad
            Hid(0x044F, 0xB679, "T-Rudder"),                  // Thrustmaster flight gear
            Hid(0x1234, 0xBEAD, "vJoy - Virtual Joystick"),
            Hid(0x4098, 0xBC27, "WINCTRL URSA MINOR 32 Joystick L"),
        ]);
        Assert.Empty(r);
    }

    [Fact]
    public void PidVidGuid_Decodes()
    {
        // {0004346E-0000-0000-0000-504944564944} = MOZA VID 346E, PID 0004
        var g = new Guid("0004346E-0000-0000-0000-504944564944");
        Assert.Equal(Key(0x346E, 0x0004), RaceHardwareScanner.VidPidFromProductGuid(g));
        Assert.Null(RaceHardwareScanner.VidPidFromProductGuid(Guid.NewGuid()));
    }
}
