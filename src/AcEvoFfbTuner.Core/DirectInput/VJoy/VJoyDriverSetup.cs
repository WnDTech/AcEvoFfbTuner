using System.Diagnostics;
using System.Security.Cryptography;

namespace AcEvoFfbTuner.Core.DirectInput.VJoy;

/// <summary>Outcome of the in-app vJoy driver install.</summary>
public sealed class DriverSetupResult
{
    public bool Success { get; init; }
    /// <summary>Short human-readable status for the UI.</summary>
    public string Message { get; init; } = "";
    /// <summary>True when Windows needs a reboot before the device appears.</summary>
    public bool RebootRequired { get; init; }
}

/// <summary>
/// Downloads the pinned vJoy installer, hash-verifies it, and runs it
/// elevated + silent. This is what the Game Config "Install driver" button
/// calls — the driver cannot be used from the app bundle directly, Windows
/// must install it (admin UAC prompt appears once).
///
/// Pin source of truth: installer/third-party/vjoy/VERSION.txt (kept in sync
/// by hand — same tag, URL and SHA256 as below).
/// </summary>
public static class VJoyDriverSetup
{
    public const string DownloadUrl =
        "https://github.com/jshafer817/vJoy/releases/download/v2.1.9.1/vJoySetup.exe";
    // SHA256 of the pinned asset (uppercase hex).
    public const string ExpectedSha256 =
        "F103CED4E7FF7CCB49C8415A542C56768ED4DA4FEA252B8F4FFDAC343074654A";

