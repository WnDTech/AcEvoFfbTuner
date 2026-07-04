using AcEvoFfbTuner.Core.FfbProcessing;
using AcEvoFfbTuner.Core.FfbProcessing.Models;
using FluentAssertions;

namespace AcEvoFfbTuner.Tests;

public class FfbSlipEnhancerTests
{
    private readonly FfbSlipEnhancer _sut = new();
    private readonly FfbRawData _raw = new();

    [Fact]
    public void Apply_ZeroGains_ReturnsForceUnchanged()
    {
        var result = _sut.Apply(0.5f, _raw);
        result.Should().Be(0.5f);
    }

    [Fact]
    public void Apply_SlipRatioGainIncreases_ForceIncreases()
    {
        _sut.SlipRatioGain = 0.5f;
        _sut.SlipThreshold = 0.01f;
        _raw.SlipRatio[0] = 0.1f;
        _raw.SlipRatio[1] = 0.1f;
        var result = _sut.Apply(0.5f, _raw);
        result.Should().BeGreaterThan(0.5f);
    }

    [Fact]
    public void Apply_NegativeSlipRatio_DecreasesForce()
    {
        _sut.SlipRatioGain = 0.5f;
        _sut.SlipThreshold = 0.01f;
        _raw.SlipRatio[0] = -0.1f;
        _raw.SlipRatio[1] = -0.1f;
        var result = _sut.Apply(0.5f, _raw);
        result.Should().BeLessThan(0.5f);
    }

    [Fact]
    public void Apply_SlipAngleGainIncreases_ForceIncreases()
    {
        _sut.SlipAngleGain = 0.5f;
        _sut.SlipThreshold = 0.01f;
        _raw.SlipAngle[0] = 0.1f;
        _raw.SlipAngle[1] = 0.1f;
        var result = _sut.Apply(0.5f, _raw);
        result.Should().BeGreaterThan(0.5f);
    }

    [Fact]
    public void Apply_ZeroInputForce_ReturnsSmallForce()
    {
        _sut.SlipAngleGain = 0.5f;
        _sut.SlipThreshold = 0.01f;
        _raw.SlipAngle[0] = 0.1f;
        _raw.SlipAngle[1] = 0.1f;
        var result = _sut.Apply(0f, _raw);
        result.Should().BeInRange(-0.5f, 0.5f);
    }

    [Fact]
    public void Apply_SlipAngleShapeGain_ModulatesForce()
    {
        _sut.SlipAngleShapeGain = 0.3f;
        _sut.PeakSlipAngle = 0.08f;
        _sut.SlipThreshold = 0.01f;
        _raw.SlipAngle[0] = 0.08f;
        _raw.SlipAngle[1] = 0.08f;
        var result = _sut.Apply(0.5f, _raw);
        result.Should().NotBe(0.5f);
    }
}
