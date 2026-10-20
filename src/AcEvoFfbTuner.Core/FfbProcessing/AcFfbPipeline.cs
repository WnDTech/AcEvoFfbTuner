using AcEvoFfbTuner.Core.FfbProcessing.Models;

namespace AcEvoFfbTuner.Core.FfbProcessing;

/// <summary>
/// AC1 core force synthesizer — builds FFB from telemetry, not from the
/// game's own FinalFF. This is what makes the app useful for AC1: the game's
/// FFB output goes to vJoy (dropped), and we send our own force to the real
/// wheel, fully under DSP control.
///
/// Physics model (single-stage, easy to tune):
///   coreForce = lateralG × speedScale × loadScale × steerSign × gain
///
/// - lateralG from AccG[0]: the main cornering force indicator
/// - speedScale: force ramps from 0 at standstill to full at reference speed
/// - loadScale: average front wheel load relative to static (loaded = more grip
///   = more force; unloaded/braking-transfer = less)
/// - steerSign: direction — force opposes steer displacement
/// - understeer reduction: front slip above threshold scales force down
///
/// All other stages (damping, brake boost, grip guard, crash, tyre condition,
/// vibration mixer, detail path) run on top unchanged. ISOLATION: all of this
/// lives in AcFfbPipeline — zero impact on EVO/R3E/LMU.
/// </summary>
public sealed class AcFfbPipeline : FfbPipeline
{
    public float PhysicsDt { get; set; } = 1f / 333f;

    private bool _gearChangeMuteEnabled = true;
    public bool GearChangeMuteEnabled
    {
        get => _gearChangeMuteEnabled;
        set { _gearChangeMuteEnabled = value; base.GearShiftFilterEnabled = value; }
    }
    public override float GearSpikeThreshold { get; set; } = 3000f;
    public float BrakeBoostGain { get; set; } = 0.4f;
    public float BrakeBoostThreshold { get; set; } = 0.1f;
    public override float CoreForceMultiplier { get; set; } = 1.0f;

    public float GripScaleGain { get; set; } = 0.6f;
    public float LastFrontGrip { get; private set; } = 1f;

    public float CoreSmoothing { get; set; } = 0.0f;
    public float DetailSmoothing { get; set; } = 0.0f;

    // ── Synthesis parameters (profile-tunable) ───────────────────────────
    /// <summary>Master gain for the synthesized core. Scales the entire output.
    /// Tune this first — everything else is relative to it.</summary>
    public float SynthGain { get; set; } = 1.0f;

    /// <summary>Reference speed (km/h) where force reaches full scale.
    /// Below this, force ramps linearly from 0. Typical GT car: 60-100.</summary>
    public float SynthSpeedRefKmh { get; set; } = 80f;

    /// <summary>Speed below which force is zeroed (km/h). Prevents jitter
    /// from low-speed G noise. Typical: 2-5.</summary>
    public float SynthMinSpeedKmh { get; set; } = 3f;

    /// <summary>Front slip ratio above which understeer reduction kicks in.
    /// 0.1-0.3 is typical for street tyres, 0.3-0.6 for race slicks.</summary>
    public float SynthSlipThreshold { get; set; } = 0.2f;

    /// <summary>How aggressively understeer reduces force. 0 = off, 1 = full
    /// drop to SynthMinGrip at high slip. Typical: 0.5-0.8.</summary>
    public float SynthSlipReduction { get; set; } = 0.6f;

    /// <summary>Minimum grip factor during understeer. Prevents force from
    /// going to absolute zero (driver loses all feel). 0.1-0.3 typical.</summary>
    public float SynthMinGrip { get; set; } = 0.15f;

    /// <summary>How much braking boosts front force (load transfer).
    /// 0 = off, 1 = strong. Typical: 0.2-0.5.</summary>
    public float SynthBrakeBoost { get; set; } = 0.3f;

