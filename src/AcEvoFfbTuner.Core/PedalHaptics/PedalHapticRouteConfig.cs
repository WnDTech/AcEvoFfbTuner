namespace AcEvoFfbTuner.Core.PedalHaptics;

public sealed class PedalHapticRouteConfig
{
    public bool Enabled { get; set; }

    public string BrakeHapticDevice { get; set; } = "Auto";
    public string GasHapticDevice { get; set; } = "Auto";
    public string ClutchHapticDevice { get; set; } = "Auto";

    public float BrakeHapticGain { get; set; } = 1.0f;
    public float GasHapticGain { get; set; } = 1.0f;
    public float ClutchHapticGain { get; set; } = 1.0f;

    public List<HapticRouteEntry> Routes { get; set; } =
    [
        new() { Signal = "abs",   TargetPedal = "brake", Gain = 1.0f, Mode = "vibration" },
        new() { Signal = "tc",    TargetPedal = "gas",   Gain = 1.0f, Mode = "vibration" },
        new() { Signal = "curb",  TargetPedal = "both",  Gain = 0.5f, Mode = "pulse" },
        new() { Signal = "road",  TargetPedal = "brake", Gain = 0.3f, Mode = "vibration" },
        new() { Signal = "scrub", TargetPedal = "gas",   Gain = 0.4f, Mode = "vibration" },
        new() { Signal = "clutchpos", TargetPedal = "clutch", Gain = 1.0f, Mode = "vibration" }
    ];
}

public sealed class HapticRouteEntry
{
    public string Signal { get; set; } = "";
    public string TargetPedal { get; set; } = "";
    public float Gain { get; set; } = 1.0f;
    public string Mode { get; set; } = "vibration";
}
