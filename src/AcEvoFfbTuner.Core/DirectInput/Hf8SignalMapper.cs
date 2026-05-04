using AcEvoFfbTuner.Core.FfbProcessing;
using AcEvoFfbTuner.Core.FfbProcessing.Models;

namespace AcEvoFfbTuner.Core.DirectInput;

public sealed class Hf8SignalMapper
{
    public const int ZoneCount = 8;

    public enum Hf8Zone
    {
        FrontLeft = 0,
        FrontRight = 1,
        RearLeft = 2,
        RearRight = 3,
        SeatLeft = 4,
        SeatRight = 5,
        BackUpper = 6,
        BackLower = 7
    }

    public float MasterGain { get; set; } = 0.7f;
    public bool Enabled { get; set; } = true;

    public float[] ZoneGains { get; set; } = new float[ZoneCount]
    {
        0.8f, 0.8f, 0.8f, 0.8f, 0.6f, 0.6f, 0.5f, 0.7f
    };

    public bool[] ZoneEnabled { get; set; } = new bool[ZoneCount]
    {
        true, true, true, true, true, true, true, true
    };

    private float[] _prevSuspTravel = new float[4];
    private float[] _suspDelta = new float[4];
    private const float TickSeconds = 1f / 333f;

    public float[] Map(
        FfbRawData raw,
        FfbProcessedData processed,
        FfbVibrationMixer vibrationMixer,
        FfbLfeGenerator lfeGenerator)
    {
        var intensities = new float[ZoneCount];

        if (!Enabled || raw.SpeedKmh < 1.0f)
            return intensities;

        float speedFade = raw.SpeedKmh < 10.0f
            ? (raw.SpeedKmh - 1.0f) / 9.0f
            : 1.0f;

        float[] suspDelta = ComputeSuspensionDelta(raw);
        float[] wheelSlip = ComputeWheelSlip(raw);
        float lateralG = raw.AccG.Length > 1 ? MathF.Abs(raw.AccG[1]) : 0f;
        float rpmNorm = Math.Clamp(raw.RpmPercent / 100f, 0f, 1f);
        float absActive = raw.AbsVibrations > 0.001f || raw.AbsInAction != 0 ? 1f : 0f;
        float kerbVib = raw.KerbVibration;
        float slipVib = raw.SlipVibrations;
        float roadVib = raw.RoadVibrations;
        float lfeOut = MathF.Abs(lfeGenerator.LfeOutput);
        float absMod = MathF.Abs(vibrationMixer.AbsForceModulation);
        float roadMod = MathF.Abs(vibrationMixer.RoadForceModulation);
        float scrubMod = MathF.Abs(vibrationMixer.ScrubModulation);
        float rearSlipMod = MathF.Abs(vibrationMixer.RearSlipModulation);

        float frontSlipAngle = (MathF.Abs(raw.SlipAngle[0]) + MathF.Abs(raw.SlipAngle[1])) * 0.5f;
        float rearSlipAngle = (MathF.Abs(raw.SlipAngle[2]) + MathF.Abs(raw.SlipAngle[3])) * 0.5f;

        // Zone 0: Front Left — FL suspension delta + FL slip ratio + kerb impacts
        float flSusp = Math.Clamp(suspDelta[0] * 80f, 0f, 0.6f);
        float flSlip = Math.Clamp(wheelSlip[0] * 5f, 0f, 0.4f);
        intensities[(int)Hf8Zone.FrontLeft] = (flSusp + flSlip + kerbVib * 0.3f) * 0.8f;

        // Zone 1: Front Right — FR suspension delta + FR slip ratio + kerb impacts
        float frSusp = Math.Clamp(suspDelta[1] * 80f, 0f, 0.6f);
        float frSlip = Math.Clamp(wheelSlip[1] * 5f, 0f, 0.4f);
        intensities[(int)Hf8Zone.FrontRight] = (frSusp + frSlip + kerbVib * 0.3f) * 0.8f;

        // Zone 2: Rear Left — RL suspension delta + RL slip ratio
        float rlSusp = Math.Clamp(suspDelta[2] * 80f, 0f, 0.6f);
        float rlSlip = Math.Clamp(wheelSlip[2] * 5f, 0f, 0.4f);
        intensities[(int)Hf8Zone.RearLeft] = (rlSusp + rlSlip) * 0.8f;

        // Zone 3: Rear Right — RR suspension delta + RR slip ratio
        float rrSusp = Math.Clamp(suspDelta[3] * 80f, 0f, 0.6f);
        float rrSlip = Math.Clamp(wheelSlip[3] * 5f, 0f, 0.4f);
        intensities[(int)Hf8Zone.RearRight] = (rrSusp + rrSlip) * 0.8f;

        // Zone 4: Seat Left — LFE + lateral G (left-biased)
        float lateralBiasL = lateralG > 0.3f ? (lateralG - 0.3f) * 0.5f : 0f;
        intensities[(int)Hf8Zone.SeatLeft] = (lfeOut * 1.5f + lateralBiasL + slipVib * 0.2f) * 0.6f;

        // Zone 5: Seat Right — LFE + lateral G (right-biased)
        float lateralBiasR = lateralG > 0.3f ? (lateralG - 0.3f) * 0.5f : 0f;
        intensities[(int)Hf8Zone.SeatRight] = (lfeOut * 1.5f + lateralBiasR + slipVib * 0.2f) * 0.6f;

        // Zone 6: Back Upper — RPM envelope + rear slip warning + scrub
        float rpmVib = rpmNorm * 0.2f;
        float rpmLimiter = raw.IsRpmLimiterOn ? 0.6f : 0f;
        intensities[(int)Hf8Zone.BackUpper] = (rpmVib + rpmLimiter + rearSlipMod * 2f + scrubMod * 1.5f) * 0.5f;

        // Zone 7: Back Lower — Road vibration + ABS + LFE
        intensities[(int)Hf8Zone.BackLower] = (roadMod * 3f + absMod * 2f + lfeOut + roadVib * 0.3f + absActive * 0.3f) * 0.7f;

        for (int i = 0; i < ZoneCount; i++)
        {
            if (!ZoneEnabled[i])
                intensities[i] = 0f;
            else
                intensities[i] = Math.Clamp(intensities[i] * ZoneGains[i] * MasterGain * speedFade, 0f, 1f);
        }

        return intensities;
    }

    private float[] ComputeSuspensionDelta(FfbRawData raw)
    {
        for (int i = 0; i < 4; i++)
        {
            float delta = raw.SuspensionTravel[i] - _prevSuspTravel[i];
            _prevSuspTravel[i] = raw.SuspensionTravel[i];
            _suspDelta[i] = _suspDelta[i] * 0.5f + MathF.Abs(delta) * 0.5f;
        }
        return _suspDelta;
    }

    private static float[] ComputeWheelSlip(FfbRawData raw)
    {
        var slip = new float[4];
        for (int i = 0; i < 4; i++)
        {
            float sr = MathF.Abs(raw.SlipRatio[i]);
            float sa = MathF.Abs(raw.SlipAngle[i]);
            slip[i] = Math.Clamp(sr + sa * 0.5f, 0f, 1f);
        }
        return slip;
    }

    public void Reset()
    {
        Array.Clear(_prevSuspTravel);
        Array.Clear(_suspDelta);
    }
}
