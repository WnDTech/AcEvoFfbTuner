namespace AcEvoFfbTuner.Core.FfbProviders;

public interface IFFBProvider : IDisposable
{
    string ProviderName { get; }
    bool IsInitialized { get; }
    bool IsAvailable { get; }

    /// <summary>True while the provider has a live transport connection,
    /// regardless of whether its force session is engaged (stream-based
    /// providers are "connected" in standby too). Used by auto-detect to
    /// avoid churning a healthy provider.</summary>
    bool IsConnected => IsAvailable;

    bool Initialize();
    void UpdateTorque(float signal);
    void SetHaptics(HapticData data);
    void ZeroTorque();
    void Shutdown();

    /// <summary>Engage the provider's force session (e.g. start the TrueForce
    /// stream). Default no-op — stream-based providers override this so the
    /// session only becomes active while the app is actually outputting force.
    /// </summary>
    bool Engage() => true;

    /// <summary>Release the provider's force session back to standby (e.g.
    /// stop the TrueForce stream so the wheel returns to its normal FFB path
    /// and stops humming). Default no-op.</summary>
    void Disengage() { }
}
