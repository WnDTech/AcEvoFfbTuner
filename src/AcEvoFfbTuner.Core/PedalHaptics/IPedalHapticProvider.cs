namespace AcEvoFfbTuner.Core.PedalHaptics;

public interface IPedalHapticProvider : IDisposable
{
    string DeviceName { get; }
    bool IsAvailable { get; }
    bool IsBrakeSupported { get; }
    bool IsGasSupported { get; }
    bool IsClutchSupported { get; }

    void SetBrakeHaptic(float intensity, HapticSignalType signal);
    void SetGasHaptic(float intensity, HapticSignalType signal);
    void SetClutchHaptic(float intensity, HapticSignalType signal);
    void StopAll();
}
