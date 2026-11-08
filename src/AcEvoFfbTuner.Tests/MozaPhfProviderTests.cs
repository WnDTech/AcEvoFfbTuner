using AcEvoFfbTuner.Core.Config;
using AcEvoFfbTuner.Core.PedalHaptics;
using AcEvoFfbTuner.Core.PedalHaptics.Providers;
using FluentAssertions;

namespace AcEvoFfbTuner.Tests;

public class MozaPhfProviderTests
{
    private static byte[] Hex(string h) =>
        Enumerable.Range(0, h.Length / 2).Select(i => Convert.ToByte(h.Substring(i * 2, 2), 16)).ToArray();

    // Frames below are verbatim from the 2026-11-06 USB capture of real
    // PHF effects (and hardware-verified to vibrate when sent by the app).
    [Fact]
    public void BuildFrame_MatchesCapturedBrakeTc()
    {
        MozaPhfProvider.BuildFrame(0x02, MozaPhfProvider.EffectTc, true, 200, 50, 0x7FFF)
            .Should().Equal(Hex("7e0b4d12010102000100c800327fff72"));
    }

    [Fact]
    public void BuildFrame_MatchesCapturedBrakeRoadTexture()
    {
        MozaPhfProvider.BuildFrame(0x02, MozaPhfProvider.EffectRoad, true, 100, 100, 0x7FFF)
            .Should().Equal(Hex("7e0b4d120101020801006400647fff48"));
    }

    [Fact]
    public void BuildFrame_MatchesCapturedThrottleGearShiftGain()
    {
        MozaPhfProvider.BuildFrame(0x01, MozaPhfProvider.EffectGearShift, true, 200, 100, 0xCCCC)
            .Should().Equal(Hex("7e0b4d12010101060100c80064cccc").Concat(new[] {
                MozaPhfProvider.Checksum(Hex("7e0b4d12010101060100c80064cccc"), 15) }));
    }

    [Fact]
    public void BuildFrame_StopFrameCarriesZeros()
    {
        var f = MozaPhfProvider.BuildFrame(0x02, MozaPhfProvider.EffectTc, false, 200, 50, 0x7FFF);
        f.Skip(8).Take(7).All(b => b == 0).Should().BeTrue();
        f[15].Should().Be(MozaPhfProvider.Checksum(f, 15));
    }

    [Fact]
    public void BuildFrame_ClampsFrequencyAndCarriesAmplitude()
    {
        var f = MozaPhfProvider.BuildFrame(0x02, MozaPhfProvider.EffectRoad, true, 100, 250, 0xABCD);
        f[12].Should().Be(100);
        f[13].Should().Be(0xAB);
        f[14].Should().Be(0xCD);
    }

    [Fact]
    public void Checksum_MatchesMozaPing()
    {
        // 7E 00 00 12 -> 9D (captured keep-alive)
        MozaPhfProvider.Checksum([0x7E, 0x00, 0x00, 0x12], 4).Should().Be(0x9D);
    }

    [Fact]
    public void Config_ModuleMask_RS102vsRS103()
    {
        var cfg = new MozaPhfConfig();

        // Default = brake module only (single RS102 module fitted to brake).
        cfg.HasModule(MozaPhfChannel.Brake).Should().BeTrue();
        cfg.HasModule(MozaPhfChannel.Throttle).Should().BeFalse();
        cfg.HasModule(MozaPhfChannel.Clutch).Should().BeFalse();

        // Adding RS103 modules enables more ports.
        cfg.SetModule(MozaPhfChannel.Throttle, true);
        cfg.SetModule(MozaPhfChannel.Clutch, true);
        cfg.HasModule(MozaPhfChannel.Throttle).Should().BeTrue();
        cfg.HasModule(MozaPhfChannel.Clutch).Should().BeTrue();

        cfg.SetModule(MozaPhfChannel.Brake, false);
        cfg.HasModule(MozaPhfChannel.Brake).Should().BeFalse();
    }

    [Fact]
    public void FrequencyFor_MapsSignalToConfiguredFrequency()
    {
        var cfg = new MozaPhfConfig
        {
            AbsFrequency = 18,
            TcFrequency = 30,
            CurbFrequency = 45,
            RoadFrequency = 65,
            ScrubFrequency = 50,
            RearSlipFrequency = 35,
            BrakePressureFrequency = 25
        };
        using var provider = new MozaPhfProvider(cfg);

        provider.FrequencyFor(HapticSignalType.Abs).Should().Be(18);
        provider.FrequencyFor(HapticSignalType.Tc).Should().Be(30);
        provider.FrequencyFor(HapticSignalType.Curb).Should().Be(45);
        provider.FrequencyFor(HapticSignalType.Road).Should().Be(65);
        provider.FrequencyFor(HapticSignalType.Scrub).Should().Be(50);
        provider.FrequencyFor(HapticSignalType.RearSlip).Should().Be(35);
        provider.FrequencyFor(HapticSignalType.BrakePressure).Should().Be(25);
    }

    [Fact]
    public void Provider_NotAvailableBeforeConnect()
    {
        using var provider = new MozaPhfProvider(new MozaPhfConfig());

        provider.IsAvailable.Should().BeFalse();
        // No hardware — must not throw.
        provider.SetBrakeHaptic(1f, HapticSignalType.Abs);
        provider.SetGasHaptic(1f, HapticSignalType.Tc);
        provider.SetClutchHaptic(1f, HapticSignalType.Curb);
        provider.StopAll();
    }

    [Fact]
    public void Provider_SupportedChannelsFollowModuleMask()
    {
        var cfg = new MozaPhfConfig(); // brake only
        using var provider = new MozaPhfProvider(cfg);

        provider.IsBrakeSupported.Should().BeTrue();
        provider.IsGasSupported.Should().BeFalse();
        provider.IsClutchSupported.Should().BeFalse();
    }
}
