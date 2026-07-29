namespace AcEvoFfbTuner.Core.Config;

public sealed class PedalConfig
{
    public int Version { get; set; } = 1;
    public bool Enabled { get; set; }
    public bool AutoDetectDevice { get; set; } = true;
    public PedalSourceType PreferredSource { get; set; } = PedalSourceType.Auto;
    public PedalAxisConfig Gas { get; set; } = new();
    public PedalAxisConfig Brake { get; set; } = new();
    public PedalAxisConfig Clutch { get; set; } = new()
    {
        Smoothing = 0.50f
    };
}

public sealed class PedalAxisConfig
{
    public float Deadzone { get; set; } = 0.02f;
    public float Min { get; set; }
    public float Max { get; set; } = 1.0f;
    public bool Invert { get; set; }
    public float Smoothing { get; set; } = 0.85f;
}

public enum PedalSourceType
{
    Auto,
    ScLink,
    Hid,
    DirectInput,
    Keyboard,
    Replay
}
