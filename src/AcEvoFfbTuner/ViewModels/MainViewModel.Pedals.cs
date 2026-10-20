using AcEvoFfbTuner.Core;
using AcEvoFfbTuner.Core.Config;
using AcEvoFfbTuner.Core.PedalHaptics;
using AcEvoFfbTuner.Core.PedalInput;
using AcEvoFfbTuner.Core.PedalInput.Sources;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AcEvoFfbTuner.ViewModels;

public sealed partial class MainViewModel
{
    // ── Pedal Config (from global pedal_config.json) ──
    [ObservableProperty] private bool _pedalInputEnabled;
    [ObservableProperty] private float _pedalGasDeadzone;
    [ObservableProperty] private float _pedalBrakeDeadzone;
    [ObservableProperty] private float _pedalClutchDeadzone;
    [ObservableProperty] private float _pedalGasMin;
    [ObservableProperty] private float _pedalGasMax = 1.0f;
    [ObservableProperty] private float _pedalBrakeMin;
    [ObservableProperty] private float _pedalBrakeMax = 1.0f;
    [ObservableProperty] private float _pedalClutchMin;
    [ObservableProperty] private float _pedalClutchMax = 1.0f;
    [ObservableProperty] private float _pedalGasSmoothing = 0.85f;
    [ObservableProperty] private float _pedalBrakeSmoothing = 0.85f;
    [ObservableProperty] private float _pedalClutchSmoothing = 0.5f;
    [ObservableProperty] private bool _pedalGasInvert;
    [ObservableProperty] private bool _pedalBrakeInvert;
    [ObservableProperty] private bool _pedalClutchInvert;

    // ── Pedal Axis Mapping (from DirectInputPedalSource.AxisMap) ──
    [ObservableProperty] private string _pedalGasAxis = "Y";
    [ObservableProperty] private string _pedalBrakeAxis = "Z";
    [ObservableProperty] private string _pedalClutchAxis = "Rx";
    public string[] PedalAvailableAxes => DirectInputPedalSource.AxisMap.AvailableAxes;

    // ── Live Pedal State (updated from TelemetryLoop ~30fps) ──
    [ObservableProperty] private float _pedalLiveGas;
    [ObservableProperty] private float _pedalLiveBrake;
    [ObservableProperty] private float _pedalLiveClutch;
    [ObservableProperty] private string _pedalLiveSource = "—";
    [ObservableProperty] private string _pedalDiagnosticInfo = "";
    [ObservableProperty] private string _pedalDeviceInfo = "";

    // ── Separate-USB pedal device picker ("Auto" + detected devices).
    // Wheelbase-routed pedals need no selection — they follow the wheel.
    [ObservableProperty] private List<string> _pedalUsbDeviceNames = ["Auto"];
    [ObservableProperty] private int _pedalUsbDeviceIndex;
    [ObservableProperty] private bool _pedalUsbDevicePresent;

    // ── Setup-card live physical state (ignores the Enabled toggle, so the
    // AC setup step verifies real hardware even in telemetry-pedal mode) ──
    [ObservableProperty] private float _setupPedalGas;
    [ObservableProperty] private float _setupPedalBrake;
    [ObservableProperty] private float _setupPedalClutch;
    [ObservableProperty] private string _setupPedalSource = "—";

    // ── Haptic Routing Gains (per-signal → pedal, stored in LiveServer.HapticRouteConfig) ──
    [ObservableProperty] private float _pedalMasterBrakeGain = 1.0f;
    [ObservableProperty] private float _pedalMasterGasGain = 1.0f;
    [ObservableProperty] private float _pedalMasterClutchGain = 1.0f;
    [ObservableProperty] private float _pedalAbsBrakeGain = 1.0f;
    [ObservableProperty] private float _pedalTcGasGain = 1.0f;
    [ObservableProperty] private float _pedalCurbBothGain = 0.5f;
    [ObservableProperty] private float _pedalRoadBrakeGain = 0.3f;
    [ObservableProperty] private float _pedalScrubGasGain = 0.4f;
    [ObservableProperty] private float _pedalBrakePressureGain = 1.0f;
    [ObservableProperty] private float _pedalThrottlePositionGain = 0.0f;
    [ObservableProperty] private float _pedalEngineRpmGain = 0.0f;

