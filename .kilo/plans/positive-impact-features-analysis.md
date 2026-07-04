# Positive-Impact Feature Analysis

Comprehensive analysis of high-value features derived from a deep codebase audit.
Each feature is rated on **Impact** (1–5), **Effort** (1–5),
and **Synergy** with existing code.

---

## Quick Wins (Low Effort, High Impact)

### Q1. Profile Notes & Tags

| Impact | Effort | Synergy |
|--------|--------|---------|
| 4      | 1      | 5       |

**What:** Add a user-editable `Notes` field to `FfbProfile` and a `Tags` (list of strings)
field. Display a multi-line text box + tag editor on the `ProfilesPage` detail panel.

**Why:** Users save profiles for different cars, tracks, feels. Without notes they rely
on naming conventions. This is the single most requested QoL feature in sim tuning apps.

**Work:**
- `FfbProfile.cs` — add `Notes` (string) + `Tags` (List<string>) to profile model
- `ProfilesPage.xaml` — add a text box + tag chip input to the right-side detail panel
- `ProfilesPage.xaml.cs` — bind save/load
- `FfbProfile.cs:ApplyToPipeline/UpdateFromPipeline` — no changes needed (display only)

**Files to modify:** 3 (FfbProfile.cs, ProfilesPage.xaml, ProfilesPage.xaml.cs)

---

### Q2. Profile Quick A/B Toggle

| Impact | Effort | Synergy |
|--------|--------|---------|
| 5      | 1      | 5       |

**What:** Bind a hotkey (e.g. Ctrl+Shift+P) or a dashboard button to toggle between two
profile "slots". Slot A is the current profile. Slot B is a secondary profile. Toggling
applies the other profile to the pipeline instantly.

**Why:** This is the #1 workflow tool for tuners. Dial in two settings, flip between them
mid-lap, feel the difference immediately. No menus, no page navigation.

**Work:**
- `MainViewModel.cs` — add `ProfileSlotA`, `ProfileSlotB` properties, `ToggleProfileSlot`
  command that applies the alternate profile
- `HomePage.xaml` — add a "A/B" toggle button in the dashboard next to the profile name
- `FfbDeviceManager.cs` or `MainWindow.xaml.cs` — register Ctrl+Shift+P global hotkey
- `ProfileManager.cs` — add `SetSlotA`/`SetSlotB` convenience methods

**Files to modify:** 4–5

---

### Q3. Snapshot Annotations

| Impact | Effort | Synergy |
|--------|--------|---------|
| 3      | 1      | 4       |

**What:** Add a `Notes` field to `TelemetrySnapshotDto`. After capturing a snapshot, show
a small inline text box to type what was changed and how it felt.

**Why:** Snapshots are the core tuning feedback loop. Without annotations users forget
what they changed between "snapshot_001" and "snapshot_027".

**Work:**
- `TelemetrySnapshotDto.cs` — add `Notes` string field
- `SnapshotFileLoader.cs` — preserve notes when saving/loading snapshot files
- `TelemetryPage.xaml` — add a notes text box below the snapshot list

**Files to modify:** 3

---

### Q4. FFB Peak Force / Clipping Warning HUD

| Impact | Effort | Synergy |
|--------|--------|---------|
| 4      | 2      | 4       |

**What:** A tiny always-on-top overlay (or dashboard widget) showing real-time peak
force, clipping %, and oscillation detection. Changes color from green → yellow → red
when thresholds are crossed.

**Why:** Users need to know instantly when they're over-driving the wheel. A floating
indicator during driving is more useful than checking graphs after.

**Work:**
- New `PeakForceOverlay.xaml/.cs` — small semitransparent window, bound to pipeline
  output stats
- `FfbPipeline.cs` or `MainViewModel.cs` — expose rolling peak force, clipping count,
  oscillation flag as observable properties
- Register in `MainWindow.xaml.cs` overlay management

**Files to create:** 2 (PeakForceOverlay.xaml, PeakForceOverlay.xaml.cs)
**Files to modify:** 2–3

---

## Mid-Term Features (Medium Effort, High Impact)

### M1. Telemetry Snapshot Comparison Tool

| Impact | Effort | Synergy |
|--------|--------|---------|
| 5      | 3      | 5       |

**What:** Load two snapshot files side-by-side and overlay their graphs. Show a numeric
diff summary for key metrics (peak force, clipping %, avg speed, oscillation count,
snap count). Highlight what changed between "before" and "after" tuning.

