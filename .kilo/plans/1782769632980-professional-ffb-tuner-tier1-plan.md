# Professional FFB Tuner — Tier 1 Upgrade Plan

## Goal
Elevate the app from advanced enthusiast tool to professional-grade product by addressing five foundational gaps.

---

## 1. Refactor MainViewModel (4795 lines → partial classes)

**Why:** Single God ViewModel is a maintenance risk. No team-scale contributor can safely touch it. Any regression affects all features.

**Approach:** Mechanical split into partial class files by domain — no behavior change, no binding breakage.

| Partial File | Domain | Est. Lines Moved |
|---|---|---|
| `MainViewModel.Game.cs` | Game selection, reader connection state, supported game enum | ~300 |
| `MainViewModel.Telemetry.cs` | 333 Hz dispatcher, telemetry property bindings, dash/race info | ~700 |
| `MainViewModel.Tuning.cs` | All pipeline parameter properties (gains, damping, slip, EQ, etc.) | ~1500 |
| `MainViewModel.Profile.cs` | Profile CRUD, save/load/auto-detect, import/export | ~600 |
| `MainViewModel.Services.cs` | Recording, coach, diagnostics, voice, Discord, updates | ~500 |
| `MainViewModel.Navigation.cs` | Sidebar, page switching, overlays, popouts | ~300 |
| `MainViewModel.Snapshots.cs` | Snapshot capture, CSV export, snapshot file loading | ~400 |
| `MainViewModel.cs` | Remaining — constructor, init, core state | ~500 |

**Key risks & mitigations:**
- XAML bindings reference `MainViewModel` properties — partial classes are in the same namespace and class, so bindings are unaffected
- `CommunityToolkit.Mvvm` source generators (`[ObservableProperty]`, `[RelayCommand]`) must remain in the same file as the partial class declaration that owns them (or be in a separate `MainViewModel.Properties.cs` with `partial`). Verify no generators break across partial files.
- Verify all 11 pages, 10+ overlays, and their `.xaml` + `.xaml.cs` files still compile without binding errors
- After split: verify `dotnet build` succeeds, then run the app and click through every page

**Definition of done:**
- `MainViewModel.cs` < 600 lines
- All partial files compile and app launches
- All slider bindings, page switches, profile ops work identically to before
- `dotnet build AcEvoFfbTuner.slnx -c Release` succeeds

---

## 2. Auto-Profile Switching

**Why:** `FfbProfile` already has `CarMatch`, `TrackMatch`, `GameMatch` fields and scope enum (`General`, `PerGame`, `PerCar`, `PerTrack`, `PerCarAndTrack`), but switching is manual. Power users expect profiles to follow car/track changes automatically mid-session.

**Changes needed:**

1. **ProfileManager** — Add `FindMatchingProfile(string game, string car, string track)` that checks scope precedence:
   - `PerCarAndTrack` > `PerTrack` > `PerCar` > `PerGame` > `General`
   - Fallback: current profile / last used

2. **TelemetryLoop.cs** — Expose events: `CarChanged(string carName)`, `TrackChanged(string trackName)`. These fire when shared memory reader detects a change (car_model field in graphics page, track name in static page).

3. **MainViewModel** — Subscribe to car/track changes, call `ProfileManager.FindMatchingProfile()`, auto-load. Show a toast notification overlay: *"Auto-loaded: Monza — BMW M4 GT3"*.

4. **ProfilePill UI** — Add lock icon toggle button. When locked, auto-switch is disabled and a padlock icon shows next to profile name.

5. **AppSettings** — Add `AutoSwitchProfiles` bool (default true), `ProfileLocked` bool.

**Definition of done:**
- Switching car in-game auto-loads matching profile from disk
- Switching track with same car auto-loads per-track profile
- Toast notification appears on switch
- Lock toggle prevents auto-switch
- Settings persist across restarts

---

## 3. Auto-Game Detection

**Why:** User must manually select game from dropdown. Professional apps detect the running game automatically.

**Changes needed:**

