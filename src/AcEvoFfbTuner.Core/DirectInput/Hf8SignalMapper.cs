using AcEvoFfbTuner.Core.FfbProcessing;
using AcEvoFfbTuner.Core.FfbProcessing.Models;

namespace AcEvoFfbTuner.Core.DirectInput;

public sealed class Hf8SignalMapper
{
    public const int ZoneCount = 8;
    public const int SourceCount = 5;

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
    };

    public enum Hf8Source
    {
        Suspension = 0,
        Slip = 1,
        Kerb = 2,
        LateralG = 3,
        Engine = 4
    };

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

    public float[,] ZoneSourceWeights { get; set; } = CreateDefaultSourceWeights();

    public static float[,] CreateDefaultSourceWeights()
    {
        var w = new float[ZoneCount, SourceCount];

        w[(int)Hf8Zone.SeatFrontRight, (int)Hf8Source.Suspension] = 1.2f;
        w[(int)Hf8Zone.SeatFrontRight, (int)Hf8Source.Slip] = 1.0f;
        w[(int)Hf8Zone.SeatFrontRight, (int)Hf8Source.Kerb] = 0.3f;
        w[(int)Hf8Zone.SeatFrontRight, (int)Hf8Source.LateralG] = 0.3f;
        w[(int)Hf8Zone.SeatFrontRight, (int)Hf8Source.Engine] = 0.0f;

        w[(int)Hf8Zone.SeatFrontLeft, (int)Hf8Source.Suspension] = 1.2f;
        w[(int)Hf8Zone.SeatFrontLeft, (int)Hf8Source.Slip] = 1.0f;
        w[(int)Hf8Zone.SeatFrontLeft, (int)Hf8Source.Kerb] = 0.3f;
        w[(int)Hf8Zone.SeatFrontLeft, (int)Hf8Source.LateralG] = 0.3f;
        w[(int)Hf8Zone.SeatFrontLeft, (int)Hf8Source.Engine] = 0.0f;

        w[(int)Hf8Zone.SeatRearRight, (int)Hf8Source.Suspension] = 1.2f;
        w[(int)Hf8Zone.SeatRearRight, (int)Hf8Source.Slip] = 1.0f;
        w[(int)Hf8Zone.SeatRearRight, (int)Hf8Source.Kerb] = 0.3f;
        w[(int)Hf8Zone.SeatRearRight, (int)Hf8Source.LateralG] = 0.3f;
        w[(int)Hf8Zone.SeatRearRight, (int)Hf8Source.Engine] = 0.0f;

        w[(int)Hf8Zone.SeatRearLeft, (int)Hf8Source.Suspension] = 1.2f;
        w[(int)Hf8Zone.SeatRearLeft, (int)Hf8Source.Slip] = 1.0f;
        w[(int)Hf8Zone.SeatRearLeft, (int)Hf8Source.Kerb] = 0.3f;
        w[(int)Hf8Zone.SeatRearLeft, (int)Hf8Source.LateralG] = 0.3f;
        w[(int)Hf8Zone.SeatRearLeft, (int)Hf8Source.Engine] = 0.0f;

        w[(int)Hf8Zone.BackLowerRight, (int)Hf8Source.Suspension] = 0.0f;
        w[(int)Hf8Zone.BackLowerRight, (int)Hf8Source.Slip] = 1.0f;
        w[(int)Hf8Zone.BackLowerRight, (int)Hf8Source.Kerb] = 1.0f;
        w[(int)Hf8Zone.BackLowerRight, (int)Hf8Source.LateralG] = 0.0f;
        w[(int)Hf8Zone.BackLowerRight, (int)Hf8Source.Engine] = 2.0f;

        w[(int)Hf8Zone.BackLowerLeft, (int)Hf8Source.Suspension] = 0.0f;
        w[(int)Hf8Zone.BackLowerLeft, (int)Hf8Source.Slip] = 1.0f;
        w[(int)Hf8Zone.BackLowerLeft, (int)Hf8Source.Kerb] = 1.0f;
        w[(int)Hf8Zone.BackLowerLeft, (int)Hf8Source.LateralG] = 0.0f;
        w[(int)Hf8Zone.BackLowerLeft, (int)Hf8Source.Engine] = 2.0f;

        w[(int)Hf8Zone.BackUpperRight, (int)Hf8Source.Suspension] = 0.0f;
        w[(int)Hf8Zone.BackUpperRight, (int)Hf8Source.Slip] = 1.0f;
        w[(int)Hf8Zone.BackUpperRight, (int)Hf8Source.Kerb] = 0.0f;
        w[(int)Hf8Zone.BackUpperRight, (int)Hf8Source.LateralG] = 1.0f;
        w[(int)Hf8Zone.BackUpperRight, (int)Hf8Source.Engine] = 1.0f;

        w[(int)Hf8Zone.BackUpperLeft, (int)Hf8Source.Suspension] = 0.0f;
        w[(int)Hf8Zone.BackUpperLeft, (int)Hf8Source.Slip] = 1.0f;
        w[(int)Hf8Zone.BackUpperLeft, (int)Hf8Source.Kerb] = 0.0f;
        w[(int)Hf8Zone.BackUpperLeft, (int)Hf8Source.LateralG] = 1.0f;
        w[(int)Hf8Zone.BackUpperLeft, (int)Hf8Source.Engine] = 1.0f;

        return w;
    }

    public float GetSourceWeight(int zone, int source)
    {
        if (zone < 0 || zone >= ZoneCount || source < 0 || source >= SourceCount) return 0f;
        return ZoneSourceWeights[zone, source];
    }

    public void SetSourceWeight(int zone, int source, float weight)
    {
        if (zone >= 0 && zone < ZoneCount && source >= 0 && source < SourceCount)
            ZoneSourceWeights[zone, source] = weight;
    }

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

        float leftSuspMix = flSusp + rlSusp * 0.4f;
        float rightSuspMix = frSusp + rrSusp * 0.4f;
        float leftSuspRearMix = rlSusp + flSusp * 0.4f;
        float rightSuspRearMix = rrSusp + frSusp * 0.4f;

        float leftLowerEngineMix = absMod + rearSlipMod;
        float rightLowerEngineMix = absMod + rearSlipMod;
        float leftUpperEngineMix = lfeOut * 1.5f + rpmVib + rpmLimiter;
        float rightUpperEngineMix = lfeOut * 1.5f + rpmVib + rpmLimiter;

        float[] zoneScale = [0.8f, 0.8f, 0.8f, 0.8f, 0.7f, 0.7f, 0.5f, 0.5f];

        float[,] engineSignals = new float[ZoneCount, 1];
        engineSignals[(int)Hf8Zone.SeatFrontLeft, 0] = 0f;
        engineSignals[(int)Hf8Zone.SeatFrontRight, 0] = 0f;
        engineSignals[(int)Hf8Zone.SeatRearLeft, 0] = 0f;
        engineSignals[(int)Hf8Zone.SeatRearRight, 0] = 0f;
        engineSignals[(int)Hf8Zone.BackLowerLeft, 0] = leftLowerEngineMix;
        engineSignals[(int)Hf8Zone.BackLowerRight, 0] = rightLowerEngineMix;
        engineSignals[(int)Hf8Zone.BackUpperLeft, 0] = leftUpperEngineMix;
        engineSignals[(int)Hf8Zone.BackUpperRight, 0] = rightUpperEngineMix;

        float[] suspSignals =
        [
            rightSuspMix, leftSuspMix, rightSuspRearMix, leftSuspRearMix,
            0f, 0f, 0f, 0f
        ];
        float[] slipSignals =
        [
            frSlip, flSlip, rrSlip, rlSlip,
            slipVib * 0.2f + absActive * 0.3f, slipVib * 0.2f + absActive * 0.3f, scrubMod, scrubMod
        ];
        float[] kerbSignals =
        [
            kerbRight, kerbLeft, kerbRight, kerbLeft,
            roadMod * leftSuspRatio * 2f, roadMod * rightSuspRatio * 2f, 0f, 0f
        ];
        float[] latSignals =
        [
            pushedRight, pushedLeft, pushedRight, pushedLeft,
            0f, 0f, pushedRight, pushedLeft
        ];

        for (int z = 0; z < ZoneCount; z++)
        {
            float suspW = ZoneSourceWeights[z, (int)Hf8Source.Suspension];
            float slipW = ZoneSourceWeights[z, (int)Hf8Source.Slip];
            float kerbW = ZoneSourceWeights[z, (int)Hf8Source.Kerb];
            float latW = ZoneSourceWeights[z, (int)Hf8Source.LateralG];
            float engW = ZoneSourceWeights[z, (int)Hf8Source.Engine];

            intensities[z] = (suspSignals[z] * suspW
                            + slipSignals[z] * slipW
                            + kerbSignals[z] * kerbW
                            + latSignals[z] * latW
                            + engineSignals[z, 0] * engW) * zoneScale[z];
        }

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
