using System.Runtime.InteropServices;
using System.Text;

namespace AcEvoFfbTuner.Core.DirectInput;

public enum WheelVendor
{
    Unknown,
    Fanatec,
    Moza,
    Thrustmaster,
    Simagic,
    Logitech,
    Simucube
}

public sealed class WheelLedController : IDisposable
{
    #region Native Interop

    private static readonly Guid HidGuidValue = new(0x4D1E55B2, 0xF16F, 0x11CF, 0x88, 0xCB, 0x00, 0x11, 0x11, 0x00, 0x00, 0x30);
    private static readonly Guid UsbSerialGuid = new(0xA5DCBF10, 0x6530, 0x11D2, 0x90, 0x1F, 0x00, 0xC0, 0x4F, 0xB9, 0x51, 0xED);
    private static readonly Guid PortsClassGuid = new(0x4D36E978, 0xE325, 0x11CE, 0xBF, 0xC1, 0x08, 0x00, 0x2B, 0xE1, 0x03, 0x18);
    private static readonly Guid ComPortInterfaceGuid = new(0x86E0D1E0, 0x8089, 0x11D0, 0x9C, 0xE4, 0x08, 0x00, 0x3E, 0x30, 0x1F, 0x73);

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
    private static extern bool HidD_FreePreparsedData(ref IntPtr preparsed);

    [DllImport("hid.dll")]
    private static extern bool HidD_GetAttributes(IntPtr dev, out HidDAttributes attrs);

    [DllImport("hid.dll")]
    private static extern bool HidD_SetOutputReport(IntPtr dev, byte[] buf, int len);

    [DllImport("hid.dll")]
    private static extern bool HidD_SetFeature(IntPtr dev, byte[] buf, int len);

    [DllImport("hid.dll", CharSet = CharSet.Auto)]
    private static extern bool HidD_GetProductString(IntPtr dev, byte[] buf, int len);

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr CreateFile(string path, uint access, uint share, IntPtr sec, uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr h);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool WriteFile(IntPtr h, byte[] buf, int len, out int written, IntPtr overlapped);

    [DllImport("kernel32.dll")]
    private static extern bool GetCommState(IntPtr h, ref Dcb dcb);

    [DllImport("kernel32.dll")]
    private static extern bool SetCommState(IntPtr h, ref Dcb dcb);

    [DllImport("kernel32.dll")]
    private static extern bool BuildCommDCB(string def, ref Dcb dcb);

    [DllImport("kernel32.dll")]
    private static extern bool PurgeComm(IntPtr h, uint flags);

    [StructLayout(LayoutKind.Sequential)]
    private struct Dcb
    {
        public int DCBlength;
        public int BaudRate;
        public uint Flags;
        public ushort wReserved;
        public byte XonChar;
        public byte XoffChar;
        public byte ErrorChar;
        public byte EofChar;
        public byte EvtChar;
        public ushort wReserved1;
    }

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

    private static readonly IntPtr InvalidHandle = new(-1);

    private IntPtr _handle = new(-1);
    private WheelVendor _vendor;
    private byte[] _reportBuf = new byte[64];
    private bool _disposed;
    private bool _useSerialPort;
    private bool _useMozaSdk;
    private bool _mozaSdkConfigured;

    private bool _flashOn;
    private int _flashTick;

    private long _lastUpdate;
    private static readonly long UpdateInterval = TimeSpan.TicksPerSecond / 60;

    private volatile LedEffectConfig _config = new();

    private readonly List<string> _diagnosticLog = new();

    private int _sendCount;
    private int _sendFailCount;

    private readonly MozaSdkLedController _mozaSdk = new();

    public bool IsConnected => _handle != InvalidHandle || _useMozaSdk;
    public bool HasDirectControl => _handle != InvalidHandle;
    public bool HasSdkConfig => _useMozaSdk;
    public WheelVendor Vendor => _vendor;
    public string? LastError { get; private set; }
    public string DiagnosticSummary => string.Join("\n", _diagnosticLog);

    public LedEffectConfig Config
    {
        get => _config;
        set => _config = value ?? new LedEffectConfig();
    }

    public bool TryConnect(string productName)
    {
        _diagnosticLog.Clear();
        Log($"LED init for: '{productName}'");

        _vendor = DetectVendor(productName);
        Log($"Detected vendor: {_vendor}");

        if (_vendor == WheelVendor.Unknown)
        {
            LastError = $"No LED protocol for: {productName}";
            Log(LastError);
            return false;
        }

        if (_vendor == WheelVendor.Moza)
        {
            bool pitHouseRunning = IsPitHouseRunning();
            Log($"Moza: PitHouse {(pitHouseRunning ? "IS running" : "not running")}");

            bool serialOk = TryConnectMozaSerial();

            bool sdkOk = false;
            if (!serialOk)
            {
                sdkOk = TryConnectMozaSdk();
                if (!sdkOk)
                {
                    Log("Moza: serial locked + SDK unavailable, trying HID...");
                    TryConnectMozaHid();
                }
            }

            if (serialOk || _handle != InvalidHandle || _useMozaSdk)
                return true;

            LastError = pitHouseRunning
                ? "Moza LED: PitHouse is locking the serial port. Close PitHouse and reconnect."
                : "Moza LED: no serial/HID interface found";
            Log(LastError);
            return false;
        }

        return ConnectHid();
    }

    #region Moza SDK

