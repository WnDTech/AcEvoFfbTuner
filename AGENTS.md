# Kilo Rules

## FFB Pipeline Development

### Code vs Profile — Always Clarify
When making changes, ALWAYS explicitly state whether you are:
- **Changing app code** (C# source in `src/`) — affects all users, persisted across profiles
- **Changing a profile** (JSON in `AppData/Roaming/AcEvoFfbTuner/Profiles/`) — per-user settings

Our goal is to **fix the code** so we can establish a reliable baseline profile that works for all users of this car (BMW GT3 with Moza R5).

### Build & Verify
- Build command: `dotnet build AcEvoFfbTuner.slnx -c Release`
- Always build after code changes
- Run lint/typecheck if available

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
Strings: smVersion, acVersion, carModel, track, playerName, playerSurname, playerNick.

Configuration: numberOfSessions, numCars, maxRpm, maxFuel.

Scaling Factors:

steerRatio (Crucial for normalizing wheel degrees to rack movement).

suspensionMaxTravel[4].