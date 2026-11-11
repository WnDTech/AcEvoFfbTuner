using AcEvoFfbTuner.Core.FfbProcessing;
using FluentAssertions;

namespace AcEvoFfbTuner.Tests;

public class FfbEqualizerTests
{
    private readonly FfbEqualizer _sut = new() { AutoSampleRate = false, ApplyToCore = false }; // effects-only mode for Process() tests

    private static float Sine(int n, float hz, float rate, float amp = 0.3f) =>
        amp * MathF.Sin(2f * MathF.PI * hz * n / rate);

    /// <summary>Peak output amplitude of a sine through the EQ after settling.</summary>
    private static float PeakAt(FfbEqualizer eq, float hz, float rate, bool core = false)
    {
        float peak = 0f;
        for (int n = 0; n < (int)(rate * 4); n++)
        {
            float y = core ? eq.ProcessFinal(Sine(n, hz, rate)) : eq.Process(Sine(n, hz, rate));
            if (n > rate * 2) peak = Math.Max(peak, Math.Abs(y));
        }
        return peak;
    }

    [Fact]
    public void MasterDisabled_ReturnsInputUnchanged()
    {
        _sut.MasterEnabled = false;
        _sut.SetBandGain(3, 6f);
        _sut.Process(0.5f).Should().Be(0.5f);
    }

    [Fact]
    public void AllBandsAtZero_WithMasterOn_IsExactPassthrough()
    {
        _sut.MasterEnabled = true;
        for (int n = 0; n < 100; n++)
            _sut.Process(Sine(n, 20f, 333f)).Should().Be(Sine(n, 20f, 333f));
    }

    [Fact]
    public void BandCount_IsTen() => FfbEqualizer.BandCount.Should().Be(10);

    [Fact]
    public void SetBandGain_ClampsToRange_AndRejectsNaN()
    {
        _sut.SetBandGain(0, -20f);
        _sut.GetBandGain(0).Should().Be(-12f);
        _sut.SetBandGain(0, 20f);
        _sut.GetBandGain(0).Should().Be(12f);
        _sut.SetBandGain(0, float.NaN);
        _sut.GetBandGain(0).Should().Be(0f);
    }

    [Fact]
    public void SetBandGain_InvalidBand_DoesNothing()
    {
        _sut.SetBandGain(99, 5f);
        _sut.GetBandGain(99).Should().Be(0f);
    }

    [Fact]
    public void BoostAtBandCentre_RaisesAmplitude()
    {
        _sut.MasterEnabled = true;
        _sut.SetSampleRate(333f);
        _sut.SetBandGain(4, 6f); // 18 Hz
        PeakAt(_sut, 18f, 333f).Should().BeInRange(0.55f, 0.65f); // 0.3 * 2.0 (+6 dB)
    }

    [Fact]
    public void CutAtBandCentre_LowersAmplitude()
    {
        _sut.MasterEnabled = true;
        _sut.SetSampleRate(333f);
        _sut.SetBandGain(6, -6f); // 35 Hz
        PeakAt(_sut, 35f, 333f).Should().BeInRange(0.13f, 0.17f);
    }

    [Fact]
    public void GainChangeMidStream_DoesNotClick()
    {
        // Steady force through a peaking band: DC gain is 1 at any setting, so a
        // correct implementation never moves the output. The old version zeroed
        // filter state on every slider tick, producing a jump to ~0 and back.
        _sut.MasterEnabled = true;
        _sut.SetBandGain(3, 6f);
        for (int n = 0; n < 1000; n++) _sut.Process(0.5f);

        float maxDev = 0f;
        for (int n = 0; n < 1000; n++)
        {
            if (n % 10 == 0) _sut.SetBandGain(3, (n / 10 % 2 == 0) ? -12f : 12f);
            maxDev = Math.Max(maxDev, Math.Abs(_sut.Process(0.5f) - 0.5f));
        }
        maxDev.Should().BeLessThan(0.01f);
    }

    [Fact]
    public void EngagingFromFlat_DoesNotClick()
    {
        _sut.MasterEnabled = true;
        for (int n = 0; n < 500; n++) _sut.Process(0.5f);
        _sut.SetBandGain(5, 12f);
        float maxDev = 0f;
        for (int n = 0; n < 500; n++) maxDev = Math.Max(maxDev, Math.Abs(_sut.Process(0.5f) - 0.5f));
        maxDev.Should().BeLessThan(0.01f);
    }

