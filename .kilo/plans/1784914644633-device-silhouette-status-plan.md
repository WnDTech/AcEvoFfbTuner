# Device Silhouette Connection Status Visualizer — Plan

## Goal
Replace the "THE GARAGE / Hardware Insight Strip" section of the Home page with a professional device connection visualizer showing silhouettes of each physical component (wheelbase, wheel, pedals, haptics, game). Each silhouette glows when connected, dims when disconnected, and shows a tooltip on hover.

---

## Devices to Visualize

| Silhouette | Connection Source (ViewModel) | Display Name Source |
|---|---|---|
| **Wheelbase** (FFB motor/base) | `IsDeviceConnected` | `DeviceName` |
| **Wheel** (rim + LED controller) | `_deviceManager.IsLedControllerConnected` (needs VM exposure) | `_deviceManager.LedVendorDisplayName` |
| **Pedals** | `_telemetryLoop.PedalInput.IsAnySourceAvailable` (needs VM exposure) | Pedal source `DeviceName` (first available) |
| **Haptics** (HF8 pad) | `Hf8Connected` | `Hf8ConnectionStatus` |
| **Game** (telemetry session) | `IsGameConnected` | `GameDisplayName` |

---

## Implementation Tasks

### Task 1 — Create DeviceStatus Model
**File:** `src/AcEvoFfbTuner/Models/DeviceStatus.cs` (new)

```csharp
public enum DeviceIconType { Wheelbase, Wheel, Pedals, Haptics, Game }

public sealed class DeviceStatus : ObservableObject
{
    public DeviceIconType IconType { get; init; }
    public bool IsConnected { get; set; }
    public string Name { get; set; } = "";
    public string TooltipText => $"{Name}\n{(IsConnected ? "Connected" : "Disconnected")}";
}
```

### Task 2 — Add Missing VM Properties
**File:** `src/AcEvoFfbTuner/ViewModels/MainViewModel.cs`

Add observable properties:
- `IsWheelConnected` — backed by `_deviceManager.IsLedControllerConnected` (mirror via timer or callback)
- `IsPedalConnected` — reads `_telemetryLoop.PedalInput.IsAnySourceAvailable`
- `PedalName` — from first available pedal source's `DeviceName`
- `WheelDisplayName` — from `_deviceManager.LedVendorDisplayName`

Wire up `PropertyChanged` for these in the UI update tick (`OnUiUpdate`) so they stay current.

### Task 3 — Add DeviceStatus Collection
**File:** `src/AcEvoFfbTuner/ViewModels/MainViewModel.cs`

Add:
```csharp
public ObservableCollection<DeviceStatus> DeviceStatuses { get; } = new();
```

In the constructor, initialize with 5 entries (one per `DeviceIconType`). Update the `IsConnected` and `Name` for each entry inside `OnUiUpdate` (the 33ms timer tick).

### Task 4 — Create Silhouette Path Geometries
**File:** `src/AcEvoFfbTuner/Resources/SilhouettePaths.cs` (new)

Static class with `StreamGeometry` factory methods returning `Geometry` for each `DeviceIconType`:

- **Wheelbase**: Rectangular DD motor profile with cooling fin lines
- **Wheel**: Circular steering wheel shape with spokes
- **Pedals**: Three angled pedal shapes
- **Haptics**: Rectangular seat cushion pad shape
- **Game**: Monitor/display icon

Each geometry returns a simplified vector silhouette at a standard 48x48 or 64x64 viewbox.

### Task 5 — Create BoolToOpacityMultiConverter (optional)
**File:** `src/AcEvoFfbTuner/Converters/BoolToOpacityConverter.cs`

```csharp
// Connected=true → 1.0, Connected=false → 0.25
public class BoolToOpacityConverter : IValueConverter
```

(This can also be done with XAML DataTriggers, but a converter is cleaner.)

### Task 6 — Replace THE GARAGE Section in HomePage.xaml

Replace the entire "═══ ZONE 3: THE GARAGE ═══" section (lines 464–571 in current `HomePage.xaml`) with the new device visualizer:

```xml
<!-- ═══ DEVICE STATUS VISUALIZER ═══ -->
<Border Background="#FF1C2128" CornerRadius="8" Padding="24,16" Margin="0,0,0,16"
        BorderThickness="1" BorderBrush="#FF30363D">
    <ItemsControl ItemsSource="{Binding DeviceStatuses}">
        <ItemsControl.ItemsPanel>
            <ItemsPanelTemplate>
                <StackPanel Orientation="Horizontal" HorizontalAlignment="Center"/>
            </ItemsPanelTemplate>
        </ItemsControl.ItemsPanel>
        <ItemsControl.ItemTemplate>
            <DataTemplate DataType="{x:Type models:DeviceStatus}">
                <Border ToolTip="{Binding TooltipText}" ...>
                    <Grid>
                        <!-- Silhouette Path (always visible, dim/bright via Opacity) -->
                        <Path Data="{Binding IconType, Converter={...}}"
                              Fill="#FFE6EDF3"
                              Opacity="{Binding IsConnected, Converter={...}}"
                              ... />
                        <!-- Glow effect when connected -->
                        <Path.Effect>
                            <DropShadowEffect ShadowDepth="0"
                                              Color="{Binding IsConnected, Converter={...}}"
                                              Opacity="{Binding IsConnected, ...}"
                                              BlurRadius="12"/>
                        </Path.Effect>
                        <!-- Label below icon -->
                        <TextBlock Text="{Binding Name}" FontSize="10" ... />
                    </Grid>
                </Border>
            </DataTemplate>
        </ItemsControl.ItemTemplate>
    </ItemsControl>
</Border>
```

**Behavior spec:**
- Gray/fully-dim when `IsConnected == false` (opacity ~0.2, no glow)
- Full white/bright when `IsConnected == true` (opacity 1.0, blue glow)
- On hover: `ToolTip` shows "DeviceName\nConnected" or "DeviceName\nDisconnected"
- A subtle animated pulse glow on connected devices (optional, use a `DoubleAnimation` on `DropShadowEffect.Opacity`)

### Task 7 — Add BoolToColorConverter for Glow Color
```csharp
// Connected → #FF79C0FF (blue glow), Disconnected → transparent
```

---

## Edge Cases / Failure Modes

| Scenario | Handling |
|---|---|
| Pedal source not yet registered | `IsPedalConnected` defaults to `false`, name = "No pedals" |
| Wheel LED controller not detected | `IsWheelConnected` false, name = "No wheel detected" |
| No wheelbase connected (but game is) | Wheelbase silhouette dims, game silhouette lights up |
| HF8 disconnected mid-session | Haptics silhouette dims on next UI tick |
| All devices disconnected | All silhouettes dim, no glow — clear visual indication |
| Rapid connect/disconnect | UI updates at 33ms timer rate — no event-storm risk |

---

## What Stays / What Goes

- **Keep**: Glass cockpit PFD (the instrument panel), Setup Wizard card, Update banner
- **Remove**: THE GARAGE section (lines 464–571 in `HomePage.xaml`) — its info is redundant with the new visualizer + existing annunciator strip
- **Keep**: Bottom annunciator strip in the PFD (lines 282–381) — it serves as a compact status line alongside the new visualizer
- **Keep**: Connection pills in the header bar (MainWindow) — they're for quick-access interactions

---

## Validation

1. **Build**: Run `dotnet clean AcEvoFfbTuner.slnx -c Release -q 2>&1; dotnet build AcEvoFfbTuner.slnx -c Release`
2. **Visual check**: Launch app, observe all 5 silhouettes in dim state on first load
3. **Connect wheelbase**: Wheelbase silhouette lights up (blue glow), name appears
4. **Connect game**: Game silhouette lights up (blue glow)
5. **Hover each**: Tooltip shows correct device name and connected/disconnected
6. **Disconnect**: Silhouette returns to dim state with no glow
7. **Verify no regression**: All other pages (FFB Tuning, Devices, Settings, etc.) unchanged
