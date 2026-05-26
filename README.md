# AC Evo FFB Tuner

[![CI Build](https://github.com/WnDTech/AcEvoFfbTuner/actions/workflows/ci.yml/badge.svg)](https://github.com/WnDTech/AcEvoFfbTuner/actions/workflows/ci.yml)
[![Release](https://github.com/WnDTech/AcEvoFfbTuner/actions/workflows/release.yml/badge.svg)](https://github.com/WnDTech/AcEvoFfbTuner/actions/workflows/release.yml)
[![Latest Release](https://img.shields.io/github/v/release/WnDTech/AcEvoFfbTuner?label=latest)](https://github.com/WnDTech/AcEvoFfbTuner/releases/latest)

**Website:** [ffbtuner.wndtech.tips](https://ffbtuner.wndtech.tips/)

A Windows desktop application that intercepts, processes, and enhances Force Feedback signals from **Assetto Corsa EVO**, **RaceRoom Racing Experience**, and **Assetto Corsa** before sending them to your DirectInput-compatible steering wheel.

Instead of relying on the game's native FFB output, this tool reads raw physics telemetry from each game's shared memory — tire forces, slip data, suspension travel, G-forces, vibrations — runs it through a fully configurable DSP pipeline with game-specific overrides, and sends the processed result directly to the wheel via a 1 kHz interpolation thread for buttery-smooth force output.

Each game has its own isolated FFB pipeline, shared memory reader, and haptic synthesis path, so tuning one game never affects another.

## Download & Install

Download the latest installer from [GitHub Releases](https://github.com/WnDTech/AcEvoFfbTuner/releases/latest).

The app checks for updates automatically on startup and will notify you when a new version is available.

**Requirements:** Windows 10+, .NET 8.0 runtime (bundled in the self-contained installer), a DirectInput-compatible FFB wheel.

## Features

### Multi-Game Support

Select your game from a dropdown — the app automatically switches to the correct shared memory reader, FFB pipeline, and haptic synthesis:

| Game | Reader | Pipeline | Shared Memory |
|------|--------|----------|---------------|
| **Assetto Corsa EVO** | `SharedMemoryReader` | `FfbPipeline` (base) | `LocalSurface` memory-mapped file |
| **RaceRoom Racing Experience** | `RaceroomSharedMemoryReader` | `R3eFfbPipeline` | `$R3E` memory-mapped file |
| **Assetto Corsa** | `AssettoCorsaSharedMemoryReader` | `AcFfbPipeline` | AC shared memory |

### Multi-Stage FFB Processing Pipeline

1. **Auto-Gain** — Compensates for car-specific FFB multipliers from shared memory
2. **Channel Mixer** — Blends Mz (self-aligning torque), Fx (longitudinal), and Fy (lateral) tire forces (front + rear) with independent gain, normalization, and wheel-load weighting; single speed-dependent filter for minimal phase lag
3. **Normalize + Compress** — Force scaling with `tanh`-based dynamic range compression and configurable power curve
4. **LUT Response Curve** — Preset force response curves (Linear, Soft Center, Progressive, Dead Zone)
5. **Slip Enhancement** — Amplifies FFB during wheelspin and oversteer with configurable thresholds
6. **Damping & Friction** — Speed-dependent damping, Coulomb friction model (constant opposing motion), angular acceleration inertia
7. **Dynamic Effects** — Cornering force, suspension travel, acceleration/braking, road feel, car rotation (yaw rate)
8. **Tyre Flex** — Simulates carcass deformation and contact patch dynamics for realistic steering feel
9. **Output Clipping** — Soft clipping with configurable threshold
10. **Physics-Preserving Center Fade** — Linear ramp within narrow band to prevent zero-crossing notchiness while preserving the Mz curve including the critical grip-limit peak/dropoff
11. **Low-Speed Fade** — Gradual force reduction below 5 km/h, zero output below 0.5 km/h
12. **Slew Rate Limiter** — Speed-scaled maximum force change per tick for fast transients on kerb strikes
13. **10-Band Equalizer** — Parametric biquad EQ shaping force signal frequency content (see below)
14. **Vibration Mixer** — Curb, slip, road surface, ABS vibration, tire scrub texture (30–50 Hz at grip limit), rear slip warning (12–25 Hz oversteer rumble), ABS force modulation
15. **LFE/Rumble Generator** — Low-frequency effects driven by suspension travel and RPM for engine rumble and bump impacts

### Game-Specific Pipeline Overrides

Each game's pipeline extends the base with game-specific force shaping:

**RaceRoom (`R3eFfbPipeline`):**
- **Brake Weight-Transfer Boost** — Scales Mz proportionally when braking, simulating increased front tyre grip from weight transfer
- **DC Blocker** — Removes turn-correlated bias from suspension EMAs that would push the wheel off-center during cornering
- **Dynamic Suppression** — Prevents the detail channel from reversing the core centering direction at small steer angles
- **V-Shape Centre Suppression** — Quadratic force fade near steering centre for smooth, natural self-centering

**Assetto Corsa (`AcFfbPipeline`):**
- **AC-Specific Mixer Scales** — Overrides channel mixer gains (Mz, Fx, Fy) tuned for AC's synthesized force data
- **Gear Shift Filter** — Mutes force spikes during gear changes
- **Brake Boost** — Configurable weight-transfer boost for braking zones
- **Simplified Pipeline** — Skips detail path (dynamic effects, tyre flex, EQ) for AC's lower-fidelity telemetry

### 10-Band FFB Equalizer

A full parametric EQ running at the physics sample rate (~333 Hz) with biquad filters:

| Band | Name | Center Freq | Purpose |
|------|------|-------------|---------|
| 0 | Steer Weight | 2 Hz | Heavy resistance, parking effort |
| 1 | Weight Transfer | 4 Hz | Body roll, brake dive, accel squat |
| 2 | Self Align | 8 Hz | Self-centering, grip balance feel |
| 3 | Steering Precision | 12 Hz | Line tracking, small corrections |
| 4 | Turn-In Response | 18 Hz | Initial bite, direction change |
| 5 | Road Texture | 25 Hz | Tarmac grain, surface roughness |
| 6 | Bump Detail | 35 Hz | Patches, seams, road features |
| 7 | Kerb Strike | 50 Hz | Kerb hits, potholes, sharp impacts |
| 8 | Wheel Chatter | 70 Hz | Rapid vibration, wheel buzz |
| 9 | ABS/TC Pulses | 100 Hz | ABS/TC pulsing, finest vibration |

Each band supports -12 dB to +12 dB gain with dynamic heat-map colors and EQ curve visualization overlay.

### Live Auto-Tuner

Real-time automatic FFB parameter correction while driving:
- Monitors average force, peak force, snap events, clipping rate, center oscillation, and steering jitter
- Tick-level corrections (40-tick windows) for clipping, high force, snap/jitter, and center oscillation
- Lap-level corrections for sustained oscillations, straight-line snaps, and lap clipping
- Cooldown system with correction logging

### Hardware Abstraction Layer — Vendor SDK Integration

The app uses an `IFFBProvider` abstraction layer that allows vendor-specific SDKs to sit between the DSP pipeline and the wheel, enabling features beyond what DirectInput alone can deliver:

| Vendor | Provider | Status | SDK Features |
|--------|----------|--------|--------------|
| **Fanatec** | `FanatecProvider` | **Active** | FullForce 500Hz haptic samples, rim rumble motors, rev LED RGB control, gear digit display, torque safety capping via `FSWheelMaxTorqueGet`, Maurice (FWPnpService) detection |
| **Moza** | DirectInput + Serial LED | Active | Native LED protocol via serial/HID |
| **Simucube** | `SimucubeProvider` | Stub | Simucube Link API — awaiting SDK access from Granite Devices |
| **Logitech** | `LogitechTrueForceProvider` | Stub | TrueForce audio-haptic SDK — awaiting SDK access from Logitech |
| **Asetek** | `AsetekProvider` | Stub | RaceHub TIC Mode — awaiting SDK access from Asetek |
| **VNM** | `VnmProvider` | Stub | Telemetry API — awaiting SDK access from VNM |
| **Others** | `GenericDirectInputProvider` | Active | Standard DirectInput constant force + periodic vibration |

All providers inherit from `IFFBProvider` and are auto-selected by `WheelbaseFactory` based on USB vendor ID and product name detection. The Fanatec provider loads `EndorFanatecSdk64_VS2019.dll` directly from the Fanatec driver installation directory — no DLLs bundled.

**Status bar** shows active provider capabilities as live feature pills (e.g., *FullForce Active*, *Rim Rumble*, *Gear Display*, *Torque Capped 8Nm*, *Maurice*). A **Haptic Test** button sends a 500ms 50Hz sine wave through both torque and vibration paths to verify the SDK link without needing to be in-car.

### HF8 Haptic Pad Integration

Direct support for the **ButtKicker HF8** haptic seat pad via the ForceFeel SDK:

| Feature | Details |
|---------|---------|
| **8 Motor Zones** | Seat Front L/R, Seat Rear L/R, Back Lower L/R, Back Upper L/R |
| **5 Telemetry Sources** | Suspension, Tire Slip, Kerb, Lateral G, Engine RPM |
| **Per-Zone Source Weights** | Independent weight sliders for each source on each motor zone |
| **Per-Zone Gain & Enable** | Individual intensity and on/off control per zone |
| **Master Gain** | Global volume control for all zones |
| **Output Rate** | Configurable 15–120 Hz (75 Hz for HF8 Pro, 15 Hz for original HF8) |
| **Motor Test** | Popup to test individual motors directly |

Source weights are mapped intelligently: seat zones emphasize suspension and slip feedback, back zones emphasize engine RPM and lateral G for maximum immersion.

### Auto Setup & Wheel Detection

- **Auto Setup** generates a complete baseline profile tuned for the detected wheel type and torque
- Detects wheel type: Direct Drive, Belt-Driven, Gear-Driven
- Auto-detects wheelbase torque from device name (Moza R5/R9/R12/R16/R21, Fanatec CSL DD/DD1/DD2, Simagic Alpha/Mini, Simucube, Logitech, Thrustmaster, and more)
- Auto-detects FFB strength and steering lock from AC EVO shared memory

### Wheel LED Controller

RPM shift lights, ABS flash, and race flag indicators with multi-vendor support:

| Vendor | LEDs | RGB | Brightness | Flags | Protocol |
|--------|------|-----|------------|-------|----------|
| **Moza** | 10 | Yes | Yes | Yes | Serial / HID / Native SDK |
| **Fanatec** | 9 | Yes (SDK) | Yes (SDK) | Yes | HID output report / EndorFanatecSdk64 |
| **Logitech** | 5 | No | No | No | HID output report |
| **Simucube** | 10 | Yes (RGBA) | No | Yes | HID output/feature report |

Configurable brightness, flash rate, color schemes (Traffic Light, Blue Gradient, Red Hot, Monochrome, Custom), and per-LED RPM thresholds.

### Track Mapping

- Records track layout from car position data during a lap
- Auto-detects corners and sectors with labels
- Real-time car position and heading indicator on 2D track map
- **Satellite Map View** — Mapsui-based ESRI tile overlay with auto-alignment, calibration mode (drag to shift, scroll to rotate), and zoom-to-cursor
- Hardcoded GPS coordinates for 25+ known tracks (Nurburgring, Spa, Monza, Suzuka, etc.)
- Force heatmap and diagnostic heatmap overlay (snap events, oscillations, clipping, anomalies)
- Popout overlay window for second-screen use during track creation
- Generates actionable tuning recommendations with one-click apply

### Telemetry Profiler

- Real-time scrolling graph of all FFB pipeline stages
- Snapshot capture (manual or via wheel button) with CSV export
- Animated HTML replay visualizer — self-contained HTML file with steering wheel animation, gauges, force charts, and playback controls

### Session Recording

- Records gameplay via FFmpeg (D3D11 hardware capture with GDI fallback)
- Auto-starts when speed > 5 km/h, auto-stops when stationary
- Encoder auto-detection (h264_mf > NVENC > AMF > QSV > libx264)
- System audio capture via WASAPI loopback
- Cloud upload for sharing recordings with the developer

### Profile System

- JSON-based profiles for all FFB, EQ, LFE, tyre flex, vibration, LED, and advanced settings
- Save, rename, delete, export, and import profiles
- **Sidebar browser** with track/car grouping for quick profile selection
- Optional auto-upgrade of profiles when the app updates
- Baseline profiles included for popular wheelbases (7+ pre-configured)

### Testing Guide

- Built-in step-by-step testing walkthrough for FFB evaluation
- Pop-out overlay window for single-screen users
- Structured feedback dialog for capturing tester notes

### Diagnostics

- One-click diagnostic pack submission (profiles, track maps, snapshots with HTML replays, recording manifest, and logs)
- In-app email integration with automatic video upload from latest recording

### Additional Features

- **Multi-Game Selection** — Switch between AC EVO, RaceRoom, and Assetto Corsa from a dropdown; pipeline and reader auto-switch
- **What's New Dialog** — Versioned changelog shown on startup after updates
- **1 kHz Output Interpolation** — Dedicated thread interpolating between 333 Hz physics frames for smooth force transitions
- **Auto-Update** — Downloads and installs new releases automatically
- **Splash Screen** — Animated rotating wheel with engine startup sound
- **Game FFB Detection** — Warns if in-game FFB is not set to 0
- **Panic Stop** — Immediately zeroes all FFB output and disconnects from the device
- **Custom Slider Controls** — Editable values, logarithmic scale, undo, reset, context menu, section color theming
- **Collapsible Sidebar** — Icon-only mode for more screen real estate, wider 200px default layout

## Tech Stack

| Layer | Technology |
|---|---|
| Framework | .NET 8.0 (`net8.0-windows`) |
| UI | WPF + Material Design Themes |
| Plotting | ScottPlot.WPF 5.0 |
| MVVM | CommunityToolkit.Mvvm |
| Audio | NAudio |
| FFB Device | SharpDX.DirectInput |
| Telemetry | Windows Memory-Mapped Files |
| Satellite Maps | Mapsui.Wpf (ESRI tile overlay) |
| Video Capture | FFmpeg (D3D11 ddagrab / gdigrab) |
| Moza SDK | Native DLL interop |
| Fanatec SDK | EndorFanatecSdk64 P/Invoke (120+ exports) |
| HF8 Haptics | ForceFeel SDK (reflection-based) |
| Installer | Inno Setup |
| CI/CD | GitHub Actions |
| Testing | xUnit |

## Project Structure

```
src/AcEvoFfbTuner.Core/            Core library (no UI dependencies)
  DirectInput/                      FFB device management, 1 kHz interpolation, multi-vendor LED control
  FfbProcessing/                    FFB DSP pipeline (base + R3E/AC subclasses), EQ, LFE, tyre flex, live auto-tuner
  FfbProviders/                     Hardware Abstraction Layer — vendor SDK providers (Fanatec, Simucube, Logitech, Asetek, VNM) + GenericDirectInput + WheelbaseFactory
  Profiles/                         Profile model, CRUD, auto-detection, wheelbase auto-configurator
  SharedMemory/                     Shared memory readers (AC EVO, RaceRoom, Assetto Corsa) + struct definitions
  TrackMapping/                     Track map, heatmap, diagnostics, recommendations, satellite tile service
src/AcEvoFfbTuner/                  WPF application (UI layer)
  Controls/                         Custom LabeledSlider, MapsuiMapControl (satellite view)
  Services/                         Auto-update, changelog, session recording, replay visualizer, diagnostics
  ViewModels/                       MVVM view models
  Views/                            Windows, tabs, overlays (10+ tabs, splash, what's new, popouts, HF8 motor test)
src/AcEvoFfbTuner.Tests/            Unit tests
tools/                              Utility projects (MmfChecker, MozaLedTest)
installer/                          Inno Setup installer script
lib/moza/                           Native Moza SDK DLLs
```

## Building

**Prerequisites:** .NET 8.0 SDK, Windows OS

```
dotnet build AcEvoFfbTuner.slnx -c Release
```

Run tests:

```
dotnet test src/AcEvoFfbTuner.Tests
```

## Data Flow

```
Game Shared Memory (selected via dropdown)
  → SharedMemoryReader (game-specific: AC EVO ~333Hz / R3E / AC)
    → FfbPipeline.Process() (game-specific subclass):
       EVO:  FfbPipeline (base class)
       R3E:  R3eFfbPipeline (brake boost, DC blocker, V-shape centre suppression)
       AC:   AcFfbPipeline (AC mixer scales, simplified detail path)

       Core Path (Zero-Latency):
       1. Auto-Gain (car FFB multiplier compensation)
       2. Channel Mixer (Mz/Fx/Fy blend, front + rear, wheel-load weighting)
       3. Normalize + compress
       4. LUT response curve
       5. Damping & friction (Coulomb model, angular acceleration inertia)
       6. Centre fade + low-speed fade
       7. GripGuard / CrashDetector / TyreCondition / WetWeather

       Detail Path (Filtered):
       8. Slip enhancement
       9. Dynamic effects (suspension, G-force, yaw rate)
      10. Tyre flex (carcass deformation + contact patch dynamics)
      11. Vibration Mixer (curb/slip/road/ABS + tire scrub + rear slip warning)
      12. LFE/Rumble (suspension + RPM driven)
      13. 10-Band Equalizer (parametric biquad filters)
      14. Slew rate limiter (speed-scaled)

       Final Mix:
      15. Gear shift filter + output clipper + noise floor gate

    → IFFBProvider (Hardware Abstraction Layer):
        WheelbaseFactory auto-detects vendor → selects provider:
        ├─ FanatecProvider → EndorFanatecSdk64 (FullForce, LEDs, rumble, torque cap)
        ├─ GenericDirectInputProvider → SharpDX DirectInput
        └─ Stub providers (Simucube, Logitech, Asetek, VNM) → DirectInput fallback
    → FfbDeviceManager
      → 1kHz interpolation thread (time-based sliding lerp)
        → DirectInput ConstantForce → Wheel
        → DirectInput Periodic vibration → Wheel (if supported)
      → WheelLedController → LED shift lights / ABS flash / flags
    → Hf8SignalMapper → HF8 Haptic Pad (8-zone seat vibration)
```

## License

All rights reserved.
