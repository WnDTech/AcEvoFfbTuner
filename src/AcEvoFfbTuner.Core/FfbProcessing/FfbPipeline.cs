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
    public Hf8SignalMapper Hf8SignalMapper { get; } = new();
    public FfbGripGuard GripGuard { get; } = new();
    public FfbCrashDetector CrashDetector { get; } = new();
    public FfbTyreCondition TyreCondition { get; } = new();
    public FfbWetWeather WetWeather { get; } = new();

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
    public float CenterSuppressionDegrees { get; set; } = 1.5f;
    public float HysteresisThreshold { get; set; } = 0.015f;
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

    private float _prevDetailOutput;
    private float _shiftMuteDuration = 1.500f;
    private float _fadeInDuration = 0.500f;
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
        detailForce += (postSlip - corePostLut);

        // Dynamic effects contribution (independent of force magnitude)
        float dynamicContrib = DynamicEffects.Apply(0f, raw);
        detailForce += dynamicContrib;

        // Tyre flex contribution (independent of force magnitude)
        float flexContrib = TyreFlex.Apply(0f, raw);
        detailForce += flexContrib;

        // Vibration signals (suspension-based road/curb, scrub, rear slip)
        // Wet weather: suppress curb feel (water layer absorbs curb impacts)
        VibrationMixer.WetCurbScale = WetWeather.CurbScale;
        float vibration = VibrationMixer.Mix(raw);

        // ABS force modulation (directional — follows core force sign)
        float absMod = VibrationMixer.AbsForceModulation;
        if (Math.Abs(absMod) > 0.001f)
        {
            float sign = coreOutput >= 0f ? 1f : -1f;
            if (Math.Abs(coreOutput) < 0.01f)
                sign = Math.Abs(raw.SteerAngle) > SteerDirDeadzone ? -Math.Sign(raw.SteerAngle) : 1f;
            detailForce += absMod * sign;
        }

        // Wet weather: suppress road vibration (water film damps surface texture)
        float roadMod = VibrationMixer.RoadForceModulation;
        if (Math.Abs(roadMod) > 0.001f)
            detailForce += roadMod * WetWeather.RoadVibScale;

        // Wet weather: suppress scrub texture (water lubricates contact patch)
        float scrubMod = VibrationMixer.ScrubModulation;
        if (Math.Abs(scrubMod) > 0.001f)
            detailForce += scrubMod * WetWeather.ScrubScale;

        // Rear slip warning (12-25Hz rumble when rear loses grip)
        float rearMod = VibrationMixer.RearSlipModulation;
        if (Math.Abs(rearMod) > 0.001f)
            detailForce += rearMod;

        // Offtrack surface rumble (when all 4 wheels leave the racing surface)
        float offtrackMod = VibrationMixer.OfftrackModulation;
        if (Math.Abs(offtrackMod) > 0.001f)
            detailForce += offtrackMod;

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

        if (isReversing)
            detailForce *= ReverseDetailPass;

        _prevDetailOutput = raw.SpeedKmh < 0.5f ? 0f : detailForce;

        // Hook for subclasses to modify detailForce before final mix
        OnDetailForceProcessed(coreOutput, ref detailForce);

        // ═══════════════════════════════════════════════════════════════════════
        // FINAL MIX — Core (zero-latency) + Detail (filtered)
        // ═══════════════════════════════════════════════════════════════════════

        // Gear shift filter: EMA on core force only — detail effects pass through
        float deltaTime = 1f / 60f;
        float filteredCore = ApplyGearShiftFilter(coreOutput, deltaTime, raw.Gear);
        float finalOutput = filteredCore + detailForce;

        // Output clipper (soft clip at threshold)
        finalOutput = OutputClipper.Process(finalOutput, out bool isClipping);

        // Noise floor gate (amplified at low speed for physics jitter suppression)
        // Wet weather: raise noise floor (water film masks tiny surface details)
        float speedNoiseScale = raw.SpeedKmh < 10.0f
            ? 1.0f + (1.0f - raw.SpeedKmh / 10.0f) * 2.0f
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
            GearShiftMuteGain = GearShiftMuteGain
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
        Hf8SignalMapper.Reset();
        GripGuard.Reset();
        CrashDetector.Reset();
        TyreCondition.Reset();
        WetWeather.Reset();



        _prevDetailOutput = 0f;
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

    public float ApplyGearShiftFilter(float currentForce, float deltaTime, int currentGear)
    {
        if (currentGear != _lastGear)
        {
            _shiftTimeTracker = _shiftMuteDuration + _fadeInDuration;
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
        float alpha = forceDelta > 0.02f ? 0.005f : 1.0f;
        float smoothed = _gearShiftPrevForce + alpha * (currentForce - _gearShiftPrevForce);

        _gearShiftPrevForce = smoothed;
        GearShiftMuteGain = 1.0f;
        return smoothed;
    }
}
