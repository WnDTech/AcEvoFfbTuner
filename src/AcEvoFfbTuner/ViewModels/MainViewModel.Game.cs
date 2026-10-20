using System.Windows;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using AcEvoFfbTuner.Core;
using AcEvoFfbTuner.Core.FfbProcessing;
using AcEvoFfbTuner.Core.SharedMemory;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AcEvoFfbTuner.ViewModels;

public enum SupportedGame
{
    None,
    AcEvo,
    Raceroom,
    AssettoCorsa,
    LeMansUltimate,
    AssettoCorsaCompetizione,
    Unsupported
}

public sealed partial class MainViewModel
{
    [ObservableProperty]
    private SupportedGame _selectedGame = SupportedGame.None;

    [ObservableProperty]
    private bool _gameAutoMode = true;

    public string GameDisplayName
    {
        get
        {
            if (GameAutoMode && _lastAutoDetectedGame.HasValue)
            {
                if (_lastAutoDetectedGame.Value == SupportedGame.None)
                    return "No game";
                return _lastAutoDetectedGame.Value switch
                {
                    SupportedGame.Raceroom => "RaceRoom (auto)",
                    SupportedGame.AssettoCorsa => "Assetto Corsa (auto)",
                    SupportedGame.LeMansUltimate => "Le Mans Ultimate (auto)",
                    SupportedGame.AssettoCorsaCompetizione => "ACC (auto)",
                    SupportedGame.Unsupported => "Unsupported game",
                    _ => "AC EVO (auto)"
                };
            }
            return SelectedGame switch
            {
                SupportedGame.None => "No game",
                SupportedGame.Raceroom => "RaceRoom",
                SupportedGame.AssettoCorsa => "Assetto Corsa",
                SupportedGame.LeMansUltimate => "Le Mans Ultimate",
                SupportedGame.AssettoCorsaCompetizione => "ACC",
                _ => "AC EVO"
            };
        }
    }

    public bool IsAcEvo => SelectedGame == SupportedGame.AcEvo;
    public bool IsRaceroom => SelectedGame == SupportedGame.Raceroom;
    public bool IsAssettoCorsa => SelectedGame == SupportedGame.AssettoCorsa;
    public bool IsLeMansUltimate => SelectedGame == SupportedGame.LeMansUltimate;
    public bool IsAssettoCorsaCompetizione => SelectedGame == SupportedGame.AssettoCorsaCompetizione;
    public bool IsNoGame => SelectedGame == SupportedGame.None;
    public bool IsColumnForceGame => SelectedGame is SupportedGame.Raceroom or SupportedGame.LeMansUltimate;
    public bool IsPerWheelGame => SelectedGame is SupportedGame.AcEvo or SupportedGame.AssettoCorsa or SupportedGame.AssettoCorsaCompetizione;

    // Feature visibility — which sections/sliders actually have an effect per game
    public bool IsTyreForceSectionVisible => IsPerWheelGame;
    public bool IsTyreSlipFullVisible => IsPerWheelGame || IsRaceroom;
    public bool IsLimitFeelSectionVisible => IsAcEvo || IsAssettoCorsa;
    public bool IsTyreFlexSectionVisible => IsAcEvo || IsAssettoCorsa || IsRaceroom;
    public bool IsStationaryFrictionSectionVisible => IsPerWheelGame;
    public bool IsCrashSectionVisible => IsAcEvo || IsAssettoCorsa || IsAssettoCorsaCompetizione;
    public bool IsVibrationSectionVisible => !IsAssettoCorsaCompetizione;
    public bool IsDampingSectionVisible => IsAcEvo || IsAssettoCorsa || IsLeMansUltimate;
    public bool IsDynamicEffectsSectionVisible => IsAcEvo || IsAssettoCorsa || IsRaceroom;
    public bool IsAutoGainSectionVisible => IsAcEvo || IsAssettoCorsa || IsAssettoCorsaCompetizione;
    public bool IsWetWeatherSectionVisible => IsAcEvo || IsAssettoCorsa || IsAssettoCorsaCompetizione;
    public bool IsGripScaleAndTempVisible => IsLeMansUltimate;

    public int SelectedGameIndex
    {
        get => (int)SelectedGame;
        set => SelectedGame = (SupportedGame)value;
    }

