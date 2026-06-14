using System.Runtime.InteropServices;
using AcEvoFfbTuner.Core.DirectInput;

namespace AcEvoFfbTuner.Core.FfbProviders;

public sealed class CammusProvider : IFFBProvider
{
    private const string CammusProductMatch = "CAMMUS";
    private const ushort CammusVid = 0x3480;
    private const int MinTorqueUpdateIntervalMs = 1;
    private const int KeepAliveTimeoutMs = 50;

    private readonly FfbDeviceManager _deviceManager;
    private IntPtr _hidHandle = InvalidHandle;
    private uint _maxReportLen;
    private long _lastTorqueTicks;
    private int _lastTorqueRaw;
    private bool _disposed;

    public string ProviderName => _hidHandle != InvalidHandle
        ? "CAMMUS (HID Direct)"
        : "CAMMUS (DirectInput fallback)";

    public bool IsInitialized { get; private set; }
    public bool IsAvailable => _hidHandle != InvalidHandle || _deviceManager.IsDeviceAcquired;
    public string? LastError { get; private set; }

    public CammusProvider(FfbDeviceManager deviceManager)
    {
        _deviceManager = deviceManager;
    }

    #region Native HID (mirrors WheelLedController pattern)

    private static readonly Guid HidGuid = new(0x4D1E55B2, 0xF16F, 0x11CF, 0x88, 0xCB, 0x00, 0x11, 0x11, 0x00, 0x00, 0x30);

    private const uint DIGCF_PRESENT = 0x02;
    private const uint DIGCF_DEVICEINTERFACE = 0x10;
    private const uint GENERIC_WRITE = 0x40000000;
    private const uint GENERIC_READ = 0x80000000;
    private const uint FILE_SHARE_RW = 0x03;
    private const uint OPEN_EXISTING = 3;

    [DllImport("setupapi.dll", CharSet = CharSet.Auto)]
    private static extern IntPtr SetupDiGetClassDevs(ref Guid classGuid, IntPtr enumerator, IntPtr hwndParent, uint flags);

    [DllImport("setupapi.dll", CharSet = CharSet.Auto)]
    private static extern bool SetupDiEnumDeviceInterfaces(IntPtr handle, IntPtr devInfo, ref Guid ifaceGuid, uint index, ref SpDeviceInterfaceData data);

