# Race Information Overlay — Implementation Guide

## 1. Architecture Overview

```
┌─────────────────────────────────────────────────────────────────────┐
│                        TelemetryLoop (background thread)             │
│                                                                     │
│  TryReadPhysics → physics (SPageFilePhysicsEvo)                     │
│  TryReadGraphics → graphics (SPageFileGraphicEvo)  ─── STORE ───►  │
│  MapRawData(physics, graphics) → FfbRawData          _latestRaw     │
│  pipeline.Process(raw) → FfbProcessedData            _latestProcessed│
│                                                       _latestPhysicsRaw│
│  DataUpdated?.Invoke(raw, processed)                  _latestGraphicsRaw│
└─────────────────────────────────────────────────────────────────────┘
         │
         │ DataUpdated event (60+ Hz)
         ▼
┌─────────────────────────────────────────────────────────────────────┐
│                    MainViewModel (UI thread, timer)                  │
│                                                                     │
│  OnUiUpdate: reads _telemetryLoop.LatestRaw / LatestProcessed       │
│  Updates UI bindings (speed, force, clipping, etc.)                 │
│  Calls mw.UpdateProfiler(...)  — drives ProfilerOverlay              │
│  Calls mw.ShowWheelCenterOverlay — drives WheelCenterOverlay        │
└─────────────────────────────────────────────────────────────────────┘
         │
         │ Create + Update (from OnUiUpdate)
         ▼
┌─────────────────────────────────────────────────────────────────────┐
│                   RaceInfoOverlay (transparent WPF window)          │
│                                                                     │
│  Tapped into same data path: reads _telemetryLoop._latestGraphicsRaw│
│  (via a new property) rather than duplicating the reader            │
└─────────────────────────────────────────────────────────────────────┘
```

## 2. The Data Problem: AC EVO Graphics Struct Is Stubbed

In `AssettoCorsaSharedMemoryReader.cs`, `MapGraphics()` (line 308) maps the raw AC Classic graphics struct into `SPageFileGraphicEvo`. The problem:

```csharp
// Lines 308-341 — STUBBED: returns defaults for most fields
private static SPageFileGraphicEvo MapGraphics(SPageFileGraphicAC ac)
{
    return new SPageFileGraphicEvo
    {
        // Fields that ARE mapped from the raw struct:
        PacketId = ac.PacketId,
        Status = ...,
        Npos = ac.NormalizedCarPosition,
        CarLocation = ...,
        SessionState = { CurrentLap = ac.CompletedLaps + 1,
                        TotalLap = ac.NumberOfLaps },
        // Everything else is DEFAULT:
        FuelLiterCurrentQuantity = 0,   // ✗ no fuel data
        CurrentPos = 0,                 // ✗ no position
        GapAhead = 0, GapBehind = 0,    // ✗ no gaps
        Flag = AcNoFlag,                // ✗ no flags
        CarDamage = new SmevoDamageState(), // ✗ empty
        TyreLf = new SmevoTyreState(),      // ✗ empty
        TimingState = new SmevoTimingState(), // ✗ empty
    };
}
```

**Fix required**: The AC EVO shared memory layout differs from AC Classic. The `SPageFileGraphicEvo` struct defines all the fields, but the reader uses the old AC Classic struct `SPageFileGraphicAC` to map. There are two approaches:

**Approach A (Recommended)**: Add a raw buffer reader path in `AssettoCorsaSharedMemoryReader` that reads `SPageFileGraphicEvo` directly from the memory-mapped file using the correct EVO struct layout. `SharedMemoryReader.cs` at line 211 already does this correctly:

```csharp
// SharedMemoryReader.cs:211 — already works correctly for EVO struct layout
public bool TryReadGraphics(out SPageFileGraphicEvo graphics)
{
    graphics = Marshal.PtrToStructure<SPageFileGraphicEvo>(handle.AddrOfPinnedObject());
}
```

**Approach B (Quick fix)**: Fix `MapGraphics` to extract more fields from the raw buffer bytes rather than relying on `SPageFileGraphicAC`.

For the overlay, the simplest path is to expose `_latestGraphicsRaw` and `_latestPhysicsRaw` from `TelemetryLoop` so the overlay VM can read the already-populated structs. The snapshot system (`TelemetrySnapshotDto`) already does this correctly — its `FromStruct` methods read all the graphics fields. The overlay just needs access to the same stored structs.

