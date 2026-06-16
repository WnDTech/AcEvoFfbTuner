# FFB Coach — New Sidebar Page Plan

## Overview
Add a new **FFB Coach** page to the sidebar that acts as an intelligent interactive assistant. It uses saved telemetry snapshots or live profiler data to guide the user through a structured Q&A session, helping them tune their FFB profile. The coach analyzes the data, detects issues (clipping, snaps, oscillations, force anomalies), generates recommendations, and walks the user through adjustments in a conversational wizard-like flow.

## Files to Create (4 new files)

### 1. `src/AcEvoFfbTuner.Core/Profiles/SnapshotFileLoader.cs`
Parses saved snapshot `.txt` files from disk.
- Method: `LoadSnapshotFiles() → List<SnapshotFileEntry>` — scans `%APPDATA%\AcEvoFfbTuner\snapshots` for `snapshot_*.txt`
- Method: `ParseCsvData(string filePath) → SnapshotCsvData?` — extracts the CSV time-series section and profiler stats from a snapshot file
- DTO: `SnapshotFileEntry` (FilePath, FileName, FileSize, Timestamp, ProfileName)
- DTO: `SnapshotCsvData` (CsvLines, Stats string, ProfileName, TorqueNm)

### 2. `src/AcEvoFfbTuner/Services/FfbCoachService.cs`
Core coaching engine.
- **States:** Idle, Analyzing, Questioning, Applying, Summary
- **Question flow model:** Tree of `CoachQuestion` nodes, each with:
  - `Id`, `Text` (displayed to user)
  - `Answers[]` — each with label + next question ID
  - `DataTrigger` — condition that auto-selects this branch based on analysis
  - `Adjustment` — optional parameter change to apply
- **Analysis phase:**
  - If snapshot loaded: parse CSV, run `FfbEventDetector` → `DiagnosticLapSummary`, then `ProfileRecommender.Generate()` for baseline recommendations
  - If live data: access `MainViewModel`'s profiler buffer and pipeline state
- **Question generation:** Maps detected issues to conversational questions:
  - Clipping > 15% → "I see X% clipping. Does the wheel feel too strong?"
  - Suspicious snaps on straight → "The data shows some force snaps on straights. Is the wheel jerky at high speed?"
  - High road vibration → "Road vibration seems strong. Do you feel excessive vibration?"
  - Oscillations → "The wheel may be oscillating. Does it feel unstable on straights?"
  - No issues detected → "Everything looks good. Want to fine-tune any specific feel aspect?"
