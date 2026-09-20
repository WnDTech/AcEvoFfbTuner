using AcEvoFfbTuner.Core.DirectInput;
using AcEvoFfbTuner.Core.FfbProcessing.Models;

namespace AcEvoFfbTuner.Core.FfbProcessing;

public sealed class LmuFfbPipeline : FfbPipeline
{
    private float _dcBlockSmooth;
    private readonly LmuHf8SignalMapper _lmuHf8Mapper = new();
    private float _prevDetailOutput;

    public override Hf8SignalMapper Hf8SignalMapper => _lmuHf8Mapper;

    private bool _gearChangeMuteEnabled = true;
    public bool GearChangeMuteEnabled
    {
        get => _gearChangeMuteEnabled;
        set { _gearChangeMuteEnabled = value; base.GearShiftFilterEnabled = value; }
    }
    public override float GearSpikeThreshold { get; set; } = 3000f;
    public float BrakeBoostGain { get; set; } = 0.4f;
    public float BrakeBoostThreshold { get; set; } = 0.1f;
    public override float CoreForceMultiplier { get; set; } = 3.0f;
    public float GripScaleGain { get; set; } = 0.6f;
    public float TyreTempGain { get; set; } = 0.0f;

    // ── Plan B1: front/rear grip split ──
    // Front grip lightens the core (understeer). Rear grip no longer lightens
    // the core; it feeds the rear-slip detail warning (C3) via RearGripDetailGain.
    // Default 0 = rear detail off (zero-impact; current feel preserved).
    public float RearGripDetailGain { get; set; } = 0f;
    public float LastFrontGrip { get; private set; } = 1f;
    public float LastRearGrip { get; private set; } = 1f;

    // ── Plan B2: column-force auto-norm ──
    // Rolling-peak tracker mirroring FfbChannelMixer auto-norm. When the peak
    // exceeds AutoMinPeak, the effective scale grows so Hypercar fast corners
    // stop hard-clipping. Default on with conservative floor (standstill noise
    // never amplifies).
    public float AutoMinPeak { get; set; } = 1.0f;
    public float AutoTarget { get; set; } = 1.0f;
    private float _lmuPeak;
    private const float LmuPeakDecay = 0.997f;

    // ── Plan B3: deterministic physics rate ──
    public float PhysicsDt { get; set; } = 1f / 250f;

    // ── Plan B4: adaptive DC blocker cutoff (Hz). 1-exp(-dt*cutoff) replaces
    // the fixed 0.02 coefficient. At 250Hz/5Hz ≈ 0.0198 — same as legacy.
    public float DcBlockCutoffHz { get; set; } = 5f;

    // ── Plan B7: LMU keeps its legacy 0.5x low-speed noise boost ──
    public override float LowSpeedNoiseBoost { get; set; } = 0.5f;

    // ── Plan C3: rear-slip warning from live rear signals ──
    // Fy-derivative + YawAccel + SlipRatio gate. Rear Mz is always zero for LMU
    // and SlipAngle is a single bicycle value x4, so neither is used here.
    public float RearFyScale { get; set; } = 0f;
    public float RearSlipAngleThreshold { get; set; } = 0.07f;
    public float RearYawThreshold { get; set; } = 2.0f;
    public float RearMaxOutput { get; set; } = 0.20f;
    private float _prevRearFy;
    private float _prevYawRateC3;
    private bool _yawInitC3;
    private float _rearIntensity;

    // ── Plan C4: yaw-onset impulse ──
    public float YawOnsetGain { get; set; } = 0f;
    public float YawOnsetThreshold { get; set; } = 0f;
    public float YawOnsetDecay { get; set; } = 8f;
    public float YawOnsetMax { get; set; } = 0.12f;
    private float _yawOnsetState;

    // ── Plan C5: curb/road double-layer calibration ──
    // The reader already synthesizes kerb/road envelopes AND the mixer adds a
    // suspension-delta layer. SuspRoadScale scales the mixer susp-delta layer
    // at the consumption site (default 1 = current stacking preserved);
    // UseMixerSuspRoad=false gates it off entirely (reader envelopes only).
    // Kerb-channel rescaling needs EVO-tuned constants inside the mixer
    // (10x/150x/0.002) — deferred to snapshot-measured follow-up, not guessed.
    public float SuspRoadScale { get; set; } = 1f;
    public bool UseMixerSuspRoad { get; set; } = true;

