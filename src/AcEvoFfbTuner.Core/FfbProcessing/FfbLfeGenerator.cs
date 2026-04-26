using AcEvoFfbTuner.Core.FfbProcessing.Models;

namespace AcEvoFfbTuner.Core.FfbProcessing;

public sealed class FfbLfeGenerator
{
    public bool Enabled { get; set; } = false;
    public float Gain { get; set; } = 0.5f;
    public float Frequency { get; set; } = 10.0f;
    public float SuspensionDrive { get; set; } = 0.6f;
    public float SpeedScaling { get; set; } = 0.5f;
    public float RpmDrive { get; set; } = 0.3f;

    private float _phase;
    private float[] _prevSuspTravel = new float[4];
    private float _suspEnvelope;
    private const float TickSeconds = 1f / 333f;

    public float LfeOutput { get; private set; }

    public float Generate(FfbRawData raw)
    {
        if (!Enabled || raw.SpeedKmh < 2.0f)
        {
            _phase = 0f;
            _suspEnvelope = 0f;
            LfeOutput = 0f;
            return 0f;
        }

        float speedFade = raw.SpeedKmh < 15.0f
            ? (raw.SpeedKmh - 2.0f) / 13.0f
            : 1.0f;

        float speedScale = Math.Clamp(raw.SpeedKmh / 150f, 0.1f, 1.5f) * SpeedScaling
            + (1.0f - SpeedScaling);

        float suspDelta = 0f;
        for (int i = 0; i < 4; i++)
        {
            float delta = raw.SuspensionTravel[i] - _prevSuspTravel[i];
            _prevSuspTravel[i] = raw.SuspensionTravel[i];
            float weight = i < 2 ? 1.2f : 0.8f;
            suspDelta += MathF.Abs(delta) * weight;
        }

        float suspEnvelopeTarget = Math.Clamp(suspDelta * 50f * SuspensionDrive, 0f, 1f);
        float attackSpeed = suspEnvelopeTarget > _suspEnvelope ? 0.6f : 0.15f;
        _suspEnvelope = _suspEnvelope * (1f - attackSpeed) + suspEnvelopeTarget * attackSpeed;

        float rpmNorm = Math.Clamp(raw.RpmPercent / 100f, 0f, 1f);
        float rpmEnvelope = rpmNorm * RpmDrive;

        float baseEnvelope = 0.15f + 0.85f * _suspEnvelope + rpmEnvelope;
        baseEnvelope = Math.Clamp(baseEnvelope, 0f, 1.5f);

        float rpmFreqBoost = 1f + rpmNorm * RpmDrive * 0.8f;
        float effectiveFreq = Frequency * speedScale * rpmFreqBoost;
        _phase += effectiveFreq * TickSeconds;
        if (_phase > 1f) _phase -= 1f;

        float sine = MathF.Sin(_phase * MathF.PI * 2f);
        float harmonic = MathF.Sin(_phase * MathF.PI * 4f) * 0.3f;
        float waveform = sine + harmonic;

        float output = waveform * baseEnvelope * speedFade * Gain;
        LfeOutput = Math.Clamp(output, -0.2f, 0.2f);

        return LfeOutput;
    }

    public void Reset()
    {
        _phase = 0f;
        _suspEnvelope = 0f;
        LfeOutput = 0f;
        Array.Clear(_prevSuspTravel);
    }
}
