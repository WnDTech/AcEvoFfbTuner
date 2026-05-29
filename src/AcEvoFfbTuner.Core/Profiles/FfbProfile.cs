using System.Text.Json.Serialization;
using AcEvoFfbTuner.Core.DirectInput;
using AcEvoFfbTuner.Core.FfbProcessing;

namespace AcEvoFfbTuner.Core.Profiles;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ProfileScope
{
    General,
    PerGame,
    PerCar,
    PerTrack,
    PerCarAndTrack
}

public sealed class FfbProfile
{
    public const int CurrentVersion = 21;

    public int Version { get; set; } = CurrentVersion;
    public string Name { get; set; } = "Default";
    public string CarMatch { get; set; } = "";
    public string TrackMatch { get; set; } = "";
    public string GameMatch { get; set; } = "";
    public ProfileScope Scope { get; set; } = ProfileScope.General;

    public override bool Equals(object? obj)
    {
        return obj is FfbProfile profile && Name == profile.Name;
    }

    public override int GetHashCode()
    {
        return Name.GetHashCode();
    }

    public override string ToString() => Name;

    [JsonIgnore]
    public bool IsBuiltIn => AllDefaultNames.Contains(Name);

    [JsonIgnore]
    public bool NeedsMigration => Version < CurrentVersion;

    public FfbMixModeDto MixMode { get; set; } = FfbMixModeDto.Replace;
    public float OutputGain { get; set; } = 0.621f;

    [JsonPropertyName("forceSensitivity")]
    public float NormalizationScale { get; set; } = 1000f;

    public float ForceScale { get; set; } = 1.0f;
    public float SoftClipThreshold { get; set; } = 0.8f;
    public float CompressionPower { get; set; } = 1.0f;
    public bool SignCorrectionEnabled { get; set; } = true;
    public bool ForceInvertEnabled { get; set; } = false;
    public float WheelMaxTorqueNm { get; set; } = 5.5f;

    public ChannelConfig MzFront { get; set; } = new() { Gain = 0.42f, Enabled = true };
    public ChannelConfig FxFront { get; set; } = new() { Gain = 0.15f, Enabled = true };
    public ChannelConfig FyFront { get; set; } = new() { Gain = 0.20f, Enabled = true };
    public ChannelConfig MzRear { get; set; } = new() { Gain = 0.0f, Enabled = false };
    public ChannelConfig FxRear { get; set; } = new() { Gain = 0.0f, Enabled = false };
    public ChannelConfig FyRear { get; set; } = new() { Gain = 0.0f, Enabled = false };
    public ChannelConfig FinalFf { get; set; } = new() { Gain = 0.0f, Enabled = false };

    public float WheelLoadWeighting { get; set; } = 0.0f;

    public bool FyInverted { get; set; } = true;

    public float MzScale { get; set; } = 30f;
    public float FxScale { get; set; } = 4000f;
    public float FyScale { get; set; } = 5000f;

    public LutCurveDto LutCurve { get; set; } = LutCurveDto.Linear();

    public int SteeringLockDegrees { get; set; } = 900;

    public DampingConfig Damping { get; set; } = new();
    public SlipConfig Slip { get; set; } = new();
    public DynamicConfig Dynamic { get; set; } = new();
    public AutoGainConfig AutoGain { get; set; } = new();
    public VibrationConfig Vibrations { get; set; } = new();
    public AdvancedConfig Advanced { get; set; } = new();
    public LfeConfig Lfe { get; set; } = new();
    public EqConfig Equalizer { get; set; } = new();
    public TyreFlexConfig TyreFlex { get; set; } = new();
    public LedEffectConfigDto LedEffects { get; set; } = new();
    public Hf8Config Hf8 { get; set; } = new();
    public GripGuardConfig GripGuard { get; set; } = new();
    public StaticFrictionConfig StaticFriction { get; set; } = new();
    public CrashConfig Crash { get; set; } = new();
    public TyreConditionConfig TyreCondition { get; set; } = new();
    public WetWeatherConfig WetWeather { get; set; } = new();
    public TelemetrySnapshotDto? LastTelemetrySnapshot { get; set; }

