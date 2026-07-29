# Device Detection & Registry — Plan

## Goal
Add a unified device detection system that reports **what is connected** (wheelbase, wheel rim, pedals, haptics, game) and **what is not**, across all supported platforms — without breaking any existing detection or connection code.

---

## Current State (unchanged code paths)

| Detection Target | Current Method | Problem Addressed |
|---|---|---|
| **Wheelbase** | `FfbDeviceManager.TryConnectDevice()` — DI exclusive | OK. Only one device tracked. |
| **Wheel rim** | = `_deviceManager.IsLedControllerConnected` — conflated with LED HID | A no-LED wheel shows "No wheel". Fanatec SDK has `RimProductName` (unused by UI). |
| **Pedals** | `IsPedalConnected` = gas/brake > 1% | Heuristic fires on noise/wobble. No device name shown. |
| **Haptics** (HF8) | Checked once at device connect | No hot-plug detection for HF8. |
| **Game** | `TelemetryLoop.IsGameConnected` | OK — event-driven. |
| **Hot-plug** | None | Device list static after `RefreshDevices()`. |

### Critical finding: `PedalInputManager.IsAnySourceAvailable` is always true

`FfbDevicePedalSource.IsAvailable` returns `_deviceManager != null` (always true — the manager lives for app lifetime). `KeyboardPedalSource.IsAvailable` always returns `_enabled` (default true). So `IsAnySourceAvailable` cannot be used as pedal-presence signal. The gas/brake > 1% heuristic on the VM side is the **only** runtime pedal presence indicator for wheelbase-integrated pedals. For separate USB pedals, `DirectInputPedalSource.DeviceCount > 0` is the signal.

---

## Architecture

```
UI Timer (60fps) ──→ UpdateDeviceStatuses() ──→ reads DeviceRegistry
                                                      │
                                                 DeviceRegistry
                                                  (owned by MainViewModel)
                                                      │
                                          ┌───────────┼──────────────┐
                                          │           │              │
                                     FfbDeviceMgr  Platform       HotPlugMonitor
                                     events        Detectors      (WM_DEVICECHANGE
                                         │        (per vendor)      + 10s poll)
                                         │           │
                                    [unchanged]   uses existing
                                     connection   providers &
                                     & events     detection code
```

### Key rule: new layer reads from existing subsystems, never replaces them

All existing `MainViewModel` properties (`IsDeviceConnected`, `IsWheelConnected`, `IsPedalConnected`, etc.) retain their current values. `UpdateDeviceStatuses()` becomes a thin shim that copies registry → `DeviceStatuses` collection.

---

## New Types (all in `AcEvoFfbTuner.Core/DeviceDetection/`)

```csharp
public enum DeviceCategory { WheelBase, WheelRim, Pedals, HapticPad, ButtonBox, Game }

public enum DeviceConnectionState { Disconnected, Detecting, Connected, Error }

public sealed record DeviceCapabilities {
    public bool HasLeds { get; init; }
    public bool HasScreen { get; init; }
    public bool HasRumbleMotors { get; init; }
    public int LedCount { get; init; }
    public bool SupportsRgb { get; init; }
    public bool SupportsBrightnessControl { get; init; }
    public bool SupportsFlagIndicators { get; init; }
    public bool HasGearDisplay { get; init; }
    public bool SupportsFullForce { get; init; }  // Fanatec FullForce / high-fidelity haptics
}

public sealed class DetectedDevice : ObservableObject {
    public DeviceCategory Category { get; init; }
    public string DeviceId { get; init; }          // "VENDOR_PRODUCT_INSTANCE_GUID"
    public string ProductName { get; init; }
    public string VendorName { get; init; }
    public WheelbaseVendor Vendor { get; init; }

    public DeviceConnectionState State { get; set; }
    public string DisplayName { get; set; }
    public string? ErrorMessage { get; set; }

    public DeviceCapabilities Capabilities { get; init; }

    // No PlatformContext — registry holds extracted data only.
    // Volatile references to providers/controllers live in the VM.
}
```

