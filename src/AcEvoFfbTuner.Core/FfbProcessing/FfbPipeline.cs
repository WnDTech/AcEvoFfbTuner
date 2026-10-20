using AcEvoFfbTuner.Core.DirectInput;
using AcEvoFfbTuner.Core.FfbProcessing.Models;

namespace AcEvoFfbTuner.Core.FfbProcessing;

public class FfbPipeline
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
    /// <summary>
    /// HF8 signal mapper. Virtual so game-specific pipelines can expose their
    /// own mapper instance (R3eHf8SignalMapper / LmuHf8SignalMapper) through
    /// this property — otherwise UI/profile writes (Enable, gains, weights)
    /// would land on this unused base instance and have no effect.
    /// </summary>
    public virtual Hf8SignalMapper Hf8SignalMapper { get; } = new();
    public FfbGripGuard GripGuard { get; } = new();
    public FfbCrashDetector CrashDetector { get; } = new();
    public FfbTyreCondition TyreCondition { get; } = new();
    public FfbWetWeather WetWeather { get; } = new();

    /// <summary>
    /// Maps raw + processed telemetry to HF8 haptic pad motor intensities.
    /// Virtual so game-specific pipelines can provide their own mapping.
    /// Base implementation delegates to Hf8SignalMapper.
    /// </summary>
    public virtual float[] MapHf8Motors(FfbRawData raw, FfbProcessedData processed)
    {
        return Hf8SignalMapper.Map(raw, processed, VibrationMixer, LfeGenerator);
    }

    public TyreCompoundCategory CurrentTyreCategory { get; private set; } = TyreCompoundCategory.Unknown;
    public string CurrentTyreCompoundFront { get; private set; } = "";
    public string CurrentTyreCompoundRear { get; private set; } = "";


    public float ForceScale { get; set; } = 1.0f;
    public float OutputGain { get; set; } = 1.0f;
    public float MasterGain { get; set; } = 1.0f;
    public float CompressionPower { get; set; } = 1.0f;

    public bool AutoGainEnabled { get; set; } = false;
    public float AutoGainScale { get; set; } = 1.0f;
    public virtual bool GearShiftFilterEnabled { get; set; } = false;

    public float GearShiftMuteGain { get; private set; } = 1.0f;

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
    /// <summary>
    /// RESERVED (not consumed by base Process): center deadzone width in degrees.
    /// Round-trips profile ↔ pipeline so saved values are preserved. Game-specific
    /// centering lives in subclass overrides (e.g. R3E ApplyCenteringOverride).
    /// Do NOT wire into the base core path without a baseline re-tune.
    /// </summary>
    public float CenterSuppressionDegrees { get; set; } = 1.5f;
    /// <summary>
    /// RESERVED (not consumed by base Process): output deadband for noise rejection.
    /// Round-trips profile ↔ pipeline so saved values are preserved.
    /// Do NOT wire into the base core path without a baseline re-tune.
    /// </summary>
    public float HysteresisThreshold { get; set; } = 0.015f;
    /// <summary>
    /// RESERVED (not consumed by base Process): watchdog ticks for the hysteresis deadband.
    /// Round-trips profile ↔ pipeline so saved values are preserved.
    /// </summary>
    public int HysteresisWatchdogFrames { get; set; } = 5;
    public float CenterKneePower { get; set; } = 1.0f;

    /// <summary>
    /// Center sharpness angle (R3E): steering degrees at which centering force
    /// reaches full strength via smoothstep ramp. Lower = sharper on-center feel,
    /// higher = softer/progressive. Default 3.0° matches reader smooth zone.
    /// 0 = disabled (reader-only shaping, maximum crispness).
    /// RaceRoom uses this via ApplyCenteringOverride.
    /// </summary>
    public float CenterSharpnessDegrees { get; set; } = 3.0f;

    /// <summary>
    /// Core force multiplier. Scales total core/centering force at ALL speeds.
    /// Used by R3E (default 3.0x) to compensate for pipeline processing attenuation.
    /// EVO default 1.0x (no compensation needed).
    /// Virtual so R3eFfbPipeline can override the default.
    /// </summary>
    public virtual float CoreForceMultiplier { get; set; } = 1.0f;

    public float ReverseAttenuation { get; set; } = 0.45f;
    public float ReverseDetailPass { get; set; } = 0.50f;
    public int ReverseGearValue { get; set; } = 0;

    // ─── Phase 1 safety scaffold: new effects default OFF (zero-impact) ───
    // Each effect is gated behind Enabled=false + Gain=0 and a virtual no-op
    // hook. Subclasses override per-game; base returns input unchanged.
    public bool SoftLockEnabled { get; set; } = false;
    public float SoftLockStrength { get; set; } = 0f;
    public float SoftLockDegrees { get; set; } = 900f;
    public bool GyroEnabled { get; set; } = false;
    public float GyroGain { get; set; } = 0f;
    public bool TcWheelEnabled { get; set; } = false;
    public float TcWheelGain { get; set; } = 0f;

    /// <summary>
    /// Low-speed noise-gate boost: gate = NoiseFloor * (1 + (1-speed/10) * boost).
    /// Base default 2.0 preserves EVO behavior. LMU overrides to 0.5.
    /// </summary>
    public virtual float LowSpeedNoiseBoost { get; set; } = 2.0f;

    /// <summary>
    /// RPM spike gate for gear-shift mute. Consumed by the ApplyGearShiftFilter
    /// overload (rpm, rpmThreshold). Base default float.MaxValue = disabled
    /// (EVO unchanged). R3E/LMU/AC/ACC override the default to 3000.
    /// Previously these per-game properties were stored but never read.
    /// </summary>
    public virtual float GearSpikeThreshold { get; set; } = float.MaxValue;

    private float _prevDetailOutput;
    private float _gyroSmooth;
    private float _tcWheelSmooth;
    private float _shiftMuteDuration = 0.030f;
    private float _fadeInDuration = 0.030f;
    private float _shiftTimeTracker;
    private int _lastGear = 1;
    private float _gearShiftPrevForce;

    public virtual FfbProcessedData Process(FfbRawData raw)
    {
        float autoGain = 1.0f;
        if (AutoGainEnabled && raw.CarFfbMultiplier > 0.001f)
        {
            autoGain = AutoGainScale / raw.CarFfbMultiplier;
        }

        // Wet weather: compute wetness factor before any processing.
        // The WetnessFactor (0=dry, 1=full wet) modulates vibration suppression,
        // damping, slip curve shape, and hydroplaning feel.
        WetWeather.Update(raw);

        CurrentTyreCategory = TyreCompoundClassifier.Classify(raw.TyreCompoundFront);
        CurrentTyreCompoundFront = raw.TyreCompoundFront;
        CurrentTyreCompoundRear = raw.TyreCompoundRear;

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
        // Wet weather: reduce damping to match lighter self-aligning torque feel.
        float savedViscous = Damping.ViscousCoefficient;
        float savedSpeedDamp = Damping.SpeedDampingCoefficient;
        float savedFriction = Damping.FrictionLevel;
        if (WetWeather.WetnessFactor > 0.01f)
        {
            float ds = WetWeather.DampingScale;
            Damping.ViscousCoefficient *= ds;
            Damping.SpeedDampingCoefficient *= ds;
            Damping.FrictionLevel *= ds;
        }
        float coreDamped = Damping.Apply(corePostLut, raw.SpeedKmh, raw.SteerAngle);
        Damping.ViscousCoefficient = savedViscous;
        Damping.SpeedDampingCoefficient = savedSpeedDamp;
        Damping.FrictionLevel = savedFriction;

        // Center knee power (optional non-linear shaping of core force)
        if (CenterKneePower > 1.001f && Math.Abs(coreDamped) > 0f)
            coreDamped = Math.Sign(coreDamped) * MathF.Pow(Math.Abs(coreDamped), CenterKneePower);

        // Output gain
        float coreOutput = coreDamped * OutputGain;
        float stageGainForce = coreOutput;

        // Game-specific centering override (e.g. R3E V-shape center suppression)
        coreOutput = ApplyCenteringOverride(coreOutput, raw);

        // Grip guard: prevents dangerous pull-away forces when front tyres lose grip.
        // Caps forces that pull wheel toward steer direction at post-peak slip angles.
        // Adds mechanical trail centering (like real caster) that's always active.
        coreOutput = GripGuard.Apply(coreOutput, raw);

        // Crash detection: generates impact kick forces for wall/car collisions.
        // Uses G-force spikes and speed deltas to detect crashes, then applies
        // a directional force pulse that decays exponentially.
        // Safety-clamped to prevent dangerous torque levels.
        coreOutput = CrashDetector.Apply(coreOutput, raw);

        // Tyre condition: blowout vibration, pressure loss feel, suspension damage asymmetry.
        // Modifies force based on real-time tyre pressure and damage data.
        coreOutput = TyreCondition.Apply(coreOutput, raw);

        // Wet weather hydroplaning: high speed + wet + load variation causes
        // momentary force drop and subtle steering wobble through standing water.
        coreOutput = WetWeather.ApplyHydroplaning(coreOutput, raw);

        // Reverse gear: invert physics sign and attenuate.
        // AC EVO's tire model produces forces with correct MAGNITUDE but
        // inverted DIRECTION when reversing — the self-aligning torque pushes
        // the wheel further into the turn instead of centering. Fix: simply
        // flip the sign so the existing centering logic (LUT, damping,
        // GripGuard, speed fade) all work naturally. Attenuate to ~45% since
        // reverse forces feel stronger than they should at low speed.
        bool isReversing = raw.Gear == ReverseGearValue && raw.SpeedKmh > 0.3f;
        if (isReversing)
        {
            coreOutput = -coreOutput * ReverseAttenuation;
        }

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

        // Slip enhancer: widen peak slip angle in wet conditions.
        // Wet tires have a broader Mz curve — peak occurs at higher slip angles.
        float savedPeakSlipAngle = SlipEnhancer.PeakSlipAngle;
        if (WetWeather.WetnessFactor > 0.01f)
            SlipEnhancer.PeakSlipAngle *= WetWeather.CurrentPeakSlipAngleScale;
        float postSlip = SlipEnhancer.Apply(corePostLut, raw);
        SlipEnhancer.PeakSlipAngle = savedPeakSlipAngle;
        float stageSlipDelta = postSlip - corePostLut;
        detailForce += stageSlipDelta;

        // Dynamic effects contribution (independent of force magnitude)
        float dynamicContrib = DynamicEffects.Apply(0f, raw);
        detailForce += dynamicContrib;

        // Tyre flex contribution (independent of force magnitude)
        float flexContrib = TyreFlex.Apply(0f, raw);
        detailForce += flexContrib;
        float stageDynFlex = dynamicContrib + flexContrib;

        // Vibration signals (suspension-based road/curb, scrub, rear slip)
        // Wet weather: suppress curb feel (water layer absorbs curb impacts)
        VibrationMixer.WetCurbScale = WetWeather.CurbScale;
        float vibration = VibrationMixer.Mix(raw);

        // Route the full vibration mix (kerb/slip/road/ABS channels) into the wheel
        // detail force. Previously only the sub-modulations below reached the wheel;
        // the raw curb channel was sent only to the separate vibration effect, which
        // direct-drive wheels (e.g. Moza R5) do not render on the main motor.
        detailForce += vibration;

        // ABS force modulation (directional — follows core force sign)
        float stageSubMods = 0f;
        float absMod = VibrationMixer.AbsForceModulation;
        if (Math.Abs(absMod) > 0.001f)
        {
            float sign = coreOutput >= 0f ? 1f : -1f;
            if (Math.Abs(coreOutput) < 0.01f)
                sign = Math.Abs(raw.SteerAngle) > SteerDirDeadzone ? -Math.Sign(raw.SteerAngle) : 1f;
            detailForce += absMod * sign;
            stageSubMods += absMod * sign;
        }

        // Wet weather: suppress road vibration (water film damps surface texture)
        float roadMod = VibrationMixer.RoadForceModulation;
        if (Math.Abs(roadMod) > 0.001f)
        {
            detailForce += roadMod * WetWeather.RoadVibScale;
            stageSubMods += roadMod * WetWeather.RoadVibScale;
        }

        // Wet weather: suppress scrub texture (water lubricates contact patch)
        float scrubMod = VibrationMixer.ScrubModulation;
        if (Math.Abs(scrubMod) > 0.001f)
        {
            detailForce += scrubMod * WetWeather.ScrubScale;
            stageSubMods += scrubMod * WetWeather.ScrubScale;
        }

        // Rear slip warning (12-25Hz rumble when rear loses grip)
        float rearMod = VibrationMixer.RearSlipModulation;
        if (Math.Abs(rearMod) > 0.001f)
        {
            detailForce += rearMod;
            stageSubMods += rearMod;
        }

        // Offtrack surface rumble (when all 4 wheels leave the racing surface)
        float offtrackMod = VibrationMixer.OfftrackModulation;
        if (Math.Abs(offtrackMod) > 0.001f)
        {
            detailForce += offtrackMod;
            stageSubMods += offtrackMod;
        }

        // LFE (engine RPM / suspension-driven low-frequency effects)
        float lfe = LfeGenerator.Generate(raw);
        if (Math.Abs(lfe) > 0.001f)
        {
            detailForce += lfe;
            stageSubMods += lfe;
        }

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

        if (isReversing)
            detailForce *= ReverseDetailPass;

        _prevDetailOutput = raw.SpeedKmh < 0.5f ? 0f : detailForce;

        // Hook for subclasses to modify detailForce before final mix
        OnDetailForceProcessed(coreOutput, ref detailForce);

        // Phase 1 scaffold hooks — no-op unless enabled + overridden per-game.
        // Gyro stabilizer (detail-path opposing torque) and TC wheel feel.
        detailForce = ApplyGyroStabilizer(detailForce, raw);
        detailForce = ApplyTcWheelEffect(detailForce, raw);
        // Rear-slip + yaw-onset hooks: base no-ops (EVO unchanged).
        detailForce = ApplyRearSlipOverride(detailForce, raw, 1f / 333f);
        detailForce = ApplyYawOnset(detailForce, raw, 1f / 333f);

        // ═══════════════════════════════════════════════════════════════════════
        // FINAL MIX — Core (zero-latency) + Detail (filtered)
        // ═══════════════════════════════════════════════════════════════════════

        // Gear shift filter: EMA on core force only — detail effects pass through.
        // Passes EngineRpm + per-game threshold (EVO base default = disabled).
        float deltaTime = 1f / 60f;
        float filteredCore = ApplyGearShiftFilter(coreOutput, deltaTime, raw.Gear, raw.EngineRpm, GearSpikeThreshold);
        float finalOutput = filteredCore + detailForce;
        float stagePreClip = finalOutput;

        // Output clipper (soft clip at threshold)
        finalOutput = OutputClipper.Process(finalOutput, out bool isClipping);

        // Soft lock end-stop: progressive wall past steering lock.
        // Disabled by default (SoftLockEnabled=false, Strength=0).
        finalOutput = ApplySoftLock(finalOutput, raw);

        // Noise floor gate (amplified at low speed for physics jitter suppression)
        // Wet weather: raise noise floor (water film masks tiny surface details)
        // Boost factor is per-game (EVO 2.0 legacy default; LMU overrides to 0.5).
        float speedNoiseScale = raw.SpeedKmh < 10.0f
            ? 1.0f + (1.0f - raw.SpeedKmh / 10.0f) * LowSpeedNoiseBoost
            : 1.0f;
        float effectiveNoiseFloor = NoiseFloor * speedNoiseScale * WetWeather.NoiseFloorScale;
        if (Math.Abs(finalOutput) < effectiveNoiseFloor)
            finalOutput = 0f;



        return new FfbProcessedData
        {
            MainForce = finalOutput,
            VibrationForce = vibration * GearShiftMuteGain,
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
            PostDampingForce = coreDamped,
            PostOutputGainForce = coreOutput,
            PostDynamicForce = detailForce,
            AutoGainApplied = autoGain,
            IsClipping = isClipping,
            SpeedKmh = raw.SpeedKmh,
            SteerAngle = raw.SteerAngle,
            PacketId = raw.PacketId,
            CoreForce = coreOutput,
            DetailForce = detailForce,
            WetnessFactor = WetWeather.WetnessFactor,
            TyreCategory = TyreCompoundClassifier.Classify(raw.TyreCompoundFront),
            TyreCompoundFrontName = raw.TyreCompoundFront,
            TyreCompoundRearName = raw.TyreCompoundRear,
            GearShiftMuteGain = GearShiftMuteGain,
            StageMixedForce = mixedForce,
            StageGainForce = stageGainForce,
            StageSlipDelta = stageSlipDelta,
            StageDynFlex = stageDynFlex,
            StageSubMods = stageSubMods,
            StagePreClipForce = stagePreClip
        };
    }

    public virtual void Reset()
    {
        ChannelMixer.Reset();
        Damping.Reset();
        DynamicEffects.Reset();
        VibrationMixer.Reset();
        LfeGenerator.Reset();
        Equalizer.Reset();
        TyreFlex.Reset();
        Hf8SignalMapper.Reset();
        GripGuard.Reset();
        CrashDetector.Reset();
        TyreCondition.Reset();
        WetWeather.Reset();



        _prevDetailOutput = 0f;
        _gyroSmooth = 0f;
        _tcWheelSmooth = 0f;
        _shiftTimeTracker = 0f;
        _lastGear = 1;
        _gearShiftPrevForce = 0f;
        GearShiftMuteGain = 1.0f;
    }

    /// <summary>
    /// Called after detailForce is fully computed but before final core+detail mix.
    /// Override in subclass to apply game-specific detail path processing.
    /// Base implementation is a no-op.
    /// </summary>
    protected virtual void OnDetailForceProcessed(float coreOutput, ref float detailForce)
    {
    }

    /// <summary>
    /// Called after output gain but before GripGuard. Override to apply game-specific
    /// centering force shaping. Base implementation returns coreOutput unchanged.
    /// R3E override applies V-shape quadratic center suppression.
    /// </summary>
    protected virtual float ApplyCenteringOverride(float coreOutput, FfbRawData raw)
    {
        return coreOutput;
    }

    /// <summary>
    /// Soft-lock end-stop wall: progressive resistance toward center once the
    /// normalized steer position passes 92% of full lock.
    /// Convention: codebase-wide SteerAngle is normalized -1..1 (see
    /// FfbChannelMixer center blend, R3e ApplyCenteringOverride), so lock is
    /// always |SteerAngle| = 1 regardless of per-car SteerDegrees.
    /// Direction follows GripGuard.CenteringForceMatchesSteerSign (R3E sets it;
    /// EVO/LMU/AC/ACC use the default), so no per-game direction override is
    /// needed — subclasses only need to CALL this in their final mix.
    /// Fail-safes: disabled/zero-gain = passthrough; |steer| outside
    /// [0.92, 1.5] = passthrough (values above 1.5 mean unexpected units —
    /// never synthesize a wall from them); output hard-clamped to ±1.
    /// </summary>
    protected virtual float ApplySoftLock(float force, FfbRawData raw)
    {
        if (!SoftLockEnabled || SoftLockStrength <= 0f)
            return force;
        float abs = Math.Abs(raw.SteerAngle);
        if (abs < 0.92f || abs > 1.5f)
            return force;
        float steerSign = Math.Sign(raw.SteerAngle);
        if (steerSign == 0f)
            return force;
        float t = Math.Min((abs - 0.92f) / 0.08f, 1.2f);
        float wall = t * t * SoftLockStrength * 0.6f;
        float towardCenter = GripGuard.CenteringForceMatchesSteerSign ? steerSign : -steerSign;
        return Math.Clamp(force + towardCenter * wall, -1f, 1f);
    }

    /// <summary>
    /// Gyro yaw stabilizer (detail path): torque opposing yaw rate, EMA-smoothed
    /// and clamped to ±0.15 (same envelope as FfbDynamicEffects yaw).
    /// Source is raw.LocalAngularVel[1], the same yaw signal the dynamic yaw
    /// term uses — no sign-convention branch (consistent with YawRateGain
    /// precedent; use a negative gain if the direction feels wrong for a game).
    /// Gated on GyroEnabled + gain. NaN/Inf yaw is ignored (fail-safe).
    /// </summary>
    protected virtual float ApplyGyroStabilizer(float detailForce, FfbRawData raw)
    {
        if (!GyroEnabled || Math.Abs(GyroGain) <= 0.0001f)
            return detailForce;
        float yaw = raw.LocalAngularVel.Length > 1 ? raw.LocalAngularVel[1] : 0f;
        if (float.IsNaN(yaw) || float.IsInfinity(yaw))
            return detailForce;
        float target = Math.Clamp(-yaw * GyroGain * 0.25f, -0.15f, 0.15f);
        _gyroSmooth = _gyroSmooth * 0.8f + target * 0.2f;
        return detailForce + _gyroSmooth;
    }

    /// <summary>
    /// TC-cut wheel feel (detail path): gentle toward-center nudge while the
    /// game reports a LIVE traction-control cut, EMA-smoothed (fast attack,
    /// natural release) and capped at ±0.12.
    /// Trigger uses live-activity flags only — raw.TcActiveGfx (EVO graphics
    /// TcActive; R3E injected IsTcActive = AidSettings.Tc == 5) or
    /// raw.TcinAction (EVO/AC physics, ACC slip-synthesized). Static
    /// setup values are never consulted. LMU exposes neither flag yet, so the
    /// effect stays inert there until the reader provides TC state.
    /// Direction follows GripGuard.CenteringForceMatchesSteerSign (R3E-aware).
    /// Gated on TcWheelEnabled + gain.
    /// </summary>
    protected virtual float ApplyTcWheelEffect(float detailForce, FfbRawData raw)
    {
        if (!TcWheelEnabled || Math.Abs(TcWheelGain) <= 0.0001f)
            return detailForce;
        bool cutting = raw.TcActiveGfx || raw.TcinAction != 0;
        float steerSign = Math.Abs(raw.SteerAngle) > 0.002f ? Math.Sign(raw.SteerAngle) : 0f;
        float centerDir = GripGuard.CenteringForceMatchesSteerSign ? steerSign : -steerSign;
        float target = cutting && steerSign != 0f ? centerDir * Math.Abs(TcWheelGain) * 0.12f : 0f;
        _tcWheelSmooth = _tcWheelSmooth * 0.7f + target * 0.3f;
        return detailForce + _tcWheelSmooth;
    }

    /// <summary>
    /// Rear-slip warning override (detail path). Base is no-op.
    /// LMU overrides with Fy-derivative + YawAccel + SlipRatio gate because
    /// rear Mz is always zero there (column-torque synth) and SlipAngle is a
    /// single bicycle value replicated x4. Zero-impact for EVO/R3E/AC/ACC.
    /// </summary>
    protected virtual float ApplyRearSlipOverride(float detailForce, FfbRawData raw, float deltaTime)
    {
        return detailForce;
    }

    /// <summary>
    /// Yaw-onset impulse (detail path). Base is no-op.
    /// LMU overrides with a thresholded YawAccel kick + exp decay + speed gate.
    /// Threshold/gain default 0 = inert. Zero-impact for EVO/R3E/AC/ACC.
    /// </summary>
    protected virtual float ApplyYawOnset(float detailForce, FfbRawData raw, float deltaTime)
    {
        return detailForce;
    }

    public float ApplyGearShiftFilter(float currentForce, float deltaTime, int currentGear)
    {
        return ApplyGearShiftFilter(currentForce, deltaTime, currentGear, 0, float.MaxValue);
    }

    /// <summary>
    /// RPM-gated overload. Consumes the per-game GearSpikeThreshold properties
    /// (R3E/LMU/AC/ACC), which were previously stored but never read.
    /// When a gear change coincides with RPM above rpmThreshold, the mute
    /// window is extended by one extra mute+fade cycle (limiter/shift shock).
    /// Default threshold float.MaxValue = legacy behavior (no RPM gating).
    /// Callers: base passes disabled; game pipelines pass their own threshold.
    /// </summary>
    public float ApplyGearShiftFilter(float currentForce, float deltaTime, int currentGear, int rpm, float rpmThreshold)
    {
        if (currentGear != _lastGear)
        {
            _shiftTimeTracker = _shiftMuteDuration + _fadeInDuration;
            if (rpmThreshold < float.MaxValue && rpm > rpmThreshold)
                _shiftTimeTracker += _shiftMuteDuration + _fadeInDuration;
            _lastGear = currentGear;
        }

        if (!GearShiftFilterEnabled || _shiftTimeTracker <= 0.0f)
        {
            _gearShiftPrevForce = currentForce;
            GearShiftMuteGain = 1.0f;
            return currentForce;
        }

        _shiftTimeTracker -= deltaTime;

        // Adaptive EMA: only smooth large deltas (>2%). Small changes pass through.
        float forceDelta = Math.Abs(currentForce - _gearShiftPrevForce);
        float alpha = forceDelta > 0.02f ? 0.05f : 1.0f;
        float smoothed = _gearShiftPrevForce + alpha * (currentForce - _gearShiftPrevForce);

        _gearShiftPrevForce = smoothed;
        GearShiftMuteGain = 1.0f;
        return smoothed;
    }
}
