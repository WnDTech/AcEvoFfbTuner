using System;
using System.Collections.Generic;

namespace AcEvoFfbTuner.Core.Profiles;

/// <summary>
/// Zero-risk diff helper for A/B profile comparison.
/// Pure function over two detached <see cref="FfbProfile"/> snapshots —
/// never touches the pipeline, device, or saved files.
/// </summary>
public sealed record ProfileDiffEntry(string Path, string ValueA, string ValueB);

public static class ProfileComparator
{
    private const float Epsilon = 1e-6f;

    public static IReadOnlyList<ProfileDiffEntry> Diff(FfbProfile? a, FfbProfile? b)
    {
        var result = new List<ProfileDiffEntry>();
        if (a == null || b == null)
            return result;

        AddFloat(result, "OutputGain", a.OutputGain, b.OutputGain);
        AddFloat(result, "ForceScale", a.ForceScale, b.ForceScale);
        AddFloat(result, "NormalizationScale", a.NormalizationScale, b.NormalizationScale);
        AddFloat(result, "CompressionPower", a.CompressionPower, b.CompressionPower);
        AddFloat(result, "SoftClipThreshold", a.SoftClipThreshold, b.SoftClipThreshold);

        AddChannel(result, "MzFront", a.MzFront.Gain, b.MzFront.Gain, a.MzFront.Enabled, b.MzFront.Enabled);
        AddChannel(result, "FxFront", a.FxFront.Gain, b.FxFront.Gain, a.FxFront.Enabled, b.FxFront.Enabled);
        AddChannel(result, "FyFront", a.FyFront.Gain, b.FyFront.Gain, a.FyFront.Enabled, b.FyFront.Enabled);
        AddChannel(result, "MzRear", a.MzRear.Gain, b.MzRear.Gain, a.MzRear.Enabled, b.MzRear.Enabled);
        AddChannel(result, "FxRear", a.FxRear.Gain, b.FxRear.Gain, a.FxRear.Enabled, b.FxRear.Enabled);
        AddChannel(result, "FyRear", a.FyRear.Gain, b.FyRear.Gain, a.FyRear.Enabled, b.FyRear.Enabled);

        AddFloat(result, "Damping.Viscous", a.Damping.ViscousDamping, b.Damping.ViscousDamping);
        AddFloat(result, "Damping.Speed", a.Damping.SpeedDamping, b.Damping.SpeedDamping);
        AddFloat(result, "Damping.Friction", a.Damping.Friction, b.Damping.Friction);
        AddFloat(result, "Damping.Inertia", a.Damping.Inertia, b.Damping.Inertia);

        AddFloat(result, "Slip.SlipRatioGain", a.Slip.SlipRatioGain, b.Slip.SlipRatioGain);
        AddFloat(result, "Slip.SlipAngleGain", a.Slip.SlipAngleGain, b.Slip.SlipAngleGain);
        AddFloat(result, "Slip.SlipThreshold", a.Slip.SlipThreshold, b.Slip.SlipThreshold);
        AddFloat(result, "Slip.BrakeBoostGain", a.Slip.BrakeBoostGain, b.Slip.BrakeBoostGain);
        AddFloat(result, "Slip.TcFeelGain", a.Slip.TcFeelGain, b.Slip.TcFeelGain);
        AddFloat(result, "Slip.ForceGain", a.Slip.ForceGain, b.Slip.ForceGain);

        AddFloat(result, "Dynamic.LateralG", a.Dynamic.LateralGGain, b.Dynamic.LateralGGain);
        AddFloat(result, "Dynamic.LongitudinalG", a.Dynamic.LongitudinalGGain, b.Dynamic.LongitudinalGGain);
        AddFloat(result, "Dynamic.Suspension", a.Dynamic.SuspensionGain, b.Dynamic.SuspensionGain);
        AddFloat(result, "Dynamic.YawRate", a.Dynamic.YawRateGain, b.Dynamic.YawRateGain);

        AddFloat(result, "Vibrations.Kerb", a.Vibrations.KerbGain, b.Vibrations.KerbGain);
        AddFloat(result, "Vibrations.Slip", a.Vibrations.SlipGain, b.Vibrations.SlipGain);
        AddFloat(result, "Vibrations.Road", a.Vibrations.RoadGain, b.Vibrations.RoadGain);
        AddFloat(result, "Vibrations.Abs", a.Vibrations.AbsGain, b.Vibrations.AbsGain);
        AddFloat(result, "Vibrations.Master", a.Vibrations.MasterGain, b.Vibrations.MasterGain);
        AddFloat(result, "Vibrations.Scrub", a.Vibrations.ScrubGain, b.Vibrations.ScrubGain);
        AddFloat(result, "Vibrations.RearSlip", a.Vibrations.RearSlipGain, b.Vibrations.RearSlipGain);

        AddFloat(result, "Advanced.MaxSlewRate", a.Advanced.MaxSlewRate, b.Advanced.MaxSlewRate);
        AddFloat(result, "Advanced.NoiseFloor", a.Advanced.NoiseFloor, b.Advanced.NoiseFloor);
        AddFloat(result, "Advanced.CoreForceMultiplier", a.Advanced.CoreForceMultiplier, b.Advanced.CoreForceMultiplier);

        AddBool(result, "SoftLock.Enabled", a.SoftLock.Enabled, b.SoftLock.Enabled);
        AddFloat(result, "SoftLock.Strength", a.SoftLock.Strength, b.SoftLock.Strength);
        AddBool(result, "Gyro.Enabled", a.Gyro.Enabled, b.Gyro.Enabled);
        AddFloat(result, "Gyro.Gain", a.Gyro.Gain, b.Gyro.Gain);
        AddBool(result, "TcWheel.Enabled", a.TcWheel.Enabled, b.TcWheel.Enabled);
        AddFloat(result, "TcWheel.Gain", a.TcWheel.Gain, b.TcWheel.Gain);

        return result;
    }

    public static string Format(IReadOnlyList<ProfileDiffEntry> diffs, int maxRows = 40)
    {
        if (diffs.Count == 0)
            return "No differences — A and B are identical on compared fields.";
        var lines = new List<string>(Math.Min(diffs.Count + 1, maxRows + 1))
        {
            $"{diffs.Count} differing field(s):"
        };
        for (int i = 0; i < diffs.Count && i < maxRows; i++)
            lines.Add($"{diffs[i].Path}: {diffs[i].ValueA} → {diffs[i].ValueB}");
        if (diffs.Count > maxRows)
            lines.Add($"… +{diffs.Count - maxRows} more");
        return string.Join(Environment.NewLine, lines);
    }

    private static void AddFloat(List<ProfileDiffEntry> out_, string path, float a, float b)
    {
        if (Math.Abs(a - b) > Epsilon)
            out_.Add(new ProfileDiffEntry(path, a.ToString("F4"), b.ToString("F4")));
    }

    private static void AddBool(List<ProfileDiffEntry> out_, string path, bool a, bool b)
    {
        if (a != b)
            out_.Add(new ProfileDiffEntry(path, a.ToString(), b.ToString()));
    }

    private static void AddChannel(List<ProfileDiffEntry> out_, string path, float gainA, float gainB, bool enA, bool enB)
    {
        if (Math.Abs(gainA - gainB) > Epsilon || enA != enB)
            out_.Add(new ProfileDiffEntry(path, $"{gainA:F3} {(enA ? "on" : "off")}", $"{gainB:F3} {(enB ? "on" : "off")}"));
    }
}
