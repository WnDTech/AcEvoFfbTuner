using AcEvoFfbTuner.Core.FfbProcessing.Models;

namespace AcEvoFfbTuner.Core.FfbProcessing;

public sealed class FfbChannelMixer
{
    public float MzScale { get; set; } = 25f;
    public float FxScale { get; set; } = 5000f;
    public float FyScale { get; set; } = 5000f;
    public float LoadReference { get; set; } = 5000f;

    public float MzFrontGain { get; set; } = 1.0f;
    public bool MzFrontEnabled { get; set; } = true;
    public float MzRearGain { get; set; } = 0.0f;
    public bool MzRearEnabled { get; set; } = false;

    public float FxFrontGain { get; set; } = 0.0f;
    public bool FxFrontEnabled { get; set; } = false;
    public float FxRearGain { get; set; } = 0.0f;
    public bool FxRearEnabled { get; set; } = false;

    public float FyFrontGain { get; set; } = 0.25f;
    public bool FyFrontEnabled { get; set; } = true;
    public float FyRearGain { get; set; } = 0.0f;
    public bool FyRearEnabled { get; set; } = false;

    public float FinalFfGain { get; set; } = 0.0f;
    public bool FinalFfEnabled { get; set; } = false;

    public float WheelLoadWeighting { get; set; } = 0.0f;

    public FfbMixMode MixMode { get; set; } = FfbMixMode.Replace;

    private float _smMzFront, _smFxFront, _smFyFront;
    private float _smMzRear, _smFxRear, _smFyRear;

    // Parallel slow EMA for Mz: runs on raw signal, blended with fast EMA at high speed.
    // Eliminates the cascading phase lag of the old double-EMA approach.
    private float _sm2MzFront, _sm2MzRear;
    // Parallel slow EMA for Fy: same approach, blended with fast EMA at high speed.
    private float _sm2FyFront, _sm2FyRear;
    private const float FySlowAlpha = 0.04f;

    // Center blend zone: below this steering angle (degrees), Fy is suppressed
    // in favor of the cleaner Mz signal. Removes the grainy Fy buzz at center.
    public float CenterBlendDegrees { get; set; } = 1.0f;

    // Per-channel smoothing: alpha = (1 - smooth). Higher alpha = more responsive, more noise.
    // Mz (steering torque): responsive — detail matters
    private const float MzAlpha = 0.20f;
    // Fx (longitudinal): heavy smoothing — noisiest channel, needs 3x more filtering than Mz
    private const float FxAlpha = 0.08f;
    // Fy (lateral): moderate smoothing
    private const float FyAlpha = 0.12f;

    // Parallel slow EMA for Mz: runs on raw signal (not cascaded from fast EMA).
    // Provides heavy smoothing at high speed without the phase lag of cascaded filters.
    // Blended with fast EMA based on speed — fast keeps transient bite, slow kills oscillation.
    private const float MzSlowAlpha = 0.05f;

    // Adaptive slip smoothing: alpha used for Fx when slip ratio exceeds threshold
    public float AdaptiveSlipThreshold { get; set; } = 0.05f;
    private const float FxSlipAlpha = 0.04f; // even heavier smoothing during slip

    // Low-speed alpha scaling: suppresses physics jitter at parking/creeping speeds
    // where tire models are noisy and DD motors amplify micro-oscillations.
    // Extended to 25 km/h to cover pit lane cornering with full lock steering.
    public float LowSpeedSmoothKmh { get; set; } = 25.0f;
    private const float MinAlphaScale = 0.05f;

    // High-speed smoothing blend threshold: above this speed, the parallel slow EMA
    // is progressively blended in to suppress tire physics oscillation.
    public float HighSpeedMzSmoothKmh { get; set; } = 150.0f;

    private float _prevRawMzFront, _prevRawFxFront, _prevRawFyFront;
    private float _prevRawMzRear, _prevRawFxRear, _prevRawFyRear;
    private float _prevMixedOutput;

