using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace AcEvoFfbTuner.Services;

public sealed class ChangeLogEntry
{
    public string Version { get; init; } = "";
    public DateTime Date { get; init; }
    public string Title { get; init; } = "";
    public List<string> Features { get; init; } = [];
    public List<string> Fixes { get; init; } = [];
    public List<string> Improvements { get; init; } = [];
}

public static class ChangeLogService
{
    public static readonly List<ChangeLogEntry> Entries =
    [
        new ChangeLogEntry
        {
            Version = "1.5.1",
            Date = new DateTime(2026, 4, 28),
            Title = "FFB Realism Overhaul & UX Improvements",
            Features =
            [
                "Auto Setup & Live Tune: wheelbase-aware automatic FFB configuration",
                "Tire Grip Feel: front scrub intensity and rear slip warning through the wheel",
                "Dynamic heat-map colors on EQ sliders showing gain value at a glance",
                "Custom LabeledSlider control with editable values, section colors, log scale, undo, and context menu"
            ],
            Improvements =
            [
                "FFB pipeline overhaul: fixed median filter bug, stripped harmful processing, updated damping model",
                "Preserved physics Mz curve for more authentic self-aligning torque feel",
                "Context menu restyled: dark background, light text, hover highlights matching the dark theme",
                "Auto-updater: installer now closes the running app instead of unreliable self-shutdown",
                "Replaced ScreenRecorderLib with FFmpeg subprocess for game recording"
            ],
            Fixes =
            [
                "Fixed zero-FFB output: corrected force scale divisors and DirectInput fallback",
                "Fixed EQ not affecting FFB output",
                "Fixed LiveAutoTuner threshold and slider precision issues",
                "Hidden 5 disabled sliders from UI to reduce clutter"
            ]
        },
        new ChangeLogEntry
        {
            Version = "1.5.0",
            Date = new DateTime(2026, 4, 18),
            Title = "Multi-Brand LED Support & Auto-Setup",
            Features =
            [
                "Logitech and Simucube wheel LED support via HID",
                "Vendor-specific LED controls based on detected wheelbase capabilities",
                "Game FFB detection warning with in-game FFB=0 instructions"
            ],
            Improvements =
            [
                "Auto-updater improvements for smoother upgrade experience"
            ],
            Fixes =
            [
                "Fixed infinite DEVICE LOST loop: reset error state on reconnect with cooldown and attempt limits",
                "Fixed Moza SDK native DLLs missing from installer and single-file publish"
            ]
        },
        new ChangeLogEntry
        {
            Version = "1.4.1",
            Date = new DateTime(2026, 4, 10),
            Title = "Stability Fixes",
            Fixes =
            [
                "Fixed JSON crash on startup",
                "Fixed device lost handling on Moza wheelbases",
                "Fixed Moza DLL deployment in installer"
            ]
        }
    ];

    public static string CurrentVersion =>
        Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3) ?? "0.0.0";

    public static List<ChangeLogEntry> GetEntriesSince(string? lastSeenVersion)
    {
        if (string.IsNullOrWhiteSpace(lastSeenVersion))
            return Entries;

        return Entries.Where(e => IsVersionNewer(e.Version, lastSeenVersion)).ToList();
    }

    public static bool IsVersionNewer(string version, string thanVersion)
    {
        return CompareVersions(version, thanVersion) > 0;
    }

    private static int CompareVersions(string a, string b)
    {
        var partsA = a.Split('.').Select(int.Parse).ToArray();
        var partsB = b.Split('.').Select(int.Parse).ToArray();
        var maxLen = Math.Max(partsA.Length, partsB.Length);

        for (var i = 0; i < maxLen; i++)
        {
            var valA = i < partsA.Length ? partsA[i] : 0;
            var valB = i < partsB.Length ? partsB[i] : 0;
            if (valA != valB) return valA.CompareTo(valB);
        }

        return 0;
    }
}
