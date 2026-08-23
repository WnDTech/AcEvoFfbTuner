# Kilo Rules

## FFB Pipeline Development

### Code vs Profile — Always Clarify
When making changes, ALWAYS explicitly state whether you are:
- **Changing app code** (C# source in `src/`) — affects all users, persisted across profiles
- **Changing a profile** (JSON in `AppData/Roaming/AcEvoFfbTuner/Profiles/`) — per-user settings

Our goal is to **fix the code** so we can establish a reliable baseline profile that works for all users of this car (BMW GT3 with Moza R5).

### Git — Do NOT Commit or Push Unless Asked
- **Never commit or push unless the user explicitly asks.** No exceptions.
- Only stage files and run builds during the iterative coding loop.
- Wait for the user to say "commit and push" before touching git for commits/pushes.

### Build & Verify
- Build command: `dotnet build AcEvoFfbTuner.slnx -c Release`
- **Always run a FULL clean build after any edit:** `dotnet clean AcEvoFfbTuner.slnx -c Release -q 2>&1; dotnet build AcEvoFfbTuner.slnx -c Release`
- Incremental builds can cache stale XAML/code-behind artifacts, hiding mismatches until runtime. Always clean first.
- Run lint/typecheck if available

### Release Notes — ALWAYS Update What's New
- **Every release MUST ship full What's New release notes** (features / improvements / fixes). NEVER leave the auto-generated "Full Changelog" link-only body — a link is not release notes.
- Format release notes with `### Features` / `### Improvements` / `### Fixes` headings and `- ` bullets — `ChangeLogService.ParseMarkdownBody` parses GitHub release bodies into the in-app What's New dialog, so keep that format.
- When bumping the version: update (1) the csproj version, (2) the hardcoded entry in `ChangeLogService.cs`, (3) the GitHub release notes via `gh release edit <tag> --notes-file`, and (4) the README feature list.
- Keep release notes and the hardcoded changelog entry in sync — same content, same wording.
- Editing the release body (`gh release edit <tag> --notes-file`) triggers the `discord-release-notify.yml` workflow, which posts the release link + version notes to Discord channel 1440318628661825587 (the same bot token as the feedback relay, stored as the `DISCORD_BOT_TOKEN` repo secret). Editing the body again re-posts; run the workflow manually (`workflow_dispatch`) to re-post the latest release.

### Release Procedures — "push" vs "beta"
When the user says **"push"** they mean the full STABLE release procedure below. When they say **"beta"** they mean a beta prerelease — same core procedure plus the beta-specific rules. Never shortcut either: a release is not done until the GitHub release exists WITH full What's New notes and the Discord announce has fired.

**"push" = stable release:**
1. Bump version in the csproj (`<Version>`), add the hardcoded `ChangeLogService.cs` entry, and update the README feature list (see Release Notes above).
2. Write the What's New notes (`### Features` / `### Improvements` / `### Fixes`) — identical content in the hardcoded changelog entry and the release body.
3. Full clean build + tests: `dotnet clean AcEvoFfbTuner.slnx -c Release -q 2>&1; dotnet build AcEvoFfbTuner.slnx -c Release` then `dotnet test src/AcEvoFfbTuner.Tests/... -c Release --no-build`.
4. Commit + push, then create tag `v<X.Y.Z>` (must match the csproj version exactly) and `git push origin <tag>`.
5. Wait for the Build & Release workflow to finish, then verify the release exists and is NOT a prerelease: `gh release view vX.Y.Z`.
6. Write the full release notes into the body: `gh release edit vX.Y.Z --notes-file <file>`. NEVER leave the auto-generated "Full Changelog" link-only body.
7. The Discord announce fires automatically on publish/edit (re-post with `workflow_dispatch` if needed).

**"beta" = beta prerelease (Test Drive channel):**
1. Version format `X.Y.Z-beta.N` (e.g. `1.29.0-beta.1`), always a HIGHER numeric `X.Y.Z` than the current stable — testers ride the next release number. Never publish a beta after the stable with the same `X.Y.Z` is out (it confuses the beta updater's equal-version upgrade rule).
2. The csproj `<Version>` gets the NUMERIC part only (`1.29.0`) — `System.Version` cannot parse the `-beta.N` suffix and a suffixed `AssemblyVersion` throws at runtime. The suffix lives only in: the git tag (`v1.29.0-beta.1`), the installer display name, the hardcoded changelog entry (full string — the version comparators handle the suffix), and `InformationalVersion` (the workflow stamps the FULL version via `-p:InformationalVersion` so the app can tell `beta.1` from `beta.2` when their numeric versions are equal).
3. The Build & Release workflow auto-marks the release `prerelease: true` when the tag contains `-` — no manual flag needed.
4. Same full What's New notes requirement (`gh release edit vX.Y.Z-beta.N --notes-file <file>`), same build/test step.
5. Verify with `gh release view v1.29.0-beta.1` that it is a prerelease.
6. No extra gating work: stable-channel users never see prereleases in the updater or What's New; only Test Drive testers with the build channel enabled (server-gated by `me.betaChannel` = approved/paused) receive the beta. If it's the first beta after enabling the channel, confirm the server's `beta.php` with `me.betaChannel` is deployed.
7. The Discord announce posts the beta release too — that is intended (tells testers a beta is up).
8. Workflow `workflow_dispatch` accepts a version input for both stable and beta tags.

### Local Backups
- The official local backup directory is: `C:\Users\paul_\OneDrive\Documents\APP\ACEVO - Telemetry FFB\backups\YYYY-MM-DD\src`
- Always create a dated backup before making sweeping or experimental changes: `Copy-Item -Path src -Destination "backups\$(Get-Date -Format 'yyyy-MM-dd')\src" -Recurse`
- To restore a backup: `Copy-Item -Path "backups\YYYY-MM-DD\src\*" -Destination src -Recurse -Force`
- The backup folder is the canonical source of truth for reverting after a screw-up.

### Pipeline Isolation — CRITICAL
- **Each game's FFB pipeline must be completely separate.** No game-specific code may touch another game's pipeline in any way. This avoids breaking another game's FFB.
- **EVO pipeline** lives in `FfbPipeline.cs` (base class). Do NOT add R3E-specific logic here.
- **R3E pipeline** lives in `R3eFfbPipeline.cs` (subclass). All R3E-specific processing must be here.
- Shared base class changes must be zero-impact for EVO: virtual hooks that default to no-op, or existing properties that default to zero/disabled.
- Profile changes for one game must not alter the other game's behavior — check default values before modifying `FfbProfile.cs` or shared configs.
- When editing `FfbProfile.cs`, always confirm and state whether the change is for RaceRoom or EVO. R3E-specific pipeline properties must be guarded behind `if (pipeline is R3eFfbPipeline r3e)` casts.

### Haptics Pipeline Isolation — CRITICAL
- **Each game's haptics (vibration) pipeline must be completely separate.** R3E haptic data comes from `RaceroomSharedMemoryReader` (synthesized from shared memory). EVO haptic data comes from `AssettoCorsaSharedMemoryReader` (real game telemetry). No cross-contamination.
- **R3E haptics** are synthesized in `RaceroomSharedMemoryReader`: `KerbVibration`, `SlipVibrations`, `RoadVibrations`, `AbsVibrations`. These feed the shared `VibrationMixer` with game-specific data.
- **EVO haptics** come from real game physics via `AssettoCorsaSharedMemoryReader`. The shared `VibrationMixer` processes game-specific data — not shared logic.
- **Profile vibration gains** (`vibrations.masterGain`, `curbGain`, etc.) apply to both games. R3E-specific vibration handling must be in `R3eFfbPipeline.cs` via `OnDetailForceProcessed` override or a new virtual hook in the base class.
- **All R3E-specific centering/force shaping** must be in `R3eFfbPipeline.cs` via virtual hooks (`ApplyCenteringOverride`, `OnDetailForceProcessed`). EVO must not be affected.
- **Virtual hooks added to `FfbPipeline`** must default to no-op in the base class. Zero-impact for EVO.

### Key Files
- `src/AcEvoFfbTuner.Core/FfbProcessing/FfbPipeline.cs` — Main FFB pipeline (center suppression, slew, hysteresis)
- `src/AcEvoFfbTuner.Core/FfbProcessing/FfbChannelMixer.cs` — Channel mixing, EMAs, spike clamp
- `src/AcEvoFfbTuner.Core/FfbProcessing/FfbSlipEnhancer.cs` — Slip-based force enhancement
- `src/AcEvoFfbTuner.Core/FfbProcessing/FfbDamping.cs` — Damping forces
- `src/AcEvoFfbTuner/ViewModels/MainViewModel.cs` — Profile save/load, telemetry update loop
- `src/AcEvoFfbTuner/Views/MainWindow.xaml` + `.cs` — Telemetry Profiler UI
- `src/AcEvoFfbTuner/Services/ReplayVisualizerService.cs` — HTML replay visualizer generator
- `src/AcEvoFfbTuner/Services/GameRecordingService.cs` — Screen recording (Windows Graphics Capture)
- `src/AcEvoFfbTuner/Services/DiagnosticPackService.cs` — Diagnostic ZIP pack and email sender

### Snapshot Analysis
- Snapshot dir: `C:\Users\paul_\AppData\Roaming\AcEvoFfbTuner\snapshots`
- CSV columns: `Time,SpeedKmh,SteerAngle,ForceOut,RawFF,Compress,LUT,Slip,Damping,Dynamic,MzFront,FxFront,FyFront,Clipping,Gas,Brake`
- Profile dir: `C:\Users\paul_\AppData\Roaming\AcEvoFfbTuner\Profiles`
- Recording dir: `C:\Users\paul_\AppData\Roaming\AcEvoFfbTuner\recordings`
- Snapshots now generate both `.txt` (analysis) and `.html` (animated replay visualizer) files
- Diagnostic packs include: Profiles, Track Maps, Snapshots (incl. HTML replays), Recording Manifest, and Logs

### Iterative FFB Tuning Process
1. User drives and takes a snapshot (presses wheel button)
2. Analyze snapshot data for snap/oscillation/bounceback issues
3. Identify root cause from code + data
4. Make targeted fix (state clearly: code change or profile change)
5. Build, user tests, repeat

### Investigate Thoroughly — No Guessing
- Never say "likely" or "probably" when diagnosing issues. Investigate the actual code paths, read the files, trace the data flow end-to-end.
- If a snapshot shows unexpected values (zero channels, wrong ranges), trace the pipeline code that produces that field to confirm whether it's by design or a bug.
- When analyzing telemetry data, cross-reference the CSV columns against the pipeline code that writes them. Every field in `FfbProcessedData` has a specific assignment site.
- Profile parameter ranges must be verified against the actual config model defaults and clamping logic — do not assume typical ranges from memory.
- When the root cause is found, explain the exact code path with file:line references, not general impressions.
- "Test and verify" means run a full clean build, launch the app, take a snapshot, and confirm the fix with data.

### The Following Data are current AC EVO Shared memory propeties for refrence 
This is the exhaustive list of all properties, constants, and structures extracted from every page of the documentation you provided. I have grouped them logically so you can use them to build your C# classes or monitor buffers.

1. Enumerations (State Definitions)
Use these to interpret the integer values coming from the telemetry.

ACEVO_STATUS: OFF (0), REPLAY (1), LIVE (2), PAUSE (3).

ACEVO_SESSION_TYPE: UNKNOWN (-1), PRACTICE (0), QUALIFY (1), RACE (2), HOTLAP (3), TIME_ATTACK (4), DRIFT (5), DRAG (6).

ACEVO_FLAG_TYPE: NO_FLAG (0), BLUE (1), YELLOW (2), BLACK (3), WHITE (4), CHECKERED (5), PENALTY (6), GREEN (7), ORANGE (8).

ACEVO_CAR_LOCATION: NONE (0), TRACK (1), PITLANE (2), PITBOX (3).

ACEVO_ENGINE_TYPE: ICE (0), ELECTRIC (1), HYBRID (2).

ACEVO_STARTING_GRIP: GREEN (0), FAST (1), OPTIMAL (2).

2. SPageFilePhysics (The High-Frequency Data)
Core Values: packetId, gas, brake, fuel, gear, rpms, steerAngle, speedKmh, finalFF (Sim's internal torque).

Vector Data (float[3]): velocity, accG.

Wheel Specific (float[4]):

wheelLoad, slipRatio, slipAngle.

mz (Aligning Torque), fx (Longitudinal Force), fy (Lateral Force).

suspensionTravel, wheelAngularSpeed.

tireTemp.

Special: driftingScore.

3. SMEvoTyreState (Nested in Graphics - 256 bytes per corner)
tyrePressure

tyreWear

tyreDirt

coreTemp[3] (Inner, Middle, Outer)

carcassTemp

surfaceTemp

contactPatchLocal[3] (X, Y, Z deformation)

contactPatchVelocity[3]

grainLevel

blisterLevel

4. SMEvoElectronics (128 bytes)
tc (Traction Control level)

abs (ABS level)

engineMap

turboMap

brakeBias

diffPreload

ersDeploymentMode

ersRecoveryLevel

drsAvailable (bool)

drsEnabled (bool)

5. SMEvoInstrumentation & Timing (128 - 256 bytes)
Instrumentation: rpmLights, gear, fuelIndicator, engineWarning, pitLimiterOn, absInAction, tcInAction, displayCurrentPageIndex[16].

Timing: currentTime, lastTime, bestTime, split, delta, lapCount, position, distanceTraveled.

Session: sessionType, sessionStatus, sessionTimeLeft, sessionLapsLeft, totalLaps, airTemp, roadTemp.

6. SMEvoPitInfo & Damage
Pit Info: fuelToAdd, tyreChange (bool), tyreCompound (string), frontWing, rearWing, suspensionRepair, bodyRepair, brakeRepair.

Damage (float 0.0–1.0): body, engine, gearbox, transmission, suspension, brakes, tyres, electronics, aero.

7. SMEvoAssistsState
abs, tc, stabilityControl, idealLine, autoGear, autoClutch, autoBlip.

8. SPageFileStaticEvo (Metadata)
Strings: smVersion, acVersion, track.

Session info: session, sessionName, eventId, sessionId, startingGrip, startingAmbientTemperatureC, startingGroundTemperatureC, isStaticWeather, isTimedRace, isOnline, numberOfSessions.

Location: nation, longitude, latitude.

Track: track, trackConfiguration, trackLengthM.

NOTE: carModel, playerName, playerSurname, playerNick, maxRpm, maxFuel, steerRatio, suspensionMaxTravel are NOT in the static struct. carModel is in SPageFileGraphicEvo (graphics page) as car_model (char[33]).

9. Car Detection
car_model (char[33]) is available in SPageFileGraphicEvo (graphics struct), after driver_surname. Read from graphics data each frame for car detection and per-car profile auto-loading.

## LMU Shared Memory — Player Telemetry Offset

LMU's shared memory (`LMU_Data`) has a telemetry section with multiple vehicle entries.
The telemetry **header** contains the player's slot index — use it directly. Do NOT use the
scoring section player index (`FindPlayerVehIndex`) — scoring and telemetry ordering can differ.

### Header Layout (at fixed offset 128464 from MMF start)

```
byte[0]: active      - number of active telemetry entries
byte[1]: playerIdx   - index of the human player WITHIN the telemetry array
byte[2]: hasVehicle  - sanity flag (should be 1)
```

### Correct Offset Calculation

```csharp
const int kTelemHeaderOff = 128464;               // Telemetry section header
const int kTelemInfoOff  = kTelemHeaderOff + 4;   // First TelemInfoV01 entry
const int stride = 1888;                           // sizeof(TelemInfoV01)

byte playerIdx = buf[kTelemHeaderOff + 1];
int playerTelemetryOffset = kTelemInfoOff + playerIdx * stride;
```

All fields (steeringShaftTorque, wheel data, etc.) are read relative to
`playerTelemetryOffset`, NOT the base array offset.

### List of TI_ Constants (offsets from playerTelemetryOffset)

```
TI_VEHICLE_NAME        = 32
TI_TRACK_NAME          = 96
TI_LOCAL_VEL           = 184
TI_LOCAL_ACCEL         = 208
TI_LOCAL_ROT           = 304
TI_GEAR                = 352
TI_ENGINE_RPM          = 356
TI_UNFILTERED_THROTTLE = 388
TI_UNFILTERED_BRAKE    = 396
TI_UNFILTERED_STEERING = 404
TI_STEERING_SHAFT_TORQUE = 452
TI_LAP_NUMBER          = 20
```

### Wheel Array (mWheel[4])

```
wheelBaseOff = 848    // Pre-wheel fields = 848 (native struct, includes 32 bytes of filtered controls)
wheelStride = 260     // sizeof(LmuTelemWheelV01) with pack=4

for wi in 0..3:
    wOff = playerTelemetryOffset + wheelBaseOff + wi * wheelStride
    suspensionDeflection @ wOff + 0
    rotation (wheel speed)        @ wOff + 40
    lateralForce (Fy)             @ wOff + 88
    longitudinalForce (Fx)        @ wOff + 96
    tireLoad                      @ wOff + 104
    gripFract                     @ wOff + 112
    pressure                      @ wOff + 120
    temperature[3] (inner, mid, outer) @ wOff + 128, 136, 144
    wear                          @ wOff + 152
```

### Common Pitfalls

- **DO NOT** use `FindPlayerVehIndex` (scoring section) to compute telemetry offset.
  The scoring entry order can differ from telemetry entry order.
- **ALWAYS** use `buf[kTelemHeaderOff + 1]` (telemetry header playerIdx) for the telemetry slot.
- The `LmuTelemInfoV01` C# struct is incomplete (missing 4 filtered-control doubles).
  Marshal.SizeOf = 816 but native pre-wheel size = 848. Always use hardcoded raw offsets,
  never Marshal-based reading for this struct.
- The wheel dump diagnostic guard `tirePressures[0] > 50f` will silent-skip if tire data reads
  zero — make the first-frame dump unconditional when investigating.

## R3E Shared Memory — Authoritative Field Semantics (KW Studios r3e-api)

Source of truth: the official `kwstudios-sweden/r3e-api` repo (`sample-c/src/r3e.h`,
`sample-csharp/src/R3E.cs`) + KW Studios forum thread "Shared Memory API".
Community mirror: `Yuvix25/r3e-python-api` (`data.cs`). Cross-checked against
`mrbelowski/R3EMemoryTranslator` and Crew Chief.

**ALWAYS consult this section before using any R3E telemetry field in FFB effects,
haptics (pedals, HF8), or diagnostics. These semantics are verified against the
official SDK and this app's own telemetry logs — do not guess or copy from memory.**

### Shared memory layout facts

- MMF name: `$R3E`, version `R3E_VERSION_MAJOR = 3`, `R3E_VERSION_MINOR = 5`.
- Struct is `#pragma pack(push, 1)` — all fields tightly packed, NO padding.
- `R3E_NUM_DRIVERS_MAX = 128` — `all_drivers_data_1[128]` at end of struct.
- Header fields: `version_major`, `version_minor`, `all_drivers_offset` (offset to
  num_cars), `driver_data_size` (size of the driver data struct).
- High-detail player vehicle data is in the `player` substruct (`r3e_playerdata`),
  which contains the double-precision vectors and steering force.

### Enumerations (State Definitions)

**Game mode** (`game_mode`): -1 unavailable, 0 tracktest, 1 leaderboardchallenge,
2 competition, 3 singlerace, 4 championship, 5 multiplayer, 6 multiplayerranked,
7 trybeforeyoubuy.

**Session type** (`session_type`): -1 unavailable, 0 practice, 1 qualify, 2 race,
3 warmup.

**Session phase** (`session_phase`): -1 unavailable, 1 garage (MP countdown),
2 gridwalk, 3 formation lap, 4 countdown, 5 green (racing), 6 checkered.

**Control type** (`control_type`): -1 unavailable, 0 player, 1 AI, 2 remote
(network), 3 replay/ghost.

**Pit window** (`pit_window_status`): -1 unavailable, 0 disabled, 1 closed,
2 open, 3 stopped (performing changes), 4 completed.

**Pit menu selection** (`pit_menu_selection`): -1 unavailable, 0 preset,
1 penalty, 2 driverchange, 3 fuel, 4 fronttires, 5 reartires, 6 body,
7 frontwing, 8 rearwing, 9 suspension, 10 button_top, 11 button_bottom, 12 max.

**Tire type** (`tire_type`): -1 unavailable, 0 option, 1 prime.
**Tire subtype** (`tire_subtype`): -1 unavailable, 0 primary, 1 alternate,
2 soft, 3 medium, 4 hard.
**Tire material** (`tire_on_mtrl`): -1 unavailable, 0 none, 1 tarmac, 2 grass,
3 dirt, 4 gravel, 5 rumble strip, 6 concrete.
**Tire index**: 0 FL, 1 FR, 2 RL, 3 RR. **Tire temp index**: 0 left, 1 center, 2 right.
**Engine type**: 0 combustion, 1 electric, 2 hybrid.
**Finish status**: -1 unavailable, 0 none (still on track), 1 finished, 2 DNF,
3 DNQ, 4 DNS, 5 DQ.
**Session length format**: -1 unavailable, 0 time based, 1 lap based,
2 time+lap (extra lap after time runs out).
**Pitstop status**: -1 unavailable, 0 two tyres unserved, 1 four tyres unserved,
2 served.
**Pit state** (`pit_state`): -1 N/A, 0 none, 1 requested stop, 2 entered pitlane,
3 stopped at pitspot, 4 exiting pitspot.
**Pit action** (`pit_action`): -1 N/A, 0 none, 1 preparing, bitmask:
2 penalty serve, 4 driver change, 8 refueling, 16 front tires, 32 rear tires,
64 body, 128 front wing, 256 rear wing, 512 suspension.
**Engine state** (`engineState`): -1 unavailable, 0 ignition off, 1 ignition on
not running, 2 ignition on starter running, 3 ignition on and running.
**Penalty type**: -1 unavailable, 0 DriveThrough, 1 StopAndGo, 2 Pitstop,
3 Time, 4 Slowdown, 5 Disqualify.
**Start lights**: -1 unavailable, 0 off, 1-5 redlight countdown, 6 greenlight.

### Flags struct (r3e_flags) — all -1 = no data, 0 = not active, 1 = active

- `yellow`, `yellowCausedIt`, `yellowOvertake`, `yellowPositionsGained` (n = positions),
  `sector_yellow[3]`, `closest_yellow_distance_into_track` (meters, -1.0 = none),
  `blue`, `black`, `green`, `checkered`, `white`.
- `black_and_white`: 0 not active, 1 blue flag 1st warning, 2 blue flag 2nd warning,
  3 wrong way, 4 cutting track.

### AidSettings (R3eAidSettings struct) — LIVE AID STATE FLAGS

```
tc / abs / esp / countersteer / cornering:
  -1 = N/A (field unavailable)
   0 = off
   1 = on (enabled but NOT actively intervening)
   5 = currently active (intervening right now)
```

- **Value 5 is the ONLY reliable "aid is actively cutting/intervening this frame" flag.**
- Values 0/1 are STATIC configuration, not live activity. NEVER use `== 1` or `!= 0`
  to detect active intervention — it will be permanently true whenever the assist is enabled.
- `RaceroomSharedMemoryReader.IsTcActive` (`AidSettings.Tc == 5`) is the reference
  implementation for this pattern (added for pedal haptics TC trigger).
- ESP is special: `2 = on low, 3 = on medium` (levels), still `5 = currently active`.
- ABS equivalent: `AidSettings.Abs == 5` = ABS actively cycling. The legacy line
  `physics.AbsInAction = (absState == 1 || absState == 5) ? 1 : 0` is WRONG for
  live-activity detection (1 = just enabled) — see the pedal feed fix which uses
  authoritative graphics flags instead.

### TractionControlPercent (float) — EMPIRICALLY VERIFIED (2026-08-01 logs)

```
-1.0      = N/A (field unavailable)
 0.0      = no cut (build 3.1+ behavior when TC off/stationary)
 5–95     = actual % of engine power being cut RIGHT NOW
 100.0    = STUCK AT 100.0 IN THE SHIPPED GAME BUILD — do NOT gate on this
```

- **REAL-WORLD BEHAVIOR (verified from this app's own serial log, osoyoo_serial_log.txt)**: in the
  shipped game build, `TractionControlPercent` stays pinned at **100.0** even during
  actual TC cuts. It is unusable as a live-cut gate — use it only for intensity
  scaling (value / 100) when a cut is confirmed by the aid flag.
- **Live-cut gate for R3E: `AidSettings.Tc == 5` (`TcActiveGfx`)** — empirically
  verified: the value FLIPS between 1 (armed, not cutting) and 5 (actively cutting)
  during real driving. Exactly as the official SDK documents.
- `AidSettings.tc` observed flipping 1 ↔ 5 in the same logs — value 5 IS live activity.
- **This is a shared-memory field semantic, not a pipeline detail.** Each haptics
  pipeline (pedal, HF8, steering) reads the same field from `RaceroomSharedMemoryReader`
  and applies this gate independently in its own code. Pipelines must NOT share code
  or state — only the raw telemetry field.

### TractionControlSetting / AbsSetting / EngineMapSetting / EngineBrakeSetting (int)

```
-1 = N/A, otherwise a STATIC setup/config value (0 = off, 1+ = level).
```

- These are driver configuration, NOT live activity. Do not drive effects from them.
- `abs_setting` (int, -1 = N/A) is the standalone ABS setup value.

### DRS / Push-to-Pass

- `r3e_drs`: `equipped` (0/1/-1), `available` (0/1/-1), `numActivationsLeft`
  (int32::max = endless; -1 N/A), `engaged` (0/1/-1).
- `drs_state` (per-driver): -1 unavailable, 0 not engaged, 1 engaged.
- `r3e_push_to_pass`: `available` (1 exists, 2 charging, 3 charged, -1 N/A),
  `engaged`, `amount_left`, `engaged_time_left`, `wait_time_left` (seconds).

### Damage (r3e_car_damage) — float 0.0–1.0, -1.0 = N/A

- `engine`, `transmission`, `aerodynamics` (0.0 doesn't necessarily mean destroyed),
  `suspension` (+ 2 reserved floats).

### Player data (r3e_playerdata — high precision, player vehicle only)

- `user_id`, `game_simulation_ticks` (1 tick = 1/400 s), `game_simulation_time` (s).
- `position`, `velocity`, `local_velocity`, `acceleration`, `local_acceleration`
  (vec3_f64, m/s, m/s²), `orientation`, `rotation`, `angular_acceleration`,
  `angular_velocity`, `local_angular_velocity` (rad/s).
- `local_g_force` (driver g-force local to car).
- **`steering_force`** and **`steering_force_percentage`** (r3e_float64) — total
  steering force through the steering bars; the FFB app derives centering force
  from `SteeringForcePercentage` (0-100%) with direction from steer input.
- `engine_torque` (current engine torque), `current_downforce` (N), `voltage`,
  `ers_level`, `power_mgu_h`, `power_mgu_k`, `torque_mgu_k` (currently unused).
- Suspension (radians/meters/m/s): `suspension_deflection[4]`,
  `suspension_velocity[4]`, `camber[4]`, `ride_height[4]`,
  `front_wing_height`, `front_roll_angle`, `rear_roll_angle`,
  `third_spring_suspension_deflection_front/rear`, `third_spring_suspension_velocity_front/rear`.

### Vehicle state (main struct) — units and ranges

- `car_speed` (m/s), `engine_rps` (rad/s), `max_engine_rps`, `upshift_rps`.
- `gear`: -2 N/A, -1 reverse, 0 neutral, 1 first... (electric: 2 = regen braking).
- `car_cg_location` (vec3_f32, Y up), `car_orientation` (pitch/yaw/roll, radians).
- `local_acceleration` (vec3_f32, +X=left, +Y=up, +Z=back, m/s²).
- `total_mass` (kg = car + penalty weight + fuel), `fuel_left`/`fuel_capacity`/
  `fuel_per_lap` (liters), `virtual_energy_left/capacity/per_lap` (MJ).
- `engine_temp`, `engine_oil_temp` (°C), `fuel_pressure`, `engine_oil_pressure`
  (KPa), `turbo_pressure` (Bar, -1.0 N/A).
- `throttle`/`throttle_raw`, `brake`/`brake_raw`, `clutch`/`clutch_raw`
  (0.0–1.0, -1.0 N/A), `steer_input_raw` (-1.0..1.0).
- `steer_lock_degrees` (center to full lock), `steer_wheel_range_degrees`,
  `steer_wheel_max_rotation` (-1 N/A, 0 auto, 180–1800 manual).
- `brake_bias` (0.3 = 30% rear, -1.0 N/A), `pit_limiter` (-1 N/A, 0 inactive, 1 active).
- `battery_soc` (0.0–100.0, -1.0 N/A), `water_left` (brake water tank, liters),
  `headlights` (-1 N/A, 0 off, 1 on, 2 strobing).
- `tire_wear_active` / `fuel_use_active`: -1 N/A, 0 off, 1-4 = multiplier x1-x4.
- `session_pit_speed_limit` (m/s), `session_time_duration`, `session_time_remaining` (s).

### Per-wheel fields (R3eTireData<T>)

- `TireRps` (rad/s), `TireSpeed` (m/s), `TireGrip` (0.0–1.0), `TireLoad` (N),
  `TirePressure` (KPa), `TireWear` (0.0–1.0), `TireFlatspot` (0 false / 1 true per wheel),
  `BrakePressure` (N — EMPIRICALLY VERIFIED 2026-08-01 logs: SDK header claims kN but the
  shipped game sends Newtons, e.g. 3653 N ≈ 3.65 kN under braking; normalize by ~5000 N,
  NOT by 5), `TireOnMtrl` (see tire material enum), `TireTemp` (°C).
- `tire_temp[4]` is `r3e_tire_temp` per wheel: `current_temp[3]` (left/center/right),
  `optimal_temp`, `cold_temp`, `hot_temp`.
- `brake_temp[4]` is `r3e_brake_temp` per wheel: `current_temp`, `optimal_temp`,
  `cold_temp`, `hot_temp` (°C).
- `TireFlatspot` per-wheel > 0.5 = flatspot active (field semantic shared by all pipelines).
- `BrakePressure` asymmetry (left vs right) is NOT by itself an ABS signal —
  normal cornering braking produces asymmetric pressure.

### Driver data (r3e_driver_data[128], all drivers in place order)

- `driver_info` (name[64] utf8, car_number, class_id, model_id, team_id, livery_id,
  manufacturer_id, user_id, slot_id, class_performance_index, engine_type,
  car_width, car_length, rating, reputation).
- `finish_status`, `place`, `place_class`, `lap_distance`, `lap_distance_fraction`,
  `position` (vec3_f32), `track_sector`, `completed_laps`, `current_lap_valid`.
- Timing (seconds): `lap_time_current_self`, `sector_time_current_self[3]`,
  `sector_time_previous_self[3]`, `sector_time_best_self[3]`, `time_delta_front`,
  `time_delta_behind`, `car_speed`.
- `pitstop_status`, `in_pitlane`, `num_pitstops`, `penalties` (cut track),
  `tire_type_front/rear`, `tire_subtype_front/rear`, `base_penalty_weight`,
  `aid_penalty_weight`, `drs_state`, `ptp_state`, `virtual_energy`, `penaltyType`,
  `penaltyReason`, `engineState`, `orientation`.
- `penaltyReason` values per penalty type (drive-through: 0 invalid, 1 cut track,
  2 pit speeding, 3 false start, 4 ignored blue, 5 driving too slow,
  6 illegally passed before green, 7 illegally passed before finish,
  8 illegally passed before pit entrance, 9 ignored slow down, 10 max).

### Session/event fields

- `track_name[64]`, `layout_name[64]` (utf8), `track_id`, `layout_id`,
  `layout_length` (m), `sector_start_factors` (sector1/2/3).
- `race_session_laps[3]`, `race_session_minutes[3]` (index 0-2 = race 1-3;
  if both > 0, session starts with minutes then adds laps).
- `event_index` (0-indexed, -1 N/A), `session_type`, `session_iteration`
  (1 = first...), `session_length_format`, `session_phase`.
- `number_of_laps` (or -1 in practice/test), `max_incident_points`, `incident_points`.
- `lap_time_best_leader`, `lap_time_best_leader_class`, `session_best_lap_sector_times[3]`,
  `lap_time_best_self`, `sector_time_best_self[3]`, `lap_time_previous_self`,
  `sector_time_previous_self[3]`, `lap_time_current_self`, `sector_time_current_self[3]`,
  `lap_time_delta_leader`, `lap_time_delta_leader_class`, `time_delta_front`,
  `time_delta_behind`, `time_delta_best_self` (-1000.0 = N/A),
  `best_individual_sector_time_self[3]`, `best_individual_sector_time_leader[3]`,
  `best_individual_sector_time_leader_class[3]`.
- `lap_valid_state` (-1 N/A, 0 this and next valid, 1 this invalid,
  2 this and next invalid), `prev_lap_valid` (-1 N/A, 0 invalid, 1 valid).
- `discharge_rate`, `brake_regen` (-1.0 N/A, 0.0–1.0).

### Application rules (enforced)

1. **R3E live-activity detection** must use `AidSettings.X == 5` (tc/abs/esp) or
   `TractionControlPercent > 5f && < 95f` for TC cuts. Never `setting != 0`.
   NOTE: `AidSettings.X == 5` IS empirically verified as the live-cut flag
   (observed flipping 1 ↔ 5 during driving, 2026-08-01 logs).
2. **Percent magnitude fields** (TractionControlPercent) drive intensity scaling,
   never the on/off gate.
3. If a new R3E effect is being built (steering FFB, HF8, pedal haptics), re-read this
   section and verify against the official SDK comments before wiring any field.
4. When in doubt about an R3E field, search the official `r3e-api` source first —
   do not rely on community forum guesses or older code comments in this repo.
5. **Struct is pack(1)** — C# structs must use `[StructLayout(LayoutKind.Sequential, Pack = 1)]`
   and Marshal.SizeOf must match the native layout exactly; verify offsets with
   hex dumps when reading late fields (e.g., `traction_control_percent`).

