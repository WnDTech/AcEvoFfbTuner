using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AcEvoFfbTuner.Services;

public sealed class BetaUserDto
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Avatar { get; set; } = "";
}

public sealed class BetaTaskDto
{
    public int Id { get; set; }
    public string TaskCode { get; set; } = "";
    public string Type { get; set; } = "";
    public string Title { get; set; } = "";
    public string? Details { get; set; }
    public string Status { get; set; } = "";
    public string? ReportId { get; set; }
    public string? DiscordThreadId { get; set; }
    public string? DiscordThreadUrl { get; set; }
    public string? Notes { get; set; }
    public int Points { get; set; }
    public string? CreatedAt { get; set; }
}

public sealed class BetaApplicationDto
{
    public int Id { get; set; }
    public string? DiscordId { get; set; }
    public string? Username { get; set; }
    public string? DisplayName { get; set; }
    public string Status { get; set; } = "";
    public string Tier { get; set; } = "";
    public int Points { get; set; }
    public string? Timezone { get; set; }
    public List<string> Games { get; set; } = [];
    public JsonElement? Hardware { get; set; }
    public JsonElement? Experience { get; set; }
    public string? AppliedAt { get; set; }
    public string? ApprovedAt { get; set; }
    public List<BetaTaskDto> Tasks { get; set; } = [];
}

public sealed class BetaPodiumEntryDto
{
    public string Name { get; set; } = "";
    public string Avatar { get; set; } = "";
    public string Tier { get; set; } = "";
    public int Points { get; set; }
    public int Credits { get; set; }
    public string? Joined { get; set; }
}

public sealed class BetaMeResult
{
    public bool Ok { get; set; }
    public string? Error { get; set; }
    public bool Unauthorized { get; set; }
    public BetaUserDto? User { get; set; }
    public bool IsAdmin { get; set; }
    public bool BetaChannel { get; set; }
    public BetaApplicationDto? Application { get; set; }
}

public sealed class BetaPodiumResult
{
    public bool Ok { get; set; }
    public string? Error { get; set; }
    public List<BetaPodiumEntryDto> Podium { get; set; } = [];
}

public sealed class BetaStatusResult
{
    public bool Ok { get; set; }
    public string? Error { get; set; }
    public bool Open { get; set; }
    public int ActiveTesters { get; set; }
    public int OpenTasks { get; set; }
    public int VerifiedTasks { get; set; }
}

public sealed class BetaReportResult
{
    public bool Ok { get; set; }
    public string? Error { get; set; }
    public string? TaskCode { get; set; }
    public string? Status { get; set; }
    public string? ReportId { get; set; }
}

public sealed class BetaTokenResult
{
    public bool Ok { get; set; }
    public string? Error { get; set; }
    public string? Token { get; set; }
    public BetaUserDto? User { get; set; }
    public bool IsAdmin { get; set; }
}

public sealed class BetaSignInResult
{
    public bool Ok { get; set; }
    public string? Error { get; set; }
    public string? Token { get; set; }
    public BetaUserDto? User { get; set; }
    public bool IsAdmin { get; set; }
}

/// <summary>Small persisted cache of the signed-in beta user (name/avatar/tier/
/// status) so the Test Drive page can paint instantly before the first API call.
/// Stored in settings.json as BetaUserCacheJson (camelCase).</summary>
public sealed class BetaUserCache
{
    public string? Name { get; set; }
    public string? Avatar { get; set; }
    public string? Tier { get; set; }
    public string? Status { get; set; }
    public bool BetaChannel { get; set; }
}

