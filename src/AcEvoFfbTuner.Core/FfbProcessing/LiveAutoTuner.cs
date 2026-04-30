using AcEvoFfbTuner.Core.TrackMapping;

namespace AcEvoFfbTuner.Core.FfbProcessing;

public sealed class LiveAutoTuner
{
    private FfbPipeline? _pipeline;
    private float _torqueNm;
    private DateTime _lastCorrectionTime = DateTime.MinValue;
    private readonly List<string> _correctionLog = new();

    private const float ForceFloor = 0.35f;
    private const float HystCeiling = 0.04f;
    private const float SuspRoadFloor = 0.2f;
    private const float SpeedDampCeiling = 2.0f;
    private const float FrictionCeiling = 1.0f;
    private const float CompPowerCeiling = 2.5f;
    private const float CenterSuppCeiling = 2.0f;  // Physics Mz preserved; cap suppression
    private const float NoiseFloorCeiling = 0.04f;

    private const int TickWindowSize = 40;
    private const int MinCorrectionsToSlowDown = 4;

    private int _tickSampleCount;
    private float _absForceSum;
    private float _peakAbsForce;
    private float _maxDelta;
    private float _prevForce;
    private int _clippingCount;

    private float _prevSteerAngle;
    private int _centerTicks;
    private int _centerForceTicks;
    private float _centerForceSum;
    private float _steerVelocitySum;
    private bool _steerInitialized;

    public bool Enabled { get; set; }
    public int CorrectionsApplied { get; private set; }
    public string LastCorrection { get; private set; } = "";
    public IReadOnlyList<string> CorrectionLog => _correctionLog;

    public event Action<string, float>? CorrectionApplied;

    public void Configure(FfbPipeline pipeline, float torqueNm)
    {
        _pipeline = pipeline;
        _torqueNm = torqueNm;
    }

    public void OnTick(float outputForce, bool isClipping, float speedKmh, float steerAngle)
    {
        if (!Enabled || _pipeline == null) return;
        if (speedKmh < 15f) return;

        _tickSampleCount++;

        float absForce = MathF.Abs(outputForce);
        _absForceSum += absForce;
        if (absForce > _peakAbsForce) _peakAbsForce = absForce;
        if (isClipping) _clippingCount++;

        float forceDelta = MathF.Abs(outputForce - _prevForce);
        if (forceDelta > _maxDelta) _maxDelta = forceDelta;
        _prevForce = outputForce;

        bool nearCenter = MathF.Abs(steerAngle) < 0.03f;
        if (nearCenter)
        {
            _centerTicks++;
            if (absForce > 0.02f)
            {
                _centerForceTicks++;
                _centerForceSum += absForce;
            }
        }

        if (_steerInitialized)
        {
            float steerVel = MathF.Abs(steerAngle - _prevSteerAngle);
            _steerVelocitySum += steerVel;
        }
        _prevSteerAngle = steerAngle;
        _steerInitialized = true;

        if (_tickSampleCount < TickWindowSize) return;

        float avgForce = _absForceSum / _tickSampleCount;
        float clipRate = (float)_clippingCount / _tickSampleCount;
        float avgSteerVel = _steerVelocitySum / _tickSampleCount;
        float centerForceRatio = _centerTicks > 5 ? (float)_centerForceTicks / _centerTicks : 0f;
        float avgCenterForce = _centerForceTicks > 0 ? _centerForceSum / _centerForceTicks : 0f;

        TryTickCorrections(avgForce, _peakAbsForce, _maxDelta, clipRate, speedKmh,
            centerForceRatio, avgCenterForce, avgSteerVel);

        _tickSampleCount = 0;
        _absForceSum = 0f;
        _peakAbsForce = 0f;
        _maxDelta = 0f;
        _clippingCount = 0;
        _centerTicks = 0;
        _centerForceTicks = 0;
        _centerForceSum = 0f;
        _steerVelocitySum = 0f;
    }

