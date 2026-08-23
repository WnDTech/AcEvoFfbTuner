# Fake RS50 — Virtual TrueForce Wheel (dev-only test rig)

**Status:** IN PROGRESS — Phase 0 done, Phase 1 driver builds/installs/loads; VHF setup blocked on degraded UMDF host state → reboot + `post-reboot-verify.ps1` first (see session log below)
**Goal:** Create a virtual Logitech RS50 (VID_046D, PID_C276) HID device on the dev machine so EVO/G HUB initializes TrueForce against it and we capture the exact protocol traffic — no tester, no usbpcap on his machine, no BIOS drama.

---

## Why this works (the key insight)

The VHF (Microsoft Virtual HID Framework) device traffic is **kernel-internal** — it never touches the USB bus, so usbpcap would NOT see it. **But our fake-wheel app sees EVERYTHING sent to the device** — every HID++ request and every TrueForce stream packet arrives in the app. **The app IS the capture** — better than a bus capture, byte-for-byte, with no third-party tooling.

Flow: AC EVO → Logitech SDK → G HUB (installed on dev machine) → opens the fake RS50's HID interfaces → sends HID++ queries + the TrueForce stream → our app logs everything and answers from the emulated RS50 feature map.

## What we already have (from the tester's hardware)

1. **Feature map** (from `logitech_hidpp.log`, successful connect):
   `rotation=0x18 profile=0x17 trueforce=0x19 damping=0x14 oled=0x12` (plus the standard root features 0x0000-0x0003, 0x0005-0x0007 etc.)
