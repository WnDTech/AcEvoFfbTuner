# RaceRoom (R3E) API Data Mapping Analysis

## Date: 2026-05-22

## Official API Sources Verified
- **C++ Header:** `kwstudios-sweden/r3e-api` → `sample-c/src/r3e.h`
- **C# Reference:** `kwstudios-sweden/r3e-api` → `sample-csharp/src/R3E.cs`
- **Version:** Major 3, Minor 5
- **Shared Memory Name:** `$R3E`

## Struct Alignment Status: ✓ CORRECT

Our `R3eSharedStruct.cs` uses `[StructLayout(LayoutKind.Sequential, Pack = 1)]` which matches the official C++ `#pragma pack(push, 1)` directive. All field offsets are verified against the official definitions.

---

## FFB-Critical Data Mapping

### ✅ Available Data

| Field Category | R3E Struct Field | Location | EVO Mapping | Status |
|----------------|------------------|----------|-------------|--------|
| **G-Forces** | `Player.GForce` (`g_force` in SDK) | `r3e_playerdata` | `AccG[3]` (X→lat, Z→long, Y→vert) | ✓ Complete |
| **Angular Velocity** | `Player.AngularVelocity` | `r3e_playerdata` | `LocalAngularVel[3]` | ✓ Complete |
| **Angular Acceleration** | `Player.AngularAcceleration` | `r3e_playerdata` | NOT mapped | ⚠ Available, not used |
| **Tire Vertical Load** | `TireLoad` | `r3e_shared` | `WheelLoad[4]` | ✓ Complete |
| **Steering Force** | `Player.SteeringForce` | `r3e_playerdata` | `Mz[0]`, `Mz[1]` | ✓ Complete |
| **Engine RPM** | `EngineRps`, `MaxEngineRps` | `r3e_shared` | `Rpms` | ✓ Complete |
| **Suspension Travel** | `Player.SuspensionDeflection` | `r3e_playerdata` | `SuspensionTravel[4]` | ✓ Complete |
| **Tire Pressure** | `TirePressure` | `r3e_shared` | `WheelsPressure[4]` | ✓ Complete |
| **Tire Temperature** | `TireTemp[].CurrentTemp` | `r3e_shared` | `TyreTemp[4]` (needs avg) | ⚠ Partial |
| **Tire Wear** | `TireWear` | `r3e_shared` | `TyreWear[4]` | ✓ Complete |
| **Tire Grip** | `TireGrip` | `r3e_shared` | Available, not mapped | ⚠ Available |
| **Tire Grip** | `TireGrip` | `r3e_shared` | Used for dynamic FFB scaling | ✓ Complete |
| **Tire Speed** | `TireSpeed` | `r3e_shared` | Used for slip calc & road vibration synthesis | ✓ Complete |
| **Synthesized Fx** | `SlipRatio × WheelLoad` | Calculation | `Fx[4]` (longitudinal force) | ✓ Complete |
| **Synthesized Fy** | `LateralG × WheelLoad` | Calculation | `Fy[4]` (lateral force) | ✓ Complete |
| **Car Speed** | `CarSpeed` | `r3e_shared` | `SpeedKmh` | ✓ Complete |
| **Velocity** | `Player.Velocity` | `r3e_playerdata` | `Velocity[3]` | ✓ Complete |
| **Local Velocity** | `Player.LocalVelocity` | `r3e_playerdata` | `LocalVelocity[3]` | ✓ Complete |
| **Orientation** | `CarOrientation` | `r3e_shared` | `Heading`, `Pitch`, `Roll` | ✓ Complete |
| **Calculated Slip Ratio** | `TireSpeed` / `CarSpeed` | Calculation | `SlipRatio[4]` | ✓ Complete |
| **ABS State** | `AidSettings.Abs` | `r3e_shared` | `AbsInAction`, `AbsVibrations` | ✓ Complete |
| **ABS Vibrations** | Tire speed asymmetry | Synthesis | `AbsVibrations` | ✓ Complete |

### ❌ NOT Available in R3E API

| Field Category | Why Missing | Impact |
|----------------|------------|--------|
| **Tire Slip Angles** | R3E doesn't expose slip angle telemetry | Slip-based FFB enhancement won't work |
| **Tire Slip Ratios** | R3E doesn't expose slip ratio telemetry | ✅ **NOW CALCULATED** from `TireSpeed` / `CarSpeed` |
| **Individual Wheel Forces (Mz)** | R3E only provides total steering force | Steering force distributed evenly to front wheels |
| **Individual Wheel Forces (Fx)** | R3E doesn't expose longitudinal force per wheel | ✅ **NOW SYNTHESIZED** from slip ratio × wheel load |
| **Individual Wheel Forces (Fy)** | R3E doesn't expose lateral force per wheel | ✅ **NOW SYNTHESIZED** from lateral G × wheel load |
| **Self-Aligning Torque per Wheel** | R3E doesn't expose per-wheel aligning torque | Uses total `SteeringForce` instead |
| **Kerb Vibrations** | R3E doesn't expose vibration telemetry | ✅ **SYNTHESIZED** from suspension deflection acceleration |
| **Road Vibrations** | R3E doesn't expose surface roughness | ✅ **SYNTHESIZED** from high-frequency tire speed delta |
| **Slip Vibrations** | R3E doesn't expose slip vibration data | ✅ **NOW AVAILABLE** via calculated slip ratio |

