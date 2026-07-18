using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Media;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using AcEvoFfbTuner.Core;
using AcEvoFfbTuner.Core.DirectInput;
using AcEvoFfbTuner.Core.FfbProcessing;
using AcEvoFfbTuner.Core.FfbProcessing.Models;
using AcEvoFfbTuner.Core.FfbProviders;
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
    private ISharedMemoryReader _reader;
    private FfbPipeline _pipeline;
    private readonly FfbDeviceManager _deviceManager;
    private TelemetryLoop _telemetryLoop;
    private readonly ProfileManager _profileManager;
    private readonly Services.GameRecordingService _gameRecordingService = new();
    private readonly Services.VoiceService _voiceService = new();
    public Services.VoiceService VoiceService => _voiceService;
    private volatile bool _mapClearedByUser;
    private string? _lastAutoLoadedTrack;
    private RaceInfoOverlay? _raceInfoOverlay;
    private readonly RaceInfoProcessor _raceInfoProcessor = new();
    private readonly DiscordPresenceService _discordPresence = new();
    private readonly GameDetectorService _gameDetector = new();
    internal bool _gameDetectorManualOverride;
    private FfbCoachService _coachService;

    private float _profilerMinOut = float.MaxValue;
    private float _profilerMaxOut = float.MinValue;
    private float _profilerSumOut;
    private int _profilerFrames;
    private int _profilerClips;
    private float _profilerPeakMz;
    private float _profilerPeakFx;
    private float _profilerPeakFy;
    private const int ProfilerStatsWindow = 300;
    private bool _isLiveMonitoring;
    private List<ProfilerSample> _profilerSamples = [];
    private const int MinMonitorSamples = 4;

    private readonly record struct ProfilerSample(
        float OutputMin, float OutputMax, float OutputAvg,
        float ClippingPercent, float PeakMz, float PeakFx, float PeakFy,
        int FrameCount);

    public string AppVersion { get; } =
        System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0.0";

    [ObservableProperty]
    private string _statusText = "Ready";

    [ObservableProperty]
    private NavPage _currentPage = NavPage.Home;

    [ObservableProperty]
    private bool _isGameConnected;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(TestHapticsCommand))]
    [NotifyCanExecuteChangedFor(nameof(TestBuzzCommand))]
    private bool _isDeviceConnected;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(TestBuzzCommand))]
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
    private bool _isScreenRecording;

    [ObservableProperty]
    private string _screenRecordingPath = "";

    [ObservableProperty]
    private string _ffmpegStatusText = "";

    [ObservableProperty]
    private bool _isFfmpegDownloading;

    [ObservableProperty]
    private bool _isFfmpegReady;

    [ObservableProperty]
    private string _updateStatusText = "";

    [ObservableProperty]
    private bool _isUpdateAvailable;

    [ObservableProperty]
    private bool _showGameFfbWarning;

    [ObservableProperty]
    private bool _showConflictingAppsWarning;

    [ObservableProperty]
    private string _conflictingAppsNames = "";

    [ObservableProperty]
    private string _conflictingAppsDetail = "";

    [ObservableProperty]
    private string _latestVersionText = "";

    [ObservableProperty]
    private bool _isDownloadingUpdate;

    [ObservableProperty]
    private int _downloadProgressPercent;

    private UpdateInfo? _pendingUpdate;

    private bool? _ffbWarningDismissed;

    [ObservableProperty]
    private float _speedKmh;

    [ObservableProperty]
    private float _steerAngle;

    [ObservableProperty]
    private float _latG;

    [ObservableProperty]
    private float _longG;

    [ObservableProperty]
    private string _debugSnapshot = "Waiting for telemetry data...";

    [ObservableProperty]
    private FfbMixMode _selectedMixMode = FfbMixMode.Replace;

    [ObservableProperty]
    private float _outputGain = 1.0f;

    [ObservableProperty]
    private float _forceSensitivity = 20f;

    [ObservableProperty]
    private float _forceScale = 1.0f;

    [ObservableProperty]
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
    private float _slipAngleShapeGain;

    [ObservableProperty]
    private float _slipThreshold = 0.05f;

    [ObservableProperty]
    private bool _slipUseFrontOnly = true;

    [ObservableProperty]
    private bool _gearChangeMuteEnabled = true;

    [ObservableProperty]
    private float _corneringForce;

    [ObservableProperty]
    private float _accelerationBrakingForce;

    [ObservableProperty]
    private float _roadFeel;

    [ObservableProperty]
    private float _carRotationForce;

    [ObservableProperty]
    private float _tyreFlexGain;

    [ObservableProperty]
    private float _carcassStiffness = 1.0f;

    [ObservableProperty]
    private float _flexSmoothing = 0.70f;

    [ObservableProperty]
    private float _contactPatchWeight = 0.5f;

    [ObservableProperty]
    private float _loadFlexGain = 0.3f;

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
    private float _absPulseAmplitude = 0.25f;

    [ObservableProperty]
    private float _vibrationMasterGain = 0.7f;

    [ObservableProperty]
    private float _suspensionRoadGain = 1.5f;

    [ObservableProperty]
    private float _scrubGain = 0.50f;

    [ObservableProperty]
    private float _rearSlipGain = 0.60f;

    [ObservableProperty]
    private float _gripScaleGain = 0.6f;

    [ObservableProperty]
    private float _tyreTempGain = 0.0f;

    [ObservableProperty]
    private float _offtrackGain = 0.5f;

    [ObservableProperty]
    private float _offtrackSeverityScale = 3.0f;

    [ObservableProperty]
    private bool _lfeEnabled;

    [ObservableProperty]
    private float _lfeGain = 0.5f;

    [ObservableProperty]
    private float _lfeFrequency = 10.0f;

    [ObservableProperty]
    private float _lfeSuspensionDrive = 0.6f;

    [ObservableProperty]
    private float _lfeSpeedScaling = 0.5f;

    [ObservableProperty]
    private float _lfeRpmDrive = 0.3f;

    [ObservableProperty]
    private bool _eqEnabled;

    [ObservableProperty]
    private float _eqBand0Gain;

    [ObservableProperty]
    private float _eqBand1Gain;

    [ObservableProperty]
    private float _eqBand2Gain;

    [ObservableProperty]
    private float _eqBand3Gain;

    [ObservableProperty]
    private float _eqBand4Gain;

    [ObservableProperty]
    private float _eqBand5Gain;

    [ObservableProperty]
    private float _eqBand6Gain;

    [ObservableProperty]
    private float _eqBand7Gain;

    [ObservableProperty]
    private float _eqBand8Gain;

    [ObservableProperty]
    private float _eqBand9Gain;

    [ObservableProperty]
    private float _maxForceLimit = 0.8f;

    [ObservableProperty]
    private float _wheelMaxTorqueNm = 5.5f;

    [ObservableProperty]
    private bool _isAutoSetupAvailable;


    [ObservableProperty]
    private string _autoSetupStatus = "";

    [ObservableProperty]
    private float _compressionPower = 1.0f;

    [ObservableProperty]
    private bool _signCorrectionEnabled = true;

    [ObservableProperty]
    private bool _fyInverted = true;

    [ObservableProperty]
    private float _maxSlewRate = 0.40f;

    [ObservableProperty]
    private float _centerSuppressionDegrees = 1.5f;

    [ObservableProperty]
    private float _centerKneePower = 1.0f;

    [ObservableProperty]
    private float _hysteresisThreshold = 0f;

    [ObservableProperty]
    private float _noiseFloor = 0.003f;

    [ObservableProperty]
    private int _hysteresisWatchdogFrames = 0;

    [ObservableProperty]
    private float _centerBlendDegrees = 1.0f;

    [ObservableProperty]
    private float _centerSharpnessDegrees = 3.0f;

    [ObservableProperty]
    private float _coreForceMultiplier = 1.0f;

    [ObservableProperty]
    private float _tyreGripScale = 1.0f;

    [ObservableProperty]
    private float _flatspotGain = 1.0f;

    [ObservableProperty]
    private float _surfaceFeelGain = 1.0f;

    [ObservableProperty]
    private float _engineTorqueLfeMod = 1.0f;

    [ObservableProperty]
    private float _brakePressureGain = 1.0f;

    [ObservableProperty]
    private float _tcFeelGain = 1.0f;

    [ObservableProperty]
    private float _coreSmoothing = 0.0f;

    [ObservableProperty]
    private float _detailSmoothing = 0.0f;

    [ObservableProperty]
    private float _brakeBoostGain = 0.15f;

    [ObservableProperty]
    private float _brakeBoostThreshold = 0.1f;

    [ObservableProperty]
    private float _forceGain = 2.5f;

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
    private string _profileCarMatch = "";

    [ObservableProperty]
    private string _profileTrackMatch = "";

    [ObservableProperty]
    private int _profileOrganisationMode = 0;

    [ObservableProperty]
    private FfbProfile? _selectedProfile;

    [ObservableProperty]
    private FfbProfile _builtInDefaults = new();


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
    private bool _ledPitLimiterFlashEnabled = true;

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
    private bool _ledTabVisible;

    [ObservableProperty]
    private bool _ledBrightnessVisible;

    [ObservableProperty]
    private bool _ledColorSchemeVisible;

    [ObservableProperty]
    private bool _ledFlagIndicatorsVisible;

    [ObservableProperty]
    private int _ledVisibleCount;

    [ObservableProperty]
    private int _activeLedCount;

    [ObservableProperty]
    private string _ledVendorName = string.Empty;

    [ObservableProperty]
    private string _ledSupportedInfo = "Connect a wheel to configure LED effects";

    [ObservableProperty]
    private bool _ledRpmThreshold1Visible;
    [ObservableProperty]
    private bool _ledRpmThreshold2Visible;
    [ObservableProperty]
    private bool _ledRpmThreshold3Visible;
    [ObservableProperty]
    private bool _ledRpmThreshold4Visible;
    [ObservableProperty]
    private bool _ledRpmThreshold5Visible;
    [ObservableProperty]
    private bool _ledRpmThreshold6Visible;
    [ObservableProperty]
    private bool _ledRpmThreshold7Visible;
    [ObservableProperty]
    private bool _ledRpmThreshold8Visible;
    [ObservableProperty]
    private bool _ledRpmThreshold9Visible;
    [ObservableProperty]
    private bool _ledRpmThreshold10Visible;

    [ObservableProperty]
    private bool _hf8Enabled;

    [ObservableProperty]
    private float _hf8MasterGain = 0.7f;

    [ObservableProperty]
    private int _hf8OutputRateHz = 75;

    [ObservableProperty]
    private float _hf8ZoneGain0 = 0.8f;
    [ObservableProperty]
    private float _hf8ZoneGain1 = 0.8f;
    [ObservableProperty]
    private float _hf8ZoneGain2 = 0.8f;
    [ObservableProperty]
    private float _hf8ZoneGain3 = 0.8f;
    [ObservableProperty]
    private float _hf8ZoneGain4 = 0.6f;
    [ObservableProperty]
    private float _hf8ZoneGain5 = 0.6f;
    [ObservableProperty]
    private float _hf8ZoneGain6 = 0.5f;
    [ObservableProperty]
    private float _hf8ZoneGain7 = 0.7f;

    [ObservableProperty]
    private bool _hf8ZoneEnabled0 = true;
    [ObservableProperty]
    private bool _hf8ZoneEnabled1 = true;
    [ObservableProperty]
    private bool _hf8ZoneEnabled2 = true;
    [ObservableProperty]
    private bool _hf8ZoneEnabled3 = true;
    [ObservableProperty]
    private bool _hf8ZoneEnabled4 = true;
    [ObservableProperty]
    private bool _hf8ZoneEnabled5 = true;
    [ObservableProperty]
    private bool _hf8ZoneEnabled6 = true;
    [ObservableProperty]
    private bool _hf8ZoneEnabled7 = true;

    [ObservableProperty]
    private string _hf8ConnectionStatus = "No HF8 device detected";

    [ObservableProperty]
    private float _hf8SrcSusp0 = 1.2f;
    [ObservableProperty]
    private float _hf8SrcSlip0 = 1.0f;
    [ObservableProperty]
    private float _hf8SrcKerb0 = 0.3f;
    [ObservableProperty]
    private float _hf8SrcLatG0 = 0.3f;
    [ObservableProperty]
    private float _hf8SrcEngine0 = 0.0f;

    [ObservableProperty]
    private float _hf8SrcSusp1 = 1.2f;
    [ObservableProperty]
    private float _hf8SrcSlip1 = 1.0f;
    [ObservableProperty]
    private float _hf8SrcKerb1 = 0.3f;
    [ObservableProperty]
    private float _hf8SrcLatG1 = 0.3f;
    [ObservableProperty]
    private float _hf8SrcEngine1 = 0.0f;

    [ObservableProperty]
    private float _hf8SrcSusp2 = 1.2f;
    [ObservableProperty]
    private float _hf8SrcSlip2 = 1.0f;
    [ObservableProperty]
    private float _hf8SrcKerb2 = 0.3f;
    [ObservableProperty]
    private float _hf8SrcLatG2 = 0.3f;
    [ObservableProperty]
    private float _hf8SrcEngine2 = 0.0f;

    [ObservableProperty]
    private float _hf8SrcSusp3 = 1.2f;
    [ObservableProperty]
    private float _hf8SrcSlip3 = 1.0f;
    [ObservableProperty]
    private float _hf8SrcKerb3 = 0.3f;
    [ObservableProperty]
    private float _hf8SrcLatG3 = 0.3f;
    [ObservableProperty]
    private float _hf8SrcEngine3 = 0.0f;

    [ObservableProperty]
    private float _hf8SrcSusp4 = 0.0f;
    [ObservableProperty]
    private float _hf8SrcSlip4 = 1.0f;
    [ObservableProperty]
    private float _hf8SrcKerb4 = 1.0f;
    [ObservableProperty]
    private float _hf8SrcLatG4 = 0.0f;
    [ObservableProperty]
    private float _hf8SrcEngine4 = 2.0f;

    [ObservableProperty]
    private float _hf8SrcSusp5 = 0.0f;
    [ObservableProperty]
    private float _hf8SrcSlip5 = 1.0f;
    [ObservableProperty]
    private float _hf8SrcKerb5 = 1.0f;
    [ObservableProperty]
    private float _hf8SrcLatG5 = 0.0f;
    [ObservableProperty]
    private float _hf8SrcEngine5 = 2.0f;

    [ObservableProperty]
    private float _hf8SrcSusp6 = 0.0f;
    [ObservableProperty]
    private float _hf8SrcSlip6 = 1.0f;
    [ObservableProperty]
    private float _hf8SrcKerb6 = 0.0f;
    [ObservableProperty]
    private float _hf8SrcLatG6 = 1.0f;
    [ObservableProperty]
    private float _hf8SrcEngine6 = 1.0f;

    [ObservableProperty]
    private float _hf8SrcSusp7 = 0.0f;
    [ObservableProperty]
    private float _hf8SrcSlip7 = 1.0f;
    [ObservableProperty]
    private float _hf8SrcKerb7 = 0.0f;
    [ObservableProperty]
    private float _hf8SrcLatG7 = 1.0f;
    [ObservableProperty]
    private float _hf8SrcEngine7 = 1.0f;

    [ObservableProperty]
    private bool _hf8Connected;

    [ObservableProperty]
    private int _hf8CopySourceIndex = -1;

    public bool Hf8IsCopySource0 => Hf8CopySourceIndex == 0;
    public bool Hf8IsCopySource1 => Hf8CopySourceIndex == 1;
    public bool Hf8IsCopySource2 => Hf8CopySourceIndex == 2;
    public bool Hf8IsCopySource3 => Hf8CopySourceIndex == 3;
    public bool Hf8IsCopySource4 => Hf8CopySourceIndex == 4;
    public bool Hf8IsCopySource5 => Hf8CopySourceIndex == 5;
    public bool Hf8IsCopySource6 => Hf8CopySourceIndex == 6;
    public bool Hf8IsCopySource7 => Hf8CopySourceIndex == 7;

    public bool Hf8CanCopyTo0 => Hf8CopySourceIndex >= 0 && Hf8CopySourceIndex != 0;
    public bool Hf8CanCopyTo1 => Hf8CopySourceIndex >= 0 && Hf8CopySourceIndex != 1;
    public bool Hf8CanCopyTo2 => Hf8CopySourceIndex >= 0 && Hf8CopySourceIndex != 2;
    public bool Hf8CanCopyTo3 => Hf8CopySourceIndex >= 0 && Hf8CopySourceIndex != 3;
    public bool Hf8CanCopyTo4 => Hf8CopySourceIndex >= 0 && Hf8CopySourceIndex != 4;
    public bool Hf8CanCopyTo5 => Hf8CopySourceIndex >= 0 && Hf8CopySourceIndex != 5;
    public bool Hf8CanCopyTo6 => Hf8CopySourceIndex >= 0 && Hf8CopySourceIndex != 6;
    public bool Hf8CanCopyTo7 => Hf8CopySourceIndex >= 0 && Hf8CopySourceIndex != 7;

    public bool Hf8CopyActive => Hf8CopySourceIndex >= 0;

    [ObservableProperty]
    private bool _gripGuardEnabled = true;

    [ObservableProperty]
    private float _gripGuardPeakSlipAngle = 0.10f;

    [ObservableProperty]
    private float _gripGuardAttenuationStrength = 1.0f;

    [ObservableProperty]
    private float _gripGuardMechanicalTrailGain = 0.015f;

    [ObservableProperty]
    private float _gripGuardMinSpeedKmh = 10.0f;

    [ObservableProperty]
    private bool _crashEnabled = true;

    [ObservableProperty]
    private float _crashImpactGain = 0.60f;

    [ObservableProperty]
    private float _crashSafetyClamp = 0.50f;

    [ObservableProperty]
    private float _crashDecayRate = 0.88f;

    [ObservableProperty]
    private float _crashTriggerThresholdG = 3.0f;

    [ObservableProperty]
    private float _crashMinSpeedKmh = 5.0f;

    [ObservableProperty]
    private bool _crashSafetyOverride;

    [ObservableProperty]
    private bool _showCrashSafetyWarning;

    [ObservableProperty]
    private bool _tyreConditionEnabled = true;

    [ObservableProperty]
    private float _tyreConditionBlowoutGain = 0.40f;

    [ObservableProperty]
    private float _tyreConditionPressureLossGain = 0.20f;

    [ObservableProperty]
    private float _tyreConditionDamageAsymmetryGain = 0.15f;

    [ObservableProperty]
    private float _tyreConditionBlowoutThreshold = 0.40f;

    [ObservableProperty]
    private float _tyreConditionMaxBlowoutAmplitude = 0.25f;

    // ── Wet Weather ──────────────────────────────────────────────────
    [ObservableProperty]
    private bool _wetWeatherEnabled;

    [ObservableProperty]
    private bool _wetWeatherAutoDetect = true;

    [ObservableProperty]
    private float _wetWeatherManualIntensity = 1.0f;

    [ObservableProperty]
    private float _wetWeatherRoadVibSuppression = 0.70f;

    [ObservableProperty]
    private float _wetWeatherCurbSuppression = 0.40f;

    [ObservableProperty]
    private float _wetWeatherScrubSuppression = 0.25f;

    [ObservableProperty]
    private float _wetWeatherPeakSlipAngleMultiplier = 1.60f;

    [ObservableProperty]
    private float _wetWeatherDampingReduction = 0.30f;

    [ObservableProperty]
    private float _wetWeatherNoiseFloorSuppression = 0.50f;

    [ObservableProperty]
    private bool _wetWeatherHydroplaningEnabled = true;

    [ObservableProperty]
    private float _wetWeatherHydroplaningSpeedThreshold = 120f;

    [ObservableProperty]
    private float _wetWeatherHydroplaningMaxAttenuation = 0.30f;

    [ObservableProperty]
    private float _wetWeatherCurrentFactor;

    // ── Tyre Compound ─────────────────────────────────────────────────
    [ObservableProperty]
    private string _currentTyreCompoundFront = "";

    [ObservableProperty]
    private string _currentTyreCompoundRear = "";

    [ObservableProperty]
    private string _currentTyreCategoryName = "Unknown";

    // ── Stationary Friction (engine-off wheel scrub) ──────────────────
    [ObservableProperty]
    private float _staticFrictionGain = 1.0f;

    [ObservableProperty]
    private float _staticFrictionMaxElasticStretch = 0.01f;

    [ObservableProperty]
    private float _staticFrictionSpringStiffness = 15.0f;

    [ObservableProperty]
    private float _staticFrictionKineticFrictionBase = 0.20f;

    [ObservableProperty]
    private float _staticFrictionEngineOffDamping = 0.15f;

    [ObservableProperty]
    private float _staticFrictionEngineOnDamping = 0.02f;

    [ObservableProperty]
    private float _staticFrictionEngineOffScale = 1.0f;

    [ObservableProperty]
    private float _staticFrictionEngineOnScale = 0.3f;

    [ObservableProperty]
    private float _staticFrictionActiveDecay = 0.995f;

    [ObservableProperty]
    private float _staticFrictionReturnDecay = 0.85f;

    [ObservableProperty]
    private float _staticFrictionOutputSmoothAlpha = 0.35f;

    [ObservableProperty]
    private int _snapshotButtonIndex = -1;
    private bool _suppressSettingsSave;

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
    private string _currentCornerType = "";

    [ObservableProperty]
    private string _currentCornerName = "";

    private TrackCorner? _lastCurrentCorner;




    public int GameSectorIndex => _telemetryLoop?.CurrentSector ?? 0;

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
    private string _detectedCarModel = "";

    [ObservableProperty]
    private bool _isPerCarAutoLoadEnabled = true;

    [ObservableProperty]
    private bool _isPitDetected;

    [ObservableProperty]
    private bool _isInPit;

    [ObservableProperty]
    private string _trackMapStatus = "";

    [ObservableProperty]
    private float _trackLatitude;

    [ObservableProperty]
    private float _trackLongitude;

    [ObservableProperty]
    private float _trackRotation;

    [ObservableProperty]
    private bool _showSatelliteMap;

    [ObservableProperty]
    private bool _satelliteMapReady;

    public TrackMap? CurrentTrackMap => _telemetryLoop.MapBuilder.CurrentMap;




    private bool[]? _prevSnapshotButtons;
    private bool[]? _prevPanicButtons;



    public ObservableCollection<FfbDeviceInfo> AvailableDevices { get; } = new();
    public ObservableCollection<FfbDeviceInfo> PanicDevices { get; } = new();
    public ObservableCollection<string> SnapshotButtonNames { get; } = new();
    public ObservableCollection<string> PanicButtonNames { get; } = new();
    public ObservableCollection<string> ActiveFeatures { get; } = new();
    public ObservableCollection<string> SystemLogEntries { get; } = new();
    public ObservableCollection<string> RecentSystemLogEntries { get; } = new();

    private static readonly string SystemLogFilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "AcEvoFfbTuner", "system_log.json");

    [ObservableProperty]
    private bool _isHapticTestRunning;

    private bool CanTestHaptics => IsDeviceConnected && !IsHapticTestRunning;

    [ObservableProperty]
    private string _halProviderName = "None";

    [ObservableProperty]
    private bool _isHalSdkConnected;

    [ObservableProperty]
    private bool _isHalHapticEngineActive;

    [ObservableProperty]
    private bool _isHalPeripheralSynced;

    [ObservableProperty]
    private bool _isTestBuzzRunning;

    private bool CanTestBuzz => IsDeviceConnected && !IsTestBuzzRunning && !IsRunning;

    private readonly int _signalMonitorMaxPoints = 120;
    private int _signalMonitorTickCounter;
    private readonly List<float> _signalLowFreqBuffer = new();
    private readonly List<float> _signalHighFreqBuffer = new();

    [ObservableProperty]
    private System.Windows.Media.PointCollection _signalMonitorLowFreq = new();

    [ObservableProperty]
    private System.Windows.Media.PointCollection _signalMonitorHighFreq = new();

    [ObservableProperty]
    private FfbDeviceInfo? _selectedDevice;

    [ObservableProperty]
    private FfbDeviceInfo? _selectedPanicDevice;

    [ObservableProperty]
    private int _panicButtonComboIndex;

    [ObservableProperty]
    private bool _isAssigningSnapshotButton;

    [ObservableProperty]
    private bool _isAssigningPanicButton;

    [ObservableProperty]
    private string _snapshotAssignStatus = "";

    [ObservableProperty]
    private string _panicAssignStatus = "";

    [ObservableProperty]
    private string _buttonDetectionText = "";

    [ObservableProperty]
    private CoachSessionState _coachSessionState = Services.CoachSessionState.Idle;

    [ObservableProperty]
    private string _coachLoadingText = "";

    [ObservableProperty]
    private string _coachDataSourceLabel = "";

    [ObservableProperty]
    private string _coachCurrentProfileName = "";

    public ObservableCollection<CoachPendingRec> CoachPendingRecs { get; } = [];

    [ObservableProperty]
    private bool _coachHasPendingRecs;

    [RelayCommand]
    private Task CoachApplyRec(CoachPendingRec rec)
    {
        if (rec == null) return Task.CompletedTask;
        var result = _coachService.ApplyRecommendation(rec.ToFfbRecommendation());
        if (result.State == CoachSessionState.Questioning)
        {
            CoachPendingRecs.Remove(rec);
            CoachHasPendingRecs = CoachPendingRecs.Count > 0;
            foreach (var msg in result.Messages)
                CoachMessages.Add(msg);
            if (_coachService.IsAiEnabled)
                PlayCoachAlert();
            RefreshProfileValues();
        }
        return Task.CompletedTask;
    }

    [RelayCommand]
    private Task CoachApplyAll()
    {
        var recs = CoachPendingRecs.ToList();
        foreach (var rec in recs)
        {
            var result = _coachService.ApplyRecommendation(rec.ToFfbRecommendation());
            if (result.State == CoachSessionState.Questioning)
            {
                CoachPendingRecs.Remove(rec);
                foreach (var msg in result.Messages)
                    CoachMessages.Add(msg);
            }
        }
        CoachHasPendingRecs = CoachPendingRecs.Count > 0;
        if (_coachService.IsAiEnabled)
            PlayCoachAlert();
        RefreshProfileValues();
        return Task.CompletedTask;
    }

    [RelayCommand]
    private void CoachUndo()
    {
        var result = _coachService.UndoLastChange();
        foreach (var msg in result.Messages)
            CoachMessages.Add(msg);
        if (_coachService.IsAiEnabled)
            PlayCoachAlert();
        RefreshProfileValues();
    }

    private void RefreshProfileValues()
    {
        var profile = _profileManager.ActiveProfile;
        if (profile == null) return;
        profile.ApplyToPipeline(_pipeline);
        LoadProfileValues(profile);
        SystemLogEntries.Add($"[Coach] Profile values refreshed in UI");
    }

    private void AddPendingRec(FfbRecommendation rec)
    {
        if (string.IsNullOrWhiteSpace(rec.Parameter))
        {
            SystemLogEntries.Add($"[Coach] Skipped empty-param rec: {rec.Reason}");
            return;
        }
        var existing = CoachPendingRecs.FirstOrDefault(p => p.Parameter == rec.Parameter);
        if (existing != null)
        {
            existing.CurrentValue = rec.CurrentValue;
            existing.SuggestedValue = rec.SuggestedValue;
            existing.Label = $"{rec.Parameter}: {rec.CurrentValue:F2} → {rec.SuggestedValue:F2}";
            existing.Description = rec.Impact ?? rec.Reason ?? "";
            SystemLogEntries.Add($"[Coach] Updated pending rec: {rec.Parameter} {rec.CurrentValue:F3}→{rec.SuggestedValue:F3}");
            return;
        }
        CoachPendingRecs.Add(new CoachPendingRec
        {
            Label = $"{rec.Parameter}: {rec.CurrentValue:F2} → {rec.SuggestedValue:F2}",
            Description = rec.Impact ?? rec.Reason ?? "",
            Parameter = rec.Parameter,
            CurrentValue = rec.CurrentValue,
            SuggestedValue = rec.SuggestedValue,
            Reason = rec.Reason ?? ""
        });
        CoachHasPendingRecs = CoachPendingRecs.Count > 0;
        SystemLogEntries.Add($"[Coach] Added pending rec: {rec.Parameter} {rec.CurrentValue:F3}→{rec.SuggestedValue:F3}");
    }

    [ObservableProperty]
    private bool _coachIsBusy;

    public ObservableCollection<CoachMessage> CoachMessages { get; } = [];

    public int PanicButtonIndex => PanicButtonComboIndex <= 0 ? -1 : PanicButtonComboIndex - 1;
    public int PanicDeviceButtonCount => _deviceManager.SecondaryButtonCount;

    public FfbPipeline Pipeline => _pipeline;
    public FfbDeviceManager DeviceManager => _deviceManager;
    public TelemetryLoop TelemetryLoop => _telemetryLoop;
    public int DeviceButtonCount => _deviceManager.ButtonCount;
    public FfbCoachService CoachService => _coachService;

    private readonly DispatcherTimer _uiUpdateTimer;

    public MainViewModel()
    {
        _reader = CreateReader(SelectedGame);
        _pipeline = CreatePipeline(SelectedGame);
        _deviceManager = new FfbDeviceManager();
        _telemetryLoop = new TelemetryLoop(_reader, _pipeline, _deviceManager);
        _profileManager = new ProfileManager();
        _coachService = new FfbCoachService(_profileManager, _pipeline);
        InitializeAiCoach();

        _deviceManager.DeviceRequiresReconnect += () => Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            if (SelectedDevice == null) return;

            var timeSinceLastReconnect = DateTime.Now - _deviceManager.LastReconnectAttempt;
            if (timeSinceLastReconnect < TimeSpan.FromSeconds(5))
            {
                System.Diagnostics.Debug.WriteLine($"Reconnect throttled — last attempt was {timeSinceLastReconnect.TotalSeconds:F1}s ago (cooldown: 5s)");
                return;
            }

            _deviceManager.LastReconnectAttempt = DateTime.Now;
            _deviceManager.ReconnectAttemptCount++;

            if (_deviceManager.ReconnectAttemptCount > 3)
            {
                IsDeviceConnected = false;
                DeviceName = "Reconnect limit reached";
                StatusText = "Auto-reconnect failed 3 times. Disconnect and reconnect the wheel manually.";
                return;
            }

            StatusText = $"FFB device lost — attempting auto-reconnect (attempt {_deviceManager.ReconnectAttemptCount}/3)...";
            _deviceManager.DisconnectDevice();
            Thread.Sleep(200);
            if (_deviceManager.TryConnectDevice(SelectedDevice))
            {
                IsDeviceConnected = true;
                DeviceName = SelectedDevice.ProductName;
                StatusText = $"Reconnected to {SelectedDevice.ProductName}";
                PushLedConfig();
            }
            else
            {
                IsDeviceConnected = false;
                DeviceName = "Reconnect failed";
                StatusText = "Auto-reconnect failed. Disconnect and reconnect the wheel manually.";
            }
        });

        PopulateFallbackButtonNames(SnapshotButtonNames, 32);
        PopulateFallbackButtonNames(PanicButtonNames, 16);

        _telemetryLoop.StatusChanged += status => Application.Current?.Dispatcher.Invoke(() => StatusText = status);
        _telemetryLoop.GameConnectionChanged += () => Application.Current?.Dispatcher.Invoke(() =>
        {
            IsGameConnected = _telemetryLoop.IsGameConnected;
            if (IsGameConnected)
            {
                AddSystemLog($"Game connected ({GameDisplayName})");
                _voiceService.Speak("Game connected");
            }
            else
            {
                _gameRecordingService.StopRecording();
                IsScreenRecording = false;
                AddSystemLog("Game disconnected");
                _voiceService.Speak("Game disconnected");
            }
        });
        _gameRecordingService.RecordingStateChanged += (msg, isRec) => Application.Current?.Dispatcher.Invoke(() =>
        {
            WriteDiagLog("RECORDING", msg);
            IsScreenRecording = _gameRecordingService.IsRecording;
            if (!isRec && msg.Contains("saved:"))
                ScreenRecordingPath = msg;
            if (!isRec && !msg.Contains("saved:") && !msg.Contains("stopped") && !msg.Contains("finalizing"))
                StatusText = msg;
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
            _telemetryLoop.StaticDataReceived += (trackName, config, lengthM, latitude, longitude) => Application.Current?.Dispatcher.Invoke(() =>
            {
                DetectedTrackName = trackName;

                var calibration = SatelliteMapService.LoadCalibration(trackName);
                if (calibration != null)
                {
                    TrackLatitude = calibration.Value.lat;
                    TrackLongitude = calibration.Value.lon;
                    TrackRotation = calibration.Value.rotationDeg;
                }
                else if (latitude != 0 || longitude != 0)
                {
                    TrackLatitude = latitude;
                    TrackLongitude = longitude;
                    TrackRotation = 0f;
                }
                else
                {
                    var lookup = SatelliteMapService.LookupTrackLocation(trackName);
                    if (lookup != null)
                    {
                        TrackLatitude = lookup.Value.lat;
                        TrackLongitude = lookup.Value.lon;
                    }

                    var rotation = SatelliteMapService.LookupTrackRotation(trackName);
                    TrackRotation = rotation ?? 0f;
                }

                if (!string.IsNullOrEmpty(trackName))
                {
                    StatusText = $"Connected — Track: {trackName} ({config}) {lengthM:F0}m";
                }

                if (!_telemetryLoop.MapBuilder.HasCompleteMap && !string.IsNullOrEmpty(trackName) && !_mapClearedByUser && _lastAutoLoadedTrack != trackName)
                {
                    var savedMap = TrackMap.Load(trackName);
                    if (savedMap != null && savedMap.Waypoints.Count >= 10)
                    {
                        savedMap.InvalidateCache();
                        savedMap.GetCumulativeDistances();
                        if (savedMap.Corners.Count == 0) savedMap.Analyze();

                        _lastAutoLoadedTrack = trackName;
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
        _telemetryLoop.CarModelChanged += carModel => Application.Current?.Dispatcher.Invoke(() =>
        {
            DetectedCarModel = carModel;
            StatusText = $"Car detected: {carModel}";

            if (!_appSettings.AutoSwitchProfiles || _appSettings.ProfileLocked) return;

            var gameName = SelectedGame.ToString().ToLowerInvariant();
            var match = _profileManager.FindMatchingProfile(gameName, carModel, _telemetryLoop.DetectedTrackName);

            if (match != null && (SelectedProfile == null || match.Name != SelectedProfile.Name))
            {
                SelectedProfile = match;
                match.ApplyToPipeline(_pipeline);
                match.ApplyToStaticFriction(_telemetryLoop.StaticFriction);
                LoadProfileValues(match);
                _profileManager.SetActiveProfile(match);
                StatusText = $"Auto-loaded profile '{match.Name}' for {carModel}";
                _voiceService.Speak("Profile loaded");
                if (Application.Current?.MainWindow is Views.MainWindow mw)
                    mw.ShowToast("Auto-Loaded Profile", $"{match.Name} — {carModel}");
            }
        });
        _telemetryLoop.TrackChanged += newTrackName => Application.Current?.Dispatcher.Invoke(() =>
        {
            _mapClearedByUser = false;
            _lastAutoLoadedTrack = null;
            IsTrackMapAvailable = false;
            IsTrackMapRecording = false;
            TrackLengthM = 0;
            TrackWaypointCount = 0;
            CornerCount = 0;
            SectorCount = 0;
            IsPitDetected = false;
            TrackMapStatus = $"Track changed to: {newTrackName}";
            StatusText = $"Track changed to: {newTrackName} — loading map…";
        });

        _uiUpdateTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };
        _uiUpdateTimer.Tick += OnUiUpdate;

        _voiceService.Enabled = _appSettings.VoiceEnabled;
        _voiceService.Volume = _appSettings.VoiceVolume;
        _voiceService.LogMessage += msg => Application.Current?.Dispatcher.Invoke(() => AddSystemLog(msg));

        _discordPresence.Initialize();
        _discordPresence.Attach(_telemetryLoop);

        if (_appSettings.AutoDetectGame)
        {
            _gameDetector.GameDetected += game => Application.Current?.Dispatcher.Invoke(() =>
            {
                SetDetectedGame(game);
                if (SelectedGame == game) return;
                if (_gameDetectorManualOverride) return;
                SelectedGame = game;
                AddSystemLog($"Auto-detected game: {GameDisplayName}");
                StatusText = $"Auto-detected: {GameDisplayName}";
            });
            _gameDetector.GameExitedAll += () => Application.Current?.Dispatcher.Invoke(() =>
            {
                _gameDetectorManualOverride = false;
            });
            _gameDetector.Start();
        }

        AddSystemLog("Application initialized");
     }















    [RelayCommand]
    private void ToggleTelemetry()
    {
        if (IsRunning)
        {
            _telemetryLoop.Start();
            _uiUpdateTimer.Start();
            StatusText = "Running";
            RefreshProviderFeatures();
            AddSystemLog("Telemetry loop started");
            _voiceService.Speak("Telemetry started");
            _signalLowFreqBuffer.Clear();
            _signalHighFreqBuffer.Clear();
            SignalMonitorLowFreq = new System.Windows.Media.PointCollection();
            SignalMonitorHighFreq = new System.Windows.Media.PointCollection();
        }
        else
        {
            _telemetryLoop.Stop();
            _uiUpdateTimer.Stop();
            StatusText = "Stopped";
            AddSystemLog("Telemetry loop stopped");
            _voiceService.Speak("Telemetry stopped");
        }
    }
















    private bool _conflictingAppsWarningDismissed;


    private int _conflictingAppsCheckCounter;












    [RelayCommand]
    private void StartTrackMapRecording()
    {
        _mapClearedByUser = false;
        _lastAutoLoadedTrack = null;
        _telemetryLoop.MapBuilder.Reset();
        _telemetryLoop.PositionDetector.ClearMap();
        _telemetryLoop.MapBuilder.StartRecording();
        IsTrackMapRecording = true;
        IsTrackMapAvailable = false;
        StatusText = IsScreenRecording
            ? "Track map recording started — video capture is also active"
            : "Track map recording started - drive a full lap";
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
        _mapClearedByUser = true;
        _lastAutoLoadedTrack = null;
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
        TrackMapStatus = "Map cleared — press Record to remap";
        StatusText = "Track map cleared — press Record to remap";
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




    public float MaxGaugeForce => WheelMaxTorqueNm > 0 ? Math.Max(WheelMaxTorqueNm, 8f) : 10f;

    public string MaxForceLimitNmText => $"{MaxForceLimit:F3} ({MaxForceLimit * WheelMaxTorqueNm:F2} Nm)";
    public string OutputGainNmText => $"{OutputGain:F3} (peak {MaxForceLimit * WheelMaxTorqueNm:F2} Nm)";
    public string CrashSafetyClampPercentText => $"{CrashSafetyClamp * 100:F0}% ({CrashSafetyClamp * MaxForceLimit * WheelMaxTorqueNm:F2} Nm max)";

    public double ForceBarFillHeight => Math.Min(200, Math.Max(0, Math.Abs(CurrentForceOutput)) * 200);
    public double SpeedNeedleAngle => -90 + (Math.Min(360, Math.Max(0, SpeedKmh)) / 360.0) * 180;

    partial void OnLedBrightnessChanged(int value) => PushLedConfig();
    partial void OnLedFlashRateChanged(int value) => PushLedConfig();
    partial void OnLedAbsFlashEnabledChanged(bool value) => PushLedConfig();
    partial void OnLedFlagIndicatorsEnabledChanged(bool value) => PushLedConfig();
    partial void OnLedShiftLimiterFlashEnabledChanged(bool value) => PushLedConfig();
    partial void OnLedPitLimiterFlashEnabledChanged(bool value) => PushLedConfig();
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

    partial void OnHf8EnabledChanged(bool value) => _pipeline.Hf8SignalMapper.Enabled = value;
    partial void OnHf8MasterGainChanged(float value) => _pipeline.Hf8SignalMapper.MasterGain = value;
    partial void OnHf8OutputRateHzChanged(int value) => _deviceManager.ApplyHf8Config(value);

    partial void OnHf8CopySourceIndexChanged(int value)
    {
        OnPropertyChanged(nameof(Hf8IsCopySource0));
        OnPropertyChanged(nameof(Hf8IsCopySource1));
        OnPropertyChanged(nameof(Hf8IsCopySource2));
        OnPropertyChanged(nameof(Hf8IsCopySource3));
        OnPropertyChanged(nameof(Hf8IsCopySource4));
        OnPropertyChanged(nameof(Hf8IsCopySource5));
        OnPropertyChanged(nameof(Hf8IsCopySource6));
        OnPropertyChanged(nameof(Hf8IsCopySource7));
        OnPropertyChanged(nameof(Hf8CanCopyTo0));
        OnPropertyChanged(nameof(Hf8CanCopyTo1));
        OnPropertyChanged(nameof(Hf8CanCopyTo2));
        OnPropertyChanged(nameof(Hf8CanCopyTo3));
        OnPropertyChanged(nameof(Hf8CanCopyTo4));
        OnPropertyChanged(nameof(Hf8CanCopyTo5));
        OnPropertyChanged(nameof(Hf8CanCopyTo6));
        OnPropertyChanged(nameof(Hf8CanCopyTo7));
        OnPropertyChanged(nameof(Hf8CopyActive));
    }

    partial void OnHf8ZoneGain0Changed(float value) => _pipeline.Hf8SignalMapper.ZoneGains[0] = value;
    partial void OnHf8ZoneGain1Changed(float value) => _pipeline.Hf8SignalMapper.ZoneGains[1] = value;
    partial void OnHf8ZoneGain2Changed(float value) => _pipeline.Hf8SignalMapper.ZoneGains[2] = value;
    partial void OnHf8ZoneGain3Changed(float value) => _pipeline.Hf8SignalMapper.ZoneGains[3] = value;
    partial void OnHf8ZoneGain4Changed(float value) => _pipeline.Hf8SignalMapper.ZoneGains[4] = value;
    partial void OnHf8ZoneGain5Changed(float value) => _pipeline.Hf8SignalMapper.ZoneGains[5] = value;
    partial void OnHf8ZoneGain6Changed(float value) => _pipeline.Hf8SignalMapper.ZoneGains[6] = value;
    partial void OnHf8ZoneGain7Changed(float value) => _pipeline.Hf8SignalMapper.ZoneGains[7] = value;
    partial void OnHf8ZoneEnabled0Changed(bool value) => _pipeline.Hf8SignalMapper.ZoneEnabled[0] = value;
    partial void OnHf8ZoneEnabled1Changed(bool value) => _pipeline.Hf8SignalMapper.ZoneEnabled[1] = value;
    partial void OnHf8ZoneEnabled2Changed(bool value) => _pipeline.Hf8SignalMapper.ZoneEnabled[2] = value;
    partial void OnHf8ZoneEnabled3Changed(bool value) => _pipeline.Hf8SignalMapper.ZoneEnabled[3] = value;
    partial void OnHf8ZoneEnabled4Changed(bool value) => _pipeline.Hf8SignalMapper.ZoneEnabled[4] = value;
    partial void OnHf8ZoneEnabled5Changed(bool value) => _pipeline.Hf8SignalMapper.ZoneEnabled[5] = value;
    partial void OnHf8ZoneEnabled6Changed(bool value) => _pipeline.Hf8SignalMapper.ZoneEnabled[6] = value;
    partial void OnHf8ZoneEnabled7Changed(bool value) => _pipeline.Hf8SignalMapper.ZoneEnabled[7] = value;

    partial void OnHf8SrcSusp0Changed(float value) => _pipeline.Hf8SignalMapper.SetSourceWeight(0, 0, value);
    partial void OnHf8SrcSlip0Changed(float value) => _pipeline.Hf8SignalMapper.SetSourceWeight(0, 1, value);
    partial void OnHf8SrcKerb0Changed(float value) => _pipeline.Hf8SignalMapper.SetSourceWeight(0, 2, value);
    partial void OnHf8SrcLatG0Changed(float value) => _pipeline.Hf8SignalMapper.SetSourceWeight(0, 3, value);
    partial void OnHf8SrcEngine0Changed(float value) => _pipeline.Hf8SignalMapper.SetSourceWeight(0, 4, value);

    partial void OnHf8SrcSusp1Changed(float value) => _pipeline.Hf8SignalMapper.SetSourceWeight(1, 0, value);
    partial void OnHf8SrcSlip1Changed(float value) => _pipeline.Hf8SignalMapper.SetSourceWeight(1, 1, value);
    partial void OnHf8SrcKerb1Changed(float value) => _pipeline.Hf8SignalMapper.SetSourceWeight(1, 2, value);
    partial void OnHf8SrcLatG1Changed(float value) => _pipeline.Hf8SignalMapper.SetSourceWeight(1, 3, value);
    partial void OnHf8SrcEngine1Changed(float value) => _pipeline.Hf8SignalMapper.SetSourceWeight(1, 4, value);

    partial void OnHf8SrcSusp2Changed(float value) => _pipeline.Hf8SignalMapper.SetSourceWeight(2, 0, value);
    partial void OnHf8SrcSlip2Changed(float value) => _pipeline.Hf8SignalMapper.SetSourceWeight(2, 1, value);
    partial void OnHf8SrcKerb2Changed(float value) => _pipeline.Hf8SignalMapper.SetSourceWeight(2, 2, value);
    partial void OnHf8SrcLatG2Changed(float value) => _pipeline.Hf8SignalMapper.SetSourceWeight(2, 3, value);
    partial void OnHf8SrcEngine2Changed(float value) => _pipeline.Hf8SignalMapper.SetSourceWeight(2, 4, value);

    partial void OnHf8SrcSusp3Changed(float value) => _pipeline.Hf8SignalMapper.SetSourceWeight(3, 0, value);
    partial void OnHf8SrcSlip3Changed(float value) => _pipeline.Hf8SignalMapper.SetSourceWeight(3, 1, value);
    partial void OnHf8SrcKerb3Changed(float value) => _pipeline.Hf8SignalMapper.SetSourceWeight(3, 2, value);
    partial void OnHf8SrcLatG3Changed(float value) => _pipeline.Hf8SignalMapper.SetSourceWeight(3, 3, value);
    partial void OnHf8SrcEngine3Changed(float value) => _pipeline.Hf8SignalMapper.SetSourceWeight(3, 4, value);

    partial void OnHf8SrcSusp4Changed(float value) => _pipeline.Hf8SignalMapper.SetSourceWeight(4, 0, value);
    partial void OnHf8SrcSlip4Changed(float value) => _pipeline.Hf8SignalMapper.SetSourceWeight(4, 1, value);
    partial void OnHf8SrcKerb4Changed(float value) => _pipeline.Hf8SignalMapper.SetSourceWeight(4, 2, value);
    partial void OnHf8SrcLatG4Changed(float value) => _pipeline.Hf8SignalMapper.SetSourceWeight(4, 3, value);
    partial void OnHf8SrcEngine4Changed(float value) => _pipeline.Hf8SignalMapper.SetSourceWeight(4, 4, value);

    partial void OnHf8SrcSusp5Changed(float value) => _pipeline.Hf8SignalMapper.SetSourceWeight(5, 0, value);
    partial void OnHf8SrcSlip5Changed(float value) => _pipeline.Hf8SignalMapper.SetSourceWeight(5, 1, value);
    partial void OnHf8SrcKerb5Changed(float value) => _pipeline.Hf8SignalMapper.SetSourceWeight(5, 2, value);
    partial void OnHf8SrcLatG5Changed(float value) => _pipeline.Hf8SignalMapper.SetSourceWeight(5, 3, value);
    partial void OnHf8SrcEngine5Changed(float value) => _pipeline.Hf8SignalMapper.SetSourceWeight(5, 4, value);

    partial void OnHf8SrcSusp6Changed(float value) => _pipeline.Hf8SignalMapper.SetSourceWeight(6, 0, value);
    partial void OnHf8SrcSlip6Changed(float value) => _pipeline.Hf8SignalMapper.SetSourceWeight(6, 1, value);
    partial void OnHf8SrcKerb6Changed(float value) => _pipeline.Hf8SignalMapper.SetSourceWeight(6, 2, value);
    partial void OnHf8SrcLatG6Changed(float value) => _pipeline.Hf8SignalMapper.SetSourceWeight(6, 3, value);
    partial void OnHf8SrcEngine6Changed(float value) => _pipeline.Hf8SignalMapper.SetSourceWeight(6, 4, value);

    partial void OnHf8SrcSusp7Changed(float value) => _pipeline.Hf8SignalMapper.SetSourceWeight(7, 0, value);
    partial void OnHf8SrcSlip7Changed(float value) => _pipeline.Hf8SignalMapper.SetSourceWeight(7, 1, value);
    partial void OnHf8SrcKerb7Changed(float value) => _pipeline.Hf8SignalMapper.SetSourceWeight(7, 2, value);
    partial void OnHf8SrcLatG7Changed(float value) => _pipeline.Hf8SignalMapper.SetSourceWeight(7, 3, value);
    partial void OnHf8SrcEngine7Changed(float value) => _pipeline.Hf8SignalMapper.SetSourceWeight(7, 4, value);

    [RelayCommand]
    private void Hf8SetCopySource(string zoneIndex)
    {
        if (int.TryParse(zoneIndex, out int idx))
            Hf8CopySourceIndex = idx;
    }

    [RelayCommand]
    private void Hf8CopyToZone(string zoneIndex)
    {
        if (!int.TryParse(zoneIndex, out int dst)) return;
        int src = Hf8CopySourceIndex;
        if (src < 0 || src == dst) return;
        CopyHf8ZoneSettings(src, dst);
        Hf8CopySourceIndex = -1;
    }

    private void CopyHf8ZoneSettings(int src, int dst)
    {
        SetHf8ZoneGain(dst, GetHf8ZoneGain(src));
        SetHf8ZoneEnabled(dst, GetHf8ZoneEnabled(src));
        SetHf8SrcSusp(dst, GetHf8SrcSusp(src));
        SetHf8SrcSlip(dst, GetHf8SrcSlip(src));
        SetHf8SrcKerb(dst, GetHf8SrcKerb(src));
        SetHf8SrcLatG(dst, GetHf8SrcLatG(src));
        SetHf8SrcEngine(dst, GetHf8SrcEngine(src));
    }

    private float GetHf8ZoneGain(int i) => i switch
    {
        0 => Hf8ZoneGain0, 1 => Hf8ZoneGain1, 2 => Hf8ZoneGain2, 3 => Hf8ZoneGain3,
        4 => Hf8ZoneGain4, 5 => Hf8ZoneGain5, 6 => Hf8ZoneGain6, _ => Hf8ZoneGain7,
    };

    private bool GetHf8ZoneEnabled(int i) => i switch
    {
        0 => Hf8ZoneEnabled0, 1 => Hf8ZoneEnabled1, 2 => Hf8ZoneEnabled2, 3 => Hf8ZoneEnabled3,
        4 => Hf8ZoneEnabled4, 5 => Hf8ZoneEnabled5, 6 => Hf8ZoneEnabled6, _ => Hf8ZoneEnabled7,
    };

    private float GetHf8SrcSusp(int i) => i switch
    {
        0 => Hf8SrcSusp0, 1 => Hf8SrcSusp1, 2 => Hf8SrcSusp2, 3 => Hf8SrcSusp3,
        4 => Hf8SrcSusp4, 5 => Hf8SrcSusp5, 6 => Hf8SrcSusp6, _ => Hf8SrcSusp7,
    };

    private float GetHf8SrcSlip(int i) => i switch
    {
        0 => Hf8SrcSlip0, 1 => Hf8SrcSlip1, 2 => Hf8SrcSlip2, 3 => Hf8SrcSlip3,
        4 => Hf8SrcSlip4, 5 => Hf8SrcSlip5, 6 => Hf8SrcSlip6, _ => Hf8SrcSlip7,
    };

    private float GetHf8SrcKerb(int i) => i switch
    {
        0 => Hf8SrcKerb0, 1 => Hf8SrcKerb1, 2 => Hf8SrcKerb2, 3 => Hf8SrcKerb3,
        4 => Hf8SrcKerb4, 5 => Hf8SrcKerb5, 6 => Hf8SrcKerb6, _ => Hf8SrcKerb7,
    };

    private float GetHf8SrcLatG(int i) => i switch
    {
        0 => Hf8SrcLatG0, 1 => Hf8SrcLatG1, 2 => Hf8SrcLatG2, 3 => Hf8SrcLatG3,
        4 => Hf8SrcLatG4, 5 => Hf8SrcLatG5, 6 => Hf8SrcLatG6, _ => Hf8SrcLatG7,
    };

    private float GetHf8SrcEngine(int i) => i switch
    {
        0 => Hf8SrcEngine0, 1 => Hf8SrcEngine1, 2 => Hf8SrcEngine2, 3 => Hf8SrcEngine3,
        4 => Hf8SrcEngine4, 5 => Hf8SrcEngine5, 6 => Hf8SrcEngine6, _ => Hf8SrcEngine7,
    };

    private void SetHf8ZoneGain(int i, float v)
    {
        switch (i)
        {
            case 0: Hf8ZoneGain0 = v; break;
            case 1: Hf8ZoneGain1 = v; break;
            case 2: Hf8ZoneGain2 = v; break;
            case 3: Hf8ZoneGain3 = v; break;
            case 4: Hf8ZoneGain4 = v; break;
            case 5: Hf8ZoneGain5 = v; break;
            case 6: Hf8ZoneGain6 = v; break;
            case 7: Hf8ZoneGain7 = v; break;
        }
    }

    private void SetHf8ZoneEnabled(int i, bool v)
    {
        switch (i)
        {
            case 0: Hf8ZoneEnabled0 = v; break;
            case 1: Hf8ZoneEnabled1 = v; break;
            case 2: Hf8ZoneEnabled2 = v; break;
            case 3: Hf8ZoneEnabled3 = v; break;
            case 4: Hf8ZoneEnabled4 = v; break;
            case 5: Hf8ZoneEnabled5 = v; break;
            case 6: Hf8ZoneEnabled6 = v; break;
            case 7: Hf8ZoneEnabled7 = v; break;
        }
    }

    private void SetHf8SrcSusp(int i, float v)
    {
        switch (i)
        {
            case 0: Hf8SrcSusp0 = v; break;
            case 1: Hf8SrcSusp1 = v; break;
            case 2: Hf8SrcSusp2 = v; break;
            case 3: Hf8SrcSusp3 = v; break;
            case 4: Hf8SrcSusp4 = v; break;
            case 5: Hf8SrcSusp5 = v; break;
            case 6: Hf8SrcSusp6 = v; break;
            case 7: Hf8SrcSusp7 = v; break;
        }
    }

    private void SetHf8SrcSlip(int i, float v)
    {
        switch (i)
        {
            case 0: Hf8SrcSlip0 = v; break;
            case 1: Hf8SrcSlip1 = v; break;
            case 2: Hf8SrcSlip2 = v; break;
            case 3: Hf8SrcSlip3 = v; break;
            case 4: Hf8SrcSlip4 = v; break;
            case 5: Hf8SrcSlip5 = v; break;
            case 6: Hf8SrcSlip6 = v; break;
            case 7: Hf8SrcSlip7 = v; break;
        }
    }

    private void SetHf8SrcKerb(int i, float v)
    {
        switch (i)
        {
            case 0: Hf8SrcKerb0 = v; break;
            case 1: Hf8SrcKerb1 = v; break;
            case 2: Hf8SrcKerb2 = v; break;
            case 3: Hf8SrcKerb3 = v; break;
            case 4: Hf8SrcKerb4 = v; break;
            case 5: Hf8SrcKerb5 = v; break;
            case 6: Hf8SrcKerb6 = v; break;
            case 7: Hf8SrcKerb7 = v; break;
        }
    }

    private void SetHf8SrcLatG(int i, float v)
    {
        switch (i)
        {
            case 0: Hf8SrcLatG0 = v; break;
            case 1: Hf8SrcLatG1 = v; break;
            case 2: Hf8SrcLatG2 = v; break;
            case 3: Hf8SrcLatG3 = v; break;
            case 4: Hf8SrcLatG4 = v; break;
            case 5: Hf8SrcLatG5 = v; break;
            case 6: Hf8SrcLatG6 = v; break;
            case 7: Hf8SrcLatG7 = v; break;
        }
    }

    private void SetHf8SrcEngine(int i, float v)
    {
        switch (i)
        {
            case 0: Hf8SrcEngine0 = v; break;
            case 1: Hf8SrcEngine1 = v; break;
            case 2: Hf8SrcEngine2 = v; break;
            case 3: Hf8SrcEngine3 = v; break;
            case 4: Hf8SrcEngine4 = v; break;
            case 5: Hf8SrcEngine5 = v; break;
            case 6: Hf8SrcEngine6 = v; break;
            case 7: Hf8SrcEngine7 = v; break;
        }
    }

    partial void OnGripGuardEnabledChanged(bool value) => _pipeline.GripGuard.Enabled = value;
    partial void OnGripGuardPeakSlipAngleChanged(float value) => _pipeline.GripGuard.PeakSlipAngle = value;
    partial void OnGripGuardAttenuationStrengthChanged(float value) => _pipeline.GripGuard.AttenuationStrength = value;
    partial void OnGripGuardMechanicalTrailGainChanged(float value) => _pipeline.GripGuard.MechanicalTrailGain = value;
    partial void OnGripGuardMinSpeedKmhChanged(float value) => _pipeline.GripGuard.MinSpeedKmh = value;

    partial void OnCrashEnabledChanged(bool value) => _pipeline.CrashDetector.Enabled = value;
    partial void OnCrashImpactGainChanged(float value) => _pipeline.CrashDetector.ImpactGain = value;
    partial void OnCrashSafetyClampChanged(float value) => _pipeline.CrashDetector.SafetyClamp = value;
    partial void OnCrashDecayRateChanged(float value) => _pipeline.CrashDetector.DecayRate = value;
    partial void OnCrashTriggerThresholdGChanged(float value) => _pipeline.CrashDetector.TriggerThresholdG = value;
    partial void OnCrashMinSpeedKmhChanged(float value) => _pipeline.CrashDetector.MinSpeedKmh = value;
    partial void OnCrashSafetyOverrideChanged(bool value)
    {
        _pipeline.CrashDetector.SafetyOverride = value;
        ShowCrashSafetyWarning = value;
    }

    partial void OnTyreConditionEnabledChanged(bool value) => _pipeline.TyreCondition.Enabled = value;
    partial void OnTyreConditionBlowoutGainChanged(float value) => _pipeline.TyreCondition.BlowoutVibrationGain = value;
    partial void OnTyreConditionPressureLossGainChanged(float value) => _pipeline.TyreCondition.PressureLossGain = value;
    partial void OnTyreConditionDamageAsymmetryGainChanged(float value) => _pipeline.TyreCondition.DamageAsymmetryGain = value;
    partial void OnTyreConditionBlowoutThresholdChanged(float value) => _pipeline.TyreCondition.BlowoutPressureThreshold = value;
    partial void OnTyreConditionMaxBlowoutAmplitudeChanged(float value) => _pipeline.TyreCondition.MaxBlowoutAmplitude = value;

    partial void OnWetWeatherEnabledChanged(bool value) => _pipeline.WetWeather.Enabled = value;
    partial void OnWetWeatherAutoDetectChanged(bool value) => _pipeline.WetWeather.AutoDetect = value;
    partial void OnWetWeatherManualIntensityChanged(float value) => _pipeline.WetWeather.ManualIntensity = value;
    partial void OnWetWeatherRoadVibSuppressionChanged(float value) => _pipeline.WetWeather.RoadVibSuppression = value;
    partial void OnWetWeatherCurbSuppressionChanged(float value) => _pipeline.WetWeather.CurbSuppression = value;
    partial void OnWetWeatherScrubSuppressionChanged(float value) => _pipeline.WetWeather.ScrubSuppression = value;
    partial void OnWetWeatherPeakSlipAngleMultiplierChanged(float value) => _pipeline.WetWeather.PeakSlipAngleMultiplier = value;
    partial void OnWetWeatherDampingReductionChanged(float value) => _pipeline.WetWeather.DampingReduction = value;
    partial void OnWetWeatherNoiseFloorSuppressionChanged(float value) => _pipeline.WetWeather.NoiseFloorSuppression = value;
    partial void OnWetWeatherHydroplaningEnabledChanged(bool value) => _pipeline.WetWeather.HydroplaningEnabled = value;
    partial void OnWetWeatherHydroplaningSpeedThresholdChanged(float value) => _pipeline.WetWeather.HydroplaningSpeedThreshold = value;
    partial void OnWetWeatherHydroplaningMaxAttenuationChanged(float value) => _pipeline.WetWeather.HydroplaningMaxAttenuation = value;

    // ── Stationary Friction handlers ──────────────────────────────────





    private void UpdateLedCapabilities()
    {
        bool connected = _deviceManager.IsLedControllerConnected;
        LedTabVisible = connected;

        if (!connected)
        {
            ResetLedCapabilities();
            return;
        }

        LedVendorName = _deviceManager.LedVendorDisplayName;
        int count = _deviceManager.LedCount;
        LedVisibleCount = count;
        LedBrightnessVisible = _deviceManager.LedSupportsBrightness;
        LedColorSchemeVisible = _deviceManager.LedSupportsRgb;
        LedFlagIndicatorsVisible = _deviceManager.LedSupportsFlags;

        LedRpmThreshold1Visible = count >= 1;
        LedRpmThreshold2Visible = count >= 2;
        LedRpmThreshold3Visible = count >= 3;
        LedRpmThreshold4Visible = count >= 4;
        LedRpmThreshold5Visible = count >= 5;
        LedRpmThreshold6Visible = count >= 6;
        LedRpmThreshold7Visible = count >= 7;
        LedRpmThreshold8Visible = count >= 8;
        LedRpmThreshold9Visible = count >= 9;
        LedRpmThreshold10Visible = count >= 10;

        var supported = new List<string>();
        if (LedBrightnessVisible) supported.Add("Brightness");
        if (LedColorSchemeVisible) supported.Add("RGB Colors");
        if (LedFlagIndicatorsVisible) supported.Add("Flag Indicators");
        supported.Add($"{count} RPM LEDs");

        LedSupportedInfo = $"{LedVendorName} wheel detected — {string.Join(" · ", supported)}";

        Hf8Connected = _deviceManager.IsHf8Connected;
        Hf8ConnectionStatus = _deviceManager.IsHf8Connected
            ? $"Connected: {_deviceManager.Hf8DeviceInfo}"
            : "No HF8 device detected — connect via USB and ensure HFS software is closed";
    }

    private void ResetLedCapabilities()
    {
        LedTabVisible = false;
        LedBrightnessVisible = false;
        LedColorSchemeVisible = false;
        LedFlagIndicatorsVisible = false;
        LedVisibleCount = 0;
        LedVendorName = string.Empty;
        LedSupportedInfo = "Connect a wheel to configure LED effects";
        LedRpmThreshold1Visible = false;
        LedRpmThreshold2Visible = false;
        LedRpmThreshold3Visible = false;
        LedRpmThreshold4Visible = false;
        LedRpmThreshold5Visible = false;
        LedRpmThreshold6Visible = false;
        LedRpmThreshold7Visible = false;
        LedRpmThreshold8Visible = false;
        LedRpmThreshold9Visible = false;
        LedRpmThreshold10Visible = false;

        Hf8Connected = false;
        Hf8ConnectionStatus = "No HF8 device detected";
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
            PitLimiterFlashEnabled = LedPitLimiterFlashEnabled,
            ColorScheme = colorScheme,
            RpmPreset = rpmPreset,
            RpmThresholds = new[]
            {
                LedRpmThreshold1, LedRpmThreshold2, LedRpmThreshold3, LedRpmThreshold4, LedRpmThreshold5,
                LedRpmThreshold6, LedRpmThreshold7, LedRpmThreshold8, LedRpmThreshold9, LedRpmThreshold10
            }
        };

        _deviceManager.LedConfig = config;
        StatusText = $"LED config applied: brightness={config.Brightness}% flash={config.FlashRateTicks} abs={config.AbsFlashEnabled} pit={config.PitLimiterFlashEnabled}";
    }

    private void LoadLedValues(LedEffectConfigDto dto)
    {
        LedBrightness = dto.Brightness;
        LedFlashRate = dto.FlashRateTicks;
        LedAbsFlashEnabled = dto.AbsFlashEnabled;
        LedFlagIndicatorsEnabled = dto.FlagIndicatorsEnabled;
        LedShiftLimiterFlashEnabled = dto.ShiftLimiterFlashEnabled;
        LedPitLimiterFlashEnabled = dto.PitLimiterFlashEnabled;
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


    [ObservableProperty]
    private bool _isSendingDiagnosticPack;

    [ObservableProperty]
    private string _diagnosticPackStatus = string.Empty;

    private readonly Services.AppSettings _appSettings = Services.AppSettings.Load();

    public Services.AppSettings AppSettings => _appSettings;

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

    [ObservableProperty]
    private bool _startMinimised;

    [ObservableProperty]
    private bool _autoConnect;

    [ObservableProperty]
    private bool _autoStart;

    [ObservableProperty]
    private int _defaultStartPageIndex;

    [ObservableProperty]
    private bool _tooltipsEnabled = true;

    [ObservableProperty]
    private bool _autoProfileUpgrade;

    [ObservableProperty]
    private string _themeMode = ThemeManager.DefaultTheme;

    partial void OnThemeModeChanged(string value)
    {
        ThemeManager.ApplyTheme(value);
        _appSettings.ThemeName = value;
        _appSettings.Save();
    }

    [ObservableProperty]
    private bool _voiceEnabled = true;

    [ObservableProperty]
    private int _voiceVolume = 75;



    public string VoiceEngine => _voiceService.ActiveEngine;

    public bool VoicePackReady => VoiceService.IsCacheReady;

    public string VoiceCacheStatus => VoiceService.IsCacheReady
        ? $"{_voiceService.CachedCount}/{_voiceService.TotalPhrases} phrases cached"
        : "Voice pack not found. Place MP3 files in:\n" + GetVoiceCachePath();



    [ObservableProperty]
    private ObservableCollection<string> _availableVoices = new();

    [ObservableProperty]
    private string? _selectedVoice;

    private bool _voiceInitialized;


    [ObservableProperty]
    private bool _isInstallingVoices;



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


    partial void OnStartMinimisedChanged(bool value)
    {
        _appSettings.StartMinimised = value;
        _appSettings.Save();
    }

    partial void OnAutoConnectChanged(bool value)
    {
        _appSettings.AutoConnect = value;
        _appSettings.Save();
    }

    partial void OnAutoStartChanged(bool value)
    {
        _appSettings.AutoStart = value;
        _appSettings.Save();
    }

    partial void OnDefaultStartPageIndexChanged(int value)
    {
        var pages = Enum.GetValues<NavPage>();
        if (value >= 0 && value < pages.Length)
        {
            _appSettings.DefaultStartPage = pages[value].ToString();
            _appSettings.Save();
        }
    }

    partial void OnTooltipsEnabledChanged(bool value)
    {
        _appSettings.TooltipsEnabled = value;
        _appSettings.Save();
    }

    partial void OnAutoProfileUpgradeChanged(bool value)
    {
        _appSettings.AutoProfileUpgrade = value;
        _appSettings.Save();
    }






    private string? GetSelectedOutputDeviceId()
    {
        if (string.IsNullOrEmpty(SelectedAudioOutputDevice)) return null;
        return _deviceNameToId.TryGetValue(SelectedAudioOutputDevice, out var id) ? id : null;
    }















    private static void PlayCoachAlert()
    {
        try
        {
            int sampleRate = 44100;
            int durationMs = 120;
            int samples = sampleRate * durationMs / 1000;
            short[] buffer = new short[samples];

            double freq1 = 880.0;
            double freq2 = 1320.0;
            for (int i = 0; i < samples; i++)
            {
                double t = (double)i / sampleRate;
                double env = i < samples / 2
                    ? (double)i / (samples / 2)
                    : 1.0 - (double)(i - samples / 2) / (samples / 2);
                double sample = env * 0.35 * (
                    Math.Sin(2.0 * Math.PI * freq1 * t) * 0.6 +
                    Math.Sin(2.0 * Math.PI * freq2 * t) * 0.4);
                buffer[i] = (short)(sample * short.MaxValue);
            }

            using var ms = new MemoryStream();
            WriteWavHeader(ms, sampleRate, buffer.Length);
            foreach (short s in buffer)
            {
                ms.WriteByte((byte)(s & 0xFF));
                ms.WriteByte((byte)((s >> 8) & 0xFF));
            }
            ms.Position = 0;
            using var player = new SoundPlayer(ms);
            player.Play();
        }
        catch { }
    }

    private static void WriteWavHeader(Stream stream, int sampleRate, int dataSampleCount)
    {
        int byteRate = sampleRate * 2;
        int dataSize = dataSampleCount * 2;
        int fileSize = 36 + dataSize;

        WriteLE32(stream, 0x46464952); // "RIFF"
        WriteLE32(stream, fileSize);
        WriteLE32(stream, 0x45564157); // "WAVE"

        WriteLE32(stream, 0x20746D66); // "fmt "
        WriteLE32(stream, 16);         // chunk size
        WriteLE16(stream, 1);          // PCM
        WriteLE16(stream, 1);          // mono
        WriteLE32(stream, sampleRate);
        WriteLE32(stream, byteRate);
        WriteLE16(stream, 2);          // block align
        WriteLE16(stream, 16);         // bits per sample

        WriteLE32(stream, 0x61746164); // "data"
        WriteLE32(stream, dataSize);
    }

    private static void WriteLE16(Stream stream, ushort value)
    {
        stream.WriteByte((byte)(value & 0xFF));
        stream.WriteByte((byte)((value >> 8) & 0xFF));
    }

    private static void WriteLE32(Stream stream, int value)
    {
        stream.WriteByte((byte)(value & 0xFF));
        stream.WriteByte((byte)((value >> 8) & 0xFF));
        stream.WriteByte((byte)((value >> 16) & 0xFF));
        stream.WriteByte((byte)((value >> 24) & 0xFF));
    }

    private void CoachStartLiveMonitor()
    {
        if (_isLiveMonitoring) return;

        // Reset profiler buffers so we sample fresh data from this point
        _profilerMinOut = float.MaxValue;
        _profilerMaxOut = float.MinValue;
        _profilerSumOut = 0f;
        _profilerFrames = 0;
        _profilerClips = 0;
        _profilerPeakMz = 0f;
        _profilerPeakFx = 0f;
        _profilerPeakFy = 0f;

        _isLiveMonitoring = true;
        _profilerSamples.Clear();
        CoachMessages.Clear();
        CoachSessionState = CoachSessionState.Analyzing;
        CoachMessages.Add(new CoachMessage
        {
            Text = "🎯 Live Monitor active — drive normally for about 20 seconds while I collect data across 4 sampling windows. I'll analyze the trends and give you targeted recommendations.",
            Icon = "🎯"
        });
        CoachMessages.Add(new CoachMessage
        {
            Text = "No need to stop — just keep driving. I'll alert you when the analysis is ready.",
            Icon = "⏳"
        });
    }

    private async Task ProcessMonitorSamplesAsync()
    {
        _isLiveMonitoring = false;
        var samples = _profilerSamples.ToList();
        _profilerSamples.Clear();

        if (CoachMessages.Count > 0)
            CoachMessages.RemoveAt(CoachMessages.Count - 1);
        CoachMessages.Add(new CoachMessage
        {
            Text = $"✅ Collected {samples.Count} samples over ~{samples.Count * ProfilerStatsWindow / 60}s. Analyzing with AI...",
            Icon = "📊"
        });

        string statsText = string.Join("\n\n", samples.Select((s, i) =>
            $"=== Sample {i + 1} ===\n" +
            $"Window: {s.FrameCount} frames\n" +
            $"OutputMin: {s.OutputMin:F6}\n" +
            $"OutputMax: {s.OutputMax:F6}\n" +
            $"OutputAvg: {s.OutputAvg:F6}\n" +
            $"ClippingPct: {s.ClippingPercent:F1}%\n" +
            $"PeakMz: {s.PeakMz:F4}\n" +
            $"PeakFx: {s.PeakFx:F4}\n" +
            $"PeakFy: {s.PeakFy:F4}"));

        var csvData = new SnapshotCsvData
        {
            ProfileName = SelectedProfile?.Name ?? "Live",
            TorqueNm = 5.5f,
            StatsText = statsText
        };

        CoachSessionState = CoachSessionState.Analyzing;
        var result = await _coachService.AnalyzeSnapshotAsync(csvData);
        CoachSessionState = result.State;
        CoachDataSourceLabel = "Live Monitor — Temporal";

        foreach (var msg in result.Messages)
            CoachMessages.Add(msg);
            foreach (var rec in result.Recommendations ?? [])
                AddPendingRec(rec);
            if (_coachService.IsAiEnabled)
                PlayCoachAlert();
    }

    private async Task CoachLoadSnapshotFile(string filePath)
    {
        CoachLoadingText = "Analyzing snapshot...";
        CoachIsBusy = true;
        try
        {
            CoachMessages.Clear();
            var csvData = SnapshotFileLoader.ParseCsvData(filePath);
            if (csvData == null)
            {
                CoachMessages.Add(new CoachMessage { Text = "Could not parse snapshot file.", Icon = "⚠️" });
                return;
            }

            CoachSessionState = CoachSessionState.Analyzing;
            var result = await _coachService.AnalyzeSnapshotAsync(csvData);
            CoachSessionState = result.State;
            CoachDataSourceLabel = _coachService.DataSourceLabel;
            CoachCurrentProfileName = _coachService.CurrentProfileName;

            foreach (var msg in result.Messages)
                CoachMessages.Add(msg);
            foreach (var rec in result.Recommendations ?? [])
                AddPendingRec(rec);
            if (_coachService.IsAiEnabled)
                PlayCoachAlert();
        }
        finally
        {
            CoachIsBusy = false;
        }
    }

    [RelayCommand]
    private async Task CoachUseLatestSnapshot()
    {
        CoachLoadingText = "Analyzing snapshot...";
        CoachIsBusy = true;
        try
        {
            CoachMessages.Clear();
            var files = SnapshotFileLoader.LoadSnapshotFiles();
            if (files.Count == 0)
            {
                CoachMessages.Add(new CoachMessage { Text = "No saved snapshots found. Take a snapshot first (wheel button or Telemetry page), or use live data.", Icon = "📭" });
                return;
            }

            var csvData = SnapshotFileLoader.ParseCsvData(files[0].FilePath);
            if (csvData == null)
            {
                CoachMessages.Add(new CoachMessage { Text = "Could not parse the snapshot file.", Icon = "⚠️" });
                return;
            }

            CoachSessionState = CoachSessionState.Analyzing;
            var result = await _coachService.AnalyzeSnapshotAsync(csvData);
            CoachSessionState = result.State;
            CoachDataSourceLabel = _coachService.DataSourceLabel;
            CoachCurrentProfileName = _coachService.CurrentProfileName;

            foreach (var msg in result.Messages)
                CoachMessages.Add(msg);
            foreach (var rec in result.Recommendations ?? [])
                AddPendingRec(rec);
            if (_coachService.IsAiEnabled)
                PlayCoachAlert();
        }
        finally
        {
            CoachIsBusy = false;
        }
    }

    [RelayCommand]
    private async Task CoachUseLiveData()
    {
        CoachLoadingText = "Analyzing live data...";
        CoachIsBusy = true;
        try
        {
            CoachMessages.Clear();

            if (_profilerFrames < 30)
            {
                CoachMessages.Add(new CoachMessage { Text = "Not enough live telemetry data yet. Make sure the game is running with FFB output active, then try again after a few seconds of driving.", Icon = "📡" });
                return;
            }

            int frames = _profilerFrames;
            var stats = new LiveProfilerStats
            {
                OutputMin = _profilerMinOut,
                OutputMax = _profilerMaxOut,
                OutputAvg = _profilerSumOut / frames,
                ClippingPercent = (float)_profilerClips / frames * 100f,
                PeakMz = _profilerPeakMz,
                PeakFx = _profilerPeakFx,
                PeakFy = _profilerPeakFy,
                FrameCount = frames,
                ClipCount = _profilerClips
            };

            CoachSessionState = CoachSessionState.Analyzing;
            var result = await _coachService.AnalyzeLiveDataAsync(stats, SelectedProfile?.Name ?? "Live");
            CoachSessionState = result.State;
            CoachDataSourceLabel = _coachService.DataSourceLabel;
            CoachCurrentProfileName = _coachService.CurrentProfileName;

            foreach (var msg in result.Messages)
                CoachMessages.Add(msg);
            foreach (var rec in result.Recommendations ?? [])
                AddPendingRec(rec);
            if (_coachService.IsAiEnabled)
                PlayCoachAlert();
        }
        finally
        {
            CoachIsBusy = false;
        }
    }


    [RelayCommand]
    private void CoachRestart()
    {
        _coachService.Reset();
        CoachMessages.Clear();
        CoachPendingRecs.Clear();
        CoachHasPendingRecs = false;
        CoachSessionState = CoachSessionState.Idle;
        CoachDataSourceLabel = "";
        CoachCurrentProfileName = "";

        foreach (var msg in _coachService.BuildWelcomeMessages())
            CoachMessages.Add(msg);
        CoachSessionState = CoachSessionState.SelectingSource;
    }

    [ObservableProperty]
    private string _coachInput = "";

    [RelayCommand]
    private async Task CoachSendText()
    {
        var text = CoachInput?.Trim();
        if (string.IsNullOrWhiteSpace(text)) return;
        CoachInput = "";

        CoachMessages.Add(new CoachMessage
        {
            Text = text,
            IsUser = true,
            Icon = "👤"
        });

        if (_coachService.IsAiEnabled && _coachService.HasActiveConversation)
        {
            await ProcessAiCustomText(text);
        }
        else
        {
            var history = CoachMessages.ToList();
            var result = await _coachService.ProcessAnswerAsync(text, history);
            CoachSessionState = result.State;
            foreach (var msg in result.Messages)
                CoachMessages.Add(msg);
        }
    }

    private async Task ProcessAiCustomText(string text)
    {
        CoachLoadingText = "Thinking...";
        CoachIsBusy = true;
        try
        {
            var history = CoachMessages.Take(CoachMessages.Count - 1).ToList();
            _coachService.SetCustomInput(text);
            var result = await _coachService.ProcessAnswerAsync("__custom__", history);
            CoachSessionState = result.State;
            foreach (var msg in result.Messages)
                CoachMessages.Add(msg);
            foreach (var rec in result.Recommendations ?? [])
                AddPendingRec(rec);
            if (_coachService.IsAiEnabled)
                PlayCoachAlert();
        }
        finally
        {
            CoachIsBusy = false;
        }
    }

    [ObservableProperty]
    private string _openAiApiKey = "";

    partial void OnOpenAiApiKeyChanged(string value)
    {
        _appSettings.OpenAiApiKey = string.IsNullOrWhiteSpace(value) ? null : value;
        _appSettings.Save();
        RebuildAiCoach();
    }

    [ObservableProperty]
    private string _openAiModel = "deepseek-v4-flash";

    partial void OnOpenAiModelChanged(string value)
    {
        _appSettings.OpenAiModel = value;
        _appSettings.Save();
        if (_aiAnalyzer != null)
            _aiAnalyzer.Model = value;
    }

    [ObservableProperty]
    private string _aiBaseUrl = "https://opencode.ai/zen/go/v1";

    partial void OnAiBaseUrlChanged(string value)
    {
        _appSettings.AiBaseUrl = value;
        _appSettings.Save();
        if (_aiAnalyzer != null)
            _aiAnalyzer.BaseUrl = value;
    }

    [RelayCommand]
    private async Task CoachAnswer(string answerId)
    {
        if (answerId == "source_latest")
        {
            await CoachUseLatestSnapshot();
            return;
        }
        if (answerId == "source_live")
        {
            await CoachUseLiveData();
            return;
        }
        if (answerId == "source_monitor")
        {
            CoachStartLiveMonitor();
            return;
        }
        if (answerId == "source_pick")
        {
            ShowSnapshotPicker();
            return;
        }
        if (answerId == "go_back")
        {
            CoachRestart();
            return;
        }
        if (answerId.StartsWith("snap_"))
        {
            await CoachLoadSnapshotFile(answerId["snap_".Length..]);
            return;
        }

        CoachLoadingText = "Thinking...";
        CoachIsBusy = true;
        try
        {
            var history = CoachMessages.ToList();
            var result = await _coachService.ProcessAnswerAsync(answerId, history);
            CoachSessionState = result.State;
            CoachDataSourceLabel = _coachService.DataSourceLabel;
            foreach (var msg in result.Messages)
                CoachMessages.Add(msg);
            foreach (var rec in result.Recommendations ?? [])
                AddPendingRec(rec);
            if (_coachService.IsAiEnabled)
                PlayCoachAlert();
        }
        finally
        {
            CoachIsBusy = false;
        }
    }

    public bool HasOpenAiKey => !string.IsNullOrWhiteSpace(OpenAiApiKey);

    private FfbAIAnalyzer? _aiAnalyzer;

    private void RebuildAiCoach()
    {
        _aiAnalyzer?.Dispose();
        _aiAnalyzer = null;

        var apiKey = _appSettings.OpenAiApiKey ?? "";
        var baseUrl = !string.IsNullOrWhiteSpace(_appSettings.AiBaseUrl)
            ? _appSettings.AiBaseUrl
            : "https://opencode.ai/zen/go/v1";
        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            _aiAnalyzer = new FfbAIAnalyzer(apiKey, _appSettings.OpenAiModel, baseUrl);
            _coachService.SetAiAnalyzer(_aiAnalyzer);
        }
        else
        {
            _coachService.SetAiAnalyzer(null);
        }

        OnPropertyChanged(nameof(HasOpenAiKey));
    }

    private void InitializeAiCoach()
    {
        var apiKey = _appSettings.OpenAiApiKey ?? "";
        var baseUrl = !string.IsNullOrWhiteSpace(_appSettings.AiBaseUrl)
            ? _appSettings.AiBaseUrl
            : "https://opencode.ai/zen/go/v1";
        OpenAiApiKey = apiKey;
        OpenAiModel = _appSettings.OpenAiModel;
        AiBaseUrl = baseUrl;
        RebuildAiCoach();

        // AI coach logs go to file only -- not the status bar (too verbose, contains JSON)
    }

    public void Dispose()
    {
        _uiUpdateTimer.Stop();
        _voiceService.Dispose();
        _gameRecordingService.Dispose();
        _discordPresence.Dispose();
        _telemetryLoop.Dispose();
        _deviceManager.Dispose();
        _reader.Dispose();
        _aiAnalyzer?.Dispose();
    }

public sealed class CoachPendingRec : System.ComponentModel.INotifyPropertyChanged
{
    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    private void Notify(string prop) => PropertyChanged?.Invoke(this, new(prop));

    string _label = "";
    public string Label { get => _label; set { _label = value; Notify(nameof(Label)); } }

    string _description = "";
    public string Description { get => _description; set { _description = value; Notify(nameof(Description)); } }

    public string Parameter { get; set; } = "";
    public float CurrentValue { get; set; }
    public float SuggestedValue { get; set; }
    public string Reason { get; set; } = "";

    public FfbRecommendation ToFfbRecommendation() => new()
    {
        Type = RecommendationType.ProfileChange,
        Parameter = Parameter,
        CurrentValue = CurrentValue,
        SuggestedValue = SuggestedValue,
        Reason = Reason,
        Impact = Description
    };
}
}
