# Plan: Add LMU (Le Mans Ultimate) Game Support

## Summary

Add Le Mans Ultimate (LMU) as a third supported game following the same pattern as the existing RaceRoom (R3E) integration. LMU runs on the rFactor 2 engine and exposes shared memory with steering column force telemetry, similar to R3E.

**Pattern to follow:** R3E integration — LMU/rF2 provides total column force (not SAT/Mz), requiring similar centering shaping, DC blocking, and brake boost in the pipeline.

---

## New Files to Create

### 1. `src/AcEvoFfbTuner.Core/SharedMemory/Structs/LmuSharedStruct.cs`

rFactor 2 / LMU shared memory struct definitions. The shared memory map name for LMU needs verification (likely `Local\rFactor2SMMP_0` or an LMU-specific name). Define the `RFactor2Shared` struct with all necessary fields sourced from the public rFactor 2 shared memory specification.

Key fields to define:
- `mVersion`, `mSteeringForce`, `mUnfilteredSteeringForce`, `mSpeed`, `mEngineRps`, `mEngineMaxRps`
- `mFuel`, `mGear`, `mSteeringWheelAngle`, `mSteeringArm`
- `mTireSpeed[4]`, `mTireGrip[4]`, `mTireLoad[4]`, `mTirePressure[4]`, `mTireWear[4]`
- `mTireTemp[4]` (per-corner or structured)
- `mSuspensionDeflection[4]`, `mSuspensionVelocity[4]`
- `mSlipAngle[4]` — LMU/rF2 provides native per-wheel slip angle
- `mLateralAcceleration`, `mLongitudinalAcceleration`, `mVerticalAcceleration`
- `mLocalVelocity` (Vec3), `mLocalAcceleration` (Vec3), `mOrientation`, `mAngularVelocity` (Vec3)
- `mBrake`, `mThrottle`, `mClutch`
- `mFlag`, `mGamePhase`, `mTrackName[64]`, `mCarName[64]`, `mLayoutName[64]`
- `mLapNumber`, `mLapDistance`, `mPosition`, `mNumCars`

**Research needed:** Exact struct layout from rF2 public spec. Verify LMU doesn't deviate from rF2 spec.

### 2. `src/AcEvoFfbTuner.Core/SharedMemory/LmuSharedMemoryReader.cs`

Implement `ISharedMemoryReader`. Pattern follows `RaceroomSharedMemoryReader.cs`.

- Connect to shared memory map (`MemoryMappedFile.OpenExisting`)
- `TryReadPhysics()`: Map rF2 fields → `SPageFilePhysicsEvo`:
  - `FinalFf` → from `mSteeringForce`
  - `SteerAngle` → from `mSteeringWheelAngle`
  - `SpeedKmh` → from `mSpeed * 3.6f`
  - `Gas` / `Brake` → from `mThrottle` / `mBrake`
  - `Gear` → from `mGear` (rF2: 0=neutral, 1=R, 2+=forward)
  - `Rpms` → from `mEngineRps * 60 / (2*PI)`
  - `Mz[0]/Mz[1]` → from `mSteeringForce` (centering logic similar to R3E)
  - `SlipAngle[0..3]` → from native `mSlipAngle[4]`
  - `SlipRatio[0..3]` → computed from tire speeds vs car speed
  - `WheelLoad`, `WheelsPressure`, `TyreWear` → direct map
  - `TyreTempI/M/O` → from `mTireTemp`
  - `SuspensionTravel` → from `mSuspensionDeflection`
  - `Fx/Fy` → synthesized from load & slip (same pattern as R3E)
  - `KerbVibration` → synthesized from suspension deflection deltas
  - `RoadVibrations` → synthesized from tire speed deltas
  - `SlipVibrations` → synthesized from slip ratio
  - `AbsVibrations` → synthesized from brake + tire speed asymmetry
  - `AbsInAction` → detected from brake + wheel speed
  - `Heading`, `Pitch`, `Roll` → from orientation
  - `LocalAngularVel` → from `mAngularVelocity`
  - `AccG` → from lateral/longitudinal/vertical acceleration
  - `Velocity`, `LocalVelocity` → from `mLocalVelocity`
  - `CarDamage` → from suspension velocity / metrics if available
