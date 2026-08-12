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
            Version = "1.26.6",
            Date = new DateTime(2026, 8, 12),
            Title = "Startup crashes fixed (DirectInput effect race — root cause finally pinned) + wheel stays silent when idle",
            Fixes =
            [
                "Startup crashes fixed — the root cause is finally pinned, and it was never TrueForce: the 1 ms DirectInput interpolation thread called SharpDX effect methods while another thread disposed the effect (device loss / force zeroing) — a use-after-dispose access violation that killed the app. Windows event logs show the same fault in every version since 1.25.8. All effect lifecycle access is now serialized and the device state is re-checked before every native call",
                "The wheel no longer hums or spins when idle: the TrueForce session used to engage at app start (the 500 Hz stream carrier = the \"beeee\" hum and the startup wheelspin, even before pressing Start). The session now stays in standby until the app actually outputs force (Start, wheel test, wizard drive) — idle means a silent wheel with normal game FFB",
                "Focusing the app also zeros the TrueForce stream now — the wheel no longer holds the last set-and-hold force while suppressed"
            ],
            Improvements =
            [
                "The TrueForce card shows the standby state (\"connected, standby — engages when telemetry runs\") instead of a misleading connecting/active status when idle"
            ],
        },
        new ChangeLogEntry
        {
            Version = "1.26.5",
            Date = new DateTime(2026, 8, 12),
            Title = "TrueForce force now flows while the game holds the wheel + native crash capture (minidump + event log in diag packs)",
            Fixes =
            [
                "TrueForce force now reaches the wheel in-game: the force output path was gated on DirectInput acquisition, and the game holds the wheel exclusively while racing, so the TrueForce stream (which needs no DirectInput at all) sat idle at zero while the pipeline computed force. The stream is now driven directly, so tuned FFB flows in-game",
                "Output suppression also releases the TrueForce stream: focusing the app previously left the wheel holding the last set-and-hold force — it now zeros the stream too"
            ],
            Improvements =
            [
                "Native crash capture: startup crashes are native access violations the managed exception handlers never see, which is why crash logs stayed empty. The app now installs a native exception filter that records the exception code in crash.log and writes a minidump (crash.dmp) before terminating",
                "Diag packs now include crash evidence: crash.dmp (native stack) and eventlog_crashes.txt (Windows Application-log crash entries — faulting module + exception code for every AcEvoFfbTuner crash, including crashes from earlier versions). The Settings \"zip logs\" button includes them too"
            ],
        },
        new ChangeLogEntry
        {
            Version = "1.26.4",
            Date = new DateTime(2026, 8, 12),
            Title = "TrueForce FFB restored after game launch — provider survives game switches, reconnect restores it, HID++ flood + teardown race fixes",
            Fixes =
            [
                "TrueForce FFB restored after game launch: switching game sources (game auto-detection re-fires when the game's process changes) used to tear the TrueForce stream session down and never re-create it, so the wheel had zero FFB in-game while the game held the wheel exclusively. The FFB provider now survives game switches — no teardown, no re-init spin",
                "Reconnecting the wheel after a device loss now re-establishes the FFB provider automatically, instead of leaving the app with no force path",
                "No more provider churn on reconnect: repeated connect/disconnect cycles were tearing the TrueForce session down and re-initializing it every ~90 s, and a momentary DirectInput disconnect replaced the TrueForce stream with the generic DirectInput path mid-session",
                "TrueForce shutdown race fixed: the stream pump is stopped before the session-end handshake is sent — a teardown racing the init sequence was observed replaying the full init AFTER the handshake, leaving the wheel confused",
                "HID++ log flood gone: the RS50 replays its state reports at kHz while a TrueForce session is active, and every report was written to the HID++ log (~2000 lines/s at connect). Duplicates are now suppressed (two-report history — the flood alternated between two reports) and the log line is capped at ~1/s",
                "HID++ reads can no longer return corrupted settings: the read loops reuse their receive buffer, so a response handed to a pending request could be overwritten before it was consumed — responses are now copied",
                "Safe game switching under failure: if a source switch fails mid-way, the TrueForce session is torn down cleanly instead of leaking a dangling session that overrides all force paths until the wheel is power-cycled"
            ],
        },
        new ChangeLogEntry
        {
            Version = "1.26.3",
            Date = new DateTime(2026, 8, 12),
            Title = "Log purge no longer eats current-session logs; TrueForce session teardown on exit",
            Fixes =
            [
                "The update log purge ran AFTER the app's auto-connect and deleted this session's own connect logs (connection_debug.log, logitech_trueforce.log, LED logs) — the exact evidence the next diagnostic pack should contain. The purge now only removes files written before the app started",
                "Closing the app now tears the TrueForce stream session down cleanly (neutral force + the captured session-end handshake) — previously an abrupt close left the wheel in a stale \"session active\" state that overrode ALL force paths until a power cycle"
            ],
        },
        new ChangeLogEntry
        {
            Version = "1.26.2",
            Date = new DateTime(2026, 8, 11),
            Title = "TrueForce force tamed for the first user test",
            Improvements =
            [
                "Force scale defaults to 0.5 (was full-scale — arm-ripping even at 35% test force on the user's RS50)",
                "Stream pump runs at 500 Hz instead of 1 kHz — the TrueForce amplifier emitted a constant audible \"bee\" tone at 1 kHz; the wheel accepts 250-1000 Hz (the game's own FFB runs at 140-333 Hz)",
                "The force-scale slider on the Wheelbase tab tunes the strength without a new build"
            ],
        },
        new ChangeLogEntry
        {
            Version = "1.26.1",
            Date = new DateTime(2026, 8, 11),
            Title = "TrueForce stream FFB for RS50/G PRO — no G HUB needed",
            Features =
            [
                "Logitech TrueForce stream FFB (RS50 / G PRO): the wheel ignored DirectInput force while a TrueForce session was active — TEST FORCE worked, driving was dead. The app now writes force on the same TrueForce channel the game uses (1 kHz, 64-byte stream, no PC-mode dependency), so the wheel executes the app's tuned force in-game with no G HUB. The wheel falls back to its normal FFB path whenever the app's force is zero, and TEST FORCE now validates the stream directly. Force output is the first step — motor haptics via the stream's audio window is a later iteration",
                "Wheelbase tab gains a TRUEFORCE STREAM card: live session status (packet rate, rotation, write failures), an enable/disable toggle and a force-scale slider"
            ],
            Improvements =
            [
                "Connecting no longer resets the steering angle: the stream init replays G HUB's 68-packet sequence but patches the operating-range push with the wheel's own rotation (read from the HID++ interface), so no more 90°/2700° range sweeps",
                "HID++ settings reads briefly pause the stream (set-and-hold keeps the last force), so reading/writing wheel settings never fights the FFB stream"
            ],
            Fixes =
            [
                "crash.log survives app updates — the log purge used to delete it, which is why the 21:17 crash stack was lost",
                "The G29-style LED controller no longer writes G29 garbage to the Logitech USB Receiver (mouse dongle): Logitech LED control now requires a wheel product name (G29/G920/G923/Driving Force/RS50/G PRO), never VID alone",
                "HID++ disconnect hardening: read threads are drained before handles close, preventing use-after-close crashes on reconnect"
            ],
        },
        new ChangeLogEntry
        {
            Version = "1.26.0",
            Date = new DateTime(2026, 8, 11),
            Title = "Logitech force-path cleanup + HID++ refresh fix",
            Fixes =
            [
                "Logitech wheel settings now refresh continuously: the wheel answers a settings GET with the same state bytes it broadcasts, and the duplicate filter wrongly suppressed those answers — the card froze after the first read. Responses to pending requests are now always delivered; only unsolicited broadcasts are deduplicated",
                "The G29-style LED controller no longer connects to direct-drive Logitech wheels (RS50, G PRO) — it was writing G29 LED reports to the wheel's HID++ control interface every frame",
                "Force updates no longer perform a stop/restart dance on every zero-crossing during driving — that dance was based on an unproven theory and only fired during driving (never during the steady wheel test), dropping force for a moment each time"
            ],
            Improvements =
            [
                "The connect-time force-direction test now measures maximum deflection over a 400 ms pulse (was a single sample at 100 ms) — a direct-drive motor needs time to visibly move, so the old test could wrongly report 'wheel doesn't move'"
            ],
        },
        new ChangeLogEntry
        {
            Version = "1.25.9",
            Date = new DateTime(2026, 8, 11),
            Title = "Wheel settings can no longer be written by accident",
            Fixes =
            [
                "Logitech wheel settings are never written unless you actually change them: a slider clamp round-trip could previously push a clamped value (e.g. 1 Nm strength) to the wheel when the read was zero or out of range — that throttled the wheel's motor gain to 1 Nm and killed FFB everywhere. Values that still match the wheel's reported state are now skipped, and out-of-range reads are rejected before they reach the UI",
                "Each write now logs exactly which value changed (strength and/or rotation), so the log always shows what was sent to the wheel"
            ],
        },
        new ChangeLogEntry
        {
            Version = "1.25.8",
            Date = new DateTime(2026, 8, 11),
            Title = "Logitech HID++ response cross-talk fixed — settings now read correctly",
            Fixes =
            [
                "Logitech wheel settings reads are now correct: the RS50 broadcasts every state report on all three HID++ collections and repeats cached reports, which caused stale duplicates to satisfy the wrong requests — feature discovery returned crossed indices and rotation showed 49151° (actually the strength value). The app now reads only the canonical 64-byte collection, suppresses duplicate reports, drains stale copies between requests, and rejects colliding feature indices with a retry",
                "The wheel is automatically switched to Desktop mode on connect, so slider changes apply immediately — no need to select a profile on the wheel itself"
            ],
            Improvements =
            [
                "HID++ log no longer floods with duplicate reports (was over 1.9 MB in one session)"
            ],
        },
        new ChangeLogEntry
        {
            Version = "1.25.7",
            Date = new DateTime(2026, 8, 11),
            Title = "HID++ settings no longer freeze the app",
            Fixes =
            [
                "App freezing and slider lag with Logitech HID++ settings fixed: all wheel-communication (connect, 5-second refresh, slider writes, desktop-mode switch, re-read) now runs on background threads with 800-1000 ms protocol timeouts, so the UI never blocks while talking to the wheel",
                "Rotation read-back is sanity-checked (90-2700°): out-of-range values like the reported 49151° are rejected and logged instead of being displayed or written to the wheel"
            ],
            Improvements =
            [
                "Wheel FFB TEST and the rest of the app stay responsive even while the wheel is slow to answer HID++ requests"
            ],
        },
        new ChangeLogEntry
        {
            Version = "1.25.6",
            Date = new DateTime(2026, 8, 11),
            Title = "Logitech HID++ response transport hardening",
            Fixes =
            [
                "Logitech HID++ wheel settings now listen on ALL HID++ collections at once — short (0x10), long (0x11) and very-long (0x12) — via both interrupt reads and control-pipe reads (HidD_GetInputReport), because some Windows builds deliver HID++ responses only on the control path. The probe also tries writing on both the short and long report collections, so the settings connection works regardless of how the wheel answers"
            ],
            Improvements =
            [
                "Every read attempt is logged with its source (interrupt vs control, collection size) in logitech_hidpp.log, so the next connection attempt tells us exactly which transport the wheel answers on"
            ],
        },
        new ChangeLogEntry
        {
            Version = "1.25.5",
            Date = new DateTime(2026, 8, 11),
            Title = "Logitech HID++ connect fix + log purge on update",
            Fixes =
            [
                "Logitech HID++ wheel settings now connect properly: the app writes short HID++ reports to the wheel's 7-byte control collection and reads responses from the 64-byte very-long collection (the RS50 always answers with 0x12 reports) — previously the probe targeted the wrong collections and the wheel never saw a valid request",
                "Log files are purged when the app updates to a new version, so diagnostic packs only contain logs from the current build — no more stale logs from previous versions in send-to-dev submissions"
            ],
            Improvements =
            [
                "Full HID interface enumeration is logged (every collection's usage page and report sizes), so connection failures are diagnosable from logitech_hidpp.log alone"
            ],
        },
        new ChangeLogEntry
        {
            Version = "1.25.4",
            Date = new DateTime(2026, 8, 11),
            Title = "Direct HID++ wheel settings for Logitech — no G HUB needed",
            Features =
            [
                "Logitech HID++ wheel settings (RS50 / G PRO / G923): the app now talks to the wheel directly over its HID++ interface and reads/writes the wheel's own stored settings — FFB strength (1-8 Nm), rotation range (90-2700°), TrueForce and damping levels — with NO Logitech G HUB required",
                "Wheelbase tab gains a LOGITECH WHEEL SETTINGS card: live-reported strength/rotation, mode indicator (desktop vs onboard profile slot), Switch to Desktop Mode and Re-read buttons",
                "Slider changes write to the wheel immediately (400 ms debounce) with read-back verification; the wheel is re-read every 5 s to stay in sync with wheel-side profile changes"
            ],
            Improvements =
            [
                "If a wheel's force ever feels dead, the reported FFB strength makes the cause visible instantly — G HUB writes strength to the wheel's onboard profile, and a value near 0 Nm explains no force anywhere",
                "Full protocol logging to logitech_hidpp.log — every packet sent/received, feature discovery, reads, writes and errors — for diagnosis without extra tooling"
            ],
            Fixes =
            [
                "Logitech wheels that stopped producing force after G HUB touched their settings can now be fixed in-app: set the strength slider and switch to Desktop mode — no G HUB needed"
            ],
        },
        new ChangeLogEntry
        {
            Version = "1.25.3",
            Date = new DateTime(2026, 8, 11),
            Title = "Wheelbase tab with live wheel test, live visuals without Start, Discord-only Send to Dev",
            Features =
            [
                "New Wheelbase tab in Devices: connection info (device, force inversion, periodic effects) and the WHEEL FFB TEST — hold any force level for 3 seconds with telemetry paused to verify the wheelbase executes force",
                "Wheel visual and pedal bars now respond WITHOUT clicking Start — they read the physical devices directly, so the app feels alive the moment a wheel is connected",
                "Device dropdown is grouped into WHEELBASES and OTHER DEVICES so pedals/button boxes can never be mistaken for the force-feedback wheel"
            ],
            Improvements =
            [
                "Setup wizard auto-connects the first FFB-capable wheel and keeps retrying every second while waiting at step 0 — no more stalling when the wheel enumerates a moment after the wizard opens",
                "Force-direction auto-detect retries with a stronger 40% pulse when the initial test shows no movement, so a firmly held wheel no longer causes a false static-database fallback",
                "Pedal device status is shown in the Pedals panel (e.g. \"Pedal device: Thrustmaster Pedals\") — USB pedals are handled there, no need to select them in the Device menu",
                "Send to Dev is now Discord-only: instant delivery via webhook with full exception-chain logging if anything fails (email path removed)"
            ],
            Fixes =
            [
                "Setup wizard could stay open at step 0 if the wheel connected after the wizard loaded — now auto-connected within a second",
                "Status bar now shows \"Game detected — press Start to enable FFB\" when the game and wheel are connected but telemetry is idle"
            ],
        },
        new ChangeLogEntry
        {
            Version = "1.25.2",
            Date = new DateTime(2026, 8, 10),
            Title = "Manual pedal axis mapping, wheel FFB test, Logitech effect restart fix",
            Features =
            [
                "Manual pedal axis mapping: assign Gas/Brake/Clutch to any DirectInput axis (X/Y/Z/Rx/Ry/Rz/Slider0/Slider1) directly in Devices → Pedals → Calibration — no more relying on auto-detection or the live-server API"
            ],
            Improvements =
            [
                "WHEEL FFB TEST on the Devices → LED Effects tab: hold any force level for 3 seconds with telemetry paused, to verify the wheelbase actually executes DirectInput force — instantly tells you if the wheel motor works without driving",
                "Logitech wheels (G29/G923/G Pro/RS50): when the force crosses zero→non-zero the effect is now explicitly restarted — Logitech wheels stop playing after a zero-magnitude update and previously stayed stopped, which appeared as 'no force feedback'",
                "Connecting a Logitech wheel while G HUB is not running now warns in the status bar, system log, and setup wizard step 0"
            ],
            Fixes =
            [
                "No force feedback on Logitech wheels after any zero-force moment (pits, straights, standstill) — the constant-force effect is now restarted on the zero→non-zero transition"
            ],
        },
        new ChangeLogEntry
        {
            Version = "1.25.1",
            Date = new DateTime(2026, 8, 10),
            Title = "Diagnostic send fix, wizard close fix, Settings log tools",
            Fixes =
            [
                "Diagnostic pack email sender now forces TLS 1.2 — some Windows builds negotiated an older protocol that modern SMTP servers rejected",
                "Diagnostic pack email failure no longer blocks the Discord post — email is tried first, then Discord runs regardless of email outcome",
                "Setup wizard no longer stays open after completing — the save callback is now exception-safe so the wizard always closes"
            ],
            Improvements =
            [
                "Settings page now has a DIAGNOSTICS section with \"Open Log Folder\" (opens AppData in Explorer) and \"Zip All Logs\" (creates a timestamped ZIP of all logs, profiles, snapshots, and track maps — ready to share manually if Send to Dev fails)"
            ],
        },
        new ChangeLogEntry
        {
            Version = "1.25.0",
            Date = new DateTime(2026, 8, 10),
            Title = "In-app Help Guide: searchable, plain-language guide to every page, slider, and feature",
            Features =
            [
                "In-app Help Guide: a new \"Help\" button in the sidebar opens a searchable, plain-language reference covering every page and slider in the app",
                "Guide includes 16 articles in logical order — from Getting Started through every FFB Tuning slider's \"feel\" effect to Troubleshooting",
                "Searchable topic list with live filtering — type any keyword (e.g. \"kerb\", \"damping\", \"pedals\") to jump to the relevant section",
                "Every FFB Tuning, Equalizer, Devices, and haptic slider is documented with its range, default, and plain-language feel description"
            ],
        },
        new ChangeLogEntry
        {
            Version = "1.24.2",
            Date = new DateTime(2026, 8, 9),
            Title = "Clutch pedal customization, USB pedal mod support (T3PA/Arduino), device list clarity",
            Features =
            [
                "Clutch pedal customization: per-axis calibration (deadzone, min/max range, invert, smoothing), live clutch position bar, and a clutch channel in the haptic routing (master gain + clutch-position signal) ready for clutch-capable hardware"
            ],
            Improvements =
            [
                "USB pedal mods (Thrustmaster T3PA Arduino mod and other Arduino/Leonardo-based pedal boxes) are now always detected as pedals, even when the device name only shows the board name",
                "T3PA-style Arduino pedal mods are auto-mapped from the firmware layout (Gas=Slider0, Brake=Rx, Clutch=Z) unless the mapping was customized — no manual axis setup needed",
                "Device dropdown marks non-FFB devices as \"(no FFB)\" so pedals/button boxes can't be mistaken for the wheelbase; connecting a non-FFB device shows a warning",
                "Live server reports the clutch position signal and clutch haptic gain (routedClutch / clutchHapticGain, clutchGain settable via API)"
            ],
            Fixes =
            [
                "Pedal detection no longer skips Arduino-class USB pedal devices whose name contains generic terms like \"joystick\" or \"controller\" (previously the T3PA mod could be missed, mislabeling the wheelbase as having integrated pedals)"
            ],
        },
        new ChangeLogEntry
        {
            Version = "1.24.1",
            Date = new DateTime(2026, 8, 9),
            Title = "Logitech G RS50 support, wheel vibration on the main motor, clipper sign fix",
            Features =
            [
                "Logitech G RS50 support: full device recognition (vendor detection, force polarity, 8 Nm Auto Setup baseline) and correct round-wheel dashboard image",
                "Setup wizard now auto-connects the first FFB-capable wheel on load — no more stalling on \"Waiting for wheel connection\"",
                "Single-instance guard: a duplicate launch signals the running instance and exits (no second copy fighting over the wheel)"
            ],
            Improvements =
            [
                "EVO steering angle display reads the game's authoritative steer_degrees (actual wheel rotation in degrees from centre) instead of lock-normalized math",
                "Kerb/road/ABS vibration is routed into the main motor detail force so direct-drive wheels render vibration on the primary FFB motor",
                "FFB suppression engages only after the app holds focus for 1 second, so transient popups can never cut force; foreground window changes are logged for diagnosis",
                "HF8 signal mapper is now virtual so R3E/LMU mappers receive UI/profile writes (enable, gains, weights)"
            ],
            Fixes =
            [
                "Dashboard wheel image no longer flickers between brands when the wheel isn't recognized (fallback locked once per session)",
                "FFB output clipper sign inversion at SoftClipThreshold = 1.0: division by zero made the wheel yank in the opposite direction on strong force",
                "LMU kerb/road/impact vibration rewritten with DC-free per-frame deltas — was pinned at full strength on straights",
                "Force is zeroed immediately when output is suppressed instead of holding the stale target",
                "LMU/R3E HF8 mapper reset now handled through the base virtual property"
            ],
        },
        new ChangeLogEntry
        {
            Version = "1.24.0",
            Date = new DateTime(2026, 8, 2),
            Title = "New games (LMU, AC, ACC), pedal haptics, FFB Coach AI, track map rework",
            Features =
            [
                "Le Mans Ultimate support: shared-memory reader (20+ extended telemetry fields) and standalone LMU FFB pipeline with gear shift filter, brake boost, grip scaling, and dedicated HF8 signal mapping",
                "Assetto Corsa (original) support: shared memory reader and dedicated pipeline tuned for AC's synthesized force data",
                "Assetto Corsa Competizione support: reader and pipeline with 5x Mz scaling and trimmed vibration path",
                "RaceRoom pipeline rewrite: fully standalone R3E pipeline with brake weight-transfer boost, DC blocker, dynamic suppression, and V-shape centre suppression",
                "FFB Coach AI engine: interactive tuning assistant with chat, live telemetry monitoring, snapshot analysis, and pending-changes UI",
                "Pedal haptics: Osoyoo Arduino serial device manager, per-pedal (brake/gas) haptic routing, ABS/brake-pressure/TC/throttle/RPM/curb sources, TC live-cut gate, per-side curb routing, deadzones, and diagnostics",
                "HF8 haptic pad enhancements: per-motor copy/paste and new R3E telemetry data sources",
                "Track mapping rework: OSM relation-based circuit parsing with node-ID adjacency ordering, tiered data provider, and live map page with real-time car position on satellite tiles plus Npos-based start/finish auto-calibration",
                "Overlays management page for OBS browser sources and telemetry overlays",
                "Game auto-detection overhaul: defaults to None, unsupported-game awareness, fixed RaceRoom process detection",
                "Device silhouette connection visualizer on the Home dashboard",
                "Feedback relay: two-way support chat via Discord threads with report IDs and in-app reply polling",
                "Splash screen redesign with version/commit display, theme-aware colors, and smooth wheel rotation",
                "Sidebar reorder with WIP badges for in-progress features (Live Map, Track Map, Pedals)",
                "Unit tests for core FFB pipeline components plus CI build workflow",
                "Profile model expansion: R3E/LMU/AI parameters in profiles"
            ],
            Improvements =
            [
                "R3E kerb vibration: SuspensionVelocity x3 scaling with deflection fallback",
                "MainViewModel refactor: monolithic 3000-line class split into focused partials",
                "Fixed all 46 C# compiler warnings across 3 projects",
                "FFB Effects and Tuning pages: game-specific section visibility",
                "Overpass API rate limiting (60s gate) and non-retryable 429 handling to avoid IP blocks",
                "EVO shared memory: raw Npos read at corrected offset + last-known-good graphics returned on duplicate packets",
                "Wheel snapshot button now warns when no live telemetry is captured instead of saving stale data",
                "Setup wizard: braking pull detection and LMU support"
            ],
            Fixes =
            [
                "R3E BrakePressure unit: game sends Newtons not kN (3653 N verified in logs)",
                "Profile save/load now uses the profile as the single source of truth",
                "Overlay initialization: removed orphan MainViewModel from XAML DataContext",
                "CI test crash: guard save_debug.log write with Directory.CreateDirectory",
                "Circuit walk no longer drops or mirrors track segments when OSM ways connect in reverse orientation",
                "Live Map auto-calibration no longer fires on non-crossing frames (Npos wrap detection fully guarded)",
                "Snapshot no-data guard now detects a disconnected game",
                "Removed unused bounding-box track search tier (TrackOsmService)"
            ],
        },
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
