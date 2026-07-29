namespace AcEvoFfbTuner.Core.PedalHaptics;

public enum HapticSignalType
{
    Abs,
    Tc,
    Curb,
    Road,
    Scrub,
    RearSlip,
    BrakePressure
}

public enum HapticOutputMode
{
    Vibration,
    Pulse,
    ForcePushBack,
    Both
}