### DeviceRegistry (owned by MainViewModel)
```csharp
public sealed class DeviceRegistry : ObservableObject {
    public ObservableCollection<DetectedDevice> Devices { get; }

    public event EventHandler<DetectedDevice>? DeviceAdded;
    public event EventHandler<DetectedDevice>? DeviceRemoved;
    public event EventHandler<DetectedDevice>? DeviceStateChanged;
    public event EventHandler? RegistryRefreshed;

    public DetectedDevice? WheelBase => Devices.FirstOrDefault(d => d.Category == DeviceCategory.WheelBase);
    public DetectedDevice? WheelRim  => Devices.FirstOrDefault(d => d.Category == DeviceCategory.WheelRim);
    public DetectedDevice? Pedals    => Devices.FirstOrDefault(d => d.Category == DeviceCategory.Pedals);
    public DetectedDevice? Haptics   => Devices.FirstOrDefault(d => d.Category == DeviceCategory.HapticPad);
    public DetectedDevice? Game      => Devices.FirstOrDefault(d => d.Category == DeviceCategory.Game);

    // Thread-safe: dispatches all collection changes to UI thread
    public void Refresh() { /* re-query all platform detectors */ }

    // Called explicitly from ConnectDevice() AFTER AutoDetectAndSetProvider().
    // Triggers per-platform detector to read provider data (e.g. rim name from Fanatec SDK).
    public void UpdateFromProvider(IFFBProvider? provider) { /* update rim, caps */ }
}
```

### Assumption: one active device per category
The registry tracks at most one device per `DeviceCategory`. In practice the app supports one wheelbase at a time; USB pedals and the wheelbase are different categories. If multiple devices match the same category (e.g. two USB pedal sets), the first one found wins. This matches the current architecture and avoids complexity with no current use case for multi-device.

### IPlatformDeviceDetector (per-vendor strategy)
```csharp
public interface IPlatformDeviceDetector {
    string PlatformName { get; }
    WheelbaseVendor Vendor { get; }
    bool CanDetectWheelRim { get; }
    bool CanDetectPedals { get; }
    void SetProvider(IFFBProvider? provider);          // called after provider init
    IReadOnlyList<DetectedDevice> Detect();            // full scan
    DetectedDevice? DetectWheelRim();                  // rim-specific (lightweight)
}
```

### Device ID scheme
```csharp
// Session-stable: unique per DirectInput instance GUID
DeviceId = $"{vendor}_{StableProductKey(productName)}_{instanceGuid}"

// Where StableProductKey strips revision/version numbers from product name
// Fallback for HID-only: VID_PID_instancePathHash
```

---

## Per-Platform Rim Detection — Capability Table

| Vendor | Detector | Can detect rim? | Method | Detail Level |
|---|---|---|---|---|
| **Fanatec** | `FanatecDeviceDetector` | Yes | `FanatecSdkNative.FSUtilWheelRimProductNameGet()` via provider | Exact model name, LED count, display, rumble |
| **Moza** | `MozaDeviceDetector` | Best-effort | HID VID=0x346E product string + known PID table (KS=0x3509, ES=0x3502, FSR=0x3506, etc.) | Product string or "Moza wheel (unknown)" |
| **Simagic** | `SimagicDeviceDetector` | Best-effort | HID VID=0x3235/0x0483, feature report 0x01 probing, product string matching | "Simagic GT4" or "Simagic wheel (unknown)" |
| **Thrustmaster** | `ThrustmasterDeviceDetector` | Best-effort | HID VID=0x044F, feature report 0x01 returns base+rim type | T818 with SF1000 etc., or generic |
| **Logitech** | `LogitechDeviceDetector` | No | Device is one-piece (base+rim inseparable). Product name = whole unit. | WheelBase entry covers both |
| **Simucube** | `SimucubeDeviceDetector` | No (stub) | SDK pending. HID VID=0x16C0/0x16D0 for presence only. | Unknown rim |
| **Asetek** | `AsetekDeviceDetector` | No (stub) | SDK pending. HID VID=0x2433 for presence. | Unknown rim |
| **Generic** | `GenericDeviceDetector` | No | Only knows DI product name, no SDK. Reports "Wheel (unknown model)". | Unknown rim |

---

## Implementation Tasks

### Phase 1 — Core Types (unchanged scope)

**Task 1** — Create `AcEvoFfbTuner.Core/DeviceDetection/` files:
- `DeviceCategory.cs`, `DeviceConnectionState.cs`, `DeviceCapabilities.cs`, `DetectedDevice.cs`, `DeviceRegistry.cs`, `IPlatformDeviceDetector.cs`

