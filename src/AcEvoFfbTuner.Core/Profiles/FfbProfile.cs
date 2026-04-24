using System.Text.Json.Serialization;
using AcEvoFfbTuner.Core.DirectInput;
using AcEvoFfbTuner.Core.FfbProcessing;

namespace AcEvoFfbTuner.Core.Profiles;

public sealed class FfbProfile
{
    public const int CurrentVersion = 5;

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

    public static string[] AllDefaultNames => new[] { "Default", "Heavy", "Light", "Moza R5 - Final Stable Baseline" };

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

        Version = CurrentVersion;
    }
}

public sealed class ChannelConfig
{
    public float Gain { get; set; } = 1.0f;
    public bool Enabled { get; set; } = true;
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
}

public sealed class SlipConfig
{
    public float SlipRatioGain { get; set; } = 0.0f;
    public float SlipAngleGain { get; set; } = 0.0f;
    public float SlipThreshold { get; set; } = 0.05f;
    public bool UseFrontOnly { get; set; } = true;
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
}

public sealed class AutoGainConfig
{
    public bool Enabled { get; set; } = false;
    public float Scale { get; set; } = 1.0f;
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
