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

    // Single EMA state per channel
    private float _smMzFront, _smFxFront, _smFyFront;
    private float _smMzRear, _smFxRear, _smFyRear;

    // Center blend zone: below this steering angle (degrees), Fy is suppressed
    // in favor of the cleaner Mz signal. Removes the grainy Fy buzz at center.
    public float CenterBlendDegrees { get; set; } = 1.0f;

    // Per-channel smoothing: higher alpha = more responsive, more noise.
    // Mz (steering torque): most responsive — detail matters most
    private const float MzAlpha = 0.40f;   // was 0.20
    // Fx (longitudinal): moderate smoothing — noisiest channel
    private const float FxAlpha = 0.15f;   // was 0.08
    // Fy (lateral): responsive
    private const float FyAlpha = 0.30f;   // was 0.12

    // Adaptive slip smoothing: alpha used for Fx when slip ratio exceeds threshold
    public float AdaptiveSlipThreshold { get; set; } = 0.05f;
    private const float FxSlipAlpha = 0.08f; // was 0.04, slightly more responsive during slip

    // Low-speed alpha scaling: suppresses physics jitter at parking/creeping speeds.
    // Reduced from 25 to 15 km/h — less aggressive gating.
    public float LowSpeedSmoothKmh { get; set; } = 15.0f;
    private const float MinAlphaScale = 0.10f;  // was 0.05

    // 3-sample median filter buffers (ring buffers for each channel)
    private readonly int[] _medianPos = new int[6]; // per-channel ring position
    private readonly float[] _medMzFront = new float[3];
    private readonly float[] _medFxFront = new float[3];
    private readonly float[] _medFyFront = new float[3];
    private readonly float[] _medMzRear = new float[3];
    private readonly float[] _medFxRear = new float[3];
    private readonly float[] _medFyRear = new float[3];
    private readonly bool[] _medianBufReady = new bool[6];
    private int _medianBufIdx; // tracks which buffer is being processed (0-5)

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
            float loadFactor = Math.Clamp(frontLoad / LoadReference, 0.1f, 1.2f);

            rawMzFront = (raw.Mz[0] + raw.Mz[1]) * 0.5f * loadFactor;
            rawFxFront = (raw.Fx[0] + raw.Fx[1]) * 0.5f;
            rawFyFront = fySign * (raw.Fy[0] + raw.Fy[1]) * 0.5f;
            rawMzRear = (raw.Mz[2] + raw.Mz[3]) * 0.5f;
            rawFxRear = (raw.Fx[2] + raw.Fx[3]) * 0.5f;
            rawFyRear = fySign * (raw.Fy[2] + raw.Fy[3]) * 0.5f;
        }

        // 3-sample median filter: rejects single-sample spikes (noise) but preserves
        // real transients (multi-sample changes like kerb strikes).
        _medianBufIdx = 0;
        rawMzFront = MedianFilter(rawMzFront, _medMzFront);
        rawFxFront = MedianFilter(rawFxFront, _medFxFront);
        rawFyFront = MedianFilter(rawFyFront, _medFyFront);
        rawMzRear = MedianFilter(rawMzRear, _medMzRear);
        rawFxRear = MedianFilter(rawFxRear, _medFxRear);
        rawFyRear = MedianFilter(rawFyRear, _medFyRear);

        float mzFront = MzFrontEnabled ? Normalize(rawMzFront, MzScale) * MzFrontGain : 0f;
        float fxFront = FxFrontEnabled ? Normalize(rawFxFront, FxScale) * FxFrontGain : 0f;
        float fyFront = FyFrontEnabled ? Normalize(rawFyFront, FyScale) * FyFrontGain : 0f;
        float mzRear = MzRearEnabled ? Normalize(rawMzRear, MzScale) * MzRearGain : 0f;
        float fxRear = FxRearEnabled ? Normalize(rawFxRear, FxScale) * FxRearGain : 0f;
        float fyRear = FyRearEnabled ? Normalize(rawFyRear, FyScale) * FyRearGain : 0f;

        // Adaptive Fx alpha: heavier smoothing when front wheels are slipping (ABS/TC active)
        float maxFrontSlip = Math.Max(Math.Abs(raw.SlipRatio[0]), Math.Abs(raw.SlipRatio[1]));
        float fxFrontAlpha = maxFrontSlip > AdaptiveSlipThreshold ? FxSlipAlpha : FxAlpha;

        // Speed-dependent alpha scaling: at low speed, tire physics is noisy.
        // Scale alphas down (heavier smoothing) below LowSpeedSmoothKmh.
        // At 0 km/h: alpha = normal * 0.10. At LowSpeedSmoothKmh+: alpha = normal * 1.0.
        float speedAlphaScale = Math.Clamp(raw.SpeedKmh / LowSpeedSmoothKmh, MinAlphaScale, 1.0f);

        float mzAlphaS = MzAlpha * speedAlphaScale;
        float fxFrontAlphaS = fxFrontAlpha * speedAlphaScale;
        float fxRearAlphaS = FxAlpha * speedAlphaScale;
        float fyAlphaS = FyAlpha * speedAlphaScale;

        // Single EMA per channel — no parallel slow EMAs, no high-speed blend.
        _smMzFront = _smMzFront * (1f - mzAlphaS) + mzFront * mzAlphaS;
        _smFxFront = _smFxFront * (1f - fxFrontAlphaS) + fxFront * fxFrontAlphaS;
        _smFyFront = _smFyFront * (1f - fyAlphaS) + fyFront * fyAlphaS;
        _smMzRear = _smMzRear * (1f - mzAlphaS) + mzRear * mzAlphaS;
        _smFxRear = _smFxRear * (1f - fxRearAlphaS) + fxRear * fxRearAlphaS;
        _smFyRear = _smFyRear * (1f - fyAlphaS) + fyRear * fyAlphaS;

        // Center Blend Zone: at very low steering angles, suppress Fy in favor of Mz.
        float maxDeg = 450f;
        float absSteerDeg = Math.Abs(raw.SteerAngle * maxDeg);
        float bt = Math.Clamp(absSteerDeg / Math.Max(CenterBlendDegrees, 0.1f), 0f, 1f);
        float fyBlend = bt * bt * (3f - 2f * bt);
        float blendedFyFront = _smFyFront * fyBlend;
        float blendedFyRear = _smFyRear * fyBlend;

        float finalFf = FinalFfEnabled ? raw.FinalFf * FinalFfGain : 0f;

        channels = new FfbChannelOutputs
        {
            MzFront = _smMzFront,
            FxFront = _smFxFront,
            FyFront = blendedFyFront,
            MzRear = _smMzRear,
            FxRear = _smFxRear,
            FyRear = blendedFyRear,
            FinalFf = finalFf
        };

        float mixed = MixMode switch
        {
            FfbMixMode.Replace => _smMzFront + _smFxFront + blendedFyFront + _smMzRear + _smFxRear + blendedFyRear,
            FfbMixMode.Overlay => raw.FinalFf + _smMzFront + _smFxFront + blendedFyFront + _smMzRear + _smFxRear + blendedFyRear,
            _ => raw.FinalFf
        };

        // No mixed-output rate limiter — the pipeline's slew rate handles this.

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

    /// <summary>
    /// 3-sample median filter. Writes new sample into ring buffer and returns
    /// the median of the last 3 samples. Rejects single-sample spikes (noise)
    /// while preserving real transients (multi-sample changes like kerb strikes).
    /// </summary>
    private float MedianFilter(float sample, float[] buffer)
    {
        int bufIdx = _medianBufIdx++;
        if (!_medianBufReady[bufIdx])
        {
            // Fill buffer with first sample to avoid startup transient
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

    /// <summary>
    /// Called when the median filter should reset (e.g., car respawn, new session).
    /// </summary>
    public void Reset()
    {
        _smMzFront = _smFxFront = _smFyFront = 0f;
        _smMzRear = _smFxRear = _smFyRear = 0f;
        Array.Clear(_medianBufReady);
        Array.Clear(_medianPos);
        Array.Clear(_medMzFront);
        Array.Clear(_medFxFront);
        Array.Clear(_medFyFront);
        Array.Clear(_medMzRear);
        Array.Clear(_medFxRear);
        Array.Clear(_medFyRear);
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
