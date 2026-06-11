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
    private const int MaxSteps = 4;
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
    private bool _calibrationComplete; // true when strength calibration data collection is done
    private bool _polarityDetermined; // true after user answers the polarity question
    private int _polarityResultCooldown;
    private float _calibratedMasterGain = 1.0f; // Stores the pure 92% peak target from Step 2
    private float _intensityMultiplier = 1.0f; // Slider/Preset modifier (0.5 to 1.2)
    private float _polarityFlipTarget; // target MzFrontGain for gradual flip
    private float _polarityFlipStep;   // per-frame increment
    private int _polarityFlipFrames;   // frames remaining in gradual flip
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
    private float _initialMasterGainSnapshot = -1f;
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
    private float _step2RunningSum;
    private float _step2PeakForce; // R3E: peak |mainForce| for anti-clipping routine
    private float _step2MzSum; // running sum of |mzFront| for stable averaging
    private const int AutoSampleCount = 1200;
    private float[] _step2Samples = new float[AutoSampleCount];
    private int _step2Idx;
    private int _advanceStep = -1;
    private int _advanceStepDelay; // frames to wait before auto-advancing (~60 = 1s at 60fps)
    private int _logFrame;

    private readonly FfbPipeline _pipeline;
    private readonly FfbDeviceManager _deviceManager;
    private readonly TelemetryLoop _telemetryLoop;
    private readonly MainViewModel _viewModel;
    private readonly Action<string, ProfileScope> _saveCallback;

    private readonly string[] _stepTitles =
    [
        "Welcome & Safety",
        "Drive & Calibrate",
        "Intensity Preference",
        "Save Profile"
    ];

    private readonly string[] _panelNames =
    [
        nameof(PanelStep0),     // Step 1: Welcome
        nameof(PanelStep2),     // Step 2: Drive & Calibrate
        nameof(PanelStep3),     // Step 3: Intensity Preference
        nameof(PanelStep7)      // Step 4: Save Profile
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

        // Initialize LutCurve to match current CenterSuppressionDegrees for non-R3E pipelines.
        // R3E uses its own ApplyCenteringOverride with CenterSuppressionDegrees directly.
        if (_pipeline is not R3eFfbPipeline)
        {
            float initPower = 1.0f + (_pipeline.CenterSuppressionDegrees / 30f) * 3.0f;
            _pipeline.LutCurve.SetProgressive(initPower);
        }

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
        }
        _centerPhase = CenterPhase.PolarityDetect;

        // Populate detected game/car/track from viewmodel
        if (_viewModel != null)
        {
            _detectedGame = _viewModel.GameDisplayName ?? "";
            if (!string.IsNullOrEmpty(_viewModel.DetectedCarModel))
                _detectedCar = _viewModel.DetectedCarModel;
            if (!string.IsNullOrEmpty(_viewModel.DetectedTrackName))
                _detectedTrack = _viewModel.DetectedTrackName;
        }

        UpdateLabels();
        UpdateScopeDetectedInfo();
        Log($"OnLoaded: done OG={_pipeline.OutputGain:F3} Mz={_pipeline.ChannelMixer.MzFrontGain:F3} inv={needsInvert}");
    }

    private void UpdateLabels()
    {
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


    }

    private void EnableCenteringSliders(bool enable)
    {
        // Centering sliders removed from Drive step — kept as no-op for call compatibility
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

    private void OnPolarityYes(object sender, RoutedEventArgs e)
    {
        _polarityDetermined = true;

        // Start gradual flip over 30 frames (~0.5s) instead of instant reversal
        float currentMz = _pipeline.ChannelMixer.MzFrontGain;
        _polarityFlipTarget = -currentMz;
        _polarityFlipFrames = 30;
        _polarityFlipStep = (_polarityFlipTarget - currentMz) / _polarityFlipFrames;
        _calibratedMasterGain = _pipeline.MasterGain;

        // Commit final target to profile data model
        if (_viewModel?.SelectedProfile != null)
        {
            _viewModel.SelectedProfile.MzFront.Gain = _polarityFlipTarget;
            _viewModel.SelectedProfile.FyFront.Gain = -_pipeline.ChannelMixer.FyFrontGain;
        }

        // Hide question, show result
        PolarityQuestionPanel.Visibility = Visibility.Collapsed;
        SetPhaseBanner("\u2713 CENTERING FIXING", "Adjusting centering direction gradually...", "#FF66BB6A");
        Step3Status.Text = $"\u2713 Reversing centering direction smoothly \u2014 hold the wheel...";
        Step3Status.Foreground = new SolidColorBrush(Color.FromRgb(0x66, 0xBB, 0x6A));
        Log($"User polarity: Gradual flip from {currentMz:F2} to {_polarityFlipTarget:F2} over {_polarityFlipFrames} frames");
        
        // Auto-advance after the gradual flip completes
        _polarityResultCooldown = 60;
        _advanceStep = _currentStep;
        _advanceStepDelay = 30;
    }

    private void OnPolarityNo(object sender, RoutedEventArgs e)
    {
        _polarityDetermined = true;
        // User confirmed polarity is correct
        PolarityQuestionPanel.Visibility = Visibility.Collapsed;
        SetPhaseBanner("✓ CENTERING OK", "Centering direction is correct — continuing calibration", "#FF66BB6A");
        Step3Status.Text = "✓ Centering direction is correct — driving to calibrate force strength...";
        Step3Status.Foreground = new SolidColorBrush(Color.FromRgb(0x66, 0xBB, 0x6A));
        _polarityResultCooldown = 60;
        _advanceStep = _currentStep;
        _advanceStepDelay = 30;
        Log($"User polarity: MzFrontGain confirmed at {_pipeline.ChannelMixer.MzFrontGain:F2}");
    }

    private void OnCloseWizard(object sender, RoutedEventArgs e)
    {
        Log("Wizard closed by user");
        Close();
    }

    private void OnIntensitySliderChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppressSliderEvents) return;
        float sliderPercent = (float)Math.Round(e.NewValue, 0);
        ApplyIntensity(sliderPercent);
    }

    private void ApplyIntensity(float sliderPercent)
    {
        _intensityMultiplier = sliderPercent / 100f;
        float dynamicGain = _calibratedMasterGain * _intensityMultiplier;
        _pipeline.MasterGain = Math.Clamp(dynamicGain, 0.1f, 5.0f);
        if (LblIntensityPercent != null) LblIntensityPercent.Text = $"{sliderPercent:F0}%";
        if (Step3IntensityStatus != null)
        {
            Step3IntensityStatus.Text = $"Force strength: {(int)sliderPercent}% — {(sliderPercent < 75 ? "Light feel" : sliderPercent < 95 ? "Balanced feel" : "Full dynamic range")}";
            Step3IntensityStatus.Foreground = new SolidColorBrush(Color.FromRgb(0x66, 0xBB, 0x6A));
        }
    }

    private void OnIntensityPresetLight(object sender, RoutedEventArgs e)
    {
        if (SliderIntensity != null) SliderIntensity.Value = 65;
    }

    private void OnIntensityPresetBalanced(object sender, RoutedEventArgs e)
    {
        if (SliderIntensity != null) SliderIntensity.Value = 85;
    }

    private void OnIntensityPresetMax(object sender, RoutedEventArgs e)
    {
        if (SliderIntensity != null) SliderIntensity.Value = 100;
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
        // Step number announcement first, then instructions
        Speak($"Step {stepIndex + 1}. {title}.");
        // Voice instructions for each step
        switch (stepIndex)
        {
            case 0:
                Speak("When you are ready, drive onto the track and click next.");
                break;
            case 1:
                Speak("Drive through a few corners. I will check your centering direction and set your force strength.");
                break;
            case 2:
                Speak("Choose how heavy or light you want this car to feel, then click next.");
                break;
            case 3:
                Speak("Give your profile a name and click save and finish.");
                break;
        }
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

        // Gradual polarity flip: apply in small increments over 30 frames (~0.5s)
        // to prevent sudden force reversals that could rip the wheel from the user's hands.
        if (_polarityFlipFrames > 0)
        {
            _polarityFlipFrames--;
            float oldMz = _pipeline.ChannelMixer.MzFrontGain;
            float newMz = oldMz + _polarityFlipStep;
            // Check if crossing the target this frame
            if ((_polarityFlipStep > 0f && newMz >= _polarityFlipTarget) ||
                (_polarityFlipStep < 0f && newMz <= _polarityFlipTarget))
            {
                newMz = _polarityFlipTarget;
                _polarityFlipFrames = 0;
            }
            _pipeline.ChannelMixer.MzFrontGain = newMz;
            _pipeline.ChannelMixer.FyFrontGain += _polarityFlipStep; // same step for Fy
            // Sync viewmodel for save
            if (_viewModel != null)
            {
                _viewModel.MzFrontGain = newMz;
                _viewModel.FyFrontGain = _pipeline.ChannelMixer.FyFrontGain;
            }
        }

        // Welcome step: keep checking device connection so the Next button
        // enables once the user connects their wheel
        if (_currentStep == 0)
        {
            bool connected = _deviceManager.IsDeviceAcquired;
            if (connected != BtnNext.IsEnabled)
            {
                BtnNext.IsEnabled = connected;
                Step0Status.Text = connected
                    ? $"Wheel connected: {_deviceManager.ConnectedDevice?.ProductName ?? "Unknown"}"
                    : "Waiting for wheel connection...";
            }
        }

        // Wait for gradual flip to complete before advancing
        if (_advanceStep >= 0 && _polarityFlipFrames > 0)
        {
            // Flip still in progress — don't advance yet
        }
        else
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
            // Step 2: Drive & Calibrate
            // Run centering detection only until polarity is determined (user answered question)
            if (!_polarityDetermined)
                UpdateAutoCentering(speedKmh, mainForce, steerAngle);
            // Strength calibration always runs — it auto-advances when data collection completes
            UpdateAutoTyreForces(speedKmh, mainForce, steerAngle, mzFront);
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
        // mainForce × steerAngle > 0 (same sign). Negate for ALL games when inv=False
        // so the three polarity checks below work with the standard convention.
        float runCheck = _viewModel?.ForceInvertEnabled == true ? mainForce : -mainForce;

        // Only run centering phases until polarity is determined
        if (_polarityDetermined)
        {
            // User already answered the question -- no centering phases needed
            if (_centerPhase >= CenterPhase.Warmup)
            {
                // Skip all legacy centering phases
                _centerPhase = CenterPhase.PolarityDetect;
            }
            // Keep running PolarityDetect for status text display only
            if (_centerPhase == CenterPhase.PolarityDetect)
            {
                // Show calibration progress
                if (speedKmh > 15f)
                {
                    Step3Status.Text = _calibrationComplete
                        ? "Calibration complete -- see question above"
                        : $"Calibrating force strength... drive through corners";
                }
                else
                {
                    Step3Status.Text = speedKmh > 5f
                        ? $"Speed: {speedKmh:F0} km/h -- drive through a corner"
                        : "Drive to 20+ km/h and steer through a corner";
                }
                Step3Status.Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0xD6, 0x00));
            }
            return;
        }

        // --- Legacy centering detection (only runs until polarity is determined) ---
        switch (_centerPhase)
        {
            case CenterPhase.PolarityDetect:
                // Polarity question is shown after calibration completes (in strength calibration end).
                // Just wait here - don't show any question early.
                if (speedKmh > 15f)
                {
                    Step3Status.Text = _calibrationComplete
                        ? "Calibration complete - see question above"
                        : $"Calibrating force strength... drive through corners";
                }
                else
                {
                    Step3Status.Text = speedKmh > 5f
                        ? $"Speed: {speedKmh:F0} km/h - drive through a corner"
                        : "Drive to 20+ km/h and steer through a corner";
                }
                Step3Status.Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0xD6, 0x00));
                break;
            case CenterPhase.Warmup:
                if (speedKmh >= minSpeedForDetection)
                {
                    _centerPhase = CenterPhase.StraightDetect;
                    _centerSampleIndex = 0;
                    _straightDetectResult = 0f;
                    _straightBiasSum = 0f;
                }
                Step3Status.Text = speedKmh > 5f
                    ? $"Speed: {speedKmh:F0} km/h — calibrating centering and strength"
                    : "Drive forward — calibration starts at 20+ km/h";
                Step3Status.Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0xD6, 0x00));
                break;

            case CenterPhase.StraightDetect:
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
                Step3Status.Text = $"Calibrating — drive normally through corners";
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
                }
                break;

            case CenterPhase.CornerDetect:
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

                    // If FFB is inverted, the PolarityDetect at start already handled the sign.
                    // Do NOT flip mid-corner — that reverses force direction while driving,
                    // causing the wheel to suddenly pull the opposite way (dangerous).
                    // Just log it so the user can correct on the FFB tuning page.
                    if (forcesAreInverted)
                    {
                        Log($"WARNING: CornerDetect detected inverted forces ({runawayRatio*100f:F0}% runaway). Centering sign may need adjustment in FFB tuning page.");
                    }

                    // For R3E: set CSD for V-shape suppression, then advance immediately.
                    // For AC EVO/ACC: CSD does nothing in base pipeline (ApplyCenteringOverride is no-op).
                    // Don't touch LUT curve either — it's a global shaper, not center-specific.
                    if (_pipeline is R3eFfbPipeline)
                    {
                        float targetCs = 2.0f;
                        _pipeline.CenterSuppressionDegrees = targetCs;
                        // Centering slider removed — CSD set on pipeline directly
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
                    // Auto-advance removed — only strength calibration advances

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
                        // Centering slider preview removed — value stays on pipeline
                    }

                    Step3Status.Text = isTurning
                        ? $"Fight: {(float)_cornerFightFrames / Math.Max(_cornerTurnFrames, 1) * 100f:F0}%  |  Turns: {_cornerTurnFrames}/{CornerSampleCount}"
                        : $"Waiting for turns — {_cornerTurnFrames}/{minTurnFramesBeforeExit} min, {_cornerTotalFrames}/{CornerFrameTimeout} timeout";
                    Step3Status.Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0xD6, 0x00));
                }
                break;

            case CenterPhase.FinalTune:

                if (!_lutApplied)
                {
                    // FinalTune init: do NOT touch LUT curve, NoiseFloor, or MzFrontGain.
                    // These are the main app's responsibility, not the wizard's.

                    _lutApplied = true;
                    _finalTuneCleanSamples = 0;
                    _finalTuneTotalFrames = 0;
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
                    // FinalTune auto-advance removed — only strength calibration advances
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
        }
    }

    private void UpdateAutoTyreForces(float speedKmh, float mainForce, float steerAngle, float mzFront)
    {
        // Take a one-time snapshot of the user's starting MasterGain when sampling begins
        if (_initialMasterGainSnapshot < 0f)
        {
            _initialMasterGainSnapshot = _pipeline.MasterGain;
        }

        const float minSpeedForSampling = 15f;
        int maxSamples = (_pipeline is R3eFfbPipeline) ? 600 : 1200;

        if (_step2Phase == AutoPhase.Warmup)
        {
            Step2Status.Text = speedKmh > 5f ? "Accelerate to 20+ km/h to start sampling..." : "Drive forward to start calibration...";
            Step2Status.Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0xD6, 0x00));
            Step3Status.Text = Step2Status.Text;
            Step3Status.Foreground = Step2Status.Foreground;

            if (speedKmh >= 20f)
            {
                _step2Phase = AutoPhase.Sampling;
                _step2Idx = 0;
                _step2PeakForce = 0f;
                _cornerFightFrames = 0;
                _cornerTurnFrames = 0;
                _initialMasterGainSnapshot = _pipeline.MasterGain;
            }
            return;
        }

        if (_step2Phase == AutoPhase.Sampling)
        {
            if (speedKmh > minSpeedForSampling)
            {
                float absF = Math.Abs(mainForce);
                _step2Idx++;

                // 1. FORCE CALIBRATION: Track absolute peak physics force hit during the run
                if (absF > _step2PeakForce)
                {
                    _step2PeakForce = absF;
                }

                // 2. POLARITY DETECTION: Track if force pushes AWAY from center
                // If user turns right (steer > 0) and force pushes right (mainForce > 0), it's a runaway force.
                // Normalize convention based on ForceInvertEnabled state
                float behaviorCheck = _viewModel?.ForceInvertEnabled == true ? mainForce : -mainForce;
                if (Math.Abs(steerAngle) > 0.05f)
                {
                    _cornerTurnFrames++;
                    if (behaviorCheck * steerAngle > 0.001f) // Same sign = pulling away from center into lock
                    {
                        _cornerFightFrames++;
                    }
                }

                // Live UI Updates
                float progressPct = ((float)_step2Idx / maxSamples) * 100f;
                Step2Status.Text = $"Analyzing: {progressPct:F0}% | Peak Force: {(_step2PeakForce * 100f):F0}%";
                Step2Status.Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0xD6, 0x00));
                Step3Status.Text = $"Analyzing: {progressPct:F0}% | Peak Force: {(_step2PeakForce * 100f):F0}%";
                Step3Status.Foreground = Step2Status.Foreground;
            }

            // Sampling Complete -> Run the Math Output
            if (_step2Idx >= maxSamples)
            {
                _step2Phase = AutoPhase.Manual;
                _calibrationComplete = true;

                // --- FORCE ADJUSTMENT MATH ---
                // Always scale relative to the static initial snapshot to prevent exponential loops
                float targetUtilization = 0.92f;
                if (_step2PeakForce > 0.01f)
                {
                    float calculatedMG = _initialMasterGainSnapshot * (targetUtilization / _step2PeakForce);
                    _calibratedMasterGain = Math.Clamp(calculatedMG, 0.2f, 5.0f);
                    _pipeline.MasterGain = _calibratedMasterGain;
                    Log($"Strength cal: peak={_step2PeakForce*100f:F1}% MG={_initialMasterGainSnapshot:F2}->{_calibratedMasterGain:F2}");
                }
                else
                {
                    _calibratedMasterGain = _initialMasterGainSnapshot;
                }

                // Safe-range clamp for MzFrontGain (preserve sign)
                float safeMzStart = _pipeline.ChannelMixer.MzFrontGain;
                if (Math.Abs(safeMzStart) < 0.01f || Math.Abs(safeMzStart) > 2.0f)
                {
                    float sign = MathF.Sign(safeMzStart);
                    if (MathF.Abs(sign) < 0.001f) sign = 1f;
                    float corrected = 0.5f * sign;
                    _pipeline.ChannelMixer.MzFrontGain = corrected;
                    SliderMzGain.Value = corrected;
                    LblMzGain.Text = corrected.ToString("F2");
                }

                // --- AUTOMATIC WHEEL CENTERING POLARITY ---
                float runawayRatio = _cornerTurnFrames > 0 ? (float)_cornerFightFrames / _cornerTurnFrames : 0f;

                if (runawayRatio > 0.55f) // Definite inverted behavior detected
                {
                    Log($"Auto-detected INVERTED polarity (Runaway: {runawayRatio * 100f:F0}%). Applying centering correction.");

                    // Automatically invert the target for the gradual correction shift
                    float currentMz = _pipeline.ChannelMixer.MzFrontGain;
                    _polarityFlipTarget = -currentMz;
                    _polarityFlipFrames = 30;
                    _polarityFlipStep = (_polarityFlipTarget - currentMz) / _polarityFlipFrames;

                    Step2Status.Text = "Inverted forces detected! Auto-correcting centering direction...";
                    Step2Status.Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0x17, 0x44));
                    Step3Status.Text = Step2Status.Text;
                    Step3Status.Foreground = Step2Status.Foreground;

                    if (PolarityQuestionPanel != null) PolarityQuestionPanel.Visibility = Visibility.Collapsed;
                    _polarityDetermined = true;
                    _advanceStep = _currentStep;
                    _advanceStepDelay = 35;
                }
                else
                {
                    Log($"Auto-detected CORRECT polarity (Runaway: {runawayRatio * 100f:F0}%). Wheel centers properly.");
                    Step2Status.Text = $"Calibration complete! Force aligned (Peak: {(_step2PeakForce * 100f):F0}%) — advancing to intensity tune";
                    Step2Status.Foreground = new SolidColorBrush(Color.FromRgb(0x66, 0xBB, 0x6A));
                    Step3Status.Text = $"Calibration complete! Peak force: {(_step2PeakForce * 100f):F0}%";
                    Step3Status.Foreground = Step2Status.Foreground;

                    if (PolarityQuestionPanel != null) PolarityQuestionPanel.Visibility = Visibility.Collapsed;
                    _polarityDetermined = true;
                    _advanceStep = _currentStep;
                    _advanceStepDelay = 30;
                }
            }
        }
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


}