    public void ApplyToPipeline(FfbPipeline pipeline)
    {
        pipeline.ChannelMixer.MzFrontGain = MzFront.Gain;
        pipeline.ChannelMixer.MzFrontEnabled = MzFront.Enabled;
        pipeline.ChannelMixer.FxFrontGain = FxFront.Gain;
        pipeline.ChannelMixer.FxFrontEnabled = FxFront.Enabled;
        pipeline.ChannelMixer.FyFrontGain = FyFront.Gain;
        pipeline.ChannelMixer.FyFrontEnabled = FyFront.Enabled;
        pipeline.ChannelMixer.MzRearGain = MzRear.Gain;
        pipeline.ChannelMixer.MzRearEnabled = MzRear.Enabled;
        pipeline.ChannelMixer.FxRearGain = FxRear.Gain;
        pipeline.ChannelMixer.FxRearEnabled = FxRear.Enabled;
        pipeline.ChannelMixer.FyRearGain = FyRear.Gain;
        pipeline.ChannelMixer.FyRearEnabled = FyRear.Enabled;
        pipeline.ChannelMixer.FinalFfGain = FinalFf.Gain;
        pipeline.ChannelMixer.FinalFfEnabled = FinalFf.Enabled;
        pipeline.ChannelMixer.WheelLoadWeighting = WheelLoadWeighting;
        pipeline.ChannelMixer.MzScale = MzScale;
        pipeline.ChannelMixer.FxScale = FxScale;
        pipeline.ChannelMixer.FyScale = FyScale;
        pipeline.ChannelMixer.MixMode = MixMode switch
        {
            FfbMixModeDto.Replace => FfbMixMode.Replace,
            FfbMixModeDto.Overlay => FfbMixMode.Overlay,
            _ => FfbMixMode.Replace
        };

        pipeline.ChannelMixer.FyInverted = FyInverted;
        pipeline.GearShiftFilterEnabled = Slip.GearChangeMuteEnabled;

        pipeline.MasterGain = 1000f / Math.Max(NormalizationScale, 1f);
        pipeline.ForceScale = ForceScale;
        pipeline.OutputGain = OutputGain;
        pipeline.CompressionPower = CompressionPower;
        pipeline.SignCorrectionEnabled = SignCorrectionEnabled;
        pipeline.OutputClipper.SoftClipThreshold = SoftClipThreshold;

        if (LutCurve.OutputValues.Length == FfbLutCurve.DefaultPointCount)
        {
            var inputs = new float[FfbLutCurve.DefaultPointCount];
            for (int i = 0; i < FfbLutCurve.DefaultPointCount; i++)
                inputs[i] = (float)i / (FfbLutCurve.DefaultPointCount - 1);

            pipeline.LutCurve.InputValues = inputs;
            pipeline.LutCurve.OutputValues = LutCurve.OutputValues;
        }

        pipeline.Damping.ViscousCoefficient = Damping.ViscousDamping;
        pipeline.Damping.SpeedDampingCoefficient = Damping.SpeedDamping;
        pipeline.Damping.FrictionLevel = Damping.Friction;
        pipeline.Damping.InertiaWeight = Damping.Inertia;
        pipeline.Damping.MaxSpeedReference = Damping.MaxSpeedReference;

        pipeline.SlipEnhancer.SlipRatioGain = Slip.SlipRatioGain;
        pipeline.SlipEnhancer.SlipAngleGain = Slip.SlipAngleGain;
        pipeline.SlipEnhancer.SlipAngleShapeGain = Slip.SlipAngleShapeGain;
        pipeline.SlipEnhancer.SlipThreshold = Slip.SlipThreshold;
        pipeline.SlipEnhancer.UseFrontOnly = Slip.UseFrontOnly;

        if (pipeline is R3eFfbPipeline r3e)
        {
            r3e.GearChangeMuteEnabled = Slip.GearChangeMuteEnabled;
            r3e.GearSpikeThreshold = Slip.GearSpikeThreshold;
            r3e.BrakeBoostGain = Slip.BrakeBoostGain;
            r3e.BrakeBoostThreshold = Slip.BrakeBoostThreshold;
            r3e.CoreForceMultiplier = Slip.CoreForceMultiplier;
        }

        if (pipeline is AcFfbPipeline ac)
        {
            ac.GearChangeMuteEnabled = Slip.GearChangeMuteEnabled;
            ac.GearSpikeThreshold = Slip.GearSpikeThreshold;
            ac.BrakeBoostGain = Slip.BrakeBoostGain;
            ac.BrakeBoostThreshold = Slip.BrakeBoostThreshold;
        }

        pipeline.DynamicEffects.LateralGGain = Dynamic.LateralGGain;
        pipeline.DynamicEffects.LongitudinalGGain = Dynamic.LongitudinalGGain;
        pipeline.DynamicEffects.SuspensionGain = Dynamic.SuspensionGain;
        pipeline.DynamicEffects.YawRateGain = Dynamic.YawRateGain;

        pipeline.AutoGainEnabled = AutoGain.Enabled;
        pipeline.AutoGainScale = AutoGain.Scale;

        pipeline.VibrationMixer.KerbGain = Vibrations.KerbGain;
        pipeline.VibrationMixer.SlipGain = Vibrations.SlipGain;
        pipeline.VibrationMixer.RoadGain = Vibrations.RoadGain;
        pipeline.VibrationMixer.AbsGain = Vibrations.AbsGain;
        pipeline.VibrationMixer.MasterGain = Vibrations.MasterGain;
        pipeline.VibrationMixer.SuspensionRoadGain = Vibrations.SuspensionRoadGain;
        pipeline.VibrationMixer.ScrubGain = Vibrations.ScrubGain;
        pipeline.VibrationMixer.RearSlipGain = Vibrations.RearSlipGain;
        pipeline.VibrationMixer.AbsPulseAmplitude = Vibrations.AbsPulseAmplitude;
        pipeline.VibrationMixer.CurbSeverityScale = Vibrations.CurbSeverityScale;
        pipeline.VibrationMixer.ScrubForceScale = Vibrations.ScrubForceScale;
        pipeline.VibrationMixer.RearSlipForceScale = Vibrations.RearSlipForceScale;
        pipeline.VibrationMixer.OfftrackGain = Vibrations.OfftrackGain;
        pipeline.VibrationMixer.OfftrackSeverityScale = Vibrations.OfftrackSeverityScale;

        pipeline.MaxSlewRate = Advanced.MaxSlewRate;
        pipeline.CenterSuppressionDegrees = Advanced.CenterSuppressionDegrees;
        pipeline.CenterKneePower = Advanced.CenterKneePower;
        pipeline.HysteresisThreshold = Advanced.HysteresisThreshold;
        pipeline.NoiseFloor = Advanced.NoiseFloor;
        pipeline.HysteresisWatchdogFrames = Advanced.HysteresisWatchdogFrames;
        pipeline.ChannelMixer.CenterBlendDegrees = Advanced.CenterBlendDegrees;
        pipeline.CenterSharpnessDegrees = Advanced.CenterSharpnessDegrees;
        pipeline.CoreForceMultiplier = Advanced.CoreForceMultiplier;
        pipeline.Damping.SteerVelocityReference = Advanced.SteerVelocityReference;
        pipeline.Damping.VelocityDeadzone = Advanced.VelocityDeadzone;
        pipeline.ChannelMixer.LowSpeedSmoothKmh = Advanced.LowSpeedSmoothKmh;

        pipeline.LfeGenerator.Enabled = Lfe.Enabled;
        pipeline.LfeGenerator.Gain = Lfe.Gain;
        pipeline.LfeGenerator.Frequency = Lfe.Frequency;
        pipeline.LfeGenerator.SuspensionDrive = Lfe.SuspensionDrive;
        pipeline.LfeGenerator.SpeedScaling = Lfe.SpeedScaling;
        pipeline.LfeGenerator.RpmDrive = Lfe.RpmDrive;

        pipeline.Equalizer.MasterEnabled = Equalizer.Enabled;
        for (int i = 0; i < FfbEqualizer.BandInfo.Length; i++)
        {
            pipeline.Equalizer.SetBandGain(i, Equalizer.GetGain(i));
        }

        pipeline.TyreFlex.FlexGain = TyreFlex.FlexGain;
        pipeline.TyreFlex.CarcassStiffness = TyreFlex.CarcassStiffness;
        pipeline.TyreFlex.FlexSmoothing = TyreFlex.FlexSmoothing;
        pipeline.TyreFlex.ContactPatchWeight = TyreFlex.ContactPatchWeight;
        pipeline.TyreFlex.LoadFlexGain = TyreFlex.LoadFlexGain;
        pipeline.TyreFlex.ContactPatchSensitivity = TyreFlex.ContactPatchSensitivity;

        pipeline.Hf8SignalMapper.Enabled = Hf8.Enabled;
        pipeline.Hf8SignalMapper.MasterGain = Hf8.MasterGain;
        for (int i = 0; i < Hf8SignalMapper.ZoneCount; i++)
        {
            pipeline.Hf8SignalMapper.ZoneGains[i] = Hf8.GetZoneGain(i);
            pipeline.Hf8SignalMapper.ZoneEnabled[i] = Hf8.GetZoneEnabled(i);
            for (int s = 0; s < Hf8SignalMapper.SourceCount; s++)
                pipeline.Hf8SignalMapper.SetSourceWeight(i, s, Hf8.GetSourceWeight(i, s));
        }

        pipeline.GripGuard.Enabled = GripGuard.Enabled;
        pipeline.GripGuard.PeakSlipAngle = GripGuard.PeakSlipAngle;
        pipeline.GripGuard.AttenuationStrength = GripGuard.AttenuationStrength;
        pipeline.GripGuard.MechanicalTrailGain = GripGuard.MechanicalTrailGain;
        pipeline.GripGuard.MinSpeedKmh = GripGuard.MinSpeedKmh;

        pipeline.CrashDetector.Enabled = Crash.Enabled;
        pipeline.CrashDetector.ImpactGain = Crash.ImpactGain;
        pipeline.CrashDetector.SafetyClamp = Crash.SafetyClamp;
        pipeline.CrashDetector.DecayRate = Crash.DecayRate;
        pipeline.CrashDetector.TriggerThresholdG = Crash.TriggerThresholdG;
        pipeline.CrashDetector.MinSpeedKmh = Crash.MinSpeedKmh;
        pipeline.CrashDetector.SafetyOverride = Crash.SafetyOverride;

        pipeline.TyreCondition.Enabled = TyreCondition.Enabled;
        pipeline.TyreCondition.BlowoutVibrationGain = TyreCondition.BlowoutVibrationGain;
        pipeline.TyreCondition.PressureLossGain = TyreCondition.PressureLossGain;
        pipeline.TyreCondition.DamageAsymmetryGain = TyreCondition.DamageAsymmetryGain;
        pipeline.TyreCondition.BlowoutPressureThreshold = TyreCondition.BlowoutPressureThreshold;
        pipeline.TyreCondition.MaxBlowoutAmplitude = TyreCondition.MaxBlowoutAmplitude;

        pipeline.WetWeather.Enabled = WetWeather.Enabled;
        pipeline.WetWeather.AutoDetect = WetWeather.AutoDetect;
        pipeline.WetWeather.ManualIntensity = WetWeather.ManualIntensity;
        pipeline.WetWeather.RoadVibSuppression = WetWeather.RoadVibSuppression;
        pipeline.WetWeather.CurbSuppression = WetWeather.CurbSuppression;
        pipeline.WetWeather.ScrubSuppression = WetWeather.ScrubSuppression;
        pipeline.WetWeather.PeakSlipAngleMultiplier = WetWeather.PeakSlipAngleMultiplier;
        pipeline.WetWeather.DampingReduction = WetWeather.DampingReduction;
        pipeline.WetWeather.NoiseFloorSuppression = WetWeather.NoiseFloorSuppression;
        pipeline.WetWeather.HydroplaningEnabled = WetWeather.HydroplaningEnabled;
        pipeline.WetWeather.HydroplaningSpeedThreshold = WetWeather.HydroplaningSpeedThreshold;
        pipeline.WetWeather.HydroplaningMaxAttenuation = WetWeather.HydroplaningMaxAttenuation;
    }

    public void ApplyToStaticFriction(FfbStaticFriction sf)
    {
        sf.Gain = StaticFriction.Gain;
        sf.MaxElasticStretch = StaticFriction.MaxElasticStretch;
        sf.SpringStiffness = StaticFriction.SpringStiffness;
        sf.KineticFrictionBase = StaticFriction.KineticFrictionBase;
        sf.EngineOffDamping = StaticFriction.EngineOffDamping;
        sf.EngineOnDamping = StaticFriction.EngineOnDamping;
        sf.EngineOffScale = StaticFriction.EngineOffScale;
        sf.EngineOnScale = StaticFriction.EngineOnScale;
        sf.ActiveDecay = StaticFriction.ActiveDecay;
        sf.ReturnDecay = StaticFriction.ReturnDecay;
        sf.OutputSmoothAlpha = StaticFriction.OutputSmoothAlpha;
    }

    public static FfbProfile CreateFromPipeline(FfbPipeline pipeline, string name)
    {
        var profile = new FfbProfile { Name = name };
        profile.UpdateFromPipeline(pipeline);
        return profile;
    }

