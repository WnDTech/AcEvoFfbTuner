// LogitechHidppWheelProvider.cs — direct HID++ settings provider for
// Logitech direct-drive wheels (RS50, G PRO) and the G923.
//
// Part of AC Evo FFB Tuner.
// Copyright (C) 2026 WnDTech
//
// This program is free software; you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation; version 2 of the License.
//
// This program is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
// GNU General Public License for more details.
//
// You should have received a copy of the GNU General Public License along
// with this program; if not, see <https://www.gnu.org/licenses/>.
//
// Protocol reference and implementation details from mescon's
// logitech-trueforce-linux-driver
// (https://github.com/mescon/logitech-trueforce-linux-driver),
// Copyright (c) mescon, GPL-2.0. Dynamic OLED (DisplayGameData 0x8130)
// writes verified on hardware with PeposCJ.
using System.Runtime.InteropServices;
using System.Text;
using AcEvoFfbTuner.Core.DeviceDetection;

namespace AcEvoFfbTuner.Core.FfbProviders;

/// <summary>
/// Direct HID++ settings provider for Logitech direct-drive wheels (RS50, G PRO)
/// and the G923. Talks to the wheel's HID++ interface (interface 1, usage page
/// 0xFF43) WITHOUT any Logitech software — G HUB is not required and must stay closed.
///
/// Protocol reference: mescon/logitech-trueforce-linux-driver
/// docs/PROTOCOL_SPECIFICATION.md (reverse-engineered, verified on RS50 hardware):
///   - Short HID++ reports (0x10, 7 bytes) for all commands, device index 0xFF
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
///   - sw_id 0x0A (Linux driver convention). DD wheels answer with sw_id 0 in
///     the response — responses are matched by feature index + function nibble.
///
/// Windows transport notes:
///   - The RS50 exposes the HID++ protocol as three report-ID collections on
///     mi_01 (usage page 0xFF43): 0x10 short (7B), 0x11 long (20B),
///     0x12 very-long (64B). G HUB writes short reports to the 0x10 collection.
///   - Responses are read from ALL collections via interrupt ReadFile AND via
///     HidD_GetInputReport control reads (some Windows builds deliver HID++
///     responses only on the control path).
///   - The TrueForce stream collection (usage page 0xFFFD) is never written to.
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
    private const ushort FeatOled = 0x8130; // DisplayGameData — the Dynamic OLED

    private const byte SwId = 0x0A;

    /// <summary>SW-ID for OLED (0x8130) exchanges — distinct from the settings
    /// surface so replies can never be mistaken across features (per the
    /// mescon/issue-20 probe-hazard finding: root getFeature replies do not
    /// echo the page they answer).</summary>
    private const byte OledSwId = 0x0B;
    private const int ResponseTimeoutMs = 1000;

    private static readonly IntPtr InvalidHandle = new(-1);

    private const uint GENERIC_READ = 0x80000000;
    private const uint GENERIC_WRITE = 0x40000000;
    private const uint FILE_SHARE_RW = 0x03;
    private const uint OPEN_EXISTING = 3;
    private const uint FILE_FLAG_OVERLAPPED = 0x40000000;
    private const uint WAIT_TIMEOUT = 0x00000102;

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr CreateFile(string path, uint access, uint share, IntPtr sec, uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr h);

    [DllImport("hid.dll", SetLastError = true)]
    private static extern bool HidD_SetOutputReport(IntPtr dev, byte[] buf, int len);

    [DllImport("hid.dll", SetLastError = true)]
    private static extern bool HidD_GetInputReport(IntPtr dev, byte[] buf, uint len);

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

    private IntPtr _writeHandle = InvalidHandle;
    private int _writeReportLen = 7;
    private readonly List<ReadChannel> _readChannels = new();
    private string _devicePath = "";
    private string _productName = "";
    private volatile bool _running;
    private int _readThreadCount;

    // HID++ is strictly request/response and the wheel has one in-flight slot —
    // concurrent requests (UI refresh + slider write) could cross-talk responses
    // between features. Serialize every exchange.
    private readonly SemaphoreSlim _requestGate = new(1, 1);

    private readonly object _sync = new();
    private readonly List<PendingRequest> _pending = new();

    private byte _featureFfbStrength;
    private byte _featureRotation;
    private byte _featureProfile;
    private byte _featureTrueForce;
    private byte _featureDamping;
    private byte _featureOled;

    // The OLED (DisplayGameData) frame writes need the 64-byte very-long OUT
    // collection — separate from the short-report settings handle.
    private IntPtr _oledWriteHandle = InvalidHandle;

    private readonly string _logPath;

    public LogitechHidppWheelProvider()
    {
        _logPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "AcEvoFfbTuner", "logitech_hidpp.log");
        Log("=== LogitechHidppWheelProvider created ===");
    }

    public bool IsConnected => _writeHandle != InvalidHandle;
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

        // The RS50/G PRO expose the HID++ protocol as three report-ID collections
        // on mi_01 (usage page 0xFF43): 0x10 short (7B), 0x11 long (20B),
        // 0x12 very-long (64B). The TrueForce stream (0xFFFD) must never be written to.
        var wheelDevices = devices
            .Where(d => SupportedPids.Contains(d.ProductId))
            .Where(d => d.UsagePage != 0xFFFD)
            .ToList();

        var writeCandidates = wheelDevices
            .Where(d => d.OutputReportByteLength >= 7)
            .OrderBy(d => d.OutputReportByteLength)
            .ToList();

        // The wheel broadcasts each state report on ALL report-ID collections
        // (0x10 short, 0x11 long, 0x12 very-long). The 0x12 (64-byte) form is the
        // canonical one and arrives via HidD_GetInputReport — reading only that
        // collection avoids duplicate reports satisfying the wrong request.
        // The TrueForce stream (0xFFFD) and the joystick (page 0x0001) are excluded.
        var readCollections = wheelDevices
            .Where(d => d.UsagePage == 0xFF43 && d.InputReportByteLength >= 64)
            .ToList();

        Log($"Connect: {writeCandidates.Count} write candidate(s), {readCollections.Count} read collection(s)");

        foreach (var wc in writeCandidates)
        {
            Log($"Connect: probing write \"{wc.ProductString}\" (page 0x{wc.UsagePage:X4}, outLen={wc.OutputReportByteLength})...");

            IntPtr wh = CreateFile(wc.DevicePath, GENERIC_WRITE, FILE_SHARE_RW, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
            if (wh == InvalidHandle)
            {
                Log($"Connect:   write open FAILED (err={Marshal.GetLastWin32Error()}) — skipping");
                continue;
            }

            _writeHandle = wh;
            _writeReportLen = wc.OutputReportByteLength;
            _devicePath = wc.DevicePath;
            _productName = wc.ProductString;

            // Open every read collection (interrupt ReadFile + control GetInputReport).
            int opened = 0;
            foreach (var rc in readCollections)
            {
                IntPtr rh = CreateFile(rc.DevicePath, GENERIC_READ, FILE_SHARE_RW, IntPtr.Zero, OPEN_EXISTING, FILE_FLAG_OVERLAPPED, IntPtr.Zero);
                if (rh == InvalidHandle)
                {
                    Log($"Connect:   read open FAILED for inLen={rc.InputReportByteLength} (err={Marshal.GetLastWin32Error()})");
                    continue;
                }
                var channel = new ReadChannel
                {
                    Handle = rh,
                    InLen = rc.InputReportByteLength,
                    ReportId = (byte)(rc.Usage == 0x0701 ? 0x10 : rc.Usage == 0x0702 ? 0x11 : 0x12),
                    Event = CreateEvent(IntPtr.Zero, false, false, null)
                };
                _readChannels.Add(channel);
                opened++;
            }
            Log($"Connect:   opened {opened} read channel(s)");

            if (_readChannels.Count == 0)
            {
                Log("Connect:   no read channels — closing write handle");
                CloseHandle(_writeHandle);
                _writeHandle = InvalidHandle;
                continue;
            }

            StartReadThreads();

            byte? probe = FindFeatureIndex(FeatFfbStrength, 900);
            if (probe is null or 0x00 or 0xFF)
            {
                Log($"Connect:   probe FAILED (feature 0x{FeatFfbStrength:X4} not found on this write candidate) — closing");
                StopReadThreads();
                CloseHandle(_writeHandle);
                _writeHandle = InvalidHandle;
                continue;
            }

            _featureFfbStrength = probe.Value;
            Log($"Connect:   HID++ interface confirmed — FFBStrength at index 0x{probe:X2} (write outLen={_writeReportLen}, {_readChannels.Count} read channels)");
            break;
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
        _featureOled = FindFeatureIndex(FeatOled, 800) ?? 0;
        Log($"Connect: feature indices — rotation=0x{_featureRotation:X2} profile=0x{_featureProfile:X2} trueforce=0x{_featureTrueForce:X2} damping=0x{_featureDamping:X2} oled=0x{_featureOled:X2}");

        // OLED frame writes need the 64-byte very-long OUT collection — open it
        // best-effort (the OLED feature is optional; nothing fails without it).
        if (_featureOled != 0 && _oledWriteHandle == InvalidHandle)
        {
            foreach (var wc in wheelDevices.Where(d => d.UsagePage == 0xFF43 && d.OutputReportByteLength >= 60))
            {
                IntPtr oh = CreateFile(wc.DevicePath, GENERIC_WRITE, FILE_SHARE_RW, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
                if (oh == InvalidHandle)
                {
                    Log($"Connect:   OLED very-long OUT open FAILED (err={Marshal.GetLastWin32Error()})");
                    continue;
                }
                _oledWriteHandle = oh;
                Log($"Connect: OLED very-long OUT collection opened (outLen={wc.OutputReportByteLength})");
                break;
            }
        }

        // Read current state
        ReadAllSettings();

        DiagnosticSummary = $"{_productName}: strength={FfbStrengthNm:F1} Nm, rotation={RotationDegrees}°, mode={(IsDesktopMode ? "desktop" : ProfileMode == 0xFF ? "unknown" : $"onboard slot {ProfileMode}")}, trueforce={TrueForceLevelPct}%, damping={DampingPct}%";
        Log($"Connect: DONE — {DiagnosticSummary}");
        return true;
    }

    public void Disconnect()
    {
        Log("Disconnect: stopping read threads and closing handles");
        StopReadThreads();

        lock (_sync)
        {
            foreach (var p in _pending)
                p.Tcs.TrySetCanceled();
            _pending.Clear();
        }

        // Serialize against in-flight request threads: an exchange holds
        // _requestGate for up to timeoutMs while inside a P/Invoke on a
        // handle we are about to close (a Task.Run OLED push can be mid-write).
        _requestGate.Wait(TimeSpan.FromSeconds(2));
        try
        {
            if (_writeHandle != InvalidHandle)
            {
                CloseHandle(_writeHandle);
                _writeHandle = InvalidHandle;
            }
            if (_oledWriteHandle != InvalidHandle)
            {
                CloseHandle(_oledWriteHandle);
                _oledWriteHandle = InvalidHandle;
            }
        }
        finally
        {
            _requestGate.Release();
        }
        foreach (var ch in _readChannels)
        {
            if (ch.Handle != InvalidHandle) CloseHandle(ch.Handle);
            if (ch.Event != IntPtr.Zero) CloseHandle(ch.Event);
        }
        _readChannels.Clear();
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
            if (readBack >= 90 && readBack <= 2700)
            {
                RotationDegrees = readBack;
                Log($"SetRotationDegrees: read-back {readBack}°");
            }
            else
            {
                Log($"SetRotationDegrees: read-back {readBack}° OUT OF RANGE (90-2700) — keeping previous value");
            }
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

    /// <summary>Select an onboard profile slot 1-5 (its stored settings then
    /// apply and PERSIST across wheel restarts — desktop mode/G HUB profile 0
    /// does NOT persist, user-verified on the RS50).</summary>
    public bool SetOnboardSlot(byte slot)
    {
        Log($"SetOnboardSlot: slot {slot}");
        if (_featureProfile == 0 || !SendSet(_featureProfile, 2, [slot, 0x00, 0x00]))
            return false;
        ProfileMode = slot;
        Thread.Sleep(50);
        ReadProfileMode();
        Log($"SetOnboardSlot: mode now {(IsDesktopMode ? "desktop" : ProfileMode.ToString())}");
        return !IsDesktopMode && ProfileMode == slot;
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
        ReadProfileMode();

        var strength = ReadValue(_featureFfbStrength, 1);
        if (strength != null)
        {
            ushort v = (ushort)((strength[4] << 8) | strength[5]);
            float readNm = v / 8191.875f;
            if (readNm >= 0.5f && readNm <= 8.5f)
            {
                FfbStrengthNm = readNm;
                Log($"ReadAllSettings: strength 0x{v:X4} = {readNm:F2} Nm");
            }
            else
            {
                Log($"ReadAllSettings: strength read-back 0x{v:X4} = {readNm:F2} Nm OUT OF RANGE (1-8 Nm) — ignoring");
            }
        }
        else
        {
            Log("ReadAllSettings: strength read FAILED/timeout");
        }

        var rotation = ReadValue(_featureRotation, 1);
        if (rotation != null)
        {
            int readBack = (rotation[4] << 8) | rotation[5];
            if (readBack >= 90 && readBack <= 2700)
            {
                RotationDegrees = readBack;
                Log($"ReadAllSettings: rotation {RotationDegrees}°");
            }
            else
            {
                Log($"ReadAllSettings: rotation read-back {readBack}° OUT OF RANGE (90-2700) — ignoring");
            }
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

    public void ReadProfileMode()    {
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

        // A stale duplicate of an earlier discovery response could satisfy this
        // query with the previous feature's index — retry once if it collides.
        byte[] known = [_featureFfbStrength, _featureRotation, _featureProfile, _featureTrueForce, _featureDamping, _featureOled];
        if (idx != 0x00 && idx != 0xFF && known.Contains(idx))
        {
            Log($"FindFeatureIndex: 0x{featureId:X4} index 0x{idx:X2} collides with a previously discovered feature — retrying once");
            Thread.Sleep(120);
            resp = RequestResponse(0x00, 0x00, [(byte)(featureId >> 8), (byte)(featureId & 0xFF), 0x00], timeoutMs);
            if (resp == null)
            {
                Log($"FindFeatureIndex: 0x{featureId:X4} retry no response");
                return null;
            }
            idx = resp[4];
            Log($"FindFeatureIndex: 0x{featureId:X4} retry -> index 0x{idx:X2}");
        }
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

    // ── Dynamic OLED (feature 0x8130 DisplayGameData) ────────────────────────
    // Transport: HID++ SET_REPORT on the 0xFF43 interface (never the DI Escape
    // path — its Acquire/Unacquire lifecycle stomps 0x8123). Typed renderer,
    // 10 layouts A-J; layout J exposes four text fields 19/10/19/10 chars.
    // Safe to drive here because our force rides the TrueForce stream and the
    // HID++ endpoint is force-free (12.5: non-force writes cut force only when
    // force is present on the endpoint — verified by TF4ALL/PeposCJ hardware).
    public bool OledAvailable => _featureOled != 0 && _oledWriteHandle != InvalidHandle;
    public int OledLayoutCount { get; private set; }
    public string OledLayoutJDescriptorHex { get; private set; } = "";

    /// <summary>fn0 layout count + fn1 layout-J descriptor — read-only, safe.</summary>
    public bool OledQueryLayouts(int timeoutMs = 1000)
    {
        if (_featureOled == 0) return false;
        var resp = RequestResponse(_featureOled, 0x00, [0x00, 0x00, 0x00], timeoutMs);
        if (resp == null)
        {
            Log("OledQueryLayouts: fn0 (layout count) no response");
            return false;
        }
        OledLayoutCount = resp[4];
        Log($"OledQueryLayouts: fn0 layout count = {OledLayoutCount}");

        var desc = RequestResponse(_featureOled, 0x01, [(byte)Math.Max(0, OledLayoutCount - 1), 0x00, 0x00], timeoutMs);
        if (desc == null)
            desc = RequestResponse(_featureOled, 0x01, [0x00, 0x00, 0x00], timeoutMs);
        if (desc != null)
        {
            OledLayoutJDescriptorHex = Convert.ToHexString(desc);
            Log($"OledQueryLayouts: fn1 layout descriptor (raw) = {OledLayoutJDescriptorHex}");
        }
        else
        {
            Log("OledQueryLayouts: fn1 layout descriptor no response");
        }
        return true;
    }

    /// <summary>fn2 — clear pending Dynamic data (returns the panel to its
    /// default screen).</summary>
    public bool OledClear()
    {
        if (_featureOled == 0) return false;
        bool ok = RequestResponse(_featureOled, 0x02, [0x00, 0x00, 0x00], ResponseTimeoutMs) != null;
        Log($"OledClear: {(ok ? "ok" : "FAILED")}");
        return ok;
    }

    /// <summary>fn3 — set layout + typed field text. Fields are padded with
    /// spaces to their layout capacity (spaces are content; the firmware
    /// renderer draws fixed-width fields). Layout J = 19/10/19/10.</summary>
    public bool OledWriteFrame(byte layoutIndex, string[] fields, int[] capacities, int timeoutMs = 1000)
    {
        if (_featureOled == 0 || _oledWriteHandle == InvalidHandle) return false;
        if (fields.Length == 0 || fields.Length != capacities.Length) return false;
        if (OledLayoutCount > 0 && layoutIndex >= OledLayoutCount)
        {
            Log($"OledWriteFrame: layout {layoutIndex} exceeds queried count {OledLayoutCount} — rejected");
            return false;
        }

        var caps = new int[capacities.Length];
        for (int i = 0; i < capacities.Length; i++)
            caps[i] = Math.Max(0, capacities[i]);
        var payload = new byte[1 + caps.Sum()];
        if (payload.Length > 60) return false;
        payload[0] = layoutIndex;
        int off = 1;
        for (int i = 0; i < fields.Length; i++)
        {
            string text = fields[i] ?? "";
            int cap = caps[i];
            for (int j = 0; j < cap; j++)
                payload[off + j] = j < text.Length ? (byte)text[j] : (byte)0x20;
            off += cap;
        }

        var resp = RequestResponseVeryLong(_featureOled, 0x03, payload, timeoutMs);
        Log($"OledWriteFrame: layout {(char)('A' + layoutIndex)} (fn3) {(resp != null ? "ok" : "FAILED")}");
        return resp != null;
    }

    private byte[]? RequestResponseVeryLong(byte featureIndex, byte fn, byte[] payload, int timeoutMs)
    {
        if (payload.Length > 60) return null;
        _requestGate.Wait();
        try
        {
            var tcs = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
            var pending = new PendingRequest { FeatureIndex = featureIndex, Fn = fn, Tcs = tcs };
            lock (_sync) _pending.Add(pending);

            byte[] buf = new byte[64];
            buf[0] = 0x12; // very-long report — the only collection wide enough for a frame
            buf[1] = 0xFF;
            buf[2] = featureIndex;
            buf[3] = (byte)((fn << 4) | OledSwId);
            Array.Copy(payload, 0, buf, 4, payload.Length);

            Log($"TX(vlong): {Hex(buf, 12)} (len 64, {payload.Length} payload bytes)");

            bool sent = false;
            try
            {
                sent = HidD_SetOutputReport(_oledWriteHandle, buf, buf.Length);
            }
            catch (Exception ex)
            {
                Log($"TX(vlong): HidD_SetOutputReport threw: {ex.Message}");
            }
            if (!sent)
            {
                Log($"TX(vlong): HidD_SetOutputReport returned false (err={Marshal.GetLastWin32Error()})");
                lock (_sync) _pending.Remove(pending);
                return null;
            }

            try
            {
                if (!tcs.Task.Wait(timeoutMs))
                {
                    Log($"RX: timeout waiting for OLED response (feat 0x{featureIndex:X2} fn={fn})");
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
                Thread.Sleep(20); // let duplicate broadcasts drain before the next exchange
            }
        }
        finally
        {
            _requestGate.Release();
        }
    }

    private byte[]? RequestResponse(byte featureIndex, byte fn, byte[] payload, int timeoutMs)
    {
        // Serialize all HID++ exchanges — the wheel has a single in-flight slot
        // and concurrent requests can cross-talk responses between features.
        _requestGate.Wait();
        try
        {
            return RequestResponseCore(featureIndex, fn, payload, timeoutMs);
        }
        finally
        {
            _requestGate.Release();
        }
    }

    private byte[]? RequestResponseCore(byte featureIndex, byte fn, byte[] payload, int timeoutMs)
    {
        var tcs = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = new PendingRequest { FeatureIndex = featureIndex, Fn = fn, Tcs = tcs };
        lock (_sync)
        {
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
            // The wheel broadcasts each report on multiple collections — let the
            // duplicate copies drain before the next request so a stale copy can
            // never satisfy it.
            Thread.Sleep(40);
        }
    }

    // ── Read threads: interrupt ReadFile AND control GetInputReport on every collection ──

    private void StartReadThreads()
    {
        _running = true;
        foreach (var ch in _readChannels)
        {
            ch.Thread = new Thread(() => ReadLoop(ch))
            {
                IsBackground = true,
                Name = $"LogitechHidppRead-{ch.InLen}"
            };
            ch.Thread.Start();

            // Control-pipe reader as well — some Windows builds only deliver
            // HID++ responses via HidD_GetInputReport.
            ch.InputThread = new Thread(() => InputReportLoop(ch))
            {
                IsBackground = true,
                Name = $"LogitechHidppInput-{ch.InLen}"
            };
            ch.InputThread.Start();
        }
    }

    private void StopReadThreads()
    {
        _running = false;
        foreach (var ch in _readChannels)
        {
            if (ch.Event != IntPtr.Zero && ch.Handle != InvalidHandle)
            {
                try { CancelIoEx(ch.Handle, IntPtr.Zero); } catch { }
            }
        }
        // Drain fully before Disconnect closes any handle — a thread still inside
        // WaitForSingleObject/ReadFile on a closed handle is undefined behaviour.
        foreach (var ch in _readChannels)
        {
            for (int i = 0; i < 10 && ch.Thread?.IsAlive == true; i++)
                ch.Thread.Join(100);
            for (int i = 0; i < 10 && ch.InputThread?.IsAlive == true; i++)
                ch.InputThread.Join(100);
            ch.Thread = null;
            ch.InputThread = null;
        }
    }

    private void ReadLoop(ReadChannel ch)
    {
        Log($"ReadLoop: started (inLen={ch.InLen})");
        var buf = new byte[ch.InLen];
        var overlapped = new NativeOverlapped();
        overlapped.EventHandle = ch.Event;

        while (_running && ch.Handle != InvalidHandle)
        {
            try
            {
                if (!ReadLoopIteration(ch, buf, ref overlapped))
                    break;
            }
            catch (Exception ex)
            {
                Log($"ReadLoop({ch.InLen}): unexpected exception — {ex.GetType().Name}: {ex.Message}");
                break;
            }
        }

        Log($"ReadLoop({ch.InLen}): stopped");
    }

    private bool ReadLoopIteration(ReadChannel ch, byte[] buf, ref NativeOverlapped overlapped)
    {
        ResetEvent(ch.Event);

        uint read = 0;
        bool ok;
        try
        {
            ok = ReadFile(ch.Handle, buf, (uint)ch.InLen, out read, ref overlapped);
        }
        catch (Exception ex)
        {
            Log($"ReadLoop({ch.InLen}): ReadFile threw: {ex.Message}");
            return false;
        }

        if (!ok)
        {
            int err = Marshal.GetLastWin32Error();
            if (err == 997) // ERROR_IO_PENDING
            {
                uint wait = WaitForSingleObject(ch.Event, 250);
                if (wait == WAIT_TIMEOUT)
                {
                    CancelIoEx(ch.Handle, IntPtr.Zero);
                    WaitForSingleObject(ch.Event, 250);
                    if (!_running) return false;
                    return true;
                }
                if (!_running) return false;
            }
            else if (err == 995 || err == 6 || err == 1)
            {
                return false;
            }
            else
            {
                Log($"ReadLoop({ch.InLen}): ReadFile error {err}");
                return false;
            }
        }

        if (!_running) return false;

        if (read >= 4)
            DispatchResponse(buf, (int)read, $"ReadFile({ch.InLen})", ch);
        else if (read > 0)
            Log($"ReadLoop({ch.InLen}): short read {read} bytes");
        return true;
    }

    private void InputReportLoop(ReadChannel ch)
    {
        Log($"InputReportLoop: started (inLen={ch.InLen}, reportId=0x{ch.ReportId:X2})");
        var buf = new byte[ch.InLen];
        buf[0] = ch.ReportId;

        while (_running && ch.Handle != InvalidHandle)
        {
            bool ok;
            try
            {
                ok = HidD_GetInputReport(ch.Handle, buf, (uint)ch.InLen);
            }
            catch (Exception ex)
            {
                Log($"InputReportLoop({ch.InLen}): threw: {ex.Message}");
                break;
            }
            if (!ok)
            {
                int err = Marshal.GetLastWin32Error();
                if (!_running || err == 995 || err == 6 || err == 1)
                    break;
                Log($"InputReportLoop({ch.InLen}): error {err}");
                break;
            }

            if (_running)
            {
                try
                {
                    DispatchResponse(buf, ch.InLen, $"GetInputReport({ch.InLen})", ch);
                }
                catch (Exception ex)
                {
                    Log($"InputReportLoop({ch.InLen}): dispatch exception — {ex.GetType().Name}: {ex.Message}");
                    break;
                }
            }
        }

        Log($"InputReportLoop({ch.InLen}): stopped");
    }

    private void DispatchResponse(byte[] report, int len, string source, ReadChannel? channel = null)
    {
        byte reportId = report[0];
        if (reportId != 0x12 && reportId != 0x11 && reportId != 0x10)
        {
            Log($"RX({source}): unexpected report id 0x{reportId:X2} ({len} bytes)");
            return;
        }

        byte featureIndex = report[2];
        byte fn = (byte)(report[3] >> 4);

        // A pending request's answer is ALWAYS delivered — the wheel answers a
        // GET with the same state bytes it broadcasts, so byte-dedup must never
        // suppress a response to an outstanding request.
        PendingRequest? match = null;
        lock (_sync)
        {
            match = _pending.FirstOrDefault(p => p.FeatureIndex == featureIndex && p.Fn == fn);
        }
        if (match != null)
        {
            Log($"RX({source}): id=0x{reportId:X2} feat=0x{featureIndex:X2} fn={fn} {Hex(report, 10)}");
            // The caller's read loop reuses this buffer — hand off a copy so the
            // response cannot be overwritten by the next read before it is consumed.
            var copy = new byte[len];
            Array.Copy(report, copy, len);
            match.Tcs.TrySetResult(copy);
            return;
        }

        // Unsolicited broadcast — suppress identical repeats (the wheel re-sends
        // its state stream and HidD_GetInputReport returns the cached report).
        // Two-slot history: the field flood alternated between TWO reports, so
        // a single last-hash never fired.
        if (channel != null)
        {
            long hash = 17;
            int n = Math.Min(len, 16);
            for (int i = 0; i < n; i++)
                hash = hash * 31 + report[i];
            if (hash == channel.LastHash || hash == channel.PrevHash)
            {
                // Duplicate flood: the wheel replays its reports at kHz while a
                // session is active — yield briefly instead of spinning and
                // writing a log line per report.
                Thread.Sleep(2);
                return;
            }
            channel.PrevHash = channel.LastHash;
            channel.LastHash = hash;

            // New unsolicited report — brief yield so the read loops cannot spin
            // at kHz on a talkative wheel, then cap the log line at ~1/s.
            Thread.Sleep(1);
            long nowMs = Environment.TickCount64;
            if (nowMs - channel.LastLogMs < 1000)
                return;
            channel.LastLogMs = nowMs;
        }

        Log($"RX({source}): unsolicited event (feat 0x{featureIndex:X2} fn={fn}) — no pending request");
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

    private sealed class ReadChannel
    {
        public IntPtr Handle;
        public int InLen;
        public byte ReportId;
        public IntPtr Event;
        public Thread? Thread;
        public Thread? InputThread;
        public long LastHash; // duplicate suppression across both read loops
        public long PrevHash; // two-slot history — the wheel alternates between reports
        public long LastLogMs; // ~1 Hz cap on the unsolicited-event log line
    }

    private sealed class PendingRequest
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