**Task 2** — `PlatformDeviceDetectorFactory.cs`
- `static IPlatformDeviceDetector Create(WheelbaseVendor vendor, IFFBProvider? provider)`
- Each detector stores the provider reference for later queries
- FanatecDetector stores it as `FanatecProvider` cast; others may ignore

### Phase 2 — Platform Detectors

**Task 3** — `FanatecDeviceDetector.cs`
- Receives `FanatecProvider` via `SetProvider(IFFBProvider?)`
- `DetectWheelRim()` reads `provider.RimProductName`, `HasRimRevLeds`, `HasRimLedDisplay`, `HasRumbleMotors`
- Populates `DetectedDevice` with `Category = WheelRim`, proper `DeviceCapabilities`
- Tooltip shows: "Fanatec {RimProductName} | Rev LEDs | Gear Display | Rumble"

**Task 4** — `MozaDeviceDetector.cs`
- `DetectWheelRim()` enumerates HID VID=0x346E devices, matches PID against known Moza wheel table
- Does NOT use Moza SDK (SDK has no wheel-type query API)
- Known PID-to-capabilities mapping:

  | PID | Wheel Model | DisplayName | Capabilities |
  |---|---|---|---|
  | 0x3509 | KS | "Moza KS Steering Wheel" | 10 LEDs, RGB, dimming, flags |
  | 0x3502 | ES | "Moza ES Steering Wheel" | 10 LEDs (base), dimming |
  | 0x3506 | FSR | "Moza FSR Steering Wheel" | 10 LEDs, RGB, dimming, flags, screen |
  | 0x3505 | GS | "Moza GS Steering Wheel" | 10 LEDs, RGB, dimming, flags |
  | 0x3504 | CS | "Moza CS Steering Wheel" | 10 LEDs, dimming |

- If PID unknown, product string from HID descriptor is used as display name, capabilities default to 10 LEDs basic.
- Fallback: "Moza wheel (unknown)"

**Task 5** — `SimagicDeviceDetector.cs`
- Enumerates HID VID=0x3235/0x0483
- Attempts feature report 0x01 to read wheel model
- Known model name patterns: GT4, FX, FX Pro, GTS, Neo, Neo X Hub, Alpha, Alpha Mini, Alpha U
- Fallback: "Simagic wheel (unknown model)"

**Task 6** — `ThrustmasterDeviceDetector.cs`
- HID VID=0x044F, feature report 0x01 probes for base + rim type
- Known PID-to-model mapping for bases (T818=0x0200, T300=0x0206, etc.)
- Known rim names via feature report
- Fallback: "Thrustmaster wheel"

**Task 7** — `LogitechDeviceDetector.cs`
- HID VID=0x046D, match product name: G27, G29, G920, G923, G Pro
- One-piece device — WheelRim entry mirrors WheelBase (same product name)
- Capabilities: LED count from known model (5 for G923, 0 for G27/G29/G920)

**Task 8** — `GenericDeviceDetector.cs`
- No SDK. Only knows `productName` from DI + vendor from `WheelbaseFactory.DetectVendor`
- `DetectWheelRim()` returns null (cannot distinguish rim from base)
- `Detect()` returns WheelBase entry with generic capabilities (DI only, no LEDs/screen)
- Used as fallback for Simucube, Asetek, VNM, and unknown vendors

### Design note: Detector error handling

All `Detect()` / `DetectWheelRim()` implementations **must catch all exceptions**. On failure:
- Log the exception to the detector's diagnostic log
- Return `null` for `DetectWheelRim()` (rim becomes unknown)
- `Detect()` returns an entry with `State = Error` and `ErrorMessage` set
- The registry continues with other detectors — one vendor's failure does not block others

### Phase 3 — HotPlugMonitor

**Task 9** — `AcEvoFfbTuner/Services/HotPlugMonitor.cs`
- Created by `MainViewModel` (no DI container — follows existing pattern):
  ```csharp
  public HotPlugMonitor HotPlugMonitor { get; } = new();
  ```
- Wraps `WM_DEVICECHANGE` via `HwndSource` hook. Registration happens in `MainWindow.xaml.cs` after the window handle is available:
  ```csharp
  SourceInitialized += (s, e) => {
      var vm = DataContext as MainViewModel;
      var hwndSource = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
      vm?.HotPlugMonitor.Register(hwndSource);
  };
  ```