    private void TryTickCorrections(float avgForce, float peakForce, float maxDelta, float clipRate,
        float speedKmh, float centerForceRatio, float avgCenterForce, float avgSteerVel)
    {
        var now = DateTime.UtcNow;
        float cooldownSeconds = CorrectionsApplied > MinCorrectionsToSlowDown ? 30f : 10f;
        if ((now - _lastCorrectionTime).TotalSeconds < cooldownSeconds) return;

        if (centerForceRatio > 0.30f && avgCenterForce > 0.04f && _centerTicks > 12)
        {
            CorrectCenterOscillation(centerForceRatio, avgCenterForce, speedKmh);
            return;
        }

        if (clipRate > 0.10f)
        {
            float currentGain = _pipeline!.OutputGain;
            float reducedGain = currentGain * 0.93f;
            if (reducedGain >= ForceFloor)
            {
                _pipeline.OutputGain = reducedGain;
                LogCorrection("OutputGain", currentGain, reducedGain, $"Clipping {clipRate:P0}");
            }

            float currentComp = _pipeline!.CompressionPower;
            float increasedComp = Math.Min(currentComp + 0.05f, CompPowerCeiling);
            _pipeline.CompressionPower = increasedComp;
            LogCorrection("CompressionPower", currentComp, increasedComp, $"Clipping compress");
            return;
        }

        if (avgForce > 0.55f && peakForce > 0.80f)
        {
            float aggressiveness = Math.Clamp((avgForce - 0.55f) / 0.20f, 0f, 1f);
            float factor = 0.97f - aggressiveness * 0.03f;
            float current = _pipeline!.OutputGain;
            float reduced = current * factor;
            if (reduced >= ForceFloor)
            {
                _pipeline.OutputGain = reduced;
                LogCorrection("OutputGain", current, reduced, $"Force high avg={avgForce:F3} peak={peakForce:F3}");
            }
            return;
        }

        if (maxDelta > 0.08f && speedKmh > 30f)
        {
            float currentFriction = _pipeline!.Damping.FrictionLevel;
            float increasedFriction = Math.Min(currentFriction * 1.08f, FrictionCeiling);
            _pipeline.Damping.FrictionLevel = increasedFriction;
            LogCorrection("Friction", currentFriction, increasedFriction, $"Snap delta={maxDelta:F3}");

            float currentDamp = _pipeline.Damping.SpeedDampingCoefficient;
            float increasedDamp = Math.Min(currentDamp * 1.05f, SpeedDampCeiling);
            _pipeline.Damping.SpeedDampingCoefficient = increasedDamp;
            LogCorrection("SpeedDamping", currentDamp, increasedDamp, $"Snap damp");
            return;
        }

        if (avgSteerVel > 0.008f && speedKmh > 40f)
        {
            float currentFriction = _pipeline!.Damping.FrictionLevel;
            float increasedFriction = Math.Min(currentFriction * 1.10f, FrictionCeiling);
            _pipeline.Damping.FrictionLevel = increasedFriction;
            LogCorrection("Friction", currentFriction, increasedFriction, $"Steer jitter vel={avgSteerVel:F4}");

            float currentNoise = _pipeline!.NoiseFloor;
            float increasedNoise = Math.Min(currentNoise + 0.001f, NoiseFloorCeiling);
            _pipeline.NoiseFloor = increasedNoise;
            LogCorrection("NoiseFloor", currentNoise, increasedNoise, $"Steer jitter noise");
            return;
        }
    }

    private void CorrectCenterOscillation(float centerForceRatio, float avgCenterForce, float speedKmh)
    {
        float currentCenterSupp = _pipeline!.CenterSuppressionDegrees;
        float increasedCenterSupp = Math.Min(currentCenterSupp + 0.2f, CenterSuppCeiling);
        _pipeline.CenterSuppressionDegrees = increasedCenterSupp;
        LogCorrection("CenterSuppressionDegrees", currentCenterSupp, increasedCenterSupp,
            $"Center osc {centerForceRatio:P0} active avgF={avgCenterForce:F3}");

        float currentNoise = _pipeline!.NoiseFloor;
        float increasedNoise = Math.Min(currentNoise + 0.001f, NoiseFloorCeiling);
        _pipeline.NoiseFloor = increasedNoise;
        LogCorrection("NoiseFloor", currentNoise, increasedNoise, $"Center osc noise");

        float currentHyst = _pipeline!.HysteresisThreshold;
        float increasedHyst = Math.Min(currentHyst + 0.002f, HystCeiling);
        _pipeline.HysteresisThreshold = increasedHyst;
        LogCorrection("HysteresisThreshold", currentHyst, increasedHyst, $"Center osc hyst");

        float currentGain = _pipeline.OutputGain;
        float reducedGain = currentGain * 0.97f;
        if (reducedGain >= ForceFloor)
        {
            _pipeline.OutputGain = reducedGain;
            LogCorrection("OutputGain", currentGain, reducedGain, $"Center osc gain reduce");
        }
    }

