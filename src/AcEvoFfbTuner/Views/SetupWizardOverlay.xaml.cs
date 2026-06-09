using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using AcEvoFfbTuner.Core;
using AcEvoFfbTuner.Core.DirectInput;
using AcEvoFfbTuner.Core.FfbProcessing;
using AcEvoFfbTuner.Core.Profiles;
using AcEvoFfbTuner.ViewModels;

namespace AcEvoFfbTuner.Views;

public partial class SetupWizardOverlay : Window
{
    private bool _isTransparent;
    private bool _isCompact;
    private bool _suppressSliderEvents;
    private int _currentStep;
    private const int MaxSteps = 8;
    private bool _isStrongWheelbase;
    private string _detectedGame = "";
    private string _detectedCar = "";
    private string _detectedTrack = "";

    private static void Log(string msg)
    {
        try { File.AppendAllText(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AcEvoFfbTuner", "setupwizard.log"),
            $"[{DateTime.Now:HH:mm:ss.fff}] {msg}\n"); } catch { }
    }

    private void Speak(string text)
    {
        _viewModel?.VoiceService?.Speak(text);
    }

    // Auto-centering + cornering state for Step 1 — 3 phases
    private enum CenterPhase { PolarityDetect, Warmup, StraightDetect, CornerDetect, FinalTune }
    private CenterPhase _centerPhase = CenterPhase.Warmup;
    private int _polarityResultCooldown;
    private const int CenterSampleCount = 600;
    private int _centerSampleIndex;
    private float[] _centerForceSamples = new float[CenterSampleCount];
    private float _straightDetectResult;
    private float _straightBiasSum; // signed sum to detect DC offset direction
    private float _totalCenterSuppressAdjust; // cumulative cap

    // Corner detection uses ratio of |force| / |steer| across all turns
    private const int CornerSampleCount = 600;
    private const int CornerFrameTimeout = 3000; // forced transition after this many total CornerDetect frames
    private int _cornerTurnFrames;        // frames where actively cornering
    private int _cornerTotalFrames;       // total frames in CornerDetect (timeout)
    private float _cornerRatioSum;        // sum of |force|/|steer| ratios
    private int _cornerFightFrames;       // frames where force opposes steer direction
    private int _cornerInvertedRunawayFrames; // frames where force pulls away from center into lock
    private bool _wasTurning;
    private bool _lutApplied;

    // FinalTune phase — continued monitoring after Soft Center is applied
    private const int FinalTuneSampleCount = 600;
    private const int FinalTuneFrameTimeout = 3600;
    private int _finalTuneCleanSamples;
    private int _finalTuneTotalFrames;

    // Auto-detect state for steps 2-5
    private enum AutoPhase { Warmup, Sampling, Applied, Manual }
    private AutoPhase _step2Phase = AutoPhase.Warmup;
    private AutoPhase _step3Phase = AutoPhase.Warmup;
    private AutoPhase _step4Phase = AutoPhase.Warmup;
    private AutoPhase _step5Phase = AutoPhase.Warmup;
    private float _step2RunningSum;
    private float _step2PeakForce; // R3E: peak |mainForce| for anti-clipping routine
    private float _step2MzSum; // running sum of |mzFront| for stable averaging
    private float _step3RunningSum;
    private float _step4RunningMax;
    private const int AutoSampleCount = 1200;
    private float[] _step2Samples = new float[AutoSampleCount];
    private float[] _step3Samples = new float[AutoSampleCount];
    private float[] _step4Samples = new float[AutoSampleCount];
    private float[] _step5Samples = new float[AutoSampleCount];
    private int _step2Idx, _step3Idx, _step4Idx, _step5Idx;
    private int _advanceStep = -1;
    private int _advanceStepDelay; // frames to wait before auto-advancing (~60 = 1s at 60fps)
    private int _step2PolarityFlips; // inversion detection counter during Step 1 sampling
    private int _step2PolarityMsgCooldown; // frames to show polarity message before returning to normal
    private int _logFrame;

    private readonly FfbPipeline _pipeline;
    private readonly FfbDeviceManager _deviceManager;
    private readonly TelemetryLoop _telemetryLoop;
    private readonly MainViewModel _viewModel;
    private readonly Action<string, ProfileScope> _saveCallback;

    private readonly string[] _stepTitles =
    [
        "Welcome & Safety",
        "Wheel Centering",
        "Core Tyre Forces",
        "Master Output Gain",
        "Damping & Friction",
        "Curb & Vibration",
        "Review & Confirm",
        "Save Profile"
    ];

    private readonly string[] _panelNames =
    [
        nameof(PanelStep0),
        nameof(PanelStep2),     // Wheel Centering (was PanelStep1)
        nameof(PanelStep1),     // Core Tyre Forces (was PanelStep2)
        nameof(PanelStep3),
        nameof(PanelStep4),
        nameof(PanelStep5),
        nameof(PanelStep6),
        nameof(PanelStep7)
    ];