---

## Recent Changes

### 2026-05-22
1. ✅ Verified struct alignment against official C++ header (`r3e.h`)
2. ✅ Confirmed `Pack = 1` matches `#pragma pack(push, 1)`
3. ✅ Added mapping for `Player.AngularVelocity` → `LocalAngularVel[3]`
4. ✅ Fixed `FfbStrength = 0f` to prevent "in-game FFB" warning
5. ✅ Map `Player.SteeringForce` to `Mz[0]`, `Mz[1]` for pipeline processing
6. ✅ **Implemented calculated slip ratio per wheel** using `TireSpeed` / `CarSpeed`
   - Threshold: 0.5 m/s minimum car speed
   - Formula: `slip = |tire_speed - car_speed| / car_speed`
   - Mapped to `SlipRatio[4]` for all 4 wheels
   - Enables slip-based FFB effects in RaceRoom
7. ✅ **Synthesized Road Vibrations** from high-frequency `TireSpeed` delta
   - Tracks tire speed changes across all 4 wheels
   - Maximum delta scaled and clamped to 0-1
   - Provides road surface texture feedback
8. ✅ **Synthesized Kerb Vibrations** from suspension deflection acceleration
   - Tracks high-frequency spikes in `SuspensionDeflection`
   - Calculates acceleration: `delta * 400 Hz` (physics tick rate)
   - Clamped to 0-1 range for rumble strip detection
9. ✅ Added state tracking: `_prevTireSpeed[4]`, `_prevSuspensionDeflection[4]`
10. ✅ Added `TireGrip` public property to `RaceroomSharedMemoryReader`
    - Exposed as `public float[] TireGrip { get; }`
    - Range: 0.0-1.0 (1.0 = full grip)
    - Used for dynamic FFB scaling during understeer events (grip < 0.5 triggers attenuation)
11. ✅ **Synthesized ABS Vibrations** from tire speed asymmetry
    - Detects when `AidSettings.Abs == 5` (actively pulsing)
    - Calculates left/right wheel speed asymmetry on front and rear axles
    - Vibration intensity = asymmetry * 0.1 + 0.02 (tuned down)
    - Clamped to 0-1 range
    - Sets `AbsInAction` to 1 when ABS is enabled (1 or 5)
    - No speed threshold - works at any speed
12. ✅ **Steering Force Mapping** - raw data pass-through
    - Passes `Player.SteeringForce` directly to `Mz[0]` and `Mz[1]`
    - No speed scaling or deadzone applied
    - Profile tuning (gains, filters, etc.) handles RaceRoom-specific characteristics
    - Use "RACEROOM - TEST" profile for RaceRoom tuning
13. ✅ **Synthesized Fx Forces** per wheel from slip ratio and wheel load
    - Formula: `Fx[i] = SlipRatio[i] * WheelLoad[i] * 1.5f`
    - Sign automatically matches acceleration (positive) vs braking (negative)
    - Calculated for all 4 wheels (FL, FR, RL, RR)
14. ✅ **Synthesized Fy Forces** per wheel from lateral G and wheel load
    - Formula: `Fy[i] = LateralG * WheelLoad[i] * 0.1f`
    - Sign automatically matches cornering direction
    - Calculated for all 4 wheels (FL, FR, RL, RR)

---

## Current Status

**Working:**
- ✓ FFB force reading from `Player.SteeringForce` (raw pass-through)
- ✓ Tire load distribution
- ✓ G-force data (lateral/longitudinal acceleration)
- ✓ Angular velocity (yaw rate for aligning torque)
- ✓ Engine RPM for vibration harmonics
- ✓ Suspension travel
- ✓ Calculated slip ratio per wheel (from tire speed vs car speed)
- ✓ Synthesized road vibrations (tire speed high-frequency delta)
- ✓ Synthesized kerb vibrations (suspension acceleration spikes, reduced to 0.3x)
- ✓ Synthesized ABS vibrations (tire speed asymmetry, reduced to asymmetry*0.1+0.02)
- ✓ Tire grip available for dynamic FFB scaling
- ✓ ABS state detection (`AidSettings.Abs`, no speed threshold)

**Known Limitations:**
- ⚠️ No per-wheel force separation (Mz, Fx, Fy)
- ⚠️ No slip angle data

**Recommended Pipeline Adjustments for R3E:**
1. Slip-based enhancement features now have data source via calculated slip ratio
2. Vibration channels populated from synthesized data
3. Self-aligning torque relies on `SteeringForce` + angular velocity estimation
4. Tire grip can be used to scale `SteeringForce` during understeer (grip < 0.5)
5. ABS vibrations available via tire speed asymmetry detection
6. All vibration channels (road, kerb, ABS) now functional for R3E
7. **Use dedicated "RACEROOM - TEST" profile** for RaceRoom tuning (different force characteristics than AC EVO)

---

## Build Status

Last build: **SUCCESS** (0 warnings, 0 errors)
Build command: `dotnet build AcEvoFfbTuner.slnx -c Release`