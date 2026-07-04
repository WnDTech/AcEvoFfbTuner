using AcEvoFfbTuner.Core.FfbProcessing;
using FluentAssertions;

namespace AcEvoFfbTuner.Tests;

public class FfbEqualizerTests
{
    private readonly FfbEqualizer _sut = new();

    [Fact]
    public void MasterDisabled_ReturnsInputUnchanged()
    {
        _sut.MasterEnabled = false;
        var result = _sut.Process(0.5f);
        result.Should().Be(0.5f);
    }

    [Fact]
    public void AllBandsAtZero_WithMasterOn_ReturnsInputUnchanged()
    {
        _sut.MasterEnabled = true;
        var result = _sut.Process(0.5f);
        result.Should().BeApproximately(0.5f, 0.001f);
    }

    [Fact]
    public void BandCount_IsTen()
    {
        FfbEqualizer.BandCount.Should().Be(10);
    }

    [Fact]
    public void BandInfo_AllBandsHaveValidFrequencies()
    {
        foreach (var band in FfbEqualizer.BandInfo)
            band.CenterHz.Should().BeGreaterThan(0f);
    }

    [Fact]
    public void SetBandGain_ClampsToRange()
    {
        _sut.SetBandGain(0, -20f);
        _sut.GetBandGain(0).Should().Be(-12f);
        _sut.SetBandGain(0, 20f);
        _sut.GetBandGain(0).Should().Be(12f);
    }

    [Fact]
    public void SetBandGain_InvalidBand_DoesNothing()
    {
        _sut.SetBandGain(99, 5f);
        _sut.GetBandGain(99).Should().Be(0f);
    }

    [Fact]
    public void Process_WithAllBandsAtZero_Passthrough()
    {
        _sut.MasterEnabled = true;
        for (int i = 0; i < FfbEqualizer.BandCount; i++)
            _sut.SetBandGain(i, 0f);
        var result = _sut.Process(0.3f);
        result.Should().BeApproximately(0.3f, 0.001f);
    }

    [Fact]
    public void Process_WithGain_ModifiesOutput()
    {
        _sut.MasterEnabled = true;
        _sut.SetBandGain(2, 6f);
        var result = _sut.Process(0.3f);
        result.Should().NotBe(0.3f);
    }

    [Fact]
    public void Reset_ClearsFilterState()
    {
        _sut.MasterEnabled = true;
        _sut.SetBandGain(2, 6f);
        _sut.Process(0.3f);
        _sut.Reset();
        var result = _sut.Process(0.3f);
        result.Should().NotBe(0f);
    }

    [Fact]
    public void SetSampleRate_RecalculatesFilters()
    {
        _sut.MasterEnabled = true;
        _sut.SetBandGain(2, 6f);
        var before = _sut.Process(0.3f);
        _sut.SetSampleRate(100f);
        var after = _sut.Process(0.3f);
        Math.Abs(after - before).Should().BeGreaterThan(0.0001f);
    }
}
