using System.Text.Json.Serialization;
using AcEvoFfbTuner.Core.DirectInput;
using AcEvoFfbTuner.Core.FfbProcessing;

namespace AcEvoFfbTuner.Core.Profiles;

public sealed class FfbProfile
{
    public const int CurrentVersion = 12;

    public int Version { get; set; } = CurrentVersion;
    public string Name { get; set; } = "Default";
    public string CarMatch { get; set; } = "";

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

    public FfbMixModeDto MixMode { get; set; } = FfbMixModeDto.Replace;
    public float OutputGain { get; set; } = 1.0f;

    [JsonPropertyName("forceSensitivity")]
    public float NormalizationScale { get; set; } = 1.0f;

    public float ForceScale { get; set; } = 1.0f;
    public float SoftClipThreshold { get; set; } = 0.8f;
    public float CompressionPower { get; set; } = 1.0f;
    public bool SignCorrectionEnabled { get; set; } = true;
    public float WheelMaxTorqueNm { get; set; } = 5.5f;

    public ChannelConfig MzFront { get; set; } = new() { Gain = 1.0f, Enabled = true };
    public ChannelConfig FxFront { get; set; } = new() { Gain = 0.1f, Enabled = true };
    public ChannelConfig FyFront { get; set; } = new() { Gain = 0.1f, Enabled = true };
    public ChannelConfig MzRear { get; set; } = new() { Gain = 0.0f, Enabled = false };
    public ChannelConfig FxRear { get; set; } = new() { Gain = 0.0f, Enabled = false };
    public ChannelConfig FyRear { get; set; } = new() { Gain = 0.0f, Enabled = false };
    public ChannelConfig FinalFf { get; set; } = new() { Gain = 0.0f, Enabled = false };

    public float WheelLoadWeighting { get; set; } = 0.0f;

    public bool FyInverted { get; set; } = true;

    public float MzScale { get; set; } = 30f;
    public float FxScale { get; set; } = 500f;
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

        pipeline.Damping.SpeedDampingCoefficient = Damping.SpeedDamping;
        pipeline.Damping.FrictionLevel = Damping.Friction;
        pipeline.Damping.InertiaWeight = Damping.Inertia;
        pipeline.Damping.MaxSpeedReference = Damping.MaxSpeedReference;

        pipeline.SlipEnhancer.SlipRatioGain = Slip.SlipRatioGain;
        pipeline.SlipEnhancer.SlipAngleGain = Slip.SlipAngleGain;
        pipeline.SlipEnhancer.SlipThreshold = Slip.SlipThreshold;
        pipeline.SlipEnhancer.UseFrontOnly = Slip.UseFrontOnly;

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

