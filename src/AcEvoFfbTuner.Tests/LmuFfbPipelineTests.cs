using AcEvoFfbTuner.Core.FfbProcessing;
using AcEvoFfbTuner.Core.FfbProcessing.Models;
using FluentAssertions;

namespace AcEvoFfbTuner.Tests;

/// <summary>
/// LMU core direction (verified across 22 Oct 2026 snapshots): in steady
/// cornering LMU shaft torque runs OPPOSITE to steer, and the output carries
/// the steer sign (centering). In oversteer the game torque turns SAME-as-steer
/// (self-steer into the countersteer) — the native-sign core must follow it
/// instead of centring against the driver's catch.
/// </summary>
public class LmuFfbPipelineTests
{
    private readonly LmuFfbPipeline _sut = new();
    private readonly FfbRawData _raw = new();

    public LmuFfbPipelineTests()
    {
        _raw.FinalFf = -30f;            // Nm, opposite to steer = normal cornering
        _raw.SteerAngle = 0.2f;
        _raw.SpeedKmh = 120f;
        _raw.Gear = 4;
        _raw.GasInput = 0.5f;
        _raw.BrakeInput = 0f;
        _raw.TyreGrip = [1f, 1f, 1f, 1f];
        _raw.SlipRatio = [0f, 0f, 0f, 0f];
        _raw.SlipAngle = [0f, 0f, 0f, 0f];
        _raw.WheelLoad = [5000f, 5000f, 5000f, 5000f];
        _raw.SuspensionTravel = [0f, 0f, 0f, 0f];
        _raw.Mz = [0f, 0f, 0f, 0f];
        _raw.Fy = [0f, 0f, 0f, 0f];
        _raw.AccG = [0f, 0f, 0f];
    }

    [Fact]
    public void NativeSign_NormalCornering_CentresLikeLegacy()
    {
        var native = _sut.Process(_raw);

        var legacyPipe = new LmuFfbPipeline { UseNativeTorqueSign = false };
        var legacy = legacyPipe.Process(_raw);

        native.CoreForce.Should().BePositive();
        legacy.CoreForce.Should().BePositive();
    }

    [Fact]
    public void NativeSign_Oversteer_FollowsGameSelfSteer()
    {
        // Countersteering right (+) while the game torque is also + (self-steer).
        _raw.FinalFf = 40f;
        _raw.SteerAngle = 0.3f;

        var result = _sut.Process(_raw);

        // Must push the same way the game does (away from centre), not centre.
        result.CoreForce.Should().BeNegative();
    }

    [Fact]
    public void SelfSteerGain_ScalesSlideForce_NotCentring()
    {
        _raw.FinalFf = 40f;               // self-steer
        _raw.SteerAngle = 0.3f;
        var full = new LmuFfbPipeline { SelfSteerGain = 1f }.Process(_raw).CoreForce;
        var half = new LmuFfbPipeline { SelfSteerGain = 0.5f }.Process(_raw).CoreForce;
        half.Should().BeApproximately(full * 0.5f, 0.001f);

        _raw.FinalFf = -40f;              // centring — gain must not apply
        var cFull = new LmuFfbPipeline { SelfSteerGain = 1f }.Process(_raw).CoreForce;
        var cHalf = new LmuFfbPipeline { SelfSteerGain = 0.5f }.Process(_raw).CoreForce;
        cHalf.Should().BeApproximately(cFull, 0.0001f);
    }

    [Fact]
    public void SelfSteerGain_LeavesNearCentreTextureUntouched()
    {
        _raw.FinalFf = 4f;
        _raw.SteerAngle = 0.005f;         // ≈2° — inside the ramp-in deadband
        var full = new LmuFfbPipeline { SelfSteerGain = 1f, CenterSharpnessDegrees = 0f }.Process(_raw).CoreForce;
        var zero = new LmuFfbPipeline { SelfSteerGain = 0f, CenterSharpnessDegrees = 0f }.Process(_raw).CoreForce;
        zero.Should().BeApproximately(full, 0.0001f);
    }