    public SetupWizardOverlay(
        FfbPipeline pipeline, 
        FfbDeviceManager deviceManager, 
        TelemetryLoop telemetryLoop,
        MainViewModel viewModel,
        Action<string, ProfileScope> saveCallback)
    {
        Log("Constructor: start");

        // Assign fields BEFORE InitializeComponent — XAML parsing fires
        // ValueChanged events that access these fields.
        _pipeline = pipeline;
        _deviceManager = deviceManager;
        _telemetryLoop = telemetryLoop;
        _viewModel = viewModel;
        _saveCallback = saveCallback;
        Log("Fields assigned");

        Log("InitComp before try");
        try
        {
            InitializeComponent();
            Log("InitComp OK");
        }
        catch (Exception ex)
        {
            Log($"InitComp FAILED: {ex.GetType().Name}: {ex.Message}");
            Log($"Stack: {ex.StackTrace}");
            throw;
        }
        Log("InitComp passed");

        _currentStep = 0;
        Log("Constructor: calling UpdateSafetyState");
        UpdateSafetyState();
        Log("Constructor: calling ShowStep(0)");
        ShowStep(_currentStep);
        Log("Constructor: done");
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        Log("OnLoaded: start");
        var workingArea = SystemParameters.WorkArea;
        Left = (workingArea.Width - Width) / 2;
        Top = 20;

        // Initialize sliders from current pipeline state
        // Suppress ValueChanged events to prevent pipeline state cascade on startup.
        _suppressSliderEvents = true;
        SliderOutputGain.Value = _pipeline.OutputGain;
        SliderMasterGain.Value = _pipeline.OutputClipper.SoftClipThreshold;
        SliderFriction.Value = _pipeline.Damping.FrictionLevel;
        SliderDamping.Value = _pipeline.Damping.SpeedDampingCoefficient;
        
        SliderMzGain.Value = _pipeline.ChannelMixer.MzFrontGain;
        SliderFxGain.Value = _pipeline.ChannelMixer.FxFrontGain;
        SliderFyGain.Value = _pipeline.ChannelMixer.FyFrontGain;
        ChkMzEnabled.IsChecked = _pipeline.ChannelMixer.MzFrontEnabled;
        ChkFxEnabled.IsChecked = _pipeline.ChannelMixer.FxFrontEnabled;
        ChkFyEnabled.IsChecked = _pipeline.ChannelMixer.FyFrontEnabled;
        ChkFyInverted.IsChecked = _pipeline.ChannelMixer.FyInverted;

        SliderKerbGain.Value = _pipeline.VibrationMixer.KerbGain;
        SliderRoadGain.Value = _pipeline.VibrationMixer.RoadGain;
        SliderSlipGain.Value = _pipeline.SlipEnhancer.SlipRatioGain;
        SliderAbsGain.Value = _pipeline.VibrationMixer.AbsGain;
        SliderVibMaster.Value = _pipeline.VibrationMixer.MasterGain;
        _suppressSliderEvents = false;

        SliderCenterSuppress.Value = _pipeline.CenterSuppressionDegrees;
        // Initialize LutCurve to match current CenterSuppressionDegrees for non-R3E pipelines.
        // R3E uses its own ApplyCenteringOverride with CenterSuppressionDegrees directly.
        if (_pipeline is not R3eFfbPipeline)
        {
            float initPower = 1.0f + (_pipeline.CenterSuppressionDegrees / 30f) * 3.0f;
            _pipeline.LutCurve.SetProgressive(initPower);
        }
        SliderSlewRate.Value = _pipeline.MaxSlewRate;

        // Apply ForceInvertEnabled from wheelbase connection test to Mz/Fy signs
        bool needsInvert = _viewModel?.ForceInvertEnabled == true;
        if (needsInvert)
        {
            _pipeline.ChannelMixer.MzFrontGain = -Math.Abs(_pipeline.ChannelMixer.MzFrontGain);
            _pipeline.ChannelMixer.FyFrontGain = -Math.Abs(_pipeline.ChannelMixer.FyFrontGain);
            SliderMzGain.Value = _pipeline.ChannelMixer.MzFrontGain;
            SliderFyGain.Value = _pipeline.ChannelMixer.FyFrontGain;
        }

        // R3E: Fully automated baseline from a blank slate.
        // Fx/Fy are artificial slip reconstructions — zero them out.
        // MzFrontGain is auto-calculated: R3E's Mz synthesis produces a natively
        // negative raw vector (Mz = -absForce * steerSign). Standard hardware
        // (inv=False) needs -1.0f to flip it back toward center. Inverted
        // hardware (inv=True) is already naturally aligned with R3E's sign,
        // so +1.0f is correct. This reconciles the global hardware polarity
        // state with R3E's unique physics signature — zero-touch, no guesswork.
        if (_pipeline is R3eFfbPipeline)
        {
            _pipeline.ChannelMixer.FxFrontGain = 0.0f;
            _pipeline.ChannelMixer.FyFrontGain = 0.0f;
            _pipeline.ChannelMixer.FxFrontEnabled = false;
            _pipeline.ChannelMixer.FyFrontEnabled = false;
            _pipeline.ChannelMixer.MzRearEnabled = false;
            SliderFxGain.Value = 0.0f;
            SliderFyGain.Value = 0.0f;

            // Sync to viewmodel so PushValuesToPipeline doesn't overwrite with old values
            if (_viewModel != null)
            {
                _viewModel.FxFrontGain = 0.0f;
                _viewModel.FyFrontGain = 0.0f;
            }

            // Leave MzFrontGain at pipeline default — CornerDetect's polarity
            // correction (using polarityF fix) will determine the correct sign.
            if (_viewModel?.SelectedProfile != null)
            {
                _viewModel.SelectedProfile.FxFront.Gain = 0.0f;
                _viewModel.SelectedProfile.FyFront.Gain = 0.0f;
            }
            _centerPhase = CenterPhase.PolarityDetect;
            Log($"R3E OnLoaded: MzFrontGain left at {_pipeline.ChannelMixer.MzFrontGain:F2} — PolarityDetect will determine sign");
        }

        UpdateLabels();
        UpdateScopeDetectedInfo();
        Speak("Setup wizard loaded. Drive safely and follow the on screen instructions.");
        Log($"OnLoaded: done OG={_pipeline.OutputGain:F3} Mz={_pipeline.ChannelMixer.MzFrontGain:F3} inv={needsInvert}");
    }

    private void UpdateLabels()
    {
        if (LblOutputGain != null) LblOutputGain.Text = _pipeline.OutputGain.ToString("F2");
        if (LblMasterGain != null) LblMasterGain.Text = _pipeline.OutputClipper.SoftClipThreshold.ToString("F2");
        if (LblFriction != null) LblFriction.Text = _pipeline.Damping.FrictionLevel.ToString("F2");
        if (LblDamping != null) LblDamping.Text = _pipeline.Damping.SpeedDampingCoefficient.ToString("F2");
        
        if (LblMzGain != null) LblMzGain.Text = _pipeline.ChannelMixer.MzFrontGain.ToString("F2");
        if (LblFxGain != null) LblFxGain.Text = _pipeline.ChannelMixer.FxFrontGain.ToString("F2");
        if (LblFyGain != null) LblFyGain.Text = _pipeline.ChannelMixer.FyFrontGain.ToString("F2");

        if (LblKerbGain != null) LblKerbGain.Text = _pipeline.VibrationMixer.KerbGain.ToString("F2");
        if (LblRoadGain != null) LblRoadGain.Text = _pipeline.VibrationMixer.RoadGain.ToString("F2");
        if (LblSlipGain != null) LblSlipGain.Text = _pipeline.SlipEnhancer.SlipRatioGain.ToString("F2");
        if (LblAbsGain != null) LblAbsGain.Text = _pipeline.VibrationMixer.AbsGain.ToString("F2");
        if (LblVibMaster != null) LblVibMaster.Text = _pipeline.VibrationMixer.MasterGain.ToString("F2");

        if (LblCenterSuppress != null) LblCenterSuppress.Text = _pipeline.CenterSuppressionDegrees.ToString("F1") + "°";
        if (LblSlewRate != null) LblSlewRate.Text = _pipeline.MaxSlewRate.ToString("F2");
    }

    private void EnableCenteringSliders(bool enable)
    {
        SliderCenterSuppress.IsEnabled = enable;
        SliderSlewRate.IsEnabled = enable;
    }

    private void OnDeactivated(object sender, EventArgs e)
    {
        if (!_isTransparent) Topmost = true; // Pin it
    }