    // ── Plan C6: load-proportioned brake transfer ──
    // 0 = legacy scalar boost only. >0 blends in front-load bias.
    public float BrakeLoadBias { get; set; } = 0f;

    // ── Plan C1: LMU dynamics synthesis gates ──
    // SynthYawEnabled=true populates raw.YawAccel (measurement only; effects
    // stay gain-gated). ABS/TC synthesis defaults OFF until snapshot
    // thresholds are confirmed (their consumers fire immediately when set).
    public bool SynthYawEnabled { get; set; } = true;
    public bool SynthAbsEnabled { get; set; } = false;
    public bool SynthTcEnabled { get; set; } = false;
    public float LockupSlipThreshold { get; set; } = -0.12f;
    public float SpinSlipThreshold { get; set; } = -0.12f;

    public LmuFfbPipeline()
    {
        // On-center damping authority (Plan B5 consumer): shared Damping
        // instance default is 0 (EVO legacy); LMU opts into 0.03.
        Damping.CenterDampFloor = 0.03f;
        Equalizer.SetSampleRate(250f);
    }

    /// <summary>Core smoothing factor (EMA alpha). 0 = no smoothing (raw). 0.1 = light. 0.5 = heavy. Zero-impact default.</summary>
    public float CoreSmoothing { get; set; } = 0.0f;

    /// <summary>Detail smoothing factor (EMA alpha). 0 = no smoothing. 0.2 = light. 0.8 = very smooth. Zero-impact default.</summary>
    public float DetailSmoothing { get; set; } = 0.0f;

    private float _prevSmoothedCore;
    private float _prevSmoothedDetail;
    private float _lastEqRate = 250f;
    private float _peakHold;
    private float _clipRate;
    private int _clipCount;
    private int _clipTotal;

    public override bool GearShiftFilterEnabled
    {
        get => _gearChangeMuteEnabled;
        set { _gearChangeMuteEnabled = value; }
    }

