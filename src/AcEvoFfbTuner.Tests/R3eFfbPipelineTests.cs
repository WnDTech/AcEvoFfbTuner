using AcEvoFfbTuner.Core.FfbProcessing;
using AcEvoFfbTuner.Core.FfbProcessing.Models;
using FluentAssertions;

namespace AcEvoFfbTuner.Tests;

/// <summary>
/// Regression tests for the R3E centering convention (verified 2026-08-16
/// on hardware + live telemetry): R3E reports steer positive to the RIGHT,
/// and a positive output pushes LEFT on the device pass-through — so the
/// centering force carries the SAME sign as the steer angle.
/// A flipped core (opposite sign) was user-confirmed as a full pull-away
/// into the corner (snapshot: 28/28 corner frames opposite-signed).
/// </summary>
public class R3eFfbPipelineTests
{
    private readonly R3eFfbPipeline _sut = new();
    private readonly FfbRawData _raw = new();

    public R3eFfbPipelineTests()
    {
        _raw.FinalFf = 0.5f;            // R3E SteeringForcePercentage (0-100%, here 50%)
        _raw.SteerAngle = 0.3f;         // turned right
        _raw.SpeedKmh = 120f;
        _raw.Gear = 4;
        _raw.GasInput = 0.8f;
        _raw.BrakeInput = 0f;
        _raw.TyreGrip = [1f, 1f, 1f, 1f];
        _raw.SlipRatio = [0f, 0f, 0f, 0f];
        _raw.SlipAngle = [0f, 0f, 0f, 0f];
        _raw.WheelLoad = [5000f, 5000f, 5000f, 5000f];
        _raw.SuspensionTravel = [0f, 0f, 0f, 0f];
        _raw.Mz = [0f, 0f, 0f, 0f];
        _raw.AccG = [0f, 0f, 0f];
    }

    [Fact]
    public void Core_MatchesSteerSign_TowardCenter()
    {
        var result = _sut.Process(_raw);
        // steer +0.3 (right) → centering force carries the SAME sign → positive
        result.CoreForce.Should().BePositive();
        result.MainForce.Should().BePositive();
    }

    [Fact]
    public void Core_FlipsDirectionWithSteer()
    {
        _raw.SteerAngle = -0.3f;
        var result = _sut.Process(_raw);
        result.CoreForce.Should().BeNegative();
    }

    [Fact]
    public void Core_ZeroAtCenter()
    {
        _raw.SteerAngle = 0f;
        var result = _sut.Process(_raw);
        Math.Abs(result.CoreForce).Should().BeLessThan(0.001f);
    }

    [Fact]
    public void Core_MagnitudeTracksSteeringForce()
    {
        var r1 = _sut.Process(_raw);
        _raw.FinalFf = 0.9f;
        _sut.Reset();
        var r2 = _sut.Process(_raw);
        Math.Abs(r2.CoreForce).Should().BeGreaterThan(Math.Abs(r1.CoreForce));
    }

    [Fact]
    public void Braking_AddsHeavinessToCore()
    {
        // Regression: R3E's SteeringForce barely responds to brake load, so
        // braking must add an absolute toward-center heaviness (2026-08-16:
        // output/SteeringForce ratio dropped to 0.59 under braking before fix).
        var noBrake = _sut.Process(_raw);
        _raw.BrakeInput = 0.8f;
        var withBrake = _sut.Process(_raw);
        Math.Abs(withBrake.CoreForce).Should().BeGreaterThan(Math.Abs(noBrake.CoreForce) + 0.05f);
    }

    [Fact]
    public void Braking_DetailIsNotNegative_LongitudinalMagnitude()
    {
        // R3E GForce.Z is negative during braking (driver pushed forward).
        // With AbsLongitudinalG the dynamic term must ADD heaviness instead
        // of subtracting (detail was -0.1..-0.27 while braking in data).
        _sut.DynamicEffects.LongitudinalGGain = 0.1f;
        _raw.AccG = [0f, -1.0f, 0f]; // braking deceleration
        var r1 = _sut.Process(_raw);
        _sut.Reset();
        _raw.AccG = [0f, 0f, 0f];
        var r2 = _sut.Process(_raw);
        r1.DetailForce.Should().BeGreaterThan(r2.DetailForce - 0.01f);
    }

    [Fact]
    public void GripGuard_DoesNotClampCenteringForceAtHighSlip()
    {
        // R3E convention: the centering force matches the steer sign, so
        // GripGuard must NOT classify it as "pulling away" and clamp it when
        // front slip exceeds the peak slip angle. Regression for the
        // CenteringForceMatchesSteerSign flag (defaults false = EVO behavior).
        _sut.GripGuard.PeakSlipAngle = 0.1f;
        _sut.GripGuard.AttenuationStrength = 0.3f;

        // Warm up the peak-force reference with low slip.
        for (int i = 0; i < 5; i++)
            _sut.Process(_raw);

        // High slip (0.3 rad, well past peak) must not chop the core force.
        // Core = |0.5| * 1.0 (default multiplier) + 0.015 trail ≈ 0.515.
        // Without the convention flag GripGuard would clamp it to ~0.36.
        float[] highSlip = { 0.3f, 0.3f, 0.3f, 0.3f };
        _raw.SlipAngle = highSlip;
        float minForce = 1f;
        for (int i = 0; i < 15; i++)
        {
            var r = _sut.Process(_raw);
            minForce = Math.Min(minForce, Math.Abs(r.CoreForce));
        }
        minForce.Should().BeGreaterThan(0.45f);
    }
}