- Polling fallback timer (10s interval) starts on construction — fires even without a window handle
- Fires `DeviceArrived` / `DeviceRemoved` event args with `HardwareId` and `DevicePath`
- Events coalesce via 500ms debounce before firing registry refresh
- Skips HID re-enumeration when FFB output is active (checks `_deviceManager.IsDeviceAcquired`)

### Phase 4 — Registry Lifecycle & VM Integration

**Task 10** — Wire `DeviceRegistry` in `MainViewModel`

- `DeviceRegistry` created in VM constructor, pre-populated with 5 default entries (all `State = Disconnected`):
  ```csharp
  _deviceRegistry = new DeviceRegistry();
  _deviceRegistry.AddDefault(DeviceCategory.WheelBase,  "No wheelbase");
  _deviceRegistry.AddDefault(DeviceCategory.WheelRim,   "No wheel");
  _deviceRegistry.AddDefault(DeviceCategory.Pedals,     "No pedals");
  _deviceRegistry.AddDefault(DeviceCategory.HapticPad,  "No haptics");
  _deviceRegistry.AddDefault(DeviceCategory.Game,       "No game");
  ```

- Subscribes to events:
  | Event | Handler |
  |---|---|
  | `_deviceManager.DeviceConnected` | Adds/updates WheelBase entry with product name + vendor; triggers peripheral detection on background thread |
  | `_deviceManager.DeviceDisconnected` | Sets all non-Game entries to `State = Disconnected` |
  | `_deviceManager.DeviceRequiresReconnect` | Sets WheelBase + WheelRim to `State = Detecting` |
  | `_telemetryLoop.GameConnectionChanged` | Updates Game entry (name + state) |
  | `HotPlugMonitor.DeviceArrived` | Debounce 500ms, then `RefreshDevices()` |
  | `HotPlugMonitor.DeviceRemoved` | Debounce 500ms, then `RefreshDevices()` |

- **Provider wiring: explicit call in `ConnectDevice()`**. After `_telemetryLoop.AutoDetectAndSetProvider()` returns, the VM calls:
  ```csharp
  _telemetryLoop.AutoDetectAndSetProvider();
  _deviceRegistry.UpdateFromProvider(_telemetryLoop.ActiveProvider);  // NEW
  ```
  The `DeviceConnected` event fires before the provider exists, so the registry uses it only for the basic WheelBase entry. `UpdateFromProvider` triggers the per-platform detector to read provider-specific data (Fanatec rim name, SDK capabilities). If `ActiveProvider` is null (provider init failed), `UpdateFromProvider(null)` is safe — rim detection is skipped, WheelBase entry stays as-is.

- **Registry.RefreshDevices()** (called from HotPlugMonitor or manually):
  1. Logs "device scan starting"
  2. Calls `_deviceManager.EnumerateFfbDevices()` — updates AvailableDevices/PanicDevices (existing behavior)
  3. Calls `DirectInputPedalSource.RefreshDevices()` — re-enumerates DI pedal sources
  4. Retrieves per-platform detector and calls `DetectWheelRim()` — re-checks HID for attached rims
  5. Calls `_deviceManager.Hf8Controller.TryConnect()` — retries HF8 connection
  6. Updates all registry entries
  7. Fires `RegistryRefreshed` event
  8. Guards against concurrent refresh via `Interlocked.CompareExchange`

- Thread safety: all `ObservableCollection` mutations dispatch to `Application.Current.Dispatcher` and never touch the collection from background threads

**Task 11** — Update `DeviceStatus` model and `UpdateDeviceStatuses()`

First, change `DeviceStatus.TooltipText` from a computed property to a settable string so the VM can construct rich tooltips from registry data:
```csharp
// Before: public string TooltipText => $"{Name}\n{(IsConnected ? "Connected" : "Disconnected")}";
// After:
private string _tooltipText = "";
public string TooltipText { get => _tooltipText; set => SetProperty(ref _tooltipText, value); }
```

