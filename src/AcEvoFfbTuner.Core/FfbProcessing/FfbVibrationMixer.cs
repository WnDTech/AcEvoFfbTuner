using AcEvoFfbTuner.Core.FfbProcessing.Models;

namespace AcEvoFfbTuner.Core.FfbProcessing;

public sealed class FfbVibrationMixer
{
    public float KerbGain { get; set; } = 1.0f;
    public float SlipGain { get; set; } = 0.8f;
    public float RoadGain { get; set; } = 0.5f;
    public float AbsGain { get; set; } = 1.0f;
    public float MasterGain { get; set; } = 0.7f;

    public float SuspensionRoadGain { get; set; } = 1.5f;

    /// <summary>
    /// Amplification of curb force based on TyreContactNormal deviation severity.
    /// Higher = more punchy curb feel. Default 10.0.
    /// </summary>
    public float CurbSeverityScale { get; set; } = 10.0f;

    /// <summary>
    /// Normalization factor for Mz force derivative in scrub modulation.
    /// Converts Mz/second into ~[-1,1] FFB range. AC EVO Mz is typically 0–500 Nm.
    /// At 333Hz a 1 Nm/frame change = 333 Nm/s * 0.0005 = 0.167 normalized.
    /// Default 0.0005. Increase if scrub feels too subtle.
    /// </summary>
    public float ScrubForceScale { get; set; } = 0.0005f;

    /// <summary>
    /// Normalization factor for rear Mz/Fy force derivatives in rear slip warning.
    /// Same scale reasoning as ScrubForceScale. Default 0.0005.
    /// </summary>
    public float RearSlipForceScale { get; set; } = 0.0005f;

    public float RoadForceModulation { get; private set; }

    private float _absPhase;
    private const float AbsPulseHz = 15f;
    private const float TickSeconds = 1f / 333f;

    public float AbsForceModulation { get; private set; }

    public float AbsPulseAmplitude { get; set; } = 0.25f;

    private float _smAbsModulation;

    private float[] _prevSuspTravel = new float[4];
    private float[] _prevWheelLoad = new float[4];
    private float _smSuspCurb;
    private float _smSuspRoad;

    private int _contactNormalZeroFrames;
    private bool _contactNormalFallback;
    private const int FallbackCheckFrames = 60;
    private const float CurbSuspDeltaThreshold = 0.002f;

    // ── Front Tire Scrub Modulation ──
    // Data-driven: uses real Mz force rate-of-change (derivative) gated by slip angle.
    // When front tyres slide, Mz naturally oscillates — this IS the scrub signal.
    // No synthetic sine waves.

    public float ScrubGain { get; set; } = 0.50f;
    public float ScrubOnsetAngle { get; set; } = 0.05f;
    public float ScrubPeakAngle { get; set; } = 0.08f;
    public float ScrubMaxAmplitude { get; set; } = 0.15f;
    public float ScrubModulation { get; private set; }

    private float _prevFrontMz;
    private float _smScrubIntensity;

    // ── Rear Slip Warning Modulation ──
    // Data-driven: uses real rear Mz/Fy force derivatives + yaw acceleration.
    // Rear tyres losing grip causes force oscillations — captured from real data.

    public float RearSlipGain { get; set; } = 0.60f;
    public float RearSlipOnsetAngle { get; set; } = 0.07f;
    public float RearSlipPeakAngle { get; set; } = 0.10f;
    public float RearSlipMaxAmplitude { get; set; } = 0.20f;
    public float YawAccelMultiplier { get; set; } = 1.5f;
    public float YawAccelReference { get; set; } = 2.0f;
    public float RearSlipModulation { get; private set; }

    public float WetCurbScale { get; set; } = 1.0f;

    private float _prevRearMz;
    private float _prevRearFy;
    private float _smRearSlipIntensity;
    private float _prevYawRate;
    private bool _yawInitialized;

