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
    // ── Mix mode ─────────────────────────────────────────────────────────
    partial void OnSelectedMixModeChanged(FfbMixMode value) => _pipeline.ChannelMixer.MixMode = value;
    partial void OnForceSensitivityChanged(float value) => _pipeline.MasterGain = 1000f / Math.Max(value, 1f);
    partial void OnForceScaleChanged(float value) => _pipeline.ForceScale = value;

    // ── Channel gains ─────────────────────────────────────────────────────
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

    // ── Damping ───────────────────────────────────────────────────────────
    partial void OnSpeedDampingChanged(float value) => _pipeline.Damping.SpeedDampingCoefficient = value;
    partial void OnFrictionLevelChanged(float value) => _pipeline.Damping.FrictionLevel = value;
    partial void OnInertiaWeightChanged(float value) => _pipeline.Damping.InertiaWeight = value;
    partial void OnDampingSpeedReferenceChanged(float value) => _pipeline.Damping.MaxSpeedReference = value;

    // ── Slip enhancement ──────────────────────────────────────────────────
    partial void OnSlipRatioGainChanged(float value) => _pipeline.SlipEnhancer.SlipRatioGain = value;
    partial void OnSlipAngleGainChanged(float value) => _pipeline.SlipEnhancer.SlipAngleGain = value;
    partial void OnSlipThresholdChanged(float value) => _pipeline.SlipEnhancer.SlipThreshold = value;
    partial void OnSlipUseFrontOnlyChanged(bool value) => _pipeline.SlipEnhancer.UseFrontOnly = value;

    // ── Gear change mute ──────────────────────────────────────────────────
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

    // ── Dynamic effects ───────────────────────────────────────────────────
    partial void OnCorneringForceChanged(float value) => _pipeline.DynamicEffects.LateralGGain = value;
    partial void OnAccelerationBrakingForceChanged(float value) => _pipeline.DynamicEffects.LongitudinalGGain = value;
    partial void OnRoadFeelChanged(float value) => _pipeline.DynamicEffects.SuspensionGain = value;
    partial void OnCarRotationForceChanged(float value) => _pipeline.DynamicEffects.YawRateGain = value;

    // ── Tyre flex ─────────────────────────────────────────────────────────
    partial void OnTyreFlexGainChanged(float value) => _pipeline.TyreFlex.FlexGain = value;
    partial void OnCarcassStiffnessChanged(float value) => _pipeline.TyreFlex.CarcassStiffness = value;
    partial void OnFlexSmoothingChanged(float value) => _pipeline.TyreFlex.FlexSmoothing = value;
    partial void OnContactPatchWeightChanged(float value) => _pipeline.TyreFlex.ContactPatchWeight = value;
    partial void OnLoadFlexGainChanged(float value) => _pipeline.TyreFlex.LoadFlexGain = value;

    // ── Auto gain ─────────────────────────────────────────────────────────
    partial void OnAutoGainEnabledChanged(bool value) => _pipeline.AutoGainEnabled = value;
    partial void OnAutoGainScaleChanged(float value) => _pipeline.AutoGainScale = value;

    // ── Vibration / haptics ───────────────────────────────────────────────
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
    partial void OnGripScaleGainChanged(float value)
    {
        if (_pipeline is LmuFfbPipeline lmu)
            lmu.GripScaleGain = value;
    }
    partial void OnTyreTempGainChanged(float value)
    {
        if (_pipeline is LmuFfbPipeline lmu)
            lmu.TyreTempGain = value;
    }

    // ── LFE ───────────────────────────────────────────────────────────────
    partial void OnLfeEnabledChanged(bool value) => _pipeline.LfeGenerator.Enabled = value;
    partial void OnLfeGainChanged(float value) => _pipeline.LfeGenerator.Gain = value;
    partial void OnLfeFrequencyChanged(float value) => _pipeline.LfeGenerator.Frequency = value;
    partial void OnLfeSuspensionDriveChanged(float value) => _pipeline.LfeGenerator.SuspensionDrive = value;
    partial void OnLfeSpeedScalingChanged(float value) => _pipeline.LfeGenerator.SpeedScaling = value;
    partial void OnLfeRpmDriveChanged(float value) => _pipeline.LfeGenerator.RpmDrive = value;

    // ── EQ bands ──────────────────────────────────────────────────────────
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

    // ── Steering / output shaping ─────────────────────────────────────────
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
    partial void OnTyreGripScaleChanged(float value)
    {
        if (_pipeline is R3eFfbPipeline r3e)
            r3e.TyreGripScale = value;
    }
    partial void OnFlatspotGainChanged(float value)
    {
        if (_pipeline is R3eFfbPipeline r3e)
            r3e.FlatspotGain = value;
    }
    partial void OnSurfaceFeelGainChanged(float value)
    {
        if (_pipeline is R3eFfbPipeline r3e)
            r3e.SurfaceFeelGain = value;
    }
    partial void OnEngineTorqueLfeModChanged(float value)
    {
        if (_pipeline is R3eFfbPipeline r3e)
            r3e.EngineTorqueLfeMod = value;
    }
    partial void OnBrakePressureGainChanged(float value)
    {
        if (_pipeline is R3eFfbPipeline r3e)
            r3e.BrakePressureGain = value;
    }
    partial void OnTcFeelGainChanged(float value)
    {
        if (_pipeline is R3eFfbPipeline r3e)
            r3e.TcFeelGain = value;
    }
    partial void OnCoreSmoothingChanged(float value)
    {
        if (_pipeline is R3eFfbPipeline r3e)
            r3e.CoreSmoothing = value;
    }
    partial void OnDetailSmoothingChanged(float value)
    {
        if (_pipeline is R3eFfbPipeline r3e)
            r3e.DetailSmoothing = value;
    }
    partial void OnBrakeBoostGainChanged(float value)
    {
        if (_pipeline is R3eFfbPipeline r3e)
            r3e.BrakeBoostGain = value;
    }
    partial void OnBrakeBoostThresholdChanged(float value)
    {
        if (_pipeline is R3eFfbPipeline r3e)
            r3e.BrakeBoostThreshold = value;
    }
    partial void OnSteerVelocityReferenceChanged(float value) => _pipeline.Damping.SteerVelocityReference = value;
    partial void OnVelocityDeadzoneChanged(float value) => _pipeline.Damping.VelocityDeadzone = value;
    partial void OnLowSpeedSmoothKmhChanged(float value) => _pipeline.ChannelMixer.LowSpeedSmoothKmh = value;
    partial void OnForceInvertEnabledChanged(bool value) => _deviceManager.ForceInvert = value;

    // ── Force limiter ─────────────────────────────────────────────────────
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

    // ── LUT presets ───────────────────────────────────────────────────────
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

    // ── Stationary Friction ───────────────────────────────────────────────
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
}
