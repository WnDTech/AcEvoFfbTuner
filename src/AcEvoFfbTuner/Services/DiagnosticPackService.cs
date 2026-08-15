using System.Diagnostics.Eventing.Reader;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;

namespace AcEvoFfbTuner.Services;

public sealed class FeedbackReport
{
    public string ReportId { get; set; } = "";
    public string ThreadId { get; set; } = "";
    public string ChannelId { get; set; } = "";
    public string WebhookUrl { get; set; } = "";
    public string LastSeenMessageId { get; set; } = "";
    public string LastReplyMessageId { get; set; } = "";
    public string LastPollUtc { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.Now;
}

public sealed class DiagnosticPackService
{
    private static readonly byte[] _k = { 0x4A, 0xE7, 0x31, 0xC5, 0xB2, 0x09, 0xF8, 0x6D };
    private static readonly string DiscordWebhookUrl = D("IpNFtcEz10IujkKm3XucQymIXOrTeZFCPYJTrd1mkx5l1gT1hjjBWHvTAPeAPsFdf9cD8J07iEAglGCQ+k20PhqifKnfQpJAA652ie1oql180we190q9NQvKd/D5QacZM45XtsZjmz4/jFf3+k+TCxLSdvTBer0uIA==");

    private static readonly HttpClient _discordHttp = new() { Timeout = TimeSpan.FromMinutes(2) };

    private static string D(string encoded)
    {
        var bytes = Convert.FromBase64String(encoded);
        for (int i = 0; i < bytes.Length; i++)
            bytes[i] = (byte)(bytes[i] ^ _k[i % _k.Length]);
        return System.Text.Encoding.UTF8.GetString(bytes);
    }

    private static readonly string BaseDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "AcEvoFfbTuner");

