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
        _raw.FinalFf = 0.5f;            // R3E SteeringForcePercentage (0-1 normalized)
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
    public void SteeringAngleComp_DefaultsOn()
    {
        _sut.SteeringAngleCompensationEnabled.Should().BeTrue();
    }

    [Fact]
    public void SteeringAngleComp_Off_RemovesHighSteerBoost()
    {
        // At 0.3 steer (above the 0.20 full-boost point) the legacy path
        // applies 1.15x. Disabling must remove exactly that factor so the
        // Historic Update's own dynamic SAT curve comes through untouched.
        _raw.FinalFf = 0.5f;
        _raw.SteerAngle = 0.3f;
        _sut.SteeringAngleCompensationEnabled = true;
        var on = _sut.Process(_raw);
        _sut.Reset();
        _sut.SteeringAngleCompensationEnabled = false;
        var off = _sut.Process(_raw);
        Math.Abs(off.CoreForce).Should().BeLessThan(Math.Abs(on.CoreForce));
        (Math.Abs(on.CoreForce) / Math.Abs(off.CoreForce)).Should().BeApproximately(1.15f, 0.02f);
    }

    [Fact]
    public void SteeringAngleComp_Off_LowSteerUnchanged()
    {
        // Below 0.08 steer neither path boosts — toggle must be a no-op there.
        _raw.FinalFf = 0.5f;
        _raw.SteerAngle = 0.05f;
        _sut.SteeringAngleCompensationEnabled = true;
        var on = _sut.Process(_raw);
        _sut.Reset();
        _sut.SteeringAngleCompensationEnabled = false;
        var off = _sut.Process(_raw);
        Math.Abs(on.CoreForce - off.CoreForce).Should().BeLessThan(0.001f);
    }

    [Fact]
    public void GripRelease_DefaultsToThree()
    {
        _sut.GripReleaseRate.Should().Be(3.0f);
    }

    [Fact]
    public void GripRelease_SnapsDownInstantlyOnUnload()
    {
        _raw.SteerAngle = 0.05f; // below comp-boost threshold
        _sut.Process(_raw); // loaded: scale 1 → out ≈ 0.5
        _raw.TyreGrip = [0f, 0f, 1f, 1f];
        var unloaded = _sut.Process(_raw);
        // target 0.25 applies in the SAME frame — unload feel preserved
        Math.Abs(unloaded.CoreForce).Should().BeApproximately(0.125f, 0.015f);
    }

    [Fact]
    public void GripRelease_RisesAtCappedRate()
    {
        _raw.SteerAngle = 0.05f;
        _raw.TyreGrip = [0f, 0f, 1f, 1f];
        _sut.Process(_raw); // snap down to 0.25
        _raw.TyreGrip = [1f, 1f, 1f, 1f];
        var first = _sut.Process(_raw);
        // rise ≤ rate*dt = 3/400 ≈ 0.0075 → scale ≤ 0.2575 → out ≈ 0.129
        Math.Abs(first.CoreForce).Should().BeGreaterThan(0.11f);
        Math.Abs(first.CoreForce).Should().BeLessThan(0.15f);
    }

    [Fact]
    public void GripRelease_RecoversFullyGivenTime()
    {
        _raw.SteerAngle = 0.05f;
        _raw.TyreGrip = [0f, 0f, 1f, 1f];
        _sut.Process(_raw); // snap down to 0.25
        _raw.TyreGrip = [1f, 1f, 1f, 1f];
        var last = _sut.Process(_raw);
        for (int i = 1; i <= 200; i++)
        {
            _raw.PacketId = i;
            last = _sut.Process(_raw);
        }
        // 200 frames × 3/400 = 1.5 ≥ 0.75 deficit → fully recovered
        Math.Abs(last.CoreForce).Should().BeApproximately(0.5f, 0.03f);
    }

    [Fact]
    public void GripRelease_ResetRestoresFullScale()
    {
        _raw.SteerAngle = 0.05f;
        _raw.TyreGrip = [0f, 0f, 1f, 1f];
        _sut.Process(_raw); // snap down to 0.25
        _sut.Reset();
        _raw.TyreGrip = [1f, 1f, 1f, 1f];
        var result = _sut.Process(_raw);
        Math.Abs(result.CoreForce).Should().BeApproximately(0.5f, 0.03f);
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