    public void UpdateFromPipeline(FfbPipeline pipeline)
    {
        MixMode = pipeline.ChannelMixer.MixMode switch
        {
            FfbMixMode.Replace => FfbMixModeDto.Replace,
            _ => FfbMixModeDto.Overlay
        };
        OutputGain = pipeline.OutputGain;
        ForceScale = pipeline.ForceScale;
        CompressionPower = pipeline.CompressionPower;
        SignCorrectionEnabled = pipeline.SignCorrectionEnabled;
        SoftClipThreshold = pipeline.OutputClipper.SoftClipThreshold;
        NormalizationScale = 1000f / Math.Max(pipeline.MasterGain, 0.001f);

        MzFront = new ChannelConfig { Gain = pipeline.ChannelMixer.MzFrontGain, Enabled = pipeline.ChannelMixer.MzFrontEnabled };
        FxFront = new ChannelConfig { Gain = pipeline.ChannelMixer.FxFrontGain, Enabled = pipeline.ChannelMixer.FxFrontEnabled };
        FyFront = new ChannelConfig { Gain = pipeline.ChannelMixer.FyFrontGain, Enabled = pipeline.ChannelMixer.FyFrontEnabled };
        MzRear = new ChannelConfig { Gain = pipeline.ChannelMixer.MzRearGain, Enabled = pipeline.ChannelMixer.MzRearEnabled };
        FxRear = new ChannelConfig { Gain = pipeline.ChannelMixer.FxRearGain, Enabled = pipeline.ChannelMixer.FxRearEnabled };
        FyRear = new ChannelConfig { Gain = pipeline.ChannelMixer.FyRearGain, Enabled = pipeline.ChannelMixer.FyRearEnabled };
        FinalFf = new ChannelConfig { Gain = pipeline.ChannelMixer.FinalFfGain, Enabled = pipeline.ChannelMixer.FinalFfEnabled };
        WheelLoadWeighting = pipeline.ChannelMixer.WheelLoadWeighting;
        FyInverted = pipeline.ChannelMixer.FyInverted;
        MzScale = pipeline.ChannelMixer.MzScale;
        FxScale = pipeline.ChannelMixer.FxScale;
        FyScale = pipeline.ChannelMixer.FyScale;
        LutCurve = new LutCurveDto { OutputValues = pipeline.LutCurve.OutputValues };

        Damping = new DampingConfig
        {
            ViscousDamping = pipeline.Damping.ViscousCoefficient,
            SpeedDamping = pipeline.Damping.SpeedDampingCoefficient,
            Friction = pipeline.Damping.FrictionLevel,
            Inertia = pipeline.Damping.InertiaWeight,
            MaxSpeedReference = pipeline.Damping.MaxSpeedReference
        };
        Slip = new SlipConfig
        {
            SlipRatioGain = pipeline.SlipEnhancer.SlipRatioGain,
            SlipAngleGain = pipeline.SlipEnhancer.SlipAngleGain,
            SlipAngleShapeGain = pipeline.SlipEnhancer.SlipAngleShapeGain,
            SlipThreshold = pipeline.SlipEnhancer.SlipThreshold,
            UseFrontOnly = pipeline.SlipEnhancer.UseFrontOnly,
            GearChangeMuteEnabled = (pipeline as R3eFfbPipeline)?.GearChangeMuteEnabled
                                  ?? (pipeline as AcFfbPipeline)?.GearChangeMuteEnabled
                                  ?? pipeline.GearShiftFilterEnabled,
            GearChangeMuteFrames = 20,
            GearSpikeThreshold = (pipeline as R3eFfbPipeline)?.GearSpikeThreshold
                               ?? (pipeline as AcFfbPipeline)?.GearSpikeThreshold
                               ?? 3000f,
            BrakeBoostGain = (pipeline as R3eFfbPipeline)?.BrakeBoostGain
                           ?? (pipeline as AcFfbPipeline)?.BrakeBoostGain
                           ?? 0.4f,
            BrakeBoostThreshold = (pipeline as R3eFfbPipeline)?.BrakeBoostThreshold
                                ?? (pipeline as AcFfbPipeline)?.BrakeBoostThreshold
                                ?? 0.1f
        };
        Dynamic = new DynamicConfig
        {
            LateralGGain = pipeline.DynamicEffects.LateralGGain,
            LongitudinalGGain = pipeline.DynamicEffects.LongitudinalGGain,
            SuspensionGain = pipeline.DynamicEffects.SuspensionGain,
            YawRateGain = pipeline.DynamicEffects.YawRateGain
        };
        AutoGain = new AutoGainConfig
        {
            Enabled = pipeline.AutoGainEnabled,
            Scale = pipeline.AutoGainScale
        };
        Vibrations = new VibrationConfig
        {
            KerbGain = pipeline.VibrationMixer.KerbGain,
            SlipGain = pipeline.VibrationMixer.SlipGain,
            RoadGain = pipeline.VibrationMixer.RoadGain,
            AbsGain = pipeline.VibrationMixer.AbsGain,
            MasterGain = pipeline.VibrationMixer.MasterGain,
            SuspensionRoadGain = pipeline.VibrationMixer.SuspensionRoadGain,
            ScrubGain = pipeline.VibrationMixer.ScrubGain,
            RearSlipGain = pipeline.VibrationMixer.RearSlipGain,
            AbsPulseAmplitude = pipeline.VibrationMixer.AbsPulseAmplitude,
            CurbSeverityScale = pipeline.VibrationMixer.CurbSeverityScale,
            ScrubForceScale = pipeline.VibrationMixer.ScrubForceScale,
            RearSlipForceScale = pipeline.VibrationMixer.RearSlipForceScale,
            OfftrackGain = pipeline.VibrationMixer.OfftrackGain,
            OfftrackSeverityScale = pipeline.VibrationMixer.OfftrackSeverityScale
        };
        Advanced = new AdvancedConfig
        {
            MaxSlewRate = pipeline.MaxSlewRate,
            CenterSuppressionDegrees = pipeline.CenterSuppressionDegrees,
            CenterKneePower = pipeline.CenterKneePower,
            HysteresisThreshold = pipeline.HysteresisThreshold,
            NoiseFloor = pipeline.NoiseFloor,
            HysteresisWatchdogFrames = pipeline.HysteresisWatchdogFrames,
            CenterBlendDegrees = pipeline.ChannelMixer.CenterBlendDegrees,
            CenterSharpnessDegrees = pipeline.CenterSharpnessDegrees,
            CoreForceMultiplier = pipeline.CoreForceMultiplier,
            SteerVelocityReference = pipeline.Damping.SteerVelocityReference,
            VelocityDeadzone = pipeline.Damping.VelocityDeadzone,
            LowSpeedSmoothKmh = pipeline.ChannelMixer.LowSpeedSmoothKmh
        };
        Lfe = new LfeConfig
        {
            Enabled = pipeline.LfeGenerator.Enabled,
            Gain = pipeline.LfeGenerator.Gain,
            Frequency = pipeline.LfeGenerator.Frequency,
            SuspensionDrive = pipeline.LfeGenerator.SuspensionDrive,
            SpeedScaling = pipeline.LfeGenerator.SpeedScaling,
            RpmDrive = pipeline.LfeGenerator.RpmDrive
        };
        Equalizer = new EqConfig
        {
            Enabled = pipeline.Equalizer.MasterEnabled
        };
        for (int i = 0; i < FfbEqualizer.BandInfo.Length; i++)
        {
            Equalizer.SetGain(i, pipeline.Equalizer.GetBandGain(i));
        }
        TyreFlex = new TyreFlexConfig
        {
            FlexGain = pipeline.TyreFlex.FlexGain,
            CarcassStiffness = pipeline.TyreFlex.CarcassStiffness,
            FlexSmoothing = pipeline.TyreFlex.FlexSmoothing,
            ContactPatchWeight = pipeline.TyreFlex.ContactPatchWeight,
            LoadFlexGain = pipeline.TyreFlex.LoadFlexGain,
            ContactPatchSensitivity = pipeline.TyreFlex.ContactPatchSensitivity
        };
        Hf8 = new Hf8Config
        {
            Enabled = pipeline.Hf8SignalMapper.Enabled,
            MasterGain = pipeline.Hf8SignalMapper.MasterGain,
            ZoneGains = (float[])pipeline.Hf8SignalMapper.ZoneGains.Clone(),
            ZoneEnabled = (bool[])pipeline.Hf8SignalMapper.ZoneEnabled.Clone(),
            OutputRateHz = Hf8?.OutputRateHz ?? 75
        };
        for (int z = 0; z < Hf8SignalMapper.ZoneCount; z++)
            for (int s = 0; s < Hf8SignalMapper.SourceCount; s++)
                Hf8.SetSourceWeight(z, s, pipeline.Hf8SignalMapper.GetSourceWeight(z, s));
        GripGuard = new GripGuardConfig
        {
            Enabled = pipeline.GripGuard.Enabled,
            PeakSlipAngle = pipeline.GripGuard.PeakSlipAngle,
            AttenuationStrength = pipeline.GripGuard.AttenuationStrength,
            MechanicalTrailGain = pipeline.GripGuard.MechanicalTrailGain,
            MinSpeedKmh = pipeline.GripGuard.MinSpeedKmh
        };
        Crash = new CrashConfig
        {
            Enabled = pipeline.CrashDetector.Enabled,
            ImpactGain = pipeline.CrashDetector.ImpactGain,
            SafetyClamp = pipeline.CrashDetector.SafetyClamp,
            DecayRate = pipeline.CrashDetector.DecayRate,
            TriggerThresholdG = pipeline.CrashDetector.TriggerThresholdG,
            MinSpeedKmh = pipeline.CrashDetector.MinSpeedKmh,
            SafetyOverride = pipeline.CrashDetector.SafetyOverride
        };
        TyreCondition = new TyreConditionConfig
        {
            Enabled = pipeline.TyreCondition.Enabled,
            BlowoutVibrationGain = pipeline.TyreCondition.BlowoutVibrationGain,
            PressureLossGain = pipeline.TyreCondition.PressureLossGain,
            DamageAsymmetryGain = pipeline.TyreCondition.DamageAsymmetryGain,
            BlowoutPressureThreshold = pipeline.TyreCondition.BlowoutPressureThreshold,
            MaxBlowoutAmplitude = pipeline.TyreCondition.MaxBlowoutAmplitude
        };
        WetWeather = new WetWeatherConfig
        {
            Enabled = pipeline.WetWeather.Enabled,
            AutoDetect = pipeline.WetWeather.AutoDetect,
            ManualIntensity = pipeline.WetWeather.ManualIntensity,
            RoadVibSuppression = pipeline.WetWeather.RoadVibSuppression,
            CurbSuppression = pipeline.WetWeather.CurbSuppression,
            ScrubSuppression = pipeline.WetWeather.ScrubSuppression,
            PeakSlipAngleMultiplier = pipeline.WetWeather.PeakSlipAngleMultiplier,
            DampingReduction = pipeline.WetWeather.DampingReduction,
            NoiseFloorSuppression = pipeline.WetWeather.NoiseFloorSuppression,
            HydroplaningEnabled = pipeline.WetWeather.HydroplaningEnabled,
            HydroplaningSpeedThreshold = pipeline.WetWeather.HydroplaningSpeedThreshold,
            HydroplaningMaxAttenuation = pipeline.WetWeather.HydroplaningMaxAttenuation
        };
    }