**Why:** This directly completes the tuning feedback loop. Users tune → drive → snapshot
→ tune → drive → snapshot → compare. Without comparison, the second snapshot has no
context.

**Work:**
- New `SnapshotCompareService.cs` — computes diff statistics between two snapshots
- New `SnapshotComparePage.xaml/.cs` — dual-axis chart overlay or split view
  - Option A: Overlay two line series on the same chart with different colors
  - Option B: Split pane (top: snapshot A, bottom: snapshot B)
  - Numeric diff panel: metric | before | after | change (+/- %)
- Reuse existing `OxyPlot` or chart control from `TelemetryPage`
- Add a "Compare with..." button on the snapshot list in `TelemetryPage`
- Navigation: new sidebar entry or accessible from TelemetryPage

**Files to create:** 2–3
**Files to modify:** 4–5

---

### M2. A/B Profile Diff Viewer

| Impact | Effort | Synergy |
|--------|--------|---------|
| 4      | 2      | 5       |

**What:** Load two profiles into a side-by-side viewer that highlights different
parameter values. Categorized by section (Output, Tyre Forces, Damping, EQ, etc.).
Show the delta (diff) in a clean tree view.

**Why:** Complements Q2 (A/B toggle). Users need to know *what* is different between
the two profiles they're toggling between.

**Work:**
- New `ProfileComparer.cs` — reflection- or dictionary-based diff of two `FfbProfile`
  objects
- `ProfilesPage.xaml` — "Compare" button that opens a diff panel below the detail view
  or switches to a comparison layout
- Reuse `LabeledSlider` or new read-only diff row control

**Files to create:** 1 (ProfileComparer.cs)
**Files to modify:** 2 (ProfilesPage.xaml, ProfilesPage.xaml.cs)

---

### M3. Bass Shaker / Buttkicker Audio Output

| Impact | Effort | Synergy |
|--------|--------|---------|
| 4      | 3      | 4       |

**What:** Route the LFE generator + vibration mixer to a secondary audio device
(via WASAPI exclusive/shared mode or NAudio) instead of (or in addition to) mixing
into the main FFB output. Add crossover filters to split the signal: low frequencies
→ bass shaker, higher frequencies → wheel.

**Why:** Many sim racers use Buttkicker or similar bass shakers bolted to their rig.
Feeling curbs/engine rumble through the seat is a massive immersion upgrade. Currently
the app has rich LFE + vibration synthesis but no way to output it to a transducer.

**Work:**
- Reference `NAudio` NuGet or use `ManagedWASAPI`/`WASAPI` via P/Invoke
- New `AudioBassShakerService.cs` — manages audio device selection, buffer routing,
  crossover filter
- New `AudioBassShakerConfig` section in `FfbProfile`:
  - `Enable` (bool)
  - `DeviceName` (string — audio endpoint)
  - `CrossoverHz` (float — typically 40–80 Hz)
  - `ChannelGainLfe` / `ChannelGainVibration` / `ChannelGainEngine`
  - `OutputMode` (enum: MonoToSub, StereoLR, FrontBack)
- `FfbPipeline.cs` or `FfbLfeGenerator.cs` — after processing LFE/vibration signals,
  push a copy to the audio service
- `DevicesPage.xaml` — add a "Bass Shaker" section with device picker + gain sliders

**Files to create:** 2–3
**Files to modify:** 4–5

---

### M4. Live Frequency Spectrum Analyzer

| Impact | Effort | Synergy |
|--------|--------|---------|
| 4      | 3      | 4       |

**What:** Add a real-time FFT spectrum visualization of the processed FFB signal.
Overlaid on the EqualizerPage EQ curve so users can see which frequencies are dominant
while adjusting the EQ bands.

**Why:** The 10-band EQ is powerful but blind — users guess which bands need adjustment.
A live spectrum shows exactly where energy is concentrated. This turns EQ tuning from
guesswork into data-driven adjustment.

**Work:**
- Reference `MathNet.Numerics` (has `Fourier.Forward`) or implement a simple FFT
- New `FftAnalyzer.cs` — ring buffer of recent samples, compute FFT every N frames,
  return magnitude spectrum (log-scaled, 0–100 Hz)
