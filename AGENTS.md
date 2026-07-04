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