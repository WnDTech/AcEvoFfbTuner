using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AcEvoFfbTuner.Services;

public sealed class GitHubUpdateService
{
    private const string Owner = "WnDTech";
    private const string Repo = "AcEvoFfbTuner";
    private const string ApiUrl = $"https://api.github.com/repos/{Owner}/{Repo}/releases/latest";

    private static readonly string LogDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "AcEvoFfbTuner");
    private static readonly string LogPath = Path.Combine(LogDir, "update.log");

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool DeleteFile(string lpFileName);

    private static void UnblockFile(string path)
    {
        DeleteFile(path + ":Zone.Identifier");
    }

    private static void Log(string message)
    {
        try
        {
            Directory.CreateDirectory(LogDir);
            File.AppendAllText(LogPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {message}\n");
        }
        catch { }
    }

    private static readonly HttpClient _http = new()
    {
        Timeout = TimeSpan.FromSeconds(10)
    };

    private static readonly HttpClient _downloadHttp = new()
    {
        Timeout = TimeSpan.FromMinutes(10)
    };

    static GitHubUpdateService()
    {
        _http.DefaultRequestHeaders.Add("User-Agent", "AcEvoFfbTuner-UpdateCheck");
        _downloadHttp.DefaultRequestHeaders.Add("User-Agent", "AcEvoFfbTuner-UpdateCheck");
    }

    public Version CurrentVersion { get; } = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(1, 0, 0);

    public async Task<UpdateInfo?> CheckForUpdateAsync()
    {
        try
        {
            Log($"CheckForUpdate: requesting {ApiUrl}");
            var response = await _http.GetAsync(ApiUrl);
            Log($"CheckForUpdate: HTTP {(int)response.StatusCode} {response.StatusCode}");
            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync();
                Log($"CheckForUpdate: response body (first 500 chars): {body.Substring(0, Math.Min(body.Length, 500))}");
                return null;
            }

            var json = await response.Content.ReadAsStringAsync();
            var release = JsonSerializer.Deserialize<GitHubRelease>(json);
            if (release == null) { Log("CheckForUpdate: failed to deserialize release JSON"); return null; }

            var tagName = release.TagName?.TrimStart('v', 'V') ?? "";
            Log($"CheckForUpdate: remote tag={release.TagName}, parsed={tagName}, current={CurrentVersion}");
            if (!Version.TryParse(tagName, out var latestVersion)) { Log($"CheckForUpdate: version parse failed for '{tagName}'"); return null; }

            if (latestVersion <= CurrentVersion) { Log($"CheckForUpdate: {latestVersion} <= {CurrentVersion}, no update"); return null; }

            var asset = release.Assets?.FirstOrDefault(a =>
                a.Name?.Contains("Setup", StringComparison.OrdinalIgnoreCase) == true &&
                a.Name?.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) == true);

            if (asset == null)
            {
                asset = release.Assets?.FirstOrDefault(a =>
                    a.Name?.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) == true);
                Log($"CheckForUpdate: no Setup.exe asset found, fallback zip={asset?.Name ?? "none"}");
            }
            else
            {
                Log($"CheckForUpdate: selected asset={asset.Name}, size={asset.Size}, url={asset.BrowserDownloadUrl}");
            }

            if (asset == null) { Log("CheckForUpdate: no suitable asset found at all"); return null; }

            var info = new UpdateInfo
            {
                Version = latestVersion,
                ReleaseUrl = release.HtmlUrl ?? $"https://github.com/{Owner}/{Repo}/releases/latest",
                DownloadUrl = asset.BrowserDownloadUrl,
                FileName = asset.Name,
                FileSize = asset.Size,
                ReleaseNotes = release.Body ?? "",
                PublishedAt = release.PublishedAt
            };
            Log($"CheckForUpdate: update available v{info.Version}, download={info.DownloadUrl}");
            return info;
        }
        catch (Exception ex)
        {
            Log($"CheckForUpdate FAILED: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
            return null;
        }
    }

    public static async Task DownloadAndInstallAsync(UpdateInfo update, IProgress<DownloadProgress>? progress = null)
    {
        if (string.IsNullOrEmpty(update.DownloadUrl))
            throw new InvalidOperationException("No download URL available for this update.");

        Log($"DownloadAndInstall: starting for v{update.Version}, url={update.DownloadUrl}, file={update.FileName}");

        var tempDir = Path.Combine(Path.GetTempPath(), "AcEvoFfbTuner_Update");
        Directory.CreateDirectory(tempDir);
        Log($"DownloadAndInstall: tempDir={tempDir}");

        var fileName = !string.IsNullOrEmpty(update.FileName) ? update.FileName : "AcEvoFfbTuner_Update.exe";
        var filePath = Path.Combine(tempDir, fileName);
        Log($"DownloadAndInstall: target path={filePath}");

        progress?.Report(new DownloadProgress { State = DownloadState.Downloading, Percent = 0 });

        using var response = await _downloadHttp.GetAsync(update.DownloadUrl, HttpCompletionOption.ResponseHeadersRead);
        Log($"DownloadAndInstall: download HTTP {(int)response.StatusCode}, content-length={response.Content.Headers.ContentLength}");
        response.EnsureSuccessStatusCode();

        var totalBytes = response.Content.Headers.ContentLength ?? update.FileSize;
        var downloadedBytes = 0L;

        using var contentStream = await response.Content.ReadAsStreamAsync();
        using var fileStream = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.None, 8192, true);

        var buffer = new byte[81920];
        int bytesRead;

        while ((bytesRead = await contentStream.ReadAsync(buffer)) > 0)
        {
            await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead));
            downloadedBytes += bytesRead;

            if (totalBytes > 0)
            {
                var percent = (int)((double)downloadedBytes / totalBytes * 100);
                progress?.Report(new DownloadProgress { State = DownloadState.Downloading, Percent = percent });
            }
        }

        Log($"DownloadAndInstall: download complete, {downloadedBytes} bytes written to {filePath}");

        progress?.Report(new DownloadProgress { State = DownloadState.Installing, Percent = 100 });

        UnblockFile(filePath);
        Log($"DownloadAndInstall: unblocked file (Zone.Identifier removed), file exists={File.Exists(filePath)}, size={new FileInfo(filePath).Length}");

        var psi = new ProcessStartInfo(filePath)
        {
            UseShellExecute = true,
            Arguments = "/FORCECLOSEAPPLICATIONS /CLOSEAPPLICATIONS"
        };
        Log($"DownloadAndInstall: launching installer: {filePath} {psi.Arguments}");
        Log($"DownloadAndInstall: UseShellExecute={psi.UseShellExecute}, WorkingDirectory={psi.WorkingDirectory}");

        try
        {
            var proc = Process.Start(psi);
            if (proc == null)
            {
                Log("DownloadAndInstall: Process.Start returned null — no process was started");
                throw new InvalidOperationException("Failed to start installer: Process.Start returned null.");
            }

            Log($"DownloadAndInstall: installer process started, PID={proc.Id}, HasExited={proc.HasExited}");
            if (proc.HasExited)
            {
                Log($"DownloadAndInstall: installer exited immediately with code {proc.ExitCode}");
            }
        }
        catch (Exception ex)
        {
            Log($"DownloadAndInstall: FAILED to launch installer: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
            throw;
        }
    }

    public static void OpenReleasePage(string url)
    {
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }
}

public sealed class UpdateInfo
{
    public Version Version { get; init; } = new(0, 0, 0);
    public string ReleaseUrl { get; init; } = "";
    public string? DownloadUrl { get; init; }
    public string? FileName { get; init; }
    public long FileSize { get; init; }
    public string ReleaseNotes { get; init; } = "";
    public DateTime? PublishedAt { get; init; }
}

public enum DownloadState { Downloading, Installing }

public sealed class DownloadProgress
{
    public DownloadState State { get; init; }
    public int Percent { get; init; }
}

internal sealed class GitHubRelease
{
    [JsonPropertyName("tag_name")] public string? TagName { get; set; }
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("body")] public string? Body { get; set; }
    [JsonPropertyName("html_url")] public string? HtmlUrl { get; set; }
    [JsonPropertyName("published_at")] public DateTime? PublishedAt { get; set; }
    [JsonPropertyName("assets")] public List<GitHubAsset>? Assets { get; set; }
}

internal sealed class GitHubAsset
{
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("browser_download_url")] public string? BrowserDownloadUrl { get; set; }
    [JsonPropertyName("size")] public long Size { get; set; }
}