    public static FfbProfile GetDefaultProfile(string name)
    {
        return name switch
        {
            "Heavy" => new FfbProfile
            {
                Name = "Heavy",
                OutputGain = 0.85f,
                NormalizationScale = 1000f,
                MzScale = 30f, FxScale = 4000f, FyScale = 5000f,
                SoftClipThreshold = 0.8f,
                MzFront = new ChannelConfig { Gain = 0.50f, Enabled = true },
                FxFront = new ChannelConfig { Gain = 0.15f, Enabled = true },
                FyFront = new ChannelConfig { Gain = 0.22f, Enabled = true },
                Damping = new DampingConfig { ViscousDamping = 0.15f, SpeedDamping = 0.50f, Friction = 0.18f, Inertia = 0.12f, MaxSpeedReference = 200f },
                Slip = new SlipConfig { SlipRatioGain = 0.10f, SlipAngleGain = 0.20f, SlipThreshold = 0.10f, UseFrontOnly = true },
                Dynamic = new DynamicConfig { SuspensionGain = 0.40f },
                Vibrations = new VibrationConfig { KerbGain = 1.0f, SlipGain = 0.8f, RoadGain = 0f, AbsGain = 1.0f, MasterGain = 0.55f, ScrubGain = 0.5f, RearSlipGain = 0.6f },
                Advanced = new AdvancedConfig { MaxSlewRate = 0.85f, NoiseFloor = 0.003f, CenterBlendDegrees = 1.0f }
            },
            "Light" => new FfbProfile
            {
                Name = "Light",
                OutputGain = 0.45f,
                NormalizationScale = 1000f,
                MzScale = 30f, FxScale = 4000f, FyScale = 5000f,
                SoftClipThreshold = 0.8f,
                MzFront = new ChannelConfig { Gain = 0.35f, Enabled = true },
                FxFront = new ChannelConfig { Gain = 0.12f, Enabled = true },
                FyFront = new ChannelConfig { Gain = 0.15f, Enabled = true },
                Damping = new DampingConfig { ViscousDamping = 0.10f, SpeedDamping = 0.30f, Friction = 0.10f, Inertia = 0.08f, MaxSpeedReference = 200f },
                Slip = new SlipConfig { SlipRatioGain = 0.08f, SlipAngleGain = 0.15f, SlipThreshold = 0.10f, UseFrontOnly = true },
                Dynamic = new DynamicConfig { SuspensionGain = 0.30f },
                Vibrations = new VibrationConfig { KerbGain = 0.8f, SlipGain = 0.6f, RoadGain = 0f, AbsGain = 0.8f, MasterGain = 0.40f, ScrubGain = 0.4f, RearSlipGain = 0.5f },
                Advanced = new AdvancedConfig { MaxSlewRate = 0.85f, NoiseFloor = 0.003f, CenterBlendDegrees = 1.0f }
            },

            // ──── Moza R5 Baseline (5.5Nm) — tested V2 profile ────
            "Moza R5 - Final Stable Baseline" => CreateDefaultWheelbaseProfile("Moza R5 - Final Stable Baseline",
                maxTorqueNm: 5.5f, outputGain: 0.621f, normalizationScale: 1000f,
                viscousDamping: 0.15f, speedDamping: 0.50f, friction: 0.15f, inertia: 0.10f,
                vibrationMaster: 0.50f),

            // ──── Logitech G27 (2.5Nm) — gear-driven, same VID as G29 ────
            "Default - Logitech G27" => CreateGearDrivenProfile("Default - Logitech G27",
                maxTorqueNm: 2.5f, outputGain: 1.5f, normalizationScale: 1000f,
                viscousDamping: 0.12f, speedDamping: 0.38f, friction: 0.14f, inertia: 0.09f,
                vibrationMaster: 0.50f),

            // ──── Logitech G29/G920 (2.1Nm) — gear-driven ────
            "Default - Logitech G29/G920" => CreateGearDrivenProfile("Default - Logitech G29/G920",
                maxTorqueNm: 2.1f, outputGain: 1.5f, normalizationScale: 1000f,
                viscousDamping: 0.10f, speedDamping: 0.35f, friction: 0.12f, inertia: 0.08f,
                vibrationMaster: 0.55f),

            // ──── Thrustmaster T300/TX (4.5Nm) ────
            "Default - Thrustmaster T300/TX" => CreateDefaultWheelbaseProfile("Default - Thrustmaster T300/TX",
                maxTorqueNm: 4.5f, outputGain: 0.72f, normalizationScale: 1000f,
                viscousDamping: 0.13f, speedDamping: 0.45f, friction: 0.14f, inertia: 0.09f,
                vibrationMaster: 0.50f),

            // ──── Fanatec CSL DD 5Nm ────
            "Default - Fanatec CSL DD 5Nm" => CreateDefaultWheelbaseProfile("Default - Fanatec CSL DD 5Nm",
                maxTorqueNm: 5.0f, outputGain: 0.65f, normalizationScale: 1000f,
                viscousDamping: 0.14f, speedDamping: 0.48f, friction: 0.15f, inertia: 0.10f,
                vibrationMaster: 0.50f),

            // ──── Fanatec CSL DD 8Nm ────
            "Default - Fanatec CSL DD 8Nm" => CreateDefaultWheelbaseProfile("Default - Fanatec CSL DD 8Nm",
                maxTorqueNm: 8.0f, outputGain: 0.42f, normalizationScale: 1000f,
                viscousDamping: 0.18f, speedDamping: 0.50f, friction: 0.15f, inertia: 0.10f,
                vibrationMaster: 0.42f),

            // ──── Moza R9 (9Nm) ────
            "Default - Moza R9" => CreateDefaultWheelbaseProfile("Default - Moza R9",
                maxTorqueNm: 9.0f, outputGain: 0.38f, normalizationScale: 1000f,
                viscousDamping: 0.19f, speedDamping: 0.50f, friction: 0.15f, inertia: 0.10f,
                vibrationMaster: 0.40f),

            // ──── Fanatec ClubSport DD (15Nm) ────
            "Default - Fanatec ClubSport DD" => CreateDefaultWheelbaseProfile("Default - Fanatec ClubSport DD",
                maxTorqueNm: 15.0f, outputGain: 0.23f, normalizationScale: 1000f,
                viscousDamping: 0.25f, speedDamping: 0.50f, friction: 0.16f, inertia: 0.12f,
                vibrationMaster: 0.35f),

            // ──── Simagic Alpha (15Nm) ────
            "Default - Simagic Alpha" => CreateDefaultWheelbaseProfile("Default - Simagic Alpha",
                maxTorqueNm: 15.0f, outputGain: 0.23f, normalizationScale: 1000f,
                viscousDamping: 0.25f, speedDamping: 0.50f, friction: 0.16f, inertia: 0.12f,
                vibrationMaster: 0.35f,
                forceInvert: true),

            // ──── Simucube 2 Pro (25Nm) ────
            "Default - Simucube 2 Pro" => CreateDefaultWheelbaseProfile("Default - Simucube 2 Pro",
                maxTorqueNm: 25.0f, outputGain: 0.14f, normalizationScale: 1000f,
                viscousDamping: 0.32f, speedDamping: 0.50f, friction: 0.18f, inertia: 0.14f,
                vibrationMaster: 0.30f),

            _ => new FfbProfile
            {
                Name = name,
                OutputGain = 0.621f,
                NormalizationScale = 1000f,
                MzScale = 30f, FxScale = 4000f, FyScale = 5000f,
                SoftClipThreshold = 0.8f,
                MzFront = new ChannelConfig { Gain = 0.42f, Enabled = true },
                FxFront = new ChannelConfig { Gain = 0.15f, Enabled = true },
                FyFront = new ChannelConfig { Gain = 0.20f, Enabled = true },
                Damping = new DampingConfig { ViscousDamping = 0.15f, SpeedDamping = 0.50f, Friction = 0.15f, Inertia = 0.10f, MaxSpeedReference = 200f },
                Slip = new SlipConfig { SlipRatioGain = 0.10f, SlipAngleGain = 0.20f, SlipThreshold = 0.10f, UseFrontOnly = true },
                Dynamic = new DynamicConfig { SuspensionGain = 0.40f },
                Vibrations = new VibrationConfig { KerbGain = 1.0f, SlipGain = 0.8f, RoadGain = 0f, AbsGain = 1.0f, MasterGain = 0.50f, ScrubGain = 0.5f, RearSlipGain = 0.6f },
                Advanced = new AdvancedConfig { MaxSlewRate = 0.85f, NoiseFloor = 0.003f, CenterBlendDegrees = 1.0f }
            }
        };
    }