    public static async Task<DriverSetupResult> InstallAsync(
        IProgress<double>? progress = null, Action<string>? log = null, CancellationToken ct = default)
    {
        string dir = Path.Combine(Path.GetTempPath(), "FFBTuner");
        try
        {
            Directory.CreateDirectory(dir);

            if (IsSetupAlreadyRunning(out bool stuck))
            {
                if (stuck)
                {
                    KillStuckSetups();
                    log?.Invoke("Removed a stuck setup left over from before — starting fresh…");
                }
                else
                {
                    return new DriverSetupResult { Success = false, Message = "Setup is already running in the background — give it a minute." };
                }
            }

            CleanupStaleDownloads(dir);

            // Reuse a previously verified copy when possible (fast re-runs);
            // otherwise download to a UNIQUE name so a stale lock (running
            // setup, virus scanner) can never block us with "file in use".
            string exe = Path.Combine(dir, "vJoySetup.exe");
            if (!File.Exists(exe) || HashOfQuiet(exe) != ExpectedSha256)
                exe = await DownloadVerifiedCopy(dir, progress, ct).ConfigureAwait(false);
            if (exe == "")
                return new DriverSetupResult { Success = false, Message = "Download failed or the file failed its safety check — check your connection and retry." };

            int exit;
            try
            {
                log?.Invoke("Installing — approve the admin prompt, then give it a minute (no window appears).");
                exit = await RunElevatedSilent(exe, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                return new DriverSetupResult { Success = false, Message = $"Could not start the installer: {ex.Message}" };
            }
            if (exit == 1223)
                return new DriverSetupResult { Success = false, Message = "Cancelled — the driver needs admin permission to install." };
            if (exit == TimeoutExitCode)
                return new DriverSetupResult { Success = false, Message = "Setup timed out after 5 minutes and was stopped — reboot and try once more." };
            if (exit != 0)
                return new DriverSetupResult { Success = false, Message = $"Installer exited with code {exit} — retry, or install from the app setup." };

            // Fresh installs usually start WITHOUT a reboot (the reboot prompt
            // exists for upgrades). Probe for the virtual wheel before asking
            // the user to restart anything.
            log?.Invoke("Checking for the new virtual wheel…");
            bool appeared = await WaitForDeviceAsync(30, ct).ConfigureAwait(false);
            if (appeared)
                return new DriverSetupResult
                {
                    Success = true,
                    RebootRequired = false,
                    Message = "Driver installed and ready — no reboot needed, carry on to step 2."
                };
            return new DriverSetupResult
            {
                Success = true,
                RebootRequired = true,
                Message = "Driver installed but the wheel isn't showing yet — reboot once, then come back to step 2."
            };
        }
        catch (Exception ex)
        {
            return new DriverSetupResult { Success = false, Message = $"Install failed: {ex.Message}" };
        }
    }

    /// <summary>Polls DirectInput for a vJoy device until it appears or the
    /// timeout elapses. Never throws.</summary>
    public static async Task<bool> WaitForDeviceAsync(int timeoutSeconds, CancellationToken ct = default)
    {
        var deadline = DateTime.UtcNow.AddSeconds(Math.Max(timeoutSeconds, 1));
        while (DateTime.UtcNow < deadline)
        {
            if (ProbePresent()) return true;
            try { await Task.Delay(1000, ct).ConfigureAwait(false); }
            catch { return ProbePresent(); }
        }
        return ProbePresent();
    }

    private static bool ProbePresent()
    {
        try
        {
            using var di = new SharpDX.DirectInput.DirectInput();
            foreach (var d in di.GetDevices())
            {
                if ((d.ProductName ?? "").Contains("vJoy", StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }
        catch { return false; }
    }

    /// <summary>True when a vJoySetup process exists. stuck=true when every
    /// instance has been running longer than a silent install should take
    /// (observed: a silent install can hang forever with no window).</summary>
    private static bool IsSetupAlreadyRunning(out bool stuck)
    {
        stuck = false;
        try
        {
            var procs = Process.GetProcessesByName("vJoySetup");
            if (procs.Length == 0) return false;
            stuck = procs.All(p =>
            {
                try { return DateTime.Now - p.StartTime > TimeSpan.FromMinutes(10); }
                catch { return false; }
            });
            foreach (var p in procs) { try { p.Dispose(); } catch { } }
            return true;
        }
        catch { return false; }
    }

    private static void KillStuckSetups()
    {
        try
        {
            foreach (var p in Process.GetProcessesByName("vJoySetup"))
            {
                try { p.Kill(); } catch { }
                try { p.Dispose(); } catch { }
            }
        }
        catch { }
    }

    private static void CleanupStaleDownloads(string dir)
    {
        try
        {
            foreach (var f in Directory.GetFiles(dir, "vJoySetup-*.exe"))
            {
                try
                {
                    if (File.GetLastWriteTimeUtc(f) < DateTime.UtcNow.AddDays(-1))
                        File.Delete(f);
                }
                catch { }
            }
        }
        catch { }
    }

    /// <summary>Downloads to a unique temp name and returns its path, or "" on
    /// any failure (network, hash mismatch). Never throws.</summary>
    private static async Task<string> DownloadVerifiedCopy(string dir, IProgress<double>? progress, CancellationToken ct)
    {
        string tmp = Path.Combine(dir, $"vJoySetup-{DateTime.UtcNow.Ticks}.exe");
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
            using var resp = await http.GetAsync(DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();
            long? total = resp.Content.Headers.ContentLength;
            await using var net = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            await using var file = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            var buffer = new byte[81920];
            long done = 0;
            int read;
            while ((read = await net.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                await file.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                done += read;
                if (total > 0) progress?.Report(done * 100.0 / total.Value);
            }
        }
        catch
        {
            try { File.Delete(tmp); } catch { }
            return "";
        }
        if (HashOfQuiet(tmp) != ExpectedSha256)
        {
            try { File.Delete(tmp); } catch { }
            return "";
        }
        // Promote to the stable name for fast re-runs (best effort only —
        // a lock here must never fail the install, we launch tmp instead).
        try { File.Copy(tmp, Path.Combine(dir, "vJoySetup.exe"), overwrite: true); } catch { }
        return tmp;
    }

    private static string HashOf(string path)
    {
        using var sha = SHA256.Create();
        using var s = File.OpenRead(path);
        return Convert.ToHexString(sha.ComputeHash(s));
    }

    private static string HashOfQuiet(string path)
    {
        try { return HashOf(path); }
        catch { return ""; }
    }

    /// <summary>Sentinel exit code when the silent installer never finishes.</summary>
    public const int TimeoutExitCode = -2;

    private static async Task<int> RunElevatedSilent(string exe, CancellationToken ct)
    {
        try
        {
            using var proc = Process.Start(new ProcessStartInfo
            {
                FileName = exe,
                Arguments = "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART",
                Verb = "runas",
                UseShellExecute = true
            });
            if (proc == null) return -1;
            // A silent driver install takes ~1 minute. Never wait forever: a
            // hung setup left the button busy and the lock held indefinitely.
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
            try
            {
                await proc.WaitForExitAsync(linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested)
            {
                try { proc.Kill(); } catch { }
                return TimeoutExitCode;
            }
            return proc.ExitCode;
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            // UAC "No" — user cancelled the elevation prompt.
            return 1223;
        }
    }
}