    // ── TyreContactNormal-based curb detection ──
    // When on flat ground, TyreContactNormal ≈ (0, 1, 0).
    // On a curb/angled surface, the normal tilts — magnitude of X+Z deviation
    // directly measures surface angle. Combined with WheelLoad rate-of-change
    // for actual compression force.
    // Falls back to old suspension-delta method if contact normal data is all zeros.
    private const float CurbNormalThreshold = 0.15f;
    private const float LoadReference = 5000f;

    public float Mix(FfbRawData raw)
    {
        if (raw.SpeedKmh < 2.0f)
        {
            _absPhase = 0f;
            AbsForceModulation = 0f;
            ScrubModulation = 0f;
            RearSlipModulation = 0f;
            _smRearSlipIntensity = 0f;
            return 0f;
        }

        float kerb = raw.KerbVibration * KerbGain;
        float slip = raw.SlipVibrations * SlipGain;
        float road = raw.RoadVibrations * RoadGain;

        float abs;
        bool absActive = false;
        if (raw.AbsVibrations > 0.001f)
        {
            abs = raw.AbsVibrations * AbsGain;
            absActive = true;
        }
        else if (raw.AbsInAction != 0 && raw.BrakeInput > 0.1f)
        {
            absActive = true;
            abs = 0f;
        }
        else
        {
            abs = 0f;
            _absPhase = 0f;
        }

        if (absActive)
        {
            _absPhase += AbsPulseHz * TickSeconds;
            if (_absPhase > 1f) _absPhase -= 1f;

            float frontSlipRatio = (Math.Abs(raw.SlipRatio[0]) + Math.Abs(raw.SlipRatio[1])) * 0.5f;
            float slipIntensity = Math.Clamp(frontSlipRatio / 0.08f, 0.3f, 1.5f);

            float speedFactor = Math.Clamp(raw.SpeedKmh / 200f, 0.2f, 1.0f);
            float brakeIntensity = Math.Clamp(raw.BrakeInput, 0.3f, 1.0f);

            float absAmp = AbsPulseAmplitude * AbsGain * brakeIntensity * speedFactor * slipIntensity;

            float pulse = MathF.Sin(_absPhase * MathF.PI * 2f);
            float targetMod = absAmp * pulse;

            _smAbsModulation = _smAbsModulation * 0.50f + targetMod * 0.50f;

            AbsForceModulation = _smAbsModulation;

            abs = Math.Max(abs, Math.Abs(AbsForceModulation));
        }
        else
        {
            AbsForceModulation = 0f;
            _smAbsModulation *= 0.8f;
        }

        float combined = kerb + slip + road + abs;

        float speedFade = raw.SpeedKmh < 10.0f
            ? (raw.SpeedKmh - 2.0f) / 8.0f
            : 1.0f;

        // ── Data-driven curb/road detection ──
        // Check if TyreContactNormal data is populated (non-zero).
        // If all zeros for FallbackCheckFrames, fall back to suspension-delta method.
        if (!_contactNormalFallback && _contactNormalZeroFrames < FallbackCheckFrames)
        {
            bool anyNonZero = false;
            for (int i = 0; i < 4; i++)
            {
                if (raw.TyreContactNormalX[i] != 0f || raw.TyreContactNormalZ[i] != 0f)
                {
                    anyNonZero = true;
                    break;
                }
            }
            if (anyNonZero)
            {
                _contactNormalZeroFrames = FallbackCheckFrames;
            }
            else
            {
                _contactNormalZeroFrames++;
                if (_contactNormalZeroFrames >= FallbackCheckFrames)
                    _contactNormalFallback = true;
            }
        }

        float curbAccum = 0f;
        float roadAccum = 0f;
        for (int i = 0; i < 4; i++)
        {
            float weight = i < 2 ? 1.5f : 0.75f;

            float loadDelta = raw.WheelLoad[i] - _prevWheelLoad[i];
            float suspDelta = raw.SuspensionTravel[i] - _prevSuspTravel[i];
            _prevWheelLoad[i] = raw.WheelLoad[i];
            _prevSuspTravel[i] = raw.SuspensionTravel[i];

            if (!_contactNormalFallback)
            {
                float normalDeviation = MathF.Sqrt(
                    raw.TyreContactNormalX[i] * raw.TyreContactNormalX[i] +
                    raw.TyreContactNormalZ[i] * raw.TyreContactNormalZ[i]);

                if (normalDeviation > CurbNormalThreshold)
                {
                    float excessDeviation = normalDeviation - CurbNormalThreshold;
                    curbAccum += (loadDelta / LoadReference) * (1.0f + excessDeviation * CurbSeverityScale) * weight;
                }
                else
                {
                    roadAccum += suspDelta * MathF.Max(MathF.Abs(loadDelta) * 0.001f, 0.1f) * weight;
                }
            }
            else
            {
                if (MathF.Abs(suspDelta) > CurbSuspDeltaThreshold)
                    curbAccum += suspDelta * weight;
                else
                    roadAccum += suspDelta * weight;
            }
        }

        _smSuspCurb = _smSuspCurb * 0.3f + curbAccum * 0.7f;
        _smSuspRoad = _smSuspRoad * 0.3f + roadAccum * 0.7f;

        float suspSpeedScale = Math.Clamp(raw.SpeedKmh / 100f, 0f, 2f);
        float curbForce = _smSuspCurb * 2.0f * MathF.Max(KerbGain, 0.1f) * suspSpeedScale * WetCurbScale;
        float roadForce = _smSuspRoad * 150f * MathF.Max(RoadGain, 0.1f) * suspSpeedScale;
        float rawVib = (curbForce + roadForce) * SuspensionRoadGain;
        RoadForceModulation = Math.Clamp(rawVib, -0.15f, 0.15f);

        GenerateScrubModulation(raw);
        GenerateRearSlipModulation(raw);

        return Math.Clamp(combined * MasterGain * speedFade, 0f, 1f);
    }