## 3. Required Changes — Step by Step

### Step 1: Expose Latest Graphics Data from TelemetryLoop

**File**: `src/AcEvoFfbTuner.Core/TelemetryLoop.cs`

Add two new properties to expose the stored raw data:

```csharp
// After existing properties:
public SPageFilePhysicsEvo? LatestPhysicsRaw => _latestPhysicsRaw;
public SPageFileGraphicEvo? LatestGraphicsRaw => _latestGraphicsRaw;
```

These are already stored at lines 383-384:
```csharp
_latestPhysicsRaw = physics;
_latestGraphicsRaw = graphics;
```

### Step 2: Create RaceInfoViewModel

**File**: `src/AcEvoFfbTuner/ViewModels/RaceInfoViewModel.cs`

A lightweight observable class that `RaceInfoOverlay` binds to. Updated from `MainViewModel.OnUiUpdate`.

```
class RaceInfoViewModel
{
    // — Gap Panel (Pit Board Replacement) —
    // Data from: SPageFileGraphicEvo
    public float GapAhead              → graphics.GapAhead          // seconds
    public float GapBehind             → graphics.GapBehind         // seconds
    public int Position                → graphics.CurrentPos        // overall position
    public int TotalDrivers            → graphics.TotalDrivers
    public string GapTrendAhead        → "CLOSING"/"STABLE"/"DROPPING" (calc from rolling window)

    // — Fuel Panel (Engineer's Radio) —
    // Data from: SPageFileGraphicEvo + SPageFilePhysicsEvo
    public float FuelLevel             → graphics.FuelLiterCurrentQuantity  // litres
    public float FuelPerLap            → graphics.FuelLiterPerLap           // litres/lap
    public float FuelLapsRemaining     → graphics.LapsPossibleWithFuel      // calculated by game
    public int CurrentLap              → graphics.SessionState.CurrentLap
    public int TotalLaps               → graphics.TotalLapCount
    public string SessionTimeLeft      → graphics.SessionState.TimeLeft     // formatted string

    // — Tyre Panel (GT3 Dash Display) —
    // Data from: SPageFilePhysicsEvo
    public float[] TyreWear            → physics.TyreWear[4]                // 0.0–1.0
    public float[] TyrePressures       → physics.WheelsPressure[4]
    public float[] BrakeTemps          → physics.BrakeTemp[4]
    public string[] TyreTempClass      → PhysicsSnapshotDto.TyreTempI/M/O → "COLD"/"OK"/"HOT"/"PEAK"
    public string TyreCompound         → graphics.TyreLf.TyreCompoundFront (decoded bytes)

    // — Session Bar (Situation Awareness) —
    public string Flag                 → graphics.Flag.ToString()           // enum: AcNoFlag, Blue, Yellow, etc.
    public string GlobalFlag           → graphics.GlobalFlag.ToString()
    public bool IsInPitLane            → graphics.IsInPitLane
    public bool IsLastLap              → graphics.IsLastLap
    public float AirTemperature        → physics.AirTemp
    public float RoadTemperature       → physics.RoadTemp

    // — Damage & Penalty Panel —
    public float DamageBody            → graphics.CarDamage.DamageCenter    // 0.0–1.0
    public float DamageSuspensionFL    → graphics.CarDamage.DamageSuspensionLf
    public float DamageSuspensionFR    → graphics.CarDamage.DamageSuspensionRf
    public float DamageSuspensionRL    → graphics.CarDamage.DamageSuspensionLr
    public float DamageSuspensionRR    → graphics.CarDamage.DamageSuspensionRr
    public string DamageSummary        → thresholds: NONE (<0.05) / LIGHT (<0.20) / MODERATE (<0.50) / HEAVY (≥0.50)
    public int RaceCutGainedTimeMs     → graphics.RaceCutGainedTimeMs       // total penalty time
    public bool IsWrongWay             → graphics.IsWrongWay

    // Update method
    public void UpdateFrom(SPageFilePhysicsEvo p, SPageFileGraphicEvo g)
}
```

**Fairness check** — every value is available to a real driver:
| Field | Real-world equivalent |
|---|---|
| GapAhead/GapBehind | Pit board display |
| Position/TotalDrivers | Timing screen, radio |
| FuelLevel/FuelPerLap | Dash display (BMW M4 GT3 has this) |
| TyreWear/TyrePressures | GT3 dash telemetry page |
| BrakeTemps | Dash warning lights |
| Flag | Flag lights + marshalling |
| Damage | Dash warning lights |
| AirTemp/RoadTemp | Dash display |

