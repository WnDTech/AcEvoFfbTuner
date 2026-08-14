using System.Diagnostics;
using AcEvoFfbTuner.Core.FfbProcessing;
using AcEvoFfbTuner.Core.FfbProcessing.Models;

namespace AcEvoFfbTuner.Core.DirectInput;

/// <summary>
/// RaceRoom-specific HF8 signal mapper.
///
/// R3E shared memory does NOT expose per-wheel Mz (aligning torque) or Fy (lateral force).
/// The base FfbVibrationMixer therefore produces zero ScrubModulation and RearSlipModulation.
/// This mapper replaces those dead signals with:
///   - Real wheel slip computed from raw.SlipRatio + raw.SlipAngle
///   - Real brake pressure (per-wheel kN) for lower-back braking feel
///   - Real traction control cuts for engine-zone rumble
///   - Real flatspot detection for sharp per-revolution pulses
///   - Surface material feel from TireOnMtrl (grass, gravel, rumble strips)
///
/// Zone source weights are tuned for R3E's synthesized data ranges.
/// </summary>
public sealed class R3eHf8SignalMapper : Hf8SignalMapper
{
    private float _rumblePhase;
    private float _slipPhase;

    /// <summary>
    /// Kerb-channel latch window after the last TireOnMtrl == 5 (rumble strip)
    /// frame. R3E's kerb synthesis is derived from suspension velocity/deflection,
    /// so it is gated on the authoritative surface material instead of amplitude —
    /// logs prove tarmac noise (0.02-0.27) overlaps real strikes (0.096). The latch
    /// keeps the strike tail audible and survives mtrl flicker between reads.
    /// </summary>
    private const long KerbLatchTicks = 120L * TimeSpan.TicksPerMillisecond;
    private long _kerbLatchUntilTicks;