    // ── Live axis mapping — update the DI source immediately on ComboBox change ──
    partial void OnPedalGasAxisChanged(string value) => ApplyAxisMapping();
    partial void OnPedalBrakeAxisChanged(string value) => ApplyAxisMapping();
    partial void OnPedalClutchAxisChanged(string value) => ApplyAxisMapping();

    private void ApplyAxisMapping()
    {
        var diSource = _telemetryLoop?.PedalInput?.Sources
            .OfType<DirectInputPedalSource>().FirstOrDefault();
        if (diSource?.Mapping != null)
        {
            diSource.Mapping.GasAxis = PedalGasAxis;
            diSource.Mapping.BrakeAxis = PedalBrakeAxis;
            diSource.Mapping.ClutchAxis = PedalClutchAxis;
        }
        // Same mapping drives the wheelbase source, so axis choice works for
        // any wheelbase (Moza or otherwise) — not just separate USB pedals.
        var wbSource = _telemetryLoop?.PedalInput?.Sources
            .OfType<FfbDevicePedalSource>().FirstOrDefault();
        if (wbSource?.Mapping != null)
        {
            wbSource.Mapping.GasAxis = PedalGasAxis;
            wbSource.Mapping.BrakeAxis = PedalBrakeAxis;
            wbSource.Mapping.ClutchAxis = PedalClutchAxis;
        }
    }

    // ── Methods ──

    public void LoadPedalConfig()
    {
        var config = PedalConfigManager.Instance.Config;
        PedalInputEnabled = config.Enabled;
        PedalGasDeadzone = config.Gas.Deadzone;
        PedalBrakeDeadzone = config.Brake.Deadzone;
        PedalClutchDeadzone = config.Clutch.Deadzone;
        PedalGasMin = config.Gas.Min;
        PedalGasMax = config.Gas.Max;
        PedalBrakeMin = config.Brake.Min;
        PedalBrakeMax = config.Brake.Max;
        PedalClutchMin = config.Clutch.Min;
        PedalClutchMax = config.Clutch.Max;
        PedalGasSmoothing = config.Gas.Smoothing;
        PedalBrakeSmoothing = config.Brake.Smoothing;
        PedalClutchSmoothing = config.Clutch.Smoothing;
        PedalGasInvert = config.Gas.Invert;
        PedalBrakeInvert = config.Brake.Invert;
        PedalClutchInvert = config.Clutch.Invert;

        string wantGas = string.IsNullOrEmpty(config.GasAxis) ? "Y" : config.GasAxis;
        string wantBrake = string.IsNullOrEmpty(config.BrakeAxis) ? "Z" : config.BrakeAxis;
        string wantClutch = string.IsNullOrEmpty(config.ClutchAxis) ? "Rx" : config.ClutchAxis;
        PedalGasAxis = wantGas;
        PedalBrakeAxis = wantBrake;
        PedalClutchAxis = wantClutch;
        ApplyAxisMapping();

        // Log what the sources actually have after push — confirms the loop's
        // sources got the values, not just the VM properties.
        try
        {
            var di = _telemetryLoop?.PedalInput?.Sources
                .OfType<Core.PedalInput.Sources.DirectInputPedalSource>().FirstOrDefault();
            var wb = _telemetryLoop?.PedalInput?.Sources
                .OfType<Core.PedalInput.Sources.FfbDevicePedalSource>().FirstOrDefault();
            Core.DirectInput.VJoy.VJoyFeeder.LogAlways(
                $"LoadPedalConfig: want={wantGas}/{wantBrake}/{wantClutch}" +
                $" | DI.Mapping={di?.Mapping.GasAxis}/{di?.Mapping.BrakeAxis}/{di?.Mapping.ClutchAxis}" +
                $" | WB.Mapping={wb?.Mapping.GasAxis}/{wb?.Mapping.BrakeAxis}/{wb?.Mapping.ClutchAxis}" +
                $" | DI.devices={di?.DeviceCount} loop={_telemetryLoop != null}");
        }
        catch { }

        RefreshPedalUsbDevices();

        LoadHapticRouteConfig();
    }

