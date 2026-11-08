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
        _raw.FinalFf = -0.5f;           // R3E SteeringForcePercentage: centering torque is OPPOSITE steer (verified live 2026-11-08)
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
        _raw.FinalFf = 0.5f; // game centering torque flips with steer
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
    public void KerbStrikeDecay_DefaultsToFifteen()
    {
        _sut.KerbStrikeDecayMs.Should().Be(15f);
    }

    private static FfbRawData FreshRaw()
    {
        return new FfbRawData
        {
            FinalFf = -0.5f, // centering torque opposite steer (live 2026-11-08)
            SteerAngle = 0.05f, // below comp-boost threshold
            SpeedKmh = 120f,
            Gear = 4,
            GasInput = 0.8f,
            BrakeInput = 0f,
            TyreGrip = [1f, 1f, 1f, 1f],
            SlipRatio = [0f, 0f, 0f, 0f],
            SlipAngle = [0f, 0f, 0f, 0f],
            WheelLoad = [5000f, 5000f, 5000f, 5000f],
            SuspensionTravel = [0f, 0f, 0f, 0f],
            Mz = [0f, 0f, 0f, 0f],
            AccG = [0f, 0f, 0f],
        };
    }

    private float KerbTail(R3eFfbPipeline pipe, float decayMs)
    {
        // Sustained 0.4 kerb step; tail-vs-onset ratio. Short decay must
        // leave far less tail than long decay (rigid hits vs blur).
        pipe.Reset();
        pipe.KerbStrikeDecayMs = decayMs;
        var raw = FreshRaw();
        raw.SteerAngle = 0.05f; // below comp-boost threshold
        raw.KerbVibration = 0f;
        for (int i = 0; i < 10; i++) pipe.Process(raw); // settle envelope at 0
        raw.KerbVibration = 0.4f;
        var outs = new List<float>();
        for (int i = 0; i < 20; i++) outs.Add(Math.Abs(pipe.Process(raw).VibrationForce));
        float onset = (outs[0] + outs[1] + outs[2]) / 3f;
        float tail = 0f;
        for (int i = 10; i < 15; i++) tail += outs[i];
        tail /= 5f;
        return tail / Math.Max(onset, 1e-6f);
    }

    [Fact]
    public void KerbStrike_ShorterDecay_EndsSooner()
    {
        KerbTail(new R3eFfbPipeline(), 15f).Should().BeLessThan(KerbTail(new R3eFfbPipeline(), 60f) * 0.5f);
    }

    [Fact]
    public void KerbStrike_OnsetPassesWhileWashFades()
    {
        // Strike attack passes unsmoothed regardless of decay, then the
        // sustained wash fades: onset equal across decays, settled << onset.
        // (Relative assertions only — absolute level includes fixture-wide
        // detail offsets that are identical across legs.)
        static (float onset, float settled) Run(float decayMs)
        {
            var pipe = new R3eFfbPipeline { KerbStrikeDecayMs = decayMs };
            var raw = FreshRaw();
            raw.KerbVibration = 0f;
            for (int i = 0; i < 10; i++) pipe.Process(raw);
            raw.KerbVibration = 0.4f;
            float onset = 0f;
            for (int i = 0; i < 3; i++) onset += Math.Abs(pipe.Process(raw).VibrationForce);
            onset /= 3f;
            float settled = 0f;
            for (int i = 3; i < 40; i++) settled = Math.Abs(pipe.Process(raw).VibrationForce);
            return (onset, settled);
        }
        var fast = Run(15f);
        var slow = Run(60f);
        fast.onset.Should().BeApproximately(slow.onset, 0.05f);
        fast.settled.Should().BeLessThan(fast.onset * 0.4f);
    }

    [Fact]
    public void KerbStrike_ResetClearsEnvelope()
    {
        var pipe = new R3eFfbPipeline();
        var raw = FreshRaw();
        raw.SteerAngle = 0.05f;
        raw.KerbVibration = 0.4f;
        float settled = 0f;
        for (int i = 0; i < 30; i++) settled = Math.Abs(pipe.Process(raw).VibrationForce);
        pipe.Reset();
        float reopened = Math.Abs(pipe.Process(raw).VibrationForce);
        reopened.Should().BeGreaterThan(settled + 0.2f);
    }

    [Fact]
    public void BrakeShimmer_DefaultsToOne()
    {
        _sut.BrakeShimmerGain.Should().Be(1.0f);
    }

    private static float ShimmerRange(R3eFfbPipeline pipe, float brake, float speedKmh = 150f)
    {
        // Peak-to-peak DiagSlipVib over ~1.8 shimmer cycles. (VibrationForce
        // is mixer-only; the shimmer reports through DiagSlipVib.)
        var raw = FreshRaw();
        raw.BrakeInput = brake;
        raw.SpeedKmh = speedKmh;
        float lo = float.MaxValue, hi = float.MinValue;
        for (int i = 0; i < 40; i++)
        {
            float v = pipe.Process(raw).DiagSlipVib;
            lo = Math.Min(lo, v);
            hi = Math.Max(hi, v);
        }
        return hi - lo;
    }

    [Fact]
    public void BrakeShimmer_SilentWithoutBrake()
    {
        ShimmerRange(new R3eFfbPipeline(), 0f).Should().BeLessThan(0.01f);
    }

    [Fact]
    public void BrakeShimmer_OscillatesUnderFirmBraking()
    {
        var pipe = new R3eFfbPipeline();
        float quiet = ShimmerRange(pipe, 0f);
        pipe.Reset();
        float braking = ShimmerRange(pipe, 0.8f);
        braking.Should().BeGreaterThan(quiet + 0.04f); // oscillation appears
        braking.Should().BeLessThan(0.15f); // capped patter, not lockup buzz
    }

    [Fact]
    public void BrakeShimmer_GatedBySpeed()
    {
        ShimmerRange(new R3eFfbPipeline(), 0.8f, 30f).Should().BeLessThan(0.01f);
    }

    [Fact]
    public void BrakeShimmer_ZeroGainDisables()
    {
        var pipe = new R3eFfbPipeline { BrakeShimmerGain = 0f };
        ShimmerRange(pipe, 0.8f).Should().BeLessThan(0.01f);
    }

    [Fact]
    public void CenterFade_ScalesTextureWithSteerAuthority()
    {
        // Sustained slip texture at near-zero steer must arrive much smaller
        // than the same input with steer applied — otherwise detail dominates
        // the near-zero core through crossings (wrong-sign pushes + snaps).
        static float SettledSlip(float steer, float slip)
        {
            var pipe = new R3eFfbPipeline();
            var raw = FreshRaw();
            raw.SteerAngle = steer;
            raw.SlipVibrations = 0f;
            for (int i = 0; i < 10; i++) pipe.Process(raw);
            float baseline = pipe.Process(raw).VibrationForce;
            raw.SlipVibrations = slip;
            float v = baseline;
            for (int i = 0; i < 60; i++) v = pipe.Process(raw).VibrationForce;
            return v - baseline; // offset-immune: relative to no-input baseline
        }
        float center = SettledSlip(0.004f, 0.5f);
        float corner = SettledSlip(0.05f, 0.5f);
        corner.Should().BeGreaterThan(0.15f); // full-strength reference arrives
        (center / corner).Should().BeApproximately(0.004f / 0.015f, 0.12f);
    }

    [Fact]
    public void CenterFade_FullPassAboveThreshold()
    {
        static float Settled(float steer)
        {
            var pipe = new R3eFfbPipeline();
            var raw = FreshRaw();
            raw.SteerAngle = steer;
            raw.SlipVibrations = 0.5f;
            float v = 0f;
            for (int i = 0; i < 60; i++) v = pipe.Process(raw).VibrationForce;
            return v;
        }
        (Settled(0.02f) / Settled(0.05f)).Should().BeApproximately(1f, 0.1f);
    }

    [Fact]
    public void CenterFade_SymmetricAcrossCoreSign()
    {
        // The fade depends on |steer| only: identical texture arrives whether
        // the core pulls left or right, so no directional bias (rocking) can
        // develop. (Mixer output is unipolar by design — clamp [0,1] — so
        // bipolar character lives in the Dynamic/road-mod channels instead.)
        static float Settled(float steer)
        {
            var pipe = new R3eFfbPipeline();
            var raw = FreshRaw();
            raw.SteerAngle = steer;
            raw.SlipVibrations = 0.5f;
            float v = 0f;
            for (int i = 0; i < 60; i++) v = pipe.Process(raw).VibrationForce;
            return v;
        }
        Settled(0.004f).Should().BeApproximately(Settled(-0.004f), 0.02f);
    }

    [Fact]
    public void CoreFloor_DefaultsToFiveHundredths()
    {
        _sut.CoreFloor.Should().Be(0.05f);
    }

    [Fact]
    public void CoreFloor_FillsCrossingDip()
    {
        // Game torque collapses to ~0.003 at center crossings; the floor
        // keeps tissue (0.05 pre-scale) instead of a dry riverbed.
        var pipe = new R3eFfbPipeline { UseNativeTorqueSign = false };
        var raw = FreshRaw();
        raw.FinalFf = 0.003f;
        float core = Math.Abs(pipe.Process(raw).CoreForce);
        core.Should().BeGreaterThan(0.03f);
        core.Should().BeLessThan(0.08f);
    }

    [Fact]
    public void CoreFloor_DoesNotBindNormalSignals()
    {
        var pipe = new R3eFfbPipeline { UseNativeTorqueSign = false };
        var raw = FreshRaw();
        raw.FinalFf = 0.5f;
        Math.Abs(pipe.Process(raw).CoreForce).Should().BeApproximately(0.5f, 0.03f);
    }

    [Fact]
    public void CoreFloor_UnloadsStillFadeDeeply()
    {
        // Floor applies BEFORE grip scaling: zero grip still cuts ~20x.
        // (0.0125 from the chain + ~0.005 mechanical trail, hence 0.025.)
        var pipe = new R3eFfbPipeline { UseNativeTorqueSign = false };
        var raw = FreshRaw();
        raw.FinalFf = 0.003f;
        raw.TyreGrip = [0f, 0f, 1f, 1f];
        Math.Abs(pipe.Process(raw).CoreForce).Should().BeLessThan(0.025f);
    }

    [Fact]
    public void CoreFloor_StandstillStillZero()
    {
        var pipe = new R3eFfbPipeline { UseNativeTorqueSign = false };
        var raw = FreshRaw();
        raw.FinalFf = 0.003f;
        raw.SpeedKmh = 0f;
        pipe.Process(raw).CoreForce.Should().Be(0f);
    }

    [Fact]
    public void CenterBlend_DefaultsToOnePercent()
    {
        _sut.CenterDirectionBlend.Should().Be(0.01f);
    }

    [Fact]
    public void CenterBlend_SweepThroughZeroHasNoStep()
    {
        // The felt notch: a hard sign() step flips the full force in one
        // tick at exactly zero. Sweeping -0.02..+0.02 in fine steps must
        // show no single-frame jump (real steering builds torque smoothly).
        var pipe = new R3eFfbPipeline { UseNativeTorqueSign = false };
        float prev = 0f;
        bool first = true;
        float maxJump = 0f;
        for (int i = -20; i <= 20; i++)
        {
            var raw = FreshRaw();
            raw.FinalFf = 0.3f;
            raw.SteerAngle = i * 0.001f;
            float core = pipe.Process(raw).CoreForce;
            if (!first) maxJump = Math.Max(maxJump, Math.Abs(core - prev));
            first = false;
            prev = core;
        }
        // Sweep -0.02..+0.02 in 0.001 steps: smoothstep ramp spreads the
        // flip over ~20 frames. Bound 0.08 (old hard step jumped ~0.6 here).
        maxJump.Should().BeLessThan(0.08f);
    }

    [Fact]
    public void CenterBlend_PreservesSteerSideConvention()
    {
        // The verified 28/28 rule survives: force follows the steer side,
        // only the zero instant is softened.
        var pipe = new R3eFfbPipeline { UseNativeTorqueSign = false };
        var raw = FreshRaw();
        raw.FinalFf = 0.4f;
        raw.SteerAngle = 0.15f;
        pipe.Process(raw).CoreForce.Should().BePositive();
        raw.SteerAngle = -0.15f;
        pipe.Process(raw).CoreForce.Should().BeNegative();
    }

    [Fact]
    public void CenterBlend_ZeroAtZero()
    {
        var pipe = new R3eFfbPipeline { UseNativeTorqueSign = false };
        var raw = FreshRaw();
        raw.FinalFf = 0.4f;
        raw.SteerAngle = 0f;
        Math.Abs(pipe.Process(raw).CoreForce).Should().BeLessThan(0.005f);
    }

    [Fact]
    public void CenterBlend_FullAuthorityOutsideBlend()
    {
        static float CoreAt(float steer)
        {
            var pipe = new R3eFfbPipeline { UseNativeTorqueSign = false };
            var raw = FreshRaw();
            raw.FinalFf = 0.4f;
            raw.SteerAngle = steer;
            return Math.Abs(pipe.Process(raw).CoreForce);
        }
        (CoreAt(0.05f) / CoreAt(0.03f)).Should().BeApproximately(1f, 0.05f);
    }

    [Fact]
    public void CenterFade_AttenuatesLfeAtCenter()
    {
        // Engine drone was the loudest center channel in snapshots; it must
        // fade with all other texture (flatspot/ABS/rear/shimmer stay exempt).
        static float AvgLfe(float steer)
        {
            var pipe = new R3eFfbPipeline();
            pipe.LfeGenerator.Enabled = true;
            var raw = FreshRaw();
            raw.SteerAngle = steer;
            raw.RpmPercent = 60f;
            raw.EngineTorque = 400f;
            float s = 0f;
            for (int i = 0; i < 60; i++) s += Math.Abs(pipe.Process(raw).DiagLfe);
            return s / 60f;
        }
        float center = AvgLfe(0.004f);
        float corner = AvgLfe(0.05f);
        corner.Should().BeGreaterThan(0.01f); // LFE alive in fixture
        (center / corner).Should().BeApproximately(0.004f / 0.015f, 0.12f);
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

    // ── Road compression (2026-11-07 Lakeview) ──

    private float CoreAfterHeave(R3eFfbPipeline p, float steer, float heave)
    {
        var raw = FreshRaw();
        raw.SteerAngle = steer;
        raw.FinalFf = -0.5f * Math.Sign(steer); // centering torque opposite steer
        raw.SuspensionTravel = [0.03f, 0.03f, 0.05f, 0.05f];
        for (int i = 0; i < 400; i++) { raw.PacketId = i; p.Process(raw); }   // settle baseline
        raw.SuspensionTravel = [0.03f + heave, 0.03f + heave, 0.05f, 0.05f];
        float core = 0f;
        for (int i = 400; i < 420; i++) { raw.PacketId = i; core = p.Process(raw).CoreForce; }
        return core;
    }

    [Theory]
    [InlineData(0.3f)]
    [InlineData(-0.3f)]
    public void RoadCompression_CrestAddsWeight_InSteerDirection(float steer)
    {
        float flat = CoreAfterHeave(new R3eFfbPipeline(), steer, 0f);
        float crest = CoreAfterHeave(new R3eFfbPipeline(), steer, 0.02f);
        float dip = CoreAfterHeave(new R3eFfbPipeline(), steer, -0.02f);
        Math.Sign(crest).Should().Be(Math.Sign(steer));
        Math.Sign(dip).Should().Be(Math.Sign(steer));
        Math.Abs(crest).Should().BeGreaterThan(Math.Abs(flat) * 1.1f);
        Math.Abs(dip).Should().BeLessThan(Math.Abs(flat) * 0.9f);
    }

    [Fact]
    public void RoadCompression_GainZero_NoEffect()
    {
        var a = new R3eFfbPipeline { RoadCompressionGain = 0f, BumpTwistGain = 0f };
        var b = new R3eFfbPipeline { RoadCompressionGain = 0f, BumpTwistGain = 0f };
        float flat = CoreAfterHeave(a, 0.3f, 0f);
        float crest = CoreAfterHeave(b, 0.3f, 0.02f);
        crest.Should().BeApproximately(flat, 1e-4f);
    }

    [Fact]
    public void BumpTwist_LeftFrontHit_PushesPositive()
    {
        var p = new R3eFfbPipeline { RoadCompressionGain = 0f, BumpTwistGain = 1f };
        var raw = FreshRaw();
        raw.SteerAngle = 0f;
        raw.SuspensionTravel = [0.03f, 0.03f, 0.05f, 0.05f];
        for (int i = 0; i < 50; i++) { raw.PacketId = i; p.Process(raw); }
        float maxTwist = 0f;
        for (int i = 50; i < 60; i++)
        {
            raw.PacketId = i;
            raw.SuspensionTravel = [0.03f + (i - 49) * 0.002f, 0.03f, 0.05f, 0.05f]; // FL compressing 0.8 m/s
            p.Process(raw);
            maxTwist = Math.Max(maxTwist, p.DiagBumpTwist);
        }
        maxTwist.Should().BeGreaterThan(0.01f);
    }

    // ── Native torque sign (2026-11-08) ──

    [Fact]
    public void NativeSign_CenteringTorque_MatchesLegacyConvention()
    {
        // Game centering torque points AWAY from steer: steer right (+), pct negative.
        var p = new R3eFfbPipeline { UseNativeTorqueSign = true, RoadCompressionGain = 0f };
        var raw = FreshRaw(); raw.SteerAngle = 0.05f; raw.FinalFf = -0.4f;
        p.Process(raw).CoreForce.Should().BePositive();
    }

    [Fact]
    public void NativeSign_SelfSteerAwayFromCenter_PassesThrough()
    {
        // Camber/crown pull: game torque same sign as steer → output follows the game, not steer.
        var p = new R3eFfbPipeline { UseNativeTorqueSign = true, RoadCompressionGain = 0f };
        var raw = FreshRaw(); raw.SteerAngle = 0.008f; raw.FinalFf = 0.06f;
        p.Process(raw).CoreForce.Should().BeNegative();
    }

    [Fact]
    public void LegacySign_IgnoresGameSign()
    {
        var p = new R3eFfbPipeline { UseNativeTorqueSign = false, RoadCompressionGain = 0f };
        var raw = FreshRaw(); raw.SteerAngle = 0.05f; raw.FinalFf = 0.4f;
        p.Process(raw).CoreForce.Should().BePositive();
    }

    // ── Grip knee (2026-11-08) ──

    [Fact]
    public void GripKnee_NormalCorneringGrip_NoExtraCut()
    {
        var full = new R3eFfbPipeline { RoadCompressionGain = 0f };
        var cornering = new R3eFfbPipeline { RoadCompressionGain = 0f };
        var a = FreshRaw(); var b = FreshRaw();
        b.TyreGrip = [0.5f, 0.5f, 0.5f, 0.5f];   // Historic Update mid-corner reading
        Math.Abs(cornering.Process(b).CoreForce).Should().BeApproximately(Math.Abs(full.Process(a).CoreForce), 1e-4f);
    }

    [Fact]
    public void GripKnee_AirborneGrip_StillFades()
    {
        var full = new R3eFfbPipeline { RoadCompressionGain = 0f };
        var air = new R3eFfbPipeline { RoadCompressionGain = 0f };
        var a = FreshRaw(); var b = FreshRaw();
        b.TyreGrip = [0f, 0f, 1f, 1f];           // fronts unloaded over a crest
        Math.Abs(air.Process(b).CoreForce).Should().BeLessThan(Math.Abs(full.Process(a).CoreForce) * 0.3f);
    }
}