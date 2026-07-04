using AcEvoFfbTuner.Core.FfbProcessing.Models;

namespace AcEvoFfbTuner.Core.FfbProcessing;

/// <summary>
/// RaceRoom Racing Experience standalone FFB pipeline.
///
/// R3E's SteeringForce is the TOTAL steering column torque — NOT per-wheel
/// self-aligning torque like AC EVO's Mz. This pipeline uses SteeringForce
/// directly as the core force.
///
/// ISOLATION: This pipeline NEVER calls base.Process(raw), which runs the
/// EVO pipeline (ChannelMixer, LUT, Damping, SlipEnhancer, DynamicEffects,
/// TyreFlex, GripGuard, CrashDetector, TyreCondition, WetWeather). Changes
/// here must NOT affect EVO — and vice versa.
/// </summary>
public sealed class R3eFfbPipeline : FfbPipeline
{
    private float _dcBlockSmooth;
    private float _prevDetailOutput;

    private bool _gearChangeMuteEnabled = true;
    public bool GearChangeMuteEnabled
    {
        get => _gearChangeMuteEnabled;
        set
        {
            _gearChangeMuteEnabled = value;
            base.GearShiftFilterEnabled = value;
        }
    }
    public float GearSpikeThreshold { get; set; } = 3000f;
    public float BrakeBoostGain { get; set; } = 0.15f;
    public float BrakeBoostThreshold { get; set; } = 0.1f;

    /// <summary>Core force multiplier. Default 1.0 — no EVO compensation needed now that SteeringForce bypasses the channel mixer.</summary>
    public override float CoreForceMultiplier { get; set; } = 1.0f;

    /// <summary>
    /// Tyre grip scale: how strongly average front tyre grip attenuates core force.
    /// 1.0 = full effect (grip scales force proportionally). 0.0 = no grip effect.
    /// Grip values from R3E: ~1.0 dry optimal, ~0.8 cold/slightly worn, lower = sliding/wet.
    /// </summary>
    public float TyreGripScale { get; set; } = 1.0f;

    /// <summary>Flatspot vibration gain. 0 = off, 1 = normal. Synthesised from R3E TireFlatspot per-wheel.</summary>
    public float FlatspotGain { get; set; } = 1.0f;

    /// <summary>Surface feel gain. 0 = off, 1 = normal. Synthesised from R3E TireOnMtrl (grass/gravel rumble).</summary>
    public float SurfaceFeelGain { get; set; } = 1.0f;

    /// <summary>Engine torque LFE modulation. 0 = no torque effect, 1 = full. Scales LFE amplitude by actual engine torque from R3E telemetry.</summary>
    public float EngineTorqueLfeMod { get; set; } = 1.0f;

    /// <summary>Brake pressure feel gain. Synthesised from R3E BrakePressure per-wheel (kN). Adds texture proportional to brake force.</summary>
    public float BrakePressureGain { get; set; } = 1.0f;

    /// <summary>Traction control feel gain. Synthesised from R3E TractionControlPercent (0-100%). Subtle force pulse when TC cuts power.</summary>
    public float TcFeelGain { get; set; } = 1.0f;

    /// <summary>Core smoothing factor (EMA alpha). 0 = no smoothing (raw). 0.1 = light. 0.5 = heavy. Smoothes sharp transitions in centering force.</summary>
    public float CoreSmoothing { get; set; } = 0.0f;

    /// <summary>Detail smoothing factor (EMA alpha). 0 = no smoothing. 0.2 = light. 0.8 = very smooth. Smoothes vibration/texture feel.</summary>
    public float DetailSmoothing { get; set; } = 0.0f;

    private float[] _flatspotPhase = new float[4];
    private float _prevSmoothedCore;
    private float _prevSmoothedDetail;

    public override bool GearShiftFilterEnabled
    {
        get => _gearChangeMuteEnabled;
        set => _gearChangeMuteEnabled = value;
    }

