namespace AcEvoFfbTuner.Core.FfbProviders;

public sealed class VnmProvider : IFFBProvider
{
    public string ProviderName => "VNM SDK (stub)";
    public bool IsInitialized => false;
    public bool IsAvailable => false;
    public string? LastError => "VNM SDK integration pending — awaiting SDK access from VNM";

    public bool Initialize()
    {
        Console.WriteLine("[VnmProvider] SDK Not Found — Defaulting to DirectInput");
        return false;
    }

    public void UpdateTorque(float signal) { }
    public void SetHaptics(HapticData data) { }
    public void ZeroTorque() { }
    public void Shutdown() { }
    public void Dispose() { }
}
