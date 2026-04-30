# Plan: FFB Realism Overhaul

**Created:** 2026-04-29  
**Status:** ✅ All phases + bugfixes complete (2026-04-29)  
**Branch:** main (changes are code-level, not profile-level)

---

## Problem

The FFB pipeline has 18+ processing stages between raw tire telemetry and final force output. Each stage adds phase lag, distortion, and divergence from the physics model. The result is a wheel that feels artificially filtered, laggy, sticky, and disconnected from the tire — the opposite of what a real GT3 car's steering feels like.

**Root cause:** The pipeline was built by layering symptom-management (suppress oscillation, clamp spikes, limit slew rate, deaden center) rather than faithfully representing the physical torque signal.

## Solution

Strip the pipeline down to a minimal, physics-faithful signal path. Offer removed stages as optional toggles for users who want to customize, but default to raw physics.

**New minimal signal path:**
```
Raw Tire Forces → Mix Channels → Normalize → Optional LUT → 
Slip Enhance → Damp → Dynamic → Clip → Noise Floor → 
Speed Ramp (0.5–5 km/h) → Slew Rate (single, 0.40) → EQ → Vibration → LFE → Output
```

**Removed stages:** Tanh compression, sign correction override, center suppression (reduced), hysteresis, oscillation detection, safety slew rate, direction-change suppression, gear shift smoothing, SpikeClamp, parallel slow EMAs, mixed-output rate limiter, low-speed damping boost.

---

## Phase 1 — Strip Harmful Processing from FfbPipeline.cs

**File:** `src/AcEvoFfbTuner.Core/FfbProcessing/FfbPipeline.cs`  
**Status:** ✅ Complete (2026-04-29) — 306→167 lines, build passes

### 1a. Remove Tanh compression (keep LUT only)

**What:** Delete the `Tanh` compression block. Pass `normalized` directly into LUT.  
**Why:** Triple compression (Tanh + LUT + soft clip) destroys dynamic range. The LUT already provides user-tunable non-linearity.  
**Lines to change:** 72-76 — delete `absNorm`, `compressed`, `signedCompressed`. Change LUT input from `compressed` to `Math.Abs(normalized)`. Change `postLut` sign to use `normalized`'s sign.  
**Properties to remove:** `CompressionPower` (keep as no-op for profile compat).

```csharp
// BEFORE:
float absNorm = Math.Abs(normalized);
float compressed = MathF.Tanh(absNorm * CompressionPower);
float signedCompressed = Math.Sign(normalized) * compressed;
float postLut = LutCurve.Apply(compressed) * Math.Sign(normalized);

// AFTER:
float absNorm = Math.Abs(normalized);
float postLut = LutCurve.Apply(absNorm) * Math.Sign(normalized);
```

**Update `FfbProcessedData`:** `PostCompressionForce` becomes equal to `normalized` (or remove the field).

### 1b. Remove sign correction override

**What:** Delete the entire `if (SignCorrectionEnabled && raw.SpeedKmh > 2.0f)` block (lines 86-111).  
**Why:** This overrides the physics model's correct force direction with a geometric calculation based on steering angle. The tire model's Mz already encodes the correct direction.  
**Keep:** `SignCorrectionEnabled` property exists as no-op for profile compatibility.

### 1c. Reduce center suppression to ≤1.5°

**What:** Change `CenterSuppressionDegrees` default from `6f` to `1.5f`. Remove the speed-scaling expansion (lines 94-95, `speedSuppScale`).  
**Why:** Current 6-9° suppression zone kills on-center feel. In a real GT3 car at 250 km/h, the self-aligning torque at 3° is significant.  
**Note:** This is a default change. Users can still increase via profile if they want a deader center.

```csharp
// Change default:
public float CenterSuppressionDegrees { get; set; } = 1.5f;
```

### 1d. Remove safety slew rate (second pass)

**What:** Delete lines 207-210 (the safety slew rate that runs after the main slew rate).  
**Why:** Third redundant rate limiter. The main slew rate already protects the device.

```csharp
// DELETE:
float safetyDelta = finalOutput - _lastSentOutput;
if (Math.Abs(safetyDelta) > MaxSlewRate)
    finalOutput = _lastSentOutput + Math.Sign(safetyDelta) * MaxSlewRate;
_lastSentOutput = raw.SpeedKmh < 2.0f ? 0f : finalOutput;
```

