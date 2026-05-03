using AcEvoFfbTuner.Core.FfbProcessing.Models;

namespace AcEvoFfbTuner.Core.FfbProcessing;

public sealed class FfbPipeline
{
    public FfbChannelMixer ChannelMixer { get; } = new();
    public FfbLutCurve LutCurve { get; } = new();
    public FfbDamping Damping { get; } = new();
    public FfbSlipEnhancer SlipEnhancer { get; } = new();
    public FfbDynamicEffects DynamicEffects { get; } = new();
    public FfbVibrationMixer VibrationMixer { get; } = new();
    public FfbLfeGenerator LfeGenerator { get; } = new();
    public FfbOutputClipper OutputClipper { get; } = new();
    public FfbEqualizer Equalizer { get; } = new();
    public FfbTyreFlex TyreFlex { get; } = new();


    public float ForceScale { get; set; } = 1.0f;
    public float OutputGain { get; set; } = 1.0f;
    public float MasterGain { get; set; } = 1.0f;
    public float CompressionPower { get; set; } = 1.0f;

    public bool AutoGainEnabled { get; set; } = false;
    public float AutoGainScale { get; set; } = 1.0f;

    // No-op: sign correction is handled by device-level inversion
    public bool SignCorrectionEnabled { get; set; } = true;

    public float CenterDeadzone { get; set; } = 0.04f;
    public float SoftCenterDegrees { get; set; } = 1.5f;
    public float NoiseFloor { get; set; } = 0.003f;

    /// <summary>
    /// Maximum slew rate for the DETAIL path only (per frame).
    /// The core path has NO slew limit (zero-latency).
    /// Default 0.85 — high enough to pass transients, low enough for USB spike protection.
    /// </summary>
    public float MaxSlewRate { get; set; } = 0.85f;

    public float SteerDirDeadzone { get; set; } = 0.004f;
    public float CenterSuppressionDegrees { get; set; } = 1.5f;
    public float HysteresisThreshold { get; set; } = 0.015f;
    public int HysteresisWatchdogFrames { get; set; } = 5;
    public float CenterKneePower { get; set; } = 1.0f;

    private float _prevDetailOutput;

