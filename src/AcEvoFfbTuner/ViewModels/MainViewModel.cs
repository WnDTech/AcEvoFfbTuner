using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
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

public enum SupportedGame
{
    AcEvo,
    Raceroom,
    AssettoCorsa
}

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
    private volatile bool _autoAlignInProgress;
    private volatile bool _mapClearedByUser;
    private string? _lastAutoLoadedTrack;
    private RaceInfoOverlay? _raceInfoOverlay;
    private readonly RaceInfoProcessor _raceInfoProcessor = new();
    private readonly DiscordPresenceService _discordPresence = new();

    public string AppVersion { get; } =
        System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0.0";

    [ObservableProperty]
    private SupportedGame _selectedGame = SupportedGame.AcEvo;

    public string GameDisplayName => SelectedGame switch
    {
        SupportedGame.Raceroom => "RaceRoom",
        SupportedGame.AssettoCorsa => "Assetto Corsa",
        _ => "AC EVO"
    };

    public bool IsAcEvo => SelectedGame == SupportedGame.AcEvo;
    public bool IsRaceroom => SelectedGame == SupportedGame.Raceroom;
    public bool IsAssettoCorsa => SelectedGame == SupportedGame.AssettoCorsa;

    public int SelectedGameIndex
    {
        get => (int)SelectedGame;
        set => SelectedGame = (SupportedGame)value;
    }

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

    public bool IsBuiltInProfileSelected => SelectedProfile?.IsBuiltIn ?? false;

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

    partial void OnSnapshotButtonComboIndexChanged(int value)
    {
        SnapshotButtonIndex = value <= 0 ? -1 : value - 1;
        _appSettings.SnapshotButtonComboIndex = value;
        if (!_suppressSettingsSave)
            _appSettings.Save();
    }

    partial void OnPanicButtonComboIndexChanged(int value)
    {
        _appSettings.PanicButtonComboIndex = value;
        _appSettings.Save();
    }

    partial void OnIsPerCarAutoLoadEnabledChanged(bool value)
    {
        _appSettings.PerCarAutoLoadEnabled = value;
        _appSettings.Save();
    }

    private bool[]? _prevSnapshotButtons;
    private bool[]? _prevPanicButtons;

    private void PollSnapshotButton()
    {
        if (!IsDeviceConnected && !IsAssigningSnapshotButton) return;

        var buttons = _deviceManager.PollButtons();
        if (buttons == null)
        {
            ButtonDetectionText = "";
        }
        else
        {
            var pressed = new System.Text.StringBuilder();
            for (int i = 0; i < buttons.Length; i++)
            {
                if (buttons[i])
                {
                    if (pressed.Length > 0) pressed.Append(", ");
                    string btnName = i < SnapshotButtonNames.Count - 1
                        ? SnapshotButtonNames[i + 1]
                        : $"Btn{i + 1}";
                    pressed.Append(btnName);
                }
            }

            ButtonDetectionText = pressed.Length > 0 ? $"Pressed: {pressed}" : "";

            if (IsAssigningSnapshotButton)
            {
                for (int i = 0; i < buttons.Length; i++)
                {
                    bool prev = _prevSnapshotButtons != null && i < _prevSnapshotButtons.Length && _prevSnapshotButtons[i];
                    if (buttons[i] && !prev)
                    {
                        SnapshotButtonComboIndex = i + 1;
                        IsAssigningSnapshotButton = false;
                        string name = i < SnapshotButtonNames.Count - 1 ? SnapshotButtonNames[i + 1] : $"Button {i + 1}";
                        SnapshotAssignStatus = $"Assigned: {name}";
                        break;
                    }
                }
                _prevSnapshotButtons = (bool[])buttons.Clone();
            }
            else if (SnapshotButtonIndex >= 0 && SnapshotButtonIndex < buttons.Length)
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
                        _telemetryLoop.LiveServer.TriggerSnapshot();
                        _voiceService.Speak("Snapshot saved");
                    }
                }
            }
        }

        PollPanicButton();
    }

    private void PollPanicButton()
    {
        if (PanicButtonIndex < 0 && !IsAssigningPanicButton) return;

        var buttons = _deviceManager.PollSecondaryButtons();
        if (buttons == null) return;

        if (IsAssigningPanicButton)
        {
            for (int i = 0; i < buttons.Length; i++)
            {
                bool prev = _prevPanicButtons != null && i < _prevPanicButtons.Length && _prevPanicButtons[i];
                if (buttons[i] && !prev)
                {
                    PanicButtonComboIndex = i + 1;
                    IsAssigningPanicButton = false;
                    string name = i < PanicButtonNames.Count - 1 ? PanicButtonNames[i + 1] : $"Button {i + 1}";
                    PanicAssignStatus = $"Assigned: {name}";
                    break;
                }
            }
            _prevPanicButtons = (bool[])buttons.Clone();
            return;
        }

        if (PanicButtonIndex < 0 || PanicButtonIndex >= buttons.Length) return;

        bool pressed = buttons[PanicButtonIndex];
        bool wasPressed = _prevPanicButtons != null && PanicButtonIndex < _prevPanicButtons.Length && _prevPanicButtons[PanicButtonIndex];
        _prevPanicButtons = (bool[])buttons.Clone();

        if (pressed && !wasPressed)
            PanicStop();
    }

    public ObservableCollection<FfbDeviceInfo> AvailableDevices { get; } = new();
    public ObservableCollection<FfbDeviceInfo> PanicDevices { get; } = new();
    public ObservableCollection<FfbProfile> Profiles { get; } = new();
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

    public int PanicButtonIndex => PanicButtonComboIndex <= 0 ? -1 : PanicButtonComboIndex - 1;
    public int PanicDeviceButtonCount => _deviceManager.SecondaryButtonCount;

    public FfbPipeline Pipeline => _pipeline;
    public FfbDeviceManager DeviceManager => _deviceManager;
    public TelemetryLoop TelemetryLoop => _telemetryLoop;
    public int DeviceButtonCount => _deviceManager.ButtonCount;

    private readonly DispatcherTimer _uiUpdateTimer;

    public MainViewModel()
    {
        _reader = CreateReader(SelectedGame);
        _pipeline = CreatePipeline(SelectedGame);
        _deviceManager = new FfbDeviceManager();
        _telemetryLoop = new TelemetryLoop(_reader, _pipeline, _deviceManager);
        _profileManager = new ProfileManager();

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

                if (!_autoAlignInProgress && SatelliteMapService.LoadCalibration(trackName) == null)
                    _ = AutoAlignTrackAsync(trackName);
            }

            if (!string.IsNullOrEmpty(trackName))
                StatusText = $"Connected — Track: {trackName} ({config}) {lengthM:F0}m";

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

            if (!IsPerCarAutoLoadEnabled) return;

            var match = Profiles.FirstOrDefault(p =>
                !string.IsNullOrEmpty(p.CarMatch) &&
                p.CarMatch.Equals(carModel, StringComparison.OrdinalIgnoreCase));

            if (match != null && (SelectedProfile == null || match.Name != SelectedProfile.Name))
            {
                SelectedProfile = match;
                match.ApplyToPipeline(_pipeline);
                match.ApplyToStaticFriction(_telemetryLoop.StaticFriction);
                LoadProfileValues(match);
                _profileManager.SetActiveProfile(match);
                StatusText = $"Auto-loaded profile '{match.Name}' for {carModel}";
                _voiceService.Speak("Loaded profile for {0}", carModel);
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

        AddSystemLog("Application initialized");
     }

    private static ISharedMemoryReader CreateReader(SupportedGame game) => game switch
    {
        SupportedGame.Raceroom => new RaceroomSharedMemoryReader(),
        SupportedGame.AssettoCorsa => new SharedMemoryReader(),
        _ => new SharedMemoryReader()
    };

    private static FfbPipeline CreatePipeline(SupportedGame game) => game switch
    {
        SupportedGame.Raceroom => new R3eFfbPipeline(),
        SupportedGame.AssettoCorsa => new AcFfbPipeline(),
        _ => new FfbPipeline()
    };

    partial void OnSelectedGameChanged(SupportedGame value)
    {
        var wasRunning = _telemetryLoop.IsRunning;
        if (wasRunning)
            _telemetryLoop.Stop();

        _discordPresence.Detach();
        _telemetryLoop.Dispose();
        _reader.Dispose();
        _reader = CreateReader(value);
        _pipeline = CreatePipeline(value);
        OnPropertyChanged(nameof(GameDisplayName));
        OnPropertyChanged(nameof(IsAcEvo));
        OnPropertyChanged(nameof(IsRaceroom));
        OnPropertyChanged(nameof(IsAssettoCorsa));

        // Re-apply active profile settings to the new pipeline
        if (_profileManager.ActiveProfile != null)
            _profileManager.ActiveProfile.ApplyToPipeline(_pipeline);

        var newLoop = new TelemetryLoop(_reader, _pipeline, _deviceManager);
        WireTelemetryLoopEvents(newLoop);
        _telemetryLoop = newLoop;

        if (wasRunning)
        {
            _telemetryLoop.Start();
            StatusText = $"Switched to {GameDisplayName} — telemetry restarted";
        }
        else
        {
            StatusText = $"Switched to {GameDisplayName}";
        }

        AddSystemLog($"Game source changed to {GameDisplayName}");
    }

    private void WireTelemetryLoopEvents(TelemetryLoop newLoop)
    {
        newLoop.StatusChanged += status => Application.Current?.Dispatcher.Invoke(() => StatusText = status);
        newLoop.GameConnectionChanged += () => Application.Current?.Dispatcher.Invoke(() =>
        {
            IsGameConnected = newLoop.IsGameConnected;
            if (IsGameConnected)
                AddSystemLog($"Game connected ({GameDisplayName})");
            else
            {
                _gameRecordingService.StopRecording();
                IsScreenRecording = false;
                AddSystemLog("Game disconnected");
            }
        });
        newLoop.TrackMapCompleted += map => Application.Current?.Dispatcher.Invoke(() =>
        {
            IsTrackMapAvailable = true;
            IsTrackMapRecording = false;
            TrackLengthM = map.TrackLengthM;
            TrackWaypointCount = map.Waypoints.Count;
            CornerCount = map.Corners.Count;
            SectorCount = map.Sectors.Count;
            if (string.IsNullOrEmpty(map.TrackName) || map.TrackName.StartsWith("track_"))
                map.TrackName = newLoop.DetectedTrackName;
            DetectedTrackName = map.TrackName;
            IsPitDetected = map.PitLane.IsDetected;
            TrackMapStatus = $"{map.Waypoints.Count} pts | {map.TrackLengthM:F0}m | {map.Corners.Count} corners | {map.Sectors.Count} sectors";
        });
        newLoop.StaticDataReceived += (trackName, config, lengthM, latitude, longitude) => Application.Current?.Dispatcher.Invoke(() =>
        {
            DetectedTrackName = trackName;
            if (!string.IsNullOrEmpty(trackName))
                StatusText = $"Connected — Track: {trackName} ({config}) {lengthM:F0}m";
        });
        newLoop.CarModelChanged += carModel => Application.Current?.Dispatcher.Invoke(() =>
        {
            DetectedCarModel = carModel;
            StatusText = $"Car detected: {carModel}";
        });
        newLoop.TrackChanged += newTrackName => Application.Current?.Dispatcher.Invoke(() =>
        {
            _mapClearedByUser = false;
            _lastAutoLoadedTrack = null;
            IsTrackMapAvailable = false;
            IsTrackMapRecording = false;
            StatusText = $"Track changed to: {newTrackName}";
        });
        _discordPresence.Attach(newLoop);
    }

    private async Task AutoAlignTrackAsync(string trackName)
    {
        _autoAlignInProgress = true;
        try
        {
            await Task.Delay(2000);

            var map = _telemetryLoop.MapBuilder.CurrentMap ?? TrackMap.Load(trackName);
            if (map == null || map.Waypoints.Count < 10) return;

            if (SatelliteMapService.LoadCalibration(trackName) != null) return;

            var alignment = await TrackAlignmentService.ComputeAlignmentAsync(trackName, map.Waypoints);
            if (alignment == null) return;

            SatelliteMapService.SaveCalibration(trackName,
                (float)alignment.CenterLat, (float)alignment.CenterLon, (float)alignment.RotationDeg);

            Application.Current?.Dispatcher.Invoke(() =>
            {
                TrackLatitude = (float)alignment.CenterLat;
                TrackLongitude = (float)alignment.CenterLon;
                TrackRotation = (float)alignment.RotationDeg;
            });
        }
        catch { }
        finally
        {
            _autoAlignInProgress = false;
        }
    }

    public void Initialize()
    {
        _profileManager.AutoMigrate = _appSettings.AutoProfileUpgrade;
        _profileManager.Initialize();
        RefreshProfiles();
        RefreshDevices();
        RestoreButtonSettings();

        if (_profileManager.ActiveProfile != null)
        {
            _profileManager.ActiveProfile.ApplyToPipeline(_pipeline);
            _profileManager.ActiveProfile.ApplyToStaticFriction(_telemetryLoop.StaticFriction);
            LoadProfileValues(_profileManager.ActiveProfile);
            SelectedProfile = Profiles.FirstOrDefault(p => p.Name == _profileManager.ActiveProfile.Name);
        }

        LoadSystemLog();

        _ = CheckForUpdatesAsync();
        _ = EnsureFfmpegAsync();
    }

    private async Task EnsureFfmpegAsync()
    {
        if (Services.FfmpegDownloader.IsInstalled)
        {
            IsFfmpegReady = true;
            FfmpegStatusText = "FFmpeg ready";
            return;
        }

        IsFfmpegDownloading = true;
        FfmpegStatusText = "Downloading FFmpeg...";

        var result = await Services.FfmpegDownloader.EnsureFfmpegAsync(
            new Progress<(int percent, string message)>(p =>
            {
                Application.Current?.Dispatcher.Invoke(() =>
                {
                    FfmpegStatusText = p.message;
                });
            }));

        Application.Current?.Dispatcher.Invoke(() =>
        {
            IsFfmpegDownloading = false;
            if (result != null)
            {
                IsFfmpegReady = true;
                FfmpegStatusText = "FFmpeg ready";
            }
            else
            {
                FfmpegStatusText = "FFmpeg unavailable — recording disabled";
            }
        });
    }

    [RelayCommand]
    private void RefreshDevices()
    {
        try
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
        catch (Exception ex)
        {
            StatusText = $"Device enumeration failed: {ex.Message}";
        }
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
            ForceInvertEnabled = _deviceManager.AutoDetectedForceInvert;
            IsAutoSetupAvailable = WheelMaxTorqueNm > 0;
            RefreshSnapshotButtonNames();
            RefreshPanicButtonNames();
            RestoreButtonSettings();
            if (!_uiUpdateTimer.IsEnabled)
                _uiUpdateTimer.Start();
            _telemetryLoop.AutoDetectAndSetProvider();
            RefreshProviderFeatures();
            string providerInfo = _telemetryLoop.ActiveProviderName != "DirectInput (Built-in)"
                ? $" | Provider: {_telemetryLoop.ActiveProviderName}"
                : "";
            string ledStatus = _deviceManager.IsLedControllerConnected
                ? $" | LEDs: {_deviceManager.LedControllerVendor}"
                : $" | LEDs: {_deviceManager.LedDiagnosticInfo.Split('\n').LastOrDefault() ?? "not found"}";
            string vibStatus = _deviceManager.SupportsPeriodicEffects
                ? ""
                : " | WARNING: wheel does not report periodic effect support — kerb/slip vibration may not work";
            StatusText = (_deviceManager.LastError ?? $"Connected to {SelectedDevice.ProductName}") + providerInfo + ledStatus + vibStatus;
            UpdateLedCapabilities();
            PushLedConfig();
            _appSettings.LastConnectedDeviceInstanceId = SelectedDevice.DeviceInstance.InstanceGuid.ToString();
            _appSettings.Save();
            AddSystemLog($"Device connected: {SelectedDevice.ProductName}");
            _voiceService.Speak("Wheelbase connected");
        }
        else
        {
            StatusText = _deviceManager.LastError ?? "Failed to connect to device";
        }
    }

    [RelayCommand]
    private void DisconnectDevice()
    {
        _telemetryLoop.SetFfbProvider(null);
        RefreshProviderFeatures();
        _deviceManager.DisconnectDevice();
        IsDeviceConnected = false;
        IsAutoSetupAvailable = false;
        DeviceName = "No device";
        ResetLedCapabilities();
        _appSettings.LastConnectedDeviceInstanceId = null;
        _appSettings.Save();
        AddSystemLog("Device disconnected");
        _voiceService.Speak("Wheelbase disconnected");
    }

    partial void OnSelectedPanicDeviceChanged(FfbDeviceInfo? value)
    {
        _deviceManager.DisconnectSecondaryDevice();

        if (value == null || value.ProductName == "None")
        {
            OnPropertyChanged(nameof(PanicDeviceButtonCount));
            _appSettings.PanicDeviceInstanceId = null;
            _appSettings.Save();
            RefreshPanicButtonNames();
            return;
        }

        var window = Application.Current?.MainWindow;
        if (window != null)
        {
            var helper = new System.Windows.Interop.WindowInteropHelper(window);
            _deviceManager.SetWindowHandle(helper.Handle);
        }

        if (_deviceManager.TryConnectSecondaryDevice(value))
        {
            StatusText = $"Panic button device: {value.ProductName} ({_deviceManager.SecondaryButtonCount} buttons)";
            _appSettings.PanicDeviceInstanceId = value.DeviceInstance.InstanceGuid.ToString();
            _appSettings.Save();
            if (!_uiUpdateTimer.IsEnabled)
                _uiUpdateTimer.Start();
        }
        else
        {
            StatusText = "Failed to connect panic button device";
            _appSettings.PanicDeviceInstanceId = null;
            _appSettings.Save();
        }

        OnPropertyChanged(nameof(PanicDeviceButtonCount));
        RefreshPanicButtonNames();
        RestoreButtonSettings();
    }

    private void RefreshSnapshotButtonNames()
    {
        int savedIndex = SnapshotButtonComboIndex;
        _suppressSettingsSave = true;
        try
        {
            var names = _deviceManager.GetButtonNames();
            SnapshotButtonNames.Clear();
            SnapshotButtonNames.Add("Disabled");
            foreach (var name in names)
                SnapshotButtonNames.Add(name);
        }
        finally
        {
            _suppressSettingsSave = false;
            SnapshotButtonComboIndex = savedIndex;
        }
    }

    private void RefreshPanicButtonNames()
    {
        var names = _deviceManager.GetSecondaryButtonNames();
        PanicButtonNames.Clear();
        PanicButtonNames.Add("Disabled");
        foreach (var name in names)
            PanicButtonNames.Add(name);
    }

    private static void PopulateFallbackButtonNames(ObservableCollection<string> collection, int count)
    {
        collection.Clear();
        collection.Add("Disabled");
        for (int i = 1; i <= count; i++)
            collection.Add($"Button {i}");
    }

    [RelayCommand]
    private void AssignSnapshotButton()
    {
        if (!IsDeviceConnected)
        {
            StatusText = "Connect a wheel device first";
            return;
        }
        IsAssigningSnapshotButton = true;
        SnapshotAssignStatus = "Listening... press a button on your wheel";
    }

    [RelayCommand]
    private void AssignPanicButton()
    {
        if (SelectedPanicDevice == null || SelectedPanicDevice.ProductName == "None")
        {
            StatusText = "Select a panic device first";
            return;
        }
        IsAssigningPanicButton = true;
        PanicAssignStatus = "Listening... press a button on the panic device";
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
        if (n.Contains("G27")) return 2.5f;
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

    [RelayCommand]
    private void AutoSetup()
    {
        if (!IsAutoSetupAvailable || !IsDeviceConnected) return;

        string deviceName = DeviceName;
        float torque = WheelMaxTorqueNm;
        if (torque <= 0) return;

        EvoDetectedSettings? evoSettings = null;
        var raw = _telemetryLoop.LatestRaw;
        if (raw != null)
        {
            evoSettings = EvoSettingsDetector.DetectFromRaw(raw);
            if (evoSettings != null)
                evoSettings = new EvoDetectedSettings
                {
                    FfbStrength = evoSettings.FfbStrength,
                    CarFfbMultiplier = evoSettings.CarFfbMultiplier,
                    SteerDegrees = evoSettings.SteerDegrees,
                    RecommendedOutputGain = evoSettings.RecommendedOutputGain,
                    RecommendedNormalizationScale = evoSettings.RecommendedNormalizationScale,
                    RecommendedSteeringLock = evoSettings.RecommendedSteeringLock,
                    IsValid = false
                };
        }

        var profile = WheelbaseAutoConfigurator.GenerateProfile(torque, deviceName, null);
        if (evoSettings != null)
            profile.SteeringLockDegrees = evoSettings.RecommendedSteeringLock;

        profile.Name = $"Auto Setup - {deviceName}";

        var existing = Profiles.FirstOrDefault(p => p.Name == profile.Name);
        if (existing != null)
            _profileManager.DeleteProfile(existing);

        _profileManager.SaveProfile(profile);
        RefreshProfiles();

        SelectedProfile = profile;
        profile.ApplyToPipeline(_pipeline);
        profile.ApplyToStaticFriction(_telemetryLoop.StaticFriction);
        LoadProfileValues(profile);
        _profileManager.SetActiveProfile(profile);

        var wheelType = WheelbaseAutoConfigurator.DetectWheelType(deviceName);
        AutoSetupStatus = $"Auto Setup — {deviceName} ({torque:F1}Nm, {wheelType})";
        StatusText = AutoSetupStatus;
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

        if (SelectedProfile.IsBuiltIn)
        {
            StatusText = $"\"{SelectedProfile.Name}\" is a built-in profile. Use Save As to create your own copy.";
            SaveAsNewProfile();
            return;
        }

        try
        {
            PushValuesToPipeline();
            SelectedProfile.LedEffects = LedEffectConfigDto.FromConfig(new LedEffectConfig
            {
                Brightness = LedBrightness,
                FlashRateTicks = LedFlashRate,
                AbsFlashEnabled = LedAbsFlashEnabled,
                FlagIndicatorsEnabled = LedFlagIndicatorsEnabled,
                ShiftLimiterFlashEnabled = LedShiftLimiterFlashEnabled,
                PitLimiterFlashEnabled = LedPitLimiterFlashEnabled,
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
            SelectedProfile.ForceInvertEnabled = ForceInvertEnabled;
            SelectedProfile.SteeringLockDegrees = SteeringLockDegrees;
            SelectedProfile.Hf8.OutputRateHz = Hf8OutputRateHz;
            SelectedProfile.LastTelemetrySnapshot = _telemetryLoop.CaptureTelemetrySnapshot();
            // Write static friction slider values back to profile before saving
            SelectedProfile.StaticFriction.Gain = StaticFrictionGain;
            SelectedProfile.StaticFriction.MaxElasticStretch = StaticFrictionMaxElasticStretch;
            SelectedProfile.StaticFriction.SpringStiffness = StaticFrictionSpringStiffness;
            SelectedProfile.StaticFriction.KineticFrictionBase = StaticFrictionKineticFrictionBase;
            SelectedProfile.StaticFriction.EngineOffDamping = StaticFrictionEngineOffDamping;
            SelectedProfile.StaticFriction.EngineOnDamping = StaticFrictionEngineOnDamping;
            SelectedProfile.StaticFriction.EngineOffScale = StaticFrictionEngineOffScale;
            SelectedProfile.StaticFriction.EngineOnScale = StaticFrictionEngineOnScale;
            SelectedProfile.StaticFriction.ActiveDecay = StaticFrictionActiveDecay;
            SelectedProfile.StaticFriction.ReturnDecay = StaticFrictionReturnDecay;
            SelectedProfile.StaticFriction.OutputSmoothAlpha = StaticFrictionOutputSmoothAlpha;
            _profileManager.SaveProfile(SelectedProfile);
            _profileManager.SetActiveProfile(SelectedProfile);
        }
        catch (Exception ex)
        {
            StatusText = $"Profile save failed: {ex.Message}";
        }
    }

    private void AutoSaveDiagnosticProfile()
    {
        try
        {
            PushValuesToPipeline();

            string baseName = SelectedProfile?.Name ?? "unsaved";
            string diagName = $"{baseName}_diag_{DateTime.Now:yyyyMMdd_HHmmss}";
            var profile = _profileManager.SaveProfileFromPipeline(_pipeline, diagName);
            profile.WheelMaxTorqueNm = WheelMaxTorqueNm;
            profile.ForceInvertEnabled = ForceInvertEnabled;
            profile.SteeringLockDegrees = SteeringLockDegrees;
            profile.Hf8.OutputRateHz = Hf8OutputRateHz;
            profile.LastTelemetrySnapshot = _telemetryLoop.CaptureTelemetrySnapshot();
            _profileManager.SaveProfile(profile);
        }
        catch { }
    }

    private void SaveProfileMetadata(FfbProfile profile)
    {
        profile.WheelMaxTorqueNm = WheelMaxTorqueNm;
        profile.ForceInvertEnabled = ForceInvertEnabled;
        profile.SteeringLockDegrees = SteeringLockDegrees;
        profile.Hf8.OutputRateHz = Hf8OutputRateHz;
        profile.LastTelemetrySnapshot = _telemetryLoop.CaptureTelemetrySnapshot();
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
            SaveProfileMetadata(profile);
            _profileManager.SaveProfile(profile);
            _profileManager.SetActiveProfile(profile);
            RefreshProfiles();
            SelectedProfile = profile;
        }
    }

    public void WizardSaveProfile(string name, ProfileScope scope)
    {
        PushValuesToPipeline();

        var profile = _profileManager.SaveProfileFromPipeline(_pipeline, name);
        profile.Scope = scope;
        profile.GameMatch = GameDisplayName;

        switch (scope)
        {
            case ProfileScope.PerCar:
                profile.CarMatch = DetectedCarModel ?? "";
                break;
            case ProfileScope.PerTrack:
                profile.TrackMatch = DetectedTrackName ?? "";
                break;
            case ProfileScope.PerCarAndTrack:
                profile.CarMatch = DetectedCarModel ?? "";
                profile.TrackMatch = DetectedTrackName ?? "";
                break;
        }

        SaveProfileMetadata(profile);
        _profileManager.SaveProfile(profile);
        _profileManager.SetActiveProfile(profile);
        RefreshProfiles();
        SelectedProfile = profile;
        StatusText = $"Setup profile '{name}' saved and active";
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
        if (SelectedProfile.IsBuiltIn)
        {
            StatusText = $"Cannot delete built-in profile \"{SelectedProfile.Name}\".";
            return;
        }
        _profileManager.DeleteProfile(SelectedProfile);
        RefreshProfiles();
        SelectedProfile = _profileManager.ActiveProfile;
    }

    [RelayCommand]
    private void SetCarMatchFromDetected()
    {
        if (string.IsNullOrEmpty(DetectedCarModel))
        {
            StatusText = "No car model detected yet — start a session in AC EVO first";
            return;
        }
        ProfileCarMatch = DetectedCarModel;
        if (SelectedProfile != null)
        {
            SelectedProfile.CarMatch = DetectedCarModel;
            _profileManager.SaveProfile(SelectedProfile);
            StatusText = $"Car Match set to '{DetectedCarModel}' — saved and will auto-load for that car";
        }
    }

    [RelayCommand]
    private void SetTrackMatchFromDetected()
    {
        if (string.IsNullOrEmpty(DetectedTrackName))
        {
            StatusText = "No track detected yet — start a session in AC EVO first";
            return;
        }
        ProfileTrackMatch = DetectedTrackName;
        if (SelectedProfile != null)
        {
            SelectedProfile.TrackMatch = DetectedTrackName;
            _profileManager.SaveProfile(SelectedProfile);
            StatusText = $"Track Match set to '{DetectedTrackName}' — saved";
        }
    }

    [RelayCommand]
    private void RenameProfile()
    {
        if (SelectedProfile == null) return;
        if (SelectedProfile.IsBuiltIn)
        {
            StatusText = $"Cannot rename built-in profile \"{SelectedProfile.Name}\". Use Save As to create a renamed copy.";
            return;
        }

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
    private void DismissGameFfbWarning()
    {
        ShowGameFfbWarning = false;
        _ffbWarningDismissed = true;
    }

    private bool _conflictingAppsWarningDismissed;

    [RelayCommand]
    private void DismissConflictingAppsWarning()
    {
        ShowConflictingAppsWarning = false;
        _conflictingAppsWarningDismissed = true;
    }

    private int _conflictingAppsCheckCounter;

    private void CheckConflictingApps()
    {
        _conflictingAppsCheckCounter++;
        if (_conflictingAppsCheckCounter % 30 != 0 && !ShowConflictingAppsWarning) return;

        var result = Services.ConflictingAppDetector.Detect();
        if (result.HasConflicts)
        {
            ConflictingAppsNames = string.Join(", ", result.DetectedApps.Select(a => a.DisplayName));
            var sb = new System.Text.StringBuilder();
            foreach (var app in result.DetectedApps)
                sb.AppendLine($"  • {app.DisplayName} — {app.Reason}");
            ConflictingAppsDetail = sb.ToString();

            if (!_conflictingAppsWarningDismissed)
            {
                ShowConflictingAppsWarning = true;
                AddSystemLog($"Conflicting FFB apps detected: {ConflictingAppsNames}");
            }
        }
        else
        {
            ShowConflictingAppsWarning = false;
            ConflictingAppsNames = "";
            ConflictingAppsDetail = "";
            _conflictingAppsWarningDismissed = false;
        }
    }

    [RelayCommand]
    private void PanicStop()
    {
        IsRunning = false;
        _telemetryLoop.Stop();
        _uiUpdateTimer.Stop();
        _deviceManager.ZeroForce();
        StatusText = "PANIC STOP - Telemetry stopped, FFB zeroed";
    }

    [RelayCommand(CanExecute = nameof(CanTestHaptics))]
    private async Task TestHaptics()
    {
        if (IsHapticTestRunning) return;
        IsHapticTestRunning = true;
        TestHapticsCommand.NotifyCanExecuteChanged();

        try
        {
            var provider = _telemetryLoop.ActiveProvider;
            const int durationMs = 500;
            const double frequencyHz = 50.0;
            const double amplitude = 0.5;
            int stepMs = 2;
            int steps = durationMs / stepMs;

            StatusText = "Haptic test: 50Hz sine wave (500ms)...";

            if (provider is FanatecProvider fanatec)
                fanatec.TriggerAbsRumble(true);

            for (int i = 0; i < steps; i++)
            {
                double t = i * stepMs / 1000.0;
                float signal = (float)(amplitude * Math.Sin(2.0 * Math.PI * frequencyHz * t));
                float absSignal = (float)Math.Abs(signal);

                if (provider != null && provider.IsInitialized)
                {
                    provider.UpdateTorque(signal);
                    var haptic = new HapticData { VibrationIntensity = absSignal };
                    provider.SetHaptics(haptic);
                }
                else
                {
                    _deviceManager.SendConstantForce(signal);
                    _deviceManager.SetTargetVibration(absSignal);
                }

                await Task.Delay(stepMs);
            }

            if (provider != null && provider.IsInitialized)
            {
                provider.UpdateTorque(0f);
                provider.SetHaptics(new HapticData());
            }
            else
            {
                _deviceManager.SendConstantForce(0f);
                _deviceManager.SetTargetVibration(0f);
            }

            if (provider is FanatecProvider fanatec2)
                fanatec2.TriggerAbsRumble(false);

            StatusText = "Haptic test complete";
        }
        catch (Exception ex)
        {
            StatusText = $"Haptic test failed: {ex.Message}";
        }
        finally
        {
            IsHapticTestRunning = false;
            TestHapticsCommand.NotifyCanExecuteChanged();
        }
    }

    private bool CanTestHaptics => IsDeviceConnected && !IsHapticTestRunning;

    private void RefreshProviderFeatures()
    {
        ActiveFeatures.Clear();
        var provider = _telemetryLoop.ActiveProvider;

        if (provider == null)
        {
            HalProviderName = "None";
            IsHalSdkConnected = false;
            IsHalHapticEngineActive = false;
            IsHalPeripheralSynced = false;
            return;
        }

        ActiveFeatures.Add(provider.ProviderName);
        HalProviderName = provider.ProviderName;
        IsHalSdkConnected = provider.IsInitialized;
        IsHalHapticEngineActive = provider.IsInitialized && provider.IsAvailable;
        IsHalPeripheralSynced = IsDeviceConnected && provider.IsInitialized;

        if (provider is FanatecProvider fp)
        {
            if (fp.IsFullForceAvailable) ActiveFeatures.Add("FullForce Active");
            if (fp.HasRimRevLeds) ActiveFeatures.Add("Rev LEDs");
            if (fp.HasRumbleMotors) ActiveFeatures.Add("Rim Rumble");
            if (fp.HasRimLedDisplay) ActiveFeatures.Add("Gear Display");
            if (fp.MaxTorqueNm > 0) ActiveFeatures.Add($"Torque Capped {fp.MaxTorqueNm}Nm");
            if (fp.IsMauriceDetected) ActiveFeatures.Add("Maurice");
        }
        else if (provider is GenericDirectInputProvider)
        {
            ActiveFeatures.Add("DirectInput Only");
        }
        else
        {
            ActiveFeatures.Add("SDK Pending");
        }
    }

    private void AddSystemLog(string entry)
    {
        var timestamp = DateTime.Now.ToString("HH:mm:ss");
        var formatted = $"[{timestamp}] {entry}";
        SystemLogEntries.Add(formatted);
        while (SystemLogEntries.Count > 500)
            SystemLogEntries.RemoveAt(0);

        RecentSystemLogEntries.Clear();
        if (SystemLogEntries.Count > 0)
            RecentSystemLogEntries.Add(SystemLogEntries[^1]);

        SaveSystemLog();
    }

    private void SaveSystemLog()
    {
        try
        {
            var dir = Path.GetDirectoryName(SystemLogFilePath)!;
            Directory.CreateDirectory(dir);
            var json = JsonSerializer.Serialize(SystemLogEntries.ToList());
            File.WriteAllText(SystemLogFilePath, json);
        }
        catch { }
    }

    private void LoadSystemLog()
    {
        try
        {
            if (!File.Exists(SystemLogFilePath)) return;
            var json = File.ReadAllText(SystemLogFilePath);
            var entries = JsonSerializer.Deserialize<List<string>>(json);
            if (entries == null) return;
            SystemLogEntries.Clear();
            foreach (var e in entries)
                SystemLogEntries.Add(e);

            RecentSystemLogEntries.Clear();
            if (SystemLogEntries.Count > 0)
            {
                RecentSystemLogEntries.Add(SystemLogEntries[0]);
            }
        }
        catch { }
    }

    private void UpdateSignalMonitor(float lowFreqValue, float highFreqValue)
    {
        _signalMonitorTickCounter++;
        if (_signalMonitorTickCounter % 3 != 0) return;

        _signalLowFreqBuffer.Add(lowFreqValue);
        _signalHighFreqBuffer.Add(highFreqValue);

        while (_signalLowFreqBuffer.Count > _signalMonitorMaxPoints)
            _signalLowFreqBuffer.RemoveAt(0);
        while (_signalHighFreqBuffer.Count > _signalMonitorMaxPoints)
            _signalHighFreqBuffer.RemoveAt(0);

        double canvasWidth = 260;
        double canvasHeight = 80;
        double midY = canvasHeight / 2.0;
        double stepX = canvasWidth / _signalMonitorMaxPoints;

        var low = new System.Windows.Media.PointCollection(_signalLowFreqBuffer.Count);
        for (int i = 0; i < _signalLowFreqBuffer.Count; i++)
        {
            double y = midY - Math.Clamp(_signalLowFreqBuffer[i], -1, 1) * midY;
            low.Add(new System.Windows.Point(i * stepX, y));
        }

        var high = new System.Windows.Media.PointCollection(_signalHighFreqBuffer.Count);
        for (int i = 0; i < _signalHighFreqBuffer.Count; i++)
        {
            double y = midY - Math.Clamp(_signalHighFreqBuffer[i], -1, 1) * midY;
            high.Add(new System.Windows.Point(i * stepX, y));
        }

        SignalMonitorLowFreq = low;
        SignalMonitorHighFreq = high;
    }

    [RelayCommand(CanExecute = nameof(CanTestBuzz))]
    private async Task TestBuzz()
    {
        if (IsTestBuzzRunning) return;
        IsTestBuzzRunning = true;
        TestBuzzCommand.NotifyCanExecuteChanged();

        try
        {
            var provider = _telemetryLoop.ActiveProvider;
            AddSystemLog("Test Buzz: 500ms diagnostic vibration...");

            const int durationMs = 500;
            const double frequencyHz = 60.0;
            const double amplitude = 0.6;
            int stepMs = 4;
            int steps = durationMs / stepMs;

            for (int i = 0; i < steps; i++)
            {
                double t = i * stepMs / 1000.0;
                float signal = (float)(amplitude * Math.Sin(2.0 * Math.PI * frequencyHz * t));
                float absSignal = (float)Math.Abs(signal);

                if (provider != null && provider.IsInitialized)
                {
                    provider.UpdateTorque(signal);
                    provider.SetHaptics(new HapticData { VibrationIntensity = absSignal });
                }
                else
                {
                    _deviceManager.SendConstantForce(signal);
                    _deviceManager.SetTargetVibration(absSignal);
                }

                await Task.Delay(stepMs);
            }

            if (provider != null && provider.IsInitialized)
            {
                provider.UpdateTorque(0f);
                provider.SetHaptics(new HapticData());
            }
            else
            {
                _deviceManager.SendConstantForce(0f);
                _deviceManager.SetTargetVibration(0f);
            }

            AddSystemLog("Test Buzz: complete");
        }
        catch (Exception ex)
        {
            AddSystemLog($"Test Buzz: failed — {ex.Message}");
        }
        finally
        {
            IsTestBuzzRunning = false;
            TestBuzzCommand.NotifyCanExecuteChanged();
        }
    }

    private bool CanTestBuzz => IsDeviceConnected && !IsTestBuzzRunning && !IsRunning;

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
    private void ToggleScreenRecording()
    {
        if (_gameRecordingService.IsRecording)
        {
            _gameRecordingService.StopRecording();
            IsScreenRecording = false;
        }
        else
        {
            _gameRecordingService.StartRecording();
        }
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
    partial void OnSlipRatioGainChanged(float value) => _pipeline.SlipEnhancer.SlipRatioGain = value;
    partial void OnSlipAngleGainChanged(float value) => _pipeline.SlipEnhancer.SlipAngleGain = value;
    partial void OnSlipThresholdChanged(float value) => _pipeline.SlipEnhancer.SlipThreshold = value;
    partial void OnSlipUseFrontOnlyChanged(bool value) => _pipeline.SlipEnhancer.UseFrontOnly = value;

    partial void OnGearChangeMuteEnabledChanged(bool value)
    {
        var logPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "AcEvoFfbTuner", "ffb_debug.log");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
            File.AppendAllText(logPath,
                $"[VM] OnGearChangeMuteEnabledChanged called: value={value} | _pipeline type={_pipeline.GetType().Name} | BEFORE: _pipeline.GearShiftFilterEnabled={_pipeline.GearShiftFilterEnabled}\n");
        }
        catch { }

        _pipeline.GearShiftFilterEnabled = value;
        R3eFfbPipeline? r3e = null;
        if (_pipeline is R3eFfbPipeline r3ePipeline)
            r3e = r3ePipeline;

        if (r3e != null)
            r3e.GearChangeMuteEnabled = value;

        try
        {
            File.AppendAllText(logPath,
                $"[VM] OnGearChangeMuteEnabledChanged AFTER: _pipeline.GearShiftFilterEnabled={_pipeline.GearShiftFilterEnabled} | r3e={(r3e != null ? r3e.GearChangeMuteEnabled.ToString() : "N/A")}\n");
        }
        catch { }
    }
    partial void OnCorneringForceChanged(float value) => _pipeline.DynamicEffects.LateralGGain = value;
    partial void OnAccelerationBrakingForceChanged(float value) => _pipeline.DynamicEffects.LongitudinalGGain = value;
    partial void OnRoadFeelChanged(float value) => _pipeline.DynamicEffects.SuspensionGain = value;
    partial void OnCarRotationForceChanged(float value) => _pipeline.DynamicEffects.YawRateGain = value;
    partial void OnTyreFlexGainChanged(float value) => _pipeline.TyreFlex.FlexGain = value;
    partial void OnCarcassStiffnessChanged(float value) => _pipeline.TyreFlex.CarcassStiffness = value;
    partial void OnFlexSmoothingChanged(float value) => _pipeline.TyreFlex.FlexSmoothing = value;
    partial void OnContactPatchWeightChanged(float value) => _pipeline.TyreFlex.ContactPatchWeight = value;
    partial void OnLoadFlexGainChanged(float value) => _pipeline.TyreFlex.LoadFlexGain = value;
    partial void OnAutoGainEnabledChanged(bool value) => _pipeline.AutoGainEnabled = value;
    partial void OnAutoGainScaleChanged(float value) => _pipeline.AutoGainScale = value;
    partial void OnCurbGainChanged(float value) => _pipeline.VibrationMixer.KerbGain = value;
    partial void OnSlipGainChanged(float value) => _pipeline.VibrationMixer.SlipGain = value;
    partial void OnRoadGainChanged(float value) => _pipeline.VibrationMixer.RoadGain = value;
    partial void OnAbsGainChanged(float value) => _pipeline.VibrationMixer.AbsGain = value;

    partial void OnAbsPulseAmplitudeChanged(float value) => _pipeline.VibrationMixer.AbsPulseAmplitude = value;
    partial void OnVibrationMasterGainChanged(float value) => _pipeline.VibrationMixer.MasterGain = value;
    partial void OnSuspensionRoadGainChanged(float value) => _pipeline.VibrationMixer.SuspensionRoadGain = value;
    partial void OnScrubGainChanged(float value) => _pipeline.VibrationMixer.ScrubGain = value;
    partial void OnRearSlipGainChanged(float value) => _pipeline.VibrationMixer.RearSlipGain = value;
    partial void OnOfftrackGainChanged(float value) => _pipeline.VibrationMixer.OfftrackGain = value;
    partial void OnOfftrackSeverityScaleChanged(float value) => _pipeline.VibrationMixer.OfftrackSeverityScale = value;
    partial void OnLfeEnabledChanged(bool value) => _pipeline.LfeGenerator.Enabled = value;
    partial void OnLfeGainChanged(float value) => _pipeline.LfeGenerator.Gain = value;
    partial void OnLfeFrequencyChanged(float value) => _pipeline.LfeGenerator.Frequency = value;
    partial void OnLfeSuspensionDriveChanged(float value) => _pipeline.LfeGenerator.SuspensionDrive = value;
    partial void OnLfeSpeedScalingChanged(float value) => _pipeline.LfeGenerator.SpeedScaling = value;
    partial void OnLfeRpmDriveChanged(float value) => _pipeline.LfeGenerator.RpmDrive = value;
    partial void OnEqEnabledChanged(bool value) => _pipeline.Equalizer.MasterEnabled = value;
    partial void OnEqBand0GainChanged(float value) => _pipeline.Equalizer.SetBandGain(0, value);
    partial void OnEqBand1GainChanged(float value) => _pipeline.Equalizer.SetBandGain(1, value);
    partial void OnEqBand2GainChanged(float value) => _pipeline.Equalizer.SetBandGain(2, value);
    partial void OnEqBand3GainChanged(float value) => _pipeline.Equalizer.SetBandGain(3, value);
    partial void OnEqBand4GainChanged(float value) => _pipeline.Equalizer.SetBandGain(4, value);
    partial void OnEqBand5GainChanged(float value) => _pipeline.Equalizer.SetBandGain(5, value);
    partial void OnEqBand6GainChanged(float value) => _pipeline.Equalizer.SetBandGain(6, value);
    partial void OnEqBand7GainChanged(float value) => _pipeline.Equalizer.SetBandGain(7, value);
    partial void OnEqBand8GainChanged(float value) => _pipeline.Equalizer.SetBandGain(8, value);
    partial void OnEqBand9GainChanged(float value) => _pipeline.Equalizer.SetBandGain(9, value);
    partial void OnSteeringLockDegreesChanged(int value) { }
    partial void OnCompressionPowerChanged(float value) => _pipeline.CompressionPower = value;
    partial void OnSignCorrectionEnabledChanged(bool value) => _pipeline.SignCorrectionEnabled = value;
    partial void OnFyInvertedChanged(bool value) => _pipeline.ChannelMixer.FyInverted = value;
    partial void OnMaxSlewRateChanged(float value) => _pipeline.MaxSlewRate = value;
    partial void OnCenterSuppressionDegreesChanged(float value) => _pipeline.CenterSuppressionDegrees = value;
    partial void OnCenterKneePowerChanged(float value) => _pipeline.CenterKneePower = value;
    partial void OnHysteresisThresholdChanged(float value) => _pipeline.HysteresisThreshold = value;
    partial void OnNoiseFloorChanged(float value) => _pipeline.NoiseFloor = value;
    partial void OnHysteresisWatchdogFramesChanged(int value) => _pipeline.HysteresisWatchdogFrames = value;
    partial void OnCenterBlendDegreesChanged(float value) => _pipeline.ChannelMixer.CenterBlendDegrees = value;
    partial void OnCenterSharpnessDegreesChanged(float value) => _pipeline.CenterSharpnessDegrees = value;
    partial void OnCoreForceMultiplierChanged(float value) => _pipeline.CoreForceMultiplier = value;
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
    partial void OnCurrentForceOutputChanged(float value)
    {
        OnPropertyChanged(nameof(ForceBarFillHeight));
    }
    partial void OnSpeedKmhChanged(float value) => OnPropertyChanged(nameof(SpeedNeedleAngle));

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
    partial void OnStaticFrictionGainChanged(float value) => _telemetryLoop.StaticFriction.Gain = value;
    partial void OnStaticFrictionMaxElasticStretchChanged(float value) => _telemetryLoop.StaticFriction.MaxElasticStretch = value;
    partial void OnStaticFrictionSpringStiffnessChanged(float value) => _telemetryLoop.StaticFriction.SpringStiffness = value;
    partial void OnStaticFrictionKineticFrictionBaseChanged(float value) => _telemetryLoop.StaticFriction.KineticFrictionBase = value;
    partial void OnStaticFrictionEngineOffDampingChanged(float value) => _telemetryLoop.StaticFriction.EngineOffDamping = value;
    partial void OnStaticFrictionEngineOnDampingChanged(float value) => _telemetryLoop.StaticFriction.EngineOnDamping = value;
    partial void OnStaticFrictionEngineOffScaleChanged(float value) => _telemetryLoop.StaticFriction.EngineOffScale = value;
    partial void OnStaticFrictionEngineOnScaleChanged(float value) => _telemetryLoop.StaticFriction.EngineOnScale = value;
    partial void OnStaticFrictionActiveDecayChanged(float value) => _telemetryLoop.StaticFriction.ActiveDecay = value;
    partial void OnStaticFrictionReturnDecayChanged(float value) => _telemetryLoop.StaticFriction.ReturnDecay = value;
    partial void OnStaticFrictionOutputSmoothAlphaChanged(float value) => _telemetryLoop.StaticFriction.OutputSmoothAlpha = value;

    partial void OnSelectedProfileChanged(FfbProfile? value)
    {
        if (value == null) return;
        _profileManager.SetActiveProfile(value);
        value.ApplyToPipeline(_pipeline);
        value.ApplyToStaticFriction(_telemetryLoop.StaticFriction);
        LoadProfileValues(value);
        OnPropertyChanged(nameof(IsBuiltInProfileSelected));
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
            var g = raw.DisplayAccG?.Length > 0 && raw.DisplayAccG.Any(v => v != 0) ? raw.DisplayAccG : raw.AccG;
            LatG = g?.Length > 0 ? g[0] : 0f;
            LongG = g?.Length > 1 ? g[1] : 0f;
            ActiveLedCount = _deviceManager.ActiveLedCount;

            float highFreqHaptics = processed.VibrationForce;
            UpdateSignalMonitor(processed.MainForce, highFreqHaptics);

            _gameRecordingService.OnTelemetryTick(processed.SpeedKmh);
            IsScreenRecording = _gameRecordingService.IsRecording;

            if (IsGameConnected && raw.FfbStrength > 0.01f && !ShowGameFfbWarning && _ffbWarningDismissed != true)
                ShowGameFfbWarning = true;
            int rawDeg = raw.SteerDegrees;
            int lockDeg = (rawDeg > 90 && rawDeg <= 1440) ? rawDeg : SteeringLockDegrees;
            if (lockDeg <= 0) lockDeg = 900;
            SteerAngle = (float)raw.SteerAngle * (lockDeg / 2f);

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
                $"PostDamping:    {processed.PostDampingForce:F6}\n" +
                $"PostGainOut:    {processed.PostOutputGainForce:F6}\n" +
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
                    StatusText = "FFB device lost exclusive access — auto-reconnect in progress...";
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
                    processed.PostCompressionForce, processed.PostDampingForce,
                    processed.PostOutputGainForce, processed.PostDynamicForce,
                    processed.ChannelMzFront, processed.ChannelFxFront, processed.ChannelFyFront,
                    processed.PostLutForce, processed.IsClipping,
                    raw.GasInput, raw.BrakeInput, raw, processed.WetnessFactor,
                    lockDeg);

                mw.UpdateCalibrationWizard(raw.SpeedKmh, processed.MainForce, processed.IsClipping);
                mw.UpdateSetupWizard(raw.SpeedKmh, processed.MainForce, raw.SteerAngle, processed.IsClipping, processed.ChannelMzFront);

                // Auto-show WheelCenter overlay when RaceRoom AI takes control
                bool r3eAi = _telemetryLoop.IsR3eAiControlled;
                if (r3eAi)
                {
                    float physicalNorm = _telemetryLoop.LatestPhysicalWheelNormalized;
                    float physicalAngleDeg = physicalNorm * (lockDeg / 2f);
                    mw.UpdateWheelCenter(physicalNorm, physicalAngleDeg);
                    mw.ShowWheelCenterOverlay();
                }
                else
                {
                    mw.CloseWheelCenterOverlay();
                }








            }

            WetWeatherCurrentFactor = processed.WetnessFactor;
            CurrentTyreCompoundFront = processed.TyreCompoundFrontName;
            CurrentTyreCompoundRear = processed.TyreCompoundRearName;
            CurrentTyreCategoryName = processed.TyreCategory.ToString();

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
                    ShowDiagnostics,
                    TrackLatitude,
                    TrackLongitude,
                    TrackRotation);
            }

            if (Application.Current?.MainWindow is MainWindow mw3)
            {
                mw3.UpdateLiveMap(
                    DetectedTrackName ?? "",
                    _telemetryLoop.MapBuilder.CurrentMap,
                    raw.CarX, raw.CarZ, raw.Heading, raw.SpeedKmh,
                    IsOnTrackMap);
            }
        }


        var racePhysics = _telemetryLoop.LatestPhysicsRaw;
        var raceGraphics = _telemetryLoop.LatestGraphicsRaw;
        if (racePhysics != null && raceGraphics != null && _raceInfoOverlay != null)
        {
            _raceInfoProcessor.Process(racePhysics.Value, raceGraphics.Value, out var raceInfo);
            _raceInfoOverlay.UpdateData(raceInfo, racePhysics.Value, raceGraphics.Value);
        }
        if (IsDeviceConnected || IsAssigningSnapshotButton || IsAssigningPanicButton)
            PollSnapshotButton();

        CheckConflictingApps();

        PacketsPerSecond = _telemetryLoop.PacketsPerSecond;
        IsGameConnected = _telemetryLoop.IsGameConnected;
    }

    private void LoadProfileValues(FfbProfile profile)
    {
        BuiltInDefaults = FfbProfile.GetDefaultProfile(profile.Name);
        ProfileCarMatch = profile.CarMatch;
        ProfileTrackMatch = profile.TrackMatch;
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
        SlipRatioGain = profile.Slip.SlipRatioGain;
        SlipAngleGain = profile.Slip.SlipAngleGain;
        SlipThreshold = profile.Slip.SlipThreshold;
        SlipUseFrontOnly = profile.Slip.UseFrontOnly;
        GearChangeMuteEnabled = profile.Slip.GearChangeMuteEnabled;
        CorneringForce = profile.Dynamic.LateralGGain;
        AccelerationBrakingForce = profile.Dynamic.LongitudinalGGain;
        RoadFeel = profile.Dynamic.SuspensionGain;
        CarRotationForce = profile.Dynamic.YawRateGain;
        TyreFlexGain = profile.TyreFlex.FlexGain;
        CarcassStiffness = profile.TyreFlex.CarcassStiffness;
        FlexSmoothing = profile.TyreFlex.FlexSmoothing;
        ContactPatchWeight = profile.TyreFlex.ContactPatchWeight;
        LoadFlexGain = profile.TyreFlex.LoadFlexGain;
        AutoGainEnabled = profile.AutoGain.Enabled;
        AutoGainScale = profile.AutoGain.Scale;
        CurbGain = profile.Vibrations.KerbGain;
        SlipGain = profile.Vibrations.SlipGain;
        RoadGain = profile.Vibrations.RoadGain;
        AbsGain = profile.Vibrations.AbsGain;
        AbsPulseAmplitude = profile.Vibrations.AbsPulseAmplitude;
        VibrationMasterGain = profile.Vibrations.MasterGain;
        SuspensionRoadGain = profile.Vibrations.SuspensionRoadGain;
        ScrubGain = profile.Vibrations.ScrubGain;
        RearSlipGain = profile.Vibrations.RearSlipGain;
        OfftrackGain = profile.Vibrations.OfftrackGain;
        OfftrackSeverityScale = profile.Vibrations.OfftrackSeverityScale;
        LfeEnabled = profile.Lfe.Enabled;
        LfeGain = profile.Lfe.Gain;
        LfeFrequency = profile.Lfe.Frequency;
        LfeSuspensionDrive = profile.Lfe.SuspensionDrive;
        LfeSpeedScaling = profile.Lfe.SpeedScaling;
        LfeRpmDrive = profile.Lfe.RpmDrive;
        CompressionPower = profile.CompressionPower;
        SteeringLockDegrees = profile.SteeringLockDegrees;
        ForceScale = profile.ForceScale;
        SignCorrectionEnabled = profile.SignCorrectionEnabled;
        FyInverted = profile.FyInverted;
        ForceInvertEnabled = profile.ForceInvertEnabled;
        MaxSlewRate = profile.Advanced.MaxSlewRate;
        CenterSuppressionDegrees = profile.Advanced.CenterSuppressionDegrees;
        CenterKneePower = profile.Advanced.CenterKneePower;
        HysteresisThreshold = profile.Advanced.HysteresisThreshold;
        NoiseFloor = profile.Advanced.NoiseFloor;
        HysteresisWatchdogFrames = profile.Advanced.HysteresisWatchdogFrames;
        CenterBlendDegrees = profile.Advanced.CenterBlendDegrees;
        CenterSharpnessDegrees = profile.Advanced.CenterSharpnessDegrees;
        CoreForceMultiplier = profile.Advanced.CoreForceMultiplier;
        SteerVelocityReference = profile.Advanced.SteerVelocityReference;
        VelocityDeadzone = profile.Advanced.VelocityDeadzone;
        LowSpeedSmoothKmh = profile.Advanced.LowSpeedSmoothKmh;
        EqEnabled = profile.Equalizer.Enabled;
        EqBand0Gain = profile.Equalizer.GetGain(0);
        EqBand1Gain = profile.Equalizer.GetGain(1);
        EqBand2Gain = profile.Equalizer.GetGain(2);
        EqBand3Gain = profile.Equalizer.GetGain(3);
        EqBand4Gain = profile.Equalizer.GetGain(4);
        EqBand5Gain = profile.Equalizer.GetGain(5);
        EqBand6Gain = profile.Equalizer.GetGain(6);
        EqBand7Gain = profile.Equalizer.GetGain(7);
        EqBand8Gain = profile.Equalizer.GetGain(8);
        EqBand9Gain = profile.Equalizer.GetGain(9);
        LoadLedValues(profile.LedEffects);

        Hf8Enabled = profile.Hf8.Enabled;
        Hf8MasterGain = profile.Hf8.MasterGain;
        Hf8OutputRateHz = profile.Hf8.OutputRateHz;
        Hf8ZoneGain0 = profile.Hf8.GetZoneGain(0);
        Hf8ZoneGain1 = profile.Hf8.GetZoneGain(1);
        Hf8ZoneGain2 = profile.Hf8.GetZoneGain(2);
        Hf8ZoneGain3 = profile.Hf8.GetZoneGain(3);
        Hf8ZoneGain4 = profile.Hf8.GetZoneGain(4);
        Hf8ZoneGain5 = profile.Hf8.GetZoneGain(5);
        Hf8ZoneGain6 = profile.Hf8.GetZoneGain(6);
        Hf8ZoneGain7 = profile.Hf8.GetZoneGain(7);
        Hf8ZoneEnabled0 = profile.Hf8.GetZoneEnabled(0);
        Hf8ZoneEnabled1 = profile.Hf8.GetZoneEnabled(1);
        Hf8ZoneEnabled2 = profile.Hf8.GetZoneEnabled(2);
        Hf8ZoneEnabled3 = profile.Hf8.GetZoneEnabled(3);
        Hf8ZoneEnabled4 = profile.Hf8.GetZoneEnabled(4);
        Hf8ZoneEnabled5 = profile.Hf8.GetZoneEnabled(5);
        Hf8ZoneEnabled6 = profile.Hf8.GetZoneEnabled(6);
        Hf8ZoneEnabled7 = profile.Hf8.GetZoneEnabled(7);

        Hf8SrcSusp0 = profile.Hf8.GetSourceWeight(0, 0);
        Hf8SrcSlip0 = profile.Hf8.GetSourceWeight(0, 1);
        Hf8SrcKerb0 = profile.Hf8.GetSourceWeight(0, 2);
        Hf8SrcLatG0 = profile.Hf8.GetSourceWeight(0, 3);
        Hf8SrcEngine0 = profile.Hf8.GetSourceWeight(0, 4);

        Hf8SrcSusp1 = profile.Hf8.GetSourceWeight(1, 0);
        Hf8SrcSlip1 = profile.Hf8.GetSourceWeight(1, 1);
        Hf8SrcKerb1 = profile.Hf8.GetSourceWeight(1, 2);
        Hf8SrcLatG1 = profile.Hf8.GetSourceWeight(1, 3);
        Hf8SrcEngine1 = profile.Hf8.GetSourceWeight(1, 4);

        Hf8SrcSusp2 = profile.Hf8.GetSourceWeight(2, 0);
        Hf8SrcSlip2 = profile.Hf8.GetSourceWeight(2, 1);
        Hf8SrcKerb2 = profile.Hf8.GetSourceWeight(2, 2);
        Hf8SrcLatG2 = profile.Hf8.GetSourceWeight(2, 3);
        Hf8SrcEngine2 = profile.Hf8.GetSourceWeight(2, 4);

        Hf8SrcSusp3 = profile.Hf8.GetSourceWeight(3, 0);
        Hf8SrcSlip3 = profile.Hf8.GetSourceWeight(3, 1);
        Hf8SrcKerb3 = profile.Hf8.GetSourceWeight(3, 2);
        Hf8SrcLatG3 = profile.Hf8.GetSourceWeight(3, 3);
        Hf8SrcEngine3 = profile.Hf8.GetSourceWeight(3, 4);

        Hf8SrcSusp4 = profile.Hf8.GetSourceWeight(4, 0);
        Hf8SrcSlip4 = profile.Hf8.GetSourceWeight(4, 1);
        Hf8SrcKerb4 = profile.Hf8.GetSourceWeight(4, 2);
        Hf8SrcLatG4 = profile.Hf8.GetSourceWeight(4, 3);
        Hf8SrcEngine4 = profile.Hf8.GetSourceWeight(4, 4);

        Hf8SrcSusp5 = profile.Hf8.GetSourceWeight(5, 0);
        Hf8SrcSlip5 = profile.Hf8.GetSourceWeight(5, 1);
        Hf8SrcKerb5 = profile.Hf8.GetSourceWeight(5, 2);
        Hf8SrcLatG5 = profile.Hf8.GetSourceWeight(5, 3);
        Hf8SrcEngine5 = profile.Hf8.GetSourceWeight(5, 4);

        Hf8SrcSusp6 = profile.Hf8.GetSourceWeight(6, 0);
        Hf8SrcSlip6 = profile.Hf8.GetSourceWeight(6, 1);
        Hf8SrcKerb6 = profile.Hf8.GetSourceWeight(6, 2);
        Hf8SrcLatG6 = profile.Hf8.GetSourceWeight(6, 3);
        Hf8SrcEngine6 = profile.Hf8.GetSourceWeight(6, 4);

        Hf8SrcSusp7 = profile.Hf8.GetSourceWeight(7, 0);
        Hf8SrcSlip7 = profile.Hf8.GetSourceWeight(7, 1);
        Hf8SrcKerb7 = profile.Hf8.GetSourceWeight(7, 2);
        Hf8SrcLatG7 = profile.Hf8.GetSourceWeight(7, 3);
        Hf8SrcEngine7 = profile.Hf8.GetSourceWeight(7, 4);

        GripGuardEnabled = profile.GripGuard.Enabled;
        GripGuardPeakSlipAngle = profile.GripGuard.PeakSlipAngle;
        GripGuardAttenuationStrength = profile.GripGuard.AttenuationStrength;
        GripGuardMechanicalTrailGain = profile.GripGuard.MechanicalTrailGain;
        GripGuardMinSpeedKmh = profile.GripGuard.MinSpeedKmh;

        CrashEnabled = profile.Crash.Enabled;
        CrashImpactGain = profile.Crash.ImpactGain;
        CrashSafetyClamp = profile.Crash.SafetyClamp;
        CrashDecayRate = profile.Crash.DecayRate;
        CrashTriggerThresholdG = profile.Crash.TriggerThresholdG;
        CrashMinSpeedKmh = profile.Crash.MinSpeedKmh;
        CrashSafetyOverride = profile.Crash.SafetyOverride;
        ShowCrashSafetyWarning = profile.Crash.SafetyOverride;

        TyreConditionEnabled = profile.TyreCondition.Enabled;
        TyreConditionBlowoutGain = profile.TyreCondition.BlowoutVibrationGain;
        TyreConditionPressureLossGain = profile.TyreCondition.PressureLossGain;
        TyreConditionDamageAsymmetryGain = profile.TyreCondition.DamageAsymmetryGain;
        TyreConditionBlowoutThreshold = profile.TyreCondition.BlowoutPressureThreshold;
        TyreConditionMaxBlowoutAmplitude = profile.TyreCondition.MaxBlowoutAmplitude;

        WetWeatherEnabled = profile.WetWeather.Enabled;
        WetWeatherAutoDetect = profile.WetWeather.AutoDetect;
        WetWeatherManualIntensity = profile.WetWeather.ManualIntensity;
        WetWeatherRoadVibSuppression = profile.WetWeather.RoadVibSuppression;
        WetWeatherCurbSuppression = profile.WetWeather.CurbSuppression;
        WetWeatherScrubSuppression = profile.WetWeather.ScrubSuppression;
        WetWeatherPeakSlipAngleMultiplier = profile.WetWeather.PeakSlipAngleMultiplier;
        WetWeatherDampingReduction = profile.WetWeather.DampingReduction;
        WetWeatherNoiseFloorSuppression = profile.WetWeather.NoiseFloorSuppression;
        WetWeatherHydroplaningEnabled = profile.WetWeather.HydroplaningEnabled;
        WetWeatherHydroplaningSpeedThreshold = profile.WetWeather.HydroplaningSpeedThreshold;
        WetWeatherHydroplaningMaxAttenuation = profile.WetWeather.HydroplaningMaxAttenuation;

        StaticFrictionGain = profile.StaticFriction.Gain;
        StaticFrictionMaxElasticStretch = profile.StaticFriction.MaxElasticStretch;
        StaticFrictionSpringStiffness = profile.StaticFriction.SpringStiffness;
        StaticFrictionKineticFrictionBase = profile.StaticFriction.KineticFrictionBase;
        StaticFrictionEngineOffDamping = profile.StaticFriction.EngineOffDamping;
        StaticFrictionEngineOnDamping = profile.StaticFriction.EngineOnDamping;
        StaticFrictionEngineOffScale = profile.StaticFriction.EngineOffScale;
        StaticFrictionEngineOnScale = profile.StaticFriction.EngineOnScale;
        StaticFrictionActiveDecay = profile.StaticFriction.ActiveDecay;
        StaticFrictionReturnDecay = profile.StaticFriction.ReturnDecay;
        StaticFrictionOutputSmoothAlpha = profile.StaticFriction.OutputSmoothAlpha;
    }

    private void PushValuesToPipeline()
    {
        if (SelectedProfile != null)
        {
            SelectedProfile.CarMatch = ProfileCarMatch;
            SelectedProfile.TrackMatch = ProfileTrackMatch;
        }

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
        _pipeline.SlipEnhancer.SlipRatioGain = SlipRatioGain;
        _pipeline.SlipEnhancer.SlipAngleGain = SlipAngleGain;
        _pipeline.SlipEnhancer.SlipThreshold = SlipThreshold;
        _pipeline.SlipEnhancer.UseFrontOnly = SlipUseFrontOnly;
        _pipeline.DynamicEffects.LateralGGain = CorneringForce;
        _pipeline.DynamicEffects.LongitudinalGGain = AccelerationBrakingForce;
        _pipeline.DynamicEffects.SuspensionGain = RoadFeel;
        _pipeline.DynamicEffects.YawRateGain = CarRotationForce;
        _pipeline.TyreFlex.FlexGain = TyreFlexGain;
        _pipeline.TyreFlex.CarcassStiffness = CarcassStiffness;
        _pipeline.TyreFlex.FlexSmoothing = FlexSmoothing;
        _pipeline.TyreFlex.ContactPatchWeight = ContactPatchWeight;
        _pipeline.TyreFlex.LoadFlexGain = LoadFlexGain;
        _pipeline.AutoGainEnabled = AutoGainEnabled;
        _pipeline.AutoGainScale = AutoGainScale;
        _pipeline.VibrationMixer.KerbGain = CurbGain;
        _pipeline.VibrationMixer.SlipGain = SlipGain;
        _pipeline.VibrationMixer.RoadGain = RoadGain;
        _pipeline.VibrationMixer.AbsGain = AbsGain;
        _pipeline.VibrationMixer.AbsPulseAmplitude = AbsPulseAmplitude;
        _pipeline.VibrationMixer.MasterGain = VibrationMasterGain;
        _pipeline.VibrationMixer.SuspensionRoadGain = SuspensionRoadGain;
        _pipeline.VibrationMixer.ScrubGain = ScrubGain;
        _pipeline.VibrationMixer.RearSlipGain = RearSlipGain;
        _pipeline.LfeGenerator.Enabled = LfeEnabled;
        _pipeline.LfeGenerator.Gain = LfeGain;
        _pipeline.LfeGenerator.Frequency = LfeFrequency;
        _pipeline.LfeGenerator.SuspensionDrive = LfeSuspensionDrive;
        _pipeline.LfeGenerator.SpeedScaling = LfeSpeedScaling;
        _pipeline.LfeGenerator.RpmDrive = LfeRpmDrive;
        _pipeline.CompressionPower = CompressionPower;
        _pipeline.ForceScale = ForceScale;
        _pipeline.SignCorrectionEnabled = SignCorrectionEnabled;
        _pipeline.ChannelMixer.FyInverted = FyInverted;
        _pipeline.MaxSlewRate = MaxSlewRate;
        _pipeline.CenterSuppressionDegrees = CenterSuppressionDegrees;
        _pipeline.CenterKneePower = CenterKneePower;
        _pipeline.HysteresisThreshold = HysteresisThreshold;
        _pipeline.NoiseFloor = NoiseFloor;
        _pipeline.HysteresisWatchdogFrames = HysteresisWatchdogFrames;
        _pipeline.ChannelMixer.CenterBlendDegrees = CenterBlendDegrees;
        _pipeline.CenterSharpnessDegrees = CenterSharpnessDegrees;
        _pipeline.CoreForceMultiplier = CoreForceMultiplier;
        _pipeline.Damping.SteerVelocityReference = SteerVelocityReference;
        _pipeline.Damping.VelocityDeadzone = VelocityDeadzone;
        _pipeline.ChannelMixer.LowSpeedSmoothKmh = LowSpeedSmoothKmh;
        _pipeline.Equalizer.MasterEnabled = EqEnabled;
        _pipeline.Equalizer.SetBandGain(0, EqBand0Gain);
        _pipeline.Equalizer.SetBandGain(1, EqBand1Gain);
        _pipeline.Equalizer.SetBandGain(2, EqBand2Gain);
        _pipeline.Equalizer.SetBandGain(3, EqBand3Gain);
        _pipeline.Equalizer.SetBandGain(4, EqBand4Gain);
        _pipeline.Equalizer.SetBandGain(5, EqBand5Gain);
        _pipeline.Equalizer.SetBandGain(6, EqBand6Gain);
        _pipeline.Equalizer.SetBandGain(7, EqBand7Gain);
        _pipeline.Equalizer.SetBandGain(8, EqBand8Gain);
        _pipeline.Equalizer.SetBandGain(9, EqBand9Gain);

        _pipeline.GripGuard.Enabled = GripGuardEnabled;
        _pipeline.GripGuard.PeakSlipAngle = GripGuardPeakSlipAngle;
        _pipeline.GripGuard.AttenuationStrength = GripGuardAttenuationStrength;
        _pipeline.GripGuard.MechanicalTrailGain = GripGuardMechanicalTrailGain;
        _pipeline.GripGuard.MinSpeedKmh = GripGuardMinSpeedKmh;

        _pipeline.CrashDetector.Enabled = CrashEnabled;
        _pipeline.CrashDetector.ImpactGain = CrashImpactGain;
        _pipeline.CrashDetector.SafetyClamp = CrashSafetyClamp;
        _pipeline.CrashDetector.DecayRate = CrashDecayRate;
        _pipeline.CrashDetector.TriggerThresholdG = CrashTriggerThresholdG;
        _pipeline.CrashDetector.MinSpeedKmh = CrashMinSpeedKmh;
        _pipeline.CrashDetector.SafetyOverride = CrashSafetyOverride;

        _pipeline.TyreCondition.Enabled = TyreConditionEnabled;
        _pipeline.TyreCondition.BlowoutVibrationGain = TyreConditionBlowoutGain;
        _pipeline.TyreCondition.PressureLossGain = TyreConditionPressureLossGain;
        _pipeline.TyreCondition.DamageAsymmetryGain = TyreConditionDamageAsymmetryGain;
        _pipeline.TyreCondition.BlowoutPressureThreshold = TyreConditionBlowoutThreshold;
        _pipeline.TyreCondition.MaxBlowoutAmplitude = TyreConditionMaxBlowoutAmplitude;

        _pipeline.WetWeather.Enabled = WetWeatherEnabled;
        _pipeline.WetWeather.AutoDetect = WetWeatherAutoDetect;
        _pipeline.WetWeather.ManualIntensity = WetWeatherManualIntensity;
        _pipeline.WetWeather.RoadVibSuppression = WetWeatherRoadVibSuppression;
        _pipeline.WetWeather.CurbSuppression = WetWeatherCurbSuppression;
        _pipeline.WetWeather.ScrubSuppression = WetWeatherScrubSuppression;
        _pipeline.WetWeather.PeakSlipAngleMultiplier = WetWeatherPeakSlipAngleMultiplier;
        _pipeline.WetWeather.DampingReduction = WetWeatherDampingReduction;
        _pipeline.WetWeather.NoiseFloorSuppression = WetWeatherNoiseFloorSuppression;
        _pipeline.WetWeather.HydroplaningEnabled = WetWeatherHydroplaningEnabled;
        _pipeline.WetWeather.HydroplaningSpeedThreshold = WetWeatherHydroplaningSpeedThreshold;
        _pipeline.WetWeather.HydroplaningMaxAttenuation = WetWeatherHydroplaningMaxAttenuation;

        _telemetryLoop.StaticFriction.Gain = StaticFrictionGain;
        _telemetryLoop.StaticFriction.MaxElasticStretch = StaticFrictionMaxElasticStretch;
        _telemetryLoop.StaticFriction.SpringStiffness = StaticFrictionSpringStiffness;
        _telemetryLoop.StaticFriction.KineticFrictionBase = StaticFrictionKineticFrictionBase;
        _telemetryLoop.StaticFriction.EngineOffDamping = StaticFrictionEngineOffDamping;
        _telemetryLoop.StaticFriction.EngineOnDamping = StaticFrictionEngineOnDamping;
        _telemetryLoop.StaticFriction.EngineOffScale = StaticFrictionEngineOffScale;
        _telemetryLoop.StaticFriction.EngineOnScale = StaticFrictionEngineOnScale;
        _telemetryLoop.StaticFriction.ActiveDecay = StaticFrictionActiveDecay;
        _telemetryLoop.StaticFriction.ReturnDecay = StaticFrictionReturnDecay;
        _telemetryLoop.StaticFriction.OutputSmoothAlpha = StaticFrictionOutputSmoothAlpha;

        PushLedConfig();
    }

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

    private void RefreshProfiles()
    {
        Profiles.Clear();
        foreach (var p in _profileManager.Profiles)
            Profiles.Add(p);
    }

    [ObservableProperty]
    private bool _isSendingDiagnosticPack;

    [ObservableProperty]
    private string _diagnosticPackStatus = string.Empty;

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

    partial void OnVoiceEnabledChanged(bool value)
    {
        _voiceService.Enabled = value;
        _appSettings.VoiceEnabled = value;
        _appSettings.Save();
    }

    partial void OnVoiceVolumeChanged(int value)
    {
        _voiceService.Volume = value;
        _appSettings.VoiceVolume = value;
        _appSettings.Save();
    }

    public string VoiceEngine => _voiceService.ActiveEngine;

    public bool VoicePackReady => VoiceService.IsCacheReady;

    public string VoiceCacheStatus => VoiceService.IsCacheReady
        ? $"{_voiceService.CachedCount}/{_voiceService.TotalPhrases} phrases cached"
        : "Voice pack not found. Place MP3 files in:\n" + GetVoiceCachePath();

    [RelayCommand]
    private void OpenVoiceCacheFolder()
    {
        try
        {
            var dir = GetVoiceCachePath();
            Directory.CreateDirectory(dir);
            Process.Start(new ProcessStartInfo("explorer.exe", dir) { UseShellExecute = true });
        }
        catch { }
    }

    private static string GetVoiceCachePath()
    {
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "AcEvoFfbTuner", "voice-cache");
    }

    [ObservableProperty]
    private ObservableCollection<string> _availableVoices = new();

    [ObservableProperty]
    private string? _selectedVoice;

    private bool _voiceInitialized;

    partial void OnSelectedVoiceChanged(string? value)
    {
        if (string.IsNullOrEmpty(value) || value == _voiceService.SelectedVoice) return;
        _voiceService.SelectedVoice = value;
        _appSettings.VoiceName = value;
        _appSettings.Save();

        if (_voiceInitialized)
            _voiceService.Speak("This is {0}", value);
    }

    [ObservableProperty]
    private bool _isInstallingVoices;

    [RelayCommand]
    private void OpenVoiceSettings()
    {
        Services.VoiceService.OpenSpeechSettings();
    }

    [RelayCommand]
    private async Task InstallNaturalVoicesAsync()
    {
        if (IsInstallingVoices) return;
        IsInstallingVoices = true;
        try
        {
            await Task.Run(() =>
            {
                var psi = new ProcessStartInfo("powershell.exe")
                {
                    Arguments = "-NoProfile -Command \"Add-WindowsCapability -Online -Name 'Language.Speech.en-US~~~0.0.1.0' -LimitAccess -Source 'WindowsUpdate'\"",
                    Verb = "runas",
                    UseShellExecute = true,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden
                };
                using var proc = Process.Start(psi);
                proc?.WaitForExit(120000);
            });
            RefreshVoices();
            if (_voiceService.HasNaturalVoice)
                _voiceService.Speak("Natural voices installed");
        }
        catch (Exception ex)
        {
            AddSystemLog($"Voice install failed: {ex.Message}");
        }
        finally
        {
            IsInstallingVoices = false;
        }
    }

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

    public void LoadAppSettings()
    {
        SplashScreenEnabled = _appSettings.SplashScreenEnabled;
        CustomStartupSoundPath = _appSettings.CustomStartupSoundPath;
        StartMinimised = _appSettings.StartMinimised;
        AutoConnect = _appSettings.AutoConnect;
        AutoStart = _appSettings.AutoStart;
        IsPerCarAutoLoadEnabled = _appSettings.PerCarAutoLoadEnabled;
        TooltipsEnabled = _appSettings.TooltipsEnabled;
        AutoProfileUpgrade = _appSettings.AutoProfileUpgrade;
        ThemeMode = _appSettings.ThemeName;
        VoiceEnabled = _appSettings.VoiceEnabled;
        VoiceVolume = _appSettings.VoiceVolume;
        if (Enum.TryParse<NavPage>(_appSettings.DefaultStartPage, out var startPage))
        {
            var pages = Enum.GetValues<NavPage>();
            DefaultStartPageIndex = Array.IndexOf(pages, startPage);
            CurrentPage = startPage;
        }
        else
        {
            DefaultStartPageIndex = 0;
            CurrentPage = NavPage.Home;
        }

        RefreshVoices();
        _voiceInitialized = true;
        RefreshRecordingDevices();
    }

    private void RefreshVoices()
    {
        var current = _voiceService.SelectedVoice;
        AvailableVoices.Clear();
        foreach (var v in _voiceService.AvailableVoices)
            AvailableVoices.Add(v);

        if (!string.IsNullOrEmpty(_appSettings.VoiceName) && AvailableVoices.Contains(_appSettings.VoiceName))
            SelectedVoice = _appSettings.VoiceName;
        else if (current != null && AvailableVoices.Contains(current))
            SelectedVoice = current;
        else if (AvailableVoices.Count > 0)
            SelectedVoice = AvailableVoices[0];
    }

    public void ApplyStartupActions()
    {
        if (AutoConnect && !string.IsNullOrEmpty(_appSettings.LastConnectedDeviceInstanceId)
            && Guid.TryParse(_appSettings.LastConnectedDeviceInstanceId, out var deviceGuid))
        {
            var match = AvailableDevices.FirstOrDefault(d =>
                d.DeviceInstance != null && d.DeviceInstance.InstanceGuid == deviceGuid);
            if (match != null)
            {
                SelectedDevice = match;
                ConnectDevice();
            }
        }

        if (AutoStart && IsDeviceConnected)
        {
            IsRunning = true;
            ToggleTelemetry();
        }
    }

    public void RestoreButtonSettings()
    {
        SnapshotButtonComboIndex = _appSettings.SnapshotButtonComboIndex;
        PanicButtonComboIndex = _appSettings.PanicButtonComboIndex;

        if (!string.IsNullOrEmpty(_appSettings.PanicDeviceInstanceId) && Guid.TryParse(_appSettings.PanicDeviceInstanceId, out var guid))
        {
            var match = PanicDevices.FirstOrDefault(d =>
                d.DeviceInstance != null && d.DeviceInstance.InstanceGuid == guid);
            if (match != null)
                SelectedPanicDevice = match;
        }
    }

    [RelayCommand]
    private void RefreshRecordingDevices()
    {
        try
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
        catch (Exception ex)
        {
            StatusText = $"Audio device enumeration failed: {ex.Message}";
        }
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
        WriteDiagLog("START", $"MainWindow={mainWin?.GetType().Name ?? "null"}");
        if (mainWin == null) return;

        var dialog = new Views.FeedbackDialog { Owner = mainWin };
        if (dialog.ShowDialog() != true)
        {
            WriteDiagLog("CANCELLED", "User cancelled feedback dialog");
            return;
        }

        IsSendingDiagnosticPack = true;
        DiagnosticPackStatus = "Auto-saving...";
        StatusText = "Auto-saving profile and snapshot...";

        try
        {
            WriteDiagLog("STEP", "Auto-saving profile...");
            AutoSaveDiagnosticProfile();
            WriteDiagLog("STEP", "Auto-saving snapshot...");
            (mainWin as Views.MainWindow)?.AutoSaveSnapshot();

            StatusText = "Sending diagnostic pack...";
            DiagnosticPackStatus = "Sending...";
            var progress = new Progress<string>(msg =>
            {
                StatusText = msg;
                DiagnosticPackStatus = msg;
                WriteDiagLog("PROGRESS", msg);
            });
            WriteDiagLog("STEP", "Calling DiagnosticPackService.SendAsync...");
            var (success, message) = await DiagnosticPackService.SendAsync(dialog.Feedback, progress);
            StatusText = message;
            WriteDiagLog("RESULT", $"Success={success}, Message={message}");

            if (!success)
            {
                DiagnosticPackStatus = "Failed";
                MessageBox.Show(mainWin, $"{message}\n\nLog: {DiagLogDir()}", "Send Failed", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            else
            {
                DiagnosticPackStatus = message;
            }
        }
        catch (Exception ex)
        {
            StatusText = $"Error: {ex.Message}";
            DiagnosticPackStatus = "Error";
            WriteDiagLog("ERROR", $"{ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
            MessageBox.Show(mainWin, $"Failed to send diagnostics:\n\n{ex.Message}\n\nLog: {DiagLogDir()}", "Send Failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            IsSendingDiagnosticPack = false;
        }
    }

    private static string DiagLogDir()
    {
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "AcEvoFfbTuner", "diag_send.log");
    }

    private static void WriteDiagLog(string category, string detail)
    {
        try
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "AcEvoFfbTuner");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "diag_send.log"),
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [{category}] {detail}\n");
        }
        catch { }
    }

    private static void LogUpdate(string message)
    {
        try
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "AcEvoFfbTuner");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "update.log"),
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {message}\n");
        }
        catch { }
    }

    [RelayCommand]
    private async Task CheckForUpdatesAsync()
    {
        UpdateStatusText = "Checking for updates...";
        IsUpdateAvailable = false;

        try
        {
            var service = new GitHubUpdateService();
            LogUpdate($"CheckForUpdates: current version={service.CurrentVersion}, checking...");
            var update = await service.CheckForUpdateAsync();

            if (update != null)
            {
                _pendingUpdate = update;
                LatestVersionText = $"v{update.Version}";
                IsUpdateAvailable = true;
                UpdateStatusText = $"Update available: v{update.Version}";
                LogUpdate($"CheckForUpdates: update available v{update.Version}, url={update.DownloadUrl}");
            }
            else
            {
                UpdateStatusText = $"You're up to date (v{service.CurrentVersion.ToString(3)})";
                _pendingUpdate = null;
                LogUpdate("CheckForUpdates: up to date");
            }
        }
        catch (Exception ex)
        {
            UpdateStatusText = "Update check failed";
            LogUpdate($"CheckForUpdates FAILED: {ex.GetType().Name}: {ex.Message}");
        }
    }

    [RelayCommand]
    private async Task DownloadAndInstallUpdateAsync()
    {
        if (_pendingUpdate == null || IsDownloadingUpdate) return;

        IsDownloadingUpdate = true;
        DownloadProgressPercent = 0;
        UpdateStatusText = "Downloading update...";
        LogUpdate($"DownloadAndInstall: starting for v{_pendingUpdate.Version}");

        try
        {
            var progress = new Progress<DownloadProgress>(p =>
            {
                if (p.State == DownloadState.Downloading)
                {
                    DownloadProgressPercent = p.Percent;
                    UpdateStatusText = $"Downloading update... {p.Percent}%";
                }
                else
                {
                    UpdateStatusText = "Launching installer...";
                }
            });

            await GitHubUpdateService.DownloadAndInstallAsync(_pendingUpdate, progress);

            LogUpdate("DownloadAndInstall: installer launched successfully, shutting down app");
            UpdateStatusText = "Installer launched — closing app...";
            Dispose();
            Application.Current.Shutdown();
        }
        catch (Exception ex)
        {
            UpdateStatusText = $"Download failed: {ex.Message}";
            LogUpdate($"DownloadAndInstall FAILED: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
        }
        finally
        {
            IsDownloadingUpdate = false;
            DownloadProgressPercent = 0;
        }
    }

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
            if (!_uiUpdateTimer.IsEnabled) _uiUpdateTimer.Start();
            System.Windows.Application.Current.Dispatcher.Invoke(() =>
            {
                _raceInfoOverlay = new RaceInfoOverlay();
                _raceInfoOverlay.Closed += (_, _) => _raceInfoOverlay = null;
                _raceInfoOverlay.Show();
            });
        }
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
    }
}
