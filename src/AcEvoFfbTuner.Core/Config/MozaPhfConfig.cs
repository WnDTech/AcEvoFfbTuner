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

public enum MozaPhfChannel
{
    Throttle = 0,
    Brake = 1,
    Clutch = 2
}