1. **New service: `GameDetectorService`**
   - Polls `Process.GetProcessesByName()` every 2 seconds
   - Maps executable name → `SupportedGame`:
     - `acevo` / `ac_evo` → `AssettoCorsaEvo`
     - `raceroom` / `raceroomracing` → `RaceRoom`
     - `acs` → `AssettoCorsa`
     - `LMU` / `lmU` → `LeMansUltimate`
     - `acc` / `acc2` → `AssettoCorsaCompetizione`
   - Fires `GameDetected(SupportedGame game)` event
   - Handles transition: if game exits, go to idle state (don't auto-stop pipeline unless user configured that)

2. **MainViewModel integration**
   - Subscribe to `GameDetected` event
   - Only auto-switch if current game is "None" or the detected game — honor manual override
   - Update GamePill display

3. **GamePill UI**
   - Add "Auto" option at top of dropdown (default)
   - When in Auto mode, show detected game name with "(auto)" suffix
   - Manual selection exits auto mode; "Auto" re-enables it

4. **AppSettings** — Add `AutoDetectGame` bool (default true)

**Edge cases:**
- Two games running simultaneously → keep current, show warning banner
- Game launches while app is already processing → auto-switch if no manual override
- No game running → pipeline stopped / idle state

**Definition of done:**
- Launching AC EVO auto-selects EVO pipeline within 2 seconds
- Switching to RaceRoom auto-selects R3E pipeline
- Manual override persists until user selects "Auto" again
- Multiple game detection shows warning, keeps current selection

---

## 4. OBS Browser-Source Overlay

**Why:** Every sim streaming tool needs an OBS overlay. `FfbLiveServer` already serves telemetry via HTTP SSE — extend it with an HTML overlay page.

**Changes needed:**

1. **FfbLiveServer.cs** — Add route `/overlay` serving a static HTML page. Add route `/overlay/config` returning current overlay configuration.

2. **New file: `overlay.html`** — Self-contained HTML overlay page:
   - **Data display**: Speed (km/h), gear, RPM, force output (Nm), lap time, delta, current lap
   - **Mini track map**: Canvas-based 2D track rendering with car position dot
   - **FFB mini-waveform**: Simple real-time scrolling strip of output force
   - **App branding**: Subtle logo/name in corner
   - **Design**: Dark theme, 1920×1080 safe zone, semi-transparent backgrounds, configurable opacity

3. **Data source**: Connect to `FfbLiveServer` SSE endpoint (`http://localhost:8321/stream`) — already provides 600-sample rolling telemetry history every frame.

4. **URL configuration**: `?opacity=0.7&showTrack=true&showWaveform=true` query params for streamer customization.

5. **FfbLiveServer CORS**: Allow `*` origin for OBS browser source.

**Definition of done:**
- Navigating to `http://localhost:8321/overlay` shows live telemetry overlay
- Adding Browser Source in OBS to this URL works with transparent background
- Track map renders car position in real time
- Overlay updates at 10+ FPS
- Force waveform scrolls smoothly

---

## 5. Tests (Unit + Pipeline Integration)

**Why:** Test project exists but is empty. For a DSP app with 5 game pipelines, untested changes WILL regress something. Professional product requires automated test coverage.

**Changes needed:**

### Unit Tests (target: `AcEvoFfbTuner.Tests`)

| Test Class | What It Tests |
|---|---|
| `FfbChannelMixerTests` | Mz/Fx/Fy blending, center blend zone, 3-sample median filter, adaptive normalization, clamp |
| `FfbLutCurveTests` | All presets (Linear, SoftCenter, Progressive, DeadZone), custom points, out-of-range |
| `FfbSlipEnhancerTests` | Linear slip boost, Pacejka MzCurve modulation, zero input, edge cases |
| `FfbDampingTests` | Viscous, Coulomb, gyroscopic speed scale, inertia acceleration scale, deadzone, min floor |
| `FfbEqualizerTests` | All 10 bands coefficient calc, biquad filter output, bypass at 0 dB, clipping |
| `FfbOutputClipperTests` | Soft clip threshold, sqrt overshoot, passthrough below threshold |
| `FfbProfileTests` | Serialization round-trip, migration from v3→v21, SanitizeFloats, default creation |
| `ProfileManagerTests` | CRUD, FindMatchingProfile scope ordering, file I/O mocking, car/track matching |
| `FfbPipelineTests` | Process() with known inputs, verify core/detail path outputs, center fade, low-speed fade, gear shift filter, noise floor gate |

### Pipeline Integration Tests

- Feed the same known physics data through `FfbPipeline.Process()`, `R3eFfbPipeline.Process()`, `AcFfbPipeline.Process()`, `LmuFfbPipeline.Process()`
- Assert that R3E-specific overrides (DC blocker, dynamic suppression, brake boost) do NOT affect base pipeline output
- Assert that base pipeline changes do NOT affect subclass outputs (regression guard)

### Test Infrastructure

- Use `FluentAssertions` for readable assertions (check if already referenced; add if not)
- Use `NSubstitute` or `Moq` for mocking `ISharedMemoryReader`, `IFFBProvider`
- Add `net8.0` target framework match (verify .csproj)
- Add parameterized theory tests for LUT curves, EQ biquads, damping

### CI Integration

- `.github/workflows/build.yml` — add step: `dotnet test AcEvoFfbTuner.Tests -c Release --no-restore`
- If no CI exists, add a basic GitHub Actions workflow for PR validation

**Definition of done:**
- `dotnet test` passes with 50+ test cases
- Pipeline regression tests cover all 5 games
- CI runs tests on push/PR

---

## Implementation Order

1. **Tests** — Write tests first (safety net for refactoring). This validates existing behavior before changes.
2. **Refactor MainViewModel** — Mechanical split with tests to verify no regression.
3. **Auto-Game Detection** — Independent feature, no dependency on 1 or 2.
4. **Auto-Profile Switching** — Depends on game detection (knowing car name) and profile manager.
5. **OBS Overlay** — Independent, can be done in parallel with 3/4.

Each item should be done, built, and verified before moving to the next.

---

## Validation

After each item:
```
dotnet clean AcEvoFfbTuner.slnx -c Release -q 2>&1
dotnet build AcEvoFfbTuner.slnx -c Release
dotnet test AcEvoFfbTuner.Tests -c Release --no-restore
```
