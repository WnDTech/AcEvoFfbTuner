using AcEvoFfbTuner.Core.FfbProcessing.Models;

namespace AcEvoFfbTuner.Core.FfbProcessing;

/// <summary>
/// RaceRoom-specific FFB pipeline extensions.
///
/// R3E's SteeringForce is total column force (not self-aligning torque like AC EVO's Mz),
/// and the game doesn't provide slip angle telemetry. This subclass adds:
/// 1. DC blocker on detail path — removes turn-correlated bias from suspension EMAs
///    that would otherwise push the wheel away from center during cornering.
/// 2. Dynamic suppression — prevents the detail channel from reversing the core
///    centering direction at small steer angles where core force is naturally weak.
/// 3. Brake weight-transfer boost — when braking, front tyre grip increases due to
///    weight shift, so we scale Mz up proportionally.
/// 4. Gear shift filter — time-based smooth fade using actual gear value changes,
///    not force-spike detection. Uses 180ms hard mute + 50ms smoothstep recovery.
/// </summary>
public sealed class R3eFfbPipeline : FfbPipeline
{
    private float _dcBlockSmooth;

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

    public override bool GearShiftFilterEnabled
    {
        get => _gearChangeMuteEnabled;
        set
        {
            _gearChangeMuteEnabled = value;
        }
    }

    public override FfbProcessedData Process(FfbRawData raw)
    {
        // ── Brake weight-transfer boost ─────────────────────────────────
        // When braking: weight shifts to front tyres → more grip → stronger Mz.
        // SteeringForce is proportional to grip, so boost it while braking.
        float brakeBoost = 1f;
        if (raw.BrakeInput > BrakeBoostThreshold)
        {
            float brakeIntensity = Math.Min((raw.BrakeInput - BrakeBoostThreshold) / (1f - BrakeBoostThreshold), 1f);
            brakeBoost = 1f + BrakeBoostGain * brakeIntensity;
        }

        var result = base.Process(raw);

        result.MainForce *= brakeBoost;
        result.CoreForce *= brakeBoost;

        return result;
    }

    protected override void OnDetailForceProcessed(float coreOutput, ref float detailForce)
    {
        float dcBlocked = detailForce - _dcBlockSmooth;
        _dcBlockSmooth += dcBlocked * 0.02f;
        detailForce = dcBlocked;

        if (coreOutput > 0f && detailForce < 0f)
            detailForce = Math.Max(detailForce, -coreOutput * 0.5f);
        else if (coreOutput < 0f && detailForce > 0f)
            detailForce = Math.Min(detailForce, -coreOutput * 0.5f);
    }

    protected override float ApplyCenteringOverride(float coreOutput, FfbRawData raw)
    {
        // V-shape quadratic centre suppression for R3E.
        // Smooth force fade near steering centre — no notchiness.
        // Only applies when a non-zero suppression zone is configured.
        if (CenterSuppressionDegrees > 0.001f)
        {
            // SteerAngle is normalized -1..+1 (not radians).
            // Convert to actual wheel degrees using the configured steering lock.
            float lockHalf = Math.Abs(raw.SteerDegrees) * 0.5f;
            if (lockHalf < 1f) lockHalf = 450f; // fallback to 900° lock
            float absSteerDeg = Math.Abs(raw.SteerAngle) * lockHalf;
            float normalized = Math.Clamp(absSteerDeg / CenterSuppressionDegrees, 0f, 1f);
            return coreOutput * normalized * normalized;
        }
        return coreOutput;
    }
}
