using AcEvoFfbTuner.Core.DirectInput;

namespace AcEvoFfbTuner.Core.FfbProviders;

public sealed class GenericDirectInputProvider : IFFBProvider
{
    private readonly FfbDeviceManager _deviceManager;
    private bool _disposed;

    public string ProviderName => "DirectInput (Generic)";
    public bool IsInitialized { get; private set; }
    public bool IsAvailable => _deviceManager.IsDeviceAcquired;

    public GenericDirectInputProvider(FfbDeviceManager deviceManager)
    {
        _deviceManager = deviceManager;
    }

    public bool Initialize()
    {
        IsInitialized = _deviceManager.IsDeviceAcquired;
        return IsInitialized;
    }

    public void UpdateTorque(float signal)
    {
        if (!IsAvailable) return;
        _deviceManager.SendConstantForce(Math.Clamp(signal, -1f, 1f));
    }

    public void SetHaptics(HapticData data)
    {
        if (!IsAvailable) return;
        _deviceManager.SetTargetVibration(Math.Clamp(data.VibrationIntensity, 0f, 1f));
    }

    public void ZeroTorque()
    {
        if (!IsAvailable) return;
        _deviceManager.SendConstantForce(0f);
        _deviceManager.SetTargetVibration(0f);
    }

    public void Shutdown()
    {
        ZeroTorque();
        IsInitialized = false;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Shutdown();
    }
}