    private static ISharedMemoryReader CreateReader(SupportedGame game) => game switch
    {
        SupportedGame.Raceroom => new RaceroomSharedMemoryReader(),
        // AC1 has its own MMFs (acpmf_*) + reader — NOT the EVO reader
        // (acevo_pmf_*). Wiring the EVO reader here connected to nothing
        // while AC1 was running, so detection fired but no telemetry flowed.
        SupportedGame.AssettoCorsa => new AssettoCorsaSharedMemoryReader(),
        SupportedGame.LeMansUltimate => new LmuSharedMemoryReader(),
        SupportedGame.AssettoCorsaCompetizione => new AccSharedMemoryReader(),
        _ => new SharedMemoryReader()
    };

    private static FfbPipeline CreatePipeline(SupportedGame game) => game switch
    {
        SupportedGame.Raceroom => new R3eFfbPipeline(),
        SupportedGame.AssettoCorsa => new AcFfbPipeline(),
        SupportedGame.LeMansUltimate => new LmuFfbPipeline(),
        SupportedGame.AssettoCorsaCompetizione => new AccFfbPipeline(),
        _ => new FfbPipeline()
    };

    private SupportedGame? _lastAutoDetectedGame;

    /// <summary>
    /// True while the wheel is shared with the game (game owns force).
    /// Currently only Assetto Corsa 1 without the vJoy bridge: its engine
    /// reads inputs via DirectInput and our exclusive DI acquire starved it
    /// (invisible wheel, dead inputs).
    /// </summary>
    public bool IsWheelSharedMode => _deviceManager.SharedWheelMode;

    /// <summary>AC1 vJoy bridge status line for the UI (driver/connection/feed state).</summary>
    public string VJoyBridgeStatus
    {
        get
        {
            var feeder = _telemetryLoop.VJoyFeeder;
            if (feeder.IsConnected)
            {
                if (feeder.UpdateCount > 0)
                {
                    double age = (DateTime.UtcNow - feeder.LastUpdateUtc).TotalSeconds;
                    if (age < 2.0)
                        return $"vJoy bridge live (device {feeder.DeviceId}) — feeding {feeder.UpdateCount} frames" +
                            (feeder.LastNativeError == "" ? "" : $" — WARNING: {feeder.LastNativeError}");
                    return $"vJoy bridge connected but no frames for {age:F0}s — press Start with Assetto Corsa selected";
                }
                return $"vJoy bridge connected (device {feeder.DeviceId}) — waiting for telemetry frames (press Start with Assetto Corsa selected)";
            }
            return _appSettings.AcVJoyBridgeEnabled
                ? $"vJoy bridge OFF ({feeder.LastError})"
                : "vJoy bridge disabled";
        }
    }

    /// <summary>Whether the vJoy driver is installed (setup card step 1).</summary>
    [ObservableProperty]
    private bool _vJoyDriverPresent;

    private void RefreshVJoyDriverState()
    {
        try { VJoyDriverPresent = Core.DirectInput.VJoy.VJoyFeeder.IsDriverInstalled(); }
        catch { VJoyDriverPresent = false; }
    }

    /// <summary>Re-checks vJoy driver + bridge state for the Game Config card
    /// (called when the section opens, so installs done outside the app —
    /// setup.exe component, manual download — show up immediately).</summary>
    public void RefreshGameConfigState()
    {
        RefreshVJoyDriverState();
        try { RefreshPedalUsbDevices(); } catch { }
        try { RefreshVJoyButtonSlots(); } catch { }
        // Driver appeared after the last game switch (installed/rebooted while
        // the app ran): engage now instead of showing the stale connect error.
        if (SelectedGame == SupportedGame.AssettoCorsa
            && _appSettings.AcVJoyBridgeEnabled
            && VJoyDriverPresent
            && !_telemetryLoop.VJoyFeeder.IsConnected)
        {
            var (bridgeOk, _) = TryEngageVJoyBridge(SelectedGame);
            if (bridgeOk)
            {
                AddSystemLog($"AC1 vJoy bridge live on device {_telemetryLoop.VJoyFeeder.DeviceId}: bind AC controls to vJoy, game FFB output is dropped");
                if (_deviceManager.SharedWheelMode)
                {
                    _deviceManager.SharedWheelMode = false;
                    OnPropertyChanged(nameof(IsWheelSharedMode));
                    if (_deviceManager.ConnectedDevice != null)
                    {
                        bool ok = _deviceManager.ReconnectDevice();
                        AddSystemLog($"Wheel re-acquired exclusive (reconnect {(ok ? "OK" : "FAILED")})");
                    }
                }
            }
        }
        OnPropertyChanged(nameof(VJoyBridgeStatus));
    }

