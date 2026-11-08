using System.Text.Json;
using AcEvoFfbTuner.Core.Config;
using FluentAssertions;

namespace AcEvoFfbTuner.Tests;

/// <summary>Round-trips the haptic settings through JSON with the same
/// options the config managers use, proving every field survives a restart.</summary>
public class HapticSettingsPersistenceTests
{
    private static readonly JsonSerializerOptions Opts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private static T RoundTrip<T>(T value) =>
        JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value, Opts), Opts)!;

    [Fact]
    public void PhfConfig_AllNewSettings_Persist()
    {
        var c = new MozaPhfConfig
        {
            ModuleMask = 0b111,
            AbsStrength = 150, LockupStrength = 40, BrakePressureStrength = 0,
            RoadStrength = 120, CurbStrength = 80, TcStrength = 175,
            WheelspinStrength = 60, ThrottlePositionStrength = 10,
            EngineStrength = 90, ShiftStrength = 200, ScrubStrength = 5,
            LockupFrequency = 61, WheelspinFrequency = 33, ThrottlePositionFrequency = 22,
            AbsFrequency = 19, ShiftFrequency = 70
        };
        c.SetRoute(PhfEffectKeys.Abs, 0b101);
        c.SetRoute(PhfEffectKeys.Kerbs, 0b010);
        c.SetRoute(PhfEffectKeys.Engine, 0);

        var r = RoundTrip(c);

        r.Should().BeEquivalentTo(c);
        r.GetRoute(PhfEffectKeys.Abs).Should().Be(0b101);
        r.GetRoute(PhfEffectKeys.Kerbs).Should().Be(0b010);
        r.GetRoute(PhfEffectKeys.Engine).Should().Be(0);
        // Untouched effect keeps its default route.
        r.GetRoute(PhfEffectKeys.Tc).Should().Be(MozaPhfConfig.DefaultRoute(PhfEffectKeys.Tc));
    }

    [Fact]
    public void PhfConfig_OldFileWithoutNewFields_GetsDefaults()
    {
        var r = JsonSerializer.Deserialize<MozaPhfConfig>("{\"enabled\":true,\"absFrequency\":18}", Opts)!;
        r.AbsStrength.Should().Be(100);
        r.GetRoute(PhfEffectKeys.Abs).Should().Be(MozaPhfConfig.DefaultRoute(PhfEffectKeys.Abs));
    }

    [Fact]
    public void PedalConfig_HapticGains_Persist()
    {
        var c = new PedalConfig();
        c.HapticGains = new PedalHapticGains
        {
            MasterBrake = 1.5f, MasterGas = 0.7f, MasterClutch = 1.9f,
            AbsBrake = 0.2f, TcGas = 1.1f, CurbBoth = 0.9f, RoadBrake = 0.6f,
            ScrubGas = 0.8f, BrakePressure = 0.4f, ThrottlePosition = 0.3f, EngineRpm = 1.2f
        };

        RoundTrip(c).HapticGains.Should().BeEquivalentTo(c.HapticGains);
    }

    [Fact]
    public void PedalConfig_OldFileWithoutGains_GetsPreviousDefaults()
    {
        var r = JsonSerializer.Deserialize<PedalConfig>("{\"gasAxis\":\"Y\"}", Opts)!;
        r.HapticGains.CurbBoth.Should().Be(0.5f);
        r.HapticGains.RoadBrake.Should().Be(0.3f);
        r.HapticGains.EngineRpm.Should().Be(0f);
    }
}