/// <summary>Client for the Test Drive Program API (beta.php): Discord loopback
/// OAuth2 with PKCE, session header calls, tasks, and the Podium. Mirrors the
/// HubClient conventions: System.Text.Json camelCase, swallowed exceptions
/// returning {Ok=false, Error}, https-normalized base URL, 15s timeout.</summary>
public sealed class BetaClient : IDisposable
{
    private const string DefaultBaseUrl = "https://ffbtuner.wndtech.tips/api/beta.php";
    private const int MaxResponseBytes = 2_000_000;

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals
    };

    private readonly HttpClient _http;
    private readonly string _baseUrl;

    public BetaClient(string baseUrl)
    {
        _baseUrl = NormalizeHttpsBaseUrl(baseUrl);
        _http = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(15),
            MaxResponseContentBufferSize = MaxResponseBytes
        };
        _http.DefaultRequestHeaders.Add("User-Agent", "AcEvoFfbTuner-Beta");
    }

    private static string NormalizeHttpsBaseUrl(string baseUrl)
    {
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
            uri = new Uri(DefaultBaseUrl);
        return new UriBuilder(uri) { Scheme = Uri.UriSchemeHttps, Port = -1 }.Uri.AbsoluteUri.TrimEnd('/');
    }

    /// <summary>Website application page for users who are signed in but have no
    /// application yet (the apply form is web-only by design).</summary>
    public static string ApplicationPageUrl(string? betaApiBaseUrl)
    {
        _ = betaApiBaseUrl;
        return "https://ffbtuner.wndtech.tips/beta.html";
    }

    public async Task<BetaMeResult> GetMeAsync(string token, CancellationToken ct = default)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, $"{_baseUrl}?action=me");
            req.Headers.Add("X-Beta-Session", token);
            var resp = await _http.SendAsync(req, ct);
            var body = await resp.Content.ReadAsStringAsync(ct);

            if ((int)resp.StatusCode == 401)
                return new BetaMeResult { Ok = false, Unauthorized = true, Error = "Session expired — sign in again" };
            if (!resp.IsSuccessStatusCode)
                return new BetaMeResult { Ok = false, Error = TryParseError(body) ?? $"Server error ({resp.StatusCode})" };

            var data = JsonSerializer.Deserialize<BetaMeData>(body, JsonOptions);
            if (data == null)
                return new BetaMeResult { Ok = false, Error = "Invalid server response" };
            return new BetaMeResult
            {
                Ok = data.Ok,
                Error = data.Error,
                User = data.User,
                IsAdmin = data.IsAdmin,
                BetaChannel = data.BetaChannel,
                Application = data.Application
            };
        }
        catch (TaskCanceledException)
        {
            return new BetaMeResult { Ok = false, Error = "Request timed out — check your connection" };
        }
        catch (HttpRequestException)
        {
            return new BetaMeResult { Ok = false, Error = "Could not reach the Test Drive server — are you online?" };
        }
        catch (Exception ex)
        {
            return new BetaMeResult { Ok = false, Error = ex.Message };
        }
    }

    public async Task<BetaPodiumResult> GetPodiumAsync(CancellationToken ct = default)
    {
        try
        {
            var json = await _http.GetStringAsync($"{_baseUrl}?action=podium", ct);
            var data = JsonSerializer.Deserialize<BetaPodiumData>(json, JsonOptions);
            if (data == null)
                return new BetaPodiumResult { Ok = false, Error = "Invalid server response" };
            return new BetaPodiumResult { Ok = data.Ok, Error = data.Error, Podium = data.Podium };
        }
        catch (TaskCanceledException)
        {
            return new BetaPodiumResult { Ok = false, Error = "Request timed out — check your connection" };
        }
        catch (HttpRequestException)
        {
            return new BetaPodiumResult { Ok = false, Error = "Could not reach the Test Drive server — are you online?" };
        }
        catch (Exception ex)
        {
            return new BetaPodiumResult { Ok = false, Error = ex.Message };
        }
    }

    public async Task<BetaStatusResult> GetStatusAsync(CancellationToken ct = default)
    {
        try
        {
            var json = await _http.GetStringAsync($"{_baseUrl}?action=status", ct);
            var data = JsonSerializer.Deserialize<BetaStatusData>(json, JsonOptions);
            if (data == null)
                return new BetaStatusResult { Ok = false, Error = "Invalid server response" };
            return new BetaStatusResult
            {
                Ok = data.Ok,
                Error = data.Error,
                Open = data.Open,
                ActiveTesters = data.ActiveTesters,
                OpenTasks = data.OpenTasks,
                VerifiedTasks = data.VerifiedTasks
            };
        }
        catch (TaskCanceledException)
        {
            return new BetaStatusResult { Ok = false, Error = "Request timed out — check your connection" };
        }
        catch (HttpRequestException)
        {
            return new BetaStatusResult { Ok = false, Error = "Could not reach the Test Drive server — are you online?" };
        }
        catch (Exception ex)
        {
            return new BetaStatusResult { Ok = false, Error = ex.Message };
        }
    }

    public async Task<BetaReportResult> ReportTaskAsync(string token, string taskCode, string reportId, CancellationToken ct = default)
    {
        try
        {
            var body = JsonSerializer.Serialize(new { taskCode, reportId }, JsonOptions);
            using var req = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}?action=task_report")
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
            req.Headers.Add("X-Beta-Session", token);
            var resp = await _http.SendAsync(req, ct);
            var json = await resp.Content.ReadAsStringAsync(ct);

            if ((int)resp.StatusCode == 401)
                return new BetaReportResult { Ok = false, Error = "Session expired — sign in again" };
            if (!resp.IsSuccessStatusCode)
                return new BetaReportResult { Ok = false, Error = TryParseError(json) ?? $"Server error ({resp.StatusCode})" };

            var data = JsonSerializer.Deserialize<BetaReportData>(json, JsonOptions);
            if (data == null)
                return new BetaReportResult { Ok = false, Error = "Invalid server response" };
            return new BetaReportResult
            {
                Ok = data.Ok,
                Error = data.Error,
                TaskCode = data.TaskCode,
                Status = data.Status,
                ReportId = data.ReportId
            };
        }
        catch (TaskCanceledException)
        {
            return new BetaReportResult { Ok = false, Error = "Request timed out — check your connection" };
        }
        catch (HttpRequestException)
        {
            return new BetaReportResult { Ok = false, Error = "Could not reach the Test Drive server — are you online?" };
        }
        catch (Exception ex)
        {
            return new BetaReportResult { Ok = false, Error = ex.Message };
        }
    }

    public async Task<BetaTokenResult> ExchangeAppTokenAsync(string code, string verifier, CancellationToken ct = default)
    {
        try
        {
            var body = JsonSerializer.Serialize(new { code, verifier }, JsonOptions);
            using var req = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}?action=app_token")
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
            var resp = await _http.SendAsync(req, ct);
            var json = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode)
                return new BetaTokenResult { Ok = false, Error = TryParseError(json) ?? $"Server error ({resp.StatusCode})" };

            var data = JsonSerializer.Deserialize<BetaTokenData>(json, JsonOptions);
            if (data == null)
                return new BetaTokenResult { Ok = false, Error = "Invalid server response" };
            return new BetaTokenResult
            {
                Ok = data.Ok,
                Error = data.Error,
                Token = data.Token,
                User = data.User,
                IsAdmin = data.IsAdmin
            };
        }
        catch (TaskCanceledException)
        {
            return new BetaTokenResult { Ok = false, Error = "Request timed out — check your connection" };
        }
        catch (HttpRequestException)
        {
            return new BetaTokenResult { Ok = false, Error = "Could not reach the Test Drive server — are you online?" };
        }
        catch (Exception ex)
        {
            return new BetaTokenResult { Ok = false, Error = ex.Message };
        }
    }

    /// <summary>Loopback OAuth2 flow: starts a listener on 127.0.0.1:<random
    /// port>, opens the system browser at auth_start with an app-generated
    /// state + PKCE challenge, waits for the callback code, then exchanges it
    /// via app_token. The verifier never leaves this process.</summary>
    public async Task<BetaSignInResult> SignInAsync(CancellationToken ct = default)
    {
        for (int attempt = 0; attempt < 2; attempt++)
        {
            int port;
            try
            {
                port = PickFreePort();
            }
            catch
            {
                return new BetaSignInResult { Ok = false, Error = "Could not start sign-in — try again" };
            }

            var state = NewB64UrlToken(32);
            var verifier = NewB64UrlToken(64);
            var challenge = Sha256B64Url(verifier);

            using var server = new LoopbackCallbackServer(port);
            try
            {
                server.Start();
            }
            catch (SocketException)
            {
                if (attempt == 1)
                    return new BetaSignInResult { Ok = false, Error = "Could not start sign-in — try again" };
                continue; // port was grabbed in between — pick a new one
            }

            var url = new StringBuilder(_baseUrl);
            url.Append("?action=auth_start")
               .Append("&return=app:").Append(port)
               .Append("&state=").Append(Uri.EscapeDataString(state))
               .Append("&challenge=").Append(Uri.EscapeDataString(challenge));

            try
            {
                Process.Start(new ProcessStartInfo(url.ToString()) { UseShellExecute = true });
            }
            catch
            {
                return new BetaSignInResult
                {
                    Ok = false,
                    Error = "Could not open your browser — sign in on the website instead (ffbtuner.wndtech.tips/beta.html)"
                };
            }

            var (ok, error, code) = await server.WaitForCallbackAsync(TimeSpan.FromMinutes(2), ct);
            if (!ok || code == null)
                return new BetaSignInResult { Ok = false, Error = error ?? "Sign-in timed out — try again" };

            if (!string.Equals(state, code.State, StringComparison.Ordinal))
                return new BetaSignInResult { Ok = false, Error = "Sign-in failed — security check did not match. Try again." };

            var token = await ExchangeAppTokenAsync(code.Code, verifier, ct);
            if (!token.Ok || string.IsNullOrEmpty(token.Token))
                return new BetaSignInResult { Ok = false, Error = token.Error ?? "Sign-in failed — try again" };

            return new BetaSignInResult { Ok = true, Token = token.Token, User = token.User, IsAdmin = token.IsAdmin };
        }

        return new BetaSignInResult { Ok = false, Error = "Could not start sign-in — try again" };
    }

    private static int PickFreePort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        try
        {
            return ((IPEndPoint)probe.LocalEndpoint).Port;
        }
        finally
        {
            probe.Stop();
        }
    }

    private static string NewB64UrlToken(int bytes)
    {
        return Convert.ToBase64String(RandomNumberGenerator.GetBytes(bytes))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private static string Sha256B64Url(string input)
    {
        return Convert.ToBase64String(SHA256.HashData(Encoding.ASCII.GetBytes(input)))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
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

    public void Dispose() => _http.Dispose();

    /* ---------- Server payload shapes ---------- */

    private sealed class BetaMeData
    {
        public bool Ok { get; set; }
        public string? Error { get; set; }
        public BetaUserDto? User { get; set; }
        public bool IsAdmin { get; set; }
        public bool BetaChannel { get; set; }
        public BetaApplicationDto? Application { get; set; }
    }

    private sealed class BetaPodiumData
    {
        public bool Ok { get; set; }
        public string? Error { get; set; }
        public List<BetaPodiumEntryDto> Podium { get; set; } = [];
    }

    private sealed class BetaStatusData
    {
        public bool Ok { get; set; }
        public string? Error { get; set; }
        public bool Open { get; set; }
        public int ActiveTesters { get; set; }
        public int OpenTasks { get; set; }
        public int VerifiedTasks { get; set; }
    }

    private sealed class BetaReportData
    {
        public bool Ok { get; set; }
        public string? Error { get; set; }
        public string? TaskCode { get; set; }
        public string? Status { get; set; }
        public string? ReportId { get; set; }
    }

    private sealed class BetaTokenData
    {
        public bool Ok { get; set; }
        public string? Error { get; set; }
        public string? Token { get; set; }
        public BetaUserDto? User { get; set; }
        public bool IsAdmin { get; set; }
    }

    /* ---------- Minimal loopback HTTP listener (no HTTP.sys / ACL issues) ---------- */

    private sealed class CallbackCode
    {
        public string Code { get; init; } = "";
        public string State { get; init; } = "";
    }

    private sealed class LoopbackCallbackServer : IDisposable
    {
        private readonly int _port;
        private TcpListener? _listener;
        private CancellationTokenSource? _cts;
        private readonly TaskCompletionSource<CallbackCode?> _tcs =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public LoopbackCallbackServer(int port) => _port = port;

        public void Start()
        {
            _listener = new TcpListener(IPAddress.Loopback, _port);
            _listener.Start();
            _cts = new CancellationTokenSource();
            _ = AcceptLoopAsync(_cts.Token);
        }

        public async Task<(bool Ok, string? Error, CallbackCode? Code)> WaitForCallbackAsync(TimeSpan timeout, CancellationToken ct)
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(timeout);
            try
            {
                var code = await _tcs.Task.WaitAsync(timeoutCts.Token);
                return (code != null, null, code);
            }
            catch (OperationCanceledException)
            {
                return (false, ct.IsCancellationRequested ? "Sign-in cancelled" : "Sign-in timed out — try again", null);
            }
        }

        private async Task AcceptLoopAsync(CancellationToken ct)
        {
            var listener = _listener;
            if (listener == null) return;
            try
            {
                while (!ct.IsCancellationRequested && !_tcs.Task.IsCompleted)
                {
                    TcpClient client;
                    try
                    {
                        client = await listener.AcceptTcpClientAsync(ct);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    catch
                    {
                        break;
                    }
                    _ = HandleClientAsync(client);
                }
            }
            catch
            {
                // listener closed
            }
        }

        private async Task HandleClientAsync(TcpClient client)
        {
            using (client)
            {
                try
                {
                    using var stream = client.GetStream();
                    var head = await ReadRequestHeadAsync(stream);
                    var target = ParseRequestTarget(head);
                    CallbackCode? code = null;
                    if (target != null && target.StartsWith("/callback", StringComparison.Ordinal))
                        code = ParseCallbackQuery(target);
                    if (code != null)
                        _tcs.TrySetResult(code);
                    await WriteResponseAsync(stream, code != null);
                }
                catch
                {
                    // best-effort — the browser will retry or the sign-in times out
                }
            }
        }

        private static async Task<string> ReadRequestHeadAsync(NetworkStream stream)
        {
            var sb = new StringBuilder();
            var buffer = new byte[2048];
            while (sb.Length < 65536)
            {
                int n = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length));
                if (n <= 0) break;
                sb.Append(Encoding.ASCII.GetString(buffer, 0, n));
                int idx = sb.ToString().IndexOf("\r\n\r\n", StringComparison.Ordinal);
                if (idx >= 0)
                {
                    sb.Length = idx;
                    break;
                }
            }
            return sb.ToString();
        }

        private static string? ParseRequestTarget(string head)
        {
            int lineEnd = head.IndexOf('\n');
            if (lineEnd <= 0) return null;
            var parts = head[..lineEnd].TrimEnd('\r').Split(' ', StringSplitOptions.RemoveEmptyEntries);
            return parts.Length >= 2 ? parts[1] : null;
        }

        private static CallbackCode? ParseCallbackQuery(string pathAndQuery)
        {
            int q = pathAndQuery.IndexOf('?');
            string query = q >= 0 ? pathAndQuery[(q + 1)..] : "";
            string? code = null;
            string? state = null;
            foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                int eq = pair.IndexOf('=');
                string key = eq >= 0 ? pair[..eq] : pair;
                string val = eq >= 0 ? Uri.UnescapeDataString(pair[(eq + 1)..]) : "";
                if (key == "code") code = val;
                else if (key == "state") state = val;
            }
            if (string.IsNullOrEmpty(code)) return null;
            return new CallbackCode { Code = code, State = state ?? "" };
        }

        private static async Task WriteResponseAsync(NetworkStream stream, bool success)
        {
            var html = success
                ? "<!DOCTYPE html><html><head><meta charset=\"utf-8\"><title>AC Evo FFB Tuner</title></head>" +
                  "<body style=\"font-family:Segoe UI,sans-serif;background:#0D1117;color:#E6EDF3;text-align:center;padding-top:70px\">" +
                  "<h2 style=\"color:#F0883E\">Sign-in complete</h2>" +
                  "<p>You can close this tab and return to AC Evo FFB Tuner.</p>" +
                  "<script>window.close();</script></body></html>"
                : "not found";
            var body = Encoding.UTF8.GetBytes(html);
            var reason = success ? "200 OK" : "404 Not Found";
            var head = Encoding.ASCII.GetBytes(
                $"HTTP/1.1 {reason}\r\nContent-Type: text/html; charset=utf-8\r\n" +
                $"Content-Length: {body.Length}\r\nConnection: close\r\n\r\n");
            await stream.WriteAsync(head.AsMemory());
            await stream.WriteAsync(body.AsMemory());
            await stream.FlushAsync();
        }

        public void Dispose()
        {
            _cts?.Cancel();
            _listener?.Stop();
        }
    }
}
