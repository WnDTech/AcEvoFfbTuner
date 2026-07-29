using AcEvoFfbTuner.Core.FfbProcessing;
using AcEvoFfbTuner.Core.FfbProcessing.Models;

namespace AcEvoFfbTuner.Core.DirectInput;

/// <summary>
/// LMU-specific HF8 signal mapper.
///
/// LMU's Mz data is approximated (front Mz = total steering force, rear Mz = 0),
/// so VibrationMixer ScrubModulation/RearSlipModulation are unreliable.
/// This mapper computes slip feel from raw wheel slip ratio + angle instead.
/// LMU provides TyreGrip and TyreTemp but lacks R3E-specific fields
/// (BrakePressure, TractionControlPercent, TireFlatspot, TireOnMtrl).
/// </summary>
public sealed class LmuHf8SignalMapper : Hf8SignalMapper
{
    private float _slipPhase;

    private static float[,] CreateLmuDefaultSourceWeights()
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
        w[(int)Hf8Zone.BackLowerRight, (int)Hf8Source.Slip] = 1.2f;
        w[(int)Hf8Zone.BackLowerRight, (int)Hf8Source.Kerb] = 0.5f;
        w[(int)Hf8Zone.BackLowerRight, (int)Hf8Source.LateralG] = 0.0f;
        w[(int)Hf8Zone.BackLowerRight, (int)Hf8Source.Engine] = 1.8f;

        w[(int)Hf8Zone.BackLowerLeft, (int)Hf8Source.Suspension] = 0.0f;
        w[(int)Hf8Zone.BackLowerLeft, (int)Hf8Source.Slip] = 1.2f;
        w[(int)Hf8Zone.BackLowerLeft, (int)Hf8Source.Kerb] = 0.5f;
        w[(int)Hf8Zone.BackLowerLeft, (int)Hf8Source.LateralG] = 0.0f;
        w[(int)Hf8Zone.BackLowerLeft, (int)Hf8Source.Engine] = 1.8f;

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

    public LmuHf8SignalMapper()
    {
        ZoneSourceWeights = CreateLmuDefaultSourceWeights();
    }

    /// <summary>
    /// Map LMU telemetry to 8-zone HF8 motor intensities.
    ///
    /// LMU-specific behaviour:
    ///   - Slip feel uses raw wheel slip ratio/angle instead of Mz-derived scrub/rear-slip
    ///   - No R3E-specific fields (BrakePressure, TC, Flatspot, TireOnMtrl) — clean base
    ///   - TyreGrip-based grip feel is available via VibrationMixer (road/curb detection works)
    /// </summary>
    public override float[] Map(
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
        float kerbVib = raw.KerbVibration;
        float lfeOut = MathF.Abs(lfeGenerator.LfeOutput);
        float absMod = MathF.Abs(vibrationMixer.AbsForceModulation);
        float roadMod = MathF.Abs(vibrationMixer.RoadForceModulation);

        // ── Slip feel from raw wheel data (VibrationMixer scrub/rear-slip unreliable for LMU) ──
        float frontSlipCombined = 0f;
        float rearSlipCombined = 0f;
        for (int i = 0; i < 4; i++)
        {
            float sr = MathF.Abs(raw.SlipRatio?[i] ?? 0f);
            float sa = MathF.Abs(raw.SlipAngle?[i] ?? 0f) * 0.5f;
            float c = sr + sa;
            if (i < 2) frontSlipCombined = Math.Max(frontSlipCombined, c);
            else rearSlipCombined = Math.Max(rearSlipCombined, c);
        }
        frontSlipCombined = Math.Clamp(frontSlipCombined, 0f, 1f);
        rearSlipCombined = Math.Clamp(rearSlipCombined, 0f, 1f);

        float speedHz = Math.Clamp(raw.SpeedKmh * 0.3f, 5f, 80f);
        _slipPhase += speedHz * (1f / 60f) * MathF.PI * 2f;
        if (_slipPhase > MathF.PI * 200f) _slipPhase -= MathF.PI * 200f;
        float slipWave = (MathF.Sin(_slipPhase) * 0.5f + 0.5f);

        float lmuScrubFeel = frontSlipCombined * slipWave * 0.12f;
        float lmuRearSlipFeel = rearSlipCombined * slipWave * 0.12f;

        // ── Per-wheel suspension and slip magnitudes ──
        float flSusp = Math.Clamp(suspDelta[0] * 80f, 0f, 0.6f);
        float flSlip = Math.Clamp(wheelSlip[0] * 2f, 0f, 0.4f);
        float frSusp = Math.Clamp(suspDelta[1] * 80f, 0f, 0.6f);
        float frSlip = Math.Clamp(wheelSlip[1] * 2f, 0f, 0.4f);
        float rlSusp = Math.Clamp(suspDelta[2] * 80f, 0f, 0.6f);
        float rlSlip = Math.Clamp(wheelSlip[2] * 2f, 0f, 0.4f);
        float rrSusp = Math.Clamp(suspDelta[3] * 80f, 0f, 0.6f);
        float rrSlip = Math.Clamp(wheelSlip[3] * 2f, 0f, 0.4f);

        float rpmVib = rpmNorm * 0.2f;
        float rpmLimiter = raw.IsRpmLimiterOn ? 0.6f : 0f;

        float leftSusp = flSusp + rlSusp;
        float rightSusp = frSusp + rrSusp;
        float totalSusp = leftSusp + rightSusp + 0.001f;
        float leftSuspRatio = leftSusp / totalSusp;
        float rightSuspRatio = rightSusp / totalSusp;

        float kerbLeft = kerbVib * leftSuspRatio;
        float kerbRight = kerbVib * rightSuspRatio;

        float pushedRight = signedLateralG > 0.3f ? (signedLateralG - 0.3f) * 0.5f : 0f;
        float pushedLeft = signedLateralG < -0.3f ? (-signedLateralG - 0.3f) * 0.5f : 0f;

        float leftSuspMix = flSusp + rlSusp * 0.4f;
        float rightSuspMix = frSusp + rrSusp * 0.4f;
        float leftSuspRearMix = rlSusp + flSusp * 0.4f;
        float rightSuspRearMix = rrSusp + frSusp * 0.4f;

        // ── Engine signals ──
        float leftLowerEngineMix = absMod + lmuRearSlipFeel;
        float rightLowerEngineMix = absMod + lmuRearSlipFeel;
        float leftUpperEngineMix = lfeOut * 1.5f + rpmVib + rpmLimiter;
        float rightUpperEngineMix = lfeOut * 1.5f + rpmVib + rpmLimiter;

        float[] zoneScale = [0.8f, 0.8f, 0.8f, 0.8f, 0.7f, 0.7f, 0.5f, 0.5f];

        float[,] engineSignals = new float[ZoneCount, 1];
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
            lmuScrubFeel * 0.5f + lmuRearSlipFeel * 0.5f,
            lmuScrubFeel * 0.5f + lmuRearSlipFeel * 0.5f,
            lmuScrubFeel * 0.3f + lmuRearSlipFeel * 0.7f,
            lmuScrubFeel * 0.3f + lmuRearSlipFeel * 0.7f,
            lmuRearSlipFeel, lmuRearSlipFeel,
            lmuScrubFeel, lmuScrubFeel
        ];

        float[] kerbSignals =
        [
            kerbRight, kerbLeft, kerbRight, kerbLeft,
            roadMod * leftSuspRatio * 2f, roadMod * rightSuspRatio * 2f,
            0f, 0f
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

    public override void Reset()
    {
        base.Reset();
        _slipPhase = 0f;
    }
}
