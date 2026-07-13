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

public sealed partial class MainViewModel
{
    public ObservableCollection<FfbProfile> Profiles { get; } = new();

    public bool IsBuiltInProfileSelected => SelectedProfile?.IsBuiltIn ?? false;

    partial void OnIsPerCarAutoLoadEnabledChanged(bool value)
    {
        _appSettings.PerCarAutoLoadEnabled = value;
        _appSettings.Save();
    }

    partial void OnSelectedProfileChanged(FfbProfile? value)
    {
        if (value == null) return;
        _profileManager.SetActiveProfile(value);
        value.ApplyToPipeline(_pipeline);
        value.ApplyToStaticFriction(_telemetryLoop.StaticFriction);
        LoadProfileValues(value);
        OnPropertyChanged(nameof(IsBuiltInProfileSelected));
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
            var logPath = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AcEvoFfbTuner", "save_debug.log");
            System.IO.File.AppendAllText(logPath,
                $"[SaveCurrentProfile] VM: MzFront={MzFrontGain:F4} FxFront={FxFrontGain:F4} FyFront={FyFrontGain:F4} " +
                $"ForceScale={ForceScale:F4} CoreMult={CoreForceMultiplier:F4} OutGain={OutputGain:F4} " +
                $"GripScale={GripScaleGain:F4} TyreTemp={TyreTempGain:F4} BrakeBoost={BrakeBoostGain:F4}\n" +
                $"[SaveCurrentProfile] Pipeline before: MzFront={_pipeline.ChannelMixer.MzFrontGain:F4} " +
                $"ForceScale={_pipeline.ForceScale:F4} CoreMult={_pipeline.CoreForceMultiplier:F4} OutGain={_pipeline.OutputGain:F4}\n");
            PushValuesToPipeline();
            System.IO.File.AppendAllText(logPath,
                $"[SaveCurrentProfile] Pipeline after push: MzFront={_pipeline.ChannelMixer.MzFrontGain:F4} " +
                $"ForceScale={_pipeline.ForceScale:F4} CoreMult={_pipeline.CoreForceMultiplier:F4} OutGain={_pipeline.OutputGain:F4}\n");
            _profileManager.SaveProfileFromPipeline(_pipeline, SelectedProfile.Name);
            SaveNonPipelineSettings(SelectedProfile);
            _profileManager.SaveProfile(SelectedProfile);
            _profileManager.SetActiveProfile(SelectedProfile);
            System.IO.File.AppendAllText(logPath,
                $"[SaveCurrentProfile] Done. Profile: MzFront.Gain={SelectedProfile.MzFront.Gain:F4} " +
                $"ForceScale={SelectedProfile.ForceScale:F4} AdvCoreMult={SelectedProfile.Advanced.CoreForceMultiplier:F4} SlipCoreMult={SelectedProfile.Slip.CoreForceMultiplier:F4} " +
                $"OutputGain={SelectedProfile.OutputGain:F4} NormalizationScale={SelectedProfile.NormalizationScale:F4}\n");
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
            profile.CarMatch = ProfileCarMatch;
            profile.TrackMatch = ProfileTrackMatch;
            SaveNonPipelineSettings(profile);
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

    private void SaveNonPipelineSettings(FfbProfile profile)
    {
        SaveProfileMetadata(profile);
        profile.LedEffects = LedEffectConfigDto.FromConfig(new LedEffectConfig
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
        profile.StaticFriction.Gain = StaticFrictionGain;
        profile.StaticFriction.MaxElasticStretch = StaticFrictionMaxElasticStretch;
        profile.StaticFriction.SpringStiffness = StaticFrictionSpringStiffness;
        profile.StaticFriction.KineticFrictionBase = StaticFrictionKineticFrictionBase;
        profile.StaticFriction.EngineOffDamping = StaticFrictionEngineOffDamping;
        profile.StaticFriction.EngineOnDamping = StaticFrictionEngineOnDamping;
        profile.StaticFriction.EngineOffScale = StaticFrictionEngineOffScale;
        profile.StaticFriction.EngineOnScale = StaticFrictionEngineOnScale;
        profile.StaticFriction.ActiveDecay = StaticFrictionActiveDecay;
        profile.StaticFriction.ReturnDecay = StaticFrictionReturnDecay;
        profile.StaticFriction.OutputSmoothAlpha = StaticFrictionOutputSmoothAlpha;
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
            profile.CarMatch = ProfileCarMatch;
            profile.TrackMatch = ProfileTrackMatch;
            profile.GameMatch = GameDisplayName;
            profile.Scope = SelectedProfile?.Scope ?? ProfileScope.General;
            SaveNonPipelineSettings(profile);
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

        SaveNonPipelineSettings(profile);
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

    private void RefreshProfiles()
    {
        Profiles.Clear();
        foreach (var p in _profileManager.Profiles)
            Profiles.Add(p);
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
        SteeringLockDegrees = Math.Clamp(profile.SteeringLockDegrees, 180, 1080);
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
        CoreForceMultiplier = profile.Slip.CoreForceMultiplier;
        TyreGripScale = profile.Slip.TyreGripScale;
        FlatspotGain = profile.Slip.FlatspotGain;
        SurfaceFeelGain = profile.Slip.SurfaceFeelGain;
        EngineTorqueLfeMod = profile.Slip.EngineTorqueLfeMod;
        BrakePressureGain = profile.Slip.BrakePressureGain;
        TcFeelGain = profile.Slip.TcFeelGain;
        CoreSmoothing = profile.Slip.CoreSmoothing;
        DetailSmoothing = profile.Slip.DetailSmoothing;
        BrakeBoostGain = profile.Slip.BrakeBoostGain;
        BrakeBoostThreshold = profile.Slip.BrakeBoostThreshold;
        GripScaleGain = profile.Slip.GripScaleGain;
        TyreTempGain = profile.Slip.TyreTempGain;
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
        _pipeline.VibrationMixer.OfftrackGain = OfftrackGain;
        _pipeline.VibrationMixer.OfftrackSeverityScale = OfftrackSeverityScale;
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
        if (_pipeline is R3eFfbPipeline r3eLoad)
        {
            r3eLoad.TyreGripScale = TyreGripScale;
            r3eLoad.FlatspotGain = FlatspotGain;
            r3eLoad.SurfaceFeelGain = SurfaceFeelGain;
            r3eLoad.EngineTorqueLfeMod = EngineTorqueLfeMod;
            r3eLoad.BrakePressureGain = BrakePressureGain;
            r3eLoad.TcFeelGain = TcFeelGain;
            r3eLoad.CoreSmoothing = CoreSmoothing;
            r3eLoad.DetailSmoothing = DetailSmoothing;
            r3eLoad.BrakeBoostGain = BrakeBoostGain;
            r3eLoad.BrakeBoostThreshold = BrakeBoostThreshold;
        }
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
}
