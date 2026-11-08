namespace AcEvoFfbTuner.Core.Config;

/// <summary>
/// Persistent settings for the MOZA PHF Pedal Haptic Feedback system.
/// RS102 = PHF Kit (control box + 1 module). RS103 = add-on PHF Module.
/// The control box exposes THROTTLE / BRAKE / CLUTCH ports — one provider
/// covers both SKUs; <see cref="ModuleMask"/> tracks which ports have a
/// physical module fitted (bit0=throttle, bit1=brake, bit2=clutch).
/// </summary>
public sealed class MozaPhfConfig
{
    public bool Enabled { get; set; } = true;

    /// <summary>Which ports have a module fitted. Default 0b011 = RS102 kit
    /// on throttle+brake... actually the single kit module ships unassigned —
    /// default enables brake only; the user enables extra ports as RS103
    /// modules are added.</summary>
    public int ModuleMask { get; set; } = 0b010;

    /// <summary>Last-used HID device path. Empty = auto-detect.</summary>
    public string DevicePath { get; set; } = "";

    /// <summary>HID output report id. 0 = first byte is data (no id).</summary>
    public byte ReportId { get; set; } = 0x00;

    /// <summary>HID output report length in bytes.</summary>
    public int ReportLength { get; set; } = 64;

    /// <summary>Global intensity scale 0-2 applied after routing gains.</summary>
    public float IntensityScale { get; set; } = 1.0f;

    /// <summary>Per-signal vibration frequencies in Hz (0-100 per PHF spec).</summary>
    public int AbsFrequency { get; set; } = 18;
    public int TcFrequency { get; set; } = 30;
    public int CurbFrequency { get; set; } = 45;
    public int RoadFrequency { get; set; } = 65;
    public int ScrubFrequency { get; set; } = 50;
    public int RearSlipFrequency { get; set; } = 35;
    public int BrakePressureFrequency { get; set; } = 25;
    public int RpmFrequency { get; set; } = 80;
    public int ShiftFrequency { get; set; } = 55;
    public int ClutchFrequency { get; set; } = 40;
    public int LockupFrequency { get; set; } = 50;
    public int WheelspinFrequency { get; set; } = 35;
    public int ThrottlePositionFrequency { get; set; } = 30;

    /// <summary>Per-effect strength in percent (0-200, 100 = unchanged).
    /// PHF-only: multiplies that effect's contribution before the pedal's
    /// master gain. The Osoyoo pedals do not read these.</summary>
    public int AbsStrength { get; set; } = 100;
    public int LockupStrength { get; set; } = 100;
    public int BrakePressureStrength { get; set; } = 100;
    public int RoadStrength { get; set; } = 100;
    public int CurbStrength { get; set; } = 100;
    public int TcStrength { get; set; } = 100;
    public int WheelspinStrength { get; set; } = 100;
    public int ThrottlePositionStrength { get; set; } = 100;
    public int EngineStrength { get; set; } = 100;
    public int ShiftStrength { get; set; } = 100;
    public int ScrubStrength { get; set; } = 100;

    /// <summary>Which pedals each PHF effect plays on. Bitmask matching
    /// <see cref="ModuleMask"/>: 1 = throttle, 2 = brake, 4 = clutch.
    /// Keys are <see cref="PhfEffectKeys"/>. Missing keys fall back to
    /// <see cref="DefaultRoute"/>, so older saved configs keep working.</summary>
    public Dictionary<string, int> EffectRoutes { get; set; } = new();

    public int GetRoute(string effectKey) =>
        EffectRoutes.TryGetValue(effectKey, out var m) ? m : DefaultRoute(effectKey);

    public void SetRoute(string effectKey, int mask) => EffectRoutes[effectKey] = mask & 0b111;

    public static int DefaultRoute(string effectKey) => effectKey switch
    {
        PhfEffectKeys.Abs or PhfEffectKeys.Lockup or PhfEffectKeys.BrakePressure
            or PhfEffectKeys.Road => 0b010,
        PhfEffectKeys.Kerbs => 0b011,
        PhfEffectKeys.Engine or PhfEffectKeys.Shift => 0b101,
        _ => 0b001 // TC, wheelspin, throttle position, scrub
    };

    public bool HasModule(MozaPhfChannel channel) =>
        (ModuleMask & (1 << (int)channel)) != 0;

    public void SetModule(MozaPhfChannel channel, bool fitted)
    {
        if (fitted)
            ModuleMask |= 1 << (int)channel;
        else
            ModuleMask &= ~(1 << (int)channel);
    }
}

/// <summary>Stable keys for PHF effects (used for routing).</summary>
public static class PhfEffectKeys
{
    public const string Abs = "Abs";
    public const string Lockup = "Lockup";
    public const string BrakePressure = "BrakePressure";
    public const string Road = "Road";
    public const string Kerbs = "Kerbs";
    public const string Tc = "Tc";
    public const string Wheelspin = "Wheelspin";
    public const string ThrottlePosition = "ThrottlePosition";
    public const string Engine = "Engine";
    public const string Shift = "Shift";
    public const string Scrub = "Scrub";
}

public enum MozaPhfChannel
{
    Throttle = 0,
    Brake = 1,
    Clutch = 2
}
