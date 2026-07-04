using AcEvoFfbTuner.Core.FfbProcessing;
using FluentAssertions;

namespace AcEvoFfbTuner.Tests;

public class FfbOutputClipperTests
{
    private readonly FfbOutputClipper _sut = new();

    [Fact]
    public void Process_PassthroughBelowThreshold_ReturnsSameForce()
    {
        var result = _sut.Process(0.5f, out bool clipping);
        result.Should().BeApproximately(0.5f, 1e-6f);
        clipping.Should().BeFalse();
    }

    [Fact]
    public void Process_PassthroughZeroInput_ReturnsZero()
    {
        var result = _sut.Process(0f, out bool clipping);
        result.Should().Be(0f);
        clipping.Should().BeFalse();
    }

    [Fact]
    public void Process_AboveThreshold_SoftClips()
    {
        var result = _sut.Process(0.95f, out bool clipping);
        clipping.Should().BeTrue();
        result.Should().BeLessThan(0.95f);
    }

    [Fact]
    public void Process_AboveThreshold_DoesNotExceedOne()
    {
        var result = _sut.Process(2.0f, out _);
        result.Should().BeInRange(-1f, 1f);
    }

    [Fact]
    public void Process_NegativeAboveThreshold_SoftClips()
    {
        var result = _sut.Process(-0.95f, out bool clipping);
        clipping.Should().BeTrue();
        result.Should().BeGreaterThan(-0.95f);
    }

    [Fact]
    public void Process_AtThreshold_DoesNotClip()
    {
        _sut.SoftClipThreshold = 0.8f;
        var result = _sut.Process(0.8f, out bool clipping);
        clipping.Should().BeFalse();
        result.Should().BeApproximately(0.8f, 1e-6f);
    }

    [Fact]
    public void Process_NegativeBelowThreshold_ReturnsSameForce()
    {
        var result = _sut.Process(-0.3f, out bool clipping);
        result.Should().BeApproximately(-0.3f, 1e-6f);
        clipping.Should().BeFalse();
    }
}
