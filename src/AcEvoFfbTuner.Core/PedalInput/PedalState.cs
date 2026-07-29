namespace AcEvoFfbTuner.Core.PedalInput;

public enum SourceType
{
    None,
    ScLink,
    Hid,
    DirectInput,
    Keyboard,
    Replay
}

public readonly struct RawPedalState
{
    public float GasRaw { get; init; }
    public float BrakeRaw { get; init; }
    public float ClutchRaw { get; init; }
    public SourceType Source { get; init; }
    public long TimestampTicks { get; init; }
}

public readonly struct PedalState
{
    public float GasInput { get; init; }
    public float BrakeInput { get; init; }
    public float ClutchInput { get; init; }
    public SourceType Source { get; init; }
    public long TimestampTicks { get; init; }
}