    /// <summary>How much throttle reduces front force (rearward transfer).
    /// 0 = off, 0.3 = moderate. Typical: 0.1-0.2.</summary>
    public float SynthThrottleCut { get; set; } = 0.1f;

    /// <summary>Scaling factor for lateral G. AC1 AccG[0] is in m/s².
    /// A GT car at 1.5G in a fast corner produces ~14.7 m/s².
    /// This normalises to roughly -1..+1 core range. Tune per car feel.</summary>
    public float SynthLatScale { get; set; } = 0.07f;

    /// <summary>Additional weight-transfer scaling from brake/throttle.
    /// Amplifies the load-based factor. 1 = from loads only, 2 = double
    /// the effect. Typical: 1.0-1.5.</summary>
    public float SynthLoadTransferGain { get; set; } = 1.0f;

    // ── Internal state ──────────────────────────────────────────────────
    private float _prevSmoothedCore;
    private float _prevSmoothedDetail;
    private float _prevDetailOutput;
    private float _dcBlockSmooth;
    private float _peakHold;
    private int _clipCount;
    private int _clipTotal;
    private float _clipRate;

    public override bool GearShiftFilterEnabled
    {
        get => _gearChangeMuteEnabled;
        set { _gearChangeMuteEnabled = value; }
    }

    public AcFfbPipeline()
    {
        Equalizer.SetSampleRate(333f);
    }

    // ═══════════════════════════════════════════════════════════════════
    // CORE SYNTHESIS
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Builds the core steering force from AC1 telemetry.
    /// All other stages (damping, brake boost, etc.) run on top.
    /// </summary>
    private float SynthesizeCoreForce(FfbRawData raw)
    {
        float speed = raw.SpeedKmh;

        // Speed ramp: linear 0→1 between SynthMinSpeedKmh and SynthSpeedRefKmh.
        if (speed < SynthMinSpeedKmh) return 0f;
        float speedScale = Math.Clamp((speed - SynthMinSpeedKmh) / MathF.Max(SynthSpeedRefKmh - SynthMinSpeedKmh, 1f), 0f, 1f);

        // Lateral G → force magnitude. AccG[0] is lateral accel in m/s²;
        // positive = car accelerates right. The core force must oppose this
        // (push the wheel left when turning right = centering).
        float latG = raw.AccG is { Length: >= 3 } ? raw.AccG[0] : 0f;
        float force = latG * SynthLatScale;

        // Direction: force opposes steer displacement.
        // When steer is positive (right turn), latG is negative (centrifugal
        // pushes left), and we want a positive (rightward) centering force.
        // The latG already encodes direction, so steerSign just ensures the
        // force always opposes the steer input.
        float steerSign = raw.SteerAngle > 0.001f ? 1f : raw.SteerAngle < -0.001f ? -1f : 0f;
        float forceSign = MathF.Sign(force);
        if (forceSign != 0 && steerSign != 0 && forceSign == steerSign)
            force = -force; // Flip if force aligns with steer (wrong direction)

        // Load factor: ratio of average front load to a reference static load.
        // Higher load = more grip = stronger force. Lower load = lightening.
        float loadFactor = 1f;
        if (raw.WheelLoad is { Length: >= 4 })
        {
            float frontLoad = (MathF.Max(raw.WheelLoad[0], 0f) + MathF.Max(raw.WheelLoad[1], 0f)) * 0.5f;
            float rearLoad = (MathF.Max(raw.WheelLoad[2], 0f) + MathF.Max(raw.WheelLoad[3], 0f)) * 0.5f;
            float avgLoad = (frontLoad + rearLoad) * 0.5f;
            if (avgLoad > 100f)
                loadFactor = Math.Clamp(frontLoad / avgLoad, 0.4f, 1.6f);
            // Brake/accel weight transfer modulation
            if (SynthBrakeBoost > 0.001f && raw.BrakeInput > 0.05f)
                loadFactor *= 1f + SynthBrakeBoost * raw.BrakeInput;
            if (SynthThrottleCut > 0.001f && raw.GasInput > 0.1f)
                loadFactor *= 1f - SynthThrottleCut * raw.GasInput;
        }

        // Understeer reduction: front slip above threshold reduces force.
        float gripFactor = 1f;
        if (SynthSlipReduction > 0.001f && raw.SlipRatio is { Length: >= 4 } && speed > 5f)
        {
            float frontSlip = MathF.Max(MathF.Abs(raw.SlipRatio[0]), MathF.Abs(raw.SlipRatio[1]));
            if (frontSlip > SynthSlipThreshold)
            {
                float excess = (frontSlip - SynthSlipThreshold) * SynthSlipReduction;
                gripFactor = MathF.Max(1f - excess, SynthMinGrip);
            }
        }

        return force * speedScale * loadFactor * gripFactor * SynthGain;
    }

