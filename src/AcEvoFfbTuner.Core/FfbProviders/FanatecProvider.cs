using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using AcEvoFfbTuner.Core.DirectInput;

namespace AcEvoFfbTuner.Core.FfbProviders;

public sealed class FanatecProvider : IFFBProvider
{
    private const string MauriceProcessName = "FWPnpService";
    private const int FullForceSampleRate = 500;
    private const int FullForceRingSeconds = 2;
    private const int FullForceBufferSize = FullForceSampleRate * FullForceRingSeconds;

    private const float TorqueOutputFloor = 0.001f;

    private readonly FfbDeviceManager _deviceManager;
    private readonly float[] _fullForceBuffer = new float[FullForceBufferSize];
    private readonly object _bufferLock = new();

    private IntPtr _sdkHandle;
    private IntPtr _deviceHandle;
    private IntPtr _interfaceHandle;
    private int _bufferWritePos;
    private bool _fullForceActive;
    private bool _disposed;

    private int _maxTorqueNm = -1;
    private float _torqueScale = 1.0f;
    private int _lastGearDisplayed = -999;
    private bool _absRumbleActive;

    private Stopwatch? _sampleStopwatch;
    private float _lastSampleValue;

    public string ProviderName
    {
        get
        {
            if (_sdkHandle != IntPtr.Zero && _interfaceHandle != IntPtr.Zero)
                return _mauriceRunning
                    ? "Fanatec SDK (via FWPnpService)"
                    : "Fanatec SDK (Direct)";
            return "Fanatec (DirectInput fallback)";
        }
    }

    public bool IsInitialized { get; private set; }
    public bool IsAvailable =>
        (_interfaceHandle != IntPtr.Zero) || _deviceManager.IsDeviceAcquired;

    public bool IsFullForceAvailable { get; private set; }
    public bool IsMauriceDetected => _mauriceRunning;

    public bool HasRimRevLeds { get; private set; }
    public bool HasRimLedDisplay { get; private set; }
    public bool HasRumbleMotors { get; private set; }
    public bool IsDirectDrive { get; private set; }

    public int MaxTorqueNm => _maxTorqueNm;
    public float TorqueScale => _torqueScale;

    public string? BaseProductName { get; private set; }
    public string? RimProductName { get; private set; }
    public string? LastError { get; private set; }

    private bool _mauriceRunning;

    public FanatecProvider(FfbDeviceManager deviceManager)
    {
        _deviceManager = deviceManager;
    }

    public bool Initialize()
    {
        CheckMaurice();

        _sdkHandle = FanatecSdkNative.TryLoad();
        if (_sdkHandle == IntPtr.Zero)
        {
            LastError = "EndorFanatecSdk64_VS2019.dll not found — falling back to DirectInput";
            Console.WriteLine($"[FanatecProvider] {LastError}");
            IsInitialized = _deviceManager.IsDeviceAcquired;
            return IsInitialized;
        }

        Console.WriteLine("[FanatecProvider] SDK DLL loaded successfully");

        if (!EnumerateAndOpenDevice())
        {
            LastError = "No Fanatec device enumerated — falling back to DirectInput";
            Console.WriteLine($"[FanatecProvider] {LastError}");
            IsInitialized = _deviceManager.IsDeviceAcquired;
            return IsInitialized;
        }

        QueryCapabilities();
        QueryMaxTorque();
        InitFullForce();

        if (_mauriceRunning)
        {
            Console.WriteLine("[FanatecProvider] FWPnpService.exe detected (Maurice) — " +
                              "FFB routed through service; HID direct-write avoided");
            Log("Maurice detected: FWPnpService.exe running. Torque via DirectInput is safe " +
                "because FWPnpService owns the HID filter, not the DirectInput path.");
        }
        else
        {
            Console.WriteLine("[FanatecProvider] FWPnpService.exe not found — Direct path");
        }

        IsInitialized = true;
        return true;
    }

    public void UpdateTorque(float signal)
    {
        if (!IsAvailable) return;

        float clamped = Math.Clamp(signal, -1f, 1f);

        if (_torqueScale < 1.0f)
            clamped *= _torqueScale;

        if (Math.Abs(clamped) < TorqueOutputFloor)
            clamped = 0f;

        _deviceManager.SendConstantForce(clamped);
    }

    public void SetHaptics(HapticData data)
    {
        if (!IsAvailable) return;

        _deviceManager.SetTargetVibration(Math.Clamp(data.VibrationIntensity, 0f, 1f));

        if (_fullForceActive && _interfaceHandle != IntPtr.Zero)
        {
            float scaled = ScaleHighPassForFullForce(data.VibrationIntensity);
            WriteFullForceSample(scaled);
        }
    }

