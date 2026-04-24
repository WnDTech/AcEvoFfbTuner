namespace AcEvoFfbTuner.Core.DirectInput;

public sealed class LedEffectConfig
{
    public const int MaxLedCount = 10;

    public int Brightness { get; set; } = 100;

    public int FlashRateTicks { get; set; } = 16;

    public bool AbsFlashEnabled { get; set; } = true;

    public bool FlagIndicatorsEnabled { get; set; } = true;

    public bool ShiftLimiterFlashEnabled { get; set; } = true;

    public LedColorScheme ColorScheme { get; set; } = LedColorScheme.TrafficLight;

    public LedRpmPreset RpmPreset { get; set; } = LedRpmPreset.Default;

    public int[] RpmThresholds { get; set; } = BuildDefaultRpmThresholds();

    public string[] CustomColors { get; set; } = BuildTrafficLightColors();

    public static int[] BuildDefaultRpmThresholds() =>
        new int[] { 50, 60, 70, 80, 85, 90, 93, 96, 98, 100 };

    public static int[] BuildEarlyRpmThresholds() =>
        new int[] { 30, 40, 50, 60, 68, 76, 82, 88, 94, 100 };

    public static int[] BuildLateRpmThresholds() =>
        new int[] { 70, 75, 80, 85, 89, 92, 95, 97, 99, 100 };

    public static int[] BuildLinearRpmThresholds()
    {
        var t = new int[MaxLedCount];
        for (int i = 0; i < MaxLedCount; i++)
            t[i] = (int)(10 + 90.0 * (i + 1) / MaxLedCount);
        return t;
    }

    public static string[] BuildTrafficLightColors() => new string[]
    {
        "#FF00CE00", "#FF00CE00", "#FF00CE00",
        "#FFFFCC00", "#FFFFCC00", "#FFFFCC00",
        "#FFFF6600", "#FFFF6600",
        "#FFFF0606", "#FFFF0606"
    };

    public static string[] BuildBlueGradientColors() => new string[]
    {
        "#FF0055FF", "#FF0055FF", "#FF0055FF",
        "#FF00AAFF", "#FF00AAFF", "#FF00AAFF",
        "#FF00FFFF", "#FF00FFFF",
        "#FFFFFFFF", "#FFFFFFFF"
    };

    public static string[] BuildRedHotColors() => new string[]
    {
        "#FFFF6600", "#FFFF6600", "#FFFF6600",
        "#FFFF3300", "#FFFF3300", "#FFFF3300",
        "#FFFF0000", "#FFFF0000",
        "#FFFF0000", "#FFFF0000"
    };

    public static string[] BuildMonochromeColors() => new string[]
    {
        "#FF333333", "#FF333333", "#FF555555",
        "#FF777777", "#FF777777", "#FF999999",
        "#FFBBBBBB", "#FFBBBBBB",
        "#FFFFFFFF", "#FFFFFFFF"
    };

    public int[] GetEffectiveRpmThresholds()
    {
        return RpmPreset switch
        {
            LedRpmPreset.Default => BuildDefaultRpmThresholds(),
            LedRpmPreset.Early => BuildEarlyRpmThresholds(),
            LedRpmPreset.Late => BuildLateRpmThresholds(),
            LedRpmPreset.Linear => BuildLinearRpmThresholds(),
            _ => RpmThresholds
        };
    }

    public string[] GetEffectiveColors()
    {
        return ColorScheme switch
        {
            LedColorScheme.TrafficLight => BuildTrafficLightColors(),
            LedColorScheme.BlueGradient => BuildBlueGradientColors(),
            LedColorScheme.RedHot => BuildRedHotColors(),
            LedColorScheme.Monochrome => BuildMonochromeColors(),
            _ => CustomColors
        };
    }

    public LedEffectConfig Clone()
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
            RpmThresholds = (int[])RpmThresholds.Clone(),
            CustomColors = (string[])CustomColors.Clone()
        };
    }
}

public enum LedColorScheme
{
    TrafficLight,
    BlueGradient,
    RedHot,
    Monochrome,
    Custom
}

public enum LedRpmPreset
{
    Default,
    Early,
    Late,
    Linear,
    Custom
}
