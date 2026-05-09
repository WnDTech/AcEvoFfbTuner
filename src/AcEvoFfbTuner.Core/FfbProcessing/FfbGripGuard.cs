using AcEvoFfbTuner.Core.FfbProcessing.Models;

namespace AcEvoFfbTuner.Core.FfbProcessing;

public sealed class FfbGripGuard
{
    public bool Enabled { get; set; } = true;

    public float PeakSlipAngle { get; set; } = 0.10f;

    public float AttenuationStrength { get; set; } = 1.0f;

    public float MechanicalTrailGain { get; set; } = 0.015f;

    public float MinSpeedKmh { get; set; } = 10.0f;

    private float _peakForceRef;
    private float _smAttenuationFactor = 1.0f;
    private float _smSlipAngle;

    public float Apply(float force, FfbRawData raw)
    {
        if (!Enabled || raw.SpeedKmh < MinSpeedKmh)
            return force;

        float avgSlipAngle = (Math.Abs(raw.SlipAngle[0]) + Math.Abs(raw.SlipAngle[1])) * 0.5f;

        _smSlipAngle = _smSlipAngle * 0.70f + avgSlipAngle * 0.30f;
        float smoothSlip = _smSlipAngle;

        float absForce = Math.Abs(force);
        float steerSign = Math.Abs(raw.SteerAngle) > 0.002f ? Math.Sign(raw.SteerAngle) : 0f;
        float forceSign = absForce > 0.001f ? Math.Sign(force) : 0f;

        bool isPullingAway = steerSign != 0f && forceSign == steerSign;

        if (smoothSlip < PeakSlipAngle)
        {
            if (absForce > _peakForceRef)
                _peakForceRef = _peakForceRef * 0.70f + absForce * 0.30f;
            else
                _peakForceRef = _peakForceRef * 0.97f + absForce * 0.03f;

            _smAttenuationFactor = _smAttenuationFactor * 0.90f + 1.0f * 0.10f;
        }
        else
        {
            float overRatio = Math.Min((smoothSlip - PeakSlipAngle) / Math.Max(PeakSlipAngle, 0.01f), 3.0f);
            float targetAttenuation = Math.Max(1.0f - MathF.Pow(overRatio, 1.5f) * AttenuationStrength * 0.4f, 0.05f);
            _smAttenuationFactor = _smAttenuationFactor * 0.85f + targetAttenuation * 0.15f;

            if (isPullingAway && _peakForceRef > 0.001f)
            {
                float maxAllowed = _peakForceRef * _smAttenuationFactor;
                if (absForce > maxAllowed)
                    force = forceSign * maxAllowed;
            }
        }

        if (MechanicalTrailGain > 0.001f && steerSign != 0f)
        {
            float speedFactor = Math.Clamp((raw.SpeedKmh - MinSpeedKmh) / 30.0f, 0f, 1f);
            float absSteer = Math.Abs(raw.SteerAngle);
            float steerFactor = Math.Clamp(absSteer * 10f, 0f, 1f);
            float centeringForce = -steerSign * MechanicalTrailGain * speedFactor * steerFactor;
            force += centeringForce;
        }

        if (absForce < 0.01f)
            _peakForceRef *= 0.9997f;
        if (_peakForceRef < 0.001f) _peakForceRef = 0f;

        return force;
    }

    public void Reset()
    {
        _peakForceRef = 0f;
        _smAttenuationFactor = 1.0f;
        _smSlipAngle = 0f;
    }
}
