using System.Runtime.InteropServices;
using System.Text;
using AcEvoFfbTuner.Core.DeviceDetection;

namespace AcEvoFfbTuner.Core.FfbProviders;

/// <summary>
/// Direct HID++ settings provider for Logitech direct-drive wheels (RS50, G PRO)
/// and the G923. Talks to the wheel's HID++ interface (interface 1) WITHOUT any
/// Logitech software — G HUB is not required and must stay closed.
///
/// Protocol reference: mescon/logitech-trueforce-linux-driver
/// docs/PROTOCOL_SPECIFICATION.md (reverse-engineered, verified on RS50 hardware):
///   - Short HID++ reports (0x10) for all commands, device index 0xFF
///   - The RS50 always answers with VERY LONG reports (0x12, 64 bytes)
///   - Feature discovery via Root feature 0x0000 fn=0 getFeature
///   - FFB strength  = feature 0x8136, SET fn=2, value = Nm × 8191.875 (1.0-8.0 Nm)
///   - Rotation      = feature 0x8138, SET fn=2, degrees 16-bit BE (90-2700)
///   - Profile/mode  = feature 0x8137, GET fn=1, SET fn=2 ([0,0,0] = desktop mode,
///                     [slot,0,0] = onboard slot 1-5)
///   - TrueForce     = feature 0x8139, SET fn=3, 0-100% (×655.35)
///   - Damping       = feature 0x8133, SET fn=1, 0-100%
///   - The wheel boots in ONBOARD mode; live host SETs are silently ignored
///     unless the wheel is in DESKTOP mode (0x8137 fn=2 [0,0,0]).
///   - sw_id 0x0A (Linux driver convention; must not use 0x01 on the pedal
///     sub-device, root device 0xFF is fine).
///
/// Everything is logged to logitech_hidpp.log in the AppData folder for diagnosis.
/// </summary>
public sealed class LogitechHidppWheelProvider : IDisposable
{
    public const ushort LogitechVid = 0x046D;

    // RS50 native / compat, G PRO (Xbox/PC + PS/PC), G923 (PS + Xbox editions)
    private static readonly ushort[] SupportedPids = [0xC276, 0xC272, 0xC268, 0xC266, 0xC267, 0xC26D, 0xC26E];

    private const ushort FeatRoot = 0x0000;
    private const ushort FeatFfbStrength = 0x8136;
    private const ushort FeatProfile = 0x8137;
    private const ushort FeatRotationRange = 0x8138;
    private const ushort FeatTrueForce = 0x8139;
    private const ushort FeatDamping = 0x8133;

    private const byte SwId = 0x0A;
    private const int ResponseTimeoutMs = 1000;

    private static readonly IntPtr InvalidHandle = new(-1);

    private const uint GENERIC_READ = 0x80000000;
    private const uint GENERIC_WRITE = 0x40000000;
    private const uint FILE_SHARE_RW = 0x03;
    private const uint OPEN_EXISTING = 3;
    private const uint FILE_FLAG_OVERLAPPED = 0x40000000;
    private const uint WAIT_OBJECT_0 = 0x00000000;
    private const uint WAIT_TIMEOUT = 0x00000102;

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr CreateFile(string path, uint access, uint share, IntPtr sec, uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr h);