    /// <summary>
    /// Games whose engines read wheel/pedals through DirectInput and therefore
    /// cannot coexist with our exclusive DI acquire. Only consulted when the
    /// vJoy bridge is unavailable — bridge mode keeps exclusive + feeds vJoy.
    /// Scoped narrowly: EVO and other games keep exclusive mode.
    /// </summary>
    /// <summary>
    /// Tries to engage the vJoy bridge for the given game. Returns
    /// (bridgeOk, statusNote). Safe to call repeatedly: a live feeder is
    /// reused, a dead one is disconnected exactly once by the caller path.
    /// </summary>
    private (bool bridgeOk, string bridgeNote) TryEngageVJoyBridge(SupportedGame game)
    {
        bool bridgeOk = false;
        string bridgeNote = "";
        if (game == SupportedGame.AssettoCorsa && _appSettings.AcVJoyBridgeEnabled)
        {
            _telemetryLoop.VJoyFeeder.DeviceId = _appSettings.AcVJoyDeviceId;
            _telemetryLoop.VJoyBridgeEnabled = true;
            bridgeOk = _telemetryLoop.VJoyFeeder.IsConnected || _telemetryLoop.VJoyFeeder.Connect();
            if (bridgeOk)
            {
                ApplyVJoyButtonMap();
            }
            try { RefreshVJoyButtonSlots(); } catch { }
            bridgeNote = bridgeOk
                ? $" (vJoy device {_telemetryLoop.VJoyFeeder.DeviceId} feeding)"
                : $" (vJoy FAILED: {_telemetryLoop.VJoyFeeder.LastError} — shared fallback)";
        }
        else
        {
            _telemetryLoop.VJoyBridgeEnabled = false;
            try { _telemetryLoop.VJoyFeeder.Disconnect(); } catch { }
        }
        return (bridgeOk, bridgeNote);
    }

    private static bool GameRequiresSharedWheel(SupportedGame game) => game switch
    {
        SupportedGame.AssettoCorsa => true,
        _ => false
    };

