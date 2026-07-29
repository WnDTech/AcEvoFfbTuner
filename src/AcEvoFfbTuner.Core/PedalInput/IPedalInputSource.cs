namespace AcEvoFfbTuner.Core.PedalInput;

public interface IPedalInputSource
{
    bool IsAvailable { get; }
    SourceType SourceType { get; }
    string DeviceName { get; }
    bool TryReadRaw(out RawPedalState state);
}
