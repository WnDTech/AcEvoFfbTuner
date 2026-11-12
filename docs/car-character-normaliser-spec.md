# Car Character Normaliser + Portable Feel Profiles — Design Spec

Status: DRAFT (2026-11-11) · Scope: app code (all games) + new optional profile fields

## 1. Why

Competitor scan (Nov 2026: MAIRA, irFFB, LMUFFB, FanaLab, Simucube Tuner, Pit House,
FFBClip, Sim Commander, SimHub) — every "auto" FFB feature only sets **strength**, and
the common ones flatten every car to the same peak. The two most repeated user complaints:

1. "Auto FFB makes every car feel identical" (iRacing Auto, MAIRA auto-scale, FFBClip).
2. "I hand-tune a gain per car, per wheelbase" — and vendor per-car auto-switching
   (FanaLab) is unreliable.

No competitor offers clip-free normalisation that **keeps the real torque ratio between
cars**, nor profiles that transfer correctly between wheelbases of different torque.
We already own most of the plumbing (multi-game telemetry pipelines, per-car auto-load,
base torque detection, Hub with ratings), so this is defensible.

## 2. What exists today (verified)

| Piece | Where | Behaviour |
|---|---|---|
| Auto-gain | `FfbPipeline.cs:152-156` | `autoGain = AutoGainScale / raw.CarFfbMultiplier` — cancels the game's per-car multiplier → **flattens cars**. EVO-only signal in practice. |
| Sensitivity | `FfbProfile.cs:141` | `pipeline.MasterGain = 1000 / NormalizationScale` — one static divisor per profile. |
| Wheel torque | `FfbProfile.WheelMaxTorqueNm` (default 5.5), `WheelbaseAutoConfigurator`, Fanatec `MaxTorqueNm` | Used for Nm readouts and wizard tables; output is still expressed as % of base. |
| Auto-load | Profile `CarMatch` priority PerCarAndTrack > PerTrack > PerCar > PerGame > General | One General profile serves many cars with one gain. |
| Hub | `HubClient.cs` — list/facets/download/upload/`RateProfileAsync` | Ratings exist; no wheelbase-aware scaling of downloaded profiles. |

## 3. Feature overview

**A. Torque fingerprint (learned, per game + car).** While driving, the app learns each
car's core-torque distribution in the game's native units and stores it.

**B. Character-preserving normaliser.** A new gain stage scales each car so no car clips,
while preserving a user-controlled fraction of the true torque ratio between cars.

**C. Portable feel (Nm targets).** Profiles can state their intended peak in Newton-metres.
On a different base, the app derives the gain so the *feel* transfers, capped by what the
base can do.

**D. Hub suggestions.** When a car with no matching profile loads, suggest the top-rated
Hub profile for that game/car/class, scaled via C.

## 4. A — Torque fingerprint

**Signal:** the core force *before* `MasterGain` / output gain (game-native units), i.e.
the same value each pipeline already feeds into normalisation. Measured once at a single
shared site in the base class after the game-specific core is computed — read-only, so it
is zero-impact for every pipeline (Pipeline Isolation rule).

**Valid frames only:** speed > 40 km/h, not off-track/kerb-spike (reuse vibration mixer
kerb flag), not in the crash-safety clamp, game status LIVE, steering |rate| below a crash
threshold.

**Stats:** streaming P50 / P95 / P99 of |core| via a fixed-bin log histogram (cheap, no
sort, 1 kHz safe). Fingerprint is "converged" after ≥ 90 s of valid frames AND ≥ 1 lap.
Keep updating with slow decay so setup changes are tracked.

**Storage — new file, not profile, not settings:** `AppData/Roaming/AcEvoFfbTuner/car-fingerprints.json`,
keyed `{game}|{carModel}` with `{ p50, p95, p99, validSeconds, laps, carClass, updatedUtc, appVersion }`.
Rationale: it is *learned measurement data*, not a user choice. It must survive profile
switches (so not in the profile) and is per car (so not in app-wide settings).

## 5. B — Character-preserving normaliser

Per car `c` with fingerprint P99ᶜ, and the reference P99ʳ = median P99 of converged cars of
the same game+class (fallback: same game; fallback: the car itself):

```
ratio_c     = P99_c / P99_ref
character   = profile.CarCharacterKeep  (0..1)   // 0 = every car equal peak, 1 = full real ratio
targetPeak  = profile.TargetPeakNm * ratio_c ^ character
targetPeak  = min(targetPeak, WheelMaxTorqueNm * profile.HeadroomFraction)   // never clip
carGain_c   = (targetPeak / WheelMaxTorqueNm) / (P99_c * MasterGain)          // fraction of full scale
```