    public void OnLapSummary(DiagnosticLapSummary summary)
    {
        if (!Enabled || _pipeline == null || summary == null) return;
        if (summary.TotalEvents == 0) return;

        var now = DateTime.UtcNow;
        if ((now - _lastCorrectionTime).TotalSeconds < 20) return;

        if (summary.TotalOscillations >= 3)
        {
            float currentDamp = _pipeline!.Damping.SpeedDampingCoefficient;
            float increasedDamp = Math.Min(currentDamp * 1.08f, SpeedDampCeiling);
            _pipeline.Damping.SpeedDampingCoefficient = increasedDamp;
            LogCorrection("SpeedDamping", currentDamp, increasedDamp, $"Lap osc x{summary.TotalOscillations}");

            float currentFriction = _pipeline.Damping.FrictionLevel;
            float increasedFriction = Math.Min(currentFriction * 1.05f, FrictionCeiling);
            _pipeline.Damping.FrictionLevel = increasedFriction;
            LogCorrection("Friction", currentFriction, increasedFriction, $"Lap osc friction");

            float currentCenterSupp = _pipeline.CenterSuppressionDegrees;
            float increasedCenterSupp = Math.Min(currentCenterSupp + 0.2f, CenterSuppCeiling);
            _pipeline.CenterSuppressionDegrees = increasedCenterSupp;
            LogCorrection("CenterSuppressionDegrees", currentCenterSupp, increasedCenterSupp, $"Lap osc center");
            return;
        }

        if (summary.SuspiciousSnapsOnStraight >= 3)
        {
            float currentHyst = _pipeline!.HysteresisThreshold;
            float increasedHyst = Math.Min(currentHyst + 0.002f, HystCeiling);
            _pipeline.HysteresisThreshold = increasedHyst;
            LogCorrection("HysteresisThreshold", currentHyst, increasedHyst, $"Straight snaps x{summary.SuspiciousSnapsOnStraight}");

            float currentFriction = _pipeline.Damping.FrictionLevel;
            float increasedFriction = Math.Min(currentFriction * 1.05f, FrictionCeiling);
            _pipeline.Damping.FrictionLevel = increasedFriction;
            LogCorrection("Friction", currentFriction, increasedFriction, $"Straight snap friction");
            return;
        }

        if (summary.SuspiciousRoadVibrationSnaps >= 2)
        {
            float current = _pipeline!.VibrationMixer.SuspensionRoadGain;
            float reduced = current * 0.93f;
            if (reduced >= SuspRoadFloor)
            {
                _pipeline.VibrationMixer.SuspensionRoadGain = reduced;
                LogCorrection("SuspensionRoadGain", current, reduced, $"Road-vib x{summary.SuspiciousRoadVibrationSnaps}");
            }
            return;
        }

        float clipPct = summary.TotalEvents > 0
            ? (float)summary.TotalClippingEvents / summary.TotalEvents * 100f
            : 0f;
        if (clipPct >= 10f)
        {
            float currentGain = _pipeline!.OutputGain;
            float reducedGain = currentGain * 0.95f;
            if (reducedGain >= ForceFloor)
            {
                _pipeline.OutputGain = reducedGain;
                LogCorrection("OutputGain", currentGain, reducedGain, $"Lap clip {clipPct:F0}%");
            }

            float currentComp = _pipeline.CompressionPower;
            float increasedComp = Math.Min(currentComp + 0.05f, CompPowerCeiling);
            _pipeline.CompressionPower = increasedComp;
            LogCorrection("CompressionPower", currentComp, increasedComp, $"Lap clip compress");
        }
    }

    private void LogCorrection(string parameter, float oldValue, float newValue, string reason)
    {
        _lastCorrectionTime = DateTime.UtcNow;
        CorrectionsApplied++;
        string direction = newValue > oldValue ? "↑" : "↓";
        string entry = $"#{CorrectionsApplied} {parameter} {direction} {oldValue:F4}→{newValue:F4} ({reason})";
        LastCorrection = entry;
        _correctionLog.Add($"[{DateTime.UtcNow:HH:mm:ss}] {entry}");
        CorrectionApplied?.Invoke(parameter, newValue);
    }

    public void Reset()
    {
        CorrectionsApplied = 0;
        LastCorrection = "";
        _correctionLog.Clear();
        _lastCorrectionTime = DateTime.MinValue;
        _tickSampleCount = 0;
        _absForceSum = 0f;
        _peakAbsForce = 0f;
        _maxDelta = 0f;
        _prevForce = 0f;
        _clippingCount = 0;
        _centerTicks = 0;
        _centerForceTicks = 0;
        _centerForceSum = 0f;
        _steerVelocitySum = 0f;
        _prevSteerAngle = 0f;
        _steerInitialized = false;
    }
}
