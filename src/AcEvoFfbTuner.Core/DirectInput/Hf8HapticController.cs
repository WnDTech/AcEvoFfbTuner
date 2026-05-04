using System.Reflection;
using System.Runtime.InteropServices;

namespace AcEvoFfbTuner.Core.DirectInput;

public sealed class Hf8HapticController : IDisposable
{
    private object? _forceFeel;
    private Type? _forceFeelType;
    private MethodInfo? _openMethod;
    private MethodInfo? _closeMethod;
    private MethodInfo? _checkConnectedMethod;
    private MethodInfo? _setDirectModeMethod;
    private MethodInfo? _setAmplitudesMethod;
    private MethodInfo? _stopVibrationMethod;
    private PropertyInfo? _commActiveProperty;

    private bool _disposed;
    private bool _connected;

    private volatile float[] _targetIntensities = new float[8];
    private volatile float[] _currentIntensities = new float[8];

    private Thread? _outputThread;
    private volatile bool _outputRunning;
    private bool _timerResolutionSet;

    [DllImport("winmm.dll")]
    private static extern uint timeBeginPeriod(uint period);

    [DllImport("winmm.dll")]
    private static extern uint timeEndPeriod(uint period);

    private int _sendCount;
    private int _sendFailCount;
    private readonly List<string> _diagnosticLog = new();

    public const int MotorCount = 8;

    public bool IsConnected => _connected && _forceFeel != null;
    public string? LastError { get; private set; }
    public string DiagnosticSummary => string.Join("\n", _diagnosticLog);
    public string DeviceInfo { get; private set; } = "";

    public int OutputRateHz { get; set; } = 75;