- Applied as one multiplicative stage `CarCharacterGain` in the base pipeline, **default 1.0**,
  replacing `autoGain` only when the new mode is on → existing profiles feel identical.
- Gain changes are slewed (≤ 2 %/s) and frozen while |core| is high, so the wheel never
  steps mid-corner.
- Until a fingerprint converges: use the class reference, or 1.0 if none (= today's behaviour).
- Mutually exclusive with the legacy `AutoGain` (UI shows one or the other).
- The live Auto-Tuner's clip corrections write into the fingerprint (raise P99) instead of
  fighting the normaliser.

## 6. C — Portable Nm profiles

New **profile** fields (FfbProfile, all nullable — absent = today's behaviour):

| Field | Type | Meaning |
|---|---|---|
| `CarCharacterMode` | bool? | Enables B. |
| `TargetPeakNm` | float? | Intended reference-car peak at the rim. |
| `CarCharacterKeep` | float? (0..1, default 0.7) | How much cross-car difference to keep. |
| `HeadroomFraction` | float? (default 0.9) | Max share of base torque used. |
| `AuthoredOnNm` | float? | `WheelMaxTorqueNm` of the author's base (stamped on save/upload). |

These belong in the **profile** because they change how cars feel (AGENTS.md persistence
rule). Must round-trip via `UpdateFromPipeline` / `ApplyToPipeline` and the VM load in
`MainViewModel.Profile.cs`, and survive Save As / wizard / diagnostic saves.

**Transfer rule on load:** if `AuthoredOnNm` ≠ local `WheelMaxTorqueNm`, keep `TargetPeakNm`
(the feel) and recompute gain. If `TargetPeakNm` > local headroom, cap it and show a one-line
notice ("This profile was built on 21 Nm; peak capped to 4.9 Nm on your R5, detail preserved").
Low-force detail stages (EQ, vibration, slip) are untouched — only the core gain moves.

## 7. D — Hub suggestions

- Upload adds `TargetPeakNm`, `AuthoredOnNm`, `CarCharacterKeep` and the car's fingerprint
  (anonymous numbers only).
- On car load with no PerCar/PerCarAndTrack match: query Hub for the game+car (then class),
  sorted by rating; show a non-blocking banner "Top-rated profile for <car> (★4.6, built on
  Moza R12) — Try it". Applying uses section 6 scaling. Never auto-applies.
- Hub fingerprints can seed a car's reference before the local one converges.

## 8. UI

FFB Tuning → Overall Strength & Wheel:
- Toggle **Car Character** (replaces Auto Gain toggle when on).
- Slider **Target peak (Nm)**, slider **Keep car character 0–100 %**, slider **Headroom**.
- Readout: `This car: P99 14.2 → 4.6 Nm peak (ratio 1.12 vs GT3 median) · fingerprint 3 laps ✓`.
- Telemetry page: "Car torque league" table of all fingerprinted cars in the class.

## 9. Pipeline isolation checklist

- One new base-class property `CarCharacterGain` (default 1.0) and one read-only
  measurement tap. No game-specific branches in the base class.
- Game differences live only in the *signal* each subclass already produces; the normaliser is
  unit-agnostic (works on ratios).
- R3E: run it after the existing force-scale auto-normalisation guard; disable that guard when
  Car Character mode is on, to avoid double normalisation. LMU/AC/ACC need no special handling.

## 10. Phases

1. Fingerprint capture + storage + readout only (no output change) — ship in beta, collect data.
2. Normaliser stage + profile fields + UI; legacy AutoGain untouched.
3. Portable Nm transfer on load + Hub upload fields.
4. Hub suggestions banner.

## 11. Test plan

- Unit: histogram percentiles, gain formula (character 0 → equal peaks, 1 → exact ratios,
  headroom cap), slew limiting, profile round-trip of the new nullable fields, older profiles
  load with mode off and an identical pipeline state.
- Snapshot verification: two cars of different torque in the same class, character 0.7 →
  peak Nm ratio ≈ ratio^0.7, zero clipping frames.
- Cross-base: same profile on 5.5 Nm and 21 Nm config → matching Nm peak where possible,
  capped notice otherwise.

## 12. Open questions

- Reference choice: class median vs a user-picked "reference car"? (Proposal: median, with an
  optional override.)
- LMU car class strings are still being verified — fallback to game-level reference until then.
- Should fingerprints be uploaded by default or opt-in? (Proposal: opt-in alongside profile upload.)
