using AcEvoFfbTuner.Core.Config;
using AcEvoFfbTuner.Core.PedalHaptics;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AcEvoFfbTuner.ViewModels;

public sealed partial class MainViewModel
{
    // ── Pedal Config (from global pedal_config.json) ──
    [ObservableProperty] private bool _pedalInputEnabled;
    [ObservableProperty] private string _pedalSourceType = "—";
    [ObservableProperty] private float _pedalGasDeadzone;
    [ObservableProperty] private float _pedalBrakeDeadzone;
    [ObservableProperty] private float _pedalGasMin;
    [ObservableProperty] private float _pedalGasMax = 1.0f;
    [ObservableProperty] private float _pedalBrakeMin;
    [ObservableProperty] private float _pedalBrakeMax = 1.0f;
    [ObservableProperty] private float _pedalGasSmoothing = 0.85f;
    [ObservableProperty] private float _pedalBrakeSmoothing = 0.85f;
    [ObservableProperty] private bool _pedalGasInvert;
    [ObservableProperty] private bool _pedalBrakeInvert;

    // ── Live Pedal State (updated from TelemetryLoop ~30fps) ──
    [ObservableProperty] private float _pedalLiveGas;
    [ObservableProperty] private float _pedalLiveBrake;
    [ObservableProperty] private string _pedalLiveSource = "—";
    [ObservableProperty] private string _pedalDiagnosticInfo = "";

    // ── Haptic Routing Gains (per-signal → pedal, stored in LiveServer.HapticRouteConfig) ──
    [ObservableProperty] private float _pedalMasterBrakeGain = 1.0f;
    [ObservableProperty] private float _pedalMasterGasGain = 1.0f;
    [ObservableProperty] private float _pedalAbsBrakeGain = 1.0f;
    [ObservableProperty] private float _pedalTcGasGain = 1.0f;
    [ObservableProperty] private float _pedalCurbBothGain = 0.5f;
    [ObservableProperty] private float _pedalRoadBrakeGain = 0.3f;
    [ObservableProperty] private float _pedalScrubGasGain = 0.4f;
    [ObservableProperty] private float _pedalBrakePressureGain = 1.0f;
    [ObservableProperty] private float _pedalThrottlePositionGain = 0.0f;
    [ObservableProperty] private float _pedalEngineRpmGain = 0.0f;

    // ── Methods ──

    public void LoadPedalConfig()
    {
        var config = PedalConfigManager.Instance.Config;
        PedalInputEnabled = config.Enabled;
        PedalGasDeadzone = config.Gas.Deadzone;
        PedalBrakeDeadzone = config.Brake.Deadzone;
        PedalGasMin = config.Gas.Min;
        PedalGasMax = config.Gas.Max;
        PedalBrakeMin = config.Brake.Min;
        PedalBrakeMax = config.Brake.Max;
        PedalGasSmoothing = config.Gas.Smoothing;
        PedalBrakeSmoothing = config.Brake.Smoothing;
        PedalGasInvert = config.Gas.Invert;
        PedalBrakeInvert = config.Brake.Invert;

        LoadHapticRouteConfig();
    }

    public void SavePedalConfig()
    {
        var config = PedalConfigManager.Instance.Config;
        config.Enabled = PedalInputEnabled;
        config.Gas.Deadzone = PedalGasDeadzone;
        config.Brake.Deadzone = PedalBrakeDeadzone;
        config.Gas.Min = PedalGasMin;
        config.Gas.Max = PedalGasMax;
        config.Brake.Min = PedalBrakeMin;
        config.Brake.Max = PedalBrakeMax;
        config.Gas.Smoothing = PedalGasSmoothing;
        config.Brake.Smoothing = PedalBrakeSmoothing;
        config.Gas.Invert = PedalGasInvert;
        config.Brake.Invert = PedalBrakeInvert;
        PedalConfigManager.Instance.Save(config);

        SaveHapticRouteConfig();
    }

    private void LoadHapticRouteConfig()
    {
        var routeCfg = _telemetryLoop?.LiveServer?.HapticRouteConfig;
        if (routeCfg == null) return;

        PedalMasterBrakeGain = routeCfg.BrakeHapticGain;
        PedalMasterGasGain = routeCfg.GasHapticGain;

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
            PedalLiveSource = "—";
            return;
        }

        var raw = _telemetryLoop.LatestRaw;
        var pedalInput = _telemetryLoop.PedalInput;

        if (pedalInput.TryGetState(out var pedalState))
        {
            PedalLiveGas = pedalState.GasInput;
            PedalLiveBrake = pedalState.BrakeInput;
            PedalLiveSource = pedalState.Source.ToString();
            PedalSourceType = pedalState.Source.ToString();

            if (_pedalUpdateCounter++ % 30 == 0)
                PedalDiagnosticInfo = pedalInput.GetDiagnosticSummary();
        }
        else if (raw != null)
        {
            PedalLiveGas = raw.GasInput;
            PedalLiveBrake = raw.BrakeInput;
            PedalLiveSource = "Game Telemetry";
        }
    }

    private int _pedalUpdateCounter;
}
