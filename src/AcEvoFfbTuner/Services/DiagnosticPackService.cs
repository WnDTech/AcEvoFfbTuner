using System.IO;
using System.IO.Compression;
using System.Net.Mail;
using System.Net;
using System.Net.Mime;

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
                AddLogFilesToZip(zip, progress);
            }

            progress?.Report("Sending email...");

            var zipBytes = File.ReadAllBytes(zipPath);
            var zipSizeMb = zipBytes.Length / (1024.0 * 1024.0);

            using var client = new SmtpClient(SmtpHost, SmtpPort);
            client.EnableSsl = true;
            client.Credentials = new NetworkCredential(SmtpUser, SmtpPass);
            client.DeliveryMethod = SmtpDeliveryMethod.Network;

            using var mail = new MailMessage();
            mail.From = new MailAddress(FromAddress);
            mail.To.Add(ToAddress);
            mail.Subject = $"AC EVO FFB Tuner - Diagnostic Pack ({DateTime.Now:yyyy-MM-dd HH:mm})";
            mail.Body = $"AC EVO FFB Tuner Diagnostic Pack\n" +
                         $"Generated: {DateTime.Now:yyyy-MM-dd HH:mm:ss}\n" +
                         $"Package size: {zipSizeMb:F1} MB\n\n" +
                         $"Contains: Profiles, Track Maps, Snapshots, and Log files.\n\n" +
                         $"--- USER FEEDBACK ---\n{feedback}";

            var attachment = new Attachment(zipPath, new ContentType("application/zip"));
            attachment.ContentDisposition!.FileName = $"AcEvoFfbTuner_DiagPack_{DateTime.Now:yyyyMMdd_HHmmss}.zip";
            mail.Attachments.Add(attachment);

            await client.SendMailAsync(mail);

            try { File.Delete(zipPath); } catch { }

            progress?.Report("Sent successfully!");
            return (true, $"Diagnostic pack sent ({zipSizeMb:F1} MB)");
        }
        catch (Exception ex)
        {
            progress?.Report($"Failed: {ex.Message}");
            return (false, $"Send failed: {ex.Message}");
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
}
