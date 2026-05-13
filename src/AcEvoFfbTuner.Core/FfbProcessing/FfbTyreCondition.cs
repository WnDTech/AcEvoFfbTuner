using AcEvoFfbTuner.Core.FfbProcessing.Models;

namespace AcEvoFfbTuner.Core.FfbProcessing;

public sealed class FfbTyreCondition
{
    public bool Enabled { get; set; } = true;

    public float BlowoutVibrationGain { get; set; } = 0.40f;

    public float PressureLossGain { get; set; } = 0.20f;

    public float DamageAsymmetryGain { get; set; } = 0.15f;

    public float BlowoutPressureThreshold { get; set; } = 0.40f;

    public int BaselineLearnFrames { get; set; } = 300;

    public float MaxBlowoutAmplitude { get; set; } = 0.25f;

    private float[] _baselinePressure = new float[4];
    private int _baselineFrames;
    private bool _baselineEstablished;
    private bool[] _blownTyre = new bool[4];

    private float _blowoutPhase;
    private const float BlowoutBaseHz = 8f;
    private const float TickSeconds = 1f / 333f;

    private float _smBlowoutForce;
    private float _smPressureLossForce;
    private float _smDamageBias;

    public float Apply(float force, FfbRawData raw)
    {
        if (!Enabled || raw.SpeedKmh < 5f)
            return force;

        LearnBaseline(raw);

        if (!_baselineEstablished)
            return force;

        DetectBlowouts(raw);

        float blowoutForce = ComputeBlowoutVibration(raw);
        float pressureLoss = ComputePressureLossFeel(raw);
        float damageBias = ComputeDamageAsymmetry(raw);

        float combined = blowoutForce + pressureLoss + damageBias;

        return Math.Clamp(force + combined, -1.5f, 1.5f);
    }

    private void LearnBaseline(FfbRawData raw)
    {
        if (_baselineEstablished) return;

        bool anyNonZero = false;
        for (int i = 0; i < 4; i++)
        {
            if (raw.WheelsPressure[i] > 0.1f)
                anyNonZero = true;
        }

        if (!anyNonZero) return;

        for (int i = 0; i < 4; i++)
        {
            float p = raw.WheelsPressure[i];
            if (p > 0.1f)
            {
                if (_baselineFrames == 0)
                    _baselinePressure[i] = p;
                else
                    _baselinePressure[i] = _baselinePressure[i] * 0.95f + p * 0.05f;
            }
        }

        _baselineFrames++;
        if (_baselineFrames >= BaselineLearnFrames)
            _baselineEstablished = true;
    }

    private void DetectBlowouts(FfbRawData raw)
    {
        for (int i = 0; i < 4; i++)
        {
            if (_baselinePressure[i] < 0.1f) continue;

            float ratio = raw.WheelsPressure[i] / _baselinePressure[i];
            _blownTyre[i] = ratio < BlowoutPressureThreshold;
        }
    }

    private float ComputeBlowoutVibration(FfbRawData raw)
    {
        float frontBlowoutIntensity = 0f;
        int frontBlownSide = -1;

        for (int i = 0; i < 2; i++)
        {
            if (_blownTyre[i] && _baselinePressure[i] > 0.1f)
            {
                float ratio = raw.WheelsPressure[i] / _baselinePressure[i];
                float intensity = Math.Clamp(1.0f - ratio / BlowoutPressureThreshold, 0f, 1f);
                if (intensity > frontBlowoutIntensity)
                {
                    frontBlowoutIntensity = intensity;
                    frontBlownSide = i;
                }
            }
        }

        float speedFactor = Math.Clamp(raw.SpeedKmh / 80f, 0.1f, 1.5f);

        if (frontBlowoutIntensity > 0.01f)
        {
            float freq = BlowoutBaseHz * (0.5f + speedFactor);
            _blowoutPhase += freq * TickSeconds;
            if (_blowoutPhase > 1f) _blowoutPhase -= 1f;

            float primary = MathF.Sin(_blowoutPhase * MathF.PI * 2f);
            float secondary = MathF.Sin(_blowoutPhase * MathF.PI * 2f * 1.73f) * 0.4f;
            float sub = MathF.Sin(_blowoutPhase * MathF.PI * 2f * 0.5f) * 0.3f;

            float vibration = (primary + secondary + sub) / 1.7f;

            float amplitude = frontBlowoutIntensity * speedFactor * BlowoutVibrationGain * MaxBlowoutAmplitude;
            amplitude = Math.Min(amplitude, MaxBlowoutAmplitude);

            float targetForce = vibration * amplitude;

            _smBlowoutForce = _smBlowoutForce * 0.3f + targetForce * 0.7f;

            float pullBias = 0f;
            if (frontBlownSide == 0)
                pullBias = -frontBlowoutIntensity * 0.08f * speedFactor;
            else if (frontBlownSide == 1)
                pullBias = frontBlowoutIntensity * 0.08f * speedFactor;

            return _smBlowoutForce + pullBias;
        }

        _smBlowoutForce *= 0.85f;
        return _smBlowoutForce;
    }

    private float ComputePressureLossFeel(FfbRawData raw)
    {
        float maxLoss = 0f;
        for (int i = 0; i < 2; i++)
        {
            if (_baselinePressure[i] < 0.1f) continue;
            if (_blownTyre[i]) continue;

            float ratio = raw.WheelsPressure[i] / _baselinePressure[i];
            if (ratio < 0.85f)
            {
                float loss = (0.85f - ratio) / 0.45f;
                maxLoss = Math.Max(maxLoss, loss);
            }
        }

        float targetForce = maxLoss * PressureLossGain * 0.05f;
        _smPressureLossForce = _smPressureLossForce * 0.9f + targetForce * 0.1f;

        return _smPressureLossForce;
    }

    private float ComputeDamageAsymmetry(FfbRawData raw)
    {
        float leftDamage = raw.SuspensionDamage[0] + raw.SuspensionDamage[2];
        float rightDamage = raw.SuspensionDamage[1] + raw.SuspensionDamage[3];
        float asymmetry = (leftDamage - rightDamage) * 0.5f;

        float targetBias = asymmetry * DamageAsymmetryGain;
        _smDamageBias = _smDamageBias * 0.92f + targetBias * 0.08f;

        return _smDamageBias;
    }

    public void Reset()
    {
        Array.Clear(_baselinePressure);
        _baselineFrames = 0;
        _baselineEstablished = false;
        Array.Clear(_blownTyre);
        _blowoutPhase = 0f;
        _smBlowoutForce = 0f;
        _smPressureLossForce = 0f;
        _smDamageBias = 0f;
    }
}
