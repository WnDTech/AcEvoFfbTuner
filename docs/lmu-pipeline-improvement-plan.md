# LMU Pipeline Improvement Plan (Clean-Room, App Code Only)

> STATUS 2026-09-19: Phase B + C + D1/D2 implemented, profile-plumbed, UI-exposed,
> help- and coach-integrated. Full clean Release build 0 errors, 179/179 tests pass.
> Phase A (drive snapshots) and D3 (deferred) remain. See §5 implementation log and §6 what's left.

> Scope: **app code** in `src/` only. No profile changes. No LMUFFB code, no references to it.
> Pipeline isolation rule applies: new behavior lives in `LmuFfbPipeline` overrides or new
> virtual no-op hooks in `FfbPipeline` (EVO default unchanged). R3E/AC/ACC must not move.
> Verify every step with a snapshot + full clean build:
> `dotnet clean AcEvoFfbTuner.slnx -c Release -q 2>&1; dotnet build AcEvoFfbTuner.slnx -c Release`

## 1. Recheck of the previous 16 proposals

Each claim was re-traced to its assignment site. Corrections are folded into the plan below.

| # | Prior claim | Verdict | Evidence / correction |
|---|---|---|---|
| 1 | LMU core direction `Abs()*tanh(steer)` is wrong | SOFTENED to verify-first | `LmuFfbPipeline.cs:52-58` does discard native sign. But native `FinalFf` IS signed (`LmuSharedMemoryReader.cs:513`: `steeringTorque F64` else `ffbTorque F32`). R3E uses the same `magnitude*steerSign` pattern intentionally for its unsigned column signal with device convention `+steer = toward center` (`R3eFfbPipeline.cs:137-148`). EVO convention is opposite (`FfbGripGuard.cs:10-17`: EVO/LMU centering opposes steer, `CenteringForceMatchesSteerSign=false`). Device note says Moza positive = LEFT (`TelemetryLoop.cs:600`). Do NOT flip blindly — snapshot-verify first (Task A1). |
| 2 | Grip average misnamed, mixes axles | VERIFIED | `LmuFfbPipeline.cs:79-94` loops 0..3, calls it `frontGrip`. R3E uses front-only 0,1 (`R3eFfbPipeline.cs:155-160`). LMU `TyreGrip` IS per-wheel real (`W_GRIP_FRACT` read at `LmuSharedMemoryReader.cs:466`, injected at `TelemetryLoop.cs:540`). Split front/rear. |
| 3 | Hardcoded `120/ForceScale` | VERIFIED | `LmuFfbPipeline.cs:57`. EVO uses rolling-peak auto-norm (`FfbChannelMixer.cs:146-163`). Add LMU peak tracker. |
| 4 | `Mix()` unipolar DC bias | VERIFIED with nuance | `FfbVibrationMixer.cs:250` clamps main return to `0..1`; reader vibes are all `>=0` (`LmuSharedMemoryReader.cs:592-633`). Separate modulations (`Road ±0.3`, scrub, rear, offtrack) ARE bipolar. LMU has fixed DC-blocker (`LmuFfbPipeline.cs:213-217`, coeff `0.02`); EVO has none. Verify offset on straight snapshot, then sign it. |
| 5 | Damping wall-clock dt | VERIFIED | `FfbDamping.cs:64-67` uses `DateTimeOffset.UtcNow`, clamped 1-50ms. Pipelines use fixed `1/250` (LMU), `1/60` (EVO gear), `1/333` (mixer `TickSeconds`). Pass deterministic dt. |
| 6 | Damping cap zero at center | VERIFIED | `FfbDamping.cs:121-124`: `maxDamp = |force|*0.25`. At center this is 0. Add small absolute cap. |
| 7 | `GearSpikeThreshold` dead | VERIFIED, scope widened | Defined in all 5 pipelines (`Lmu:20`, `R3e:49`, `Ac:26`, `Acc:22`) + profile plumbing (`FfbProfile.cs:168-412,1200`), but zero reads in any `Process()`. `FfbCoachService.cs:147` calls it RPM threshold; `ApplyGearShiftFilter` uses gear+force-delta only. Fix globally or remove + migrate. |
| 8 | SlipEnhancer averaging dilution | VERIFIED | `FfbSlipEnhancer.cs:38-55`: `count++` unconditional, divides active sum by all wheels. Average only active wheels. LMU never calls it (bypass confirmed). |
| 9 | Scrub/rear need alternate inputs | CORRECTED | LMU `Mz=[totalForce,totalForce,0,0]` (`LmuSharedMemoryReader.cs:663`) so front scrub HAS signal, rear Mz is dead zero. `SlipAngle=[slipA x4]` single bicycle value (`:516,:662`) — front/rear identical, cannot separate axles from it. `Fy/Fx` per-wheel real-or-synth (`:551-556`), `SlipRatio` per-wheel real (`:558-571`), `TyreGrip` real. Rear warning must use `Fy` derivative + yaw, not `Mz`/slip-angle split. |
| 10 | Yaw onset from `YawAccel` | CORRECTED order | `FfbRawData.YawAccel` exists but LMU never populates it (`TelemetryLoop.cs:513-546`: only R3E sets it at `:525`). Mixer falls back to differentiating yaw rate (`FfbVibrationMixer.cs:331-333`) — noisy. First populate `YawAccel` for LMU (smoothed derivative of `rotY`), then add onset hook. |
| 11 | ABS/TC flags dead for LMU | VERIFIED | `MapRawData` reads `AbsInAction/TcinAction` from physics (`TelemetryLoop.cs:1230,1234`); LMU-built physics leaves them 0. `TcActiveGfx` from graphics (`:1233`) also 0 for LMU (only R3E injected at `:523`). Synthesize from `SlipRatio`+brake/throttle first. |
| 12 | Curb/road scale + double layer | CORRECTED, widened | Mixer fallback `|suspDelta|>0.002 → *10`, `road *150` (`FfbVibrationMixer.cs:228-244`) was EVO-tuned. LMU reader ALREADY synthesizes `kerbVib=maxSuspDelta*400*0.3` (`:592-599`) + impact events (`:614-618`) and `roadVib=vertDeflDelta*60` (`:601-610`). Both layers stack. Calibrate mixer gains for deflection-meter units or gate one layer for LMU. |
| 13 | dt/rate mismatch | VERIFIED, widened | `SetSampleRate` exists (`FfbEqualizer.cs:53`, default `333`) but is never called outside tests. LMU hot path is 250Hz (reader comment), `deltaTime=1/250` (LMU) vs `1/60` (EVO gear) vs `TickSeconds=1/333` (mixer/LFE/wet/tyre-condition). Add per-pipeline `PhysicsDt` + call `SetSampleRate`. |
| 14 | Noise floor inconsistency | VERIFIED | LMU `1.0+(1-speed/10)*0.5` (`LmuFfbPipeline.cs:177-179`) vs base `1.0+(1-speed/10)*2.0` (`FfbPipeline.cs:359-361`). Unify behind property, keep per-game defaults. |
| 15 | LMU bypass list | VERIFIED | `LmuFfbPipeline.Process` calls none of: `ChannelMixer`, `SlipEnhancer`, `DynamicEffects`, `TyreFlex`, `GripGuard`, `CrashDetector`, `TyreCondition`, `WetWeather`, `LutCurve`. R3E bypass is documented intentional (`R3eFfbPipeline.cs:106-112`); LMU bypass has no such justification recorded. Wire selectively (P1). |
| 16 | Scalar brake boost | VERIFIED | LMU `1+0.4*brakeInput` (`LmuFfbPipeline.cs:70-77`); R3E has multiplicative + additive heaviness (`R3eFfbPipeline.cs:120-135,192-199`). Use load shift front/rear. |