    private bool TryConnectMozaSdk()
    {
        Log("Moza: attempting SDK connection (config-level)...");

        if (!_mozaSdk.Initialize())
        {
            Log($"Moza: SDK init failed: {_mozaSdk.DiagnosticLog}");
            return false;
        }

        var colors = _config.GetEffectiveColors();
        var rpmThresholds = _config.GetEffectiveRpmThresholds();

        bool configured = _mozaSdk.ConfigureShiftIndicator(
            brightness: _config.Brightness,
            switchMode: 2,
            displayMode: 1,
            colors: colors,
            rpmThresholds: rpmThresholds);

        if (!configured)
        {
            Log("Moza: SDK connected but configure failed");
        }

        _useMozaSdk = true;
        _mozaSdkConfigured = configured;
        LastError = null;
        Log("Moza: SDK configured (brightness + color profile + RPM thresholds)");
        return true;
    }

    #endregion

    #region Moza Serial (MI_00)

    private bool TryConnectMozaSerial()
    {
        Log("Moza: searching for serial interface (MI_00)...");

        string? serialPath = FindMozaSerialPath();
        if (serialPath == null)
        {
            Log("Moza: no USB serial path found, trying COM port...");
            string? comPort = FindMozaComPort();
            if (comPort != null)
            {
                Log($"  Trying COM port: {comPort}");
                string comPath = $@"\\.\{comPort}";
                if (OpenSerialDevice(comPath))
                    return true;
            }

            return false;
        }

        Log($"  Found MI_00: {serialPath}");
        if (OpenSerialDevice(serialPath))
            return true;

        if (Marshal.GetLastWin32Error() == 5)
        {
            Log("  MI_00 locked by PitHouse (ACCESS_DENIED)");
            Log("  >>> Close PitHouse to enable direct LED control <<<");
        }

        return false;
    }

    private static bool IsPitHouseRunning()
    {
        try
        {
            return System.Diagnostics.Process.GetProcessesByName("PitHouse").Length > 0
                || System.Diagnostics.Process.GetProcessesByName("pithouse").Length > 0;
        }
        catch { return false; }
    }