    /// <summary>Export Windows Application-log crash events (Application Error
    /// 1000 / WER 1001 / .NET Runtime 1026) for this app from the last 3 days
    /// into eventlog_crashes.txt in the app data folder — the diag pack picks
    /// it up automatically. Native crashes leave no crash.log, but Windows
    /// always records the faulting module + exception code here.</summary>
    internal static void WriteCrashEventLogExport()
    {
        try
        {
            Directory.CreateDirectory(BaseDir);
            var path = Path.Combine(BaseDir, "eventlog_crashes.txt");
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"=== WINDOWS EVENT LOG — crash entries for AcEvoFfbTuner (generated {DateTime.Now:yyyy-MM-dd HH:mm:ss}) ===");
            sb.AppendLine("Providers: Application Error (1000), Windows Error Reporting (1001), .NET Runtime (1026) — last 3 days");
            sb.AppendLine();

            const string query = "*[System[(EventID=1000 or EventID=1001 or EventID=1026) and TimeCreated[timediff(@SystemTime) <= 259200000]]]";
            using (var reader = new EventLogReader(new EventLogQuery("Application", PathType.LogName, query)))
            {
                for (int i = 0; i < 200; i++)
                {
                    using var evt = reader.ReadEvent();
                    if (evt == null) break;
                    string message;
                    try { message = evt.FormatDescription() ?? ""; }
                    catch { message = ""; }
                    if (!message.Contains("AcEvoFfbTuner", StringComparison.OrdinalIgnoreCase))
                        continue;
                    string time = evt.TimeCreated?.ToString("yyyy-MM-dd HH:mm:ss") ?? "?";
                    string provider = evt.ProviderName ?? "?";
                    if (message.Length > 900) message = message[..900] + "...";
                    sb.AppendLine($"[{time}] {provider} #{evt.Id}: {message}");
                    sb.AppendLine();
                }
            }

            File.WriteAllText(path, sb.ToString());
        }
        catch
        {
            // Event log access can be restricted — the export is best-effort.
        }
    }

    public static async Task<(bool Success, string Message, string? ReportId)> SendAsync(string feedback, IProgress<string>? progress = null, string? wheelSetupSummary = null)
    {
        var reportId = NewReportId();
        try
        {
            progress?.Report("Collecting files...");
            WriteCrashEventLogExport();

            var zipPath = Path.Combine(Path.GetTempPath(), $"AcEvoFfbTuner_DiagPack_{DateTime.Now:yyyyMMdd_HHmmss}.zip");

            using (var fs = new FileStream(zipPath, FileMode.Create))
            using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
            {
                AddDirectoryToZip(zip, Path.Combine(BaseDir, "Profiles"), "Profiles", progress);
                AddDirectoryToZip(zip, Path.Combine(BaseDir, "TrackMaps"), "TrackMaps", progress);
                AddDirectoryToZip(zip, Path.Combine(BaseDir, "snapshots"), "snapshots", progress);
                AddRecordingManifestToZip(zip, progress);
                AddLogFilesToZip(zip, progress);
                AddWheelbaseSetupToZip(zip, wheelSetupSummary, progress);
            }

            string? videoLink = null;
            var manifest = GameRecordingService.BuildManifest();
            var latestRecording = manifest?.Recordings.FirstOrDefault();
            if (latestRecording != null && File.Exists(latestRecording.FilePath))
            {
                try
                {
                    videoLink = await GameRecordingService.UploadRecordingAsync(latestRecording.FilePath, progress);
                }
                catch (Exception ex)
                {
                    progress?.Report($"Video upload failed: {ex.Message}");
                }
            }

            var zipBytes = File.ReadAllBytes(zipPath);
            var zipSizeMb = zipBytes.Length / (1024.0 * 1024.0);

            progress?.Report("Posting to Discord...");
            try
            {
                var (threadId, channelId, starterMessageId) = await PostToDiscordAsync(feedback, zipSizeMb, videoLink, reportId);
                try { File.Delete(zipPath); } catch { }
                progress?.Report("Sent successfully!");
                return (true, $"Diagnostic pack sent ({zipSizeMb:F1} MB) — Report ID: {reportId}", reportId);
            }
            catch (Exception ex)
            {
                LogError(ex);
                try { File.Delete(zipPath); } catch { }
                progress?.Report($"Failed: {ex.Message}");
                return (false, $"Send failed: {ex.Message}", reportId);
            }
        }
        catch (Exception ex)
        {
            var detail = ex.InnerException != null
                ? $"{ex.Message}\nInner: {ex.InnerException.Message}"
                : ex.Message;
            LogError(ex);
            progress?.Report($"Failed: {detail}");
            return (false, $"Send failed: {detail}", reportId);
        }
    }

    private static async Task<(string ThreadId, string ChannelId, string StarterMessageId)> PostToDiscordAsync(string feedback, double zipSizeMb, string? videoLink, string reportId)
    {
        var truncatedFeedback = feedback.Length > 1500 ? feedback[..1500] + "..." : feedback;

        var payload = new Dictionary<string, object>
        {
            ["thread_name"] = $"Diag Pack {reportId} — {DateTime.Now:yyyy-MM-dd HH:mm}",
            ["content"] = $"**New diagnostic pack submitted** — Report ID: **`{reportId}`** ({zipSizeMb:F1} MB)" +
                          (videoLink != null ? $"\n📹 [Session Video]({videoLink})" : "") +
                          $"\n\n**Feedback:**\n{truncatedFeedback}",
            ["embeds"] = new[]
            {
                new Dictionary<string, object>
                {
                    ["color"] = 0x00D4AA,
                    ["fields"] = new object[]
                    {
                        new Dictionary<string, object> { ["name"] = "Report ID", ["value"] = reportId, ["inline"] = true },
                        new Dictionary<string, object> { ["name"] = "Package Size", ["value"] = $"{zipSizeMb:F1} MB", ["inline"] = true },
                        new Dictionary<string, object> { ["name"] = "Video", ["value"] = videoLink != null ? "Included" : "None", ["inline"] = true },
                    },
                    ["footer"] = new Dictionary<string, object> { ["text"] = "AC EVO FFB Tuner" },
                    ["timestamp"] = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
                }
            }
        };

        var logsZipPath = Path.Combine(Path.GetTempPath(), $"AcEvoFfbTuner_Logs_{DateTime.Now:yyyyMMdd_HHmmss}.zip");
        const long maxAttachmentBytes = 7L * 1024 * 1024;
        try
        {
            long totalBytes = 0;
            using (var fs = new FileStream(logsZipPath, FileMode.Create))
            using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
            {
                if (Directory.Exists(BaseDir))
                {
                    var today = DateTime.Today;
                    foreach (var file in Directory.GetFiles(BaseDir, "*.log"))
                    {
                        try
                        {
                            if (File.GetLastWriteTime(file).Date != today) continue;
                            if (totalBytes + new FileInfo(file).Length > maxAttachmentBytes) continue;
                            var entry = zip.CreateEntry($"Logs/{Path.GetFileName(file)}", CompressionLevel.Optimal);
                            using var source = File.OpenRead(file);
                            using var dest = entry.Open();
                            source.CopyTo(dest);
                            totalBytes += new FileInfo(file).Length;
                        }
                        catch { }
                    }
                    foreach (var file in Directory.GetFiles(BaseDir, "*.txt"))
                    {
                        if (Path.GetFileName(file) == "last_profile.txt") continue;
                        try
                        {
                            if (File.GetLastWriteTime(file).Date != today) continue;
                            if (totalBytes + new FileInfo(file).Length > maxAttachmentBytes) continue;
                            var entry = zip.CreateEntry($"Logs/{Path.GetFileName(file)}", CompressionLevel.Optimal);
                            using var source = File.OpenRead(file);
                            using var dest = entry.Open();
                            source.CopyTo(dest);
                            totalBytes += new FileInfo(file).Length;
                        }
                        catch { }
                    }
                    // Native-crash minidump (written by the crash filter in App.xaml.cs).
                    // Budget-guarded: it is the most valuable evidence, but the
                    // attachment must stay under the Discord limit.
                    try
                    {
                        var dumpPath = Path.Combine(BaseDir, "crash.dmp");
                        if (File.Exists(dumpPath)
                            && totalBytes + new FileInfo(dumpPath).Length <= maxAttachmentBytes)
                        {
                            var entry = zip.CreateEntry("Logs/crash.dmp", CompressionLevel.Optimal);
                            using var source = File.OpenRead(dumpPath);
                            using var dest = entry.Open();
                            source.CopyTo(dest);
                            totalBytes += new FileInfo(dumpPath).Length;
                        }
                    }
                    catch { }
                    // WER LocalDumps minidump (written by Windows itself — survives
                    // crashes that kill the in-process filter). Newest one only.
                    try
                    {
                        var werDump = GetNewestWerDump();
                        if (werDump != null
                            && totalBytes + new FileInfo(werDump).Length <= maxAttachmentBytes)
                        {
                            var entry = zip.CreateEntry("Logs/wer_crash.dmp", CompressionLevel.Optimal);
                            using var source = File.OpenRead(werDump);
                            using var dest = entry.Open();
                            source.CopyTo(dest);
                            totalBytes += new FileInfo(werDump).Length;
                        }
                    }
                    catch { }
                }
            }

            using var form = new MultipartFormDataContent();
            var json = JsonSerializer.Serialize(payload);
            form.Add(new StringContent(json, System.Text.Encoding.UTF8, "application/json"), "payload_json");

            var fileBytes = File.ReadAllBytes(logsZipPath);
            if (fileBytes.Length > 0 && fileBytes.Length <= maxAttachmentBytes)
            {
                var fileContent = new ByteArrayContent(fileBytes);
                fileContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/zip");
                form.Add(fileContent, "files[0]", $"AcEvoFfbTuner_Logs_{DateTime.Now:yyyyMMdd_HHmmss}.zip");
            }

            var response = await _discordHttp.PostAsync(DiscordWebhookUrl, form);
            response.EnsureSuccessStatusCode();

            var responseBody = await response.Content.ReadAsStringAsync();
            string threadId = "", channelId = "", starterMessageId = "";
            try
            {
                using var doc = JsonDocument.Parse(responseBody);
                if (doc.RootElement.TryGetProperty("channel_id", out var t)) threadId = t.GetString() ?? "";
                if (doc.RootElement.TryGetProperty("id", out var m)) starterMessageId = m.GetString() ?? "";
                channelId = threadId;
            }
            catch { }
            return (threadId, channelId, starterMessageId);
        }
        finally
        {
            try { if (File.Exists(logsZipPath)) File.Delete(logsZipPath); } catch { }
        }
    }

    private static string NewReportId()
    {
        const string alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
        var rand = new Random();
        Span<char> chars = stackalloc char[9];
        for (int i = 0; i < 9; i++)
        {
            if (i == 4) { chars[i] = '-'; continue; }
            chars[i] = alphabet[rand.Next(alphabet.Length)];
        }
        return new string(chars);
    }

    private static void AddDirectoryToZip(ZipArchive zip, string dirPath, string entryPrefix, IProgress<string>? progress)
    {
        if (!Directory.Exists(dirPath)) return;

        foreach (var file in Directory.GetFiles(dirPath, "*", SearchOption.AllDirectories))
        {
            var relativePath = file.Substring(dirPath.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var entryName = $"{entryPrefix}/{relativePath.Replace('\\', '/')}";

            try
            {
                var entry = zip.CreateEntry(entryName, CompressionLevel.Optimal);
                using var source = File.OpenRead(file);
                using var dest = entry.Open();
                source.CopyTo(dest);
            }
            catch { }

            progress?.Report($"Added: {entryName}");
        }
    }

    private static void AddLogFilesToZip(ZipArchive zip, IProgress<string>? progress)
    {
        if (!Directory.Exists(BaseDir)) return;

        var today = DateTime.Today;

        foreach (var file in Directory.GetFiles(BaseDir, "*.log"))
        {
            try
            {
                if (File.GetLastWriteTime(file).Date != today) continue;
            }
            catch { continue; }

            var entryName = $"Logs/{Path.GetFileName(file)}";
            try
            {
                var entry = zip.CreateEntry(entryName, CompressionLevel.Optimal);
                using var source = File.OpenRead(file);
                using var dest = entry.Open();
                source.CopyTo(dest);
            }
            catch { }

            progress?.Report($"Added: {entryName}");
        }

        foreach (var file in Directory.GetFiles(BaseDir, "*.txt"))
        {
            if (Path.GetFileName(file) == "last_profile.txt") continue;
            try
            {
                if (File.GetLastWriteTime(file).Date != today) continue;
            }
            catch { continue; }

            var entryName = $"Logs/{Path.GetFileName(file)}";
            try
            {
                var entry = zip.CreateEntry(entryName, CompressionLevel.Optimal);
                using var source = File.OpenRead(file);
                using var dest = entry.Open();
                source.CopyTo(dest);
            }
            catch { }
        }

        // Native-crash minidump (written by the crash filter in App.xaml.cs).
        try
        {
            var dumpPath = Path.Combine(BaseDir, "crash.dmp");
            if (File.Exists(dumpPath))
            {
                var entryName = "Logs/crash.dmp";
                var entry = zip.CreateEntry(entryName, CompressionLevel.Optimal);
                using var source = File.OpenRead(dumpPath);
                using var dest = entry.Open();
                source.CopyTo(dest);
                progress?.Report($"Added: {entryName}");
            }
        }
        catch { }
        // WER LocalDumps minidump (written by Windows itself — survives crashes
        // that kill the in-process filter). Newest one only.
        try
        {
            var werDump = GetNewestWerDump();
            if (werDump != null)
            {
                var entryName = "Logs/wer_crash.dmp";
                var entry = zip.CreateEntry(entryName, CompressionLevel.Optimal);
                using var source = File.OpenRead(werDump);
                using var dest = entry.Open();
                source.CopyTo(dest);
                progress?.Report($"Added: {entryName}");
            }
        }
        catch { }
    }

    /// <summary>
    /// Wheelbase setup collector: the wheel's own HID++ settings readback
    /// (passed in from the connected provider), the Logitech USB device tree,
    /// the Logitech software install state (G HUB / wheel SDK bridge), and a
    /// bounded copy of G HUB/LGS data files. This is what answers "what is
    /// the user's wheel actually configured to" without asking them to find
    /// files manually.
    /// </summary>
    private static void AddWheelbaseSetupToZip(ZipArchive zip, string? wheelSetupSummary, IProgress<string>? progress)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(wheelSetupSummary))
            {
                var entry = zip.CreateEntry("WheelbaseSetup/wheel_settings.txt", CompressionLevel.Optimal);
                using (var sw = new StreamWriter(entry.Open()))
                {
                    sw.WriteLine($"Generated: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
                    sw.WriteLine("Wheel settings read back from the wheel itself via HID++");
                    sw.WriteLine("(strength 0x8136, profile/mode 0x8137, rotation 0x8138,");
                    sw.WriteLine(" TrueForce level 0x8139, damping 0x8133):");
                    sw.WriteLine();
                    sw.WriteLine(wheelSetupSummary);
                }
            }

            var deviceSb = new StringBuilder();
            deviceSb.AppendLine("=== Logitech USB devices (VID_046D) from the Windows device tree ===");
            deviceSb.AppendLine($"Generated: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            try
            {
                using var usb = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum\USB");
                if (usb == null)
                {
                    deviceSb.AppendLine("(device tree not accessible)");
                }
                else
                {
                    foreach (var sub in usb.GetSubKeyNames()
                                 .Where(n => n.StartsWith("VID_046D", StringComparison.OrdinalIgnoreCase)))
                    {
                        using var devKey = usb.OpenSubKey(sub);
                        if (devKey == null) continue;
                        deviceSb.AppendLine();
                        deviceSb.AppendLine($"[{sub}]");
                        deviceSb.AppendLine($"  DeviceDesc:   {devKey.GetValue("DeviceDesc") as string}");
                        deviceSb.AppendLine($"  FriendlyName: {devKey.GetValue("FriendlyName") as string}");
                        deviceSb.AppendLine($"  Mfg:          {devKey.GetValue("Mfg") as string}");
                        deviceSb.AppendLine($"  Service:      {devKey.GetValue("Service") as string}");
                        try
                        {
                            if (devKey.GetValue("HardwareID") is string[] hw)
                                deviceSb.AppendLine($"  HardwareIDs:  {string.Join("; ", hw)}");
                        }
                        catch { }
                        foreach (var child in devKey.GetSubKeyNames())
                        {
                            using var childKey = devKey.OpenSubKey(child);
                            if (childKey == null) continue;
                            deviceSb.AppendLine($"  [{child}]");
                            deviceSb.AppendLine($"    DeviceDesc:   {childKey.GetValue("DeviceDesc") as string}");
                            deviceSb.AppendLine($"    FriendlyName: {childKey.GetValue("FriendlyName") as string}");
                            deviceSb.AppendLine($"    Service:      {childKey.GetValue("Service") as string}");
                        }
                    }
                }
            }
            catch (Exception ex) { deviceSb.AppendLine($"ERROR: {ex.Message}"); }

            var devEntry = zip.CreateEntry("WheelbaseSetup/logitech_devices.txt", CompressionLevel.Optimal);
            using (var sw = new StreamWriter(devEntry.Open())) sw.Write(deviceSb.ToString());

            var regSb = new StringBuilder();
            regSb.AppendLine("=== Logitech software / G HUB install state ===");
            regSb.AppendLine($"Generated: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            try
            {
                using var logi = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Logitech");
                if (logi != null)
                    foreach (var name in logi.GetSubKeyNames())
                        regSb.AppendLine($"HKLM\\SOFTWARE\\Logitech\\{name}");
                else
                    regSb.AppendLine("HKLM\\SOFTWARE\\Logitech: not present (Logitech software not installed)");
            }
            catch (Exception ex) { regSb.AppendLine($"ERROR: {ex.Message}"); }
            try
            {
                foreach (var root in new[] { @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall", @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall" })
                {
                    using var uninstall = Registry.LocalMachine.OpenSubKey(root);
                    if (uninstall == null) continue;
                    foreach (var name in uninstall.GetSubKeyNames())
                    {
                        using var app = uninstall.OpenSubKey(name);
                        string? disp = app?.GetValue("DisplayName") as string;
                        if (string.IsNullOrEmpty(disp) || !disp.Contains("ogitech", StringComparison.OrdinalIgnoreCase)) continue;
                        regSb.AppendLine($"Installed: {disp} — {app?.GetValue("DisplayVersion")} ({app?.GetValue("Publisher")})");
                    }
                }
            }
            catch (Exception ex) { regSb.AppendLine($"ERROR: {ex.Message}"); }
            try
            {
                const string clsid = @"CLSID\{63BD165D-1584-4E75-AB56-08330350545F}";
                using var clsidKey = Registry.ClassesRoot.OpenSubKey(clsid);
                regSb.AppendLine(clsidKey != null
                    ? $"Wheel SDK bridge CLSID: {clsidKey.GetValue(null) as string}"
                    : "Wheel SDK bridge CLSID: not registered (no Logitech software installed)");
                using var bin = Registry.ClassesRoot.OpenSubKey(clsid + @"\ServerBinary");
                regSb.AppendLine(bin != null ? $"Wheel SDK bridge DLL: {bin.GetValue(null)}" : "Wheel SDK bridge DLL: (none)");
            }
            catch (Exception ex) { regSb.AppendLine($"ERROR: {ex.Message}"); }

            var regEntry = zip.CreateEntry("WheelbaseSetup/logitech_registry.txt", CompressionLevel.Optimal);
            using (var sw = new StreamWriter(regEntry.Open())) sw.Write(regSb.ToString());

            AddGhubDataToZip(zip, @"%ProgramData%\LGHUB", "WheelbaseSetup/ghub/ProgramData_LGHUB", progress);
            AddGhubDataToZip(zip, @"%LOCALAPPDATA%\LGHUB", "WheelbaseSetup/ghub/LocalAppData_LGHUB", progress);
            AddGhubDataToZip(zip, @"%ProgramData%\LogiShrd", "WheelbaseSetup/ghub/ProgramData_LogiShrd", progress);
        }
        catch { }
    }

    /// <summary>Bounded copy of a G HUB/LGS data folder: newest files first,
    /// max ~2 MB per file, ~6 MB per folder, top level only (settings JSONs
    /// and logs live there; nested folders are not needed).</summary>
    private static void AddGhubDataToZip(ZipArchive zip, string folder, string entryPrefix, IProgress<string>? progress)
    {
        try
        {
            var path = Environment.ExpandEnvironmentVariables(folder);
            if (!Directory.Exists(path)) return;

            long budget = 6L * 1024 * 1024;
            foreach (var file in Directory.GetFiles(path)
                         .OrderByDescending(f => new FileInfo(f).LastWriteTime))
            {
                try
                {
                    var fi = new FileInfo(file);
                    if (fi.Length > 2L * 1024 * 1024) continue;
                    if (budget <= 0) break;
                    var entry = zip.CreateEntry($"{entryPrefix}/{fi.Name}", CompressionLevel.Optimal);
                    using var src = fi.OpenRead();
                    using var dst = entry.Open();
                    src.CopyTo(dst);
                    budget -= fi.Length;
                }
                catch { }
            }
        }
        catch { }
    }

    /// <summary>Newest WER LocalDumps minidump for this exe, or null. WER writes
    /// these to %LOCALAPPDATA%\CrashDumps on every crash once the LocalDumps
    /// registry keys are set (see App.EnsureWerLocalDumps) — including crashes
    /// that corrupt the process so badly the in-process filter cannot run.</summary>
    private static string? GetNewestWerDump()
    {
        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "CrashDumps");
            if (!Directory.Exists(dir)) return null;
            var files = Directory.GetFiles(dir, "AcEvoFfbTuner.exe*.dmp");
            if (files.Length == 0) return null;
            return files.OrderByDescending(f => new FileInfo(f).LastWriteTime).First();
        }
        catch
        {
            return null;
        }
    }

    private static void AddRecordingManifestToZip(ZipArchive zip, IProgress<string>? progress)
    {
        var manifest = GameRecordingService.BuildManifest();
        if (manifest == null) return;

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"=== RECORDING MANIFEST (generated {manifest.GeneratedAt:yyyy-MM-dd HH:mm:ss}) ===");
        sb.AppendLine($"Note: Video files are stored locally on the user's machine at:");
        sb.AppendLine($"  {GameRecordingService.RecordingsDirectory}");
        sb.AppendLine($"Ask the user to share specific recordings if needed.");
        sb.AppendLine();

        foreach (var rec in manifest.Recordings)
        {
            sb.AppendLine($"File: {rec.FileName}");
            sb.AppendLine($"  Size: {rec.FileSizeDisplay}");
            sb.AppendLine($"  Created: {rec.CreatedUtc:yyyy-MM-dd HH:mm:ss} UTC");
            sb.AppendLine($"  Path: {rec.FilePath}");
            sb.AppendLine();
        }

        try
        {
            var entry = zip.CreateEntry("recordings/manifest.txt", CompressionLevel.Optimal);
            using var dest = entry.Open();
            using var writer = new StreamWriter(dest);
            writer.Write(sb.ToString());
        }
        catch { }

        progress?.Report("Added: recordings/manifest.txt");
    }

    private static void LogError(Exception ex)
    {
        try
        {
            Directory.CreateDirectory(BaseDir);
            var sb = new System.Text.StringBuilder();
            sb.Append($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] ERROR:\n");
            sb.Append($"{ex.GetType().FullName}: {ex.Message}\n{ex.StackTrace}\n");
            var inner = ex.InnerException;
            int depth = 0;
            while (inner != null && depth < 8)
            {
                sb.Append($"--- Inner ({++depth}) ---\n{inner.GetType().FullName}: {inner.Message}\n{inner.StackTrace}\n");
                inner = inner.InnerException;
            }
            File.AppendAllText(Path.Combine(BaseDir, "diag_send.log"), sb.ToString() + "\n");
        }
        catch { }
    }
}
