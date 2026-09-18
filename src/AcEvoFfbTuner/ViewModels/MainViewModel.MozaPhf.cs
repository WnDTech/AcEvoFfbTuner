using System.Collections.ObjectModel;
using System.Windows.Input;
using AcEvoFfbTuner.Core.Config;
using AcEvoFfbTuner.Core.PedalHaptics.Providers;
using CommunityToolkit.Mvvm.Input;

namespace AcEvoFfbTuner.ViewModels;

/// <summary>
/// MOZA PHF Pedal Haptic Feedback (RS102 kit + RS103 add-on modules).
/// The RS102 kit ships one control box + one module; each RS103 adds a
/// module for another pedal. All three ports (throttle/brake/clutch) are
/// driven through the single control-box HID device — the module checkboxes
/// below record which ports are physically fitted.
/// </summary>
public sealed partial class MainViewModel
{
    private MozaPhfProvider? _mozaPhf;
    private List<MozaPhfProvider.PhfCandidate> _mozaPhfCandidates = [];

    public ObservableCollection<string> MozaPhfAvailableDevices { get; } = [];

    private string _mozaPhfSelectedDevice = "";
    public string MozaPhfSelectedDevice
    {
        get => _mozaPhfSelectedDevice;
        set => SetProperty(ref _mozaPhfSelectedDevice, value);
    }

    private bool _mozaPhfIsConnected;
    public bool MozaPhfIsConnected
    {
        get => _mozaPhfIsConnected;
        set => SetProperty(ref _mozaPhfIsConnected, value);
    }

    private string _mozaPhfStatus = "No PHF control box connected";
    public string MozaPhfStatus
    {
        get => _mozaPhfStatus;
        set => SetProperty(ref _mozaPhfStatus, value);
    }

    private bool _mozaPhfEnabled = true;
    public bool MozaPhfEnabled
    {
        get => _mozaPhfEnabled;
        set
        {
            if (SetProperty(ref _mozaPhfEnabled, value))
                SaveMozaPhfConfig();
        }
    }

    private bool _mozaPhfBrakeModule = true;
    public bool MozaPhfBrakeModule
    {
        get => _mozaPhfBrakeModule;
        set
        {
            if (SetProperty(ref _mozaPhfBrakeModule, value))
                SaveMozaPhfConfig();
        }
    }

    private bool _mozaPhfThrottleModule;
    public bool MozaPhfThrottleModule
    {
        get => _mozaPhfThrottleModule;
        set
        {
            if (SetProperty(ref _mozaPhfThrottleModule, value))
                SaveMozaPhfConfig();
        }
    }

    private bool _mozaPhfClutchModule;
    public bool MozaPhfClutchModule
    {
        get => _mozaPhfClutchModule;
        set
        {
            if (SetProperty(ref _mozaPhfClutchModule, value))
                SaveMozaPhfConfig();
        }
    }

    private int _mozaPhfTestIntensity = 60;
    public int MozaPhfTestIntensity
    {
        get => _mozaPhfTestIntensity;
        set => SetProperty(ref _mozaPhfTestIntensity, value);
    }

    private int _mozaPhfTestFrequency = 40;
    public int MozaPhfTestFrequency
    {
        get => _mozaPhfTestFrequency;
        set => SetProperty(ref _mozaPhfTestFrequency, value);
    }

    private bool _mozaPhfTestActive;
    public bool MozaPhfTestActive
    {
        get => _mozaPhfTestActive;
        set => SetProperty(ref _mozaPhfTestActive, value);
    }

    private int _mozaPhfAbsFreq = 18;
    public int MozaPhfAbsFreq
    {
        get => _mozaPhfAbsFreq;
        set { if (SetProperty(ref _mozaPhfAbsFreq, value)) SaveMozaPhfConfig(); }
    }

    private int _mozaPhfTcFreq = 30;
    public int MozaPhfTcFreq
    {
        get => _mozaPhfTcFreq;
        set { if (SetProperty(ref _mozaPhfTcFreq, value)) SaveMozaPhfConfig(); }
    }

    private int _mozaPhfCurbFreq = 45;
    public int MozaPhfCurbFreq
    {
        get => _mozaPhfCurbFreq;
        set { if (SetProperty(ref _mozaPhfCurbFreq, value)) SaveMozaPhfConfig(); }
    }

    private int _mozaPhfRoadFreq = 65;
    public int MozaPhfRoadFreq
    {
        get => _mozaPhfRoadFreq;
        set { if (SetProperty(ref _mozaPhfRoadFreq, value)) SaveMozaPhfConfig(); }
    }

    private int _mozaPhfScrubFreq = 50;
    public int MozaPhfScrubFreq
    {
        get => _mozaPhfScrubFreq;
        set { if (SetProperty(ref _mozaPhfScrubFreq, value)) SaveMozaPhfConfig(); }
    }