        pipeline.MaxSlewRate = Advanced.MaxSlewRate;
        pipeline.CenterSuppressionDegrees = Advanced.CenterSuppressionDegrees;
        pipeline.CenterKneePower = Advanced.CenterKneePower;
        pipeline.HysteresisThreshold = Advanced.HysteresisThreshold;
        pipeline.NoiseFloor = Advanced.NoiseFloor;
        pipeline.HysteresisWatchdogFrames = Advanced.HysteresisWatchdogFrames;
        pipeline.ChannelMixer.CenterBlendDegrees = Advanced.CenterBlendDegrees;
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
            SpeedDamping = pipeline.Damping.SpeedDampingCoefficient,
            Friction = pipeline.Damping.FrictionLevel,
            Inertia = pipeline.Damping.InertiaWeight,
            MaxSpeedReference = pipeline.Damping.MaxSpeedReference
        };
        Slip = new SlipConfig
        {
            SlipRatioGain = pipeline.SlipEnhancer.SlipRatioGain,
            SlipAngleGain = pipeline.SlipEnhancer.SlipAngleGain,
            SlipThreshold = pipeline.SlipEnhancer.SlipThreshold,
            UseFrontOnly = pipeline.SlipEnhancer.UseFrontOnly
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
            SuspensionRoadGain = pipeline.VibrationMixer.SuspensionRoadGain
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
            LoadFlexGain = pipeline.TyreFlex.LoadFlexGain
        };
    }

    public static FfbProfile GetDefaultProfile(string name)
    {
        return name switch
        {
            "Heavy" => new FfbProfile
            {
                Name = "Heavy",
                OutputGain = 1.3f,
                NormalizationScale = 1.0f,
                Damping = new DampingConfig { SpeedDamping = 0.2f, Friction = 0.1f, Inertia = 0.15f, MaxSpeedReference = 200f }
            },
            "Light" => new FfbProfile
            {
                Name = "Light",
                OutputGain = 0.8f,
                NormalizationScale = 1.0f,
                Damping = new DampingConfig { SpeedDamping = 0.05f, Friction = 0.02f, Inertia = 0.05f, MaxSpeedReference = 200f }
            },
            "Moza R5 - Final Stable Baseline" => new FfbProfile
            {
                Name = "Moza R5 - Final Stable Baseline",
                OutputGain = 0.8f,
                NormalizationScale = 600f,
                ForceScale = 1.0f,
                SoftClipThreshold = 0.8f,
                CompressionPower = 1.0f,
                SignCorrectionEnabled = true,
                MzFront = new ChannelConfig { Gain = 1.0f, Enabled = true },
                FxFront = new ChannelConfig { Gain = 0.15f, Enabled = true },
                FyFront = new ChannelConfig { Gain = 0.2f, Enabled = true },
                MzRear = new ChannelConfig { Gain = 0.0f, Enabled = false },
                FxRear = new ChannelConfig { Gain = 0.0f, Enabled = false },
                FyRear = new ChannelConfig { Gain = 0.0f, Enabled = false },
                FinalFf = new ChannelConfig { Gain = 0.0f, Enabled = false },
                WheelLoadWeighting = 0.0f,
                MzScale = 30f,
                FxScale = 500f,
                FyScale = 5000f,
                LutCurve = LutCurveDto.Linear(),
                SteeringLockDegrees = 900,
                Damping = new DampingConfig { SpeedDamping = 0.5f, Friction = 0.15f, Inertia = 0.1f, MaxSpeedReference = 200f },
                Slip = new SlipConfig { SlipRatioGain = 0.1f, SlipAngleGain = 0.2f, SlipThreshold = 0.05f, UseFrontOnly = true },
                Dynamic = new DynamicConfig { LateralGGain = 0f, LongitudinalGGain = 0f, SuspensionGain = 0.4f, YawRateGain = 0f },                AutoGain = new AutoGainConfig { Enabled = false, Scale = 1.0f },
                Vibrations = new VibrationConfig { KerbGain = 1.0f, SlipGain = 0.8f, RoadGain = 0.5f, AbsGain = 1.0f, MasterGain = 0.5f }
            },
            "Default - Logitech G29/G920" => CreateDefaultWheelbaseProfile("Default - Logitech G29/G920",
                maxTorqueNm: 2.5f, outputGain: 0.7f, normalizationScale: 350f,
                speedDamping: 0.2f, friction: 0.10f, inertia: 0.1f,
                vibrationMaster: 0.4f),
            "Default - Thrustmaster T300/TX" => CreateDefaultWheelbaseProfile("Default - Thrustmaster T300/TX",
                maxTorqueNm: 4.5f, outputGain: 0.7f, normalizationScale: 500f,
                speedDamping: 0.4f, friction: 0.12f, inertia: 0.08f,
                vibrationMaster: 0.5f),
            "Default - Fanatec CSL DD 5Nm" => CreateDefaultWheelbaseProfile("Default - Fanatec CSL DD 5Nm",
                maxTorqueNm: 5.0f, outputGain: 0.7f, normalizationScale: 550f,
                speedDamping: 0.4f, friction: 0.12f, inertia: 0.08f,
                vibrationMaster: 0.4f),
            "Default - Fanatec CSL DD 8Nm" => CreateDefaultWheelbaseProfile("Default - Fanatec CSL DD 8Nm",
                maxTorqueNm: 8.0f, outputGain: 0.55f, normalizationScale: 600f,
                speedDamping: 0.45f, friction: 0.12f, inertia: 0.08f,
                vibrationMaster: 0.35f),
            "Default - Moza R9" => new FfbProfile
            {
                Name = "Default - Moza R9",
                OutputGain = 0.55f,
                NormalizationScale = 650f,
                ForceScale = 1.0f,
                SoftClipThreshold = 0.8f,
                CompressionPower = 1.0f,
                SignCorrectionEnabled = true,
                WheelMaxTorqueNm = 9.0f,
                MzFront = new ChannelConfig { Gain = 1.0f, Enabled = true },
                FxFront = new ChannelConfig { Gain = 0.12f, Enabled = true },
                FyFront = new ChannelConfig { Gain = 0.15f, Enabled = true },
                MzRear = new ChannelConfig { Gain = 0.0f, Enabled = false },
                FxRear = new ChannelConfig { Gain = 0.0f, Enabled = false },
                FyRear = new ChannelConfig { Gain = 0.0f, Enabled = false },
                FinalFf = new ChannelConfig { Gain = 0.0f, Enabled = false },
                WheelLoadWeighting = 0.0f,
                MzScale = 30f,
                FxScale = 500f,
                FyScale = 5000f,
                LutCurve = LutCurveDto.Linear(),
                SteeringLockDegrees = 900,
                Damping = new DampingConfig
                {
                    SpeedDamping = 0.4f,
                    Friction = 0.10f,
                    Inertia = 0.08f,
                    MaxSpeedReference = 200f
                },
                Slip = new SlipConfig { SlipRatioGain = 0.15f, SlipAngleGain = 0.15f, SlipThreshold = 0.05f, UseFrontOnly = true },
                Dynamic = new DynamicConfig { LateralGGain = 0.3f, LongitudinalGGain = 0.1f, SuspensionGain = 0.4f, YawRateGain = 0.15f },
                AutoGain = new AutoGainConfig { Enabled = false, Scale = 1.0f },
                Vibrations = new VibrationConfig { KerbGain = 0.8f, SlipGain = 0.6f, RoadGain = 0.4f, AbsGain = 0.8f, MasterGain = 0.3f }
            },
            "Default - Fanatec ClubSport DD" => CreateDefaultWheelbaseProfile("Default - Fanatec ClubSport DD",
                maxTorqueNm: 15.0f, outputGain: 0.4f, normalizationScale: 700f,
                speedDamping: 0.5f, friction: 0.15f, inertia: 0.12f,
                vibrationMaster: 0.25f),
            "Default - Simagic Alpha" => new FfbProfile
            {
                Name = "Default - Simagic Alpha",
                OutputGain = 0.75f,
                NormalizationScale = 700f,
                ForceScale = 1.0f,
                SoftClipThreshold = 0.75f,
                CompressionPower = 1.0f,
                SignCorrectionEnabled = true,
                WheelMaxTorqueNm = 15.0f,
                MzFront = new ChannelConfig { Gain = 1.0f, Enabled = true },
                FxFront = new ChannelConfig { Gain = 0.15f, Enabled = true },
                FyFront = new ChannelConfig { Gain = 0.3f, Enabled = true },
                MzRear = new ChannelConfig { Gain = 0.0f, Enabled = false },
                FxRear = new ChannelConfig { Gain = 0.0f, Enabled = false },
                FyRear = new ChannelConfig { Gain = 0.0f, Enabled = false },
                FinalFf = new ChannelConfig { Gain = 0.0f, Enabled = false },
                WheelLoadWeighting = 0.0f,
                MzScale = 30f,
                FxScale = 500f,
                FyScale = 5000f,
                LutCurve = LutCurveDto.Linear(),
                SteeringLockDegrees = 900,
                Damping = new DampingConfig
                {
                    SpeedDamping = 0.5f, Friction = 0.15f, Inertia = 0.12f,
                    MaxSpeedReference = 200f
                },
                Slip = new SlipConfig { SlipRatioGain = 0.08f, SlipAngleGain = 0.15f, SlipThreshold = 0.05f, UseFrontOnly = true },
                Dynamic = new DynamicConfig { LateralGGain = 0f, LongitudinalGGain = 0f, SuspensionGain = 0.3f, YawRateGain = 0f },
                AutoGain = new AutoGainConfig { Enabled = false, Scale = 1.0f },
                Vibrations = new VibrationConfig { KerbGain = 0.8f, SlipGain = 0.6f, RoadGain = 0.4f, AbsGain = 0.8f, MasterGain = 0.25f }
            },
            "Default - Simucube 2 Pro" => CreateDefaultWheelbaseProfile("Default - Simucube 2 Pro",
                maxTorqueNm: 25.0f, outputGain: 0.3f, normalizationScale: 800f,
                speedDamping: 0.5f, friction: 0.15f, inertia: 0.15f,
                vibrationMaster: 0.2f),
            _ => new FfbProfile
            {
                Name = name,
                OutputGain = 0.8f,
                NormalizationScale = 250f,
                MzScale = 30f,
                FxScale = 500f,
                FyScale = 5000f,
                CompressionPower = 1.0f,
                Damping = new DampingConfig { SpeedDamping = 0.5f, Friction = 0.15f, Inertia = 0.1f, MaxSpeedReference = 200f },
                FxFront = new ChannelConfig { Gain = 0.15f, Enabled = true },
                FyFront = new ChannelConfig { Gain = 0.2f, Enabled = true },
                Dynamic = new DynamicConfig { SuspensionGain = 0.4f }
            }
        };
    }

    private static FfbProfile CreateDefaultWheelbaseProfile(
        string name, float maxTorqueNm, float outputGain, float normalizationScale,
        float speedDamping, float friction, float inertia, float vibrationMaster)
    {
        return new FfbProfile
        {
            Name = name,
            OutputGain = outputGain,
            NormalizationScale = normalizationScale,
            ForceScale = 1.0f,
            SoftClipThreshold = 0.75f,
            CompressionPower = 1.0f,
            SignCorrectionEnabled = true,
            WheelMaxTorqueNm = maxTorqueNm,
            MzFront = new ChannelConfig { Gain = 1.0f, Enabled = true },
            FxFront = new ChannelConfig { Gain = 0.12f, Enabled = true },
            FyFront = new ChannelConfig { Gain = 0.15f, Enabled = true },
            MzRear = new ChannelConfig { Gain = 0.0f, Enabled = false },
            FxRear = new ChannelConfig { Gain = 0.0f, Enabled = false },
            FyRear = new ChannelConfig { Gain = 0.0f, Enabled = false },
            FinalFf = new ChannelConfig { Gain = 0.0f, Enabled = false },
            WheelLoadWeighting = 0.0f,
            MzScale = 30f,
            FxScale = 500f,
            FyScale = 5000f,
            LutCurve = LutCurveDto.Linear(),
            SteeringLockDegrees = 900,
            Damping = new DampingConfig
            {
                SpeedDamping = speedDamping,
                Friction = friction,
                Inertia = inertia,
                MaxSpeedReference = 200f
            },
            Slip = new SlipConfig { SlipRatioGain = 0.08f, SlipAngleGain = 0.15f, SlipThreshold = 0.05f, UseFrontOnly = true },
            Dynamic = new DynamicConfig { LateralGGain = 0f, LongitudinalGGain = 0f, SuspensionGain = 0.3f, YawRateGain = 0f },
            AutoGain = new AutoGainConfig { Enabled = false, Scale = 1.0f },
            Vibrations = new VibrationConfig { KerbGain = 0.8f, SlipGain = 0.6f, RoadGain = 0.4f, AbsGain = 0.8f, MasterGain = vibrationMaster }
        };
    }

    public static string[] AllDefaultNames => new[]
    {
        "Default", "Heavy", "Light", "Moza R5 - Final Stable Baseline",
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
                    SpeedDamping = 1.0f,
                    Friction = 0.5f,
                    Inertia = 0.2f,
                    MaxSpeedReference = 200f
                };
            }
            else if (Name == "Default")
            {
                MzScale = 30f;
                FxScale = 500f;
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
            // Mz Realism: physics-preserving sign handling, Mz curve fidelity
            // Device inversion moved to FfbDeviceManager (_invertForce=false).
            // CenterSuppression reduced: physics Mz sign preserved, only narrow zero-crossing fade.
            // Mz alpha increased for better transient response.
            // Load sensitivity now sublinear (sqrt).
            Advanced ??= new AdvancedConfig();
            Advanced.CenterSuppressionDegrees = 0.5f;
            Advanced.CenterBlendDegrees = 0.5f;
            Advanced.LowSpeedSmoothKmh = 10.0f;
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
    public float SpeedDamping { get; set; } = 0.3f;
    public float Friction { get; set; } = 0.25f;
    public float Inertia { get; set; } = 0.1f;

    [JsonPropertyName("dampingSpeedReference")]
    public float MaxSpeedReference { get; set; } = 300f;

    [JsonIgnore]
    public float LowSpeedDampingBoost { get; set; } = 1.0f;

    [JsonIgnore]
    public float LowSpeedThreshold { get; set; } = 20f;

    private static float S(float v) => float.IsNaN(v) ? 0f : float.IsPositiveInfinity(v) ? float.MaxValue : float.IsNegativeInfinity(v) ? float.MinValue : v;
    public void SanitizeFloats() { SpeedDamping = S(SpeedDamping); Friction = S(Friction); Inertia = S(Inertia); MaxSpeedReference = S(MaxSpeedReference); }
}

