using AcEvoFfbTuner.Core.DirectInput.VJoy;
using FluentAssertions;

namespace AcEvoFfbTuner.Tests;

public sealed class FakeVJoyNative : IVJoyNative
{
    public bool EnabledResult = true;
    public VjdStat StatusResult = VjdStat.Free;
    public bool AcquireResult = true;
    public int ButtonCountResult = 32;
    public int DiscPovResult = 0;
    public int ContPovResult = 0;
    public bool AxisExistsResult = true;
    public readonly Dictionary<(uint dev, HidUsage axis), int> Axes = new();
    public readonly Dictionary<(uint dev, int btn), bool> Buttons = new();
    public int AcquireCalls;
    public int RelinquishCalls;
    public int ResetCalls;

    public bool DriverEnabled() => EnabledResult;
    public VjdStat GetStatus(uint deviceId) => StatusResult;
    public bool Acquire(uint deviceId) { AcquireCalls++; return AcquireResult; }
    public void Relinquish(uint deviceId) { RelinquishCalls++; }
    public bool SetAxis(int value, uint deviceId, HidUsage axis) { Axes[(deviceId, axis)] = value; return true; }
    public bool SetButton(bool pressed, uint deviceId, int buttonId1Based) { Buttons[(deviceId, buttonId1Based)] = pressed; return true; }
    public bool Reset(uint deviceId) { ResetCalls++; return true; }
    public int GetButtonCount(uint deviceId) => ButtonCountResult;
    public int GetDiscPovCount(uint deviceId) => DiscPovResult;
    public int GetContPovCount(uint deviceId) => ContPovResult;
    public bool AxisExists(uint deviceId, HidUsage axis) => AxisExistsResult;
}

public class VJoyFeederTests
{
    public VJoyFeederTests()
    {
        // Feeder file logging targets the production log path — silence it so
        // unit runs (which use fake natives) don't pollute ac_vjoy.log.
        VJoyFeeder.LoggingEnabled = false;
    }    [Fact]
    public void MapSteer_CenterLeftRight()
    {
        VJoyFeeder.MapSteer(0f).Should().Be(VJoyFeeder.AxisCenter);
        VJoyFeeder.MapSteer(-1f).Should().Be(VJoyFeeder.AxisMin);
        VJoyFeeder.MapSteer(1f).Should().Be(VJoyFeeder.AxisMax);
        VJoyFeeder.MapSteer(-2f).Should().Be(VJoyFeeder.AxisMin);
        VJoyFeeder.MapSteer(2f).Should().Be(VJoyFeeder.AxisMax);
    }

    [Fact]
    public void MapPedal_ReleasedPressed()
    {
        VJoyFeeder.MapPedal(0f).Should().Be(VJoyFeeder.AxisMin);
        VJoyFeeder.MapPedal(1f).Should().Be(VJoyFeeder.AxisMax);
        VJoyFeeder.MapPedal(-0.5f).Should().Be(VJoyFeeder.AxisMin);
        VJoyFeeder.MapPedal(1.5f).Should().Be(VJoyFeeder.AxisMax);
    }

    [Fact]
    public void Connect_AcquiresFreeDevice()
    {
        var fake = new FakeVJoyNative();
        var feeder = new VJoyFeeder(1, fake);
        feeder.Connect().Should().BeTrue();
        feeder.IsConnected.Should().BeTrue();
        fake.AcquireCalls.Should().Be(1);
    }

    [Fact]
    public void Connect_FailsClosedWithoutDriver()
    {
        var fake = new FakeVJoyNative { EnabledResult = false };
        var feeder = new VJoyFeeder(1, fake);
        feeder.Connect().Should().BeFalse();
        feeder.IsConnected.Should().BeFalse();
        feeder.LastError.Should().Contain("vJoy driver");
    }

    [Fact]
    public void Connect_RefusesBusyOrMissing()
    {
        var busy = new VJoyFeeder(1, new FakeVJoyNative { StatusResult = VjdStat.Busy });
        busy.Connect().Should().BeFalse();
        var miss = new VJoyFeeder(1, new FakeVJoyNative { StatusResult = VjdStat.Miss });
        miss.Connect().Should().BeFalse();
    }

