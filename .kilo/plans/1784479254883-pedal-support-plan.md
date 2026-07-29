# Pedal Support — Independent Physical Pedal Input & Haptic Take-Over

## Problem

The app takes **exclusive DirectInput access** on the wheelbase for FFB output. This forces the user to close vendor software (Pit House, G HUB, etc.). Wheelbase-connected pedals (Moza SR-P Lite via RJ12) then lose their input bridge because:
1. Pit House provided a virtual joystick layer for games; without it, pedal axes may not reach the game
2. The HID path may be disrupted by exclusive access on the wheelbase's other HID collections

**Currently**: All pedal data comes from game shared memory (`MapRawData()` at `TelemetryLoop.cs:1051-1052`). The app never reads physical pedals.

## Design Decisions (Resolved)

| Decision | Choice | Rationale |
|---|---|---|
| **Input override semantics** | Replace (not merge) | Physical pedal values replace game telemetry. Exception: AI mode still uses telemetry. |
| **Config storage** | Split | Calibration → global `pedal_config.json` (hardware-specific, set once). Haptic routing → per-profile in FfbProfile (car-specific feel). |
| **SC Bridge rebuild** | Build on demand | Extended `sc_bridge.cpp` source provided; pre-built x64 binary committed. SC Link features deferred until rebuilt. |
| **Haptic update rate** | Decoupled 60Hz | PedalHapticManager runs on its own timer, reads latest FfbVibrationMixer signals at 60Hz. |
| **Input source priority** | SC Link > HID Direct > DirectInput > Keyboard > Game Telemetry | Auto-detect best available. Fall back gracefully on failure. |

## Threading Model

```
┌─ TelemetryLoop Thread (333Hz) ─────────────────────────────┐
│                                                             │
│  MapRawData() → overwrite BrakeInput/GasInput → pipeline    │
│       ↑ reads from latest lock-free PedalState struct       │
│                                                             │
│  After pipeline: store latest FfbVibrationMixer signals     │
│       → update lock-free haptic signal buffer               │
│                                                             │
│  (never blocks — 1-2μs atomic reads)                       │
└─────────────────────────────────────────────────────────────┘

┌─ HID Read Thread (250Hz) ──────────────────────────────────┐
│                                                             │
│  ReadFile(hidHandle, inputReport, ...)                      │
│  Parse axis bytes → normalized 0-1                          │
│  Write to lock-free PedalState buffer via Interlocked.Exchange│
│  (or Volatile.Write on struct)                              │
│                                                             │
│  On ReadFile timeout (>16ms) → mark source as failed        │
│  On handle invalid → close, re-enumerate on next cycle      │
└─────────────────────────────────────────────────────────────┘

┌─ Pedal Haptic Thread (60Hz) ───────────────────────────────┐
│                                                             │
│  Read latest haptic signals from lock-free buffer           │
│  Route per PedalHapticRouteConfig                           │
│  Write haptic commands to provider backends:                │
│    - ActivePedal: SC Link force_N generateEffect()          │
│    - P-HPR: WriteFile to HID handle                         │
│    - Fanatec: FSTransducerDownloadEffect()                  │
│                                                             │
│  (never blocks main loop)                                   │
└─────────────────────────────────────────────────────────────┘
```

## Data Flow