    public override FfbProcessedData Process(FfbRawData raw)
    {
        float steeringForce = raw.FinalFf;

        // ═══════════════════════════════════════════════════════════════════════
        // CORE PATH — Fully standalone. No base.Process() — no EVO pipeline
        // cross-contamination. Same isolation pattern as R3eFfbPipeline.
        // ═══════════════════════════════════════════════════════════════════════

        // Centering direction via steer angle (smooth tanh to avoid snap at center)
        float steerSign = MathF.Tanh(raw.SteerAngle * 50f);
        float magnitude = Math.Abs(steeringForce);

        // Column force normalization. ForceScale acts as sensitivity multiplier
        // with 1.0 = safe default (20 Nm torque → low-moderate normalized force).
        // Plan B2: rolling-peak auto-norm caps Hypercar spikes without touching
        // normal forces (peak below AutoMinPeak → manual scale unchanged).
        float manualScale = Math.Max(120f / Math.Max(ForceScale, 0.01f), 1f);
        _lmuPeak = Math.Max(_lmuPeak * LmuPeakDecay, magnitude);
        float effectiveScale = manualScale;
        if (_lmuPeak > AutoMinPeak)
            effectiveScale = Math.Max(effectiveScale, _lmuPeak / Math.Max(AutoTarget, 0.01f));
        float coreNorm = magnitude * steerSign * MasterGain / effectiveScale;

        // Damping with deterministic physics dt (Plan B3). Shared instance
        // carries CenterDampFloor=0.03 (LMU ctor) + straight-line props.
        float coreDamped = Damping.Apply(coreNorm, raw.SpeedKmh, raw.SteerAngle, PhysicsDt);

        // Output scaling
        float coreForce = coreDamped * OutputGain;
        coreForce *= CoreForceMultiplier;
        float stageGainForce = coreForce;

        // Brake boost: legacy scalar gain + opt-in front-load bias (Plan C6).
        // BrakeLoadBias=0 preserves the legacy scalar exactly.
        float brakeBoost = 1f;
        float loadBias = 1f;
        if (raw.BrakeInput > BrakeBoostThreshold)
        {
            float bi = Math.Min((raw.BrakeInput - BrakeBoostThreshold) / (1f - BrakeBoostThreshold), 1f);
            brakeBoost = 1f + BrakeBoostGain * bi;
            if (BrakeLoadBias > 0.001f)
            {
                float frontLoad = Math.Max(raw.WheelLoad[0], 0f) + Math.Max(raw.WheelLoad[1], 0f);
                float totalLoad = frontLoad + Math.Max(raw.WheelLoad[2], 0f) + Math.Max(raw.WheelLoad[3], 0f);
                if (totalLoad > 1f)
                {
                    float frontFrac = frontLoad / totalLoad;
                    loadBias = 1f + (frontFrac - 0.5f) * 2f * BrakeLoadBias * bi;
                }
            }
        }
        coreForce *= brakeBoost * loadBias;

        // Grip-scaled centering, split by axle (Plan B1). Front grip lightens
        // the core (understeer); rear grip is recorded for the detail warning
        // (C3) and never lightens the core.
        bool hasGrip = false;
        for (int gi = 0; gi < 4; gi++)
            if (raw.TyreGrip is { Length: >= 4 } && Math.Abs(raw.TyreGrip[gi]) > 0.01f) { hasGrip = true; break; }
        if (raw.SpeedKmh > 5f && hasGrip && raw.TyreGrip is { Length: >= 4 } && raw.WheelLoad is { Length: >= 4 })
        {
            float fSum = 0f, fLoad = 0f, rSum = 0f, rLoad = 0f;
            for (int wi = 0; wi < 2; wi++)
            {
                float load = Math.Max(raw.WheelLoad[wi], 0f);
                if (load > 50f) { fSum += Math.Clamp(raw.TyreGrip[wi], 0f, 1f) * load; fLoad += load; }
            }
            for (int wi = 2; wi < 4; wi++)
            {
                float load = Math.Max(raw.WheelLoad[wi], 0f);
                if (load > 50f) { rSum += Math.Clamp(raw.TyreGrip[wi], 0f, 1f) * load; rLoad += load; }
            }
            LastFrontGrip = fLoad > 1f ? fSum / fLoad : 1f;
            LastRearGrip = rLoad > 1f ? rSum / rLoad : 1f;
            if (GripScaleGain > 0.001f)
                coreForce *= Math.Clamp(1f - (1f - LastFrontGrip) * GripScaleGain * 0.8f, 0.2f, 1f);
        }

        // Game-specific centering shaping (smoothstep ramp over
        // CenterSharpnessDegrees — same hook R3E uses; 0 = disabled).
        coreForce = ApplyCenteringOverride(coreForce, raw);

        // Speed fade
        if (raw.SpeedKmh < 0.5f) { coreForce = 0f; _prevDetailOutput = 0f; _prevSmoothedCore = 0f; _prevSmoothedDetail = 0f; }
        else if (raw.SpeedKmh < 5.0f)
            coreForce *= (raw.SpeedKmh - 0.5f) / 4.5f;

        // Core EMA smoothing: slider 0 = none (passthrough, zero-impact default).
        float coreAlpha = 1f - Math.Clamp(CoreSmoothing, 0f, 0.95f);
        if (coreAlpha < 0.999f)
            coreForce = _prevSmoothedCore + coreAlpha * (coreForce - _prevSmoothedCore);
        _prevSmoothedCore = coreForce;

        // ═══════════════════════════════════════════════════════════════════════
        // DETAIL PATH
        // ═══════════════════════════════════════════════════════════════════════
        float deltaTime = PhysicsDt;
        float detailForce = 0f;

        // Plan C2: slip lightening from our own SlipEnhancer (front-only).
        // Gains default 0 in shared instance → no-op until tuned.
        float slipDelta = SlipEnhancer.Apply(coreForce, raw) - coreForce;
        if (Math.Abs(slipDelta) > 0.0001f) detailForce += slipDelta;

        float vibration = VibrationMixer.Mix(raw);
        // Plan C5: LMU mixer-layer scales (default 1 = current stacking).
        // The susp-delta layer inside the mixer cannot be per-game gated from
        // here without touching shared code, so RoadForceModulation (the
        // susp-delta product) is scaled at the consumption site instead.
        detailForce += vibration;

        // LMU has no dynamics/flex stages — the tyre-temp term is reported on
        // the DynFlex tap (badged TEMP ONLY) so the meter stays truthful.
        float stageDynFlex = 0f;
        if (TyreTempGain > 0.001f && raw.TyreTemp is { Length: >= 4 } && raw.SpeedKmh > 20f)
        {
            float avg = (raw.TyreTemp[0] + raw.TyreTemp[1] + raw.TyreTemp[2] + raw.TyreTemp[3]) * 0.25f;
            float cold = avg < 75f ? Math.Clamp((75f - avg) / 35f, 0f, 1f) : 0f;
            float hot  = avg > 105f ? Math.Clamp((avg - 105f) / 25f, 0f, 1f) : 0f;
            stageDynFlex = (cold + hot) * TyreTempGain * 0.005f * (Math.Abs(raw.SteerAngle) > 0.003f ? 1f : 0.2f);
            detailForce += stageDynFlex;
        }

        float stageSubMods = 0f;
        float absMod = VibrationMixer.AbsForceModulation;
        if (Math.Abs(absMod) > 0.001f)
        {
            float sign = coreForce >= 0f ? 1f : -1f;
            if (Math.Abs(coreForce) < 0.01f)
                sign = Math.Abs(raw.SteerAngle) > SteerDirDeadzone ? -Math.Sign(raw.SteerAngle) : 1f;
            detailForce += absMod * sign;
            stageSubMods += absMod * sign;
        }

        if (UseMixerSuspRoad && Math.Abs(VibrationMixer.RoadForceModulation) > 0.001f)
        {
            detailForce += VibrationMixer.RoadForceModulation * SuspRoadScale;
            stageSubMods += VibrationMixer.RoadForceModulation * SuspRoadScale;
        }
        if (Math.Abs(VibrationMixer.RearSlipModulation) > 0.001f)
        {
            detailForce += VibrationMixer.RearSlipModulation;
            stageSubMods += VibrationMixer.RearSlipModulation;
        }
        if (Math.Abs(VibrationMixer.OfftrackModulation) > 0.001f)
        {
            detailForce += VibrationMixer.OfftrackModulation;
            stageSubMods += VibrationMixer.OfftrackModulation;
        }

        float lfe = LfeGenerator.Generate(raw);
        if (Math.Abs(lfe) > 0.001f)
        {
            detailForce += lfe;
            stageSubMods += lfe;
        }

        detailForce = Equalizer.Process(detailForce);

        if (raw.SpeedKmh >= 0.5f)
        {
            float sd = detailForce - _prevDetailOutput;
            if (Math.Abs(sd) > MaxSlewRate)
                detailForce = _prevDetailOutput + Math.Sign(sd) * MaxSlewRate;
        }
        _prevDetailOutput = raw.SpeedKmh < 0.5f ? 0f : detailForce;

        // Keep EQ coefficients matched to the LMU physics rate (Plan B3).
        // SetSampleRate recalculates only when the rate actually changed.
        float eqRate = 1f / Math.Max(PhysicsDt, 1e-4f);
        if (Math.Abs(eqRate - _lastEqRate) > 1f)
        {
            Equalizer.SetSampleRate(eqRate);
            _lastEqRate = eqRate;
        }

        OnDetailForceProcessed(coreForce, ref detailForce);

        // Gyro stabilizer + TC wheel feel (no-ops unless enabled).
        detailForce = ApplyGyroStabilizer(detailForce, raw);
        detailForce = ApplyTcWheelEffect(detailForce, raw);
        // Rear-slip + yaw-onset hooks (Plans C3/C4; inert at default gains).
        detailForce = ApplyRearSlipOverride(detailForce, raw, deltaTime);
        detailForce = ApplyYawOnset(detailForce, raw, deltaTime);

        // Detail EMA smoothing: slider 0 = none (passthrough, zero-impact default).
        float detailAlpha = 1f - Math.Clamp(DetailSmoothing, 0f, 0.95f);
        if (detailAlpha < 0.999f)
            detailForce = _prevSmoothedDetail + detailAlpha * (detailForce - _prevSmoothedDetail);
        _prevSmoothedDetail = detailForce;

        // ═══════════════════════════════════════════════════════════════════════
        // FINAL MIX
        // ═══════════════════════════════════════════════════════════════════════
        float filteredCore = ApplyGearShiftFilter(coreForce, deltaTime, raw.Gear, raw.EngineRpm, GearSpikeThreshold);
        float finalOutput = filteredCore + detailForce;
        float stagePreClip = finalOutput;

        finalOutput = OutputClipper.Process(finalOutput, out bool isClipping);

        // Soft-lock end-stop (no-op unless SoftLockEnabled).
        finalOutput = ApplySoftLock(finalOutput, raw);

        // Plan B7: per-game low-speed noise boost (LMU legacy 0.5 preserved).
        float speedNoiseScale = raw.SpeedKmh < 10.0f
            ? 1.0f + (1.0f - raw.SpeedKmh / 10.0f) * LowSpeedNoiseBoost : 1.0f;
        if (Math.Abs(finalOutput) < NoiseFloor * speedNoiseScale) finalOutput = 0f;

        // Plan D1: clip headroom telemetry (additive fields; no auto-gain).
        _peakHold = Math.Max(_peakHold * 0.999f, Math.Abs(finalOutput));
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
            PostOutputGainForce = coreForce,
            PostDynamicForce = detailForce,
            CoreForce = coreForce,
            DetailForce = detailForce,
            AutoGainApplied = 1f,
            IsClipping = isClipping,
            PeakHold = _peakHold,
            ClipRate = _clipRate,
            SpeedKmh = raw.SpeedKmh,
            SteerAngle = raw.SteerAngle,
            PacketId = raw.PacketId,
            GearShiftMuteGain = GearShiftMuteGain,
            StageGainForce = stageGainForce,
            StageSlipDelta = slipDelta,
            StageDynFlex = stageDynFlex,
            StageSubMods = stageSubMods,
            StagePreClipForce = stagePreClip
        };
    }

    /// <summary>
    /// LMU-specific HF8 haptic pad mapping. Uses LmuHf8SignalMapper which
    /// computes slip feel from raw wheel slip ratio+angle (LMU's Mz is
    /// approximated as total column force, making VibrationMixer scrub/rear-slip
    /// modulations unreliable).
    /// </summary>
    public override float[] MapHf8Motors(FfbRawData raw, FfbProcessedData processed)
    {
        return _lmuHf8Mapper.Map(raw, processed, VibrationMixer, LfeGenerator);
    }

    protected override void OnDetailForceProcessed(float coreOutput, ref float detailForce)
    {
        // Plan B4: adaptive first-order DC blocker. Alpha = 1-exp(-dt*cutoff)
        // equals ~0.0198 at 250Hz/5Hz — identical to the legacy fixed 0.02.
        float alpha = 1f - MathF.Exp(-PhysicsDt * Math.Max(DcBlockCutoffHz, 0.1f));
        float dcBlocked = detailForce - _dcBlockSmooth;
        _dcBlockSmooth += dcBlocked * alpha;
        detailForce = dcBlocked;

        if (coreOutput > 0f && detailForce < 0f)
            detailForce = Math.Max(detailForce, -coreOutput * 0.8f);
        else if (coreOutput < 0f && detailForce > 0f)
            detailForce = Math.Min(detailForce, -coreOutput * 0.8f);
    }

    /// <summary>
    /// Plan C3: rear-slip warning from live rear signals only.
    /// Fy[2,3] derivative (real per-wheel lateral force) + YawAccel intensity,
    /// gated by rear SlipRatio. Rear Mz is always 0 for LMU and SlipAngle is a
    /// single bicycle value x4 — neither is used. Default RearFyScale=0 = off.
    /// </summary>
    protected override float ApplyRearSlipOverride(float detailForce, FfbRawData raw, float deltaTime)
    {
        if (RearFyScale < 0.001f || raw.SpeedKmh < 15f)
        {
            _rearIntensity = 0f;
            _yawInitC3 = false;
            if (raw.Fy is { Length: >= 4 }) _prevRearFy = (raw.Fy[2] + raw.Fy[3]) * 0.5f;
            return detailForce;
        }
        if (raw.Fy is not { Length: >= 4 } || raw.SlipRatio is not { Length: >= 4 })
            return detailForce;

        float rearSlip = (Math.Abs(raw.SlipRatio[2]) + Math.Abs(raw.SlipRatio[3])) * 0.5f;
        float slipGate = Math.Clamp((rearSlip - 0.02f) / 0.06f, 0f, 1f);

        float yawRate = raw.LocalAngularVel is { Length: > 1 } ? raw.LocalAngularVel[1] : 0f;
        float yawAccel = raw.YawAccel;
        if (yawAccel == 0f)
        {
            yawAccel = _yawInitC3 ? (yawRate - _prevYawRateC3) / Math.Max(deltaTime, 1e-4f) : 0f;
        }
        _prevYawRateC3 = yawRate;
        _yawInitC3 = true;
        float yawGate = Math.Clamp((Math.Abs(yawAccel) - RearYawThreshold * 0.3f) / Math.Max(RearYawThreshold, 0.1f), 0f, 1f);

        float combined = Math.Max(slipGate * slipGate, yawGate * 0.6f);
        // Rear-grip drop as a third intensity source (Plan B1 wiring):
        // LastRearGrip is load-weighted 2,3 from the core path. Gain 0 = off.
        if (RearGripDetailGain > 0.001f)
        {
            float gripDrop = Math.Clamp(1f - LastRearGrip, 0f, 1f);
            combined = Math.Max(combined, gripDrop * gripDrop * RearGripDetailGain);
        }
        _rearIntensity = _rearIntensity * 0.80f + combined * 0.20f;
        if (_rearIntensity < 0.001f)
        {
            _prevRearFy = (raw.Fy[2] + raw.Fy[3]) * 0.5f;
            return detailForce;
        }

        float rearFy = (raw.Fy[2] + raw.Fy[3]) * 0.5f;
        float fyDeriv = (rearFy - _prevRearFy) / Math.Max(deltaTime, 1e-4f);
        _prevRearFy = rearFy;
        if (float.IsNaN(fyDeriv) || float.IsInfinity(fyDeriv)) return detailForce;

        float osc = Math.Clamp(fyDeriv * 0.0005f, -1f, 1f);
        float amp = Math.Min(RearMaxOutput * RearFyScale * Math.Min(_rearIntensity, 1f), RearMaxOutput);
        return detailForce + Math.Clamp(osc * amp, -RearMaxOutput, RearMaxOutput);
    }

    /// <summary>
    /// Plan C4: yaw-onset impulse. Thresholded YawAccel kick with exponential
    /// decay + 15 km/h speed gate. Defaults (gain/threshold 0) = inert.
    /// </summary>
    protected override float ApplyYawOnset(float detailForce, FfbRawData raw, float deltaTime)
    {
        float decay = MathF.Exp(-Math.Max(YawOnsetDecay, 0.1f) * Math.Max(deltaTime, 1e-4f));
        _yawOnsetState *= decay;
        if (YawOnsetGain < 0.001f || YawOnsetThreshold <= 0f || raw.SpeedKmh < 15f)
            return detailForce;
        float yawAccel = raw.YawAccel;
        if (float.IsNaN(yawAccel) || float.IsInfinity(yawAccel)) return detailForce;
        if (Math.Abs(yawAccel) > YawOnsetThreshold)
        {
            float kick = Math.Clamp(yawAccel * YawOnsetGain * 0.05f, -YawOnsetMax, YawOnsetMax);
            _yawOnsetState = Math.Clamp(_yawOnsetState + kick, -YawOnsetMax, YawOnsetMax);
        }
        return detailForce + _yawOnsetState;
    }

    protected override float ApplyCenteringOverride(float coreOutput, FfbRawData raw)
    {
        if (CenterSharpnessDegrees > 0.001f)
        {
            float lockHalf = Math.Abs(raw.SteerDegrees) * 0.5f;
            if (lockHalf < 1f) lockHalf = 450f;
            float absSteerDeg = Math.Abs(raw.SteerAngle) * lockHalf;
            float t = Math.Clamp(absSteerDeg / CenterSharpnessDegrees, 0f, 1f);
            return coreOutput * t * t * (3f - 2f * t);
        }
        return coreOutput;
    }

    public void ResetDetail()
    {
        _prevDetailOutput = 0f;
        _dcBlockSmooth = 0f;
        _prevSmoothedCore = 0f;
        _prevSmoothedDetail = 0f;
        _lmuPeak = 0f;
        _rearIntensity = 0f;
        _yawOnsetState = 0f;
        _yawInitC3 = false;
        _peakHold = 0f;
        _clipRate = 0f;
        _clipCount = 0;
        _clipTotal = 0;
    }

    public new void Reset()
    {
        base.Reset();
        _prevDetailOutput = 0f;
        _dcBlockSmooth = 0f;
        _prevSmoothedCore = 0f;
        _prevSmoothedDetail = 0f;
        _lmuPeak = 0f;
        _prevRearFy = 0f;
        _prevYawRateC3 = 0f;
        _yawInitC3 = false;
        _rearIntensity = 0f;
        _yawOnsetState = 0f;
        _peakHold = 0f;
        _clipRate = 0f;
        _clipCount = 0;
        _clipTotal = 0;
        _lastEqRate = 250f;
    }
}