    /// <summary>
    /// Data-driven scrub: uses real Mz force derivative (rate-of-change) gated by slip angle.
    /// When front tyres approach the limit, Mz naturally oscillates — this IS the
    /// contact patch scrub signal from the tyre model. No synthetic sine waves.
    /// </summary>
    private void GenerateScrubModulation(FfbRawData raw)
    {
        if (ScrubGain < 0.001f || raw.SpeedKmh < 10f)
        {
            ScrubModulation = 0f;
            _smScrubIntensity = 0f;
            return;
        }

        float absSlipAngle = (Math.Abs(raw.SlipAngle[0]) + Math.Abs(raw.SlipAngle[1])) * 0.5f;

        float scrubIntensity = 0f;
        if (absSlipAngle > ScrubOnsetAngle)
        {
            float range = Math.Max(ScrubPeakAngle - ScrubOnsetAngle, 0.01f);
            float t = Math.Clamp((absSlipAngle - ScrubOnsetAngle) / range, 0f, 1f);
            scrubIntensity = t * t;
        }

        _smScrubIntensity = _smScrubIntensity * 0.65f + scrubIntensity * 0.35f;

        if (_smScrubIntensity < 0.001f)
        {
            ScrubModulation = 0f;
            _prevFrontMz = (raw.Mz[0] + raw.Mz[1]) * 0.5f;
            return;
        }

        float frontMz = (raw.Mz[0] + raw.Mz[1]) * 0.5f;
        float mzDerivative = (frontMz - _prevFrontMz) / TickSeconds;
        _prevFrontMz = frontMz;

        float normalizedDerivative = Math.Clamp(mzDerivative * ScrubForceScale, -1f, 1f);

        float amplitude = ScrubMaxAmplitude * ScrubGain * _smScrubIntensity;
        ScrubModulation = Math.Clamp(normalizedDerivative * amplitude, -ScrubMaxAmplitude, ScrubMaxAmplitude);
    }