    private static FfbProfile CreateDefaultWheelbaseProfile(
        string name, float maxTorqueNm, float outputGain, float normalizationScale,
        float viscousDamping, float speedDamping, float friction, float inertia, float vibrationMaster,
        bool forceInvert = false)
    {
        return new FfbProfile
        {
            Name = name,
            OutputGain = outputGain,
            NormalizationScale = normalizationScale,
            ForceScale = 1.0f,
            SoftClipThreshold = 0.8f,
            CompressionPower = 1.0f,
            SignCorrectionEnabled = true,
            ForceInvertEnabled = forceInvert,
            WheelMaxTorqueNm = maxTorqueNm,
            MzFront = new ChannelConfig { Gain = 0.42f, Enabled = true },
            FxFront = new ChannelConfig { Gain = 0.15f, Enabled = true },
            FyFront = new ChannelConfig { Gain = 0.20f, Enabled = true },
            MzRear = new ChannelConfig { Gain = 0.0f, Enabled = false },
            FxRear = new ChannelConfig { Gain = 0.0f, Enabled = false },
            FyRear = new ChannelConfig { Gain = 0.0f, Enabled = false },
            FinalFf = new ChannelConfig { Gain = 0.0f, Enabled = false },
            WheelLoadWeighting = 0.0f,
            MzScale = 30f,
            FxScale = 4000f,
            FyScale = 5000f,
            LutCurve = LutCurveDto.Linear(),
            SteeringLockDegrees = 900,
            Damping = new DampingConfig
            {
                ViscousDamping = viscousDamping,
                SpeedDamping = speedDamping,
                Friction = friction,
                Inertia = inertia,
                MaxSpeedReference = 200f
            },
            Slip = new SlipConfig { SlipRatioGain = 0.10f, SlipAngleGain = 0.20f, SlipThreshold = 0.10f, UseFrontOnly = true },
            Dynamic = new DynamicConfig { LateralGGain = 0f, LongitudinalGGain = 0f, SuspensionGain = 0.40f, YawRateGain = 0f },
            AutoGain = new AutoGainConfig { Enabled = false, Scale = 1.0f },
            Vibrations = new VibrationConfig { KerbGain = 1.0f, SlipGain = 0.8f, RoadGain = 0f, AbsGain = 1.0f, MasterGain = vibrationMaster, ScrubGain = 0.5f, RearSlipGain = 0.6f },
            Advanced = new AdvancedConfig { MaxSlewRate = 0.85f, NoiseFloor = 0.003f, CenterBlendDegrees = 1.0f },
            TyreFlex = new TyreFlexConfig { FlexGain = 0.12f }
        };
    }

    private static FfbProfile CreateGearDrivenProfile(
        string name, float maxTorqueNm, float outputGain, float normalizationScale,
        float viscousDamping, float speedDamping, float friction, float inertia, float vibrationMaster)
    {
        var lutValues = new float[33];
        for (int i = 0; i < 33; i++)
        {
            float t = (float)i / 32f;
            lutValues[i] = MathF.Sqrt(t);
        }

        return new FfbProfile
        {
            Name = name,
            OutputGain = outputGain,
            NormalizationScale = normalizationScale,
            ForceScale = 1.0f,
            SoftClipThreshold = 0.75f,
            CompressionPower = 1.0f,
            SignCorrectionEnabled = true,
            ForceInvertEnabled = false,
            WheelMaxTorqueNm = maxTorqueNm,
            MzFront = new ChannelConfig { Gain = 0.42f, Enabled = true },
            FxFront = new ChannelConfig { Gain = 0.18f, Enabled = true },
            FyFront = new ChannelConfig { Gain = 0.25f, Enabled = true },
            MzRear = new ChannelConfig { Gain = 0.0f, Enabled = false },
            FxRear = new ChannelConfig { Gain = 0.0f, Enabled = false },
            FyRear = new ChannelConfig { Gain = 0.0f, Enabled = false },
            FinalFf = new ChannelConfig { Gain = 0.0f, Enabled = false },
            WheelLoadWeighting = 0.0f,
            MzScale = 30f,
            FxScale = 4000f,
            FyScale = 5000f,
            LutCurve = new LutCurveDto { OutputValues = lutValues },
            SteeringLockDegrees = 900,
            Damping = new DampingConfig
            {
                ViscousDamping = viscousDamping,
                SpeedDamping = speedDamping,
                Friction = friction,
                Inertia = inertia,
                MaxSpeedReference = 200f
            },
            Slip = new SlipConfig { SlipRatioGain = 0.10f, SlipAngleGain = 0.20f, SlipThreshold = 0.10f, UseFrontOnly = true },
            Dynamic = new DynamicConfig { LateralGGain = 0f, LongitudinalGGain = 0f, SuspensionGain = 0.40f, YawRateGain = 0f },
            AutoGain = new AutoGainConfig { Enabled = false, Scale = 1.0f },
            Vibrations = new VibrationConfig { KerbGain = 1.0f, SlipGain = 0.8f, RoadGain = 0f, AbsGain = 1.0f, MasterGain = vibrationMaster, ScrubGain = 0.5f, RearSlipGain = 0.6f },
            Advanced = new AdvancedConfig { MaxSlewRate = 0.85f, NoiseFloor = 0.003f, CenterBlendDegrees = 1.0f },
            TyreFlex = new TyreFlexConfig { FlexGain = 0.12f }
        };
    }
    public static string[] AllDefaultNames => new[]
    {
        "Default", "Heavy", "Light", "Moza R5 - Final Stable Baseline",
        "Default - Logitech G27",
        "Default - Logitech G29/G920",
        "Default - Thrustmaster T300/TX",
        "Default - Fanatec CSL DD 5Nm",
        "Default - Fanatec CSL DD 8Nm",
        "Default - Moza R9",
        "Default - Fanatec ClubSport DD",
        "Default - Simagic Alpha",
        "Default - Simucube 2 Pro"
    };

    public void SanitizeFloats()
    {
        OutputGain = Sanitize(OutputGain);
        NormalizationScale = Sanitize(NormalizationScale);
        ForceScale = Sanitize(ForceScale);
        SoftClipThreshold = Sanitize(SoftClipThreshold);
        CompressionPower = Sanitize(CompressionPower);
        WheelMaxTorqueNm = Sanitize(WheelMaxTorqueNm);
        WheelLoadWeighting = Sanitize(WheelLoadWeighting);
        MzScale = Sanitize(MzScale);
        FxScale = Sanitize(FxScale);
        FyScale = Sanitize(FyScale);

        MzFront.Gain = Sanitize(MzFront.Gain);
        FxFront.Gain = Sanitize(FxFront.Gain);
        FyFront.Gain = Sanitize(FyFront.Gain);
        MzRear.Gain = Sanitize(MzRear.Gain);
        FxRear.Gain = Sanitize(FxRear.Gain);
        FyRear.Gain = Sanitize(FyRear.Gain);
        FinalFf.Gain = Sanitize(FinalFf.Gain);

        SanitizeArray(LutCurve.OutputValues);
        Damping.SanitizeFloats();
        Slip.SanitizeFloats();
        Dynamic.SanitizeFloats();
        AutoGain.SanitizeFloats();
        Vibrations.SanitizeFloats();
        Advanced.SanitizeFloats();
        Lfe.SanitizeFloats();
        Equalizer.SanitizeFloats();
        TyreFlex.SanitizeFloats();
        Hf8.SanitizeFloats();
        GripGuard.SanitizeFloats();
        StaticFriction.SanitizeFloats();
        Crash.SanitizeFloats();
        TyreCondition.SanitizeFloats();
        WetWeather.SanitizeFloats();
    }

    private static float Sanitize(float v) =>
        float.IsNaN(v) ? 0f : float.IsPositiveInfinity(v) ? float.MaxValue : float.IsNegativeInfinity(v) ? float.MinValue : v;

    private static void SanitizeArray(float[] arr)
    {
        for (int i = 0; i < arr.Length; i++)
            arr[i] = Sanitize(arr[i]);
    }

