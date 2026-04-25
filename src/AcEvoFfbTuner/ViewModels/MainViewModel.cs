using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using AcEvoFfbTuner.Core;
using AcEvoFfbTuner.Core.DirectInput;
using AcEvoFfbTuner.Core.FfbProcessing;
using AcEvoFfbTuner.Core.FfbProcessing.Models;
using AcEvoFfbTuner.Core.Profiles;
using AcEvoFfbTuner.Core.SharedMemory;
using AcEvoFfbTuner.Core.TrackMapping;
using AcEvoFfbTuner.Services;
using AcEvoFfbTuner.Views;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AcEvoFfbTuner.ViewModels;

public sealed partial class MainViewModel : ObservableObject, IDisposable
{
    private readonly SharedMemoryReader _reader;
    private readonly FfbPipeline _pipeline;
    private readonly FfbDeviceManager _deviceManager;
    private readonly TelemetryLoop _telemetryLoop;
    private readonly ProfileManager _profileManager;

    public string AppVersion { get; } =
        System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0.0";

    [ObservableProperty]
    private string _statusText = "Ready";

    [ObservableProperty]
    private bool _isGameConnected;

    [ObservableProperty]
    private bool _isDeviceConnected;

    [ObservableProperty]
    private bool _isRunning;

    [ObservableProperty]
    private string _deviceName = "No device";

    [ObservableProperty]
    private int _packetsPerSecond;

    [ObservableProperty]
    private float _currentForceOutput;

    [ObservableProperty]
    private float _currentRawForce;

    [ObservableProperty]
    private bool _isClipping;

    [ObservableProperty]
    private float _speedKmh;

    [ObservableProperty]
    private float _steerAngle;

    [ObservableProperty]
    private string _debugSnapshot = "Waiting for telemetry data...";

    [ObservableProperty]
    private FfbMixMode _selectedMixMode = FfbMixMode.Replace;

    [ObservableProperty]
    private float _outputGain = 1.0f;

    [ObservableProperty]
    private float _forceSensitivity = 20f;

    [ObservableProperty]
    private float _forceScale = 1.0f;    [ObservableProperty]
    private float _mzFrontGain = 1.0f;

    [ObservableProperty]
    private bool _mzFrontEnabled = true;

    [ObservableProperty]
    private float _fxFrontGain = 0.1f;

    [ObservableProperty]
    private bool _fxFrontEnabled = true;

    [ObservableProperty]
    private float _fyFrontGain = 0.1f;

    [ObservableProperty]
    private bool _fyFrontEnabled = true;

    [ObservableProperty]
    private float _mzRearGain = 0.0f;

    [ObservableProperty]
    private bool _mzRearEnabled = false;

    [ObservableProperty]
    private float _fxRearGain = 0.0f;

    [ObservableProperty]
    private bool _fxRearEnabled = false;

    [ObservableProperty]
    private float _fyRearGain = 0.0f;

    [ObservableProperty]
    private bool _fyRearEnabled = false;

    [ObservableProperty]
    private float _finalFfGain = 0.0f;

    [ObservableProperty]
    private bool _finalFfEnabled = false;

    [ObservableProperty]
    private float _wheelLoadWeighting = 0.0f;

    [ObservableProperty]
    private float _mzScale = 25f;

    [ObservableProperty]
    private float _fxScale = 5000f;

    [ObservableProperty]
    private float _fyScale = 5000f;

    [ObservableProperty]
    private float _speedDamping = 0.3f;

    [ObservableProperty]
    private float _frictionLevel = 0.25f;

    [ObservableProperty]
    private float _inertiaWeight = 0.1f;

    [ObservableProperty]
    private float _dampingSpeedReference = 300f;

    [ObservableProperty]
    private float _lowSpeedDampingBoost = 3.0f;

    [ObservableProperty]
    private float _lowSpeedThreshold = 20f;

    [ObservableProperty]
    private float _slipRatioGain;

    [ObservableProperty]
    private float _slipAngleGain;

    [ObservableProperty]
    private float _slipThreshold = 0.05f;

    [ObservableProperty]
    private bool _slipUseFrontOnly = true;

    [ObservableProperty]
    private float _corneringForce;

    [ObservableProperty]
    private float _accelerationBrakingForce;

    [ObservableProperty]
    private float _roadFeel;

    [ObservableProperty]
    private float _carRotationForce;

    [ObservableProperty]
    private bool _autoGainEnabled;

    [ObservableProperty]
    private float _autoGainScale = 1.0f;

    [ObservableProperty]
    private float _curbGain = 1.0f;

    [ObservableProperty]
    private float _slipGain = 0.8f;

    [ObservableProperty]
    private float _roadGain = 0.5f;

    [ObservableProperty]
    private float _absGain = 1.0f;

    [ObservableProperty]
    private float _vibrationMasterGain = 0.7f;

    [ObservableProperty]
    private float _suspensionRoadGain = 1.5f;

    [ObservableProperty]
    private float _maxForceLimit = 0.8f;

    [ObservableProperty]
    private float _wheelMaxTorqueNm = 5.5f;

    [ObservableProperty]
    private float _compressionPower = 1.0f;

    [ObservableProperty]
    private bool _signCorrectionEnabled = true;

    [ObservableProperty]
    private float _maxSlewRate = 0.20f;

    [ObservableProperty]
    private float _centerSuppressionDegrees = 6.0f;

    [ObservableProperty]
    private float _centerKneePower = 1.0f;

    [ObservableProperty]
    private float _hysteresisThreshold = 0.015f;

    [ObservableProperty]
    private float _noiseFloor = 0.005f;

    [ObservableProperty]
    private int _hysteresisWatchdogFrames = 5;

    [ObservableProperty]
    private float _centerBlendDegrees = 1.0f;

    [ObservableProperty]
    private float _steerVelocityReference = 10.0f;

    [ObservableProperty]
    private float _velocityDeadzone = 0.05f;

    [ObservableProperty]
    private float _lowSpeedSmoothKmh = 25.0f;

    [ObservableProperty]
    private bool _forceInvertEnabled;

    [ObservableProperty]
    private int _selectedLutPresetIndex;

    [ObservableProperty]
    private int _steeringLockDegrees = 900;

    [ObservableProperty]
    private FfbProfile? _selectedProfile;

    [ObservableProperty]
    private int _ledBrightness = 100;

    [ObservableProperty]
    private int _ledFlashRate = 16;

    [ObservableProperty]
    private bool _ledAbsFlashEnabled = true;

    [ObservableProperty]
    private bool _ledFlagIndicatorsEnabled = true;

    [ObservableProperty]
    private bool _ledShiftLimiterFlashEnabled = true;

    [ObservableProperty]
    private int _ledColorSchemeIndex;

    [ObservableProperty]
    private int _ledRpmPresetIndex;

    [ObservableProperty]
    private int _ledRpmThreshold1 = 50;
    [ObservableProperty]
    private int _ledRpmThreshold2 = 60;
    [ObservableProperty]
    private int _ledRpmThreshold3 = 70;
    [ObservableProperty]
    private int _ledRpmThreshold4 = 80;
    [ObservableProperty]
    private int _ledRpmThreshold5 = 85;
    [ObservableProperty]
    private int _ledRpmThreshold6 = 90;
    [ObservableProperty]
    private int _ledRpmThreshold7 = 93;
    [ObservableProperty]
    private int _ledRpmThreshold8 = 96;
    [ObservableProperty]
    private int _ledRpmThreshold9 = 98;
    [ObservableProperty]
    private int _ledRpmThreshold10 = 100;

    [ObservableProperty]
    private int _snapshotButtonIndex = -1;

    [ObservableProperty]
    private int _snapshotButtonComboIndex;

    [ObservableProperty]
    private bool _isTrackMapAvailable;

    [ObservableProperty]
    private bool _isTrackMapRecording;

    [ObservableProperty]
    private bool _isOnTrackMap;

    [ObservableProperty]
    private float _trackProgress;

    [ObservableProperty]
    private float _trackDistanceFromCenter;

    [ObservableProperty]
    private float _trackLengthM;

    [ObservableProperty]
    private int _trackWaypointCount;

    [ObservableProperty]
    private float _carPosX;

    [ObservableProperty]
    private float _carPosZ;

    [ObservableProperty]
    private float _carHeading;

    [ObservableProperty]
    private string _currentCornerName = "";

    [ObservableProperty]
    private string _currentCornerType = "";

    private int _sectorStatsCounter;

    [ObservableProperty]
    private int _currentSectorNumber;

    [ObservableProperty]
    private string _sectorStats = "";

    [ObservableProperty]
    private int _cornerCount;

    [ObservableProperty]
    private int _sectorCount;

    [ObservableProperty]
    private int _completedLapCount;

    [ObservableProperty]
    private string _lapComparison = "";

    [ObservableProperty]
    private bool _showForceHeatmap = true;

    [ObservableProperty]
    private bool _showTrackEdges;

    [ObservableProperty]
    private bool _showDiagnostics = true;

    [ObservableProperty]
    private string _diagnosticSummary = "";

    [ObservableProperty]
    private string _diagnosticVerdict = "";

    [ObservableProperty]
    private string _lastEventInfo = "";

    [ObservableProperty]
    private string _recommendationsText = "";

    [ObservableProperty]
    private string _diagnosticCoverage = "";

    public ObservableCollection<FfbRecommendation> Recommendations { get; } = new();

    [ObservableProperty]
    private string _detectedTrackName = "";

    [ObservableProperty]
    private bool _isPitDetected;

    [ObservableProperty]
    private bool _isInPit;

    [ObservableProperty]
    private string _trackMapStatus = "";

    public TrackMap? CurrentTrackMap => _telemetryLoop.MapBuilder.CurrentMap;

    partial void OnSnapshotButtonComboIndexChanged(int value)
    {
        SnapshotButtonIndex = value <= 0 ? -1 : value - 1;
    }

    private bool[]? _prevSnapshotButtons;
    private bool[]? _prevPanicButtons;