    public void WriteFullForceSample(float sample)
    {
        if (!_fullForceActive || _interfaceHandle == IntPtr.Zero) return;

        _sampleStopwatch ??= Stopwatch.StartNew();

        float clampedSample = Math.Clamp(sample, -1f, 1f);
        _lastSampleValue = clampedSample;

        double elapsedMs = _sampleStopwatch.Elapsed.TotalMilliseconds;
        double expectedSamplesPerTick = (FullForceSampleRate / 1000.0) * elapsedMs;
        int samplesToWrite = Math.Max(1, (int)expectedSamplesPerTick);

        _sampleStopwatch.Restart();

        lock (_bufferLock)
        {
            for (int i = 0; i < samplesToWrite; i++)
            {
                _fullForceBuffer[_bufferWritePos] = clampedSample;
                _bufferWritePos = (_bufferWritePos + 1) % FullForceBufferSize;
            }
        }

        if (_bufferWritePos == 0)
            FlushFullForceBuffer();
    }

    public void EnableFullForce(bool enable)
    {
        if (_interfaceHandle == IntPtr.Zero)
        {
            Console.WriteLine("[FanatecProvider] FullForce unavailable — no SDK interface");
            return;
        }

        if (enable && !_fullForceActive)
        {
            int hr = FanatecSdkNative.FSFfSetSampleRate(_interfaceHandle, FullForceSampleRate);
            if (hr < 0)
            {
                LastError = $"FSFfSetSampleRate({FullForceSampleRate}) failed: 0x{hr:X8}";
                Console.WriteLine($"[FanatecProvider] {LastError}");
                return;
            }

            hr = FanatecSdkNative.FSFfSetSampleBufferSize(_interfaceHandle, FullForceBufferSize);
            if (hr < 0)
            {
                LastError = $"FSFfSetSampleBufferSize failed: 0x{hr:X8}";
                Console.WriteLine($"[FanatecProvider] {LastError}");
                return;
            }

            hr = FanatecSdkNative.FSFfSamplePlayStart1(_interfaceHandle);
            if (hr < 0)
            {
                LastError = $"FSFfSamplePlayStart1 failed: 0x{hr:X8}";
                Console.WriteLine($"[FanatecProvider] {LastError}");
                return;
            }

            lock (_bufferLock)
            {
                Array.Clear(_fullForceBuffer);
                _bufferWritePos = 0;
            }

            _sampleStopwatch = null;
            IsFullForceAvailable = true;
            Console.WriteLine($"[FanatecProvider] FullForce started — {FullForceSampleRate}Hz, " +
                              $"buffer {FullForceBufferSize} samples");
        }
        else if (!enable && _fullForceActive)
        {
            if (_interfaceHandle != IntPtr.Zero)
            {
                FanatecSdkNative.FSFfSamplePlayStop(_interfaceHandle);
            }

            IsFullForceAvailable = false;
            _sampleStopwatch = null;
            Console.WriteLine("[FanatecProvider] FullForce stopped");
        }

        _fullForceActive = enable;
    }

    public void UpdateLeds(int rpmPercent, bool absActive)
    {
        if (!IsInitialized || _interfaceHandle == IntPtr.Zero) return;

        if (rpmPercent < 0 || rpmPercent > 100) return;

        if (!HasRimRevLeds)
        {
            CheckBaseRevLeds();
            if (!HasRimRevLeds) return;
        }

        FanatecSdkNative.FSLedButtonsRevLedModeEnable(_interfaceHandle, true);

        int litSegments = rpmPercent switch
        {
            >= 97 => 9,
            >= 92 => 8,
            >= 86 => 7,
            >= 80 => 6,
            >= 72 => 5,
            >= 63 => 4,
            >= 53 => 3,
            >= 40 => 2,
            >= 25 => 1,
            _ => 0
        };

        byte r, g, b;
        if (absActive)
        {
            r = 0; g = 60; b = 255;
        }
        else if (rpmPercent >= 97)
        {
            r = 255; g = 0; b = 0;
        }
        else if (rpmPercent >= 86)
        {
            r = 255; g = 120; b = 0;
        }
        else
        {
            r = 0; g = 220; b = 0;
        }

        for (int i = 0; i < litSegments; i++)
        {
            FanatecSdkNative.FSLedItemColorSet1(_interfaceHandle, i, r, g, b);
        }

        for (int i = litSegments; i < 9; i++)
        {
            FanatecSdkNative.FSLedItemColorSet1(_interfaceHandle, i, (byte)0, (byte)0, (byte)0);
        }

        FanatecSdkNative.FSItemSubmitToDevice(_interfaceHandle);
    }

