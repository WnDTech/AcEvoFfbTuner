using AcEvoFfbTuner.Core.FfbProcessing;
using AcEvoFfbTuner.Core.FfbProcessing.Models;
using FluentAssertions;

namespace AcEvoFfbTuner.Tests;

/// <summary>
/// AC1 synthesizer tests. The core force is built from lateral G, speed, load
/// and slip — NOT from FinalFF. Game FFB is 0%, the app drives the wheel.
/// </summary>
public class AcFfbPipelineTests
{
    private readonly AcFfbPipeline _sut = new();
    private readonly FfbRawData _raw = new();

    public AcFfbPipelineTests()
    {
        _raw.SpeedKmh = 120f;
        _raw.SteerAngle = 0.3f;
        _raw.Gear = 4;
        _raw.GasInput = 0.8f;
        _raw.BrakeInput = 0f;
        _raw.TyreGrip = [1f, 1f, 1f, 1f];
        _raw.SlipRatio = [0f, 0f, 0f, 0f];
        _raw.SlipAngle = [0f, 0f, 0f, 0f];
        _raw.WheelLoad = [3500f, 3500f, 3500f, 3500f];
        _raw.SuspensionTravel = [0f, 0f, 0f, 0f];
        _raw.Mz = [0f, 0f, 0f, 0f];
        _raw.Fx = [0f, 0f, 0f, 0f];
        _raw.Fy = [0f, 0f, 0f, 0f];
        _raw.AccG = [5f, 0f, 0f]; // 5 m/s² lateral (~0.5G)
        _raw.SteerDegrees = 900;
    }

    [Fact]
    public void Synth_ProducesForceFromLateralG()
    {
        var result = _sut.Process(_raw);
        // Lateral G should produce non-zero core force
        Math.Abs(result.CoreForce).Should().BeGreaterThan(0.001f);
    }

    [Fact]
    public void Synth_CoreOpposesSteerSign()
    {
        var result = _sut.Process(_raw);
        // Positive steer → force should be negative (centering)
        result.CoreForce.Should().BeNegative();
    }

    [Fact]
    public void Synth_CoreFlipsWithSteer()
    {
        _raw.SteerAngle = -0.3f;
        var result = _sut.Process(_raw);
        result.CoreForce.Should().BePositive();
    }

    [Fact]
    public void Synth_CoreZeroAtStandstill()
    {
        _raw.SpeedKmh = 0f;
        var result = _sut.Process(_raw);
        result.CoreForce.Should().Be(0f);
        // Main force may have tiny detail contributions from texture stages
        Math.Abs(result.MainForce).Should().BeLessThan(0.05f);
    }

    [Fact]
    public void Synth_CoreZeroWithNoLateralG()
    {
        _raw.AccG = [0f, 0f, 0f];
        // Disable grip guard so it doesn't add trail force
        _sut.GripGuard.Enabled = false;
        var result = _sut.Process(_raw);
        // Core is zero; detail stages add small texture (expected)
        Math.Abs(result.CoreForce).Should().BeLessThan(0.01f);
    }

    [Fact]
    public void Synth_MagnitudeTracksLateralG()
    {
        _raw.AccG = [3f, 0f, 0f];
        var r1 = _sut.Process(_raw);
        _raw.AccG = [10f, 0f, 0f];
        _sut.Reset();
        var r2 = _sut.Process(_raw);
        Math.Abs(r2.CoreForce).Should().BeGreaterThan(Math.Abs(r1.CoreForce));
    }

    [Fact]
    public void Braking_BoostsCore()
    {
        var noBrake = _sut.Process(_raw);
        _raw.BrakeInput = 0.8f;
        _sut.Reset();
        var withBrake = _sut.Process(_raw);
        Math.Abs(withBrake.CoreForce).Should().BeGreaterThan(Math.Abs(noBrake.CoreForce));
    }

    [Fact]
    public void GripLoss_LightensCore()
    {
        var fullGrip = _sut.Process(_raw);
        _raw.TyreGrip = [0.4f, 0.4f, 1f, 1f];
        _sut.Reset();
        var lowGrip = _sut.Process(_raw);
        Math.Abs(lowGrip.CoreForce).Should().BeLessThanOrEqualTo(Math.Abs(fullGrip.CoreForce));
    }
}