    private void PollSnapshotButton()
    {
        if (!IsDeviceConnected) return;

        var buttons = _deviceManager.PollButtons();
        if (buttons == null)
        {
            if (Application.Current?.MainWindow is MainWindow mw)
                mw.ProfButtonsDetected.Text = "";
        }
        else
        {
            var pressed = new System.Text.StringBuilder();
            for (int i = 0; i < buttons.Length; i++)
            {
                if (buttons[i])
                {
                    if (pressed.Length > 0) pressed.Append(", ");
                    pressed.Append($"Btn{i + 1}");
                }
            }

            if (Application.Current?.MainWindow is MainWindow mw2)
                mw2.ProfButtonsDetected.Text = pressed.Length > 0 ? $"Pressed: {pressed}" : "";

            if (SnapshotButtonIndex >= 0 && SnapshotButtonIndex < buttons.Length)
            {
                bool pressed2 = buttons[SnapshotButtonIndex];
                bool wasPressed = _prevSnapshotButtons != null && SnapshotButtonIndex < _prevSnapshotButtons.Length && _prevSnapshotButtons[SnapshotButtonIndex];
                _prevSnapshotButtons = (bool[])buttons.Clone();

                if (pressed2 && !wasPressed)
                {
                    if (Application.Current?.MainWindow is MainWindow mw3)
                    {
                        string path = mw3.AutoSaveSnapshot();
                        StatusText = $"Wheel snapshot saved: {Path.GetFileName(path)}";
                    }
                }
            }
        }

        PollPanicButton();
    }

    private void PollPanicButton()
    {
        if (PanicButtonIndex < 0) return;

        var buttons = _deviceManager.PollSecondaryButtons();
        if (buttons == null) return;

        if (PanicButtonIndex >= buttons.Length) return;

        bool pressed = buttons[PanicButtonIndex];
        bool wasPressed = _prevPanicButtons != null && PanicButtonIndex < _prevPanicButtons.Length && _prevPanicButtons[PanicButtonIndex];
        _prevPanicButtons = (bool[])buttons.Clone();

        if (pressed && !wasPressed)
            PanicStop();
    }

    public ObservableCollection<FfbDeviceInfo> AvailableDevices { get; } = new();
    public ObservableCollection<FfbDeviceInfo> PanicDevices { get; } = new();
    public ObservableCollection<FfbProfile> Profiles { get; } = new();

    [ObservableProperty]
    private FfbDeviceInfo? _selectedDevice;

    [ObservableProperty]
    private FfbDeviceInfo? _selectedPanicDevice;

    [ObservableProperty]
    private int _panicButtonComboIndex;

    public int PanicButtonIndex => PanicButtonComboIndex <= 0 ? -1 : PanicButtonComboIndex - 1;
    public int PanicDeviceButtonCount => _deviceManager.SecondaryButtonCount;

    public FfbPipeline Pipeline => _pipeline;
    public int DeviceButtonCount => _deviceManager.ButtonCount;

    private readonly DispatcherTimer _uiUpdateTimer;