    public override FfbProcessedData Process(FfbRawData raw)
    {
        // ═════════════════════════════════════════════════════════════════
        // DO NOT call base.Process(raw) — that runs the EVO pipeline.
        // R3E's SteeringForce is a complete column torque, not per-wheel
        // raw forces. Processing it through EVO stages corrupts the signal.
        // ═════════════════════════════════════════════════════════════════

        float steeringForce = raw.FinalFf;

        // ═════════════════════════════════════════════════════════════════
        // CORE PATH — Direct SteeringForce to output
        // ═════════════════════════════════════════════════════════════════

        // Brake weight-transfer boost
        float brakeBoost = 1f;
        if (raw.BrakeInput > BrakeBoostThreshold)
        {
            float brakeIntensity = Math.Min(
                (raw.BrakeInput - BrakeBoostThreshold) / (1f - BrakeBoostThreshold), 1f);
            brakeBoost = 1f + BrakeBoostGain * brakeIntensity;
        }

        // Centering direction: always push toward center regardless of
        // SteeringForce sign convention (may vary by game/version).
        // At exactly 0° steer → sign = 0 → no centering force.
        float steerSign = raw.SteerAngle > 0f ? 1f : raw.SteerAngle < 0f ? -1f : 0f;
        float magnitude = Math.Abs(steeringForce);

        // Tyre grip modulation: scale core force by average front tyre grip.
        // When grip drops (sliding, wet, cold tyres), centering force reduces
        // naturally — the wheel feels lighter as the front tyres lose grip.
        // R3E TireGrip: ~1.0 optimal, ~0.8 cold/worn, lower = sliding/wet.
        float gripScale = 1f;
        if (TyreGripScale > 0.001f && raw.TyreGrip != null && raw.TyreGrip.Length >= 2)
        {
            float avgFrontGrip = (raw.TyreGrip[0] + raw.TyreGrip[1]) * 0.5f;
            gripScale = 0.1f + (1f - 0.1f) * Math.Clamp(avgFrontGrip, 0f, 1f);
            gripScale = 1f + (gripScale - 1f) * TyreGripScale;
        }
        magnitude *= gripScale;

        // Normalize via MasterGain/ForceScale.
        // Moza convention: positive force = wheel LEFT (auto-detect confirmed).
        // Core centering: when steering right, push LEFT (= positive in Moza).
        // steerSign = +1 when right → magnitude * +1 = positive = LEFT = centering.
        // Device _invertForce flag handles sign correction for other wheel brands.
        float coreNorm = magnitude * steerSign * MasterGain / Math.Max(ForceScale, 0.001f);

        // Centering override (smoothstep at small steer angles)
        float corePostCenter = ApplyCenteringOverride(coreNorm, raw);

        // Output gain
        float coreOutput = corePostCenter * OutputGain;

        // Core force multiplier: user-controlled main force gain.
        // Exposed as "Center Strength (x)" slider in the UI.
        coreOutput *= CoreForceMultiplier;

        // Brake boost (applied after centering shaping so it doesn't
        // interact with the smoothstep at small angles)
        coreOutput *= brakeBoost;

        // Speed fade: zero below 0.5 km/h, ramp to full at 5 km/h
        if (raw.SpeedKmh < 0.5f)
        {
            coreOutput = 0f;
            _prevSmoothedCore = 0f;
            _prevSmoothedDetail = 0f;
            _prevDetailOutput = 0f;
        }
        else if (raw.SpeedKmh < 5.0f)
        {
            float speedFactor = (raw.SpeedKmh - 0.5f) / 4.5f;
            coreOutput *= speedFactor;
        }

        // Core EMA smoothing: alpha is inverted so slider = 0 is none, higher = smoother.
        // alpha = 1 - CoreSmoothing: at 0 → alpha=1 (raw), at 0.5 → alpha=0.5, at 0.9 → alpha=0.1
        float coreAlpha = 1f - Math.Clamp(CoreSmoothing, 0f, 0.95f);
        if (coreAlpha < 0.999f)
        {
            coreOutput = _prevSmoothedCore + coreAlpha * (coreOutput - _prevSmoothedCore);
        }
        _prevSmoothedCore = coreOutput;

        // ═════════════════════════════════════════════════════════════════
        // DETAIL PATH — Texture effects only
        // ═════════════════════════════════════════════════════════════════

        float deltaTime = 1f / 60f;
        float detailForce = 0f;

        // Vibration mixer (road, curb, slip, ABS, scrub, rear slip, offtrack)
        float vibration = VibrationMixer.Mix(raw);
        // Scale vibration by tyre grip: less texture when grip is low
        // (sliding tyres produce less crisp vibration feel)
        vibration *= gripScale;
        detailForce += vibration;

        // ABS force modulation
        float absMod = VibrationMixer.AbsForceModulation;
        if (Math.Abs(absMod) > 0.001f)
        {
            float sign = coreOutput >= 0f ? 1f : -1f;
            if (Math.Abs(coreOutput) < 0.01f)
                sign = Math.Abs(raw.SteerAngle) > 0.004f
                    ? -Math.Sign(raw.SteerAngle) : 1f;
            detailForce += absMod * sign;
        }

        // Road force modulation
        float roadMod = VibrationMixer.RoadForceModulation;
        if (Math.Abs(roadMod) > 0.001f)
            detailForce += roadMod;

        // Rear slip warning
        float rearMod = VibrationMixer.RearSlipModulation;
        if (Math.Abs(rearMod) > 0.001f)
            detailForce += rearMod;

        // Offtrack rumble
        float offtrackMod = VibrationMixer.OfftrackModulation;
        if (Math.Abs(offtrackMod) > 0.001f)
            detailForce += offtrackMod;

        // LFE (engine RPM / suspension) modulated by engine torque
        float lfe = LfeGenerator.Generate(raw);
        if (Math.Abs(lfe) > 0.001f)
        {
            float torqueFactor = 1f;
            if (EngineTorqueLfeMod > 0.001f)
            {
                float torqueNorm = Math.Clamp(raw.EngineTorque / 500f, 0f, 1f);
                torqueFactor = 1f - EngineTorqueLfeMod + torqueNorm * EngineTorqueLfeMod;
            }
            detailForce += lfe * torqueFactor;
        }

        // Flatspot vibration: per-wheel sharp pulse at rotation rate.
        // R3E TireFlatspot: boolean trigger (0=none, 1=flatspotted).
        float flatspotVib = SynthesizeFlatspotVibration(raw, deltaTime);
        if (Math.Abs(flatspotVib) > 0.001f)
            detailForce += flatspotVib;

        // Surface feel: rumble texture based on surface material type.
        // R3E TireOnMtrl: 0=none, 1=tarmac, 2=grass, 3=dirt, 4=gravel, 5=rumble, 6=concrete.
        float surfaceVib = SynthesizeSurfaceVibration(raw, gripScale);
        if (Math.Abs(surfaceVib) > 0.001f)
            detailForce += surfaceVib;

        // Brake pressure feel: texture proportional to brake force per wheel.
        // R3E BrakePressure: per-wheel in kN. Adds subtle vibration as brakes bite.
        float brakeVib = SynthesizeBrakePressureVibration(raw, gripScale);
        if (Math.Abs(brakeVib) > 0.001f)
            detailForce += brakeVib;

        // Traction control feel: subtle pulse when TC cuts power.
        // R3E TractionControlPercent: 0-100% cut level.
        float tcVib = SynthesizeTcIntervention(raw, deltaTime);
        if (Math.Abs(tcVib) > 0.001f)
            detailForce += tcVib;

        // EQ (biquad filters on detail path only)
        detailForce = Equalizer.Process(detailForce);

        // DC blocker: remove low-frequency bias from detail path
        // (prevents turn-correlated suspension EMAs from pushing wheel away from center)
        float dcBlocked = detailForce - _dcBlockSmooth;
        _dcBlockSmooth += dcBlocked * 0.02f;
        detailForce = dcBlocked;

        // Direction-based suppression: prevent detail from reversing core
        // at small steer angles where core force is naturally weak
        if (coreOutput > 0f && detailForce < 0f)
            detailForce = Math.Max(detailForce, -coreOutput * 0.5f);
        else if (coreOutput < 0f && detailForce > 0f)
            detailForce = Math.Min(detailForce, -coreOutput * 0.5f);

        // Detail EMA smoothing: alpha is inverted so slider = 0 is none, higher = smoother.
        // alpha = 1 - DetailSmoothing: at 0 → alpha=1 (raw), at 0.6 → alpha=0.4, at 0.9 → alpha=0.1
        float detailAlpha = 1f - Math.Clamp(DetailSmoothing, 0f, 0.95f);
        if (detailAlpha < 0.999f)
        {
            detailForce = _prevSmoothedDetail + detailAlpha * (detailForce - _prevSmoothedDetail);
        }
        _prevSmoothedDetail = detailForce;

        // Slew rate limiter on detail path
        if (raw.SpeedKmh >= 0.5f)
        {
            float slewDelta = detailForce - _prevDetailOutput;
            if (Math.Abs(slewDelta) > MaxSlewRate)
                detailForce = _prevDetailOutput + Math.Sign(slewDelta) * MaxSlewRate;
        }
        _prevDetailOutput = raw.SpeedKmh < 0.5f ? 0f : detailForce;

        // ═════════════════════════════════════════════════════════════════
        // FINAL MIX
        // ═════════════════════════════════════════════════════════════════

        float filteredCore = ApplyGearShiftFilter(coreOutput, deltaTime, raw.Gear);
        float finalOutput = filteredCore + detailForce;

        // Soft clip
        finalOutput = OutputClipper.Process(finalOutput, out bool isClipping);

        // Noise floor gate
        if (Math.Abs(finalOutput) < NoiseFloor)
            finalOutput = 0f;

        return new FfbProcessedData
        {
            MainForce = finalOutput,
            VibrationForce = vibration * GearShiftMuteGain,
            RawFinalFf = steeringForce,
            PostCompressionForce = coreNorm,
            PostDampingForce = corePostCenter,
            PostOutputGainForce = coreOutput,
            PostDynamicForce = detailForce,
            CoreForce = coreOutput,
            DetailForce = detailForce,
            SpeedKmh = raw.SpeedKmh,
            SteerAngle = raw.SteerAngle,
            PacketId = raw.PacketId,
            IsClipping = isClipping,
            GearShiftMuteGain = GearShiftMuteGain
        };
    }

