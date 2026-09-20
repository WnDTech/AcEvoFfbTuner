using System.Text;
using System.Text.Json;
using AcEvoFfbTuner.Core.FfbProcessing;
using AcEvoFfbTuner.Core.Profiles;
using AcEvoFfbTuner.Core.TrackMapping;

namespace AcEvoFfbTuner.Services;

public enum CoachSessionState
{
    Idle,
    SelectingSource,
    Analyzing,
    Questioning,
    Applying,
    Summary
}

public sealed class CoachMessage
{
    public required string Text { get; init; }
    public bool IsUser { get; init; }
    public List<CoachAnswer>? Answers { get; init; }
    public FfbRecommendation? Recommendation { get; init; }
    public string? Icon { get; init; }
}

public sealed class CoachAnswer
{
    public required string Id { get; init; }
    public required string Label { get; init; }
    public string? Description { get; init; }
}

public sealed class LiveProfilerStats
{
    public float OutputMin { get; init; }
    public float OutputMax { get; init; }
    public float OutputAvg { get; init; }
    public float ClippingPercent { get; init; }
    public float PeakMz { get; init; }
    public float PeakFx { get; init; }
    public float PeakFy { get; init; }
    public int FrameCount { get; init; }
    public int ClipCount { get; init; }
}

/// <summary>One applied profile change — the coach's apply history entry.</summary>
public sealed class CoachAppliedChange
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public DateTime AppliedAt { get; init; } = DateTime.Now;
    public string Parameter { get; init; } = "";
    public float OldValue { get; init; }
    public float NewValue { get; init; }
    public string Reason { get; init; } = "";
    public string Game { get; init; } = "";
    public string Car { get; init; } = "";
    public string Track { get; init; } = "";
    public string ProfileName { get; init; } = "";
    public string Label => $"{Parameter}: {OldValue:F2} → {NewValue:F2}";
    public string Detail => $"{AppliedAt:HH:mm:ss} · {ProfileName}" +
        (string.IsNullOrWhiteSpace(Car) ? "" : $" · {Car}") +
        (string.IsNullOrWhiteSpace(Track) ? "" : $" @ {Track}");
}

public sealed class FfbCoachService
{
    private readonly ProfileManager _profileManager;
    private readonly FfbPipeline _pipeline;
    private readonly List<CoachAppliedChange> _history = new();
    private CoachResult? _lastResult;
    private SnapshotCsvData? _lastCsvData;
    private LiveProfilerStats? _lastLiveStats;
    private FfbAIAnalyzer? _aiAnalyzer;
    private List<ChatMessage>? _aiConversation;

    public CoachSessionState State { get; private set; } = CoachSessionState.Idle;
    public string CurrentProfileName { get; private set; } = "";
    public string DataSourceLabel { get; private set; } = "";
    public string CurrentGame { get; set; } = "RaceRoom Racing Experience";
    public string CurrentWheel { get; set; } = "Moza R5 (5.5 Nm)";
    public bool IsColumnForceGame { get; set; } = true;
    /// <summary>Live driving context — pushed from telemetry detection, or read from the snapshot header.</summary>
    public string CurrentCar { get; set; } = "";
    public string CurrentTrack { get; set; } = "";
    public string CurrentTrackConfig { get; set; } = "";

    private CoachGame ActiveCoachGame => CoachParameterCatalog.ParseGameName(CurrentGame);

    /// <summary>Push live detection context (game + car + track) into the coach. Call before analysis.</summary>
    public void SyncLiveContext(string game, string car, string track, string trackConfig = "")
    {
        if (!string.IsNullOrWhiteSpace(game)) CurrentGame = CoachParameterCatalog.CanonicalGameName(game);
        if (!string.IsNullOrWhiteSpace(car)) CurrentCar = car;
        if (!string.IsNullOrWhiteSpace(track)) CurrentTrack = track;
        if (!string.IsNullOrWhiteSpace(trackConfig)) CurrentTrackConfig = trackConfig;
        if (ActiveCoachGame is CoachGame.Raceroom or CoachGame.LeMansUltimate)
            IsColumnForceGame = true;
        else if (ActiveCoachGame is CoachGame.AcEvo or CoachGame.AssettoCorsa or CoachGame.AssettoCorsaCompetizione)
            IsColumnForceGame = false;
    }

    private string GameContextString(string source)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Game: {CurrentGame}");
        sb.AppendLine($"Wheel: {CurrentWheel}");
        sb.AppendLine($"Source: {source}");
        sb.AppendLine($"Car: {(string.IsNullOrWhiteSpace(CurrentCar) ? "unknown" : CurrentCar)}");
        sb.AppendLine($"Track: {(string.IsNullOrWhiteSpace(CurrentTrack) ? "unknown" : CurrentTrack)}" +
            (string.IsNullOrWhiteSpace(CurrentTrackConfig) ? "" : $" ({CurrentTrackConfig})"));
        var profile = _profileManager.ActiveProfile;
        if (profile != null)
            sb.AppendLine($"Profile scope: {profile.Scope} (CarMatch: '{profile.CarMatch}', TrackMatch: '{profile.TrackMatch}', GameMatch: '{profile.GameMatch}'). " +
                "The Game/Car/Track lines above are DETECTED FACTS — never question which game is running. " +
                "Empty CarMatch/TrackMatch/GameMatch means a General profile that applies everywhere, NOT an unknown game.");
        return sb.ToString().TrimEnd();
    }

    private void AdoptSnapshotContext(SnapshotCsvData csvData)
    {
        // Snapshot-embedded context wins (it describes where the data was driven);
        // fall back to live detection when the snapshot predates context headers.
        if (!string.IsNullOrWhiteSpace(csvData.Game)) CurrentGame = CoachParameterCatalog.CanonicalGameName(csvData.Game);
        if (!string.IsNullOrWhiteSpace(csvData.CarModel)) CurrentCar = csvData.CarModel;
        if (!string.IsNullOrWhiteSpace(csvData.TrackName)) CurrentTrack = csvData.TrackName;
        if (!string.IsNullOrWhiteSpace(csvData.TrackConfig)) CurrentTrackConfig = csvData.TrackConfig;
        IsColumnForceGame = ActiveCoachGame is CoachGame.Raceroom or CoachGame.LeMansUltimate;
    }

    /// <summary>
    /// Static parameter reference. Ranges are the REAL tuning-UI slider limits
    /// (FfbTuningPage.xaml). A per-game tunable/not-available table from
    /// <see cref="CoachParameterCatalog"/> is appended dynamically in
    /// <see cref="BuildSystemContext"/> — that table is authoritative when the
    /// two disagree.
    /// </summary>
    private static string KnowledgeBase => @"
## FFB Pipeline Architecture

The FFB signal has TWO paths that are mixed together:

### 1. CORE PATH (Zero-Latency)
Raw Mz (self-aligning torque) → Normalize → LUT curve → Damping → Output Gain → Centering shaping → Grip Guard → Crash detection → Tyre condition → Hydroplaning → Speed fade. NO filtering, NO slew limit. This is the primary centering force that prevents oscillation.

### 2. DETAIL PATH (Filtered)
Additive effects only: Slip enhancement + Dynamic effects + Tyre flex + Vibration signals (curb, road, ABS, scrub, rear slip, offtrack) + LFE. Runs through EQ biquads and slew rate limiter. These are ""feel"" details, not centering force.

### FINAL MIX
Core + Detail → Soft clip → Noise floor gate → Output to wheel.

## Mandatory Diagnostic Checklist — Check ALL items before recommending

1. **Force inversion**: Column-force games → MzFrontGain MUST be NEGATIVE (-0.3 to -0.5). Positive = wheel fights steering.
2. **Dead channels**: All channel gains (mzFrontGain, fxFrontGain, fyFrontGain) = 0? Core FFB is disabled. User feels only vibrations.
3. **Clipping root cause**: ClippingPct > 10%? Check OutputGain first, then ForceScale (slider 0.1–5, LOWER values = stronger signal = more clipping). Then SoftClipThreshold.
4. **CoreForceMultiplier**: Column-force games → expect ~3.0x. Physics-based games → this slider is hidden, do NOT suggest it.
5. **Center suppression > 5°**: Creates deadzone. Wheel feels disconnected, no centering force.
6. **Static friction masking FFB**: Gain > 1.5 + MaxElasticStretch > 0.03 = rubber-band feel. (Per-wheel games only.)
7. **All vibrations zero**: MasterGain = 0 and all vibration gains = 0? Detail path dead — no road texture, kerbs, or slip feel. (Vibrations section is hidden for ACC — skip this check there.)

## Parameter Definitions (Range = REAL UI slider min–max. NEVER suggest a value outside it.)

### Strength & Output
| Parameter | Slider Range | Typical | Effect |
|-----------|-------|---------|--------|
| OutputGain | 0–20 | 0.6-0.8 | Final multiplier on total output |
| CompressionPower | NO SLIDER (profile-only) | 1.0 | Exponential curve on core force. Prefer OutputGain — user cannot drag this |
| ForceScale | 0.1–5 (R3E/LMU slider only) | 1.0 | Inverse sensitivity for column-force games. LOWER = stronger. NOT 700-1500 |
| SoftClipThreshold | 0.1–1 (UI: Max Force Limit) | 0.8 | Maximum output clamp |
| CoreForceMultiplier | 0–10 (R3E/LMU slider only) | 3.0 | Column-force game boost. Hidden for physics games — do NOT suggest there |

### Centering & On-Center Feel
| Parameter | Slider Range | Typical | Effect |
|-----------|-------|---------|--------|
| CenterSuppressionDegrees | 0–30 | 1-3 | V-shape force reduction near center |
| CenterSharpnessDegrees | 0–10 (R3E/LMU only) | 0-3 | Smoothstep ramp. 0 = disabled. Hidden for physics games |
| CenterBlendDegrees | 0–10 | 1-3 | Transition zone between center and full suppression |
| CenterKneePower | 0.5–3 | 1-2 | Non-linear core shaping. >1 = progressive |
| HysteresisThreshold | NO SLIDER (profile-only) | 0.01-0.02 | Deadband for noise rejection. Suggest NoiseFloor instead |
| NoiseFloor | 0–0.05 | 0.003-0.01 | Minimum output gate |
| MaxSlewRate | 0.03–0.5 | 0.3-0.5 | Per-frame rate limit on DETAIL path only |
| HysteresisWatchdogFrames | NO SLIDER (profile-only) | 0-3 | Frames to wait before engaging hysteresis |
| LowSpeedSmoothKmh | 0-100 | 10-20 | Speed below which low-speed smoothing applies |

### Channel Mix (Which Forces Reach the Wheel)
| Parameter | Slider Range | Typical | Effect |
|-----------|-------|---------|--------|
| MzFrontGain | -10..10 | -0.5 to -0.3 (R3E/LMU), 0.2-0.6 (EVO/AC/ACC) | Self-aligning torque. NEGATIVE for column-force games |
| FxFrontGain | -10..10 | 0.05-0.15 | Longitudinal force (traction/braking) |
| FyFrontGain | -10..10 | 0.08-0.2 | Lateral force (cornering) |
| MzRearGain | -10..10 | 0 | Rear Mz contribution |
| WheelLoadWeighting | 0-1 | 0-0.3 | How much wheel load affects channel mix |
| MzScale | 1-500 | 30 | Scaling divisor for Mz channel. Higher = weaker Mz |
| FxScale | 100-20000 | 4000 | Scaling divisor for Fx channel |
| FyScale | 100-20000 | 5000 | Scaling divisor for Fy channel |

### Damping (UI section visible for EVO/AC/LMU only — do NOT suggest for R3E/ACC)
| Parameter | Slider Range | Typical | Effect |
|-----------|-------|---------|--------|
| ViscousDamping | 0–1 | 0.03-0.15 | Speed-proportional resistance |
| SpeedDamping | 0–25 | 0.1-0.4 | Additional damping that scales with car speed |
| Friction | 0–20 (UI: Friction Level) | 0.01-0.08 | Coulomb friction (constant resistance) |
| Inertia | 0–20 (UI: Inertia Weight) | 0.02-0.15 | Angular inertia simulation |
| MaxSpeedReference | 50-500 | 150-250 | Speed at which speed damping reaches max |
| CenterDampFloor | 0–0.1 | 0 (EVO), 0.03 (LMU) | Absolute damping-authority floor at center. Fixes weave where proportional cap falls to zero |
| StraightGain | 0–2 | 0 | Extra viscous+gyro damping above 80 km/h near center. Straight-line calm only |

### Slip Enhancement
| Parameter | Slider Range | Typical | Effect |
|-----------|-------|---------|--------|
| SlipRatioGain | 0–5 | 0.05-0.2 | Force enhancement from tire slip ratio |
| SlipAngleGain | 0–5 (physics games only) | 0 (R3E/LMU) | Enhancement from slip angle. Unreliable in column-force games — keep 0 there |
| SlipAngleShapeGain | 0–5 (physics games only) | 0 | Shape-based slip angle response |
| SlipThreshold | 0–1 | 0.1-0.25 | Slip detection threshold |
| UseFrontOnly | toggle (no slider) | true | Only use front wheel slip data |
| BrakeBoostGain | 0–1 (R3E/LMU only) | 0.2-0.5 | Force increase when braking (column-force games) |
| BrakeBoostThreshold | 0–1 (R3E/LMU only) | 0.05-0.15 | Brake pedal threshold for boost |
| GearChangeMuteEnabled | toggle (no slider) | true | Mute FFB during gear changes |
| GearSpikeThreshold | NO SLIDER (numeric) | 3000 | RPM threshold for gear spike filtering. Shifts above this RPM extend the mute window |
| RearFyScale | 0–2 (LMU only) | 0 | LMU rear-slip warning from rear Fy oscillation + yaw accel. Rear Mz is always zero in LMU telemetry |
| RearGripDetailGain | 0–2 (LMU only) | 0 | LMU rear warning from rear grip-fraction drop. Front grip lightens core; rear only warns |
| YawOnsetGain | 0–2 (LMU only) | 0 | LMU slide-onset kick strength from smoothed yaw accel |
| YawOnsetThreshold | 0–10 (LMU only) | 0 (off), ~2 to arm | LMU yaw-accel rad/s² required to fire the onset kick |
| YawOnsetDecay | NO SLIDER (LMU profile-only) | 8 | LMU onset-kick decay rate. Higher = shorter kick |
| YawOnsetMax | NO SLIDER (LMU profile-only) | 0.12 | LMU onset-kick output cap |
| SuspRoadScale | 0–2 (LMU only) | 1 | LMU mixer susp-delta road layer scale. 1 = stacked with reader envelopes |
| UseMixerSuspRoad | toggle (LMU only) | true | LMU mixer road layer gate. False = reader envelopes only |
| BrakeLoadBias | 0–1 (LMU only) | 0 | LMU front-load bias in brake boost. 0 = legacy scalar |
| SynthYawEnabled | toggle (LMU only) | true | LMU yaw-accel synthesis from rotY. Measurement only; effects stay gain-gated |
| SynthAbsEnabled | toggle (LMU only) | false (experimental) | LMU ABS-flag synthesis from front slip + brake |
| SynthTcEnabled | toggle (LMU only) | false (experimental) | LMU TC-flag synthesis from rear slip + throttle |