```
[Physical Pedals — Input Sources]
  │
  ├─→ HID Direct Reader (ReadFile input reports via setupapi+hid.dll)
  │     Primary: wheelbase-connected (Moza, Fanatec, Thrustmaster via RJ12)
  │     Also: USB direct custom HID (Heusinkveld, VRS, DC Simracing, Simagic P1000)
  │
  ├─→ DirectInput Reader (non-exclusive Joystick.Poll(), SharpDX)
  │     Fallback: standard game-controller pedals (Logitech, Thrustmaster USB, BJ)
  │     Compatible with exclusive FFB — DI exclusive only blocks FFB, not state
  │
  ├─→ SC Link Reader (ActivePedal position/force variables via sc_bridge)
  │     For Simucube ActivePedal: ap.pedal_face_pos_mm → BrakeInput
  │
  └─→ Keyboard Simulator (GetAsyncKeyState, for testing without hardware)
        ↓
  PedalInputManager (auto-select by priority, handles failover)
        ↓
  PedalCalibration (per-axis: deadzone, min/max, invert, EMA – from global config)
        ↓
  PedalState (GasInput 0-1, BrakeInput 0-1, ClutchInput 0-1, Source)
        ↓ (lock-free, latest-atomically-replaced)
  ┌── Integration Point ───────────────────────────────────┐
  │  TelemetryLoop.cs ~line 411: after MapRawData(),       │
  │  if (pedalInputEnabled && pedalInput.TryGetState(out)) │
  │      overwrite: raw.BrakeInput = state.BrakeInput      │
  │                  raw.GasInput   = state.GasInput       │
  │  EXCEPTION: if AI driving, skip override (use telemetry)│
  └────────────────────────────────────────────────────────┘
        ↓
  FfbPipeline.Process(raw)  ← uses physical pedal values
        ↓
  BrakeBoost, ABS Vibration, etc. respond to real pedal data

[Pedal Haptic Output — Actuation Path]
  FfbPipeline.Process() ← stores latest individual signals
        ↓ (lock-free, latest-atomically-replaced)
  ┌── PedalHapticManager (60Hz timer) ───────────────────┐
  │  Reads from FfbVibrationMixer outputs:               │
  │    AbsForceModulation, ScrubModulation,               │
  │    RearSlipModulation, CurbSeverity, RoadForceMod,   │
  │    TcRumble (from pipeline), BrakePressure (R3E)     │
  │                                                      │
  │  Route table (per PedalHapticRouteConfig in profile): │
  │    Device: ActivePedal Brake                          │
  │      ABS  → force_N offset ±30N @ 15Hz pulse         │
  │      Curb → force_N offset -40N 80ms pulse           │
  │    Device: ActivePedal Gas                            │
  │      TC   → force_N offset -15N sustained            │
  │    Device: P-HPR Brake Reactor                        │
  │      ABS  → motor amplitude 0-65535                  │
  │    Device: Fanatec Pedal Rumble                       │
  │      ABS  → transducer magnitude 0-10000             │
  │                                                      │
  │  Outputs per provider type:                           │
  │    IPedalHapticProvider.SetBrakeHaptic(intensity,type)│
  │    IPedalHapticProvider.SetGasHaptic(intensity,type)  │
  └──────────────────────────────────────────────────────┘
```

## New Files

### Pedal Input — `src/AcEvoFfbTuner.Core/PedalInput/`

| File | Purpose |
|---|---|
| `IPedalInputSource.cs` | `TryGetState(out PedalState)`, `DeviceName`, `SourceType`, `IsAvailable` |
| `PedalInputManager.cs` | Source selection, priority chain, failover, `TryGetState()` |
| `PedalCalibration.cs` | Per-axis: deadzone → remap [dzn, 1]→[0,1], min/max range, invert, EMA |
| `PedalState.cs` | `GasInput`, `BrakeInput`, `ClutchInput`, `SourceType`, `Timestamp` |
| `Sources/HidPedalSource.cs` | HID Direct reader — background ReadFile thread, parse usage pages |
| `Sources/DirectInputPedalSource.cs` | Non-exclusive DI — enumerate, `GetCurrentState()`, map DI axes |
| `Sources/ScLinkPedalSource.cs` | SC Link — read `ap.pedal_face_pos_mm`, `ap.primary_input` per role |
| `Sources/KeyboardPedalSource.cs` | `GetAsyncKeyState` → W=gas ramp, S=brake ramp, A=clutch |
| `Sources/PedalReplaySource.cs` | CSV snapshot replay, Stopwatch-paced |
| `PedalDeviceDetector.cs` | Enumerate HID, DI, SC Link devices; match known VIDs; present list |

### Pedal Haptic Output — `src/AcEvoFfbTuner.Core/PedalHaptics/`