    public MainViewModel()
    {
        _reader = new SharedMemoryReader();
        _pipeline = new FfbPipeline();
        _deviceManager = new FfbDeviceManager();
        _telemetryLoop = new TelemetryLoop(_reader, _pipeline, _deviceManager);
        _profileManager = new ProfileManager();

        _telemetryLoop.StatusChanged += status => Application.Current?.Dispatcher.Invoke(() => StatusText = status);
        _telemetryLoop.GameConnectionChanged += () => Application.Current?.Dispatcher.Invoke(() =>
        {
            IsGameConnected = _telemetryLoop.IsGameConnected;
        });
        _telemetryLoop.TrackMapCompleted += map => Application.Current?.Dispatcher.Invoke(() =>
        {
            IsTrackMapAvailable = true;
            IsTrackMapRecording = false;
            TrackLengthM = map.TrackLengthM;
            TrackWaypointCount = map.Waypoints.Count;
            CornerCount = map.Corners.Count;
            SectorCount = map.Sectors.Count;
            if (string.IsNullOrEmpty(map.TrackName) || map.TrackName.StartsWith("track_"))
                map.TrackName = _telemetryLoop.DetectedTrackName;
            DetectedTrackName = map.TrackName;
            IsPitDetected = map.PitLane.IsDetected;
            TrackMapStatus = $"{map.Waypoints.Count} pts | {map.TrackLengthM:F0}m | {map.Corners.Count} corners | {map.Sectors.Count} sectors";
        });
        _telemetryLoop.StaticDataReceived += (trackName, config, lengthM) => Application.Current?.Dispatcher.Invoke(() =>
        {
            DetectedTrackName = trackName;
            if (!string.IsNullOrEmpty(trackName))
                StatusText = $"Connected — Track: {trackName} ({config}) {lengthM:F0}m";

            if (!_telemetryLoop.MapBuilder.HasCompleteMap && !string.IsNullOrEmpty(trackName))
            {
                var savedMap = TrackMap.Load(trackName);
                if (savedMap != null && savedMap.Waypoints.Count >= 10)
                {
                    savedMap.InvalidateCache();
                    savedMap.GetCumulativeDistances();
                    if (savedMap.Corners.Count == 0) savedMap.Analyze();

                    _telemetryLoop.MapBuilder.SetImportedMap(savedMap);
                    _telemetryLoop.PositionDetector.SetMap(savedMap);
                    _telemetryLoop.ForceHeatmap.Initialize(savedMap.Waypoints.Count);
                    _telemetryLoop.LapRecorder.Initialize(savedMap.Waypoints.Count);
                    _telemetryLoop.DiagnosticHeatmap.Initialize(savedMap.Waypoints.Count);
                    _telemetryLoop.RealignToMap();

                    IsTrackMapAvailable = true;
                    TrackLengthM = savedMap.TrackLengthM;
                    TrackWaypointCount = savedMap.Waypoints.Count;
                    CornerCount = savedMap.Corners.Count;
                    SectorCount = savedMap.Sectors.Count;
                    IsPitDetected = savedMap.PitLane.IsDetected;
                    TrackMapStatus = $"Auto-loaded: {savedMap.Waypoints.Count} pts | {savedMap.TrackLengthM:F0}m | {savedMap.Corners.Count} corners | {savedMap.Sectors.Count} sectors";
                    StatusText = $"Auto-loaded track map: {trackName} ({savedMap.Waypoints.Count} pts, {savedMap.Corners.Count} corners)";
                }
            }
        });

        _uiUpdateTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };
        _uiUpdateTimer.Tick += OnUiUpdate;
    }

    public void Initialize()
    {
        _profileManager.Initialize();
        RefreshProfiles();
        RefreshDevices();

        if (_profileManager.ActiveProfile != null)
        {
            _profileManager.ActiveProfile.ApplyToPipeline(_pipeline);
            LoadProfileValues(_profileManager.ActiveProfile);
            SelectedProfile = Profiles.FirstOrDefault(p => p.Name == _profileManager.ActiveProfile.Name);
        }
    }

    [RelayCommand]
    private void RefreshDevices()
    {
        var allDevices = _deviceManager.EnumerateFfbDevices();

        AvailableDevices.Clear();
        foreach (var device in allDevices)
            AvailableDevices.Add(device);

        PanicDevices.Clear();
        PanicDevices.Add(new FfbDeviceInfo { ProductName = "None", IsFfbCapable = false });
        foreach (var device in allDevices)
            PanicDevices.Add(device);
    }

    [RelayCommand]
    private void ConnectDevice()
    {
        if (SelectedDevice == null) return;

        var window = Application.Current?.MainWindow;
        if (window != null)
        {
            var helper = new System.Windows.Interop.WindowInteropHelper(window);
            _deviceManager.SetWindowHandle(helper.Handle);
        }

        if (_deviceManager.TryConnectDevice(SelectedDevice))
        {
            IsDeviceConnected = true;
            DeviceName = SelectedDevice.ProductName;
            AutoDetectWheelTorque(SelectedDevice.ProductName);
            string ledStatus = _deviceManager.IsLedControllerConnected
                ? $" | LEDs: {_deviceManager.LedControllerVendor}"
                : $" | LEDs: {_deviceManager.LedDiagnosticInfo.Split('\n').LastOrDefault() ?? "not found"}";
            string vibStatus = _deviceManager.SupportsPeriodicEffects
                ? ""
                : " | WARNING: wheel does not report periodic effect support — kerb/slip vibration may not work";
            StatusText = (_deviceManager.LastError ?? $"Connected to {SelectedDevice.ProductName}") + ledStatus + vibStatus;
            PushLedConfig();
        }
        else
        {
            StatusText = _deviceManager.LastError ?? "Failed to connect to device";
        }
    }

    [RelayCommand]
    private void DisconnectDevice()
    {
        _deviceManager.DisconnectDevice();
        IsDeviceConnected = false;
        DeviceName = "No device";
    }

    partial void OnSelectedPanicDeviceChanged(FfbDeviceInfo? value)
    {
        _deviceManager.DisconnectSecondaryDevice();

        if (value == null || value.ProductName == "None")
        {
            OnPropertyChanged(nameof(PanicDeviceButtonCount));
            return;
        }

        var window = Application.Current?.MainWindow;
        if (window != null)
        {
            var helper = new System.Windows.Interop.WindowInteropHelper(window);
            _deviceManager.SetWindowHandle(helper.Handle);
        }

        if (_deviceManager.TryConnectSecondaryDevice(value))
            StatusText = $"Panic button device: {value.ProductName} ({_deviceManager.SecondaryButtonCount} buttons)";
        else
            StatusText = "Failed to connect panic button device";

        OnPropertyChanged(nameof(PanicDeviceButtonCount));
    }

    private void AutoDetectWheelTorque(string productName)
    {
        float torque = DetectTorqueFromProductName(productName);
        if (torque > 0)
        {
            WheelMaxTorqueNm = torque;
            StatusText = $"Detected {productName} — {torque:F1} Nm wheelbase";
        }
    }

    private static float DetectTorqueFromProductName(string name)
    {
        if (string.IsNullOrEmpty(name)) return 0f;
        var n = name.ToUpperInvariant();

        if (n.Contains("R5")) return 5.5f;
        if (n.Contains("R9")) return 9f;
        if (n.Contains("R12")) return 12f;
        if (n.Contains("R16")) return 16f;
        if (n.Contains("R21")) return 21f;

        if (n.Contains("CSL DD")) return 5f;
        if (n.Contains("CLUBSPORT DD")) return n.Contains("8") ? 8f : 15f;
        if (n.Contains("DD PRO")) return 8f;
        if (n.Contains("DD1")) return 18f;
        if (n.Contains("DD2")) return 25f;

        if (n.Contains("ALPHA MINI") || n.Contains("ALPHA-MINI")) return 10f;
        if (n.Contains("ALPHA U")) return 22f;
        if (n.Contains("ALPHA")) return 15f;

        if (n.Contains("T818")) return 10f;
        if (n.Contains("T300")) return 2f;
        if (n.Contains("T150")) return 1.5f;
        if (n.Contains("G29") || n.Contains("G920")) return 2.1f;

        return 0f;
    }

    [RelayCommand]
    private void ToggleTelemetry()
    {
        if (IsRunning)
        {
            _telemetryLoop.Start();
            _uiUpdateTimer.Start();
            StatusText = "Running";
        }
        else
        {
            _telemetryLoop.Stop();
            _uiUpdateTimer.Stop();
            StatusText = "Stopped";
        }
    }

    [RelayCommand]
    private void ApplyLutPreset()
    {
        var lut = _pipeline.LutCurve;
        lut.SetLinear();

        switch (SelectedLutPresetIndex)
        {
            case 0: lut.SetLinear(); break;
            case 1: lut.SetSoftCenter(); break;
            case 2: lut.SetProgressive(); break;
            case 3: lut.SetDeadZone(); break;
        }
    }

    [RelayCommand]
    private void SaveCurrentProfile()
    {
        if (SelectedProfile == null) return;

        PushValuesToPipeline();
        SelectedProfile.LedEffects = LedEffectConfigDto.FromConfig(new LedEffectConfig
        {
            Brightness = LedBrightness,
            FlashRateTicks = LedFlashRate,
            AbsFlashEnabled = LedAbsFlashEnabled,
            FlagIndicatorsEnabled = LedFlagIndicatorsEnabled,
            ShiftLimiterFlashEnabled = LedShiftLimiterFlashEnabled,
            ColorScheme = LedColorSchemeIndex >= 0 && LedColorSchemeIndex <= 4
                ? (LedColorScheme)LedColorSchemeIndex
                : LedColorScheme.TrafficLight,
            RpmPreset = LedRpmPresetIndex >= 0 && LedRpmPresetIndex <= 4
                ? (LedRpmPreset)LedRpmPresetIndex
                : LedRpmPreset.Default,
            RpmThresholds = GetCurrentRpmThresholds(),
            CustomColors = LedEffectConfig.BuildTrafficLightColors()
        });
        _profileManager.SaveProfileFromPipeline(_pipeline, SelectedProfile.Name);
        SelectedProfile.WheelMaxTorqueNm = WheelMaxTorqueNm;
        SelectedProfile.LastTelemetrySnapshot = _telemetryLoop.CaptureTelemetrySnapshot();
        _profileManager.SaveProfile(SelectedProfile);
        _profileManager.SetActiveProfile(SelectedProfile);
    }

    private void AutoSaveDiagnosticProfile()
    {
        PushValuesToPipeline();

        string baseName = SelectedProfile?.Name ?? "unsaved";
        string diagName = $"{baseName}_diag_{DateTime.Now:yyyyMMdd_HHmmss}";
        var profile = _profileManager.SaveProfileFromPipeline(_pipeline, diagName);
        profile.WheelMaxTorqueNm = WheelMaxTorqueNm;
        profile.LastTelemetrySnapshot = _telemetryLoop.CaptureTelemetrySnapshot();
        _profileManager.SaveProfile(profile);
    }

    [RelayCommand]
    private void SaveAsNewProfile()
    {
        PushValuesToPipeline();

        string baseName = SelectedProfile?.Name ?? "Profile";
        string suggestion = GetNextProfileName(baseName);

        var dialog = new Views.InputDialog(suggestion)
        {
            Owner = Application.Current?.MainWindow
        };

        if (dialog.ShowDialog() == true)
        {
            var profile = _profileManager.SaveProfileFromPipeline(_pipeline, dialog.Result!);
            profile.WheelMaxTorqueNm = WheelMaxTorqueNm;
            profile.LastTelemetrySnapshot = _telemetryLoop.CaptureTelemetrySnapshot();
            _profileManager.SaveProfile(profile);
            _profileManager.SetActiveProfile(profile);
            RefreshProfiles();
            SelectedProfile = profile;
        }
    }

    private string GetNextProfileName(string baseName)
    {
        var existingNames = _profileManager.Profiles.Select(p => p.Name).ToHashSet();
        string trimmed = System.Text.RegularExpressions.Regex.Replace(baseName, @"\s*v?\d+$", "").TrimEnd();
        int version = 2;
        while (existingNames.Contains($"{trimmed} v{version}"))
            version++;
        return $"{trimmed} v{version}";
    }

    [RelayCommand]
    private void DeleteProfile()
    {
        if (SelectedProfile == null) return;
        _profileManager.DeleteProfile(SelectedProfile);
        RefreshProfiles();
        SelectedProfile = _profileManager.ActiveProfile;
    }

    [RelayCommand]
    private void RenameProfile()
    {
        if (SelectedProfile == null) return;

        var dialog = new Views.InputDialog(SelectedProfile.Name)
        {
            Owner = Application.Current?.MainWindow,
            Title = "Rename Profile"
        };

        if (dialog.ShowDialog() == true && !string.IsNullOrWhiteSpace(dialog.Result))
        {
            var oldName = SelectedProfile.Name;
            _profileManager.RenameProfile(SelectedProfile, dialog.Result!);
            RefreshProfiles();
            StatusText = $"Renamed '{oldName}' to '{dialog.Result}'";
        }
    }

    [RelayCommand]
    private void ExportProfile()
    {
        if (SelectedProfile == null) return;
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Filter = "JSON Profile|*.json",
            FileName = $"{SelectedProfile.Name}.json"
        };
        if (dialog.ShowDialog() == true)
            _profileManager.ExportProfile(SelectedProfile, dialog.FileName);
    }

    [RelayCommand]
    private void ImportProfile()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "JSON Profile|*.json"
        };
        if (dialog.ShowDialog() == true)
        {
            var profile = _profileManager.ImportProfile(dialog.FileName);
            if (profile != null)
            {
                RefreshProfiles();
                SelectedProfile = profile;
            }
        }
    }

    [RelayCommand]
    private void PanicStop()
    {
        _deviceManager.ZeroForce();
        StatusText = "PANIC STOP - FFB zeroed";
    }

    [RelayCommand]
    private void StartTrackMapRecording()
    {
        _telemetryLoop.MapBuilder.Reset();
        _telemetryLoop.PositionDetector.ClearMap();
        _telemetryLoop.MapBuilder.StartRecording();
        IsTrackMapRecording = true;
        IsTrackMapAvailable = false;
        StatusText = "Track map recording started - drive a full lap";
    }

    [RelayCommand]
    private void StopTrackMapRecording()
    {
        _telemetryLoop.MapBuilder.StopRecording();
        IsTrackMapRecording = false;
        StatusText = "Track map recording stopped";
    }

    [RelayCommand]
    private void CompleteAndSaveTrackMap()
    {
        var builder = _telemetryLoop.MapBuilder;

        bool success = builder.ForceComplete();
        IsTrackMapRecording = false;

        var map = builder.CurrentMap;
        if (map == null)
        {
            StatusText = "No track data to save - record a lap first";
            return;
        }

        map.Analyze();

        if (!_telemetryLoop.PositionDetector.HasMap)
        {
            _telemetryLoop.PositionDetector.SetMap(map);
            _telemetryLoop.ForceHeatmap.Initialize(map.Waypoints.Count);
            _telemetryLoop.LapRecorder.Initialize(map.Waypoints.Count);
            _telemetryLoop.DiagnosticHeatmap.Initialize(map.Waypoints.Count);
        }

        IsTrackMapAvailable = true;
        TrackLengthM = map.TrackLengthM;
        TrackWaypointCount = map.Waypoints.Count;
        CornerCount = map.Corners.Count;
        SectorCount = map.Sectors.Count;

        if (string.IsNullOrEmpty(map.TrackName) || map.TrackName.StartsWith("track_"))
        {
            map.TrackName = !string.IsNullOrEmpty(_telemetryLoop.DetectedTrackName)
                ? _telemetryLoop.DetectedTrackName
                : $"track_{DateTime.Now:yyyyMMdd_HHmmss}";
        }
        map.Save();
        StatusText = $"Track map saved ({map.Waypoints.Count} pts, {map.Corners.Count} corners, {map.Sectors.Count} sectors, {map.TrackLengthM:F0}m) -> {map.TrackName}.json";
    }

    [RelayCommand]
    private void ExportTrackProfile()
    {
        var map = _telemetryLoop.MapBuilder.CurrentMap;
        if (map == null)
        {
            StatusText = "No track map to export";
            return;
        }

        var heatmap = _telemetryLoop.ForceHeatmap.GetSnapshot();
        if (heatmap != null)
        {
            map.ForceHeatmap = heatmap;
            map.UpdateSectorStats(_telemetryLoop.ForceHeatmap);
        }

        var laps = _telemetryLoop.LapRecorder.CompletedLaps;
        if (laps.Count > 0)
            map.LapSnapshots = new List<LapSnapshot>(laps);

        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Filter = "Track Profile|*.json",
            FileName = $"{map.TrackName}_profile.json"
        };
        if (dialog.ShowDialog() == true)
        {
            map.ExportProfile(dialog.FileName);
            StatusText = $"Track profile exported: {dialog.FileName}";
        }
    }

    [RelayCommand]
    private void ImportTrackProfile()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "Track Profile|*.json"
        };
        if (dialog.ShowDialog() != true) return;

        var map = TrackMap.ImportProfile(dialog.FileName);
        if (map == null || map.Waypoints.Count < 10)
        {
            StatusText = "Invalid track profile file";
            return;
        }

        map.InvalidateCache();
        map.GetCumulativeDistances();
        if (map.Corners.Count == 0) map.Analyze();

        _telemetryLoop.MapBuilder.SetImportedMap(map);
        _telemetryLoop.PositionDetector.SetMap(map);
        _telemetryLoop.ForceHeatmap.Initialize(map.Waypoints.Count);
        _telemetryLoop.LapRecorder.Initialize(map.Waypoints.Count);
        _telemetryLoop.DiagnosticHeatmap.Initialize(map.Waypoints.Count);
        _telemetryLoop.RealignToMap();

        IsTrackMapAvailable = true;
        TrackLengthM = map.TrackLengthM;
        TrackWaypointCount = map.Waypoints.Count;
        CornerCount = map.Corners.Count;
        SectorCount = map.Sectors.Count;
        StatusText = $"Imported track profile: {map.Waypoints.Count} pts, {map.Corners.Count} corners, {map.TrackLengthM:F0}m";
    }

    [RelayCommand]
    private void SaveTrackMap()
    {
        CompleteAndSaveTrackMap();
    }

    [RelayCommand]
    private void ClearTrackMap()
    {
        _telemetryLoop.MapBuilder.Reset();
        _telemetryLoop.PositionDetector.ClearMap();
        _telemetryLoop.ForceHeatmap.Clear();
        _telemetryLoop.DiagnosticHeatmap.Clear();
        _telemetryLoop.LapRecorder.Clear();
        IsTrackMapAvailable = false;
        IsTrackMapRecording = false;
        TrackProgress = 0f;
        TrackDistanceFromCenter = 0f;
        TrackWaypointCount = 0;
        TrackLengthM = 0f;
        CornerCount = 0;
        SectorCount = 0;
        CompletedLapCount = 0;
        CurrentCornerName = "";
        CurrentCornerType = "";
        CurrentSectorNumber = 0;
        SectorStats = "";
        LapComparison = "";
        RecommendationsText = "";
        Recommendations.Clear();
        StatusText = "Track map cleared";
    }

    [RelayCommand]
    private void ApplyRecommendations()
    {
        var profileChanges = Recommendations
            .Where(r => r.Type == RecommendationType.ProfileChange)
            .ToList();

        if (profileChanges.Count == 0)
        {
            StatusText = "No profile changes to apply";
            return;
        }

        string profileName = SelectedProfile?.Name ?? "Unknown";
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"Applying to profile: {profileName}");
        sb.AppendLine("");

        int applied = 0;
        foreach (var rec in profileChanges)
        {
            if (ApplySingleRecommendation(rec))
            {
                applied++;
                string dir = rec.SuggestedValue > rec.CurrentValue ? "↑" : "↓";
                sb.AppendLine($"  {rec.Parameter} {dir} {rec.CurrentValue:F3} → {rec.SuggestedValue:F3}");
            }
        }

        if (applied > 0)
        {
            SaveCurrentProfile();
            sb.AppendLine("");
            sb.AppendLine($"Applied {applied} change(s) and saved to '{profileName}'");
            StatusText = $"Applied {applied} recommendation(s) to '{profileName}'";
            RecommendationsText = sb.ToString() + "\n--- Changes applied. Drive another lap to reassess. ---";
            _lastRecommendationEventCount = -1;
        }
    }

    private bool ApplySingleRecommendation(FfbRecommendation rec)
    {
        switch (rec.Parameter)
        {
            case "OutputGain": OutputGain = rec.SuggestedValue; return true;
            case "SoftClipThreshold": MaxForceLimit = rec.SuggestedValue; return true;
            case "MaxSlewRate": MaxSlewRate = rec.SuggestedValue; return true;
            case "HysteresisThreshold": HysteresisThreshold = rec.SuggestedValue; return true;
            case "CenterSuppressionDegrees": CenterSuppressionDegrees = rec.SuggestedValue; return true;
            case "CompressionPower": CompressionPower = rec.SuggestedValue; return true;
            case "MzFrontGain": MzFrontGain = rec.SuggestedValue; return true;
            case "FyFrontGain": FyFrontGain = rec.SuggestedValue; return true;
            case "CenterBlendDegrees": CenterBlendDegrees = rec.SuggestedValue; return true;
            case "SuspensionRoadGain": SuspensionRoadGain = rec.SuggestedValue; return true;
            default: return false;
        }
    }

    private int _lastRecommendationEventCount;

    private void UpdateRecommendations(DiagnosticLapSummary? summary = null)
    {
        summary ??= _telemetryLoop.DiagnosticHeatmap.LatestLapSummary;
        if (summary == null || summary.TotalEvents == 0) return;
        if (summary.TotalEvents == _lastRecommendationEventCount) return;
        _lastRecommendationEventCount = summary.TotalEvents;

        var profile = FfbProfile.CreateFromPipeline(_pipeline, "recommender_temp");
        var recs = ProfileRecommender.Generate(summary, profile);

        Recommendations.Clear();
        foreach (var r in recs)
            Recommendations.Add(r);

        if (recs.Count > 0)
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"═══ DIAGNOSTIC RECOMMENDATIONS (DEV MODE) ═══");
            sb.AppendLine($"Events: {summary.TotalEvents} | Corner: {summary.CornerEventPct:F0}% | Suspicious: {summary.SuspiciousPct:F0}% | Verdict: {summary.Verdict}");
            sb.AppendLine($"Snap causes (ALL): Mz={summary.SnapCauseMz} Fx={summary.SnapCauseFx} Fy={summary.SnapCauseFy} Slew={summary.SnapCauseSlew} | Expected: {summary.ExpectedSnapCount}");
            sb.AppendLine($"Snap causes (SUSPICIOUS only): Mz={summary.SuspiciousSnapCauseMz} Fx={summary.SuspiciousSnapCauseFx} Fy={summary.SuspiciousSnapCauseFy} Slew={summary.SuspiciousSnapCauseSlew}");
            sb.AppendLine();
            foreach (var r in recs)
                sb.AppendLine(r.DevModeText);
            RecommendationsText = sb.ToString();
        }
    }

    partial void OnSelectedMixModeChanged(FfbMixMode value) => _pipeline.ChannelMixer.MixMode = value;
    partial void OnForceSensitivityChanged(float value) => _pipeline.MasterGain = 1000f / Math.Max(value, 1f);
    partial void OnForceScaleChanged(float value) => _pipeline.ForceScale = value;
    partial void OnMzFrontGainChanged(float value) => _pipeline.ChannelMixer.MzFrontGain = value;
    partial void OnMzFrontEnabledChanged(bool value) => _pipeline.ChannelMixer.MzFrontEnabled = value;
    partial void OnFxFrontGainChanged(float value) => _pipeline.ChannelMixer.FxFrontGain = value;
    partial void OnFxFrontEnabledChanged(bool value) => _pipeline.ChannelMixer.FxFrontEnabled = value;
    partial void OnFyFrontGainChanged(float value) => _pipeline.ChannelMixer.FyFrontGain = value;
    partial void OnFyFrontEnabledChanged(bool value) => _pipeline.ChannelMixer.FyFrontEnabled = value;
    partial void OnMzRearGainChanged(float value) => _pipeline.ChannelMixer.MzRearGain = value;
    partial void OnMzRearEnabledChanged(bool value) => _pipeline.ChannelMixer.MzRearEnabled = value;
    partial void OnFxRearGainChanged(float value) => _pipeline.ChannelMixer.FxRearGain = value;
    partial void OnFxRearEnabledChanged(bool value) => _pipeline.ChannelMixer.FxRearEnabled = value;
    partial void OnFyRearGainChanged(float value) => _pipeline.ChannelMixer.FyRearGain = value;
    partial void OnFyRearEnabledChanged(bool value) => _pipeline.ChannelMixer.FyRearEnabled = value;
    partial void OnFinalFfGainChanged(float value) => _pipeline.ChannelMixer.FinalFfGain = value;
    partial void OnFinalFfEnabledChanged(bool value) => _pipeline.ChannelMixer.FinalFfEnabled = value;
    partial void OnWheelLoadWeightingChanged(float value) => _pipeline.ChannelMixer.WheelLoadWeighting = value;
    partial void OnMzScaleChanged(float value) => _pipeline.ChannelMixer.MzScale = value;
    partial void OnFxScaleChanged(float value) => _pipeline.ChannelMixer.FxScale = value;
    partial void OnFyScaleChanged(float value) => _pipeline.ChannelMixer.FyScale = value;
    partial void OnSpeedDampingChanged(float value) => _pipeline.Damping.SpeedDampingCoefficient = value;
    partial void OnFrictionLevelChanged(float value) => _pipeline.Damping.FrictionLevel = value;
    partial void OnInertiaWeightChanged(float value) => _pipeline.Damping.InertiaWeight = value;
    partial void OnDampingSpeedReferenceChanged(float value) => _pipeline.Damping.MaxSpeedReference = value;
    partial void OnLowSpeedDampingBoostChanged(float value) => _pipeline.Damping.LowSpeedDampingBoost = value;
    partial void OnLowSpeedThresholdChanged(float value) => _pipeline.Damping.LowSpeedThreshold = value;
    partial void OnSlipRatioGainChanged(float value) => _pipeline.SlipEnhancer.SlipRatioGain = value;
    partial void OnSlipAngleGainChanged(float value) => _pipeline.SlipEnhancer.SlipAngleGain = value;
    partial void OnSlipThresholdChanged(float value) => _pipeline.SlipEnhancer.SlipThreshold = value;
    partial void OnSlipUseFrontOnlyChanged(bool value) => _pipeline.SlipEnhancer.UseFrontOnly = value;
    partial void OnCorneringForceChanged(float value) => _pipeline.DynamicEffects.LateralGGain = value;
    partial void OnAccelerationBrakingForceChanged(float value) => _pipeline.DynamicEffects.LongitudinalGGain = value;
    partial void OnRoadFeelChanged(float value) => _pipeline.DynamicEffects.SuspensionGain = value;
    partial void OnCarRotationForceChanged(float value) => _pipeline.DynamicEffects.YawRateGain = value;
    partial void OnAutoGainEnabledChanged(bool value) => _pipeline.AutoGainEnabled = value;
    partial void OnAutoGainScaleChanged(float value) => _pipeline.AutoGainScale = value;
    partial void OnCurbGainChanged(float value) => _pipeline.VibrationMixer.KerbGain = value;
    partial void OnSlipGainChanged(float value) => _pipeline.VibrationMixer.SlipGain = value;
    partial void OnRoadGainChanged(float value) => _pipeline.VibrationMixer.RoadGain = value;
    partial void OnAbsGainChanged(float value) => _pipeline.VibrationMixer.AbsGain = value;
    partial void OnVibrationMasterGainChanged(float value) => _pipeline.VibrationMixer.MasterGain = value;
    partial void OnSuspensionRoadGainChanged(float value) => _pipeline.VibrationMixer.SuspensionRoadGain = value;
    partial void OnSteeringLockDegreesChanged(int value) { }
    partial void OnCompressionPowerChanged(float value) => _pipeline.CompressionPower = value;
    partial void OnSignCorrectionEnabledChanged(bool value) => _pipeline.SignCorrectionEnabled = value;
    partial void OnMaxSlewRateChanged(float value) => _pipeline.MaxSlewRate = value;
    partial void OnCenterSuppressionDegreesChanged(float value) => _pipeline.CenterSuppressionDegrees = value;
    partial void OnCenterKneePowerChanged(float value) => _pipeline.CenterKneePower = value;
    partial void OnHysteresisThresholdChanged(float value) => _pipeline.HysteresisThreshold = value;
    partial void OnNoiseFloorChanged(float value) => _pipeline.NoiseFloor = value;
    partial void OnHysteresisWatchdogFramesChanged(int value) => _pipeline.HysteresisWatchdogFrames = value;
    partial void OnCenterBlendDegreesChanged(float value) => _pipeline.ChannelMixer.CenterBlendDegrees = value;
    partial void OnSteerVelocityReferenceChanged(float value) => _pipeline.Damping.SteerVelocityReference = value;
    partial void OnVelocityDeadzoneChanged(float value) => _pipeline.Damping.VelocityDeadzone = value;
    partial void OnLowSpeedSmoothKmhChanged(float value) => _pipeline.ChannelMixer.LowSpeedSmoothKmh = value;
    partial void OnForceInvertEnabledChanged(bool value) => _deviceManager.ForceInvert = value;
    partial void OnMaxForceLimitChanged(float value)
    {
        _pipeline.OutputClipper.SoftClipThreshold = value;
        OnPropertyChanged(nameof(MaxForceLimitNmText));
        OnPropertyChanged(nameof(OutputGainNmText));
    }
    partial void OnWheelMaxTorqueNmChanged(float value)
    {
        OnPropertyChanged(nameof(MaxForceLimitNmText));
        OnPropertyChanged(nameof(OutputGainNmText));
    }
    partial void OnOutputGainChanged(float value)
    {
        _pipeline.OutputGain = value;
        OnPropertyChanged(nameof(OutputGainNmText));
    }

    public string MaxForceLimitNmText => $"{MaxForceLimit:F2} ({MaxForceLimit * WheelMaxTorqueNm:F1} Nm)";
    public string OutputGainNmText => $"{OutputGain:F2} (peak {MaxForceLimit * WheelMaxTorqueNm:F1} Nm)";

    partial void OnLedBrightnessChanged(int value) => PushLedConfig();
    partial void OnLedFlashRateChanged(int value) => PushLedConfig();
    partial void OnLedAbsFlashEnabledChanged(bool value) => PushLedConfig();
    partial void OnLedFlagIndicatorsEnabledChanged(bool value) => PushLedConfig();
    partial void OnLedShiftLimiterFlashEnabledChanged(bool value) => PushLedConfig();
    partial void OnLedColorSchemeIndexChanged(int value) => PushLedConfig();
    partial void OnLedRpmPresetIndexChanged(int value)
    {
        if (value >= 0 && value <= 3)
        {
            var preset = (LedRpmPreset)value;
            int[] thresholds = preset switch
            {
                LedRpmPreset.Default => LedEffectConfig.BuildDefaultRpmThresholds(),
                LedRpmPreset.Early => LedEffectConfig.BuildEarlyRpmThresholds(),
                LedRpmPreset.Late => LedEffectConfig.BuildLateRpmThresholds(),
                LedRpmPreset.Linear => LedEffectConfig.BuildLinearRpmThresholds(),
                _ => LedEffectConfig.BuildDefaultRpmThresholds()
            };
            LoadRpmThresholdValues(thresholds);
        }
        PushLedConfig();
    }

    partial void OnLedRpmThreshold1Changed(int value) => PushLedConfig();
    partial void OnLedRpmThreshold2Changed(int value) => PushLedConfig();
    partial void OnLedRpmThreshold3Changed(int value) => PushLedConfig();
    partial void OnLedRpmThreshold4Changed(int value) => PushLedConfig();
    partial void OnLedRpmThreshold5Changed(int value) => PushLedConfig();
    partial void OnLedRpmThreshold6Changed(int value) => PushLedConfig();
    partial void OnLedRpmThreshold7Changed(int value) => PushLedConfig();
    partial void OnLedRpmThreshold8Changed(int value) => PushLedConfig();
    partial void OnLedRpmThreshold9Changed(int value) => PushLedConfig();
    partial void OnLedRpmThreshold10Changed(int value) => PushLedConfig();

    partial void OnSelectedProfileChanged(FfbProfile? value)
    {
        if (value == null) return;
        _profileManager.SetActiveProfile(value);
        value.ApplyToPipeline(_pipeline);
        LoadProfileValues(value);
    }

    private void OnUiUpdate(object? sender, EventArgs e)
    {
        var raw = _telemetryLoop.LatestRaw;
        var processed = _telemetryLoop.LatestProcessed;

        if (processed != null && raw != null)
        {
            CurrentForceOutput = processed.MainForce;
            CurrentRawForce = processed.RawFinalFf;
            IsClipping = processed.IsClipping;
            SpeedKmh = processed.SpeedKmh;
            int rawDeg = raw.SteerDegrees;
            int lockDeg = (rawDeg > 90 && rawDeg <= 1440) ? rawDeg : SteeringLockDegrees;
            if (lockDeg <= 0) lockDeg = 900;
            float steerFloat = (float)raw.SteerAngle;
            SteerAngle = steerFloat * (lockDeg / 2f);

            DebugSnapshot =
                $"=== RAW SHARED MEMORY ===\n" +
                $"Mz:  FL={raw.Mz[0]:F4}  FR={raw.Mz[1]:F4}  RL={raw.Mz[2]:F4}  RR={raw.Mz[3]:F4}\n" +
                $"Fx:  FL={raw.Fx[0]:F4}  FR={raw.Fx[1]:F4}  RL={raw.Fx[2]:F4}  RR={raw.Fx[3]:F4}\n" +
                $"Fy:  FL={raw.Fy[0]:F4}  FR={raw.Fy[1]:F4}  RL={raw.Fy[2]:F4}  RR={raw.Fy[3]:F4}\n" +
                $"FinalFf:       {raw.FinalFf:F6}\n" +
                $"WheelLoad: FL={raw.WheelLoad[0]:F1}  FR={raw.WheelLoad[1]:F1}  RL={raw.WheelLoad[2]:F1}  RR={raw.WheelLoad[3]:F1}\n" +
                $"SteerAngle:    {raw.SteerAngle:F4}  SteerDeg={raw.SteerDegrees}  Lock={SteeringLockDegrees}°\n" +
                $"Speed:         {raw.SpeedKmh:F1} km/h\n" +
                $"AccG:          X={raw.AccG[0]:F3}  Y={raw.AccG[1]:F3}  Z={raw.AccG[2]:F3}\n" +
                $"SlipRatio:     FL={raw.SlipRatio[0]:F4}  FR={raw.SlipRatio[1]:F4}  RL={raw.SlipRatio[2]:F4}  RR={raw.SlipRatio[3]:F4}\n" +
                $"SlipAngle:     FL={raw.SlipAngle[0]:F4}  FR={raw.SlipAngle[1]:F4}  RL={raw.SlipAngle[2]:F4}  RR={raw.SlipAngle[3]:F4}\n" +
                $"FfbStrength:   {raw.FfbStrength:F4}  CarMult={raw.CarFfbMultiplier:F4}\n" +
                $"\n=== VIBRATIONS ===\n" +
                $"Kerb: {raw.KerbVibration:F4}  Slip: {raw.SlipVibrations:F4}  Road: {raw.RoadVibrations:F4}  ABS: {raw.AbsVibrations:F4}\n" +
                $"VibForce: {processed.VibrationForce:F4}  MasterGain: {_pipeline.VibrationMixer.MasterGain:F2}  AbsGain: {_pipeline.VibrationMixer.AbsGain:F2}\n" +
                $"\n=== PIPELINE STAGES ===\n" +
                $"MzFront (norm): {processed.ChannelMzFront:F6}\n" +
                $"FxFront (norm): {processed.ChannelFxFront:F6}\n" +
                $"FyFront (norm): {processed.ChannelFyFront:F6}\n" +
                $"PostCompress:    {processed.PostCompressionForce:F6}\n" +
                $"PostLUT:         {processed.PostLutForce:F6}\n" +
                $"PostSlip:       {processed.PostSlipForce:F6}\n" +
                $"PostDamping:    {processed.PostDampingForce:F6}\n" +
                $"PostDynamic:    {processed.PostDynamicForce:F6}\n" +
                $"OUTPUT:         {processed.MainForce:F6}   Clipping={processed.IsClipping}\n" +
                $"AutoGain:       {processed.AutoGainApplied:F4}\n" +
                $"\n=== FFB DEVICE ===\n" +
                $"Acquired:       {IsDeviceConnected}\n" +
                $"MasterGain:     {_pipeline.MasterGain:F2}\n" +
                $"OutputGain:     {_pipeline.OutputGain:F2}\n" +
                $"ClipThreshold:  {_pipeline.OutputClipper.SoftClipThreshold:F2}\n" +
                $"MzScale:        {_pipeline.ChannelMixer.MzScale:F0}\n" +
                $"FxScale:        {_pipeline.ChannelMixer.FxScale:F0}\n" +
                $"FyScale:        {_pipeline.ChannelMixer.FyScale:F0}\n" +
                $"LastError:      {_deviceManager.LastError ?? "none"}\n" +
                $"PeriodicFX:     {_deviceManager.SupportsPeriodicEffects}\n" +
                $"PacketId:       {raw.PacketId}  PPS: {_telemetryLoop.PacketsPerSecond}\n" +
                $"Latency:        {_telemetryLoop.LastLatencyMs:F2}ms (avg: {_telemetryLoop.AvgLatencyMs:F2}ms)\n" +
                $"\n=== LED CONTROLLER ===\n" +
                $"Connected:      {_deviceManager.IsLedControllerConnected}\n" +
                $"Vendor:         {_deviceManager.LedControllerVendor}\n" +
                $"RPM:            {raw.RpmPercent:F1}%  ShiftUp={raw.IsChangeUpRpm}  Limiter={raw.IsRpmLimiterOn}\n" +
                $"ABS:  InAction={raw.AbsInAction}  Level={raw.AbsLevel:F3}  Gfx={raw.AbsActiveGfx}  Vib={raw.AbsVibrations:F4}  Brake={raw.BrakeInput:F3}\n" +
                $"LED Config:     Brightness={LedBrightness}% FlashRate={LedFlashRate} AbsFlash={LedAbsFlashEnabled}\n" +
                $"{_deviceManager.LedDiagnosticInfo}";

            if (IsRunning && IsDeviceConnected)
            {
                if (_deviceManager.HasLostDeviceAccess)
                {
                    IsDeviceConnected = false;
                    DeviceName = "Lost exclusive access";
                    StatusText = "FFB device lost exclusive access — disconnect and reconnect the wheel.";
                }
                else
                {
                    var err = _deviceManager.LastError;
                    if (!string.IsNullOrEmpty(err))
                        StatusText = err;
                }
            }

            if (Application.Current?.MainWindow is MainWindow mw)
            {
                mw.UpdateProfiler(
                    raw.SpeedKmh, raw.SteerAngle,
                    processed.MainForce, processed.RawFinalFf,
                    processed.PostCompressionForce, processed.PostSlipForce,
                    processed.PostDampingForce, processed.PostDynamicForce,
                    processed.ChannelMzFront, processed.ChannelFxFront, processed.ChannelFyFront,
                    processed.PostLutForce, processed.IsClipping,
                    raw.GasInput, raw.BrakeInput);
            }

            PollSnapshotButton();

            CarPosX = raw.CarX;
            CarPosZ = raw.CarZ;
            CarHeading = raw.Heading;

            if (_telemetryLoop.PositionDetector.HasMap)
            {
                var pos = _telemetryLoop.PositionDetector.GetPosition(raw.CarX, raw.CarZ);
                if (pos.IsValid)
                {
                    IsOnTrackMap = pos.IsOnTrack;
                    TrackProgress = pos.TrackProgress;
                    TrackDistanceFromCenter = pos.DistanceFromCenterM;

                    if (pos.CurrentCorner != null)
                    {
                        CurrentCornerName = pos.CurrentCorner.DisplayName;
                        CurrentCornerType = pos.CurrentCorner.TypeName;
                    }
                    else
                    {
                        CurrentCornerName = "Straight";
                        CurrentCornerType = "";
                    }

                    if (pos.CurrentSector != null)
                    {
                        _sectorStatsCounter++;
                        if (_sectorStatsCounter >= 30)
                        {
                            _sectorStatsCounter = 0;
                            var map = _telemetryLoop.MapBuilder.CurrentMap;
                            if (map != null)
                                map.UpdateSectorStats(_telemetryLoop.ForceHeatmap);
                        }

                        CurrentSectorNumber = pos.CurrentSector.SectorNumber;
                        var s = pos.CurrentSector;
                        if (s.SampleCount > 0)
                        {
                            SectorStats = $"AvgF={s.AvgOutputForce:F3} PeakF={s.PeakOutputForce:F3} Clip={s.ClippingPct:F1}% AvgMz={s.AvgMzFront:F3} AvgSpd={s.AvgSpeedKmh:F0}";
                        }
                    }
                }
            }

            IsTrackMapRecording = _telemetryLoop.MapBuilder.IsRecording;
            IsTrackMapAvailable = _telemetryLoop.MapBuilder.HasCompleteMap;
            TrackWaypointCount = _telemetryLoop.MapBuilder.WaypointCount;

            var currentMap = _telemetryLoop.MapBuilder.CurrentMap;
            if (currentMap != null)
            {
                CornerCount = currentMap.Corners.Count;
                SectorCount = currentMap.Sectors.Count;
                IsPitDetected = currentMap.PitLane.IsDetected;
            }

            IsInPit = raw.IsInPitLane;
            CompletedLapCount = _telemetryLoop.LapRecorder.CompletedLaps.Count;
            var laps = _telemetryLoop.LapRecorder.CompletedLaps;
            if (laps.Count >= 2)
            {
                var last = laps[^1];
                var prev = laps[^2];
                float forceDelta = last.AvgOutputForce - prev.AvgOutputForce;
                float clipDelta = last.ClippingPct - prev.ClippingPct;
                LapComparison = $"Lap{last.LapNumber} vs Lap{prev.LapNumber}: ΔForce={forceDelta:+F3;-F3} ΔClip={clipDelta:+F1;-F1}%";
            }

            var runningSummary = _telemetryLoop.DiagnosticHeatmap.GetRunningSummary();
            var coverage = _telemetryLoop.DiagnosticHeatmap.TrackCoveragePct;
            var sufficientCoverage = _telemetryLoop.DiagnosticHeatmap.HasSufficientCoverage;

            if (runningSummary != null && runningSummary.TotalEvents > 0)
            {
                DiagnosticSummary = $"Snaps:{runningSummary.TotalSnapEvents} Osc:{runningSummary.TotalOscillations} Clip:{runningSummary.TotalClippingEvents} Anomaly:{runningSummary.TotalForceAnomalies} | Corner:{runningSummary.CornerEventPct:F0}% Suspicious:{runningSummary.SuspiciousPct:F0}%";
                DiagnosticVerdict = runningSummary.Verdict;
            }

            DiagnosticCoverage = sufficientCoverage
                ? $"Coverage: {coverage:F0}% — ready for recommendations"
                : $"Coverage: {coverage:F0}% — keep driving ({60 - (int)coverage}% more needed)";

            var completedSummary = _telemetryLoop.DiagnosticHeatmap.LatestLapSummary;
            if (sufficientCoverage)
            {
                if (completedSummary != null && completedSummary.TotalEvents > 0)
                {
                    UpdateRecommendations(completedSummary);
                }
                else if (runningSummary != null && runningSummary.TotalEvents > 0)
                {
                    UpdateRecommendations(runningSummary);
                }
            }

            var lastEvt = _telemetryLoop.EventDetector.LastEvent;
            if (lastEvt != null)
            {
                LastEventInfo = $"{lastEvt.EventType} @ {lastEvt.CornerName ?? "straight"} ({lastEvt.Classification}) ΔF={lastEvt.ForceDelta:F3}";
            }

            if (Application.Current?.MainWindow is MainWindow mw2)
            {
                mw2.UpdateTrackMapDisplay(
                    raw.CarX, raw.CarZ, raw.Heading, raw.SpeedKmh,
                    IsOnTrackMap, TrackProgress, TrackDistanceFromCenter,
                    _telemetryLoop.MapBuilder.CurrentMap?.TrackLengthM ?? 0f,
                    TrackWaypointCount, IsTrackMapRecording, IsTrackMapAvailable,
                    _telemetryLoop.MapBuilder.CurrentMap,
                    _telemetryLoop.ForceHeatmap.GetSnapshot(),
                    ShowForceHeatmap,
                    ShowTrackEdges,
                    _telemetryLoop.DiagnosticHeatmap.GetSnapshot(),
                    ShowDiagnostics);
            }
        }

        PacketsPerSecond = _telemetryLoop.PacketsPerSecond;
        IsGameConnected = _telemetryLoop.IsGameConnected;
    }

    private void LoadProfileValues(FfbProfile profile)
    {
        SelectedMixMode = profile.MixMode switch
        {
            FfbMixModeDto.Replace => FfbMixMode.Replace,
            FfbMixModeDto.Overlay => FfbMixMode.Overlay,
            _ => FfbMixMode.Replace
        };
        OutputGain = profile.OutputGain;
        ForceSensitivity = profile.NormalizationScale;
        MaxForceLimit = profile.SoftClipThreshold;
        WheelMaxTorqueNm = profile.WheelMaxTorqueNm;
        MzFrontGain = profile.MzFront.Gain;
        MzFrontEnabled = profile.MzFront.Enabled;
        FxFrontGain = profile.FxFront.Gain;
        FxFrontEnabled = profile.FxFront.Enabled;
        FyFrontGain = profile.FyFront.Gain;
        FyFrontEnabled = profile.FyFront.Enabled;
        MzRearGain = profile.MzRear.Gain;
        MzRearEnabled = profile.MzRear.Enabled;
        FxRearGain = profile.FxRear.Gain;
        FxRearEnabled = profile.FxRear.Enabled;
        FyRearGain = profile.FyRear.Gain;
        FyRearEnabled = profile.FyRear.Enabled;
        FinalFfGain = profile.FinalFf.Gain;
        FinalFfEnabled = profile.FinalFf.Enabled;
        WheelLoadWeighting = profile.WheelLoadWeighting;
        MzScale = profile.MzScale;
        FxScale = profile.FxScale;
        FyScale = profile.FyScale;
        SpeedDamping = profile.Damping.SpeedDamping;
        FrictionLevel = profile.Damping.Friction;
        InertiaWeight = profile.Damping.Inertia;
        DampingSpeedReference = profile.Damping.MaxSpeedReference;
        LowSpeedDampingBoost = profile.Damping.LowSpeedDampingBoost;
        LowSpeedThreshold = profile.Damping.LowSpeedThreshold;
        SlipRatioGain = profile.Slip.SlipRatioGain;
        SlipAngleGain = profile.Slip.SlipAngleGain;
        SlipThreshold = profile.Slip.SlipThreshold;
        SlipUseFrontOnly = profile.Slip.UseFrontOnly;
        CorneringForce = profile.Dynamic.LateralGGain;
        AccelerationBrakingForce = profile.Dynamic.LongitudinalGGain;
        RoadFeel = profile.Dynamic.SuspensionGain;
        CarRotationForce = profile.Dynamic.YawRateGain;
        AutoGainEnabled = profile.AutoGain.Enabled;
        AutoGainScale = profile.AutoGain.Scale;
        CurbGain = profile.Vibrations.KerbGain;
        SlipGain = profile.Vibrations.SlipGain;
        RoadGain = profile.Vibrations.RoadGain;
        AbsGain = profile.Vibrations.AbsGain;
        VibrationMasterGain = profile.Vibrations.MasterGain;
        SuspensionRoadGain = profile.Vibrations.SuspensionRoadGain;
        CompressionPower = profile.CompressionPower;
        SteeringLockDegrees = profile.SteeringLockDegrees;
        ForceScale = profile.ForceScale;
        SignCorrectionEnabled = profile.SignCorrectionEnabled;
        MaxSlewRate = profile.Advanced.MaxSlewRate;
        CenterSuppressionDegrees = profile.Advanced.CenterSuppressionDegrees;
        CenterKneePower = profile.Advanced.CenterKneePower;
        HysteresisThreshold = profile.Advanced.HysteresisThreshold;
        NoiseFloor = profile.Advanced.NoiseFloor;
        HysteresisWatchdogFrames = profile.Advanced.HysteresisWatchdogFrames;
        CenterBlendDegrees = profile.Advanced.CenterBlendDegrees;
        SteerVelocityReference = profile.Advanced.SteerVelocityReference;
        VelocityDeadzone = profile.Advanced.VelocityDeadzone;
        LowSpeedSmoothKmh = profile.Advanced.LowSpeedSmoothKmh;
        LoadLedValues(profile.LedEffects);
    }

    private void PushValuesToPipeline()
    {
        _pipeline.ChannelMixer.MzFrontGain = MzFrontGain;
        _pipeline.ChannelMixer.MzFrontEnabled = MzFrontEnabled;
        _pipeline.ChannelMixer.FxFrontGain = FxFrontGain;
        _pipeline.ChannelMixer.FxFrontEnabled = FxFrontEnabled;
        _pipeline.ChannelMixer.FyFrontGain = FyFrontGain;
        _pipeline.ChannelMixer.FyFrontEnabled = FyFrontEnabled;
        _pipeline.ChannelMixer.MzRearGain = MzRearGain;
        _pipeline.ChannelMixer.MzRearEnabled = MzRearEnabled;
        _pipeline.ChannelMixer.FxRearGain = FxRearGain;
        _pipeline.ChannelMixer.FxRearEnabled = FxRearEnabled;
        _pipeline.ChannelMixer.FyRearGain = FyRearGain;
        _pipeline.ChannelMixer.FyRearEnabled = FyRearEnabled;
        _pipeline.ChannelMixer.FinalFfGain = FinalFfGain;
        _pipeline.ChannelMixer.FinalFfEnabled = FinalFfEnabled;
        _pipeline.ChannelMixer.WheelLoadWeighting = WheelLoadWeighting;
        _pipeline.ChannelMixer.MzScale = MzScale;
        _pipeline.ChannelMixer.FxScale = FxScale;
        _pipeline.ChannelMixer.FyScale = FyScale;
        _pipeline.ChannelMixer.MixMode = SelectedMixMode;
        _pipeline.OutputClipper.SoftClipThreshold = MaxForceLimit;
        _pipeline.MasterGain = 1000f / Math.Max(ForceSensitivity, 1f);
        _pipeline.Damping.SpeedDampingCoefficient = SpeedDamping;
        _pipeline.Damping.FrictionLevel = FrictionLevel;
        _pipeline.Damping.InertiaWeight = InertiaWeight;
        _pipeline.Damping.MaxSpeedReference = DampingSpeedReference;
        _pipeline.Damping.LowSpeedDampingBoost = LowSpeedDampingBoost;
        _pipeline.Damping.LowSpeedThreshold = LowSpeedThreshold;
        _pipeline.SlipEnhancer.SlipRatioGain = SlipRatioGain;
        _pipeline.SlipEnhancer.SlipAngleGain = SlipAngleGain;
        _pipeline.SlipEnhancer.SlipThreshold = SlipThreshold;
        _pipeline.SlipEnhancer.UseFrontOnly = SlipUseFrontOnly;
        _pipeline.DynamicEffects.LateralGGain = CorneringForce;
        _pipeline.DynamicEffects.LongitudinalGGain = AccelerationBrakingForce;
        _pipeline.DynamicEffects.SuspensionGain = RoadFeel;
        _pipeline.DynamicEffects.YawRateGain = CarRotationForce;
        _pipeline.AutoGainEnabled = AutoGainEnabled;
        _pipeline.AutoGainScale = AutoGainScale;
        _pipeline.VibrationMixer.KerbGain = CurbGain;
        _pipeline.VibrationMixer.SlipGain = SlipGain;
        _pipeline.VibrationMixer.RoadGain = RoadGain;
        _pipeline.VibrationMixer.AbsGain = AbsGain;
        _pipeline.VibrationMixer.MasterGain = VibrationMasterGain;
        _pipeline.VibrationMixer.SuspensionRoadGain = SuspensionRoadGain;
        _pipeline.CompressionPower = CompressionPower;
        _pipeline.ForceScale = ForceScale;
        _pipeline.SignCorrectionEnabled = SignCorrectionEnabled;
        _pipeline.MaxSlewRate = MaxSlewRate;
        _pipeline.CenterSuppressionDegrees = CenterSuppressionDegrees;
        _pipeline.CenterKneePower = CenterKneePower;
        _pipeline.HysteresisThreshold = HysteresisThreshold;
        _pipeline.NoiseFloor = NoiseFloor;
        _pipeline.HysteresisWatchdogFrames = HysteresisWatchdogFrames;
        _pipeline.ChannelMixer.CenterBlendDegrees = CenterBlendDegrees;
        _pipeline.Damping.SteerVelocityReference = SteerVelocityReference;
        _pipeline.Damping.VelocityDeadzone = VelocityDeadzone;
        _pipeline.ChannelMixer.LowSpeedSmoothKmh = LowSpeedSmoothKmh;
        PushLedConfig();
    }

    private void PushLedConfig()
    {
        if (!_deviceManager.IsDeviceAcquired) return;

        var rpmPreset = LedRpmPresetIndex >= 0 && LedRpmPresetIndex <= 4
            ? (LedRpmPreset)LedRpmPresetIndex
            : LedRpmPreset.Default;

        var colorScheme = LedColorSchemeIndex >= 0 && LedColorSchemeIndex <= 4
            ? (LedColorScheme)LedColorSchemeIndex
            : LedColorScheme.TrafficLight;

        var config = new LedEffectConfig
        {
            Brightness = LedBrightness,
            FlashRateTicks = LedFlashRate,
            AbsFlashEnabled = LedAbsFlashEnabled,
            FlagIndicatorsEnabled = LedFlagIndicatorsEnabled,
            ShiftLimiterFlashEnabled = LedShiftLimiterFlashEnabled,
            ColorScheme = colorScheme,
            RpmPreset = rpmPreset,
            RpmThresholds = new[]
            {
                LedRpmThreshold1, LedRpmThreshold2, LedRpmThreshold3, LedRpmThreshold4, LedRpmThreshold5,
                LedRpmThreshold6, LedRpmThreshold7, LedRpmThreshold8, LedRpmThreshold9, LedRpmThreshold10
            }
        };

        _deviceManager.LedConfig = config;
        StatusText = $"LED config applied: brightness={config.Brightness}% flash={config.FlashRateTicks} abs={config.AbsFlashEnabled}";
    }

    private void LoadLedValues(LedEffectConfigDto dto)
    {
        LedBrightness = dto.Brightness;
        LedFlashRate = dto.FlashRateTicks;
        LedAbsFlashEnabled = dto.AbsFlashEnabled;
        LedFlagIndicatorsEnabled = dto.FlagIndicatorsEnabled;
        LedShiftLimiterFlashEnabled = dto.ShiftLimiterFlashEnabled;
        LedColorSchemeIndex = (int)dto.ColorScheme;
        LedRpmPresetIndex = (int)dto.RpmPreset;
        LoadRpmThresholdValues(dto.RpmThresholds);
    }

    private void LoadRpmThresholdValues(int[] thresholds)
    {
        if (thresholds.Length > 0) LedRpmThreshold1 = thresholds[0];
        if (thresholds.Length > 1) LedRpmThreshold2 = thresholds[1];
        if (thresholds.Length > 2) LedRpmThreshold3 = thresholds[2];
        if (thresholds.Length > 3) LedRpmThreshold4 = thresholds[3];
        if (thresholds.Length > 4) LedRpmThreshold5 = thresholds[4];
        if (thresholds.Length > 5) LedRpmThreshold6 = thresholds[5];
        if (thresholds.Length > 6) LedRpmThreshold7 = thresholds[6];
        if (thresholds.Length > 7) LedRpmThreshold8 = thresholds[7];
        if (thresholds.Length > 8) LedRpmThreshold9 = thresholds[8];
        if (thresholds.Length > 9) LedRpmThreshold10 = thresholds[9];
    }

    private int[] GetCurrentRpmThresholds() => new[]
    {
        LedRpmThreshold1, LedRpmThreshold2, LedRpmThreshold3, LedRpmThreshold4, LedRpmThreshold5,
        LedRpmThreshold6, LedRpmThreshold7, LedRpmThreshold8, LedRpmThreshold9, LedRpmThreshold10
    };

    private void RefreshProfiles()
    {
        Profiles.Clear();
        foreach (var p in _profileManager.Profiles)
            Profiles.Add(p);
    }

    [ObservableProperty]
    private bool _isSendingDiagnosticPack;

    private readonly Services.AppSettings _appSettings = Services.AppSettings.Load();

    [ObservableProperty]
    private bool _splashScreenEnabled = true;

    [ObservableProperty]
    private string? _customStartupSoundPath;

    [ObservableProperty]
    private string? _selectedRecordingDeviceId;

    [ObservableProperty]
    private ObservableCollection<string> _audioOutputDevices = new();

    [ObservableProperty]
    private string _selectedAudioOutputDevice = "";

    [ObservableProperty]
    private bool _isRecording;

    [ObservableProperty]
    private string _recordingStatus = "";

    private NAudio.Wave.WasapiLoopbackCapture? _loopbackCapture;
    private NAudio.Wave.WaveFileWriter? _waveWriter;
    private string? _recordingTempPath;
    private string? _selectedOutputDeviceId;
    private readonly Dictionary<string, string> _deviceNameToId = new();

    partial void OnSplashScreenEnabledChanged(bool value)
    {
        _appSettings.SplashScreenEnabled = value;
        _appSettings.Save();
    }

    partial void OnCustomStartupSoundPathChanged(string? value)
    {
        _appSettings.CustomStartupSoundPath = value;
        _appSettings.Save();
    }

    partial void OnSelectedRecordingDeviceIdChanged(string? value)
    {
        _appSettings.LastRecordingDeviceId = value;
        _appSettings.Save();
    }

    public void LoadAppSettings()
    {
        SplashScreenEnabled = _appSettings.SplashScreenEnabled;
        CustomStartupSoundPath = _appSettings.CustomStartupSoundPath;
        RefreshRecordingDevices();
    }

    [RelayCommand]
    private void RefreshRecordingDevices()
    {
        AudioOutputDevices.Clear();
        _deviceNameToId.Clear();
        var enumerator = new NAudio.CoreAudioApi.MMDeviceEnumerator();
        var devices = enumerator.EnumerateAudioEndPoints(
            NAudio.CoreAudioApi.DataFlow.Render, NAudio.CoreAudioApi.DeviceState.Active);

        var savedId = _appSettings.LastRecordingDeviceId;

        foreach (var device in devices)
        {
            var name = device.FriendlyName;
            AudioOutputDevices.Add(name);
            _deviceNameToId[name] = device.ID;
            if (device.ID == savedId)
                SelectedAudioOutputDevice = name;
        }

        if (string.IsNullOrEmpty(SelectedAudioOutputDevice) && AudioOutputDevices.Count > 0)
            SelectedAudioOutputDevice = AudioOutputDevices[0];
    }

    private string? GetSelectedOutputDeviceId()
    {
        if (string.IsNullOrEmpty(SelectedAudioOutputDevice)) return null;
        return _deviceNameToId.TryGetValue(SelectedAudioOutputDevice, out var id) ? id : null;
    }

    [RelayCommand]
