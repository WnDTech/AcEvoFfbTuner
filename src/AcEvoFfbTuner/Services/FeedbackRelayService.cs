using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;

namespace AcEvoFfbTuner.Services;

public sealed class RelayMessage
{
    public string Id { get; set; } = "";
    public string Author { get; set; } = "";
    public string Content { get; set; } = "";
    public string At { get; set; } = "";
    public bool IsFix { get; set; }
}

public static class FeedbackRelayService
{
    private static readonly byte[] _k = { 0x4A, 0xE7, 0x31, 0xC5, 0xB2, 0x09, 0xF8, 0x6D };
    private static readonly string RelayToken = D("C6RUs90kvg8Ygl2kyyTPK3ifCI4=");

    private static readonly TimeSpan ReportTtl = TimeSpan.FromDays(14);

    private static string RelayBaseUrl => AppSettings.Load().FeedbackRelayUrl;

    public static string ResolvedRelayUrl => RelayBaseUrl;

    private static readonly string StatePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "AcEvoFfbTuner", "feedback_reports.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(5) };

    private static readonly object Sync = new();
    private static readonly HashSet<string> RegisteredThisRun = [];

    private static string D(string encoded)
    {
        var bytes = Convert.FromBase64String(encoded);
        for (int i = 0; i < bytes.Length; i++)
            bytes[i] = (byte)(bytes[i] ^ _k[i % _k.Length]);
        return System.Text.Encoding.UTF8.GetString(bytes);
    }

