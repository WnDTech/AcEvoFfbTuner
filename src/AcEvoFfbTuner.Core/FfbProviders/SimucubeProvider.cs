namespace AcEvoFfbTuner.Core.FfbProviders;

public sealed class SimucubeProvider : IFFBProvider
{
    public string ProviderName => "Simucube Link API (stub)";
    public bool IsInitialized => false;
    public bool IsAvailable => false;
    public string? LastError => "Simucube SDK integration pending — awaiting SDK access from Granite Devices";

    public bool Initialize() => false;
    public void UpdateTorque(float signal) { }
    public void SetHaptics(HapticData data) { }
    public void ZeroTorque() { }
    public void Shutdown() { }
    public void Dispose() { }
}
