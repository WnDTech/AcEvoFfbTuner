namespace AcEvoFfbTuner.Core.Config;

public sealed class PedalConfig
{
    public int Version { get; set; } = 1;
    public bool Enabled { get; set; }
    public bool AutoDetectDevice { get; set; } = true;
    public PedalSourceType PreferredSource { get; set; } = PedalSourceType.Auto;
    /// <summary>Instance GUID of the manually picked separate-USB pedal
    /// device. Null/empty = Auto (first available). Wheelbase-routed pedals
    /// need no selection — they follow the connected wheel automatically.</summary>
    public string? PreferredDeviceInstanceId { get; set; }
    /// <summary>Axis assignment (was memory-only before — Find-my-pedals and
    /// manual combo choices were lost every restart). Defaults match every
    /// source's own defaults, so old files behave identically.</summary>
    public string GasAxis { get; set; } = "Y";
    public string BrakeAxis { get; set; } = "Z";
    public string ClutchAxis { get; set; } = "Rx";

    /// <summary>Pedal haptic effect gains (Tune tab), shared by every pedal
    /// haptic device. Persisted here so they survive restarts — previously
    /// they lived only in memory and reset on every launch.</summary>
    public PedalHapticGains HapticGains { get; set; } = new();
    public PedalAxisConfig Gas { get; set; } = new();
    public PedalAxisConfig Brake { get; set; } = new();
    public PedalAxisConfig Clutch { get; set; } = new()
    {
        Smoothing = 0.50f
    };
}

/// <summary>Defaults match the previous in-memory defaults exactly.</summary>
public sealed class PedalHapticGains
{
    public float MasterBrake { get; set; } = 1.0f;
    public float MasterGas { get; set; } = 1.0f;
    public float MasterClutch { get; set; } = 1.0f;
    public float AbsBrake { get; set; } = 1.0f;
    public float TcGas { get; set; } = 1.0f;
    public float CurbBoth { get; set; } = 0.5f;
    public float RoadBrake { get; set; } = 0.3f;
    public float ScrubGas { get; set; } = 0.4f;
    public float BrakePressure { get; set; } = 1.0f;
    public float ThrottlePosition { get; set; } = 0.0f;
    public float EngineRpm { get; set; } = 0.0f;
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
