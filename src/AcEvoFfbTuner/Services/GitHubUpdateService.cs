using System.Diagnostics;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AcEvoFfbTuner.Services;

public sealed class GitHubUpdateService
{
    private const string Owner = "WnDTech";
    private const string Repo = "AcEvoFfbTuner";
    private const string ApiUrl = $"https://api.github.com/repos/{Owner}/{Repo}/releases/latest";

    private static readonly HttpClient _http = new()
    {
        Timeout = TimeSpan.FromSeconds(10)
    };

    static GitHubUpdateService()
    {
        _http.DefaultRequestHeaders.Add("User-Agent", "AcEvoFfbTuner-UpdateCheck");
    }

    public Version CurrentVersion { get; } = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(1, 0, 0);

    public async Task<UpdateInfo?> CheckForUpdateAsync()
    {
        try
        {
            var response = await _http.GetAsync(ApiUrl);
            if (!response.IsSuccessStatusCode) return null;

            var json = await response.Content.ReadAsStringAsync();
            var release = JsonSerializer.Deserialize<GitHubRelease>(json);
            if (release == null) return null;

            var tagName = release.TagName?.TrimStart('v', 'V') ?? "";
            if (!Version.TryParse(tagName, out var latestVersion)) return null;

            if (latestVersion <= CurrentVersion) return null;

            var asset = release.Assets?.FirstOrDefault(a =>
                a.Name?.Contains("Setup", StringComparison.OrdinalIgnoreCase) == true &&
                a.Name?.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) == true);

            if (asset == null)
                asset = release.Assets?.FirstOrDefault(a =>
                    a.Name?.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) == true);

            return new UpdateInfo
            {
                Version = latestVersion,
                ReleaseUrl = release.HtmlUrl ?? $"https://github.com/{Owner}/{Repo}/releases/latest",
                DownloadUrl = asset?.BrowserDownloadUrl,
                ReleaseNotes = release.Body ?? "",
                PublishedAt = release.PublishedAt
            };
        }
        catch
        {
            return null;
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
    public string ReleaseNotes { get; init; } = "";
    public DateTime? PublishedAt { get; init; }
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
