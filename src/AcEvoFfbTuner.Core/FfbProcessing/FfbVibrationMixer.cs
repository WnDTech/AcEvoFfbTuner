using AcEvoFfbTuner.Core.FfbProcessing.Models;

namespace AcEvoFfbTuner.Core.FfbProcessing;

public sealed class FfbVibrationMixer
{
    public float KerbGain { get; set; } = 1.0f;
    public float SlipGain { get; set; } = 0.8f;
    public float RoadGain { get; set; } = 0.5f;
    public float AbsGain { get; set; } = 1.0f;
    public float MasterGain { get; set; } = 0.7f;

    public float SuspensionRoadGain { get; set; } = 1.5f;

    public float RoadForceModulation { get; private set; }

    private float _absPhase;
    private const float AbsPulseHz = 15f;
    private const float TickSeconds = 1f / 333f;
    private const float CurbDeltaThreshold = 0.002f;

    public float AbsForceModulation { get; private set; }

    private float[] _prevSuspTravel = new float[4];
    private float _smSuspCurb;
    private float _smSuspRoad;

    public float Mix(FfbRawData raw)
    {
        if (raw.SpeedKmh < 2.0f)
        {
            _absPhase = 0f;
            AbsForceModulation = 0f;
            return 0f;
        }

        float kerb = raw.KerbVibration * KerbGain;
        float slip = raw.SlipVibrations * SlipGain;
        float road = raw.RoadVibrations * RoadGain;

        float abs;
        bool absActive = false;
        if (raw.AbsVibrations > 0.001f)
        {
            abs = raw.AbsVibrations * AbsGain;
            absActive = true;
        }
        else if (raw.AbsInAction != 0 && raw.BrakeInput > 0.1f)
        {
            absActive = true;
            abs = 0f;
        }
        else
        {
            abs = 0f;
            _absPhase = 0f;
        }

        if (absActive)
        {
            _absPhase += AbsPulseHz * TickSeconds;
            if (_absPhase > 1f) _absPhase -= 1f;
            float pulse = MathF.Sin(_absPhase * MathF.PI * 2f);
            float absAmp = (0.6f + 0.4f * raw.BrakeInput) * AbsGain;
            AbsForceModulation = absAmp * Math.Max(pulse, 0f);
            abs = Math.Max(abs, AbsForceModulation);
        }
        else
        {
            AbsForceModulation = 0f;
        }

        float combined = kerb + slip + road + abs;

        float speedFade = raw.SpeedKmh < 10.0f
            ? (raw.SpeedKmh - 2.0f) / 8.0f
            : 1.0f;

        float curbDelta = 0f;
        float roadDelta = 0f;
        for (int i = 0; i < 4; i++)
        {
            float delta = raw.SuspensionTravel[i] - _prevSuspTravel[i];
            _prevSuspTravel[i] = raw.SuspensionTravel[i];
            float weight = i < 2 ? 1.5f : 0.75f;
            if (MathF.Abs(delta) > CurbDeltaThreshold)
                curbDelta += delta * weight;
            else
                roadDelta += delta * weight;
        }

        _smSuspCurb = _smSuspCurb * 0.3f + curbDelta * 0.7f;
        _smSuspRoad = _smSuspRoad * 0.3f + roadDelta * 0.7f;

        float suspSpeedScale = Math.Clamp(raw.SpeedKmh / 100f, 0f, 2f);
        float curbForce = _smSuspCurb * 75f * MathF.Max(KerbGain, 0.1f) * suspSpeedScale;
        float roadForce = _smSuspRoad * 150f * MathF.Max(RoadGain, 0.1f) * suspSpeedScale;
        float rawVib = (curbForce + roadForce) * SuspensionRoadGain;
        RoadForceModulation = Math.Clamp(rawVib, -0.15f, 0.15f);

        return Math.Clamp(combined * MasterGain * speedFade, 0f, 1f);
    }

    public void Reset()
    {
        _absPhase = 0f;
        AbsForceModulation = 0f;
        RoadForceModulation = 0f;
        _smSuspCurb = 0f;
        _smSuspRoad = 0f;
        Array.Clear(_prevSuspTravel);
    }
}