    [DllImport("setupapi.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr handle, ref SpDeviceInterfaceData ifaceData, IntPtr detailBuf, int detailSize, out int requiredSize, IntPtr devInfo);

    [DllImport("setupapi.dll")]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr handle);

    [DllImport("hid.dll")]
    private static extern bool HidD_GetPreparsedData(IntPtr dev, out IntPtr preparsed);

    [DllImport("hid.dll")]
    private static extern bool HidP_GetCaps(IntPtr preparsed, out HidPCaps caps);

    [DllImport("hid.dll")]
    private static extern bool HidD_FreePreparsedData(IntPtr preparsed);

    [DllImport("hid.dll")]
    private static extern bool HidD_GetAttributes(IntPtr dev, out HidDAttributes attrs);

    [DllImport("hid.dll")]
    private static extern bool HidD_SetOutputReport(IntPtr dev, byte[] buf, int len);

    [DllImport("hid.dll", CharSet = CharSet.Auto)]
    private static extern bool HidD_GetProductString(IntPtr dev, byte[] buf, int len);

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr CreateFile(string path, uint access, uint share, IntPtr sec, uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr h);

    private static readonly IntPtr InvalidHandle = new(-1);

    [StructLayout(LayoutKind.Sequential)]
    private struct SpDeviceInterfaceData
    {
        public int cbSize;
        public Guid ClassGuid;
        public int Flags;
        public IntPtr Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HidDAttributes
    {
        public int Size;
        public ushort Vid;
        public ushort Pid;
        public short Version;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HidPCaps
    {
        public ushort Usage;
        public ushort UsagePage;
        public ushort InputReportByteLength;
        public ushort OutputReportByteLength;
        public ushort FeatureReportByteLength;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)]
        public ushort[] Reserved;
        public ushort NumberLinkCollectionNodes;
        public ushort NumberInputButtonCaps;
        public ushort NumberInputValueCaps;
        public ushort NumberInputDataIndices;
        public ushort NumberOutputButtonCaps;
        public ushort NumberOutputValueCaps;
        public ushort NumberOutputDataIndices;
        public ushort NumberFeatureButtonCaps;
        public ushort NumberFeatureValueCaps;
        public ushort NumberFeatureDataIndices;
    }

    #endregion

    public bool Initialize()
    {
        if (IsInitialized) return true;

        if (OpenCammusHid())
        {
            Log($"CAMMUS HID opened successfully (report len={_maxReportLen})");
            IsInitialized = true;
            return true;
        }

        LastError = "Could not open CAMMUS HID device. Falling back to DirectInput.";
        Log(LastError);

        IsInitialized = _deviceManager.IsDeviceAcquired;
        return IsInitialized;
    }

    public void UpdateTorque(float signal)
    {
        if (_disposed) return;

        if (_hidHandle != InvalidHandle)
        {
            int raw = (int)(Math.Clamp(signal, -1f, 1f) * 32767f);
            raw = Math.Clamp(raw, -32767, 32767);

            long now = DateTime.UtcNow.Ticks;
            long elapsedMs = (now - _lastTorqueTicks) / TimeSpan.TicksPerMillisecond;

            if (raw == _lastTorqueRaw && elapsedMs < KeepAliveTimeoutMs)
                return;

            _lastTorqueRaw = raw;
            _lastTorqueTicks = now;

            SendHidTorque(raw);
            return;
        }

        _deviceManager.SendConstantForce(Math.Clamp(signal, -1f, 1f));
    }

    public void SetHaptics(HapticData data)
    {
        if (_disposed) return;
        _deviceManager.SetTargetVibration(Math.Clamp(data.VibrationIntensity, 0f, 1f));
        _deviceManager.SendPeriodicVibration(data.VibrationIntensity, data.VibrationFrequencyHz);
    }

    public void ZeroTorque()
    {
        if (_hidHandle != InvalidHandle)
        {
            SendHidTorque(0);
            _lastTorqueRaw = 0;
        }
        _deviceManager.SendConstantForce(0f);
        _deviceManager.SetTargetVibration(0f);
    }

    public void Shutdown()
    {
        if (_disposed) return;

        if (_hidHandle != InvalidHandle)
        {
            SendHidTorque(0);
            CloseHandle(_hidHandle);
            _hidHandle = InvalidHandle;
            Log("CAMMUS HID handle closed");
        }
        IsInitialized = false;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Shutdown();
    }

    private bool OpenCammusHid()
    {
        Log("Scanning for CAMMUS C5 HID device...");
        var hidGuid = HidGuid;
        var invalidHandle = InvalidHandle;
        IntPtr hInfo = SetupDiGetClassDevs(ref hidGuid, IntPtr.Zero, IntPtr.Zero, DIGCF_PRESENT | DIGCF_DEVICEINTERFACE);
        if (hInfo == invalidHandle)
        {
            Log("  HID enumeration failed");
            return false;
        }

        try
        {
            uint i = 0;
            var iface = new SpDeviceInterfaceData { cbSize = Marshal.SizeOf<SpDeviceInterfaceData>() };

            while (SetupDiEnumDeviceInterfaces(hInfo, IntPtr.Zero, ref hidGuid, i++, ref iface))
            {
                SetupDiGetDeviceInterfaceDetail(hInfo, ref iface, IntPtr.Zero, 0, out int size, IntPtr.Zero);
                IntPtr buf = Marshal.AllocHGlobal(size);
                try
                {
                    Marshal.WriteInt32(buf, IntPtr.Size == 8 ? 8 : 6);
                    if (!SetupDiGetDeviceInterfaceDetail(hInfo, ref iface, buf, size, out _, IntPtr.Zero))
                        continue;

                    string path = Marshal.PtrToStringUni(buf + 4)!;

                    IntPtr hDev = CreateFile(path, GENERIC_READ | GENERIC_WRITE, FILE_SHARE_RW, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
                    if (hDev == invalidHandle)
                    {
                        hDev = CreateFile(path, GENERIC_WRITE, FILE_SHARE_RW, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
                    }
                    if (hDev == invalidHandle)
                        continue;

                    if (!HidD_GetAttributes(hDev, out var attr))
                    {
                        CloseHandle(hDev);
                        continue;
                    }

                    if (attr.Vid != CammusVid)
                    {
                        CloseHandle(hDev);
                        continue;
                    }

                    byte[] productBuf = new byte[256];
                    string product = "";
                    if (HidD_GetProductString(hDev, productBuf, productBuf.Length))
                    {
                        product = System.Text.Encoding.Unicode.GetString(productBuf).TrimEnd('\0');
                        if (!product.ToUpperInvariant().Contains(CammusProductMatch))
                        {
                            CloseHandle(hDev);
                            continue;
                        }
                    }

                    if (!HidD_GetPreparsedData(hDev, out IntPtr pp))
                    {
                        CloseHandle(hDev);
                        continue;
                    }
                    if (!HidP_GetCaps(pp, out var caps))
                    {
                        HidD_FreePreparsedData(pp);
                        CloseHandle(hDev);
                        continue;
                    }
                    HidD_FreePreparsedData(pp);

                    if (caps.OutputReportByteLength < 2)
                    {
                        CloseHandle(hDev);
                        continue;
                    }

                    _hidHandle = hDev;
                    _maxReportLen = caps.OutputReportByteLength;
                    Log($"  Found CAMMUS HID: VID=0x{attr.Vid:X4} PID=0x{attr.Pid:X4} OutLen={caps.OutputReportByteLength} Product='{product}'");
                    return true;
                }
                finally
                {
                    Marshal.FreeHGlobal(buf);
                }
            }
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(hInfo);
        }

        Log("  No CAMMUS HID device found");
        return false;
    }

    private void SendHidTorque(int torque)
    {
        if (_hidHandle == InvalidHandle) return;

        try
        {
            byte[] report = new byte[_maxReportLen];
            report[0] = 0;

            int clamped = Math.Clamp(torque, -32767, 32767);
            ushort torqueBits = clamped < 0
                ? (ushort)((~(-clamped) + 1) & 0xFFFF)
                : (ushort)(clamped & 0xFFFF);

            if (_maxReportLen >= 6)
            {
                report[4] = (byte)(torqueBits & 0xFF);
                report[5] = (byte)((torqueBits >> 8) & 0xFF);
            }
            else if (_maxReportLen >= 4)
            {
                report[2] = (byte)(torqueBits & 0xFF);
                report[3] = (byte)((torqueBits >> 8) & 0xFF);
            }
            else if (_maxReportLen >= 3)
            {
                report[1] = (byte)(torqueBits & 0xFF);
                report[2] = (byte)((torqueBits >> 8) & 0xFF);
            }
            else
            {
                report[1] = (byte)(torqueBits & 0xFF);
            }

            HidD_SetOutputReport(_hidHandle, report, (int)_maxReportLen);
        }
        catch (Exception ex)
        {
            Log($"HID write error: {ex.Message}");
        }
    }

    private static void Log(string msg)
    {
        try
        {
            string logPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "AcEvoFfbTuner", "cammus_provider.log");
            Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
            File.AppendAllText(logPath, $"[{DateTime.Now:HH:mm:ss.fff}] {msg}\n");
        }
        catch { }
    }
}