    [DllImport("hid.dll", SetLastError = true)]
    private static extern bool HidD_SetOutputReport(IntPtr dev, byte[] buf, int len);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ReadFile(IntPtr hFile, byte[] lpBuffer, uint nNumberOfBytesToRead, out uint lpNumberOfBytesRead, ref NativeOverlapped lpOverlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(IntPtr hHandle, uint dwMilliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CancelIoEx(IntPtr hFile, IntPtr lpOverlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateEvent(IntPtr lpEventAttributes, bool bManualReset, bool bInitialState, string? lpName);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ResetEvent(IntPtr hEvent);

    // HID++ writes go to the SHORT (7-byte) collection; ALL responses arrive on
    // the VERY LONG (64-byte) collection as 0x12 reports.
    private IntPtr _writeHandle = InvalidHandle;
    private IntPtr _readHandle = InvalidHandle;
    private int _writeReportLen = 7;
    private int _readReportLen = 64;
    private IntPtr _readEvent = IntPtr.Zero;
    private string _devicePath = "";
    private string _productName = "";
    private volatile bool _running;
    private Thread? _readThread;

    private readonly object _sync = new();
    private readonly List<PendingRequest> _pending = new();

    private byte _featureFfbStrength;
    private byte _featureRotation;
    private byte _featureProfile;
    private byte _featureTrueForce;
    private byte _featureDamping;

    private readonly string _logPath;

    public LogitechHidppWheelProvider()
    {
        _logPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "AcEvoFfbTuner", "logitech_hidpp.log");
        Log("=== LogitechHidppWheelProvider created ===");
    }

    public bool IsConnected => _writeHandle != InvalidHandle && _readHandle != InvalidHandle;
    public string ProductName => _productName;
    public string LastError { get; private set; } = "";
    public string DiagnosticSummary { get; private set; } = "";

    /// <summary>0 = desktop mode, 1-5 = onboard slot, 0xFF = unknown.</summary>
    public byte ProfileMode { get; private set; } = 0xFF;
    public bool IsDesktopMode => ProfileMode == 0;
    public float FfbStrengthNm { get; private set; }
    public int RotationDegrees { get; private set; }
    public int TrueForceLevelPct { get; private set; }
    public int DampingPct { get; private set; }

    // ─────────────────────────────── Connect ───────────────────────────────

    public bool Connect()
    {
        Log("Connect: enumerating HID devices (VID 046D)...");
        List<HidDeviceInfo> devices;
        try
        {
            devices = HidDeviceEnumerator.EnumerateByVid(LogitechVid);
        }
        catch (Exception ex)
        {
            Log($"Connect: HID enumeration FAILED: {ex.Message}");
            LastError = $"HID enumeration failed: {ex.Message}";
            return false;
        }

        Log($"Connect: found {devices.Count} HID interface(s):");
        foreach (var d in devices)
        {
            Log($"  [{d.DevicePath}] PID=0x{d.ProductId:X4} page=0x{d.UsagePage:X4} usage=0x{d.Usage:X4} inLen={d.InputReportByteLength} outLen={d.OutputReportByteLength} \"{d.ProductString}\"");
        }

        // The RS50/G PRO expose three HID++ collections on mi_01 (usage page 0xFF43):
        //   col01 outLen=7  — SHORT reports (0x10) — G HUB writes here
        //   col02 outLen=20 — LONG reports (0x11)
        //   col03 outLen=64 — VERY LONG reports (0x12) — ALL responses arrive here
        // The TrueForce stream (usage page 0xFFFD) must never be written to.
        var wheelDevices = devices
            .Where(d => SupportedPids.Contains(d.ProductId))
            .Where(d => d.UsagePage != 0xFFFD)
            .ToList();

        var writeCandidates = wheelDevices
            .Where(d => d.OutputReportByteLength >= 7)
            .OrderBy(d => d.OutputReportByteLength)
            .ToList();

        var readCandidates = wheelDevices
            .Where(d => d.InputReportByteLength >= 20)
            .OrderByDescending(d => d.InputReportByteLength)
            .ToList();

        Log($"Connect: {writeCandidates.Count} write candidate(s), {readCandidates.Count} read candidate(s)");

        foreach (var wc in writeCandidates)
        {
            foreach (var rc in readCandidates)
            {
                Log($"Connect: probing write \"{wc.ProductString}\" (page 0x{wc.UsagePage:X4}, outLen={wc.OutputReportByteLength}) + read (page 0x{rc.UsagePage:X4}, inLen={rc.InputReportByteLength})...");

                IntPtr wh = CreateFile(wc.DevicePath, GENERIC_WRITE, FILE_SHARE_RW, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
                if (wh == InvalidHandle)
                {
                    Log($"Connect:   write open FAILED (err={Marshal.GetLastWin32Error()}) — skipping");
                    continue;
                }
                IntPtr rh = CreateFile(rc.DevicePath, GENERIC_READ, FILE_SHARE_RW, IntPtr.Zero, OPEN_EXISTING, FILE_FLAG_OVERLAPPED, IntPtr.Zero);
                if (rh == InvalidHandle)
                {
                    Log($"Connect:   read open FAILED (err={Marshal.GetLastWin32Error()}) — skipping");
                    CloseHandle(wh);
                    continue;
                }

                _writeHandle = wh;
                _readHandle = rh;
                _writeReportLen = wc.OutputReportByteLength;
                _readReportLen = rc.InputReportByteLength;
                _devicePath = wc.DevicePath;
                _productName = wc.ProductString;
                _readEvent = CreateEvent(IntPtr.Zero, false, false, null);

                StartReadThread();

                byte? probe = FindFeatureIndex(FeatFfbStrength, 800);
                if (probe is null or 0x00 or 0xFF)
                {
                    Log($"Connect:   probe FAILED (feature 0x{FeatFfbStrength:X4} not found on this pair) — closing");
                    _running = false;
                    CancelIoEx(_readHandle, IntPtr.Zero);
                    _readThread?.Join(500);
                    _readThread = null;
                    CloseHandle(_writeHandle);
                    CloseHandle(_readHandle);
                    CloseHandle(_readEvent);
                    _writeHandle = InvalidHandle;
                    _readHandle = InvalidHandle;
                    _readEvent = IntPtr.Zero;
                    continue;
                }

                _featureFfbStrength = probe.Value;
                Log($"Connect:   HID++ interface confirmed — FFBStrength at index 0x{probe:X2} (write outLen={_writeReportLen}, read inLen={_readReportLen})");
                break;
            }
            if (_writeHandle != InvalidHandle) break;
        }

        if (_writeHandle == InvalidHandle)
        {
            LastError = "No HID++ control interface responded. Is G HUB running? It claims the HID interface — close it and reconnect.";
            Log($"Connect: FAILED — {LastError}");
            return false;
        }

        // Feature discovery
        _featureRotation = FindFeatureIndex(FeatRotationRange, 800) ?? 0;
        _featureProfile = FindFeatureIndex(FeatProfile, 800) ?? 0;
        _featureTrueForce = FindFeatureIndex(FeatTrueForce, 800) ?? 0;
        _featureDamping = FindFeatureIndex(FeatDamping, 800) ?? 0;
        Log($"Connect: feature indices — rotation=0x{_featureRotation:X2} profile=0x{_featureProfile:X2} trueforce=0x{_featureTrueForce:X2} damping=0x{_featureDamping:X2}");

        // Read current state
        ReadAllSettings();

        DiagnosticSummary = $"{_productName}: strength={FfbStrengthNm:F1} Nm, rotation={RotationDegrees}°, mode={(IsDesktopMode ? "desktop" : ProfileMode == 0xFF ? "unknown" : $"onboard slot {ProfileMode}")}, trueforce={TrueForceLevelPct}%, damping={DampingPct}%";
        Log($"Connect: DONE — {DiagnosticSummary}");
        return true;
    }

    public void Disconnect()
    {
        Log("Disconnect: stopping read thread and closing handles");
        _running = false;
        if (_readEvent != IntPtr.Zero && _readHandle != InvalidHandle)
        {
            try { CancelIoEx(_readHandle, IntPtr.Zero); } catch { }
        }
        _readThread?.Join(500);
        _readThread = null;

        lock (_sync)
        {
            foreach (var p in _pending)
                p.Tcs.TrySetCanceled();
            _pending.Clear();
        }

        if (_writeHandle != InvalidHandle)
        {
            CloseHandle(_writeHandle);
            _writeHandle = InvalidHandle;
        }
        if (_readHandle != InvalidHandle)
        {
            CloseHandle(_readHandle);
            _readHandle = InvalidHandle;
        }
        if (_readEvent != IntPtr.Zero)
        {
            CloseHandle(_readEvent);
            _readEvent = IntPtr.Zero;
        }
        Log("Disconnect: done");
    }

    // ─────────────────────────────── Settings ───────────────────────────────

    public bool SetFfbStrengthNm(float nm)
    {
        float clamped = Math.Clamp(nm, 1.0f, 8.0f);
        ushort value = (ushort)Math.Round(clamped * 8191.875);
        Log($"SetFfbStrengthNm: {clamped:F1} Nm -> value 0x{value:X4}");
        if (_featureFfbStrength == 0 || !SendSet(_featureFfbStrength, 2, [(byte)(value >> 8), (byte)(value & 0xFF), 0x00]))
            return false;

        // Verify with a read-back
        var resp = ReadValue(_featureFfbStrength, 1);
        if (resp != null)
        {
            ushort readBack = (ushort)((resp[4] << 8) | resp[5]);
            float readNm = readBack / 8191.875f;
            FfbStrengthNm = readNm;
            Log($"SetFfbStrengthNm: read-back 0x{readBack:X4} = {readNm:F2} Nm");
        }
        return true;
    }

    public bool SetRotationDegrees(int degrees)
    {
        int clamped = Math.Clamp(degrees, 90, 2700);
        ushort value = (ushort)clamped;
        Log($"SetRotationDegrees: {clamped}° -> 0x{value:X4}");
        if (_featureRotation == 0 || !SendSet(_featureRotation, 2, [(byte)(value >> 8), (byte)(value & 0xFF), 0x00]))
            return false;

        var resp = ReadValue(_featureRotation, 1);
        if (resp != null)
        {
            int readBack = (resp[4] << 8) | resp[5];
            RotationDegrees = readBack;
            Log($"SetRotationDegrees: read-back {readBack}°");
        }
        return true;
    }

    /// <summary>Switch the wheel into desktop mode so live host SETs take effect immediately.</summary>
    public bool SetDesktopMode()
    {
        Log("SetDesktopMode: 0x8137 fn=2 [0,0,0]");
        if (_featureProfile == 0 || !SendSet(_featureProfile, 2, [0x00, 0x00, 0x00]))
            return false;
        ProfileMode = 0;
        Thread.Sleep(50);
        ReadProfileMode();
        Log($"SetDesktopMode: mode now {(IsDesktopMode ? "desktop" : ProfileMode.ToString())}");
        return IsDesktopMode;
    }

    /// <summary>Select an onboard profile slot 1-5 (its stored settings then apply).</summary>
    public bool SetOnboardSlot(byte slot)
    {
        Log($"SetOnboardSlot: slot {slot}");
        if (_featureProfile == 0 || !SendSet(_featureProfile, 2, [slot, 0x00, 0x00]))
            return false;
        ProfileMode = slot;
        return true;
    }

    /// <summary>Re-read all settings from the wheel (for UI refresh).</summary>
    public void ReadAllSettingsForUi()
    {
        if (!IsConnected) return;
        Log("ReadAllSettingsForUi: reading wheel state");
        ReadAllSettings();
        DiagnosticSummary = $"{_productName}: strength={FfbStrengthNm:F1} Nm, rotation={RotationDegrees}°, mode={(IsDesktopMode ? "desktop" : ProfileMode == 0xFF ? "unknown" : $"onboard slot {ProfileMode}")}, trueforce={TrueForceLevelPct}%, damping={DampingPct}%";
        Log($"ReadAllSettingsForUi: {DiagnosticSummary}");
    }

    // ─────────────────────────────── Internals ───────────────────────────────

    private void ReadAllSettings()
    {
        // Profile/mode first — it explains whether live SETs will apply.
        ReadProfileMode();

        var strength = ReadValue(_featureFfbStrength, 1);
        if (strength != null)
        {
            ushort v = (ushort)((strength[4] << 8) | strength[5]);
            FfbStrengthNm = v / 8191.875f;
            Log($"ReadAllSettings: strength 0x{v:X4} = {FfbStrengthNm:F2} Nm");
        }
        else
        {
            Log("ReadAllSettings: strength read FAILED/timeout");
        }

        var rotation = ReadValue(_featureRotation, 1);
        if (rotation != null)
        {
            RotationDegrees = (rotation[4] << 8) | rotation[5];
            Log($"ReadAllSettings: rotation {RotationDegrees}°");
        }
        else
        {
            Log("ReadAllSettings: rotation read FAILED/timeout");
        }

        var tf = ReadValue(_featureTrueForce, 1);
        if (tf != null)
        {
            ushort v = (ushort)((tf[4] << 8) | tf[5]);
            TrueForceLevelPct = (int)Math.Round(v / 655.35f);
            Log($"ReadAllSettings: trueforce 0x{v:X4} = {TrueForceLevelPct}%");
        }

        var damp = ReadValue(_featureDamping, 1);
        if (damp != null)
        {
            ushort v = (ushort)((damp[4] << 8) | damp[5]);
            DampingPct = (int)Math.Round(v / 655.35f);
            Log($"ReadAllSettings: damping 0x{v:X4} = {DampingPct}%");
        }
    }

    private void ReadProfileMode()
    {
        var resp = ReadValue(_featureProfile, 1);
        if (resp == null)
        {
            Log("ReadProfileMode: read FAILED/timeout");
            return;
        }
        byte profile = resp[4];
        ProfileMode = profile;
        Log($"ReadProfileMode: params[0]={profile} -> {(profile == 0 ? "desktop mode" : profile == 0xFF ? "unknown" : $"onboard slot {profile}")}");
    }

    private byte? FindFeatureIndex(ushort featureId, int timeoutMs)
    {
        Log($"FindFeatureIndex: querying 0x{featureId:X4} via Root fn=0...");
        byte[]? resp = RequestResponse(0x00, 0x00, [(byte)(featureId >> 8), (byte)(featureId & 0xFF), 0x00], timeoutMs);
        if (resp == null)
        {
            Log($"FindFeatureIndex: 0x{featureId:X4} no response");
            return null;
        }
        byte idx = resp[4];
        Log($"FindFeatureIndex: 0x{featureId:X4} -> index 0x{idx:X2} (type 0x{resp[5]:X2}, ver 0x{resp[6]:X2})");
        return idx;
    }

    private bool SendSet(byte featureIndex, byte fn, byte[] payload)
    {
        var resp = RequestResponse(featureIndex, fn, payload, ResponseTimeoutMs);
        return resp != null;
    }

    private byte[]? ReadValue(byte featureIndex, byte fn)
    {
        if (featureIndex == 0) return null;
        return RequestResponse(featureIndex, fn, [0x00, 0x00, 0x00], ResponseTimeoutMs);
    }

    private byte[]? RequestResponse(byte featureIndex, byte fn, byte[] payload, int timeoutMs)
    {
        var tcs = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = new PendingRequest { FeatureIndex = featureIndex, Fn = fn, Tcs = tcs };
        lock (_sync)
        {
            if (!_running && _pending.Count > 0)
            {
                // First request after connect — the read thread starts before reads.
            }
            _pending.Add(pending);
        }

        byte[] buf = new byte[_writeReportLen];
        buf[0] = 0x10;
        buf[1] = 0xFF;
        buf[2] = featureIndex;
        buf[3] = (byte)((fn << 4) | SwId);
        for (int i = 0; i < payload.Length && i < 3; i++)
            buf[4 + i] = payload[i];

        Log($"TX: {Hex(buf, Math.Min(8, buf.Length))} (report len {buf.Length})");

        bool sent = false;
        try
        {
            sent = HidD_SetOutputReport(_writeHandle, buf, buf.Length);
        }
        catch (Exception ex)
        {
            Log($"TX: HidD_SetOutputReport threw: {ex.Message}");
        }
        if (!sent)
        {
            Log($"TX: HidD_SetOutputReport returned false (err={Marshal.GetLastWin32Error()})");
            lock (_sync) _pending.Remove(pending);
            return null;
        }

        try
        {
            if (!tcs.Task.Wait(timeoutMs))
            {
                Log($"RX: timeout waiting for response (feat 0x{featureIndex:X2} fn={fn})");
                return null;
            }
            return tcs.Task.Result;
        }
        catch
        {
            return null;
        }
        finally
        {
            lock (_sync) _pending.Remove(pending);
        }
    }

    private void StartReadThread()
    {
        _running = true;
        _readThread = new Thread(ReadLoop)
        {
            IsBackground = true,
            Name = "LogitechHidppRead"
        };
        _readThread.Start();
    }

    private void ReadLoop()
    {
        Log("ReadLoop: started");
        var buf = new byte[_readReportLen];
        var overlapped = new NativeOverlapped();
        overlapped.EventHandle = _readEvent;

        while (_running && _readHandle != InvalidHandle)
        {
            ResetEvent(_readEvent);

            uint read = 0;
            bool ok;
            try
            {
                ok = ReadFile(_readHandle, buf, (uint)_readReportLen, out read, ref overlapped);
            }
            catch (Exception ex)
            {
                Log($"ReadLoop: ReadFile threw: {ex.Message}");
                break;
            }

            if (!ok)
            {
                int err = Marshal.GetLastWin32Error();
                if (err == 997) // ERROR_IO_PENDING — wait for completion
                {
                    uint wait = WaitForSingleObject(_readEvent, 250);
                    if (wait == WAIT_TIMEOUT)
                    {
                        // Cancel and drain the pending IO so the overlapped can be reused.
                        CancelIoEx(_readHandle, IntPtr.Zero);
                        WaitForSingleObject(_readEvent, 250);
                        if (!_running) break;
                        continue;
                    }
                    if (!_running) break;
                }
                else if (err == 995 || err == 6 || err == 1) // cancelled / invalid handle — shutting down
                {
                    break;
                }
                else
                {
                    Log($"ReadLoop: ReadFile error {err}");
                    break;
                }
            }

            if (!_running) break;

            if (read >= 4)
                DispatchResponse(buf, (int)read);
            else
                Log($"ReadLoop: short read {read} bytes");
        }

        Log("ReadLoop: stopped");
    }

    private void DispatchResponse(byte[] report, int len)
    {
        byte reportId = report[0];
        if (reportId != 0x12 && reportId != 0x11 && reportId != 0x10)
        {
            Log($"RX: unexpected report id 0x{reportId:X2} ({len} bytes)");
            return;
        }

        byte featureIndex = report[2];
        byte fn = (byte)(report[3] >> 4);
        Log($"RX: id=0x{reportId:X2} feat=0x{featureIndex:X2} fn={fn} {Hex(report, 10)}");

        PendingRequest? match = null;
        lock (_sync)
        {
            match = _pending.FirstOrDefault(p => p.FeatureIndex == featureIndex && p.Fn == fn);
            if (match == null)
            {
                // Unsolicited broadcast (e.g. settings change events) — log only.
                Log($"RX: unsolicited event (feat 0x{featureIndex:X2} fn={fn}) — no pending request");
            }
        }
        match?.Tcs.TrySetResult(report);
    }

    private static string Hex(byte[] data, int count)
    {
        int n = Math.Min(count, data.Length);
        var sb = new StringBuilder(n * 3);
        for (int i = 0; i < n; i++)
            sb.Append(data[i].ToString("X2")).Append(' ');
        return sb.ToString().Trim();
    }

    private void Log(string msg)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_logPath)!);
            File.AppendAllText(_logPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {msg}\n");
        }
        catch { }
    }

    private class PendingRequest
    {
        public byte FeatureIndex;
        public byte Fn;
        public TaskCompletionSource<byte[]> Tcs = null!;
    }

    public void Dispose()
    {
        Disconnect();
        Log("=== LogitechHidppWheelProvider disposed ===");
    }
}
