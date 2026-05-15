using AcEvoFfbTuner.Core.FfbProcessing.Models;

namespace AcEvoFfbTuner.Core.FfbProcessing;

public sealed class FfbWetWeather
{
    public bool Enabled { get; set; } = false;

    public bool AutoDetect { get; set; } = true;

    public float ManualIntensity { get; set; } = 1.0f;

    public float RoadVibSuppression { get; set; } = 0.70f;

    public float CurbSuppression { get; set; } = 0.40f;

    public float ScrubSuppression { get; set; } = 0.25f;

    public float PeakSlipAngleMultiplier { get; set; } = 1.60f;

    public float DampingReduction { get; set; } = 0.30f;

    public float NoiseFloorSuppression { get; set; } = 0.50f;

    public bool HydroplaningEnabled { get; set; } = true;

    public float HydroplaningSpeedThreshold { get; set; } = 120f;

    public float HydroplaningMaxAttenuation { get; set; } = 0.30f;

    public float WetnessFactor { get; private set; }

    public float RoadVibScale => 1.0f - WetnessFactor * RoadVibSuppression;
    public float CurbScale => 1.0f - WetnessFactor * CurbSuppression;
    public float ScrubScale => 1.0f - WetnessFactor * ScrubSuppression;
    public float DampingScale => 1.0f - WetnessFactor * DampingReduction;
    public float CurrentPeakSlipAngleScale => 1.0f + WetnessFactor * (PeakSlipAngleMultiplier - 1.0f);
    public float NoiseFloorScale => 1.0f + WetnessFactor * NoiseFloorSuppression;

    private float _smWetness;
    private float _smForceLoadRatio;
    private float _baselineForceLoadRatio;
    private int _baselineFrames;
    private bool _baselineEstablished;

    private float _smHydroAttenuation;

    private const float TickSeconds = 1f / 333f;
    private float _hydroPhase;

    public void Update(FfbRawData raw)
    {
        if (!Enabled || raw.SpeedKmh < 5f)
        {
            _smWetness *= 0.95f;
            WetnessFactor = Math.Clamp(_smWetness, 0f, 1f);
            return;
        }

        float targetWetness;

        if (AutoDetect)
        {
            targetWetness = DetectWetness(raw);
        }
        else
        {
            targetWetness = ManualIntensity;
        }

        _smWetness = _smWetness * 0.995f + targetWetness * 0.005f;
        WetnessFactor = Math.Clamp(_smWetness, 0f, 1f);
    }

    private float DetectWetness(FfbRawData raw)
    {
        float frontLoad = raw.WheelLoad[0] + raw.WheelLoad[1];
        float frontMz = MathF.Abs(raw.Mz[0]) + MathF.Abs(raw.Mz[1]);

        if (frontLoad < 500f)
        {
            return _smWetness;
        }

        float currentRatio = frontMz / frontLoad;

        if (!_baselineEstablished && raw.SpeedKmh > 30f && raw.SpeedKmh < 150f)
        {
            _baselineForceLoadRatio = _baselineForceLoadRatio * 0.995f + currentRatio * 0.005f;
            _baselineFrames++;
            if (_baselineFrames >= 600)
                _baselineEstablished = true;
            return 0f;
        }

        if (!_baselineEstablished)
            return 0f;

        _smForceLoadRatio = _smForceLoadRatio * 0.97f + currentRatio * 0.03f;

        if (_baselineForceLoadRatio < 0.0001f)
            return _smWetness;

        float ratioDrop = 1.0f - (_smForceLoadRatio / _baselineForceLoadRatio);
        float detectedWetness = Math.Clamp(ratioDrop * 3.0f, 0f, 1f);

        float dirtyLevel = 0f;
        if (raw.TyreDirtyLevel != null)
        {
            for (int i = 0; i < Math.Min(raw.TyreDirtyLevel.Length, 4); i++)
                dirtyLevel = Math.Max(dirtyLevel, raw.TyreDirtyLevel[i]);
        }
        float dirtyBoost = Math.Clamp(dirtyLevel * 0.5f, 0f, 0.3f);

        float tempFactor = 0f;
        if (raw.RoadTemp > 0.1f && raw.RoadTemp < 40f)
        {
            if (raw.RoadTemp < 15f)
                tempFactor = (15f - raw.RoadTemp) / 15f * 0.2f;
        }

        return Math.Clamp(detectedWetness + dirtyBoost + tempFactor, 0f, 1f);
    }

    public float ApplyHydroplaning(float force, FfbRawData raw)
    {
        if (!Enabled || !HydroplaningEnabled || WetnessFactor < 0.1f || raw.SpeedKmh < 60f)
            return force;

        float speedFactor = Math.Clamp(
            (raw.SpeedKmh - HydroplaningSpeedThreshold * 0.5f) / (HydroplaningSpeedThreshold * 0.5f),
            0f, 1.5f);

        float loadVariation = ComputeLoadVariation(raw);

        float hydroRisk = speedFactor * WetnessFactor * Math.Clamp(loadVariation * 2.5f, 0f, 1f);
        hydroRisk = Math.Clamp(hydroRisk, 0f, 1f);

        _hydroPhase += (3f + hydroRisk * 8f) * TickSeconds;
        if (_hydroPhase > 1f) _hydroPhase -= 1f;

        float wobble = MathF.Sin(_hydroPhase * MathF.PI * 2f) * 0.3f +
                        MathF.Sin(_hydroPhase * MathF.PI * 2f * 2.17f) * 0.2f;

        float targetAttenuation = hydroRisk * HydroplaningMaxAttenuation;
        _smHydroAttenuation = _smHydroAttenuation * 0.90f + targetAttenuation * 0.10f;

        float hydroSign = Math.Abs(force) > 0.01f
            ? Math.Sign(force)
            : (Math.Abs(raw.SteerAngle) > 0.002f ? -Math.Sign(raw.SteerAngle) : 1f);
        float hydroForce = wobble * _smHydroAttenuation * 0.15f * hydroSign;
        float attenuated = force * (1.0f - _smHydroAttenuation);

        return attenuated + hydroForce;
    }

    private static float ComputeLoadVariation(FfbRawData raw)
    {
        float avgLoad = 0f;
        for (int i = 0; i < 4; i++) avgLoad += raw.WheelLoad[i];
        avgLoad /= 4f;

        if (avgLoad < 100f) return 0f;

        float variance = 0f;
        for (int i = 0; i < 4; i++)
        {
            float d = (raw.WheelLoad[i] - avgLoad) / avgLoad;
            variance += d * d;
        }
        return MathF.Sqrt(variance / 4f);
    }

    public void Reset()
    {
        WetnessFactor = 0f;
        _smWetness = 0f;
        _smForceLoadRatio = 0f;
        _baselineForceLoadRatio = 0f;
        _baselineFrames = 0;
        _baselineEstablished = false;
        _smHydroAttenuation = 0f;
        _hydroPhase = 0f;
    }
}