## 2. Plan

### Phase A — Verify + instrument (no feel change)

- [ ] **A1. Sign-convention snapshot.** Same car/track, steady sweep left/right: log `RawFinalFf`, `SteerAngle`, `CoreForce`, `MainForce`. Confirm whether native `FinalFf` sign already opposes steer (EVO convention) or needs synthesis. Check against `GripGuard.CenteringForceMatchesSteerSign=false` and Moza positive=LEFT note. Decision gate for A2. Files: `LmuSharedMemoryReader.cs:513`, `LmuFfbPipeline.cs:52-58`, `FfbGripGuard.cs:10-17`.
- [ ] **A2. Telemetry coverage snapshot.** Confirm per-field liveness for LMU: `Mz` (expect front=column, rear=0), `SlipAngle` (expect identical x4), `SlipRatio`/`Fy`/`WheelLoad`/`TyreGrip` (expect live), `YawAccel`/`AbsInAction`/`TcinAction`/`TcActiveGfx` (expect 0), `SuspensionTravel` units (deflection m), `Kerb/Slip/Road/AbsVibrations` (expect 0..1 envelopes). Uses CSV columns `Time,SpeedKmh,SteerAngle,ForceOut,RawFF,MzFront,FxFront,FyFront,Slip,Damping`. No code change; determines order of Phase B.
- [ ] **A3. Straight-line DC check.** Straight, constant speed: inspect `PostDynamicForce`/`DetailForce` mean. B4's adaptive blocker is already in (numerically identical to legacy at defaults); if mean > ~0.02 with vibration active, the remaining half of B4 — signing the `Mix()` envelope by core direction — is required.