    // ═══════════════════════════════════════════════════════════════════
    // MAIN PROCESS
    // ═══════════════════════════════════════════════════════════════════

    public override FfbProcessedData Process(FfbRawData raw)
    {
        // Core force from telemetry synthesis (NOT from FinalFF).
        float steeringForce = SynthesizeCoreForce(raw);

        float magnitude = MathF.Abs(steeringForce);
        float coreNorm = steeringForce * MasterGain;

        WetWeather.Update(raw);

        float coreDamped = Damping.Apply(coreNorm, raw.SpeedKmh, raw.SteerAngle, PhysicsDt);

        float coreOutput = coreDamped * OutputGain;
        float stageGainForce = coreOutput;
        coreOutput *= CoreForceMultiplier;

        // Brake weight-transfer boost (multiplicative, profile-tunable).
        if (raw.BrakeInput > BrakeBoostThreshold)
        {
            float bi = MathF.Min((raw.BrakeInput - BrakeBoostThreshold) / (1f - BrakeBoostThreshold), 1f);
            coreOutput *= 1f + BrakeBoostGain * bi;
        }

        // Front-grip lightening: the wheel goes light as the front tyres slide.
        if (GripScaleGain > 0.001f && raw.SpeedKmh > 5f
            && raw.TyreGrip is { Length: >= 4 } && raw.WheelLoad is { Length: >= 4 })
        {
            float fSum = 0f, fLoad = 0f;
            for (int wi = 0; wi < 2; wi++)
            {
                float load = MathF.Max(raw.WheelLoad[wi], 0f);
                if (load > 50f) { fSum += Math.Clamp(raw.TyreGrip[wi], 0f, 1f) * load; fLoad += load; }
            }
            LastFrontGrip = fLoad > 1f ? fSum / fLoad : 1f;
            coreOutput *= Math.Clamp(1f - (1f - LastFrontGrip) * GripScaleGain * 0.8f, 0.2f, 1f);
        }
        else { LastFrontGrip = 1f; }

        coreOutput = ApplyCenteringOverride(coreOutput, raw);
        coreOutput = GripGuard.Apply(coreOutput, raw);
        coreOutput = CrashDetector.Apply(coreOutput, raw);
        coreOutput = TyreCondition.Apply(coreOutput, raw);
        coreOutput = WetWeather.ApplyHydroplaning(coreOutput, raw);

        bool isReversing = raw.Gear == ReverseGearValue && raw.SpeedKmh > 0.3f;
        if (isReversing) coreOutput = -coreOutput * ReverseAttenuation;

        if (raw.SpeedKmh < 0.5f)
        {
            coreOutput = 0f;
            _prevDetailOutput = 0f;
            _prevSmoothedCore = 0f;
            _prevSmoothedDetail = 0f;
            _dcBlockSmooth = 0f;
        }
        else if (raw.SpeedKmh < 5.0f)
        {
            coreOutput *= (raw.SpeedKmh - 0.5f) / 4.5f;
        }

        float coreAlpha = 1f - Math.Clamp(CoreSmoothing, 0f, 0.95f);
        if (coreAlpha < 0.999f)
            coreOutput = _prevSmoothedCore + coreAlpha * (coreOutput - _prevSmoothedCore);
        _prevSmoothedCore = coreOutput;

        // ═══════════════════════════════════════════════════════════════
        // DETAIL PATH — additive texture only
        // ═══════════════════════════════════════════════════════════════
        float deltaTime = PhysicsDt;
        float detailForce = 0f;

        float slipDelta = SlipEnhancer.Apply(coreOutput, raw) - coreOutput;
        if (MathF.Abs(slipDelta) > 0.0001f) detailForce += slipDelta;

        float dynAdd = DynamicEffects.Apply(0f, raw);
        detailForce += dynAdd;

        float flexAdd = TyreFlex.Apply(0f, raw);
        detailForce += flexAdd;
        float stageDynFlex = dynAdd + flexAdd;

        VibrationMixer.WetCurbScale = WetWeather.CurbScale;
        float vibration = VibrationMixer.Mix(raw);
        detailForce += vibration;

        float stageSubMods = 0f;
        float absMod = VibrationMixer.AbsForceModulation;
        if (MathF.Abs(absMod) > 0.001f)
        {
            float sign = coreOutput >= 0f ? 1f : -1f;
            if (MathF.Abs(coreOutput) < 0.01f)
                sign = MathF.Abs(raw.SteerAngle) > SteerDirDeadzone ? -MathF.Sign(raw.SteerAngle) : 1f;
            detailForce += absMod * sign;
            stageSubMods += absMod * sign;
        }

        float roadMod = VibrationMixer.RoadForceModulation;
        if (MathF.Abs(roadMod) > 0.001f) { detailForce += roadMod * WetWeather.RoadVibScale; stageSubMods += roadMod * WetWeather.RoadVibScale; }

        float scrubMod = VibrationMixer.ScrubModulation;
        if (MathF.Abs(scrubMod) > 0.001f) { detailForce += scrubMod * WetWeather.ScrubScale; stageSubMods += scrubMod * WetWeather.ScrubScale; }

        float rearMod = VibrationMixer.RearSlipModulation;
        if (MathF.Abs(rearMod) > 0.001f) { detailForce += rearMod; stageSubMods += rearMod; }

        float offtrackMod = VibrationMixer.OfftrackModulation;
        if (MathF.Abs(offtrackMod) > 0.001f) { detailForce += offtrackMod; stageSubMods += offtrackMod; }

        float lfe = LfeGenerator.Generate(raw);
        if (MathF.Abs(lfe) > 0.001f) { detailForce += lfe; stageSubMods += lfe; }

        detailForce = Equalizer.Process(detailForce);

        if (raw.SpeedKmh >= 0.5f)
        {
            float slewDelta = detailForce - _prevDetailOutput;
            if (MathF.Abs(slewDelta) > MaxSlewRate)
                detailForce = _prevDetailOutput + MathF.Sign(slewDelta) * MaxSlewRate;
        }

        if (isReversing) detailForce *= ReverseDetailPass;

        _prevDetailOutput = raw.SpeedKmh < 0.5f ? 0f : detailForce;

        OnDetailForceProcessed(coreOutput, ref detailForce);

        detailForce = ApplyGyroStabilizer(detailForce, raw);
        detailForce = ApplyTcWheelEffect(detailForce, raw);
        detailForce = ApplyRearSlipOverride(detailForce, raw, deltaTime);
        detailForce = ApplyYawOnset(detailForce, raw, deltaTime);

        float detailAlpha = 1f - Math.Clamp(DetailSmoothing, 0f, 0.95f);
        if (detailAlpha < 0.999f)
            detailForce = _prevSmoothedDetail + detailAlpha * (detailForce - _prevSmoothedDetail);
        _prevSmoothedDetail = detailForce;

        // ═══════════════════════════════════════════════════════════════
        // FINAL MIX
        // ═══════════════════════════════════════════════════════════════
        float filteredCore = ApplyGearShiftFilter(coreOutput, deltaTime, raw.Gear, raw.EngineRpm, GearSpikeThreshold);
        float finalOutput = filteredCore + detailForce;
        float stagePreClip = finalOutput;

        finalOutput = OutputClipper.Process(finalOutput, out bool isClipping);
        finalOutput = ApplySoftLock(finalOutput, raw);

        float speedNoiseScale = raw.SpeedKmh < 10.0f
            ? 1.0f + (1.0f - raw.SpeedKmh / 10.0f) * LowSpeedNoiseBoost : 1.0f;
        float effectiveNoiseFloor = NoiseFloor * speedNoiseScale * WetWeather.NoiseFloorScale;
        if (MathF.Abs(finalOutput) < effectiveNoiseFloor) finalOutput = 0f;

        _peakHold = MathF.Max(_peakHold * 0.999f, MathF.Abs(finalOutput));
        _clipTotal++;
        if (isClipping) _clipCount++;
        if (_clipTotal >= 333) { _clipRate = (float)_clipCount / _clipTotal; _clipCount = 0; _clipTotal = 0; }

        return new FfbProcessedData
        {
            MainForce = finalOutput,
            VibrationForce = vibration * GearShiftMuteGain,
            RawFinalFf = steeringForce,
            PostCompressionForce = coreNorm,
            PostLutForce = coreNorm,
            PostDampingForce = coreDamped,
            PostOutputGainForce = coreOutput,
            PostDynamicForce = detailForce,
            CoreForce = coreOutput,
            DetailForce = detailForce,
            AutoGainApplied = 1f,
            IsClipping = isClipping,
            PeakHold = _peakHold,
            ClipRate = _clipRate,
            SpeedKmh = raw.SpeedKmh,
            SteerAngle = raw.SteerAngle,
            PacketId = raw.PacketId,
            WetnessFactor = WetWeather.WetnessFactor,
            TyreCategory = TyreCompoundClassifier.Classify(raw.TyreCompoundFront),
            TyreCompoundFrontName = raw.TyreCompoundFront,
            TyreCompoundRearName = raw.TyreCompoundRear,
            GearShiftMuteGain = GearShiftMuteGain,
            StageGainForce = stageGainForce,
            StageSlipDelta = slipDelta,
            StageDynFlex = stageDynFlex,
            StageSubMods = stageSubMods,
            StagePreClipForce = stagePreClip
        };
    }

