# ACEVO FFB Tuner — Today's Wheelbase Logs Analysis (2026-08-22)

## Source
Extracted from: `C:\Users\paul_\OneDrive\Documents\APP\ACEVO - Telemetry FFB\User Logs\WheelbaseData_20260822_114214.zip`
Date: **2026-08-22 11:42:14**

---

## 1. REAL RS50 HID++ DEVICE STRUCTURE (from `logitech_hidpp.log`)

### USB Device Tree (VID 046D, PID C276)
The real RS50 presents as a **USB Composite Device** with 3 interfaces:

| Interface | MI | Endpoint | Usage Page | Usage | Report Size (in/out) | Description |
|-----------|-----|----------|------------|-------|---------------------|-------------|
| 0 | MI_00 | - | 0x0001 | 0x0004 | 31/0 | Joystick (HID) |
| 1 | MI_01 | Col01 | 0xFF43 | 0x0701 | 7/7 | HID++ Short (Report ID 0x10) |
| 1 | MI_01 | Col02 | 0xFF43 | 0x0702 | 20/20 | HID++ Long (Report ID 0x11) |
| 1 | MI_01 | Col03 | 0xFF43 | 0x0704 | 64/64 | HID++ Very Long (Report ID 0x12) |
| 2 | MI_02 | - | 0xFFFD | 0xFD01 | 64/64 | TrueForce Stream |

### Key Device Identifiers
- **VID**: 0x046D (Logitech)
- **PID**: 0xC276 (RS50/G PRO)
- **GUID**: `e1a6ede0-d5d1-11f0-8003-444553540000`
- **Product Name**: "Logitech G HUB RS50 (USB)"

### HID++ Feature Indices (Discovered at Runtime)
| Feature | Index | Type | Version | Description |
|---------|-------|------|---------|-------------|
| Root (0x0000) | 0x00 | 0x00 | 0x00 | Root feature |
| Device Info (0x0001) | 0x01 | 0x00 | 0x00 | Device information |
| FFB Strength (0x8136) | 0x16 | 0x00 | 0x00 | **FFBStrength** |
| Rotation (0x8138) | 0x18 | 0x00 | 0x00 | **Rotation** |
| Profile (0x8137) | 0x17 | 0x00 | 0x00 | **Profile** |
| TrueForce (0x8139) | 0x19 | 0x00 | 0x00 | **TrueForce** |
| Damping (0x8133) | 0x14 | 0x00 | 0x00 | **Damping** |
| OLED (0x8130) | 0x12 | 0x00 | 0x00 | **OLED** |

### Default Wheel Settings (from device)
| Setting | Value | Encoding |
|---------|-------|----------|
| Strength | 8.0 Nm | 0xFFFF (LE) |
| Rotation | 1080° | 0x0438 (BE) |
| Profile Mode | Onboard Slot 5 | 0x05 |
| TrueForce | 0% | 0x0000 (LE) |
| Damping | 0% | 0x0000 (LE) |

---

## 2. HID++ COMMUNICATION PROTOCOL (from `logitech_hidpp.log`)

### Connection Sequence
1. **Enumerate** HID devices (VID 046D)
2. **Find write candidate** — page 0xFF43, outLen=7 (Report ID 0x10)
3. **Open read channel** — InputReportLoop (inLen=64, Report ID 0x12)
4. **Feature Discovery** — Query each feature via Root fn=0
5. **Read Settings** — Query each feature fn=1 (GET)
6. **OLED** — Open very-long OUT collection (outLen=64)

### HID++ Report Format
```
TX (SET_FEATURE): [ReportId][DevIdx][FeatureIdx][Fn][Params...]
RX (GET_INPUT_REPORT): [ReportId][DevIdx][FeatureIdx][Fn][Response...]
```

- **Report IDs**: 0x10 (short), 0x11 (long), 0x12 (very long)
- **Device Index**: 0xFF (broadcast)
- **Root Feature**: 0x0000 → query feature index via fn=0
- **Async Response**: Commands via SET_FEATURE, responses via GET_INPUT_REPORT

### Critical: Unsolicited Events
```
RX: unsolicited event (feat 0x19 fn=1) — no pending request
RX: unsolicited event (feat 0x22 fn=0) — no pending request
```
**The device sends async events on the interrupt IN endpoint. Must handle these.**

---

## 3. TRUEFORCE STREAM (from `logitech_trueforce.log`)

