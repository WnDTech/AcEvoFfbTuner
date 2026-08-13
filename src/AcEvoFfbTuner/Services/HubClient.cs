using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AcEvoFfbTuner.Core.Profiles;

namespace AcEvoFfbTuner.Services;

public sealed class HubProfileDto
{
    public int Id { get; set; }
    public string Title { get; set; } = "";
    public string? Description { get; set; }
    public string Author { get; set; } = "";
    public string? AuthorId { get; set; }
    public string Game { get; set; } = "";
    public string? Car { get; set; }
    public string? Track { get; set; }
    public string? Wheel { get; set; }
    public string? WheelType { get; set; }
    public float TorqueNm { get; set; }
    public int Downloads { get; set; }
    public float Rating { get; set; }
    public int RatingCount { get; set; }
    public int ProfileVersion { get; set; }
    public string? CreatedAt { get; set; }
}

public sealed class HubListResult
{
    public bool Ok { get; set; }
    public string? Error { get; set; }
    public int Total { get; set; }
    public int Page { get; set; }
    public int Pages { get; set; }
    public List<HubProfileDto> Profiles { get; set; } = [];
}

public sealed class HubFacets
{
    public List<string> Games { get; set; } = [];
    public List<string> Wheels { get; set; } = [];
    public List<string> WheelTypes { get; set; } = [];
    public List<string> Cars { get; set; } = [];
    public List<string> Tracks { get; set; } = [];
}

public sealed class HubUploadRequest
{
    public string Title { get; set; } = "";
    public string? Description { get; set; }
    public string Author { get; set; } = "";
    public string AuthorId { get; set; } = "";
    public string Game { get; set; } = "";
    public string? Car { get; set; }
    public string? Track { get; set; }
    public string? Wheel { get; set; }
    public string? WheelType { get; set; }
    public float TorqueNm { get; set; }
    public JsonElement Profile { get; set; }
}

public sealed class HubUploadResult
{
    public bool Ok { get; set; }
    public string? Error { get; set; }
    public int? Id { get; set; }
    public string? Status { get; set; }
    public bool RateLimited { get; set; }
}

public sealed record HubDownloadResult(string? Json, string? Error);

public sealed class HubRateResult
{
    public bool Ok { get; set; }
    public string? Error { get; set; }
    public float Rating { get; set; }
    public int RatingCount { get; set; }
}

public sealed class HubClient : IDisposable
{
    private const string DefaultBaseUrl = "https://ffbtuner.wndtech.tips/api/hub.php";
    private const int MaxResponseBytes = 2_000_000;

