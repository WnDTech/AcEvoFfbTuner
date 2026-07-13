using AcEvoFfbTuner.Core.FfbProcessing.Models;

namespace AcEvoFfbTuner.Core.FfbProcessing;

public sealed class LmuFfbPipeline : FfbPipeline
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
    public float BrakeBoostGain { get; set; } = 0.4f;
    public float BrakeBoostThreshold { get; set; } = 0.1f;

    public override float CoreForceMultiplier { get; set; } = 3.0f;

    /// <summary>How much centering force fades as grip decreases. 0 = no scaling, 1 = full fade.</summary>
    public float GripScaleGain { get; set; } = 0.6f;

    /// <summary>Gain for tyre temperature feel (cold marbles / hot graining). 0 = off.</summary>
    public float TyreTempGain { get; set; } = 0.0f;

    public override bool GearShiftFilterEnabled
    {
        get => _gearChangeMuteEnabled;
        set { _gearChangeMuteEnabled = value; }
    }

    public override FfbProcessedData Process(FfbRawData raw)
    {
        // LMU provides steering shaft torque (column force), not per-wheel Mz/Fx/Fy.
        // DO NOT call base.Process(raw) — that runs the EVO Mz-based pipeline.
        // Processing column force through EVO stages corrupts the signal.
        // Instead, handle the column force directly (same approach as R3eFfbPipeline).

        float steeringForce = raw.FinalFf;

        // ═══════════════════════════════════════════════════════════════════════
        // CORE PATH — Direct steering force to output
        // ═══════════════════════════════════════════════════════════════════════

        float brakeBoost = 1f;
        if (raw.BrakeInput > BrakeBoostThreshold)
        {
            float brakeIntensity = Math.Min((raw.BrakeInput - BrakeBoostThreshold) / (1f - BrakeBoostThreshold), 1f);
            brakeBoost = 1f + BrakeBoostGain * brakeIntensity;
        }

        // Centering direction: Moza convention is positive force = wheel LEFT.
        // The original column force magnitude × steerSign produces the correct
        // centering direction because magnitude = |torque| (always positive) and
        // steerSign = -1 for left turns gives negative force → push RIGHT = center.
        // steerSign = +1 for right turns gives positive force → push LEFT = center.
        float steerSign = raw.SteerAngle > 0f ? 1f : raw.SteerAngle < 0f ? -1f : 0f;
        float magnitude = Math.Abs(steeringForce);

        // LMU provides column force (0-20 Nm range). ForceScale from the profile
        // is calibrated for EVO's Mz values (hundreds of Nm), so ForceScale=1 is
        // meaningless for column force. Instead, ForceScale acts as a sensitivity
        // multiplier: 1.0 = default (20 Nm torque → 0.5 normalized pre-gain).
        // Higher = more sensitive, lower = less sensitive.
        float effectiveScale = Math.Max(40f / Math.Max(ForceScale, 0.01f), 1f);
        float coreNorm = magnitude * steerSign * MasterGain / effectiveScale;

        // Centering override (smoothstep at small steer angles)
        float corePostCenter = ApplyCenteringOverride(coreNorm, raw);

        // Output gain
        float coreOutput = corePostCenter * OutputGain;

        // Core multiplier
        coreOutput *= CoreForceMultiplier;

        // Brake boost
        coreOutput *= brakeBoost;

        // ── Grip-scaled centering ──
        // LMU provides per-wheel grip fraction (gripFract@wOff+112, 0-1 scale
        // where 1.0 = full friction). This is the real tire friction utilization
        // from LMU's physics — when the front tires approach the adhesion limit,
        // the centering force naturally fades, giving the driver an authentic
        // feel of front grip loss through the wheel.
        //
        // Safe grip = load-weighted minimum across all four wheels. A wheel with
        // zero load (airborne) doesn't contribute to grip.
        if (raw.SpeedKmh > 5f)
        {
            float gripSum = 0f, loadSum = 0f;
            for (int wi = 0; wi < 4; wi++)
            {
                float load = Math.Max(raw.WheelLoad[wi], 0f);
                if (load > 50f)
                {
                    float grip = Math.Clamp(raw.TyreGrip[wi], 0f, 1f);
                    gripSum += grip * load;
                    loadSum += load;
                }
            }
            float frontGrip = loadSum > 1f ? gripSum / loadSum : 1f;
            // Apply grip as a multiplier: GripScaleGain controls fade depth.
            // 0 = no grip scaling (full force always), 1 = full fade range.
            // At zero grip → minimum 20% to prevent total force loss.
            if (GripScaleGain > 0.001f)
            {
                float fade = (1f - frontGrip) * GripScaleGain * 0.8f;
                coreOutput *= Math.Clamp(1f - fade, 0.2f, 1f);
            }
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
        // DETAIL PATH — Texture, vibration, and road feel
        // ═══════════════════════════════════════════════════════════════════════

        float deltaTime = 1f / 250f; // telemetry loop rate
        float detailForce = 0f;

        // Vibration mixer (road, curb, slip, ABS, scrub, rear slip, offtrack)
        float vibration = VibrationMixer.Mix(raw);
        detailForce += vibration;

        // ── Tyre temperature feel ──
        // Cold tyres (< 75°C) produce a grainy/marbles texture as the tyre
        // struggles to reach operating window. Overheated (> 105°C) adds a
        // greasy vibration from thermal degradation. Both are subtle noise
        // injected into the detail path. TyreTempGain = 0 disables entirely.
        if (TyreTempGain > 0.001f && raw.TyreTemp != null && raw.TyreTemp.Length >= 4 && raw.SpeedKmh > 20f)
        {
            float avgTemp = (raw.TyreTemp[0] + raw.TyreTemp[1] + raw.TyreTemp[2] + raw.TyreTemp[3]) * 0.25f;
            float coldFactor = avgTemp < 75f ? Math.Clamp((75f - avgTemp) / 35f, 0f, 1f) : 0f;
            float hotFactor  = avgTemp > 105f ? Math.Clamp((avgTemp - 105f) / 25f, 0f, 1f) : 0f;
            float tempNoise = (coldFactor + hotFactor) * TyreTempGain * 0.005f;
            detailForce += tempNoise * (Math.Abs(raw.SteerAngle) > 0.003f ? 1f : 0.2f);
        }

        // NOTE: SlipEnhancer is NOT used for LMU. The reader computes slip angle
        // as Atan2(vx, -vz) clamped to ±0.30 — this saturates during any cornering
        // and is not based on real tire contact patch data. Slip ratio is based on
        // wheel speed vs vehicle speed but is also clamped (±0.20) and unreliable
        // for meaningful slip-based FFB effects. If slip tuning is desired later,
        // the LMU reader's slip computation needs to be improved first.

        // ABS force modulation
        float absMod = VibrationMixer.AbsForceModulation;
        if (Math.Abs(absMod) > 0.001f)
        {
            float sign = coreOutput >= 0f ? 1f : -1f;
            if (Math.Abs(coreOutput) < 0.01f)
                sign = Math.Abs(raw.SteerAngle) > SteerDirDeadzone ? -Math.Sign(raw.SteerAngle) : 1f;
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

        // LFE (engine RPM / suspension)
        float lfe = LfeGenerator.Generate(raw);
        if (Math.Abs(lfe) > 0.001f)
            detailForce += lfe;

        // EQ on detail path only
        detailForce = Equalizer.Process(detailForce);

        // DC blocker + direction suppression handled by OnDetailForceProcessed hook below

        // Slew rate limiter on detail path only
        if (raw.SpeedKmh >= 0.5f)
        {
            float slewDelta = detailForce - _prevDetailOutput;
            if (Math.Abs(slewDelta) > MaxSlewRate)
                detailForce = _prevDetailOutput + Math.Sign(slewDelta) * MaxSlewRate;
        }
        _prevDetailOutput = raw.SpeedKmh < 0.5f ? 0f : detailForce;

        // Hook for subclasses — currently unused since this is the final subclass
        OnDetailForceProcessed(coreOutput, ref detailForce);

        // ═══════════════════════════════════════════════════════════════════════
        // FINAL MIX
        // ═══════════════════════════════════════════════════════════════════════

        float filteredCore = ApplyGearShiftFilter(coreOutput, deltaTime, raw.Gear);
        float finalOutput = filteredCore + detailForce;

        // Soft clip
        finalOutput = OutputClipper.Process(finalOutput, out bool isClipping);

        // Noise floor gate
        float speedNoiseScale = raw.SpeedKmh < 10.0f
            ? 1.0f + (1.0f - raw.SpeedKmh / 10.0f) * 0.5f
            : 1.0f;
        float effectiveNoiseFloor = NoiseFloor * speedNoiseScale;
        if (Math.Abs(finalOutput) < effectiveNoiseFloor)
            finalOutput = 0f;

        return new FfbProcessedData
        {
            MainForce = finalOutput,
            VibrationForce = vibration * GearShiftMuteGain,
            RawFinalFf = steeringForce,
            PostCompressionForce = coreNorm,
            PostLutForce = corePostCenter,
            PostDampingForce = corePostCenter,
            PostOutputGainForce = coreOutput,
            PostDynamicForce = detailForce,
            CoreForce = coreOutput,
            DetailForce = detailForce,
            AutoGainApplied = 1f,
            IsClipping = isClipping,
            SpeedKmh = raw.SpeedKmh,
            SteerAngle = raw.SteerAngle,
            PacketId = raw.PacketId,
            GearShiftMuteGain = GearShiftMuteGain
        };
    }

    protected override void OnDetailForceProcessed(float coreOutput, ref float detailForce)
    {
        float dcBlocked = detailForce - _dcBlockSmooth;
        _dcBlockSmooth += dcBlocked * 0.02f;
        detailForce = dcBlocked;

        if (coreOutput > 0f && detailForce < 0f)
            detailForce = Math.Max(detailForce, -coreOutput * 0.8f);
        else if (coreOutput < 0f && detailForce > 0f)
            detailForce = Math.Min(detailForce, -coreOutput * 0.8f);
    }

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

    public void ResetDetail()
    {
        _prevDetailOutput = 0f;
        _dcBlockSmooth = 0f;
    }
}
