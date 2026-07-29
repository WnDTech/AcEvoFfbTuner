using System.Runtime.InteropServices;

namespace AcEvoFfbTuner.Core.PedalHaptics.Providers;

/// <summary>
/// Simucube ActivePedal haptic provider via SC Link API (sc_bridge.dll).
/// Requires sc_bridge.dll rebuilt with pedal-specific exports:
///   sc_get_device_count, sc_get_device_session_id,
///   sc_device_has_feedback_type, sc_get_device_role,
///   sc_read_variable_float, sc_ffb_configure_force_N,
///   sc_ffb_configure_force_relative, sc_ffb_configure_position_mm
///
/// Without these exports, the provider logs a warning and remains unavailable.
/// Rebuild sc_bridge.cpp from lib/simucube/sc-bridge/ with MSVC to enable.
/// </summary>
public sealed class ActivePedalProvider : IPedalHapticProvider
{
    private const string BridgeDllName = "sc_bridge.dll";

    private IntPtr _apiHandle;
    private IntPtr _sessionPtr;
    private IntPtr _brakePipeline;
    private IntPtr _gasPipeline;
    private bool _bridgeSupportsPedalApi;
    private bool _initialized;
    private bool _disposed;

    public string DeviceName => _bridgeSupportsPedalApi
        ? "Simucube ActivePedal (SC Link)"
        : "Simucube ActivePedal (pending bridge rebuild)";
    public bool IsAvailable => _initialized && _bridgeSupportsPedalApi && _apiHandle != IntPtr.Zero && !_disposed;
    public bool IsBrakeSupported => _brakePipeline != IntPtr.Zero;
    public bool IsGasSupported => _gasPipeline != IntPtr.Zero;
    public bool IsClutchSupported => false;