### Step 3: Create RaceInfoOverlay Window

**Files**:
- `src/AcEvoFfbTuner/Views/RaceInfoOverlay.xaml`
- `src/AcEvoFfbTuner/Views/RaceInfoOverlay.xaml.cs`

Follow the `WheelCenterOverlay` pattern — transparent, draggable, resizable:

**XAML structure:**
```xml
<Window x:Class="AcEvoFfbTuner.Views.RaceInfoOverlay"
        WindowStyle="None"
        AllowsTransparency="True"
        Background="Transparent"
        Topmost="True"
        ShowInTaskbar="False"
        Width="340" Height="360">

    <Border x:Name="RootBorder" CornerRadius="8" Padding="1"
            Background="#55E67E22"
            MouseLeftButtonDown="OnDragMove"
            MouseWheel="OnMouseWheel">
        <Border x:Name="ContentBorder" CornerRadius="7" Background="#DD0D0D0D">
            <Grid>
                <Grid.RowDefinitions>
                    <RowDefinition Height="Auto" />  <!-- Header -->
                    <RowDefinition Height="*" />      <!-- Panels -->
                    <RowDefinition Height="Auto" />  <!-- Session bar -->
                </Grid.RowDefinitions>

                <!-- Header -->
                <Border Grid.Row="0" Background="#FF161B22" CornerRadius="7,7,0,0" Padding="10,6">
                    <Grid>
                        <TextBlock Text="RACE INFO" FontWeight="Black" Foreground="#FFF0883E" />
                        <StackPanel Orientation="Horizontal" HorizontalAlignment="Right">
                            <Button x:Name="BtnClose" Click="CloseOverlay"
                                    Width="26" Height="26" Background="#FF552222">
                                <TextBlock Text="X" FontWeight="Bold" Foreground="#FFE6EDF3" />
                            </Button>
                        </StackPanel>
                    </Grid>
                </Border>

                <!-- Panel content -->
                <Border Grid.Row="1" Background="#FF0D1117" Padding="10" Margin="0,1,0,1">
                    <Grid>
                        <Grid.ColumnDefinitions>
                            <ColumnDefinition Width="*" />
                            <ColumnDefinition Width="*" />
                        </Grid.ColumnDefinitions>
                        <Grid.RowDefinitions>
                            <RowDefinition Height="Auto" />
                            <RowDefinition Height="Auto" />
                        </Grid.RowDefinitions>

                        <!-- Top-left: GAPS -->
                        <Border Grid.Column="0" Grid.Row="0" Margin="0,0,4,4" Padding="8" Background="#FF161B22"
                                CornerRadius="6">
                            <StackPanel>
                                <TextBlock Text="GAPS" FontSize="10" Foreground="#FF8B949E" />
                                <Grid>
                                    <Grid.ColumnDefinitions>
                                        <ColumnDefinition Width="Auto" />
                                        <ColumnDefinition Width="*" />
                                        <ColumnDefinition Width="Auto" />
                                    </Grid.ColumnDefinitions>
                                    <Grid.RowDefinitions>
                                        <RowDefinition Height="Auto" />
                                        <RowDefinition Height="Auto" />
                                    </Grid.RowDefinitions>
                                    <TextBlock Grid.Row="0" Grid.Column="0" Text="AHEAD" FontSize="10" Foreground="#FF8B949E" />
                                    <TextBlock Grid.Row="0" Grid.Column="1" x:Name="GapAheadText" FontSize="22" FontWeight="Bold"
                                               Foreground="#FF00E676" Text="+0.0" />
                                    <TextBlock Grid.Row="0" Grid.Column="2" x:Name="GapTrendAhead" FontSize="14" Text="→" />
                                    <TextBlock Grid.Row="1" Grid.Column="0" Text="BEHIND" FontSize="10" Foreground="#FF8B949E" />
                                    <TextBlock Grid.Row="1" Grid.Column="1" x:Name="GapBehindText" FontSize="22" FontWeight="Bold"
                                               Foreground="#FFFFD600" Text="+0.0" />
                                    <TextBlock Grid.Row="1" Grid.Column="2" x:Name="GapTrendBehind" FontSize="14" Text="→" />
                                </Grid>
                                <TextBlock x:Name="PositionText" FontSize="13" Foreground="#FFF0883E"
                                           Text="P1 / 20" />
                            </StackPanel>
                        </Border>

                        <!-- Top-right: FUEL -->
                        <Border Grid.Column="1" Grid.Row="0" Margin="4,0,0,4" Padding="8" Background="#FF161B22"
                                CornerRadius="6">
                            <StackPanel>
                                <TextBlock Text="FUEL" FontSize="10" Foreground="#FF8B949E" />
                                <TextBlock x:Name="FuelLevelText" FontSize="28" FontWeight="Bold"
                                           Foreground="#FF4CAF50" Text="-- L" />
                                <Grid>
                                    <Grid.ColumnDefinitions>
                                        <ColumnDefinition Width="*" />
                                        <ColumnDefinition Width="*" />
                                    </Grid.ColumnDefinitions>
                                    <StackPanel Grid.Column="0">
                                        <TextBlock Text="PER LAP" FontSize="9" Foreground="#FF8B949E" />
                                        <TextBlock x:Name="FuelPerLapText" FontSize="14" Foreground="#FFE6EDF3" Text="--" />
                                    </StackPanel>
                                    <StackPanel Grid.Column="1">
                                        <TextBlock Text="LAPS LEFT" FontSize="9" Foreground="#FF8B949E" />
                                        <TextBlock x:Name="FuelLapsText" FontSize="14" Foreground="#FFE6EDF3" Text="--" />
                                    </StackPanel>
                                </Grid>
                            </StackPanel>
                        </Border>

                        <!-- Bottom-left: TYRES -->
                        <Border Grid.Column="0" Grid.Row="1" Margin="0,0,4,0" Padding="8" Background="#FF161B22"
                                CornerRadius="6">
                            <StackPanel>
                                <TextBlock Text="TYRES" FontSize="10" Foreground="#FF8B949E" />
                                <Grid>
                                    <Grid.ColumnDefinitions>
                                        <ColumnDefinition Width="*" />
                                        <ColumnDefinition Width="*" />
                                        <ColumnDefinition Width="*" />
                                        <ColumnDefinition Width="*" />
                                    </Grid.ColumnDefinitions>
                                    <!-- FL -->
                                    <StackPanel Grid.Column="0" Margin="0,0,2,0">
                                        <TextBlock Text="FL" FontSize="9" Foreground="#FF8B949E" />
                                        <TextBlock x:Name="TyreWearFL" FontSize="14" FontWeight="Bold" Text="--" />
                                        <TextBlock x:Name="TyreTempFL" FontSize="10" Foreground="#FF8B949E" Text="--" />
                                    </StackPanel>
                                    <!-- FR -->
                                    <StackPanel Grid.Column="1" Margin="2,0,0,0">
                                        <TextBlock Text="FR" FontSize="9" Foreground="#FF8B949E" />
                                        <TextBlock x:Name="TyreWearFR" FontSize="14" FontWeight="Bold" Text="--" />
                                        <TextBlock x:Name="TyreTempFR" FontSize="10" Foreground="#FF8B949E" Text="--" />
                                    </StackPanel>
                                    <!-- RL -->
                                    <StackPanel Grid.Column="2" Margin="2,0,0,0">
                                        <TextBlock Text="RL" FontSize="9" Foreground="#FF8B949E" />
                                        <TextBlock x:Name="TyreWearRL" FontSize="14" FontWeight="Bold" Text="--" />
                                        <TextBlock x:Name="TyreTempRL" FontSize="10" Foreground="#FF8B949E" Text="--" />
                                    </StackPanel>
                                    <!-- RR -->
                                    <StackPanel Grid.Column="3" Margin="2,0,0,0">
                                        <TextBlock Text="RR" FontSize="9" Foreground="#FF8B949E" />
                                        <TextBlock x:Name="TyreWearRR" FontSize="14" FontWeight="Bold" Text="--" />
                                        <TextBlock x:Name="TyreTempRR" FontSize="10" Foreground="#FF8B949E" Text="--" />
                                    </StackPanel>
                                </Grid>
                                <TextBlock x:Name="TyreCompoundText" FontSize="10" Foreground="#FF8B949E" Text="--" />
                            </StackPanel>
                        </Border>

                        <!-- Bottom-right: DAMAGE + PENALTY -->
                        <Border Grid.Column="1" Grid.Row="1" Margin="4,0,0,0" Padding="8" Background="#FF161B22"
                                CornerRadius="6">
                            <StackPanel>
                                <TextBlock Text="DAMAGE" FontSize="10" Foreground="#FF8B949E" />
                                <TextBlock x:Name="DamageSummaryText" FontSize="20" FontWeight="Bold"
                                           Foreground="#FF00E676" Text="NONE" />
                                <TextBlock x:Name="PenaltyText" FontSize="11" Foreground="#FFFFD600" Text="" />
                            </StackPanel>
                        </Border>
                    </Grid>
                </Border>

                <!-- Session bar (bottom) -->
                <Border Grid.Row="2" Background="#FF161B22" CornerRadius="0,0,7,7" Padding="10,4">
                    <Grid>
                        <Grid.ColumnDefinitions>
                            <ColumnDefinition Width="Auto" />
                            <ColumnDefinition Width="*" />
                            <ColumnDefinition Width="Auto" />
                        </Grid.ColumnDefinitions>
                        <StackPanel Grid.Column="0" Orientation="Horizontal">
                            <TextBlock x:Name="FlagText" FontSize="12" FontWeight="Bold" Foreground="#FF00E676" Text="GREEN" Margin="0,0,8,0" />
                            <TextBlock x:Name="LapText" FontSize="12" Foreground="#FFE6EDF3" Text="Lap 1 / 20" Margin="0,0,8,0" />
                        </StackPanel>
                        <StackPanel Grid.Column="2" Orientation="Horizontal">
                            <TextBlock x:Name="AirTempText" FontSize="11" Foreground="#FF8B949E" Text="Air 24°C" Margin="0,0,8,0" />
                            <TextBlock x:Name="RoadTempText" FontSize="11" Foreground="#FF8B949E" Text="Road 32°C" />
                        </StackPanel>
                    </Grid>
                </Border>
            </Grid>
        </Border>
    </Border>
</Window>
```

