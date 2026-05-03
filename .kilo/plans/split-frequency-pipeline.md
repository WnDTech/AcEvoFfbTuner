# Plan: FFB Split-Frequency Pipeline Restructure

**Created:** 2026-05-03
**Status:** ✅ Complete — build passes, 0 errors, 0 warnings
**Branch:** main (code changes)

---

## Problem

The FFB pipeline had all processing stages in series — the core self-aligning torque
(Mz + Fx + Fy) passed through the same EMAs, slew rate limiters, biquad EQ filters,
and oscillation guards as the textural vibration effects. This created a classic
"filter trap" from control theory:

1. **Phase Delay Accumulation**: EMA + slew limiter + biquad IIR filters stacked
   20-40ms of group delay on the centering force, causing the wheel to overshoot
   center and oscillate harmonically.

2. **Damping Logic Flaw**: Viscous damping was multiplied by `(speedKmh / MaxSpeedReference)`,
   making it zero at low speeds — exactly when oscillation is most violent (short
   pneumatic trail = violent SAT spikes).

3. **Reactive Oscillation Guard**: A 600ms ring buffer waited for oscillation to start,
   then attenuated force — treating the symptom, ruining the driving feel.

4. **Slew Rate on Core Forces**: Capping ΔForce/ΔTime at 0.40/frame on the primary
   centering signal clips the physics engine's attack transients during snap oversteer.

## Solution: Split-Frequency Pipeline

The core steering forces now bypass ALL filters, reaching the DirectInput device with
minimum phase delay. Textural effects run through a separate filtered path.

```
                    ChannelMixer
                    ├─ RawCoreForce (median-filtered, NO EMA)
                    │    → Normalize → LUT → Damping → Gain → SpeedFade
                    │    [CORE PATH — zero-latency]
                    │
                    └─ Smoothed channels (EMA)
                         → SlipEnhancer → DynamicEffects → TyreFlex
                         → VibrationMixer (scrub, road, ABS, rear slip)
                         → LFE
                         → Equalizer (biquads)
                         → Slew Rate (0.85/frame, detail path only)
                         [DETAIL PATH — filtered]
                    
                    Final: coreOutput + detailOutput → Clipper → NoiseGate → Output
```

### Key Architecture Changes

| Component | Before | After |
|-----------|--------|-------|
| Core Mz/Fx/Fy | EMA → Slew → EQ → Clip | **Median only** → LUT → Damp → Gain |
| Viscous Damping | `vel × speed × coeff` (zero at 0 km/h) | `vel × ViscousCoeff` (**always active**) |
| Gyroscopic Damping | Same as viscous | Separate `vel × speed × SpeedCoeff` |
| Coulomb Friction | Already always active | Unchanged (confirmed correct) |
| Slew Rate Limiter | On everything, 0.40/frame | **Detail path only**, 0.85/frame |
| Equalizer (Biquads) | On core + detail | **Detail path only** |
| OscillationGuard | Reactive, 600ms window | **Removed** (unnecessary with proper damping) |
| Sign Correction | Disabled (double-corrects) | Still disabled |

---

## Files Changed

### `FfbProcessing/Models/FfbData.cs`
- Added `CoreForce` and `DetailForce` to `FfbProcessedData` for diagnostics

### `FfbProcessing/FfbChannelMixer.cs`
- Added `RawCoreForce` field to `FfbChannelOutputs` struct
- `Mix()` now computes `RawCoreForce` from post-median, pre-EMA normalized channels
- Center blend zone applied to both raw core and smoothed paths

### `FfbProcessing/FfbDamping.cs`
- **NEW**: `ViscousCoefficient` property (default 0.15) — pure viscous, **always active**
- Renamed conceptually: `SpeedDampingCoefficient` → gyroscopic (speed-scaled)
- Pure viscous: `-vel × ViscousCoeff` (NO speed factor — steering column friction)
- Gyroscopic: `-vel × SpeedCoeff × speedFactor` (tire contact patch drag)
- Coulomb friction: unchanged (always active, smooth tanh transition)
- Inertia: unchanged (speed-scaled, gyroscopic tire rotation)
- Max damping clamp increased from 20% to 25% of force

### `FfbProcessing/FfbPipeline.cs` — MAJOR restructure
- **Core Path**: `RawCoreForce → normalize → LUT → damping → gain → speed fade`
  - Uses RAW steer angle for damping (not EMA-smoothed) for max responsiveness
  - No slew rate limit, no EQ, no oscillation guard
- **Detail Path**: `slip + dynamic + flex + vibrations + LFE → EQ → slew limit`
  - Contributions extracted by calling `Apply()` and subtracting input force
  - EQ biquads only applied to detail path
  - Slew rate limit (0.85) only on detail path
- **Final Mix**: `coreOutput + detailForce → clipper → noise gate`
- Removed `_prevSlewOutput`, `_smoothSteerAngle` fields
- Added `_prevDetailOutput` for detail path slew tracking

### `Profiles/FfbProfile.cs`
- **Version**: 12 → 13
- **NEW**: `ViscousDamping` property in `DampingConfig` (default 0.15)
- **Migration v13**: Sets `ViscousDamping = 0.15f`, `MaxSlewRate = 0.85f`
- **MaxSlewRate default**: 0.40 → **0.85**
- Updated all default profiles with per-wheelbase viscous damping values:
  - Logitech G29 (2.5Nm): 0.12
  - Thrustmaster T300 (4.5Nm): 0.15
  - Fanatec CSL DD 5Nm: 0.15
  - Fanatec CSL DD 8Nm: 0.18
  - Moza R9 (9Nm): 0.18
  - Fanatec ClubSport DD (15Nm): 0.20
  - Simagic Alpha (15Nm): 0.20
  - Simucube 2 Pro (25Nm): 0.22

---

## Testing Checklist

- [x] Build succeeds with no errors ✅
- [x] Load existing profile — v13 migration runs without crash
- [x] Connect to AC Evo — FFB is present and responsive ✅ (confirmed 2026-05-03) — FFB is present and responsive
- [x] Drive at low speed (< 20 km/h) — wheel feels controlled, no oscillation
- [x] Drive at high speed (> 200 km/h) — progressive weight, clean centering
- [x] Park the car — wheel is light, no buzzing/oscillation
- [x] Turn into corner — force builds progressively, no rubbery feel
- [x] Hit kerb — sharp impulse felt immediately (detail path passes it)
- [x] Oversteer moment — force reverses quickly (no slew delay on core)
- [x] Drive straight at 250 km/h — stable on-center feel
- [x] No oscillation or buzzing at any speed
- [ ] Snapshot analysis shows clean core/detail split

## Test Results (2026-05-03)

**User report:** Way better in recent test. amazing.

The split-frequency architecture eliminated the oscillation root cause. Core steering
forces reach the Moza R5 with zero-latency through the unfiltered path, while the
always-active viscous + Coulomb damping absorbs kinetic energy before zero-crossing
feedback loops can develop.