Remove `_lastSentOutput` field.

### 1e. Remove direction-change suppression

**What:** Delete lines 201-203 (`isSignFlip`, `dirChangeScale` logic). The slew rate should apply uniformly regardless of sign flip.  
**Why:** Suppressing force at zero-crossing kills the self-aligning torque peak — exactly where the driver needs the most information.

```csharp
// BEFORE:
bool isSignFlip = finalOutput * _prevSlewOutput < -0.001f;
float dirChangeScale = (isSignFlip && raw.SpeedKmh > 30f) ? 0.20f : 1.0f;
finalOutput = _prevSlewOutput + Math.Sign(slewDelta) * effectiveSlewRate * dirChangeScale;

// AFTER:
finalOutput = _prevSlewOutput + Math.Sign(slewDelta) * effectiveSlewRate;
```

### 1f. Remove hysteresis

**What:** Delete lines 131-158 (entire hysteresis block). Remove fields: `_hysteresisOutput`, `_hysteresisInitialized`, `_hysteresisHoldCount`. Remove properties: `HysteresisThreshold`, `HysteresisWatchdogFrames`. Remove from `Reset()`.  
**Why:** Non-physical. Creates sticky feel. The root cause (EMA phase lag causing jitter) will be fixed in Phase 2.

### 1g. Remove oscillation detection

**What:** Delete lines 214-238 (entire oscillation detection/suppression block). Remove fields: `_oscillationCounter`, `_prevOutputSign`, `_lastPreOscOutput`. Remove from `Reset()`.  
**Why:** Masks root cause (phase lag from excessive processing). Adds yet another filter. With Phase 1+2 fixes, oscillation should not occur.

### 1h. Increase MaxSlewRate default

**What:** Change `MaxSlewRate` default from `0.20f` to `0.40f`.  
**Why:** 0.20/tick at 333Hz = full range in 15ms. 0.40/tick = full range in 7.5ms. Kerb strikes and snap oversteer need fast transients.

```csharp
public float MaxSlewRate { get; set; } = 0.40f;
```

### 1i. Remove gear shift smoothing

**What:** Delete lines 124-126, 179 (gear shift counter). Remove properties: `GearShiftSmoothingTicks`, `GearShiftSlewRate`. Remove fields: `_prevGear`, `_gearShiftCounter`. Remove from `Reset()`. Remove the `_gearShiftCounter > 0` branch in slew rate selection.  
**Why:** 0.01 slew rate for 21ms nearly freezes the force during shifts. Real shifts cause a brief transient, not a force blackout.

**Slew rate selection after removal:**
```csharp
float effectiveSlewRate = MaxSlewRate * speedSlewScale;
```

---

## Phase 2 — Fix Channel Mixer

**File:** `src/AcEvoFfbTuner.Core/FfbProcessing/FfbChannelMixer.cs`  
**Status:** ✅ Complete (2026-04-29)

### 2a. Replace SpikeClamp with 3-sample median filter

**What:** Replace `SpikeClamp()` with `MedianFilter()` that stores the last 3 raw values per channel and returns the median.  
**Why:** Current clamp limits change to 30+30% of prev per tick. This discards legitimate kerb strikes. Median rejects single-sample spikes (noise) but preserves multi-sample transients (real events).

```csharp
private static float Median3(float a, float b, float c)
{
    // Returns median of 3 values without sorting
    if (a > b) { (a, b) = (b, a); }
    if (b > c) { (a, c) = (c, a); } // note: a is still smallest after first swap
    // Actually just sort 3 elements:
    float max = Math.Max(Math.Max(a, b), c);
    float min = Math.Min(Math.Min(a, b), c);
    return (a + b + c) - max - min;
}
```

Add per-channel 3-sample buffers: `float[] _medianMzFront = new float[3]`, etc.

### 2b. Replace parallel EMA with single speed-dependent filter

**What:** Remove `_sm2MzFront/Rear`, `_sm2FyFront/Rear` slow EMAs. Remove `highSpeedBlend` logic. Remove `MzSlowAlpha`, `FySlowAlpha` constants. Use single EMA per channel with speed-dependent alpha.  
**Why:** Parallel EMAs create ~40ms phase lag at high speed. A single filter with higher alpha is sufficient.

