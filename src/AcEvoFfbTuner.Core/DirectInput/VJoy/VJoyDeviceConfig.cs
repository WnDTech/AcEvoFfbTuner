using System.Diagnostics;

namespace AcEvoFfbTuner.Core.DirectInput.VJoy;

/// <summary>Outcome of a vJoy device reconfiguration.</summary>
public sealed class DeviceConfigResult
{
    public bool Success { get; init; }
    public string Message { get; init; } = "";
    public int ButtonCount { get; init; } = -1;
}

/// <summary>
/// Reconfigures a vJoy device (button count) through vJoy's own official
/// console tool — no HID descriptors hand-built, no vJoyConf clicking:
/// `vJoyConfig.exe &lt;id&gt; -f -a x y z rx ... -b N` rewrites the device's
/// HID descriptor in the registry and restarts the device.
///
/// Current axes + POV hats are read live first and passed back in, so only
/// the button count changes. FFB effects are deliberately NOT preserved:
/// bridge devices never consume vJoy FFB output (the game targets the
/// virtual device and it is dropped by design).
/// Needs admin (registry + device restart) — runs elevated, one UAC prompt.
/// </summary>
public static class VJoyDeviceConfig
{
    /// <summary>
    /// First-time vJoy button count (128 = HID max, covers every racing wheel
    /// on the market). Set by the installer and/or auto-reconfigured on first
    /// bridge engage. Only the app should ever touch this — fresh installs
    /// ship with 8 by default.
    /// </summary>
    public const int DefaultTargetButtons = 128;

    private static readonly string[] AxisNames = ["x", "y", "z", "rx", "ry", "rz", "sl0", "sl1"];
    private static readonly HidUsage[] AxisUsages =
        [HidUsage.X, HidUsage.Y, HidUsage.Z, HidUsage.Rx, HidUsage.Ry, HidUsage.Rz, HidUsage.Sl0, HidUsage.Sl1];

    /// <summary>Pure arg builder (unit-tested).</summary>
    public static string BuildArgs(uint deviceId, bool[] axes, int discPovs, int contPovs, int buttons)
    {
        var parts = new List<string> { deviceId.ToString(), "-f", "-a" };
        for (int i = 0; i < Math.Min(axes.Length, AxisNames.Length); i++)
            if (axes[i]) parts.Add(AxisNames[i]);
        parts.Add("-b");
        parts.Add(Math.Clamp(buttons, 0, 128).ToString());
        if (contPovs > 0) { parts.Add("-p"); parts.Add(Math.Min(contPovs, 4).ToString()); }
        else if (discPovs > 0) { parts.Add("-s"); parts.Add(Math.Min(discPovs, 4).ToString()); }
        return string.Join(" ", parts);
    }

    public static string? LocateConfigTool()
    {
        string sub = Environment.Is64BitProcess ? "x64" : "x86";
        var roots = new List<string>();
        try { roots.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "vJoy")); }
        catch { }
        try
        {
            string? x86 = Environment.GetEnvironmentVariable("ProgramFiles(x86)");
            if (!string.IsNullOrEmpty(x86)) roots.Add(Path.Combine(x86, "vJoy"));
        }
        catch { }
        foreach (var root in roots.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                string cand = Path.Combine(root, sub, "vJoyConfig.exe");
                if (File.Exists(cand)) return cand;
            }
            catch { }
        }
        return null;
    }

    /// <summary>
    /// Sets the device button count, preserving axes + POVs. Relinquishes the
    /// feeder first (caller) — the restart drops all claims anyway.
    /// </summary>
    public static async Task<DeviceConfigResult> SetButtonCountAsync(
        uint deviceId, int buttons, IVJoyNative native, Action<string>? log = null,
        CancellationToken ct = default)
    {
        buttons = Math.Clamp(buttons, 0, 128);
        string? tool = LocateConfigTool();
        if (tool == null)
            return new DeviceConfigResult { Success = false, Message = "vJoyConfig.exe not found — reinstall the vJoy driver." };

        bool[] axes = new bool[8];
        int disc = 0, cont = 0;
        try
        {
            for (int i = 0; i < 8; i++) axes[i] = native.AxisExists(deviceId, AxisUsages[i]);
            if (!axes.Any(a => a)) for (int i = 0; i < 8; i++) axes[i] = true;
            disc = Math.Max(native.GetDiscPovCount(deviceId), 0);
            cont = Math.Max(native.GetContPovCount(deviceId), 0);
        }
        catch { }

        string args = BuildArgs(deviceId, axes, disc, cont, buttons);
        log?.Invoke($"Reconfiguring vJoy device {deviceId} to {buttons} buttons… (one admin prompt)");
        try
        {
            using var proc = Process.Start(new ProcessStartInfo
            {
                FileName = tool,
                Arguments = args,
                Verb = "runas",
                UseShellExecute = true,
                // Console tool: hide the window (a brief flash may still show
                // through UAC elevation — harmless, no interaction needed).
                WindowStyle = ProcessWindowStyle.Hidden
            });
            if (proc == null)
                return new DeviceConfigResult { Success = false, Message = "Could not start vJoyConfig." };
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
            try
            {
                await proc.WaitForExitAsync(linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested)
            {
                try { proc.Kill(); } catch { }
                return new DeviceConfigResult { Success = false, Message = "vJoyConfig timed out — try once more or use vJoyConf." };
            }
            if (proc.ExitCode != 0)
                return new DeviceConfigResult { Success = false, Message = $"vJoyConfig exited with code {proc.ExitCode}." };
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            return new DeviceConfigResult { Success = false, Message = "Cancelled — admin permission is needed to reconfigure the device." };
        }
        catch (Exception ex)
        {
            return new DeviceConfigResult { Success = false, Message = $"Reconfigure failed: {ex.Message}" };
        }

        // Verify: device restarts asynchronously — poll the reported count.
        for (int i = 0; i < 30; i++)
        {
            try
            {
                await Task.Delay(1000, ct).ConfigureAwait(false);
                int reported = native.GetButtonCount(deviceId);
                if (reported == buttons)
                    return new DeviceConfigResult { Success = true, ButtonCount = reported, Message = $"vJoy device now offers {reported} buttons — verified live, no reboot needed." };
            }
            catch { return new DeviceConfigResult { Success = false, Message = "Cancelled while verifying." }; }
        }
        int final = -1;
        try { final = native.GetButtonCount(deviceId); } catch { }
        return new DeviceConfigResult
        {
            Success = false,
            ButtonCount = final,
            Message = final >= 0
                ? $"Device reports {final} buttons, expected {buttons} — reboot and retry, or set it in vJoyConf."
                : "Device didn't come back — reboot and check vJoyConf."
        };
    }
}
