using AcEvoFfbTuner.Core.FfbProcessing;
using AcEvoFfbTuner.Core.FfbProcessing.Models;

namespace AcEvoFfbTuner.Core.DirectInput;

public sealed class Hf8SignalMapper
{
    public const int ZoneCount = 8;

    public enum Hf8Zone
    {
        SeatFrontRight = 0,
        SeatFrontLeft = 1,
        SeatRearRight = 2,
        SeatRearLeft = 3,
        BackLowerRight = 4,
        BackLowerLeft = 5,
        BackUpperRight = 6,
        BackUpperLeft = 7
    }

    public static readonly int[] PhysicalMotorToSdkIndex = [6, 7, 4, 5, 2, 3, 0, 1];

    public float MasterGain { get; set; } = 0.7f;
    public bool Enabled { get; set; } = true;

    public float[] ZoneGains { get; set; } = new float[ZoneCount]
    {
        0.8f, 0.8f, 0.8f, 0.8f, 0.7f, 0.7f, 0.5f, 0.5f
    };

    public bool[] ZoneEnabled { get; set; } = new bool[ZoneCount]
    {
        true, true, true, true, true, true, true, true
    };

    private float[] _prevSuspTravel = new float[4];
    private float[] _suspDelta = new float[4];

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
        float signedLateralG = raw.AccG.Length > 1 ? raw.AccG[1] : 0f;
        float rpmNorm = Math.Clamp(raw.RpmPercent / 100f, 0f, 1f);
        float absActive = raw.AbsVibrations > 0.001f || raw.AbsInAction != 0 ? 1f : 0f;
        float kerbVib = raw.KerbVibration;
        float lfeOut = MathF.Abs(lfeGenerator.LfeOutput);
        float absMod = MathF.Abs(vibrationMixer.AbsForceModulation);
        float roadMod = MathF.Abs(vibrationMixer.RoadForceModulation);
        float scrubMod = MathF.Abs(vibrationMixer.ScrubModulation);
        float rearSlipMod = MathF.Abs(vibrationMixer.RearSlipModulation);
        float slipVib = raw.SlipVibrations;

        float flSusp = Math.Clamp(suspDelta[0] * 80f, 0f, 0.6f);
        float flSlip = Math.Clamp(wheelSlip[0] * 5f, 0f, 0.4f);
        float frSusp = Math.Clamp(suspDelta[1] * 80f, 0f, 0.6f);
        float frSlip = Math.Clamp(wheelSlip[1] * 5f, 0f, 0.4f);
        float rlSusp = Math.Clamp(suspDelta[2] * 80f, 0f, 0.6f);
        float rlSlip = Math.Clamp(wheelSlip[2] * 5f, 0f, 0.4f);
        float rrSusp = Math.Clamp(suspDelta[3] * 80f, 0f, 0.6f);
        float rrSlip = Math.Clamp(wheelSlip[3] * 5f, 0f, 0.4f);

        float rpmVib = rpmNorm * 0.2f;
        float rpmLimiter = raw.IsRpmLimiterOn ? 0.6f : 0f;

        float leftSusp = flSusp + rlSusp;
        float rightSusp = frSusp + rrSusp;
        float totalSusp = leftSusp + rightSusp + 0.001f;
        float leftSuspRatio = leftSusp / totalSusp;
        float rightSuspRatio = rightSusp / totalSusp;

        float kerbLeft = kerbVib * leftSuspRatio;
        float kerbRight = kerbVib * rightSuspRatio;

        // AccG[1] > 0 = turn left = pushed RIGHT | AccG[1] < 0 = turn right = pushed LEFT
        float pushedRight = signedLateralG > 0.3f ? (signedLateralG - 0.3f) * 0.5f : 0f;
        float pushedLeft = signedLateralG < -0.3f ? (-signedLateralG - 0.3f) * 0.5f : 0f;

        // SDK[1] Seat Front Left (driver's left thigh) — FL primary + RL cross + kerb left + pushed left
        intensities[(int)Hf8Zone.SeatFrontLeft] = (flSusp * 1.2f + rlSusp * 0.4f + flSlip + kerbLeft * 0.3f + pushedLeft * 0.3f) * 0.8f;

        // SDK[0] Seat Front Right (driver's right thigh) — FR primary + RR cross + kerb right + pushed right
        intensities[(int)Hf8Zone.SeatFrontRight] = (frSusp * 1.2f + rrSusp * 0.4f + frSlip + kerbRight * 0.3f + pushedRight * 0.3f) * 0.8f;

        // SDK[3] Seat Rear Left (driver's left rear) — RL primary + FL cross + kerb left + pushed left
        intensities[(int)Hf8Zone.SeatRearLeft] = (rlSusp * 1.2f + flSusp * 0.4f + rlSlip + kerbLeft * 0.3f + pushedLeft * 0.3f) * 0.8f;

        // SDK[2] Seat Rear Right (driver's right rear) — RR primary + FR cross + kerb right + pushed right
        intensities[(int)Hf8Zone.SeatRearRight] = (rrSusp * 1.2f + frSusp * 0.4f + rrSlip + kerbRight * 0.3f + pushedRight * 0.3f) * 0.8f;

        // SDK[5] Back Lower Left (driver's left lower back) — road + ABS biased left
        intensities[(int)Hf8Zone.BackLowerLeft] = (roadMod * 2f * leftSuspRatio + absMod * 2f + rearSlipMod * 2f + absActive * 0.3f + slipVib * 0.2f) * 0.7f;

        // SDK[4] Back Lower Right (driver's right lower back) — road + ABS biased right
        intensities[(int)Hf8Zone.BackLowerRight] = (roadMod * 2f * rightSuspRatio + absMod * 2f + rearSlipMod * 2f + absActive * 0.3f + slipVib * 0.2f) * 0.7f;

        // SDK[7] Back Upper Left (driver's left upper back) — LFE + RPM + pushed left + scrub
        intensities[(int)Hf8Zone.BackUpperLeft] = (lfeOut * 1.5f + rpmVib + rpmLimiter + pushedLeft + scrubMod) * 0.5f;

        // SDK[6] Back Upper Right (driver's right upper back) — LFE + RPM + pushed right + scrub
        intensities[(int)Hf8Zone.BackUpperRight] = (lfeOut * 1.5f + rpmVib + rpmLimiter + pushedRight + scrubMod) * 0.5f;

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