| File | Purpose |
|---|---|
| `IPedalHapticProvider.cs` | `SetBrakeHaptic(float, HapticType)`, `SetGasHaptic(float, HapticType)`, `DeviceName`, `IsAvailable` |
| `PedalHapticManager.cs` | 60Hz timer, reads signals, routes via config, calls providers |
| `PedalHapticRouteConfig.cs` | Model: per-device signal→pedal→gain mapping (serialized in FfbProfile) |
| `HapticType.cs` | Enum: `Abs`, `Tc`, `Curb`, `Road`, `Scrub`, `RearSlip`, `BrakePressure` |
| `Providers/ActivePedalProvider.cs` | SC Link force_N FFB pipeline for ActivePedal ABS/TC push-back |
| `Providers/SimagicHprProvider.cs` | HID output reports to P-HPR (VID 0x3235), 16-bit amplitude per motor |
| `Providers/FanatecPedalProvider.cs` | `FSTransducerDownloadEffect` for Clubsport V3 / CSL Elite V2 |
| `Providers/GenericHidHapticProvider.cs` | HID feature/output report for any HID haptic device |

### Config — `src/AcEvoFfbTuner.Core/Config/`

| File | Purpose |
|---|---|
| `PedalConfigManager.cs` | Load/save `pedal_config.json` from AppData. Singleton. Defaults on missing. |
| `PedalConfig.cs` | Model: `GasDeadzone`, `BrakeDeadzone`, `ClutchDeadzone`, `GasMin`/`Max`, `BrakeMin`/`Max`, `ClutchMin`/`Max`, per-axis `Invert`, `Smoothing` |

### SC Bridge Extension — `lib/simucube/sc-bridge/sc_bridge.cpp`

Add C exports:
- Device enumeration: `sc_get_device_count(...)`, `sc_get_device_session_id(...)`, `sc_device_has_feedback_type(...)`, `sc_get_device_role(...)`, `sc_get_device_name(...)`
- Variable reading: `sc_read_variable_float(...)` for `ap.pedal_face_pos_mm`, `ap.force_N`, `ap.primary_input`
- FFB pipeline: `sc_ffb_configure_force_N(...)`, `sc_ffb_configure_force_relative(...)`, `sc_ffb_configure_position_mm(...)`

## Modified Files

| File | Changes |
|---|---|
| `TelemetryLoop.cs` | Add `PedalInputManager`; after `MapRawData()`, overwrite if enabled (skip if AI); expose mixer signals for haptic thread |
| `FfbProfile.cs` | Add `PedalHapticConfig` sub-config; bump to v22; migration |
| `FfbVibrationMixer.cs` | Expose `AbsModulation`, `ScrubModulation`, `RearSlipModulation`, `CurbSeverity`, `RoadForceMod` as public properties |
| `MainViewModel.cs` | Observable properties for pedal haptic routing (gains, device select, enable toggle) |
| `MainWindow.xaml` (pages) | `PedalHapticPage.xaml` — routing config per pedal+signal |
| `SimucubeProvider.cs` | Replace stub — integrate SC Link for wheelbase torque + detect ActivePedals |
| `SimucubeSdkNative.cs` | NEW — P/Invoke declarations for sc_bridge.dll extended exports |
| `FanatecProvider.cs` | New: `SetPedalHaptic(intensity)` method using `FSTransducerDownloadEffect` |
| `HapticData.cs` | Unchanged — continues to drive wheelbase haptics only |
| `IFFBProvider.cs` | Unchanged |
| `FfbLiveServer.cs` | Add pedal source indicator to HTML visualizer |
| `ProfileManager.cs` | Load default haptic routing config when creating new profiles |
| `AcEvoFfbTuner.Core.csproj` | Add `<Content Include>` for `sc_bridge.dll` |

## Global Config: `pedal_config.json`

Stored at `%AppData%/AcEvoFfbTuner/pedal_config.json`

