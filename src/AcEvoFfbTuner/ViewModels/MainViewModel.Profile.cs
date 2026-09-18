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
        // Phase 3: attribute subsequent laps to this profile. A manual switch
        // ends any A/B context (slots kept, active marker cleared).
        _telemetryLoop.LapRecorder.ActiveProfileName = value.Name;
        _telemetryLoop.LapRecorder.ActiveGameName = string.IsNullOrWhiteSpace(value.GameMatch) ? SelectedGame.ToString() : value.GameMatch;
        _telemetryLoop.LapRecorder.ActiveCarName = string.IsNullOrWhiteSpace(value.CarMatch) ? DetectedCarModel : value.CarMatch;
        _telemetryLoop.LapRecorder.ActiveTrackName = string.IsNullOrWhiteSpace(value.TrackMatch) ? DetectedTrackName : value.TrackMatch;
        AbActiveSlot = "";
        RefreshAbLapSummary();
        OnPropertyChanged(nameof(IsBuiltInProfileSelected));

        if (_deviceManager.MozaMotor.IsAvailable)
        {
            value.MozaMotor.Apply(_deviceManager.MozaMotor);
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
        // VM-owned ACC knob: the pipeline has no ForceGain on non-ACC games,
        // so persist the slider value explicitly (runs after CreateFromPipeline).
        profile.Slip.ForceGain = ForceGain;
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

    /// <summary>
    /// Builds a detached profile holding the CURRENT on-screen/pipeline settings
    /// (VM sliders + live pipeline state) without touching the saved file.
    /// Export/Share must use this: the SelectedProfile object goes stale as soon
    /// as any slider moves (it is only refreshed on Save), so exporting it
    /// directly would silently drop unsaved adjustments.
    /// </summary>
    private FfbProfile BuildCurrentStateProfile()
    {
        PushValuesToPipeline();
        var source = SelectedProfile;
        var copy = new FfbProfile();
        copy.UpdateFromPipeline(_pipeline);
        if (source != null)
        {
            copy.Name = source.Name;
            copy.GameMatch = source.GameMatch;
            copy.Scope = source.Scope;
            copy.MozaMotor = source.MozaMotor;
            copy.Version = FfbProfile.CurrentVersion;
        }
        copy.CarMatch = ProfileCarMatch;
        copy.TrackMatch = ProfileTrackMatch;
        SaveNonPipelineSettings(copy);
        return copy;
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
        {
            _profileManager.ExportProfile(BuildCurrentStateProfile(), dialog.FileName);
            StatusText = $"Exported current settings as '{SelectedProfile.Name}'";
        }
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
    private async Task ShareProfileToHub()
    {
        if (SelectedProfile == null) return;
        // Share what the user currently feels (live sliders), not the last-saved file.
        await ShareProfileToHub(BuildCurrentStateProfile());
    }

    public async Task ShareProfileToHub(FfbProfile profile)
    {
        if (profile.IsBuiltIn)
        {
            StatusText = $"\"{profile.Name}\" is a built-in profile. Use Save As to create your own copy before sharing.";
            return;
        }

        if (string.IsNullOrEmpty(_appSettings.HubAuthorId))
        {
            _appSettings.HubAuthorId = Guid.NewGuid().ToString("N");
            _appSettings.Save();
        }

        string wheelName = _deviceManager.ConnectedDevice?.ProductName ?? "";
        if (string.IsNullOrEmpty(wheelName) && WheelDisplayName != "No wheel detected")
            wheelName = WheelDisplayName;

        string wheelType = string.IsNullOrEmpty(wheelName)
            ? ""
            : WheelbaseAutoConfigurator.DetectWheelType(wheelName).ToString();

        string gameName = SelectedGame is SupportedGame.None or SupportedGame.Unsupported
            ? HubClient.ToHubGameName(profile.GameMatch)
            : SelectedGame.ToString();

        var previewParts = new List<string>
        {
            GameDisplayName,
            string.IsNullOrWhiteSpace(profile.CarMatch) ? DetectedCarModel : profile.CarMatch,
            string.IsNullOrWhiteSpace(profile.TrackMatch) ? DetectedTrackName : profile.TrackMatch
        };
        var preview = string.Join(" · ", previewParts.Where(p => !string.IsNullOrWhiteSpace(p)));
        if (!string.IsNullOrEmpty(wheelName))
            preview += $"\nWheel: {wheelName} ({wheelType}) — {profile.WheelMaxTorqueNm:F1} Nm max";

        var dialog = new Views.ShareProfileDialog
        {
            Owner = Application.Current?.MainWindow,
            TitleText = profile.Name,
            AuthorText = _appSettings.HubAuthorName,
            PreviewText = preview
        };

        if (dialog.ShowDialog() != true) return;

        _appSettings.HubAuthorName = dialog.AuthorText.Trim();
        _appSettings.Save();

        profile.SanitizeFloats();
        var request = new HubUploadRequest
        {
            Title = dialog.TitleText.Trim(),
            Description = string.IsNullOrWhiteSpace(dialog.DescriptionText) ? null : dialog.DescriptionText.Trim(),
            Author = dialog.AuthorText.Trim(),
            AuthorId = _appSettings.HubAuthorId,
            Game = gameName,
            Car = string.IsNullOrWhiteSpace(profile.CarMatch) ? null : profile.CarMatch,
            Track = string.IsNullOrWhiteSpace(profile.TrackMatch) ? null : profile.TrackMatch,
            Wheel = string.IsNullOrEmpty(wheelName) ? null : wheelName,
            WheelType = string.IsNullOrEmpty(wheelType) ? null : wheelType,
            TorqueNm = profile.WheelMaxTorqueNm,
            Profile = HubClient.SerializeProfile(profile)
        };

        StatusText = "Submitting profile to Hub...";
        var result = await HubClient.UploadProfileAsync(request);
        if (result.Ok)
        {
            StatusText = result.Status == "pending"
                ? "Submitted for review — will appear on the Hub after approval"
                : "Profile shared to the Hub";
            AddSystemLog($"[Hub] Uploaded '{request.Title}' (id={result.Id}) status={result.Status}");
        }
        else
        {
            StatusText = result.RateLimited ? result.Error! : $"Share failed: {result.Error}";
            AddSystemLog($"[Hub] Upload failed: {result.Error}");
        }
    }

    public FfbProfile? ImportHubProfile(int hubId, string json)
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"hub_{hubId}_{Guid.NewGuid():N}.json");
        try
        {
            var profile = JsonSerializer.Deserialize<FfbProfile>(json, HubClient.ProfileJsonOptions);
            if (profile == null)
            {
                StatusText = "Import failed — the profile file was invalid";
                return null;
            }

            if (string.IsNullOrWhiteSpace(profile.Name))
                profile.Name = "Imported Profile";
            if (_profileManager.Profiles.Any(p => string.Equals(p.Name, profile.Name, StringComparison.OrdinalIgnoreCase)))
                profile.Name = GetNextProfileName(profile.Name);

            File.WriteAllText(tempFile, JsonSerializer.Serialize(profile, HubClient.ProfileJsonOptions));
            var imported = _profileManager.ImportProfile(tempFile);
            if (imported != null)
            {
                RefreshProfiles();
                SelectedProfile = imported;
                StatusText = $"Imported: {imported.Name}";
                AddSystemLog($"[Hub] Imported '{imported.Name}' (hub id={hubId})");
            }
            else
            {
                StatusText = "Import failed — the profile file was invalid";
            }
            return imported;
        }
        catch (Exception ex)
        {
            StatusText = $"Import failed: {ex.Message}";
            return null;
        }
        finally
        {
            try { File.Delete(tempFile); } catch { }
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
        SlipAngleShapeGain = profile.Slip.SlipAngleShapeGain;
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
        ForceGain = profile.Slip.ForceGain;
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

        SoftLockEnabled = profile.SoftLock.Enabled;
        SoftLockStrength = profile.SoftLock.Strength;
        SoftLockDegrees = profile.SoftLock.LockDegrees;
        GyroEnabled = profile.Gyro.Enabled;
        GyroGain = profile.Gyro.Gain;
        TcWheelEnabled = profile.TcWheel.Enabled;
        TcWheelGain = profile.TcWheel.Gain;

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

    /// <summary>
    /// Pulls live pipeline (and stationary-friction) state back into the
    /// viewmodel sliders. Needed after flows that write the pipeline directly
    /// and bypass the VM (calibration wizard, setup wizard polarity flip):
    /// without this, the next Save pushes stale VM values over the pipeline
    /// and the wizard's tuning is silently lost. Each assignment re-pushes the
    /// same value to the pipeline via OnXChanged handlers, so this is idempotent.
    /// Game-specific knobs are only pulled when that game's pipeline is active.
    /// </summary>
    public void SyncVmFromPipeline()
    {
        SelectedMixMode = _pipeline.ChannelMixer.MixMode;
        OutputGain = _pipeline.OutputGain;
        ForceSensitivity = 1000f / Math.Max(_pipeline.MasterGain, 0.001f);
        MaxForceLimit = _pipeline.OutputClipper.SoftClipThreshold;
        MzFrontGain = _pipeline.ChannelMixer.MzFrontGain;
        MzFrontEnabled = _pipeline.ChannelMixer.MzFrontEnabled;
        FxFrontGain = _pipeline.ChannelMixer.FxFrontGain;
        FxFrontEnabled = _pipeline.ChannelMixer.FxFrontEnabled;
        FyFrontGain = _pipeline.ChannelMixer.FyFrontGain;
        FyFrontEnabled = _pipeline.ChannelMixer.FyFrontEnabled;
        MzRearGain = _pipeline.ChannelMixer.MzRearGain;
        MzRearEnabled = _pipeline.ChannelMixer.MzRearEnabled;
        FxRearGain = _pipeline.ChannelMixer.FxRearGain;
        FxRearEnabled = _pipeline.ChannelMixer.FxRearEnabled;
        FyRearGain = _pipeline.ChannelMixer.FyRearGain;
        FyRearEnabled = _pipeline.ChannelMixer.FyRearEnabled;
        FinalFfGain = _pipeline.ChannelMixer.FinalFfGain;
        FinalFfEnabled = _pipeline.ChannelMixer.FinalFfEnabled;
        WheelLoadWeighting = _pipeline.ChannelMixer.WheelLoadWeighting;
        MzScale = _pipeline.ChannelMixer.MzScale;
        FxScale = _pipeline.ChannelMixer.FxScale;
        FyScale = _pipeline.ChannelMixer.FyScale;
        SpeedDamping = _pipeline.Damping.SpeedDampingCoefficient;
        FrictionLevel = _pipeline.Damping.FrictionLevel;
        InertiaWeight = _pipeline.Damping.InertiaWeight;
        DampingSpeedReference = _pipeline.Damping.MaxSpeedReference;
        SlipRatioGain = _pipeline.SlipEnhancer.SlipRatioGain;
        SlipAngleGain = _pipeline.SlipEnhancer.SlipAngleGain;
        SlipAngleShapeGain = _pipeline.SlipEnhancer.SlipAngleShapeGain;
        SlipThreshold = _pipeline.SlipEnhancer.SlipThreshold;
        SlipUseFrontOnly = _pipeline.SlipEnhancer.UseFrontOnly;
        GearChangeMuteEnabled = _pipeline.GearShiftFilterEnabled;
        CorneringForce = _pipeline.DynamicEffects.LateralGGain;
        AccelerationBrakingForce = _pipeline.DynamicEffects.LongitudinalGGain;
        RoadFeel = _pipeline.DynamicEffects.SuspensionGain;
        CarRotationForce = _pipeline.DynamicEffects.YawRateGain;
        TyreFlexGain = _pipeline.TyreFlex.FlexGain;
        CarcassStiffness = _pipeline.TyreFlex.CarcassStiffness;
        FlexSmoothing = _pipeline.TyreFlex.FlexSmoothing;
        ContactPatchWeight = _pipeline.TyreFlex.ContactPatchWeight;
        LoadFlexGain = _pipeline.TyreFlex.LoadFlexGain;
        AutoGainEnabled = _pipeline.AutoGainEnabled;
        AutoGainScale = _pipeline.AutoGainScale;
        CurbGain = _pipeline.VibrationMixer.KerbGain;
        SlipGain = _pipeline.VibrationMixer.SlipGain;
        RoadGain = _pipeline.VibrationMixer.RoadGain;
        AbsGain = _pipeline.VibrationMixer.AbsGain;
        AbsPulseAmplitude = _pipeline.VibrationMixer.AbsPulseAmplitude;
        VibrationMasterGain = _pipeline.VibrationMixer.MasterGain;
        SuspensionRoadGain = _pipeline.VibrationMixer.SuspensionRoadGain;
        ScrubGain = _pipeline.VibrationMixer.ScrubGain;
        RearSlipGain = _pipeline.VibrationMixer.RearSlipGain;
        OfftrackGain = _pipeline.VibrationMixer.OfftrackGain;
        OfftrackSeverityScale = _pipeline.VibrationMixer.OfftrackSeverityScale;
        LfeEnabled = _pipeline.LfeGenerator.Enabled;
        LfeGain = _pipeline.LfeGenerator.Gain;
        LfeFrequency = _pipeline.LfeGenerator.Frequency;
        LfeSuspensionDrive = _pipeline.LfeGenerator.SuspensionDrive;
        LfeSpeedScaling = _pipeline.LfeGenerator.SpeedScaling;
        LfeRpmDrive = _pipeline.LfeGenerator.RpmDrive;
        CompressionPower = _pipeline.CompressionPower;
        ForceScale = _pipeline.ForceScale;
        SignCorrectionEnabled = _pipeline.SignCorrectionEnabled;
        FyInverted = _pipeline.ChannelMixer.FyInverted;
        MaxSlewRate = _pipeline.MaxSlewRate;
        CenterSuppressionDegrees = _pipeline.CenterSuppressionDegrees;
        CenterKneePower = _pipeline.CenterKneePower;
        HysteresisThreshold = _pipeline.HysteresisThreshold;
        NoiseFloor = _pipeline.NoiseFloor;
        HysteresisWatchdogFrames = _pipeline.HysteresisWatchdogFrames;
        CenterBlendDegrees = _pipeline.ChannelMixer.CenterBlendDegrees;
        CenterSharpnessDegrees = _pipeline.CenterSharpnessDegrees;
        CoreForceMultiplier = _pipeline.CoreForceMultiplier;
        SteerVelocityReference = _pipeline.Damping.SteerVelocityReference;
        VelocityDeadzone = _pipeline.Damping.VelocityDeadzone;
        LowSpeedSmoothKmh = _pipeline.ChannelMixer.LowSpeedSmoothKmh;
        EqEnabled = _pipeline.Equalizer.MasterEnabled;
        EqBand0Gain = _pipeline.Equalizer.GetBandGain(0);
        EqBand1Gain = _pipeline.Equalizer.GetBandGain(1);
        EqBand2Gain = _pipeline.Equalizer.GetBandGain(2);
        EqBand3Gain = _pipeline.Equalizer.GetBandGain(3);
        EqBand4Gain = _pipeline.Equalizer.GetBandGain(4);
        EqBand5Gain = _pipeline.Equalizer.GetBandGain(5);
        EqBand6Gain = _pipeline.Equalizer.GetBandGain(6);
        EqBand7Gain = _pipeline.Equalizer.GetBandGain(7);
        EqBand8Gain = _pipeline.Equalizer.GetBandGain(8);
        EqBand9Gain = _pipeline.Equalizer.GetBandGain(9);

        if (_pipeline is R3eFfbPipeline r3eSync)
        {
            TyreGripScale = r3eSync.TyreGripScale;
            FlatspotGain = r3eSync.FlatspotGain;
            SurfaceFeelGain = r3eSync.SurfaceFeelGain;
            EngineTorqueLfeMod = r3eSync.EngineTorqueLfeMod;
            BrakePressureGain = r3eSync.BrakePressureGain;
            TcFeelGain = r3eSync.TcFeelGain;
            CoreSmoothing = r3eSync.CoreSmoothing;
            DetailSmoothing = r3eSync.DetailSmoothing;
            BrakeBoostGain = r3eSync.BrakeBoostGain;
            BrakeBoostThreshold = r3eSync.BrakeBoostThreshold;
        }
        else if (_pipeline is LmuFfbPipeline lmuSync)
        {
            BrakeBoostGain = lmuSync.BrakeBoostGain;
            BrakeBoostThreshold = lmuSync.BrakeBoostThreshold;
            GripScaleGain = lmuSync.GripScaleGain;
            TyreTempGain = lmuSync.TyreTempGain;
        }
        else if (_pipeline is AcFfbPipeline acSync)
        {
            BrakeBoostGain = acSync.BrakeBoostGain;
            BrakeBoostThreshold = acSync.BrakeBoostThreshold;
        }
        else if (_pipeline is AccFfbPipeline accSync)
        {
            BrakeBoostGain = accSync.BrakeBoostGain;
            BrakeBoostThreshold = accSync.BrakeBoostThreshold;
            ForceGain = accSync.ForceGain;
        }

        Hf8Enabled = _pipeline.Hf8SignalMapper.Enabled;
        Hf8MasterGain = _pipeline.Hf8SignalMapper.MasterGain;
        Hf8ZoneGain0 = _pipeline.Hf8SignalMapper.ZoneGains[0];
        Hf8ZoneGain1 = _pipeline.Hf8SignalMapper.ZoneGains[1];
        Hf8ZoneGain2 = _pipeline.Hf8SignalMapper.ZoneGains[2];
        Hf8ZoneGain3 = _pipeline.Hf8SignalMapper.ZoneGains[3];
        Hf8ZoneGain4 = _pipeline.Hf8SignalMapper.ZoneGains[4];
        Hf8ZoneGain5 = _pipeline.Hf8SignalMapper.ZoneGains[5];
        Hf8ZoneGain6 = _pipeline.Hf8SignalMapper.ZoneGains[6];
        Hf8ZoneGain7 = _pipeline.Hf8SignalMapper.ZoneGains[7];
        Hf8ZoneEnabled0 = _pipeline.Hf8SignalMapper.ZoneEnabled[0];
        Hf8ZoneEnabled1 = _pipeline.Hf8SignalMapper.ZoneEnabled[1];
        Hf8ZoneEnabled2 = _pipeline.Hf8SignalMapper.ZoneEnabled[2];
        Hf8ZoneEnabled3 = _pipeline.Hf8SignalMapper.ZoneEnabled[3];
        Hf8ZoneEnabled4 = _pipeline.Hf8SignalMapper.ZoneEnabled[4];
        Hf8ZoneEnabled5 = _pipeline.Hf8SignalMapper.ZoneEnabled[5];
        Hf8ZoneEnabled6 = _pipeline.Hf8SignalMapper.ZoneEnabled[6];
        Hf8ZoneEnabled7 = _pipeline.Hf8SignalMapper.ZoneEnabled[7];
        Hf8SrcSusp0 = _pipeline.Hf8SignalMapper.GetSourceWeight(0, 0);
        Hf8SrcSlip0 = _pipeline.Hf8SignalMapper.GetSourceWeight(0, 1);
        Hf8SrcKerb0 = _pipeline.Hf8SignalMapper.GetSourceWeight(0, 2);
        Hf8SrcLatG0 = _pipeline.Hf8SignalMapper.GetSourceWeight(0, 3);
        Hf8SrcEngine0 = _pipeline.Hf8SignalMapper.GetSourceWeight(0, 4);
        Hf8SrcSusp1 = _pipeline.Hf8SignalMapper.GetSourceWeight(1, 0);
        Hf8SrcSlip1 = _pipeline.Hf8SignalMapper.GetSourceWeight(1, 1);
        Hf8SrcKerb1 = _pipeline.Hf8SignalMapper.GetSourceWeight(1, 2);
        Hf8SrcLatG1 = _pipeline.Hf8SignalMapper.GetSourceWeight(1, 3);
        Hf8SrcEngine1 = _pipeline.Hf8SignalMapper.GetSourceWeight(1, 4);
        Hf8SrcSusp2 = _pipeline.Hf8SignalMapper.GetSourceWeight(2, 0);
        Hf8SrcSlip2 = _pipeline.Hf8SignalMapper.GetSourceWeight(2, 1);
        Hf8SrcKerb2 = _pipeline.Hf8SignalMapper.GetSourceWeight(2, 2);
        Hf8SrcLatG2 = _pipeline.Hf8SignalMapper.GetSourceWeight(2, 3);
        Hf8SrcEngine2 = _pipeline.Hf8SignalMapper.GetSourceWeight(2, 4);
        Hf8SrcSusp3 = _pipeline.Hf8SignalMapper.GetSourceWeight(3, 0);
        Hf8SrcSlip3 = _pipeline.Hf8SignalMapper.GetSourceWeight(3, 1);
        Hf8SrcKerb3 = _pipeline.Hf8SignalMapper.GetSourceWeight(3, 2);
        Hf8SrcLatG3 = _pipeline.Hf8SignalMapper.GetSourceWeight(3, 3);
        Hf8SrcEngine3 = _pipeline.Hf8SignalMapper.GetSourceWeight(3, 4);
        Hf8SrcSusp4 = _pipeline.Hf8SignalMapper.GetSourceWeight(4, 0);
        Hf8SrcSlip4 = _pipeline.Hf8SignalMapper.GetSourceWeight(4, 1);
        Hf8SrcKerb4 = _pipeline.Hf8SignalMapper.GetSourceWeight(4, 2);
        Hf8SrcLatG4 = _pipeline.Hf8SignalMapper.GetSourceWeight(4, 3);
        Hf8SrcEngine4 = _pipeline.Hf8SignalMapper.GetSourceWeight(4, 4);
        Hf8SrcSusp5 = _pipeline.Hf8SignalMapper.GetSourceWeight(5, 0);
        Hf8SrcSlip5 = _pipeline.Hf8SignalMapper.GetSourceWeight(5, 1);
        Hf8SrcKerb5 = _pipeline.Hf8SignalMapper.GetSourceWeight(5, 2);
        Hf8SrcLatG5 = _pipeline.Hf8SignalMapper.GetSourceWeight(5, 3);
        Hf8SrcEngine5 = _pipeline.Hf8SignalMapper.GetSourceWeight(5, 4);
        Hf8SrcSusp6 = _pipeline.Hf8SignalMapper.GetSourceWeight(6, 0);
        Hf8SrcSlip6 = _pipeline.Hf8SignalMapper.GetSourceWeight(6, 1);
        Hf8SrcKerb6 = _pipeline.Hf8SignalMapper.GetSourceWeight(6, 2);
        Hf8SrcLatG6 = _pipeline.Hf8SignalMapper.GetSourceWeight(6, 3);
        Hf8SrcEngine6 = _pipeline.Hf8SignalMapper.GetSourceWeight(6, 4);
        Hf8SrcSusp7 = _pipeline.Hf8SignalMapper.GetSourceWeight(7, 0);
        Hf8SrcSlip7 = _pipeline.Hf8SignalMapper.GetSourceWeight(7, 1);
        Hf8SrcKerb7 = _pipeline.Hf8SignalMapper.GetSourceWeight(7, 2);
        Hf8SrcLatG7 = _pipeline.Hf8SignalMapper.GetSourceWeight(7, 3);
        Hf8SrcEngine7 = _pipeline.Hf8SignalMapper.GetSourceWeight(7, 4);

        GripGuardEnabled = _pipeline.GripGuard.Enabled;
        GripGuardPeakSlipAngle = _pipeline.GripGuard.PeakSlipAngle;
        GripGuardAttenuationStrength = _pipeline.GripGuard.AttenuationStrength;
        GripGuardMechanicalTrailGain = _pipeline.GripGuard.MechanicalTrailGain;
        GripGuardMinSpeedKmh = _pipeline.GripGuard.MinSpeedKmh;

        SoftLockEnabled = _pipeline.SoftLockEnabled;
        SoftLockStrength = _pipeline.SoftLockStrength;
        SoftLockDegrees = _pipeline.SoftLockDegrees;
        GyroEnabled = _pipeline.GyroEnabled;
        GyroGain = _pipeline.GyroGain;
        TcWheelEnabled = _pipeline.TcWheelEnabled;
        TcWheelGain = _pipeline.TcWheelGain;

        CrashEnabled = _pipeline.CrashDetector.Enabled;
        CrashImpactGain = _pipeline.CrashDetector.ImpactGain;
        CrashSafetyClamp = _pipeline.CrashDetector.SafetyClamp;
        CrashDecayRate = _pipeline.CrashDetector.DecayRate;
        CrashTriggerThresholdG = _pipeline.CrashDetector.TriggerThresholdG;
        CrashMinSpeedKmh = _pipeline.CrashDetector.MinSpeedKmh;
        CrashSafetyOverride = _pipeline.CrashDetector.SafetyOverride;

        TyreConditionEnabled = _pipeline.TyreCondition.Enabled;
        TyreConditionBlowoutGain = _pipeline.TyreCondition.BlowoutVibrationGain;
        TyreConditionPressureLossGain = _pipeline.TyreCondition.PressureLossGain;
        TyreConditionDamageAsymmetryGain = _pipeline.TyreCondition.DamageAsymmetryGain;
        TyreConditionBlowoutThreshold = _pipeline.TyreCondition.BlowoutPressureThreshold;
        TyreConditionMaxBlowoutAmplitude = _pipeline.TyreCondition.MaxBlowoutAmplitude;

        WetWeatherEnabled = _pipeline.WetWeather.Enabled;
        WetWeatherAutoDetect = _pipeline.WetWeather.AutoDetect;
        WetWeatherManualIntensity = _pipeline.WetWeather.ManualIntensity;
        WetWeatherRoadVibSuppression = _pipeline.WetWeather.RoadVibSuppression;
        WetWeatherCurbSuppression = _pipeline.WetWeather.CurbSuppression;
        WetWeatherScrubSuppression = _pipeline.WetWeather.ScrubSuppression;
        WetWeatherPeakSlipAngleMultiplier = _pipeline.WetWeather.PeakSlipAngleMultiplier;
        WetWeatherDampingReduction = _pipeline.WetWeather.DampingReduction;
        WetWeatherNoiseFloorSuppression = _pipeline.WetWeather.NoiseFloorSuppression;
        WetWeatherHydroplaningEnabled = _pipeline.WetWeather.HydroplaningEnabled;
        WetWeatherHydroplaningSpeedThreshold = _pipeline.WetWeather.HydroplaningSpeedThreshold;
        WetWeatherHydroplaningMaxAttenuation = _pipeline.WetWeather.HydroplaningMaxAttenuation;

        StaticFrictionGain = _telemetryLoop.StaticFriction.Gain;
        StaticFrictionMaxElasticStretch = _telemetryLoop.StaticFriction.MaxElasticStretch;
        StaticFrictionSpringStiffness = _telemetryLoop.StaticFriction.SpringStiffness;
        StaticFrictionKineticFrictionBase = _telemetryLoop.StaticFriction.KineticFrictionBase;
        StaticFrictionEngineOffDamping = _telemetryLoop.StaticFriction.EngineOffDamping;
        StaticFrictionEngineOnDamping = _telemetryLoop.StaticFriction.EngineOnDamping;
        StaticFrictionEngineOffScale = _telemetryLoop.StaticFriction.EngineOffScale;
        StaticFrictionEngineOnScale = _telemetryLoop.StaticFriction.EngineOnScale;
        StaticFrictionActiveDecay = _telemetryLoop.StaticFriction.ActiveDecay;
        StaticFrictionReturnDecay = _telemetryLoop.StaticFriction.ReturnDecay;
        StaticFrictionOutputSmoothAlpha = _telemetryLoop.StaticFriction.OutputSmoothAlpha;
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
        _pipeline.OutputGain = OutputGain;
        _pipeline.MasterGain = 1000f / Math.Max(ForceSensitivity, 1f);
        _pipeline.Damping.SpeedDampingCoefficient = SpeedDamping;
        _pipeline.Damping.FrictionLevel = FrictionLevel;
        _pipeline.Damping.InertiaWeight = InertiaWeight;
        _pipeline.Damping.MaxSpeedReference = DampingSpeedReference;
        _pipeline.SlipEnhancer.SlipRatioGain = SlipRatioGain;
        _pipeline.SlipEnhancer.SlipAngleGain = SlipAngleGain;
        _pipeline.SlipEnhancer.SlipAngleShapeGain = SlipAngleShapeGain;
        _pipeline.SlipEnhancer.SlipThreshold = SlipThreshold;
        _pipeline.SlipEnhancer.UseFrontOnly = SlipUseFrontOnly;
        _pipeline.GearShiftFilterEnabled = GearChangeMuteEnabled;
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
            r3eLoad.GearChangeMuteEnabled = GearChangeMuteEnabled;
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
        if (_pipeline is LmuFfbPipeline lmuLoad)
        {
            lmuLoad.GearChangeMuteEnabled = GearChangeMuteEnabled;
            lmuLoad.BrakeBoostGain = BrakeBoostGain;
            lmuLoad.BrakeBoostThreshold = BrakeBoostThreshold;
            lmuLoad.GripScaleGain = GripScaleGain;
            lmuLoad.TyreTempGain = TyreTempGain;
        }
        if (_pipeline is AcFfbPipeline acLoad)
        {
            acLoad.GearChangeMuteEnabled = GearChangeMuteEnabled;
            acLoad.BrakeBoostGain = BrakeBoostGain;
            acLoad.BrakeBoostThreshold = BrakeBoostThreshold;
        }
        if (_pipeline is AccFfbPipeline accLoad)
        {
            accLoad.GearChangeMuteEnabled = GearChangeMuteEnabled;
            accLoad.BrakeBoostGain = BrakeBoostGain;
            accLoad.BrakeBoostThreshold = BrakeBoostThreshold;
            accLoad.ForceGain = ForceGain;
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

        _pipeline.Hf8SignalMapper.Enabled = Hf8Enabled;
        _pipeline.Hf8SignalMapper.MasterGain = Hf8MasterGain;
        _pipeline.Hf8SignalMapper.ZoneGains[0] = Hf8ZoneGain0;
        _pipeline.Hf8SignalMapper.ZoneGains[1] = Hf8ZoneGain1;
        _pipeline.Hf8SignalMapper.ZoneGains[2] = Hf8ZoneGain2;
        _pipeline.Hf8SignalMapper.ZoneGains[3] = Hf8ZoneGain3;
        _pipeline.Hf8SignalMapper.ZoneGains[4] = Hf8ZoneGain4;
        _pipeline.Hf8SignalMapper.ZoneGains[5] = Hf8ZoneGain5;
        _pipeline.Hf8SignalMapper.ZoneGains[6] = Hf8ZoneGain6;
        _pipeline.Hf8SignalMapper.ZoneGains[7] = Hf8ZoneGain7;
        _pipeline.Hf8SignalMapper.ZoneEnabled[0] = Hf8ZoneEnabled0;
        _pipeline.Hf8SignalMapper.ZoneEnabled[1] = Hf8ZoneEnabled1;
        _pipeline.Hf8SignalMapper.ZoneEnabled[2] = Hf8ZoneEnabled2;
        _pipeline.Hf8SignalMapper.ZoneEnabled[3] = Hf8ZoneEnabled3;
        _pipeline.Hf8SignalMapper.ZoneEnabled[4] = Hf8ZoneEnabled4;
        _pipeline.Hf8SignalMapper.ZoneEnabled[5] = Hf8ZoneEnabled5;
        _pipeline.Hf8SignalMapper.ZoneEnabled[6] = Hf8ZoneEnabled6;
        _pipeline.Hf8SignalMapper.ZoneEnabled[7] = Hf8ZoneEnabled7;
        _pipeline.Hf8SignalMapper.SetSourceWeight(0, 0, Hf8SrcSusp0);
        _pipeline.Hf8SignalMapper.SetSourceWeight(0, 1, Hf8SrcSlip0);
        _pipeline.Hf8SignalMapper.SetSourceWeight(0, 2, Hf8SrcKerb0);
        _pipeline.Hf8SignalMapper.SetSourceWeight(0, 3, Hf8SrcLatG0);
        _pipeline.Hf8SignalMapper.SetSourceWeight(0, 4, Hf8SrcEngine0);
        _pipeline.Hf8SignalMapper.SetSourceWeight(1, 0, Hf8SrcSusp1);
        _pipeline.Hf8SignalMapper.SetSourceWeight(1, 1, Hf8SrcSlip1);
        _pipeline.Hf8SignalMapper.SetSourceWeight(1, 2, Hf8SrcKerb1);
        _pipeline.Hf8SignalMapper.SetSourceWeight(1, 3, Hf8SrcLatG1);
        _pipeline.Hf8SignalMapper.SetSourceWeight(1, 4, Hf8SrcEngine1);
        _pipeline.Hf8SignalMapper.SetSourceWeight(2, 0, Hf8SrcSusp2);
        _pipeline.Hf8SignalMapper.SetSourceWeight(2, 1, Hf8SrcSlip2);
        _pipeline.Hf8SignalMapper.SetSourceWeight(2, 2, Hf8SrcKerb2);
        _pipeline.Hf8SignalMapper.SetSourceWeight(2, 3, Hf8SrcLatG2);
        _pipeline.Hf8SignalMapper.SetSourceWeight(2, 4, Hf8SrcEngine2);
        _pipeline.Hf8SignalMapper.SetSourceWeight(3, 0, Hf8SrcSusp3);
        _pipeline.Hf8SignalMapper.SetSourceWeight(3, 1, Hf8SrcSlip3);
        _pipeline.Hf8SignalMapper.SetSourceWeight(3, 2, Hf8SrcKerb3);
        _pipeline.Hf8SignalMapper.SetSourceWeight(3, 3, Hf8SrcLatG3);
        _pipeline.Hf8SignalMapper.SetSourceWeight(3, 4, Hf8SrcEngine3);
        _pipeline.Hf8SignalMapper.SetSourceWeight(4, 0, Hf8SrcSusp4);
        _pipeline.Hf8SignalMapper.SetSourceWeight(4, 1, Hf8SrcSlip4);
        _pipeline.Hf8SignalMapper.SetSourceWeight(4, 2, Hf8SrcKerb4);
        _pipeline.Hf8SignalMapper.SetSourceWeight(4, 3, Hf8SrcLatG4);
        _pipeline.Hf8SignalMapper.SetSourceWeight(4, 4, Hf8SrcEngine4);
        _pipeline.Hf8SignalMapper.SetSourceWeight(5, 0, Hf8SrcSusp5);
        _pipeline.Hf8SignalMapper.SetSourceWeight(5, 1, Hf8SrcSlip5);
        _pipeline.Hf8SignalMapper.SetSourceWeight(5, 2, Hf8SrcKerb5);
        _pipeline.Hf8SignalMapper.SetSourceWeight(5, 3, Hf8SrcLatG5);
        _pipeline.Hf8SignalMapper.SetSourceWeight(5, 4, Hf8SrcEngine5);
        _pipeline.Hf8SignalMapper.SetSourceWeight(6, 0, Hf8SrcSusp6);
        _pipeline.Hf8SignalMapper.SetSourceWeight(6, 1, Hf8SrcSlip6);
        _pipeline.Hf8SignalMapper.SetSourceWeight(6, 2, Hf8SrcKerb6);
        _pipeline.Hf8SignalMapper.SetSourceWeight(6, 3, Hf8SrcLatG6);
        _pipeline.Hf8SignalMapper.SetSourceWeight(6, 4, Hf8SrcEngine6);
        _pipeline.Hf8SignalMapper.SetSourceWeight(7, 0, Hf8SrcSusp7);
        _pipeline.Hf8SignalMapper.SetSourceWeight(7, 1, Hf8SrcSlip7);
        _pipeline.Hf8SignalMapper.SetSourceWeight(7, 2, Hf8SrcKerb7);
        _pipeline.Hf8SignalMapper.SetSourceWeight(7, 3, Hf8SrcLatG7);
        _pipeline.Hf8SignalMapper.SetSourceWeight(7, 4, Hf8SrcEngine7);

        _pipeline.GripGuard.Enabled = GripGuardEnabled;
        _pipeline.GripGuard.PeakSlipAngle = GripGuardPeakSlipAngle;
        _pipeline.GripGuard.AttenuationStrength = GripGuardAttenuationStrength;
        _pipeline.GripGuard.MechanicalTrailGain = GripGuardMechanicalTrailGain;
        _pipeline.GripGuard.MinSpeedKmh = GripGuardMinSpeedKmh;

        _pipeline.SoftLockEnabled = SoftLockEnabled;
        _pipeline.SoftLockStrength = SoftLockStrength;
        _pipeline.SoftLockDegrees = SoftLockDegrees;
        _pipeline.GyroEnabled = GyroEnabled;
        _pipeline.GyroGain = GyroGain;
        _pipeline.TcWheelEnabled = TcWheelEnabled;
        _pipeline.TcWheelGain = TcWheelGain;

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
