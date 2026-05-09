namespace AcEvoFfbTuner.Core.FfbProviders;

public sealed class AsetekProvider : IFFBProvider
{
    public string ProviderName => "Asetek RaceHub API (stub)";
    public bool IsInitialized => false;
    public bool IsAvailable => false;
    public string? LastError => "Asetek SDK integration pending — awaiting SDK access from Asetek";

    public bool Initialize()
    {
        Console.WriteLine("[AsetekProvider] SDK Not Found — Defaulting to DirectInput");
        return false;
    }

    public void UpdateTorque(float signal) { }
    public void SetHaptics(HapticData data) { }
    public void ZeroTorque() { }
    public void Shutdown() { }
    public void Dispose() { }
}