    internal void Migrate()
    {
        if (Version >= CurrentVersion) return;

        if (Version < 3)
        {
            if (Name == "Moza R5 - Final Stable Baseline")
            {
                Damping = new DampingConfig
                {
                    ViscousDamping = 0.15f,
                    SpeedDamping = 1.0f,
                    Friction = 0.5f,
                    Inertia = 0.2f,
                    MaxSpeedReference = 200f
                };
            }
            else if (Name == "Moza R5 - Connected Baseline")
            {
                MzScale = 30f;
                Damping = new DampingConfig
                {
                    ViscousDamping = 0.15f,
                    SpeedDamping = 1.0f,
                    Friction = 0.5f,
                    Inertia = 0.2f,
                    MaxSpeedReference = 200f
                };
            }
            else if (Name == "Default")
            {
                MzScale = 30f;
                FxScale = 4000f;
                FyScale = 5000f;
                FxFront = new ChannelConfig { Gain = 0.15f, Enabled = true };
                FyFront = new ChannelConfig { Gain = 0.2f, Enabled = true };
                Damping = new DampingConfig { SpeedDamping = 1.0f, Friction = 0.5f, Inertia = 0.2f, MaxSpeedReference = 200f };
            }
        }

        if (Version < 4)
        {
            Advanced = new AdvancedConfig();
        }

        if (Version < 5)
        {
            LastTelemetrySnapshot = null;
        }

        if (Version < 6)
        {
            Lfe = new LfeConfig();
        }

        if (Version < 7)
        {
            Equalizer = new EqConfig();
        }

        if (Version < 8)
        {
            var oldEq = Equalizer;
            Equalizer = new EqConfig { Enabled = oldEq.Enabled };
            if (oldEq.BandGains.Length >= 5)
            {
                Equalizer.SetGain(0, oldEq.GetGain(0));
                Equalizer.SetGain(1, oldEq.GetGain(1));
                Equalizer.SetGain(2, oldEq.GetGain(2));
                Equalizer.SetGain(3, oldEq.GetGain(3));
                Equalizer.SetGain(4, oldEq.GetGain(4));
            }
        }

        if (Version < 9)
        {
            // FFB Realism Overhaul: removed processing stages, updated defaults
            CompressionPower = 1.0f;
            Advanced ??= new AdvancedConfig();
            Advanced.MaxSlewRate = 0.40f;
            Advanced.CenterSuppressionDegrees = 1.5f;
            Advanced.HysteresisThreshold = 0f;
            Advanced.NoiseFloor = 0.003f;
            Advanced.HysteresisWatchdogFrames = 0;
            Advanced.LowSpeedSmoothKmh = 15.0f;
        }

        if (Version < 10)
        {
            // Mz Realism update: restored sign correction with _invertForce=true.
            // CenterSuppression restored to 1.5° for smooth quadratic center fade.
            Advanced ??= new AdvancedConfig();
            Advanced.CenterSuppressionDegrees = 1.5f;
            Advanced.CenterBlendDegrees = 1.0f;
            Advanced.LowSpeedSmoothKmh = 15.0f;
        }

        if (Version < 11)
        {
            TyreFlex ??= new TyreFlexConfig();
        }

        if (Version < 12)
        {
            if (Name.StartsWith("Default - ") && AllDefaultNames.Contains(Name))
            {
                var codeDefault = GetDefaultProfile(Name);
                OutputGain = codeDefault.OutputGain;
                NormalizationScale = codeDefault.NormalizationScale;
                ForceScale = codeDefault.ForceScale;
                SoftClipThreshold = codeDefault.SoftClipThreshold;
                CompressionPower = codeDefault.CompressionPower;
                SignCorrectionEnabled = codeDefault.SignCorrectionEnabled;
                WheelMaxTorqueNm = codeDefault.WheelMaxTorqueNm;
                MzFront = codeDefault.MzFront;
                FxFront = codeDefault.FxFront;
                FyFront = codeDefault.FyFront;
                MzRear = codeDefault.MzRear;
                FxRear = codeDefault.FxRear;
                FyRear = codeDefault.FyRear;
                FinalFf = codeDefault.FinalFf;
                WheelLoadWeighting = codeDefault.WheelLoadWeighting;
                MzScale = codeDefault.MzScale;
                FxScale = codeDefault.FxScale;
                FyScale = codeDefault.FyScale;
                Damping = codeDefault.Damping;
                Slip = codeDefault.Slip;
                Dynamic = codeDefault.Dynamic;
                Vibrations = codeDefault.Vibrations;
            }
        }


        if (Version < 13)
        {
            Damping ??= new DampingConfig();
            Damping.ViscousDamping = 0.15f;
            Advanced ??= new AdvancedConfig();
            Advanced.MaxSlewRate = 0.85f;
        }

        if (Version < 14)
        {
            Hf8 ??= new Hf8Config();
        }

        if (Version < 15)
        {
            GripGuard ??= new GripGuardConfig();
        }

        if (Version < 16)
        {
            StaticFriction ??= new StaticFrictionConfig();
        }

        if (Version < 17)
        {
            Crash ??= new CrashConfig();
        }

        if (Version < 18)
        {
            TyreCondition ??= new TyreConditionConfig();
        }

        if (Version < 19)
        {
            WetWeather ??= new WetWeatherConfig();
        }

        if (Version < 20)
        {
            TrackMatch ??= "";
        }

        if (Version < 21)
        {
            GameMatch ??= "";
            Scope = ProfileScope.General;
        }

        Version = CurrentVersion;
    }
}

public sealed class ChannelConfig
{
    public float Gain { get; set; } = 1.0f;
    public bool Enabled { get; set; } = true;

    private static float S(float v) => float.IsNaN(v) ? 0f : float.IsPositiveInfinity(v) ? float.MaxValue : float.IsNegativeInfinity(v) ? float.MinValue : v;
    public void SanitizeFloats() => Gain = S(Gain);
}

public sealed class LutCurveDto
{
    public float[] OutputValues { get; set; } = Array.Empty<float>();

    public static LutCurveDto Linear()
    {
        var values = new float[33];
        for (int i = 0; i < 33; i++)
            values[i] = (float)i / 32f;
        return new LutCurveDto { OutputValues = values };
    }

    public static LutCurveDto SoftCenter()
    {
        var values = new float[33];
        for (int i = 0; i < 33; i++)
        {
            float t = (float)i / 32f;
            if (t < 0.45f)
                values[i] = t * 0.7f;
            else if (t > 0.55f)
                values[i] = 1f - (1f - t) * 0.7f;
            else
                values[i] = t;
        }
        return new LutCurveDto { OutputValues = values };
    }

    public static LutCurveDto Progressive()
    {
        var values = new float[33];
        for (int i = 0; i < 33; i++)
        {
            float t = (float)i / 32f;
            values[i] = MathF.Pow(t, 2.0f);
        }
        return new LutCurveDto { OutputValues = values };
    }
}

public sealed class DampingConfig
{
    /// <summary>
    /// Pure viscous damping — always active, NOT speed-dependent.
    /// Represents steering column friction / hydraulic fluid resistance.
    /// Primary defense against DD wheel oscillation at all speeds.
    /// </summary>
    public float ViscousDamping { get; set; } = 0.15f;

    /// <summary>
    /// Gyroscopic (speed-sensitive) damping coefficient.
    /// </summary>
    [JsonPropertyName("speedDamping")]
    public float SpeedDamping { get; set; } = 0.50f;

    public float Friction { get; set; } = 0.15f;
    public float Inertia { get; set; } = 0.1f;

    [JsonPropertyName("dampingSpeedReference")]
    public float MaxSpeedReference { get; set; } = 200f;

    [JsonIgnore]
    public float LowSpeedDampingBoost { get; set; } = 1.0f;

    [JsonIgnore]
    public float LowSpeedThreshold { get; set; } = 20f;

    private static float S(float v) => float.IsNaN(v) ? 0f : float.IsPositiveInfinity(v) ? float.MaxValue : float.IsNegativeInfinity(v) ? float.MinValue : v;
    public void SanitizeFloats() { ViscousDamping = S(ViscousDamping); SpeedDamping = S(SpeedDamping); Friction = S(Friction); Inertia = S(Inertia); MaxSpeedReference = S(MaxSpeedReference); }
}

public sealed class SlipConfig
{
    public float SlipRatioGain { get; set; } = 0.10f;
    public float SlipAngleGain { get; set; } = 0.20f;
    public float SlipAngleShapeGain { get; set; } = 0.0f;
    public float SlipThreshold { get; set; } = 0.10f;
    public bool UseFrontOnly { get; set; } = true;
    public bool GearChangeMuteEnabled { get; set; } = true;
    public int GearChangeMuteFrames { get; set; } = 20;
    public float GearSpikeThreshold { get; set; } = 3000f;
    public float BrakeBoostGain { get; set; } = 0.4f;
    public float BrakeBoostThreshold { get; set; } = 0.1f;
    public float CoreForceMultiplier { get; set; } = 3.0f;

    private static float S(float v) => float.IsNaN(v) ? 0f : float.IsPositiveInfinity(v) ? float.MaxValue : float.IsNegativeInfinity(v) ? float.MinValue : v;
    public void SanitizeFloats() { SlipRatioGain = S(SlipRatioGain); SlipAngleGain = S(SlipAngleGain); SlipAngleShapeGain = S(SlipAngleShapeGain); SlipThreshold = S(SlipThreshold); BrakeBoostGain = S(BrakeBoostGain); BrakeBoostThreshold = S(BrakeBoostThreshold); CoreForceMultiplier = S(CoreForceMultiplier); }
}

public sealed class DynamicConfig
{
    [JsonPropertyName("corneringForce")]
    public float LateralGGain { get; set; } = 0.0f;

    [JsonPropertyName("accelerationBrakingForce")]
    public float LongitudinalGGain { get; set; } = 0.0f;

    [JsonPropertyName("roadFeel")]
    public float SuspensionGain { get; set; } = 0.40f;

    [JsonPropertyName("carRotationForce")]
    public float YawRateGain { get; set; } = 0.0f;

    private static float S(float v) => float.IsNaN(v) ? 0f : float.IsPositiveInfinity(v) ? float.MaxValue : float.IsNegativeInfinity(v) ? float.MinValue : v;
    public void SanitizeFloats() { LateralGGain = S(LateralGGain); LongitudinalGGain = S(LongitudinalGGain); SuspensionGain = S(SuspensionGain); YawRateGain = S(YawRateGain); }
}

