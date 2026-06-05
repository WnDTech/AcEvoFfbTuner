using AcEvoFfbTuner.Core.FfbProcessing.Models;

namespace AcEvoFfbTuner.Core.FfbProcessing;

public sealed class FfbChannelMixer
{
    public float MzScale { get; set; } = 30f;
    public float FxScale { get; set; } = 500f;
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

    /// <summary>
    /// When true, Fy is negated before mixing. Toggleable for A/B testing sign convention.
    /// Default true matches original behavior: -(Fy[FL] + Fy[FR]) * 0.5.
    /// </summary>
    public bool FyInverted { get; set; } = true;

    // Single EMA state per channel (detail path only)
    private float _smMzFront, _smFxFront, _smFyFront;
    private float _smMzRear, _smFxRear, _smFyRear;

    // Center blend zone: below this steering angle (degrees), both Fx and Fy are
    // suppressed in favor of the cleaner Mz signal. Removes channel noise (Fy buzz,
    // Fx snap from longitudinal oscillations) when driving straight.
    // Fades in smoothly via SmoothStep as steer angle increases.
    public float CenterBlendDegrees { get; set; } = 1.0f;

    // Per-channel smoothing: higher alpha = more responsive, more noise.
    // Mz (steering torque): most responsive — detail matters most
    private const float MzAlpha = 0.40f;
    // Fx (longitudinal): moderate smoothing — noisiest channel
    private const float FxAlpha = 0.15f;
    // Fy (lateral): responsive
    private const float FyAlpha = 0.30f;

    // Adaptive slip smoothing: alpha used for Fx when slip ratio exceeds threshold
    public float AdaptiveSlipThreshold { get; set; } = 0.05f;
    private const float FxSlipAlpha = 0.08f;

    // Low-speed alpha scaling: suppresses physics jitter at parking/creeping speeds.
    public float LowSpeedSmoothKmh { get; set; } = 15.0f;
    private const float MinAlphaScale = 0.10f;

    // 3-sample median filter buffers (ring buffers for each channel)
    private readonly int[] _medianPos = new int[6];
    private readonly float[] _medMzFront = new float[3];
    private readonly float[] _medFxFront = new float[3];
    private readonly float[] _medFyFront = new float[3];
    private readonly float[] _medMzRear = new float[3];
    private readonly float[] _medFxRear = new float[3];
    private readonly float[] _medFyRear = new float[3];
    private readonly bool[] _medianBufReady = new bool[6];
    private int _medianBufIdx;

    // Adaptive auto-normalization: tracks per-channel rolling peaks
    // to prevent massive raw telemetry values from overwhelming output.
    // When a channel's raw peak exceeds (MzScale * AutoTarget), the
    // effective scale is bumped up so the output stays in range.
    private float _mzPeak, _fxPeak, _fyPeak;
    private const float ChannelPeakDecay = 0.997f;
    private const float ChannelAutoTarget = 1.0f;
    private const float AutoMinPeakDefault = 1.0f;
    public float AutoMinPeak { get; set; } = AutoMinPeakDefault;

    private float _smCoreFxFront;
    private float _smCoreFxRear;