- `EqualizerPage.xaml` — add a translucent FFT trace overlay on the EQ curve panel
- `FfbPipeline.cs` — expose a snapshot of the final output buffer for FFT analysis
- Performance: compute FFT on a background thread, only update visualization at 15 Hz

**Files to create:** 1 (FftAnalyzer.cs)
**Files to modify:** 2–3

---

### M5. Profile Export to Vendor Format

| Impact | Effort | Synergy |
|--------|--------|---------|
| 3      | 3      | 3       |

**What:** Export the app's profile parameters to vendor-specific FFB profile formats
(Moza Pit House, Fanatec, Simucube, Simagic). At minimum, export Output Gain as Moza's
overall strength / Fanatec's FEI / Simucube's output torque.

**Why:** Users who switch between the app's FFB and native game FFB need consistent
baseline settings. Also, sharing profiles across the community is easier when the format
is portable.

**Work:**
- New `ProfileFormatExporters.cs` with per-vendor converters:
  - `MozaPitHouseExporter` — produce .mcf or CoAP commands
  - `FanatecExporter` — produce FanaLEDS-compatible format or SDK params
  - `SimucubeExporter` — produce TrueDrive .scp compatible format
- `ProfilesPage.xaml` — add "Export to..." submenu in profile context menu
- Each converter maps a subset of the app's FFB profile to vendor equivalents

**Files to create:** 1–2
**Files to modify:** 2

---

## Ambitious Projects (High Effort, Transformative)

### A1. Cloud Profile Sharing & Community Repository

| Impact | Effort | Synergy |
|--------|--------|---------|
| 5      | 4      | 4       |

**What:** A cloud-hosted repository where users can upload, browse, rate, and download
profiles. Integrated into the app's ProfilesPage as "Community Profiles" tab. Profiles
are tagged with car, track, wheelbase, torque, and game.

**Why:** FFB tuning is trial-and-error. A community repository turns every user's
tuning session into knowledge for others. This is the feature that builds a community
around the app.

**Work:**
- Backend: Simple REST API (Azure Functions / AWS Lambda / minimal ASP.NET):
  - POST /profiles (upload)
  - GET /profiles?car=x&track=y&wheelbase=z (search)
  - GET /profiles/{id} (download)
  - POST /profiles/{id}/rate
  - GET /profiles/popular (curated/homepage)
  - Auth: anonymous upload with device fingerprint + optional username
- `ProfileManager.cs` — add `UploadToCommunity`, `DownloadFromCommunity` methods
- `ProfilesPage.xaml` — add "Community" tab with search filters (car, track, wheel,
  torque range, rating) + results grid + one-click apply
- Terms: community guidelines, no personal data, profiles are CC0/public domain

**Alternative (lower effort):** Use GitHub Gist as the backend. Profiles are JSON.
Upload = create gist. Browse = search gists by tag. This avoids infrastructure cost.

**Files to create:** Backend (separate repo or in tools/) + 1 service class
**Files to modify:** 2–3

---

### A2. Motion Platform Connector

| Impact | Effort | Synergy |
|--------|--------|---------|
| 4      | 4      | 3       |

**What:** Output processed FFB forces, suspension data, and chassis accelerations to
motion simulators via standard protocols: SimTools shared memory, serial port (SFU),
UDP (D-BOX / SimX / Next Level Racing). Let users map telemetry channels to motion axes.

**Why:** Users with motion rigs are the most invested sim racers. They already have FFB
tuners and motion platforms. Integrating both into a single app is a strong differentiator.

**Work:**
- New `MotionPlatformService.cs`:
  - Detect active motion protocols (SimTools shared memory scan, serial port scan)
  - Read motion config from profile: axis-to-channel mapping, scaling, inversion, filters
  - Push data at physics rate (~333 Hz) via the selected interface
- New `MotionConfig` section in `FfbProfile`:
  - `Protocol` (SimTools / Serial / UDP / SimX)
  - Per-axis: `Source` (enum: SuspensionTravel, Acceleration, YawRate, ForceOut, etc.),
    `Scale`, `Invert`, `Min`, `Max`
  - `PortName` / `IpAddress` / `PortNumber`
- `DevicesPage.xaml` — add "Motion" section with axis mapping UI
- `TelemetryLoop.cs` — invoke motion platform update in the main loop

**Files to create:** 2–3
**Files to modify:** 3–4

---

### A3. Automated Genetic Algorithm FFB Tuner

| Impact | Effort | Synergy |
|--------|--------|---------|
| 5      | 5      | 3       |