### Phase B — Correctness (LMU-only, zero-impact defaults)

- [x] **B1. Front/rear grip split.** `LmuFfbPipeline.cs:79-94`: compute `frontGripAvg` (0,1) and `rearGripAvg` (2,3) load-weighted. Front scales core down; rear feeds detail warning only. Keep `hasGrip`/`load>50` guards. Acceptance: front lockup lightens, rear slide does not lighten core.
- [x] **B2. Column-force auto-norm.** Add `_lmuPeak` (decay 0.997, `AutoMinPeak`) mirroring `FfbChannelMixer.cs:146-163`. `effectiveScale = Max(manualScale, peak/target)`. Default preserves current feel; Hypercar fast corners stop hard-clipping.
- [x] **B3. Deterministic dt.** Add `PhysicsDt` (LMU 1/250) and pass into `Damping.Apply()` (new overload, old signature delegates). Call `Equalizer.SetSampleRate(1/PhysicsDt)`. Replace wall-clock in LMU path; EVO untouched. Files: `FfbDamping.cs:63`, `FfbEqualizer.cs:53`, `LmuFfbPipeline.cs:114`, `FfbPipeline.cs:346`.
- [x] **B4. Bipolar detail vibration (gated on A3).** Sign main `vibration` envelope by core direction (same pattern as `AbsForceModulation` at `LmuFfbPipeline.cs:128-135`) or remove DC via adaptive blocker `1-exp(-dt*5)` replacing fixed `0.02` (`LmuFfbPipeline.cs:213-217`). Acceptance: straight mean ~0.
- [x] **B5. Damping center cap.** `FfbDamping.cs:121`: `maxDamp = Max(|force|*0.25, 0.03)`. Acceptance: no straight weave introduced, off-center feel unchanged.
- [x] **B6. `GearSpikeThreshold` decision (global).** Either wire as RPM spike gate in `ApplyGearShiftFilter` (`FfbPipeline.cs:518-543`) or delete property + migrate `FfbProfile.cs:1200,1217` + coach text (`FfbCoachService.cs:147`). No silent dead slider. LMU + R3E + AC + ACC share the outcome.
- [x] **B7. Noise-floor unification.** Single property (e.g. `LowSpeedNoiseBoost`) with LMU default preserving `0.5x` and base preserving `2.0x`. Files: `LmuFfbPipeline.cs:177-179`, `FfbPipeline.cs:359-363`.