    public float Mix(FfbRawData raw, out FfbChannelOutputs channels)
    {
        float rawMzFront, rawFxFront, rawFyFront;
        float rawMzRear, rawFxRear, rawFyRear;

        float fySign = FyInverted ? -1f : 1f;

        if (WheelLoadWeighting > 0.001f)
        {
            rawMzFront = WeightedChannel(raw.Mz, raw.WheelLoad, 0, 1);
            rawFxFront = WeightedChannel(raw.Fx, raw.WheelLoad, 0, 1);
            rawFyFront = fySign * WeightedChannel(raw.Fy, raw.WheelLoad, 0, 1);
            rawMzRear = WeightedChannel(raw.Mz, raw.WheelLoad, 2, 3);
            rawFxRear = WeightedChannel(raw.Fx, raw.WheelLoad, 2, 3);
            rawFyRear = fySign * WeightedChannel(raw.Fy, raw.WheelLoad, 2, 3);
        }
        else
        {
            float frontLoad = Math.Max(raw.WheelLoad[0] + raw.WheelLoad[1], 1f);
            float normalizedLoad = Math.Clamp(frontLoad / LoadReference, 0.1f, 2.0f);
            float loadFactor = MathF.Sqrt(normalizedLoad);

            rawMzFront = (raw.Mz[0] + raw.Mz[1]) * 0.5f * loadFactor;
            rawFxFront = (raw.Fx[0] + raw.Fx[1]) * 0.5f;
            rawFyFront = fySign * (raw.Fy[0] + raw.Fy[1]) * 0.5f;
            rawMzRear = (raw.Mz[2] + raw.Mz[3]) * 0.5f;
            rawFxRear = (raw.Fx[2] + raw.Fx[3]) * 0.5f;
            rawFyRear = fySign * (raw.Fy[2] + raw.Fy[3]) * 0.5f;
        }

        // 3-sample median filter: rejects single-sample spikes
        _medianBufIdx = 0;
        rawMzFront = MedianFilter(rawMzFront, _medMzFront);
        rawFxFront = MedianFilter(rawFxFront, _medFxFront);
        rawFyFront = MedianFilter(rawFyFront, _medFyFront);
        rawMzRear = MedianFilter(rawMzRear, _medMzRear);
        rawFxRear = MedianFilter(rawFxRear, _medFxRear);
        rawFyRear = MedianFilter(rawFyRear, _medFyRear);

        // ═══════════════════════════════════════════════════════════════
        // Adaptive auto-normalization (post-median, pre-EMA)
        //
        // AC EVO's raw Mz can range from ~0.005 (parked) to ~1441 (high-speed
        // cornering) — a 288000x dynamic range. A static Scale cannot handle
        // both extremes. We track rolling peaks and auto-adjust the effective
        // scale so the channel output never exceeds AutoTarget * Gain.
        // When the raw peak is below AutoMinPeak, the manual Scale is used
        // unchanged (avoiding amplification of noise at standstill).
        // ═══════════════════════════════════════════════════════════════
        float absRawMz = Math.Max(Math.Abs(rawMzFront), Math.Abs(rawMzRear));
        _mzPeak = Math.Max(_mzPeak * ChannelPeakDecay, absRawMz);
        float absRawFx = Math.Max(Math.Abs(rawFxFront), Math.Abs(rawFxRear));
        _fxPeak = Math.Max(_fxPeak * ChannelPeakDecay, absRawFx);
        float absRawFy = Math.Max(Math.Abs(rawFyFront), Math.Abs(rawFyRear));
        _fyPeak = Math.Max(_fyPeak * ChannelPeakDecay, absRawFy);

        float effectiveMzScale = MzScale;
        if (_mzPeak > AutoMinPeak)
            effectiveMzScale = Math.Max(effectiveMzScale, _mzPeak / ChannelAutoTarget);

        float effectiveFxScale = FxScale;
        if (_fxPeak > AutoMinPeak)
            effectiveFxScale = Math.Max(effectiveFxScale, _fxPeak / ChannelAutoTarget);

        float effectiveFyScale = FyScale;
        if (_fyPeak > AutoMinPeak)
            effectiveFyScale = Math.Max(effectiveFyScale, _fyPeak / ChannelAutoTarget);

        float mzFront = MzFrontEnabled ? SoftClamp(Normalize(rawMzFront, effectiveMzScale) * MzFrontGain) : 0f;
        float fxFront = FxFrontEnabled ? SoftClamp(Normalize(rawFxFront, effectiveFxScale) * FxFrontGain) : 0f;
        float fyFront = FyFrontEnabled ? SoftClamp(Normalize(rawFyFront, effectiveFyScale) * FyFrontGain) : 0f;
        float mzRear = MzRearEnabled ? SoftClamp(Normalize(rawMzRear, effectiveMzScale) * MzRearGain) : 0f;
        float fxRear = FxRearEnabled ? SoftClamp(Normalize(rawFxRear, effectiveFxScale) * FxRearGain) : 0f;
        float fyRear = FyRearEnabled ? SoftClamp(Normalize(rawFyRear, effectiveFyScale) * FyRearGain) : 0f;

        // Center blend zone (uses raw steer angle — no latency)
        // Fy is suppressed near center because lateral forces should be near zero
        // when driving straight — removes the grainy Fy buzz at center.
        float maxDeg = 450f;
        float absSteerDeg = Math.Abs(raw.SteerAngle * maxDeg);
        float bt = Math.Clamp(absSteerDeg / Math.Max(CenterBlendDegrees, 0.1f), 0f, 1f);
        float centerBlend = bt * bt * (3f - 2f * bt);

        // ═══════════════════════════════════════════════════════════════
        // CORE PATH: zero-latency with always-on Fx EMA smoothing
        //
        // Mz and Fy go through unfiltered (zero-latency) — these carry
        // the essential self-aligning torque and lateral forces.
        //
        // Fx (longitudinal) gets a light EMA always, with heavier smoothing
        // during braking/ABS. This kills the high-frequency Fx oscillations
        // (throttle modulation, bumps, engine torque pulses) that cause
        // snap-like behavior on DD wheels, while preserving the low-frequency
        // braking dive feel. Real steering columns have natural mechanical
        // lowpass from rubber bushings and hydraulic assist — our EMA
        // approximates that on the digital path.
        // ═══════════════════════════════════════════════════════════════
        bool isBrakingHard = raw.BrakeInput > 0.3f;
        bool absActive = raw.AbsInAction != 0;

        float fxAlpha;
        if (FxFrontEnabled)
        {
            fxAlpha = isBrakingHard ? (absActive ? 0.08f : 0.15f) : 0.30f;
            _smCoreFxFront = _smCoreFxFront * (1f - fxAlpha) + fxFront * fxAlpha;
        }
        float coreFxFront = FxFrontEnabled ? _smCoreFxFront : 0f;

        if (FxRearEnabled)
        {
            fxAlpha = isBrakingHard ? (absActive ? 0.08f : 0.15f) : 0.30f;
            _smCoreFxRear = _smCoreFxRear * (1f - fxAlpha) + fxRear * fxAlpha;
        }
        float coreFxRear = FxRearEnabled ? _smCoreFxRear : 0f;

        float coreBlendedFyFront = fyFront * centerBlend;
        float coreBlendedFyRear = fyRear * centerBlend;
        float rawCoreForce = mzFront + coreFxFront + coreBlendedFyFront
                           + mzRear + coreFxRear + coreBlendedFyRear;

        // ═══════════════════════════════════════════════════════════════
        // DETAIL PATH: EMA-smoothed channels (for diagnostics & detail extraction)
        // ═══════════════════════════════════════════════════════════════
        float maxFrontSlip = Math.Max(Math.Abs(raw.SlipRatio[0]), Math.Abs(raw.SlipRatio[1]));
        float fxFrontAlpha = maxFrontSlip > AdaptiveSlipThreshold ? FxSlipAlpha : FxAlpha;

        float speedAlphaScale = Math.Clamp(raw.SpeedKmh / LowSpeedSmoothKmh, MinAlphaScale, 1.0f);

        float mzAlphaS = MzAlpha * speedAlphaScale;
        float fxFrontAlphaS = fxFrontAlpha * speedAlphaScale;
        float fxRearAlphaS = FxAlpha * speedAlphaScale;
        float fyAlphaS = FyAlpha * speedAlphaScale;

        _smMzFront = _smMzFront * (1f - mzAlphaS) + mzFront * mzAlphaS;
        _smFxFront = _smFxFront * (1f - fxFrontAlphaS) + fxFront * fxFrontAlphaS;
        _smFyFront = _smFyFront * (1f - fyAlphaS) + fyFront * fyAlphaS;
        _smMzRear = _smMzRear * (1f - mzAlphaS) + mzRear * mzAlphaS;
        _smFxRear = _smFxRear * (1f - fxRearAlphaS) + fxRear * fxRearAlphaS;
        _smFyRear = _smFyRear * (1f - fyAlphaS) + fyRear * fyAlphaS;

        float blendedFyFront = _smFyFront * centerBlend;
        float blendedFyRear = _smFyRear * centerBlend;

        float finalFf = FinalFfEnabled ? raw.FinalFf * FinalFfGain : 0f;

        channels = new FfbChannelOutputs
        {
            MzFront = _smMzFront,
            FxFront = _smFxFront,
            FyFront = blendedFyFront,
            MzRear = _smMzRear,
            FxRear = _smFxRear,
            FyRear = blendedFyRear,
            FinalFf = finalFf,
            RawCoreForce = rawCoreForce
        };

        float mixed = MixMode switch
        {
            FfbMixMode.Replace => _smMzFront + _smFxFront + blendedFyFront + _smMzRear + _smFxRear + blendedFyRear,
            FfbMixMode.Overlay => raw.FinalFf + _smMzFront + _smFxFront + blendedFyFront + _smMzRear + _smFxRear + blendedFyRear,
            _ => raw.FinalFf
        };

        return mixed;
    }