    private string? FindMozaSerialPath()
    {
        foreach (var guid in new[] { ComPortInterfaceGuid, UsbSerialGuid, PortsClassGuid })
        {
            Guid g = guid;
            IntPtr hInfo = SetupDiGetClassDevs(ref g, IntPtr.Zero, IntPtr.Zero, DIGCF_PRESENT | DIGCF_DEVICEINTERFACE);
            if (hInfo == InvalidHandle) continue;

            try
            {
                uint i = 0;
                var iface = new SpDeviceInterfaceData { cbSize = Marshal.SizeOf<SpDeviceInterfaceData>() };

                while (SetupDiEnumDeviceInterfaces(hInfo, IntPtr.Zero, ref g, i++, ref iface))
                {
                    SetupDiGetDeviceInterfaceDetail(hInfo, ref iface, IntPtr.Zero, 0, out int size, IntPtr.Zero);
                    IntPtr buf = Marshal.AllocHGlobal(size);
                    try
                    {
                        Marshal.WriteInt32(buf, IntPtr.Size == 8 ? 8 : 6);
                        if (!SetupDiGetDeviceInterfaceDetail(hInfo, ref iface, buf, size, out _, IntPtr.Zero))
                            continue;

                        string path = Marshal.PtrToStringUni(buf + 4)!;
                        string pathLower = path.ToLowerInvariant();

                        Log($"  Serial scan ({g:B}): {path}");

                        if (pathLower.Contains("vid_346e") && pathLower.Contains("mi_00"))
                        {
                            Log($"  Found Moza MI_00: {path}");
                            return path;
                        }
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
        }

        return null;
    }

    private string? FindMozaComPort()
    {
        try
        {
            string[] ports = System.IO.Ports.SerialPort.GetPortNames();
            Log($"  COM ports found: {string.Join(", ", ports)}");

            using var searcher = new System.Management.ManagementObjectSearcher(
                "SELECT DeviceID, PNPDeviceID FROM Win32_SerialPort");
            foreach (var obj in searcher.Get())
            {
                string? deviceId = obj["DeviceID"]?.ToString();
                string? pnpId = obj["PNPDeviceID"]?.ToString();
                Log($"  WMI SerialPort: {deviceId} PNP={pnpId}");

                if (pnpId != null && pnpId.ToLowerInvariant().Contains("vid_346e"))
                    return deviceId;
            }
        }
        catch (Exception ex)
        {
            Log($"  COM port scan failed: {ex.Message}");
        }

        try
        {
            using var searcher2 = new System.Management.ManagementObjectSearcher(
                "SELECT Name, PNPDeviceID FROM Win32_PnPEntity WHERE PNPDeviceID LIKE '%VID_346E%MI_00%'");
            foreach (var obj in searcher2.Get())
            {
                string? name = obj["Name"]?.ToString();
                string? pnpId = obj["PNPDeviceID"]?.ToString();
                Log($"  WMI PnPEntity MI_00: Name={name} PNP={pnpId}");

                if (name != null)
                {
                    var match = System.Text.RegularExpressions.Regex.Match(name, @"COM\d+");
                    if (match.Success)
                        return match.Value;
                }
            }
        }
        catch (Exception ex)
        {
            Log($"  WMI PnP scan failed: {ex.Message}");
        }

        return null;
    }

    private bool OpenSerialDevice(string path)
    {
        Log($"  Opening serial device: {path}");
        IntPtr hDev = CreateFile(path, GENERIC_WRITE | GENERIC_READ, FILE_SHARE_RW, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
        if (hDev == InvalidHandle)
        {
            hDev = CreateFile(path, GENERIC_WRITE, FILE_SHARE_RW, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
        }
        if (hDev == InvalidHandle)
        {
            Log($"  Failed to open serial device (err={Marshal.GetLastWin32Error()})");
            return false;
        }

        _handle = hDev;
        _useSerialPort = true;
        _reportBuf = new byte[64];

        SendMozaBrightness((byte)Math.Clamp(_config.Brightness, 5, 100));

        Log($"  Connected via serial (MI_00): {path}");
        LastError = null;
        return true;
    }

    private bool TryConnectMozaHid()
    {
        Log("Moza: scanning all HID interfaces for LED output...");
        Guid hidGuid = HidGuidValue;
        IntPtr hInfo = SetupDiGetClassDevs(ref hidGuid, IntPtr.Zero, IntPtr.Zero, DIGCF_PRESENT | DIGCF_DEVICEINTERFACE);
        if (hInfo == InvalidHandle)
        {
            Log("  HID enumeration failed");
            return false;
        }

        var candidates = new List<(IntPtr handle, string path, ushort vid, ushort pid, ushort usagePage, ushort usage, ushort outLen, ushort featLen, string product)>();

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
                    string pathLower = path.ToLowerInvariant();

                    if (!pathLower.Contains("vid_346e") && !pathLower.Contains("vid_3468") && !pathLower.Contains("vid_34de"))
                        continue;

                    IntPtr hDev = CreateFile(path, GENERIC_READ | GENERIC_WRITE, FILE_SHARE_RW, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
                    if (hDev == InvalidHandle)
                    {
                        hDev = CreateFile(path, GENERIC_WRITE, FILE_SHARE_RW, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
                    }
                    if (hDev == InvalidHandle)
                    {
                        Log($"  HID skip (access denied): {path}");
                        continue;
                    }

                    if (!HidD_GetAttributes(hDev, out var attr))
                    {
                        CloseHandle(hDev);
                        continue;
                    }

                    string product = GetHidString(hDev, HidD_GetProductString);

                    if (!HidD_GetPreparsedData(hDev, out IntPtr pp) || !HidP_GetCaps(pp, out var caps))
                    {
                        HidD_FreePreparsedData(ref pp);
                        CloseHandle(hDev);
                        continue;
                    }
                    HidD_FreePreparsedData(ref pp);

                    string mi = "unknown";
                    var miMatch = System.Text.RegularExpressions.Regex.Match(pathLower, @"mi_(\d+)");
                    if (miMatch.Success) mi = miMatch.Value;

                    Log($"  HID: VID=0x{attr.Vid:X4} PID=0x{attr.Pid:X4} {mi} UsagePage=0x{caps.UsagePage:X4} Usage=0x{caps.Usage:X4} OutLen={caps.OutputReportByteLength} FeatLen={caps.FeatureReportByteLength} '{product}'");

                    candidates.Add((hDev, path, attr.Vid, attr.Pid, caps.UsagePage, caps.Usage, caps.OutputReportByteLength, caps.FeatureReportByteLength, product));
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

        if (candidates.Count == 0)
        {
            Log("  No Moza HID interfaces found");
            return false;
        }

        // Priority: MI_02 vendor-specific with output reports > MI_01 with output > any with feature reports
        foreach (var c in candidates)
        {
            string pLower = c.path.ToLowerInvariant();
            bool isVendor = pLower.Contains("mi_02") || c.usagePage == 0xFF00 || c.usagePage == 0x00;
            bool hasOutput = c.outLen > 0;
            bool hasFeature = c.featLen > 0;

            if (isVendor && hasOutput)
            {
                // Close others
                foreach (var other in candidates)
                    if (other.handle != c.handle) CloseHandle(other.handle);

                _handle = c.handle;
                _useSerialPort = false;
                _reportBuf = new byte[c.outLen];
                Log($"  Selected HID (vendor MI_02): OutLen={c.outLen}");
                LastError = null;
                return true;
            }
        }

        // Try any HID with output reports (non-game-controller)
        foreach (var c in candidates)
        {
            bool hasOutput = c.outLen > 0;
            string pLower = c.path.ToLowerInvariant();
            bool isGameCtrl = c.usagePage == 0x01 && (c.usage == 0x04 || c.usage == 0x05);

            if (hasOutput && !isGameCtrl)
            {
                foreach (var other in candidates)
                    if (other.handle != c.handle) CloseHandle(other.handle);

                _handle = c.handle;
                _useSerialPort = false;
                _reportBuf = new byte[c.outLen];
                Log($"  Selected HID (non-game): OutLen={c.outLen}");
                LastError = null;
                return true;
            }
        }

        // Try any with feature reports
        foreach (var c in candidates)
        {
            if (c.featLen > 0)
            {
                foreach (var other in candidates)
                    if (other.handle != c.handle) CloseHandle(other.handle);

                _handle = c.handle;
                _useSerialPort = false;
                _reportBuf = new byte[c.featLen];
                Log($"  Selected HID (feature reports): FeatLen={c.featLen}");
                LastError = null;
                return true;
            }
        }

        // Nothing usable
        foreach (var c in candidates)
            CloseHandle(c.handle);

        Log("  No HID interface with output/feature capability found");
        return false;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ReadFile(IntPtr h, byte[] buf, int len, out int bytesRead, IntPtr overlapped);

    private void ProbeSerialFormats()
    {
        Log("  Sending brightness/enable commands before LED data...");

        byte[][] initPackets = new byte[][]
        {
            BuildInitPacket(0x01, 0x01, 0x64),
            BuildInitPacket(0x01, 0x02, 0x64),
            BuildInitPacket(0x01, 0x04, 0x64),
            BuildInitPacket(0x01, 0x05, 0x01),
            BuildInitPacket(0x02, 0x01, 0x64),
        };

        foreach (var pkt in initPackets)
        {
            bool ok = WriteFile(_handle, pkt, pkt.Length, out int written, IntPtr.Zero);
            var hex = string.Join(",", pkt.Take(6).Select(b => "0x" + b.ToString("X2")));
            Log($"  Init [{hex}]: ok={ok} written={written}");
            System.Threading.Thread.Sleep(50);
        }

        byte[][] testPackets = new byte[][]
        {
            BuildTestPacket(new byte[] { 0x01, 0x03, 0xFF, 0x00, 0x00, 0xFF, 0x00, 0x00, 0xFF, 0x00, 0x00, 0xFF, 0x00, 0x00, 0xFF, 0x00, 0x00, 0xFF, 0x00, 0x00, 0xFF, 0x00, 0x00, 0xFF, 0x00, 0x00, 0xFF, 0x00, 0x00, 0xFF, 0x00, 0x00 }),
        };

        Log("  Sending all-red LED command...");
        for (int i = 0; i < testPackets.Length; i++)
        {
            bool ok = WriteFile(_handle, testPackets[i], testPackets[i].Length, out int written, IntPtr.Zero);
            var hex = string.Join(",", testPackets[i].Take(12).Select(b => "0x" + b.ToString("X2")));
            Log($"  LED cmd: ok={ok} written={written} [{hex}...]");
        }
        Log("  Probe complete — check LEDs");
    }

    private static byte[] BuildInitPacket(byte rid, byte cmd, byte val)
    {
        var buf = new byte[64];
        buf[0] = rid;
        buf[1] = cmd;
        buf[2] = val;
        return buf;
    }

    private static byte[] BuildTestPacket(byte[] headerAndData)
    {
        var buf = new byte[64];
        Array.Clear(buf, 0, buf.Length);
        int copyLen = Math.Min(headerAndData.Length, buf.Length);
        Array.Copy(headerAndData, buf, copyLen);
        return buf;
    }

    #endregion

    #region HID Fallback

    private bool ConnectHid()
    {
        _useSerialPort = false;
        Guid hidGuid = HidGuidValue;
        IntPtr hInfo = SetupDiGetClassDevs(ref hidGuid, IntPtr.Zero, IntPtr.Zero, DIGCF_PRESENT | DIGCF_DEVICEINTERFACE);
        if (hInfo == InvalidHandle)
        {
            LastError = "SetupDi enumeration failed";
            Log(LastError);
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
                    if (hDev == InvalidHandle)
                    {
                        hDev = CreateFile(path, GENERIC_WRITE, FILE_SHARE_RW, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
                        if (hDev == InvalidHandle)
                            continue;
                    }

                    if (!HidD_GetAttributes(hDev, out var attr))
                    {
                        CloseHandle(hDev);
                        continue;
                    }

                    string product = GetHidString(hDev, HidD_GetProductString);

                    if (!HidD_GetPreparsedData(hDev, out IntPtr pp) || !HidP_GetCaps(pp, out var caps))
                    {
                        HidD_FreePreparsedData(ref pp);
                        CloseHandle(hDev);
                        continue;
                    }
                    HidD_FreePreparsedData(ref pp);

                    bool match = VendorMatch(attr.Vid) || ProductNameMatch(product);
                    Log($"  HID {i}: VID=0x{attr.Vid:X4} PID=0x{attr.Pid:X4} UsagePage=0x{caps.UsagePage:X4} OutLen={caps.OutputReportByteLength} FeatLen={caps.FeatureReportByteLength} Product='{product}' {(match ? "<<MATCH>>" : "")}");

                    if (match && caps.OutputReportByteLength > 0)
                    {
                        _handle = hDev;
                        _reportBuf = new byte[caps.OutputReportByteLength];
                        Log($"  Connected via HID OUTPUT: VID=0x{attr.Vid:X4} PID=0x{attr.Pid:X4} OutLen={caps.OutputReportByteLength}");
                        LastError = null;
                        return true;
                    }

                    CloseHandle(hDev);
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

        LastError = "No usable device found";
        Log(LastError);
        return false;
    }

    #endregion

    private int _absLogCount;
    private bool _lastAbsState;

    public void UpdateLeds(float rpmPercent, bool shiftUp, bool limiter, int flag, bool absActive = false)
    {
        if (!IsConnected || _reportBuf == null) return;

        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        if (now - _lastUpdate < UpdateInterval) return;
        _lastUpdate = now;

        float rpm = rpmPercent > 1.0f ? rpmPercent / 100.0f : rpmPercent;
        rpm = Math.Clamp(rpm, 0f, 1f);

        if (_sendCount <= 3)
            Log($"UpdateLeds: raw={rpmPercent:F2} norm={rpm:F3} shift={shiftUp} limiter={limiter} flag={flag} abs={absActive} serial={_useSerialPort}");

        if (absActive != _lastAbsState)
        {
            _lastAbsState = absActive;
            if (absActive && _absLogCount < 10)
            {
                _absLogCount++;
                Log($"ABS ON: rpm={rpmPercent:F1}% config.AbsFlashEnabled={_config.AbsFlashEnabled} vendor={_vendor} serial={_useSerialPort}");
            }
        }

        try
        {
            switch (_vendor)
            {
                case WheelVendor.Fanatec:
                    WriteFanatec(rpm, shiftUp, limiter, flag, absActive);
                    break;
                case WheelVendor.Moza:
                    WriteMoza(rpm, shiftUp, limiter, flag, absActive);
                    break;
                case WheelVendor.Logitech:
                    WriteLogitech(rpm, shiftUp, limiter, flag, absActive);
                    break;
                case WheelVendor.Simucube:
                    WriteSimucube(rpm, shiftUp, limiter, flag, absActive);
                    break;
            }
        }
        catch (Exception ex)
        {
            LastError = $"{_vendor}: write failed: {ex.Message}";
        }
    }

    #region Fanatec

    private const int FanatecLedCount = 9;

    private void WriteFanatec(float rpm, bool shift, bool limiter, int flag, bool absActive)
    {
        Array.Clear(_reportBuf!);
        _reportBuf![0] = 0x11;

        ushort bits;
        bool flash = (shift || limiter) && _config.ShiftLimiterFlashEnabled;

        if (absActive && _config.AbsFlashEnabled)
        {
            if (++_absFlashTick >= Math.Max(_config.FlashRateTicks / 2, 2))
            {
                _absFlashTick = 0;
                _absFlashAlternate = !_absFlashAlternate;
            }

            bits = _absFlashAlternate
                ? (ushort)0x3
                : (ushort)(3 << (FanatecLedCount - 2));
        }
        else if (flash)
        {
            _absFlashTick = 0;
            _absFlashAlternate = false;
            if (++_flashTick >= _config.FlashRateTicks) { _flashTick = 0; _flashOn = !_flashOn; }
            bits = _flashOn ? (ushort)((1 << FanatecLedCount) - 1) : (ushort)0;
        }
        else
        {
            _absFlashTick = 0;
            _absFlashAlternate = false;
            _flashTick = 0;
            _flashOn = false;

            int n = CountLedsForRpm(rpm, FanatecLedCount);
            bits = (ushort)((1 << n) - 1);
        }

        if (flag > 0 && _config.FlagIndicatorsEnabled)
            bits |= FanatecFlagBits(flag);

        _reportBuf[2] = (byte)(bits & 0xFF);
        _reportBuf[3] = (byte)((bits >> 8) & 0xFF);
        _reportBuf[4] = FanatecFlagByte(flag);

        SendReport();
    }

    private int CountLedsForRpm(float rpmFraction, int ledCount)
    {
        int[] thresholds = _config.GetEffectiveRpmThresholds();
        int count = 0;
        for (int i = 0; i < ledCount && i < thresholds.Length; i++)
        {
            if (rpmFraction * 100f >= thresholds[i])
                count = i + 1;
            else
                break;
        }
        return count;
    }

    private static ushort FanatecFlagBits(int f) => f switch
    {
        2 => 0x0101,
        5 => 0x0202,
        3 => 0x0404,
        4 => 0x0808,
        1 => 0x1010,
        _ => 0
    };

    private static byte FanatecFlagByte(int f) => f switch
    {
        2 => 0x01,
        5 => 0x02,
        3 => 0x04,
        4 => 0x08,
        1 => 0x10,
        8 => 0x01,
        9 => 0x02,
        _ => 0x00
    };

    #endregion

    #region Logitech

    private const int LogitechLedCount = 5;
    private const byte LogitechLedReportId = 0x03;

    private void WriteLogitech(float rpm, bool shift, bool limiter, int flag, bool absActive)
    {
        Array.Clear(_reportBuf!);
        _reportBuf![0] = LogitechLedReportId;

        ushort bits;
        bool flash = (shift || limiter) && _config.ShiftLimiterFlashEnabled;

        if (absActive && _config.AbsFlashEnabled)
        {
            if (++_absFlashTick >= Math.Max(_config.FlashRateTicks / 2, 2))
            {
                _absFlashTick = 0;
                _absFlashAlternate = !_absFlashAlternate;
            }

            bits = _absFlashAlternate
                ? (ushort)0x3
                : (ushort)(3 << (LogitechLedCount - 2));
        }
        else if (flash)
        {
            _absFlashTick = 0;
            _absFlashAlternate = false;
            if (++_flashTick >= _config.FlashRateTicks) { _flashTick = 0; _flashOn = !_flashOn; }
            bits = _flashOn ? (ushort)((1 << LogitechLedCount) - 1) : (ushort)0;
        }
        else
        {
            _absFlashTick = 0;
            _absFlashAlternate = false;
            _flashTick = 0;
            _flashOn = false;

            int n = CountLedsForRpm(rpm, LogitechLedCount);
            bits = (ushort)((1 << n) - 1);
        }

        _reportBuf[1] = (byte)(bits & 0xFF);
        SendReport();
    }

    #endregion

    #region Simucube

    private const int SimucubeLedCount = 10;

    private void WriteSimucube(float rpm, bool shift, bool limiter, int flag, bool absActive)
    {
        Array.Clear(_reportBuf!);

        int ledCount = SimucubeLedCount;
        bool flash = (shift || limiter) && _config.ShiftLimiterFlashEnabled;

        uint[] colorData;

        if (absActive && _config.AbsFlashEnabled)
        {
            if (++_absFlashTick >= Math.Max(_config.FlashRateTicks / 2, 2))
            {
                _absFlashTick = 0;
                _absFlashAlternate = !_absFlashAlternate;
            }

            colorData = new uint[ledCount];
            for (int i = 0; i < ledCount; i++)
            {
                bool isAbsLed = _absFlashAlternate ? i < 2 : i >= ledCount - 2;
                colorData[i] = isAbsLed ? 0xFFFFAA00u : 0x00000000u;
            }
        }
        else if (flash)
        {
            _absFlashTick = 0;
            _absFlashAlternate = false;
            if (++_flashTick >= _config.FlashRateTicks) { _flashTick = 0; _flashOn = !_flashOn; }

            uint allColor = _flashOn ? 0xFFFF0606u : 0x00000000u;
            colorData = Enumerable.Repeat(allColor, ledCount).ToArray();
        }
        else
        {
            _absFlashTick = 0;
            _absFlashAlternate = false;
            _flashTick = 0;
            _flashOn = false;

            int n = CountLedsForRpm(rpm, ledCount);
            var effectiveColors = _config.GetEffectiveColors();

            colorData = new uint[ledCount];
            for (int i = 0; i < ledCount; i++)
            {
                if (i < n)
                {
                    if (flag > 0 && _config.FlagIndicatorsEnabled)
                        colorData[i] = FlagToUIntColor(flag);
                    else
                        colorData[i] = i < effectiveColors.Length ? ArgbToUInt(effectiveColors[i]) : 0xFF00CE00u;
                }
                else
                {
                    colorData[i] = 0x00000000u;
                }
            }
        }

        _reportBuf[0] = 0x00;
        for (int i = 0; i < ledCount && (1 + i * 4 + 3) < _reportBuf.Length; i++)
        {
            int offset = 1 + i * 4;
            _reportBuf[offset] = (byte)(colorData[i] & 0xFF);
            _reportBuf[offset + 1] = (byte)((colorData[i] >> 8) & 0xFF);
            _reportBuf[offset + 2] = (byte)((colorData[i] >> 16) & 0xFF);
            _reportBuf[offset + 3] = (byte)((colorData[i] >> 24) & 0xFF);
        }

        SendReport();
    }

    private static uint ArgbToUInt(string argb)
    {
        if (argb.StartsWith("#") && argb.Length == 9)
        {
            byte a = Convert.ToByte(argb.Substring(1, 2), 16);
            byte r = Convert.ToByte(argb.Substring(3, 2), 16);
            byte g = Convert.ToByte(argb.Substring(5, 2), 16);
            byte b = Convert.ToByte(argb.Substring(7, 2), 16);
            return ((uint)a << 24) | ((uint)r << 16) | ((uint)g << 8) | b;
        }
        return 0xFF00CE00u;
    }

    private static uint FlagToUIntColor(int flag) => flag switch
    {
        2 => 0xFF00FF00u,
        5 => 0xFFFFFF00u,
        3 => 0xFFFF0000u,
        4 => 0xFF0000FFu,
        1 => 0xFFFFFFFFu,
        _ => 0x00000000u
    };

    #endregion

    #region Moza

    private const int MozaLedCount = 10;
    private const byte MozaFrameStart = 0x7E;
    private const byte MozaChecksumMagic = 0x0D;
    private const byte MozaEsWheelDevice = 0x13;
    private const byte MozaOldWriteGroup = 0x41;

    private enum MozaProtocol { Unknown, EsTelemetry, ColorRgb, BitmaskNew }

    private MozaProtocol _mozaProto;
    private int _mozaEnableCount;

    private void WriteMoza(float rpm, bool shift, bool limiter, int flag, bool absActive)
    {
        if (HasDirectControl)
        {
            WriteMozaSerial(rpm, shift, limiter, flag, absActive);
            return;
        }

        if (_useMozaSdk)
        {
            WriteMozaSdk(rpm, shift, limiter, flag, absActive);
            return;
        }
    }

    private bool _absFlashAlternate;
    private int _absFlashTick;

    private void WriteMozaSerial(float rpm, bool shift, bool limiter, int flag, bool absActive)
    {
        if (_mozaProto == MozaProtocol.Unknown)
        {
            _mozaProto = MozaProtocol.EsTelemetry;
            Log("Moza: ES wheel wake-up flash...");
            SendMozaBitmask(0x3FF);
            Thread.Sleep(200);
            SendMozaBitmask(0);
        }

        _mozaEnableCount++;

        if (absActive && _config.AbsFlashEnabled)
        {
            if (++_absFlashTick >= Math.Max(_config.FlashRateTicks / 2, 2))
            {
                _absFlashTick = 0;
                _absFlashAlternate = !_absFlashAlternate;
            }

            int absBitmask = _absFlashAlternate
                ? 0x3
                : (3 << (MozaLedCount - 2));

            SendMozaBitmask(absBitmask & 0x3FF);
            return;
        }

        _absFlashTick = 0;
        _absFlashAlternate = false;

        bool flash = (shift || limiter) && _config.ShiftLimiterFlashEnabled;

        if (flash)
        {
            if (++_flashTick >= _config.FlashRateTicks) { _flashTick = 0; _flashOn = !_flashOn; }
        }
        else
        {
            _flashTick = 0;
            _flashOn = false;
        }

        int ledsToLight = flash
            ? (_flashOn ? MozaLedCount : 0)
            : CountLedsForRpm(rpm, MozaLedCount);

        int bitmask2 = ledsToLight > 0 ? (1 << ledsToLight) - 1 : 0;
        bitmask2 &= 0x3FF;

        SendMozaBitmask(bitmask2);
    }

    private void SendMozaBitmask(int bitmask)
    {
        SendMozaFrame(MozaOldWriteGroup, MozaEsWheelDevice,
            new byte[]
            {
                0xFD, 0xDE,
                (byte)((bitmask >> 24) & 0xFF),
                (byte)((bitmask >> 16) & 0xFF),
                (byte)((bitmask >> 8) & 0xFF),
                (byte)(bitmask & 0xFF)
            });
    }

    private void SendMozaInitSequence()
    {
        Log("  Init: ES wheel wake-up flash...");
        SendMozaBitmask(0x3FF);
        Thread.Sleep(200);
        SendMozaBitmask(0);
        Log("  Init complete");
    }

    private void SendMozaBrightness(byte brightness)
    {
        var payload = new byte[] { 0x1B, 0x00, 0xFF, brightness };
        SendMozaFrame(0x3F, MozaEsWheelDevice, payload);
    }

    private void SendMozaFrame(byte group, byte device, byte[] payload)
    {
        int n = payload.Length;
        int frameLen = 1 + 1 + 1 + 1 + n + 1;
        var frame = new byte[frameLen];
        int pos = 0;

        frame[pos++] = MozaFrameStart;
        frame[pos++] = (byte)n;
        frame[pos++] = group;
        frame[pos++] = device;
        Buffer.BlockCopy(payload, 0, frame, pos, n);
        pos += n;

        byte checksum = MozaChecksumMagic;
        for (int i = 0; i < pos; i++)
            checksum = (byte)((checksum + frame[i]) % 256);
        frame[pos] = checksum;

        bool ok = WriteFile(_handle, frame, frame.Length, out int written, IntPtr.Zero);
        _sendCount++;

        if (_sendCount <= 5)
        {
            var hex = string.Join(",", frame.Take(Math.Min(frame.Length, 30)).Select(b => "0x" + b.ToString("X2")));
            Log($"Moza TX #{_sendCount}: ok={ok} written={written} err={Marshal.GetLastWin32Error()} [{hex}] proto={_mozaProto}");
        }
    }

    private void SendMozaColorChunk((byte r, byte g, byte b)[] colors, int startIdx)
    {
        int count = Math.Min(5, colors.Length - startIdx);
        int payloadLen = 2 + count * 4;
        var payload = new byte[payloadLen];

        payload[0] = 0x19;
        payload[1] = 0x00;

        for (int i = 0; i < count; i++)
        {
            int offset = 2 + i * 4;
            int ledIdx = startIdx + i;
            payload[offset] = (byte)ledIdx;
            payload[offset + 1] = colors[ledIdx].r;
            payload[offset + 2] = colors[ledIdx].g;
            payload[offset + 3] = colors[ledIdx].b;
        }

        SendMozaFrame(0x3F, MozaEsWheelDevice, payload);
    }

    private void WriteMozaSdk(float rpm, bool shift, bool limiter, int flag, bool absActive)
    {
        if (!_mozaSdk.IsAvailable) return;

        int ledCount = MozaLedCount;
        var colors = new string[ledCount];
        var effectiveColors = _config.GetEffectiveColors();

        bool flash = (shift || limiter) && _config.ShiftLimiterFlashEnabled;

        if (absActive && _config.AbsFlashEnabled)
        {
            if (++_absFlashTick >= Math.Max(_config.FlashRateTicks / 2, 2))
            {
                _absFlashTick = 0;
                _absFlashAlternate = !_absFlashAlternate;
            }

            for (int i = 0; i < ledCount; i++)
            {
                bool isAbsLed = _absFlashAlternate
                    ? i < 2
                    : i >= ledCount - 2;

                colors[i] = isAbsLed ? "#FFFFAA00" : "#00000000";
            }

            _mozaSdk.SetShiftIndicatorColors(colors);
            return;
        }

        _absFlashTick = 0;
        _absFlashAlternate = false;

        if (flash)
        {
            if (++_flashTick >= _config.FlashRateTicks) { _flashTick = 0; _flashOn = !_flashOn; }
        }
        else
        {
            _flashTick = 0;
            _flashOn = false;
        }

        int ledsToLight = flash
            ? (_flashOn ? ledCount : 0)
            : CountLedsForRpm(rpm, ledCount);

        for (int i = 0; i < ledCount; i++)
        {
            if (i < ledsToLight)
            {
                if (flash && _flashOn)
                    colors[i] = "#FFFF0606";
                else if (flag > 0 && _config.FlagIndicatorsEnabled)
                    colors[i] = FlagToArgb(flag);
                else
                    colors[i] = i < effectiveColors.Length ? effectiveColors[i] : "#FF00CE00";
            }
            else
            {
                colors[i] = "#00000000";
            }
        }

        _mozaSdk.SetShiftIndicatorColors(colors);
    }

    private static string FlagToArgb(int flag) => flag switch
    {
        2 => "#FF00FF00",
        5 => "#FFFFFF00",
        3 => "#FFFF0000",
        4 => "#FF0000FF",
        1 => "#FFFFFFFF",
        _ => "#00000000"
    };

    private static (byte r, byte g, byte b) FlagToRgb(int flag) => flag switch
    {
        2 => (0x00, 0xFF, 0x00),
        5 => (0xFF, 0xFF, 0x00),
        3 => (0xFF, 0x00, 0x00),
        4 => (0x00, 0x00, 0xFF),
        1 => (0xFF, 0xFF, 0xFF),
        _ => (0x00, 0x00, 0x00)
    };

    #endregion

    private void SendReport()
    {
        if (_reportBuf == null) return;

        bool ok = WriteFile(_handle, _reportBuf, _reportBuf.Length, out int written, IntPtr.Zero);
        int writeErr = Marshal.GetLastWin32Error();

        if (!ok && !_useSerialPort)
        {
            ok = HidD_SetOutputReport(_handle, _reportBuf, _reportBuf.Length);
            writeErr = Marshal.GetLastWin32Error();
        }

        if (!ok && !_useSerialPort)
        {
            ok = HidD_SetFeature(_handle, _reportBuf, _reportBuf.Length);
            writeErr = Marshal.GetLastWin32Error();
        }

        _sendCount++;
        if (!ok) _sendFailCount++;

        if (_sendCount <= 5)
        {
            var hex = string.Join(",", _reportBuf.Take(Math.Min(_reportBuf.Length, 36)).Select(b => "0x" + b.ToString("X2")));
            Log($"Send #{_sendCount}: ok={ok} written={written} err={writeErr} buf=[{hex}] len={_reportBuf.Length} serial={_useSerialPort}");
        }
    }

    private bool ProductNameMatch(string product)
    {
        if (string.IsNullOrEmpty(product)) return false;
        var p = product.ToLowerInvariant();

        return _vendor switch
        {
            WheelVendor.Fanatec => p.Contains("fanatec") || p.Contains("csl") || p.Contains("clubsport") || p.Contains("podium"),
            WheelVendor.Moza => p.Contains("moza"),
            WheelVendor.Thrustmaster => p.Contains("thrustmaster"),
            WheelVendor.Simagic => p.Contains("simagic"),
            WheelVendor.Logitech => p.Contains("logitech") || p.Contains("g29") || p.Contains("g923"),
            WheelVendor.Simucube => p.Contains("simucube"),
            _ => false
        };
    }

    private static string GetHidString(IntPtr hDev, Func<IntPtr, byte[], int, bool> getter)
    {
        try
        {
            var buf = new byte[256];
            if (getter(hDev, buf, buf.Length))
                return Encoding.Unicode.GetString(buf).TrimEnd('\0').Trim();
        }
        catch { }
        return "";
    }

    private bool VendorMatch(ushort vid) => _vendor switch
    {
        WheelVendor.Fanatec => vid == 0x0EB7,
        WheelVendor.Moza => vid == 0x3468 || vid == 0x346E || vid == 0x34DE,
        WheelVendor.Thrustmaster => vid == 0x044F,
        WheelVendor.Simagic => vid == 0x0483 || vid == 0x3335,
        WheelVendor.Logitech => vid == 0x046D,
        WheelVendor.Simucube => vid == 0x16C0,
        _ => false
    };

    private static WheelVendor DetectVendor(string name)
    {
        var s = name.ToLowerInvariant();
        if (s.Contains("fanatec") || s.Contains("csl") || s.Contains("clubsport") || s.Contains("podium"))
            return WheelVendor.Fanatec;
        if (s.Contains("moza"))
            return WheelVendor.Moza;
        if (s.Contains("thrustmaster") || s.Contains("t300") || s.Contains("tx"))
            return WheelVendor.Thrustmaster;
        if (s.Contains("simagic"))
            return WheelVendor.Simagic;
        if (s.Contains("logitech") || s.Contains("g29") || s.Contains("g923"))
            return WheelVendor.Logitech;
        if (s.Contains("simucube"))
            return WheelVendor.Simucube;
        return WheelVendor.Unknown;
    }

    private static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "AcEvoFfbTuner", "led_debug.log");

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

    public void ApplyConfig()
    {
        if (_useMozaSdk && _mozaSdk.IsAvailable)
        {
            try
            {
                var colors = _config.GetEffectiveColors();
                var rpmThresholds = _config.GetEffectiveRpmThresholds();

                _mozaSdk.ConfigureShiftIndicator(
                    brightness: _config.Brightness,
                    switchMode: 2,
                    displayMode: 1,
                    colors: colors,
                    rpmThresholds: rpmThresholds);
            }
            catch { }
        }

        if (HasDirectControl && _useSerialPort)
        {
            try
            {
                SendMozaBrightness((byte)Math.Clamp(_config.Brightness, 5, 100));
            }
            catch { }
        }
    }

    public void ClearLeds()
    {
        if (!IsConnected) return;
        try
        {
            if (_handle != InvalidHandle && _useSerialPort)
            {
                var offColors = new (byte, byte, byte)[MozaLedCount];
                SendMozaColorChunk(offColors, 0);
                if (MozaLedCount > 5)
                    SendMozaColorChunk(offColors, 5);
            }
            else if (_handle != InvalidHandle && _reportBuf != null)
            {
                Array.Clear(_reportBuf);
                SendReport();
            }

            if (_useMozaSdk && _mozaSdk.IsAvailable)
            {
                var off = new string[MozaLedCount];
                Array.Fill(off, "#00000000");
                _mozaSdk.SetShiftIndicatorColors(off);
            }
        }
        catch { }
    }

    public void Disconnect()
    {
        try { ClearLeds(); } catch { }

        if (_useMozaSdk)
        {
            _mozaSdk.Dispose();
            _useMozaSdk = false;
            _mozaSdkConfigured = false;
        }

        if (_handle != InvalidHandle)
        {
            CloseHandle(_handle);
            _handle = InvalidHandle;
        }
        _reportBuf = null!;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Disconnect();
    }
}