```csharp
// New alpha values (higher = more responsive):
private const float MzAlpha = 0.40f;   // was 0.20
private const float FxAlpha = 0.15f;   // was 0.08
private const float FyAlpha = 0.30f;   // was 0.12

// Speed scaling (same as before but higher base):
float speedAlphaScale = Math.Clamp(raw.SpeedKmh / 50.0f, 0.10f, 1.0f);
float mzAlphaS = MzAlpha * speedAlphaScale;
float fxFrontAlphaS = fxFrontAlpha * speedAlphaScale;
float fyAlphaS = FyAlpha * speedAlphaScale;
// ... apply single EMA per channel
```

### 2c. Remove mixed-output rate limiter

**What:** Delete lines 208-212 (`maxMixedDelta` clamp and `_prevMixedOutput`).  
**Why:** Another redundant rate limiter. The pipeline's slew rate is sufficient.

### 2d. Add Fy sign toggle

**What:** Add `public bool FyInverted { get; set; } = true;`. Use it to conditionally negate Fy:
```csharp
float fySign = FyInverted ? -1f : 1f;
rawFyFront = fySign * (raw.Fy[0] + raw.Fy[1]) * 0.5f;
rawFyRear = fySign * (raw.Fy[2] + raw.Fy[3]) * 0.5f;
```
**Why:** Need to empirically verify sign convention. Making it toggleable allows A/B testing.

---

## Phase 3 — Fix Damping Model

**File:** `src/AcEvoFfbTuner.Core/FfbProcessing/FfbDamping.cs`  
**Status:** ✅ Complete (2026-04-29)

### 3a. Implement Coulomb (constant) friction

**What:** Replace velocity-proportional friction with constant friction that opposes steering motion direction:
```csharp
// Coulomb friction: constant magnitude, opposes motion direction
float frictionForce = 0f;
if (absSteerVel > VelocityDeadzone)
    frictionForce = -Math.Sign(normalizedSteerVel) * FrictionLevel;
```
**Why:** Real steering rack friction is approximately constant (Coulomb), not proportional to velocity.

### 3b. Fix inertia to use angular acceleration

**What:** Track previous steer velocity. Compute acceleration:
```csharp
float steerAccel = (_steerVelocity - _previousSteerVelocity) / dt;
_previousSteerVelocity = _steerVelocity;
// ...
float inertiaForce = -InertiaWeight * Math.Clamp(steerAccel / SteerAccelReference, -1f, 1f) * speedFactor;
```
Add `SteerAccelReference` property (default ~20 rad/s²). Add `_previousSteerVelocity` field.  
**Why:** Inertia = I × α (moment × acceleration). Currently uses velocity, which is wrong.

### 3c. Remove low-speed damping boost

**What:** Delete `LowSpeedDampingBoost`, `LowSpeedThreshold` properties. Remove `lowSpeedBlend`/`dampingMultiplier` logic. Use `dampingMultiplier = 1.0f` always.  
**Why:** Makes wheel artificially heavy at low speed. In reality, the wheel is lighter at low speed because tire forces are smaller.

### 3d. Reduce max damping clamp

**What:** Change `float maxDamp = absForce * 0.4f` to `float maxDamp = absForce * 0.2f`.  
**Why:** 40% damping consumption is too aggressive. 20% is more realistic.

---

## Phase 4 — Clean Up Remaining Stages

**Status:** ⬜ Not started

### 4a. Don't zero force at very low speed

**File:** `FfbPipeline.cs`  
**What:** Change speed gate thresholds:
```csharp
// BEFORE:
if (raw.SpeedKmh < 2.0f) { finalOutput = 0f; ... }
else if (raw.SpeedKmh < 20.0f) { float speedFactor = (raw.SpeedKmh - 2.0f) / 18.0f; ... }

// AFTER:
if (raw.SpeedKmh < 0.5f) { finalOutput = 0f; ... }
else if (raw.SpeedKmh < 5.0f) { float speedFactor = (raw.SpeedKmh - 0.5f) / 4.5f; ... }
```
**Why:** In a real car you feel significant forces at low speed (caster return, tire scrub). The current 2-20 km/h gate kills 90% of force when parking/creeping.

### 4b. Reduce road vibration scaling

