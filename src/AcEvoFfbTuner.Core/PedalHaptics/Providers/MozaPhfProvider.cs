using System.IO.Ports;
using AcEvoFfbTuner.Core.Config;

namespace AcEvoFfbTuner.Core.PedalHaptics.Providers;

/// <summary>
/// Driver for the MOZA PHF Pedal Haptic Feedback system
/// (RS102 = PHF Kit control box + 1 module, RS103 = add-on module).
///
/// PROTOCOL (verified 2026-11-06 against a USB capture + hardware test):
/// The control box (VID 0x346E, PID 0x002F) is a composite USB device: a
/// read-only game-controller HID collection, a vendor HID collection, and a
/// CDC serial port (interface MI_00). Effects are driven ONLY over the serial
/// port, using MOZA's standard framing:
///
///   7E LEN 4D 12 | 01 01 PEDAL EFFECT ON DUR_hi DUR_lo FREQ_hi FREQ_lo AMP_hi AMP_lo | CHK
///
///   PEDAL   01 throttle, 02 brake, 03 clutch (clutch not yet hardware-verified)
///   EFFECT  00 TC, 02 lockup, 03 brake threshold, 04 engine, 06 gear shift,
///           07 wheelspin, 08 road texture
///   ON      01 run / 00 stop (stop frame carries zeros)
///   DUR     uint16 BE pulse duration ms (captured 13..400)
///   FREQ    uint16 BE vibration frequency Hz (engine tracked RPM here)
///   AMP     uint16 BE amplitude 0..0xFFFF = strength (0x7FFF = 50%)
///   CHK     (0x0D + sum of every preceding byte) &amp; 0xFF
///
/// The box acknowledges each frame (group 0xCD). Keep-alive pings
/// (7E 00 00 12 9D, 7E 00 00 19 A4) are sent every 500 ms.
///
/// TIMING MATTERS (hardware-verified): re-sending an IDENTICAL frame every
/// ~30 ms restarts the effect and nothing is felt; identical frames must be
/// refreshed at ~100 ms. Frames whose values CHANGE may be sent every ~30 ms.
/// A background loop owns all sending; setters only update per-pedal state.
///
/// Only one process can open a serial port — any other software holding the
/// PHF COM port blocks this provider (Connect fails with access denied).
/// </summary>
public sealed class MozaPhfProvider : IPedalHapticProvider
{
    public const ushort MozaVid = 0x346E;
    public const ushort PhfPid = 0x002F;

    public const byte EffectTc = 0x00;
    public const byte EffectLockup = 0x02;
    public const byte EffectBrakeThreshold = 0x03;
    public const byte EffectEngine = 0x04;
    public const byte EffectGearShift = 0x06;
    public const byte EffectWheelspin = 0x07;
    public const byte EffectRoad = 0x08;

    private const int ChangedMinIntervalMs = 30;
    private const int RefreshIntervalMs = 100;
    private const int PingIntervalMs = 500;
    // Pulse length per frame; frames are refreshed every 100 ms so a 200 ms
    // pulse (the captured TC value) keeps the vibration continuous.
    private const int PulseDurationMs = 200;

    private static readonly byte[] PingMain = [0x7E, 0x00, 0x00, 0x12, 0x9D];
    private static readonly byte[] PingPedals = [0x7E, 0x00, 0x00, 0x19, 0xA4];

