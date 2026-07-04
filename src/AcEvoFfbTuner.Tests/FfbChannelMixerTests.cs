using AcEvoFfbTuner.Core.FfbProcessing;
using AcEvoFfbTuner.Core.FfbProcessing.Models;
using FluentAssertions;

namespace AcEvoFfbTuner.Tests;

public class FfbChannelMixerTests
{
    private readonly FfbChannelMixer _sut = new();
    private readonly FfbRawData _raw = new();

    public FfbChannelMixerTests()
    {
        _raw.Mz = [10f, 10f, 0f, 0f];
        _raw.Fx = [100f, 100f, 0f, 0f];
        _raw.Fy = [500f, 500f, 0f, 0f];
        _raw.WheelLoad = [500f, 500f, 500f, 500f];
        _raw.SlipRatio = [0f, 0f, 0f, 0f];
        _raw.SlipAngle = [0f, 0f, 0f, 0f];
    }

    [Fact]
    public void Mix_WithEnabledChannels_ReturnsNonZero()
    {
        _sut.MzFrontEnabled = true;
        _sut.MzFrontGain = 1.0f;
        _sut.FxFrontEnabled = false;
        _sut.FyFrontEnabled = false;
        var result = _sut.Mix(_raw, out var channels);
        result.Should().NotBe(0f);
        channels.MzFront.Should().NotBe(0f);
    }

    [Fact]
    public void Mix_AllChannelsDisabled_ReturnsZero()
    {
        _sut.MzFrontEnabled = false;
        _sut.FxFrontEnabled = false;
        _sut.FyFrontEnabled = false;
        _sut.MzRearEnabled = false;
        _sut.FxRearEnabled = false;
        _sut.FyRearEnabled = false;
        var result = _sut.Mix(_raw, out var channels);
        result.Should().Be(0f);
    }

    [Fact]
    public void Mix_CenterBlendSuppressesFyAtStraight()
    {
        _sut.FyFrontEnabled = true;
        _sut.FyFrontGain = 1.0f;
        _sut.CenterBlendDegrees = 5.0f;
        _raw.SteerAngle = 0f;
        var result = _sut.Mix(_raw, out var channels);
        channels.FyFront.Should().BeLessThan(Math.Abs(channels.MzFront));
    }

    [Fact]
    public void Mix_SteerAngleIncreasesFyBlend()
    {
        _sut.FyFrontEnabled = true;
        _sut.FyFrontGain = 1.0f;
        _raw.SteerAngle = 0.2f;
        var smallAngle = _sut.Mix(_raw, out var smallChannels);
        _raw.SteerAngle = 1.0f;
        var largeAngle = _sut.Mix(_raw, out var largeChannels);
        Math.Abs(largeChannels.FyFront).Should().BeGreaterThan(Math.Abs(smallChannels.FyFront));
    }

    [Fact]
    public void Mix_MzFrontDisabled_ReturnsZeroMzChannel()
    {
        _sut.MzFrontEnabled = false;
        _sut.Mix(_raw, out var channels);
        channels.MzFront.Should().Be(0f);
    }

    [Fact]
    public void Mix_FyInverted_NegatesFyOutput()
    {
        _sut.FyFrontEnabled = true;
        _sut.FyFrontGain = 1.0f;
        _sut.FyInverted = true;
        var result = _sut.Mix(_raw, out var channels);
        channels.FyFront.Should().BeLessThanOrEqualTo(0f);
    }

    [Fact]
    public void Reset_ClearsInternalState()
    {
        _sut.Mix(_raw, out _);
        _sut.Reset();
        _sut.GetAutoNormDiagnostics().MzPeak.Should().Be(0f);
    }

    [Fact]
    public void Mix_FinalFfEnabled_IncludesFinalFf()
    {
        _sut.FinalFfEnabled = true;
        _sut.FinalFfGain = 0.5f;
        _raw.FinalFf = 0.8f;
        var result = _sut.Mix(_raw, out var channels);
        channels.FinalFf.Should().BeApproximately(0.4f, 1e-5f);
    }
}
