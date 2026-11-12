using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using AcEvoFfbTuner.Core.Profiles;

namespace AcEvoFfbTuner.Services;

/// <summary>
/// Community car torque fingerprints (no UI).
///
/// DOWNLOAD: when a car is detected, fetch &lt;store&gt;/&lt;GAME&gt;/&lt;car-slug&gt;.json from the
/// public GitHub store (read-only, no token) and cache it. Re-checked at most once a day per car.
///
/// UPLOAD: converged local fingerprints are POSTed to hub.php (?action=fingerprint_submit,
/// existing app key). The server pools contributors and publishes to GitHub with its own
/// token — a GitHub write token is never shipped in the app.
///
/// Anonymous: only game, car name/class, torque histogram, driving seconds/laps, app
/// version and a random install id. AC is never sent (its torque is synthesised from the
/// user's own settings, so it is not comparable between users).
/// </summary>
public sealed class FingerprintSyncService : IDisposable
{
    private static readonly TimeSpan RecheckEvery = TimeSpan.FromHours(24);
    private static readonly TimeSpan UploadEvery = TimeSpan.FromMinutes(2);
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private readonly CarTorqueFingerprintStore _store;
    private readonly AppSettings _settings;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(15), MaxResponseContentBufferSize = 1_000_000 };
    private readonly Timer _uploadTimer;
    private readonly HashSet<string> _fetchedThisSession = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _appVersion;
    private int _uploading;

    public static bool IsShareableGame(string game) => game is "LMU" or "R3E" or "EVO" or "ACC";

    public FingerprintSyncService(CarTorqueFingerprintStore store, AppSettings settings)
    {
        _store = store;
        _settings = settings;
        _http.DefaultRequestHeaders.Add("User-Agent", "AcEvoFfbTuner-Fingerprints");
        _appVersion = Assembly.GetEntryAssembly()?
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "";
        if (string.IsNullOrEmpty(_settings.FingerprintInstallId))
        {
            _settings.FingerprintInstallId = Guid.NewGuid().ToString("N");
            _settings.Save();
        }
        _store.ActiveCarChanged += (game, car) => _ = DownloadAsync(game, car);
        _uploadTimer = new Timer(_ => _ = UploadPendingAsync(), null, UploadEvery, UploadEvery);
    }

    public async Task DownloadAsync(string game, string car, CancellationToken ct = default)
    {
        if (!IsShareableGame(game)) return;
        string slug = CarTorqueFingerprint.Slug(car);
        if (slug.Length == 0) return;
        var cached = _store.GetCommunity(game, car);
        if (cached != null && DateTime.UtcNow - cached.FetchedUtc < RecheckEvery) return;
        lock (_fetchedThisSession) if (!_fetchedThisSession.Add($"{game}|{slug}")) return;

        try
        {
            string url = $"{_settings.FingerprintStoreBaseUrl.TrimEnd('/')}/{game}/{Uri.EscapeDataString(slug)}.json";
            using var resp = await _http.GetAsync(url, ct);
            if (resp.StatusCode == HttpStatusCode.NotFound) return; // nobody has pooled this car yet
            if (!resp.IsSuccessStatusCode) return;
            var c = await resp.Content.ReadFromJsonAsync<CommunityFingerprint>(Json, ct);
            if (c == null || c.P99 <= 0 || !float.IsFinite(c.P99)) return;
            c.Game = game;
            c.Car = car; // key by the local name so lookups match exactly
            c.FetchedUtc = DateTime.UtcNow;
            _store.SetCommunity(c);
        }
        catch { /* offline / store missing — local learning carries on */ }
    }

    public async Task UploadPendingAsync()
    {
        if (!_settings.ShareCarFingerprints) return;
        if (Interlocked.Exchange(ref _uploading, 1) == 1) return;
        try
        {
            foreach (var fp in _store.UploadCandidates(IsShareableGame))
            {
                var body = new
                {
                    installId = _settings.FingerprintInstallId,
                    game = fp.Game,
                    car = fp.Car,
                    slug = CarTorqueFingerprint.Slug(fp.Car),
                    carClass = fp.CarClass,
                    signal = fp.Signal,
                    gameStrength = fp.GameStrength,
                    validSeconds = fp.ValidSeconds,
                    laps = fp.Laps,
                    binsPerOctave = CarTorqueFingerprint.BinsPerOctave,
                    minExp = CarTorqueFingerprint.MinExp,
                    bins = fp.Bins,
                    appVersion = _appVersion,
                };
                using var req = new HttpRequestMessage(HttpMethod.Post,
                    $"{_settings.HubApiBaseUrl.TrimEnd('/')}?action=fingerprint_submit")
                {
                    Content = JsonContent.Create(body, options: Json),
                };
                req.Headers.Add("X-App-Key", _settings.HubApiKey);
                using var resp = await _http.SendAsync(req);
                if (resp.IsSuccessStatusCode)
                    _store.MarkUploaded(fp.Game, fp.Car, fp.ValidSeconds);
                else if (resp.StatusCode == HttpStatusCode.TooManyRequests)
                    break; // try again next tick
            }
        }
        catch { /* offline — retried next tick */ }
        finally { Interlocked.Exchange(ref _uploading, 0); }
    }

    public void Dispose()
    {
        _uploadTimer.Dispose();
        _http.Dispose();
    }
}
