using AcEvoFfbTuner.Core.Config;
using AcEvoFfbTuner.Core.PedalHaptics;
using AcEvoFfbTuner.Core.PedalInput.Sources;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AcEvoFfbTuner.ViewModels;

public sealed partial class MainViewModel
{
    // ── Pedal Config (from global pedal_config.json) ──
    [ObservableProperty] private bool _pedalInputEnabled;
    [ObservableProperty] private string _pedalSourceType = "—";
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
        if (diSource?.Mapping == null) return;
        diSource.Mapping.GasAxis = PedalGasAxis;
        diSource.Mapping.BrakeAxis = PedalBrakeAxis;
        diSource.Mapping.ClutchAxis = PedalClutchAxis;
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

        // Read axis mapping from the active DirectInput pedal source
        var diSource = _telemetryLoop?.PedalInput?.Sources
            .OfType<DirectInputPedalSource>().FirstOrDefault();
        if (diSource?.Mapping != null)
        {
            PedalGasAxis = diSource.Mapping.GasAxis;
            PedalBrakeAxis = diSource.Mapping.BrakeAxis;
            PedalClutchAxis = diSource.Mapping.ClutchAxis;
        }

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
        PedalConfigManager.Instance.Save(config);

        // Write axis mapping to the active DirectInput pedal source
        var diSource = _telemetryLoop?.PedalInput?.Sources
            .OfType<DirectInputPedalSource>().FirstOrDefault();
        if (diSource?.Mapping != null)
        {
            diSource.Mapping.GasAxis = PedalGasAxis;
            diSource.Mapping.BrakeAxis = PedalBrakeAxis;
            diSource.Mapping.ClutchAxis = PedalClutchAxis;
        }

        SaveHapticRouteConfig();
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
            PedalSourceType = pedalState.Source.ToString();

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
