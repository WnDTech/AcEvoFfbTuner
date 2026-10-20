namespace AcEvoFfbTuner.Core.DirectInput.VJoy;

/// <summary>
/// vJoy input bridge for Assetto Corsa 1.
///
/// WHY THIS EXISTS: our DI force effects need an exclusive acquire of the
/// physical wheel, and AC1's engine reads wheel/pedals through DirectInput —
/// one owner starves the other (the game showed no inputs at all). Content
/// Manager avoids this by never taking the wheel: it configures AC's OWN FFB
/// (gain/LUT/effects) and the game renders force. That single-owner model
/// can't run OUR live DSP pipeline — so the bridge gives the game a SECOND
/// device it can own: a vJoy virtual joystick we feed every frame from the
/// physical wheel. The app keeps exclusive on the physical wheel (our FFB
/// rules it); AC1 binds the vJoy device and gets full inputs; AC1's own FFB
/// output targets the vJoy device and is dropped (no callback registered).
///
/// SETUP (one-time, user side):
/// 1. Install the vJoy driver (vjoystick.sourceforge.net), enable device 1
///    with X/Y/Z/Rx axes + 32 buttons (vJoyConf).
/// 2. In the app: enable the AC1 vJoy bridge (Devices/settings toggle).
/// 3. In Content Manager / AC controls: bind steering/gas/brake/clutch to the
///    "vJoy Device". AC's FFB settings become irrelevant (output is dropped).
///
/// Axis map: X = steer (-1..+1), Y = throttle (0..1), Z = brake (0..1),
/// Rx = clutch (0..1). Buttons 1..32 mirror the wheel buttons.
/// vJoy axis range is 1..32768 (center 16384).
/// </summary>
public sealed class VJoyFeeder : IDisposable
{
    public const int AxisMin = 1;
    public const int AxisMax = 32768;
    public const int AxisCenter = 16384;

    private readonly IVJoyNative _native;
    private bool _disposed;

    private uint _deviceId;
    public uint DeviceId
    {
        get => _deviceId;
        set
        {
            if (_deviceId == value) return;
            Disconnect();
            _deviceId = value;
        }
    }

    /// <summary>Diagnostics: frames fed since connect, last feed time, last
    /// native-call failure. Surfaced in the Game Config status + ac_vjoy.log.</summary>
    public long UpdateCount { get; private set; }
    public DateTime LastUpdateUtc { get; private set; }
    public string LastNativeError { get; private set; } = "";
    /// <summary>vJoy device button count as reported at connect (-1 unknown).
    /// Drives the setup UI (slot rows + mismatch hints).</summary>
    public int DeviceButtonCount { get; private set; } = -1;
    /// <summary>File logging kill-switch (unit tests run with fakes and must
    /// not scribble into the production log).</summary>
    public static bool LoggingEnabled { get; set; } = true;
    public bool IsConnected { get; private set; }
    public bool DriverPresent { get; private set; } = true;
    public string LastError { get; private set; } = "";
    public int MaxButtons { get; set; } = 32;

    /// <summary>
    /// Per-slot source mapping: ButtonMap[slot] = physical button index, or
    /// -1 for unmapped (always released), or >= physical count (released).
    /// Default is identity (slot i ← physical i). Applied on every Update so
    /// users can rearrange wheel buttons onto vJoy slots in the setup UI.
    /// </summary>
    public int[] ButtonMap { get; set; } = DefaultButtonMap();

    public static int[] DefaultButtonMap()
    {
        var map = new int[128];
        for (int i = 0; i < map.Length; i++) map[i] = i;
        return map;
    }

    public VJoyFeeder(uint deviceId = 1, IVJoyNative? native = null)
    {
        _deviceId = deviceId;
        _native = native ?? new VJoyNative();
    }

    public bool Connect()
    {
        LastError = "";
        Log($"connect attempt: device {DeviceId}");
        try
        {
            if (!_native.DriverEnabled())
            {
                if (_native is VJoyNative v && !v.DriverPresent) DriverPresent = false;
                LastError = IsDriverInstalled()
                    ? "vJoy driver files found but its interface would not load — reinstall vJoy, then reboot"
                    : "vJoy driver not found — install it (vjoystick.sourceforge.net) and enable device " + DeviceId;
                Log($"connect FAILED: {LastError}");
                return false;
            }

            var status = _native.GetStatus(DeviceId);
            Log($"device {DeviceId} status: {status}");
            if (status == VjdStat.Miss)
            {
                LastError = $"vJoy device {DeviceId} missing — enable it in vJoyConf (X/Y/Z/Rx + buttons)";
                Log($"connect FAILED: {LastError}");
                return false;
            }
            if (status == VjdStat.Busy)
            {
                LastError = $"vJoy device {DeviceId} busy — another feeder owns it";
                Log($"connect FAILED: {LastError}");
                return false;
            }
            // Own = already ours (reconnect after game switch); Free = take it.
            if (status == VjdStat.Free && !_native.Acquire(DeviceId))
            {
                LastError = $"vJoy device {DeviceId} acquire failed";
                Log($"connect FAILED: {LastError}");
                return false;
            }

            _native.Reset(DeviceId);
            IsConnected = true;
            UpdateCount = 0;
            LastUpdateUtc = DateTime.MinValue;
            LastNativeError = "";
            DeviceButtonCount = -1;
            // Clamp the button mirror range to what the device actually has
            // (fresh installs report 8 — writing past it fails and would trip
            // the feed warning forever). Reset toward the default cap first so
            // raising the count in vJoyConf takes effect on next connect.
            try
            {
                int reported = _native.GetButtonCount(DeviceId);
                DeviceButtonCount = reported;
                if (reported > 0)
                    MaxButtons = Math.Min(32, reported);
            }
            catch { }
            Log($"connect OK: device {DeviceId} acquired (buttons {MaxButtons})");
            return true;
        }
        catch (Exception ex)
        {
            LastError = $"vJoy connect failed: {ex.Message}";
            Log($"connect FAILED: {LastError}");
            return false;
        }
    }