**What:** Run an automated optimization loop that tweaks FFB parameters, measures
objective quality metrics (clipping %, oscillation count, force bandwidth utilization),
and evolves toward an optimal profile — no human intervention needed.

**Why:** This is the holy grail of FFB tuning. Users run it once and get a profile that
is provably better across measurable dimensions. It leverages the existing snapshot
analysis (`FfbEventDetector`, `ProfileRecommender`) and drives it to its logical
conclusion.

**Work:**
- New `GeneticOptimizerService.cs`:
  - Define chromosome: vector of ~20 key FFB parameters (gains, damping, EQ, etc.)
  - Fitness function: weighted combination of:
    - Minimize clipping %
    - Minimize oscillation count
    - Minimize snap events
    - Maximize dynamic range utilization
    - Minimize road noise amplitude
  - Population evolution: tournament selection, crossover, mutation
  - Requires: automated driving data input. Options:
    - Pre-recorded telemetry playback (replay snapshot CSV data through the pipeline)
    - Live driving with "optimization session" (user drives a consistent pattern)
- `FfbPipeline.cs` — add a "replay mode" that accepts `FfbRawData` from snapshot CSV
  instead of live telemetry, for offline optimization
- New `OptimizerPage.xaml` — progress, best-fitness chart, parameter evolution view
- Integration with `ProfileManager` — save the elite chromosome as a named profile

**Simpler alternative:** Grid search / hill climbing on fewer parameters (Output Gain,
Damping, Tone, Center Suppression — 4 params) — 80% of the value with 20% of the code.

**Files to create:** 3–4
**Files to modify:** 3–4

---

### A4. DirectX In-Game Overlay

| Impact | Effort | Synergy |
|--------|--------|---------|
| 4      | 4      | 4       |

**What:** Replace the WPF transparent overlays with a true DirectX 11/12 overlay
injected into the game process. Render HUD telemetry, force bars, tire temps,
clipping warnings, and the track map over the actual game view.

**Why:** WPF overlays can flicker, lag behind, or be blocked by exclusive fullscreen.
A DX overlay renders smoothly at game frame rate, works in fullscreen, and looks
professional. It also enables OBS Browser Source-free streaming overlays.

**Work:**
- Option A: Use `SharpDX` or `Vortice.Windows` for DX11 overlay rendering
- Option B: Use `Discord GameOverlay` or `ImGui` with DX hook
- New `GameOverlayService.cs`:
  - Detects game process (AC EVO, R3E, etc.)
  - Hooks swap chain `Present()` via Detours or `MinHook`
  - Renders overlay UI components (force bar, tire temps, clipping indicator, lap info)
  - Overlay components configurable from app settings
- Move existing overlay content from WPF windows to DX overlay where appropriate
- Proper DPI scaling, multiple monitor support, overlay toggle hotkeys

**Files to create:** 3–4
**Files to modify:** 2–3

---

### A5. Lap Performance Correlation Engine

| Impact | Effort | Synergy |
|--------|--------|---------|
| 4      | 3      | 4       |

**What:** Correlate FFB quality metrics with lap times. For each completed lap,
record: lap time, sector times, average clipping %, oscillation count, snap events,
peak force. Then show: "Your fastest lap had 8% clipping vs 14% on your average lap"
or "Less damping correlates with 0.3s faster lap times on this track."

**Why:** This turns FFB tuning from "feeling good" into "demonstrably faster." Users
can objectively see which settings help their lap times.

**Work:**
- `LapDataRecorder.cs` — already records lap stats. Extend to store per-lap FFB metrics
  in a rolling window (last 100 laps per car/track)
- New `LapPerformanceAnalyzer.cs`:
  - Aggregate laps by car+track combination
  - Compute correlation coefficients between FFB parameters and lap time
  - Identify "best lap" settings
  - Generate recommendations: "Your 3 fastest laps used CenterSuppression=4 vs your
    average of 7. Try reducing CenterSuppression."
