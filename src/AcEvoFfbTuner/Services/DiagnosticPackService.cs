using System.IO;
using System.IO.Compression;
using System.Net.Mail;
using System.Net;
using System.Net.Mime;
using System.Net.Http;
using System.Text.Json;

namespace AcEvoFfbTuner.Services;

public sealed class DiagnosticPackService
{
    private static readonly byte[] _k = { 0x4A, 0xE7, 0x31, 0xC5, 0xB2, 0x09, 0xF8, 0x6D };
    private static readonly string SmtpHost = D("PYlVsddqkEM+jkG2");
    private const int SmtpPort = 587;
    private static readonly string SmtpUser = D("K4RUs91vng8+kl+gwEmPAy6TVKbaJ4wEOpQ=");
    private static readonly string SmtpPass = D("GohFpMZm1lx41AU=");
    private static readonly string FromAddress = D("K4RUs91vng8+kl+gwEmPAy6TVKbaJ4wEOpQ=");
    private static readonly string ToAddress = D("OoZEqfJ+lgk+glKtnH2RHTk=");
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

    public static async Task<(bool Success, string Message)> SendAsync(string feedback, IProgress<string>? progress = null)
    {
        try
        {
            progress?.Report("Collecting files...");

            var zipPath = Path.Combine(Path.GetTempPath(), $"AcEvoFfbTuner_DiagPack_{DateTime.Now:yyyyMMdd_HHmmss}.zip");

            using (var fs = new FileStream(zipPath, FileMode.Create))
            using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
            {
                AddDirectoryToZip(zip, Path.Combine(BaseDir, "Profiles"), "Profiles", progress);
                AddDirectoryToZip(zip, Path.Combine(BaseDir, "TrackMaps"), "TrackMaps", progress);
                AddDirectoryToZip(zip, Path.Combine(BaseDir, "snapshots"), "snapshots", progress);
                AddRecordingManifestToZip(zip, progress);
                AddLogFilesToZip(zip, progress);
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

            progress?.Report("Sending email...");

            var zipBytes = File.ReadAllBytes(zipPath);
            var zipSizeMb = zipBytes.Length / (1024.0 * 1024.0);

            var emailTask = SendEmailAsync(feedback, zipPath, zipSizeMb, videoLink);
            var discordTask = PostToDiscordAsync(feedback, zipSizeMb, videoLink);

            await emailTask;

            try { File.Delete(zipPath); } catch { }

            progress?.Report("Posting to Discord...");
            try { await discordTask; } catch { }

            progress?.Report("Sent successfully!");
            return (true, $"Diagnostic pack sent ({zipSizeMb:F1} MB)");
        }
        catch (Exception ex)
        {
            var detail = ex.InnerException != null
                ? $"{ex.Message}\nInner: {ex.InnerException.Message}"
                : ex.Message;
            LogError(ex);
            progress?.Report($"Failed: {detail}");
            return (false, $"Send failed: {detail}");
        }
    }

    private static async Task SendEmailAsync(string feedback, string zipPath, double zipSizeMb, string? videoLink)
    {
        using var client = new SmtpClient(SmtpHost, SmtpPort);
        client.EnableSsl = true;
        client.Credentials = new NetworkCredential(SmtpUser, SmtpPass);
        client.DeliveryMethod = SmtpDeliveryMethod.Network;

        using var mail = new MailMessage();
        mail.From = new MailAddress(FromAddress);
        mail.To.Add(ToAddress);
        mail.Subject = $"AC EVO FFB Tuner - Diagnostic Pack ({DateTime.Now:yyyy-MM-dd HH:mm})";

        string videoSection = videoLink != null
            ? $"\n\n--- SESSION VIDEO ---\n{videoLink}\n(Download to view the recorded driving session)"
            : "\n\n--- SESSION VIDEO ---\nNo video uploaded (no recording found or upload failed)";

        mail.Body = $"AC EVO FFB Tuner Diagnostic Pack\n" +
                     $"Generated: {DateTime.Now:yyyy-MM-dd HH:mm:ss}\n" +
                     $"Package size: {zipSizeMb:F1} MB\n\n" +
                     $"Contains: Profiles, Track Maps, Snapshots, Recording Manifest, and Log files." +
                     videoSection +
                     $"\n\n--- USER FEEDBACK ---\n{feedback}";

        var attachment = new Attachment(zipPath, new ContentType("application/zip"));
        attachment.ContentDisposition!.FileName = $"AcEvoFfbTuner_DiagPack_{DateTime.Now:yyyyMMdd_HHmmss}.zip";
        mail.Attachments.Add(attachment);

        await client.SendMailAsync(mail);
    }

    private static async Task PostToDiscordAsync(string feedback, double zipSizeMb, string? videoLink)
    {
        var truncatedFeedback = feedback.Length > 1500 ? feedback[..1500] + "..." : feedback;

        var payload = new Dictionary<string, object>
        {
            ["thread_name"] = $"Diagnostic Pack — {DateTime.Now:yyyy-MM-dd HH:mm}",
            ["content"] = $"**New diagnostic pack submitted** ({zipSizeMb:F1} MB)" +
                          (videoLink != null ? $"\n📹 [Session Video]({videoLink})" : "") +
                          $"\n\n**Feedback:**\n{truncatedFeedback}",
            ["embeds"] = new[]
            {
                new Dictionary<string, object>
                {
                    ["color"] = 0x00D4AA,
                    ["fields"] = new object[]
                    {
                        new Dictionary<string, object> { ["name"] = "Package Size", ["value"] = $"{zipSizeMb:F1} MB", ["inline"] = true },
                        new Dictionary<string, object> { ["name"] = "Video", ["value"] = videoLink != null ? "Included" : "None", ["inline"] = true },
                    },
                    ["footer"] = new Dictionary<string, object> { ["text"] = "AC EVO FFB Tuner" },
                    ["timestamp"] = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
                }
            }
        };

        var logsZipPath = Path.Combine(Path.GetTempPath(), $"AcEvoFfbTuner_Logs_{DateTime.Now:yyyyMMdd_HHmmss}.zip");
        try
        {
            using (var fs = new FileStream(logsZipPath, FileMode.Create))
            using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
            {
                if (Directory.Exists(BaseDir))
                {
                    foreach (var file in Directory.GetFiles(BaseDir, "*.log"))
                    {
                        try
                        {
                            var entry = zip.CreateEntry($"Logs/{Path.GetFileName(file)}", CompressionLevel.Optimal);
                            using var source = File.OpenRead(file);
                            using var dest = entry.Open();
                            source.CopyTo(dest);
                        }
                        catch { }
                    }
                    foreach (var file in Directory.GetFiles(BaseDir, "*.txt"))
                    {
                        if (Path.GetFileName(file) == "last_profile.txt") continue;
                        try
                        {
                            var entry = zip.CreateEntry($"Logs/{Path.GetFileName(file)}", CompressionLevel.Optimal);
                            using var source = File.OpenRead(file);
                            using var dest = entry.Open();
                            source.CopyTo(dest);
                        }
                        catch { }
                    }
                }
            }

            using var form = new MultipartFormDataContent();
            var json = JsonSerializer.Serialize(payload);
            form.Add(new StringContent(json, System.Text.Encoding.UTF8, "application/json"), "payload_json");

            var fileBytes = File.ReadAllBytes(logsZipPath);
            var fileContent = new ByteArrayContent(fileBytes);
            fileContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/zip");
            form.Add(fileContent, "files[0]", $"AcEvoFfbTuner_Logs_{DateTime.Now:yyyyMMdd_HHmmss}.zip");

            var response = await _discordHttp.PostAsync(DiscordWebhookUrl, form);
            response.EnsureSuccessStatusCode();
        }
        finally
        {
            try { if (File.Exists(logsZipPath)) File.Delete(logsZipPath); } catch { }
        }
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

        foreach (var file in Directory.GetFiles(BaseDir, "*.log"))
        {
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
            File.AppendAllText(Path.Combine(BaseDir, "diag_send.log"),
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] ERROR:\n" +
                $"{ex.GetType().FullName}: {ex.Message}\n" +
                $"{ex.StackTrace}\n" +
                (ex.InnerException != null
                    ? $"--- Inner ---\n{ex.InnerException.GetType().FullName}: {ex.InnerException.Message}\n{ex.InnerException.StackTrace}\n"
                    : "") +
                "\n");
        }
        catch { }
    }
}