- `TryReadGraphics()`: Map to `SPageFileGraphicEvo`:
  - `Status` → from `mGamePhase`
  - `CarModel` → from `mCarName`
  - `RpmPercent` → from `mEngineRps / mEngineMaxRps`
  - Lap distance, position, flag, session state
  - `SteerDegrees` → estimated or from wheel angle range
- `TryReadStatic()`: Map to `SPageFileStaticEvo`:
  - `Track` → from `mTrackName`
  - `TrackConfiguration` → from `mLayoutName`
  - `TrackLengthM` → if available
- Expose `TireGrip` (float[4]) and `LocalAccelG` (float[3]) for injection in `TelemetryLoop`

### 3. `src/AcEvoFfbTuner.Core/FfbProcessing/LmuFfbPipeline.cs`

Subclass `FfbPipeline` following `R3eFfbPipeline.cs` pattern.

- `CoreForceMultipler` default = 3.0f (same as R3E)
- Override `Process()`:
  - Brake weight-transfer boost (same `BrakeBoostGain`/`BrakeBoostThreshold`)
  - Apply `combinedBoost = brakeBoost * CoreForceMultiplier` to result
- Override `OnDetailForceProcessed()`:
  - DC blocker: `detailForce -= _dcBlockSmooth; _dcBlockSmooth += dcBlocked * 0.02f`
  - Dynamic suppression: prevent detail from reversing core direction
- Override `ApplyCenteringOverride()`:
  - Smoothstep ramp from 0 to full force over `CenterSharpnessDegrees`
- `GearChangeMuteEnabled` / `GearSpikeThreshold` properties (same pattern)

---

## Existing Files to Modify

### A. `MainViewModel.cs` — Game Enum & Registration

| Location | Change |
|----------|--------|
| Line 25-30 `SupportedGame` enum | Add `LeMansUltimate` |
| Line 65-70 `GameDisplayName` | Add `SupportedGame.LeMansUltimate => "Le Mans Ultimate"` |
| Line 72-74 Boolean properties | Add `public bool IsLeMansUltimate => SelectedGame == SupportedGame.LeMansUltimate;` |
| Line 1406-1411 `CreateReader()` | Add `SupportedGame.LeMansUltimate => new LmuSharedMemoryReader()` |
| Line 1413-1418 `CreatePipeline()` | Add `SupportedGame.LeMansUltimate => new LmuFfbPipeline()` |
| Line 1431-1434 `OnSelectedGameChanged()` | Add `nameof(IsLeMansUltimate)` |

### B. `MainWindow.xaml.cs` — Game Cycle

| Location | Change |
|----------|--------|
| Line 408-417 `OnGamePillClick` | Add `SupportedGame.AssettoCorsa => SupportedGame.LeMansUltimate` then `_ => SupportedGame.AcEvo` |

### C. `MainWindow.xaml` — Game Pill Tooltip

| Location | Change |
|----------|--------|
| Line 186 | Update tooltip: `"Switch between AC EVO, RaceRoom, Assetto Corsa, and Le Mans Ultimate"` |

### D. `TelemetryLoop.cs` — Game-Specific Data Injection

| Location | Change |
|----------|--------|
| After line 390 | Add `if (_reader is LmuSharedMemoryReader lmu) { raw.TyreGrip = lmu.TireGrip; raw.DisplayAccG = lmu.LocalAccelG; }` |
| Line 423 | Add LMU to the physical wheel position read check if needed |
| Line 461 (handover fade) | Add `_reader is LmuSharedMemoryReader` to the pattern match |

### E. `FfbProfile.cs` — Profile Apply/Update

| Location | Change |
|----------|--------|
| After line 163 (R3E block) | Add `if (pipeline is LmuFfbPipeline lmu)` block with same properties: `GearChangeMuteEnabled`, `GearSpikeThreshold`, `BrakeBoostGain`, `BrakeBoostThreshold`, `CoreForceMultiplier` |
| `UpdateFromPipeline()` (find location) | Add LMU reverse mapping |

### F. `ProfilesPage.xaml` — Game Filter UI