**Code-behind** follows the `WheelCenterOverlay` pattern:
- Drag to move (`OnDragMove`)
- Scroll to resize (`OnMouseWheel`)
- Toggle compact mode (double-click header)
- Close button
- `UpdateData(SPageFilePhysicsEvo, SPageFileGraphicEvo)` method dispatches to UI thread
- Template helpers: `FormatWear()`, `FormatTempClass()`, `FormatDamageSummary()`, `GetGapTrendArrow()`

### Step 4: Wire Update from MainViewModel

**File**: `src/AcEvoFfbTuner/ViewModels/MainViewModel.cs`

In `OnUiUpdate` (~line 2987), after the existing update block:

```csharp
private void OnUiUpdate(object? sender, EventArgs e)
{
    var raw = _telemetryLoop.LatestRaw;
    var processed = _telemetryLoop.LatestProcessed;

    if (processed != null && raw != null)
    {
        // ... existing updates (force output, speed, clipping, etc.) ...

        // NEW: Feed RaceInfoOverlay from the stored raw structs
        var physics = _telemetryLoop.LatestPhysicsRaw;
        var graphics = _telemetryLoop.LatestGraphicsRaw;
        if (physics != null && graphics != null)
        {
            _raceInfoOverlay?.UpdateData(physics.Value, graphics.Value);
        }
    }
}
```

