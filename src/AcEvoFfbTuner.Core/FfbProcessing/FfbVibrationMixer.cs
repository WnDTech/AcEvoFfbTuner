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

    public float RoadForceModulation { get; private set; }

    private float _absPhase;
    private const float AbsPulseHz = 15f;
    private const float TickSeconds = 1f / 333f;
    private const float CurbDeltaThreshold = 0.002f;

    public float AbsForceModulation { get; private set; }

    private float[] _prevSuspTravel = new float[4];
    private float _smSuspCurb;
    private float _smSuspRoad;

    // ── Front Tire Scrub Modulation ──
    // High-frequency "grainy" vibration (30-50Hz) injected into the main force
    // when front tire slip angles approach the Mz peak. Simulates rubber tearing/
    // micro-skidding at the contact patch — the texture you feel through a real
    // steering wheel just before the front tires let go.

    public float ScrubGain { get; set; } = 0.50f;
    public float ScrubOnsetAngle { get; set; } = 0.05f;
    public float ScrubPeakAngle { get; set; } = 0.08f;
    public float ScrubMaxAmplitude { get; set; } = 0.15f;
    public float ScrubModulation { get; private set; }

    private float _scrubPhase1;
    private float _scrubPhase2;
    private float _scrubPhase3;
    private const float ScrubFreq1 = 35f;
    private const float ScrubFreq2 = 47f;
    private const float ScrubFreq3 = 23f;
    private float _smScrubIntensity;

    // ── Rear Slip Warning Modulation ──
    // When rear tires lose grip (oversteer onset), the driver needs to feel it
    // through the steering wheel immediately. In a real car this manifests as:
    //   1. A sudden force reduction (steering goes light as front slip angle drops)
    //   2. A distinct low-frequency rumble/shudder (15-25Hz) — different from the
    //      front scrub's high-frequency grain. Feels like the car oscillating beneath you.
    //   3. A yaw-rate-driven force pulse that pushes the wheel in the countersteer direction.
    //
    // We detect rear slip via:
    //   - Rear slip angles exceeding a threshold (rear tires sliding)
    //   - Yaw rate acceleration (car rotating faster than steering input commands)
    //   - Lateral G sustained at high levels (cornering at the limit)

    /// <summary>
    /// Gain for the rear-end slip warning effect. 0 = disabled, 1.0 = full.
    /// Default 0.25 — slightly stronger than front scrub because rear slip is more
    /// critical to detect (you need to catch oversteer early).
    /// </summary>
    public float RearSlipGain { get; set; } = 0.60f;

    /// <summary>
    /// Rear slip angle threshold (radians) where the warning starts.
    /// Rear tires typically generate their peak lateral force at slightly higher slip
    /// angles than fronts. Default 0.07 rad (~4.0 degrees).
    /// </summary>
    public float RearSlipOnsetAngle { get; set; } = 0.07f;

    /// <summary>
    /// Rear slip angle (radians) where the warning reaches full intensity.
    /// Default 0.10 rad (~5.7 degrees) — beyond peak, clearly sliding.
    /// </summary>
    public float RearSlipPeakAngle { get; set; } = 0.10f;

    /// <summary>
    /// Maximum amplitude of the rear slip rumble (fraction of main force).
    /// Default 0.12 (12%) — more noticeable than front scrub because rear slip is dangerous.
    /// </summary>
    public float RearSlipMaxAmplitude { get; set; } = 0.20f;

    /// <summary>
    /// How much the yaw rate acceleration amplifies the rear slip warning.
    /// A car snapping into oversteer has rapid yaw acceleration. Default 1.5x.
    /// </summary>
    public float YawAccelMultiplier { get; set; } = 1.5f;

    /// <summary>
    /// Reference yaw acceleration (rad/s^2) for normalization.
    /// Typical oversteer onset: 1-3 rad/s^2. Default 2.0.
    /// </summary>
    public float YawAccelReference { get; set; } = 2.0f;

    /// <summary>
    /// Current rear slip modulation signal. Added to main force by FfbPipeline.
    /// Uses LOWER frequencies (15-25Hz) than front scrub (30-50Hz) so the driver
    /// can distinguish "rear is sliding" from "front is at the limit".
    /// </summary>
    public float RearSlipModulation { get; private set; }

    private float _rearPhase1;  // ~18 Hz primary rumble
    private float _rearPhase2;  // ~25 Hz secondary
    private float _rearPhase3;  // ~12 Hz sub-harmonic (weight)
    private const float RearFreq1 = 18f;
    private const float RearFreq2 = 25f;
    private const float RearFreq3 = 12f;
    private float _smRearSlipIntensity;
    private float _prevYawRate;
    private bool _yawInitialized;

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
            float pulse = MathF.Sin(_absPhase * MathF.PI * 2f);
            float absAmp = (0.6f + 0.4f * raw.BrakeInput) * AbsGain;
            AbsForceModulation = absAmp * Math.Max(pulse, 0f);
            abs = Math.Max(abs, AbsForceModulation);
        }
        else
        {
            AbsForceModulation = 0f;
        }

        float combined = kerb + slip + road + abs;

        float speedFade = raw.SpeedKmh < 10.0f
            ? (raw.SpeedKmh - 2.0f) / 8.0f
            : 1.0f;

        float curbDelta = 0f;
        float roadDelta = 0f;
        for (int i = 0; i < 4; i++)
        {
            float delta = raw.SuspensionTravel[i] - _prevSuspTravel[i];
            _prevSuspTravel[i] = raw.SuspensionTravel[i];
            float weight = i < 2 ? 1.5f : 0.75f;
            if (MathF.Abs(delta) > CurbDeltaThreshold)
                curbDelta += delta * weight;
            else
                roadDelta += delta * weight;
        }

        _smSuspCurb = _smSuspCurb * 0.3f + curbDelta * 0.7f;
        _smSuspRoad = _smSuspRoad * 0.3f + roadDelta * 0.7f;

        float suspSpeedScale = Math.Clamp(raw.SpeedKmh / 100f, 0f, 2f);
        float curbForce = _smSuspCurb * 75f * MathF.Max(KerbGain, 0.1f) * suspSpeedScale;
        float roadForce = _smSuspRoad * 150f * MathF.Max(RoadGain, 0.1f) * suspSpeedScale;
        float rawVib = (curbForce + roadForce) * SuspensionRoadGain;
        RoadForceModulation = Math.Clamp(rawVib, -0.15f, 0.15f);

        // ── Front tire scrub modulation ──
        GenerateScrubModulation(raw);

        // ── Rear slip warning modulation ──
        GenerateRearSlipModulation(raw);

        return Math.Clamp(combined * MasterGain * speedFade, 0f, 1f);
    }

    /// <summary>
    /// Front scrub: high-frequency grain (30-50Hz) as front slip angle approaches Mz peak.
    /// </summary>
    private void GenerateScrubModulation(FfbRawData raw)
    {
        if (ScrubGain < 0.001f || raw.SpeedKmh < 10f)
        {
            ScrubModulation = 0f;
            _smScrubIntensity = 0f;
            return;
        }

        // Average absolute front slip angle
        float absSlipAngle = (Math.Abs(raw.SlipAngle[0]) + Math.Abs(raw.SlipAngle[1])) * 0.5f;

        // Compute scrub intensity: ramps from 0 at onset to 1 at peak
        float scrubIntensity = 0f;
        if (absSlipAngle > ScrubOnsetAngle)
        {
            float range = Math.Max(ScrubPeakAngle - ScrubOnsetAngle, 0.01f);
            float t = Math.Clamp((absSlipAngle - ScrubOnsetAngle) / range, 0f, 1f);
            // Smooth ramp (ease-in) so the scrub builds gradually
            scrubIntensity = t * t;

            // Fade out beyond peak (post-peak scrub is not realistic —
            // the Mz dropoff handles the warning, scrub would just be noise)
            // if (absSlipAngle > ScrubPeakAngle)
            {
                // float fadeRange = ScrubPeakAngle * 0.5f;
                // float fadeOut = 1f - Math.Clamp((absSlipAngle - ScrubPeakAngle) / fadeRange, 0f, 1f);
            // scrubIntensity *= fadeOut;  // REMOVED: keep scrub active at and past peak
            }
        }

        // Smooth the intensity to prevent flicker
        _smScrubIntensity = _smScrubIntensity * 0.65f + scrubIntensity * 0.35f;

        if (_smScrubIntensity < 0.001f)
        {
            ScrubModulation = 0f;
            return;
        }

        // Advance phase accumulators (333Hz tick rate)
        _scrubPhase1 += ScrubFreq1 * TickSeconds;
        _scrubPhase2 += ScrubFreq2 * TickSeconds;
        _scrubPhase3 += ScrubFreq3 * TickSeconds;
        if (_scrubPhase1 > 1f) _scrubPhase1 -= 1f;
        if (_scrubPhase2 > 1f) _scrubPhase2 -= 1f;
        if (_scrubPhase3 > 1f) _scrubPhase3 -= 1f;

        // Mix 3 incommensurate sine waves for noise-like texture
        // Primary (35Hz) is loudest, secondary (47Hz) adds grain, sub-harmonic (23Hz) adds weight
        float scrubSignal =
            0.50f * MathF.Sin(_scrubPhase1 * MathF.PI * 2f) +
            0.30f * MathF.Sin(_scrubPhase2 * MathF.PI * 2f) +
            0.20f * MathF.Sin(_scrubPhase3 * MathF.PI * 2f);

        // Apply gain and intensity
        float amplitude = ScrubMaxAmplitude * ScrubGain * _smScrubIntensity;
        ScrubModulation = Math.Clamp(scrubSignal * amplitude, -ScrubMaxAmplitude, ScrubMaxAmplitude);
    }

    /// <summary>
    /// Rear slip warning: low-frequency rumble (12-25Hz) when rear tires lose grip.
    /// Also detects rapid yaw acceleration (car snapping into oversteer).
    /// Uses LOWER frequencies than front scrub so the driver can distinguish
    /// "the rear is sliding" from "the front is at the limit".
    /// </summary>
    private void GenerateRearSlipModulation(FfbRawData raw)
    {
        if (RearSlipGain < 0.001f || raw.SpeedKmh < 15f)
        {
            RearSlipModulation = 0f;
            _smRearSlipIntensity = 0f;
            _prevYawRate = 0f;
            _yawInitialized = false;
            return;
        }

        // ── Signal 1: Rear slip angle magnitude ──
        float rearSlipAngle = (Math.Abs(raw.SlipAngle[2]) + Math.Abs(raw.SlipAngle[3])) * 0.5f;

        float slipIntensity = 0f;
        if (rearSlipAngle > RearSlipOnsetAngle)
        {
            float range = Math.Max(RearSlipPeakAngle - RearSlipOnsetAngle, 0.01f);
            float t = Math.Clamp((rearSlipAngle - RearSlipOnsetAngle) / range, 0f, 1f);
            // Steeper ramp than front scrub — rear slip is more urgent
            slipIntensity = t * t * t;

            // Unlike front scrub, DON'T fade out beyond peak — if the rear is fully
            // sliding the driver needs continuous warning until they correct it.
            // Instead, cap at 1.0.
        }

        // ── Signal 2: Yaw rate acceleration ──
        // Oversteer onset causes rapid yaw acceleration. This detects the "snap".
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
            // Normalize yaw acceleration — rapid oversteer onset can hit 3-5 rad/s^2
            float normalizedYawAccel = Math.Clamp(Math.Abs(yawAccel) / YawAccelReference, 0f, 3f);
            yawIntensity = Math.Min(normalizedYawAccel, 1f);
        }

        // ── Combine: slip angle is the base, yaw acceleration amplifies it ──
        // If yaw is accelerating but slip angle is still low, still trigger a warning
        // (the rear is starting to step out even if the absolute angle is small).
        float combinedIntensity = Math.Max(
            slipIntensity,
            yawIntensity * 0.6f  // Yaw-only trigger at reduced intensity
        );

        // Yaw acceleration boost on top of slip detection
        if (slipIntensity > 0.1f && yawIntensity > 0.1f)
        {
            combinedIntensity = Math.Min(combinedIntensity * YawAccelMultiplier, 1.5f);
        }

        // Smooth to prevent flicker, but faster response than front scrub
        // (rear slip needs quicker detection — the driver has less time to react)
        _smRearSlipIntensity = _smRearSlipIntensity * 0.80f + combinedIntensity * 0.20f;

        if (_smRearSlipIntensity < 0.001f)
        {
            RearSlipModulation = 0f;
            return;
        }

        // Advance phase accumulators
        _rearPhase1 += RearFreq1 * TickSeconds;
        _rearPhase2 += RearFreq2 * TickSeconds;
        _rearPhase3 += RearFreq3 * TickSeconds;
        if (_rearPhase1 > 1f) _rearPhase1 -= 1f;
        if (_rearPhase2 > 1f) _rearPhase2 -= 1f;
        if (_rearPhase3 > 1f) _rearPhase3 -= 1f;

        // Mix 3 LOW frequencies for a distinct rumble (different character from front scrub)
        // Primary (18Hz) is the main rumble, secondary (25Hz) adds urgency,
        // sub-harmonic (12Hz) adds weight/oscillation feel
        float rearSignal =
            0.45f * MathF.Sin(_rearPhase1 * MathF.PI * 2f) +
            0.35f * MathF.Sin(_rearPhase2 * MathF.PI * 2f) +
            0.20f * MathF.Sin(_rearPhase3 * MathF.PI * 2f);

        // Apply gain and intensity
        float amplitude = RearSlipMaxAmplitude * RearSlipGain * Math.Min(_smRearSlipIntensity, 1f);
        RearSlipModulation = Math.Clamp(rearSignal * amplitude, -RearSlipMaxAmplitude, RearSlipMaxAmplitude);
    }

    public void Reset()
    {
        _absPhase = 0f;
        AbsForceModulation = 0f;
        RoadForceModulation = 0f;
        _smSuspCurb = 0f;
        _smSuspRoad = 0f;

        ScrubModulation = 0f;
        _smScrubIntensity = 0f;
        _scrubPhase1 = 0f;
        _scrubPhase2 = 0f;
        _scrubPhase3 = 0f;

        RearSlipModulation = 0f;
        _smRearSlipIntensity = 0f;
        _rearPhase1 = 0f;
        _rearPhase2 = 0f;
        _rearPhase3 = 0f;
        _prevYawRate = 0f;
        _yawInitialized = false;

        Array.Clear(_prevSuspTravel);
    }
}