    private void Window_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left)
        {
            try { DragMove(); } catch { }
        }
    }

    private void Window_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        // Simple sizing adjustments via mouse wheel scroll
        double scale = e.Delta > 0 ? 1.05 : 0.95;
        double newWidth = Width * scale;
        double newHeight = Height * scale;

        if (newWidth >= MinWidth && newWidth <= 800) Width = newWidth;
        if (newHeight >= MinHeight && newHeight <= 1000) Height = newHeight;
    }

    private void OnBack(object sender, RoutedEventArgs e)
    {
        if (_currentStep > 0)
        {
            _currentStep--;
            ShowStep(_currentStep);
        }
    }

    private void OnNext(object sender, RoutedEventArgs e)
    {
        if (_currentStep == MaxSteps - 1)
        {
            SaveAndFinish();
            return;
        }

        _currentStep++;
        ShowStep(_currentStep);
    }

    private void ShowStep(int stepIndex)
    {
        Log($"ShowStep: step={stepIndex} title={_stepTitles[stepIndex]}");
        // Toggle step visibility containers
        for (int i = 0; i < _panelNames.Length; i++)
        {
            var element = FindName(_panelNames[i]) as UIElement;
            if (element != null)
                element.Visibility = i == stepIndex ? Visibility.Visible : Visibility.Collapsed;
        }

        // Setup texts
        string title = _stepTitles[stepIndex];
        // R3E uses Step 3 for MasterGain calibration (not channel balancing)
        if (stepIndex == 2 && _pipeline is R3eFfbPipeline)
            title = "Force Strength Calibration";
        StepTitleText.Text = $"Step {stepIndex + 1}: {title}";
        StepProgressText.Text = $"{stepIndex + 1} / {MaxSteps}";
        ProgressBar.Value = stepIndex + 1;

        BtnBack.IsEnabled = stepIndex > 0;
        
        if (stepIndex == MaxSteps - 1)
        {
            BtnNext.Content = "Save & Finish";
            BtnNext.Background = new SolidColorBrush(Color.FromRgb(0x23, 0x86, 0x36)); // Green call to action
            if (FooterStatus != null)
                FooterStatus.Text = "Changes are LIVE but not saved — click Save & Finish to persist";
        }
        else
        {
            BtnNext.Content = "Next";
            BtnNext.Background = new SolidColorBrush(Color.FromRgb(0x44, 0x55, 0x66));
            if (FooterStatus != null)
                FooterStatus.Text = "Drag to move  |  Scroll to resize  |  Changes apply in real-time";
        }

        if (_currentStep == 0)
        {
            bool connected = _deviceManager.IsDeviceAcquired;
            Step0Status.Text = connected
                ? $"Wheel connected: {_deviceManager.ConnectedDevice?.ProductName ?? "Unknown"}"
                : "Waiting for wheel connection...";
            Step0Status.Foreground = connected
                ? new SolidColorBrush(Color.FromRgb(0x66, 0xBB, 0x6A))
                : new SolidColorBrush(Color.FromRgb(0xFF, 0xD6, 0x00));
            BtnNext.IsEnabled = connected;
        }

        if (_currentStep == 6)
        {
            PopulateReviewValues();
        }

        UpdateSafetyBannerVisibility();

        // Voice notification for the new step (skip Step 0 — already announced on load)
        if (stepIndex > 0)
        {
            string stepName = _stepTitles[stepIndex];
            Speak($"Step {stepIndex + 1}. {stepName}.");
        }
    }

    private void PopulateReviewValues()
    {
        if (ReviewCenterSuppress != null)
                    ReviewCenterSuppress.Text = _pipeline.CenterSuppressionDegrees.ToString("F1") + "\u00B0" +
                        $"  |  Sharp: {_pipeline.CenterSharpnessDegrees:F1}\u00B0" +
                        $"  |  Str: {_pipeline.CoreForceMultiplier:F1}x";
        if (ReviewSlewRate != null)
            ReviewSlewRate.Text = _pipeline.MaxSlewRate.ToString("F2");
        if (ReviewMzGain != null)
        {
            ReviewMzGain.Text = _pipeline.ChannelMixer.MzFrontGain.ToString("F2");
            if (SliderReviewMzGain != null)
                SliderReviewMzGain.Value = _pipeline.ChannelMixer.MzFrontGain;
        }
        if (ReviewFxGain != null)
            ReviewFxGain.Text = _pipeline.ChannelMixer.FxFrontGain.ToString("F2");
        if (ReviewFyGain != null)
            ReviewFyGain.Text = _pipeline.ChannelMixer.FyFrontGain.ToString("F2");
        if (ReviewFyInv != null)
            ReviewFyInv.Text = _pipeline.ChannelMixer.FyInverted ? "On" : "Off";
        if (ReviewOutputGain != null)
            ReviewOutputGain.Text = _pipeline.OutputGain.ToString("F2");
        if (ReviewMasterGain != null)
        {
            ReviewMasterGain.Text = _pipeline.MasterGain.ToString("F2");
            if (SliderReviewMasterGain != null)
                SliderReviewMasterGain.Value = _pipeline.MasterGain;
        }
        if (ReviewDamping != null)
            ReviewDamping.Text = _pipeline.Damping.SpeedDampingCoefficient.ToString("F1");
        if (ReviewFriction != null)
            ReviewFriction.Text = _pipeline.Damping.FrictionLevel.ToString("F2");
        if (ReviewKerbGain != null)
            ReviewKerbGain.Text = _pipeline.VibrationMixer.KerbGain.ToString("F2");
        if (ReviewRoadGain != null)
            ReviewRoadGain.Text = _pipeline.VibrationMixer.RoadGain.ToString("F2");
        if (ReviewSlipGain != null)
            ReviewSlipGain.Text = _pipeline.SlipEnhancer.SlipRatioGain.ToString("F2");
        if (ReviewAbsGain != null)
            ReviewAbsGain.Text = _pipeline.VibrationMixer.AbsGain.ToString("F2");
    }

    private void UpdateSafetyState()
    {
        float maxTorque = _viewModel.WheelMaxTorqueNm;
        _isStrongWheelbase = maxTorque >= 8f;

        Step0SafetyBox.Visibility = _isStrongWheelbase ? Visibility.Visible : Visibility.Collapsed;
        if (_isStrongWheelbase)
            Step0TorqueText.Text = maxTorque.ToString("F1");
    }

    private void UpdateSafetyBannerVisibility()
    {
        bool shouldShow = _isStrongWheelbase && _currentStep >= 1 && _currentStep <= 6;
        SafetyBanner.Visibility = shouldShow ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateScopeDetectedInfo()
    {
        string parts = "";
        if (!string.IsNullOrEmpty(_detectedGame))
            parts += $"Game: {_detectedGame}";
        if (!string.IsNullOrEmpty(_detectedCar))
            parts += $"  ·  Car: {_detectedCar}";
        if (!string.IsNullOrEmpty(_detectedTrack))
            parts += $"  ·  Track: {_detectedTrack}";

        if (ScopeDetectedText != null)
            ScopeDetectedText.Text = string.IsNullOrEmpty(parts)
                ? "No game data detected yet — start your game to auto-fill scope info"
                : $"Detected: {parts}";
    }

    private ProfileScope GetSelectedScope()
    {
        if (ScopePerGame?.IsChecked == true) return ProfileScope.PerGame;
        if (ScopePerCar?.IsChecked == true) return ProfileScope.PerCar;
        if (ScopePerTrack?.IsChecked == true) return ProfileScope.PerTrack;
        if (ScopePerCarAndTrack?.IsChecked == true) return ProfileScope.PerCarAndTrack;
        return ProfileScope.General;
    }

    private void SaveAndFinish()
    {
        string name = TxtProfileName.Text.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            TxtProfileName.Focus();
            TxtProfileName.BorderBrush = new SolidColorBrush(Color.FromRgb(0xFF, 0x17, 0x44));
            return;
        }

        var scope = GetSelectedScope();
        // Re-sync viewmodel from pipeline — something (profile auto-match, LoadProfileValues)
        // may have overwritten it since the polarity test. PushValuesToPipeline reads from
        // viewmodel properties, so this ensures the corrected values survive the save.
        if (_pipeline is R3eFfbPipeline && _viewModel != null)
        {
            _viewModel.MzFrontGain = _pipeline.ChannelMixer.MzFrontGain;
            _viewModel.FyFrontGain = _pipeline.ChannelMixer.FyFrontGain;
        }
        // Diagnostic: capture pipeline and viewmodel state at save time
        float pipeMz = _pipeline.ChannelMixer.MzFrontGain;
        float vmMz = _viewModel?.MzFrontGain ?? float.NaN;
        float vmFx = _viewModel?.FxFrontGain ?? float.NaN;
        float vmFy = _viewModel?.FyFrontGain ?? float.NaN;
        Log($"SAVE: pipeline MzFront={pipeMz:F3}  viewModel MzFront={vmMz:F3} FxFront={vmFx:F3} FyFront={vmFy:F3}");
        _saveCallback(name, scope);
        Speak("Profile saved. Setup complete.");
        Close();
    }

    public void UpdateLiveValues(float speedKmh, float mainForce, float steerAngle, bool isClipping, float mzFront)
    {
        if (++_logFrame % 100 == 0)
            Log($"ULV: step={_currentStep} speed={speedKmh:F1} force={mainForce:F4}");

        // Auto-advance: when an auto-detect step completes, transition to next
        if (_advanceStep >= 0 && _advanceStep == _currentStep)
        {
            if (_advanceStepDelay > 0)
            {
                _advanceStepDelay--;
            }
            else
            {
                _advanceStep = -1;
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (_currentStep < MaxSteps - 1) OnNext(null, null);
                }));
            }
        }

        if (_currentStep == 1)
        {
            UpdateAutoCentering(speedKmh, mainForce, steerAngle);
        }
        else if (_currentStep == 2)
        {
            UpdateAutoTyreForces(speedKmh, mainForce, steerAngle, mzFront);
        }
        else if (_currentStep == 3)
        {
            UpdateAutoOutputGain(speedKmh, mainForce, isClipping);
        }
        else if (_currentStep == 4)
        {
            UpdateAutoDamping(speedKmh, mainForce, steerAngle);
        }
        else if (_currentStep == 5)
        {
            UpdateAutoVibration(speedKmh, mainForce, steerAngle);
        }
    }

    private void UpdateAutoCentering(float speedKmh, float mainForce, float steerAngle)
    {
        // NOTE: steerAngle is normalized [-1, 1] (not degrees)
        const float minSpeedForDetection = 20f;
        const float steerStraightThreshold = 0.03f;  // only very straight for straight samples
        const float steerCornerThreshold = 0.05f;    // gentle turns and sweepers included
        const float pullForceThreshold = 0.003f;
        const float fightThreshold = 0.20f;
        const float maxCenterSuppressCumulative = 15f; // hard cap: don't add more than this total

        // Moza (inv=False) reverses centering polarity: correct centering produces
        // mainForce × steerAngle > 0 (same sign). Negate for Moza on R3E so all
        // three polarity checks below work correctly with the standard convention.
        float runCheck = (_viewModel?.ForceInvertEnabled == true || _pipeline is not R3eFfbPipeline)
            ? mainForce
            : -mainForce;

        switch (_centerPhase)
        {
            case CenterPhase.PolarityDetect:
                // If we just detected polarity, show result for ~1s then advance to Warmup
                if (_polarityResultCooldown > 0)
                {
                    _polarityResultCooldown--;
                    if (_polarityResultCooldown == 0)
                    {
                        // Skip Warmup — user is already driving. Transition straight
                        // to StraightDetect to sample centering pull.
                        _centerPhase = CenterPhase.StraightDetect;
                        _centerSampleIndex = 0;
                        _straightDetectResult = 0f;
                        _straightBiasSum = 0f;
                        Step3Status.Text = "Sampling straight-line force — keep driving straight...";
                        Step3Status.Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0xD6, 0x00));
                    }
                    break;
                }
                SetPhaseBanner("POLARITY TEST", "Drive 15+ km/h, STEER LEFT or RIGHT — detects centering direction", "#FFAA00");
        // Skip runaway guard during PolarityDetect — the polarity test handles
        // the sign determination, and the guard would reset to Warmup before
        // the test can complete.
        if (_centerPhase != CenterPhase.PolarityDetect && speedKmh > 15f && Math.Abs(steerAngle) > 0.05f)
                {
                    // User is turning (either direction). Determine correct polarity by checking
                    // force direction relative to steer. Works for both left and right turns.
                    // Moza convention (inv=False, positive=LEFT) has inverted polarity vs
                    // standard (inv=True, positive=RIGHT).
                    bool isInvHw = _viewModel?.ForceInvertEnabled == true;
                    float polarityCheck = (isInvHw ? mainForce : -mainForce) * steerAngle;
                    if (polarityCheck > 0.005f)
                    {
                        _pipeline.ChannelMixer.MzFrontGain *= -1f;
                        _pipeline.ChannelMixer.FyFrontGain *= -1f;
                        // Commit corrected polarity to profile data model so profile
                        // reloads don't revert it (ApplyToPipeline would restore old value).
                        if (_viewModel?.SelectedProfile != null)
                        {
                            _viewModel.SelectedProfile.MzFront.Gain = _pipeline.ChannelMixer.MzFrontGain;
                            _viewModel.SelectedProfile.FyFront.Gain = _pipeline.ChannelMixer.FyFrontGain;
                        }
                        // Also sync to viewmodel properties so PushValuesToPipeline
                        // reads the corrected values when saving the profile.
                        if (_viewModel != null)
                        {
                            _viewModel.MzFrontGain = _pipeline.ChannelMixer.MzFrontGain;
                            _viewModel.FyFrontGain = _pipeline.ChannelMixer.FyFrontGain;
                        }
                        SetPhaseBanner("✓ POLARITY FIXED", $"MzFrontGain flipped to {_pipeline.ChannelMixer.MzFrontGain:F2} — force now centers", "#FF66BB6A");
                        Step3Status.Text = $"✓ Inverted! Flipped to {_pipeline.ChannelMixer.MzFrontGain:F2} — auto-advancing...";
                        Step3Status.Foreground = new SolidColorBrush(Color.FromRgb(0x66, 0xBB, 0x6A));
                        Log($"R3E PolarityDetect: MzFrontGain flipped to {_pipeline.ChannelMixer.MzFrontGain:F2}");
                    }
                    else
                    {
                        SetPhaseBanner("✓ POLARITY CORRECT", $"MzFrontGain stays at {_pipeline.ChannelMixer.MzFrontGain:F2} — centering fine", "#FF66BB6A");
                        Step3Status.Text = $"✓ Polarity correct at {_pipeline.ChannelMixer.MzFrontGain:F2} — auto-advancing...";
                        Step3Status.Foreground = new SolidColorBrush(Color.FromRgb(0x66, 0xBB, 0x6A));
                        Log($"R3E PolarityDetect: MzFrontGain correct at {_pipeline.ChannelMixer.MzFrontGain:F2}");
                    }
                    _polarityResultCooldown = 60; // Show result for ~1s before advancing
                }
                else
                {
                    Step3Status.Text = "DRIVE 15+ km/h, then tilt STEER LEFT or RIGHT ~30° — I'll detect centering direction";
                    Step3Status.Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0xD6, 0x00));
                }
                break;

            case CenterPhase.Warmup:
                SetPhaseBanner("PHASE 1/3", "Drive straight at 20+ km/h — detecting centering pull", "#FFF0883E");
                if (speedKmh >= minSpeedForDetection)
                {
                    _centerPhase = CenterPhase.StraightDetect;
                    _centerSampleIndex = 0;
                    _straightDetectResult = 0f;
                    _straightBiasSum = 0f;
                    Step3Status.Text = "Sampling straight-line force — keep driving straight...";
                    Step3Status.Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0xD6, 0x00));
                    Speak("Phase 1 of 3. Drive straight and hold the wheel steady.");
                }
                else
                {
                    Step3Status.Text = speedKmh > 5f
                        ? $"Speed: {speedKmh:F0} km/h — accelerate to 20+ km/h"
                        : "Drive forward — auto-tune starts at 20+ km/h";
                    Step3Status.Foreground = new SolidColorBrush(Color.FromRgb(0x66, 0xBB, 0x6A));
                }
                break;

            case CenterPhase.StraightDetect:
                SetPhaseBanner("PHASE 1/3", "Drive straight — sampling centering pull", "#FFF0883E");
                if (speedKmh < minSpeedForDetection * 0.7f)
                {
                    _centerPhase = CenterPhase.Warmup;
                    Step3Status.Text = "Speed dropped below threshold — accelerate to 20+ km/h and drive straight";
                    Step3Status.Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0xD6, 0x00));
                    break;
                }

                if (Math.Abs(steerAngle) <= steerStraightThreshold)
                {
                    _centerForceSamples[_centerSampleIndex++] = mainForce;
                    _straightBiasSum += mainForce;
                }

                float pct = (float)_centerSampleIndex / CenterSampleCount * 100f;
                Step3Status.Text = $"Sampling straight-line force... {_centerSampleIndex}/{CenterSampleCount} ({pct:F0}%)";
                Step3Status.Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0xD6, 0x00));

                if (_centerSampleIndex >= CenterSampleCount)
                {
                    // Absolute average — how much total force is present
                    _straightDetectResult = 0f;
                    for (int i = 0; i < CenterSampleCount; i++)
                        _straightDetectResult += Math.Abs(_centerForceSamples[i]);
                    _straightDetectResult /= CenterSampleCount;

                    // Signed average — detects DC bias (force consistently to one side)
                    float biasMean = _straightBiasSum / CenterSampleCount;

                    // If there's a DC bias, raise noise floor to gate it out
                    const float biasThreshold = 0.002f;
                    if (Math.Abs(biasMean) > biasThreshold)
                    {
                        float currentNoise = _pipeline.NoiseFloor;
                        float targetNoise = Math.Max(currentNoise, Math.Abs(biasMean) * 3f);
                        targetNoise = Math.Clamp(targetNoise, 0.003f, 0.03f);
                        _pipeline.NoiseFloor = targetNoise;

                        Step3Status.Text = $"DC bias {biasMean * 1000f:F2}mNm detected — NoiseFloor raised to {targetNoise * 1000f:F0}mNm";
                        Step3Status.Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0xD6, 0x00));
                    }
                    else
                    {
                        Step3Status.Text = "✓ Straight-line sampling complete. Now: drive through turns normally.";
                        Step3Status.Foreground = new SolidColorBrush(Color.FromRgb(0x66, 0xBB, 0x6A));
                    }

                    _cornerTurnFrames = 0;
                    _cornerTotalFrames = 0;
                    _cornerRatioSum = 0f;
                    _cornerFightFrames = 0;
                    _cornerInvertedRunawayFrames = 0;
                    _wasTurning = false;
                    _totalCenterSuppressAdjust = 0f;
                    _centerPhase = CenterPhase.CornerDetect;
                    Speak("Phase 2 of 3. Drive through turns normally.");
                }
                break;

            case CenterPhase.CornerDetect:
                SetPhaseBanner("PHASE 2/3", "Drive through turns — detecting corner fight", "#FF56D4A0");
                bool isTurning = Math.Abs(steerAngle) > steerCornerThreshold;

                _cornerTotalFrames++;

                if (isTurning && speedKmh > 10f)
                {
                    _cornerTurnFrames++;
                    float absSteer = Math.Max(Math.Abs(steerAngle), 0.01f);
                    float ratio = Math.Abs(mainForce) / absSteer;
                    _cornerRatioSum += Math.Min(ratio, 2f);

                    if (runCheck * steerAngle < 0f)
                        _cornerFightFrames++;

                    // Detect inverted runaway: force pulls AWAY from center into lock
                    if (runCheck * steerAngle > 0.001f)
                    {
                        _cornerInvertedRunawayFrames++;
                        // OutputGain reduction removed — it corrupted Step 3 tuning.
                        // Inverted FFB is handled by MzFrontGain/FyFrontGain flips below.
                    }

                    _wasTurning = true;
                }

                // Transition: full samples, corner exit with minimum, or hard timeout
                const int minTurnFramesBeforeExit = 200;
                bool enoughData = _cornerTurnFrames >= CornerSampleCount;
                bool exitingCorner = _wasTurning && !isTurning && _cornerTurnFrames >= minTurnFramesBeforeExit;
                bool timedOut = _cornerTotalFrames >= CornerFrameTimeout;

                if (enoughData || exitingCorner || timedOut)
                {
                    // Average force/steer ratio across all corner samples
                    float avgRatio = _cornerTurnFrames > 0
                        ? _cornerRatioSum / _cornerTurnFrames
                        : 0f;

                    // Fight proportion: fraction of corner frames where force opposes steer
                    float fightRatio = _cornerTurnFrames > 0
                        ? (float)_cornerFightFrames / _cornerTurnFrames
                        : 0f;

                    // Runaway proportion: fraction of corner frames where force pulls into lock
                    float runawayRatio = _cornerTurnFrames > 0
                        ? (float)_cornerInvertedRunawayFrames / _cornerTurnFrames
                        : 0f;
                    bool forcesAreInverted = runawayRatio > 0.60f;
                    bool cornerHasFight = !forcesAreInverted && fightRatio > fightThreshold && avgRatio > 0.1f;

                    // If FFB is inverted, flip the sign alignment so centering returns to normal
                    if (forcesAreInverted)
                    {
                        _pipeline.ChannelMixer.MzFrontGain *= -1f;
                        _pipeline.ChannelMixer.FyFrontGain *= -1f;
                    }

                    // For R3E: set CSD for V-shape suppression, then advance immediately.
                    // For AC EVO/ACC: CSD does nothing in base pipeline (ApplyCenteringOverride is no-op).
                    // Don't touch LUT curve either — it's a global shaper, not center-specific.
                    if (_pipeline is R3eFfbPipeline)
                    {
                        float targetCs = 2.0f;
                        _pipeline.CenterSuppressionDegrees = targetCs;
                        SliderCenterSuppress.Value = targetCs;
                        LblCenterSuppress.Text = targetCs.ToString("F1") + "\u00B0";
                        Log($"R3E: CenterSuppression set to {targetCs:F1}\u00B0 — immediate advance");
                    }
                    else
                    {
                        Log($"AC Evo: Centering detect complete — no CSD/LUT adjustment needed");
                    }

                    // Always advance immediately for all games
                    _centerPhase = CenterPhase.FinalTune;
                    _lutApplied = false;
                    EnableCenteringSliders(true);
                    _advanceStep = _currentStep;
                    _advanceStepDelay = 60;
                    Speak("Centering auto tune complete.");

                    string invertedMsg = forcesAreInverted
                        ? $"FFB inverted! Gains flipped (runaway {runawayRatio * 100f:F0}%)"
                        : "";
                    string biasInfo = _pipeline.NoiseFloor > 0.005f
                        ? $" | NoiseFloor: {_pipeline.NoiseFloor * 1000f:F0}mNm"
                        : "";
                    string straightMsg = !forcesAreInverted && _straightDetectResult > pullForceThreshold
                        ? $"Straight pull corrected"
                        : "No straight pull";
                    string cornerMsg = cornerHasFight
                        ? $" | Corner fight: {fightRatio * 100f:F0}% opposing (ratio {avgRatio:F2})"
                        : " | No corner fight";
                    Step3Status.Text = $"✓ {straightMsg}{cornerMsg}{biasInfo}  |  Center Suppress: {_pipeline.CenterSuppressionDegrees:F1}°{invertedMsg}";
                    Step3Status.Foreground = new SolidColorBrush(Color.FromRgb(0x66, 0xBB, 0x6A));
                }
                else
                {
                    // Live preview: every 100 frames, compute running suppression and update slider
                    bool showLivePreview = _cornerTurnFrames % 100 == 0 && _cornerTurnFrames > 50;
                    if (showLivePreview && _cornerTurnFrames > 0)
                    {
                        float runRatio = _cornerRatioSum / _cornerTurnFrames;
                        float runFight = (float)_cornerFightFrames / _cornerTurnFrames;
                        float previewAdjust = Math.Clamp(runRatio * 8f, 0.5f, 10f);
                        if (runFight > 0.5f) previewAdjust *= 1.3f;
                        float previewCS = Math.Min(_pipeline.CenterSuppressionDegrees + previewAdjust, 30f);
                        SliderCenterSuppress.Value = previewCS;
                        LblCenterSuppress.Text = previewCS.ToString("F1") + "\u00B0";
                    }

                    Step3Status.Text = isTurning
                        ? $"Fight: {(float)_cornerFightFrames / Math.Max(_cornerTurnFrames, 1) * 100f:F0}%  |  Turns: {_cornerTurnFrames}/{CornerSampleCount}"
                        : $"Waiting for turns — {_cornerTurnFrames}/{minTurnFramesBeforeExit} min, {_cornerTotalFrames}/{CornerFrameTimeout} timeout";
                    Step3Status.Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0xD6, 0x00));
                }
                break;

            case CenterPhase.FinalTune:
                SetPhaseBanner("PHASE 3/3", "Auto-tuning center response — drive normally", "#FF56D4A0");

                if (!_lutApplied)
                {
                    float cs = _pipeline.CenterSuppressionDegrees;
                    // LUT-based center suppression only for non-R3E pipelines.
                    // R3E uses ApplyCenteringOverride with CenterSuppressionDegrees directly.
                    if (_pipeline is not R3eFfbPipeline)
                    {
                        float power = 1.0f + (cs / 30f) * 3.0f;
                        _pipeline.LutCurve.SetProgressive(power);
                    }
                    if (_pipeline.NoiseFloor < 0.005f)
                        _pipeline.NoiseFloor = 0.005f;
                    // MzFrontGain clamp only for AC EVO/ACC — R3E centering comes from
                    // CenterSuppressionDegrees via ApplyCenteringOverride, and Mz gain
                    // may legitimately be negative or outside the 0.4-1.2 range.
                    if (_pipeline is not R3eFfbPipeline)
                    {
                        if (_pipeline.ChannelMixer.MzFrontGain < 0.4f || _pipeline.ChannelMixer.MzFrontGain > 1.2f)
                            _pipeline.ChannelMixer.MzFrontGain = 0.5f;
                    }

                    _lutApplied = true;
                    _finalTuneCleanSamples = 0;
                    _finalTuneTotalFrames = 0;
                    EnableCenteringSliders(false);
                    Step3Status.Text = "Soft Center applied. Continuing auto-tune: drive normally to verify center feel.";
                    Step3Status.Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0xD6, 0x00));
                }

                // Monitor during FinalTune — observe, report, but DON'T auto-correct.
                // The baseline (progressive 1.8 LUT, 5° center suppression, 0.005 noise,
                // MzFrontGain 0.5) provides natural self-aligning torque behavior.
                // Auto-correction was counterproductive: it reduced Mz gain and raised
                // suppression, killing all force feel while leaving normal SAT as dominant.
                _finalTuneTotalFrames++;
                bool isSteering = Math.Abs(steerAngle) > 0.04f && speedKmh > 10f;

                if (isSteering)
                {
                    float absSteer = Math.Max(Math.Abs(steerAngle), 0.01f);
                    float ratio = Math.Abs(mainForce) / absSteer;
                    float expectedRatio = MathF.Pow(absSteer, 0.8f);
                    float excessFactor = ratio / Math.Max(expectedRatio, 0.001f);
                    _finalTuneCleanSamples++;

                    Step3Status.Text = $"Centering ratio: {excessFactor:F1}x expected  |  Steer: {steerAngle:F2}  |  Force: {mainForce:F3}  |  Clean: {_finalTuneCleanSamples}/{FinalTuneSampleCount}";
                    Step3Status.Foreground = new SolidColorBrush(Color.FromRgb(0x66, 0xBB, 0x6A));
                }
                else
                {
                    _finalTuneCleanSamples++;
                    Step3Status.Text = $"Monitoring... {_finalTuneCleanSamples}/{FinalTuneSampleCount} clean frames";
                    Step3Status.Foreground = new SolidColorBrush(Color.FromRgb(0x66, 0xBB, 0x6A));
                }

                if (_finalTuneCleanSamples >= FinalTuneSampleCount || _finalTuneTotalFrames >= FinalTuneFrameTimeout)
                {
                    EnableCenteringSliders(true);
                    SetPhaseBanner("✓ COMPLETE", "Auto-tune finished — tapping Next", "#FF66BB6A");
                    Step3Status.Text = $"✓ Auto-tune complete. CS={_pipeline.CenterSuppressionDegrees:F1}° Mz={_pipeline.ChannelMixer.MzFrontGain:F2}";
                    Step3Status.Foreground = new SolidColorBrush(Color.FromRgb(0x66, 0xBB, 0x6A));
                    _advanceStep = _currentStep;
                    _advanceStepDelay = 90;
                    Speak("Centering auto tune complete.");
                }
                break;
        }
    }

    private void SetPhaseBanner(string badge, string instruction, string hexColor)
    {
        if (PhaseBadge != null) PhaseBadge.Text = badge;
        if (PhaseInstruction != null) PhaseInstruction.Text = instruction;
        try
        {
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hexColor));
            if (PhaseBadgeBorder != null) PhaseBadgeBorder.Background = brush;
        }
        catch { }
    }

    private void EnableStepSliders(int step, bool enabled)
    {
        switch (step)
        {
            case 2: SliderMzGain.IsEnabled = enabled; SliderFxGain.IsEnabled = enabled;
                    SliderFyGain.IsEnabled = enabled; break;
            case 3: SliderDamping.IsEnabled = enabled; SliderFriction.IsEnabled = enabled; break;
            case 4: SliderOutputGain.IsEnabled = enabled; SliderMasterGain.IsEnabled = enabled; break;
            case 5: SliderKerbGain.IsEnabled = enabled; SliderRoadGain.IsEnabled = enabled;
                    SliderSlipGain.IsEnabled = enabled; SliderAbsGain.IsEnabled = enabled; break;
        }
    }

    private void UpdateAutoTyreForces(float speedKmh, float mainForce, float steerAngle, float mzFront)
    {
        switch (_step2Phase)
        {
            case AutoPhase.Warmup:
                Step2Status.Text = speedKmh > 5f ? "Drive to 20+ km/h — app reads force levels" : "Drive forward — app reads forces at 20+ km/h";
                Step2Status.Foreground = new SolidColorBrush(Color.FromRgb(0x66, 0xBB, 0x6A));
                if (speedKmh >= 20f) { _step2Phase = AutoPhase.Sampling; _step2Idx = 0; _step2RunningSum = 0f; _step2MzSum = 0f; _step2PolarityFlips = 0; _step2PolarityMsgCooldown = 0; _step2PeakForce = 0f; }
                break;
            case AutoPhase.Sampling:
                if (_pipeline is R3eFfbPipeline)
                {
                    // ── R3E Anti-Clipping Routine ──
                    // RaceRoom's SteeringForce is the final physics output.
                    // No channel balancing needed. Track peak force and scale
                    // MasterGain so the hardest corner uses ~92% of wheelbase range.
                    // Use 600 samples (half the standard) since R3E just needs peak tracking.
                    int r3eMaxSamples = 600;
                    if (speedKmh > 10f)
                    {
                        float absF = Math.Abs(mainForce);
                        _step2Samples[_step2Idx] = Math.Max(absF, 0.001f);
                        _step2RunningSum += _step2Samples[_step2Idx];
                        if (absF > _step2PeakForce)
                        {
                            _step2PeakForce = absF;
                            // Apply MasterGain live as new peak is detected so the user
                            // feels the calibration happening in real-time.
                            if (_step2PeakForce > 0.01f && _step2PeakForce < 0.92f)
                            {
                                // Use current MasterGain as reference so the computed gain
                                // properly scales from the existing baseline, not a fixed 1.0.
                                float liveGain = Math.Min(_pipeline.MasterGain * (0.92f / _step2PeakForce), 5.0f);
                                _pipeline.MasterGain = liveGain;
                                SliderMasterGain.Value = liveGain;
                                LblMasterGain.Text = liveGain.ToString("F2");
                            }
                        }
                        _step2Idx++;
                        // Show live peak as percentage
                        float pct = _step2PeakForce * 100f;
                        Step2Status.Text = $"Peak force: {pct:F0}% — {_step2Idx}/{r3eMaxSamples}  |  MasterGain: {_pipeline.MasterGain:F2}";
                        Step2Status.Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0xD6, 0x00));
                    }
                    if (_step2Idx >= r3eMaxSamples)
                    {
                        float peak = _step2PeakForce;
                        float targetUtilization = 0.92f;
                        float currentMasterGain = _pipeline.MasterGain;
                        if (peak > 0.01f && peak < targetUtilization)
                        {
                            float newMasterGain = Math.Min(currentMasterGain * (targetUtilization / peak), 5.0f);
                            _pipeline.MasterGain = newMasterGain;
                            SliderMasterGain.Value = newMasterGain;
                            LblMasterGain.Text = newMasterGain.ToString("F2");
                            Step2Status.Text = $"Peak {peak * 100f:F0}% → MasterGain boosted to {newMasterGain:F2} (targeting 92%)";
                            Log($"R3E anti-clip: peak={peak*100f:F1}% OG changed {currentMasterGain:F2}→{newMasterGain:F2}");
                        }
                        else if (peak >= targetUtilization)
                        {
                            Step2Status.Text = $"Peak {peak * 100f:F0}% — already utilising wheelbase well — MasterGain kept at {currentMasterGain:F2}";
                            Log($"R3E anti-clip: peak={peak*100f:F1}% ≥92% — MasterGain unchanged at {currentMasterGain:F2}");
                        }
                        else
                        {
                            Step2Status.Text = $"Peak {peak * 100f:F0}% — very low force at wheel";
                            Log($"R3E anti-clip: peak={peak*100f:F1}% <1% — very low force, MasterGain unchanged");
                        }
                        _step2Phase = AutoPhase.Manual;
                        _advanceStep = _currentStep;
                        _advanceStepDelay = 90;
                        Step2Status.Foreground = new SolidColorBrush(Color.FromRgb(0x66, 0xBB, 0x6A));
                    }
                }
                else
                {
                    // ── Standard (AC EVO / ACC) Mz channel balancing ──
                    if (speedKmh > 10f)
                    {
                        _step2Samples[_step2Idx] = Math.Max(Math.Abs(mainForce), 0.001f);
                        _step2RunningSum += _step2Samples[_step2Idx];
                        _step2MzSum += Math.Abs(mzFront);
                        _step2Idx++;
                    }
                    float runAvg = _step2Idx > 0 ? _step2RunningSum / _step2Idx : 0f;
                    // Live Mz magnitude: adjust from raw Mz channel (before LUT/damping/gain).
                    float avgMzChan = _step2Idx > 0 ? _step2MzSum / _step2Idx : 0f;
                    if (_step2Idx > 100 && avgMzChan > 0.02f)
                    {
                        float absTarget = Math.Clamp(
                            Math.Abs(_pipeline.ChannelMixer.MzFrontGain) * (0.4f / avgMzChan), 0.1f, 6.0f);
                        float sign = MathF.Sign(_pipeline.ChannelMixer.MzFrontGain);
                        if (MathF.Abs(sign) < 0.001f) sign = 1f;
                        float targetMz = absTarget * sign;
                        _pipeline.ChannelMixer.MzFrontGain = targetMz;
                        SliderMzGain.Value = targetMz;
                        LblMzGain.Text = targetMz.ToString("F2");
                    }
                    // Polarity check: if force pulls same direction as steer (away from center),
                    // the Mz sign is inverted. Flip Mz/Fy when consistently detected during turns.
                    if (_step2Idx > 50 && speedKmh > 20f && Math.Abs(steerAngle) > 0.08f)
                    {
                        if (mainForce * steerAngle > 0.005f)
                        {
                            _step2PolarityFlips++;
                            if (_step2PolarityFlips >= 30)
                            {
                                _pipeline.ChannelMixer.MzFrontGain *= -1f;
                                _pipeline.ChannelMixer.FyFrontGain *= -1f;
                                _step2PolarityFlips = 0;
                                _step2PolarityMsgCooldown = 60;
                            }
                        }
                    }
                    else if (Math.Abs(steerAngle) < 0.03f)
                    {
                        _step2PolarityFlips = 0;
                    }

                    // Show polarity message during cooldown, then resume normal status
                    if (_step2PolarityMsgCooldown > 0)
                    {
                        _step2PolarityMsgCooldown--;
                        Step2Status.Text = $"Polarity inverted! Mz/Fy flipped — Mz: {_pipeline.ChannelMixer.MzFrontGain:F2} — {_step2Idx}/{AutoSampleCount}";
                        Step2Status.Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0xD6, 0x00));
                    }
                    else
                    {
                        Step2Status.Text = $"Avg: {runAvg * 1000f:F0}mNm | MzG: {_pipeline.ChannelMixer.MzFrontGain:F2} mzCh: {mzFront:F3} — {_step2Idx}/{AutoSampleCount}";
                        Step2Status.Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0xD6, 0x00));
                    }
                    Step2Status.Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0xD6, 0x00));
                    if (_step2Idx >= AutoSampleCount)
                    {
                        _step2Phase = AutoPhase.Manual; EnableStepSliders(2, true);
                        _advanceStep = _currentStep;
                        _advanceStepDelay = 90;
                        Step2Status.Text = $"Core forces tuned — MzG: {_pipeline.ChannelMixer.MzFrontGain:F2} (avg {runAvg * 1000f:F0}mNm, ch {mzFront:F3})";
                        Step2Status.Foreground = new SolidColorBrush(Color.FromRgb(0x66, 0xBB, 0x6A));
                        Speak("Core tyre forces tuned.");
                    }
                }
                break;
        }
    }

    private void UpdateAutoDamping(float speedKmh, float mainForce, float steerAngle)
    {
        switch (_step3Phase)
        {
            case AutoPhase.Warmup:
                Step5Status.Text = speedKmh > 5f ? "Drive to 20+ km/h — app sets damping baseline" : "Drive forward — app reads damping at 20+ km/h";
                Step5Status.Foreground = new SolidColorBrush(Color.FromRgb(0x66, 0xBB, 0x6A));
                if (speedKmh >= 20f) { _step3Phase = AutoPhase.Sampling; _step3Idx = 0; _step3RunningSum = 0f; }
                break;
            case AutoPhase.Sampling:
                if (speedKmh > 10f)
                {
                    _step3Samples[_step3Idx] = Math.Abs(mainForce);
                    _step3RunningSum += _step3Samples[_step3Idx];
                    _step3Idx++;
                }
                // Live real-time: compute running average and apply slider + pipeline every frame
                if (_step3Idx > 0)
                {
                    float avg = _step3RunningSum / _step3Idx;
                    float targetDamp = _pipeline is R3eFfbPipeline
                        ? Math.Clamp(avg * 15f, 0.5f, 2.0f)
                        : Math.Clamp(avg * 40f, 3f, 6f);
                    _pipeline.Damping.SpeedDampingCoefficient = targetDamp;
                    SliderDamping.Value = targetDamp;
                    LblDamping.Text = targetDamp.ToString("F1");
                    Step5Status.Text = $"Damping: {targetDamp:F1} — {_step3Idx}/{AutoSampleCount}";
                }
                else
                {
                    Step5Status.Text = $"Monitoring damping... {_step3Idx}/{AutoSampleCount}";
                }
                Step5Status.Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0xD6, 0x00));
                if (_step3Idx >= AutoSampleCount)
                {
                    float finalAvg = _step3RunningSum / AutoSampleCount;
                    // R3E uses a gentler damping curve (factor ×15 instead of ×40, range 0.5-2.0)
                    // because its SpeedDampingCoefficient directly scales gyroscopic force.
                    // The 3-6 range was designed for AC EVO and creates 10-20× excessive
                    // damping at high speed, making the wheel feel dead.
                    float finalDamp = _pipeline is R3eFfbPipeline
                        ? Math.Clamp(finalAvg * 15f, 0.5f, 2.0f)
                        : Math.Clamp(finalAvg * 40f, 3f, 6f);
                    _pipeline.Damping.SpeedDampingCoefficient = finalDamp;
                    SliderDamping.Value = finalDamp;
                    LblDamping.Text = finalDamp.ToString("F1");
                    _step3Phase = AutoPhase.Manual; EnableStepSliders(3, true);
                    _advanceStep = _currentStep;
                    _advanceStepDelay = 90;
                    Step5Status.Text = $"Damping set to {finalDamp:F1} — tapping Next";
                    Step5Status.Foreground = new SolidColorBrush(Color.FromRgb(0x66, 0xBB, 0x6A));
                    Speak("Damping and friction tuning complete.");
                }
                break;
        }
    }

    private void UpdateAutoOutputGain(float speedKmh, float mainForce, bool isClipping)
    {
        switch (_step4Phase)
        {
            case AutoPhase.Warmup:
                Step4Status.Text = "Drive hard — app monitors force levels";
                Step4Status.Foreground = new SolidColorBrush(Color.FromRgb(0x66, 0xBB, 0x6A));
                if (speedKmh >= 30f) { _step4Phase = AutoPhase.Sampling; _step4Idx = 0; _step4RunningMax = 0f; }
                break;
            case AutoPhase.Sampling:
                if (speedKmh > 10f)
                {
                    _step4Samples[_step4Idx] = mainForce;
                    _step4RunningMax = Math.Max(_step4RunningMax, Math.Abs(mainForce));
                    _step4Idx++;
                }
                Step4Status.Text = isClipping
                    ? $"CLIPPING! Peak: {_step4RunningMax * 100f:F0}% — {_step4Idx}/{AutoSampleCount}"
                    : $"Peak force: {_step4RunningMax * 100f:F0}% — {_step4Idx}/{AutoSampleCount}";
                Step4Status.Foreground = isClipping ? new SolidColorBrush(Color.FromRgb(0xFF, 0x17, 0x44)) : new SolidColorBrush(Color.FromRgb(0xFF, 0xD6, 0x00));
                if (_step4Idx >= AutoSampleCount)
                {
                    float maxF = _step4RunningMax;
                    if (maxF < 0.65f)
                    {
                        _pipeline.OutputGain = Math.Min(_pipeline.OutputGain * 1.25f, 2.0f);
                        SliderOutputGain.Value = _pipeline.OutputGain; LblOutputGain.Text = _pipeline.OutputGain.ToString("F2");
                        Step4Status.Text = $"Forces low — Output Gain boosted to {_pipeline.OutputGain:F2}";
                    }
                    else Step4Status.Text = $"Peak force {maxF * 100f:F0}% — Output Gain kept at {_pipeline.OutputGain:F2}";
                    Speak("Force level calibrated.");
                    _step4Phase = AutoPhase.Manual; EnableStepSliders(4, true);
                    _advanceStep = _currentStep;
                    _advanceStepDelay = 90; // 1.5s delay so user sees final value
                    Step4Status.Foreground = new SolidColorBrush(Color.FromRgb(0x66, 0xBB, 0x6A));
                }
                break;
        }
    }

    private void UpdateAutoVibration(float speedKmh, float mainForce, float steerAngle)
    {
        switch (_step5Phase)
        {
            case AutoPhase.Warmup:
                Step6Status.Text = speedKmh > 5f ? "Drive to 20+ km/h — app reads vibration" : "Drive forward — app reads vibration at 20+ km/h";
                Step6Status.Foreground = new SolidColorBrush(Color.FromRgb(0x66, 0xBB, 0x6A));
                if (speedKmh >= 20f) { _step5Phase = AutoPhase.Sampling; _step5Idx = 0; }
                break;
            case AutoPhase.Sampling:
                if (speedKmh > 10f) _step5Samples[_step5Idx++] = Math.Abs(mainForce);
                Step6Status.Text = $"Monitoring surface feel... {_step5Idx}/{AutoSampleCount}";
                Step6Status.Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0xD6, 0x00));
                if (_step5Idx >= AutoSampleCount)
                {
                    _step5Phase = AutoPhase.Manual; EnableStepSliders(5, true);
                    _advanceStep = _currentStep;
                    Step6Status.Text = "Vibration levels sampled — tapping Next"; Step6Status.Foreground = new SolidColorBrush(Color.FromRgb(0x66, 0xBB, 0x6A));
                    Speak("Vibration levels sampled.");
                }
                break;
        }
    }

    private void OnOutputGainChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppressSliderEvents) return;
        float val = (float)Math.Round(e.NewValue, 2);
        _pipeline.OutputGain = val;
        if (LblOutputGain != null) LblOutputGain.Text = val.ToString("F2");
    }

    private void OnMasterGainChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppressSliderEvents) return;
        float val = (float)Math.Round(e.NewValue, 2);
        _pipeline.MasterGain = val;
        if (LblMasterGain != null) LblMasterGain.Text = val.ToString("F2");
    }

    private void OnVibMasterGainChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppressSliderEvents) return;
        float val = (float)Math.Round(e.NewValue, 2);
        _pipeline.VibrationMixer.MasterGain = val;
        if (LblVibMaster != null) LblVibMaster.Text = val.ToString("F2");
    }

    // ─── Step 3 slider handlers ───
    private void OnFrictionChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppressSliderEvents) return;
        float val = (float)Math.Round(e.NewValue, 2);
        _pipeline.Damping.FrictionLevel = val;
        if (LblFriction != null) LblFriction.Text = val.ToString("F2");
    }

    private void OnDampingChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppressSliderEvents) return;
        float val = (float)Math.Round(e.NewValue, 2);
        _pipeline.Damping.SpeedDampingCoefficient = val;
        if (LblDamping != null) LblDamping.Text = val.ToString("F2");
    }

    // ─── Step 4 slider/checkbox handlers ───
    private void OnMzGainChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppressSliderEvents) return;
        float val = (float)Math.Round(e.NewValue, 2);
        _pipeline.ChannelMixer.MzFrontGain = val;
        if (LblMzGain != null) LblMzGain.Text = val.ToString("F2");
    }

    private void OnFxGainChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppressSliderEvents) return;
        float val = (float)Math.Round(e.NewValue, 2);
        _pipeline.ChannelMixer.FxFrontGain = val;
        if (LblFxGain != null) LblFxGain.Text = val.ToString("F2");
    }

    private void OnFyGainChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppressSliderEvents) return;
        float val = (float)Math.Round(e.NewValue, 2);
        _pipeline.ChannelMixer.FyFrontGain = val;
        if (LblFyGain != null) LblFyGain.Text = val.ToString("F2");
    }

    private void OnMzEnabledChanged(object sender, RoutedEventArgs e)
    {
        if (_suppressSliderEvents) return;
        _pipeline.ChannelMixer.MzFrontEnabled = ChkMzEnabled.IsChecked == true;
    }

    private void OnFxEnabledChanged(object sender, RoutedEventArgs e)
    {
        if (_suppressSliderEvents) return;
        _pipeline.ChannelMixer.FxFrontEnabled = ChkFxEnabled.IsChecked == true;
    }

    private void OnFyEnabledChanged(object sender, RoutedEventArgs e)
    {
        if (_suppressSliderEvents) return;
        _pipeline.ChannelMixer.FyFrontEnabled = ChkFyEnabled.IsChecked == true;
    }

    private void OnFyInvertedChanged(object sender, RoutedEventArgs e)
    {
        if (_suppressSliderEvents) return;
        _pipeline.ChannelMixer.FyInverted = ChkFyInverted.IsChecked == true;
    }

    // ─── Step 5 slider handlers ───
    private void OnKerbGainChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppressSliderEvents) return;
        float val = (float)Math.Round(e.NewValue, 2);
        _pipeline.VibrationMixer.KerbGain = val;
        if (LblKerbGain != null) LblKerbGain.Text = val.ToString("F2");
    }

    private void OnRoadGainChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppressSliderEvents) return;
        float val = (float)Math.Round(e.NewValue, 2);
        _pipeline.VibrationMixer.RoadGain = val;
        if (LblRoadGain != null) LblRoadGain.Text = val.ToString("F2");
    }

    private void OnSlipGainChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppressSliderEvents) return;
        float val = (float)Math.Round(e.NewValue, 2);
        _pipeline.SlipEnhancer.SlipRatioGain = val;
        if (LblSlipGain != null) LblSlipGain.Text = val.ToString("F2");
    }

    private void OnAbsGainChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppressSliderEvents) return;
        float val = (float)Math.Round(e.NewValue, 2);
        _pipeline.VibrationMixer.AbsGain = val;
        if (LblAbsGain != null) LblAbsGain.Text = val.ToString("F2");
    }

    private void OnReviewMasterGainChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppressSliderEvents) return;
        float val = (float)Math.Round(e.NewValue, 2);
        _pipeline.MasterGain = val;
        if (ReviewMasterGain != null) ReviewMasterGain.Text = val.ToString("F2");
    }

    private void OnReviewMzGainChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppressSliderEvents) return;
        float val = (float)Math.Round(e.NewValue, 2);
        _pipeline.ChannelMixer.MzFrontGain = val;
        if (ReviewMzGain != null) ReviewMzGain.Text = val.ToString("F2");
    }

    private void OnReviewResetMasterGain(object sender, RoutedEventArgs e)
    {
        _pipeline.MasterGain = 1.0f;
        if (SliderReviewMasterGain != null) SliderReviewMasterGain.Value = 1.0f;
        if (ReviewMasterGain != null) ReviewMasterGain.Text = "1.00";
    }

    // ─── Step 5 slider handlers ───
    private void OnCenterSuppressChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppressSliderEvents) return;
        float val = (float)Math.Round(e.NewValue, 1);
        _pipeline.CenterSuppressionDegrees = val;
        if (LblCenterSuppress != null) LblCenterSuppress.Text = val.ToString("F1") + "°";
        if (_pipeline is not R3eFfbPipeline)
        {
            float power = 1.0f + (val / 30f) * 3.0f;
            _pipeline.LutCurve.SetProgressive(power);
        }
    }

    private void OnSlewRateChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppressSliderEvents) return;
        float val = (float)Math.Round(e.NewValue, 2);
        _pipeline.MaxSlewRate = val;
        if (LblSlewRate != null) LblSlewRate.Text = val.ToString("F2");
    }
}