    [Fact]
    public void Update_FeedsAxesAndButtons()
    {
        var fake = new FakeVJoyNative();
        var feeder = new VJoyFeeder(2, fake);
        feeder.Connect().Should().BeTrue();
        feeder.Update(0.5f, 1f, 0f, 0.25f, new[] { true, false, true });

        fake.Axes[(2, HidUsage.X)].Should().Be(VJoyFeeder.MapSteer(0.5f));
        fake.Axes[(2, HidUsage.Y)].Should().Be(VJoyFeeder.AxisMax);
        fake.Axes[(2, HidUsage.Z)].Should().Be(VJoyFeeder.AxisMin);
        fake.Axes[(2, HidUsage.Rx)].Should().Be(VJoyFeeder.MapPedal(0.25f));
        fake.Buttons[(2, 1)].Should().BeTrue();
        fake.Buttons[(2, 2)].Should().BeFalse();
        fake.Buttons[(2, 3)].Should().BeTrue();
    }

    [Fact]
    public void Update_IgnoredWhenDisconnected()
    {
        var fake = new FakeVJoyNative();
        var feeder = new VJoyFeeder(1, fake);
        feeder.Update(1f, 1f, 1f, 1f, new[] { true });
        fake.Axes.Should().BeEmpty();
    }

    [Fact]
    public void Update_RoutesButtonsThroughMap()
    {
        var fake = new FakeVJoyNative();
        var feeder = new VJoyFeeder(1, fake);
        feeder.Connect().Should().BeTrue();
        // vJoy slot 1 ← physical 3, slot 2 ← physical 1, slot 3 unmapped.
        var map = VJoyFeeder.DefaultButtonMap();
        map[0] = 2; map[1] = 0; map[2] = -1;
        feeder.ButtonMap = map;
        var physical = new bool[8];
        physical[2] = true; physical[0] = true;
        feeder.Update(0f, 0f, 0f, 0f, physical);

        fake.Buttons[(1u, 1)].Should().BeTrue();
        fake.Buttons[(1u, 2)].Should().BeTrue();
        fake.Buttons[(1u, 3)].Should().BeFalse();
        fake.Buttons[(1u, 4)].Should().BeFalse();
    }

    [Fact]
    public void Update_ReleasesAllWhenPhysicalMissing()
    {
        var fake = new FakeVJoyNative();
        var feeder = new VJoyFeeder(1, fake);
        feeder.Connect().Should().BeTrue();
        feeder.Update(0f, 0f, 0f, 0f, new[] { true, true });
        fake.Buttons[(1u, 1)].Should().BeTrue();
        // Physical read failed (wheel disconnected): stale presses must clear.
        feeder.Update(0f, 0f, 0f, 0f, null);
        fake.Buttons[(1u, 1)].Should().BeFalse();
        fake.Buttons[(1u, 2)].Should().BeFalse();
    }

    [Fact]
    public void Disconnect_Relinquishes()
    {
        var fake = new FakeVJoyNative();
        var feeder = new VJoyFeeder(1, fake);
        feeder.Connect().Should().BeTrue();
        feeder.Disconnect();
        feeder.IsConnected.Should().BeFalse();
        fake.RelinquishCalls.Should().Be(1);
    }

    [Fact]
    public void BuildArgs_CoversAxesPovsAndClampsButtons()
    {
        var all = new[] { true, true, true, true, true, true, true, true };
        Core.DirectInput.VJoy.VJoyDeviceConfig.BuildArgs(1, all, 0, 0, 32)
            .Should().Be("1 -f -a x y z rx ry rz sl0 sl1 -b 32");
        var four = new[] { true, true, true, true, false, false, false, false };
        Core.DirectInput.VJoy.VJoyDeviceConfig.BuildArgs(2, four, 1, 0, 200)
            .Should().Be("2 -f -a x y z rx -b 128 -s 1");
        Core.DirectInput.VJoy.VJoyDeviceConfig.BuildArgs(1, four, 0, 2, 8)
            .Should().Be("1 -f -a x y z rx -b 8 -p 2");
    }

    [Fact]
    public void Connect_ClampsButtonsToDeviceCapability()
    {
        var fake = new FakeVJoyNative { ButtonCountResult = 8 };
        var feeder = new VJoyFeeder(1, fake) { MaxButtons = 32 };
        feeder.Connect().Should().BeTrue();
        feeder.MaxButtons.Should().Be(8);
        feeder.Update(0f, 0f, 0f, 0f, new bool[12]);
        fake.Buttons.Should().HaveCount(8);
        fake.Buttons.Should().NotContainKey((1u, 9));
    }
}