    private static float Normalize(float rawValue, float scale)
    {
        if (scale < 0.001f) return 0f;
        return rawValue / scale;
    }

    private static float SoftClamp(float v, float limit = 1.0f)
    {
        float abs = Math.Abs(v);
        if (abs <= limit) return v;
        float sign = Math.Sign(v);
        float excess = abs - limit;
        return sign * (limit + (1f - limit) * MathF.Tanh(excess * 2f));
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

    private float MedianFilter(float sample, float[] buffer)
    {
        int bufIdx = _medianBufIdx++;
        if (!_medianBufReady[bufIdx])
        {
            buffer[0] = sample;
            buffer[1] = sample;
            buffer[2] = sample;
            _medianBufReady[bufIdx] = true;
            return sample;
        }

        buffer[_medianPos[bufIdx]] = sample;
        _medianPos[bufIdx] = (_medianPos[bufIdx] + 1) % 3;

        float a = buffer[0], b = buffer[1], c = buffer[2];
        float max = Math.Max(Math.Max(a, b), c);
        float min = Math.Min(Math.Min(a, b), c);
        return (a + b + c) - max - min;
    }

    public AutoNormDiagnostics GetAutoNormDiagnostics()
    {
        float effectiveMzScale = MzScale;
        bool mzAuto = _mzPeak > AutoMinPeak;
        if (mzAuto) effectiveMzScale = Math.Max(effectiveMzScale, _mzPeak / ChannelAutoTarget);

        float effectiveFxScale = FxScale;
        bool fxAuto = _fxPeak > AutoMinPeak;
        if (fxAuto) effectiveFxScale = Math.Max(effectiveFxScale, _fxPeak / ChannelAutoTarget);

        float effectiveFyScale = FyScale;
        bool fyAuto = _fyPeak > AutoMinPeak;
        if (fyAuto) effectiveFyScale = Math.Max(effectiveFyScale, _fyPeak / ChannelAutoTarget);

        return new AutoNormDiagnostics
        {
            MzPeak = _mzPeak,
            FxPeak = _fxPeak,
            FyPeak = _fyPeak,
            EffectiveMzScale = effectiveMzScale,
            EffectiveFxScale = effectiveFxScale,
            EffectiveFyScale = effectiveFyScale,
            ManualMzScale = MzScale,
            ManualFxScale = FxScale,
            ManualFyScale = FyScale,
            MzAutoActive = mzAuto,
            FxAutoActive = fxAuto,
            FyAutoActive = fyAuto
        };
    }

    public void Reset()
    {
        _smMzFront = _smFxFront = _smFyFront = 0f;
        _smMzRear = _smFxRear = _smFyRear = 0f;
        _smCoreFxFront = 0f;
        _smCoreFxRear = 0f;
        Array.Clear(_medianBufReady);
        Array.Clear(_medianPos);
        Array.Clear(_medMzFront);
        Array.Clear(_medFxFront);
        Array.Clear(_medFyFront);
        Array.Clear(_medMzRear);
        Array.Clear(_medFxRear);
        Array.Clear(_medFyRear);
        _mzPeak = _fxPeak = _fyPeak = 0f;
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

    /// <summary>
    /// Raw core steering force (zero-latency path).
    /// Median-filtered for spike rejection but NOT EMA-smoothed.
    /// Sum of normalized Mz + Fx + Fy with center blend applied.
    /// </summary>
    public float RawCoreForce;
}

public struct AutoNormDiagnostics
{
    public float MzPeak;
    public float FxPeak;
    public float FyPeak;
    public float EffectiveMzScale;
    public float EffectiveFxScale;
    public float EffectiveFyScale;
    public float ManualMzScale;
    public float ManualFxScale;
    public float ManualFyScale;
    public bool MzAutoActive;
    public bool FxAutoActive;
    public bool FyAutoActive;
}