Then, `UpdateDeviceStatuses()` builds tooltips from full registry data:
```csharp
private void UpdateDeviceStatuses() {
    var wb = _deviceRegistry.WheelBase;
    DeviceStatuses[0].IsConnected = wb?.State == DeviceConnectionState.Connected;
    DeviceStatuses[0].Name = wb?.DisplayName ?? "No wheelbase";
    DeviceStatuses[0].TooltipText = BuildTooltip(wb);

    // Wheel rim: prefer registry, fall back to LED controller for backward compat
    var rim = _deviceRegistry.WheelRim;
    bool rimConnected = rim?.State == DeviceConnectionState.Connected;
    DeviceStatuses[1].IsConnected = rimConnected || _deviceManager.IsLedControllerConnected;
    DeviceStatuses[1].Name = rimConnected ? rim!.DisplayName : WheelDisplayName;
    DeviceStatuses[1].TooltipText = BuildWheelRimTooltip(rim, _deviceManager);

    // Pedals: separate USB detected first, then wheelbase-integrated via heuristic
    var diPedalSource = _telemetryLoop.PedalInput.Sources
        .OfType<DirectInputPedalSource>().FirstOrDefault();
    bool hasSeparateUsbPedals = diPedalSource?.DeviceCount > 0;
    bool hasWheelbasePedals = IsDeviceConnected
        && _telemetryLoop.LatestRaw is { } raw
        && (raw.GasInput > 0.01f || raw.BrakeInput > 0.01f);
    DeviceStatuses[2].IsConnected = hasSeparateUsbPedals || hasWheelbasePedals;
    DeviceStatuses[2].Name = hasSeparateUsbPedals
        ? diPedalSource!.DeviceName
        : (hasWheelbasePedals ? "Wheelbase-integrated pedals" : "No pedals");
    DeviceStatuses[2].TooltipText = DeviceStatuses[2].IsConnected
        ? $"{DeviceStatuses[2].Name}\nConnected"
        : "No pedals\nDisconnected";

    // Haptics
    var hap = _deviceRegistry.Haptics;
    bool hapConnected = hap?.State == DeviceConnectionState.Connected || Hf8Connected;
    DeviceStatuses[3].IsConnected = hapConnected;
    DeviceStatuses[3].Name = hap?.DisplayName ?? "No haptics";
    DeviceStatuses[3].TooltipText = BuildTooltip(hap);

    // Game
    var game = _deviceRegistry.Game;
    bool gameConnected = game?.State == DeviceConnectionState.Connected || IsGameConnected;
    DeviceStatuses[4].IsConnected = gameConnected;
    DeviceStatuses[4].Name = game?.DisplayName ?? "No game";
    DeviceStatuses[4].TooltipText = BuildTooltip(game);
}

// Build a tooltip that shows capabilities when connected, errors when errored
private static string BuildTooltip(DetectedDevice? d) {
    if (d == null) return "Disconnected";
    return d.State switch {
        DeviceConnectionState.Connected => $"{d.DisplayName}\nConnected\n{d.VendorName} | {CapabilitiesSummary(d.Capabilities)}",
        DeviceConnectionState.Error => $"{d.DisplayName}\nError: {d.ErrorMessage}",
        _ => $"{d.DisplayName}\n{d.State}"
    };
}

private static string BuildWheelRimTooltip(DetectedDevice? rim, FfbDeviceManager mgr) {
    if (rim?.State == DeviceConnectionState.Connected)
        return $"{rim.DisplayName}\nConnected\nRim: {rim.ProductName}\n{CapabilitiesSummary(rim.Capabilities)}";
    if (mgr.IsLedControllerConnected)
        return $"{mgr.LedVendorDisplayName}\nConnected\nLED controller active, rim model unknown";
    return "No wheel\nDisconnected";
}

private static string CapabilitiesSummary(DeviceCapabilities c) {
    var parts = new List<string>();
    if (c.HasLeds) parts.Add($"{c.LedCount} LEDs" + (c.SupportsRgb ? " RGB" : ""));
    if (c.HasScreen) parts.Add("Screen");
    if (c.HasRumbleMotors) parts.Add("Rumble");
    if (c.HasGearDisplay) parts.Add("Gear display");
    if (c.SupportsBrightnessControl) parts.Add("Dimming");
    if (c.SupportsFlagIndicators) parts.Add("Flags");
    if (c.SupportsFullForce) parts.Add("FullForce");
    return parts.Count > 0 ? string.Join(" | ", parts) : "DirectInput";
}
```

### Phase 5 — Pedal Detection Improvement

**Task 12** — Pedal source detection

Three changes:

1. **Fix `FfbDevicePedalSource.IsAvailable`** to actually reflect wheelbase acquisition:
   ```csharp
   // Before: public bool IsAvailable => _deviceManager != null;
   // After:  public bool IsAvailable => _deviceManager?.IsDeviceAcquired == true;
   ```
   Safe: `IsAvailable` is checked in `PedalInputManager.TryGetState()`'s priority loop, which falls through to other sources if the wheelbase isn't acquired. `ReadAllAxes()` would return null anyway when not acquired — this just moves the check earlier.

2. **Add `RefreshDevices()` to `DirectInputPedalSource`** to support hot-plug re-enumeration:
   ```csharp
   public void RefreshDevices() {
       _allDevices.Clear();
       _initialized = false;  // force re-initialize
       Initialize();          // re-enumerates all DI devices
   }
   ```
   Called from `DeviceRegistry.RefreshDevices()` during hot-plug detection so newly plugged USB pedals are found.

3. **Registry queries `DirectInputPedalSource.DeviceCount`** as the primary USB pedal signal. The existing gas/brake > 1% heuristic remains as the fallback for wheelbase-integrated pedals (which lack separate USB enumeration).
   ```csharp
   var diPedalSource = _pedalInputManager.Sources
       .OfType<DirectInputPedalSource>().FirstOrDefault();
   bool hasSeparateUsbPedals = diPedalSource?.DeviceCount > 0;
   ```

### Phase 6 — Dashboard Wheel Image

**Task 13** — Select wheel image from detected rim
- New VM property `DashboardWheelImageSource` computed from `_deviceRegistry.WheelRim`
- Mapping:
  - Rim vendor = Moza AND (KS, FSR, CS wheel) → `MOZA-KS-PRO_1.png`
  - Rim vendor = Fanatec + any → `FanCSLElite.png`
  - DeviceName contains "G Pro" → `GPro.png`
  - DeviceName contains "G27" → `G27.png`
  - Unknown → random from existing pool (current behavior preserved)
- `HomePage.xaml.cs` removes the random selection code, reads from VM property instead

### Phase 7 — Devices Page Enhancement

**Task 14** — Add "Connected Devices" read-only summary tab
- New section in `DevicesPage.xaml` showing all `DeviceRegistry.Devices` entries
- Each entry: icon + name + vendor + connection state + capability badges
- Tooltip with platform-specific details

---

## Thread Safety & Race Conditions

| Risk | Mitigation |
|---|---|
| HID enumeration during FFB output | `HotPlugMonitor` checks `_deviceManager.IsDeviceAcquired` before triggering HID scan; delays if output active |
| Registry update from background thread | All `ObservableCollection` mutations dispatched via `Application.Current.Dispatcher.Invoke` |
| Provider reference in detector becomes stale | VM calls `_deviceRegistry.UpdateFromProvider(null)` on disconnect, clears rim entry; calls `UpdateFromProvider(provider)` on reconnect with fresh provider |
| Hot-plug storm (device repeatedly appears/disappears) | 500ms debounce timer on `HotPlugMonitor` event; only fires registry refresh after quiescence |
| Concurrent `RefreshDevices()` calls | Registry uses `Interlocked.CompareExchange` guard: only one refresh runs at a time |

---

## Failure Modes

| Scenario | Behaviour |
|---|---|
| **Wheelbase, no pedals** | WheelBase = Connected. Pedals = Disconnected. Pedal silhouette dims. PedalName = "No pedals". Heuristic stays false (no gas/brake input). |
| **Pedals USB, no wheelbase** | `DirectInputPedalSource.DeviceCount > 0`. Pedals = Connected with device name. WheelBase = Disconnected. Wheelbase silhouette dims. |
| **Wheelbase-integrated pedals (RJ12)** | WheelBase = Connected. No separate USB pedal device. Pedal detection relies on gas/brake > 1% heuristic. Registry reports "Wheelbase-integrated pedals". |
| **Fanatec base, unknown rim** | `RimProductName` null/empty → set WheelRim State=Connected, DisplayName="Fanatec wheel". LED state from `WheelLedController` still drives `IsWheelConnected` fallback. |
| **Moza wheel, PitHouse running** | Existing path: serial locked → SDK fallback → HID fallback. Rim detection reads HID PID table → match or "Moza wheel". |
| **Moza wheel, PitHouse closed** | Serial port available. Rim detection same HID PID path. SDK unavailable but LEDs work via serial. |
| **HF8 plugged after wheelbase** | HotPlugMonitor → Registry refreshes → re-checks `Hf8HapticController.TryConnect()` → updates Haptics entry. |
| **Device lost during gameplay** | Existing `DeviceRequiresReconnect` flow handles it. On reconnect success, Registry gets `DeviceConnected` event and refreshes peripherals. |
| **Game running, no wheelbase** | Game entry = Connected. All others = Disconnected. Game silhouette lit, others dim. |
| **Keyboard pedal source only** | Registry filters out `SourceType.Keyboard` from pedal detection. Pedals = Disconnected until a real pedal source appears. |
| **Simagic wheel, unknown model** | HID product string used as DisplayName. WheelRim State=Connected but named "Simagic wheel (unknown model)". |

