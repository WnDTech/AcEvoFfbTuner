using System.Runtime.InteropServices;
using AcEvoFfbTuner.Core.Config;

namespace AcEvoFfbTuner.Core.PedalHaptics.Providers;

/// <summary>
/// Driver for the MOZA PHF Pedal Haptic Feedback system:
/// RS102 = PHF Kit (control box + 1 module), RS103 = add-on PHF Module.
/// The control box connects to the PC via USB (direct-PC mode) and exposes
/// THROTTLE / BRAKE / CLUTCH ports for up to 3 modules. One provider instance
/// covers both SKUs — <see cref="MozaPhfConfig.ModuleMask"/> tracks which
/// ports have a physical module fitted.
///
/// PHF spec (mozaracing.com/pages/phf-module): vibration frequency 0–100 Hz,
/// intensity 0–100% per module, driven by Pit House telemetry effects
/// (ABS, TC, RPM, lock-up, slip, gear shift).
///
/// USB PROTOCOL STATUS: MOZA has not published the PHF control-box HID
/// protocol (SDK 1.0.1.8 predates the PHF launch, Sep 2026). The report
/// layout in <see cref="BuildReport"/> is a documented best-guess placeholder:
/// every sent report is logged to moza_phf_log.txt so it can be compared
/// against a Pit House USB capture and corrected once hardware is in hand.
/// <see cref="RawReportOverride"/> allows pushing arbitrary bytes for
/// protocol reverse-engineering from the UI test panel.
///
/// Direct-PC mode requires Pit House to NOT own the control box — same
/// conflict as the MOZA LED serial path: close Pit House before connecting.
/// </summary>
public sealed class MozaPhfProvider : IPedalHapticProvider
{
    public const ushort MozaVid = 0x346E;

    private static readonly string[] LikelyPhfNameTokens =
        ["PHF", "HAPTIC", "PEDAL HAPTIC", "CONTROL BOX", "RS102", "RS103"];

