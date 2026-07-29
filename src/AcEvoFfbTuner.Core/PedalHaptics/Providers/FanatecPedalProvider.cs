using System.Runtime.InteropServices;

namespace AcEvoFfbTuner.Core.PedalHaptics.Providers;

public sealed class FanatecPedalProvider : IPedalHapticProvider
{
    private const string DllName = "EndorFanatecSdk64_VS2019.dll";

    private IntPtr _sdkHandle;
    private IntPtr _deviceHandle;
    private IntPtr _interfaceHandle;
    private int _brakeEffectId = -1;
    private int _gasEffectId = -1;
    private bool _disposed;

    public string DeviceName => "Fanatec Pedal Rumble";
    public bool IsAvailable => _interfaceHandle != IntPtr.Zero && !_disposed;
    public bool IsBrakeSupported => true;
    public bool IsGasSupported => true;
    public bool IsClutchSupported => false;

    public bool Initialize()
    {
        Shutdown();

        foreach (var dir in SearchPaths)
        {
            var path = Path.Combine(dir, DllName);
            if (!File.Exists(path)) continue;

            try
            {
                _sdkHandle = NativeLibrary.Load(path);
                break;
            }
            catch { }
        }

        if (_sdkHandle == IntPtr.Zero) return false;

        for (int i = 0; i < 16; i++)
        {
            if (FSEnumerateInstance2(i, out IntPtr devHandle) < 0 || devHandle == IntPtr.Zero)
                break;

            if (FSDeviceQueryInterface(out IntPtr iface, devHandle) >= 0 && iface != IntPtr.Zero)
            {
                _deviceHandle = devHandle;
                _interfaceHandle = iface;
                return true;
            }

            FSDeviceRelease(devHandle);
        }

        Shutdown();
        return false;
    }

    public void SetBrakeHaptic(float intensity, HapticSignalType signal)
    {
        if (!IsAvailable) return;
        SendTransducerEffect(ref _brakeEffectId, intensity);
    }

    public void SetGasHaptic(float intensity, HapticSignalType signal)
    {
        if (!IsAvailable) return;
        SendTransducerEffect(ref _gasEffectId, intensity);
    }

    public void SetClutchHaptic(float intensity, HapticSignalType signal) { }

    public void StopAll()
    {
        if (_interfaceHandle != IntPtr.Zero)
        {
            if (_brakeEffectId >= 0)
            {
                FSTransducerStopEffect(_interfaceHandle, _brakeEffectId);
                _brakeEffectId = -1;
            }
            if (_gasEffectId >= 0)
            {
                FSTransducerStopEffect(_interfaceHandle, _gasEffectId);
                _gasEffectId = -1;
            }
        }
    }

    private void SendTransducerEffect(ref int effectId, float intensity)
    {
        if (_interfaceHandle == IntPtr.Zero) return;

        try
        {
            if (intensity < 0.01f)
            {
                if (effectId >= 0)
                {
                    FSTransducerStopEffect(_interfaceHandle, effectId);
                    effectId = -1;
                }
                return;
            }

            var effect = new FsbTransducerEffect
            {
                Type = 0,
                Duration = 100,
                Frequency = 80,
                Magnitude = (int)Math.Clamp(intensity * 10000f, 0f, 10000f)
            };

            if (effectId >= 0)
                FSTransducerStopEffect(_interfaceHandle, effectId);

            int hr = FSTransducerDownloadEffect(_interfaceHandle, ref effect, out int newId);
            if (hr >= 0)
            {
                effectId = newId;
                FSTransducerStartEffect(_interfaceHandle, effectId);
            }
        }
        catch { }
    }

    private void Shutdown()
    {
        StopAll();

        if (_interfaceHandle != IntPtr.Zero)
        {
            FSInterfaceDestroy(_interfaceHandle);
            _interfaceHandle = IntPtr.Zero;
        }

        if (_deviceHandle != IntPtr.Zero)
        {
            FSDeviceRelease(_deviceHandle);
            _deviceHandle = IntPtr.Zero;
        }

        if (_sdkHandle != IntPtr.Zero)
        {
            NativeLibrary.Free(_sdkHandle);
            _sdkHandle = IntPtr.Zero;
        }

        _brakeEffectId = -1;
        _gasEffectId = -1;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Shutdown();
    }

    private static readonly string[] SearchPaths =
    [
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Fanatec", "Fanatec Wheel", "fw"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Fanatec", "Fanatec Wheel", "fw"),
    ];

    [StructLayout(LayoutKind.Sequential)]
    private struct FsbTransducerEffect
    {
        public int Type;
        public int Magnitude;
        public int Duration;
        public int Frequency;
    }

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    private static extern int FSEnumerateInstance2(int index, out IntPtr ppDevice);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    private static extern int FSDeviceQueryInterface(out IntPtr ppInterface, IntPtr pDevice);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    private static extern int FSDeviceRelease(IntPtr pDevice);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    private static extern int FSInterfaceDestroy(IntPtr pInterface);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    private static extern int FSTransducerDownloadEffect(IntPtr pInterface, ref FsbTransducerEffect effect, out int effectId);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    private static extern int FSTransducerStartEffect(IntPtr pInterface, int effectId);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    private static extern int FSTransducerStopEffect(IntPtr pInterface, int effectId);
}
