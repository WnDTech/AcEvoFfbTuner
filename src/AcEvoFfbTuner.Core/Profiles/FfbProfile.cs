using System.Text.Json.Serialization;
using AcEvoFfbTuner.Core.DirectInput;
using AcEvoFfbTuner.Core.FfbProcessing;

namespace AcEvoFfbTuner.Core.Profiles;

public sealed class FfbProfile
{
    public const int CurrentVersion = 8;

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

    public float MzScale { get; set; } = 25f;
    public float FxScale { get; set; } = 5000f;
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
        pipeline.Damping.LowSpeedDampingBoost = Damping.LowSpeedDampingBoost;
        pipeline.Damping.LowSpeedThreshold = Damping.LowSpeedThreshold;

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
            pipeline.Equalizer.SetBandEnabled(i, Equalizer.GetEnabled(i));
        }
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
        MzScale = pipeline.ChannelMixer.MzScale;
        FxScale = pipeline.ChannelMixer.FxScale;
        FyScale = pipeline.ChannelMixer.FyScale;
        LutCurve = new LutCurveDto { OutputValues = pipeline.LutCurve.OutputValues };

        Damping = new DampingConfig
        {
            SpeedDamping = pipeline.Damping.SpeedDampingCoefficient,
            Friction = pipeline.Damping.FrictionLevel,
            Inertia = pipeline.Damping.InertiaWeight,
            MaxSpeedReference = pipeline.Damping.MaxSpeedReference,
            LowSpeedDampingBoost = pipeline.Damping.LowSpeedDampingBoost,
            LowSpeedThreshold = pipeline.Damping.LowSpeedThreshold
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
            Equalizer.SetEnabled(i, pipeline.Equalizer.GetBandEnabled(i));
        }
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
                Damping = new DampingConfig { SpeedDamping = 0.2f, Friction = 0.1f, Inertia = 0.15f }
            },
            "Light" => new FfbProfile
            {
                Name = "Light",
                OutputGain = 0.8f,
                NormalizationScale = 1.0f,
                Damping = new DampingConfig { SpeedDamping = 0.05f, Friction = 0.02f, Inertia = 0.05f }
            },
            "Moza R5 - Final Stable Baseline" => new FfbProfile
            {
                Name = "Moza R5 - Final Stable Baseline",
                OutputGain = 0.8f,
                NormalizationScale = 600f,
                ForceScale = 1.0f,
                SoftClipThreshold = 0.8f,
                CompressionPower = 1.5f,
                SignCorrectionEnabled = true,
                MzFront = new ChannelConfig { Gain = 1.0f, Enabled = true },
                FxFront = new ChannelConfig { Gain = 0.15f, Enabled = true },
                FyFront = new ChannelConfig { Gain = 0.2f, Enabled = true },
                MzRear = new ChannelConfig { Gain = 0.0f, Enabled = false },
                FxRear = new ChannelConfig { Gain = 0.0f, Enabled = false },
                FyRear = new ChannelConfig { Gain = 0.0f, Enabled = false },
                FinalFf = new ChannelConfig { Gain = 0.0f, Enabled = false },
                WheelLoadWeighting = 0.0f,
                MzScale = 150f,
                FxScale = 4000f,
                FyScale = 4000f,
                LutCurve = LutCurveDto.Linear(),
                SteeringLockDegrees = 900,
                Damping = new DampingConfig { SpeedDamping = 1.0f, Friction = 0.5f, Inertia = 0.2f, MaxSpeedReference = 200f },
                Slip = new SlipConfig { SlipRatioGain = 0.1f, SlipAngleGain = 0.2f, SlipThreshold = 0.05f, UseFrontOnly = true },
                Dynamic = new DynamicConfig { LateralGGain = 0f, LongitudinalGGain = 0f, SuspensionGain = 0.4f, YawRateGain = 0f },                AutoGain = new AutoGainConfig { Enabled = false, Scale = 1.0f },
                Vibrations = new VibrationConfig { KerbGain = 1.0f, SlipGain = 0.8f, RoadGain = 0.5f, AbsGain = 1.0f, MasterGain = 0.5f }
            },
            "Safe - Logitech G29/G920" => CreateSafeWheelbaseProfile("Safe - Logitech G29/G920",
                maxTorqueNm: 2.5f, outputGain: 0.7f, normalizationScale: 350f,
                speedDamping: 0.4f, friction: 0.3f, inertia: 0.1f,
                vibrationMaster: 0.4f),
            "Safe - Thrustmaster T300/TX" => CreateSafeWheelbaseProfile("Safe - Thrustmaster T300/TX",
                maxTorqueNm: 4.5f, outputGain: 0.7f, normalizationScale: 500f,
                speedDamping: 0.8f, friction: 0.4f, inertia: 0.15f,
                vibrationMaster: 0.5f),
            "Safe - Fanatec CSL DD 5Nm" => CreateSafeWheelbaseProfile("Safe - Fanatec CSL DD 5Nm",
                maxTorqueNm: 5.0f, outputGain: 0.7f, normalizationScale: 550f,
                speedDamping: 0.8f, friction: 0.4f, inertia: 0.15f,
                vibrationMaster: 0.4f),
            "Safe - Fanatec CSL DD 8Nm" => CreateSafeWheelbaseProfile("Safe - Fanatec CSL DD 8Nm",
                maxTorqueNm: 8.0f, outputGain: 0.55f, normalizationScale: 600f,
                speedDamping: 0.9f, friction: 0.45f, inertia: 0.18f,
                vibrationMaster: 0.35f),
            "Safe - Moza R9" => CreateSafeWheelbaseProfile("Safe - Moza R9",
                maxTorqueNm: 9.0f, outputGain: 0.5f, normalizationScale: 650f,
                speedDamping: 1.0f, friction: 0.5f, inertia: 0.2f,
                vibrationMaster: 0.3f),
            "Safe - Fanatec ClubSport DD" => CreateSafeWheelbaseProfile("Safe - Fanatec ClubSport DD",
                maxTorqueNm: 15.0f, outputGain: 0.4f, normalizationScale: 700f,
                speedDamping: 1.0f, friction: 0.5f, inertia: 0.25f,
                vibrationMaster: 0.25f),
            "Safe - Simagic Alpha" => new FfbProfile
            {
                Name = "Safe - Simagic Alpha",
                OutputGain = 0.75f,
                NormalizationScale = 700f,
                ForceScale = 1.0f,
                SoftClipThreshold = 0.75f,
                CompressionPower = 1.5f,
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
                MzScale = 100f,
                FxScale = 4000f,
                FyScale = 4000f,
                LutCurve = LutCurveDto.Linear(),
                SteeringLockDegrees = 900,
                Damping = new DampingConfig
                {
                    SpeedDamping = 1.0f, Friction = 0.5f, Inertia = 0.25f,
                    MaxSpeedReference = 200f, LowSpeedDampingBoost = 3.0f, LowSpeedThreshold = 20f
                },
                Slip = new SlipConfig { SlipRatioGain = 0.08f, SlipAngleGain = 0.15f, SlipThreshold = 0.05f, UseFrontOnly = true },
                Dynamic = new DynamicConfig { LateralGGain = 0f, LongitudinalGGain = 0f, SuspensionGain = 0.3f, YawRateGain = 0f },
                AutoGain = new AutoGainConfig { Enabled = false, Scale = 1.0f },
                Vibrations = new VibrationConfig { KerbGain = 0.8f, SlipGain = 0.6f, RoadGain = 0.4f, AbsGain = 0.8f, MasterGain = 0.25f }
            },
            "Safe - Simucube 2 Pro" => CreateSafeWheelbaseProfile("Safe - Simucube 2 Pro",
                maxTorqueNm: 25.0f, outputGain: 0.3f, normalizationScale: 800f,
                speedDamping: 1.0f, friction: 0.5f, inertia: 0.3f,
                vibrationMaster: 0.2f),
            _ => new FfbProfile
            {
                Name = name,
                OutputGain = 0.8f,
                NormalizationScale = 250f,
                MzScale = 150f,
                FxScale = 4000f,
                FyScale = 4000f,
                CompressionPower = 1.5f,
                Damping = new DampingConfig { SpeedDamping = 1.0f, Friction = 0.5f, Inertia = 0.2f, MaxSpeedReference = 200f },
                FxFront = new ChannelConfig { Gain = 0.15f, Enabled = true },
                FyFront = new ChannelConfig { Gain = 0.2f, Enabled = true },
                Dynamic = new DynamicConfig { SuspensionGain = 0.4f }
            }
        };
    }

    private static FfbProfile CreateSafeWheelbaseProfile(
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
            CompressionPower = 1.5f,
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
            MzScale = 150f,
            FxScale = 4000f,
            FyScale = 4000f,
            LutCurve = LutCurveDto.Linear(),
            SteeringLockDegrees = 900,
            Damping = new DampingConfig
            {
                SpeedDamping = speedDamping,
                Friction = friction,
                Inertia = inertia,
                MaxSpeedReference = 200f,
                LowSpeedDampingBoost = 3.0f,
                LowSpeedThreshold = 20f
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
        "Safe - Logitech G29/G920",
        "Safe - Thrustmaster T300/TX",
        "Safe - Fanatec CSL DD 5Nm",
        "Safe - Fanatec CSL DD 8Nm",
        "Safe - Moza R9",
        "Safe - Fanatec ClubSport DD",
        "Safe - Simagic Alpha",
        "Safe - Simucube 2 Pro"
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
                MzScale = 150f;
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
                MzScale = 150f;
                FxScale = 4000f;
                FyScale = 4000f;
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
                Equalizer.SetEnabled(0, oldEq.GetEnabled(0));
                Equalizer.SetEnabled(1, oldEq.GetEnabled(1));
                Equalizer.SetEnabled(2, oldEq.GetEnabled(2));
                Equalizer.SetEnabled(3, oldEq.GetEnabled(3));
                Equalizer.SetEnabled(4, oldEq.GetEnabled(4));
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

    /// <summary>
    /// Low-speed damping boost multiplier. At 0 km/h, damping is this many times stronger.
    /// Prevents wheel "hunting" at low/zero speed.
    /// </summary>
    public float LowSpeedDampingBoost { get; set; } = 3.0f;

    /// <summary>
    /// Speed (km/h) below which the low-speed damping boost fades in.
    /// </summary>
    public float LowSpeedThreshold { get; set; } = 20f;

    private static float S(float v) => float.IsNaN(v) ? 0f : float.IsPositiveInfinity(v) ? float.MaxValue : float.IsNegativeInfinity(v) ? float.MinValue : v;
    public void SanitizeFloats() { SpeedDamping = S(SpeedDamping); Friction = S(Friction); Inertia = S(Inertia); MaxSpeedReference = S(MaxSpeedReference); LowSpeedDampingBoost = S(LowSpeedDampingBoost); LowSpeedThreshold = S(LowSpeedThreshold); }
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

    private static float S(float v) => float.IsNaN(v) ? 0f : float.IsPositiveInfinity(v) ? float.MaxValue : float.IsNegativeInfinity(v) ? float.MinValue : v;
    public void SanitizeFloats() { KerbGain = S(KerbGain); SlipGain = S(SlipGain); RoadGain = S(RoadGain); AbsGain = S(AbsGain); MasterGain = S(MasterGain); SuspensionRoadGain = S(SuspensionRoadGain); }
}

public sealed class AdvancedConfig
{
    public float MaxSlewRate { get; set; } = 0.20f;
    public float CenterSuppressionDegrees { get; set; } = 6.0f;
    public float CenterKneePower { get; set; } = 1.0f;
    public float HysteresisThreshold { get; set; } = 0.015f;
    public float NoiseFloor { get; set; } = 0.005f;
    public int HysteresisWatchdogFrames { get; set; } = 5;
    public float CenterBlendDegrees { get; set; } = 1.0f;
    public float SteerVelocityReference { get; set; } = 10.0f;
    public float VelocityDeadzone { get; set; } = 0.05f;
    public float LowSpeedSmoothKmh { get; set; } = 25.0f;

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
    private readonly bool[] _bandEnabled = new bool[FfbEqualizer.BandInfo.Length];

    public float[] BandGains
    {
        get => _bandGains;
        set
        {
            for (int i = 0; i < Math.Min(value.Length, _bandGains.Length); i++)
                _bandGains[i] = value[i];
        }
    }

    public bool[] BandEnabled
    {
        get => _bandEnabled;
        set
        {
            for (int i = 0; i < Math.Min(value.Length, _bandEnabled.Length); i++)
                _bandEnabled[i] = value[i];
        }
    }

    public float GetGain(int band) =>
        band >= 0 && band < _bandGains.Length ? _bandGains[band] : 0f;

    public void SetGain(int band, float gain)
    {
        if (band >= 0 && band < _bandGains.Length)
            _bandGains[band] = gain;
    }

    public bool GetEnabled(int band) =>
        band >= 0 && band < _bandEnabled.Length && _bandEnabled[band];

    public void SetEnabled(int band, bool enabled)
    {
        if (band >= 0 && band < _bandEnabled.Length)
            _bandEnabled[band] = enabled;
    }

    private static float S(float v) => float.IsNaN(v) ? 0f : float.IsPositiveInfinity(v) ? float.MaxValue : float.IsNegativeInfinity(v) ? float.MinValue : v;

    public void SanitizeFloats()
    {
        for (int i = 0; i < _bandGains.Length; i++)
            _bandGains[i] = S(_bandGains[i]);
    }
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