---

## Validation

1. **Build**: `dotnet clean AcEvoFfbTuner.slnx -c Release -q 2>&1; dotnet build AcEvoFfbTuner.slnx -c Release`
2. **Startup**: All 5 entries in `DeviceStatuses` show "Disconnected" with dim silhouettes
3. **Connect Fanatec base**: WheelBase lights up. WheelRim shows actual rim name (e.g., "CSL Elite Steering Wheel McLaren GT3"). Pedal and Haptics entries populate if detected. Game entry still dim.
4. **Connect Moza base**: WheelBase lights up. WheelRim shows model name from HID PID table. Same for other entries.
5. **Connect generic/base**: WheelBase lights up. WheelRim shows "Wheel (unknown model)". Pedal heuristic fallback active.
6. **Hot-plug**: Unplug USB → within 10s, device dims in UI. Plug back → within 10s, device re-lights.
7. **Dashboard wheel**: Image matches actual rim (Fanatec rim → Fanatec dashboard image, Moza KS wheel → KS image, unknown → random fallback).
8. **Pedal only, no wheelbase**: Launch app without wheelbase connected, plug USB pedals → Pedal silhouette lights up (shows device name from `DirectInputPedalSource`), Wheelbase stays dim.
9. **Wheelbase + no pedals (RJ12 disconnected)**: Wheelbase connects normally. `FfbDevicePedalSource.IsAvailable` is true (wheelbase acquired). Heuristic stays false (no input detected). Pedal silhouette stays dim with tooltip "No pedal input detected — press pedals or check connection".
10. **Tooltips**: Hover over each device silhouette shows detailed info (vendor, model, capabilities, SDK version for Fanatec/Moza).
11. **Detector exception recovery**: If a platform detector throws, registry sets that entry to `State = Error` with `ErrorMessage` in the tooltip. Other detectors continue unaffected.

---

## Testability

- `IPlatformDeviceDetector` and `DeviceRegistry` accept references to existing objects (deviceManager, provider, pedalInputManager) — they can be mocked or stubbed in unit tests.
- The registry's `UpdateFromProvider(IFFBProvider?)` allows injecting a mock provider to test rim detection without real hardware.
- `DirectInputPedalSource.DeviceCount` can be verified against a known test DI environment.
- HotPlugMonitor's polling fallback can be triggered programmatically via a test helper method.
- UI integration tests should use the existing app with physical hardware for end-to-end verification.

## Implementation Order

1. **Phase 1** (Tasks 1-2) — Core types, DeviceRegistry, PlatformDeviceDetectorFactory
2. **Phase 2** (Tasks 3-8) — Per-platform detectors (Fanatec first, Moza second, others in any order)
3. **Phase 3** (Task 9) — HotPlugMonitor
4. **Phase 4** (Tasks 10-11) — VM wiring + UpdateDeviceStatuses rewrite
5. **Phase 5** (Task 12) — Pedal source name exposure
6. **Phase 6** (Task 13) — Dashboard wheel image
7. **Phase 7** (Task 14) — Devices page summary

## Out of Scope

- **Game-specific per-car profile auto-load based on wheel rim** (future concern)
- **Wheel rim screen support** (future — this plan adds `HasScreen` to capabilities for data, but no screen rendering)
- **Button box detection** (`DeviceCategory.ButtonBox` defined but no detectors — hook point for future)
- **Refactoring `FfbDeviceManager` / `WheelLedController`** — left entirely untouched