    // Run n frames of the same raw and return the per-frame detail force.
    private static float[] RunDetail(LmuFfbPipeline p, FfbRawData raw, int n)
    {
        var d = new float[n];
        for (int i = 0; i < n; i++) d[i] = p.Process(raw).DetailForce;
        return d;
    }

    [Fact]
    public void VibrationEnvelope_SustainedLevel_DoesNotPullWheel()
    {
        // Slip/kerb envelopes are one-sided intensities. Held steady (a long
        // slide), the texture must decay away — no lasting one-way push.
        _raw.KerbVibration = 0f; _raw.SlipVibrations = 0f;
        var quiet = RunDetail(new LmuFfbPipeline(), _raw, 150);

        _raw.KerbVibration = 1f; _raw.SlipVibrations = 1f;
        var loud = RunDetail(new LmuFfbPipeline(), _raw, 150);   // 0.6 s ≫ 40 ms

        loud[^1].Should().BeApproximately(quiet[^1], 0.002f);
    }

    [Fact]
    public void VibrationEnvelope_OnsetKnocks_AndOnOffIsZeroMean()
    {
        var p = new LmuFfbPipeline { DetailSmoothing = 0f };
        _raw.KerbVibration = 0f; _raw.SlipVibrations = 0f;
        var baseLine = RunDetail(p, _raw, 100);

        _raw.KerbVibration = 1f;                       // kerb strike on
        var on = RunDetail(p, _raw, 60);
        _raw.KerbVibration = 0f;                       // and off
        var off = RunDetail(p, _raw, 60);

        float b = baseLine[^1];
        (on[0] - b).Should().BeGreaterThan(0.05f);     // felt as a knock
        float net = on.Sum(v => v - b) + off.Sum(v => v - b);
        float onArea = on.Sum(v => Math.Abs(v - b));
        Math.Abs(net).Should().BeLessThan(onArea * 0.1f); // no net pull
    }

    [Fact]
    public void Texture_StillKnocks_WhenMixerEnvelopeIsSaturated()
    {
        // Violent kerb with spinning/airborne wheels: (kerb 1 + slip 0.8) x 0.7
        // = 1.26 — Mix() clamps that to a constant 1.0. The texture must still
        // follow the kerb changing underneath (it read exactly 0 before).
        var p = new LmuFfbPipeline { DetailSmoothing = 0f };
        _raw.KerbVibration = 1f; _raw.SlipVibrations = 1f;
        var steady = RunDetail(p, _raw, 100);
        _raw.KerbVibration = 0.5f;                     // kerb drops, slip stays
        var change = RunDetail(p, _raw, 1);

        Math.Abs(change[0] - steady[^1]).Should().BeGreaterThan(0.05f);
    }

    [Fact]
    public void LegacySign_Oversteer_CentresAgainstCountersteer()
    {
        // Documents the defect the native mode fixes.
        _sut.UseNativeTorqueSign = false;
        _raw.FinalFf = 40f;
        _raw.SteerAngle = 0.3f;

        var result = _sut.Process(_raw);

        result.CoreForce.Should().BePositive();
    }

    [Fact]
    public void NativeSign_StraightLine_KeepsRoadTorqueIndependentOfSteerNoise()
    {
        // Wheel dead centre: legacy tanh(0)=0 erased the camber/road torque.
        // CenterSharpnessDegrees 0 = the LMU profiles' setting (the 3° base
        // default deliberately ramps the core to zero on centre).
        _sut.CenterSharpnessDegrees = 0f;
        _raw.SteerAngle = 0f;
        _raw.FinalFf = -4f;

        var result = _sut.Process(_raw);

        result.CoreForce.Should().BePositive();
        Math.Abs(result.CoreForce).Should().BeGreaterThan(0.001f);
    }
}