### Step 5: Add Overlay Lifecycle to MainViewModel

**File**: `src/AcEvoFfbTuner/ViewModels/MainViewModel.cs`

```csharp
private RaceInfoOverlay? _raceInfoOverlay;

[RelayCommand]
private void ToggleRaceInfoOverlay()
{
    if (_raceInfoOverlay != null)
    {
        _raceInfoOverlay.Close();
        _raceInfoOverlay = null;
    }
    else
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            _raceInfoOverlay = new RaceInfoOverlay();
            _raceInfoOverlay.Closed += (_, _) => _raceInfoOverlay = null;
            _raceInfoOverlay.Show();
        });
    }
}
```

**File**: `src/AcEvoFfbTuner/Views/MainWindow.xaml.cs` — add keyboard shortcut:

```csharp
// In constructor or OnKeyDown handler
// Ctrl+Shift+R toggles the Race Info overlay
if (Keyboard.IsKeyDown(Key.LeftCtrl) && Keyboard.IsKeyDown(Key.LeftShift) && e.Key == Key.R)
{
    _viewModel.ToggleRaceInfoOverlayCommand.Execute(null);
}
```

## 4. Data Source Truth Table

| Overlay Field | SPageFilePhysicsEvo field | SPageFileGraphicEvo field | Notes |
|---|---|---|---|
| GapAhead | — | `GapAhead` | Seconds. Negative = behind. |
| GapBehind | — | `GapBehind` | Seconds. Negative = ahead. |
| Position | — | `CurrentPos` | 1-based race position. |
| TotalDrivers | — | `TotalDrivers` | Number of cars in session. |
| FuelLevel | — | `FuelLiterCurrentQuantity` | Litres remaining. |
| FuelPerLap | — | `FuelLiterPerLap` | Average litres per lap. |
| FuelLapsRemaining | — | `LapsPossibleWithFuel` | Calculated by game. |
| CurrentLap | — | `SessionState.CurrentLap` | Current lap number. |
| TotalLaps | — | `TotalLapCount` | Total laps in race. |
| SessionTimeLeft | — | `SessionState.TimeLeft` | Formatted time string. |
| TyreWear[4] | `TyreWear[0..3]` | — | 0.0 = new, 1.0 = worn out. |
| WheelsPressure[4] | `WheelsPressure[0..3]` | — | PSI or kPa. |
| BrakeTemp[4] | `BrakeTemp[0..3]` | — | Degrees C. |
| TyreTempI/M/O[4] | `TyreTempI/M/O[0..3]` | — | Inner/Middle/Outer. |
| TyreCompound | — | `TyreLf.TyreCompoundFront` | Decoded from bytes. |
| Flag | — | `Flag` / `GlobalFlag` | `AcEvoFlagType` enum. |
| IsInPitLane | — | `IsInPitLane` | Bool. |
| IsLastLap | — | `IsLastLap` | Bool. |
| AirTemp | `AirTemp` | — | Celsius. |
| RoadTemp | `RoadTemp` | — | Celsius. |
| DamageCenter | — | `CarDamage.DamageCenter` | 0.0–1.0. |
| DamageSuspensionLf | — | `CarDamage.DamageSuspensionLf` | 0.0–1.0. |
| RaceCutGainedTimeMs | — | `RaceCutGainedTimeMs` | Track limit penalty time. |
| IsWrongWay | — | `IsWrongWay` | Bool. |
| WaterTemp | `WaterTemp` | — | Engine temp. |
| OilTemperatureC | — | `OilTemperatureC` | Display value. |