    partial void OnSelectedGameChanged(SupportedGame value)
    {
        var wasRunning = _telemetryLoop.IsRunning;
        if (wasRunning)
            _telemetryLoop.Stop();

        _discordPresence.Detach();

        // Preserve the FFB provider across the switch — it is transport-level
        // (e.g. the Logitech TrueForce stream) and must survive reader swaps.
        // Disposing it here killed the wheel's FFB whenever game auto-detection
        // re-fired mid-session: the new loop had no provider, and the DirectInput
        // fallback was dead because the game holds the wheel exclusively.
        var ffbProvider = _telemetryLoop.DetachFfbProvider();

        try
        {
            _telemetryLoop.Dispose();
            _reader.Dispose();
            _reader = CreateReader(value);
            _pipeline = CreatePipeline(value);
            OnPropertyChanged(nameof(GameDisplayName));
            OnPropertyChanged(nameof(IsAcEvo));
            OnPropertyChanged(nameof(IsRaceroom));
            OnPropertyChanged(nameof(IsAssettoCorsa));
            OnPropertyChanged(nameof(IsLeMansUltimate));
            OnPropertyChanged(nameof(IsAssettoCorsaCompetizione));
            OnPropertyChanged(nameof(IsNoGame));
            OnPropertyChanged(nameof(IsColumnForceGame));
            OnPropertyChanged(nameof(IsPerWheelGame));
            OnPropertyChanged(nameof(IsTyreForceSectionVisible));
            OnPropertyChanged(nameof(IsTyreSlipFullVisible));
            OnPropertyChanged(nameof(IsLimitFeelSectionVisible));
            OnPropertyChanged(nameof(IsTyreFlexSectionVisible));
            OnPropertyChanged(nameof(IsStationaryFrictionSectionVisible));
            OnPropertyChanged(nameof(IsCrashSectionVisible));
            OnPropertyChanged(nameof(IsVibrationSectionVisible));
            OnPropertyChanged(nameof(IsDampingSectionVisible));
            OnPropertyChanged(nameof(IsDynamicEffectsSectionVisible));
            OnPropertyChanged(nameof(IsAutoGainSectionVisible));
            OnPropertyChanged(nameof(IsWetWeatherSectionVisible));
            OnPropertyChanged(nameof(IsGripScaleAndTempVisible));

            _coachService.CurrentGame = value switch
            {
                SupportedGame.Raceroom => "RaceRoom Racing Experience",
                SupportedGame.AssettoCorsa => "Assetto Corsa",
                SupportedGame.LeMansUltimate => "Le Mans Ultimate",
                SupportedGame.AssettoCorsaCompetizione => "Assetto Corsa Competizione",
                SupportedGame.AcEvo => "Assetto Corsa EVO",
                SupportedGame.Unsupported => "Unsupported game",
                _ => "No game"
            };
            _coachService.IsColumnForceGame = value is SupportedGame.Raceroom or SupportedGame.LeMansUltimate;
            _coachService.SyncLiveContext(_coachService.CurrentGame, DetectedCarModel, DetectedTrackName, DetectedTrackConfig);

            if (value != SupportedGame.None && _profileManager.ActiveProfile != null)
                _profileManager.ActiveProfile.ApplyToPipeline(_pipeline);

            var newLoop = new TelemetryLoop(_reader, _pipeline, _deviceManager);
            WireTelemetryLoopEvents(newLoop);
            _telemetryLoop = newLoop;
            // Fresh pedal sources come with defaults — re-apply the persisted
            // hardware setup (own store, untouched by FFB profiles).
            LoadPedalConfig();

            // Re-attach the preserved provider so the wheel keeps receiving force
            // immediately after the switch (no teardown, no re-init spin).
            newLoop.SetFfbProvider(ffbProvider);

            // Wheel ownership handover (AC1 only; every other game keeps
            // exclusive mode untouched):
            // - vJoy bridge (toggle ON + driver present): app keeps EXCLUSIVE
            //   on the physical wheel (our FFB rules it) and feeds the vJoy
            //   virtual device AC1 binds. Full our-FFB mode.
            // - otherwise: SHARED (non-exclusive) so AC1 gets inputs + native
            //   FFB; app force stands down, LEDs/HF8/snapshots keep running.
            //   A failed vJoy connect falls back to shared, never exclusive.
            var (bridgeOk, bridgeNote) = TryEngageVJoyBridge(value);
            OnPropertyChanged(nameof(VJoyBridgeStatus));
            RefreshVJoyDriverState();
            bool wantShared = GameRequiresSharedWheel(value) && !bridgeOk;
            if (_deviceManager.SharedWheelMode != wantShared)
            {
                _deviceManager.SharedWheelMode = wantShared;
                OnPropertyChanged(nameof(IsWheelSharedMode));
                if (_deviceManager.ConnectedDevice != null)
                {
                    bool ok = _deviceManager.ReconnectDevice();
                    AddSystemLog(wantShared
                        ? $"Wheel shared with {GameDisplayName} (non-exclusive): game owns inputs + native FFB, app force off"
                        : $"Wheel re-acquired exclusive (reconnect {(ok ? "OK" : "FAILED")})");
                }
            }

            if (wasRunning)
            {
                _telemetryLoop.Start();
                StatusText = wantShared
                    ? $"Switched to {GameDisplayName} — wheel shared with game (native FFB), telemetry restarted"
                    : $"Switched to {GameDisplayName}{bridgeNote} — telemetry restarted";
            }
            else
            {
                StatusText = wantShared
                    ? $"Switched to {GameDisplayName} — wheel shared with game (native FFB)"
                    : $"Switched to {GameDisplayName}{bridgeNote}";
            }

            AddSystemLog($"Game source changed to {GameDisplayName}");
        }
        catch
        {
            // Never leak a live TrueForce stream: tear it down (neutral + the
            // session-end handshake) so the wheel cannot keep holding a stale
            // force or a dangling session if the switch failed mid-way.
            try { ffbProvider?.Dispose(); } catch { }
            throw;
        }
    }

    /// <summary>AC1 vJoy input bridge toggle (persisted). Takes effect via the
    /// game-switch path: re-runs ownership so the wheel flips between full
    /// our-FFB (bridge) and the shared fallback without manual reconnects.
    /// Telemetry restarts as part of the switch — expected for a hardware
    /// routing change.</summary>
    [ObservableProperty]
    private bool _acVJoyBridgeEnabled;

    partial void OnAcVJoyBridgeEnabledChanged(bool value)
    {
        _appSettings.AcVJoyBridgeEnabled = value;
        _appSettings.Save();
        OnPropertyChanged(nameof(VJoyBridgeStatus));
        if (SelectedGame == SupportedGame.AssettoCorsa)
            OnSelectedGameChanged(SelectedGame);
    }