```json
{
  "version": 1,
  "calibration": {
    "gas": {
      "deadzone": 0.02,
      "min": 0.0,
      "max": 1.0,
      "invert": false,
      "smoothing": 0.85
    },
    "brake": {
      "deadzone": 0.03,
      "min": 0.0,
      "max": 1.0,
      "invert": false,
      "smoothing": 0.85
    },
    "clutch": {
      "deadzone": 0.02,
      "min": 0.0,
      "max": 1.0,
      "invert": false,
      "smoothing": 0.50
    }
  },
  "autoDetectDevice": true,
  "preferredSource": "Auto"
}
```

`PedalConfigManager`:
- `Load()` — reads file, returns `PedalConfig` with defaults on missing/corrupt
- `Save(PedalConfig)` — writes to disk
- `CalibrationChanged` event for live UI updates
- Thread-safe via `ReaderWriterLockSlim`

## Profile Additions: `FfbProfile.PedalHapticConfig`

```
PedalHapticConfig:
  enabled (bool, default false)
  brakeHapticDevice (Auto | None | ActivePedal | P-HPR | FanatecPedal | GenericHID, default Auto)
  gasHapticDevice (Auto | None | ActivePedal | P-HPR | FanatecPedal | GenericHID, default Auto)
  clutchHapticDevice (Auto | None | ActivePedal, default Auto)
  brakeHapticGain (0-2, default 1.0)
  gasHapticGain (0-2, default 1.0)
  routes: [
    { signal: "abs",   targetPedal: "brake", device: "auto", gain: 1.0, hapticType: "vibration" },
    { signal: "tc",    targetPedal: "gas",   device: "auto", gain: 1.0, hapticType: "vibration" },
    { signal: "curb",  targetPedal: "both",  device: "auto", gain: 0.5, hapticType: "pulse" },
    { signal: "road",  targetPedal: "brake", device: "auto", gain: 0.3, hapticType: "vibration" },
    { signal: "scrub", targetPedal: "gas",   device: "auto", gain: 0.4, hapticType: "vibration" }
  ]
```

