using AcEvoFfbTuner.Core.FfbProcessing;
using FluentAssertions;

namespace AcEvoFfbTuner.Tests;

public class FfbDampingTests
{
    private readonly FfbDamping _sut = new();

    [Fact]
    public void Apply_ZeroForceAndSpeed_ReturnsForcePlusNoDamping()
    {
        var result = _sut.Apply(0.1f, 0f, 0f);
        result.Should().BeGreaterThan(0f);
    }

    [Fact]
    public void Apply_IncreasingSpeed_ChangesDamping()
    {
        _sut.SpeedDampingCoefficient = 1.0f;
        _sut.ViscousCoefficient = 0f;
        _sut.FrictionLevel = 0f;
        _sut.InertiaWeight = 0f;
        _sut.Reset();
        var lowSpeed = _sut.Apply(1.0f, 10f, 0.1f);
        _sut.Reset();
        var highSpeed = _sut.Apply(1.0f, 200f, 0.1f);
        lowSpeed.Should().NotBe(highSpeed);
    }

    [Fact]
    public void Apply_SteerVelocityBelowDeadzone_SkipsDamping()
    {
        _sut.VelocityDeadzone = 1.0f;
        _sut.SteerVelocityReference = 1.0f;
        var result = _sut.Apply(0.5f, 50f, 0f);
        result.Should().BeApproximately(0.5f, 1e-4f);
    }

    [Fact]
    public void Apply_NegativeSteerVelocity_ProducesOpposingForce()
    {
        var result = _sut.Apply(0.5f, 50f, -0.1f);
        result.Should().NotBe(0.5f);
    }

    [Fact]
    public void Apply_ViscousCoefficientZero_StillHasMinFloor()
    {
        _sut.ViscousCoefficient = 0f;
        _sut.FrictionLevel = 0f;
        _sut.SpeedDampingCoefficient = 0f;
        _sut.InertiaWeight = 0f;
        var result = _sut.Apply(0.5f, 50f, 0.1f);
        result.Should().NotBe(0.5f);
    }

    [Fact]
    public void Reset_ClearsState()
    {
        _sut.Apply(0.5f, 50f, 0.1f);
        _sut.Reset();
        var result = _sut.Apply(0.5f, 50f, 0.1f);
        result.Should().NotBe(0.5f);
    }

    [Fact]
    public void Apply_ReturnsForceWithinExpectedRange()
    {
        var result = _sut.Apply(1.0f, 100f, 0.2f);
        result.Should().BeInRange(0f, 1.5f);
    }
}