2. **Settings values**: strength=8.0 Nm, rotation=1080°, mode=onboard slot 5, TrueForce level 0%, damping 0%
3. **The 68-packet G HUB stream init sequence** we replay in `LogitechTrueForceProvider` (the byte-exact init including the 0x0e range patch)
4. **The OLED descriptor readback**: fn0 = 10 layouts, fn1 (layout J) = `09 0A 13 0A 13 0A`
5. **The device topology**: MI_00 (HID++ control), MI_01 (HID++), MI_02 (0xFFFD stream interface, 64-byte reports), OLED on the base
6. **The SDKs in the project root**: `LogitechSteeringWheelSDK_8.75.30.zip`, `LED_SDK_9.00.zip` (official semantics reference)
7. **The mescon thread knowledge** (issues #20/#62): the stream format (cur/0x8000, byte10 count, 0x0d audio pairing, teardown 67+68), SW-ID conventions (0x0A/0x0B), the no-arm semantics

## Architecture

```
AC EVO ──(Logitech SDK)──► G HUB service ──► VHF virtual RS50 device (VID_046D PID_C276)
                                                │
                                                ▼
                                      FakeWheelApp (C#/WinForms)
                                      ├─ HID++ responder (answers from the feature map)
                                      ├─ Stream sink (logs every packet byte-for-byte)
                                      ├─ Optional virtual steering axis (so EVO has input)
                                      └─ Live UI (RS50 image, counters, raw log)
```

## Repository layout (new session)

```
tools/FakeWheel/
  PLAN.md                  ← copy of this plan (or link)
  driver/                  ← VHF UMDF client driver (C++, WDK)
    vhf_sample/            ← based on Microsoft's VirtualHidDevice sample
    fake_rs50.inf
  app/                     ← FakeWheelApp (C# .NET 8, WinForms)
    FakeWheelApp.csproj
    Program.cs             ← entry + UI
    HidppResponder.cs      ← the HID++ protocol emulation
    FeatureMap.cs          ← the RS50 feature table + values
    StreamSink.cs          ← the TrueForce stream capture/log
    VirtualAxis.cs         ← optional steering input axis
  capture-analysis/        ← log parsers (reuse the ETL/pcap parsing style)
```

## Phases

### Phase 0 — Setup & assets (0.5–1 day)
- [ ] Install **WDK** + VS driver workload on the dev machine (test-signing or debugger-attached for the client driver)
- [ ] Get Microsoft's **VirtualHidDevice sample** (VHF): `https://github.com/microsoft/Windows-driver-samples` → `general/VhfSample`
- [ ] Download the **RS50 images** (official product gallery):
  - Hero: `https://resource.logitechg.com/content/dam/gaming/en/products/rs50-base-pdp/gallery/rs50-base-wheel-hub-front-angle-gallery-1.png`
  - Alt: `.../rs50-base-wheel-hub-3qtr-front-right-angle-gallery-4.png`, `.../rs50-base-wheel-hub-back-angle-gallery-8.png` (same path, swap the filename)
  - (Strip the `c_fill,q_auto,f_auto,dpr_1.0/d_transparent.gif/` CDN prefix for the raw PNG; trim/transparent-background in the app)
- [ ] Extract the FULL feature list + response bytes from the tester's packs (grep `logitech_hidpp.log` for every `TX/RX` of the connect flow) — we need the exact response payloads for: root feature discovery, feature count/indices, each wheel feature's read responses

### Phase 1 — VHF skeleton (1–2 days)
- [ ] VHF UMDF driver: register a virtual HID device with **VID_046D, PID_C276**, product string "Logitech G HUB RS50 (USB)", serial from the tester's device
- [ ] Report descriptor v1: the **HID++ control collection** (usage page 0xFF00, 20-byte reports) — enough for G HUB to start talking
- [ ] Verify: Device Manager shows the fake; G HUB's device list reacts (it should try HID++ feature discovery)

### Phase 2 — HID++ responder (2–3 days)
- [ ] Implement the HID++ short-message protocol: report 0x11, device index 0xFF, SW-ID echo (0x0A), the 20-byte frame
- [ ] Answer feature discovery from the real map: root (0x0000) fn0/fn1/fn2, feature set (0x0001/0x0002), and the wheel features at their real indices (0x12=OLED, 0x14=damping, 0x17=profile, 0x18=rotation, 0x19=trueforce, plus 0x8136 strength — need its index from the logs)
- [ ] Answer the settings reads (fn1): strength 8.0 Nm, rotation 1080°, TF level 0, damping 0, profile mode = onboard slot 5
- [ ] Accept the SETs (fn2) and store state (strength/rotation/profile slot/OLED enable)
- [ ] Answer OLED fn0/fn1 with the captured descriptor data
- [ ] **Milestone:** G HUB shows the fake RS50 in its device list + settings UI reads 8 Nm / 1080°

### Phase 3 — Stream interface + EVO capture (2–3 days)
- [ ] Add the **0xFFFD stream collection** (64-byte IN/OUT reports) to the descriptor (or a second VHF instance)
- [ ] Log EVERY received stream byte to `stream_capture.bin` + a timestamped hex log
- [ ] Launch **AC EVO** (dev machine, G HUB running) → EVO should detect the RS50 and initialize TrueForce
- [ ] Capture the **init sequence** — compare against our 68-packet replay (the ground truth!)
- [ ] Feed EVO some input (the virtual steering axis, or EVO's AI) so it produces force → capture the **force stream** with TF gain on/off, TF effects on/off
- [ ] **Milestone:** the definitive "what does EVO send to the wheel" capture, on our machine

### Phase 4 — Experiments (as needed)
- [ ] The **contention proof**: does EVO re-init its session mid-stream? What does EVO do when the fake wheel stops responding? (the whine mechanism)
- [ ] The **audio window**: with the fake (no audio endpoint) does EVO send 0x0d-paired samples? (mescon's byte-10/0x0d finding)
- [ ] The **range push** timing: when does EVO push 0x0e? (init-only vs between START/STOP)
- [ ] Feed the captured protocol into the real app's knowledge base (our provider's init can be byte-validated against EVO's actual stream)

## Key technical notes

1. **VHF device topology**: VHF creates one HID device per instance — EVO/G HUB probably only need (a) the HID++ control collection and (b) the 0xFFFD stream collection. Start with ONE device with BOTH collections in one descriptor; if G HUB demands separate interfaces, run multiple VHF instances with the same VID/PID.
2. **Driver signing**: dev machine only — enable test signing (`bcdedit /set testsigning on`) or use the debugger-attached flow. Never ships anywhere.
3. **G HUB recognition risk**: G HUB may require the exact composite topology or the audio interface (MI_03) which VHF cannot fake. The fallback: skip G HUB recognition — connect to the SDK differently (the LG Steering Wheel SDK needs G HUB; if G HUB won't adopt the fake, the EVO path dies) — **this is the biggest unknown; Phase 2's milestone is the go/no-go gate**.
4. **The steering axis**: a HID collection with a rotation axis (usage page 0x01, wheel/rotation usage) lets EVO read input and drive force; we can wiggle it programmatically.
5. **Everything the fake receives = the capture.** No usbpcap needed; the app's log is the deliverable.

## Session-start checklist (new session)

1. Read this plan (`plans/fake-rs50-virtual-wheel.md`).
2. Phase 0: WDK install + fetch the VhfSample + RS50 images (URLs above).
3. Extract the full connect-flow TX/RX from the tester's `logitech_hidpp.log` packs (in `C:\Users\paul_\AppData\Local\Temp\kilo\diag_*` or the User Logs) — build `FeatureMap.cs` from the real bytes.
4. Build the VHF skeleton (Phase 1) and reach the Phase 2 milestone before touching EVO.

---

## Session log — 2026-08-17 (day session, post-reboot)

### VERDICT: UMDF is exhausted on this machine — the fake wheel needs a kernel (KMDF) driver

Both supported user-mode HID device technologies are broken at the OS level on this
Windows 11 build:

**1. VHF (Virtual HID Framework) — UMDF client — DEAD.**
`WdfIoTargetOpen(WdfIoTargetOpenLocalTargetByFile)` requires the reflector's
`CreateWdfFile` (host opens the reflector control device), which returns
INVALID_HANDLE_VALUE on this machine for EVERY configuration tried:
- INF fixed correctly (the missing pieces, verified: `Include = hidvhf.inf` /
  `Needs = vhfservice.NT(.Services)`, the `vhf` LowerFilters AddReg, and
  **`UmdfDispatcher = FileHandle`** — these made the OPEN succeed, previously
  it failed 0x80070001)
- file name variants: NULL, `L"FakeRs50"`, `L"ROOT#SAMPLE#0000"` — all → NULL handle
- file-object config with NULL and with real callbacks
- WDF 2.15 and 2.33 (the WDK's `UmdfVersion` property)
- The OSR community hit the identical wall (community.osr.com/t/59116, Oct 2024,
  unresolved). All real-world VHF deployments (SoftU2F, WinUHid, …) are KMDF.

**2. HID minidriver (mshidumdf) — DEAD on this build.**
The inbox `mshidumdf.sys` is a deprecated HID-miniport stub with NO export table
(dumpbin-verified; imports `HidRegisterMinidriver` from hidclass.sys). PnP refuses
to load it as a function driver: `STATUS_DRIVER_ENTRYPOINT_NOT_FOUND` (0xC00000B9),
post-reboot and pristine. The vhidmini2 sample pattern is broken on this build.

**3. Conclusion + the kernel pivot (the recommended next step):**
Port the existing code to a KMDF driver — either:
- **KMDF VHF client** (SoftU2F pattern): `VHF_CONFIG_INIT` takes
  `WdfDeviceWdmGetDeviceObject(device)` instead of a file handle — no reflector,
  no handle. All the descriptor/responder code (`fake_rs50.h` / responder in
  `fake_rs50.c`) ports mechanically. VHF descriptor constraints still apply
  (single collection per VhfCreate, 0xFF00 page, 8-bit usages) → 3 VhfCreate
  calls, one per HID++ collection.
- **KMDF HID minidriver** (vhidmini2 kmdf variant): our driver becomes the
  function driver — no mshidumdf, real 0xFF43 multi-collection descriptor.
Loading kernel drivers requires test signing → **Secure Boot must be disabled
(BIOS)** + `bcdedit /set testsigning on` + trust the WDK test cert (all staged in
`tools/FakeWheel/enable-testing.ps1`). Secure Boot is confirmed ON
(`UEFISecureBootEnabled=1`). Reversible.

### What was proven along the way (all staged, builds/signs/installs)
- **`tools/FakeWheel/driver/fake_rs50/`** — VHF UMDF client; the INF is now
  CORRECT for VHF (hidvhf.inf + vhf filter + FileHandle dispatcher) — only the
  reflector handle blocks it.
- **`tools/FakeWheel/driver/fake_rs50mini/`** — full HID minidriver port
  (3-collection 0xFF43 descriptor, HID++ responder, capture log) — builds/signs,
  blocked only by the broken inbox mshidumdf.
- HID++ responder logic is final and verified against the tester's wire format;
  the capture log (`C:\Windows\Temp\FakeRs50.log`) design is in both drivers.
- The app (`FakeWheelApp`) + the provider patch notes (page filter) are ready.

### Next session (kernel pivot, needs Secure Boot OFF)
1. BIOS: disable Secure Boot; `bcdedit /set testsigning on` + import
   `FakeRs50.cer` (`tools/FakeWheel/enable-testing.ps1`), reboot.
2. Port `fake_rs50.c` to KMDF (copy + change EvtDeviceAdd: no file-object config,
   no interface, `VHF_CONFIG_INIT(…, WdfDeviceWdmGetDeviceObject(device), …)`,
   VhfCreate in EvtDeviceAdd). Build with the KMDF toolset
   (`WindowsKernelModeDriver10.0`) — swap the vcxproj toolset and
   `DriverType=KMDF`.
3. Install (devcon, root\FakeRs50), verify 3 VHF children, then the G HUB test.
4. If G HUB adopts: patch `LogitechHidppWheelProvider` page filter
   (0xFF43 → also accept 0xFF00) and run the app's HID++ connect against the fake.

### What got done
- **Toolchain**: WDK 10.0.26100 (winget) + Windows 11 SDK 26100 (VS installer component; the winget WDK alone left the SDK headers broken — `shared` had 13 files) + VS 2022 `Component.Microsoft.Windows.DriverKit` (installed via `setup.exe modify` — the installPath MUST be quoted as ONE `-ArgumentList` string, and the elevated launch needs UAC approval; `--passive` refuses non-elevated with exit 5007). MSB8040 spectre error → build with `/p:SpectreMitigation=false`. INF verification DLL missing → `/p:SkipPackageVerification=true`. inf2cat rejects postdated DriverVer → pinned `<DateStamp>01/01/2025</DateStamp><TimeStamp>00.00.00.000</TimeStamp>` on the Inf item.
- **Reference sample**: `general/VhfSample` is GONE from the samples repo (checked main, win11-22h2, 99498 tags). The actual VHF-era sample is **`hid/vhidmini2`** (UMDF2 HID minidriver — useful but it is NOT VHF). The working UMDF2 VHF pattern came from **Microsoft/DMF `Dmf_VirtualHidDeviceVhf.c`** (open-by-file) and **SoftU2F-Win** (descriptor shape).
- **`tools/FakeWheel/`** is fully scaffolded: `driver/fake_rs50/` (UMDF2 VHF client), `app/` (FakeWheelApp, builds), `capture-analysis/rs50_protocol_notes.md` (exact wire format + response bytes), `assets/rs50/` (5 official images).
- **Driver**: builds, signs (WDKTestCert, imported into LocalMachine Root + TrustedPublisher), installs (root\FakeRs50, oem51.inf), the UMDF host loads it and the device node starts OK. Root-enumerated INF must use the wudfrd.inf Include/Needs mechanism (template at `...\Common7\IDE\Extensions\WDK\ProjectTemplates\Windows Drivers\WDF\UMDF2\UMDF.inf`) and `AddService=WUDFRd,0x000001fa` (the 0x2 ASSOC flag or setupapi fails with 0xe0000219).
- **Protocol knowledge banked** (rs50_protocol_notes.md): wire format (short 0x10 7-byte, response 0x12 64-byte broadcast on all three report ids, `(fn<<4)|0x0A`), all feature indices, all fn1 payloads (strength LE Nm×8191.875 → FF FF = 8Nm, rotation BE, profile param0, TF/damping LE ×655.35, OLED `09 0A 13 0A 13 0A`), SET semantics.

### VHF constraints discovered (VhfCreate 0xC0070001 = invalid descriptor per vhf.sys)
1. **16-bit USAGE items (`0x0A ...`) are rejected** — use 8-bit (`0x09`) usages only.
2. **Most usage pages are rejected when report IDs are used** — probed: 0xFF43, 0x0001, 0x0005, 0x0007, 0x000C, 0xFF42, 0xFF01, 0xF100, 0xFE43, 0xFF40 ALL fail; **0xFF00 and 0xF1D0 pass**. Without report IDs 0xFF43 passes.
3. **More than one top-level collection is rejected** — single collection per VhfCreate only. → Architecture: **three VHF devices** (one per HID++ collection: short 7B rid 0x10 / long 20B rid 0x11 / very-long 64B rid 0x12), all page 0xFF00. The 3-device loop code was written and tested as far as the open (see next bullet) — the current `fake_rs50.h/.c` are the single-device step-5 reconstruction (re-apply the loop diff from the notes in the code comments).

### The open blocker (post-reboot verification pending)
- `WdfIoTargetOpen(WdfIoTargetOpenLocalTargetByFile, NULL)` (DMF pattern, ioTarget parented to device, called from a retry timer in SelfManagedIoInit) **worked early in the session** (VhfCreate-era traces) then **degraded to `0x80070001` (ERROR_INVALID_FUNCTION) for every build** — identical code included. A plain `CreateFileW` on a device interface works but vhf.sys rejects that handle kind at VhfCreate (0xC0070001). Conclusion: the UMDF host/reflector state degraded over ~35 installs; **reboot the machine, then run `tools/FakeWheel/post-reboot-verify.ps1` (elevated)** and check the `RESULT:` line + `C:\Windows\Temp\FakeRs50.trace` for `VhfSetup=OK`.
- If open-by-file works post-reboot: re-apply the 3-device loop, install, and go straight to the **G HUB adoption test** (the Phase 2 milestone).
- If it still fails post-reboot: try the WDK's own UMDF2 template project (fresh `WDF\UMDF2` VS template) with the identical VHF snippet to isolate whether it's our INF/driver or the machine.

### Next-session commands
```
# build (Release x64; spectre + INF verify disabled for this toolchain)
MSBuild tools\FakeWheel\driver\fake_rs50\fake_rs50.vcxproj /p:Configuration=Release /p:Platform=x64 /p:SpectreMitigation=false /p:SkipPackageVerification=true /p:WDKContentRoot="C:\Program Files (x86)\Windows Kits\10\" /p:WDKBuildFolder=10.0.26100.0 /p:KitsRoot10="C:\Program Files (x86)\Windows Kits\10\" /v:m /nologo
# install + verify (elevated)
tools\FakeWheel\post-reboot-verify.ps1        # first thing after reboot
tools\FakeWheel\install-driver.ps1            # subsequent iterations
# tail the capture
C:\Windows\Temp\FakeRs50.log   (driver byte log)  |  FakeWheelApp (log tail + device presence)
# app page filter: LogitechHidppWheelProvider matches page 0xFF43 for reads — fake uses 0xFF00 → patch provider to accept 0xFF00 (or run a second enum pass)
```
- Test signing: NOT needed for UMDF2 (user-mode driver) — cert trust in Root/TrustedPublisher suffices. bcdedit was never successfully set (Secure Boot may refuse).
- G HUB on this machine is running with real Logitech gear (G203, PRO X headset) — the fake will appear alongside; the milestone is G HUB showing "RS50" in devices or probing HID++ on our VID_046D/PID_C276 interfaces.