    public void SavePedalConfig()
    {
        var config = PedalConfigManager.Instance.Config;
        config.Enabled = PedalInputEnabled;
        config.Gas.Deadzone = PedalGasDeadzone;
        config.Brake.Deadzone = PedalBrakeDeadzone;
        config.Clutch.Deadzone = PedalClutchDeadzone;
        config.Gas.Min = PedalGasMin;
        config.Gas.Max = PedalGasMax;
        config.Brake.Min = PedalBrakeMin;
        config.Brake.Max = PedalBrakeMax;
        config.Clutch.Min = PedalClutchMin;
        config.Clutch.Max = PedalClutchMax;
        config.Gas.Smoothing = PedalGasSmoothing;
        config.Brake.Smoothing = PedalBrakeSmoothing;
        config.Clutch.Smoothing = PedalClutchSmoothing;
        config.Gas.Invert = PedalGasInvert;
        config.Brake.Invert = PedalBrakeInvert;
        config.Clutch.Invert = PedalClutchInvert;
        config.GasAxis = PedalGasAxis;
        config.BrakeAxis = PedalBrakeAxis;
        config.ClutchAxis = PedalClutchAxis;
        PedalConfigManager.Instance.Save(config);

        Core.DirectInput.VJoy.VJoyFeeder.LogAlways(
            $"SavePedalConfig: gasAxis={config.GasAxis} brakeAxis={config.BrakeAxis} clutchAxis={config.ClutchAxis}" +
            $" enabled={config.Enabled}");

        // Write axis mapping to BOTH pedal sources (USB + wheelbase) so the
        // choice holds whichever hardware is active.
        ApplyAxisMapping();

        // Also persist the USB picker selection (was in the property handler,
        // moved here to avoid startup race writing defaults to the file).
        PersistUsbPickerChoice();

        SaveHapticRouteConfig();
    }

    /// <summary>
    /// Rebuilds the separate-USB pedal picker ("Auto" + detected devices) and
    /// restores the persisted choice. Wheelbase pedals never appear here —
    /// they follow the connected wheel automatically.
    /// </summary>
    public void RefreshPedalUsbDevices()
    {
        var names = new List<string> { "Auto" };
        int selected = 0;
        try
        {
            var diSource = _telemetryLoop?.PedalInput?.Sources
                .OfType<DirectInputPedalSource>().FirstOrDefault();
            if (diSource != null)
            {
                foreach (var d in diSource.Devices)
                    names.Add(d.ProductName);
                string? want = PedalConfigManager.Instance.Config.PreferredDeviceInstanceId;
                if (!string.IsNullOrEmpty(want))
                {
                    string? pinned = diSource.SelectPreferredDevice(want);
                    if (pinned != null)
                    {
                        int idx = diSource.Devices.ToList()
                            .FindIndex(d => d.ProductName == pinned);
                        if (idx >= 0) selected = idx + 1;
                    }
                }
            }
        }
        catch { }
        PedalUsbDeviceNames = names;
        PedalUsbDevicePresent = names.Count > 1;
        PedalUsbDeviceIndex = Math.Clamp(selected, 0, names.Count - 1);
    }

    partial void OnPedalUsbDeviceIndexChanged(int value)
    {
        try
        {
            var diSource = _telemetryLoop?.PedalInput?.Sources
                .OfType<DirectInputPedalSource>().FirstOrDefault();
            if (diSource == null) return;
            if (value <= 0)
            {
                diSource.SelectPreferredDevice(null);
                // Defer save: only persist when the user actually changes the
                // picker, not during programmatic startup/refresh. The explicit
                // SavePedalConfig path handles the file write.
                return;
            }
            var devices = diSource.Devices.ToList();
            if (value - 1 < devices.Count)
            {
                diSource.ActiveDeviceIndex = value - 1;
            }
        }
        catch { }
    }

