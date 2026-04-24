# AC Evo FFB Tuner

A Windows desktop application that intercepts, processes, and enhances Force Feedback signals from **Assetto Corsa EVO** before sending them to your DirectInput-compatible steering wheel.

Instead of relying on the game's native FFB output, this tool reads raw physics telemetry from AC EVO's shared memory — tire forces, slip data, suspension travel, G-forces, vibrations — runs it through a fully configurable multi-stage DSP pipeline, and sends the processed result directly to the wheel.

## Features

### Multi-Stage FFB Processing Pipeline
- **Channel Mixer** — Blend Mz (self-aligning torque), Fx (longitudinal), and Fy (lateral) tire forces with independent gain, normalization, and wheel-load weighting
- **Compression** — `tanh`-based dynamic range compression with configurable power curve
- **LUT Response Curve** — Preset force response curves (Linear, Soft Center, Progressive, Dead Zone)
- **Slip Enhancement** — Amplify FFB during wheelspin and oversteer with configurable thresholds
- **Damping & Friction** — Speed-dependent damping, constant friction, inertia weighting, low-speed damping boost
- **Dynamic Effects** — Cornering force, acceleration/braking force, road feel, car rotation (yaw rate)
- **Center Suppression** — Reduces artificial center forces with speed-aware scaling
- **Hysteresis** — Prevents force jitter with watchdog-timer-based output hold
- **Slew Rate Limiter** — Caps maximum force change per tick; reduced during gear shifts and near-center at high speed
- **Soft Clipping** — Output clipping with configurable threshold
- **Vibration Mixer** — Curb, slip, road surface, and ABS vibration channels with individual gains
- **Auto-Gain** — Automatically compensates for car-specific FFB multipliers
- **Sign Correction** — Corrects self-aligning torque direction so forces pull toward center

### Wheel LED Controller
- RPM shift lights, ABS flash, race flag indicators
- Supports **Moza** (native SDK) and **Fanatec** wheels with per-LED RGB control
- Configurable brightness, flash rate, color schemes, and per-LED RPM thresholds

### Telemetry Profiler
- Real-time scrolling graph of all FFB pipeline stages
- Snapshot capture (manual or via wheel button) with CSV export

### Track Mapping
- Records track layout from car position data during a lap
- Auto-detects corners and sectors
- Real-time car position tracking on the 2D track map
- Force heatmap and diagnostic heatmap overlay
- Detects snap events, oscillations, clipping, and force anomalies
- Generates actionable tuning recommendations with one-click apply

### Profile System
- JSON-based profiles for all FFB, damping, vibration, LED, and advanced settings
- Save, rename, delete, export, and import profiles
- Auto-detects wheelbase torque from device name (Moza R5/R9/R12/R16/R21, Fanatec CSL DD/DD1/DD2, Simagic Alpha, and more)
- Auto-detects FFB strength and steering lock from AC EVO shared memory

## Tech Stack

| Layer | Technology |
|---|---|
| Framework | .NET 8.0 (`net8.0-windows`) |
| UI | WPF + Material Design Themes |
| Plotting | ScottPlot.WPF 5.0 |
| MVVM | CommunityToolkit.Mvvm |
| FFB Device | SharpDX.DirectInput |
| Telemetry | Windows Memory-Mapped Files |
| Moza SDK | Native DLL interop |
| Testing | xUnit |

## Project Structure

```
src/AcEvoFfbTuner.Core/          Core library (no UI dependencies)
  DirectInput/                    FFB device management & LED control
  FfbProcessing/                  Multi-stage FFB DSP pipeline
  Profiles/                       Profile model, CRUD, auto-detection
  SharedMemory/                   AC EVO telemetry reader & struct definitions
  TrackMapping/                   Track map, heatmap, diagnostics, recommendations
src/AcEvoFfbTuner/                WPF application (UI layer)
src/AcEvoFfbTuner.Tests/          Unit tests
tools/                            Utility projects (MmfChecker, MozaLedTest)
lib/moza/                         Native Moza SDK DLLs
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
AC EVO Shared Memory
  → SharedMemoryReader (physics @ ~333Hz)
    → FfbPipeline.Process():
      1. Channel Mixer (Mz/Fx/Fy blend)
      2. Normalize + compress
      3. LUT response curve
      4. Slip enhancement
      5. Damping & friction
      6. Dynamic effects (G-force, suspension, yaw)
      7. Soft clipping + gain
      8. Center suppression + sign correction
      9. Noise floor + hysteresis
      10. Slew rate limiter
      11. Vibration mixer
    → FfbDeviceManager → DirectInput → Wheel
```

## License

All rights reserved.