    private static readonly string PhfLogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "AcEvoFfbTuner", "moza_phf_log.txt");

    private readonly MozaPhfConfig _config;
    private readonly object _lock = new();
    private SerialPort? _port;
    private Thread? _loop;
    private volatile bool _running;
    private bool _disposed;

    private sealed class PedalState
    {
        public byte Effect = EffectTc;
        public float Intensity;
        public int Rate;
        public byte[]? LastFrame;
        public long LastSentMs;
        public bool Active;
    }

    private readonly PedalState _throttle = new();
    private readonly PedalState _brake = new();
    private readonly PedalState _clutch = new();

    public MozaPhfProvider(MozaPhfConfig config)
    {
        _config = config;
    }

    public string DeviceName => "MOZA PHF Pedal Haptics (RS102/RS103)";
    public bool IsAvailable => _port?.IsOpen == true && !_disposed;

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
                        || name.Equals("PitHouse", StringComparison.OrdinalIgnoreCase)
                        || name.Contains("Racing Lab", StringComparison.OrdinalIgnoreCase)
                        || name.Contains("RacingLab", StringComparison.OrdinalIgnoreCase);
                }
                catch { return false; }
            });
        }
        catch { return false; }
    }

    /// <summary>A MOZA serial port. <see cref="Path"/> is the COM port name.</summary>
    public sealed record PhfCandidate(
        string Path, ushort VendorId, ushort ProductId,
        string Product, bool LikelyPhf);

    /// <summary>Enumerate MOZA serial ports; the PHF control box (PID 0x002F)
    /// is flagged so it can be auto-connected.</summary>
    public static List<PhfCandidate> EnumerateCandidates()
    {
        var result = new List<PhfCandidate>();
        try
        {
            using var searcher = new System.Management.ManagementObjectSearcher(
                "SELECT Name, PNPDeviceID FROM Win32_PnPEntity WHERE PNPDeviceID LIKE '%VID_346E%' AND Name LIKE '%(COM%'");
            foreach (var obj in searcher.Get())
            {
                string name = obj["Name"]?.ToString() ?? "";
                string pnp = obj["PNPDeviceID"]?.ToString() ?? "";
                var m = System.Text.RegularExpressions.Regex.Match(name, @"\((COM\d+)\)");
                if (!m.Success) continue;
                var pidMatch = System.Text.RegularExpressions.Regex.Match(pnp, @"PID_([0-9A-Fa-f]{4})");
                ushort pid = pidMatch.Success ? Convert.ToUInt16(pidMatch.Groups[1].Value, 16) : (ushort)0;
                bool phf = pid == PhfPid;
                string product = phf ? $"MOZA PHF control box ({m.Groups[1].Value})" : $"MOZA device ({m.Groups[1].Value})";
                result.Add(new PhfCandidate(m.Groups[1].Value, MozaVid, pid, product, phf));
            }
        }
        catch (Exception ex)
        {
            Log($"ENUM FAILED: {ex.GetType().Name}: {ex.Message}");
        }

        foreach (var c in result)
            Log($"ENUM: {c.Path} pid=0x{c.ProductId:X4} phf={c.LikelyPhf}");
        return result;
    }

    /// <summary>Connect to the PHF control box's serial port. Empty path =
    /// auto-pick the PHF (PID 0x002F) port.</summary>
    public bool Connect(string? devicePath = null)
    {
        Disconnect();

        string port = devicePath ?? "";
        ushort pid = PhfPid;
        if (string.IsNullOrEmpty(port))
        {
            var pick = EnumerateCandidates().FirstOrDefault(c => c.LikelyPhf);
            if (pick == null)
            {
                Log("CONNECT FAILED: no PHF control box serial port found");
                return false;
            }
            port = pick.Path;
            pid = pick.ProductId;
        }

        try
        {
            var sp = new SerialPort(port, 115200)
            {
                DtrEnable = true,
                RtsEnable = true,
                WriteTimeout = 200,
                ReadTimeout = 50,
                ReadBufferSize = 65536
            };
            sp.Open();

            lock (_lock)
            {
                _port = sp;
                ConnectedPath = port;
                ConnectedPid = pid;
            }

            _running = true;
            _loop = new Thread(SendLoop) { IsBackground = true, Name = "MozaPhfSend", Priority = ThreadPriority.AboveNormal };
            _loop.Start();

            Log($"CONNECT OK: {port}");
            return true;
        }
        catch (Exception ex)
        {
            Log($"CONNECT FAILED: {port}: {ex.GetType().Name}: {ex.Message}"
                + (ex is UnauthorizedAccessException ? " (port is held by another program)" : ""));
            return false;
        }
    }

    public void Disconnect()
    {
        _running = false;
        try { _loop?.Join(500); } catch { }
        _loop = null;

        lock (_lock)
        {
            if (_port != null)
            {
                try
                {
                    if (_port.IsOpen)
                    {
                        SendStopLocked(MozaPhfChannel.Throttle, _throttle);
                        SendStopLocked(MozaPhfChannel.Brake, _brake);
                        SendStopLocked(MozaPhfChannel.Clutch, _clutch);
                    }
                }
                catch { }
                try { _port.Close(); } catch { }
                _port.Dispose();
                _port = null;
                ConnectedPath = "";
            }
            foreach (var s in new[] { _throttle, _brake, _clutch })
            {
                s.Intensity = 0; s.Active = false; s.LastFrame = null;
            }
        }
    }

    public void SetBrakeHaptic(float intensity, HapticSignalType signal)
    {
        if (!IsBrakeSupported) return;
        Update(_brake, intensity, FrequencyFor(signal), EffectFor(signal));
    }

    public void SetGasHaptic(float intensity, HapticSignalType signal)
    {
        if (!IsGasSupported) return;
        Update(_throttle, intensity, FrequencyFor(signal), EffectFor(signal));
    }

    public void SetClutchHaptic(float intensity, HapticSignalType signal)
    {
        if (!IsClutchSupported) return;
        Update(_clutch, intensity, FrequencyFor(signal), EffectFor(signal));
    }

    /// <summary>Direct per-channel drive (intensity 0..1, rate/frequency Hz).
    /// Used by the telemetry feed which blends effects per pedal; runs on the
    /// verified steady-vibration slot (see <see cref="EffectFor"/>).</summary>
    public void SetChannel(MozaPhfChannel channel, float intensity01, int frequencyHz)
    {
        if (!_config.HasModule(channel)) return;
        Update(StateFor(channel), intensity01, frequencyHz, EffectTc);
    }

    public void StopAll()
    {
        lock (_lock)
        {
            foreach (var s in new[] { _throttle, _brake, _clutch })
                s.Intensity = 0;
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

    /// <summary>Effect slot used for every signal. Hardware-verified
    /// 2026-11-06: slot 00 (TC) plays a steady vibration at the requested
    /// rate/intensity; slot 08 (road texture) produces NOTHING for a steady
    /// value (it only responds to a changing value). Other slots are not yet
    /// verified standalone, so all signals use slot 00 — the app already
    /// blends effects per pedal, so one slot per pedal is sufficient.</summary>
    public static byte EffectFor(HapticSignalType signal) => EffectTc;

    public string GetDiagnosticInfo()
        => $"PHF available={IsAvailable} port={ConnectedPath} "
         + $"modules=0b{Convert.ToString(_config.ModuleMask, 2).PadLeft(3, '0')}";

    private PedalState StateFor(MozaPhfChannel ch) => ch switch
    {
        MozaPhfChannel.Throttle => _throttle,
        MozaPhfChannel.Brake => _brake,
        _ => _clutch
    };

    private static byte PedalByte(MozaPhfChannel ch) => ch switch
    {
        MozaPhfChannel.Throttle => 0x01,
        MozaPhfChannel.Brake => 0x02,
        _ => 0x03
    };

    private void Update(PedalState s, float intensity01, int rate, byte effect)
    {
        lock (_lock)
        {
            float scaled = Math.Clamp(intensity01, 0f, 1f) * Math.Clamp(_config.IntensityScale, 0f, 2f);
            // Switching effect slot: stop the old one so it does not linger.
            if (s.Active && s.Effect != effect)
                s.LastFrame = null;
            s.Intensity = Math.Clamp(scaled, 0f, 1f);
            s.Rate = rate;
            s.Effect = effect;
        }
    }

    /// <summary>Build a run (or stop) frame. Public for tests.
    /// Field semantics (from the capture, confirmed by feel 2026-11-06):
    /// bytes 5-6 = pulse duration ms, 7-8 = vibration frequency Hz (engine
    /// tracked RPM here), 9-10 = amplitude 0..0xFFFF (the user's strength:
    /// 0x7FFF = 50%, 0xCCCC = 80%, 0x1999 = 10%).</summary>
    public static byte[] BuildFrame(byte pedal, byte effect, bool on, int durationMs, int frequencyHz, ushort amplitude)
    {
        var f = new byte[16];
        f[0] = 0x7E; f[1] = 0x0B; f[2] = 0x4D; f[3] = 0x12;
        f[4] = 0x01; f[5] = 0x01; f[6] = pedal; f[7] = effect;
        if (on)
        {
            durationMs = Math.Clamp(durationMs, 1, 1000);
            frequencyHz = Math.Clamp(frequencyHz, 1, 100);
            f[8] = 0x01;
            f[9] = (byte)(durationMs >> 8); f[10] = (byte)durationMs;
            f[11] = (byte)(frequencyHz >> 8); f[12] = (byte)frequencyHz;
            f[13] = (byte)(amplitude >> 8); f[14] = (byte)amplitude;
        }
        f[15] = Checksum(f, 15);
        return f;
    }

    public static byte Checksum(byte[] buf, int len)
    {
        int sum = 0x0D;
        for (int i = 0; i < len; i++) sum += buf[i];
        return (byte)(sum & 0xFF);
    }

    private void SendLoop()
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        long nextPing = 0;
        while (_running)
        {
            try
            {
                lock (_lock)
                {
                    var port = _port;
                    if (port == null || !port.IsOpen) break;
                    long now = sw.ElapsedMilliseconds;

                    if (now >= nextPing)
                    {
                        port.Write(PingMain, 0, PingMain.Length);
                        port.Write(PingPedals, 0, PingPedals.Length);
                        nextPing = now + PingIntervalMs;
                    }

                    Service(MozaPhfChannel.Throttle, _throttle, now);
                    Service(MozaPhfChannel.Brake, _brake, now);
                    Service(MozaPhfChannel.Clutch, _clutch, now);

                    // Acks/status stream back continuously — drain them.
                    if (port.BytesToRead > 0) port.DiscardInBuffer();
                }
            }
            catch (Exception ex)
            {
                Log($"SEND LOOP ERROR: {ex.GetType().Name}: {ex.Message}");
                Thread.Sleep(200);
            }
            Thread.Sleep(5);
        }
    }

    private void Service(MozaPhfChannel ch, PedalState s, long now)
    {
        int amp = (int)MathF.Round(s.Intensity * 0xFFFF);
        if (amp < 0x0100) // < ~0.4% — treat as off
        {
            if (s.Active) SendStopLocked(ch, s);
            return;
        }

        var frame = BuildFrame(PedalByte(ch), s.Effect, true, PulseDurationMs,
            s.Rate, (ushort)Math.Min(amp, 0xFFFF));
        bool changed = s.LastFrame == null || !s.LastFrame.AsSpan().SequenceEqual(frame);
        long since = now - s.LastSentMs;

        // Identical frames too often restart the effect (nothing felt);
        // refresh them at ~100 ms. Changed values may go every ~30 ms.
        if ((changed && since >= ChangedMinIntervalMs) || since >= RefreshIntervalMs)
        {
            if (s.Active && s.LastFrame != null && s.LastFrame[7] != s.Effect)
                Write(BuildFrame(PedalByte(ch), s.LastFrame[7], false, 0, 0, 0));
            Write(frame);
            s.LastFrame = frame;
            s.LastSentMs = now;
            s.Active = true;
        }
    }

    private void SendStopLocked(MozaPhfChannel ch, PedalState s)
    {
        byte effect = s.LastFrame?[7] ?? s.Effect;
        Write(BuildFrame(PedalByte(ch), effect, false, 0, 0, 0));
        s.Active = false;
        s.LastFrame = null;
    }

    private void Write(byte[] frame)
    {
        _port?.Write(frame, 0, frame.Length);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Disconnect();
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
}
