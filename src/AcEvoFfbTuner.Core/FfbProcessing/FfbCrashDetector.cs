using AcEvoFfbTuner.Core.FfbProcessing.Models;

namespace AcEvoFfbTuner.Core.FfbProcessing;

public sealed class FfbCrashDetector
{
    public bool Enabled { get; set; } = true;

    public float ImpactGain { get; set; } = 0.60f;

    public float SafetyClamp { get; set; } = 0.50f;

    public float DecayRate { get; set; } = 0.88f;

    public float TriggerThresholdG { get; set; } = 3.0f;

    public float MinSpeedKmh { get; set; } = 5.0f;

    public bool SafetyOverride { get; set; } = false;

    public bool IsCrashing => _impactForce != 0f;

    public float LastImpactSeverity => _lastSeverity;

    private float _impactForce;
    private float _impactDirection;
    private float _lastSeverity;
    private float _prevSpeedKmh;
    private float _prevAccGLong;
    private float _smAccGLong;

    public float Apply(float force, FfbRawData raw)
    {
        if (!Enabled) return force;

        float speed = raw.SpeedKmh;
        float accGLat = raw.AccG.Length > 0 ? raw.AccG[0] : 0f;
        float accGLong = raw.AccG.Length > 1 ? raw.AccG[1] : 0f;
        float accGVert = raw.AccG.Length > 2 ? raw.AccG[2] : 0f;

        _smAccGLong = _smAccGLong * 0.70f + accGLong * 0.30f;

        float speedDelta = _prevSpeedKmh - speed;
        if (_prevSpeedKmh < 1f) speedDelta = 0f;

        float totalG = MathF.Sqrt(accGLat * accGLat + accGLong * accGLong + accGVert * accGVert);
        float longImpactG = MathF.Abs(accGLong - _smAccGLong);
        float latImpactG = MathF.Abs(accGLat);

        bool isCrash = false;
        float severity = 0f;

        if (speed > MinSpeedKmh)
        {
            if (longImpactG > TriggerThresholdG)
            {
                severity = Math.Clamp(longImpactG / TriggerThresholdG - 1.0f, 0f, 1.0f);
                isCrash = true;
            }

            if (speedDelta > 50f && _prevSpeedKmh > 20f)
            {
                float speedSeverity = Math.Clamp((speedDelta - 50f) / 80f, 0f, 1.0f);
                severity = MathF.Max(severity, speedSeverity);
                isCrash = true;
            }

            if (totalG > TriggerThresholdG * 1.5f)
            {
                float totalSeverity = Math.Clamp((totalG / (TriggerThresholdG * 1.5f) - 1.0f) * 0.5f, 0f, 1.0f);
                severity = MathF.Max(severity, totalSeverity);
                isCrash = true;
            }
        }

        if (isCrash && MathF.Abs(_impactForce) < 0.05f && severity > 0.1f)
        {
            float direction;
            if (MathF.Abs(accGLat) > MathF.Abs(accGLong))
            {
                direction = -Math.Sign(accGLat);
            }
            else
            {
                direction = accGLong > 0
                    ? (raw.SteerAngle >= 0 ? -1f : 1f)
                    : (raw.SteerAngle >= 0 ? 1f : -1f);
            }

            _impactDirection = direction;
            _impactForce = severity * ImpactGain;
            _lastSeverity = severity;
        }

        float crashContribution = 0f;
        if (MathF.Abs(_impactForce) > 0.001f)
        {
            float maxForce = SafetyOverride ? 1.0f : SafetyClamp;
            crashContribution = _impactForce * _impactDirection;
            crashContribution = Math.Clamp(crashContribution, -maxForce, maxForce);

            _impactForce *= DecayRate;
            if (MathF.Abs(_impactForce) < 0.005f)
            {
                _impactForce = 0f;
                _impactDirection = 0f;
                _lastSeverity = 0f;
            }
        }
        else
        {
            _lastSeverity *= 0.95f;
        }

        _prevSpeedKmh = speed;
        _prevAccGLong = accGLong;

        return force + crashContribution;
    }

    public void Reset()
    {
        _impactForce = 0f;
        _impactDirection = 0f;
        _lastSeverity = 0f;
        _prevSpeedKmh = 0f;
        _prevAccGLong = 0f;
        _smAccGLong = 0f;
    }
}