    /// <summary>One-click AC axis binding status.</summary>
    [ObservableProperty]
    private string _vJoyBindStatus = "";

    /// <summary>Driver install status + busy flag (setup card step 1).</summary>
    [ObservableProperty]
    private string _vJoyDriverStatus = "";
    [ObservableProperty]
    private bool _isVJoyDriverInstalling;

    /// <summary>
    /// Downloads the pinned vJoy installer, verifies it and runs it elevated.
    /// Ends with a reboot request — the virtual wheel appears after restart.
    /// </summary>
    [RelayCommand]
    private async Task InstallVJoyDriverAsync()
    {
        if (IsVJoyDriverInstalling) return;
        IsVJoyDriverInstalling = true;
        VJoyDriverStatus = "Downloading…";
        try
        {
            var progress = new Progress<double>(p =>
                VJoyDriverStatus = $"Downloading… {p:F0}%");
            var res = await Core.DirectInput.VJoy.VJoyDriverSetup.InstallAsync(
                progress, msg => VJoyDriverStatus = msg);
            VJoyDriverStatus = res.Message;
            RefreshVJoyDriverState();
            OnPropertyChanged(nameof(VJoyBridgeStatus));
            if (res.Success)
                AddSystemLog(res.RebootRequired
                    ? "vJoy driver installed — reboot required before the virtual wheel appears"
                    : "vJoy driver installed — virtual wheel ready, no reboot needed");
        }
        finally
        {
            IsVJoyDriverInstalling = false;
        }
    }

    /// <summary>
    /// Points AC's STEER/THROTTLE/BRAKES/CLUTCH at the vJoy virtual device
    /// (axles 0/1/2/3 = X/Y/Z/Rx as the bridge feeds). Button bindings, gains
    /// and all other keys are left untouched; a .ffbtuner-bak backup is kept.
    /// Refuses while AC is running.
    /// </summary>
    [RelayCommand]
    private void WriteAcVJoyBindings()
    {
        var dev = _deviceManager.FindVJoyDevice();
        if (dev?.DeviceInstance == null)
        {
            VJoyBindStatus = "vJoy device not found — install the driver and enable a device (X/Y/Z/Rx) in vJoyConf first";
            return;
        }
        var res = Core.DirectInput.VJoy.VJoyAcBinding.WriteBindings(
            Core.DirectInput.VJoy.VJoyAcBinding.DefaultControlsIniPath(),
            dev.DeviceInstance.InstanceGuid,
            dev.DeviceInstance.ProductGuid,
            Core.DirectInput.VJoy.VJoyAcBinding.IsAcRunning(),
            _appSettings.AcVJoyTaskButtons);
        VJoyBindStatus = res.Success ? res.Message : $"Binding failed: {res.Message}";
        if (res.Success)
            AddSystemLog($"AC controls.ini → vJoy (controller {res.VJoyIndex}); backup {res.BackupPath}");
    }

    // ── vJoy button tasks (wheel button → vJoy slot → AC task). AC can only
    // see vJoy, so gears/pits/lights are arranged here, then written into
    // the game's file by the one-click binding. ──
    [ObservableProperty] private string _vJoyButtonsText = "";
    [ObservableProperty] private string _vJoyTaskCapturePrompt = "";
    [ObservableProperty] private string _lastWheelButtonText = "—";
    [ObservableProperty] private ObservableCollection<AcTaskRow> _acTaskRows = new();
    private string? _taskCaptureSection;
    private bool[]? _vJoyCapturePrev;

    public sealed partial class AcTaskRow : ObservableObject
    {
        public string DisplayName { get; init; } = "";
        public string Section { get; init; } = "";
        [ObservableProperty] private string _assignment = "unassigned";
    }

    /// <summary>Normalized 32-slot map from settings (missing = identity).</summary>
    private int[] GetVJoyButtonMap()
    {
        var map = Core.DirectInput.VJoy.VJoyFeeder.DefaultButtonMap();
        try
        {
            var saved = _appSettings.AcVJoyButtonMap;
            for (int i = 0; i < Math.Min(saved.Count, map.Length); i++)
                map[i] = saved[i];
        }
        catch { }
        return map;
    }

    private void ApplyVJoyButtonMap()
    {
        try { _telemetryLoop.VJoyFeeder.ButtonMap = GetVJoyButtonMap(); }
        catch { }
    }

