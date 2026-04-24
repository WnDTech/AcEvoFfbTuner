# Developer Checklist: FFB Signal Processing for AC EVO

This document outlines the technical requirements for processing noisy longitudinal forces ($F_x$) when building a custom FFB application for Assetto Corsa EVO.

## 1. Data Acquisition & Timing (The Foundation)
Noise often originates from jittery timing rather than the physics engine itself.
- [x] **Verify Frequency:** Ensure your loop is polling the Shared Memory at exactly **333Hz** (~3.003ms). — *Current: `Thread.Sleep(0)` spin-loop with `timeBeginPeriod(1)`. PacketId dedup prevents double-reads. ~333Hz achieved via spin-wait.*
- [x] **Check Packet Continuity:** Monitor the `packetId` in the shared memory. If IDs are skipped, your app is lagging; if IDs repeat, you are polling too fast. — *Current: `_lastPhysicsPacketId` check in `SharedMemoryReader.TryReadPhysics()` line 99.*
- [x] **High-Precision Sleep:** Avoid `Thread.Sleep(1)`. On Windows, use `Multimedia Timer` (winmm.dll) or a `SpinWait` loop with a `Stopwatch` to ensure microsecond accuracy. — *Current: Uses `timeBeginPeriod(1)` + `Thread.Sleep(0)`. Falls back to `Sleep(1)` after 3 idle spins.*

## 2. Signal Processing Pipeline (The "Noise Killers")
Apply these filters in order before sending the final value to the motor.

### A. Low-Pass Filter (IIR / EMA)
*Purpose: Removes high-frequency "fuzz" and electrical hum.*
- [x] Implement an **Exponential Moving Average**: `output = (alpha * raw) + (1.0 - alpha) * lastOutput`. — *Current: Channel-level EMA in `FfbChannelMixer` (alpha=0.05, line 82-87). Output EMA in `FfbPipeline` (alpha=0.08, line 92).*
- [x] **ISSUE FOUND: Channel-level EMA alpha is too aggressive (0.05).** This means 95% of the previous value is retained per tick, creating significant lag. The checklist recommends alpha=0.15 as a starting point. **Fix:** Tune `ChannelSmooth` from `0.95f` → `0.85f` (i.e., alpha from 0.05 → 0.15) in `FfbChannelMixer.cs`. — *Fixed: Per-channel alphas implemented (Mz: 0.20, Fx: 0.08, Fy: 0.12).*
- [x] **ISSUE FOUND: No per-channel alpha control.** Fx needs ~3x more smoothing than Mz. **Fix:** Add separate smoothing constants per channel type:
  - `Mz`: alpha = 0.20 (responsive, steering detail matters)
  - `Fx`: alpha = 0.08 (heavy smoothing, longitudinal is noisy)
  - `Fy`: alpha = 0.12 (moderate smoothing)

### B. Slew Rate Limiting
*Purpose: Prevents "mechanical clacking" by capping how fast the motor can change direction.*
- [x] **Calculate Delta:** `delta = currentFilteredValue - lastFilteredValue`. — *Current: `FfbPipeline.cs` lines 87-89.*
- [x] **Clamp Delta:** If `abs(delta) > maxSlew`, force delta to `maxSlew * sign(delta)`. — *Current: `MaxSlewRate = 0.02f`, clamped at line 88.*
- [x] **ISSUE FOUND: Slew rate is applied AFTER the output EMA, creating double-smoothing.** The pipeline does: raw → channel EMA → slew clamp → output EMA. The slew clamp at 0.02/tick and the output EMA at alpha=0.08 are redundant and compound the lag. **Fix:** Remove the final output EMA (`_outputSmooth`) and rely on the slew limiter alone for output smoothing, OR increase MaxSlewRate to 0.05 and remove the output EMA. — *Fixed: Output EMA removed, slew-only smoothing.*
- [x] **ISSUE FOUND: MaxSlewRate = 0.02 is too tight.** At 333Hz, a signal needs 50 ticks (150ms) to go from 0→1.0. This causes noticeable lag in quick direction changes. **Fix:** Increased to `0.35f` per tick (covers 0→1.0 in ~6 ticks = 18ms). The 1kHz interpolation thread handles smooth transitions, so slew rate only needs to filter noise spikes.

### C. Deadzone & Center Knee
*Purpose: Stops the wheel from vibrating/humming when the car is stationary or driving straight.*
- [x] **Implement Deadzone:** If `abs(Fx) < 0.005`, return `0`. — *Current: `NoiseFloor = 0.008f` in `FfbPipeline.cs` line 84.*
- [x] **Apply Power Curve:** Use `output = sign(Fx) * pow(abs(Fx), 1.1)` to soften the center response without losing total force. — *Implemented as `CenterKneePower` parameter (currently 1.0 = disabled). Can enable by setting to 1.1.*