### Dynamic Effects (UI section: EVO/AC/R3E only)
| Parameter | Slider Range | Typical | Effect |
|-----------|-------|---------|--------|
| LateralGGain | 0–5 (UI: Cornering Force) | 0.05-0.3 | Cornering G-force feel |
| LongitudinalGGain | 0–5 (UI: Accel/Braking Force) | 0.05-0.2 | Acceleration/braking G-force |
| SuspensionGain | 0–5 (UI: Road Feel) | 0.05-0.15 | Road surface texture from suspension |
| YawRateGain | 0–5 (UI: Car Rotation Force) | 0.02-0.1 | Car rotation feel |

### Vibrations (UI section hidden for ACC — do NOT suggest vibration gains for ACC)
| Parameter | Slider Range | Typical | Effect |
|-----------|-------|---------|--------|
| MasterGain | 0–10 (UI: Vibration Master Gain) | 0.3-0.7 | Global vibration volume |
| KerbGain | 0–10 (UI: Curb Gain) | 1-2 | Curb/rumble strip intensity |
| RoadGain | 0–10 (UI: Road Gain) | 0.3-1 | Road surface texture vibration |
| SlipGain | 0–10 (UI: Slip Gain) | 0.5-1.5 | Tire slip vibration |
| AbsGain | 0–10 | 0.5-1.5 | ABS pulsing vibration |
| AbsPulseAmplitude | 0-1 | 0.2-0.5 | ABS pulse strength |
| SuspensionRoadGain | 0–10 | 0.5-2 | Suspension-based road feel |
| ScrubGain | 0–1 | 0.2-0.8 | Tire scrub texture |
| RearSlipGain | 0–1 | 0.2-0.6 | Rear wheel slip vibration |
| OfftrackGain | 0-2 | 0.3-0.8 | Off-track surface feel |
| OfftrackSeverityScale | 0-10 | 3-6 | How strongly offtrack feels |
| CurbSeverityScale | NO SLIDER (profile-only) | 5-12 | Curb impact harshness scale |
| ScrubForceScale | NO SLIDER (profile-only) | 0.0005 | Tire scrub force normalization |
| RearSlipForceScale | NO SLIDER (profile-only) | 0.0005 | Rear slip force normalization |

### Tyre Flex (UI section: EVO/AC/R3E only)
| Parameter | Slider Range | Typical | Effect |
|-----------|-------|---------|--------|
| FlexGain | 0–5 (UI: Tyre Flex Gain) | 0.03-0.12 | Tyre flex feel amplitude |
| CarcassStiffness | 0.1-5 | 0.5-2 | Tyre sidewall stiffness feel |
| FlexSmoothing | 0-0.95 | 0.3-0.8 | Smoothing on tyre flex signal |
| ContactPatchWeight | 0-2 | 0.3-0.7 | Contact patch deformation feel |
| LoadFlexGain | 0-5 | 0.1-0.5 | Load-dependent flex gain |

### Static Friction — Parking Lot Feel (UI section: per-wheel games EVO/AC/ACC only)
| Parameter | Slider Range | Typical | Effect |
|-----------|-------|---------|--------|
| Gain (StaticFrictionGain) | 0–2 | 1-1.5 | Overall elastic feel strength |
| MaxElasticStretch | 0.001–0.05 | 0.01-0.03 | Max stretch before breakaway |
| SpringStiffness | 1-50 | 20-50 | Centering spring rate |
| KineticFrictionBase | 0–1 | 0.1-0.25 | Sliding friction feel |
| EngineOffDamping | 0-0.5 | 0.3-0.7 | Extra damping when car is off |
| EngineOnDamping | 0-0.2 | 0.1-0.4 | Damping reduction when car is on |
| EngineOffScale | 0-2 | 0.5-1 | Overall scale when car is off |
| EngineOnScale | 0-1 | 0.1-0.5 | Overall scale when car is on |
| ActiveDecay | 0.5-1 | 0.5-0.95 | How fast elastic force builds when turning |
| ReturnDecay | 0.1-0.99 | 0.3-0.8 | How fast elastic force returns to center |
| OutputSmoothAlpha | 0.05-1 | 0.3-0.7 | Smoothing on static friction output |

### Grip Guard (profile-only, no sliders)
| Parameter | Slider Range | Typical | Effect |
|-----------|-------|---------|--------|
| Enabled | toggle | true | Enable grip-dependent attenuation |
| PeakSlipAngle | NO SLIDER | 0.08-0.15 | Slip angle at which grip peaks |
| AttenuationStrength | NO SLIDER | 0.3-0.7 | How much force is reduced past peak grip |
| MechanicalTrailGain | NO SLIDER | 0.05-0.3 | Simulated mechanical trail feel |

### Crash Detection (UI section: EVO/AC/ACC only)
| Parameter | Slider Range | Typical | Effect |
|-----------|-------|---------|--------|
| Enabled | toggle | true | Enable crash force limiting |
| ImpactGain | 0-1 | 0.3-0.7 | Crash force amplitude |
| SafetyClamp | 0.1-1 | 0.3-0.7 | Maximum allowed crash force |
| TriggerThresholdG | 1-10 | 3-6 | G-force threshold to trigger crash response |

### Tyre Condition
| Parameter | Slider Range | Typical | Effect |
|-----------|-------|---------|--------|
| Enabled | toggle | true | Enable tyre wear/blowout effects |
| BlowoutVibrationGain | 0-1 | 0.2-0.6 | Vibration strength during blowout |
| PressureLossGain | 0-1 | 0.1-0.4 | Feel of pressure loss |
| DamageAsymmetryGain | 0-1 | 0.1-0.4 | Asymmetric pull from damage |