    public void TriggerAbsRumble(bool active)
    {
        if (!IsInitialized || _interfaceHandle == IntPtr.Zero) return;
        if (!HasRumbleMotors) return;
        if (_absRumbleActive == active) return;

        _absRumbleActive = active;

        int hr = FanatecSdkNative.FSRumbleSetOn(_interfaceHandle, active);
        if (hr < 0)
        {
            Log($"FSRumbleSetOn({active}) returned 0x{hr:X8}");
        }
    }

    public void UpdateDisplayGear(int gear)
    {
        if (!IsInitialized || _interfaceHandle == IntPtr.Zero) return;
        if (!HasRimLedDisplay) return;
        if (gear == _lastGearDisplayed) return;

        _lastGearDisplayed = gear;

        int displayValue = gear switch
        {
            > 0 => gear,
            0 => 0,
            -1 => 88,
            _ => gear
        };

        int hr = FanatecSdkNative.FSLedSetNumber(_interfaceHandle, displayValue);
        if (hr < 0)
        {
            Log($"FSLedSetNumber({displayValue}) returned 0x{hr:X8}");
        }
    }

    public void ZeroTorque()
    {
        if (_deviceManager.IsDeviceAcquired)
        {
            _deviceManager.SendConstantForce(0f);
            _deviceManager.SetTargetVibration(0f);
        }

        if (_interfaceHandle != IntPtr.Zero)
        {
            FanatecSdkNative.FSWheelStopEffects(_interfaceHandle);
            FanatecSdkNative.FSRumbleSetOn(_interfaceHandle, false);
        }

        _absRumbleActive = false;

        lock (_bufferLock)
        {
            Array.Clear(_fullForceBuffer);
            _bufferWritePos = 0;
        }
    }

    public void Shutdown()
    {
        EnableFullForce(false);
        ZeroTorque();

        if (_interfaceHandle != IntPtr.Zero)
        {
            FanatecSdkNative.FSInterfaceDestroy(_interfaceHandle);
            _interfaceHandle = IntPtr.Zero;
        }

        if (_deviceHandle != IntPtr.Zero)
        {
            FanatecSdkNative.FSDeviceRelease(_deviceHandle);
            _deviceHandle = IntPtr.Zero;
        }

        if (_sdkHandle != IntPtr.Zero)
        {
            NativeLibrary.Free(_sdkHandle);
            _sdkHandle = IntPtr.Zero;
        }

        IsInitialized = false;
        IsFullForceAvailable = false;
        HasRimRevLeds = false;
        HasRimLedDisplay = false;
        HasRumbleMotors = false;
        IsDirectDrive = false;
        BaseProductName = null;
        RimProductName = null;
        _maxTorqueNm = -1;
        _torqueScale = 1.0f;
        _lastGearDisplayed = -999;
        _absRumbleActive = false;
        _sampleStopwatch = null;
    }