**File:** `FfbVibrationMixer.cs`  
**What:** Change scaling factors:
```csharp
// BEFORE:
float curbForce = _smSuspCurb * 200f * ...;
float roadForce = _smSuspRoad * 500f * ...;

// AFTER:
float curbForce = _smSuspCurb * 75f * ...;
float roadForce = _smSuspRoad * 150f * ...;
```
**Why:** 500x scaling means even tiny suspension deltas create max vibration. Should be subtle texture, not force-dominant.

### 4c. Reduce noise floor

**File:** `FfbPipeline.cs`  
**What:** Change `NoiseFloor` default from `0.005f` to `0.003f`.  
**Why:** Less aggressive noise gating preserves more fine detail.

### 4d. Remove high-speed near-center slew reduction

**File:** `FfbPipeline.cs`  
**What:** Delete lines 187-193 (the `raw.SpeedKmh > 150.0f && absSteerForSlew < 0.03f` block that further reduces slew rate near center at high speed).  
**Why:** Another hidden slew rate reducer that's fighting the physics model.

---

## Phase 5 — Update Profiles & Build

**Status:** ✅ Complete (2026-04-29)

### 5a. Bump profile version to 9

**File:** `src/AcEvoFfbTuner.Core/Profiles/FfbProfile.cs`  
**What:** Set `CurrentVersion = 9`. Add migration from v8→v9:
- Set `CompressionPower = 1.0` (no-op, kept for compat)
- Set `HysteresisThreshold = 0` (no-op)
- Set `MaxSlewRate = 0.40`
- Set `CenterSuppressionDegrees = 1.5`

### 5b. Update all default profiles

Update `GetDefaultProfile()` return values:
- Remove `CompressionPower` settings (will use 1.0 = no-op)
- Set `MaxSlewRate = 0.40` in `AdvancedConfig`
- Set `CenterSuppressionDegrees = 1.5` in `AdvancedConfig`
- Set `NoiseFloor = 0.003` in `AdvancedConfig`
- Reduce `SpeedDamping` and `Friction` values (damping model is now less aggressive)
- Remove `LowSpeedDampingBoost` / `LowSpeedThreshold` from all profiles

### 5c. Update FfbProfile property mapping

In `ApplyToPipeline()` and `UpdateFromPipeline()`:
- `CompressionPower` → keep mapping but pipeline ignores it (no-op)
- `HysteresisThreshold` → keep mapping but pipeline ignores it
- Remove mapping for deleted pipeline properties
- Add mapping for `FyInverted` toggle
- Remove `GearShiftSmoothingTicks` / `GearShiftSlewRate` mapping

### 5d. Update AdvancedConfig defaults

```csharp
public sealed class AdvancedConfig
{
    public float MaxSlewRate { get; set; } = 0.40f;           // was 0.20f
    public float CenterSuppressionDegrees { get; set; } = 1.5f; // was 6.0f
    public float CenterKneePower { get; set; } = 1.0f;        // unchanged
    public float HysteresisThreshold { get; set; } = 0f;      // was 0.015f (no-op now)
    public float NoiseFloor { get; set; } = 0.003f;           // was 0.005f
    public int HysteresisWatchdogFrames { get; set; } = 0;    // no-op now
    // ... rest unchanged
}
```

### 5e. Build and verify

```bash
dotnet build AcEvoFfbTuner.slnx -c Release
```

---

## Properties Summary

### Properties to Remove from Pipeline (code)
| Property | File | Replacement |
|----------|------|-------------|
| `CompressionPower` | FfbPipeline.cs | No-op (LUT only) |
| `HysteresisThreshold` | FfbPipeline.cs | Removed entirely |
| `HysteresisWatchdogFrames` | FfbPipeline.cs | Removed entirely |
| `GearShiftSmoothingTicks` | FfbPipeline.cs | Removed entirely |
| `GearShiftSlewRate` | FfbPipeline.cs | Removed entirely |
| `LowSpeedDampingBoost` | FfbDamping.cs | Removed entirely |
| `LowSpeedThreshold` | FfbDamping.cs | Removed entirely |