    public bool Initialize()
    {
        if (_initialized) return _bridgeSupportsPedalApi;

        try
        {
            var bridgePath = Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory, BridgeDllName);
            if (!File.Exists(bridgePath))
            {
                System.Diagnostics.Debug.WriteLine(
                    "[ActivePedalProvider] sc_bridge.dll not found");
                return false;
            }

            _apiHandle = ScCreateApi();
            if (_apiHandle == IntPtr.Zero)
            {
                System.Diagnostics.Debug.WriteLine(
                    "[ActivePedalProvider] Failed to create SC API");
                return false;
            }

            _sessionPtr = ScGetSession(_apiHandle);
            if (_sessionPtr == IntPtr.Zero)
            {
                System.Diagnostics.Debug.WriteLine(
                    "[ActivePedalProvider] Failed to get SC session");
                ScDestroyApi(_apiHandle);
                _apiHandle = IntPtr.Zero;
                return false;
            }

            int result = ScSessionRegisterControl(_sessionPtr, 0,
                "AcEvoFfbTuner", "AC EVO FFB Tuner", "Kilo", "1.0.0");
            if (result == 0)
                return false;

            _initialized = true;

            // Probe for pedal API — sc_get_device_count is the marker export
            // for the extended bridge. If it's not exported, the bridge wasn't
            // rebuilt with pedal support.
            try
            {
                int deviceCount = ScGetDeviceCount(_apiHandle);
                _bridgeSupportsPedalApi = true;
                System.Diagnostics.Debug.WriteLine(
                    $"[ActivePedalProvider] Extended bridge detected, {deviceCount} devices");
                EnumeratePedals();
            }
            catch (EntryPointNotFoundException)
            {
                _bridgeSupportsPedalApi = false;
                System.Diagnostics.Debug.WriteLine(
                    "[ActivePedalProvider] sc_bridge.dll does not have pedal API. " +
                    "Rebuild from lib/simucube/sc-bridge/ with pedal exports.");
            }

            return true;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(
                $"[ActivePedalProvider] Init error: {ex.Message}");
            return false;
        }
    }

    private void EnumeratePedals()
    {
        if (!_bridgeSupportsPedalApi) return;

        try
        {
            int count = ScGetDeviceCount(_apiHandle);
            for (int i = 0; i < count; i++)
            {
                ushort sessionId = ScGetDeviceSessionId(_apiHandle, i);
                bool isActivePedal = ScDeviceHasFeedbackType(_apiHandle, sessionId,
                    (int)FeedbackType.ActivePedal);
                if (!isActivePedal) continue;

                int role = ScGetDeviceRole(_apiHandle, sessionId);
                var pipeline = ScCreateFfbPipeline(_apiHandle, sessionId);
                if (pipeline == IntPtr.Zero) continue;

                int configured = ScFfbConfigureForceN(pipeline, 1.0f);
                if (configured == 0)
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"[ActivePedalProvider] Pipeline config failed for role={role}");
                    ScDestroyFfbPipeline(pipeline);
                    continue;
                }

                // TODO: read pedal role via sc_get_device_role
                // 0=gas, 1=brake, 2=clutch (from SC API convention)
                if (role == 1)
                    _brakePipeline = pipeline;
                else if (role == 0)
                    _gasPipeline = pipeline;
                else
                    ScDestroyFfbPipeline(pipeline);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(
                $"[ActivePedalProvider] Enumeration error: {ex.Message}");
        }
    }

    public void SetBrakeHaptic(float intensity, HapticSignalType signal)
    {
        if (!IsAvailable || _brakePipeline == IntPtr.Zero) return;
        SendForceSample(_brakePipeline, intensity);
    }

    public void SetGasHaptic(float intensity, HapticSignalType signal)
    {
        if (!IsAvailable || _gasPipeline == IntPtr.Zero) return;
        SendForceSample(_gasPipeline, intensity);
    }

    public void SetClutchHaptic(float intensity, HapticSignalType signal) { }

    public void StopAll()
    {
        if (_brakePipeline != IntPtr.Zero)
        {
            ScFfbStop(_brakePipeline);
            ScFfbRemove(_brakePipeline);
        }
        if (_gasPipeline != IntPtr.Zero)
        {
            ScFfbStop(_gasPipeline);
            ScFfbRemove(_gasPipeline);
        }
    }

    private void SendForceSample(IntPtr pipeline, float intensity)
    {
        if (pipeline == IntPtr.Zero) return;

        try
        {
            float[] samples = [-Math.Clamp(intensity * 50f, 0f, 50f)];
            long timestampNs = ScGetTimestampNow();
            int sampleTimeNs = 16_666_666; // 60Hz

            ScFfbGenerateSamples(pipeline, timestampNs, sampleTimeNs, samples, 1);
        }
        catch { }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        StopAll();

        if (_brakePipeline != IntPtr.Zero)
        { ScDestroyFfbPipeline(_brakePipeline); _brakePipeline = IntPtr.Zero; }
        if (_gasPipeline != IntPtr.Zero)
        { ScDestroyFfbPipeline(_gasPipeline); _gasPipeline = IntPtr.Zero; }
        if (_sessionPtr != IntPtr.Zero)
        { ScReleaseSession(_sessionPtr); _sessionPtr = IntPtr.Zero; }
        if (_apiHandle != IntPtr.Zero)
        { ScDestroyApi(_apiHandle); _apiHandle = IntPtr.Zero; }
    }

    private enum FeedbackType
    {
        Wheel = 0,
        ActivePedal = 1,
        ActiveDamper = 2
    }

    // --- sc_bridge P/Invoke ---
    // Basic API (always available, from existing sc_bridge.dll)
    [DllImport(BridgeDllName)]
    private static extern IntPtr ScCreateApi();

    [DllImport(BridgeDllName)]
    private static extern void ScDestroyApi(IntPtr apiHandle);

    [DllImport(BridgeDllName)]
    private static extern IntPtr ScGetSession(IntPtr apiHandle);

    [DllImport(BridgeDllName)]
    private static extern void ScReleaseSession(IntPtr sessionPtr);

    [DllImport(BridgeDllName, CharSet = CharSet.Ansi)]
    private static extern int ScSessionRegisterControl(IntPtr sessionPtr, uint controlFlags,
        string idName, string displayName, string author, string version);

    [DllImport(BridgeDllName)]
    private static extern IntPtr ScCreateFfbPipeline(IntPtr apiHandle, ushort deviceSessionId);

    [DllImport(BridgeDllName)]
    private static extern void ScDestroyFfbPipeline(IntPtr pipelineHandle);

    [DllImport(BridgeDllName)]
    private static extern int ScFfbGenerateSamples(IntPtr pipelineHandle,
        long startTimestampNs, int sampleTimeNs, float[] samples, uint sampleCount);

    [DllImport(BridgeDllName)]
    private static extern int ScFfbStop(IntPtr pipelineHandle);

    [DllImport(BridgeDllName)]
    private static extern int ScFfbRemove(IntPtr pipelineHandle);

    [DllImport(BridgeDllName)]
    private static extern long ScGetTimestampNow();

    // Extended API (requires sc_bridge rebuilt with pedal exports)
    [DllImport(BridgeDllName)]
    private static extern int ScGetDeviceCount(IntPtr apiHandle);

    [DllImport(BridgeDllName)]
    private static extern ushort ScGetDeviceSessionId(IntPtr apiHandle, int index);

    [DllImport(BridgeDllName)]
    private static extern bool ScDeviceHasFeedbackType(IntPtr apiHandle, ushort deviceSessionId, int feedbackType);

    [DllImport(BridgeDllName)]
    private static extern int ScGetDeviceRole(IntPtr apiHandle, ushort deviceSessionId);

    [DllImport(BridgeDllName)]
    private static extern int ScFfbConfigureForceN(IntPtr pipelineHandle, float gain);
}
