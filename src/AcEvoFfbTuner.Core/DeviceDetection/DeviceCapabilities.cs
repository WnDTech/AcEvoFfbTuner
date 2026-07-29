namespace AcEvoFfbTuner.Core.DeviceDetection;

public sealed record DeviceCapabilities
{
    public bool HasLeds { get; init; }
    public bool HasScreen { get; init; }
    public bool HasRumbleMotors { get; init; }
    public int LedCount { get; init; }
    public bool SupportsRgb { get; init; }
    public bool SupportsBrightnessControl { get; init; }
    public bool SupportsFlagIndicators { get; init; }
    public bool HasGearDisplay { get; init; }
    public bool SupportsFullForce { get; init; }

    public static readonly DeviceCapabilities Empty = new();
}
