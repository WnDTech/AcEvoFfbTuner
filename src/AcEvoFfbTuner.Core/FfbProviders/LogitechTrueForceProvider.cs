namespace AcEvoFfbTuner.Core.FfbProviders;

public sealed class LogitechTrueForceProvider : IFFBProvider
{
    public string ProviderName => "Logitech TrueForce (stub)";
    public bool IsInitialized => false;
    public bool IsAvailable => false;
    public bool SupportsTrueForce => false;
    public string? LastError => "Logitech TrueForce SDK integration pending — awaiting SDK access from Logitech";

    public bool Initialize() => false;
    public void UpdateTorque(float signal) { }
    public void SetHaptics(HapticData data) { }
    public void ZeroTorque() { }
    public void Shutdown() { }
    public void Dispose() { }
}
