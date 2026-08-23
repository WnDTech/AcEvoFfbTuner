# ACEVO FFB Tuner — User Logs Analysis (2026-08-10)

## Summary
The Wheelbase tool logs capture detailed telemetry from the FFB pipeline during real driving sessions. Key data sources are in `C:\Users\paul_\OneDrive\Documents\APP\ACEVO - Telemetry FFB\User Logs\ACEVOFFBTUNER\`.

---

## 1. Device Detection & Connection (`wheelbase_factory.log`, `connection_debug.log`)

### Detected Devices
- **Logitech G HUB RS50 (USB)** — Vendor: Logitech, GUID: `e1a6ede0-d5d1-11f0-8003-444553540000`
- **Thrustmaster Pedals** — Vendor: Thrustmaster, GUID: `e9032b60-e1b3-11f0-8001-444553540000`

### DirectInput Connection (Working)
- Exclusive | Background cooperative level acquired successfully
- Window handle: `0x001105E8`
- Primary/Secondary acquisition state tracked

### Supported Force Feedback Effects (11 GUIDs)
```
13541c20-8e33-11d0-9ad0-00a0c9a06e35  ConstantForce
13541c21-8e33-11d0-9ad0-00a0c9a06e35  RampForce
13541c22-8e33-11d0-9ad0-00a0c9a06e35  Square
13541c23-8e33-11d0-9ad0-00a0c9a06e35  Sine
13541c24-8e33-11d0-9ad0-00a0c9a06e35  Triangle
13541c25-8e33-11d0-9ad0-00a0c9a06e35  SawtoothUp
13541c26-8e33-11d0-9ad0-00a0c9a06e35  SawtoothDown
13541c27-8e33-11d0-9ad0-00a0c9a06e35  Spring
13541c28-8e33-11d0-9ad0-00a0c9a06e35  Damper
13541c29-8e33-11d0-9ad0-00a0c9a06e35  Inertia
13541c2a-8e33-11d0-9ad0-00a0c9a06e35  Friction
13541c2b-8e33-11d0-9ad0-00a0c9a06e35  CustomForce
```

### FFB Axis Configuration
- **Axis**: `16777218` (DIJOFS_X — X axis)
- **Periodic**: True
- **AutoDetect**: Runs dynamic axis test, but delta=0 < threshold(20) → falls back to static DB → `invert=False`
- LED controller connects successfully

### Failed Components
- **HF8 Haptic Pad**: `ForceFeel.dll` not found at `C:\Users\Pc\AppData\Local\HFS\ForceFeel.dll`
- **USB Scan**: Fails with "Die Anfrage ist ungültig" (The request is invalid)

---

## 2. FFB Telemetry Data (`ffb_debug.log`) — HIGH VALUE

### CSV Columns (per-frame, ~10ms intervals)
```
Timestamp,SpeedKmh,SteerAngle,Gear,
Mz_FL,Mz_FR,Fx_FL,Fx_FR,Fy_FL,Fy_FR,       // Wheel torques/forces (FL,FR,RL,RR)
ChMzFront,ChFxFront,ChFyFront,             // Channel mixed front axle
PostCompress,PostLUT,PostDamping,PostGainOut,PostDynamic,Output,  // Pipeline stages
Clipping,                                   // Output clipping flag
WL_FL,WL_FR,                                // Wheel loads (front left/right)
LatencyMs,                                  // Pipeline latency
KerbVib,SlipVib,RoadVib,AbsVib,             // Vibration channels
VibForce,AbsGain                            // Vibration mix
```

### Sample Data (first frame @ 18:56:33.322)
```
Speed: 3 km/h, Steer: -0.0101, Gear: 1
Mz: FL=0.0000, FR=0.0000 | Fx: FL=0.0000, FR=0.0000 | Fy: FL=0.0000, FR=0.0000
ChMzFront=0.0000, ChFxFront=0.0000, ChFyFront=0.0000
Pipeline: Compress=0.0000, LUT=0.0000, Damping=0.0000, GainOut=0.0000, Dynamic=0.0000, Output=0.0000
Clipping=False, WL_FL=0.032956, WL_FR=0.032956, Latency=0
Vib: Kerb=0, Slip=0, Road=0, Abs=0, VibForce=0.0000, AbsGain=1.00
```

### Later Frame (peak forces @ 18:56:33.410)
```
Speed: 4 km/h, Steer: 0.0000
Mz_FR=3.7338, Fx_FL=-13.2700, Fx_FR=4499.67, Fy_FL=2029.58, Fy_FR=-2258.05
ChMzFront=0.047509, ChFxFront=0.000249, ChFyFront=-0.371775
Pipeline: Compress=-0.371775, LUT=-0.371775, Damping=-0.143741, GainOut=-0.001696, Dynamic=-0.145438, Output=-0.145438
Clipping=False, WL_FR=7912.3, WL_FR=6082.0, Latency=3
Vib: Kerb=0, Slip=0, Road=0, Abs=0.0314, VibForce=0.0000, AbsGain=1.00
```

---

## 3. Safety Watchdog (`device_timeout.log`) — CRITICAL

### Safety Mechanism
- **Trigger**: No FFB packet received for **500ms**
- **Action**: Ramp force output to zero
- **Frequency**: Triggers repeatedly during device reconnects (every ~100ms)
- **Threshold**: 500ms since last packet → ramp to zero

### Implications
- The 500ms timeout is very aggressive for a FFB device
- During normal driving, FFB updates should be ~100Hz (10ms)
- 500ms timeout = 5 missed frames max
- Currently triggers on every reconnect (device enumeration gaps)

---

## 4. Failed/Unavailable Systems

| System | Status | Reason |
|--------|--------|--------|
| HF8 Haptic Pad | ❌ Failed | `ForceFeel.dll` missing from `C:\Users\Pc\AppData\Local\HFS\` |
| USB Scan | ❌ Failed | "Die Anfrage ist ungültig" (invalid request) |
| AutoDetect Axis | ⚠️ Fallback | Delta=0 < threshold(20) → uses static DB, `invert=False` |

---

## 5. Stored Reference Data

### DirectInput Effect GUIDs (for FFB implementation)
Store in: `docs/references/directinput_effect_guids.md`

### RS50 FFB Axis
- **Axis**: `16777218` (DIJOFS_X)
- **Periodic**: True
- **Invert**: False (static DB fallback)

### Safety Watchdog Config
- **Timeout**: 500ms (too aggressive for production)
- **Action**: Ramp force to zero
- **Recommendation**: Increase to 2000ms or make configurable

### Missing Dependencies
- **ForceFeel.dll**: Required for HF8 haptic pad — install HFS software
- **USB Scan**: Fix "invalid request" for proper device enumeration

---

## Action Items

1. **Increase safety timeout** from 500ms → 2000ms (or make configurable per device)
2. **Install HFS software** for ForceFeel.dll → enables HF8 haptic pad
3. **Fix USB scan** → proper device enumeration without "invalid request"
4. **Use ffb_debug.log CSV** as ground truth for FFB pipeline tuning
5. **AutoDetect threshold** (20) may be too high for RS50 — consider device-specific tuning