    /// <summary>
    /// Persists the USB picker choice to disk. Called from the explicit save
    /// path (SavePedalConfig / user action), NOT from the property-changed
    /// handler above (which only updates the live source selection).
    /// </summary>
    private void PersistUsbPickerChoice()
    {
        try
        {
            var diSource = _telemetryLoop?.PedalInput?.Sources
                .OfType<DirectInputPedalSource>().FirstOrDefault();
            if (diSource == null) return;
            string? instanceId = null;
            if (PedalUsbDeviceIndex > 0)
            {
                var devices = diSource.Devices.ToList();
                if (PedalUsbDeviceIndex - 1 < devices.Count)
                    instanceId = devices[PedalUsbDeviceIndex - 1].InstanceGuid.ToString();
            }
            var cfg = PedalConfigManager.Instance.Config;
            cfg.PreferredDeviceInstanceId = instanceId;
            PedalConfigManager.Instance.Save(cfg);
        }
        catch { }
    }

    [RelayCommand]
    private void RescanPedalUsbDevices() => RefreshPedalUsbDevices();

    // ── Press-to-detect wizard: press each pedal in turn, the app finds the
    // live axis (wheelbase or USB, any brand). Clutch skippable. ──
    [ObservableProperty] private string _pedalDetectStatus = "";
    [ObservableProperty] private bool _isPedalDetectRunning;
    private CancellationTokenSource? _pedalDetectCts;
    private bool _pedalDetectSkip;

    [RelayCommand]
    private async Task StartPedalDetectionAsync()
    {
        if (IsPedalDetectRunning) return;
        var loop = _telemetryLoop;
        if (loop == null)
        {
            PedalDetectStatus = "No session yet — wait for the app to finish loading and retry.";
            return;
        }
        RefreshPedalUsbDevices();
        IsPedalDetectRunning = true;
        _pedalDetectCts = new CancellationTokenSource();
        try
        {
            var steps = new (string pedal, string prompt, Action<string> assign)[]
            {
                ("throttle", "Press the THROTTLE fully down and hold it…", axis => AssignPedalAxis("gas", axis)),
                ("brake", "Now press the BRAKE fully down and hold it…", axis => AssignPedalAxis("brake", axis)),
                ("clutch", "Now press the CLUTCH fully down — or Skip if you have none…", axis => AssignPedalAxis("clutch", axis)),
            };
            foreach (var (pedal, prompt, assign) in steps)
            {
                var hit = await DetectOnePedalAsync(loop, pedal, prompt, _pedalDetectCts.Token);
                if (hit == null)
                {
                    if (_pedalDetectCts.IsCancellationRequested) { PedalDetectStatus = "Detection cancelled."; return; }
                    continue; // skipped or unclear — mapping untouched
                }
                assign(hit.Axis);
                if (hit.DeviceIndex >= 0)
                    PedalUsbDeviceIndex = hit.DeviceIndex + 1; // persists via changed handler
            }
            SavePedalConfig();
            PedalDetectStatus = "Pedals found and saved — wiggle each one to confirm the bars above.";
        }
        catch (OperationCanceledException) { PedalDetectStatus = "Detection cancelled."; }
        catch (Exception ex) { PedalDetectStatus = $"Detection failed: {ex.Message}"; }
        finally
        {
            IsPedalDetectRunning = false;
            _pedalDetectCts?.Dispose();
            _pedalDetectCts = null;
        }
    }