public sealed class SlipConfig
{
    public float SlipRatioGain { get; set; } = 0.0f;
    public float SlipAngleGain { get; set; } = 0.0f;
    public float SlipThreshold { get; set; } = 0.05f;
    public bool UseFrontOnly { get; set; } = true;

    private static float S(float v) => float.IsNaN(v) ? 0f : float.IsPositiveInfinity(v) ? float.MaxValue : float.IsNegativeInfinity(v) ? float.MinValue : v;
    public void SanitizeFloats() { SlipRatioGain = S(SlipRatioGain); SlipAngleGain = S(SlipAngleGain); SlipThreshold = S(SlipThreshold); }
}

public sealed class DynamicConfig
{
    [JsonPropertyName("corneringForce")]
    public float LateralGGain { get; set; } = 0.0f;

    [JsonPropertyName("accelerationBrakingForce")]
    public float LongitudinalGGain { get; set; } = 0.0f;

    [JsonPropertyName("roadFeel")]
    public float SuspensionGain { get; set; } = 0.0f;

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
    public float RoadGain { get; set; } = 0.5f;
    public float AbsGain { get; set; } = 1.0f;
    public float MasterGain { get; set; } = 0.7f;
    public float SuspensionRoadGain { get; set; } = 1.5f;

    // ── Tire scrub / limit-feel vibration ──
    public float ScrubGain { get; set; } = 0.50f;
    public float RearSlipGain { get; set; } = 0.60f;

