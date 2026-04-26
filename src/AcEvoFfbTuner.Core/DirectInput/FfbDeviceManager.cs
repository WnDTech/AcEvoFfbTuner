using System.Runtime.InteropServices;
using DI = SharpDX.DirectInput;

namespace AcEvoFfbTuner.Core.DirectInput;

public sealed class FfbDeviceInfo
{
    public string ProductName { get; init; } = string.Empty;
    public bool IsFfbCapable { get; init; }
    public DI.DeviceInstance DeviceInstance { get; init; } = null!;

    public override string ToString() => ProductName;
}

public sealed class FfbDeviceManager : IDisposable
{
    [DllImport("winmm.dll")]
    private static extern uint timeBeginPeriod(uint period);

    [DllImport("winmm.dll")]
    private static extern uint timeEndPeriod(uint period);

    private DI.DirectInput? _directInput;
    private DI.Joystick? _device;
    private DI.Joystick? _secondaryDevice;
    private DI.Effect? _constantForceEffect;
    private DI.Effect? _periodicEffect;
    private bool _isAcquired;
    private bool _secondaryAcquired;
    private int _maxForceMagnitude = 10000;
    private bool _disposed;
    private int _lastCfMagnitude = int.MinValue;
    private int _lastPeriodicMagnitude = int.MinValue;
    private bool _periodicEffectPlaying;
    private IntPtr _windowHandle;
    private int[]? _ffAxes;
    private bool _invertForce = true;
    private int _consecutiveForceErrors;
    private const int MaxConsecutiveErrors = 10;

    /// <summary>
    /// When true, the force output to the device is inverted (multiplied by -1).
    /// Default is false. Use the pipeline's ForceInvert property instead for runtime toggling.
    /// Only enable this if the wheel hardware itself has a reversed axis that needs correction
    /// independently of the FFB processing pipeline.
    /// </summary>
    public bool ForceInvert
    {
        get => _invertForce;
        set => _invertForce = value;
    }

    // Interpolation state: 1kHz output thread smoothly transitions between physics frames
    private volatile float _targetForce;
    private volatile float _currentForce;
    private volatile float _targetVibration;
    private volatile float _currentVibration;
    private Thread? _interpolationThread;
    private volatile bool _interpolationRunning;
    private bool _timerResolutionSet;

    // Timestamp of the last physics packet arrival, used for time-based interpolation
    private long _lastTargetUpdateTicks;
    private const float PhysicsTickMs = 3.0f; // expected interval between physics packets (~333Hz)