    public FfbProcessedData Process(FfbRawData raw)
    {
        float autoGain = 1.0f;
        if (AutoGainEnabled && raw.CarFfbMultiplier > 0.001f)
        {
            autoGain = AutoGainScale / raw.CarFfbMultiplier;
        }

        // Get both raw core force (zero-latency) and smoothed detail from mixer
        float mixedForce = ChannelMixer.Mix(raw, out var channels);
        float rawCoreForce = channels.RawCoreForce;

        // ═══════════════════════════════════════════════════════════════════════
        // CORE PATH — Zero-Latency Steering Forces
        //
        // Raw Mz + Fx + Fy → Normalize → LUT → Damping → Gain → Speed Fade
        // No EMAs, no biquads, no slew limits. The primary self-aligning torque
        // reaches the DirectInput device with absolute minimum phase delay.
        // Median filter only (3-sample) for USB spike rejection (adds max 3ms).
        // ═══════════════════════════════════════════════════════════════════════

        float coreNorm = rawCoreForce * MasterGain * autoGain / Math.Max(ForceScale, 0.001f);
        float absCoreNorm = Math.Abs(coreNorm);
        float corePostLut = LutCurve.Apply(absCoreNorm) * Math.Sign(coreNorm);

        // Damping: uses RAW steer angle (not smoothed) for maximum responsiveness.
        // Pure viscous + Coulomb are ALWAYS ACTIVE (not speed-dependent).
        // Gyroscopic + inertia are speed-scaled.
        float coreDamped = Damping.Apply(corePostLut, raw.SpeedKmh, raw.SteerAngle);

        // Center knee power (optional non-linear shaping of core force)
        if (CenterKneePower > 1.001f && Math.Abs(coreDamped) > 0f)
            coreDamped = Math.Sign(coreDamped) * MathF.Pow(Math.Abs(coreDamped), CenterKneePower);

        // Output gain
        float coreOutput = coreDamped * OutputGain;

        // Speed fade: zero below 0.5 km/h, ramp to full at 5 km/h
        if (raw.SpeedKmh < 0.5f)
        {
            coreOutput = 0f;
            _prevDetailOutput = 0f;
        }
        else if (raw.SpeedKmh < 5.0f)
        {
            float speedFactor = (raw.SpeedKmh - 0.5f) / 4.5f;
            coreOutput *= speedFactor;
        }

        // ═══════════════════════════════════════════════════════════════════════
        // DETAIL PATH — Filtered Textural Forces
        //
        // Additive effects only: slip enhancement, dynamic effects, tyre flex,
        // vibration signals (scrub, road, ABS, rear slip), LFE.
        // These run through EQ biquads and a slew rate limiter.
        // Phase delay is acceptable here — these are "feel" details, not the
        // primary self-aligning torque that prevents oscillation.
        // ═══════════════════════════════════════════════════════════════════════

        float detailForce = 0f;

        // Slip enhancer contribution (force-relative scaling)
        float postSlip = SlipEnhancer.Apply(corePostLut, raw);
        detailForce += (postSlip - corePostLut);

        // Dynamic effects contribution (independent of force magnitude)
        float dynamicContrib = DynamicEffects.Apply(0f, raw);
        detailForce += dynamicContrib;

        // Tyre flex contribution (independent of force magnitude)
        float flexContrib = TyreFlex.Apply(0f, raw);
        detailForce += flexContrib;

        // Vibration signals (suspension-based road/curb, scrub, rear slip)
        float vibration = VibrationMixer.Mix(raw);

        // ABS force modulation (directional — follows core force sign)
        float absMod = VibrationMixer.AbsForceModulation;
        if (absMod > 0.001f)
        {
            float sign = coreOutput >= 0f ? 1f : -1f;
            if (Math.Abs(coreOutput) < 0.01f)
                sign = Math.Abs(raw.SteerAngle) > SteerDirDeadzone ? -Math.Sign(raw.SteerAngle) : 1f;
            detailForce += absMod * sign;
        }

        float roadMod = VibrationMixer.RoadForceModulation;
        if (Math.Abs(roadMod) > 0.001f)
            detailForce += roadMod;

        // Front tire scrub texture (30-50Hz grain at the limit)
        float scrubMod = VibrationMixer.ScrubModulation;
        if (Math.Abs(scrubMod) > 0.001f)
            detailForce += scrubMod;

        // Rear slip warning (12-25Hz rumble when rear loses grip)
        float rearMod = VibrationMixer.RearSlipModulation;
        if (Math.Abs(rearMod) > 0.001f)
            detailForce += rearMod;

        // LFE (engine RPM / suspension-driven low-frequency effects)
        float lfe = LfeGenerator.Generate(raw);
        if (Math.Abs(lfe) > 0.001f)
            detailForce += lfe;

        // Apply EQ (biquad filters) to detail path ONLY
        detailForce = Equalizer.Process(detailForce);

        // Slew rate limiter on detail path only (higher limit for transients).
        // Core path has NO slew limit — preserving zero-latency centering force.
        if (raw.SpeedKmh >= 0.5f)
        {
            float slewDelta = detailForce - _prevDetailOutput;
            if (Math.Abs(slewDelta) > MaxSlewRate)
            {
                detailForce = _prevDetailOutput + Math.Sign(slewDelta) * MaxSlewRate;
            }
        }
        _prevDetailOutput = raw.SpeedKmh < 0.5f ? 0f : detailForce;

        // ═══════════════════════════════════════════════════════════════════════
        // FINAL MIX — Core (zero-latency) + Detail (filtered)
        // ═══════════════════════════════════════════════════════════════════════

        float finalOutput = coreOutput + detailForce;

        // Output clipper (soft clip at threshold)
        finalOutput = OutputClipper.Process(finalOutput, out bool isClipping);

        // Noise floor gate (amplified at low speed for physics jitter suppression)
        float speedNoiseScale = raw.SpeedKmh < 10.0f
            ? 1.0f + (1.0f - raw.SpeedKmh / 10.0f) * 2.0f
            : 1.0f;
        float effectiveNoiseFloor = NoiseFloor * speedNoiseScale;
        if (Math.Abs(finalOutput) < effectiveNoiseFloor)
            finalOutput = 0f;

        return new FfbProcessedData
        {
            MainForce = finalOutput,
            VibrationForce = vibration,
            RawFinalFf = raw.FinalFf,
            ChannelMzFront = channels.MzFront,
            ChannelFxFront = channels.FxFront,
            ChannelFyFront = channels.FyFront,
            ChannelMzRear = channels.MzRear,
            ChannelFxRear = channels.FxRear,
            ChannelFyRear = channels.FyRear,
            ChannelFinalFf = channels.FinalFf,
            PostCompressionForce = coreNorm,
            PostLutForce = corePostLut,
            PostSlipForce = coreDamped,
            PostDampingForce = coreOutput,
            PostDynamicForce = detailForce,
            AutoGainApplied = autoGain,
            IsClipping = isClipping,
            IsOscillating = false,
            OscillationLevel = 0f,
            OscillationStabilityFactor = 1f,
            ForceDirectionWarning = false,
            SpeedKmh = raw.SpeedKmh,
            SteerAngle = raw.SteerAngle,
            PacketId = raw.PacketId,
            CoreForce = coreOutput,
            DetailForce = detailForce
        };
    }

    public void Reset()
    {
        ChannelMixer.Reset();
        Damping.Reset();
        DynamicEffects.Reset();
        VibrationMixer.Reset();
        LfeGenerator.Reset();
        Equalizer.Reset();
        TyreFlex.Reset();


        _prevDetailOutput = 0f;
    }
}