    private async Task<PedalAxisDetector.MovedAxis?> DetectOnePedalAsync(
        TelemetryLoop loop, string pedal, string prompt, CancellationToken ct)
    {
        var baseline = loop.PedalInput.ReadAllCandidateAxes();
        if (baseline.Count == 0)
        {
            PedalDetectStatus = "No pedal hardware found — connect the wheel or USB pedals first.";
            return null;
        }
        PedalDetectStatus = prompt + " (don't touch the wheel)";
        _pedalDetectSkip = false;
        PedalAxisDetector.MovedAxis? winner = null;
        int streak = 0;
        // Timed pass first; then wait patiently for Skip (late presses count).
        for (int i = 0; i < 120 && !_pedalDetectSkip; i++)
        {
            ct.ThrowIfCancellationRequested();
            await Task.Delay(100, ct);
            var current = loop.PedalInput.ReadAllCandidateAxes();
            var hit = PedalAxisDetector.FindMovedAxis(baseline, current);
            if (hit != null && winner != null && hit.DeviceIndex == winner.DeviceIndex && hit.Axis == winner.Axis)
                streak++;
            else
            {
                winner = hit;
                streak = hit != null ? 1 : 0;
            }
            if (winner != null && streak >= 3)
            {
                PedalDetectStatus = $"{pedal} found: {winner.DeviceName} {winner.Axis} — now release it…";
                await WaitForReleaseAsync(loop, baseline, ct);
                _pedalDetectSkip = false; // a skip during release-wait must not skip the next pedal
                return winner;
            }
        }
        if (_pedalDetectSkip)
        {
            PedalDetectStatus = $"{pedal} skipped.";
            return null;
        }
        PedalDetectStatus = $"Couldn't tell the {pedal} apart — move ONLY that pedal… (or Skip)";
        while (!_pedalDetectSkip)
        {
            ct.ThrowIfCancellationRequested();
            await Task.Delay(150, ct);
            var current = loop.PedalInput.ReadAllCandidateAxes();
            var hit = PedalAxisDetector.FindMovedAxis(baseline, current);
            if (hit != null && winner != null && hit.DeviceIndex == winner.DeviceIndex && hit.Axis == winner.Axis)
                streak++;
            else
            {
                winner = hit;
                streak = hit != null ? 1 : 0;
            }
            if (winner != null && streak >= 3)
            {
                PedalDetectStatus = $"{pedal} found: {winner.DeviceName} {winner.Axis} — now release it…";
                await WaitForReleaseAsync(loop, baseline, ct);
                _pedalDetectSkip = false;
                return winner;
            }
        }
        PedalDetectStatus = $"{pedal} skipped.";
        return null;
    }

    /// <summary>
    /// Waits until every candidate axis settles back near its baseline so the
    /// next pedal step doesn't baseline a still-pressed pedal (which would
    /// read the RELEASE as a press and misassign it). Bounded (~15s), then
    /// the next step retakes a fresh baseline anyway.
    /// </summary>
    private async Task WaitForReleaseAsync(
        TelemetryLoop loop, List<PedalAxisSample> baseline, CancellationToken ct)
    {
        for (int i = 0; i < 150 && !_pedalDetectSkip; i++)
        {
            ct.ThrowIfCancellationRequested();
            await Task.Delay(100, ct);
            var current = loop.PedalInput.ReadAllCandidateAxes();
            bool settled = true;
            foreach (var s in current)
            {
                foreach (var b in baseline)
                {
                    if (b.DeviceIndex == s.DeviceIndex && b.Axis == s.Axis
                        && Math.Abs(s.Value - b.Value) > 0.12f)
                    {
                        settled = false;
                        break;
                    }
                }
                if (!settled) break;
            }
            if (settled) return;
        }
    }

    [RelayCommand]
    private void SkipPedalDetectStep() => _pedalDetectSkip = true;

    [RelayCommand]
    private void CancelPedalDetection()
    {
        try { _pedalDetectCts?.Cancel(); } catch { }
    }

    [RelayCommand]
    private void OpenPedalDetectDialog()
    {
        try
        {
            var dlg = new Views.PedalDetectDialog
            {
                Owner = System.Windows.Application.Current?.MainWindow,
                DataContext = this
            };
            dlg.ShowDialog();
        }
        catch (Exception ex)
        {
            PedalDetectStatus = $"Could not open pedal setup: {ex.Message}";
        }
    }

    private static readonly string[] PedalNames = ["gas", "brake", "clutch"];
    private readonly Dictionary<string, string> _pedalAxisUndo = new(StringComparer.OrdinalIgnoreCase);

    private static string PedalDefaultAxis(string pedal) => pedal.ToLowerInvariant() switch
    {
        "gas" => "Y",
        "brake" => "Z",
        _ => "Rx",
    };

    private static string PedalPrompt(string pedal) => pedal.ToLowerInvariant() switch
    {
        "gas" => "Press the THROTTLE fully down and hold it…",
        "brake" => "Now press the BRAKE fully down and hold it…",
        _ => "Now press the CLUTCH fully down — or Skip if you have none…",
    };