### Stream Interface
- **Interface**: MI_02 (page 0xFFFD, usage 0xFD01)
- **Endpoint**: 64 bytes IN/OUT
- **Stream Rate**: 250 Hz (4ms period)
- **Packet Format**: Type 0x0E for range

### Initialization Sequence
```
1. Open stream interface (MI_02, page 0xFFFD, inLen=64, outLen=64)
2. Pump thread starts (waits up to 2500ms for range read)
3. RunInitSequence: 2 passes × 68 packets, 2ms apart (range=1080°)
   - PatchRangePacket: type-0x0e range rewritten to 1080° (was 2700° in capture)
4. PumpLoop: stream active at 250 Hz — rotation=1080°, invert=True, scale=0.80, smooth=1.0ms
```

### Key Parameters
- **Rotation Range**: 1080° (patched from capture's 2700°)
- **Invert**: True
- **Scale**: 0.80
- **Smoothing**: 1.0ms
- **Session**: Starts at sequence 69
- **Teardown**: Neutral + 0x04/0x03 handshake

---

## 4. DIRECTINPUT CONNECTION MODE (from `connection_debug.log`)

### Input-Only Mode (TrueForce-Stream Wheel)
```
InputOnlyMode: True — TrueForce-stream wheel: force rides the HID stream
DI acquired input-only (non-exclusive, no effects)
Cooperative level: NonExclusive|Background
ACQUIRED (input-only, non-exclusive — game keeps its force path)
```

### Key Points
- **Non-exclusive** background acquisition
- **No DirectInput effects** — TrueForce stream provides force
- **Game keeps its force path** — we only read input
- **LED controller**: Not found (no usable device)
- **HF8**: ForceFeel.dll missing (`C:\Users\Pc\AppData\Local\HFS\ForceFeel.dll`)

---

## 5. WHEEL SETTINGS (from `settings.json`)

```json
{
  "logitechFfbStrengthNm": 8,
  "logitechRotationDegrees": 1080,
  "logitechProfileSlot": -1,  // -1 = use current/onboard
  "logitechDiForceMode": false,  // TrueForce stream mode
  "logitechEverConnected": true
}
```

---

## 6. FFB TELEMETRY (from `ffb_debug.log`)

### CSV Columns (per-frame ~10ms)
```
Timestamp,SpeedKmh,SteerAngle,Gear,
Mz_FL,Mz_FR,Fx_FL,Fx_FR,Fy_FL,Fy_FR,
ChMzFront,ChFxFront,ChFyFront,
PostCompress,PostLUT,PostDamping,PostGainOut,PostDynamic,Output,
Clipping,WL_FL,WL_FR,LatencyMs,
KerbVib,SlipVib,RoadVib,AbsVib,VibForce,AbsGain
```

### Sample Data (stationary)
```
Speed: 0 km/h, Steer: 0, Gear: 0
Mz: ~0, Fx: ~-0.01, Fy: ~-4
Pipeline: all ~0, Output: ~-0.0015
Wheel Loads: ~2691 (both sides)
Vibrations: all 0, AbsGain: 0
```

### Pipeline Stages
1. **PostCompress** → 2. **PostLUT** → 3. **PostDamping** → 4. **PostGainOut** → 5. **PostDynamic** → 6. **Output**

---

## 7. SYSTEM STATE (from `system_log.json`)

### Application Flow
```
1. Application initialized
2. Connecting Logitech HID++ settings interface
3. WARNING: G HUB not running — force feedback will NOT work
4. Device connected: Logitech G HUB RS50 (USB)
5. HID++ connected: RS50 Base for PC: strength=8.0 Nm, rotation=1080°, mode=onboard slot 5, trueforce=0%, damping=0%
6. HID++ write: strength/rotation (unchanged)
7. WHEEL FFB TEST: sending 35% via TrueForce stream — hold the wheel!
8. WHEEL FFB TEST: stopped, force zeroed
9. TrueForce stream paused — wheel falls back to its own FFB path
10. Game source: AC EVO (auto-detected)
11. Telemetry loop started
12. Game connected (AC EVO)
```

### Critical: **G HUB Must Be Running**
> "WARNING: Logitech G HUB not running — force feedback will NOT work. Start G HUB and reconnect."

---

## 8. WHEEL PROFILE (from `Default - Logitech RS50_G PRO.json`)

### Key Profile Settings
```json
{
  "outputGain": 0.5,
  "forceSensitivity": 1000,
  "forceScale": 1,
  "softClipThreshold": 0.8,
  "forceInvertEnabled": true,
  "wheelMaxTorqueNm": 8,
  "mzFront": { "gain": 0.42, "enabled": true },
  "fxFront": { "gain": 0.15, "enabled": true },
  "fyFront": { "gain": 0.2, "enabled": true },
  "fyInverted": true,
  "mzScale": 5,
  "fxScale": 4000,
  "fyScale": 5000,
  "steeringLockDegrees": 900,
  "damping": {
    "viscousDamping": 0.18,
    "speedDamping": 0.5,
    "friction": 0.15,
    "inertia": 0.1
  },
  "vibrations": {
    "masterGain": 0.42,
    "curbGain": 1,
    "slipGain": 0.8,
    "absGain": 1
  },
  "advanced": {
    "maxSlewRate": 0.85,
    "centerSuppressionDegrees": 1.5,
    "noiseFloor": 0.003,
    "maxSlewRate": 0.85
  }
}
```

---

## 9. USB DEVICE TREE (from `Logitech/devices.txt`)

### RS50 Device Paths
```
USB\VID_046D&PID_C276              → USB Composite Device (usbccgp)
USB\VID_046D&PID_C276&MI_00        → "Logitech G HUB RS50 (USB)" (HidUsb)
USB\VID_046D&PID_C276&MI_01        → USB Input Device (HidUsb)  ← HID++ interface
USB\VID_046D&PID_C276&MI_02        → USB Input Device (HidUsb)  ← TrueForce stream

HID\VID_046D&PID_C276&MI_00        → HID-compliant game controller
HID\VID_046D&PID_C276&MI_01&Col01  → HID-compliant vendor-defined (HID++)
HID\VID_046D&PID_C276&MI_01&Col02  → HID-compliant vendor-defined (HID++)
HID\VID_046D&PID_C276&MI_01&Col03  → HID-compliant vendor-defined (HID++)
HID\VID_046D&PID_C276&MI_02        → HID-compliant vendor-defined (TrueForce)
```

---

## 10. CRITICAL FINDINGS FOR VIRTUAL RS50 IMPLEMENTATION

### ✅ What We Must Implement
1. **USB Composite Device** with 3 interfaces (MI_00, MI_01, MI_02)
2. **HID++ Protocol** on MI_01 with 3 collections (Report IDs 0x10, 0x11, 0x12)
   - Report ID 0x10: 7 bytes (short)
   - Report ID 0x11: 20 bytes (long)
   - Report ID 0x12: 64 bytes (very long) ← **Primary read channel**
2. **HID++ Feature Set**: 0x0000, 0x0001, 0x8136, 0x8137, 0x8138, 0x8139, 0x8133, 0x8130
3. **Async Event Handling**: Unsolicited events on interrupt IN (Report ID 0x12)
4. **TrueForce Stream** on MI_02 (page 0xFFFD, 64 bytes, 250 Hz)
5. **Init Sequence**: 2×68 packets @ 2ms, range patch 0x0E → 1080°
5. **Input-Only DI Mode**: Non-exclusive, no effects, TrueForce stream provides force

### ⚠️ G HUB Dependency
- **G HUB MUST BE RUNNING** for FFB to work
- Without G HUB: "force feedback will NOT work"

### 🔑 Key Parameters to Replicate
| Parameter | Value | Source |
|-----------|-------|--------|
| VID/PID | 0x046D / 0xC276 | Device tree |
| Report IDs | 0x10, 0x11, 0x12 | HID++ log |
| Feature Indices | 0x16, 0x17, 0x18, 0x19, 0x14, 0x12 | HID++ log |
| TrueForce Stream | 250 Hz, 64 bytes, 0xFFFD | TrueForce log |
| Init Sequence | 2×68 @ 2ms, range 1080° | TrueForce log |
| Default Strength | 8.0 Nm (0xFFFF) | HID++ log |
| Default Rotation | 1080° (0x0438) | HID++ log |
| Profile Slot | 5 (onboard) | HID++ log |

---

## Files for Reference
- `docs/references/rs50_hidpp_protocol.md` — HID++ protocol details
- `docs/references/rs50_trueforce_stream.md` — TrueForce stream protocol
- `docs/references/rs50_device_tree.md` — USB/HID device structure
- `docs/references/rs50_default_profile.json` — Baseline profile