#pragma warning disable CS1998
    private async Task StartRecording()
#pragma warning restore CS1998
    {
        var deviceId = GetSelectedOutputDeviceId();
        if (deviceId == null)
        {
            RecordingStatus = "No output device selected.";
            return;
        }

        NAudio.CoreAudioApi.MMDevice? device = null;
        try
        {
            var enumerator = new NAudio.CoreAudioApi.MMDeviceEnumerator();
            device = enumerator.GetDevice(deviceId);
        }
        catch (Exception ex)
        {
            RecordingStatus = $"Could not open device: {ex.Message}";
            return;
        }

        if (device == null)
        {
            RecordingStatus = "Device not found.";
            return;
        }

        try
        {
            var soundsDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "AcEvoFfbTuner", "Sounds");
            Directory.CreateDirectory(soundsDir);
            _recordingTempPath = Path.Combine(soundsDir, $"recording_{DateTime.Now:yyyyMMdd_HHmmss}.wav");

            _selectedOutputDeviceId = deviceId;
            _loopbackCapture = new NAudio.Wave.WasapiLoopbackCapture(device);

            RecordingStatus = $"Capturing from: {device.FriendlyName} ({_loopbackCapture.WaveFormat})";

            _waveWriter = new NAudio.Wave.WaveFileWriter(
                _recordingTempPath, _loopbackCapture.WaveFormat);

            _loopbackCapture.DataAvailable += (s, e) =>
            {
                if (_waveWriter == null) return;
                _waveWriter.Write(e.Buffer, 0, e.BytesRecorded);
                var duration = (double)_waveWriter.Position / _waveWriter.WaveFormat.AverageBytesPerSecond;
                Application.Current?.Dispatcher.BeginInvoke(() =>
                    RecordingStatus = $"Recording... {duration:F1}s");
            };

            _loopbackCapture.RecordingStopped += (s, e) =>
            {
                _waveWriter?.Flush();
                _waveWriter?.Dispose();
                _waveWriter = null;

                _loopbackCapture?.Dispose();
                _loopbackCapture = null;

                if (_recordingTempPath != null && File.Exists(_recordingTempPath))
                {
                    var rawPath = _recordingTempPath;
                    _recordingTempPath = null;
                    var finalPath = rawPath.Replace(".wav", "_final.wav");

                    try
                    {
                        ConvertToHighQualityWav(rawPath, finalPath);
                        File.Delete(rawPath);
                        rawPath = finalPath;
                    }
                    catch
                    {
                        try { File.Delete(finalPath); } catch { }
                    }

                    var savedPath = rawPath;
                    Application.Current?.Dispatcher.BeginInvoke(() =>
                    {
                        CustomStartupSoundPath = savedPath;
                        RecordingStatus = $"Saved: {Path.GetFileName(savedPath)}";
                        IsRecording = false;
                    });
                }
                else
                {
                    _recordingTempPath = null;
                    Application.Current?.Dispatcher.BeginInvoke(() =>
                    {
                        RecordingStatus = "Recording saved.";
                        IsRecording = false;
                    });
                }
            };

            _loopbackCapture.StartRecording();
            IsRecording = true;
            RecordingStatus = $"Recording from: {device.FriendlyName} — play audio now!";
        }
        catch (Exception ex)
        {
            RecordingStatus = $"Error: {ex.Message}";
            CleanupRecording();
        }
    }

    [RelayCommand]