    private void AssignPedalAxis(string pedal, string axis)
    {
        // Remember for Undo before overwriting.
        string current = pedal.ToLowerInvariant() switch
        {
            "gas" => PedalGasAxis,
            "brake" => PedalBrakeAxis,
            _ => PedalClutchAxis,
        };
        _pedalAxisUndo[pedal.ToLowerInvariant()] = current;
        switch (pedal.ToLowerInvariant())
        {
            case "gas": PedalGasAxis = axis; break;
            case "brake": PedalBrakeAxis = axis; break;
            default: PedalClutchAxis = axis; break;
        }
    }

    /// <summary>Detect one pedal only (dialog per-pedal button).</summary>
    [RelayCommand]
    private async Task DetectSinglePedalAsync(string? pedal)
    {
        pedal = (pedal ?? "").ToLowerInvariant();
        if (pedal != "gas" && pedal != "brake" && pedal != "clutch") return;
        if (IsPedalDetectRunning)
        {
            PedalDetectStatus = "A detection run is already going — Cancel it first.";
            return;
        }
        var loop = _telemetryLoop;
        if (loop == null)
        {
            PedalDetectStatus = "No session yet — wait for the app to finish loading and retry.";
            return;
        }
        IsPedalDetectRunning = true;
        _pedalDetectCts = new CancellationTokenSource();
        try
        {
            var hit = await DetectOnePedalAsync(loop, pedal, PedalPrompt(pedal), _pedalDetectCts.Token);
            if (hit == null)
            {
                if (!_pedalDetectCts.IsCancellationRequested && !_pedalDetectSkip)
                    PedalDetectStatus = $"Couldn't tell the {pedal} apart — try again.";
                else if (!_pedalDetectCts.IsCancellationRequested)
                    PedalDetectStatus = $"{pedal} skipped.";
                return;
            }
            AssignPedalAxis(pedal, hit.Axis);
            if (hit.DeviceIndex >= 0)
                PedalUsbDeviceIndex = hit.DeviceIndex + 1;
            SavePedalConfig();
            PedalDetectStatus = $"{pedal} set to {hit.DeviceName} {hit.Axis} — undo is available if that's wrong.";
        }
        catch (OperationCanceledException) { PedalDetectStatus = "Detection cancelled."; }
        catch (Exception ex) { PedalDetectStatus = $"Detection failed: {ex.Message}"; }
        finally
        {
            IsPedalDetectRunning = false;
            _pedalDetectCts?.Dispose();
            _pedalDetectCts = null;
        }
    }

    /// <summary>Restores the previous axis for one pedal (single-level undo).</summary>
    [RelayCommand]
    private void UndoPedalAxis(string? pedal)
    {
        pedal = (pedal ?? "").ToLowerInvariant();
        if (!_pedalAxisUndo.TryGetValue(pedal, out string? prev) || string.IsNullOrEmpty(prev))
        {
            PedalDetectStatus = $"Nothing to undo for {pedal}.";
            return;
        }
        switch (pedal)
        {
            case "gas": PedalGasAxis = prev; break;
            case "brake": PedalBrakeAxis = prev; break;
            default: PedalClutchAxis = prev; break;
        }
        _pedalAxisUndo.Remove(pedal);
        SavePedalConfig();
        PedalDetectStatus = $"{pedal} restored to {prev}.";
    }

    /// <summary>Resets one pedal to its default axis.</summary>
    [RelayCommand]
    private void ClearPedalAxis(string? pedal)
    {
        pedal = (pedal ?? "").ToLowerInvariant();
        if (pedal != "gas" && pedal != "brake" && pedal != "clutch") return;
        string def = PedalDefaultAxis(pedal);
        string current = pedal switch
        {
            "gas" => PedalGasAxis,
            "brake" => PedalBrakeAxis,
            _ => PedalClutchAxis,
        };
        if (!string.IsNullOrEmpty(current)) _pedalAxisUndo[pedal] = current;
        switch (pedal)
        {
            case "gas": PedalGasAxis = def; break;
            case "brake": PedalBrakeAxis = def; break;
            default: PedalClutchAxis = def; break;
        }
        SavePedalConfig();
        PedalDetectStatus = $"{pedal} cleared back to {def}.";
    }