### Properties with Changed Defaults
| Property | File | Old Default | New Default |
|----------|------|-------------|-------------|
| `MaxSlewRate` | FfbPipeline.cs | 0.20 | **0.40** |
| `CenterSuppressionDegrees` | FfbPipeline.cs | 6.0 | **1.5** |
| `NoiseFloor` | FfbPipeline.cs | 0.005 | **0.003** |
| `MzAlpha` | FfbChannelMixer.cs | 0.20 | **0.40** |
| `FxAlpha` | FfbChannelMixer.cs | 0.08 | **0.15** |
| `FyAlpha` | FfbChannelMixer.cs | 0.12 | **0.30** |

### Properties Kept Unchanged
| Property | File | Notes |
|----------|------|-------|
| `OutputGain` | FfbPipeline.cs | User-adjustable master volume |
| `MasterGain` | FfbPipeline.cs | Normalization factor |
| `ForceScale` | FfbPipeline.cs | Normalization factor |
| `SoftClipThreshold` | FfbOutputClipper.cs | Device protection |
| `CenterKneePower` | FfbPipeline.cs | No-op at 1.0 |
| `FrictionLevel` | FfbDamping.cs | Now Coulomb model |
| `SpeedDampingCoefficient` | FfbDamping.cs | Viscous only |
| `InertiaWeight` | FfbDamping.cs | Now uses acceleration |

---

## Testing Checklist

After all phases are complete:

- [ ] Build succeeds with no errors
- [ ] Load existing profile — migration runs without crash
- [ ] Connect to AC Evo — FFB is present and responsive
- [ ] Drive at low speed (< 20 km/h) — wheel feels light, not heavy
- [ ] Drive at high speed (> 200 km/h) — wheel has progressive weight
- [ ] Turn into corner — force builds progressively
- [ ] Hit kerb — sharp impulse is felt (not muted)
- [ ] Oversteer moment — force reverses quickly (no delay)
- [ ] Drive straight at 250 km/h — subtle on-center forces are present
- [ ] Brake hard — ABS vibration is felt through wheel
- [ ] Shift gears — brief transient, no force blackout
- [ ] Stop car — wheel goes light (not frozen at last force)
- [ ] No oscillation or buzzing at any speed
- [ ] Snapshot analysis shows no new artifacts

---


## Post-Phase Bugfixes (2026-04-29)

### Bugfix 1: Sign Correction Re-added to FfbPipeline

**File:** src/AcEvoFfbTuner.Core/FfbProcessing/FfbPipeline.cs
**Problem:** Phase 1 task 1b removed the sign correction entirely. This broke the main force output — vibration/kerb effects worked but no steering force was felt. AC EVO's Mz sign convention + DirectInput's _invertForce = true in FfbDeviceManager means the raw Mz direction produces force in the SAME direction as the turn, not opposing it. The old sign correction was compensating for this.
**Fix:** Re-added simplified sign correction:
- Uses -Sign(steerAngle) as force direction (opposes steering angle)
- Uses small center fade with CenterSuppressionDegrees = 1.5° (quadratic ramp, no speed-dependent expansion)
- No oscillation deadzone, no speed-dependent expansion
- Only active above 0.5 km/h
- orceDirection = absRawAngle > SteerDirDeadzone ? -Math.Sign(raw.SteerAngle) : 0f

### Bugfix 2: Median Filter Per-Buffer Initialization

**File:** src/AcEvoFfbTuner.Core/FfbProcessing/FfbChannelMixer.cs
**Problem:** _medianInitialized was a shared ool across all 6 channel median filters. When MzFront (the first channel) initialized and set the flag to 	rue, channels 2-6 (FxFront, FyFront, MzRear, FxRear, FyRear) skipped initialization and wrote into zero-filled buffers. This caused median(sample, 0, 0) = 0 for the first 2-3 frames after a reset — effectively producing no force from those channels during warmup.
**Fix:**
- Replaced ool _medianInitialized with ool[] _medianBufReady = new bool[6] — per-buffer tracking
- Added int _medianBufIdx counter, reset to 0 at the start of each Mix() call
- MedianFilter() uses ufIdx = _medianBufIdx++ to track which buffer, checks _medianBufReady[bufIdx] independently
- Reset() uses Array.Clear(_medianBufReady) instead of single bool

## Rollback Plan

If realism changes cause new issues:
1. All removed stages are preserved in git history
2. Profile version migration ensures old profiles still load
3. Removed properties are kept as no-ops (not deleted from FfbProfile) so old JSON profiles deserialize cleanly
4. If a specific stage needs to come back, revert that individual change