    private static readonly string ConnLogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "AcEvoFfbTuner", "connection_debug.log");

    private static void ConnLog(string msg)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ConnLogPath)!);
            File.AppendAllText(ConnLogPath, $"[{DateTime.Now:HH:mm:ss.fff}] {msg}\n");
        }
        catch { }
    }

    public FfbDeviceInfo? ConnectedDevice { get; private set; }
    public bool IsDeviceAcquired => _isAcquired && _device != null;
    public bool HasLostDeviceAccess => _consecutiveForceErrors >= MaxConsecutiveErrors;
    public bool SupportsPeriodicEffects { get; private set; }
    public string? LastError { get; private set; }

    private readonly WheelLedController _ledController = new();

    public event Action<string>? DeviceConnected;
    public event Action? DeviceDisconnected;

    public void SetWindowHandle(IntPtr handle)
    {
        _windowHandle = handle;
    }

    public List<FfbDeviceInfo> EnumerateFfbDevices()
    {
        try
        {
            _directInput ??= new DI.DirectInput();

            var allInstances = _directInput.GetDevices(DI.DeviceClass.GameControl, DI.DeviceEnumerationFlags.AllDevices);
            var ffbGuids = new HashSet<Guid>(
                _directInput.GetDevices(DI.DeviceClass.GameControl, DI.DeviceEnumerationFlags.ForceFeedback)
                    .Select(d => d.InstanceGuid));

            var devices = new List<FfbDeviceInfo>();

            foreach (var instance in allInstances)
            {
                devices.Add(new FfbDeviceInfo
                {
                    ProductName = instance.ProductName,
                    IsFfbCapable = ffbGuids.Contains(instance.InstanceGuid),
                    DeviceInstance = instance
                });
            }

            return devices;
        }
        catch (Exception ex)
        {
            ConnLog($"EnumerateFfbDevices failed: {ex.GetType().Name}: {ex.Message}");
            LastError = $"DirectInput enumeration failed: {ex.Message}";
            return new List<FfbDeviceInfo>();
        }
    }

    public bool TryConnectDevice(FfbDeviceInfo deviceInfo)
    {
        ConnLog("=== TryConnectDevice START ===");
        ConnLog($"Device: {deviceInfo.ProductName} GUID={deviceInfo.DeviceInstance.InstanceGuid}");
        ConnLog($"WindowHandle: 0x{_windowHandle.ToInt64():X8} IsZero={_windowHandle == IntPtr.Zero}");
        ConnLog($"Primary acquired: {_isAcquired}, Secondary acquired: {_secondaryAcquired}");
        ConnLog($"DirectInput instance: {_directInput != null}, Device: {_device != null}");

        DisconnectDevice();
        DisconnectSecondaryDevice();

        ConnLog("After disconnect — calling GC cleanup");
        GC.Collect();
        GC.WaitForPendingFinalizers();
        Thread.Sleep(100);

        LastError = null;

        var handle = _windowHandle != IntPtr.Zero ? _windowHandle : GetDesktopWindow();
        ConnLog($"Using handle: 0x{handle.ToInt64():X8} IsDesktop={handle == GetDesktopWindow()}");

        if (handle == GetDesktopWindow())
        {
            LastError = "No valid window handle — cannot acquire exclusive device access.";
            ConnLog("FAILED: No valid window handle");
            return false;
        }

        Exception? exclusiveEx = null;

        for (int attempt = 0; attempt < 3; attempt++)
        {
            ConnLog($"--- Attempt {attempt + 1}/3 ---");

            if (attempt > 0)
            {
                _device?.Dispose();
                _device = null;
                ConnLog("Disposing old DirectInput instance and creating fresh one");
                _directInput?.Dispose();
                _directInput = new DI.DirectInput();
                GC.Collect();
                GC.WaitForPendingFinalizers();
                Thread.Sleep(200 * attempt);
            }

            try
            {
                ConnLog("Creating Joystick...");
                _device = new DI.Joystick(_directInput!, deviceInfo.DeviceInstance.InstanceGuid);
                ConnLog("Joystick created OK. Setting cooperative level Exclusive|Background...");
                _device.SetCooperativeLevel(handle, DI.CooperativeLevel.Exclusive | DI.CooperativeLevel.Background);
                ConnLog("Cooperative level set. Acquiring...");
                _device.Acquire();
                _isAcquired = true;
                ConnLog("EXCLUSIVE ACQUIRED SUCCESSFULLY");

                DiscoverFfAxes();
                ConnLog($"FFB axes: {string.Join(",", _ffAxes ?? Array.Empty<int>())}, Periodic: {SupportsPeriodicEffects}");
                ConnectedDevice = deviceInfo;
                StartInterpolationThread();

                if (!_ledController.TryConnect(deviceInfo.ProductName))
                {
                    LastError = string.IsNullOrEmpty(LastError)
                        ? _ledController.LastError
                        : $"{LastError} | {_ledController.LastError}";
                    ConnLog($"LED controller: {_ledController.LastError}");
                }
                else
                {
                    ConnLog("LED controller connected");
                }

                DeviceConnected?.Invoke(deviceInfo.ProductName);
                return true;
            }
            catch (Exception ex)
            {
                ConnLog($"ATTEMPT {attempt + 1} FAILED: {ex.GetType().Name}: {ex.Message}");
                if (ex.InnerException != null)
                    ConnLog($"  Inner: {ex.InnerException.Message}");
                _device?.Dispose();
                _device = null;
                exclusiveEx = ex;
            }
        }

        var conflicts = DetectConflictingProcesses();
        ConnLog($"ALL 3 ATTEMPTS FAILED. Conflicts: {(conflicts.Count > 0 ? string.Join(", ", conflicts) : "none detected")}");

        LastError = $"Cannot acquire exclusive FFB access after 3 attempts.\n" +
                    $"Error: {exclusiveEx?.Message}\n" +
                    $"Window handle: 0x{handle.ToInt64():X8}\n" +
                    (conflicts.Count > 0
                        ? $"Conflicting processes detected: {string.Join(", ", conflicts)}\n"
                        : "No known conflicting processes detected.\n") +
                    $"Solutions:\n" +
                    $"- Close wheel driver software (Moza Pit House, Fanatec, Logitech G HUB)\n" +
                    $"- Close any other app using the wheel (simulators, button boxes)\n" +
                    $"- Reconnect the wheel USB cable and retry";
        DisconnectDevice();
        return false;
    }

    private static List<string> DetectConflictingProcesses()
    {
        var conflicts = new List<string>();
        string[] known = new[]
        {
            "MozaPitHouse", "PitHouse", "mozapit",
            "LGS", "LogitechG", "LogiJoy",
            "Fanatec", "FanatecDriver",
            "SimHub", "simhub",
            "vJoy",
        };

        try
        {
            foreach (var proc in System.Diagnostics.Process.GetProcesses())
            {
                try
                {
                    foreach (var k in known)
                    {
                        if (proc.ProcessName.Contains(k, StringComparison.OrdinalIgnoreCase))
                        {
                            if (!conflicts.Contains(proc.ProcessName))
                                conflicts.Add(proc.ProcessName);
                        }
                    }
                }
                catch { }
            }
        }
        catch { }

        return conflicts;
    }

    private void DiscoverFfAxes()
    {
        _ffAxes = new int[] { 0 };
        SupportsPeriodicEffects = false;

        if (_device == null) return;

        try
        {
            var effects = _device.GetEffects();
            if (effects.Count == 0) return;

            foreach (var ei in effects)
            {
                if (ei.Guid == DI.EffectGuid.Sine ||
                    ei.Guid == DI.EffectGuid.Square ||
                    ei.Guid == DI.EffectGuid.Triangle ||
                    ei.Guid == DI.EffectGuid.SawtoothUp ||
                    ei.Guid == DI.EffectGuid.SawtoothDown)
                {
                    SupportsPeriodicEffects = true;
                    break;
                }
            }

            int? primaryAxis = null;
            int? fallbackAxis = null;

            foreach (DI.DeviceObjectInstance obj in _device.GetObjects())
            {
                try
                {
                    if (obj.ObjectType == SharpDX.DirectInput.ObjectGuid.XAxis ||
                        obj.ObjectType == SharpDX.DirectInput.ObjectGuid.RxAxis)
                    {
                        primaryAxis = (int)obj.ObjectId;
                        break;
                    }

                    if (fallbackAxis == null &&
                        (obj.ObjectType == SharpDX.DirectInput.ObjectGuid.YAxis ||
                         obj.ObjectType == SharpDX.DirectInput.ObjectGuid.RyAxis ||
                         obj.ObjectType == SharpDX.DirectInput.ObjectGuid.ZAxis ||
                         obj.ObjectType == SharpDX.DirectInput.ObjectGuid.RzAxis))
                    {
                        fallbackAxis = (int)obj.ObjectId;
                    }
                }
                catch { }
            }

            if (primaryAxis != null)
            {
                _ffAxes = new int[] { primaryAxis.Value };
            }
            else if (fallbackAxis != null)
            {
                _ffAxes = new int[] { fallbackAxis.Value };
                ConnLog($"FFB: Using fallback axis {fallbackAxis.Value} (no XAxis/RxAxis found)");
            }
            else
            {
                _ffAxes = new int[] { 0 };
                ConnLog("FFB: No suitable axis found, using default axis 0");
            }

            ConnLog($"FFB axes resolved: [{string.Join(", ", _ffAxes)}]");
        }
        catch { }
    }

    public void DisconnectDevice()
    {
        StopInterpolationThread();
        StopEffects();
        _ledController.Disconnect();

        try
        {
            if (_device != null)
            {
                if (_isAcquired)
                    _device.Unacquire();
                _device.Dispose();
            }
        }
        catch { }

        _device = null;
        _isAcquired = false;
        _ffAxes = null;
        ConnectedDevice = null;
        DeviceDisconnected?.Invoke();
    }

    /// <summary>
    /// Sets the target force for the interpolation thread to smoothly ramp towards.
    /// The actual device command is sent by the interpolation thread at ~1kHz.
    /// </summary>
    public void SendConstantForce(float normalizedForce)
    {
        _targetForce = Math.Clamp(normalizedForce, -1f, 1f);
        System.Threading.Interlocked.Exchange(ref _lastTargetUpdateTicks, System.Diagnostics.Stopwatch.GetTimestamp());

        // If interpolation thread is not running, send directly (fallback)
        if (!_interpolationRunning)
            SendConstantForceDirect(_targetForce);
    }

    /// <summary>
    /// Sets the target vibration intensity for the interpolation thread.
    /// </summary>
    public void SetTargetVibration(float intensity)
    {
        _targetVibration = Math.Clamp(intensity, 0f, 1f);
    }

    /// <summary>
    /// Directly sends a constant force command to the device (bypasses interpolation).
    /// Used by the interpolation thread itself.
    /// </summary>
    private void SendConstantForceDirect(float normalizedForce)
    {
        if (_device == null || !_isAcquired) return;

        if (_consecutiveForceErrors >= MaxConsecutiveErrors)
            return;

        try
        {
            int magnitude = (int)(Math.Clamp(_invertForce ? -normalizedForce : normalizedForce, -1f, 1f) * _maxForceMagnitude);

            if (magnitude == _lastCfMagnitude)
                return;

            _lastCfMagnitude = magnitude;

            if (_constantForceEffect != null)
            {
                try
                {
                    var cf = new DI.ConstantForce { Magnitude = magnitude };
                    var parameters = new DI.EffectParameters();
                    parameters.Parameters = cf;

                    _constantForceEffect.SetParameters(parameters,
                        DI.EffectParameterFlags.TypeSpecificParameters |
                        DI.EffectParameterFlags.Start);
                    _consecutiveForceErrors = 0;
                    return;
                }
                catch (Exception ex)
                {
                    ConnLog($"SetParameters FAIL: {ex.InnerException?.Message ?? ex.Message}");
                    DestroyConstantForceEffect();

                    if (IsNotExclusiveError(ex))
                    {
                        TryReacquireDevice();
                        return;
                    }
                }
            }

            var allEffects = _device.GetEffects();
            if (allEffects.Count == 0)
            {
                LastError = "No FFB effects supported by device";
                return;
            }

            var cf2 = new DI.ConstantForce { Magnitude = magnitude };
            var newParams = new DI.EffectParameters
            {
                Duration = -1,
                Gain = 10000,
                SamplePeriod = 0,
                StartDelay = 0,
                TriggerButton = -1,
                TriggerRepeatInterval = 0,
                Flags = DI.EffectFlags.Cartesian | DI.EffectFlags.ObjectIds,
            };
            var newAxes = _ffAxes ?? new int[] { 0 };
            var newDirs = new int[newAxes.Length];
            newParams.SetAxes(newAxes, newDirs);
            newParams.Parameters = cf2;

            const int maxCreateAttempts = 3;
            for (int attempt = 1; attempt <= maxCreateAttempts; attempt++)
            {
                try
                {
                    _constantForceEffect = new DI.Effect(_device, DI.EffectGuid.ConstantForce, newParams);

                    _constantForceEffect.Start(-1, DI.EffectPlayFlags.NoDownload);
                    _consecutiveForceErrors = 0;
                    LastError = null;
                    if (attempt > 1)
                        ConnLog($"Effect create succeeded on attempt {attempt}");
                    return;
                }
                catch (Exception ex) when (attempt < maxCreateAttempts)
                {
                    ConnLog($"EFFECT CREATE attempt {attempt}/{maxCreateAttempts} failed: {ex.InnerException?.Message ?? ex.Message}");
                    DestroyConstantForceEffect();
                    Thread.Sleep(50);
                }
                catch (Exception ex)
                {
                    _consecutiveForceErrors++;
                    LastError = $"Create failed ({_consecutiveForceErrors}/{MaxConsecutiveErrors}): {ex.InnerException?.Message ?? ex.Message}";
                    ConnLog($"EFFECT CREATE FAIL ({_consecutiveForceErrors}) after {maxCreateAttempts} attempts: {ex.InnerException?.Message ?? ex.Message}");
                    if (_consecutiveForceErrors >= MaxConsecutiveErrors)
                    {
                        LastError = "Device lost exclusive FFB access. Disconnect and reconnect the device.";
                        ConnLog("DEVICE LOST — max consecutive errors reached");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _consecutiveForceErrors++;
            LastError = $"FFB error ({_consecutiveForceErrors}/{MaxConsecutiveErrors}): {ex.InnerException?.Message ?? ex.Message}";
            ConnLog($"FFB OUTER ERROR ({_consecutiveForceErrors}): {ex.InnerException?.Message ?? ex.Message}");
            DestroyConstantForceEffect();
            if (_consecutiveForceErrors >= MaxConsecutiveErrors)
            {
                LastError = "Device lost exclusive FFB access. Disconnect and reconnect the device.";
                ConnLog("DEVICE LOST — max consecutive errors reached");
            }
        }
    }

    public void SendPeriodicVibration(float intensity, int frequency = 80)
    {
        if (_device == null || !_isAcquired || intensity < 0.001f)
        {
            StopVibration();
            return;
        }

        try
        {
            int magnitude = (int)(Math.Clamp(intensity, 0f, 1f) * _maxForceMagnitude);

            if (_periodicEffect != null && _periodicEffectPlaying)
            {
                if (magnitude == _lastPeriodicMagnitude)
                    return;

                _lastPeriodicMagnitude = magnitude;

                try
                {
                    var periodic = new DI.PeriodicForce
                    {
                        Magnitude = magnitude,
                        Phase = 0,
                        Period = 1000 / Math.Max(frequency, 1)
                    };
                    var parameters = new DI.EffectParameters();
                    parameters.Parameters = periodic;

                    _periodicEffect.SetParameters(parameters,
                        DI.EffectParameterFlags.TypeSpecificParameters |
                        DI.EffectParameterFlags.Start);
                    return;
                }
                catch
                {
                    DestroyPeriodicEffect();
                }
            }

            var axes = _ffAxes ?? new int[] { 0 };
            var directions = new int[axes.Length];

            var newPeriodic = new DI.PeriodicForce
            {
                Magnitude = magnitude,
                Phase = 0,
                Period = 1000 / Math.Max(frequency, 1)
            };

            var newParams = new DI.EffectParameters
            {
                Duration = -1,
                Gain = 10000,
                SamplePeriod = 0,
                StartDelay = 0,
                TriggerButton = -1,
                TriggerRepeatInterval = 0,
                Flags = DI.EffectFlags.Cartesian | DI.EffectFlags.ObjectIds,
            };
            newParams.SetAxes(axes, directions);
            newParams.Parameters = newPeriodic;

            _periodicEffect = new DI.Effect(_device, DI.EffectGuid.Sine, newParams);
            _periodicEffect.Start(1, 0);
            _periodicEffectPlaying = true;
            _lastPeriodicMagnitude = magnitude;
        }
        catch
        {
            DestroyPeriodicEffect();
        }
    }

    public void ZeroForce()
    {
        _targetForce = 0f;
        _currentForce = 0f;
        _targetVibration = 0f;
        _currentVibration = 0f;
        _periodicEffectPlaying = false;
        _lastPeriodicMagnitude = int.MinValue;
        _consecutiveForceErrors = 0;
        SendConstantForceDirect(0f);
        StopVibration();
        ClearWheelLeds();
    }

    private static bool IsNotExclusiveError(Exception ex)
    {
        var msg = ex.InnerException?.Message ?? ex.Message;
        return msg.Contains("NotExclusiveAcquired", StringComparison.OrdinalIgnoreCase) ||
               msg.Contains("DIERR_NOTEXCLUSIVEACQUIRED", StringComparison.OrdinalIgnoreCase) ||
               (ex.InnerException != null && ex.InnerException.HResult == unchecked((int)0x80040205));
    }

    private void TryReacquireDevice()
    {
        if (_device == null) return;

        var savedDeviceInfo = ConnectedDevice;
        ConnLog("Attempting re-acquire after NOTEXCLUSIVEACQUIRED...");

        try
        {
            DestroyConstantForceEffect();
            DestroyPeriodicEffect();

            try { _device.Unacquire(); } catch { }
            _isAcquired = false;

            Thread.Sleep(100);

            _device.Acquire();
            _isAcquired = true;
            _consecutiveForceErrors = 0;
            LastError = null;
            ConnLog("Re-acquire SUCCESS — device recovered");
        }
        catch (Exception ex)
        {
            ConnLog($"Re-acquire FAILED: {ex.InnerException?.Message ?? ex.Message}");
            _consecutiveForceErrors++;
            if (_consecutiveForceErrors >= MaxConsecutiveErrors)
            {
                LastError = "Device lost exclusive FFB access. Disconnect and reconnect the device.";
                ConnLog("DEVICE LOST — re-acquire failed, max consecutive errors reached");
            }
        }
    }

    public void UpdateWheelLeds(float rpmPercent, bool shiftUp, bool limiter, int flag, bool absActive = false)
    {
        _ledController.UpdateLeds(rpmPercent, shiftUp, limiter, flag, absActive);
    }

    public void ClearWheelLeds()
    {
        _ledController.ClearLeds();
    }

    public bool IsLedControllerConnected => _ledController.IsConnected;
    public WheelVendor LedControllerVendor => _ledController.Vendor;
    public string LedDiagnosticInfo => _ledController.DiagnosticSummary;

    public int ButtonCount
    {
        get
        {
            if (_device == null) return 0;
            try { return _device.Capabilities.ButtonCount; }
            catch { return 0; }
        }
    }

    public List<string> GetButtonNames()
    {
        return GetButtonNamesForDevice(_device);
    }

    public List<string> GetSecondaryButtonNames()
    {
        return GetButtonNamesForDevice(_secondaryDevice);
    }

    private static List<string> GetButtonNamesForDevice(DI.Joystick? device)
    {
        if (device == null) return new List<string>();

        try
        {
            var buttonObjects = device.GetObjects()
                .Where(o => o.ObjectType == DI.ObjectGuid.Button)
                .OrderBy(o => o.ObjectId.InstanceNumber)
                .ToList();

            if (buttonObjects.Count == 0)
            {
                int count = device.Capabilities.ButtonCount;
                var fallback = new List<string>();
                for (int i = 0; i < count; i++)
                    fallback.Add($"Button {i + 1}");
                return fallback;
            }

            int maxIdx = buttonObjects.Max(o => o.ObjectId.InstanceNumber);
            var names = new string[maxIdx + 1];
            for (int i = 0; i <= maxIdx; i++)
                names[i] = $"Button {i + 1}";

            foreach (var obj in buttonObjects)
            {
                int idx = obj.ObjectId.InstanceNumber;
                if (!string.IsNullOrWhiteSpace(obj.Name))
                    names[idx] = obj.Name;
            }

            return names.ToList();
        }
        catch
        {
            return new List<string>();
        }
    }

    public bool[]? PollButtons()
    {
        if (_device == null || !_isAcquired) return null;
        try
        {
            _device.Poll();
            return _device.GetCurrentState().Buttons;
        }
        catch { return null; }
    }

    public LedEffectConfig LedConfig
    {
        get => _ledController.Config;
        set
        {
            _ledController.Config = value;
            _ledController.ApplyConfig();
        }
    }

    private void StartInterpolationThread()
    {
        if (_interpolationRunning) return;
        _interpolationRunning = true;

        _interpolationThread = new Thread(InterpolationLoop)
        {
            Name = "FFB Output Interpolation",
            IsBackground = true,
            Priority = ThreadPriority.AboveNormal
        };
        _interpolationThread.Start();
    }

    private void StopInterpolationThread()
    {
        _interpolationRunning = false;
        _interpolationThread?.Join(2000);
        _interpolationThread = null;

        if (_timerResolutionSet)
        {
            timeEndPeriod(1);
            _timerResolutionSet = false;
        }
    }

    /// <summary>
    /// Runs at ~1kHz, using time-based sliding interpolation between the current force
    /// and the target force set by the telemetry loop (333Hz). This turns stair-step
    /// force transitions into smooth ramps without overshooting on stuttered packets.
    /// </summary>
    private void InterpolationLoop()
    {
        timeBeginPeriod(1);
        _timerResolutionSet = true;

        long lastLoopTick = System.Diagnostics.Stopwatch.GetTimestamp();
        double ticksPerMs = System.Diagnostics.Stopwatch.Frequency / 1000.0;

        while (_interpolationRunning)
        {
            try
            {
                long nowTicks = System.Diagnostics.Stopwatch.GetTimestamp();

                // Time elapsed since the last physics packet set the target
                long lastUpdate = System.Threading.Interlocked.Read(ref _lastTargetUpdateTicks);
                float timeSinceLastPacket = (float)((nowTicks - lastUpdate) / ticksPerMs);

                // Time-based lerp: how far along the 3ms physics tick are we?
                // If the packet just arrived, we're at 0%. If 3ms have passed, we're at 100%.
                // Clamped to 1.0 to prevent overshooting if the physics engine hangs/stutters.
                float lerpFactor = Math.Min(timeSinceLastPacket / PhysicsTickMs, 1.0f);

                float target = _targetForce;
                float current = _currentForce;

                // Sliding lerp towards target based on elapsed time since last physics update
                float interpolated = current + (target - current) * lerpFactor;

                // Snap to target if very close to avoid endless micro-updates
                if (Math.Abs(interpolated - target) < 0.0005f)
                    interpolated = target;

                _currentForce = interpolated;
                SendConstantForceDirect(interpolated);

                // Also interpolate vibration
                float targetVib = _targetVibration;
                float currentVib = _currentVibration;
                float interpVib = currentVib + (targetVib - currentVib) * lerpFactor;
                if (Math.Abs(interpVib - targetVib) < 0.001f)
                    interpVib = targetVib;
                _currentVibration = interpVib;

                if (interpVib > 0.001f)
                    SendPeriodicVibration(interpVib);
                else
                    StopVibration();

                lastLoopTick = nowTicks;
                Thread.Sleep(1);
            }
            catch
            {
                Thread.Sleep(1);
            }
        }
    }

    public void StopVibration()
    {
        DestroyPeriodicEffect();
        _periodicEffectPlaying = false;
        _lastPeriodicMagnitude = int.MinValue;
    }

    private void DestroyConstantForceEffect()
    {
        try
        {
            if (_constantForceEffect != null)
            {
                _constantForceEffect.Stop();
                _constantForceEffect.Dispose();
            }
        }
        catch { }
        _constantForceEffect = null;
        _lastCfMagnitude = int.MinValue;
    }

    private void DestroyPeriodicEffect()
    {
        try
        {
            if (_periodicEffect != null)
            {
                _periodicEffect.Stop();
                _periodicEffect.Dispose();
            }
        }
        catch { }
        _periodicEffect = null;
    }

    private void StopEffects()
    {
        DestroyConstantForceEffect();
        DestroyPeriodicEffect();
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetDesktopWindow();

    public bool TryConnectSecondaryDevice(FfbDeviceInfo deviceInfo)
    {
        DisconnectSecondaryDevice();
        try
        {
            _directInput ??= new DI.DirectInput();
            _secondaryDevice = new DI.Joystick(_directInput, deviceInfo.DeviceInstance.InstanceGuid);
            _secondaryDevice.SetCooperativeLevel(
                _windowHandle != IntPtr.Zero ? _windowHandle : GetDesktopWindow(),
                DI.CooperativeLevel.NonExclusive | DI.CooperativeLevel.Background);
            _secondaryDevice.Acquire();
            _secondaryAcquired = true;
            return true;
        }
        catch
        {
            _secondaryDevice?.Dispose();
            _secondaryDevice = null;
            _secondaryAcquired = false;
            return false;
        }
    }

    public void DisconnectSecondaryDevice()
    {
        if (_secondaryDevice != null)
        {
            try { _secondaryDevice.Unacquire(); } catch { }
            try { _secondaryDevice.Dispose(); } catch { }
        }
        _secondaryDevice = null;
        _secondaryAcquired = false;
    }

    public int SecondaryButtonCount
    {
        get
        {
            if (_secondaryDevice == null) return 0;
            try { return _secondaryDevice.Capabilities.ButtonCount; }
            catch { return 0; }
        }
    }

    public bool[]? PollSecondaryButtons()
    {
        if (_secondaryDevice == null || !_secondaryAcquired) return null;
        try
        {
            _secondaryDevice.Poll();
            return _secondaryDevice.GetCurrentState().Buttons;
        }
        catch { return null; }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        StopInterpolationThread();
        DisconnectSecondaryDevice();
        DisconnectDevice();
        _ledController.Dispose();
        _directInput?.Dispose();
    }
}