    private void LoadHapticRouteConfig()
    {
        var routeCfg = _telemetryLoop?.LiveServer?.HapticRouteConfig;
        if (routeCfg == null) return;

        PedalMasterBrakeGain = routeCfg.BrakeHapticGain;
        PedalMasterGasGain = routeCfg.GasHapticGain;
        PedalMasterClutchGain = routeCfg.ClutchHapticGain;

        // Map route list entries to observable properties
        for (int i = 0; i < routeCfg.Routes.Count; i++)
        {
            var r = routeCfg.Routes[i];
            switch (r.Signal)
            {
                case "abs":   PedalAbsBrakeGain = r.Gain; break;
                case "tc":    PedalTcGasGain = r.Gain; break;
                case "curb":  PedalCurbBothGain = r.Gain; break;
                case "road":  PedalRoadBrakeGain = r.Gain; break;
                case "scrub": PedalScrubGasGain = r.Gain; break;
            }
        }
        PedalBrakePressureGain = 1.0f;
    }

    private void SaveHapticRouteConfig()
    {
        var routeCfg = _telemetryLoop?.LiveServer?.HapticRouteConfig;
        if (routeCfg == null) return;

        routeCfg.BrakeHapticGain = PedalMasterBrakeGain;
        routeCfg.GasHapticGain = PedalMasterGasGain;
        routeCfg.ClutchHapticGain = PedalMasterClutchGain;

        // Update route list entries from observable properties
        foreach (var r in routeCfg.Routes)
        {
            switch (r.Signal)
            {
                case "abs":   r.Gain = PedalAbsBrakeGain; break;
                case "tc":    r.Gain = PedalTcGasGain; break;
                case "curb":  r.Gain = PedalCurbBothGain; break;
                case "road":  r.Gain = PedalRoadBrakeGain; break;
                case "scrub": r.Gain = PedalScrubGasGain; break;
            }
        }
    }

    private void UpdatePedalLiveState()
    {
        if (_telemetryLoop == null)
        {
            PedalLiveGas = 0;
            PedalLiveBrake = 0;
            PedalLiveClutch = 0;
            PedalLiveSource = "—";
            return;
        }

        var raw = _telemetryLoop.LatestRaw;
        var pedalInput = _telemetryLoop.PedalInput;

        if (pedalInput.TryGetState(out var pedalState))
        {
            PedalLiveGas = pedalState.GasInput;
            PedalLiveBrake = pedalState.BrakeInput;
            PedalLiveClutch = pedalState.ClutchInput;
            PedalLiveSource = pedalState.Source.ToString();

            if (_pedalUpdateCounter++ % 30 == 0)
                PedalDiagnosticInfo = pedalInput.GetDiagnosticSummary();
        }
        else if (raw != null)
        {
            PedalLiveGas = raw.GasInput;
            PedalLiveBrake = raw.BrakeInput;
            PedalLiveClutch = _telemetryLoop.LatestPhysicsRaw?.Clutch ?? 0f;
            PedalLiveSource = "Game Telemetry";
        }

        // Setup-card hardware check: physical pedals regardless of the Enabled
        // toggle, so AC setup verifies real hardware (calibration applied).
        try
        {
            if (pedalInput.TryGetPhysicalState(out var phys))
            {
                SetupPedalGas = phys.GasInput;
                SetupPedalBrake = phys.BrakeInput;
                SetupPedalClutch = phys.ClutchInput;
                SetupPedalSource = phys.Source.ToString();
            }
            else
            {
                SetupPedalGas = 0;
                SetupPedalBrake = 0;
                SetupPedalClutch = 0;
                SetupPedalSource = "none detected";
            }
        }
        catch { }

        if (_telemetryLoop.LiveServer != null)
            _telemetryLoop.LiveServer.LiveClutchPosition = PedalLiveClutch;

        // Which physical device(s) the pedal input reads from (e.g. USB-modded
        // Thrustmaster pedals show up here — NOT in the main Device menu).
        var diSource = _telemetryLoop?.PedalInput?.Sources
            .OfType<DirectInputPedalSource>().FirstOrDefault();
        if (diSource != null && diSource.DeviceCount > 0)
        {
            PedalDeviceInfo = string.Join(", ", diSource.Devices.Select(d => d.ProductName));
        }
        else
        {
            PedalDeviceInfo = "No separate USB pedal device detected";
        }
    }

    private int _pedalUpdateCounter;
}