    protected override void OnDetailForceProcessed(float coreOutput, ref float detailForce)
    {
        float dcBlocked = detailForce - _dcBlockSmooth;
        _dcBlockSmooth += dcBlocked * 0.02f;
        detailForce = dcBlocked;

        if (coreOutput > 0f && detailForce < 0f)
            detailForce = MathF.Max(detailForce, -coreOutput * 0.8f);
        else if (coreOutput < 0f && detailForce > 0f)
            detailForce = MathF.Min(detailForce, -coreOutput * 0.8f);
    }

    protected override float ApplyCenteringOverride(float coreOutput, FfbRawData raw)
    {
        if (CenterSharpnessDegrees > 0.001f)
        {
            float lockHalf = MathF.Abs(raw.SteerDegrees) * 0.5f;
            if (lockHalf < 1f) lockHalf = 450f;
            float absSteerDeg = MathF.Abs(raw.SteerAngle) * lockHalf;
            float t = Math.Clamp(absSteerDeg / CenterSharpnessDegrees, 0f, 1f);
            return coreOutput * t * t * (3f - 2f * t);
        }
        return coreOutput;
    }

    public new void Reset()
    {
        base.Reset();
        _prevSmoothedCore = 0f;
        _prevSmoothedDetail = 0f;
        _prevDetailOutput = 0f;
        _dcBlockSmooth = 0f;
        _peakHold = 0f;
        _clipRate = 0f;
        _clipCount = 0;
        _clipTotal = 0;
        LastFrontGrip = 1f;
    }
}
