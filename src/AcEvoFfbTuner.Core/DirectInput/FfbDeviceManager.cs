using System.Runtime.InteropServices;
using DI = SharpDX.DirectInput;

namespace AcEvoFfbTuner.Core.DirectInput;

public sealed class FfbDeviceInfo
{
    public string ProductName { get; init; } = string.Empty;
    public bool IsFfbCapable { get; init; }
    public DI.DeviceInstance DeviceInstance { get; init; } = null!;
}

public sealed class FfbDeviceManager : IDisposable
{
    [DllImport("winmm.dll")]
    private static extern uint timeBeginPeriod(uint period);

    [DllImport("winmm.dll")]
    private static extern uint timeEndPeriod(uint period);

    private DI.DirectInput? _directInput;
    private DI.Joystick? _device;
    private DI.Effect? _constantForceEffect;
    private DI.Effect? _periodicEffect;
    private bool _isAcquired;
    private int _maxForceMagnitude = 10000;
    private bool _disposed;
    private int _lastCfMagnitude = int.MinValue;
    private IntPtr _windowHandle;
    private int[]? _ffAxes;
    private bool _invertForce = true; // Most DD wheels (Moza, Fanatec) need device-level inversion

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

    public FfbDeviceInfo? ConnectedDevice { get; private set; }
    public bool IsDeviceAcquired => _isAcquired && _device != null;
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
        _directInput ??= new DI.DirectInput();

        var devices = new List<FfbDeviceInfo>();

        foreach (var instance in _directInput.GetDevices(DI.DeviceClass.GameControl, DI.DeviceEnumerationFlags.AllDevices))
        {
            bool isFfb = false;
            try
            {
                using var tempDev = new DI.Joystick(_directInput, instance.InstanceGuid);
                isFfb = tempDev.GetEffects().Count > 0;
            }
            catch { }

            var info = new FfbDeviceInfo
            {
                ProductName = instance.ProductName,
                IsFfbCapable = isFfb,
                DeviceInstance = instance
            };
            devices.Add(info);
        }

        return devices;
    }

    public bool TryConnectDevice(FfbDeviceInfo deviceInfo)
    {
        DisconnectDevice();
        LastError = null;

        try
        {
            _device = new DI.Joystick(_directInput!, deviceInfo.DeviceInstance.InstanceGuid);

            _device.SetCooperativeLevel(
                _windowHandle != IntPtr.Zero ? _windowHandle : GetDesktopWindow(),
                DI.CooperativeLevel.Exclusive | DI.CooperativeLevel.Background);
            _device.Acquire();
            _isAcquired = true;

            DiscoverFfAxes();
            ConnectedDevice = deviceInfo;
            StartInterpolationThread();

            if (!_ledController.TryConnect(deviceInfo.ProductName))
            {
                LastError = string.IsNullOrEmpty(LastError)
                    ? _ledController.LastError
                    : $"{LastError} | {_ledController.LastError}";
            }

            DeviceConnected?.Invoke(deviceInfo.ProductName);
            return true;
        }

        catch (Exception ex)
        {
            LastError = $"Exclusive failed: {ex.Message}. Trying non-exclusive...";

            try
            {
                _device?.Dispose();
                _device = new DI.Joystick(_directInput!, deviceInfo.DeviceInstance.InstanceGuid);
                _device.SetCooperativeLevel(
                    _windowHandle != IntPtr.Zero ? _windowHandle : GetDesktopWindow(),
                    DI.CooperativeLevel.NonExclusive | DI.CooperativeLevel.Background);
                _device.Acquire();
                _isAcquired = true;

                DiscoverFfAxes();
                ConnectedDevice = deviceInfo;
                StartInterpolationThread();

                if (!_ledController.TryConnect(deviceInfo.ProductName))
                {
                    LastError = string.IsNullOrEmpty(LastError)
                        ? _ledController.LastError
                        : $"{LastError} | {_ledController.LastError}";
                }

                DeviceConnected?.Invoke(deviceInfo.ProductName);
                LastError = "Connected non-exclusive — FFB may not work. Disable in-game FFB for best results.";
                return true;
            }
            catch (Exception ex2)
            {
                LastError = $"Failed to connect: {ex2.Message}";
                DisconnectDevice();
                return false;
            }
        }
    }

    private void DiscoverFfAxes()
    {
        _ffAxes = new int[] { 0 };

        if (_device == null) return;

        try
        {
            var effects = _device.GetEffects();
            if (effects.Count == 0) return;

            foreach (var obj in _device.GetObjects())
            {
                try
                {
                    if (obj.ObjectType == SharpDX.DirectInput.ObjectGuid.XAxis ||
                        obj.ObjectType == SharpDX.DirectInput.ObjectGuid.RxAxis)
                    {
                        _ffAxes = new int[] { (int)obj.ObjectId };
                        break;
                    }
                }
                catch { }
            }
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
                    return;
                }
                catch
                {
                    DestroyConstantForceEffect();
                }
            }

            var allEffects = _device.GetEffects();
            if (allEffects.Count == 0)
            {
                LastError = $"No FFB effects";
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

            try
            {
                _constantForceEffect = new DI.Effect(_device, DI.EffectGuid.ConstantForce, newParams);
            }
            catch (Exception ex)
            {
                LastError = $"Create failed: {ex.InnerException?.Message ?? ex.Message}";
                return;
            }

            try
            {
                _constantForceEffect.Start(-1, DI.EffectPlayFlags.NoDownload);
                LastError = null;
            }
            catch (Exception ex)
            {
                LastError = $"Start failed: {ex.InnerException?.Message ?? ex.Message}";
                DestroyConstantForceEffect();
            }
        }
        catch (Exception ex)
        {
            LastError = $"FFB: {ex.InnerException?.Message ?? ex.Message}";
            DestroyConstantForceEffect();
        }
    }

    public void SendPeriodicVibration(float intensity, int frequency = 80)
    {
        if (_device == null || !_isAcquired || intensity < 0.001f) return;

        try
        {
            DestroyPeriodicEffect();

            int magnitude = (int)(Math.Clamp(intensity, 0f, 1f) * _maxForceMagnitude);

            var axes = _ffAxes ?? new int[] { 0 };
            var directions = new int[axes.Length];

            var periodic = new DI.PeriodicForce
            {
                Magnitude = magnitude,
                Phase = 0,
                Period = 1000 / Math.Max(frequency, 1)
            };

            var parameters = new DI.EffectParameters
            {
                Duration = -1,
                Gain = 10000,
                SamplePeriod = 0,
                StartDelay = 0,
                TriggerButton = -1,
                TriggerRepeatInterval = 0,
                Flags = DI.EffectFlags.Cartesian | DI.EffectFlags.ObjectIds,
            };
            parameters.SetAxes(axes, directions);
            parameters.Parameters = periodic;

            _periodicEffect = new DI.Effect(_device, DI.EffectGuid.Sine, parameters);
            _periodicEffect.Start(1, 0);
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
        SendConstantForceDirect(0f);
        StopVibration();
        ClearWheelLeds();
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

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        StopInterpolationThread();
        DisconnectDevice();
        _ledController.Dispose();
        _directInput?.Dispose();
    }
}