### Phase C — Feel from our own fields (LMU overrides only)

Order matters: C1 first (populates fields), then C2–C5 consume them.

- [x] **C1. Populate LMU dynamics fields.** `TelemetryLoop.cs:537-546`: compute smoothed `YawAccel` from `rotY` derivative (EMA, clamp, NaN guard; same fail-safe pattern as `FfbPipeline.cs:482-492`); synthesize `AbsInAction`/lockup flag from per-wheel `SlipRatio` + `BrakeInput`, and spin flag from `SlipRatio` + throttle (thresholds as new LMU-only properties, default off/0 until tuned). `TyreContactNormal` stays zero for LMU — mixer fallback path is the live one.
- [x] **C2. Wire slip lightening.** Call existing `SlipEnhancer.Apply()` in LMU detail path with front-only config; fix `FfbSlipEnhancer.cs:38-55` to average over above-threshold wheels only. Uses `SlipAngle` (bicycle value — acceptable for front lightening) + `PeakSlipAngle` curve already in code (`:72-96`). Acceptance: pushing wide lightens wheel progressively, not on/off.
- [x] **C3. Rear warning from live rear signals.** LMU override using `Fy[2,3]` derivative + `YawAccel` intensity + `SlipRatio[2,3]` gate. Do NOT use rear `Mz` (always 0) or front/rear `SlipAngle` split (identical x4). Implement as override of existing rear-modulation computation or new `ApplyRearSlipOverride` no-op hook in base. Cap and EMA-match existing rear path (`FfbVibrationMixer.cs:301-377`).
- [x] **C4. Yaw-onset impulse hook.** New `protected virtual ApplyYawOnset(detail, raw)` in `FfbPipeline` (no-op) + LMU override: thresholded `YawAccel` kick with exp decay + speed gate. Threshold/decay/gain default 0 (inert until tuned). Rationale: `FfbDynamicEffects` yaw uses heavily smoothed rate (`0.8/0.2`, `±0.15` at `FfbDynamicEffects.cs:39-60`) — onset needs acceleration, not rate.
- [x] **C5. Curb/road calibration (partial).** DONE: `SuspRoadScale` + `UseMixerSuspRoad` gate on the mixer road layer at the consumption site. DEFERRED to A2: kerb-channel rescale — the mixer `10x/150x` + `0.002` EVO-tuned constants need snapshot-measured deflection deltas, not guesses. Files: `FfbVibrationMixer.cs:201-244`, `LmuSharedMemoryReader.cs:592-633`.
- [x] **C6. Load-based brake transfer.** Replace scalar `1+0.4*bi` with front-load-proportional weighting using `WheelLoad[0,1]` vs `[2,3]` shift under braking. Keep same slider, same total gain at default so straight-line braking feel does not jump.

### Phase D — Headroom / stability (suggest, don't auto-change)

- [x] **D1. Clip headroom telemetry + UI hint.** DONE: `PeakHold` + `ClipRate` in `FfbProcessedData`, tracked in `LmuFfbPipeline` (decay 0.999, 333-frame window); Telemetry profiler header shows live Peak (green <0.8, yellow <0.95, red above) plus a headroom hint line — red "Sustained clipping — raise ForceScale / lower MasterGain for 20-30% headroom" when pipeline `ClipRate > 5%` or session average clips, yellow near full scale, grey "Headroom OK" otherwise. Suggestion only, no auto-gain. Threaded `MainViewModel.Telemetry.cs` → `MainWindow.UpdateProfiler` → `TelemetryPage` (optional trailing params, all other callers unaffected). Files: `Models/FfbData.cs`, `Views/Pages/TelemetryPage.xaml[.cs]`, `Views/MainWindow.xaml.cs`.
- [x] **D2. Straight-line stabilizer.** `if (speed>80 && |steer|<blendZone) viscous *= 1+straightGain`, default `straightGain=0`. Behind existing `Damping` floors (`MinViscous 0.04`). LMU first, EVO opt-in later.
- [ ] **D3. Selective stage wiring (deferred).** Evaluate `GripGuard` (post-peak cap + trail), `TyreFlex`, `DynamicEffects` (G/susp with LMU `AccG`/deflection), `LutCurve`/`Compression` for LMU core — one stage at a time, each behind its own enable + snapshot A/B. Explicitly NOT in this plan's build; listed so the bypass (item 15) is closed deliberately, not accidentally.