| Location | Change |
|----------|--------|
| After line 340 | Add `<RadioButton x:Name="GameFilterLeMansUltimate" Content="Le Mans Ultimate" GroupName="GameFilter" Style="{StaticResource FilterPillStyle}" Checked="OnGameFilterChanged" />` |

### G. `ProfilesPage.xaml.cs` — Game Filter Logic

| Location | Change |
|----------|--------|
| Line 93-94 `OnGameFilterChanged` | Add `else if (ReferenceEquals(sender, GameFilterLeMansUltimate)) _filterGame = "Le Mans Ultimate";` |
| Line 111-114 `GetFilteredProfiles` | Add `_filterGame == "Le Mans Ultimate"` handling |

### H. `DiscordPresenceService.cs` — Game Name Detection

| Location | Change |
|----------|--------|
| Lines 126-131 | Add `LmuSharedMemoryReader => "Le Mans Ultimate"` |
| Gear mapping (line 180-188) | LMU uses same gear convention as R3E (0=N, 1=R, 2+=forward), so existing fallback handles it correctly |

### I. `FfbTuningPage.xaml` — LMU-Specific UI

Add LMU-specific panels if needed (same as R3E panels for center sharpness, brake boost, core force multiplier). Guard with `IsLeMansUltimate` binding.

### J. `CompactTunerWindow.xaml`

Same as above — add LMU-specific controls guarded by `IsLeMansUltimate`.

---

## Research Items — Resolved

1. **Shared memory map name**: **CONFIRMED** — `"LMU_Data"` (no `Local\` prefix). Also has lock maps `"LMU_SharedMemoryLockData"` and event `"LMU_Data_Event"`. Official SDK headers in `Support/SharedMemoryInterface/`.

2. **Struct layout**: **CONFIRMED** from `InternalsPlugin.hpp` (pack=4). Layout:
   - `SharedMemoryObjectOut` = `SharedMemoryGeneric` (events[15], gameVersion, FFBTorque, ApplicationStateV01) + `SharedMemoryPathData` (5x260 paths) + `SharedMemoryScoringData` (ScoringInfoV01 + 104 VehicleScoringInfoV01 + 64KB scoringStream) + `SharedMemoryTelemetryData` (3 header bytes + 104×TelemInfoV01)
   - Total capacity: 320KB (327680 bytes)
   - Player's `TelemInfoV01` is located by scanning for the player's vehicle name

3. **Steering force**: **CONFIRMED** — `TelemInfoV01.mSteeringShaftTorque` (double at offset 420 from TelemInfoV01 start) is the steering column torque. Also `SharedMemoryGeneric.FFBTorque` (float at offset 64) available.
   - Sign convention: follows rF2 coordinate system (+x points left, +z points back). Mz synthesized from absolute torque × steer sign blend (same as R3E).

4. **AI control detection**: Available via `VehicleScoringInfoV01.mControl` field. Not yet implemented — default IsAiControlled=0.

5. **Steer lock degrees**: Available from `TelemInfoV01.mPhysicalSteeringWheelRange` (float at end of struct). Falls back to 900° if not found.

6. **Wheel data**: `TelemWheelV01` contains suspension deflection, rotation speed, tire load, pressure, temperature (3-zone), wear, grip fraction, lateral/longitudinal forces — all doubles. Available for all 4 wheels.

---

## Implementation Order

1. Create `LmuSharedStruct.cs` with rF2 shared memory struct definitions
2. Create `LmuSharedMemoryReader.cs` implementing `ISharedMemoryReader`
3. Create `LmuFfbPipeline.cs` subclassing `FfbPipeline`
4. Modify `MainViewModel.cs` (enum + factory methods)
5. Modify `MainWindow.xaml.cs` (game cycle)
6. Modify `TelemetryLoop.cs` (data injection)
7. Modify `FfbProfile.cs` (profile apply/update)
8. Modify `ProfilesPage.xaml` + `.cs` (game filter)
9. Modify `DiscordPresenceService.cs` (game name)
10. Modify `MainWindow.xaml` (tooltip), `FfbTuningPage.xaml`, `CompactTunerWindow.xaml` (UI panels)

**Build verification:** `dotnet clean AcEvoFfbTuner.slnx -c Release -q 2>&1; dotnet build AcEvoFfbTuner.slnx -c Release`