    private static float S(float v) => float.IsNaN(v) ? 0f : float.IsPositiveInfinity(v) ? float.MaxValue : float.IsNegativeInfinity(v) ? float.MinValue : v;
    public void SanitizeFloats() { KerbGain = S(KerbGain); SlipGain = S(SlipGain); RoadGain = S(RoadGain); AbsGain = S(AbsGain); MasterGain = S(MasterGain); SuspensionRoadGain = S(SuspensionRoadGain);  ScrubGain = S(ScrubGain); RearSlipGain = S(RearSlipGain); }
}

public sealed class AdvancedConfig
{
    public float MaxSlewRate { get; set; } = 0.40f;
    public float CenterSuppressionDegrees { get; set; } = 0.5f;
    public float CenterKneePower { get; set; } = 1.0f;
    public float HysteresisThreshold { get; set; } = 0f;
    public float NoiseFloor { get; set; } = 0.003f;
    public int HysteresisWatchdogFrames { get; set; } = 0;
    public float CenterBlendDegrees { get; set; } = 0.5f;
    public float SteerVelocityReference { get; set; } = 10.0f;
    public float VelocityDeadzone { get; set; } = 0.05f;
    public float LowSpeedSmoothKmh { get; set; } = 10.0f;

    private static float S(float v) => float.IsNaN(v) ? 0f : float.IsPositiveInfinity(v) ? float.MaxValue : float.IsNegativeInfinity(v) ? float.MinValue : v;
    public void SanitizeFloats() { MaxSlewRate = S(MaxSlewRate); CenterSuppressionDegrees = S(CenterSuppressionDegrees); CenterKneePower = S(CenterKneePower); HysteresisThreshold = S(HysteresisThreshold); NoiseFloor = S(NoiseFloor); CenterBlendDegrees = S(CenterBlendDegrees); SteerVelocityReference = S(SteerVelocityReference); VelocityDeadzone = S(VelocityDeadzone); LowSpeedSmoothKmh = S(LowSpeedSmoothKmh); }
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
    public float FlexGain { get; set; } = 0.0f;
    public float CarcassStiffness { get; set; } = 1.0f;
    public float FlexSmoothing { get; set; } = 0.70f;
    public float ContactPatchWeight { get; set; } = 0.5f;
    public float LoadFlexGain { get; set; } = 0.3f;

    private static float S(float v) => float.IsNaN(v) ? 0f : float.IsPositiveInfinity(v) ? float.MaxValue : float.IsNegativeInfinity(v) ? float.MinValue : v;
    public void SanitizeFloats() { FlexGain = S(FlexGain); CarcassStiffness = S(CarcassStiffness); FlexSmoothing = S(FlexSmoothing); ContactPatchWeight = S(ContactPatchWeight); LoadFlexGain = S(LoadFlexGain); }
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