public sealed class AutoGainConfig
{
    public bool Enabled { get; set; } = false;
    public float Scale { get; set; } = 1.0f;

    private static float S(float v) => float.IsNaN(v) ? 0f : float.IsPositiveInfinity(v) ? float.MaxValue : float.IsNegativeInfinity(v) ? float.MinValue : v;
    public void SanitizeFloats() => Scale = S(Scale);
}

public sealed class VibrationConfig
{
    [JsonPropertyName("curbGain")]
    public float KerbGain { get; set; } = 1.0f;
    public float SlipGain { get; set; } = 0.8f;
    public float RoadGain { get; set; } = 0f;
    public float AbsGain { get; set; } = 1.0f;
    public float MasterGain { get; set; } = 0.5f;
    public float SuspensionRoadGain { get; set; } = 1.5f;

    // ── Tire scrub / limit-feel vibration ──
    public float ScrubGain { get; set; } = 0.50f;
    public float RearSlipGain { get; set; } = 0.60f;
    public float AbsPulseAmplitude { get; set; } = 0.25f;
    public float CurbSeverityScale { get; set; } = 10.0f;
    public float ScrubForceScale { get; set; } = 0.0005f;
    public float RearSlipForceScale { get; set; } = 0.0005f;

    public float OfftrackGain { get; set; } = 0.5f;
    public float OfftrackSeverityScale { get; set; } = 3.0f;

    private static float S(float v) => float.IsNaN(v) ? 0f : float.IsPositiveInfinity(v) ? float.MaxValue : float.IsNegativeInfinity(v) ? float.MinValue : v;
    public void SanitizeFloats() { KerbGain = S(KerbGain); SlipGain = S(SlipGain); RoadGain = S(RoadGain); AbsGain = S(AbsGain); MasterGain = S(MasterGain); SuspensionRoadGain = S(SuspensionRoadGain); ScrubGain = S(ScrubGain); RearSlipGain = S(RearSlipGain); AbsPulseAmplitude = S(AbsPulseAmplitude); CurbSeverityScale = S(CurbSeverityScale); ScrubForceScale = S(ScrubForceScale); RearSlipForceScale = S(RearSlipForceScale); OfftrackGain = S(OfftrackGain); OfftrackSeverityScale = S(OfftrackSeverityScale); }
}

public sealed class AdvancedConfig
{
    public float MaxSlewRate { get; set; } = 0.85f;
    public float CenterSuppressionDegrees { get; set; } = 1.5f;
    public float CenterKneePower { get; set; } = 1.0f;
    public float HysteresisThreshold { get; set; } = 0f;
    public float NoiseFloor { get; set; } = 0.003f;
    public int HysteresisWatchdogFrames { get; set; } = 0;
    public float CenterBlendDegrees { get; set; } = 1.0f;
    public float CenterSharpnessDegrees { get; set; } = 3.0f;
    public float CoreForceMultiplier { get; set; } = 1.0f;
    public float SteerVelocityReference { get; set; } = 10.0f;
    public float VelocityDeadzone { get; set; } = 0.05f;
    public float LowSpeedSmoothKmh { get; set; } = 15.0f;

    private static float S(float v) => float.IsNaN(v) ? 0f : float.IsPositiveInfinity(v) ? float.MaxValue : float.IsNegativeInfinity(v) ? float.MinValue : v;
    public void SanitizeFloats() { MaxSlewRate = S(MaxSlewRate); CenterSuppressionDegrees = S(CenterSuppressionDegrees); CenterKneePower = S(CenterKneePower); HysteresisThreshold = S(HysteresisThreshold); NoiseFloor = S(NoiseFloor); CenterBlendDegrees = S(CenterBlendDegrees); CenterSharpnessDegrees = S(CenterSharpnessDegrees); CoreForceMultiplier = S(CoreForceMultiplier); SteerVelocityReference = S(SteerVelocityReference); VelocityDeadzone = S(VelocityDeadzone); LowSpeedSmoothKmh = S(LowSpeedSmoothKmh); }
}

public sealed class LfeConfig
{
    public bool Enabled { get; set; } = false;
    public float Gain { get; set; } = 0.5f;
    public float Frequency { get; set; } = 10.0f;
    public float SuspensionDrive { get; set; } = 0.6f;
    public float SpeedScaling { get; set; } = 0.5f;
    public float RpmDrive { get; set; } = 0.3f;

    private static float S(float v) => float.IsNaN(v) ? 0f : float.IsPositiveInfinity(v) ? float.MaxValue : float.IsNegativeInfinity(v) ? float.MinValue : v;
    public void SanitizeFloats() { Gain = S(Gain); Frequency = S(Frequency); SuspensionDrive = S(SuspensionDrive); SpeedScaling = S(SpeedScaling); RpmDrive = S(RpmDrive); }
}

public sealed class EqConfig
{
    public bool Enabled { get; set; } = false;

    private readonly float[] _bandGains = new float[FfbEqualizer.BandInfo.Length];

    public float[] BandGains
    {
        get => _bandGains;
        set
        {
            for (int i = 0; i < Math.Min(value.Length, _bandGains.Length); i++)
                _bandGains[i] = value[i];
        }
    }

    public float GetGain(int band) =>
        band >= 0 && band < _bandGains.Length ? _bandGains[band] : 0f;

    public void SetGain(int band, float gain)
    {
        if (band >= 0 && band < _bandGains.Length)
            _bandGains[band] = gain;
    }

    private static float S(float v) => float.IsNaN(v) ? 0f : float.IsPositiveInfinity(v) ? float.MaxValue : float.IsNegativeInfinity(v) ? float.MinValue : v;

    public void SanitizeFloats()
    {
        for (int i = 0; i < _bandGains.Length; i++)
            _bandGains[i] = S(_bandGains[i]);
    }
}

public sealed class TyreFlexConfig
{
    public float FlexGain { get; set; } = 0.12f;
    public float CarcassStiffness { get; set; } = 1.0f;
    public float FlexSmoothing { get; set; } = 0.70f;
    public float ContactPatchWeight { get; set; } = 0.5f;
    public float LoadFlexGain { get; set; } = 0.3f;
    public float ContactPatchSensitivity { get; set; } = 1.0f;

    private static float S(float v) => float.IsNaN(v) ? 0f : float.IsPositiveInfinity(v) ? float.MaxValue : float.IsNegativeInfinity(v) ? float.MinValue : v;
    public void SanitizeFloats() { FlexGain = S(FlexGain); CarcassStiffness = S(CarcassStiffness); FlexSmoothing = S(FlexSmoothing); ContactPatchWeight = S(ContactPatchWeight); LoadFlexGain = S(LoadFlexGain); ContactPatchSensitivity = S(ContactPatchSensitivity); }
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum FfbMixModeDto
{
    Replace,
    Overlay
}

public sealed class LedEffectConfigDto
{
    public int Brightness { get; set; } = 100;
    public int FlashRateTicks { get; set; } = 16;
    public bool AbsFlashEnabled { get; set; } = true;
    public bool FlagIndicatorsEnabled { get; set; } = true;
    public bool ShiftLimiterFlashEnabled { get; set; } = true;

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public LedColorScheme ColorScheme { get; set; } = LedColorScheme.TrafficLight;

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public LedRpmPreset RpmPreset { get; set; } = LedRpmPreset.Default;

    public int[] RpmThresholds { get; set; } = LedEffectConfig.BuildDefaultRpmThresholds();
    public string[] CustomColors { get; set; } = LedEffectConfig.BuildTrafficLightColors();

    public LedEffectConfig ToConfig()
    {
        return new LedEffectConfig
        {
            Brightness = Brightness,
            FlashRateTicks = FlashRateTicks,
            AbsFlashEnabled = AbsFlashEnabled,
            FlagIndicatorsEnabled = FlagIndicatorsEnabled,
            ShiftLimiterFlashEnabled = ShiftLimiterFlashEnabled,
            ColorScheme = ColorScheme,
            RpmPreset = RpmPreset,
            RpmThresholds = RpmThresholds,
            CustomColors = CustomColors
        };
    }

    public static LedEffectConfigDto FromConfig(LedEffectConfig config)
    {
        return new LedEffectConfigDto
        {
            Brightness = config.Brightness,
            FlashRateTicks = config.FlashRateTicks,
            AbsFlashEnabled = config.AbsFlashEnabled,
            FlagIndicatorsEnabled = config.FlagIndicatorsEnabled,
            ShiftLimiterFlashEnabled = config.ShiftLimiterFlashEnabled,
            ColorScheme = config.ColorScheme,
            RpmPreset = config.RpmPreset,
            RpmThresholds = config.RpmThresholds,
            CustomColors = config.CustomColors
        };
    }
}

public sealed class Hf8Config
{
    public bool Enabled { get; set; } = false;
    public float MasterGain { get; set; } = 0.7f;
    public int OutputRateHz { get; set; } = 75;

    public float[] ZoneGains { get; set; } = new float[8]
    {
        0.8f, 0.8f, 0.8f, 0.8f, 0.6f, 0.6f, 0.5f, 0.7f
    };

    public bool[] ZoneEnabled { get; set; } = new bool[8]
    {
        true, true, true, true, true, true, true, true
    };