    /// <summary>
    /// Center sharpness: smoothstep ramp from 0 to full force over
    /// CenterSharpnessDegrees. Lower = sharper on-center, higher = softer.
    /// </summary>
    protected override float ApplyCenteringOverride(float coreOutput, FfbRawData raw)
    {
        if (CenterSharpnessDegrees > 0.001f)
        {
            float lockHalf = Math.Abs(raw.SteerDegrees) * 0.5f;
            if (lockHalf < 1f) lockHalf = 450f;
            float absSteerDeg = Math.Abs(raw.SteerAngle) * lockHalf;
            float t = Math.Clamp(absSteerDeg / CenterSharpnessDegrees, 0f, 1f);
            float ramp = t * t * (3f - 2f * t);
            return coreOutput * ramp;
        }
        return coreOutput;
    }

    /// <summary>
    /// Synthesise flatspot vibration from R3E TireFlatspot telemetry.
    /// R3E TireFlatspot is boolean: 0=none, 1=flatspotted.
    /// Per-wheel phase accumulator at wheel rotation rate.
    /// Sharp pulse once per revolution when flatspot is present.
    /// </summary>
    private float SynthesizeFlatspotVibration(FfbRawData raw, float deltaTime)
    {
        if (FlatspotGain < 0.001f || raw.SpeedKmh < 5f) return 0f;
        if (raw.TireFlatspot == null) return 0f;

        float vibration = 0f;
        float wheelRps = raw.SpeedKmh / 7.2f; // approximate from speed / tire circumference

        for (int i = 0; i < 4; i++)
        {
            bool hasFlatspot = raw.TireFlatspot[i] > 0.5f;
            if (!hasFlatspot) continue;

            _flatspotPhase[i] += wheelRps * deltaTime;
            float phase = (_flatspotPhase[i] % 1f) * MathF.PI * 2f;
            float pulse = MathF.Pow(MathF.Max(0f, MathF.Cos(phase)), 8f);
            vibration += pulse * 0.5f * FlatspotGain;
        }

        return Math.Min(vibration, 1f);
    }