    private void RefreshVJoyButtonSlots()
    {
        try
        {
            bool connected = _telemetryLoop.VJoyFeeder.IsConnected;
            int vjoyCount = connected ? _telemetryLoop.VJoyFeeder.DeviceButtonCount : -1;
            int wheelCount = 0;
            try { wheelCount = _deviceManager.GetPhysicalButtonCount(); } catch { }
            int target = Core.DirectInput.VJoy.VJoyDeviceConfig.DefaultTargetButtons;
            if (connected && vjoyCount > 0)
            {
                VJoyButtonsText = vjoyCount >= target
                    ? $"Wheel reports {wheelCount} buttons · vJoy offers {vjoyCount} — all covered"
                    : $"Wheel reports {wheelCount} buttons · vJoy offers {vjoyCount} — reconfiguring to {target} in background";
            }
            else
            {
                VJoyButtonsText = wheelCount > 0
                    ? $"Wheel reports {wheelCount} button{(wheelCount == 1 ? "" : "s")} — engage the bridge above to map them"
                    : "vJoy device not connected yet — engage the bridge above first";
            }
            RebuildAcTaskRows();
        }
        catch { }
    }

    /// <summary>
    /// Repairs a scrambled map (e.g. slots stolen by the old fallback):
    /// back to 1:1 identity, tasks kept (each task N now means wheel N).
    /// Re-assign anything that still looks wrong afterwards.
    /// </summary>
    [RelayCommand]
    private void ResetVJoyButtonMap()
    {
        try
        {
            var map = Core.DirectInput.VJoy.VJoyFeeder.DefaultButtonMap();
            _appSettings.AcVJoyButtonMap = map.ToList();
            _appSettings.Save();
            ApplyVJoyButtonMap();
            RebuildAcTaskRows();
            VJoyTaskCapturePrompt = "Mapping reset to 1:1 — every vJoy slot follows its own wheel button. Re-assign tasks that still look wrong, then Write.";
        }
        catch (Exception ex)
        {
            VJoyTaskCapturePrompt = $"Reset failed: {ex.Message}";
        }
    }
    [RelayCommand]
    private async Task MatchVJoyButtonsAsync()
    {
        int wheel = 0;
        try { wheel = _deviceManager.GetPhysicalButtonCount(); } catch { }
        if (wheel <= 0)
        {
            VJoyButtonsText = "No wheel buttons seen — connect the wheel first, then retry.";
            return;
        }
        int target = Core.DirectInput.VJoy.VJoyDeviceConfig.DefaultTargetButtons;
        int have = _telemetryLoop.VJoyFeeder.IsConnected
            ? _telemetryLoop.VJoyFeeder.DeviceButtonCount : -1;
        if (have >= target)
        {
            VJoyButtonsText = $"Wheel reports {wheel}, vJoy offers {have} — already covered.";
            return;
        }
        VJoyButtonsText = have > 0
            ? $"Reconfiguring vJoy from {have} to {target} buttons…"
            : $"Setting vJoy device to {target} buttons for your {wheel}-button wheel…";
        try { _telemetryLoop.VJoyFeeder.Disconnect(); } catch { }
        var res = await Core.DirectInput.VJoy.VJoyDeviceConfig.SetButtonCountAsync(
            _appSettings.AcVJoyDeviceId, target,
            new Core.DirectInput.VJoy.VJoyNative(),
            msg => VJoyButtonsText = msg);
        VJoyButtonsText = res.Message;
        if (res.Success)
        {
            AddSystemLog($"vJoy buttons set to {res.ButtonCount} for {wheel}-button wheel");
            // The device restart drops our claim but not our flag — force a
            // fresh reconnect so the new button count is picked up now,
            // otherwise the feeder keeps the stale 8-slot view and every
            // further assign refuses.
            try { _telemetryLoop.VJoyFeeder.Disconnect(); } catch { }
            RefreshGameConfigState();
        }
    }