#pragma warning disable CS1998
    private async Task StopRecording()
#pragma warning restore CS1998
    {
        try
        {
            _loopbackCapture?.StopRecording();
        }
        catch (Exception ex)
        {
            RecordingStatus = $"Error stopping: {ex.Message}";
            IsRecording = false;
        }
    }

    [RelayCommand]
    private void PreviewStartupSound()
    {
        if (string.IsNullOrEmpty(CustomStartupSoundPath) || !File.Exists(CustomStartupSoundPath))
        {
            RecordingStatus = "No sound file to preview.";
            return;
        }

        try
        {
            var player = new System.Windows.Media.MediaPlayer();
            player.Open(new Uri(CustomStartupSoundPath));
            player.MediaOpened += (s, e) => player.Play();
            player.MediaFailed += (s, e) => RecordingStatus = "Failed to play sound.";
            RecordingStatus = "Playing preview...";
        }
        catch (Exception ex)
        {
            RecordingStatus = $"Preview error: {ex.Message}";
        }
    }

    [RelayCommand]
    private void ClearStartupSound()
    {
        CustomStartupSoundPath = null;
        RecordingStatus = "Custom sound cleared. Using default.";
    }

    [RelayCommand]
    private void BrowseStartupSound()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "Audio Files|*.mp3;*.wav;*.wma;*.ogg|All Files|*.*",
            Title = "Select Startup Sound"
        };

        if (dialog.ShowDialog() == true)
        {
            var soundsDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "AcEvoFfbTuner", "Sounds");
            Directory.CreateDirectory(soundsDir);
            var destPath = Path.Combine(soundsDir, Path.GetFileName(dialog.FileName));
            File.Copy(dialog.FileName, destPath, true);
            CustomStartupSoundPath = destPath;
            RecordingStatus = $"Sound set: {Path.GetFileName(destPath)}";
        }
    }

    private static void ConvertToHighQualityWav(string sourcePath, string destPath)
    {
        using var reader = new NAudio.Wave.WaveFileReader(sourcePath);
        var targetFormat = new NAudio.Wave.WaveFormat(48000, 24, reader.WaveFormat.Channels);

        using var writer = new NAudio.Wave.WaveFileWriter(destPath, targetFormat);
        using var resampler = new NAudio.Wave.MediaFoundationResampler(reader, targetFormat);
        resampler.ResamplerQuality = 60;

        var buffer = new byte[resampler.WaveFormat.AverageBytesPerSecond];
        int bytesRead;
        while ((bytesRead = resampler.Read(buffer, 0, buffer.Length)) > 0)
        {
            writer.Write(buffer, 0, bytesRead);
        }
        writer.Flush();
    }

    private void CleanupRecording()
    {
        _loopbackCapture?.Dispose();
        _loopbackCapture = null;
        _waveWriter?.Dispose();
        _waveWriter = null;
        IsRecording = false;
        _selectedOutputDeviceId = null;
        if (_recordingTempPath != null)
        {
            try { if (File.Exists(_recordingTempPath)) File.Delete(_recordingTempPath); } catch { }
            _recordingTempPath = null;
        }
    }

    [RelayCommand]
    private async Task SendDiagnosticPack()
    {
        var mainWin = Application.Current?.MainWindow;
        if (mainWin == null) return;

        var dialog = new Views.FeedbackDialog { Owner = mainWin };
        if (dialog.ShowDialog() != true) return;

        IsSendingDiagnosticPack = true;
        StatusText = "Auto-saving profile and snapshot...";

        try
        {
            AutoSaveDiagnosticProfile();
            (mainWin as Views.MainWindow)?.AutoSaveSnapshot();

            StatusText = "Sending diagnostic pack...";
            var progress = new Progress<string>(msg => StatusText = msg);
            var (success, message) = await DiagnosticPackService.SendAsync(dialog.Feedback, progress);
            StatusText = message;
        }
        finally
        {
            IsSendingDiagnosticPack = false;
        }
    }

    public void Dispose()
    {
        _uiUpdateTimer.Stop();
        _telemetryLoop.Dispose();
        _deviceManager.Dispose();
        _reader.Dispose();
    }
}
