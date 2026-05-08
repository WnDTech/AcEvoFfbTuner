using System.Runtime.InteropServices;

namespace AcEvoFfbTuner.Core.FfbProviders;

public sealed class SimucubeProvider : IFFBProvider
{
    private const string SCApiDll = "sc_api";
    private const int ScApiVersion = 1;

    private static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "AcEvoFfbTuner", "simucube_provider.log");

    [StructLayout(LayoutKind.Sequential)]
    private struct ScDeviceId
    {
        public uint DeviceType;
        public uint DeviceId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ScForceCommand
    {
        public float TorqueNm;
        public uint CommandFlags;
        public float Damping;
        public float Friction;
    }

    [DllImport(SCApiDll, EntryPoint = "sc_api_init", CallingConvention = CallingConvention.Cdecl)]
    private static extern int ScApiInit(int version);

    [DllImport(SCApiDll, EntryPoint = "sc_api_shutdown", CallingConvention = CallingConvention.Cdecl)]
    private static extern void ScApiShutdown();

    [DllImport(SCApiDll, EntryPoint = "sc_api_get_device_count", CallingConvention = CallingConvention.Cdecl)]
    private static extern int ScApiGetDeviceCount();

    [DllImport(SCApiDll, EntryPoint = "sc_api_get_device_id", CallingConvention = CallingConvention.Cdecl)]
    private static extern int ScApiGetDeviceId(int index, out ScDeviceId deviceId);

    [DllImport(SCApiDll, EntryPoint = "sc_api_connect_device", CallingConvention = CallingConvention.Cdecl)]
    private static extern int ScApiConnectDevice(ref ScDeviceId deviceId);

    [DllImport(SCApiDll, EntryPoint = "sc_api_disconnect_device", CallingConvention = CallingConvention.Cdecl)]
    private static extern int ScApiDisconnectDevice(ref ScDeviceId deviceId);

    [DllImport(SCApiDll, EntryPoint = "sc_api_set_force", CallingConvention = CallingConvention.Cdecl)]
    private static extern int ScApiSetForce(ref ScDeviceId deviceId, ref ScForceCommand command);

    [DllImport(SCApiDll, EntryPoint = "sc_api_update", CallingConvention = CallingConvention.Cdecl)]
    private static extern int ScApiUpdate();

    private ScDeviceId _activeDevice;
    private bool _apiInitialized;
    private bool _disposed;
    private bool _dllAvailable;
    private Exception? _loadError;

    public string ProviderName => "Simucube Link API";
    public bool IsInitialized { get; private set; }
    public bool IsAvailable => _dllAvailable && _apiInitialized && IsInitialized;
    public string? LastError { get; private set; }

    public SimucubeProvider()
    {
        try
        {
            int result = ScApiInit(ScApiVersion);
            _dllAvailable = true;
            if (result != 0)
            {
                Log($"Simucube sc_api init returned error: {result}");
                return;
            }
            _apiInitialized = true;
            Log("Simucube sc_api DLL loaded and initialized successfully");
        }
        catch (Exception ex)
        {
            _loadError = ex;
            _dllAvailable = false;
            Log($"Simucube sc_api DLL not available: {ex.Message}");
        }
    }

    public bool Initialize()
    {
        if (!_dllAvailable)
        {
            LastError = $"Simucube SDK not available: {(_loadError?.Message ?? "DLL not found")}";
            Log($"Initialize failed: {LastError}");
            return false;
        }

        try
        {
            int deviceCount = ScApiGetDeviceCount();
            Log($"Scanning for Simucube devices: {deviceCount} found");

            for (int i = 0; i < deviceCount; i++)
            {
                ScApiGetDeviceId(i, out var deviceId);

                int connectResult = ScApiConnectDevice(ref deviceId);
                if (connectResult == 0)
                {
                    _activeDevice = deviceId;
                    IsInitialized = true;
                    Log($"Connected to Simucube device #{i} (type={deviceId.DeviceType}, id={deviceId.DeviceId})");
                    return true;
                }

                Log($"Failed to connect device #{i}: error {connectResult}");
            }

            LastError = $"No Simucube device could be connected ({deviceCount} enumerated)";
            Log(LastError);
            return false;
        }
        catch (Exception ex)
        {
            LastError = $"Simucube initialization error: {ex.Message}";
            Log($"Initialize exception: {ex}");
            return false;
        }
    }

    public void UpdateTorque(float signal)
    {
        if (!IsAvailable) return;

        try
        {
            ScApiUpdate();

            var cmd = new ScForceCommand
            {
                TorqueNm = Math.Clamp(signal, -1f, 1f),
                CommandFlags = 1,
                Damping = 0f,
                Friction = 0f
            };

            ScApiSetForce(ref _activeDevice, ref cmd);
        }
        catch (Exception ex)
        {
            Log($"UpdateTorque error: {ex.Message}");
        }
    }

    public void SetHaptics(HapticData data)
    {
        if (!IsAvailable) return;
    }

    public void ZeroTorque()
    {
        if (!IsAvailable) return;

        try
        {
            var cmd = new ScForceCommand { TorqueNm = 0f, CommandFlags = 1 };
            ScApiSetForce(ref _activeDevice, ref cmd);
        }
        catch (Exception ex)
        {
            Log($"ZeroTorque error: {ex.Message}");
        }
    }

    public void Shutdown()
    {
        if (!IsAvailable) return;

        try
        {
            ZeroTorque();
            ScApiDisconnectDevice(ref _activeDevice);
        }
        catch (Exception ex)
        {
            Log($"Shutdown error: {ex.Message}");
        }

        IsInitialized = false;
    }

    private static void Log(string msg)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
            File.AppendAllText(LogPath, $"[{DateTime.Now:HH:mm:ss.fff}] {msg}\n");
        }
        catch { }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Shutdown();

        if (_apiInitialized)
        {
            try { ScApiShutdown(); } catch { }
            _apiInitialized = false;
        }
    }
}