    public static readonly JsonSerializerOptions ProfileJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals
    };

    private readonly HttpClient _http;
    private readonly string _baseUrl;
    private readonly string _apiKey;

    public HubClient(string baseUrl, string apiKey)
    {
        _apiKey = apiKey;
        _baseUrl = NormalizeHttpsBaseUrl(baseUrl);
        _http = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(15),
            MaxResponseContentBufferSize = MaxResponseBytes
        };
        _http.DefaultRequestHeaders.Add("User-Agent", "AcEvoFfbTuner-Hub");
    }

    private static string NormalizeHttpsBaseUrl(string baseUrl)
    {
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
            uri = new Uri(DefaultBaseUrl);
        return new UriBuilder(uri) { Scheme = Uri.UriSchemeHttps, Port = -1 }.Uri.AbsoluteUri.TrimEnd('/');
    }

    public static string ToHubGameName(string? gameMatch)
    {
        if (string.IsNullOrWhiteSpace(gameMatch)) return "AcEvo";
        var n = gameMatch.Replace(" (auto)", "", StringComparison.OrdinalIgnoreCase).Trim();
        return n switch
        {
            "RaceRoom" => "Raceroom",
            "Assetto Corsa" => "AssettoCorsa",
            "Le Mans Ultimate" => "LeMansUltimate",
            "ACC" or "Assetto Corsa Competizione" => "AssettoCorsaCompetizione",
            "AC EVO" or "Assetto Corsa EVO" => "AcEvo",
            _ => "AcEvo"
        };
    }

    public static string ToDisplayLabel(string game)
    {
        return game switch
        {
            "AcEvo" => "AC EVO",
            "Raceroom" => "RaceRoom",
            "AssettoCorsa" => "Assetto Corsa",
            "LeMansUltimate" => "Le Mans Ultimate",
            "AssettoCorsaCompetizione" => "ACC",
            _ => string.IsNullOrEmpty(game) ? "General" : game
        };
    }

    public async Task<HubListResult> GetProfilesAsync(
        string? game = null, string? q = null, string sort = "newest",
        int page = 1, int per = 24, string? wheel = null, string? car = null,
        string? track = null, string? wheelType = null, CancellationToken ct = default)
    {
        var url = new StringBuilder(_baseUrl);
        url.Append("?action=list&page=").Append(page)
           .Append("&per=").Append(per)
           .Append("&sort=").Append(Uri.EscapeDataString(sort));
        if (!string.IsNullOrEmpty(game))
            url.Append("&game=").Append(Uri.EscapeDataString(game));
        if (!string.IsNullOrEmpty(wheel))
            url.Append("&wheel=").Append(Uri.EscapeDataString(wheel));
        if (!string.IsNullOrEmpty(car))
            url.Append("&car=").Append(Uri.EscapeDataString(car));
        if (!string.IsNullOrEmpty(track))
            url.Append("&track=").Append(Uri.EscapeDataString(track));
        if (!string.IsNullOrEmpty(wheelType))
            url.Append("&wheelType=").Append(Uri.EscapeDataString(wheelType));
        if (!string.IsNullOrEmpty(q))
            url.Append("&q=").Append(Uri.EscapeDataString(q));

        try
        {
            var json = await _http.GetStringAsync(url.ToString(), ct);
            return JsonSerializer.Deserialize<HubListResult>(json, ProfileJsonOptions)
                ?? new HubListResult { Ok = false, Error = "Invalid server response" };
        }
        catch (TaskCanceledException)
        {
            return new HubListResult { Ok = false, Error = "Request timed out — check your connection" };
        }
        catch (HttpRequestException)
        {
            return new HubListResult { Ok = false, Error = "Could not reach the Hub — are you online?" };
        }
        catch (Exception ex)
        {
            return new HubListResult { Ok = false, Error = ex.Message };
        }
    }

    public async Task<HubFacets?> GetFacetsAsync(CancellationToken ct = default)
    {
        try
        {
            var json = await _http.GetStringAsync($"{_baseUrl}?action=facets", ct);
            return JsonSerializer.Deserialize<HubFacets>(json, ProfileJsonOptions);
        }
        catch
        {
            return null;
        }
    }

    public async Task<HubDownloadResult> DownloadProfileAsync(int id, CancellationToken ct = default)
    {
        try
        {
            var url = $"{_baseUrl}?action=download&id={id}";
            var json = await _http.GetStringAsync(url, ct);
            if (string.IsNullOrWhiteSpace(json))
                return new HubDownloadResult(null, "Empty response from server");
            return new HubDownloadResult(json, null);
        }
        catch (TaskCanceledException)
        {
            return new HubDownloadResult(null, "Request timed out — check your connection");
        }
        catch (HttpRequestException)
        {
            return new HubDownloadResult(null, "Could not reach the Hub — are you online?");
        }
        catch (Exception ex)
        {
            return new HubDownloadResult(null, ex.Message);
        }
    }

    public async Task<HubUploadResult> UploadProfileAsync(HubUploadRequest request, CancellationToken ct = default)
    {
        try
        {
            using var httpReq = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}?action=upload")
            {
                Content = new StringContent(JsonSerializer.Serialize(request, ProfileJsonOptions), Encoding.UTF8, "application/json")
            };
            httpReq.Headers.Add("X-App-Key", _apiKey);

            var response = await _http.SendAsync(httpReq, ct);
            var body = await response.Content.ReadAsStringAsync(ct);

            if ((int)response.StatusCode == 429)
                return new HubUploadResult { Ok = false, RateLimited = true, Error = "Upload rate limit reached (10/hour) — try again later" };

            if (!response.IsSuccessStatusCode)
                return new HubUploadResult { Ok = false, Error = TryParseError(body) ?? $"Server error ({response.StatusCode})" };

            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            var result = new HubUploadResult
            {
                Ok = root.TryGetProperty("ok", out var ok) && ok.GetBoolean(),
                Error = root.TryGetProperty("error", out var err) ? err.GetString() : null
            };
            if (root.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.Number)
                result.Id = id.GetInt32();
            if (root.TryGetProperty("status", out var status))
                result.Status = status.GetString();
            return result;
        }
        catch (TaskCanceledException)
        {
            return new HubUploadResult { Ok = false, Error = "Upload timed out — check your connection" };
        }
        catch (HttpRequestException)
        {
            return new HubUploadResult { Ok = false, Error = "Could not reach the Hub — are you online?" };
        }
        catch (Exception ex)
        {
            return new HubUploadResult { Ok = false, Error = ex.Message };
        }
    }

    public async Task<HubRateResult> RateProfileAsync(int id, int value, CancellationToken ct = default)
    {
        try
        {
            var body = JsonSerializer.Serialize(new { value }, ProfileJsonOptions);
            using var httpReq = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}?action=rate&id={id}")
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
            var response = await _http.SendAsync(httpReq, ct);
            var json = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode)
                return new HubRateResult { Ok = false, Error = TryParseError(json) ?? $"Server error ({response.StatusCode})" };

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            return new HubRateResult
            {
                Ok = root.TryGetProperty("ok", out var ok) && ok.GetBoolean(),
                Error = root.TryGetProperty("error", out var err) ? err.GetString() : null,
                Rating = root.TryGetProperty("rating", out var r) && r.ValueKind == JsonValueKind.Number ? r.GetSingle() : 0f,
                RatingCount = root.TryGetProperty("ratingCount", out var rc) && rc.ValueKind == JsonValueKind.Number ? rc.GetInt32() : 0
            };
        }
        catch (TaskCanceledException)
        {
            return new HubRateResult { Ok = false, Error = "Request timed out — check your connection" };
        }
        catch (HttpRequestException)
        {
            return new HubRateResult { Ok = false, Error = "Could not reach the Hub — are you online?" };
        }
        catch (Exception ex)
        {
            return new HubRateResult { Ok = false, Error = ex.Message };
        }
    }

    private static string? TryParseError(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("error", out var err))
                return err.GetString();
        }
        catch { }
        return null;
    }

    public static JsonElement SerializeProfile(FfbProfile profile)
    {
        return JsonSerializer.SerializeToElement(profile, ProfileJsonOptions);
    }

    public void Dispose() => _http.Dispose();
}