    private static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "AcEvoFfbTuner", "hf8_debug.log");

    private static readonly string ForceFeelDllPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "HFS", "ForceFeel.dll");

    public void SetMotorIntensities(float[] intensities)
    {
        if (intensities == null || intensities.Length != MotorCount) return;
        for (int i = 0; i < MotorCount; i++)
            _targetIntensities[i] = Math.Clamp(intensities[i], 0f, 1f);
    }

    public bool TryConnect()
    {
        _diagnosticLog.Clear();
        Log("HF8: Attempting ForceFeel SDK connection...");

        if (!File.Exists(ForceFeelDllPath))
        {
            LastError = $"ForceFeel.dll not found at: {ForceFeelDllPath}. Install HFS software first.";
            Log(LastError);
            return false;
        }

        try
        {
            Log($"  Loading ForceFeel.dll from: {ForceFeelDllPath}");
            var assembly = Assembly.LoadFrom(ForceFeelDllPath);
            _forceFeelType = assembly.GetType("ForceFeel.ForceFeel_Class");

            if (_forceFeelType == null)
            {
                LastError = "ForceFeel_Class type not found in ForceFeel.dll";
                Log(LastError);
                return false;
            }

            _forceFeel = Activator.CreateInstance(_forceFeelType);
            if (_forceFeel == null)
            {
                LastError = "Failed to create ForceFeel_Class instance";
                Log(LastError);
                return false;
            }

            _openMethod = _forceFeelType.GetMethod("Open");
            _closeMethod = _forceFeelType.GetMethod("Close");
            _checkConnectedMethod = _forceFeelType.GetMethod("CheckIfDevConnected");
            _setDirectModeMethod = _forceFeelType.GetMethod("SetDirectMotorControlMode");
            _setAmplitudesMethod = _forceFeelType.GetMethod("SetDirectMotorAmplitudes");
            _stopVibrationMethod = _forceFeelType.GetMethod("StopVibration");
            _commActiveProperty = _forceFeelType.GetProperty("DeviceCommunicationActive");

            if (_openMethod == null || _closeMethod == null || _setDirectModeMethod == null || _setAmplitudesMethod == null)
            {
                LastError = "ForceFeel SDK API methods not found";
                Log(LastError);
                return false;
            }

            Log("  ForceFeel SDK loaded. Calling Open()...");
            bool openResult = (bool)_openMethod.Invoke(_forceFeel, null)!;
            Log($"  Open() returned: {openResult}");

            if (!openResult)
            {
                bool devConnected = _checkConnectedMethod != null && (bool)_checkConnectedMethod.Invoke(_forceFeel, null)!;
                bool commActive = _commActiveProperty != null && (bool)_commActiveProperty.GetValue(_forceFeel)!;
                Log($"  CheckIfDevConnected: {devConnected}, DeviceCommunicationActive: {commActive}");

                if (!devConnected)
                {
                    LastError = "HF8 device not found by ForceFeel SDK. Ensure it is connected via USB and not held by HFS software.";
                    Log(LastError);
                    return false;
                }
            }

            Log("  Enabling direct motor control mode...");
            _setDirectModeMethod.Invoke(_forceFeel, new object[] { true });

            bool isConnected = _checkConnectedMethod != null && (bool)_checkConnectedMethod.Invoke(_forceFeel, null)!;
            bool comm = _commActiveProperty != null && (bool)_commActiveProperty.GetValue(_forceFeel)!;

            _connected = isConnected && comm;
            DeviceInfo = $"ForceFeel SDK v1.0.1 | Connected: {_connected} | CommActive: {comm}";
            Log($"  Device connected: {_connected}, CommActive: {comm}");

            if (!_connected)
            {
                LastError = "ForceFeel SDK opened but device communication not active. Close HFS software and retry.";
                Log(LastError);
                return false;
            }

            LastError = null;
            StartOutputThread();
            return true;
        }
        catch (Exception ex)
        {
            LastError = $"ForceFeel SDK error: {ex.Message}";
            Log($"  EXCEPTION: {ex.GetType().Name}: {ex.Message}");
            if (ex.InnerException != null)
                Log($"  Inner: {ex.InnerException.Message}");
            return false;
        }
    }

    private void StartOutputThread()
    {
        if (_outputRunning) return;
        _outputRunning = true;

        _outputThread = new Thread(OutputLoop)
        {
            Name = "HF8 Haptic Output",
            IsBackground = true,
            Priority = ThreadPriority.AboveNormal
        };
        _outputThread.Start();
    }

    private void StopOutputThread()
    {
        _outputRunning = false;
        _outputThread?.Join(2000);
        _outputThread = null;

        if (_timerResolutionSet)
        {
            timeEndPeriod(1);
            _timerResolutionSet = false;
        }
    }

    private void OutputLoop()
    {
        timeBeginPeriod(1);
        _timerResolutionSet = true;

        int intervalMs = Math.Max(1000 / Math.Clamp(OutputRateHz, 1, 200), 1);

        while (_outputRunning)
        {
            try
            {
                for (int i = 0; i < MotorCount; i++)
                {
                    float target = _targetIntensities[i];
                    float current = _currentIntensities[i];
                    _currentIntensities[i] = current + (target - current) * 0.5f;
                }

                SendMotorUpdate();
                Thread.Sleep(intervalMs);
            }
            catch
            {
                Thread.Sleep(intervalMs);
            }
        }
    }

    private void SendMotorUpdate()
    {
        if (_forceFeel == null || _setAmplitudesMethod == null) return;

        var amplitudes = new uint[MotorCount];
        for (int i = 0; i < MotorCount; i++)
            amplitudes[i] = (uint)Math.Clamp(_currentIntensities[i] * 1000f, 0f, 1000f);

        try
        {
            bool ok = (bool)_setAmplitudesMethod.Invoke(_forceFeel, new object[] { amplitudes })!;
            _sendCount++;
            if (!ok) _sendFailCount++;

            if (_sendCount <= 5)
            {
                var values = string.Join(",", amplitudes.Select(a => a.ToString()));
                Log($"Send #{_sendCount}: ok={ok} amplitudes=[{values}]");
            }
        }
        catch (Exception ex)
        {
            _sendCount++;
            _sendFailCount++;
            if (_sendFailCount <= 10)
                Log($"Send failed: {ex.Message}");
        }
    }

    public void AllMotorsOff()
    {
        if (!_connected || _forceFeel == null) return;
        try
        {
            Array.Clear(_targetIntensities);
            Array.Clear(_currentIntensities);

            var zeros = new uint[MotorCount];
            _setAmplitudesMethod?.Invoke(_forceFeel, new object[] { zeros });
        }
        catch { }
    }

    public void Disconnect()
    {
        try { AllMotorsOff(); } catch { }

        StopOutputThread();

        if (_forceFeel != null && _closeMethod != null)
        {
            try
            {
                _closeMethod.Invoke(_forceFeel, null);
                Log("ForceFeel Close() called");
            }
            catch (Exception ex)
            {
                Log($"ForceFeel Close() failed: {ex.Message}");
            }
        }

        _forceFeel = null;
        _connected = false;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Disconnect();
    }

    private void Log(string msg)
    {
        _diagnosticLog.Add(msg);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
            File.AppendAllText(LogPath, $"{DateTime.Now:HH:mm:ss.fff} | {msg}\n");
        }
        catch { }
    }
}
