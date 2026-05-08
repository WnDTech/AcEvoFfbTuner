using System.Runtime.InteropServices;

namespace AcEvoFfbTuner.Core.FfbProviders;

public sealed class LogitechTrueForceProvider : IFFBProvider
{
    private const string LogiSdkDll = "LogitechSteeringWheel";

    private static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "AcEvoFfbTuner", "logitech_provider.log");

    [DllImport(LogiSdkDll, EntryPoint = "LogiSteeringInitialize", CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool LogiSteeringInitialize([MarshalAs(UnmanagedType.Bool)] bool ignoreXInputControllers);

    [DllImport(LogiSdkDll, EntryPoint = "LogiSteeringShutdown", CallingConvention = CallingConvention.Cdecl)]
    private static extern void LogiSteeringShutdown();

    [DllImport(LogiSdkDll, EntryPoint = "LogiUpdate", CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool LogiUpdate();

    [DllImport(LogiSdkDll, EntryPoint = "LogiGetState", CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool LogiGetState(int index);

    [DllImport(LogiSdkDll, EntryPoint = "LogiIsConnected", CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool LogiIsConnected(int index);

    [DllImport(LogiSdkDll, EntryPoint = "LogiIsControllerConnected", CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool LogiIsControllerConnected(int index);

    [DllImport(LogiSdkDll, EntryPoint = "LogiGetControllerType", CallingConvention = CallingConvention.Cdecl)]
    private static extern int LogiGetControllerType(int index);

    [DllImport(LogiSdkDll, EntryPoint = "LogiPlayConstantForce", CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool LogiPlayConstantForce(int index, int magnitudePercent);

    [DllImport(LogiSdkDll, EntryPoint = "LogiStopConstantForce", CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool LogiStopConstantForce(int index);

    [DllImport(LogiSdkDll, EntryPoint = "LogiPlayRpmEffect", CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool LogiPlayRpmEffect(int index, int periodMs, int magnitude);

    [DllImport(LogiSdkDll, EntryPoint = "LogiStopRpmEffect", CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool LogiStopRpmEffect(int index);

    [DllImport(LogiSdkDll, EntryPoint = "LogiGenerateNonLinearValues", CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool LogiGenerateNonLinearValues(int index, int nonLinPoints);

    private const int ControllerIndex = 0;
    private const int LogiControllerTypeG923 = 22;
    private const int LogiControllerTypeG29 = 19;
    private const int LogiControllerTypeG920 = 20;

    private bool _sdkInitialized;
    private bool _disposed;
    private bool _dllAvailable;
    private Exception? _loadError;

    public string ProviderName => "Logitech TrueForce";
    public bool IsInitialized { get; private set; }
    public bool IsAvailable => _dllAvailable && _sdkInitialized && IsInitialized;
    public bool SupportsTrueForce { get; private set; }
    public string? LastError { get; private set; }

    public LogitechTrueForceProvider()
    {
        try
        {
            bool initResult = LogiSteeringInitialize(true);
            _dllAvailable = true;

            if (initResult)
            {
                _sdkInitialized = true;
                Log("Logitech Steering SDK initialized successfully");
            }
            else
            {
                Log("Logitech Steering SDK init returned false — no compatible device");
            }
        }
        catch (Exception ex)
        {
            _loadError = ex;
            _dllAvailable = false;
            Log($"Logitech Steering SDK DLL not available: {ex.Message}");
        }
    }

    public bool Initialize()
    {
        if (!_dllAvailable)
        {
            LastError = $"Logitech SDK not available: {(_loadError?.Message ?? "DLL not found")}";
            Log($"Initialize failed: {LastError}");
            return false;
        }

        if (!_sdkInitialized)
        {
            LastError = "Logitech SDK not initialized";
            return false;
        }

        try
        {
            LogiUpdate();

            if (!LogiIsConnected(ControllerIndex))
            {
                LastError = "No Logitech wheel connected";
                Log(LastError);
                return false;
            }

            int controllerType = LogiGetControllerType(ControllerIndex);
            Log($"Detected Logitech controller type: {controllerType}");

            SupportsTrueForce = controllerType >= LogiControllerTypeG923;
            IsInitialized = true;

            Log($"Connected to Logitech wheel (type={controllerType}, TrueForce={SupportsTrueForce})");
            return true;
        }
        catch (Exception ex)
        {
            LastError = $"Logitech initialization error: {ex.Message}";
            Log($"Initialize exception: {ex}");
            return false;
        }
    }

    public void UpdateTorque(float signal)
    {
        if (!IsAvailable) return;

        try
        {
            LogiUpdate();

            int magnitude = (int)(Math.Clamp(signal, -1f, 1f) * 100f);
            if (Math.Abs(magnitude) < 1)
            {
                LogiStopConstantForce(ControllerIndex);
            }
            else
            {
                LogiPlayConstantForce(ControllerIndex, magnitude);
            }
        }
        catch (Exception ex)
        {
            Log($"UpdateTorque error: {ex.Message}");
        }
    }

    public void SetHaptics(HapticData data)
    {
        if (!IsAvailable) return;

        try
        {
            LogiUpdate();

            if (SupportsTrueForce && data.RpmFrequency > 0)
            {
                int periodMs = Math.Max(1, (int)(1000f / Math.Max(data.RpmFrequency, 1f)));
                int magnitude = (int)(Math.Clamp(data.VibrationIntensity, 0f, 1f) * 100);
                LogiPlayRpmEffect(ControllerIndex, periodMs, magnitude);
            }
            else if (data.VibrationIntensity < 0.001f)
            {
                if (SupportsTrueForce)
                    LogiStopRpmEffect(ControllerIndex);
            }
        }
        catch (Exception ex)
        {
            Log($"SetHaptics error: {ex.Message}");
        }
    }

    public void ZeroTorque()
    {
        if (!IsAvailable) return;

        try
        {
            LogiUpdate();
            LogiStopConstantForce(ControllerIndex);
            if (SupportsTrueForce)
                LogiStopRpmEffect(ControllerIndex);
        }
        catch (Exception ex)
        {
            Log($"ZeroTorque error: {ex.Message}");
        }
    }

    public void Shutdown()
    {
        if (IsInitialized)
        {
            ZeroTorque();
            IsInitialized = false;
        }
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

        if (_sdkInitialized)
        {
            try { LogiSteeringShutdown(); } catch { }
            _sdkInitialized = false;
        }
    }
}
