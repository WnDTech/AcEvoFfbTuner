using AcEvoFfbTuner.Core.Config;
using AcEvoFfbTuner.Core.PedalHaptics;
using AcEvoFfbTuner.Core.PedalHaptics.Providers;
using FluentAssertions;

namespace AcEvoFfbTuner.Tests;

public class MozaPhfProviderTests
{
    private static MozaPhfConfig FullKitConfig() => new()
    {
        ModuleMask = 0b111,
        ReportId = 0x00,
        ReportLength = 64
    };

    [Fact]
    public void BuildReport_UpdateCommandAndChannelEncoding()
    {
        var cfg = FullKitConfig();

        var report = MozaPhfProvider.BuildReport(cfg,
            (0.5f, 30), (1.0f, 18), (0.0f, 40));

        report.Length.Should().Be(64);
        report[0].Should().Be(0x00);
        report[1].Should().Be(0x01); // update command
        // Brake at offset 2: 100% / 18Hz
        report[2].Should().Be(100);
        report[3].Should().Be(18);
        // Throttle at offset 4: 50% / 30Hz
        report[4].Should().Be(50);
        report[5].Should().Be(30);
        // Clutch at offset 6: 0 / fitted so freq still written
        report[6].Should().Be(0);
        report[7].Should().Be(40);
    }

    [Fact]
    public void BuildReport_UnfittedModule_ForcedToZero()
    {
        // RS102 kit with the single module on the brake only.
        var cfg = FullKitConfig();
        cfg.SetModule(MozaPhfChannel.Throttle, false);
        cfg.SetModule(MozaPhfChannel.Clutch, false);

        var report = MozaPhfProvider.BuildReport(cfg,
            (1.0f, 30), (1.0f, 18), (1.0f, 40));

        report[2].Should().Be(100); // brake driven
        report[4].Should().Be(0);   // throttle silenced
        report[5].Should().Be(0);
        report[6].Should().Be(0);   // clutch silenced
        report[7].Should().Be(0);
    }

    [Fact]
    public void BuildReport_ClampsIntensityAndFrequency()
    {
        var cfg = FullKitConfig();

        var report = MozaPhfProvider.BuildReport(cfg,
            (2.5f, 250), (-1f, -20), (0.756f, 100));

        report[4].Should().Be(100); // intensity clamped
        report[5].Should().Be(100); // freq clamped
        report[2].Should().Be(0);   // negative intensity clamped
        report[3].Should().Be(0);
        report[6].Should().Be(76);  // 0.756 * 100 rounded
    }

    [Fact]
    public void BuildStopReport_StopCommandAllZero()
    {
        var cfg = FullKitConfig();

        var report = MozaPhfProvider.BuildStopReport(cfg);

        report.Length.Should().Be(64);
        report[1].Should().Be(0x00);
        report.Skip(2).All(b => b == 0).Should().BeTrue();
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