    public float[][] ZoneSourceWeights { get; set; } = CreateDefaultWeights();

    public static float[][] CreateDefaultWeights()
    {
        var defaults = Hf8SignalMapper.CreateDefaultSourceWeights();
        var jagged = new float[8][];
        for (int z = 0; z < 8; z++)
        {
            jagged[z] = new float[5];
            for (int s = 0; s < 5; s++)
                jagged[z][s] = defaults[z, s];
        }
        return jagged;
    }

    public float GetZoneGain(int zone) =>
        zone >= 0 && zone < ZoneGains.Length ? ZoneGains[zone] : 0f;

    public void SetZoneGain(int zone, float gain)
    {
        if (zone >= 0 && zone < ZoneGains.Length)
            ZoneGains[zone] = gain;
    }

    public bool GetZoneEnabled(int zone) =>
        zone >= 0 && zone < ZoneEnabled.Length && ZoneEnabled[zone];

    public void SetZoneEnabled(int zone, bool enabled)
    {
        if (zone >= 0 && zone < ZoneEnabled.Length)
            ZoneEnabled[zone] = enabled;
    }

    public float GetSourceWeight(int zone, int source)
    {
        if (zone >= 0 && zone < ZoneSourceWeights.Length && ZoneSourceWeights[zone] != null
            && source >= 0 && source < ZoneSourceWeights[zone].Length)
            return ZoneSourceWeights[zone][source];
        return 0f;
    }

    public void SetSourceWeight(int zone, int source, float weight)
    {
        if (zone >= 0 && zone < ZoneSourceWeights.Length && ZoneSourceWeights[zone] != null
            && source >= 0 && source < ZoneSourceWeights[zone].Length)
            ZoneSourceWeights[zone][source] = weight;
    }

    private static float S(float v) => float.IsNaN(v) ? 0f : float.IsPositiveInfinity(v) ? float.MaxValue : float.IsNegativeInfinity(v) ? float.MinValue : v;
    public void SanitizeFloats()
    {
        MasterGain = S(MasterGain);
        if (ZoneGains != null)
            for (int i = 0; i < ZoneGains.Length; i++)
                ZoneGains[i] = S(ZoneGains[i]);
        if (ZoneSourceWeights != null)
            for (int z = 0; z < ZoneSourceWeights.Length; z++)
                if (ZoneSourceWeights[z] != null)
                    for (int s = 0; s < ZoneSourceWeights[z].Length; s++)
                        ZoneSourceWeights[z][s] = S(ZoneSourceWeights[z][s]);
    }
}

public sealed class GripGuardConfig
{
    public bool Enabled { get; set; } = true;
    public float PeakSlipAngle { get; set; } = 0.10f;
    public float AttenuationStrength { get; set; } = 1.0f;
    public float MechanicalTrailGain { get; set; } = 0.015f;
    public float MinSpeedKmh { get; set; } = 10.0f;

    private static float S(float v) => float.IsNaN(v) ? 0f : float.IsPositiveInfinity(v) ? float.MaxValue : float.IsNegativeInfinity(v) ? float.MinValue : v;
    public void SanitizeFloats() { PeakSlipAngle = S(PeakSlipAngle); AttenuationStrength = S(AttenuationStrength); MechanicalTrailGain = S(MechanicalTrailGain); MinSpeedKmh = S(MinSpeedKmh); }
}

public sealed class StaticFrictionConfig
{
    /// <summary>Master gain. 0 = disabled, 1 = full stationary friction.</summary>
    public float Gain { get; set; } = 1.0f;

    /// <summary>Max elastic stretch before breakout (normalized steer units). Smaller = sharper, larger = more rubbery.</summary>
    public float MaxElasticStretch { get; set; } = 0.01f;

    /// <summary>Spring stiffness during elastic phase. Controls how quickly force ramps up.</summary>
    public float SpringStiffness { get; set; } = 15.0f;

    /// <summary>Kinetic (sliding) friction force level (normalized).</summary>
    public float KineticFrictionBase { get; set; } = 0.20f;

    /// <summary>Engine-off viscous damping. Opposes velocity to prevent buzz.</summary>
    public float EngineOffDamping { get; set; } = 0.15f;

    /// <summary>Engine-on viscous damping (lower — power assist).</summary>
    public float EngineOnDamping { get; set; } = 0.02f;

    /// <summary>Force multiplier when engine is off.</summary>
    public float EngineOffScale { get; set; } = 1.0f;

    /// <summary>Force multiplier when engine is running.</summary>
    public float EngineOnScale { get; set; } = 0.3f;

    /// <summary>Per-frame displacement decay during active turning. 0.92 = 8%/frame — stable yet natural.</summary>
    public float ActiveDecay { get; set; } = 0.92f;

    /// <summary>Per-frame displacement decay when spring returns wheel to center. 0.65 = 35%/frame — kills oscillation fast.</summary>
    public float ReturnDecay { get; set; } = 0.65f;

    /// <summary>Output EMA alpha for smoothing quantization artifacts. 0.35 = responsive but smooth. 1.0 = no smoothing.</summary>
    public float OutputSmoothAlpha { get; set; } = 0.35f;

    private static float S(float v) => float.IsNaN(v) ? 0f : float.IsPositiveInfinity(v) ? float.MaxValue : float.IsNegativeInfinity(v) ? float.MinValue : v;
    public void SanitizeFloats() { Gain = S(Gain); MaxElasticStretch = S(MaxElasticStretch); SpringStiffness = S(SpringStiffness); KineticFrictionBase = S(KineticFrictionBase); EngineOffDamping = S(EngineOffDamping); EngineOnDamping = S(EngineOnDamping); EngineOffScale = S(EngineOffScale); EngineOnScale = S(EngineOnScale); ActiveDecay = S(ActiveDecay); ReturnDecay = S(ReturnDecay); OutputSmoothAlpha = S(OutputSmoothAlpha); }
}

public sealed class CrashConfig
{
    public bool Enabled { get; set; } = true;

    public float ImpactGain { get; set; } = 0.60f;

    public float SafetyClamp { get; set; } = 0.50f;

    public float DecayRate { get; set; } = 0.88f;

    public float TriggerThresholdG { get; set; } = 3.0f;

    public float MinSpeedKmh { get; set; } = 5.0f;

    public bool SafetyOverride { get; set; } = false;

    private static float S(float v) => float.IsNaN(v) ? 0f : float.IsPositiveInfinity(v) ? float.MaxValue : float.IsNegativeInfinity(v) ? float.MinValue : v;
    public void SanitizeFloats() { ImpactGain = S(ImpactGain); SafetyClamp = S(SafetyClamp); DecayRate = S(DecayRate); TriggerThresholdG = S(TriggerThresholdG); MinSpeedKmh = S(MinSpeedKmh); }
}

public sealed class TyreConditionConfig
{
    public bool Enabled { get; set; } = true;

    public float BlowoutVibrationGain { get; set; } = 0.40f;

    public float PressureLossGain { get; set; } = 0.20f;

    public float DamageAsymmetryGain { get; set; } = 0.15f;

    public float BlowoutPressureThreshold { get; set; } = 0.40f;

    public float MaxBlowoutAmplitude { get; set; } = 0.25f;

    private static float S(float v) => float.IsNaN(v) ? 0f : float.IsPositiveInfinity(v) ? float.MaxValue : float.IsNegativeInfinity(v) ? float.MinValue : v;
    public void SanitizeFloats() { BlowoutVibrationGain = S(BlowoutVibrationGain); PressureLossGain = S(PressureLossGain); DamageAsymmetryGain = S(DamageAsymmetryGain); BlowoutPressureThreshold = S(BlowoutPressureThreshold); MaxBlowoutAmplitude = S(MaxBlowoutAmplitude); }
}

public sealed class WetWeatherConfig
{
    public bool Enabled { get; set; } = false;

    public bool AutoDetect { get; set; } = true;

    public float ManualIntensity { get; set; } = 1.0f;

    public float RoadVibSuppression { get; set; } = 0.70f;

    public float CurbSuppression { get; set; } = 0.40f;

    public float ScrubSuppression { get; set; } = 0.25f;

    public float PeakSlipAngleMultiplier { get; set; } = 1.60f;

    public float DampingReduction { get; set; } = 0.30f;

    public float NoiseFloorSuppression { get; set; } = 0.50f;

    public bool HydroplaningEnabled { get; set; } = true;

    public float HydroplaningSpeedThreshold { get; set; } = 120f;

    public float HydroplaningMaxAttenuation { get; set; } = 0.30f;

    private static float S(float v) => float.IsNaN(v) ? 0f : float.IsPositiveInfinity(v) ? float.MaxValue : float.IsNegativeInfinity(v) ? float.MinValue : v;
    public void SanitizeFloats() { ManualIntensity = S(ManualIntensity); RoadVibSuppression = S(RoadVibSuppression); CurbSuppression = S(CurbSuppression); ScrubSuppression = S(ScrubSuppression); PeakSlipAngleMultiplier = S(PeakSlipAngleMultiplier); DampingReduction = S(DampingReduction); NoiseFloorSuppression = S(NoiseFloorSuppression); HydroplaningSpeedThreshold = S(HydroplaningSpeedThreshold); HydroplaningMaxAttenuation = S(HydroplaningMaxAttenuation); }
}
