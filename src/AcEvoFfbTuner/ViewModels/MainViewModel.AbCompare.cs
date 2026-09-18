using System.Text.Json;
using AcEvoFfbTuner.Core.Profiles;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AcEvoFfbTuner.ViewModels;

/// <summary>
/// Phase 2: A/B profile comparison slots.
/// Captures detached snapshots of the CURRENT live settings (pipeline + VM),
/// toggles between them without touching saved files, and diffs them via
/// <see cref="ProfileComparator"/> (pure function, zero pipeline risk).
/// </summary>
public sealed partial class MainViewModel
{
    private FfbProfile? _abSlotA;
    private FfbProfile? _abSlotB;

    [ObservableProperty]
    private string _abSlotAName = "A: (empty)";

    [ObservableProperty]
    private string _abSlotBName = "B: (empty)";

    [ObservableProperty]
    private string _abDiffText = "Capture A and B to compare.";

    [ObservableProperty]
    private string _abActiveSlot = "";

    [ObservableProperty]
    private string _abLapSummaryText = "No completed laps yet — drive a lap with a track map loaded.";

    private static FfbProfile CloneProfile(FfbProfile source)
    {
        var json = JsonSerializer.Serialize(source);
        return JsonSerializer.Deserialize<FfbProfile>(json) ?? new FfbProfile { Name = source.Name };
    }

    [RelayCommand]
    private void CaptureAbSlotA()
    {
        _abSlotA = CloneProfile(BuildCurrentStateProfile());
        AbSlotAName = $"A: {_abSlotA.Name}";
        AbActiveSlot = "A";
        _telemetryLoop.LapRecorder.ActiveProfileName = $"A: {_abSlotA.Name}";
        UpdateAbDiff();
        RefreshAbLapSummary();
        StatusText = $"Captured slot A ({_abSlotA.Name})";
    }

    [RelayCommand]
    private void CaptureAbSlotB()
    {
        _abSlotB = CloneProfile(BuildCurrentStateProfile());
        AbSlotBName = $"B: {_abSlotB.Name}";
        AbActiveSlot = "B";
        _telemetryLoop.LapRecorder.ActiveProfileName = $"B: {_abSlotB.Name}";
        UpdateAbDiff();
        RefreshAbLapSummary();
        StatusText = $"Captured slot B ({_abSlotB.Name})";
    }

    [RelayCommand]
    private void ToggleAbSlot()
    {
        if (_abSlotA == null || _abSlotB == null)
        {
            StatusText = "Capture both A and B first.";
            return;
        }

        var target = AbActiveSlot == "A" ? _abSlotB : _abSlotA;
        var targetName = AbActiveSlot == "A" ? "B" : "A";
        target.ApplyToPipeline(_pipeline);
        target.ApplyToStaticFriction(_telemetryLoop.StaticFriction);
        LoadProfileValues(target);
        AbActiveSlot = targetName;
        _telemetryLoop.LapRecorder.ActiveProfileName = $"{targetName}: {target.Name}";
        RefreshAbLapSummary();
        StatusText = $"A/B: now on {targetName} ({target.Name})";
    }

    [RelayCommand]
    private void RefreshAbLapSummary()
    {
        var summaries = _telemetryLoop.LapRecorder.GetSummaryByProfile();
        if (summaries.Count == 0)
        {
            AbLapSummaryText = "No completed laps yet — drive a lap with a track map loaded.";
            return;
        }

        var lines = new System.Collections.Generic.List<string>(summaries.Count + 1)
        {
            "Lap delta per setup (wall-clock lap time):"
        };
        foreach (var s in summaries)
            lines.Add($"{s.ProfileName}: {s.LapCount} lap(s), best {s.BestLapTimeS:F1}s, avg {s.AvgLapTimeS:F1}s, clip {s.AvgClippingPct:F1}%");
        AbLapSummaryText = string.Join(System.Environment.NewLine, lines);
    }

    private void UpdateAbDiff()
    {
        if (_abSlotA == null || _abSlotB == null)
        {
            AbDiffText = "Capture both A and B to compare.";
            return;
        }

        var diffs = ProfileComparator.Diff(_abSlotA, _abSlotB);
        AbDiffText = ProfileComparator.Format(diffs);
    }
}
