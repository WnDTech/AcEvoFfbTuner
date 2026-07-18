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
        set { _gearChangeMuteEnabled = value; base.GearShiftFilterEnabled = value; }
    }
    public float GearSpikeThreshold { get; set; } = 3000f;
    public float BrakeBoostGain { get; set; } = 0.4f;
    public float BrakeBoostThreshold { get; set; } = 0.1f;
    public override float CoreForceMultiplier { get; set; } = 3.0f;
    public float GripScaleGain { get; set; } = 0.6f;
    public float TyreTempGain { get; set; } = 0.0f;

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
        float effectiveScale = Math.Max(120f / Math.Max(ForceScale, 0.01f), 1f);
        float coreNorm = magnitude * steerSign * MasterGain / effectiveScale;

        // Damping (viscous, friction, inertia) with hardcoded minimum floors
        // (MinViscous=0.04, MinFriction=0.02). This is the PRIMARY oscillation
        // defense — velocity-opposing force bleeds energy from free oscillation.
        // The signed output MUST be preserved intact.
        float coreDamped = Damping.Apply(coreNorm, raw.SpeedKmh, raw.SteerAngle);

        // Output scaling
        float coreForce = coreDamped * OutputGain;
        coreForce *= CoreForceMultiplier;

        // Brake boost
        float brakeBoost = 1f;
        if (raw.BrakeInput > BrakeBoostThreshold)
        {
            float bi = Math.Min((raw.BrakeInput - BrakeBoostThreshold) / (1f - BrakeBoostThreshold), 1f);
            brakeBoost = 1f + BrakeBoostGain * bi;
        }
        coreForce *= brakeBoost;

        // Grip-scaled centering (only when grip data is actually available)
        bool hasGrip = false;
        for (int gi = 0; gi < 4; gi++)
            if (Math.Abs(raw.TyreGrip[gi]) > 0.01f) { hasGrip = true; break; }
        if (raw.SpeedKmh > 5f && hasGrip)
        {
            float gripSum = 0f, loadSum = 0f;
            for (int wi = 0; wi < 4; wi++)
            {
                float load = Math.Max(raw.WheelLoad[wi], 0f);
                if (load > 50f) { float g = Math.Clamp(raw.TyreGrip[wi], 0f, 1f); gripSum += g * load; loadSum += load; }
            }
            float frontGrip = loadSum > 1f ? gripSum / loadSum : 1f;
            if (GripScaleGain > 0.001f)
                coreForce *= Math.Clamp(1f - (1f - frontGrip) * GripScaleGain * 0.8f, 0.2f, 1f);
        }

        // Speed fade
        if (raw.SpeedKmh < 0.5f) { coreForce = 0f; _prevDetailOutput = 0f; }
        else if (raw.SpeedKmh < 5.0f)
            coreForce *= (raw.SpeedKmh - 0.5f) / 4.5f;

        // ═══════════════════════════════════════════════════════════════════════
        // DETAIL PATH
        // ═══════════════════════════════════════════════════════════════════════
        float deltaTime = 1f / 250f;
        float detailForce = 0f;

        float vibration = VibrationMixer.Mix(raw);
        detailForce += vibration;

        if (TyreTempGain > 0.001f && raw.TyreTemp is { Length: >= 4 } && raw.SpeedKmh > 20f)
        {
            float avg = (raw.TyreTemp[0] + raw.TyreTemp[1] + raw.TyreTemp[2] + raw.TyreTemp[3]) * 0.25f;
            float cold = avg < 75f ? Math.Clamp((75f - avg) / 35f, 0f, 1f) : 0f;
            float hot  = avg > 105f ? Math.Clamp((avg - 105f) / 25f, 0f, 1f) : 0f;
            detailForce += (cold + hot) * TyreTempGain * 0.005f * (Math.Abs(raw.SteerAngle) > 0.003f ? 1f : 0.2f);
        }

        float absMod = VibrationMixer.AbsForceModulation;
        if (Math.Abs(absMod) > 0.001f)
        {
            float sign = coreForce >= 0f ? 1f : -1f;
            if (Math.Abs(coreForce) < 0.01f)
                sign = Math.Abs(raw.SteerAngle) > SteerDirDeadzone ? -Math.Sign(raw.SteerAngle) : 1f;
            detailForce += absMod * sign;
        }

        if (Math.Abs(VibrationMixer.RoadForceModulation) > 0.001f) detailForce += VibrationMixer.RoadForceModulation;
        if (Math.Abs(VibrationMixer.RearSlipModulation) > 0.001f) detailForce += VibrationMixer.RearSlipModulation;
        if (Math.Abs(VibrationMixer.OfftrackModulation) > 0.001f) detailForce += VibrationMixer.OfftrackModulation;

        float lfe = LfeGenerator.Generate(raw);
        if (Math.Abs(lfe) > 0.001f) detailForce += lfe;

        detailForce = Equalizer.Process(detailForce);

        if (raw.SpeedKmh >= 0.5f)
        {
            float sd = detailForce - _prevDetailOutput;
            if (Math.Abs(sd) > MaxSlewRate)
                detailForce = _prevDetailOutput + Math.Sign(sd) * MaxSlewRate;
        }
        _prevDetailOutput = raw.SpeedKmh < 0.5f ? 0f : detailForce;

        OnDetailForceProcessed(coreForce, ref detailForce);

        // ═══════════════════════════════════════════════════════════════════════
        // FINAL MIX
        // ═══════════════════════════════════════════════════════════════════════
        float filteredCore = ApplyGearShiftFilter(coreForce, deltaTime, raw.Gear);
        float finalOutput = filteredCore + detailForce;

        finalOutput = OutputClipper.Process(finalOutput, out bool isClipping);

        float speedNoiseScale = raw.SpeedKmh < 10.0f
            ? 1.0f + (1.0f - raw.SpeedKmh / 10.0f) * 0.5f : 1.0f;
        if (Math.Abs(finalOutput) < NoiseFloor * speedNoiseScale) finalOutput = 0f;

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
            return coreOutput * t * t * (3f - 2f * t);
        }
        return coreOutput;
    }

    public void ResetDetail()
    {
        _prevDetailOutput = 0f;
        _dcBlockSmooth = 0f;
    }
}