    private int _mozaPhfRpmFreq = 80;
    public int MozaPhfRpmFreq
    {
        get => _mozaPhfRpmFreq;
        set { if (SetProperty(ref _mozaPhfRpmFreq, value)) SaveMozaPhfConfig(); }
    }

    public ICommand RefreshMozaPhfCommand { get; private set; } = null!;
    public ICommand ConnectMozaPhfCommand { get; private set; } = null!;
    public ICommand DisconnectMozaPhfCommand { get; private set; } = null!;
    public ICommand MozaPhfTestBrakeCommand { get; private set; } = null!;
    public ICommand MozaPhfTestGasCommand { get; private set; } = null!;
    public ICommand MozaPhfTestClutchCommand { get; private set; } = null!;
    public ICommand MozaPhfStopTestCommand { get; private set; } = null!;

    private void InitMozaPhfCommands()
    {
        RefreshMozaPhfCommand = new RelayCommand(OnRefreshMozaPhf);
        ConnectMozaPhfCommand = new RelayCommand(OnConnectMozaPhf);
        DisconnectMozaPhfCommand = new RelayCommand(OnDisconnectMozaPhf);
        MozaPhfTestBrakeCommand = new RelayCommand(OnMozaPhfTestBrake);
        MozaPhfTestGasCommand = new RelayCommand(OnMozaPhfTestGas);
        MozaPhfTestClutchCommand = new RelayCommand(OnMozaPhfTestClutch);
        MozaPhfStopTestCommand = new RelayCommand(OnMozaPhfStopTest);

        LoadMozaPhfConfigToUi();
        OnRefreshMozaPhf();
    }

    private MozaPhfConfig CurrentMozaPhfConfig()
    {
        var cfg = MozaPhfConfigManager.Instance.Config;
        cfg.Enabled = MozaPhfEnabled;
        cfg.SetModule(MozaPhfChannel.Brake, MozaPhfBrakeModule);
        cfg.SetModule(MozaPhfChannel.Throttle, MozaPhfThrottleModule);
        cfg.SetModule(MozaPhfChannel.Clutch, MozaPhfClutchModule);
        cfg.AbsFrequency = MozaPhfAbsFreq;
        cfg.TcFrequency = MozaPhfTcFreq;
        cfg.CurbFrequency = MozaPhfCurbFreq;
        cfg.RoadFrequency = MozaPhfRoadFreq;
        cfg.ScrubFrequency = MozaPhfScrubFreq;
        cfg.RpmFrequency = MozaPhfRpmFreq;
        return cfg;
    }

    private void LoadMozaPhfConfigToUi()
    {
        var cfg = MozaPhfConfigManager.Instance.Config;
        _mozaPhfEnabled = cfg.Enabled;
        _mozaPhfBrakeModule = cfg.HasModule(MozaPhfChannel.Brake);
        _mozaPhfThrottleModule = cfg.HasModule(MozaPhfChannel.Throttle);
        _mozaPhfClutchModule = cfg.HasModule(MozaPhfChannel.Clutch);
        _mozaPhfAbsFreq = cfg.AbsFrequency;
        _mozaPhfTcFreq = cfg.TcFrequency;
        _mozaPhfCurbFreq = cfg.CurbFrequency;
        _mozaPhfRoadFreq = cfg.RoadFrequency;
        _mozaPhfScrubFreq = cfg.ScrubFrequency;
        _mozaPhfRpmFreq = cfg.RpmFrequency;
    }

    private void SaveMozaPhfConfig()
    {
        try { MozaPhfConfigManager.Instance.Save(CurrentMozaPhfConfig()); }
        catch { }
    }

    private static string FormatCandidate(MozaPhfProvider.PhfCandidate c)
    {
        string name = string.IsNullOrEmpty(c.Product) ? "MOZA HID device" : c.Product;
        return $"{name} (0x{c.ProductId:X4}){(c.LikelyPhf ? " ★" : "")}";
    }

    private void OnRefreshMozaPhf()
    {
        _mozaPhfCandidates = MozaPhfProvider.EnumerateCandidates();
        MozaPhfAvailableDevices.Clear();
        foreach (var c in _mozaPhfCandidates)
            MozaPhfAvailableDevices.Add(FormatCandidate(c));

        if (_mozaPhfCandidates.Count == 0)
        {
            if (!MozaPhfIsConnected)
                MozaPhfStatus = "No MOZA HID devices found — connect the PHF control box via USB (direct-PC mode) and refresh";
        }
        else if (string.IsNullOrEmpty(MozaPhfSelectedDevice)
                 || !MozaPhfAvailableDevices.Contains(MozaPhfSelectedDevice))
        {
            // Prefer the flagged PHF candidate, else the first MOZA device.
            int pick = _mozaPhfCandidates.FindIndex(c => c.LikelyPhf);
            MozaPhfSelectedDevice = MozaPhfAvailableDevices[pick >= 0 ? pick : 0];
            if (!MozaPhfIsConnected)
                MozaPhfStatus = "Select the PHF control box and connect. Close Pit House first — it locks MOZA devices.";
        }

        if (MozaPhfProvider.IsPitHouseRunning() && !MozaPhfIsConnected)
            MozaPhfStatus += " WARNING: Pit House is running.";
    }