## 3. Hardware Interop (Output Optimization)
- [ ] **ISSUE FOUND: No interpolation (upsampling).** The wheel polls at ~1000Hz but physics runs at 333Hz. Currently, `SendConstantForce()` is called only when a new physics packet arrives. Between physics frames, the device holds the last value, creating "stair-step" force transitions. **Fix:** Implement a high-frequency output thread (1kHz) that linearly interpolates between the current and target force values:
  ```
  // In TelemetryLoop or FfbDeviceManager:
  // Store _targetForce and _currentForce
  // In 1ms timer: _currentForce = Lerp(_currentForce, _targetForce, 0.33)
  // Send _currentForce to device every 1ms
  ```
- [x] **Soft Clipping:** If the $F_x$ signal exceeds 1.0 (100% force), use a soft-clipping function (like `tanh`) instead of a hard limit to avoid a "flat" feel at high loads. — *Current: `MathF.Tanh(absNorm * CompressionPower)` in `FfbPipeline.cs` line 49, plus `FfbOutputClipper.Process()` with sqrt-based soft clip.*

## 4. AC EVO Specific Logic
- [ ] **ISSUE FOUND: Slip-Ratio filtering is not adaptive.** The checklist recommends increasing filter strength when `slipRatio` is high (ABS/TC active). Currently, `FfbSlipEnhancer` ADDS force during slip but doesn't increase smoothing. **Fix:** In `FfbChannelMixer.Mix()`, detect high-slip conditions and dynamically reduce the EMA alpha (increase smoothing) for Fx channels:
  ```csharp
  // Pseudocode:
  float maxFrontSlip = Math.Max(Math.Abs(raw.SlipRatio[0]), Math.Abs(raw.SlipRatio[1]));
  float slipAlpha = maxFrontSlip > SlipThreshold ? 0.04f : 0.08f; // heavier smoothing during slip
  _smFxFront = _smFxFront * (1f - slipAlpha) + fxFront * slipAlpha;
  ```
- [ ] **Gear Shift Smoothing:** Implement a brief (20ms) "Slew override" during gear changes to prevent the harsh jolt from the sudden change in longitudinal acceleration. — *Not currently implemented. **Fix:** Detect gear change from `raw.Gear` delta, temporarily override MaxSlewRate to a very low value (0.01) for ~6-7 ticks, then restore.*

## 5. Debugging Tools
- [x] **Live Telemetry Plotter:** Create a simple graph in your app showing **Raw $F_x$ (Red)** vs. **Filtered $F_x$ (Green)**. — *Current: `MainWindow.UpdatePlot()` using ScottPlot, called from `OnUiUpdate` at 30Hz.*
- [ ] **ISSUE FOUND: No latency measurement.** The checklist recommends measuring time from Shared Memory read to wheel command. **Fix:** Add `Stopwatch` timing in `TelemetryLoop.Loop()` around the read→process→send cycle and log/display the round-trip latency. Target: < 5ms.

---

## Implementation Priority (Ordered by Impact)

### Priority 1 — High Impact, Low Effort (Do First)
| # | Fix | File | Change |
|---|-----|------|--------|
| 1a | Tune channel EMA alpha | `FfbChannelMixer.cs` | Change `ChannelSmooth = 0.95f` → `0.85f`, or add per-channel alphas |
| 1b | Remove redundant output EMA | `FfbPipeline.cs` | Remove `_outputSmooth` EMA (lines 92-94), use slew-limited value directly |
| 1c | Increase MaxSlewRate | `FfbPipeline.cs` | Changed `MaxSlewRate = 0.05f` → `0.35f` |
| 1d | Add per-channel smoothing | `FfbChannelMixer.cs` | Separate `_smFxFront` alpha (0.08) from `_smMzFront` alpha (0.20) |

### Priority 2 — High Impact, Medium Effort
| # | Fix | File | Change |
|---|-----|------|--------|
| 2a | Add output interpolation thread | `FfbDeviceManager.cs` + `TelemetryLoop.cs` | 1kHz Lerp thread between `_currentForce` and `_targetForce` |
| 2b | Adaptive slip-based Fx smoothing | `FfbChannelMixer.cs` | Dynamically reduce Fx alpha when front slip ratio > threshold |
| 2c | Gear shift smoothing | `FfbPipeline.cs` | Detect gear delta, temporarily reduce MaxSlewRate |