    /// <summary>Opens vJoyConf elevated (button/axis counts can only change
    /// there — the driver owns that dialog, we just deep-link it).</summary>
    [RelayCommand]
    private void OpenVJoyConf()
    {
        try
        {
            string? exe = null;
            foreach (var dir in new[]
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "vJoy", "x64"),
                Path.Combine(Environment.GetEnvironmentVariable("ProgramFiles(x86)") ?? "", "vJoy", "x64"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "vJoy", "x86"),
            })
            {
                try
                {
                    string cand = Path.Combine(dir, "vJoyConf.exe");
                    if (File.Exists(cand)) { exe = cand; break; }
                }
                catch { }
            }
            if (exe == null)
            {
                VJoyButtonsText = "vJoyConf not found — reinstall the vJoy driver first";
                return;
            }
            Process.Start(new ProcessStartInfo
            {
                FileName = exe,
                Verb = "runas",
                UseShellExecute = true
            });
            VJoyButtonsText = "vJoyConf opening — set Buttons, Apply, then reopen this page";
        }
        catch (Exception ex)
        {
            VJoyButtonsText = $"Could not open vJoyConf: {ex.Message}";
        }
    }

    /// <summary>Rebuilds the task rows (display + current assignments).</summary>
    private async Task AutoReconfigureVJoyButtonsAsync()
    {
        try
        {
            VJoyButtonsText = "Configuring vJoy to 128 buttons for full wheel coverage…";
            Core.DirectInput.VJoy.VJoyFeeder.LogAlways("auto-reconfigure: device has <128 buttons, running vJoyConfig");
            try { _telemetryLoop.VJoyFeeder.Disconnect(); } catch { }
            var res = await Core.DirectInput.VJoy.VJoyDeviceConfig.SetButtonCountAsync(
                _appSettings.AcVJoyDeviceId,
                Core.DirectInput.VJoy.VJoyDeviceConfig.DefaultTargetButtons,
                new Core.DirectInput.VJoy.VJoyNative(),
                msg => VJoyButtonsText = msg);
            VJoyButtonsText = res.Message;
            Core.DirectInput.VJoy.VJoyFeeder.LogAlways($"auto-reconfigure result: {res.Message}");
            if (res.Success)
            {
                AddSystemLog($"vJoy auto-configured to {res.ButtonCount} buttons");
                RefreshGameConfigState();
            }
        }
        catch (Exception ex)
        {
            VJoyButtonsText = $"Auto-configure failed: {ex.Message} — run Match buttons manually";
            Core.DirectInput.VJoy.VJoyFeeder.LogAlways($"auto-reconfigure EXCEPTION: {ex.Message}");
        }
    }

    /// <summary>Rebuilds the task rows (display + current assignments).</summary>
    private void RebuildAcTaskRows()
    {
        try
        {
            var map = GetVJoyButtonMap();
            var tasks = _appSettings.AcVJoyTaskButtons;
            AcTaskRows.Clear();
            foreach (var t in Core.DirectInput.VJoy.AcControlTask.All)
            {
                string assignment = "unassigned";
                if (tasks.TryGetValue(t.Section, out int slot) && slot >= 1 && slot <= map.Length)
                {
                    int phys = map[slot - 1];
                    assignment = phys >= 0 ? $"wheel {phys + 1} → vJoy {slot}" : $"vJoy {slot} (unmapped)";
                }
                AcTaskRows.Add(new AcTaskRow { DisplayName = t.DisplayName, Section = t.Section, Assignment = assignment });
            }
        }
        catch { }
    }

    /// <summary>Starts press-to-assign for one task: next wheel button press
    /// claims it (physical → identity vJoy slot → task), saved at once.</summary>
    [RelayCommand]
    private void CaptureAcTask(string? section)
    {
        if (string.IsNullOrEmpty(section)) return;
        var task = Core.DirectInput.VJoy.AcControlTask.All
            .FirstOrDefault(t => t.Section == section);
        _taskCaptureSection = section;
        _vJoyCapturePrev = null;
        VJoyTaskCapturePrompt = $"Press a wheel button for {task?.DisplayName ?? section}… (press AFTER clicking)";
        Core.DirectInput.VJoy.VJoyFeeder.LogAlways($"capture ARMED for {section}");
    }

    [RelayCommand]
    private void ClearAcTask(string? section)
    {
        if (string.IsNullOrEmpty(section)) return;
        _appSettings.AcVJoyTaskButtons.Remove(section);
        _appSettings.Save();
        if (_taskCaptureSection == section)
        {
            _taskCaptureSection = null;
            VJoyTaskCapturePrompt = "";
        }
        RebuildAcTaskRows();
    }

    /// <summary>Called from the UI tick. Tracks the last-pressed physical
    /// button for visibility, and assigns it when a task capture is armed.
    /// Same-device reads as the feed.</summary>
    private void CaptureVJoyButtonTick()
    {
        bool[]? cur;
        try { cur = _deviceManager.PollButtons(); } catch { return; }
        if (cur == null) return;
        try
        {
            if (_vJoyCapturePrev != null)
            {
                for (int i = 0; i < cur.Length; i++)
                {
                    bool rising = cur[i] && (i >= _vJoyCapturePrev.Length || !_vJoyCapturePrev[i]);
                    if (!rising) continue;
                    LastWheelButtonText = $"wheel button {i + 1} pressed";
                    if (_taskCaptureSection == null) break;
                    Core.DirectInput.VJoy.VJoyFeeder.LogAlways($"capture PRESS: physical {i + 1} for {_taskCaptureSection}");
                    AssignTaskToButton(_taskCaptureSection, i);
                    _taskCaptureSection = null;
                    break;
                }
            }
            _vJoyCapturePrev = cur;
        }
        catch { }
    }

    /// <summary>Assigns a task to a physical press: vJoy slot = identity
    /// (physical i → slot i+1) when it fits the device, else first free slot.
    /// Writes map + task + settings, applies live.</summary>
    private void AssignTaskToButton(string section, int physical)
    {
        try
        {
            // Use the TARGET count (128), not the current device count (may
            // still be 8 while reconfigure runs in background). Tasks queue
            // correctly; feed only writes up to DeviceButtonCount anyway.
            int slots = Core.DirectInput.VJoy.VJoyDeviceConfig.DefaultTargetButtons;
            var map = GetVJoyButtonMap();
            int slot = (physical + 1 <= slots) ? physical + 1 : -1;
            string displaced = "";
            if (slot < 0)
            {
                // Fallback: any slot no TASK claims is free, even if its
                // identity route points elsewhere — nothing reads untasked
                // slots, so rerouting one breaks nothing. (Refusing here is
                // what made high-numbered wheel buttons unassignable.)
                for (int s = 1; s <= slots; s++)
                {
                    bool claimed = false;
                    try { claimed = _appSettings.AcVJoyTaskButtons.Values.Contains(s); }
                    catch { }
                    if (!claimed)
                    {
                        if (s - 1 < map.Length && map[s - 1] >= 0 && map[s - 1] != physical)
                            displaced = $" (vJoy {s} previously followed wheel {map[s - 1] + 1})";
                        slot = s;
                        break;
                    }
                }
            }
            var task = Core.DirectInput.VJoy.AcControlTask.All
                .FirstOrDefault(t => t.Section == section);
            if (slot < 0)
            {
                VJoyTaskCapturePrompt = $"No free vJoy slot for {task?.DisplayName ?? section} — hit “Match buttons to wheel” above to add slots, then assign again.";
                Core.DirectInput.VJoy.VJoyFeeder.LogAlways($"assign REFUSED for {section}: physical {physical + 1}, no free slot (slots={slots})");
                return;
            }
            map[slot - 1] = physical;
            _appSettings.AcVJoyButtonMap = map.ToList();
            _appSettings.AcVJoyTaskButtons[section] = slot;
            _appSettings.Save();
            ApplyVJoyButtonMap();
            RebuildAcTaskRows();
            string clash = "";
            try
            {
                var other = _appSettings.AcVJoyTaskButtons
                    .Where(kv => kv.Key != section && kv.Value == slot)
                    .Select(kv => kv.Key).ToList();
                if (other.Count > 0)
                    clash = $" (also fires {string.Join(", ", other)} — Clear it there if that's wrong)";
            }
            catch { }
            VJoyTaskCapturePrompt = $"{task?.DisplayName ?? section}: wheel {physical + 1} → vJoy {slot} — saved{clash}{displaced}. Run the Write step to push it to the game.";
            Core.DirectInput.VJoy.VJoyFeeder.LogAlways($"assign OK: {section} = wheel {physical + 1} → vJoy {slot}{clash}{displaced}");
        }
        catch (Exception ex)
        {
            VJoyTaskCapturePrompt = $"Assign failed: {ex.Message}";
            Core.DirectInput.VJoy.VJoyFeeder.LogAlways($"assign EXCEPTION for {section}: {ex.GetType().Name}: {ex.Message}");
        }
    }

    public void SetAutoMode()
    {
        GameAutoMode = true;
        _gameDetectorManualOverride = false;
        OnPropertyChanged(nameof(GameDisplayName));
    }

    [RelayCommand]
    private void EnableAutoGameDetect()
    {
        SetAutoMode();
        StatusText = "Auto-detect mode enabled";
        AddSystemLog("Auto-detect game mode re-enabled");
    }

    public void SetDetectedGame(SupportedGame game)
    {
        _lastAutoDetectedGame = game;
        OnPropertyChanged(nameof(GameDisplayName));
        SyncCoachContext();
    }
}