    public void Disconnect()
    {
        if (!IsConnected) return;
        Log($"disconnect: device {DeviceId} after {UpdateCount} frames");
        try
        {
            _native.Reset(DeviceId);
            _native.Relinquish(DeviceId);
        }
        catch { }
        IsConnected = false;
        DeviceButtonCount = -1;
    }

    /// <summary>
    /// True when the vJoy driver is actually installed. Layered so it works
    /// regardless of PATH/DLL visibility (vJoyInterface.dll is NOT on the
    /// load path after a stock install, so a P/Invoke probe alone always
    /// reports missing):
    /// 1. vjoy.sys in System32\drivers (the driver itself),
    /// 2. an uninstall registry entry with vJoy in the display name,
    /// 3. fallback: the vJoyInterface API responding.
    /// </summary>
    public static bool IsDriverInstalled()
    {
        try
        {
            string sys = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.System),
                "drivers", "vjoy.sys");
            if (File.Exists(sys)) return true;
        }
        catch { }
        if (HasUninstallEntry()) return true;
        try { return new VJoyNative().DriverEnabled(); }
        catch { return false; }
    }

    private static bool HasUninstallEntry()
    {
        try
        {
            var hives = new (Microsoft.Win32.RegistryHive hive, Microsoft.Win32.RegistryView view)[]
            {
                (Microsoft.Win32.RegistryHive.LocalMachine, Microsoft.Win32.RegistryView.Registry64),
                (Microsoft.Win32.RegistryHive.LocalMachine, Microsoft.Win32.RegistryView.Registry32),
                (Microsoft.Win32.RegistryHive.CurrentUser, Microsoft.Win32.RegistryView.Default),
            };
            foreach (var (hive, view) in hives)
            {
                try
                {
                    using var baseKey = Microsoft.Win32.RegistryKey.OpenBaseKey(hive, view);
                    using var un = baseKey.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall");
                    if (un == null) continue;
                    foreach (var name in un.GetSubKeyNames())
                    {
                        try
                        {
                            using var sub = un.OpenSubKey(name);
                            var display = sub?.GetValue("DisplayName") as string;
                            if (!string.IsNullOrEmpty(display) &&
                                display.Contains("vjoy", StringComparison.OrdinalIgnoreCase))
                                return true;
                        }
                        catch { }
                    }
                }
                catch { }
            }
        }
        catch { }
        return false;
    }

    /// <summary>Feeds one frame. All inputs normalized: steer -1..+1, pedals 0..1.</summary>
    public void Update(float steerNorm, float gas, float brake, float clutch, bool[]? buttons)
    {
        if (!IsConnected) return;
        try
        {
            bool ok = true;
            ok &= _native.SetAxis(MapSteer(steerNorm), DeviceId, HidUsage.X);
            ok &= _native.SetAxis(MapPedal(gas), DeviceId, HidUsage.Y);
            ok &= _native.SetAxis(MapPedal(brake), DeviceId, HidUsage.Z);
            ok &= _native.SetAxis(MapPedal(clutch), DeviceId, HidUsage.Rx);
            int slots = Math.Min(MaxButtons, ButtonMap?.Length ?? MaxButtons);
            if (buttons != null)
            {
                for (int i = 0; i < slots; i++)
                {
                    int src = (ButtonMap != null && i < ButtonMap.Length) ? ButtonMap[i] : i;
                    bool pressed = src >= 0 && src < buttons.Length && buttons[src];
                    ok &= _native.SetButton(pressed, DeviceId, i + 1);
                }
            }
            else
            {
                for (int i = 0; i < slots; i++)
                    ok &= _native.SetButton(false, DeviceId, i + 1);
            }
            if (!ok && LastNativeError == "")
            {
                LastNativeError = "a SetAxis/SetButton call failed — device may have been disabled";
                Log($"feed WARNING: {LastNativeError}");
            }
            UpdateCount++;
            LastUpdateUtc = DateTime.UtcNow;
        }
        catch { }
    }

    internal static void Log(string message)
    {
        if (!LoggingEnabled) return;
        LogAlways(message);
    }

    /// <summary>Unconditional file log (diagnostics callers outside Core).</summary>
    public static void LogAlways(string message)
    {
        try
        {
            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "AcEvoFfbTuner");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "ac_vjoy.log"),
                $"[{DateTime.Now:HH:mm:ss.fff}] {message}\n");
        }
        catch { }
    }

    /// <summary>Steer -1..+1 → vJoy 1..32768 (16384 center).</summary>
    public static int MapSteer(float norm)
        => (int)Math.Clamp((Math.Clamp(norm, -1f, 1f) + 1f) * 0.5f * (AxisMax - AxisMin) + AxisMin, AxisMin, AxisMax);

    /// <summary>Pedal 0..1 → vJoy 1..32768 (1 = released).</summary>
    public static int MapPedal(float value)
        => (int)Math.Clamp(Math.Clamp(value, 0f, 1f) * (AxisMax - AxisMin) + AxisMin, AxisMin, AxisMax);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Disconnect();
    }
}