## 3. Acceptance per task

- Full clean build passes; unit tests in `src/AcEvoFfbTuner.Tests` pass.
- Snapshot A/B on same car/track shows intended delta and no EVO/R3E/AC/ACC delta (pipelines isolated; base hooks no-op at default).
- No new allocations or logging on hot path (250Hz LMU); follow existing `Reset()` patterns (`LmuFfbPipeline.cs:238-253`).

## 5. Implementation log (2026-09-19, beyond the DSP tasks above)

All app code, all defaults preserve current feel, EVO/R3E/AC/ACC untouched at defaults.
Backup: `backups/2026-09-19/src`. Full clean Release build 0 errors, 179/179 tests pass.

- Profile plumbing (`FfbProfile.cs`): 19 new `SlipConfig` fields + 4 `DampingConfig`
  fields with sanitize coverage, LMU-gated apply, pipeline-isolation-safe `FromPipeline`,
  old-profile LMU damping-floor re-assert (0.03). `EngineRpm` added to `FfbRawData` + mapped.
- Tuning UI (`FfbTuningPage.xaml`, `MainViewModel[.Tuning/.Profile].cs`): 6 LMU sliders
  (Rear Slip Warning, Rear Grip Drop, Onset Gain/Threshold, Brake Load Transfer, Susp Road Scale),
  3 LMU checkboxes (Mixer Road Layer, Synth ABS/TC), 2 damping sliders (Center Authority Floor,
  Straight Stability). New params also flow through profile load/save/apply/sync.
- Help (`Help/HelpCatalog.cs`): 7 LMU entries, 2 damping entries, new LMU Telemetry
  Synthesis section.
- Coach (`FfbCoachService.cs`): knowledge-table rows, exact-name list, snapshot JSON,
  aliases, getter + setter for `RearFyScale`, `RearGripDetailGain`, `YawOnsetGain/Threshold/Decay/Max`,
  `SuspRoadScale`, `UseMixerSuspRoad`, `BrakeLoadBias`, `SynthYaw/Abs/TcEnabled`,
  `CenterDampFloor`, `StraightGain`. Coach writes flow via `ApplyToPipeline`.
- Profile/UI-only (no coach): `RearSlipAngleThreshold`, `RearYawThreshold`, `RearMaxOutput`,
  lockup/spin thresholds, `AutoMinPeak/Target`, `DcBlockCutoffHz`.

## 6. What's left

Requires driving (cannot be done in code):
1. **A1** — sign-convention snapshot (decision gate; no flip until verified).
2. **A2** — telemetry coverage snapshot (sets kerb-constant retune + ABS/TC thresholds).
3. **A3** — straight-line DC mean (decides bipolar envelope signing).
4. Tuning order after snapshots: Rear Slip Warning → Onset Threshold down from 2 →
   Brake Load Transfer → Synth ABS/TC (stay off until `-0.12` confirmed).
   The new Peak/headroom readout tells you when to stop raising gains.

Deferred code:
5. **D3** — selective `GripGuard`/`TyreFlex`/`DynamicEffects`/`LutCurve`/`Compression`
   wiring for LMU, one stage per snapshot A/B.

## 7. What was deliberately excluded

- Any use of third-party FFB code or tuning values.
- vJoy/HID-hijack architectures; our `IFFBProvider` + DirectInput path stays.
- Auto-changing user gains; suggestions only until the LMU baseline is trusted.
- Blind sign flip (gated on A1), blind scale constants (gated on A2/A3 snapshots).