- **User response handling:** Based on answer, generates targeted `FfbRecommendation` with suggested parameter changes
- **Apply method:** Creates an action delegate that modifies the pipeline (same pattern as `MainViewModel`'s real-time slider bindings)
- **Rollback support:** Stores previous values for undo

### 3. `src/AcEvoFfbTuner/Views/Pages/FFBCoachPage.xaml`
Page layout with:
- **Left panel (chat area):** Scrollable conversation card list
  - Each card: "Coach avatar" icon + message text + optional answer buttons
  - Cards are coach questions (left-aligned) or user answers (right-aligned)
- **Right panel (data display):**
  - Current profile name + snapshot info bar
  - Key metrics panel (OutputGain, Clipping%, Main metrics)
  - When a recommendation is active: shows current vs suggested value slider
  - Apply / Undo buttons
- **Bottom bar:** Restart button, progress indicator (step N of M)

### 4. `src/AcEvoFfbTuner/Views/Pages/FFBCoachPage.xaml.cs`
Code-behind:
- Binds to `MainViewModel` properties for coach state
- Handles the `CoachButtonClick` events from the chat cards
- Delegates to `FfbCoachService` for processing
- Uses a `UniformGrid` or `ItemsControl` for the chat message list

## Files to Modify (6 existing files)

### 5. `src/AcEvoFfbTuner/ViewModels/NavPage.cs`
Add: `FfbCoach` to the enum (before Settings, or between Telemetry and Devices — consistent ordering)

### 6. `src/AcEvoFfbTuner/Controls/SidebarControl.xaml`
Add new `RadioButton` for "FFB Coach" between Telemetry and Devices (preserves existing order).
- Follow exact same template pattern (Canvas icon + TextBlock label)
- Icon suggestion: A chat-bubble/lightbulb SVG path (e.g., `M12,2 A10,10,0,1,1,2,12 L2,22 L12,18 A10,10,0,0,1,12,2 Z M8,10 L16,10 M8,14 L14,14`)
- Tooltip: "FFB Coach — interactive tuning assistant"
- Tag: `{x:Static vm:NavPage.FfbCoach}`

### 7. `src/AcEvoFfbTuner/Controls/SidebarControl.xaml.cs`
- Add `NavCoachBtn` field in `SetSelected()`: `NavCoachBtn.IsChecked = page == NavPage.FfbCoach;`
- Add `NavCoachBtn` to the `foreach` loop in `ApplyCollapsedState()`

### 8. `src/AcEvoFfbTuner/Views/MainWindow.xaml`
In the `ContentArea` Grid, add:
```xml
<pages:FFBCoachPage x:Name="FFBCoachPageCtrl" Visibility="Collapsed" />
```
(Position between TelemetryPageCtrl and DevicesPageCtrl for logical ordering.)

### 9. `src/AcEvoFfbTuner/Views/MainWindow.xaml.cs`
In `UpdatePageVisibility()`, add:
```csharp
FFBCoachPageCtrl.Visibility = vm.CurrentPage == NavPage.FfbCoach ? Visibility.Visible : Visibility.Collapsed;
```

### 10. `src/AcEvoFfbTuner/ViewModels/MainViewModel.cs`
Add coach-related properties to expose to the page:
- `CoachState` (enum: Idle/Analyzing/Questioning/Applying/Summary)
- `CoachMessages` (ObservableCollection<CoachMessage> — the chat items)
- `CoachCurrentQuestion` (CoachQuestion — the active question with answer buttons)
- `CoachCurrentRecommendation` (FfbRecommendation? — the currently active recommendation)
- `CoachSnapshotFiles` (ObservableCollection<SnapshotFileEntry> — list of saved snapshots)
- `CoachSelectedSnapshot` (SnapshotFileEntry? — which snapshot is loaded)
- `CoachProgress` (string — "Step 2 of 5")
- `CoachApplyCommand` (RelayCommand — applies the current recommendation to the pipeline)
- `CoachUndoCommand` (RelayCommand — reverts last change)
- `CoachRestartCommand` (RelayCommand — resets the coaching session)
- `CoachAnswerCommand` (RelayCommand<string> — user selects an answer)
- `CoachLoadSnapshotCommand` (RelayCommand — loads selected snapshot)
- `CoachUseLiveDataCommand` (RelayCommand — starts coaching with live data)
- Initialize: `FfbCoachService = new(ProfileManager, Pipeline, TelemetryLoop)`

## Data Flow

```
User clicks "FFB Coach" sidebar
  → MainWindow shows FFBCoachPage
  → CoachService.LoadSnapshotFiles() populates CoachSnapshotFiles
  → Page shows welcome message + "Pick a snapshot or use live data"

User selects snapshot (or live data)
  → CoachService.Analyze(snapshot/pipeline state)
  → Generates DiagnosticLapSummary
  → Generates List<FfbRecommendation> via ProfileRecommender
  → Maps recommendations to CoachQuestion tree
  → Adds first coach message to CoachMessages

Coach asks first question (e.g., "The data shows 22% clipping. Does the wheel feel too strong?")
  → User answers via CoachAnswerCommand
  → CoachService.ProcessAnswer(answerId)
  → Updates CoachMessages with user's answer + next coach question
  → If adjustment available: shows slider with current→suggested values, enables Apply button

User clicks Apply
  → CoachService.ApplyRecommendation(rec) → modifies pipeline
  → Stores previous value for undo
  → Coach asks "Applied. Can you test drive and let me know?"

Flow continues through all issues, then shows Summary
  → List of all changes applied
  → "Take another snapshot to verify" prompt
```

## Question Flow Design

```
Welcome → Select Source (snapshot/live)
  │
  └─→ Analysis Phase (show "Analyzing..." spinner)
       │
       ├─→ [Clipping > 15%] "I see X% clipping. Wheel too strong?"
       │     ├─ "Yes, too strong" → Suggest OutputGain ↓ or SoftClip ↑
       │     └─ "No, feels fine" → Skip to next issue
       │
       ├─→ [SuspiciousSnapsOnStraight > 2] "Snaps detected on straights. Is the wheel jerky?"
       │     ├─ "Yes, jerky" → Suggest MaxSlewRate ↓ or Hysteresis ↑ or SuspensionRoadGain ↓
       │     └─ "No, feels smooth" → Skip
       │
       ├─→ [SuspiciousOscillations > 1] "Oscillations detected. Does the wheel feel unstable?"
       │     ├─ "Yes, unstable" → Suggest CenterSuppressionDegrees ↓ or SuspensionRoadGain ↓
       │     └─ "No, stable" → Skip
       │
       ├─→ [RoadVibration > threshold] "Road vibration seems strong. Feel excessive vibration?"
       │     ├─ "Yes, too much" → Suggest SuspensionRoadGain ↓ or RoadGain ↓
       │     └─ "No, feels good" → Skip
       │
       ├─→ [ForceAnomaly > 0] "I noticed some force direction issues. Wheel fighting you?"
       │     └─ (Info card — code issue, not profile change)
       │
       └─→ [No issues] "Everything looks healthy! Want to fine-tune?"
             ├─ "Adjust overall strength" → OutputGain slider
             ├─ "Adjust damping feel" → Damping sliders
             └─ "Adjust vibration feel" → Vibration gain sliders
                  │
                  └─→ Summary → "All done! Here's what we changed..."
```

## UI/UX Design
- Follow existing dark theme (`#FF0D1117` background, `#FFF0883E` accent)
- Chat cards styled like the existing `SectionCard` control with rounded corners
- Coach messages: accent border on left, light text
- User answers: right-aligned, accent background
- Answer buttons: styled like the existing `RadioButton` nav items (dark bg, accent on hover)
- Sliders: use the existing `LabeledSlider` control for parameter adjustments
- Progress indicator: simple "Step X of Y" text + dot indicators (like the sidebar button highlights)
- Empty state: "Welcome to FFB Coach! Select a snapshot or use live telemetry data to get started."
- Loading state: "Analyzing your data..." with subtle animation

## Key Design Decisions

| Decision | Choice | Rationale |
|---|---|---|
| ViewModel pattern | Properties on `MainViewModel` | All existing pages use this pattern; consistent |
| Coaching engine | Separate service class | Keeps logic testable and MainViewModel clean |
| Snapshot file format | Existing `.txt` snapshot files | No new file format; reuses existing data |
| AI requirement | Rule-based first, AI-ready architecture | No external dependencies; `CoachQuestion` tree is easy to swap with LLM later |
| Question representation | Predefined tree with data-triggered branches | Deterministic, testable, no latency |
| Profile modification | Direct pipeline write via action delegates | Same pattern as existing slider bindings |
| Undo support | Stack of previous values | Simple, reliable rollback |
| Chat UI | `ItemsControl` with `DataTemplate` | Native WPF, consistent with app design |

## Implementation Order
1. `SnapshotFileLoader.cs` — core parsing of snapshot files from disk
2. `FfbCoachService.cs` — coaching engine + question tree + analysis pipeline
3. `NavPage.cs` — add `FfbCoach` enum value
4. `SidebarControl.xaml` + `.cs` — add coach nav button
5. `MainWindow.xaml` + `.cs` — register coach page in ContentArea
6. `MainViewModel.cs` — add coach state properties and commands
7. `FFBCoachPage.xaml` + `.xaml.cs` — build the coach UI