    /// <summary>
    /// Data-driven rear slip warning: uses real rear Mz/Fy force derivatives + yaw acceleration.
    /// Rear tyres losing grip causes real force oscillations captured directly from the tyre model.
    /// No synthetic sine waves.
    /// </summary>
    private void GenerateRearSlipModulation(FfbRawData raw)
    {
        if (RearSlipGain < 0.001f || raw.SpeedKmh < 15f)
        {
            RearSlipModulation = 0f;
            _smRearSlipIntensity = 0f;
            _prevYawRate = 0f;
            _yawInitialized = false;
            _prevRearMz = (raw.Mz[2] + raw.Mz[3]) * 0.5f;
            _prevRearFy = (raw.Fy[2] + raw.Fy[3]) * 0.5f;
            return;
        }

        float rearSlipAngle = (Math.Abs(raw.SlipAngle[2]) + Math.Abs(raw.SlipAngle[3])) * 0.5f;

        float slipIntensity = 0f;
        if (rearSlipAngle > RearSlipOnsetAngle)
        {
            float range = Math.Max(RearSlipPeakAngle - RearSlipOnsetAngle, 0.01f);
            float t = Math.Clamp((rearSlipAngle - RearSlipOnsetAngle) / range, 0f, 1f);
            slipIntensity = t * t * t;
        }

        float yawRate = raw.LocalAngularVel.Length > 1 ? raw.LocalAngularVel[1] : 0f;
        float yawAccel = 0f;
        if (_yawInitialized)
        {
            yawAccel = (yawRate - _prevYawRate) / TickSeconds;
        }
        _prevYawRate = yawRate;
        _yawInitialized = true;

        float yawIntensity = 0f;
        if (Math.Abs(yawAccel) > YawAccelReference * 0.3f)
        {
            float normalizedYawAccel = Math.Clamp(Math.Abs(yawAccel) / YawAccelReference, 0f, 3f);
            yawIntensity = Math.Min(normalizedYawAccel, 1f);
        }

        float combinedIntensity = Math.Max(
            slipIntensity,
            yawIntensity * 0.6f
        );

        if (slipIntensity > 0.1f && yawIntensity > 0.1f)
        {
            combinedIntensity = Math.Min(combinedIntensity * YawAccelMultiplier, 1.5f);
        }

        _smRearSlipIntensity = _smRearSlipIntensity * 0.80f + combinedIntensity * 0.20f;

        if (_smRearSlipIntensity < 0.001f)
        {
            RearSlipModulation = 0f;
            _prevRearMz = (raw.Mz[2] + raw.Mz[3]) * 0.5f;
            _prevRearFy = (raw.Fy[2] + raw.Fy[3]) * 0.5f;
            return;
        }

        float rearMz = (raw.Mz[2] + raw.Mz[3]) * 0.5f;
        float rearFy = (raw.Fy[2] + raw.Fy[3]) * 0.5f;
        float mzDerivative = (rearMz - _prevRearMz) / TickSeconds;
        float fyDerivative = (rearFy - _prevRearFy) / TickSeconds;
        _prevRearMz = rearMz;
        _prevRearFy = rearFy;

        float forceOscillation = Math.Clamp(
            (mzDerivative + fyDerivative * 0.5f) * RearSlipForceScale, -1f, 1f);

        float amplitude = RearSlipMaxAmplitude * RearSlipGain * Math.Min(_smRearSlipIntensity, 1f);
        RearSlipModulation = Math.Clamp(forceOscillation * amplitude, -RearSlipMaxAmplitude, RearSlipMaxAmplitude);
    }

    public void Reset()
    {
        _absPhase = 0f;
        AbsForceModulation = 0f;
        _smAbsModulation = 0f;
        RoadForceModulation = 0f;
        _smSuspCurb = 0f;
        _smSuspRoad = 0f;
        _contactNormalZeroFrames = 0;
        _contactNormalFallback = false;
        WetCurbScale = 1.0f;

        ScrubModulation = 0f;
        _smScrubIntensity = 0f;
        _prevFrontMz = 0f;

        RearSlipModulation = 0f;
        _smRearSlipIntensity = 0f;
        _prevRearMz = 0f;
        _prevRearFy = 0f;
        _prevYawRate = 0f;
        _yawInitialized = false;

        Array.Clear(_prevSuspTravel);
        Array.Clear(_prevWheelLoad);
    }
}
