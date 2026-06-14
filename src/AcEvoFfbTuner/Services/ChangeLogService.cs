using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace AcEvoFfbTuner.Services;

public sealed class ChangeLogEntry
{
    public string Version { get; init; } = "";
    public DateTime Date { get; init; }
    public string Title { get; init; } = "";
    public List<string> Features { get; init; } = [];
    public List<string> Improvements { get; init; } = [];
    public List<string> Fixes { get; init; } = [];
    public bool FromGitHub { get; init; }
}

public static class ChangeLogService
{
    private const string Owner = "WnDTech";
    private const string Repo = "AcEvoFfbTuner";
    private const string ReleasesUrl = $"https://api.github.com/repos/{Owner}/{Repo}/releases?per_page=15";

    private static readonly string CacheDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "AcEvoFfbTuner");
    private static readonly string CachePath = Path.Combine(CacheDir, "release_cache.json");

    private static readonly HttpClient _http = new()
    {
        Timeout = TimeSpan.FromSeconds(5)
    };

    private static List<ChangeLogEntry>? _gitHubEntries;
    private static bool _initialized;
    private static readonly SemaphoreSlim _initLock = new(1, 1);

    static ChangeLogService()
    {
        _http.DefaultRequestHeaders.Add("User-Agent", "AcEvoFfbTuner-Changelog");
    }

    public static readonly List<ChangeLogEntry> HardcodedEntries =
    [
        new ChangeLogEntry
        {
            Version = "1.23.61",
            Date = new DateTime(2026, 6, 14),
            Title = "CAMMUS C5 FFB fixes + steering angle snap fix",
            Fixes =
            [
                "Fixed CAMMUS C5 ConstantForce effect creation by trying multiple DirectInput parameter sets (Duration/Gain/SamplePeriod combinations) for wider driver compatibility",
                "Fixed steering angle display snap when raw.SteerDegrees from EVO graphics was mistaken for total lock at >90° — now always uses profile SteeringLockDegrees",
            ],
        },
        new ChangeLogEntry
        {
            Version = "1.23.0",
            Date = new DateTime(2026, 6, 11),
            Title = "Voice Wizard, Pit Limiter LED, Braking Pull Fix & Stability",
            Features =
            [
                "Voice announcements with pre-cached Google TTS voice pack for setup wizard guidance and audio cues",
                "Refactored setup wizard to 4 steps with user-guided force polarity detection and intensity preference",
                "Pit speed limiter LED flash with alternating outer LEDs and persistence latch",
                "Snapshot PitLmt column for diagnosing pit limiter activation",
                "FyInverted UI toggle to correct lateral force direction per wheel",
                "Struct verifier tool for diagnosing shared memory struct alignment issues"
            ],
            Improvements =
            [
                "Moved update banner to top of home page for better visibility",
                "Consolidated UpdateAutoTyreForces: fixed exponential MasterGain loop and auto polarity detection"
            ],
            Fixes =
            [
                "Fixed braking pull and Fx snap-back: Fy blend floor + adaptive Fx EMA during braking vs cruising",
                "Fixed EVO shared memory reader: restored per-wheel Mz/Fx/Fy data",
                "Fixed Mz centering deadzone reduction (1.1°) and removed harmful Fx/Fy zero-out on reverted path",
                "Fixed Mz sanitization range for consistent self-aligning torque output",
                "Fixed R3E centering direction regression: isolated MzSignCorrection from R3E pipeline",
                "Normalized R3E damage values: handle 0-100 percentage format in RaceInfoProcessor",
                "Fixed snapshot HTML player: parseTime HH:mm:ss, accTime animation, global chart scale, removed extra brace",
                "Fixed voice pack: .wav->.mp3 extension, removed obsolete phrases, added new wizard prompts",
                "Fixed pit limiter LED: read IsPitlimiterOn from EVO electronics struct",
                "Fixed update banner button readability"
            ]
        },
        new ChangeLogEntry
        {
            Version = "1.22.5",
            Date = new DateTime(2026, 6, 5),
            Title = "Multi-Game Support: RaceRoom FFB Pipeline, PFD Dashboard & Theme System",
            Features =
            [
                "Redesigned home page to Glass Cockpit PFD layout with G-force circle and equal-width instrument columns",
                "10-theme system with live switching and Settings page picker",
                "RaceRoom (R3E) FFB pipeline with adaptive gear shift filter, slip angle synthesis, and grip-loss feel",
                "Interactive Setup Wizard overlay with R3E auto-config and force polarity detection",
                "Game filter bar, modern filter bar, and persistent collapsed state on profile browser",
                "Per-motor source weight sliders for HF8 haptic pad",
                "Collapsible Devices sidebar with icon-only mode, widened to 200px, expanded RPM thresholds by default",
                "Redesigned Profiles page with sidebar browser, track/car grouping, and optional auto-upgrade",
                "Hide game-irrelevant FFB sliders per selected game",
                "R3E dead-center feel: Center Sharpness + Center Strength sliders and nonlinear slip angle with deadband",
                "System log persistence to disk with last-entry-only display in bottom bar",
                "Persistent FFB effects page expand/collapse state across restarts",
                "Git hash embedded in window title"
            ],
            Improvements =
            [
                "Scaled G-force circle to fill panel, vertically centered force section",
                "Improved Haptic Pad page clarity with descriptive labels for motor zones and source sliders",
                "Reordered Settings page: Startup Effect beside App Options, Debug Tools under System Log"
            ],
            Fixes =
            [
                "Unified G-force sensor axes per game — LatG from AccG[0], correct LongG mapping for AC EVO vs RaceRoom",
                "Fixed R3E G-force field name, axis order, and longitudinal G source (LocalAcceleration)",
                "R3E slip angle: signed slip ratio and LocalVelocity-based calculation",
                "Fixed Equalizer not being applied in the Assetto Corsa (original) pipeline",
                "Fixed HF8 slip rumble (cross-game contamination eliminated)",
                "Fixed physics struct offsets: correct P2pStatus and vibration dump offsets",
                "Fixed telemetry display: steering angle lock, pipeline field naming, and graph labels",
                "Filtered diagnostic logs to only include files from current day",
                "Fixed Test Buzz button readability in hardware section",
                "AC pipeline: clamped synthesized Fx/Fy, normalized steer, corrected centering multiplier"
            ]
        },
        new ChangeLogEntry
        {
            Version = "1.21.6",
            Date = new DateTime(2026, 5, 18),
            Title = "Live Telemetry Dashboard Redesign & Stability Fixes",
            Features =
            [
                "Redesigned live telemetry dashboard with splash-screen wheel, responsive signal monitor, and improved layout",
                "Auto-update progress bar with download tracking and track/session reset detection"
            ],
            Improvements =
            [
            ],
            Fixes =
            [
                "Fixed track change detection: static data re-read now happens outside connection block",
                "Fixed autoupdate banner hiding during download",
                "Fixed changelog parser to handle release headings with missing apostrophe"
            ]
        },
        new ChangeLogEntry
        {
            Version = "1.21.5",
            Date = new DateTime(2026, 5, 17),
            Title = "Satellite Maps, FFB Effects Redesign & UI Polish",
            Features =
            [
                "Satellite map view with ESRI tiles, auto-alignment, calibration, and zoom-to-cursor",
                "FFB effects separated into Curb & Rumble, Surface Vibration, and Offtrack sections",
                "Per-slider reset-to-default button using built-in profile defaults",
                "Dark-themed tooltips on all controls with option to disable in Settings",
                "Random splash screen wheels with corner-turning FFB animation"
            ],
            Improvements =
            [
            ],
            Fixes =
            [
            ]
        },
        new ChangeLogEntry
        {
            Version = "1.21.1",
            Date = new DateTime(2026, 5, 16),
            Title = "Home Dashboard, Configurable Startup & UI Polish",
            Features =
            [
                "Home page is now the default start-up view with live telemetry, quick start guide, and update notifications",
                "Configurable default start page: choose which page opens on launch via Settings",
                "Update available banner moved to Home page with prominent gold styling and one-click install"
            ],
            Improvements =
            [
                "Update notifications now appear as a prominent banner on the Home dashboard instead of the status bar",
                "Page visibility syncs correctly when using a custom default start page"
            ],
            Fixes =
            [
                "Fixed page visibility not syncing when default start page was set before MainWindow loaded"
            ]
        },
        new ChangeLogEntry
        {
            Version = "1.12.0",
            Date = new DateTime(2026, 5, 15),
            Title = "Wet Weather FFB, Force Inversion Fix & Diagnostics",
            Features =
            [
                "Wet weather FFB processing: tyre compound classification and wet-condition force adjustments",
                "Conflicting FFB apps detection with warning banner when other apps are interfering",
                "Changelog fetched from GitHub Releases API with offline hardcoded fallback",
                "Auto-normalization and damping floor diagnostics in snapshot output"
            ],
            Improvements =
            [
                "Fixed force inversion for all wheels — always run dynamic axis test on connect",
                "Fixed wheel pushing away from centre when moving — implemented SignCorrectionEnabled",
                "Styled scrollbars to match dark theme with orange accent"
            ],
            Fixes =
            [
            ]
        },
        new ChangeLogEntry
        {
            Version = "1.7.0",
            Date = new DateTime(2026, 4, 30),
            Title = "FFB Realism Overhaul & Tyre Flex Simulation",
            Features =
            [
                "FFB pipeline overhaul: stripped 12+ harmful processing stages for physics-faithful force output",
                "Tyre flex/deformation simulation: contact patch dynamics for more realistic steering feel",
                "Tire Grip Feel: front scrub intensity and rear slip warning through the wheel",
                "Dynamic heat-map colors on EQ sliders showing gain value at a glance",
                "Custom LabeledSlider control with editable values, section colors, log scale, undo, and context menu",
                "What's New changelog dialog on startup and status bar button",
                "Session Recording: record driving sessions for FFB diagnosis"
            ],
            Improvements =
            [
                "Fixed median filter bug: per-buffer initialization prevents zero-force warmup frames",
                "Replaced SpikeClamp with 3-sample median filter preserving legitimate kerb strikes",
                "Replaced parallel EMAs with single speed-dependent filter reducing ~40ms phase lag",
                "Coulomb friction model: constant friction opposing motion (was velocity-proportional)",
                "Fixed inertia to use angular acceleration instead of velocity",
                "Removed: tanh compression, sign correction override, center suppression expansion, safety slew rate, direction-change suppression, hysteresis, oscillation detection, gear shift smoothing, low-speed damping boost",
                "Raised slew rate to 0.40/tick for faster transients on kerb strikes and snap oversteer",
                "Reduced center suppression to 1.5\u00b0 for better on-center feel",
                "Context menu restyled: dark background, light text, hover highlights",
                "Auto-updater: installer now closes the running app instead of unreliable self-shutdown"
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
            Version = "1.5.1",
            Date = new DateTime(2026, 4, 28),
            Title = "FFB Realism Overhaul & UX Improvements",
            Features =
            [
                "Auto Setup & Live Tune: wheelbase-aware automatic FFB configuration",
                "Tire Grip Feel: front scrub intensity and rear slip warning through the wheel",
                "Dynamic heat-map colors on EQ sliders showing gain value at a glance",
                "Custom LabeledSlider control with editable values, section colors, log scale, undo, and context menu",
                "Session Recording: record your driving sessions and send video + telemetry to the developer for FFB diagnosis"
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

    [Obsolete("Use GetEntriesSinceAsync or AllEntries instead.")]
    public static List<ChangeLogEntry> Entries => HardcodedEntries;

    public static string CurrentVersion =>
        Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3) ?? "0.0.0";

    public static async Task InitializeAsync()
    {
        if (_initialized) return;

        await _initLock.WaitAsync();
        try
        {
            if (_initialized) return;

            try
            {
                var response = await _http.GetAsync(ReleasesUrl);
                if (response.IsSuccessStatusCode)
                {
                    var json = await response.Content.ReadAsStringAsync();
                    var releases = JsonSerializer.Deserialize<List<GitHubRelease>>(json, _jsonOpts);
                    if (releases?.Count > 0)
                    {
                        _gitHubEntries = releases
                            .Select(ParseRelease)
                            .Where(e => e != null)
                            .ToList()!;
                        SaveCache(_gitHubEntries);
                    }
                }
            }
            catch
            {
            }

            _gitHubEntries ??= LoadCache();
            _initialized = true;
        }
        finally
        {
            _initLock.Release();
        }
    }

    public static List<ChangeLogEntry> AllEntries
    {
        get
        {
            if (_gitHubEntries?.Count > 0)
            {
                var gitHubVersions = _gitHubEntries.Select(e => e.Version).ToHashSet();
                var merged = new List<ChangeLogEntry>(_gitHubEntries);
                foreach (var entry in HardcodedEntries)
                {
                    if (!gitHubVersions.Contains(entry.Version))
                        merged.Add(entry);
                }
                merged.Sort((a, b) => CompareVersions(b.Version, a.Version));
                return merged;
            }

            _gitHubEntries ??= LoadCache();
            if (_gitHubEntries?.Count > 0)
            {
                var cachedVersions = _gitHubEntries.Select(e => e.Version).ToHashSet();
                var merged = new List<ChangeLogEntry>(_gitHubEntries);
                foreach (var entry in HardcodedEntries)
                {
                    if (!cachedVersions.Contains(entry.Version))
                        merged.Add(entry);
                }
                merged.Sort((a, b) => CompareVersions(b.Version, a.Version));
                return merged;
            }

            return HardcodedEntries;
        }
    }

    public static List<ChangeLogEntry> GetEntriesSince(string? lastSeenVersion)
    {
        var all = AllEntries;

        if (string.IsNullOrWhiteSpace(lastSeenVersion))
            return all;

        return all.Where(e => IsVersionNewer(e.Version, lastSeenVersion)).ToList();
    }

    public static bool IsVersionNewer(string version, string thanVersion)
    {
        return CompareVersions(version, thanVersion) > 0;
    }

    private static int ParseVersionPart(string part)
    {
        return int.TryParse(part, out var val) ? val : 0;
    }

    private static int CompareVersions(string a, string b)
    {
        var partsA = a.Split('.').Select(ParseVersionPart).ToArray();
        var partsB = b.Split('.').Select(ParseVersionPart).ToArray();
        var maxLen = Math.Max(partsA.Length, partsB.Length);

        for (var i = 0; i < maxLen; i++)
        {
            var valA = i < partsA.Length ? partsA[i] : 0;
            var valB = i < partsB.Length ? partsB[i] : 0;
            if (valA != valB) return valA.CompareTo(valB);
        }

        return 0;
    }

    private static ChangeLogEntry? ParseRelease(GitHubRelease release)
    {
        var version = release.TagName?.TrimStart('v', 'V') ?? "";
        if (string.IsNullOrEmpty(version)) return null;

        var title = release.Name ?? "";
        title = Regex.Replace(title, @"^AC\s+Evo\s+FFB\s+Tuner\s+", "", RegexOptions.IgnoreCase).Trim();
        title = Regex.Replace(title, @"^v\d+(\.\d+)+\s*", "").Trim();
        if (string.IsNullOrWhiteSpace(title))
            title = "";

        var entry = new ChangeLogEntry
        {
            Version = version,
            Date = release.PublishedAt ?? DateTime.MinValue,
            Title = title,
            Features = [],
            Improvements = [],
            Fixes = [],
            FromGitHub = true
        };

        ParseMarkdownBody(release.Body ?? "", entry);
        return entry;
    }

    private static void ParseMarkdownBody(string body, ChangeLogEntry entry)
    {
        var lines = body.Split('\n');
        var currentCategory = "Features";
        var currentItems = new List<string>();

        foreach (var rawLine in lines)
        {
            var line = rawLine.TrimEnd('\r');

            if (string.IsNullOrWhiteSpace(line)) continue;

            if (IsMainHeading(line))
                continue;

            if (line == "---" || line == "***")
                continue;

            if (line.StartsWith("**Full Changelog**", StringComparison.OrdinalIgnoreCase))
                continue;

            if (line.StartsWith("### "))
            {
                FlushItems(entry, currentCategory, currentItems);
                currentItems.Clear();
                currentCategory = ClassifySection(line);
                continue;
            }

            if (line.StartsWith("- "))
            {
                var text = StripMarkdown(line.Substring(2).Trim());
                if (!string.IsNullOrWhiteSpace(text))
                    currentItems.Add(text);
                continue;
            }

            if (line.StartsWith("## ") && !IsMainHeading(line))
            {
                FlushItems(entry, currentCategory, currentItems);
                currentItems.Clear();
                currentCategory = ClassifySection(line);
                continue;
            }
        }

        FlushItems(entry, currentCategory, currentItems);
    }

    private static bool IsMainHeading(string line)
    {
        if (!line.StartsWith("## ")) return false;
        var rest = line.Substring(3).TrimStart();
        return rest.StartsWith("What's New", StringComparison.OrdinalIgnoreCase) ||
               rest.StartsWith("Whats New", StringComparison.OrdinalIgnoreCase) ||
               rest.StartsWith("What's Changed", StringComparison.OrdinalIgnoreCase);
    }

    private static string ClassifySection(string heading)
    {
        var lower = heading.ToLowerInvariant();

        if (lower.Contains("fix") || lower.Contains("bug"))
            return "Fixes";
        if (lower.Contains("improvement") || lower.Contains("enhancement") || lower.Contains("polish"))
            return "Improvements";

        return "Features";
    }

    private static void FlushItems(ChangeLogEntry entry, string category, List<string> items)
    {
        if (items.Count == 0) return;
        switch (category)
        {
            case "Fixes":
                entry.Fixes.AddRange(items);
                break;
            case "Improvements":
                entry.Improvements.AddRange(items);
                break;
            default:
                entry.Features.AddRange(items);
                break;
        }
    }

    private static string StripMarkdown(string text)
    {
        text = Regex.Replace(text, @"\*\*(.+?)\*\*", "$1");
        text = Regex.Replace(text, @"\*(.+?)\*", "$1");
        text = Regex.Replace(text, @"`(.+?)`", "$1");
        text = Regex.Replace(text, @"\[(.+?)\]\(.+?\)", "$1");
        return text.Trim();
    }

    private static void SaveCache(List<ChangeLogEntry> entries)
    {
        try
        {
            Directory.CreateDirectory(CacheDir);
            var json = JsonSerializer.Serialize(entries, _jsonOpts);
            File.WriteAllText(CachePath, json);
        }
        catch
        {
        }
    }

    private static List<ChangeLogEntry>? LoadCache()
    {
        try
        {
            if (!File.Exists(CachePath)) return null;
            var json = File.ReadAllText(CachePath);
            return JsonSerializer.Deserialize<List<ChangeLogEntry>>(json, _jsonOpts);
        }
        catch
        {
            return null;
        }
    }

    private static readonly JsonSerializerOptions _jsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    private sealed class GitHubRelease
    {
        [JsonPropertyName("tag_name")] public string? TagName { get; set; }
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("body")] public string? Body { get; set; }
        [JsonPropertyName("published_at")] public DateTime? PublishedAt { get; set; }
    }
}
