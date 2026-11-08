using System.Collections.ObjectModel;
using AcEvoFfbTuner.Core.TrackMapping;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AcEvoFfbTuner.ViewModels;

/// <summary>One corner row of the Driver Coach panel.</summary>
public sealed class CoachRow
{
    public string Label { get; init; } = "";
    public string DeltaText { get; init; } = "";
    /// <summary>"#RRGGBB": red = time lost, green = time gained, grey = even.</summary>
    public string DeltaColor { get; init; } = "#FF8B949E";
    public string Advice { get; init; } = "";
    public string Detail { get; init; } = "";
}

/// <summary>Driver Coach (Live Map layer): last lap vs best lap, corner by corner.</summary>
public partial class MainViewModel
{
    private CoachReport? _shownCoachReport;
    private CoachLap? _shownCoachBest;

    [ObservableProperty]
    private bool _showCoach;

    [ObservableProperty]
    private string _coachHeader = "Drive two clean laps — each lap is then compared with your best, corner by corner.";

    public ObservableCollection<CoachRow> CoachRows { get; } = new();

    /// <summary>The report shown in the panel (the Live Map draws its braking points).</summary>
    public CoachReport? CoachReport => _shownCoachReport;

    /// <summary>Live gap to the best lap (s, + = slower), null when not available.</summary>
    public float? LiveDeltaS => _telemetryLoop.Coach.LiveDeltaS;

    public static string FormatLapTime(float s) =>
        s <= 0 ? "--" : $"{(int)(s / 60)}:{s % 60:00.000}";

    private void RefreshCoach(CoachReport? report)
    {
        CoachRows.Clear();
        OnPropertyChanged(nameof(CoachReport));
        if (report == null)
        {
            CoachHeader = _shownCoachBest is { FromEarlierSession: true } pb
                ? $"Personal best on file: {FormatLapTime(pb.LapTimeS)} ({pb.RecordedAtUtc.ToLocalTime():d MMM}). " +
                  "Your first clean lap is compared with it."
                : "Drive two clean laps — each lap is then compared with your best, corner by corner.";
            return;
        }

        string bestName = report.BestFromEarlierSession
            ? $"PB {FormatLapTime(report.BestLapTimeS)}, {report.BestRecordedAtUtc.ToLocalTime():d MMM}"
            : $"lap {report.BestLapNumber}, {FormatLapTime(report.BestLapTimeS)}";

        float lapDelta = report.LapTimeS - report.BestLapTimeS;
        if (!report.HasReference)
        {
            CoachHeader = $"Lap {report.LapNumber}  {FormatLapTime(report.LapTimeS)}  — reference lap set. " +
                          "Your next clean lap is compared with it.";
            foreach (var c in report.Corners.OrderBy(c => c.CornerNumber))
                CoachRows.Add(new CoachRow
                {
                    Label = c.Label,
                    Advice = c.GripNote,
                    Detail = $"brake {Fmt(c.BrakePointM, "m")} before apex  ·  min {c.MinSpeedKmh:F0} km/h  ·  exit {c.ExitSpeedKmh:F0} km/h",
                });
            return;
        }

        CoachHeader = report.IsNewBest
            ? $"Lap {report.LapNumber}  {FormatLapTime(report.LapTimeS)}  — NEW " +
              (report.BestFromEarlierSession ? "PERSONAL BEST" : "BEST") + $", {-lapDelta:0.000}s faster ({bestName})"
            : $"Lap {report.LapNumber}  {FormatLapTime(report.LapTimeS)}  {lapDelta:+0.000;-0.000}s vs best ({bestName})";

        // Biggest losses first: that is where the next tenth is.
        foreach (var c in report.Corners.OrderByDescending(c => c.DeltaS))
        {
            string detail = $"brake {Fmt(c.BrakePointM, "m")} (best {Fmt(c.BestBrakePointM, "m")})  ·  " +
                            $"min {c.MinSpeedKmh:F0} ({c.BestMinSpeedKmh:F0}) km/h  ·  " +
                            $"exit {c.ExitSpeedKmh:F0} ({c.BestExitSpeedKmh:F0}) km/h";
            string advice = c.Advice;
            if (advice.Length == 0)
                advice = c.DeltaS < -0.05f ? "faster than your best here" : "on par with your best";
            CoachRows.Add(new CoachRow
            {
                Label = c.Label,
                DeltaText = $"{c.DeltaS:+0.00;-0.00;0.00}s",
                DeltaColor = c.DeltaS > 0.05f ? "#FFFF6B6B" : c.DeltaS < -0.05f ? "#FF3FB950" : "#FF8B949E",
                Advice = advice,
                Detail = detail,
            });
        }
    }

    private static string Fmt(float v, string unit) => float.IsNaN(v) ? "—" : $"{v:F0} {unit}";
}