    private void OnConnectMozaPhf()
    {
        if (string.IsNullOrWhiteSpace(MozaPhfSelectedDevice)) return;

        int idx = MozaPhfAvailableDevices.IndexOf(MozaPhfSelectedDevice);
        string? path = idx >= 0 && idx < _mozaPhfCandidates.Count
            ? _mozaPhfCandidates[idx].Path
            : null;

        _mozaPhf?.Dispose();
        _mozaPhf = new MozaPhfProvider(CurrentMozaPhfConfig());

        if (_mozaPhf.Connect(path))
        {
            MozaPhfIsConnected = true;
            try
            {
                var cfg = CurrentMozaPhfConfig();
                cfg.DevicePath = _mozaPhf.ConnectedPath;
                MozaPhfConfigManager.Instance.Save(cfg);
            }
            catch { }
            MozaPhfStatus = $"Connected: {_mozaPhf.GetDiagnosticInfo()}";
            AddSystemLog($"MOZA PHF: {_mozaPhf.GetDiagnosticInfo()}");
        }
        else
        {
            MozaPhfIsConnected = false;
            MozaPhfStatus = "Failed to connect — close Pit House (it locks MOZA devices) and retry";
            _mozaPhf.Dispose();
            _mozaPhf = null;
            AddSystemLog("MOZA PHF: connect failed");
        }
    }

    private void OnDisconnectMozaPhf()
    {
        _mozaPhf?.Dispose();
        _mozaPhf = null;
        MozaPhfIsConnected = false;
        MozaPhfTestActive = false;
        MozaPhfStatus = "Disconnected";
    }

    private void OnMozaPhfTestBrake()
    {
        if (_mozaPhf?.IsAvailable != true)
        {
            AddSystemLog("MOZA PHF TEST BRAKE: device not available");
            return;
        }
        MozaPhfTestActive = true;
        _mozaPhf.SetChannel(MozaPhfChannel.Brake,
            MozaPhfTestIntensity / 100f, MozaPhfTestFrequency);
        MozaPhfStatus = $"TEST: Brake module at {MozaPhfTestIntensity}% / {MozaPhfTestFrequency}Hz — press Stop to halt";
    }

    private void OnMozaPhfTestGas()
    {
        if (_mozaPhf?.IsAvailable != true)
        {
            AddSystemLog("MOZA PHF TEST GAS: device not available");
            return;
        }
        MozaPhfTestActive = true;
        _mozaPhf.SetChannel(MozaPhfChannel.Throttle,
            MozaPhfTestIntensity / 100f, MozaPhfTestFrequency);
        MozaPhfStatus = $"TEST: Throttle module at {MozaPhfTestIntensity}% / {MozaPhfTestFrequency}Hz — press Stop to halt";
    }

    private void OnMozaPhfTestClutch()
    {
        if (_mozaPhf?.IsAvailable != true)
        {
            AddSystemLog("MOZA PHF TEST CLUTCH: device not available");
            return;
        }
        MozaPhfTestActive = true;
        _mozaPhf.SetChannel(MozaPhfChannel.Clutch,
            MozaPhfTestIntensity / 100f, MozaPhfTestFrequency);
        MozaPhfStatus = $"TEST: Clutch module at {MozaPhfTestIntensity}% / {MozaPhfTestFrequency}Hz — press Stop to halt";
    }

    private void OnMozaPhfStopTest()
    {
        _mozaPhf?.StopAll();
        MozaPhfTestActive = false;
        MozaPhfStatus = MozaPhfIsConnected ? "Motor test stopped" : MozaPhfStatus;
    }

    /// <summary>Called from the telemetry tick. Intensities are 0..1.</summary>
    private void DriveMozaPhf(float brake01, int brakeHz,
        float gas01, int gasHz, float clutch01, int clutchHz)
    {
        var dev = _mozaPhf;
        if (dev?.IsAvailable != true || MozaPhfTestActive || !MozaPhfEnabled)
            return;

        dev.SetChannel(MozaPhfChannel.Brake, brake01, brakeHz);
        dev.SetChannel(MozaPhfChannel.Throttle, gas01, gasHz);
        dev.SetChannel(MozaPhfChannel.Clutch, clutch01, clutchHz);
    }
}