    public void CheckMaurice()
    {
        try
        {
            _mauriceRunning = Process.GetProcessesByName(MauriceProcessName).Length > 0;
        }
        catch
        {
            _mauriceRunning = false;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Shutdown();
    }

    private bool EnumerateAndOpenDevice()
    {
        for (int i = 0; i < 16; i++)
        {
            int hr = FanatecSdkNative.FSEnumerateInstance2(i, out IntPtr devHandle);
            if (hr < 0 || devHandle == IntPtr.Zero)
                break;

            int qr = FanatecSdkNative.FSDeviceQueryInterface(out IntPtr iface, devHandle);
            if (qr >= 0 && iface != IntPtr.Zero)
            {
                _deviceHandle = devHandle;
                _interfaceHandle = iface;
                Log($"Device opened at index {i}");
                return true;
            }

            FanatecSdkNative.FSDeviceRelease(devHandle);
        }

        return false;
    }

    private void QueryCapabilities()
    {
        if (_interfaceHandle == IntPtr.Zero) return;

        var nameBuf = new StringBuilder(256);

        FanatecSdkNative.FSUtilWheelBaseProductNameGet(_interfaceHandle, nameBuf, 256);
        BaseProductName = nameBuf.ToString();
        nameBuf.Clear();

        FanatecSdkNative.FSUtilWheelRimProductNameGet(_interfaceHandle, nameBuf, 256);
        RimProductName = nameBuf.ToString();

        FanatecSdkNative.FSUtilHasWheelRimRevLeds(_interfaceHandle, out bool revLeds);
        HasRimRevLeds = revLeds;

        FanatecSdkNative.FSUtilHasWheelRimLedDisplay(_interfaceHandle, out bool display);
        HasRimLedDisplay = display;

        FanatecSdkNative.FSUtilHasWheelRimRumbleMotors(_interfaceHandle, out bool rumble);
        HasRumbleMotors = rumble;

        FanatecSdkNative.FSUtilIsWheelBaseDirectDrive(_interfaceHandle, out bool dd);
        IsDirectDrive = dd;

        Console.WriteLine($"[FanatecProvider] Base: {BaseProductName}, Rim: {RimProductName}, " +
                          $"DD: {IsDirectDrive}, RevLEDs: {HasRimRevLeds}, Display: {HasRimLedDisplay}, " +
                          $"Rumble: {HasRumbleMotors}");
        Log($"Capabilities — Base: {BaseProductName}, Rim: {RimProductName}, DD: {IsDirectDrive}, " +
            $"RevLEDs: {HasRimRevLeds}, Display: {HasRimLedDisplay}, Rumble: {HasRumbleMotors}");
    }

    private void QueryMaxTorque()
    {
        if (_interfaceHandle == IntPtr.Zero) return;

        int hr = FanatecSdkNative.FSWheelMaxTorqueGet(_interfaceHandle, out int maxTorque);
        if (hr < 0 || maxTorque <= 0)
        {
            Log($"FSWheelMaxTorqueGet returned 0x{hr:X8}, value={maxTorque} — using default 1.0 scale");
            _maxTorqueNm = -1;
            _torqueScale = 1.0f;
            return;
        }

        _maxTorqueNm = maxTorque;

        const float referenceNm = 25.0f;

        _torqueScale = Math.Clamp((float)maxTorque / referenceNm, 0.1f, 1.0f);

        Console.WriteLine($"[FanatecProvider] Max torque: {maxTorque}Nm, " +
                          $"torque scale: {_torqueScale:F3} (clamped to {maxTorque}Nm ceiling)");
        Log($"Torque safety — Max: {maxTorque}Nm, Scale: {_torqueScale:F3} " +
            $"(prevents >{maxTorque}Nm output on this base)");
    }

    private void InitFullForce()
    {
        if (_interfaceHandle == IntPtr.Zero) return;

        int hr = FanatecSdkNative.FSFfSetSampleRate(_interfaceHandle, FullForceSampleRate);
        if (hr < 0)
        {
            Log($"FSFfSetSampleRate({FullForceSampleRate}) returned 0x{hr:X8} — FullForce may not be supported");
            return;
        }

        hr = FanatecSdkNative.FSFfSetSampleBufferSize(_interfaceHandle, FullForceBufferSize);
        if (hr < 0)
        {
            Log($"FSFfSetSampleBufferSize returned 0x{hr:X8}");
            return;
        }

        IsFullForceAvailable = true;
        Log($"FullForce ready — {FullForceSampleRate}Hz, buffer {FullForceBufferSize}");
    }

    private void FlushFullForceBuffer()
    {
        if (_interfaceHandle == IntPtr.Zero) return;

        lock (_bufferLock)
        {
            float[] snapshot = new float[FullForceBufferSize];
            Array.Copy(_fullForceBuffer, snapshot, FullForceBufferSize);
            FanatecSdkNative.FSFfAppendSamples(_interfaceHandle, snapshot, FullForceBufferSize);
        }
    }

    private void CheckBaseRevLeds()
    {
        if (_interfaceHandle == IntPtr.Zero) return;
        FanatecSdkNative.FSUtilHasWheelBaseRevLeds(_interfaceHandle, out bool baseRevLeds);
        HasRimRevLeds = baseRevLeds;
    }

    private static float ScaleHighPassForFullForce(float vibrationIntensity)
    {
        float scaled = vibrationIntensity * 2.0f - 1.0f;
        return Math.Clamp(scaled, -1f, 1f);
    }

    private static void Log(string msg)
    {
        try
        {
            string logPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "AcEvoFfbTuner", "fanatec_provider.log");
            Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
            File.AppendAllText(logPath, $"[{DateTime.Now:HH:mm:ss.fff}] {msg}\n");
        }
        catch { }
    }
}