### Wet Weather (per-wheel games EVO/AC/ACC only — no wet state in R3E/LMU telemetry)
| Parameter | Slider Range | Typical | Effect |
|-----------|-------|---------|--------|
| Enabled | toggle | false | Enable wet weather FFB adjustments |
| RoadVibSuppression | 0-1 | 0.5-0.9 | How much road vibration is reduced in rain |
| PeakSlipAngleMultiplier | 1-3 | 1.2-2.0 | Slip angle increase on wet surface |
| DampingReduction | 0-1 | 0.2-0.5 | How much damping is reduced in rain |
";

    /// <summary>Build the complete system context document for the LLM.</summary>
    private string BuildSystemContext(SnapshotCsvData? csvData = null, FfbProfile? profile = null, bool isAnalysis = false)
    {
        var sb = new StringBuilder();
        profile ??= _profileManager.ActiveProfile;
        var game = CurrentGame;
        var wheel = CurrentWheel;
        var isColumn = IsColumnForceGame;

        // Game & wheel section
        var gameNotes = isColumn
            ? @"- SteeringForce (NOT self-aligning torque) is provided by the game
- Reader converts SteeringForce → Mz with sqrt center ramp (0-3 degrees)
- Fx/Fy forces are synthesized from tire loads — estimates, not real physics
- Slip angle telemetry is unreliable — SlipAngleGain should be kept at 0
- CoreForceMultiplier (default 3.0x) compensates for pipeline attenuation
- BrakeBoost simulates weight transfer under braking
- MzFrontGain should be NEGATIVE (typically -0.3 to -0.5)"
            : @"- Real physics telemetry with accurate Mz, Fx, Fy channels
- Slip angle data is reliable — SlipAngleGain can be used
- MzFrontGain should generally be POSITIVE (typically 0.2 to 0.6)
- No CoreForceMultiplier or BrakeBoost — those are column-force only";

        var coachGame = CoachParameterCatalog.ParseGameName(game);
        sb.AppendLine($"## Game & Wheel");
        sb.AppendLine($"Game: {game}");
        sb.AppendLine($"Wheel: {wheel}");
        sb.AppendLine($"Pipeline: {(isColumn ? "Column-force" : "Physics-based")}");
        sb.AppendLine();
        sb.AppendLine("### Game-Specific Rules");
        sb.AppendLine(gameNotes);
        sb.AppendLine();
        sb.AppendLine("### Pipeline Path For This Game");
        sb.AppendLine(CoachParameterCatalog.PipelinePathFor(coachGame));
        sb.AppendLine();
        sb.AppendLine("### Driving Context");
        sb.AppendLine($"Car: {(string.IsNullOrWhiteSpace(CurrentCar) ? "unknown (ask the user which car)" : CurrentCar)}");
        sb.AppendLine($"Track: {(string.IsNullOrWhiteSpace(CurrentTrack) ? "unknown (ask the user which track)" : CurrentTrack)}" +
            (string.IsNullOrWhiteSpace(CurrentTrackConfig) ? "" : $" ({CurrentTrackConfig})"));
        sb.AppendLine("Different cars/tracks need different feel: high-downforce cars and bumpy tracks " +
            "want less harshness (lower vibration/road gains, more smoothing); low-grip cars and smooth " +
            "tracks can take more detail. If car/track are unknown, say which assumption you are making.");
        sb.AppendLine();
        if (profile != null)
        {
            sb.AppendLine($"### Profile Scope");
            sb.AppendLine($"Scope: {profile.Scope} | CarMatch: '{profile.CarMatch}' | TrackMatch: '{profile.TrackMatch}' | GameMatch: '{profile.GameMatch}'");
            sb.AppendLine("The Game/Car/Track in Driving Context are DETECTED FACTS — never ask which game is running. " +
                "Empty matches mean a General profile (applies everywhere), NOT an unknown game. " +
                "If Scope is PerCar/PerTrack/PerCarAndTrack, changes only affect that car/track combo. " +
                "If the user reports the issue happens everywhere, say so and suggest a General-scope change instead.");
            sb.AppendLine();
        }

        // Knowledge base
        sb.AppendLine(KnowledgeBase.Trim());
        sb.AppendLine();
        sb.AppendLine("## Authoritative Tunable Table (overrides any range above)");
        sb.AppendLine(CoachParameterCatalog.BuildKnowledgeTable(coachGame));
        sb.AppendLine();
        sb.AppendLine("## Recommendation Rules (HARD — violating responses are discarded)");
        sb.AppendLine("- `parameter` MUST be from the tunable list above for this game. Never suggest a NOT-available parameter.");
        sb.AppendLine("- `suggestedValue` MUST be inside the slider min–max. The app clamps out-of-range values, so asking outside the range is pointless.");
        sb.AppendLine("- `suggestedValue` MUST differ from `currentValue` by a meaningful step (>= 2% of the slider span). Micro-steps do nothing.");
        sb.AppendLine("- For NO SLIDER (profile-only) parameters: only suggest when nothing with a slider can fix the issue, and say explicitly the user cannot drag a slider for it.");
        sb.AppendLine("- Every recommendation needs a non-empty `reason` (WHY: which data/issues it fixes) and `impact` (what the driver will feel).");
        sb.AppendLine("- After the fix, tell the user how to verify: take a new snapshot on the same car/track and compare clipping/avg/peak numbers.");
        sb.AppendLine();

        // Current profile
        if (profile != null)
        {
            sb.AppendLine("## Current Profile Values");
            sb.AppendLine($"```json");
            sb.AppendLine(BuildProfileFullJson(profile));
            sb.AppendLine($"```");
            sb.AppendLine();
            sb.AppendLine($"Profile name: {profile.Name}");
            sb.AppendLine($"Wheel max torque: {profile.WheelMaxTorqueNm} Nm");
            sb.AppendLine($"Steering lock: {profile.SteeringLockDegrees}°");
            sb.AppendLine($"Sign correction: {(profile.SignCorrectionEnabled ? "enabled" : "disabled")}");
            sb.AppendLine();
        }

        // Telemetry data
        if (csvData != null)
        {
            sb.AppendLine($"\n## Telemetry Data\nSource: {DataSourceLabel}\nRows: {csvData.RowCount}\n");
            if (!string.IsNullOrWhiteSpace(csvData.StatsText))
            {
                sb.AppendLine("### Profiler Statistics");
                sb.AppendLine($"```");
                sb.AppendLine(csvData.StatsText);
                sb.AppendLine($"```");
                sb.AppendLine();
            }
            if (csvData.CsvLines.Count > 0)
            {
                var header = csvData.CsvLines[0];
                var totalRows = csvData.CsvLines.Count - 1;
                int sampleCount = Math.Min(200, totalRows);
                var lines = new List<string> { header };
                if (totalRows <= sampleCount)
                    lines.AddRange(csvData.CsvLines.Skip(1));
                else
                {
                    double step = (double)totalRows / sampleCount;
                    for (int i = 0; i < sampleCount; i++)
                        lines.Add(csvData.CsvLines[1 + (int)Math.Clamp(i * step, 0, totalRows - 1)]);
                }
                sb.AppendLine("### Time-Series Data (sampled)");
                sb.AppendLine($"```");
                sb.AppendLine(string.Join('\n', lines));
                sb.AppendLine($"```");
            }
            sb.AppendLine();
        }

        return sb.ToString();
    }
    public bool IsAiEnabled => _aiAnalyzer != null;
    public bool HasActiveConversation => _aiConversation != null;

    private string? _customInput;

    public void SetCustomInput(string text) => _customInput = text;

    public FfbCoachService(ProfileManager profileManager, FfbPipeline pipeline)
    {
        _profileManager = profileManager;
        _pipeline = pipeline;
    }

    public void SetAiAnalyzer(FfbAIAnalyzer? analyzer)
    {
        _aiAnalyzer = analyzer;
    }

    public List<CoachMessage> BuildWelcomeMessages()
    {
        State = CoachSessionState.SelectingSource;
        return GenerateSourceSelectionMessages();
    }

    private CoachResult GenerateSourceSelectionResult()
    {
        return new CoachResult
        {
            State = CoachSessionState.SelectingSource,
            Messages = GenerateSourceSelectionMessages()
        };
    }

    private List<CoachMessage> GenerateSourceSelectionMessages()
    {
        var files = SnapshotFileLoader.LoadSnapshotFiles();
        var answers = new List<CoachAnswer>();

        answers.Add(new CoachAnswer
        {
            Id = "source_chat",
            Label = "💬 Chat with AI",
            Description = "Ask FFB tuning questions directly"
        });

        answers.Add(new CoachAnswer
        {
            Id = "source_monitor",
            Label = "🎯 Live Monitor",
            Description = "Sample 20s of driving for AI trend analysis"
        });

        if (files.Count > 0)
        {
            answers.Add(new CoachAnswer
            {
                Id = "source_latest",
                Label = $"Snapshot Data ({files[0].Timestamp:MMM dd, HH:mm})",
                Description = files[0].ProfileName
            });

            if (files.Count > 1)
            {
                answers.Add(new CoachAnswer
                {
                    Id = "source_pick",
                    Label = "Choose a Saved Snapshot...",
                    Description = $"{files.Count} snapshots available"
                });
            }
        }

        return
        [
            new CoachMessage
            {
                Text = "🤖  Welcome! I'm your AI FFB Coach. I can analyze your driving data, or you can just ask me questions about tuning.",
                Icon = ""
            },
            new CoachMessage
            {
                Text = "Choose a data source to get started:",
                Answers = answers
            }
        ];
    }

    public async Task<CoachResult> AnalyzeSnapshotAsync(SnapshotCsvData csvData)
    {
        State = CoachSessionState.Analyzing;
        AdoptSnapshotContext(csvData);
        var ctxBits = new List<string>();
        if (!string.IsNullOrWhiteSpace(CurrentCar)) ctxBits.Add(CurrentCar);
        if (!string.IsNullOrWhiteSpace(CurrentTrack)) ctxBits.Add(CurrentTrack);
        DataSourceLabel = $"Snapshot: {csvData.ProfileName} ({csvData.RowCount} rows)" +
            (ctxBits.Count > 0 ? $" — {string.Join(" @ ", ctxBits)}" : "");
        CurrentProfileName = csvData.ProfileName;
        _lastCsvData = csvData;
        _lastLiveStats = null;

        var profile = FindMatchingProfile(csvData.ProfileName);
        _lastResult = await AnalyzeDataAsync(csvData, profile);
        return _lastResult;
    }

    public async Task<CoachResult> AnalyzeLiveDataAsync(LiveProfilerStats stats, string profileName)
    {
        State = CoachSessionState.Analyzing;
        var ctxBits = new List<string>();
        if (!string.IsNullOrWhiteSpace(CurrentCar)) ctxBits.Add(CurrentCar);
        if (!string.IsNullOrWhiteSpace(CurrentTrack)) ctxBits.Add(CurrentTrack);
        DataSourceLabel = $"Live Data ({stats.FrameCount} frames)" +
            (ctxBits.Count > 0 ? $" — {string.Join(" @ ", ctxBits)}" : "");
        CurrentProfileName = profileName;
        _lastLiveStats = stats;
        _lastCsvData = null;

        var profile = FindMatchingProfile(profileName);
        _lastResult = await AnalyzeLiveStatsAsync(stats, profile);
        return _lastResult;
    }

    public async Task<CoachResult> StartChatSessionAsync()
    {
        if (_aiAnalyzer == null)
        {
            State = CoachSessionState.SelectingSource;
            return new CoachResult { State = State, Messages = [.. GenerateSourceSelectionMessages()] };
        }

        var profile = _profileManager.ActiveProfile;
        DataSourceLabel = "Direct Chat";

        var context = BuildChatSystemPrompt(profile);
        _aiConversation =
        [
            new() { Role = "system", Content = context },
            new() { Role = "user", Content = "Greet the user and ask what they'd like help with regarding their FFB tuning." }
        ];

        try
        {
            var aiResult = await _aiAnalyzer.ChatAsync(_aiConversation);
            _aiConversation.Add(new ChatMessage { Role = "assistant", Content = aiResult.Reply });

            var messages = new List<CoachMessage>
            {
                new() { Text = aiResult.Reply, Icon = aiResult.Icon ?? "🤖" }
            };

            var answers = aiResult.Answers.Count > 0
                ? aiResult.Answers.Select(a => new CoachAnswer { Id = a.Id, Label = a.Label, Description = a.Description }).ToList()
                : new List<CoachAnswer> { new() { Id = "finish", Label = "Finish", Description = "Done chatting" } };

            messages.Add(new CoachMessage { Text = "What next?", Answers = answers });

            State = CoachSessionState.Questioning;
            return new CoachResult { State = State, Messages = messages };
        }
        catch (Exception ex)
        {
            _aiConversation = null;
            return new CoachResult
            {
                State = CoachSessionState.SelectingSource,
                Messages =
                [
                    new CoachMessage { Text = $"⚠️ AI chat failed: {ex.Message}", Icon = "⚠️" },
                    .. GenerateSourceSelectionMessages()
                ]
            };
        }
    }

    private string BuildChatSystemPrompt(FfbProfile? profile)
    {
        var game = CurrentGame;
        var wheel = CurrentWheel;
        var isColumn = IsColumnForceGame;

        var gameSpecific = isColumn
            ? @"- Steering force maps to Mz with a sqrt center ramp (0-3 degrees)
- Fx/Fy forces are synthesized from tire loads (estimates, not real physics)
- Slip angle telemetry is unreliable — SlipAngleGain should stay at 0
- CoreForceMultiplier default is 3.0x — this is a key gain for overall feel
- BrakeBoost increases force when braking (simulates weight transfer)
- CenterSharpnessDegrees applies a smoothstep ramp (0 = disabled, uses reader shaping)"
            : @"- Real physics telemetry with accurate Mz, Fx, Fy channels
- Slip angle data is reliable — SlipAngleGain can be used
- MzFrontGain should generally be positive
- No BrakeBoost or CoreForceMultiplier concept — those are R3E/LMU specific";

        var coachGame = CoachParameterCatalog.ParseGameName(game);
        return $@"You are an expert sim racing FFB tuner integrated into the FFB Coach tool.

You help the user tune their force feedback settings by answering questions and giving advice.
You have access to their current FFB profile and understand the game and wheel they use.

## Scope Boundary
You ONLY discuss FFB tuning, force feedback, telemetry analysis, wheel setup, and sim racing feel.
If the user asks about anything else (weather, news, coding, general knowledge, etc.), politely refuse:
""I'm an FFB tuning assistant and can only help with force feedback and sim racing setup questions.""
Do not generate off-topic content. This saves tokens and keeps the conversation focused.

## Game: {game}
{wheel}

## Game-Specific Rules
{gameSpecific}

## Pipeline Path For This Game
{CoachParameterCatalog.PipelinePathFor(coachGame)}

## Driving Context
Car: {(string.IsNullOrWhiteSpace(CurrentCar) ? "unknown (ask the user which car)" : CurrentCar)}
Track: {(string.IsNullOrWhiteSpace(CurrentTrack) ? "unknown (ask the user which track)" : CurrentTrack)}{(string.IsNullOrWhiteSpace(CurrentTrackConfig) ? "" : $" ({CurrentTrackConfig})")}
Game/Car/Track above are DETECTED FACTS — never ask which game is running, and never treat empty profile matches as an unknown game.
Profiles can be scoped per car/track (Scope/CarMatch/TrackMatch on the profile). If the issue is corner-specific, say which kind of corner; if it happens everywhere, say so. After any change, tell the user to verify with a new snapshot on the same car/track.

{CoachParameterCatalog.BuildKnowledgeTable(coachGame)}

## Recommendation Rules (HARD — violating responses are discarded)
- `parameter` MUST be tunable for this game (see table). Never suggest a NOT-available parameter.
- `suggestedValue` MUST be inside the slider min–max and differ from `currentValue` by >= 2% of the slider span.
- For NO SLIDER (profile-only) parameters: only suggest when nothing with a slider can fix it, and say there is no slider.
- Every recommendation needs `reason` (WHY it fixes the issue) and `impact` (what the driver will feel).

## CRITICAL: Response Format
You MUST respond with valid JSON ONLY. No exceptions. Every response must be a JSON object with the following structure.
Put ALL conversational text in the ``reply`` field. Do NOT write any text outside the JSON structure.

```json
{{
  ""reply"": ""Your full conversational response here. Be helpful, detailed, and specific. This is the only place you write text that the user sees."",
  ""icon"": ""🤖"",
  ""recommendations"": [
    {{
      ""parameter"": ""OutputGain"",
      ""currentValue"": 0.8,
      ""suggestedValue"": 0.64,
      ""reason"": ""Clear explanation of why this change helps the specific issue"",
      ""impact"": ""What the driver will feel on track after the change""
    }}
  ],
  ""answers"": [
    {{ ""id"": ""apply"", ""label"": ""Apply Change"", ""description"": ""Apply this recommendation"" }},
    {{ ""id"": ""skip"", ""label"": ""Skip"", ""description"": ""Keep current value"" }},
    {{ ""id"": ""finish"", ""label"": ""Finish"", ""description"": ""Done for now"" }}
  ]
}}
```

Rules:
- ALL text goes inside ``reply``. No text outside the JSON.
- Include ``recommendations`` when suggesting changes. Set to [] if unsure.
- ``answers`` is for YOUR follow-up questions only (e.g. continue/finish or clarifying feel).
  Do NOT emit apply/skip/apply_* buttons — the app adds one Apply button per recommendation automatically.
- The ``parameter`` name must be exact (case-insensitive, spaces ignored).

## Exact Parameter Names
Use these EXACT names in the ""parameter"" field, ignoring spaces and case. System normalizes automatically.

Core: OutputGain, CompressionPower, ForceScale, SoftClipThreshold, CoreForceMultiplier
Mix: MzFrontGain, FxFrontGain, FyFrontGain, MzRearGain, WheelLoadWeighting, MzScale, FxScale, FyScale
Damping: ViscousDamping, SpeedDamping, Friction, Inertia, MaxSpeedReference, CenterDampFloor, StraightGain
Slip: SlipRatioGain, SlipAngleGain, SlipAngleShapeGain, SlipThreshold, UseFrontOnly, BrakeBoostGain, BrakeBoostThreshold, GearSpikeThreshold, RearFyScale, RearGripDetailGain, YawOnsetGain, YawOnsetThreshold, YawOnsetDecay, YawOnsetMax, SuspRoadScale, UseMixerSuspRoad, BrakeLoadBias, SynthYawEnabled, SynthAbsEnabled, SynthTcEnabled
Dynamic: LateralGGain, LongitudinalGGain, SuspensionGain, YawRateGain
Vibrations: MasterGain, KerbGain, RoadGain, SlipGain, AbsGain, AbsPulseAmplitude, SuspensionRoadGain, ScrubGain, RearSlipGain, OfftrackGain, OfftrackSeverityScale, CurbSeverityScale, ScrubForceScale, RearSlipForceScale
Advanced: MaxSlewRate, CenterSuppressionDegrees, CenterSharpnessDegrees, CenterKneePower, CenterBlendDegrees, HysteresisThreshold, NoiseFloor, HysteresisWatchdogFrames, LowSpeedSmoothKmh
TyreFlex: FlexGain, CarcassStiffness, FlexSmoothing, ContactPatchWeight, LoadFlexGain
GripGuard: GripGuardEnabled, GripGuardPeakSlipAngle, GripGuardAttenuationStrength, GripGuardMechanicalTrailGain
Crash: CrashEnabled, CrashImpactGain, CrashSafetyClamp, CrashTriggerThresholdG
TyreCondition: TyreConditionEnabled, BlowoutVibrationGain, PressureLossGain, DamageAsymmetryGain
WetWeather: WetWeatherEnabled, WetRoadVibSuppression, WetPeakSlipAngleMultiplier, WetDampingReduction
StaticFriction: StaticFrictionGain, StaticFrictionMaxElasticStretch, StaticFrictionSpringStiffness, StaticFrictionKineticFrictionBase, StaticFrictionEngineOffDamping, StaticFrictionEngineOnDamping, StaticFrictionEngineOffScale, StaticFrictionEngineOnScale, StaticFrictionActiveDecay, StaticFrictionReturnDecay, StaticFrictionOutputSmoothAlpha

## Current Profile Values
```json
{(profile != null ? BuildProfileFullJson(profile) : "No profile loaded")}
```

Profile name: {profile?.Name ?? "None"}
Wheel max torque: {profile?.WheelMaxTorqueNm ?? 0} Nm
Steering lock: {profile?.SteeringLockDegrees ?? 0}°";
    }

    private string BuildChatUserPrompt(FfbProfile? profile)
    {
        var profileJson = profile != null ? BuildProfileFullJson(profile) : "No profile loaded";
        return $@"## Context
Game: {CurrentGame}
Wheel: {CurrentWheel}
Profile name: {profile?.Name ?? "None"}

## Current Profile
{profileJson}

The user wants to chat about their FFB tuning. Greet them and ask what they'd like help with.";
    }

    public async Task<CoachResult> ProcessAnswerAsync(string answerId, List<CoachMessage> conversationHistory)
    {
        // Direct chat start — handled specially
        if (answerId == "source_chat")
            return await StartChatSessionAsync();

        // Navigation answers — always handled
        if (answerId == "restart" || answerId == "source_latest" || answerId == "source_live"
            || answerId == "source_monitor" || answerId == "source_pick" || answerId == "go_back")
            return GenerateSourceSelectionResult();

        // AI path — every answer goes through the LLM
        if (_aiAnalyzer != null && _aiConversation != null)
            return await ProcessAiAnswerAsync(answerId, conversationHistory);
        if (answerId == "__custom__" && _aiConversation != null)
            return await ProcessAiAnswerAsync(answerId, conversationHistory);

        // AI not configured — show source selection as minimal fallback
        State = CoachSessionState.SelectingSource;
        return GenerateSourceSelectionResult();
    }

    private async Task<CoachResult> ProcessAiAnswerAsync(string answerId, List<CoachMessage> conversationHistory)
    {
        if (answerId == "finish" || answerId == "done")
            return BuildSummaryMessages();

        if (answerId == "restart" || answerId == "source_latest" || answerId == "source_live"
            || answerId == "source_monitor" || answerId == "source_pick" || answerId == "go_back"
            )
            return GenerateSourceSelectionResult();

        // Apply flows — per-recommendation buttons (apply_0..N) or legacy apply (first).
        // The applied rec is REMOVED from the pending list; the rest stay applicable,
        // so longer chats keep working Apply buttons for every suggestion.
        if ((answerId == "apply" || answerId.StartsWith("apply_")) && _lastResult?.Recommendations.Count > 0)
        {
            int index = 0;
            if (answerId.StartsWith("apply_") && !int.TryParse(answerId["apply_".Length..], out index))
                index = 0;
            if (index < 0 || index >= _lastResult.Recommendations.Count)
            {
                return new CoachResult
                {
                    State = CoachSessionState.Questioning,
                    Messages = [new CoachMessage { Text = "That suggestion is no longer pending (already applied or skipped).", Icon = "ℹ️" }]
                };
            }
            var rec = _lastResult.Recommendations[index];
            var result = ApplyRecommendation(rec);
            if (result.ProfileChanged)
            {
                _lastResult.Recommendations.RemoveAt(index);
                _lastResult = _lastResult with
                {
                    PendingRecommendation = _lastResult.Recommendations.Count > 0
                        ? _lastResult.Recommendations[Math.Min(index, _lastResult.Recommendations.Count - 1)]
                        : null
                };
                RefreshConversationProfile();
                // Chain remaining recs as follow-up buttons so the user can keep applying.
                if (_lastResult.Recommendations.Count > 0)
                {
                    var followUps = BuildApplyButtons(_lastResult.Recommendations);
                    followUps.Add(new CoachAnswer { Id = "finish", Label = "Done", Description = "See summary" });
                    result.Messages.Add(new CoachMessage { Text = "More pending suggestions:", Answers = followUps });
                }
            }
            return result;
        }

        // Skip the current (first) pending suggestion without an LLM round-trip.
        if (answerId == "skip" && _lastResult?.Recommendations.Count > 0)
        {
            var skipped = _lastResult.Recommendations[0];
            _lastResult.Recommendations.RemoveAt(0);
            _lastResult = _lastResult with
            {
                PendingRecommendation = _lastResult.Recommendations.Count > 0 ? _lastResult.Recommendations[0] : null
            };
            if (_lastResult.Recommendations.Count == 0)
            {
                return new CoachResult
                {
                    State = CoachSessionState.Questioning,
                    Messages =
                    [
                        new CoachMessage { Text = $"Skipped {skipped.Parameter} — no more pending suggestions.", Icon = "⏭" },
                        new CoachMessage
                        {
                            Text = "What next?",
                            Answers =
                            [
                                new CoachAnswer { Id = "continue", Label = "Continue", Description = "More adjustments" },
                                new CoachAnswer { Id = "finish", Label = "Finish", Description = "See summary" }
                            ]
                        }
                    ]
                };
            }
            return ShowRecommendationsFromLastResult($"Skipped {skipped.Parameter}. ");
        }

        if (answerId == "talk" && _lastResult?.Recommendations.Count > 0)
        {
            return ShowRecommendationsFromLastResult();
        }

        try
        {
            string userMessage = answerId switch
            {
                "continue" => "I'd like to continue with the tuning. Tell me what to adjust first.",
                "__custom__" => _customInput ?? "Let me describe what I'm feeling...",
                "retry_json" => "Your last response was not valid JSON. Please respond again using ONLY the JSON format with reply, recommendations, and answers fields. Put all text inside the reply field.",
                _ => ""
            };
            _customInput = null;
            if (string.IsNullOrWhiteSpace(userMessage))
            {
                string lastAnswer = conversationHistory.Count > 0
                    ? conversationHistory[^1].Text
                    : "";
                userMessage = $"User chose: {answerId}";
                if (!string.IsNullOrWhiteSpace(lastAnswer))
                    userMessage += $" (they said: \"{lastAnswer}\")";
            }

            userMessage += $"\n\nWheel: {CurrentWheel} | Game: {CurrentGame} | Profile: {_profileManager.ActiveProfile?.Name ?? "Unknown"} | Source: {DataSourceLabel}";

            _aiConversation!.Add(new ChatMessage { Role = "user", Content = userMessage });

            var aiResult = await _aiAnalyzer!.ChatAsync(_aiConversation);

            _aiConversation.Add(new ChatMessage { Role = "assistant", Content = aiResult.Reply });

            var messages = new List<CoachMessage>
            {
                new()
                {
                    Text = aiResult.Reply,
                    Icon = aiResult.Icon ?? "🤖"
                }
            };

            var recs = new List<FfbRecommendation>();
            FfbRecommendation? pendingRec = null;
            var droppedNotes = new List<string>();
            var profileNow = _profileManager.ActiveProfile;

            var recText = new StringBuilder();
            int shown = 0;
            for (int ri = 0; ri < (aiResult.Recommendations?.Count ?? 0); ri++)
            {
                var r = aiResult.Recommendations![ri];
                FfbRecommendation fr;
                if (profileNow != null)
                {
                    var validated = ValidateRecommendation(
                        r.Parameter, r.Reason, r.Reason,
                        r.CurrentValue, r.SuggestedValue, r.Impact,
                        profileNow, droppedNotes);
                    if (validated == null) continue;
                    fr = validated;
                }
                else
                {
                    fr = new FfbRecommendation
                    {
                        Type = RecommendationType.ProfileChange,
                        Parameter = r.Parameter,
                        Description = r.Reason,
                        CurrentValue = r.CurrentValue,
                        SuggestedValue = r.SuggestedValue,
                        Impact = r.Impact
                    };
                }
                recs.Add(fr);
                pendingRec ??= fr;

                if (shown == 0) recText.AppendLine("**Suggested adjustments:**\n");
                shown++;
                recText.AppendLine($"**{shown}. {fr.Parameter}**");
                recText.AppendLine($"   {fr.CurrentValue:F3}  →  **{fr.SuggestedValue:F3}**");
                recText.AppendLine($"   {fr.Impact}");
                recText.AppendLine();
            }

            if (droppedNotes.Count > 0)
            {
                if (recText.Length > 0) recText.AppendLine();
                recText.AppendLine("Skipped (would do nothing here):");
                foreach (var n in droppedNotes)
                    recText.AppendLine($"- {n}");
            }

            if (recText.Length > 0)
            {
                messages.Add(new CoachMessage
                {
                    Text = recText.ToString().TrimEnd(),
                    Icon = "🔧"
                });
            }

            // Deterministic buttons: the app owns apply/skip (one Apply per rec),
            // the AI only contributes its own follow-up answers. This guarantees an
            // Apply button for EVERY profile change, even in long conversations.
            var answers = new List<CoachAnswer>();
            if (recs.Count > 0)
                answers.AddRange(BuildApplyButtons(recs));
            foreach (var a in aiResult.Answers)
            {
                var id = a.Id ?? "";
                if (id == "apply" || id == "skip" || id.StartsWith("apply_"))
                    continue;
                if (answers.Any(x => x.Id == id))
                    continue;
                answers.Add(new CoachAnswer
                {
                    Id = string.IsNullOrWhiteSpace(a.Id) ? $"q_{answers.Count}" : a.Id,
                    Label = a.Label,
                    Description = a.Description
                });
            }

            if (answers.Count == 0)
            {
                answers.Add(new CoachAnswer { Id = "continue", Label = "Continue", Description = "More adjustments" });
                answers.Add(new CoachAnswer { Id = "finish", Label = "Finish", Description = "See summary" });
            }
            else if (answers.All(a => a.Id != "finish" && a.Id != "done"))
            {
                answers.Add(new CoachAnswer { Id = "finish", Label = "Finish", Description = "See summary" });
            }

            messages.Add(new CoachMessage
            {
                Text = "What next?",
                Answers = answers
            });

            State = CoachSessionState.Questioning;

            _lastResult = new CoachResult
            {
                State = State,
                Messages = messages,
                Recommendations = recs,
                PendingRecommendation = pendingRec,
                CsvData = _lastCsvData,
                LiveStats = _lastLiveStats
            };

            return _lastResult;
        }
        catch (Exception ex)
        {
            return new CoachResult
            {
                State = CoachSessionState.Questioning,
                Messages =
                [
                    new CoachMessage { Text = $"AI error: {ex.Message}. Type your response below and I'll try again.", Icon = "⚠️" }
                ]
            };
        }
    }

    /// <summary>
    /// One Apply button per pending recommendation — every profile change the coach
    /// suggests must be one click away, in chat as well as in longer conversations.
    /// </summary>
    private static List<CoachAnswer> BuildApplyButtons(List<FfbRecommendation> recs)
    {
        var answers = new List<CoachAnswer>();
        for (int i = 0; i < recs.Count; i++)
        {
            var r = recs[i];
            answers.Add(new CoachAnswer
            {
                Id = $"apply_{i}",
                Label = recs.Count == 1 ? "Apply Change" : $"Apply #{i + 1}: {r.Parameter}",
                Description = $"{r.CurrentValue:F2} → {r.SuggestedValue:F2}"
            });
        }
        answers.Add(new CoachAnswer { Id = "skip", Label = "Skip", Description = "Skip the first suggestion" });
        return answers;
    }

    private CoachResult ShowRecommendationsFromLastResult(string prefix = "")
    {
        var recs = _lastResult?.Recommendations;
        if (recs == null || recs.Count == 0)
        {
            State = CoachSessionState.Questioning;
            return new CoachResult
            {
                State = State,
                Messages =
                [
                    new CoachMessage { Text = "No recommendations to show. Try starting a new analysis.", Icon = "ℹ️" }
                ]
            };
        }

        var pendingRec = recs[0];
        var itemList = new System.Text.StringBuilder();
        itemList.AppendLine("**Suggested adjustments:**\n");
        for (int i = 0; i < recs.Count; i++)
        {
            var r = recs[i];
            itemList.AppendLine($"**{i + 1}. {r.Parameter}**");
            itemList.AppendLine($"   {r.CurrentValue:F3}  →  **{r.SuggestedValue:F3}**");
            itemList.AppendLine($"   {r.Impact}");
            itemList.AppendLine();
        }

        var messages = new List<CoachMessage>
        {
            new()
            {
                Text = itemList.ToString().TrimEnd(),
                Icon = "🔧"
            }
        };

        var answers = BuildApplyButtons(recs);
        messages.Add(new CoachMessage
        {
            Text = $"{prefix}Which would you like to apply? Each change applies immediately and is recorded in history (Undo available).",
            Answers = answers
        });

        State = CoachSessionState.Questioning;
        _lastResult = new CoachResult
        {
            State = State,
            Messages = messages,
            Recommendations = recs,
            PendingRecommendation = pendingRec,
            CsvData = _lastCsvData,
            LiveStats = _lastLiveStats
        };
        return _lastResult;
    }

    public CoachResult ApplyRecommendation(FfbRecommendation rec)
    {
        var profile = _profileManager.ActiveProfile;
        if (profile == null)
            return new CoachResult
            {
                State = CoachSessionState.Idle,
                Messages =
                [
                    new CoachMessage { Text = "No active profile to modify.", IsUser = false }
                ]
            };

        // Defensive: re-check applicability + clamp at apply time (game may have
        // switched since the recommendation was created).
        var canonical = NormalizeParam(rec.Parameter);
        if (CoachParameterCatalog.TryGet(canonical, out var info) && info != null)
        {
            if (!CoachParameterCatalog.IsApplicable(info, ActiveCoachGame))
            {
                return new CoachResult
                {
                    State = CoachSessionState.Questioning,
                    Messages =
                    [
                        new CoachMessage
                        {
                            Text = $"Not applying {info.Name}: it has no effect for " +
                                $"{CoachParameterCatalog.GameShortName(ActiveCoachGame)} " +
                                $"(only: {string.Join("/", info.ApplicableGames.Select(CoachParameterCatalog.GameShortName))}).",
                            Icon = "🚫"
                        }
                    ]
                };
            }
            float clamped = CoachParameterCatalog.ClampToSlider(info, rec.SuggestedValue);
            if (Math.Abs(clamped - rec.SuggestedValue) > 1e-6f)
            {
                rec = new FfbRecommendation
                {
                    Type = rec.Type,
                    Parameter = rec.Parameter,
                    Description = rec.Description,
                    Reason = rec.Reason,
                    CurrentValue = rec.CurrentValue,
                    SuggestedValue = clamped,
                    Impact = rec.Impact
                };
            }
        }

        float oldValue = ReadProfileValue(profile, rec.Parameter);
        WriteProfileValue(profile, rec.Parameter, rec.SuggestedValue);
        profile.ApplyToPipeline(_pipeline);
        _profileManager.SaveProfile(profile);
        _history.Add(new CoachAppliedChange
        {
            Parameter = NormalizeParam(rec.Parameter),
            OldValue = oldValue,
            NewValue = rec.SuggestedValue,
            Reason = rec.Reason ?? rec.Description ?? "",
            Game = CurrentGame,
            Car = CurrentCar,
            Track = CurrentTrack,
            ProfileName = profile.Name
        });
        State = CoachSessionState.Applying;

        return new CoachResult
        {
            State = CoachSessionState.Questioning,
            ProfileChanged = true,
            Messages =
            [
                new CoachMessage
                {
                    Text = $"✓ Applied: {rec.Parameter}: {rec.CurrentValue:F3} → {rec.SuggestedValue:F3}",
                    Icon = "✓"
                },
                new CoachMessage
                {
                    Text = $"*{rec.Impact}*\n\nGive it a test drive and let me know how it feels! Use **Undo** if it doesn't feel right.\n\nTo verify with data: take a new snapshot on the same car/track{(string.IsNullOrWhiteSpace(CurrentCar) ? "" : $" ({CurrentCar} @ {CurrentTrack})")} and compare clipping/avg/peak numbers.",
                    Answers =
                    [
                        new CoachAnswer { Id = "continue", Label = "Continue Tuning" },
                        new CoachAnswer { Id = "finish", Label = "See Summary" }
                    ]
                }
            ]
        };
    }

    /// <summary>Full apply history, newest last. Survives across turns until Reset.</summary>
    public IReadOnlyList<CoachAppliedChange> AppliedHistory => _history;

    public CoachResult UndoLastChange()
    {
        if (_history.Count > 0)
        {
            var last = _history[^1];
            _history.RemoveAt(_history.Count - 1);
            var profile = _profileManager.ActiveProfile;
            if (profile != null)
            {
                WriteProfileValue(profile, last.Parameter, last.OldValue);
                profile.ApplyToPipeline(_pipeline);
                _profileManager.SaveProfile(profile);
                RefreshConversationProfile();
            }
            return new CoachResult
            {
                State = CoachSessionState.Questioning,
                ProfileChanged = true,
                Messages =
                [
                    new CoachMessage { Text = $"↩ Reverted {last.Parameter}: {last.NewValue:F3} → {last.OldValue:F3} ({_history.Count} change(s) still applied).", Icon = "↩" }
                ]
            };
        }

        return new CoachResult
        {
            State = CoachSessionState.Questioning,
            Messages =
            [
                new CoachMessage { Text = "No changes to undo." }
            ]
        };
    }

    /// <summary>
    /// Revert one specific history entry. Refuses when the current value no longer
    /// matches what the entry applied (a later change superseded it) — revert the
    /// newer entry first instead.
    /// </summary>
    public CoachResult RevertChange(Guid id)
    {
        var entry = _history.FirstOrDefault(h => h.Id == id);
        if (entry == null)
        {
            return new CoachResult
            {
                State = CoachSessionState.Questioning,
                Messages = [new CoachMessage { Text = "That history entry is gone already.", Icon = "ℹ️" }]
            };
        }

        var profile = _profileManager.ActiveProfile;
        if (profile == null)
        {
            return new CoachResult
            {
                State = CoachSessionState.Questioning,
                Messages = [new CoachMessage { Text = "No active profile to modify.", Icon = "⚠️" }]
            };
        }

        float current = ReadProfileValue(profile, entry.Parameter);
        if (Math.Abs(current - entry.NewValue) > 1e-6f)
        {
            return new CoachResult
            {
                State = CoachSessionState.Questioning,
                Messages =
                [
                    new CoachMessage
                    {
                        Text = $"Can't revert {entry.Parameter} to {entry.OldValue:F3}: it is now {current:F3} (changed after this entry was applied). Revert the newer change first, or apply a manual value.",
                        Icon = "⚠️"
                    }
                ]
            };
        }

        _history.Remove(entry);
        WriteProfileValue(profile, entry.Parameter, entry.OldValue);
        profile.ApplyToPipeline(_pipeline);
        _profileManager.SaveProfile(profile);
        RefreshConversationProfile();
        return new CoachResult
        {
            State = CoachSessionState.Questioning,
            ProfileChanged = true,
            Messages =
            [
                new CoachMessage { Text = $"↩ Reverted {entry.Parameter}: {entry.NewValue:F3} → {entry.OldValue:F3} ({_history.Count} change(s) still applied).", Icon = "↩" }
            ]
        };
    }

    public CoachResult BuildSummary()
    {
        State = CoachSessionState.Summary;
        return BuildSummaryMessages();
    }

    private CoachResult HandleApplyAnswer()
    {
        var rec = _lastResult?.PendingRecommendation;
        if (rec == null)
            return new CoachResult
            {
                State = CoachSessionState.Questioning,
                Messages =
                [
                    new CoachMessage { Text = "No pending recommendation to apply.", Icon = "⚠️" }
                ]
            };

        return ApplyRecommendation(rec);
    }

    private CoachResult ApplyOutputGainChange(float multiplier)
    {
        var profile = _profileManager.ActiveProfile;
        if (profile == null) return BuildSummaryMessages();

        float current = profile.OutputGain;
        float suggested = multiplier > 1f
            ? MathF.Min(1.2f, current * multiplier)
            : MathF.Max(0.2f, current * multiplier);

        var rec = new FfbRecommendation
        {
            Type = RecommendationType.ProfileChange,
            Parameter = "OutputGain",
            Description = multiplier > 1f ? "Increase output gain" : "Decrease output gain",
            CurrentValue = current,
            SuggestedValue = suggested,
            Impact = multiplier > 1f ? "Stronger overall FFB" : "Weaker overall FFB, more headroom"
        };

        var result = ApplyRecommendation(rec);
        return new CoachResult
        {
            State = result.State,
            Messages = result.Messages
        };
    }

    private CoachResult ApplyDampingChange(string type)
    {
        var profile = _profileManager.ActiveProfile;
        if (profile == null) return BuildSummaryMessages();

        string param = type;
        float current = ReadProfileValue(profile, param);
        float suggested = current * (current < 0.1f ? 1.5f : 1.2f);

        var rec = new FfbRecommendation
        {
            Type = RecommendationType.ProfileChange,
            Parameter = param,
            Description = $"Adjust {param}",
            CurrentValue = current,
            SuggestedValue = MathF.Min(1.0f, suggested),
            Impact = "Changed damping feel"
        };

        var result = ApplyRecommendation(rec);
        return new CoachResult
        {
            State = result.State,
            Messages = result.Messages
        };
    }

    private CoachResult ApplyVibrationChange(string type)
    {
        var profile = _profileManager.ActiveProfile;
        if (profile == null) return BuildSummaryMessages();

        float current = ReadProfileValue(profile, type);
        float suggested = current * 0.7f;

        var rec = new FfbRecommendation
        {
            Type = RecommendationType.ProfileChange,
            Parameter = type,
            Description = $"Reduce {type}",
            CurrentValue = current,
            SuggestedValue = MathF.Max(0f, suggested),
            Impact = "Reduced vibration feel"
        };

        var result = ApplyRecommendation(rec);
        return new CoachResult
        {
            State = result.State,
            Messages = result.Messages
        };
    }

    public void Reset()
    {
        State = CoachSessionState.Idle;
        _history.Clear();
        CurrentProfileName = "";
        DataSourceLabel = "";
        _lastResult = null;
        _lastCsvData = null;
        _lastLiveStats = null;
        _aiConversation = null;
    }

    private static string BuildProfileFullJson(FfbProfile profile)
    {
        return JsonSerializer.Serialize(new
        {
            profile.OutputGain,
            profile.ForceScale,
            profile.CompressionPower,
            profile.SoftClipThreshold,
            profile.SignCorrectionEnabled,
            ChannelMix = new { MzFront = profile.MzFront?.Gain, FxFront = profile.FxFront?.Gain, FyFront = profile.FyFront?.Gain, MzRear = profile.MzRear?.Gain, profile.WheelLoadWeighting, profile.MzScale, profile.FxScale, profile.FyScale },
            Damping = new { profile.Damping?.ViscousDamping, profile.Damping?.SpeedDamping, profile.Damping?.Friction, profile.Damping?.Inertia, MaxSpeedReference = profile.Damping?.MaxSpeedReference, profile.Damping?.CenterDampFloor, profile.Damping?.StraightGain },
            Slip = new { profile.Slip?.SlipRatioGain, profile.Slip?.SlipAngleGain, profile.Slip?.SlipAngleShapeGain, profile.Slip?.SlipThreshold, profile.Slip?.UseFrontOnly, profile.Slip?.CoreForceMultiplier, profile.Slip?.BrakeBoostGain, profile.Slip?.BrakeBoostThreshold, profile.Slip?.GearChangeMuteEnabled, profile.Slip?.GearSpikeThreshold, profile.Slip?.RearFyScale, profile.Slip?.RearGripDetailGain, profile.Slip?.YawOnsetGain, profile.Slip?.YawOnsetThreshold, profile.Slip?.YawOnsetDecay, profile.Slip?.YawOnsetMax, profile.Slip?.SuspRoadScale, profile.Slip?.UseMixerSuspRoad, profile.Slip?.BrakeLoadBias, profile.Slip?.SynthYawEnabled, profile.Slip?.SynthAbsEnabled, profile.Slip?.SynthTcEnabled },
            Dynamic = new { profile.Dynamic?.LateralGGain, profile.Dynamic?.LongitudinalGGain, profile.Dynamic?.SuspensionGain, profile.Dynamic?.YawRateGain },
            Vibrations = new { profile.Vibrations?.KerbGain, profile.Vibrations?.RoadGain, profile.Vibrations?.SlipGain, profile.Vibrations?.AbsGain, profile.Vibrations?.AbsPulseAmplitude, profile.Vibrations?.SuspensionRoadGain, profile.Vibrations?.ScrubGain, profile.Vibrations?.RearSlipGain, profile.Vibrations?.OfftrackGain, profile.Vibrations?.OfftrackSeverityScale, profile.Vibrations?.CurbSeverityScale, profile.Vibrations?.ScrubForceScale, profile.Vibrations?.RearSlipForceScale, profile.Vibrations?.MasterGain },
            Advanced = new { profile.Advanced?.MaxSlewRate, profile.Advanced?.CenterSuppressionDegrees, profile.Advanced?.CenterSharpnessDegrees, profile.Advanced?.CenterKneePower, profile.Advanced?.CenterBlendDegrees, profile.Advanced?.HysteresisThreshold, profile.Advanced?.NoiseFloor, profile.Advanced?.HysteresisWatchdogFrames, profile.Advanced?.CoreForceMultiplier, profile.Advanced?.LowSpeedSmoothKmh },
            TyreFlex = new { profile.TyreFlex?.FlexGain, profile.TyreFlex?.CarcassStiffness, profile.TyreFlex?.FlexSmoothing, profile.TyreFlex?.ContactPatchWeight, profile.TyreFlex?.LoadFlexGain },
            StaticFriction = new { profile.StaticFriction?.Gain, profile.StaticFriction?.MaxElasticStretch, profile.StaticFriction?.SpringStiffness, profile.StaticFriction?.KineticFrictionBase },
            GripGuard = new { profile.GripGuard?.Enabled, profile.GripGuard?.PeakSlipAngle, profile.GripGuard?.AttenuationStrength, profile.GripGuard?.MechanicalTrailGain },
            Crash = new { profile.Crash?.Enabled, profile.Crash?.ImpactGain, profile.Crash?.SafetyClamp, profile.Crash?.TriggerThresholdG },
            TyreCondition = new { profile.TyreCondition?.Enabled, BlowoutVibrationGain = profile.TyreCondition?.BlowoutVibrationGain, PressureLossGain = profile.TyreCondition?.PressureLossGain, DamageAsymmetryGain = profile.TyreCondition?.DamageAsymmetryGain },
            WetWeather = new { profile.WetWeather?.Enabled, profile.WetWeather?.RoadVibSuppression, profile.WetWeather?.PeakSlipAngleMultiplier, profile.WetWeather?.DampingReduction }
        }, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
    }

    private static List<string> RunDiagnostics(FfbProfile profile, bool isColumnForce)
    {
        var issues = new List<string>();

        // UNIVERSAL CHECK — applies to ALL games
        // SignCorrectionEnabled inverts the FFB direction. If disabled, the wheel
        // fights steering inputs regardless of any gain settings.
        if (!profile.SignCorrectionEnabled)
            issues.Add("CRITICAL: Sign correction is DISABLED. This inverts the FFB direction — "
                + "the wheel will pull away from center instead of returning to it. "
                + "Enable this for all games.");

        // GAME-SPECIFIC: MzFrontGain sign
        // Physics-based games (EVO, ACC, AC): Mz is real telemetry, gain should be POSITIVE (0.2-0.6)
        // Column-force games (R3E, LMU): Mz is synthesized from steering force, gain should be NEGATIVE (-0.3 to -0.5)
        if (isColumnForce)
        {
            if (profile.MzFront?.Gain >= 0)
                issues.Add("CRITICAL: Force inversion — MzFrontGain is " +
                    (profile.MzFront?.Gain == 0 ? "ZERO" : "POSITIVE") +
                    ". For column-force games this MUST be NEGATIVE (-0.3 to -0.5). "
                    + "The wheel will fight your steering.");
        }
        else
        {
            if (profile.MzFront?.Gain <= 0)
                issues.Add("CRITICAL: Force inversion — MzFrontGain is " +
                    (profile.MzFront?.Gain == 0 ? "ZERO" : "NEGATIVE") +
                    ". For physics-based games this MUST be POSITIVE (0.2 to 0.6). "
                    + "The wheel will feel backwards.");
        }

        // UNIVERSAL: Dead channel mix
        bool allChannelsZero = (profile.MzFront?.Gain ?? 0) == 0
                            && (profile.FxFront?.Gain ?? 0) == 0
                            && (profile.FyFront?.Gain ?? 0) == 0;
        if (allChannelsZero)
            issues.Add("CRITICAL: Dead channel mix — All channel gains (MzFront, FxFront, FyFront) are ZERO. "
                + "The core centering force path is completely disabled.");

        // COLUMN-FORCE ONLY: ForceScale out of range (slider 0.1–5, default 1.0; lower = stronger)
        if (isColumnForce && profile.ForceScale < 0.3f)
            issues.Add("ISSUE: ForceScale extremely low — Current: " + profile.ForceScale.ToString("F2") +
                ". Slider range is 0.1–5 (default 1.0); low values amplify the signal and cause massive clipping.");

        // COLUMN-FORCE ONLY: CoreForceMultiplier
        if (isColumnForce && profile.Advanced?.CoreForceMultiplier < 2.0f)
            issues.Add("ISSUE: CoreForceMultiplier too low — Current: " + (profile.Advanced?.CoreForceMultiplier ?? 0).ToString("F1") +
                ". Column-force games typically need ~3.0x. The core centering force feels weak.");

        // UNIVERSAL: CenterSuppression too high
        if (profile.Advanced?.CenterSuppressionDegrees > 5)
            issues.Add("ISSUE: Center suppression too high — " + profile.Advanced.CenterSuppressionDegrees.ToString("F1") +
                "°. Creates a large deadzone.");

        // UNIVERSAL: Static friction masking
        if ((profile.StaticFriction?.Gain ?? 0) > 1.5f && (profile.StaticFriction?.MaxElasticStretch ?? 0) > 0.03f)
            issues.Add("ISSUE: Static friction masking FFB — Gain " + (profile.StaticFriction?.Gain ?? 0).ToString("F2") +
                " + Stretch " + (profile.StaticFriction?.MaxElasticStretch ?? 0).ToString("F3") +
                " creates rubber-band feel that overpowers FFB.");

        // UNIVERSAL: All vibrations zero
        if ((profile.Vibrations?.MasterGain ?? 0) == 0)
            issues.Add("ISSUE: All vibrations disabled — MasterGain is 0. No road texture, kerbs, or slip feel.");

        return issues;
    }

    private async Task<CoachResult> AnalyzeDataAsync(SnapshotCsvData csvData, FfbProfile? profile)
    {
        if (_aiAnalyzer != null && profile != null)
        {
            try
            {
                var context = BuildSystemContext(csvData, profile, isAnalysis: true);
                var diagnostics = RunDiagnostics(profile, IsColumnForceGame);
                var userMsg = diagnostics.Count > 0
                    ? "## CRITICAL ISSUES DETECTED — You MUST address these first before any tuning:\n" +
                      string.Join("\n", diagnostics) +
                      "\n\nAfter addressing these, also analyze the telemetry for additional tuning recommendations."
                    : "Analyze this telemetry data and provide specific, actionable recommendations to improve FFB feel and performance.";

                var aiResult = await _aiAnalyzer.AnalyzeAsync(csvData, profile, GameContextString(DataSourceLabel), diagnostics);
                _aiConversation = new List<ChatMessage>
                {
                    new() { Role = "system", Content = context },
                    new() { Role = "user", Content = userMsg },
                    new() { Role = "assistant", Content = JsonSerializer.Serialize(aiResult, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }) }
                };
                return ConvertAiResult(aiResult, csvData, profile);
            }
            catch (Exception ex)
            {
                _aiConversation = null;
                return new CoachResult
                {
                    State = CoachSessionState.SelectingSource,
                    Messages =
                    [
                        new CoachMessage { Text = $"⚠️ AI Coach failed: {ex.Message}", Icon = "⚠️" },
                        new CoachMessage { Text = "Check that your OpenCode Go API key is correct in Settings and the endpoint is reachable.", Icon = "ℹ️" },
                        .. GenerateSourceSelectionMessages()
                    ]
                };
            }
        }

        _aiConversation = null;
        if (_aiAnalyzer != null)
        {
            return new CoachResult
            {
                State = CoachSessionState.SelectingSource,
                Messages =
                [
                    new CoachMessage { Text = "⚠️ No active profile found. Select or create a profile first.", Icon = "⚠️" },
                    .. GenerateSourceSelectionMessages()
                ]
            };
        }
        return new CoachResult
        {
            State = CoachSessionState.SelectingSource,
            Messages =
            [
                new CoachMessage { Text = "🤖 AI Coach is not configured. Go to Settings → FFB Coach to enter your OpenCode Go API key.", Icon = "ℹ️" },
                .. GenerateSourceSelectionMessages()
            ]
        };
    }

    private CoachResult AnalyzeDataRuleBased(SnapshotCsvData csvData, FfbProfile? profile)
    {
        var recs = new List<FfbRecommendation>();
        var issues = new List<string>();

        float? clippingPct = csvData.ExtractStatValue("ClippingPct")
                            ?? csvData.ExtractStatValue("Clipping");
        float? outputMax = csvData.ExtractStatValue("OutputMax");
        float? outputAvg = csvData.ExtractStatValue("OutputAvg");

        if (clippingPct > 5f)
        {
            recs.Add(new FfbRecommendation
            {
                Type = RecommendationType.ProfileChange,
                Parameter = "OutputGain",
                Description = $"Data shows {clippingPct:F0}% clipping",
                Reason = "Reduce output gain or increase soft clip threshold",
                CurrentValue = profile?.OutputGain ?? 0.62f,
                SuggestedValue = MathF.Max(0.3f, (profile?.OutputGain ?? 0.62f) * 0.8f),
                Impact = "Lower peak forces, less clipping"
            });
            issues.Add("clipping");
        }

        if (outputMax > 0.95f)
        {
            if (!issues.Contains("clipping"))
            {
                recs.Add(new FfbRecommendation
                {
                    Type = RecommendationType.ProfileChange,
                    Parameter = "OutputGain",
                    Description = $"Peak output is {outputMax:F1} — very close to limit",
                    Reason = "Reduce output gain to add headroom",
                    CurrentValue = profile?.OutputGain ?? 0.62f,
                    SuggestedValue = MathF.Max(0.3f, (profile?.OutputGain ?? 0.62f) * 0.85f),
                    Impact = "More headroom, safer for wheel"
                });
                issues.Add("clipping");
            }
        }

        if (outputAvg < 0.08f && outputMax < 0.3f)
        {
            recs.Add(new FfbRecommendation
            {
                Type = RecommendationType.ProfileChange,
                Parameter = "OutputGain",
                Description = $"Output signal seems weak (avg: {outputAvg:F3}, max: {outputMax:F3})",
                Reason = "Increase output gain for more force feedback strength",
                CurrentValue = profile?.OutputGain ?? 0.62f,
                SuggestedValue = MathF.Min(1.2f, (profile?.OutputGain ?? 0.62f) * 1.3f),
                Impact = "Stronger overall FFB feel"
            });
            issues.Add("weak");
        }

        float? mzPeak = csvData.ExtractStatValue("PeakMz");
        float? fxPeak = csvData.ExtractStatValue("PeakFx");
        float? fyPeak = csvData.ExtractStatValue("PeakFy");

        if (!issues.Contains("clipping") && !issues.Contains("weak") && profile != null)
        {
            if (mzPeak > 0.4f || fxPeak > 0.4f || fyPeak > 0.4f)
            {
                recs.Add(new FfbRecommendation
                {
                    Type = RecommendationType.ProfileChange,
                    Parameter = "CompressionPower",
                    Description = $"Channels have moderate peaks (Mz:{mzPeak:F2} Fx:{fxPeak:F2} Fy:{fyPeak:F2})",
                    Reason = "Increase compression to round off peak forces naturally",
                    CurrentValue = profile.CompressionPower,
                    SuggestedValue = MathF.Min(3f, profile.CompressionPower + 0.3f),
                    Impact = "Smoother transitions, more natural feel"
                });
                issues.Add("peaks");
            }
        }

        if (recs.Count == 0)
        {
            var healthyResult = BuildHealthyMessages(profile);
            return new CoachResult
            {
                State = healthyResult.State,
                Messages = healthyResult.Messages,
                Recommendations = recs
            };
        }

        State = CoachSessionState.Questioning;
        return BuildFirstIssueMessages(issues, recs);
    }

    private async Task<CoachResult> AnalyzeLiveStatsAsync(LiveProfilerStats stats, FfbProfile? profile)
    {
        var csvData = new SnapshotCsvData
        {
            ProfileName = profile?.Name ?? "Live",
            TorqueNm = 5.5f,
            StatsText =
                $"OutputMin:          {stats.OutputMin:F6}\n" +
                $"OutputMax:          {stats.OutputMax:F6}\n" +
                $"OutputAvg:          {stats.OutputAvg:F6}\n" +
                $"ClippingPct:        {stats.ClippingPercent:F1}%\n" +
                $"PeakMz:             {stats.PeakMz:F4}\n" +
                $"PeakFx:             {stats.PeakFx:F4}\n" +
                $"PeakFy:             {stats.PeakFy:F4}"
        };

        if (_aiAnalyzer != null && profile != null)
        {
            try
            {
                var context = BuildSystemContext(csvData, profile, isAnalysis: true);
                var diagnostics = RunDiagnostics(profile, IsColumnForceGame);
                var aiResult = await _aiAnalyzer.AnalyzeAsync(csvData, profile, GameContextString($"Live {stats.FrameCount}f"), diagnostics);
                _aiConversation = new List<ChatMessage>
                {
                    new() { Role = "system", Content = context },
                    new() { Role = "user", Content = diagnostics.Count > 0
                        ? "Analyze this live data. CRITICAL issues found: " + string.Join("; ", diagnostics)
                        : "Analyze this live telemetry data and provide specific, actionable recommendations." },
                    new() { Role = "assistant", Content = JsonSerializer.Serialize(aiResult, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }) }
                };
                return ConvertAiResult(aiResult, csvData, profile);
            }
            catch (Exception ex)
            {
                _aiConversation = null;
                return new CoachResult
                {
                    State = CoachSessionState.SelectingSource,
                    Messages =
                    [
                        new CoachMessage { Text = $"⚠️ AI Coach failed: {ex.Message}", Icon = "⚠️" },
                        new CoachMessage { Text = "Check your OpenCode Go API key in Settings and try again.", Icon = "ℹ️" },
                        .. GenerateSourceSelectionMessages()
                    ]
                };
            }
        }

        _aiConversation = null;
        return new CoachResult
        {
            State = CoachSessionState.SelectingSource,
            Messages =
            [
                new CoachMessage { Text = "🤖 AI Coach is not configured. Go to Settings → FFB Coach to enter your OpenCode Go API key.", Icon = "ℹ️" },
                .. GenerateSourceSelectionMessages()
            ]
        };
    }

    /// <summary>
    /// Validates one AI-proposed recommendation against the authoritative catalog:
    /// known parameter, applicable to the current game, inside the real slider range,
    /// and a meaningful step. Returns null when the suggestion must be dropped.
    /// </summary>
    private FfbRecommendation? ValidateRecommendation(
        string parameter, string issueDescription, string reason,
        float aiCurrent, float aiSuggested, string impact,
        FfbProfile profile, List<string> droppedNotes)
    {
        var canonical = NormalizeParam(parameter);
        if (!CoachParameterCatalog.TryGet(canonical, out var info) || info == null)
        {
            droppedNotes.Add($"Dropped '{parameter}': unknown parameter (no matching profile setting).");
            return null;
        }

        var game = ActiveCoachGame;
        if (!CoachParameterCatalog.IsApplicable(info, game))
        {
            droppedNotes.Add($"Dropped '{info.Name}': not available for " +
                $"{CoachParameterCatalog.GameShortName(game)} " +
                $"(only: {string.Join("/", info.ApplicableGames.Select(CoachParameterCatalog.GameShortName))}).");
            return null;
        }

        float actualCurrent = ReadProfileValue(profile, canonical);
        float suggested = aiSuggested;
        bool clamped = false;
        if (info.SliderMin.HasValue || info.SliderMax.HasValue)
        {
            float c = CoachParameterCatalog.ClampToSlider(info, suggested);
            clamped = Math.Abs(c - suggested) > 1e-6f;
            suggested = c;
        }

        bool meaningful;
        if (info.SliderMin.HasValue && info.SliderMax.HasValue)
        {
            float span = info.SliderMax.Value - info.SliderMin.Value;
            meaningful = Math.Abs(suggested - actualCurrent) >= Math.Max(span * 0.02f, 1e-6f);
        }
        else
        {
            meaningful = Math.Abs(suggested - actualCurrent) > 1e-6f;
        }
        if (!meaningful)
        {
            droppedNotes.Add($"Dropped '{info.Name}': suggested {suggested:F3} is not a meaningful " +
                $"step from current {actualCurrent:F3} (min step is 2% of slider span).");
            return null;
        }

        if (string.IsNullOrWhiteSpace(reason))
            reason = $"Addresses {issueDescription} (fixes: {info.Fixes}).";
        if (string.IsNullOrWhiteSpace(impact))
            impact = info.Effect;
        if (info.SliderMin == null)
            reason += " NOTE: no UI slider exists for this — it is profile-only.";
        if (clamped)
            reason += $" (AI asked {aiSuggested:F3}, clamped to slider limit {suggested:F3}).";

        return new FfbRecommendation
        {
            Type = RecommendationType.ProfileChange,
            Parameter = info.Name,
            Description = issueDescription,
            Reason = reason,
            CurrentValue = actualCurrent,
            SuggestedValue = suggested,
            Impact = impact
        };
    }

    private CoachResult ConvertAiResult(AIAnalysisResult aiResult, SnapshotCsvData csvData, FfbProfile profile)
    {
        var recs = new List<FfbRecommendation>();
        var issues = new List<string>();
        var messages = new List<CoachMessage>();
        var droppedNotes = new List<string>();

        messages.Add(new CoachMessage
        {
            Text = aiResult.Summary,
            Icon = "🤖"
        });

        foreach (var issue in aiResult.Issues)
        {
            if (issue.Type == "fallback" || issue.Type == "balanced") continue;

            issues.Add(issue.Type);
            foreach (var rec in issue.Recommendations)
            {
                var validated = ValidateRecommendation(
                    rec.Parameter, issue.Description, rec.Reason,
                    rec.CurrentValue, rec.SuggestedValue, rec.Impact,
                    profile, droppedNotes);
                if (validated != null)
                    recs.Add(validated);
            }

            messages.Add(new CoachMessage
            {
                Text = $"**{issue.Type.ToUpper()}** ({issue.Severity}): {issue.Description}",
                Icon = issue.Severity == "high" ? "🔴" : issue.Severity == "medium" ? "🟡" : "🟢"
            });
        }

        if (droppedNotes.Count > 0)
        {
            messages.Add(new CoachMessage
            {
                Text = "Skipped suggestions that would do nothing here:\n- " + string.Join("\n- ", droppedNotes),
                Icon = "🚫"
            });
        }

        if (recs.Count == 0 && _aiConversation == null)
        {
            var healthy = BuildHealthyMessages(profile);
            return new CoachResult
            {
                State = CoachSessionState.Questioning,
                Messages = [.. messages, .. healthy.Messages],
                Recommendations = recs,
                CsvData = csvData
            };
        }

        if (recs.Count == 0)
        {
            messages.Add(new CoachMessage
            {
                Text = "Your profile looks well-balanced. What aspect of the feel would you like to talk about? I can help with overall strength, damping, vibrations, or anything else.",
                Answers =
                [
                    new CoachAnswer { Id = "talk", Label = "Let's talk feel", Description = "Start AI conversation" },
                    new CoachAnswer { Id = "finish", Label = "I'm done", Description = "See summary" }
                ]
            });
        }
        else
        {
            messages.Add(new CoachMessage
            {
                Text = "I found some things worth adjusting. Tell me what you're feeling on track — oversteer, understeer, wheel too heavy or too light, vibrations? Or I can just walk through my recommendations one by one.",
                Answers =
                [
                    new CoachAnswer { Id = "talk", Label = "Let's tune it", Description = "AI coaching conversation" },
                    new CoachAnswer { Id = "finish", Label = "Not now", Description = "See summary" }
                ]
            });
        }

        State = CoachSessionState.Questioning;
        return new CoachResult
        {
            State = CoachSessionState.Questioning,
            Messages = messages,
            Recommendations = recs,
            ActiveIssue = issues.Count > 0 ? issues[0] : "",
            CsvData = csvData
        };
    }

    private CoachResult BuildFirstIssueMessages(List<string> issues,
        List<FfbRecommendation> recs)
    {
        var messages = new List<CoachMessage>();
        var firstIssue = issues[0];
        var rec = recs[0];

        switch (firstIssue)
        {
            case "clipping":
                messages.Add(new CoachMessage
                {
                    Text = $"I noticed your FFB signal is clipping {rec.Description?.ToLower() ?? ""}. That means the force is hitting the maximum limit and getting chopped off. Does the wheel feel too strong or numb at the limit?",
                    Icon = "📊",
                    Recommendation = rec,
                    Answers =
                    [
                        new CoachAnswer { Id = "clipping_yes", Label = "Yes, too strong", Description = $"Suggested: OutputGain {rec.CurrentValue:F2} → {rec.SuggestedValue:F2}" },
                        new CoachAnswer { Id = "clipping_no", Label = "Feels fine", Description = "Skip this adjustment" }
                    ]
                });
                break;

            case "weak":
                messages.Add(new CoachMessage
                {
                    Text = $"The FFB signal looks weak ({rec.Description?.ToLower() ?? ""}). Do you feel like the wheel lacks strength?",
                    Icon = "💪",
                    Recommendation = rec,
                    Answers =
                    [
                        new CoachAnswer { Id = "weak_yes", Label = "Yes, too weak", Description = $"Suggested: OutputGain {rec.CurrentValue:F2} → {rec.SuggestedValue:F2}" },
                        new CoachAnswer { Id = "weak_no", Label = "Feels about right", Description = "Skip this adjustment" }
                    ]
                });
                break;

            case "peaks":
                messages.Add(new CoachMessage
                {
                    Text = $"Your force channels have moderate peak values. {rec.Description}. Would you like to smooth out these peak forces?",
                    Icon = "📈",
                    Recommendation = rec,
                    Answers =
                    [
                        new CoachAnswer { Id = "peaks_yes", Label = "Yes, smooth it out", Description = $"Suggested: CompressionPower {rec.CurrentValue:F1} → {rec.SuggestedValue:F1}" },
                        new CoachAnswer { Id = "peaks_no", Label = "Feels good as is", Description = "Skip" }
                    ]
                });
                break;

            default:
                return BuildHealthyMessages(profile: null);
        }

        return new CoachResult
        {
            State = CoachSessionState.Questioning,
            Messages = messages,
            Recommendations = recs,
            ActiveIssue = firstIssue,
            CsvData = _lastCsvData,
            LiveStats = _lastLiveStats
        };
    }

    private CoachResult BuildAdjustmentQuestion(FfbRecommendation? rec)
    {
        if (rec == null)
            return BuildSummaryMessages();

        return new CoachResult
        {
            State = CoachSessionState.Questioning,
            Messages =
            [
                new CoachMessage
                {
                    Text = $"I'd recommend reducing OutputGain from {rec.CurrentValue:F2} to {rec.SuggestedValue:F2}. This will lower the peak force and reduce clipping while keeping the overall feel.",
                    Recommendation = rec,
                    Answers =
                    [
                        new CoachAnswer { Id = "apply", Label = "Apply This Change" },
                        new CoachAnswer { Id = "skip", Label = "Skip, I'll adjust manually" }
                    ]
                }
            ],
            PendingRecommendation = rec
        };
    }

    private CoachResult BuildNextIssueQuestion(string skippedIssue)
    {
        var questions = new List<CoachAnswer>
        {
            new() { Id = "fine_tune_output_gain", Label = "Adjust Overall Strength", Description = "Output gain fine-tuning" },
            new() { Id = "fine_tune_damping", Label = "Adjust Damping Feel", Description = "Viscous/speed damping" },
            new() { Id = "fine_tune_vibration", Label = "Adjust Vibrations", Description = "Kerb/road/grip vibrations" },
            new() { Id = "fine_tune_none", Label = "I'm Done — Show Summary", Description = "View all changes" }
        };

        return new CoachResult
        {
            State = CoachSessionState.Questioning,
            Messages =
            [
                new CoachMessage
                {
                    Text = "Alright! Let's fine-tune other aspects. What would you like to adjust?",
                    Answers = questions
                }
            ]
        };
    }

    private CoachResult BuildFineTuneOutputGainQuestion()
    {
        var profile = _profileManager.ActiveProfile;
        float current = profile?.OutputGain ?? 0.62f;

        return new CoachResult
        {
            State = CoachSessionState.Questioning,
            Messages =
            [
                new CoachMessage
                {
                    Text = $"Current Output Gain is {current:F2}. Would you like more or less overall strength?",
                    Answers =
                    [
                        new CoachAnswer { Id = "apply_output_high", Label = "More Strength", Description = $"Increase to {MathF.Min(1.2f, current * 1.2f):F2}" },
                        new CoachAnswer { Id = "apply_output_low", Label = "Less Strength", Description = $"Decrease to {MathF.Max(0.2f, current * 0.8f):F2}" },
                        new CoachAnswer { Id = "skip", Label = "Keep Current", Description = "No change" }
                    ]
                }
            ]
        };
    }

    private void RefreshConversationProfile()
    {
        if (_aiConversation == null || _aiConversation.Count == 0) return;
        var profile = _profileManager.ActiveProfile;
        if (profile == null) return;
        var fresh = BuildProfileFullJson(profile);
        _aiConversation.Insert(0, new ChatMessage
        {
            Role = "system",
            Content = $"## Updated Profile Values (after changes)\n```json\n{fresh}\n```\nThe profile has been updated."
        });
    }

    private CoachResult BuildFineTuneDampingQuestion()
    {
        return new CoachResult
        {
            State = CoachSessionState.Questioning,
            Messages =
            [
                new CoachMessage
                {
                    Text = "Let's tune the damping. What would you like to adjust?",
                    Answers =
                    [
                        new CoachAnswer { Id = "apply_damping_viscous", Label = "Viscous Damping", Description = "General smoothness" },
                        new CoachAnswer { Id = "apply_damping_speed", Label = "Speed Damping", Description = "High-speed stability" },
                        new CoachAnswer { Id = "apply_damping_friction", Label = "Friction", Description = "Static friction feel" },
                        new CoachAnswer { Id = "skip", Label = "Keep Current", Description = "No change" }
                    ]
                }
            ]
        };
    }

    private CoachResult BuildFineTuneVibrationQuestion()
    {
        return new CoachResult
        {
            State = CoachSessionState.Questioning,
            Messages =
            [
                new CoachMessage
                {
                    Text = "Let's adjust vibration levels. Which aspect would you like to change?",
                    Answers =
                    [
                        new CoachAnswer { Id = "apply_vib_kerb", Label = "Kerb Vibration", Description = "Curb/rumble strip feel" },
                        new CoachAnswer { Id = "apply_vib_road", Label = "Road Vibration", Description = "Road surface feel" },
                        new CoachAnswer { Id = "apply_vib_slip", Label = "Slip Vibration", Description = "Tire slip feedback" },
                        new CoachAnswer { Id = "skip", Label = "Keep Current", Description = "No change" }
                    ]
                }
            ]
        };
    }

    private CoachResult BuildHealthyMessages(FfbProfile? profile)
    {
        var messages = new List<CoachMessage>
        {
            new()
            {
                Text = "Your FFB profile looks healthy! The signal levels are in a good range with no major issues detected.",
                Icon = "✅"
            },
            new()
            {
                Text = "Want to fine-tune anything for personal preference?",
                Answers =
                [
                    new CoachAnswer { Id = "fine_tune_output_gain", Label = "Adjust Overall Strength", Description = "Output gain fine-tuning" },
                    new CoachAnswer { Id = "fine_tune_damping", Label = "Adjust Damping Feel", Description = "Viscous/speed/friction damping" },
                    new CoachAnswer { Id = "fine_tune_vibration", Label = "Adjust Vibrations", Description = "Kerb/road/slip vibration gains" },
                    new CoachAnswer { Id = "fine_tune_none", Label = "I'm Good, Thanks!", Description = "Finish session" }
                ]
            }
        };

        // If profile has recent tuning activity, mention it
        if (profile != null && HasRecentActivity(profile))
        {
            messages.Insert(1, new CoachMessage
            {
                Text = $"I noticed you've been tweaking the {GetRecentChanges(profile)} settings. Let's make sure those adjustments are working well for you.",
                Icon = "🔧"
            });
        }

        return new CoachResult
        {
            State = CoachSessionState.Questioning,
            Messages = messages
        };
    }

    private CoachResult BuildSummaryMessages()
    {
        State = CoachSessionState.Summary;
        var count = _history.Count;

        var historyText = "";
        if (count > 0)
        {
            var lines = _history.Select((h, i) => $"{i + 1}. {h.Parameter}: {h.OldValue:F3} → {h.NewValue:F3} ({h.AppliedAt:HH:mm:ss})");
            historyText = "\n\n**Applied history:**\n" + string.Join("\n", lines) +
                "\n\nUse Undo (last change) or Revert (any entry) in the history panel to roll back.";
        }

        return new CoachResult
        {
            State = CoachSessionState.Summary,
            Messages =
            [
                new CoachMessage
                {
                    Text = "Here's a summary of our tuning session:",
                    Icon = "📋"
                },
                new CoachMessage
                {
                    Text = count > 0
                        ? $"✅ {count} change{(count > 1 ? "s" : "")} applied to your profile.{historyText}\n\nTip: Take a new telemetry snapshot to verify the changes feel right on track."
                        : "No changes were applied this session. Feel free to come back anytime for tuning advice!",
                    Answers =
                    [
                        new CoachAnswer { Id = "restart", Label = "Start Over", Description = "New coaching session" },
                        new CoachAnswer { Id = "done", Label = "Done", Description = "Return to tuning" }
                    ]
                }
            ]
        };
    }

    private FfbProfile? FindMatchingProfile(string profileName)
    {
        if (_profileManager.ActiveProfile?.Name == profileName)
            return _profileManager.ActiveProfile;

        return _profileManager.Profiles.FirstOrDefault(p => p.Name == profileName)
               ?? _profileManager.ActiveProfile;
    }

    private static readonly Dictionary<string, string> ParamNormalize = new()
    {
        { "outputgain", "OutputGain" },
        { "compressionpower", "CompressionPower" },
        { "forcescale", "ForceScale" },
        { "softclipthreshold", "SoftClipThreshold" },
        { "signcorrectionenabled", "SignCorrectionEnabled" },
        { "coreforcemultiplier", "CoreForceMultiplier" },
        { "maxslewrate", "MaxSlewRate" },
        { "hysteresisthreshold", "HysteresisThreshold" },
        { "centersuppressiondegrees", "CenterSuppressionDegrees" },
        { "centersharpnessdegrees", "CenterSharpnessDegrees" },
        { "centerkneepower", "CenterKneePower" },
        { "centerblenddegrees", "CenterBlendDegrees" },
        { "noisefloor", "NoiseFloor" },
        { "hysteresiswatchdogframes", "HysteresisWatchdogFrames" },
        { "lowspeedsmoothkmh", "LowSpeedSmoothKmh" },
        { "mzfrontgain", "MzFrontGain" },
        { "fxfrontgain", "FxFrontGain" },
        { "fyfrontgain", "FyFrontGain" },
        { "mzreargain", "MzRearGain" },
        { "wheelloadweighting", "WheelLoadWeighting" },
        { "mzscale", "MzScale" },
        { "fxscale", "FxScale" },
        { "fyscale", "FyScale" },
        { "viscousdamping", "ViscousDamping" },
        { "speeddamping", "SpeedDamping" },
        { "friction", "Friction" },
        { "inertia", "Inertia" },
        { "maxspeedreference", "MaxSpeedReference" },
        { "centerdampfloor", "CenterDampFloor" },
        { "centerauthorityfloor", "CenterDampFloor" },
        { "straightgain", "StraightGain" },
        { "straightstability", "StraightGain" },
        { "slipratiogain", "SlipRatioGain" },
        { "slipanglegain", "SlipAngleGain" },
        { "slipangleshapegain", "SlipAngleShapeGain" },
        { "slipthreshold", "SlipThreshold" },
        { "usefrontonly", "UseFrontOnly" },
        { "brakeboostgain", "BrakeBoostGain" },
        { "brakeboostthreshold", "BrakeBoostThreshold" },
        { "gearquickspike", "GearSpikeThreshold" },
        { "gearquickspikethreshold", "GearSpikeThreshold" },
        { "gearspikethreshold", "GearSpikeThreshold" },
        { "rearfyscale", "RearFyScale" },
        { "rearslipwarning", "RearFyScale" },
        { "reargripdetailgain", "RearGripDetailGain" },
        { "reargripdrop", "RearGripDetailGain" },
        { "yawonsetgain", "YawOnsetGain" },
        { "onsetgain", "YawOnsetGain" },
        { "yawonsetthreshold", "YawOnsetThreshold" },
        { "onsetthreshold", "YawOnsetThreshold" },
        { "yawonsetdecay", "YawOnsetDecay" },
        { "yawonsetmax", "YawOnsetMax" },
        { "susproadscale", "SuspRoadScale" },
        { "usemixersusproad", "UseMixerSuspRoad" },
        { "mixerroadlayer", "UseMixerSuspRoad" },
        { "brakeloadbias", "BrakeLoadBias" },
        { "brakeloadtransfer", "BrakeLoadBias" },
        { "synthyawenabled", "SynthYawEnabled" },
        { "synthabsenabled", "SynthAbsEnabled" },
        { "synthtcenabled", "SynthTcEnabled" },
        { "lateralggain", "LateralGGain" },
        { "longitudinalggain", "LongitudinalGGain" },
        { "suspensiongain", "SuspensionGain" },
        { "yawrategain", "YawRateGain" },
        { "mastergain", "MasterGain" },
        { "kerbgain", "KerbGain" },
        { "roadgain", "RoadGain" },
        { "slipgain", "SlipGain" },
        { "absgain", "AbsGain" },
        { "abspulseamplitude", "AbsPulseAmplitude" },
        { "suspensionroadgain", "SuspensionRoadGain" },
        { "scrubgain", "ScrubGain" },
        { "rearslipgain", "RearSlipGain" },
        { "offtrackgain", "OfftrackGain" },
        { "offtrackseverityscale", "OfftrackSeverityScale" },
        { "curbseverityscale", "CurbSeverityScale" },
        { "scrubforcescale", "ScrubForceScale" },
        { "rearslipforcescale", "RearSlipForceScale" },
        { "flexgain", "FlexGain" },
        { "carcassstiffness", "CarcassStiffness" },
        { "flexsmoothing", "FlexSmoothing" },
        { "contactpatchweight", "ContactPatchWeight" },
        { "loadflexgain", "LoadFlexGain" },
        { "gripguardenabled", "GripGuardEnabled" },
        { "gripguardpeakslipangle", "GripGuardPeakSlipAngle" },
        { "gripguardattenuationstrength", "GripGuardAttenuationStrength" },
        { "gripguardmechanicaltrailgain", "GripGuardMechanicalTrailGain" },
        { "crashenabled", "CrashEnabled" },
        { "crashimpactgain", "CrashImpactGain" },
        { "crashsafetyclamp", "CrashSafetyClamp" },
        { "crashtriggerthresholdg", "CrashTriggerThresholdG" },
        { "tyreconditionenabled", "TyreConditionEnabled" },
        { "blowoutvibrationgain", "BlowoutVibrationGain" },
        { "pressurelossgain", "PressureLossGain" },
        { "damageasymmetrygain", "DamageAsymmetryGain" },
        { "wetweather_enabled", "WetWeatherEnabled" },
        { "wetroadvibsuppression", "WetRoadVibSuppression" },
        { "wetpeakslipanglemultiplier", "WetPeakSlipAngleMultiplier" },
        { "wetdampingreduction", "WetDampingReduction" },
        { "staticfrictiongain", "StaticFrictionGain" },
        { "staticfrictionmaxelasticstretch", "StaticFrictionMaxElasticStretch" },
        { "staticfrictionspringstiffness", "StaticFrictionSpringStiffness" },
        { "staticfrictionkineticfrictionbase", "StaticFrictionKineticFrictionBase" },
        { "staticfrictionengineoffdamping", "StaticFrictionEngineOffDamping" },
        { "staticfrictionengineondamping", "StaticFrictionEngineOnDamping" },
        { "staticfrictionengineoffscale", "StaticFrictionEngineOffScale" },
        { "staticfrictionengineonscale", "StaticFrictionEngineOnScale" },
        { "staticfrictionactivedecay", "StaticFrictionActiveDecay" },
        { "staticfrictionreturndecay", "StaticFrictionReturnDecay" },
        { "staticfrictionoutputsmoothalpha", "StaticFrictionOutputSmoothAlpha" },
    };

    private static string NormalizeParam(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return name ?? "";
        var key = new string(name.Where(c => !char.IsWhiteSpace(c)).ToArray()).ToLowerInvariant();
        return ParamNormalize.TryGetValue(key, out var canonical) ? canonical : name;
    }

    private static float ReadProfileValue(FfbProfile profile, string parameter)
    {
        parameter = NormalizeParam(parameter);
        return parameter switch
        {
            "OutputGain" => profile.OutputGain,
            "CompressionPower" => profile.CompressionPower,
            "ForceScale" => profile.ForceScale,
            "SoftClipThreshold" => profile.SoftClipThreshold,
            "SignCorrectionEnabled" => profile.SignCorrectionEnabled ? 1 : 0,
            "CoreForceMultiplier" => profile.Advanced.CoreForceMultiplier,
            "MaxSlewRate" => profile.Advanced.MaxSlewRate,
            "HysteresisThreshold" => profile.Advanced.HysteresisThreshold,
            "CenterSuppressionDegrees" => profile.Advanced.CenterSuppressionDegrees,
            "CenterSharpnessDegrees" => profile.Advanced.CenterSharpnessDegrees,
            "CenterKneePower" => profile.Advanced.CenterKneePower,
            "CenterBlendDegrees" => profile.Advanced.CenterBlendDegrees,
            "NoiseFloor" => profile.Advanced.NoiseFloor,
            "HysteresisWatchdogFrames" => profile.Advanced.HysteresisWatchdogFrames,
            "LowSpeedSmoothKmh" => profile.Advanced.LowSpeedSmoothKmh,
            "MzFrontGain" => profile.MzFront.Gain,
            "FxFrontGain" => profile.FxFront.Gain,
            "FyFrontGain" => profile.FyFront.Gain,
            "MzRearGain" => profile.MzRear.Gain,
            "WheelLoadWeighting" => profile.WheelLoadWeighting,
            "MzScale" => profile.MzScale,
            "FxScale" => profile.FxScale,
            "FyScale" => profile.FyScale,
            "ViscousDamping" => profile.Damping.ViscousDamping,
            "SpeedDamping" => profile.Damping.SpeedDamping,
            "Friction" => profile.Damping.Friction,
            "Inertia" => profile.Damping.Inertia,
            "MaxSpeedReference" => profile.Damping.MaxSpeedReference,
            "CenterDampFloor" => profile.Damping.CenterDampFloor,
            "StraightGain" => profile.Damping.StraightGain,
            "SlipRatioGain" => profile.Slip.SlipRatioGain,
            "SlipAngleGain" => profile.Slip.SlipAngleGain,
            "SlipAngleShapeGain" => profile.Slip.SlipAngleShapeGain,
            "SlipThreshold" => profile.Slip.SlipThreshold,
            "UseFrontOnly" => profile.Slip.UseFrontOnly ? 1 : 0,
            "BrakeBoostGain" => profile.Slip.BrakeBoostGain,
            "BrakeBoostThreshold" => profile.Slip.BrakeBoostThreshold,
            "GearSpikeThreshold" => profile.Slip.GearSpikeThreshold,
            "RearFyScale" => profile.Slip.RearFyScale,
            "RearGripDetailGain" => profile.Slip.RearGripDetailGain,
            "YawOnsetGain" => profile.Slip.YawOnsetGain,
            "YawOnsetThreshold" => profile.Slip.YawOnsetThreshold,
            "YawOnsetDecay" => profile.Slip.YawOnsetDecay,
            "YawOnsetMax" => profile.Slip.YawOnsetMax,
            "SuspRoadScale" => profile.Slip.SuspRoadScale,
            "UseMixerSuspRoad" => profile.Slip.UseMixerSuspRoad ? 1 : 0,
            "BrakeLoadBias" => profile.Slip.BrakeLoadBias,
            "SynthYawEnabled" => profile.Slip.SynthYawEnabled ? 1 : 0,
            "SynthAbsEnabled" => profile.Slip.SynthAbsEnabled ? 1 : 0,
            "SynthTcEnabled" => profile.Slip.SynthTcEnabled ? 1 : 0,
            "LateralGGain" => profile.Dynamic.LateralGGain,
            "LongitudinalGGain" => profile.Dynamic.LongitudinalGGain,
            "SuspensionGain" => profile.Dynamic.SuspensionGain,
            "YawRateGain" => profile.Dynamic.YawRateGain,
            "MasterGain" => profile.Vibrations.MasterGain,
            "KerbGain" => profile.Vibrations.KerbGain,
            "RoadGain" => profile.Vibrations.RoadGain,
            "SlipGain" => profile.Vibrations.SlipGain,
            "AbsGain" => profile.Vibrations.AbsGain,
            "AbsPulseAmplitude" => profile.Vibrations.AbsPulseAmplitude,
            "SuspensionRoadGain" => profile.Vibrations.SuspensionRoadGain,
            "ScrubGain" => profile.Vibrations.ScrubGain,
            "RearSlipGain" => profile.Vibrations.RearSlipGain,
            "OfftrackGain" => profile.Vibrations.OfftrackGain,
            "OfftrackSeverityScale" => profile.Vibrations.OfftrackSeverityScale,
            "CurbSeverityScale" => profile.Vibrations.CurbSeverityScale,
            "ScrubForceScale" => profile.Vibrations.ScrubForceScale,
            "RearSlipForceScale" => profile.Vibrations.RearSlipForceScale,
            "FlexGain" => profile.TyreFlex.FlexGain,
            "CarcassStiffness" => profile.TyreFlex.CarcassStiffness,
            "FlexSmoothing" => profile.TyreFlex.FlexSmoothing,
            "ContactPatchWeight" => profile.TyreFlex.ContactPatchWeight,
            "LoadFlexGain" => profile.TyreFlex.LoadFlexGain,
            "GripGuardEnabled" => profile.GripGuard.Enabled ? 1 : 0,
            "GripGuardPeakSlipAngle" => profile.GripGuard.PeakSlipAngle,
            "GripGuardAttenuationStrength" => profile.GripGuard.AttenuationStrength,
            "GripGuardMechanicalTrailGain" => profile.GripGuard.MechanicalTrailGain,
            "CrashEnabled" => profile.Crash.Enabled ? 1 : 0,
            "CrashImpactGain" => profile.Crash.ImpactGain,
            "CrashSafetyClamp" => profile.Crash.SafetyClamp,
            "CrashTriggerThresholdG" => profile.Crash.TriggerThresholdG,
            "TyreConditionEnabled" => profile.TyreCondition.Enabled ? 1 : 0,
            "BlowoutVibrationGain" => profile.TyreCondition.BlowoutVibrationGain,
            "PressureLossGain" => profile.TyreCondition.PressureLossGain,
            "DamageAsymmetryGain" => profile.TyreCondition.DamageAsymmetryGain,
            "WetWeatherEnabled" => profile.WetWeather.Enabled ? 1 : 0,
            "WetRoadVibSuppression" => profile.WetWeather.RoadVibSuppression,
            "WetPeakSlipAngleMultiplier" => profile.WetWeather.PeakSlipAngleMultiplier,
            "WetDampingReduction" => profile.WetWeather.DampingReduction,
            "StaticFrictionGain" => profile.StaticFriction.Gain,
            "StaticFrictionMaxElasticStretch" => profile.StaticFriction.MaxElasticStretch,
            "StaticFrictionSpringStiffness" => profile.StaticFriction.SpringStiffness,
            "StaticFrictionKineticFrictionBase" => profile.StaticFriction.KineticFrictionBase,
            "StaticFrictionEngineOffDamping" => profile.StaticFriction.EngineOffDamping,
            "StaticFrictionEngineOnDamping" => profile.StaticFriction.EngineOnDamping,
            "StaticFrictionEngineOffScale" => profile.StaticFriction.EngineOffScale,
            "StaticFrictionEngineOnScale" => profile.StaticFriction.EngineOnScale,
            "StaticFrictionActiveDecay" => profile.StaticFriction.ActiveDecay,
            "StaticFrictionReturnDecay" => profile.StaticFriction.ReturnDecay,
            "StaticFrictionOutputSmoothAlpha" => profile.StaticFriction.OutputSmoothAlpha,
            _ => 0f
        };
    }

    private static void WriteProfileValue(FfbProfile profile, string parameter, float value)
    {
        parameter = NormalizeParam(parameter);
        switch (parameter)
        {
            case "OutputGain": profile.OutputGain = value; break;
            case "CompressionPower": profile.CompressionPower = value; break;
            case "ForceScale": profile.ForceScale = value; break;
            case "SoftClipThreshold": profile.SoftClipThreshold = value; break;
            case "SignCorrectionEnabled": profile.SignCorrectionEnabled = value > 0.5f; break;
            case "CoreForceMultiplier": profile.Advanced.CoreForceMultiplier = value; break;
            case "MaxSlewRate": profile.Advanced.MaxSlewRate = value; break;
            case "HysteresisThreshold": profile.Advanced.HysteresisThreshold = value; break;
            case "CenterSuppressionDegrees": profile.Advanced.CenterSuppressionDegrees = value; break;
            case "CenterSharpnessDegrees": profile.Advanced.CenterSharpnessDegrees = value; break;
            case "CenterKneePower": profile.Advanced.CenterKneePower = value; break;
            case "CenterBlendDegrees": profile.Advanced.CenterBlendDegrees = value; break;
            case "NoiseFloor": profile.Advanced.NoiseFloor = value; break;
            case "HysteresisWatchdogFrames": profile.Advanced.HysteresisWatchdogFrames = (int)value; break;
            case "LowSpeedSmoothKmh": profile.Advanced.LowSpeedSmoothKmh = value; break;
            case "MzFrontGain": profile.MzFront.Gain = value; break;
            case "FxFrontGain": profile.FxFront.Gain = value; break;
            case "FyFrontGain": profile.FyFront.Gain = value; break;
            case "MzRearGain": profile.MzRear.Gain = value; break;
            case "WheelLoadWeighting": profile.WheelLoadWeighting = value; break;
            case "MzScale": profile.MzScale = value; break;
            case "FxScale": profile.FxScale = value; break;
            case "FyScale": profile.FyScale = value; break;
            case "ViscousDamping": profile.Damping.ViscousDamping = value; break;
            case "SpeedDamping": profile.Damping.SpeedDamping = value; break;
            case "Friction": profile.Damping.Friction = value; break;
            case "Inertia": profile.Damping.Inertia = value; break;
            case "MaxSpeedReference": profile.Damping.MaxSpeedReference = value; break;
            case "CenterDampFloor": profile.Damping.CenterDampFloor = value; break;
            case "StraightGain": profile.Damping.StraightGain = value; break;
            case "SlipRatioGain": profile.Slip.SlipRatioGain = value; break;
            case "SlipAngleGain": profile.Slip.SlipAngleGain = value; break;
            case "SlipAngleShapeGain": profile.Slip.SlipAngleShapeGain = value; break;
            case "SlipThreshold": profile.Slip.SlipThreshold = value; break;
            case "UseFrontOnly": profile.Slip.UseFrontOnly = value > 0.5f; break;
            case "BrakeBoostGain": profile.Slip.BrakeBoostGain = value; break;
            case "BrakeBoostThreshold": profile.Slip.BrakeBoostThreshold = value; break;
            case "GearSpikeThreshold": profile.Slip.GearSpikeThreshold = (int)value; break;
            case "RearFyScale": profile.Slip.RearFyScale = value; break;
            case "RearGripDetailGain": profile.Slip.RearGripDetailGain = value; break;
            case "YawOnsetGain": profile.Slip.YawOnsetGain = value; break;
            case "YawOnsetThreshold": profile.Slip.YawOnsetThreshold = value; break;
            case "YawOnsetDecay": profile.Slip.YawOnsetDecay = value; break;
            case "YawOnsetMax": profile.Slip.YawOnsetMax = value; break;
            case "SuspRoadScale": profile.Slip.SuspRoadScale = value; break;
            case "UseMixerSuspRoad": profile.Slip.UseMixerSuspRoad = value > 0.5f; break;
            case "BrakeLoadBias": profile.Slip.BrakeLoadBias = value; break;
            case "SynthYawEnabled": profile.Slip.SynthYawEnabled = value > 0.5f; break;
            case "SynthAbsEnabled": profile.Slip.SynthAbsEnabled = value > 0.5f; break;
            case "SynthTcEnabled": profile.Slip.SynthTcEnabled = value > 0.5f; break;
            case "LateralGGain": profile.Dynamic.LateralGGain = value; break;
            case "LongitudinalGGain": profile.Dynamic.LongitudinalGGain = value; break;
            case "SuspensionGain": profile.Dynamic.SuspensionGain = value; break;
            case "YawRateGain": profile.Dynamic.YawRateGain = value; break;
            case "MasterGain": profile.Vibrations.MasterGain = value; break;
            case "KerbGain": profile.Vibrations.KerbGain = value; break;
            case "RoadGain": profile.Vibrations.RoadGain = value; break;
            case "SlipGain": profile.Vibrations.SlipGain = value; break;
            case "AbsGain": profile.Vibrations.AbsGain = value; break;
            case "AbsPulseAmplitude": profile.Vibrations.AbsPulseAmplitude = value; break;
            case "SuspensionRoadGain": profile.Vibrations.SuspensionRoadGain = value; break;
            case "ScrubGain": profile.Vibrations.ScrubGain = value; break;
            case "RearSlipGain": profile.Vibrations.RearSlipGain = value; break;
            case "OfftrackGain": profile.Vibrations.OfftrackGain = value; break;
            case "OfftrackSeverityScale": profile.Vibrations.OfftrackSeverityScale = value; break;
            case "CurbSeverityScale": profile.Vibrations.CurbSeverityScale = value; break;
            case "ScrubForceScale": profile.Vibrations.ScrubForceScale = value; break;
            case "RearSlipForceScale": profile.Vibrations.RearSlipForceScale = value; break;
            case "FlexGain": profile.TyreFlex.FlexGain = value; break;
            case "CarcassStiffness": profile.TyreFlex.CarcassStiffness = value; break;
            case "FlexSmoothing": profile.TyreFlex.FlexSmoothing = value; break;
            case "ContactPatchWeight": profile.TyreFlex.ContactPatchWeight = value; break;
            case "LoadFlexGain": profile.TyreFlex.LoadFlexGain = value; break;
            case "GripGuardEnabled": profile.GripGuard.Enabled = value > 0.5f; break;
            case "GripGuardPeakSlipAngle": profile.GripGuard.PeakSlipAngle = value; break;
            case "GripGuardAttenuationStrength": profile.GripGuard.AttenuationStrength = value; break;
            case "GripGuardMechanicalTrailGain": profile.GripGuard.MechanicalTrailGain = value; break;
            case "CrashEnabled": profile.Crash.Enabled = value > 0.5f; break;
            case "CrashImpactGain": profile.Crash.ImpactGain = value; break;
            case "CrashSafetyClamp": profile.Crash.SafetyClamp = value; break;
            case "CrashTriggerThresholdG": profile.Crash.TriggerThresholdG = value; break;
            case "TyreConditionEnabled": profile.TyreCondition.Enabled = value > 0.5f; break;
            case "BlowoutVibrationGain": profile.TyreCondition.BlowoutVibrationGain = value; break;
            case "PressureLossGain": profile.TyreCondition.PressureLossGain = value; break;
            case "DamageAsymmetryGain": profile.TyreCondition.DamageAsymmetryGain = value; break;
            case "WetWeatherEnabled": profile.WetWeather.Enabled = value > 0.5f; break;
            case "WetRoadVibSuppression": profile.WetWeather.RoadVibSuppression = value; break;
            case "WetPeakSlipAngleMultiplier": profile.WetWeather.PeakSlipAngleMultiplier = value; break;
            case "WetDampingReduction": profile.WetWeather.DampingReduction = value; break;
            case "StaticFrictionGain": profile.StaticFriction.Gain = value; break;
            case "StaticFrictionMaxElasticStretch": profile.StaticFriction.MaxElasticStretch = value; break;
            case "StaticFrictionSpringStiffness": profile.StaticFriction.SpringStiffness = value; break;
            case "StaticFrictionKineticFrictionBase": profile.StaticFriction.KineticFrictionBase = value; break;
            case "StaticFrictionEngineOffDamping": profile.StaticFriction.EngineOffDamping = value; break;
            case "StaticFrictionEngineOnDamping": profile.StaticFriction.EngineOnDamping = value; break;
            case "StaticFrictionEngineOffScale": profile.StaticFriction.EngineOffScale = value; break;
            case "StaticFrictionEngineOnScale": profile.StaticFriction.EngineOnScale = value; break;
            case "StaticFrictionActiveDecay": profile.StaticFriction.ActiveDecay = value; break;
            case "StaticFrictionReturnDecay": profile.StaticFriction.ReturnDecay = value; break;
            case "StaticFrictionOutputSmoothAlpha": profile.StaticFriction.OutputSmoothAlpha = value; break;
        }
    }

    private static bool HasRecentActivity(FfbProfile profile)
    {
        var baseline = FfbProfile.GetDefaultProfile("Default");
        return Math.Abs(profile.OutputGain - baseline.OutputGain) > 0.05f
            || Math.Abs(profile.CompressionPower - baseline.CompressionPower) > 0.2f
            || Math.Abs(profile.Advanced.MaxSlewRate - baseline.Advanced.MaxSlewRate) > 0.02f;
    }

    private static string GetRecentChanges(FfbProfile profile)
    {
        var changed = new List<string>();
        var baseline = FfbProfile.GetDefaultProfile("Default");
        if (Math.Abs(profile.OutputGain - baseline.OutputGain) > 0.05f)
            changed.Add("OutputGain");
        if (Math.Abs(profile.CompressionPower - baseline.CompressionPower) > 0.2f)
            changed.Add("CompressionPower");
        if (Math.Abs(profile.Advanced.MaxSlewRate - baseline.Advanced.MaxSlewRate) > 0.02f)
            changed.Add("MaxSlewRate");
        if (Math.Abs(profile.Damping.ViscousDamping - baseline.Damping.ViscousDamping) > 0.03f)
            changed.Add("ViscousDamping");
        if (Math.Abs(profile.Vibrations.SuspensionRoadGain - baseline.Vibrations.SuspensionRoadGain) > 0.3f)
            changed.Add("SuspensionRoadGain");

        return changed.Count > 0 ? string.Join(", ", changed) : "FFB";
    }
}

public sealed record CoachResult
{
    public CoachSessionState State { get; init; }
    public List<CoachMessage> Messages { get; init; } = [];
    public List<FfbRecommendation> Recommendations { get; init; } = [];
    public FfbRecommendation? PendingRecommendation { get; init; }
    public string ActiveIssue { get; init; } = "";
    public SnapshotCsvData? CsvData { get; init; }
    public LiveProfilerStats? LiveStats { get; init; }
    /// <summary>
    /// True when the coach wrote the active profile + pipeline (apply/undo).
    /// Callers must refresh VM sliders from the profile, otherwise the next
    /// profile Save pushes stale VM values and silently reverts the change.
    /// </summary>
    public bool ProfileChanged { get; init; }
}