    private static HttpRequestMessage BuildRequest(HttpMethod method, string url, string? jsonBody = null)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Add("X-Relay-Token", RelayToken);
        if (jsonBody != null)
            request.Content = new StringContent(jsonBody, System.Text.Encoding.UTF8, "application/json");
        return request;
    }

    public static async Task RegisterAsync(string reportId, string threadId, string channelId, string webhookUrl, string starterMessageId)
    {
        var existing = GetReports().FirstOrDefault(r => r.ReportId == reportId);
        var report = new FeedbackReport
        {
            ReportId = reportId,
            ThreadId = threadId,
            ChannelId = channelId,
            WebhookUrl = webhookUrl,
            LastSeenMessageId = string.IsNullOrEmpty(existing?.LastSeenMessageId) ? starterMessageId : existing.LastSeenMessageId,
            LastReplyMessageId = existing?.LastReplyMessageId ?? "",
            LastPollUtc = existing?.LastPollUtc ?? "",
            CreatedAt = existing?.CreatedAt ?? DateTime.Now
        };
        Upsert(report);
        await TryRegisterAsync(report).ConfigureAwait(false);
    }

    private static async Task<bool> TryRegisterAsync(FeedbackReport report)
    {
        try
        {
            var payload = new Dictionary<string, object>
            {
                ["reportId"] = report.ReportId,
                ["threadId"] = report.ThreadId,
                ["channelId"] = report.ChannelId,
                ["webhookUrl"] = report.WebhookUrl
            };
            var json = JsonSerializer.Serialize(payload);
            using var request = BuildRequest(HttpMethod.Post, $"{RelayBaseUrl}/register", json);
            using var response = await Http.SendAsync(request).ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
            {
                lock (Sync) RegisteredThisRun.Add(report.ReportId);
                return true;
            }
            Log($"Register failed: {(int)response.StatusCode} {await response.Content.ReadAsStringAsync().ConfigureAwait(false)}");
            return false;
        }
        catch (Exception ex)
        {
            Log($"Register exception: {ex.Message}");
            return false;
        }
    }

    public static List<FeedbackReport> GetReports()
    {
        lock (Sync)
        {
            try
            {
                if (!File.Exists(StatePath)) return new List<FeedbackReport>();
                var json = File.ReadAllText(StatePath);
                return JsonSerializer.Deserialize<List<FeedbackReport>>(json, JsonOptions) ?? new List<FeedbackReport>();
            }
            catch { return new List<FeedbackReport>(); }
        }
    }

    public static List<FeedbackReport> GetActiveReports()
    {
        var reports = GetReports();
        var cutoff = DateTime.Now - ReportTtl;
        var expired = reports.Where(r => r.CreatedAt < cutoff).ToList();
        if (expired.Count > 0)
        {
            foreach (var r in expired) reports.Remove(r);
            SaveReports(reports);
        }
        return reports.Where(r => !string.IsNullOrEmpty(r.ThreadId)).ToList();
    }

    public static async Task<List<(string ReportId, RelayMessage Message)>> PollForRepliesAsync()
    {
        var allReports = GetReports();
        var reports = allReports.Where(r => !string.IsNullOrEmpty(r.ThreadId)).ToList();
        if (reports.Count == 0) return new List<(string, RelayMessage)>();

        var results = new List<(string, RelayMessage)>();
        var changed = false;
        var cutoff = DateTime.Now - ReportTtl;

        foreach (var report in reports)
        {
            if (report.CreatedAt < cutoff)
            {
                allReports.Remove(report);
                changed = true;
                continue;
            }

            bool registered;
            lock (Sync) registered = RegisteredThisRun.Contains(report.ReportId);
            if (!registered && !await TryRegisterAsync(report).ConfigureAwait(false))
                continue;

            try
            {
                var url = $"{RelayBaseUrl}/replies/{report.ReportId}?after={report.LastSeenMessageId}";
                if (!string.IsNullOrEmpty(report.LastPollUtc))
                    url += $"&edited_after={Uri.EscapeDataString(report.LastPollUtc)}";

                using var request = BuildRequest(HttpMethod.Get, url);
                using var response = await Http.SendAsync(request).ConfigureAwait(false);

                if (response.StatusCode == HttpStatusCode.NotFound)
                {
                    Log($"Report {report.ReportId} not found on relay; removing");
                    allReports.Remove(report);
                    changed = true;
                    continue;
                }
                if (!response.IsSuccessStatusCode) continue;

                var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                var messages = JsonSerializer.Deserialize<List<RelayMessage>>(json, JsonOptions);
                if (messages == null || messages.Count == 0) continue;

                var batchSeen = new HashSet<string>();
                string newestId = report.LastSeenMessageId;
                foreach (var msg in messages)
                {
                    if (!batchSeen.Add(msg.Id)) continue;
                    if (string.CompareOrdinal(msg.Id, report.LastSeenMessageId) > 0)
                    {
                        if (string.CompareOrdinal(msg.Id, newestId) > 0) newestId = msg.Id;
                    }
                    report.LastReplyMessageId = msg.Id;
                    results.Add((report.ReportId, msg));
                }

                if (string.CompareOrdinal(newestId, report.LastSeenMessageId) != 0)
                {
                    report.LastSeenMessageId = newestId;
                    changed = true;
                }
                report.LastPollUtc = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
                changed = true;
            }
            catch (Exception ex)
            {
                Log($"Poll {report.ReportId} exception: {ex.Message}");
            }
        }

        if (changed) SaveReports(allReports);

        return results;
    }

    public static async Task<List<RelayMessage>?> GetConversationAsync(string reportId)
    {
        try
        {
            using var request = BuildRequest(HttpMethod.Get, $"{RelayBaseUrl}/replies/{reportId}");
            using var response = await Http.SendAsync(request).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                Log($"GetConversation {reportId}: HTTP {(int)response.StatusCode}");
                return null;
            }
            var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            return JsonSerializer.Deserialize<List<RelayMessage>>(json, JsonOptions) ?? new List<RelayMessage>();
        }
        catch (Exception ex)
        {
            Log($"GetConversation {reportId} exception: {ex.Message}");
            return null;
        }
    }

    public static async Task<bool> SendReplyAsync(string reportId, string content)
    {
        if (string.IsNullOrWhiteSpace(content)) return false;
        try
        {
            var payload = new Dictionary<string, object> { ["reportId"] = reportId, ["content"] = content };
            var json = JsonSerializer.Serialize(payload);
            using var request = BuildRequest(HttpMethod.Post, $"{RelayBaseUrl}/reply", json);
            using var response = await Http.SendAsync(request).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                Log($"SendReply failed: {(int)response.StatusCode} {await response.Content.ReadAsStringAsync().ConfigureAwait(false)}");
                return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            Log($"SendReply exception: {ex.Message}");
            return false;
        }
    }

    private static void Upsert(FeedbackReport report)    {
        var reports = GetReports();
        reports.RemoveAll(r => r.ReportId == report.ReportId);
        reports.Insert(0, report);
        SaveReports(reports);
    }

    private static void SaveReports(List<FeedbackReport> reports)
    {
        lock (Sync)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(StatePath)!);
                File.WriteAllText(StatePath, JsonSerializer.Serialize(reports, JsonOptions));
            }
            catch { }
        }
    }

    private static void Log(string message)
    {
        try
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AcEvoFfbTuner");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "feedback_relay.log"),
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {message}\n");
        }
        catch { }
    }
}