(Default routes are sensible — most users won't touch these.)

## Edge Cases

| Scenario | Behavior |
|---|---|
| **No pedals connected** | Source returns no data → game telemetry used (current behavior). |
| **Pedals disconnect mid-session** | Source `IsAvailable` goes false → PedalInputManager falls back to next priority source within one main-loop tick (3ms). If no source: game telemetry. |
| **USB re-plug** | PedalDeviceDetector re-scans every 5 seconds in background; on new device match, source is hot-added. |
| **AI driving** | `TelemetryLoop` checks `IsAiControlled` (already exists for R3E). Skip pedal override when AI is in control — game telemetry used. |
| **Pit limiter / auto-engine start** | Not special-cased. Physical pedal values replace telemetry. If the game uses telemetry for these, the user must use the physical pedal. |
| **Startup with no profile** | `PedalConfigManager.Load()` returns defaults. `PedalHapticConfig.Enabled = false` by default. |
| **Corrupt pedal_config.json** | `PedalConfigManager` catches parse errors, logs warning, returns defaults. |
| **sc_bridge.dll not found** | `SimucubeSdkNative` fails to load → `ScLinkPedalSource` is not registered → skip. No crash. |
| **ReadFile timeout** | If no input report received within 16ms, `HidPedalSource` retries up to 3 times, then marks the device as failed and logs. |
| **Multiple pedal devices detected** | PedalInputManager uses the highest-priority source. If a user has both HID and DI pedal devices, `PedalDeviceDetector` reports all, and the user can manually select via the UI. |
| **HID ReadFile + Exclusive DI on same wheelbase** | HID collection 1 (input) and collection 2 (FFB) are separate top-level collections. DirectInput exclusive on collection 2 does not block `ReadFile` on collection 1. Verified by community (Moza R5, R9). |

## Implementation Order

```
Phase 0: Foundation (no hardware required)
  ├── 0a. PedalConfigManager + pedal_config.json (global config)
  ├── 0b. PedalCalibration (math + unit tests)
  ├── 0c. IPedalInputSource + PedalState model
  ├── 0d. PedalInputManager (source selection, priority, fallback)
  ├── 0e. KeyboardPedalSource (GetAsyncKeyState ramp)
  ├── 0f. PedalReplaySource (CSV snapshot replay)
  └── 0g. Wire into TelemetryLoop (override after MapRawData, skip during AI)

Phase 1: HID + DI Input Reading (hardware recommended, but testable via keyboard sim)
  ├── 1a. DirectInputPedalSource (non-exclusive Poll(), auto-detect axis mapping)
  ├── 1b. Extract shared HID helpers from WheelLedController → static HidApi class
  ├── 1c. HidPedalSource (ReadFile thread, usage-page parsing, known-offset fast-path)
  ├── 1d. PedalDeviceDetector (enumerate + match + present)
  └── 1e. Unit tests + diagnostic logging

Phase 2: Haptic Output Infrastructure (no hardware required for core logic)
  ├── 2a. IPedalHapticProvider + HapticType enum
  ├── 2b. PedalHapticRouteConfig (model + profile serialization)
  ├── 2c. PedalHapticManager (60Hz timer, signal reading, route dispatch)
  ├── 2d. FfbVibrationMixer: expose individual modulation values as public
  ├── 2e. GenericHidHapticProvider (HID output reports for generic haptics)
  └── 2f. Unit tests: routing logic, gain application, signal scaling

Phase 3: Vendor Haptic Providers (hardware required for full testing)
  ├── 3a. SimagicHprProvider (HID WriteFile for P-HPR reactors)
  ├── 3b. FanatecPedalProvider (FSTransducerDownloadEffect for pedal rumble)
  ├── 3c. sc_bridge extension (C++ rebuild for device enumeration + pedal FFB)
  └── 3d. ActivePedalProvider (SC Link force_N pipeline for ABS push-back)

Phase 4: UI
  ├── 4a. Calibration page (device selector, per-axis sliders, live raw display, calibrate button)
  ├── 4b. Haptic routing page (per-signal gain sliders, device select, haptic type)
  ├── 4c. Profiler overlay: pedal source indicator + raw/calibrated values
  └── 4d. Hotkey binding for keyboard toggle

Phase 5: Profile Integration
  ├── 5a. PedalHapticConfig in FfbProfile v22 (migration, ApplyToPipeline, UpdateFromPipeline)
  └── 5b. Default profile includes default haptic routes for known wheelbase+pedal combos
```

## Testing Without Hardware

| What | How |
|---|---|
| **PedalCalibration** | Unit tests: all combinations of deadzone, min/max, invert, EMA |
| **PedalInputManager priority/failover** | Unit tests: mock sources that fail, verify fallback chain |
| **TelemetryLoop integration** | Unit test: mock PedalInputManager, verify BrakeInput override |
| **PedalHapticManager routing** | Unit test: mock signals in, verify correct per-device commands out |
| **Keyboard pedal simulation** | Manual: toggle on, press W/S/A, observe profiler values change |
| **CSV replay** | Manual: load a snapshot, observe FFB output change with pedal replay |
| **AI mode skip** | Manual: enable keyboard pedals, set AI driving flag, verify game telemetry used |
| **HID ReadFile coexistence** | Requires hardware — provide `diagnostic_debug.log` when user tests |
| **SC Link enumeration** | Requires Simucube hardware — provide verbose logging for remote debugging |

## Validation

1. `dotnet clean && dotnet build -c Release` — full build succeeds
2. `dotnet test` — all unit tests pass (PedalCalibration, PedalInputManager, PedalHapticManager)
3. Keyboard pedal W/A/S/D affects profiler gas/brake bars and pipeline brake boost
4. CSV replay feeds pedal data through pipeline correctly
5. AI mode override suppresses physical pedal values
6. Profile save/reload preserves haptic routing config
7. Existing profiles (v21) load without error, haptic config defaults to disabled
8. PedalConfigManager creates `pedal_config.json` on first launch, loads defaults on missing
9. No regression: `Enabled = false` → behavior identical to before