    private static float[,] CreateR3eDefaultSourceWeights()
    {
        var w = new float[ZoneCount, SourceCount];

        // ── Seat (front/rear, left/right) — suspension + slip dominate ──
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

        // ── Lower back — brake pressure + rear slip + TC + surface ──
        w[(int)Hf8Zone.BackLowerRight, (int)Hf8Source.Suspension] = 0.0f;
        w[(int)Hf8Zone.BackLowerRight, (int)Hf8Source.Slip] = 1.5f;
        w[(int)Hf8Zone.BackLowerRight, (int)Hf8Source.Kerb] = 0.5f;
        w[(int)Hf8Zone.BackLowerRight, (int)Hf8Source.LateralG] = 0.0f;
        w[(int)Hf8Zone.BackLowerRight, (int)Hf8Source.Engine] = 1.5f;

        w[(int)Hf8Zone.BackLowerLeft, (int)Hf8Source.Suspension] = 0.0f;
        w[(int)Hf8Zone.BackLowerLeft, (int)Hf8Source.Slip] = 1.5f;
        w[(int)Hf8Zone.BackLowerLeft, (int)Hf8Source.Kerb] = 0.5f;
        w[(int)Hf8Zone.BackLowerLeft, (int)Hf8Source.LateralG] = 0.0f;
        w[(int)Hf8Zone.BackLowerLeft, (int)Hf8Source.Engine] = 1.5f;

        // ── Upper back — lateral G + engine + surface feel ──
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

    public R3eHf8SignalMapper()
    {
        ZoneSourceWeights = CreateR3eDefaultSourceWeights();
    }

    /// <summary>
    /// Map R3E telemetry to 8-zone HF8 motor intensities.
    ///
    /// R3E-specific behaviour:
    ///   - Slip feel uses raw wheel slip ratio/angle instead of Mz-derived scrub/rear-slip
    ///   - Brake pressure drives lower back zones (real per-wheel kN)
    ///   - Surface material (TireOnMtrl) adds rumble strip / gravel / grass feel
    ///   - TC cuts are more prominent (actual TC cut %)
    ///   - Flatspot generates sharp per-revolution pulses
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

        // ── Raw vibration fields: apply VibrationMixer gains so slider changes affect HF8 ──
        // Gate raw KerbVibration BEFORE gain multiplication. R3E's SynthesizeKerbVibration
        // derives kerb feel from suspension velocity/deflection, and diagnostic logs show
        // it produces 0.02-0.27 CONTINUOUSLY on smooth tarmac (mtrl=1/1/1/1) — the
        // "suspension vibration bleeding through the Kerbs sliders". Amplitude gating
        // cannot separate it: the real kerb crossing logged 0.096 while tarmac peaks hit
        // 0.27. TireOnMtrl == 5 (rumble strip) is the only reliable discriminator, so the
        // kerb channel is enabled only while a wheel is on a rumble strip, with a short
        // latch so the strike tail / mtrl flicker is not lost. If the game build never
        // populates TireOnMtrl (all <= 0), fall back to the old amplitude-only gate.
        float rawKerb = 0f;
        bool mtrlAvailable = raw.TireOnMtrl != null &&
            (raw.TireOnMtrl[0] > 0 || raw.TireOnMtrl[1] > 0 ||
             raw.TireOnMtrl[2] > 0 || raw.TireOnMtrl[3] > 0);
        if (mtrlAvailable)
        {
            bool wheelOnRumbleStrip = raw.TireOnMtrl![0] == 5 || raw.TireOnMtrl![1] == 5 ||
                                      raw.TireOnMtrl![2] == 5 || raw.TireOnMtrl![3] == 5;
            if (wheelOnRumbleStrip)
                _kerbLatchUntilTicks = Stopwatch.GetTimestamp() + KerbLatchTicks;
            if (Stopwatch.GetTimestamp() <= _kerbLatchUntilTicks)
                rawKerb = raw.KerbVibration;
        }
        else if (raw.KerbVibration >= 0.02f)
        {
            rawKerb = raw.KerbVibration;
        }
        float kerbVib = rawKerb * vibrationMixer.KerbGain;

        float lfeOut = MathF.Abs(lfeGenerator.LfeOutput);

        float absMod = MathF.Abs(vibrationMixer.AbsForceModulation);
        if (absMod < 0.01f) absMod = 0f;

        float roadMod = MathF.Abs(vibrationMixer.RoadForceModulation);
        if (roadMod < 0.02f) roadMod = 0f;

        // ── R3E: scrubMod and rearSlipMod are always 0 (no Mz data) ──
        // Use raw per-wheel slip ratio + slip angle instead.
        // Scale by VibrationMixer gains so sliders control HF8 slip feel.
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

        // Speed-oscillating slip texture (bipolar sine — no DC bias)
        bool slipActive = frontSlipCombined > 0.03f || rearSlipCombined > 0.03f;
        float slipWave;
        if (slipActive)
        {
            float speedHz = Math.Clamp(raw.SpeedKmh * 0.3f, 5f, 80f);
            _slipPhase += speedHz * (1f / 60f) * MathF.PI * 2f;
            if (_slipPhase > MathF.PI * 200f) _slipPhase -= MathF.PI * 200f;
            slipWave = MathF.Sin(_slipPhase);
        }
        else
        {
            _slipPhase = 0f;
            slipWave = 0f;
        }

        // Gate slip feel behind thresholds + apply gain sliders
        float r3eScrubFeel = frontSlipCombined > 0.03f
            ? frontSlipCombined * slipWave * 0.12f * vibrationMixer.ScrubGain : 0f;

        float r3eRearSlipFeel = rearSlipCombined > 0.03f
            ? rearSlipCombined * slipWave * 0.12f * vibrationMixer.RearSlipGain : 0f;

        // ── Brake pressure: R3E sends per-wheel brake force in NEWTONS (verified
        // from logs: 3653 N under braking), not kN as the SDK header claims ──
        float brakeMod = 0f;
        if (raw.BrakePressure != null && raw.SpeedKmh > 5f)
        {
            float maxBP = 0f;
            for (int b = 0; b < 4; b++)
                maxBP = Math.Max(maxBP, Math.Clamp(raw.BrakePressure[b] / 5000f, 0f, 1f));
            if (maxBP > 0.01f && raw.BrakeInput > 0.05f)
                brakeMod = maxBP * raw.BrakeInput * 0.5f;
        }

        // ── Traction control: real TC cut % from R3E ──
        // Live-cut gate is AidSettings.Tc == 5 (raw.TcActiveGfx) — the ONLY
        // reliable "TC is cutting engine power RIGHT NOW" flag.
        // TractionControlPercent (0-100) is intensity-only; it reads a constant
        // high value (e.g. 100) when TC is enabled but NOT cutting on some
        // builds, so it must never be used as the on/off gate.
        float tcMod = raw.TcActiveGfx && raw.TractionControlPercent > 0.5f
            ? Math.Clamp(raw.TractionControlPercent / 100f, 0f, 0.4f) * speedFade : 0f;

        // ── Flatspot: real per-wheel boolean detection ──
        float flatspotMod = 0f;
        if (raw.TireFlatspot != null && raw.SpeedKmh > 15f)
        {
            for (int f = 0; f < 4; f++)
            {
                if (raw.TireFlatspot[f] > 0.5f)
                {
                    flatspotMod = 0.4f;
                    break;
                }
            }
        }

        // ── Surface material feel from TireOnMtrl ──
        // R3E codes: 0=none, 1=tarmac, 2=grass, 3=dirt, 4=gravel, 5=rumble strip
        float surfaceMod = 0f;
        bool onRumbleStrip = false;
        if (raw.TireOnMtrl != null)
        {
            for (int i = 0; i < 4; i++)
            {
                switch (raw.TireOnMtrl[i])
                {
                    case 2: surfaceMod = Math.Max(surfaceMod, 0.20f); break;
                    case 3: surfaceMod = Math.Max(surfaceMod, 0.30f); break;
                    case 4: surfaceMod = Math.Max(surfaceMod, 0.40f); break;
                    case 5: onRumbleStrip = true; break;
                }
            }
            if (onRumbleStrip)
            {
                float rsSpeedHz = Math.Clamp(raw.SpeedKmh * 0.5f, 10f, 100f);
                _rumblePhase += rsSpeedHz * (1f / 60f) * MathF.PI * 2f;
                if (_rumblePhase > MathF.PI * 200f) _rumblePhase -= MathF.PI * 200f;
                float wave1 = MathF.Sin(_rumblePhase);
                float wave2 = MathF.Sin(_rumblePhase * 0.7f);
                float ripple = (wave1 * 0.6f + wave2 * 0.4f) * 0.5f + 0.5f;
                surfaceMod = Math.Max(surfaceMod, ripple * 0.5f);
            }
        }

        // ── Per-wheel suspension and slip magnitudes ──
        float flSusp = Math.Clamp(suspDelta[0] * 80f, 0f, 0.6f);
        float flSlip = Math.Clamp(wheelSlip[0] * 2f, 0f, 0.4f);
        float frSusp = Math.Clamp(suspDelta[1] * 80f, 0f, 0.6f);
        float frSlip = Math.Clamp(wheelSlip[1] * 2f, 0f, 0.4f);
        float rlSusp = Math.Clamp(suspDelta[2] * 80f, 0f, 0.6f);
        float rlSlip = Math.Clamp(wheelSlip[2] * 2f, 0f, 0.4f);
        float rrSusp = Math.Clamp(suspDelta[3] * 80f, 0f, 0.6f);
        float rrSlip = Math.Clamp(wheelSlip[3] * 2f, 0f, 0.4f);

        float rpmVib = rpmNorm * 0.1f;
        float rpmLimiter = raw.IsRpmLimiterOn ? 0.6f : 0f;

        float leftSusp = flSusp + rlSusp;
        float rightSusp = frSusp + rrSusp;
        float totalSusp = leftSusp + rightSusp + 0.001f;
        float leftSuspRatio = leftSusp / totalSusp;
        float rightSuspRatio = rightSusp / totalSusp;

        float kerbLeft = kerbVib * leftSuspRatio;
        float kerbRight = kerbVib * rightSuspRatio;

        // Lateral G: AccG[1] > 0 = turn left = pushed RIGHT
        float pushedRight = signedLateralG > 0.3f ? (signedLateralG - 0.3f) * 0.5f : 0f;
        float pushedLeft = signedLateralG < -0.3f ? (-signedLateralG - 0.3f) * 0.5f : 0f;

        float leftSuspMix = flSusp + rlSusp * 0.4f;
        float rightSuspMix = frSusp + rrSusp * 0.4f;
        float leftSuspRearMix = rlSusp + flSusp * 0.4f;
        float rightSuspRearMix = rrSusp + frSusp * 0.4f;

        // ── Engine signals ──
        // Lower back: abs + rear slip + TC + brake pressure + surface
        // Upper back: LFE + RPM + limiter + flatspot + surface
        float leftLowerEngineMix = absMod + r3eRearSlipFeel + tcMod + brakeMod + surfaceMod * 0.3f;
        float rightLowerEngineMix = absMod + r3eRearSlipFeel + tcMod + brakeMod + surfaceMod * 0.3f;
        float leftUpperEngineMix = lfeOut * 1.5f + rpmVib + rpmLimiter + flatspotMod + surfaceMod * 0.2f;
        float rightUpperEngineMix = lfeOut * 1.5f + rpmVib + rpmLimiter + flatspotMod + surfaceMod * 0.2f;

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

        // ── Suspension signals (front/rear per side) ──
        float[] suspSignals =
        [
            rightSuspMix, leftSuspMix, rightSuspRearMix, leftSuspRearMix,
            0f, 0f, 0f, 0f
        ];

        // ── Slip signals — R3E-specific: uses real wheel slip instead of Mz-derived scrub/rearSlip ──
        float[] slipSignals =
        [
            r3eScrubFeel * 0.5f + r3eRearSlipFeel * 0.5f + tcMod * 0.2f,
            r3eScrubFeel * 0.5f + r3eRearSlipFeel * 0.5f + tcMod * 0.2f,
            r3eScrubFeel * 0.3f + r3eRearSlipFeel * 0.7f + tcMod * 0.3f,
            r3eScrubFeel * 0.3f + r3eRearSlipFeel * 0.7f + tcMod * 0.3f,
            r3eRearSlipFeel + tcMod * 0.5f, r3eRearSlipFeel + tcMod * 0.5f,
            r3eScrubFeel, r3eScrubFeel
        ];

        // ── Kerb signals — includes surface feel for R3E ──
        float[] kerbSignals =
        [
            kerbRight, kerbLeft, kerbRight, kerbLeft,
            roadMod * leftSuspRatio * 2f + surfaceMod * 0.4f,
            roadMod * rightSuspRatio * 2f + surfaceMod * 0.4f,
            surfaceMod * 0.3f, surfaceMod * 0.3f
        ];

        // ── Lateral G signals ──
        float[] latSignals =
        [
            pushedRight, pushedLeft, pushedRight, pushedLeft,
            0f, 0f, pushedRight, pushedLeft
        ];

        // ── Mix zones with source weights ──
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
        _rumblePhase = 0f;
        _slipPhase = 0f;
        _kerbLatchUntilTicks = 0;
    }
}

