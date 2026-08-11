# RS50 TrueForce FFB Provider — Implementation Plan

**Status:** Planned — to be implemented in a new session
**Created:** 2026-08-11
**Goal:** Give the Logitech G RS50 (and G PRO) working FFB in AC EVO **without G HUB**, by implementing the TrueForce stream FFB path that mescon's Linux driver and TF4ALL (Windows) have proven.

---

## Background — why the app's force doesn't reach the wheel while driving

The RS50/G PRO expose **two independent force transports** (verified by the reverse-engineering ecosystem):

| Transport | Details | Used by |
|---|---|---|
| HID++ Feature `0x8123` fn2 | signed int16 BE motor target, set-and-hold, ~140–333 Hz, feature index 0x10 (native RS50) | Logitech's runtime, "normal" game FFB |
| **Dedicated endpoint 0x03, Interface 2 (TrueForce stream)** | 64-byte raw reports (report ID 0x01), bytes 6–9 = "cur" motor torque target, 1 kHz | **AC EVO / ACC** (SDK-native), mescon Linux driver, TF4ALL |

**While a TrueForce session is active, its "cur" force OVERRIDES both the 0x8123 path AND the DirectInput PID effects the app currently uses.**

This explains every symptom observed with the user's RS50:
- TEST FORCE works (game not streaming → DI force reaches the motor)
- Driving force dead (AC EVO's TrueForce stream owns the motor; DI updates are ignored)
- "No FFB in-game without G HUB" (the game's TrueForce session needs the wheel in PC mode — "launch G HUB once")
- HID++ settings reads go silent during driving (the wheel prioritizes the active stream)

**Conclusion: the app must write force on the same TrueForce stream channel the game uses.** The app then *is* the FFB source — no G HUB needed.

---

## Phase 0 — Quick fixes (ship first as v1.26.1)

1. **`App.xaml.cs` purge must never delete `crash.log`** — the 21:17 crash stack was lost when the update purge wiped it. Whitelist `crash.log` in `PurgeLogsFromPreviousVersion`.
   - Also check Windows Event Viewer (Application → .NET Runtime / Application Error) for the 21:17 crash — the WER report may still contain the stack.
2. **`WheelLedController` product-name matching** — it matched the Logitech USB Receiver (mouse dongle, PID 0xC548) by VID alone and wrote G29 LED garbage to it. For Logitech, require a wheel keyword in the product name (G29/G920/G923/Driving Force/RS50/G PRO).
3. **Include the uncommitted HID++ teardown hardening** (read-thread drain + exception-proof loops — already in the working tree, build clean).

---

## Phase 1 — Reference material

Local references already saved:
- `%TEMP%\kilo\proto_spec.md` — mescon protocol spec (settings + FFB transports)
- `%TEMP%\kilo\mescon_session.c` — init sequence logic
- `%TEMP%\kilo\tf4all_device.cs` — TF4ALL's C# port (TrueForceDevice)

To fetch:
- **mescon/logitech-trueforce-linux-driver**:
  - `userspace/libtrueforce/src/tf_init_data.h` — **the 68 init packets verbatim (core asset)**
  - `userspace/libtrueforce/src/stream.c` — 1 kHz pump, 13-slot rolling window, seq counter
  - `userspace/libtrueforce/src/kf.c` — filter constants
- **TF4ALL (Mhytee/Trueforce-For-All)** — working Windows C# implementation of the same (HidSharp; we use our existing Win32 HID P/Invokes instead)

---

## Phase 2 — Core provider: `LogitechTrueForceFfbProvider` (Core/FfbProviders)

- **Interface**: usage page `0xFFFD` (TrueForce stream, interface 2), 64-byte output reports, report ID `0x01`; write via `HidD_SetOutputReport` (no read channel needed for force output)
- **Init sequence**: replay the 68 packets from `tf_init_data.h`, 2 ms apart (session-local seq counter starting at 1); rewrite the type-0x0e range packet with the wheel's actual rotation (per mescon's `patch_range_packet` — prevents the 90°/2700° range resets)
- **Stream pump**: 1 kHz thread (`timeBeginPeriod(1)` infra already exists):
  - Packet layout: byte 5 = seq; **bytes 6–9 = "cur" = motor torque target (signed int16)**; byte 10 = 0 (pure force command — zero new samples)
  - Feed: the app's processed force → LSBs, **sign-inverted** (TF4ALL-verified: `FfbInvertSign = true`), IIR-smoothed (~1–3 ms), optional slew-rate limit
- **Force-only first step** — the 13-slot audio window (engine RPM haptics) is a later iteration
- **Set-and-hold**: the wheel holds the last "cur" indefinitely — safe to pause the stream for HID++ settings reads
- **Logging**: `logitech_trueforce.log` — every init packet, session state, force values, errors

---

## Phase 3 — Integration

- **Force routing**: when the TrueForce provider is active (RS50/G PRO connected), route the telemetry force to the stream **instead of** the DI constant-force effect (the stream overrides DI anyway — verified on the user's wheel)
- **Other games** (LMU, R3E, AC, non-TrueForce): keep the existing DI path
- **HID++ settings coexistence**: verify whether settings GETs still answer while *our* stream runs. If not: pause the stream ~200 ms during settings reads (safe — set-and-hold), then resume
- **UI (Wheelbase tab)**: TrueForce session status, enable toggle, force scale slider
- **Wheel test**: route TEST FORCE through the stream so the test validates the stream path

---

## Phase 4 — Test matrix (user rounds)

1. **v1.26.1** — quick fixes (crash.log purge exclusion, LED product matching) → confirm no regressions
2. **TrueForce provider** — AC EVO, **G HUB closed**: does the app's force now drive the wheel? (Expected: yes — the stream is the same channel the game uses; our writes have no PC-mode dependency. Verify.)
3. With G HUB launched once (PC mode) — game FFB vs app FFB coexistence
4. Settings reads during streaming (pause/resume behavior)
5. The 21:17 crash — have the user reproduce; crash.log now survives the purge

---

## Key risks / unknowns (resolve on hardware)

- The 68-packet init sequence is a G HUB capture (BeamNG session) — replay fidelity not guaranteed; seq counter + 2 ms pacing matter
- Whether AC EVO detects *our* session and tries to open its own (two streams = conflict → may need in-game FFB off + the app as sole streamer)
- "cur" sign convention and LSB scaling (TF4ALL defaults: negate, scale 1.0)
- HID++ settings reads during our stream (pause/resume fallback)

---

## Current state at handoff

**Shipped:**
- v1.26.0 — HID++ wheel settings fully working (real reads confirmed on the user's wheel: strength 6 Nm, rotation 1080°, desktop mode), force-path cleanup (LED exclusion, restart-dance removal, 400 ms auto-detect), HID++ refresh fix

**Uncommitted (include in v1.26.1):**
- HID++ teardown hardening (read-thread drain + exception-proof loops)

**Open items:**
- Driving-force mystery: **solved architecturally** (TrueForce stream override) — implementation is Phase 2–3 of this plan
- The 21:17 crash: cause unknown (crash.log purged) — Phase 0 fix + reproduce
