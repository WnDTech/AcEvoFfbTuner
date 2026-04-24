using AcEvoFfbTuner.Core.FfbProcessing.Models;

namespace AcEvoFfbTuner.Core.FfbProcessing;

public sealed class FfbDynamicEffects
{
    public float LateralGGain { get; set; } = 0.0f;
    public float LongitudinalGGain { get; set; } = 0.0f;
    public float SuspensionGain { get; set; } = 0.0f;
    public float YawRateGain { get; set; } = 0.0f;

    private float _prevSuspFront;
    private float _prevSuspRear;
    private float _smGForce;
    private float _smSuspForce;
    private float _smYawForce;

    public float Apply(float force, FfbRawData raw)
    {
        if (LateralGGain < 0.001f && LongitudinalGGain < 0.001f && SuspensionGain < 0.001f && YawRateGain < 0.001f)
            return force;

        float lateralG = raw.AccG.Length > 0 ? raw.AccG[0] : 0f;
        float longitudinalG = raw.AccG.Length > 1 ? raw.AccG[1] : 0f;

        float gForce = lateralG * LateralGGain + longitudinalG * LongitudinalGGain;
        gForce = Math.Clamp(gForce, -0.15f, 0.15f);
        _smGForce = _smGForce * 0.8f + gForce * 0.2f;

        float suspFront = (raw.SuspensionTravel[0] + raw.SuspensionTravel[1]) * 0.5f;
        float suspRear = (raw.SuspensionTravel[2] + raw.SuspensionTravel[3]) * 0.5f;
        float suspDelta = ((suspFront - _prevSuspFront) + (suspRear - _prevSuspRear)) * 0.5f;
        _prevSuspFront = suspFront;
        _prevSuspRear = suspRear;

        float suspForce = suspDelta * SuspensionGain;
        suspForce = Math.Clamp(suspForce, -0.15f, 0.15f);
        _smSuspForce = _smSuspForce * 0.8f + suspForce * 0.2f;

        float yawRate = raw.LocalAngularVel.Length > 1 ? raw.LocalAngularVel[1] : 0f;
        float yawForce = yawRate * YawRateGain;
        yawForce = Math.Clamp(yawForce, -0.15f, 0.15f);
        _smYawForce = _smYawForce * 0.8f + yawForce * 0.2f;

        // Total dynamic contribution clamped to prevent overwhelming Mz aligning torque
        float dynamicTotal = _smGForce + _smSuspForce + _smYawForce;
        dynamicTotal = Math.Clamp(dynamicTotal, -0.2f, 0.2f);

        return force + dynamicTotal;
    }

    public void Reset()
    {
        _prevSuspFront = 0f;
        _prevSuspRear = 0f;
        _smGForce = 0f;
        _smSuspForce = 0f;
        _smYawForce = 0f;
    }
}