### Priority 3 — Medium Impact, Low Effort
| # | Fix | File | Change |
|---|-----|------|--------|
| 3a | Center power curve | `FfbPipeline.cs` | Add `pow(abs(output), 1.1)` stage or default LUT to progressive(1.1) |
| 3b | Latency measurement | `TelemetryLoop.cs` | Add Stopwatch around read→process→send, display in UI |

---

## Pipeline Order (IMPLEMENTED)
```
SharedMemory → MapRawData → FfbPipeline.Process():
  1. ChannelMixer.Mix()     — Spike clamp → Normalize → per-channel EMA (Mz: 0.20, Fx: 0.08*, Fy: 0.12)
                               *Fx drops to 0.04 when front slip ratio > 0.05 (adaptive)
  2. MasterGain * AutoGain / ForceScale
  3. Tanh compression
  4. LUT curve
  5. SlipEnhancer.Apply()   — adds slip-based force
  6. FfbDamping.Apply()
  7. FfbDynamicEffects.Apply()
  8. OutputClipper.Process() * OutputGain
  9. Sign correction
   10. Center power curve      — pow(abs, CenterKneePower=1.0) [disabled]
  11. Noise floor cut         — below 0.001 → 0
  12. Slew rate limiter       — max 0.35/tick (0.01 during gear shifts for 7 ticks)
  13. Speed gate              — < 2 km/h → 0, < 10 km/h → fade
→ FfbDeviceManager.SendConstantForce() → sets _targetForce
→ 1kHz Interpolation Thread → timestamp-based sliding lerp → SendConstantForceDirect()
```

---
**Developer Tip:** $F_x$ (Longitudinal) usually needs **3x more smoothing** than $M_z$ (Steering Torque). Don't be afraid to filter $F_x$ aggressively; the human hand is less sensitive to longitudinal vibration than it is to steering rack detail.

---

## Phase Shift Warning (Mz vs Fx)

The per-channel alpha split creates a **phase offset** between Mz and Fx:
- **Mz** (alpha=0.20): time constant ≈ 5 ticks ≈ 15ms
- **Fx** (alpha=0.08): time constant ≈ 12.5 ticks ≈ 37ms
- **Phase delta**: ~22ms

**When this matters:** In heavy trail-braking scenarios, the longitudinal weight transfer ($F_x$) will "arrive" at the wheel ~22ms after the steering torque ($M_z$). This can feel like a "disconnected" sensation during transitions.

**Tuning guidance:**
| Symptom | Adjustment |
|---------|-----------|
| Feels disconnected during trail braking | Increase `FxAlpha` from 0.08 → 0.12 |
| Fx still too noisy at 0.12 | Reduce `MzAlpha` from 0.20 → 0.15 (bring them closer) |
| Want zero phase offset | Set all alphas equal (0.15) — trades noise for coherence |
| Need precision AND silence | Add a "Lead Compensation" delay buffer on Mz to match Fx lag |

**Current values (0.08 / 0.20) are the recommended starting point. Tune only after driving.**

---

## Implementation Status

| Feature | Importance | Status | Detail |
|---------|-----------|--------|--------|
| High-Precision Timing | 10/10 | ✅ Implemented | Spin-wait + `timeBeginPeriod(1)` |
| Per-Channel Noise Filtering | 9/10 | ✅ Implemented | Mz: 0.20, Fx: 0.08, Fy: 0.12 |
| Mechanical Protection (Slew) | 8/10 | ✅ Implemented | 0.35/tick + gear shift override at 0.01 |
| DD Motor Smoothness (Interpolation) | 10/10 | ✅ Implemented | 1kHz timestamp-based sliding lerp |
| Adaptive Slip Filtering | 9/10 | ✅ Implemented | Fx alpha → 0.04 when slip > 0.05 |
| Center Power Curve | 7/10 | ✅ Implemented | `pow(abs, 1.0)` — disabled; center suppression handles soft-center |
| Center Suppression | 9/10 | ✅ Updated | t^1.5 curve (was cubic t³), 8° base zone (was 10°), 1.5× max speed scale (was 2×) |
| Noise Floor | 8/10 | ✅ Updated | 0.001 (was 0.008) — lets suppressed forces through |
| Gear Shift Smoothing | 6/10 | ✅ Implemented | 7-tick slew override at 0.01 |
| Latency Monitor | 5/10 | ✅ Implemented | Per-frame + 60-frame rolling average |
| Output EMA Removal | 8/10 | ✅ Implemented | Eliminated double-smoothing lag source |
| Phase Shift Compensation | 7/10 | ⏳ Pending test | Needs driving validation — see tuning table above |