    [Fact]
    public void BandAboveGameRate_IsUnsupported_AndPassesThrough()
    {
        _sut.MasterEnabled = true;
        _sut.SetSampleRate(100f); // a slow sim: max usable ≈ 42 Hz
        _sut.Process(0f);
        _sut.IsBandSupported(9).Should().BeFalse(); // 100 Hz
        _sut.IsBandSupported(7).Should().BeFalse(); // 50 Hz
        _sut.IsBandSupported(6).Should().BeTrue();  // 35 Hz
        _sut.SetBandGain(9, 12f);
        for (int n = 0; n < 200; n++)
            _sut.Process(Sine(n, 30f, 100f)).Should().Be(Sine(n, 30f, 100f));
    }

    [Fact]
    public void AllBandsSupported_AtR3eRate()
    {
        _sut.SetSampleRate(400f);
        _sut.Process(0f);
        for (int i = 0; i < FfbEqualizer.BandCount; i++)
            _sut.IsBandSupported(i).Should().BeTrue();
    }

    [Fact]
    public void SampleRate_ChangesBandCentre()
    {
        _sut.MasterEnabled = true;
        _sut.SetBandGain(4, 6f); // 18 Hz
        _sut.SetSampleRate(400f);
        PeakAt(_sut, 18f, 400f).Should().BeInRange(0.55f, 0.65f);
    }

    [Fact]
    public void WholeForce_IsDefault_AndShapesFinal_NotEffects()
    {
        var eq = new FfbEqualizer { AutoSampleRate = false, MasterEnabled = true };
        eq.ApplyToCore.Should().BeTrue();
        eq.SetBandGain(0, 6f);
        float yFinal = 0f, yFx = 0f;
        for (int n = 0; n < 2000; n++) { yFx = eq.Process(0.1f); yFinal = eq.ProcessFinal(0.4f); }
        yFx.Should().Be(0.1f);                          // effects pass through in whole-force mode
        yFinal.Should().BeApproximately(0.8f, 0.02f);  // +6 dB low shelf ≈ x2 steady force
    }

    [Fact]
    public void Final_IsPassthrough_InEffectsOnlyMode()
    {
        _sut.MasterEnabled = true;
        _sut.SetBandGain(0, 6f);
        for (int n = 0; n < 100; n++) { _sut.Process(0f); _sut.ProcessFinal(0.4f).Should().Be(0.4f); }
    }

    [Fact]
    public void SwitchingModes_Crossfades_WithoutJump()
    {
        var eq = new FfbEqualizer { AutoSampleRate = false, MasterEnabled = true, ApplyToCore = false };
        eq.SetBandGain(4, 12f);
        for (int n = 0; n < 1000; n++) eq.ProcessFinal(eq.Process(0f) + 0.5f);
        eq.ApplyToCore = true;
        float maxDev = 0f;
        for (int n = 0; n < 1000; n++) maxDev = Math.Max(maxDev, Math.Abs(eq.ProcessFinal(eq.Process(0f) + 0.5f) - 0.5f));
        maxDev.Should().BeLessThan(0.01f);
    }

    [Fact]
    public void LargeBoosts_AreSoftLimited()
    {
        _sut.MasterEnabled = true;
        for (int i = 0; i < FfbEqualizer.BandCount; i++) _sut.SetBandGain(i, 12f);
        _sut.SnapToTargets();
        float peak = 0f;
        for (int n = 0; n < 2000; n++)
            peak = Math.Max(peak, Math.Abs(_sut.Process(Sine(n, 25f, 333f, 0.9f))));
        peak.Should().BeLessThanOrEqualTo(1.0f);
    }

    [Fact]
    public void Output_IsAlwaysFinite()
    {
        _sut.MasterEnabled = true;
        _sut.SetBandGain(9, 12f);
        _sut.SetSampleRate(30f);
        for (int n = 0; n < 500; n++) float.IsFinite(_sut.Process(Sine(n, 5f, 30f))).Should().BeTrue();
    }

    [Fact]
    public void Spectrum_FindsEnergyInTheRightBand()
    {
        var eq = new FfbEqualizer { AutoSampleRate = false };
        eq.SetSampleRate(400f);
        for (int n = 0; n < 8000; n++) eq.ProcessFinal(Sine(n, 25f, 400f));
        var sp = eq.GetSpectrum();
        int peak = Array.IndexOf(sp.Slow, sp.Slow.Max());
        peak.Should().Be(5); // Road Texture, 25 Hz
        (sp.Slow[5] / sp.TotalSlow).Should().BeGreaterThan(0.8f);
        (sp.Slow[1] / sp.TotalSlow).Should().BeLessThan(0.2f);
    }
}