## 5. Temperature Classification Logic

From CrewChief's `TyreMonitor` and common data containers (for reference):

```csharp
public enum TyreTempClass
{
    Cold,       // Far below optimal window
    OK,         // In optimal window
    Hot,        // Above optimal but not critical
    Peak,       // Maximum grip temperature (briefly sustainable)
    Overheating // Degrading rapidly
}

// Example thresholds for GT3 (dry) — would need per-car-class calibration
static TyreTempClass ClassifyCoreTemp(float tempC)
{
    if (tempC < 60)  return TyreTempClass.Cold;
    if (tempC < 85)  return TyreTempClass.OK;
    if (tempC < 100) return TyreTempClass.Hot;
    if (tempC < 115) return TyreTempClass.Peak;
    return TyreTempClass.Overheating;
}
```

Color mapping for the overlay:
- Cold → Blue (#4FC3F7)
- OK → Green (#00E676)
- Hot → Yellow (#FFD600)
- Peak → Orange (#F0883E)
- Overheating → Red (#E53935)

## 6. Damage Classification

```csharp
public enum DamageLevel { None, Light, Moderate, Heavy, Destroyed }

static DamageLevel ClassifyDamage(float dmg)
{
    if (dmg < 0.05f)  return DamageLevel.None;
    if (dmg < 0.20f)  return DamageLevel.Light;
    if (dmg < 0.50f)  return DamageLevel.Moderate;
    if (dmg < 0.80f)  return DamageLevel.Heavy;
    return DamageLevel.Destroyed;
}
```

## 7. Gap Trend Calculation

```csharp
// Sliding window of last 20 frames (≈0.3s at 60fps)
private readonly Queue<float> _gapHistory = new(capacity: 20);

public string GetTrend(float currentGap)
{
    _gapHistory.Enqueue(currentGap);
    if (_gapHistory.Count > 20) _gapHistory.Dequeue();
    if (_gapHistory.Count < 5) return "→"; // not enough data

    float first = _gapHistory.Peek();
    float last = _gapHistory.Last();
    float delta = last - first;

    if (Math.Abs(delta) < 0.1f) return "→"; // stable
    return delta < 0 ? "▲" : "▼"; // closing / pulling away
}
```

## 8. CrewChief Bridge (Optional Enhancement)

**File**: `src/AcEvoFfbTuner.Core/Interop/CrewChiefBridge.cs`

An optional IPC module that enriches the overlay data when CrewChief is running alongside:

| Overlay Field | Without CC | With CC (IPC) |
|---|---|---|
| Fuel strategy | Raw fuel level, simple division | Lap-averaged consumption, reserve calc, safety car adjustment |
| Tyre wear classification | Your own threshold logic | Per car-class thresholds from CC's `CarData` |
| Gap trend | Rolling 20-frame average | Session-aware trend (reset on pit stops, accounts for blue flags) |
| Damage classification | Simple threshold | CC's established levels (None/Trivial/Minor/Major/Destroyed) |
| Fuel to finish | FuelLapsRemaining × FuelPerLap | CC's fuel model: accounts for formation lap, SC laps, reserve |
| Tyre temp classification | Generic thresholds | Per-class thresholds (GT3 vs TCR vs GT4) |

**IPC protocol** (named pipe):
1. ACEVO creates `\\.\pipe\AcEvoFfbTuner_RaceInfo` as server
2. CrewChief (if running) connects as client
3. JSON messages exchanged at 10Hz max:
   - CC → ACEVO: `{"type":"fuel_update","fuelToEnd":12.5,"optimalPitLap":18}`
   - CC → ACEVO: `{"type":"tyre_classification","wear":["NEW","GOOD","SCRUBBED","MAJOR"]}`
   - CC → ACEVO: `{"type":"session_phase","phase":"FULL_COURSE_YELLOW"}`
   - CC → ACEVO: `{"type":"gap_trend","ahead":0.5,"behind":-1.2}`
4. ACEVO overlay displays enriched values when CC data is available

**Connection management**:
- Auto-detect: scan running processes for "CrewChiefV4" every 5 seconds
- Auto-reconnect: retry pipe connection every 3 seconds
- Graceful fallback: if CC disconnected, revert to raw shared memory data

## 9. Implementation Order

| Step | What | Effort | Files | Dependencies |
|---|---|---|---|---|
| 1 | Expose LatestPhysicsRaw/LatestGraphicsRaw from TelemetryLoop | 15 min | `TelemetryLoop.cs` | None |
| 2 | Create RaceInfoViewModel | 1 hr | `RaceInfoViewModel.cs` (new) | Step 1 |
| 3 | Create RaceInfoOverlay XAML | 2 hr | `RaceInfoOverlay.xaml` (new) | None (UI only) |
| 4 | Create RaceInfoOverlay code-behind | 1 hr | `RaceInfoOverlay.xaml.cs` (new) | Step 2+3 |
| 5 | Wire update from MainViewModel.OnUiUpdate | 30 min | `MainViewModel.cs` | Step 1+4 |
| 6 | Add toggle command + keyboard shortcut | 30 min | `MainViewModel.cs`, `MainWindow.xaml.cs` | Step 5 |
| 7 | Fix MapGraphics to read AC EVO data | 4 hr | `AssettoCorsaSharedMemoryReader.cs` | Understanding EVO struct |
| 8 | CrewChief IPC bridge | 8 hr | `CrewChiefBridge.cs` (new) | Named pipe experience |

**Total**: ~17 hours for full implementation. Steps 1–6 are ~5 hours for a baseline working overlay. Step 7 fixes the stubbed graphics struct (benefits FFB pipeline too). Step 8 is optional enrichment.

## 10. Fairness Statement

This overlay shows only information the driver would have in a real race:

- **Gaps**: Physical pit boards display gap to car ahead/behind
- **Fuel**: GT3 dashes display fuel level, engineer calculates strategy
- **Tyre data**: GT3 dashes have tyre status pages (BMW M4 GT3, Porsche 911 GT3 R)
- **Position/laps/flags**: Lap counter, flag lights, timing screens
- **Damage**: Dash warning lights for critical damage
- **Track/air temp**: Dash display in modern race cars

**It does NOT:**
- Change FFB output in any way
- Automate any driver decision
- Provide information unavailable to the real driver
- Predict outcomes or calculate optimal strategies automatically
- Modify car setup or controls
