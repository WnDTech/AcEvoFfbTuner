namespace AcEvoFfbTuner.Core.PedalHaptics;

public sealed class PedalHapticSignals
{
    public float AbsModulation { get; set; }
    public float ScrubModulation { get; set; }
    public float RearSlipModulation { get; set; }
    public float RoadForceModulation { get; set; }
    public float OfftrackModulation { get; set; }
    public float TcRumble { get; set; }
    public float BrakePressureLevel { get; set; }
    public float ClutchPosition { get; set; }
    public float SpeedKmh { get; set; }
    public long TimestampTicks { get; set; }
}