    /// <summary>
    /// Synthesise surface feel from R3E TireOnMtrl telemetry.
    /// 0=none, 1=tarmac, 2=grass, 3=dirt, 4=gravel, 5=rumble strip, 6=concrete.
    /// Adds rumble when tyres leave the racing surface.
    /// </summary>
    private float SynthesizeSurfaceVibration(FfbRawData raw, float gripScale)
    {
        if (SurfaceFeelGain < 0.001f || raw.TireOnMtrl == null) return 0f;

        bool onGrass = false, onGravel = false, onDirt = false, onRumble = false;
        for (int i = 0; i < 4; i++)
        {
            int mtrl = raw.TireOnMtrl[i];
            if (mtrl == 2) onGrass = true;
            if (mtrl == 3) onDirt = true;
            if (mtrl == 4) onGravel = true;
            if (mtrl == 5) onRumble = true;
        }

        float surface = 0f;
        if (onRumble) surface = 0.50f;
        else if (onGravel) surface = 0.40f;
        else if (onDirt) surface = 0.30f;
        else if (onGrass) surface = 0.20f;

        surface *= gripScale;
        return surface * SurfaceFeelGain;
    }

    /// <summary>
    /// Synthesise brake pressure feel from R3E BrakePressure telemetry.
    /// Per-wheel brake pressure in kN. Subtle vibration texture proportional
    /// to braking force — adds bite feel as pads grip the discs.
    /// </summary>
    private float SynthesizeBrakePressureVibration(FfbRawData raw, float gripScale)
    {
        if (BrakePressureGain < 0.001f || raw.SpeedKmh < 1f) return 0f;
        if (raw.BrakePressure == null || raw.BrakeInput < 0.01f) return 0f;

        float avgPressure = 0f;
        int count = 0;
        for (int i = 0; i < 4; i++)
        {
            if (raw.BrakePressure[i] > 0.01f)
            {
                avgPressure += raw.BrakePressure[i];
                count++;
            }
        }
        if (count == 0) return 0f;
        avgPressure /= count;

        // Map brake pressure to vibration intensity.
        // R3E brake pressure in kN, typical max ~2-5 kN under hard braking.
        float pressureNorm = Math.Clamp(avgPressure / 5f, 0f, 1f);
        // Subtle texture — use cos at frame rate for a light buzz proportional to pressure
        float buzz = MathF.Cos((float)(Environment.TickCount) * 0.015f) * 0.5f + 0.5f;
        float vibration = buzz * pressureNorm * 0.08f; // subtle, 8% max at full brake
        vibration *= gripScale;
        return vibration * BrakePressureGain;
    }

    /// <summary>
    /// Synthesise traction control feel from R3E TractionControlPercent.
    /// When TC cuts engine power (0-100%), produce a subtle force release
    /// as torque reduction momentarily reduces steering force.
    /// </summary>
    private float SynthesizeTcIntervention(FfbRawData raw, float deltaTime)
    {
        if (TcFeelGain < 0.001f || raw.SpeedKmh < 5f) return 0f;

        float tcCut = raw.TractionControlPercent;
        if (tcCut < 1f) return 0f;

        // Normalise TC cut: 1-100% → 0-1
        float tcNorm = Math.Clamp(tcCut / 100f, 0f, 1f);
        // Subtle force reduction when TC is active: brief torque pull-back
        // Higher TC cut = more noticeable
        return -tcNorm * 0.05f * TcFeelGain;
    }
}
