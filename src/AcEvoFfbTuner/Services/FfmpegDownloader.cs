using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Tasks;

namespace AcEvoFfbTuner.Services;

public sealed class FfmpegDownloader
{
    private static readonly string AppDir = AppDomain.CurrentDomain.BaseDirectory;
    private static readonly string FfmpegPath = Path.Combine(AppDir, "ffmpeg.exe");
    private static readonly string FfmpegVersionFile = Path.Combine(AppDir, "ffmpeg.version");
    private static readonly string TempDir = Path.Combine(AppDir, "ffmpeg_temp");

    private const string ManifestUrl = "https://www.gyan.dev/ffmpeg/builds/release-version";
    private const string ZipUrl = "https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip";

    private static readonly HttpClient _http = new()
    {
        Timeout = TimeSpan.FromMinutes(5)
    };

    static FfmpegDownloader()
    {
        _http.DefaultRequestHeaders.Add("User-Agent", "AcEvoFfbTuner-FfmpegSetup");
    }

    public static string? FfmpegExePath => File.Exists(FfmpegPath) ? FfmpegPath : null;

    public static bool IsInstalled => File.Exists(FfmpegPath) && ValidateFfmpeg(FfmpegPath);

    public static async Task<string?> EnsureFfmpegAsync(IProgress<(int percent, string message)>? progress = null)
    {
        if (IsInstalled)
            return FfmpegPath;

        try
        {
            return await DownloadAndInstallAsync(progress);
        }
        catch (Exception ex)
        {
            progress?.Report((0, $"FFmpeg download failed: {ex.Message}"));
            return null;
        }
    }

    private static async Task<string?> DownloadAndInstallAsync(IProgress<(int percent, string message)>? progress)
    {
        progress?.Report((0, "Downloading FFmpeg (one-time setup)..."));

        CleanupTempDir();
        Directory.CreateDirectory(TempDir);

        string zipPath = Path.Combine(TempDir, "ffmpeg.zip");

        try
        {
            await DownloadFileAsync(ZipUrl, zipPath, progress);

            progress?.Report((90, "Extracting FFmpeg..."));
            await Task.Run(() => ZipFile.ExtractToDirectory(zipPath, TempDir, overwriteFiles: true));

            string? exe = FindFfmpegExeInDir(TempDir);
            if (exe == null)
            {
                progress?.Report((0, "FFmpeg not found in downloaded archive"));
                return null;
            }

            if (File.Exists(FfmpegPath))
                File.Delete(FfmpegPath);

            File.Move(exe, FfmpegPath);

            if (!ValidateFfmpeg(FfmpegPath))
            {
                progress?.Report((0, "Downloaded FFmpeg is not valid"));
                return null;
            }

            progress?.Report((100, "FFmpeg ready"));
            return FfmpegPath;
        }
        finally
        {
            CleanupTempDir();
        }
    }

    private static async Task DownloadFileAsync(string url, string destPath, IProgress<(int percent, string message)>? progress)
    {
        var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();

        var totalBytes = response.Content.Headers.ContentLength ?? 0;

        await using var contentStream = await response.Content.ReadAsStreamAsync();
        await using var fileStream = new FileStream(destPath, FileMode.Create, FileAccess.Write, FileShare.None, 8192, useAsync: true);

        var buffer = new byte[8192];
        long totalRead = 0;
        int read;

        while ((read = await contentStream.ReadAsync(buffer, 0, buffer.Length)) > 0)
        {
            await fileStream.WriteAsync(buffer, 0, read);
            totalRead += read;

            if (totalBytes > 0)
            {
                int pct = (int)(totalRead * 100 / totalBytes);
                double mbRead = totalRead / (1024.0 * 1024.0);
                double mbTotal = totalBytes / (1024.0 * 1024.0);
                progress?.Report((pct, $"Downloading FFmpeg... {mbRead:F0}/{mbTotal:F0} MB"));
            }
        }
    }

    private static string? FindFfmpegExeInDir(string directory)
    {
        try
        {
            return Directory.GetFiles(directory, "ffmpeg.exe", SearchOption.AllDirectories)
                .FirstOrDefault();
        }
        catch { return null; }
    }

    private static bool ValidateFfmpeg(string path)
    {
        try
        {
            using var proc = Process.Start(new ProcessStartInfo
            {
                FileName = path,
                Arguments = "-version",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            });
            proc?.WaitForExit(5000);
            return proc?.ExitCode == 0;
        }
        catch { return false; }
    }

    private static void CleanupTempDir()
    {
        try
        {
            if (Directory.Exists(TempDir))
                Directory.Delete(TempDir, recursive: true);
        }
        catch { }
    }
}