- `TrackMapPage.xaml` — add a "Lap History" tab showing per-lap overlay on the track
  (color-coded by lap time, click to see that lap's FFB metrics)
- `ProfilesPage.xaml` — show "Recommended from your data" section

**Files to create:** 1–2
**Files to modify:** 3–4

---

## Differentiating Ideas (Unique to This App)

### D1. FFB "Prescription" — Shareable Tuning Recipe Cards

| Impact | Effort | Synergy |
|--------|--------|---------|
| 3      | 2      | 3       |

**What:** A compact, human-readable summary of a profile's key tuning philosophy.
Displayed as a card: car, track, wheelbase, torque, and 3–5 "prescription" parameters
highlighted as the defining changes. Export as a PNG card for sharing on Discord/Reddit.

**Why:** Most users share screenshots of their settings. A clean, branded recipe card
makes the app look professional and encourages community sharing conversation.

**Work:**
- New `ProfileRecipeCardExporter.cs` — render profile summary to a PNG/bitmap using
  `System.Drawing.Common` or SkiaSharp
- Card layout: app logo, car/track/wheelbase, parameter table (name + value),
  optional notes, QR code to download the profile file
- `ProfilesPage.xaml` — "Recipe Card" button in profile context menu
- Share button → generate card + open share sheet (copy to clipboard / save / share)

**Files to create:** 1–2
**Files to modify:** 1

---

### D2. Force Feedback Video Synchronization

| Impact | Effort | Synergy |
|--------|--------|---------|
| 3      | 3      | 4       |

**What:** Overlay force feedback telemetry on top of recorded gameplay video. Using
the existing `GameRecordingService` + snapshot CSV data, burn in a force/steer/clipping
HUD onto the video file at the correct time offsets.

**Why:** Users share videos of their driving. Adding FFB telemetry visualization makes
the videos educational ("see, when I lost the rear, the FFB spiked here") and showcases
the app.

**Work:**
- `ReplayVisualizerService.cs` — already generates HTML replay with charts. Extend:
  - `GenerateVideoOverlayAsync(videoPath, snapshotPath, outputPath)` — use FFmpeg
    filter_complex to overlay force bar / clipping indicator / steer angle graph
  - Timecode synchronization: align snapshot data timestamps with video frames
- `GameRecordingService.cs` — auto-generate overlay version after recording stops
- `ProfilesPage.xaml` or `ReplayVisualizerService` — "Export with Telemetry Overlay"

**Files to modify:** 2–3

---

### D3. Tire Model Visualization

| Impact | Effort | Synergy |
|--------|--------|---------|
| 3      | 3      | 3       |

**What:** A 2D top-down car visualization showing per-corner tire forces (Mz, Fx, Fy),
slip angles, temps, and load. Arrows visualize force vectors. Colors indicate temp
distribution. Real-time or from snapshot playback.

**Why:** FFB comes from tire physics. Seeing *why* the FFB feels the way it does
(tire X is overheating, tire Y has high slip angle) helps users understand both the
FFB and the car's behavior. Educational and useful.

**Work:**
- New `TireVisualizationControl.xaml/.cs` — custom-drawn 2D car top-down view
  - Car rectangle, 4 wheel circles
  - Force vector arrows (scale proportional to force magnitude)
  - Temperature gradient fill on each tire
  - Slip angle lines
  - Suspension travel bars
- Data source: `FfbRawData` from live telemetry or snapshot playback
- New `TirePage.xaml` or integrate into `TrackMapPage` as a dashboard widget
- Option: add a "ghost car" overlay to compare two snapshots' tire states

**Files to create:** 3
**Files to modify:** 1–2

---

### D4. Steering Feel Questionnaire & Auto-Profile Generator

| Impact | Effort | Synergy |
|--------|--------|---------|
| 4      | 2      | 4       |

**What:** A guided questionnaire ("Do you prefer a heavy or light wheel?" "How much
road feel do you want?" "Do you want strong kerb feedback?") that maps answers to
profile parameters and generates a starting profile. Builds on the Setup Wizard
concept but is focused on *feel preferences* rather than hardware setup.

**Why:** New users are overwhelmed by the 50+ parameters. This gives them a
personalized starting point in 30 seconds with zero FFB knowledge required.

**Work:**
- New `FeelQuestionnaireService.cs`:
  - 8–10 questions about driving style + hardware + preference
  - Weighted mapping: each answer shifts one or more profile parameters
  - Output: a complete `FfbProfile` ready to apply
- `HomePage.xaml` — "Quick Setup" button that launches the questionnaire
- Or integrate into `SetupWizardOverlay` as an additional step
- Optional: "Describe your problem" free-text → NLP map to category (future AI-ready)

**Files to create:** 1
**Files to modify:** 2

---

### D5. Steering Wheel Button Mapper & Macro System

| Impact | Effort | Synergy |
|--------|--------|---------|
| 4      | 3      | 4       |

**What:** Full button remapping system. User presses a button on their wheel → app
captures it → assigns it to an action: toggle A/B profile, capture snapshot, toggle
overlay, adjust gain +/- 5%, mute FFB, panic stop, toggle EQ preset, toggle HF8 preset.
Support macros (button sequence → multi-action).

**Why:** Currently button actions are assigned programmatically (snapshot, panic stop).
Users want to customize which button does what. This is especially important for wheels
with many buttons that need app control while driving.

**Work:**
- New `ButtonMapperPage.xaml/.cs` — UI for assign-by-pressing:
  - List of available buttons (populated from `FfbDeviceManager.DetectedButtons`)
  - List of available actions
  - Drag-to-assign or "Press a button on your wheel..." detection mode
- `AppSettings.cs` — persist button-to-action mappings
- `FfbDeviceManager.cs` — on button press, look up action in mapping table, execute
  command on `MainViewModel`
- Actions enum: `ToggleProfileA`, `ToggleABTest`, `CaptureSnapshot`, `ToggleOverlay`,
  `GainUp`/`GainDown`, `ToggleMute`, `PanicStop`, `ToggleHF8Preset`, `ToggleCoach`,
  `SaveSnapshot`, `StartRecording`, `StopRecording`, `NextEQPreset`, `PrevEQPreset`,
  `AdjustDamping+`/`-`, etc.

**Files to create:** 2
**Files to modify:** 3

---

## Implementation Priority Matrix

```
                    HIGH EFFORT
                        │
     A3. Genetic Tuner  │  A1. Cloud Sharing
     A2. Motion         │
     A4. DX Overlay     │
                        │
────────────────────────┼────────────────────────
                        │
     M1. Snapshot Comp  │  Q2. A/B Toggle ⭐
     M3. Bass Shaker    │  Q1. Profile Notes
     M5. Vendor Export  │  Q4. Peak Force HUD
     D5. Button Mapper  │  D4. Feel Questionnaire
     M4. FFT Analyzer   │  M2. Profile Diff
     A5. Lap Perf Corr  │  Q3. Snapshot Annotations
     D2. Video Synch    │  D1. Recipe Cards
     D3. Tire Vis       │
                        │
                    LOW EFFORT
```

**⭐ = Highest immediate value.** A/B profile toggle (Q2) is the single highest-impact,
lowest-effort feature. It changes the tuning workflow from "guess and check" to
"compare and contrast."

---

## Recommended Roadmap

### Phase 1 — Core Tuning UX (Week 1–2)
1. Q2: A/B Profile Toggle
2. M2: A/B Profile Diff Viewer
3. Q1: Profile Notes & Tags
4. Q3: Snapshot Annotations

### Phase 2 — Analysis & Comparison (Week 3–4)
5. M1: Telemetry Snapshot Comparison
6. M4: Live Frequency Spectrum Analyzer
7. A5: Lap Performance Correlation

### Phase 3 — Hardware Expansion (Week 5–6)
8. M3: Bass Shaker Audio Output
9. M5: Vendor Profile Export
10. D5: Button Mapper & Macro System

### Phase 4 — Community & Delight (Week 7–8)
11. Q4: Peak Force / Clipping HUD
12. D4: Feel Questionnaire
13. D1: Recipe Cards
14. D2: FFB Video Sync

### Phase 5 — Long-Term Vision (Beyond)
15. A1: Cloud Profile Sharing
16. A2: Motion Platform Connector
17. A4: DX In-Game Overlay
18. D3: Tire Visualization
19. A3: Genetic Algorithm Tuner

---

## Risk Assessment

| Feature | Risk | Mitigation |
|---------|------|------------|
| A3: Genetic Tuner | High — offline optimization requires replay mode which may not perfectly match live feel | Start with grid search on 4 params |
| A4: DX Overlay | High — game-specific hooks break on updates, anti-cheat flags | Use ImGui overlay mode as safer alternative |
| A1: Cloud Sharing | Medium — hosting cost, moderation, abuse | Use GitHub Gist backend or serverless |
| M3: Bass Shaker | Medium — audio latency, device enumeration complexity | Start with WASAPI shared mode, add exclusive later |
| A2: Motion Platform | Medium — protocol reverse engineering, user support burden | Start with SimTools shared memory (simplest) |
