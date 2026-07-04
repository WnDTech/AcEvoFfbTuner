using AcEvoFfbTuner.Core.FfbProcessing;
using FluentAssertions;

namespace AcEvoFfbTuner.Tests;

public class FfbLutCurveTests
{
    private readonly FfbLutCurve _sut = new();

    [Fact]
    public void SetLinear_ZeroInput_ReturnsZero()
    {
        var result = _sut.Apply(0f);
        result.Should().Be(0f);
    }

    [Fact]
    public void SetLinear_MidInput_ReturnsProportional()
    {
        var result = _sut.Apply(0.5f);
        result.Should().BeApproximately(0.5f, 0.02f);
    }

    [Fact]
    public void SetLinear_FullInput_ReturnsFull()
    {
        var result = _sut.Apply(1.0f);
        result.Should().BeApproximately(1.0f, 0.02f);
    }

    [Fact]
    public void SetLinear_NegativeInput_PreservesSign()
    {
        var result = _sut.Apply(-0.7f);
        result.Should().BeNegative();
    }

    [Fact]
    public void SetSoftCenter_ReducesCenterForce()
    {
        _sut.SetSoftCenter(0.05f, 0.3f);
        var atCenter = _sut.Apply(0.25f);
        var atFull = _sut.Apply(1.0f);
        atCenter.Should().BeLessThan(atFull);
    }

    [Fact]
    public void SetProgressive_QuadraticShape()
    {
        _sut.SetProgressive(2.0f);
        var atHalf = _sut.Apply(0.5f);
        atHalf.Should().BeApproximately(0.25f, 0.02f);
    }

    [Fact]
    public void SetDeadZone_FlattensAroundHalf()
    {
        _sut.SetDeadZone(0.1f);
        var atHalf = _sut.Apply(0.5f);
        atHalf.Should().BeApproximately(0.5f, 0.01f);
    }

    [Fact]
    public void Apply_InputAboveMax_ClampsToMax()
    {
        var result = _sut.Apply(2.0f);
        result.Should().BeApproximately(1.0f, 0.02f);
    }

    [Fact]
    public void Apply_NegativeInputAboveMax_ClampsToNegativeMax()
    {
        var result = _sut.Apply(-2.0f);
        result.Should().BeApproximately(-1.0f, 0.02f);
    }

    [Fact]
    public void DefaultConstructor_InitializesLinear()
    {
        var result = _sut.Apply(0.5f);
        result.Should().BeApproximately(0.5f, 0.02f);
    }
}