    private static readonly string PhfLogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "AcEvoFfbTuner", "moza_phf_log.txt");

    private IntPtr _hidHandle = InvalidHandle;
    private static readonly IntPtr InvalidHandle = new(-1);
    private static Guid HidGuid => new("{4D1E55B2-F16F-11CF-88CB-001111000030}");

    private const uint DIGCF_PRESENT = 0x02;
    private const uint DIGCF_DEVICEINTERFACE = 0x10;
    private const uint GENERIC_READ_WRITE = 0xC0000000;
    private const uint FILE_SHARE_RW = 0x03;
    private const uint OPEN_EXISTING = 3;

    private readonly MozaPhfConfig _config;
    private readonly object _lock = new();
    private bool _disposed;

    // Per-channel live state (intensity 0..1, frequency Hz). The control box
    // is driven by one combined report, so every channel setter updates state
    // then flushes the full 3-channel report.
    private float _throttleIntensity;
    private float _brakeIntensity;
    private float _clutchIntensity;
    private int _throttleFreq;
    private int _brakeFreq;
    private int _clutchFreq;

    private byte[]? _lastSentReport;

    /// <summary>When set, this exact payload (after ReportId prefix handling)
    /// is sent instead of the built report. For protocol bring-up only.</summary>
    public byte[]? RawReportOverride { get; set; }

    public MozaPhfProvider(MozaPhfConfig config)
    {
        _config = config;
    }

    public string DeviceName => "MOZA PHF Pedal Haptics (RS102/RS103)";
    public bool IsAvailable => _hidHandle != InvalidHandle && !_disposed;

    public bool IsBrakeSupported => _config.HasModule(MozaPhfChannel.Brake);
    public bool IsGasSupported => _config.HasModule(MozaPhfChannel.Throttle);
    public bool IsClutchSupported => _config.HasModule(MozaPhfChannel.Clutch);

    public string ConnectedPath { get; private set; } = "";
    public ushort ConnectedPid { get; private set; }

    public static bool IsPitHouseRunning()
    {
        try
        {
            return System.Diagnostics.Process.GetProcesses().Any(p =>
            {
                try
                {
                    var name = p.ProcessName;
                    return name.Contains("MozaPitHouse", StringComparison.OrdinalIgnoreCase)
                        || name.Equals("PitHouse", StringComparison.OrdinalIgnoreCase);
                }
                catch { return false; }
            });
        }
        catch { return false; }
    }

    public sealed record PhfCandidate(
        string Path, ushort VendorId, ushort ProductId,
        string Product, bool LikelyPhf);

    /// <summary>Enumerate all MOZA HID devices. PHF PIDs are unpublished, so
    /// no PID allowlist is applied — candidates whose product string matches
    /// PHF tokens are flagged, the user picks the control box in the UI.</summary>
    public static List<PhfCandidate> EnumerateCandidates()
    {
        var result = new List<PhfCandidate>();
        try
        {
            var hidGuid = HidGuid;
            var hInfo = SetupDiGetClassDevs(ref hidGuid, IntPtr.Zero, IntPtr.Zero,
                DIGCF_PRESENT | DIGCF_DEVICEINTERFACE);
            if (hInfo == InvalidHandle) return result;

            try
            {
                uint index = 0;
                var iface = new SpDeviceInterfaceData { cbSize = Marshal.SizeOf<SpDeviceInterfaceData>() };

                while (SetupDiEnumDeviceInterfaces(hInfo, IntPtr.Zero, ref hidGuid, index++, ref iface))
                {
                    SetupDiGetDeviceInterfaceDetail(hInfo, ref iface, IntPtr.Zero, 0, out int size, IntPtr.Zero);
                    var buf = Marshal.AllocHGlobal(size);
                    try
                    {
                        Marshal.WriteInt32(buf, IntPtr.Size == 8 ? 8 : 6);
                        if (!SetupDiGetDeviceInterfaceDetail(hInfo, ref iface, buf, size, out _, IntPtr.Zero))
                            continue;

                        var path = Marshal.PtrToStringUni(buf + 4);
                        if (string.IsNullOrEmpty(path)) continue;

                        var hDev = CreateFile(path, GENERIC_READ_WRITE, FILE_SHARE_RW,
                            IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
                        if (hDev == InvalidHandle) continue;

                        try
                        {
                            if (!HidD_GetAttributes(hDev, out var attrs)) continue;
                            if (attrs.VendorID != MozaVid) continue;

                            string product = GetProductString(hDev);
                            bool likely = LikelyPhfNameTokens.Any(t =>
                                product.Contains(t, StringComparison.OrdinalIgnoreCase)
                                || path.Contains(t, StringComparison.OrdinalIgnoreCase));

                            result.Add(new PhfCandidate(path, attrs.VendorID,
                                attrs.ProductID, product, likely));
                        }
                        finally { CloseHandle(hDev); }
                    }
                    finally { Marshal.FreeHGlobal(buf); }
                }
            }
            finally { SetupDiDestroyDeviceInfoList(hInfo); }
        }
        catch (Exception ex)
        {
            Log($"ENUM FAILED: {ex.GetType().Name}: {ex.Message}");
        }

        return result;
    }

    /// <summary>Connect to the PHF control box. Empty path = auto-pick the
    /// first likely-PHF candidate, else the first MOZA HID device.</summary>
    public bool Connect(string? devicePath = null)
    {
        Disconnect();

        string path = devicePath ?? "";
        ushort pid = 0;

        if (string.IsNullOrEmpty(path))
        {
            var candidates = EnumerateCandidates();
            if (candidates.Count == 0)
            {
                Log("CONNECT FAILED: no MOZA HID devices found");
                return false;
            }

            var pick = candidates.FirstOrDefault(c => c.LikelyPhf) ?? candidates[0];
            path = pick.Path;
            pid = pick.ProductId;
            Log($"CONNECT: auto-picked '{pick.Product}' pid=0x{pid:X4} likelyPhf={pick.LikelyPhf}");
        }

        try
        {
            var handle = CreateFile(path, GENERIC_READ_WRITE, FILE_SHARE_RW,
                IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
            if (handle == InvalidHandle)
            {
                Log($"CONNECT FAILED: CreateFile failed for {path}");
                return false;
            }

            if (!HidD_GetAttributes(handle, out var attrs) || attrs.VendorID != MozaVid)
            {
                Log("CONNECT FAILED: not a MOZA HID device");
                CloseHandle(handle);
                return false;
            }

            lock (_lock)
            {
                _hidHandle = handle;
                ConnectedPath = path;
                ConnectedPid = attrs.ProductID;
                _lastSentReport = null;
            }

            Log($"CONNECT OK: pid=0x{attrs.ProductID:X4} product='{GetProductString(handle)}'"
                + (IsPitHouseRunning() ? " WARNING: Pit House is running and may own the control box" : ""));
            return true;
        }
        catch (Exception ex)
        {
            Log($"CONNECT FAILED: {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    public void Disconnect()
    {
        lock (_lock)
        {
            if (_hidHandle != InvalidHandle)
            {
                try { SendReportLocked(BuildStopReport(_config)); }
                catch { }
                CloseHandle(_hidHandle);
                _hidHandle = InvalidHandle;
                ConnectedPath = "";
                _lastSentReport = null;
            }

            _throttleIntensity = _brakeIntensity = _clutchIntensity = 0f;
        }
    }

    public void SetBrakeHaptic(float intensity, HapticSignalType signal)
    {
        if (!IsAvailable || !IsBrakeSupported) return;
        lock (_lock)
        {
            _brakeIntensity = Math.Clamp(intensity, 0f, 1f);
            _brakeFreq = FrequencyFor(signal);
            FlushLocked();
        }
    }

    public void SetGasHaptic(float intensity, HapticSignalType signal)
    {
        if (!IsAvailable || !IsGasSupported) return;
        lock (_lock)
        {
            _throttleIntensity = Math.Clamp(intensity, 0f, 1f);
            _throttleFreq = FrequencyFor(signal);
            FlushLocked();
        }
    }

    public void SetClutchHaptic(float intensity, HapticSignalType signal)
    {
        if (!IsAvailable || !IsClutchSupported) return;
        lock (_lock)
        {
            _clutchIntensity = Math.Clamp(intensity, 0f, 1f);
            _clutchFreq = FrequencyFor(signal);
            FlushLocked();
        }
    }

    /// <summary>Direct per-channel drive with explicit frequency (Hz).
    /// Used by the telemetry feed which blends effects per pedal.</summary>
    public void SetChannel(MozaPhfChannel channel, float intensity01, int frequencyHz)
    {
        if (!IsAvailable || !_config.HasModule(channel)) return;
        lock (_lock)
        {
            intensity01 = Math.Clamp(intensity01, 0f, 1f);
            frequencyHz = Math.Clamp(frequencyHz, 0, 100);
            switch (channel)
            {
                case MozaPhfChannel.Throttle:
                    _throttleIntensity = intensity01; _throttleFreq = frequencyHz; break;
                case MozaPhfChannel.Brake:
                    _brakeIntensity = intensity01; _brakeFreq = frequencyHz; break;
                case MozaPhfChannel.Clutch:
                    _clutchIntensity = intensity01; _clutchFreq = frequencyHz; break;
            }
            FlushLocked();
        }
    }

    public void StopAll()
    {
        lock (_lock)
        {
            _throttleIntensity = _brakeIntensity = _clutchIntensity = 0f;
            _throttleFreq = _brakeFreq = _clutchFreq = 0;
            if (IsAvailable)
                SendReportLocked(BuildStopReport(_config));
            _lastSentReport = null;
        }
    }

    public int FrequencyFor(HapticSignalType signal) => signal switch
    {
        HapticSignalType.Abs => _config.AbsFrequency,
        HapticSignalType.Tc => _config.TcFrequency,
        HapticSignalType.Curb => _config.CurbFrequency,
        HapticSignalType.Road => _config.RoadFrequency,
        HapticSignalType.Scrub => _config.ScrubFrequency,
        HapticSignalType.RearSlip => _config.RearSlipFrequency,
        HapticSignalType.BrakePressure => _config.BrakePressureFrequency,
        _ => _config.RoadFrequency
    };

    public string GetDiagnosticInfo()
    {
        var cands = EnumerateCandidates();
        return $"PHF available={IsAvailable} pid=0x{ConnectedPid:X4} "
             + $"modules=0b{Convert.ToString(_config.ModuleMask, 2).PadLeft(3, '0')} "
             + $"mozaHidDevices={cands.Count} pitHouse={(IsPitHouseRunning() ? "RUNNING (close it)" : "not running")}";
    }

    private void FlushLocked()
    {
        if (_hidHandle == InvalidHandle) return;

        float scale = Math.Clamp(_config.IntensityScale, 0f, 2f);
        var report = BuildReport(_config,
            (_throttleIntensity * scale, _throttleFreq),
            (_brakeIntensity * scale, _brakeFreq),
            (_clutchIntensity * scale, _clutchFreq));

        // Skip redundant reports — the telemetry tick calls setters at ~30Hz
        // even when nothing changed; HID output reports are not free.
        if (_lastSentReport != null && _lastSentReport.AsSpan().SequenceEqual(report))
            return;

        SendReportLocked(report);
        _lastSentReport = report;
    }

    private void SendReportLocked(byte[] report)
    {
        try
        {
            byte[] payload = RawReportOverride ?? report;
            bool ok = HidD_SetOutputReport(_hidHandle, payload, payload.Length);
            Log($"SEND {(ok ? "OK" : "FAILED")}: {BitConverter.ToString(payload)}");
        }
        catch (Exception ex)
        {
            Log($"SEND FAILED: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// PROVISIONAL layout (pending USB capture against Pit House):
    /// [0]=ReportId, [1]=command (0x01 update / 0x00 stop),
    /// [2]=brake intensity 0-100, [3]=brake freq 0-100Hz,
    /// [4]=throttle intensity, [5]=throttle freq,
    /// [6]=clutch intensity, [7]=clutch freq, [8..]=reserved 0.
    /// Channels without a fitted module are forced to 0.
    /// </summary>
    public static byte[] BuildReport(
        MozaPhfConfig config,
        (float intensity01, int freqHz) throttle,
        (float intensity01, int freqHz) brake,
        (float intensity01, int freqHz) clutch)
    {
        int len = Math.Max(config.ReportLength, 8);
        var report = new byte[len];
        report[0] = config.ReportId;
        report[1] = 0x01;

        WriteChannel(report, 2, brake, config.HasModule(MozaPhfChannel.Brake));
        WriteChannel(report, 4, throttle, config.HasModule(MozaPhfChannel.Throttle));
        WriteChannel(report, 6, clutch, config.HasModule(MozaPhfChannel.Clutch));
        return report;
    }

    public static byte[] BuildStopReport(MozaPhfConfig config)
    {
        int len = Math.Max(config.ReportLength, 8);
        var report = new byte[len];
        report[0] = config.ReportId;
        report[1] = 0x00;
        return report;
    }

    private static void WriteChannel(byte[] report, int offset,
        (float intensity01, int freqHz) ch, bool moduleFitted)
    {
        if (!moduleFitted)
        {
            report[offset] = 0;
            report[offset + 1] = 0;
            return;
        }

        report[offset] = (byte)Math.Clamp((int)MathF.Round(ch.intensity01 * 100f), 0, 100);
        report[offset + 1] = (byte)Math.Clamp(ch.freqHz, 0, 100);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Disconnect();
    }

    private static string GetProductString(IntPtr handle)
    {
        try
        {
            var buf = new byte[256];
            if (HidD_GetProductString(handle, buf, buf.Length))
            {
                string s = System.Text.Encoding.Unicode.GetString(buf).TrimEnd('\0').Trim();
                if (!string.IsNullOrEmpty(s)) return s;
            }
        }
        catch { }
        return "";
    }

    internal static void Log(string entry)
    {
        try
        {
            var dir = Path.GetDirectoryName(PhfLogPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.AppendAllText(PhfLogPath,
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {entry}{Environment.NewLine}");
        }
        catch { }
        System.Diagnostics.Debug.WriteLine($"[MozaPhf] {entry}");
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SpDeviceInterfaceData
    {
        public int cbSize;
        public Guid interfaceClassGuid;
        public int flags;
        public IntPtr reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HidDAttributes
    {
        public int Size;
        public ushort VendorID;
        public ushort ProductID;
        public ushort VersionNumber;
    }

    [DllImport("setupapi.dll", CharSet = CharSet.Auto)]
    private static extern IntPtr SetupDiGetClassDevs(
        ref Guid classGuid, IntPtr enumerator, IntPtr hwndParent, uint flags);

    [DllImport("setupapi.dll", CharSet = CharSet.Auto)]
    private static extern bool SetupDiEnumDeviceInterfaces(
        IntPtr handle, IntPtr devInfo, ref Guid ifaceGuid, uint index,
        ref SpDeviceInterfaceData data);

    [DllImport("setupapi.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern bool SetupDiGetDeviceInterfaceDetail(
        IntPtr handle, ref SpDeviceInterfaceData ifaceData, IntPtr detailBuf,
        int detailSize, out int requiredSize, IntPtr devInfo);

    [DllImport("setupapi.dll")]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr handle);

    [DllImport("hid.dll")]
    private static extern bool HidD_GetAttributes(IntPtr dev, out HidDAttributes attrs);

    [DllImport("hid.dll")]
    private static extern bool HidD_SetOutputReport(IntPtr dev, byte[] buf, int len);

    [DllImport("hid.dll")]
    private static extern bool HidD_GetProductString(IntPtr dev, byte[] buf, int len);

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr CreateFile(
        string path, uint access, uint share, IntPtr sec, uint disposition,
        uint flags, IntPtr template);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr h);
}
