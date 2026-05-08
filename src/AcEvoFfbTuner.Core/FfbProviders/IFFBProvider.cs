namespace AcEvoFfbTuner.Core.FfbProviders;

public interface IFFBProvider : IDisposable
{
    string ProviderName { get; }
    bool IsInitialized { get; }
    bool IsAvailable { get; }

    bool Initialize();
    void UpdateTorque(float signal);
    void SetHaptics(HapticData data);
    void ZeroTorque();
    void Shutdown();
}