    public float Mix(FfbRawData raw, out FfbChannelOutputs channels)
    {
        float rawMzFront, rawFxFront, rawFyFront;
        float rawMzRear, rawFxRear, rawFyRear;

        if (WheelLoadWeighting > 0.001f)
        {
            rawMzFront = WeightedChannel(raw.Mz, raw.WheelLoad, 0, 1);
            rawFxFront = WeightedChannel(raw.Fx, raw.WheelLoad, 0, 1);
            rawFyFront = WeightedChannel(raw.Fy, raw.WheelLoad, 0, 1);
            rawMzRear = WeightedChannel(raw.Mz, raw.WheelLoad, 2, 3);
            rawFxRear = WeightedChannel(raw.Fx, raw.WheelLoad, 2, 3);
            rawFyRear = WeightedChannel(raw.Fy, raw.WheelLoad, 2, 3);
        }
        else
        {
            float frontLoad = Math.Max(raw.WheelLoad[0] + raw.WheelLoad[1], 1f);
            float loadFactor = Math.Clamp(frontLoad / LoadReference, 0.1f, 1.2f);

            rawMzFront = (raw.Mz[0] + raw.Mz[1]) * 0.5f * loadFactor;
            rawFxFront = (raw.Fx[0] + raw.Fx[1]) * 0.5f;
            // Fy uses NEGATED AVERAGE -(FL + FR) * 0.5. The raw Fy average opposes Mz
            // (mechanical trail opposes pneumatic trail). Negating it makes Fy ADD to Mz,
            // which represents the total steering torque the driver feels (Mz + Fy×trail).
            // This also ensures Fy compensates for Mz drop at high slip angles (>200°),
            // where self-aligning torque collapses but lateral force remains strong.
            rawFyFront = -(raw.Fy[0] + raw.Fy[1]) * 0.5f;
            rawMzRear = (raw.Mz[2] + raw.Mz[3]) * 0.5f;
            rawFxRear = (raw.Fx[2] + raw.Fx[3]) * 0.5f;
            rawFyRear = -(raw.Fy[2] + raw.Fy[3]) * 0.5f;
        }

        rawMzFront = SpikeClamp(rawMzFront, ref _prevRawMzFront);
        rawFxFront = SpikeClamp(rawFxFront, ref _prevRawFxFront);
        rawFyFront = SpikeClamp(rawFyFront, ref _prevRawFyFront);
        rawMzRear = SpikeClamp(rawMzRear, ref _prevRawMzRear);
        rawFxRear = SpikeClamp(rawFxRear, ref _prevRawFxRear);
        rawFyRear = SpikeClamp(rawFyRear, ref _prevRawFyRear);

        float mzFront = MzFrontEnabled ? Normalize(rawMzFront, MzScale) * MzFrontGain : 0f;
        float fxFront = FxFrontEnabled ? Normalize(rawFxFront, FxScale) * FxFrontGain : 0f;
        float fyFront = FyFrontEnabled ? Normalize(rawFyFront, FyScale) * FyFrontGain : 0f;
        float mzRear = MzRearEnabled ? Normalize(rawMzRear, MzScale) * MzRearGain : 0f;
        float fxRear = FxRearEnabled ? Normalize(rawFxRear, FxScale) * FxRearGain : 0f;
        float fyRear = FyRearEnabled ? Normalize(rawFyRear, FyScale) * FyRearGain : 0f;

        // Adaptive Fx alpha: heavier smoothing when front wheels are slipping (ABS/TC active)
        float maxFrontSlip = Math.Max(Math.Abs(raw.SlipRatio[0]), Math.Abs(raw.SlipRatio[1]));
        float fxFrontAlpha = maxFrontSlip > AdaptiveSlipThreshold ? FxSlipAlpha : FxAlpha;
        float fxRearAlpha = FxAlpha;

        // Speed-dependent alpha scaling: at low speed, tire physics is noisy and
        // DD motors amplify micro-jitter. Scale alphas down (heavier smoothing)
        // below LowSpeedSmoothKmh, ramping from MinAlphaScale to 1.0.
        // At 0 km/h: alpha = normal * 0.05 (extremely heavy smoothing)
        // At 15+ km/h: alpha = normal * 1.0 (full detail)
        float speedAlphaScale = Math.Clamp(raw.SpeedKmh / LowSpeedSmoothKmh, MinAlphaScale, 1.0f);

        // High-speed parallel smoothing: two independent EMAs on the raw signal.
        // Fast EMA (alpha 0.20) keeps transient steering detail.
        // Slow EMA (alpha 0.05) kills physics oscillation.
        // Blend toward slow at high speed — smooth without cascading phase lag.
        // At <150 km/h: pure fast signal. At 250+ km/h: 83% slow + 17% fast.
        float highSpeedBlend = raw.SpeedKmh > HighSpeedMzSmoothKmh
            ? Math.Clamp((raw.SpeedKmh - HighSpeedMzSmoothKmh) / 120.0f, 0f, 0.85f)
            : 0f;

        float mzFastAlphaS = MzAlpha * speedAlphaScale;
        float mzSlowAlphaS = MzSlowAlpha * speedAlphaScale;
        float fyFastAlphaS = FyAlpha * speedAlphaScale;
        float fySlowAlphaS = FySlowAlpha * speedAlphaScale;
        float fxFrontAlphaS = fxFrontAlpha * speedAlphaScale;
        float fxRearAlphaS = fxRearAlpha * speedAlphaScale;

        // MzFront: parallel fast + slow EMAs, blended by speed
        _smMzFront = _smMzFront * (1f - mzFastAlphaS) + mzFront * mzFastAlphaS;
        _sm2MzFront = _sm2MzFront * (1f - mzSlowAlphaS) + mzFront * mzSlowAlphaS;
        float outMzFront = _smMzFront + (_sm2MzFront - _smMzFront) * highSpeedBlend;

        _smFxFront = _smFxFront * (1f - fxFrontAlphaS) + fxFront * fxFrontAlphaS;

        // FyFront: parallel fast + slow EMAs, blended by speed
        _smFyFront = _smFyFront * (1f - fyFastAlphaS) + fyFront * fyFastAlphaS;
        _sm2FyFront = _sm2FyFront * (1f - fySlowAlphaS) + fyFront * fySlowAlphaS;
        float outFyFront = _smFyFront + (_sm2FyFront - _smFyFront) * highSpeedBlend;

        // MzRear: parallel fast + slow EMAs, blended by speed
        _smMzRear = _smMzRear * (1f - mzFastAlphaS) + mzRear * mzFastAlphaS;
        _sm2MzRear = _sm2MzRear * (1f - mzSlowAlphaS) + mzRear * mzSlowAlphaS;
        float outMzRear = _smMzRear + (_sm2MzRear - _smMzRear) * highSpeedBlend;

        _smFxRear = _smFxRear * (1f - fxRearAlphaS) + fxRear * fxRearAlphaS;

        // FyRear: parallel fast + slow EMAs, blended by speed
        _smFyRear = _smFyRear * (1f - fyFastAlphaS) + fyRear * fyFastAlphaS;
        _sm2FyRear = _sm2FyRear * (1f - fySlowAlphaS) + fyRear * fySlowAlphaS;
        float outFyRear = _smFyRear + (_sm2FyRear - _smFyRear) * highSpeedBlend;

        // ── Center Blend Zone ──
        // At very low steering angles, Fy is noisy (differential of large values).
        // Mz is much cleaner. Suppress Fy near center with a SmoothStep fade.
        // At 0°: Fy weight = 0 (pure Mz). At CenterBlendDegrees+: Fy weight = 1.
        float maxDeg = 450f;
        float absSteerDeg = Math.Abs(raw.SteerAngle * maxDeg);
        float bt = Math.Clamp(absSteerDeg / Math.Max(CenterBlendDegrees, 0.1f), 0f, 1f);
        float fyBlend = bt * bt * (3f - 2f * bt);
        float blendedFyFront = outFyFront * fyBlend;
        float blendedFyRear = outFyRear * fyBlend;

        float finalFf = FinalFfEnabled ? raw.FinalFf * FinalFfGain : 0f;

        channels = new FfbChannelOutputs
        {
            MzFront = outMzFront,
            FxFront = _smFxFront,
            FyFront = blendedFyFront,
            MzRear = outMzRear,
            FxRear = _smFxRear,
            FyRear = blendedFyRear,
            FinalFf = finalFf
        };

        float mixed = MixMode switch
        {
            FfbMixMode.Replace => outMzFront + _smFxFront + blendedFyFront + outMzRear + _smFxRear + blendedFyRear,
            FfbMixMode.Overlay => raw.FinalFf + outMzFront + _smFxFront + blendedFyFront + outMzRear + _smFxRear + blendedFyRear,
            _ => raw.FinalFf
        };

        float maxMixedDelta = raw.SpeedKmh > 150f ? 0.04f : 0.10f;
        float mixedDelta = mixed - _prevMixedOutput;
        if (Math.Abs(mixedDelta) > maxMixedDelta)
            mixed = _prevMixedOutput + Math.Sign(mixedDelta) * maxMixedDelta;
        _prevMixedOutput = mixed;

        return mixed;
    }

    private static float Normalize(float rawValue, float scale)
    {
        if (scale < 0.001f) return 0f;
        return rawValue / scale;
    }

    private float WeightedChannel(float[] forces, float[] loads, int idxA, int idxB)
    {
        float totalLoad = Math.Max(loads[0] + loads[1] + loads[2] + loads[3], 0.001f);
        float pairLoad = loads[idxA] + loads[idxB];
        float loadRatio = pairLoad / totalLoad;

        float w = WheelLoadWeighting;
        float blended = (1f - w) * 0.5f + w * loadRatio;

        return (forces[idxA] + forces[idxB]) * blended;
    }

    private static float SpikeClamp(float raw, ref float prev)
    {
        float delta = raw - prev;
        float maxDelta = Math.Min(Math.Abs(prev) * 0.3f + 30f, 150f);
        float clamped = prev + Math.Clamp(delta, -maxDelta, maxDelta);
        prev = clamped;
        return clamped;
    }
}

public enum FfbMixMode
{
    Replace,
    Overlay
}

public struct FfbChannelOutputs
{
    public float MzFront;
    public float FxFront;
    public float FyFront;
    public float MzRear;
    public float FxRear;
    public float FyRear;
    public float FinalFf;
}
