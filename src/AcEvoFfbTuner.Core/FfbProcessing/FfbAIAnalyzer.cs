using System.IO;
using System.Net.Http.Headers;
using System.Runtime.Serialization;
using System.Text.Json;
using System.Text.Json.Serialization;
using AcEvoFfbTuner.Core.Profiles;

namespace AcEvoFfbTuner.Core.FfbProcessing;

public sealed class FfbAIAnalyzer : IDisposable
{
    private readonly HttpClient _http;
    private readonly string _apiKey;
    private string _model;
    private string _baseUrl;

    /// <summary>
    /// Process-stable session id sent as `x-opencode-session`. The Go gateway
    /// requires it for routing/prompt-caching and rejects chat requests
    /// without it (HTTP 400 MissingSessionID). Stable per process so the
    /// provider can cache prompts across turns of a conversation.
    /// </summary>
    private static readonly string SessionId = Guid.NewGuid().ToString("N");

    /// <summary>
    /// Go models served via the OpenAI Responses API
    /// (`/responses`, NOT `/chat/completions`) — see
    /// https://opencode.ai/docs/go/#endpoints. Sending these to
    /// /chat/completions fails (observed HTTP 500 on muse-spark).
    /// </summary>
    private static readonly HashSet<string> ResponsesApiModels = new(StringComparer.OrdinalIgnoreCase)
    {
        "muse-spark-1.3-contributor",
        "muse-spark-1.2-contributor",
        "grok-4.6",
        "gpt-5.6-luna",
    };

    /// <summary>
    /// Go models served via the Anthropic Messages API (`/messages`) with a
    /// non-OpenAI wire format. The coach only speaks OpenAI Chat and
    /// Responses formats, so these are rejected with a clear message.
    /// </summary>
    private static readonly HashSet<string> MessagesApiModels = new(StringComparer.OrdinalIgnoreCase)
    {
        "minimax-m3",
        "minimax-m2.7",
        "minimax-m2.5",
        "qwen3.8-max",
        "qwen3.8-flash",
        "qwen3.7-max",
        "qwen3.7-plus",
        "qwen3.6-plus",
        "union-alpha",
    };

    private bool UseResponsesApi => ResponsesApiModels.Contains(_model);
    private bool UseMessagesApi => MessagesApiModels.Contains(_model);

    private static string MessagesApiNotSupported(string model) =>
        $"The model '{model}' uses the Anthropic Messages endpoint, which the FFB Coach does not support. " +
        "Pick a chat-compatible model in Settings (e.g. deepseek-v4-flash, mimo-v2.5, glm-5.2, kimi-k2.6).";
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip,
        Converters = { new LenientFloatConverter() }
    };

    public string Model
    {
        get => _model;
        set => _model = !string.IsNullOrWhiteSpace(value) ? value : "deepseek-v4-flash";
    }

    public string BaseUrl
    {
        get => _baseUrl;
        set => _baseUrl = !string.IsNullOrWhiteSpace(value) ? value.TrimEnd('/') : "https://opencode.ai/zen/go/v1";
    }

    public FfbAIAnalyzer(string apiKey, string model = "deepseek-v4-flash", string baseUrl = "https://opencode.ai/zen/go/v1")
    {
        _apiKey = apiKey ?? "";
        _model = !string.IsNullOrWhiteSpace(model) ? model : "deepseek-v4-flash";
        _baseUrl = !string.IsNullOrWhiteSpace(baseUrl) ? baseUrl.TrimEnd('/') : "https://opencode.ai/zen/go/v1";
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(300) };
        // The Go gateway expects a real client UA (not a generic SDK name)
        // and a stable x-opencode-session per conversation for routing.
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("AcEvoFfbTuner-AICoach/1.0");
        _http.DefaultRequestHeaders.Add("x-opencode-session", SessionId);
    }

    private void ApplyAuth(HttpRequestMessage request)
    {
        if (!string.IsNullOrEmpty(_apiKey))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
    }

    /// <summary>
    /// Extracts assistant text from an OpenAI Responses API response body
    /// (`output[].content[].text`). Returns null when no text is found.
    /// </summary>
    private static string? ExtractResponsesText(string responseBody)
    {
        try
        {
            using var doc = JsonDocument.Parse(responseBody);
            if (!doc.RootElement.TryGetProperty("output", out var output) ||
                output.ValueKind != JsonValueKind.Array)
                return null;
            var sb = new System.Text.StringBuilder();
            foreach (var item in output.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                if (!item.TryGetProperty("content", out var content) ||
                    content.ValueKind != JsonValueKind.Array)
                    continue;
                foreach (var part in content.EnumerateArray())
                {
                    if (part.ValueKind != JsonValueKind.Object) continue;
                    if (part.TryGetProperty("text", out var text) &&
                        text.ValueKind == JsonValueKind.String)
                        sb.Append(text.GetString());
                }
            }
            var s = sb.ToString();
            return string.IsNullOrWhiteSpace(s) ? null : s;
        }
        catch
        {
            return null;
        }
    }



    private string SystemPrompt => BuildSystemPrompt();

    /// <summary>
    /// Fetches the live model list from the gateway's /models endpoint (no auth required
    /// on OpenCode Go) and layers on price/context info from <see cref="OpenCodeGoModelCatalog"/>.
    /// Returns an empty list on failure so callers can fall back to the built-in catalog.
    /// </summary>
    public async Task<List<AiModelInfo>> ListModelsAsync(CancellationToken ct = default)
    {
        var results = new List<AiModelInfo>();
        try
        {
            using var requestCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            requestCts.CancelAfter(TimeSpan.FromSeconds(12));

            var request = new HttpRequestMessage(HttpMethod.Get, $"{_baseUrl}/models");
            if (!string.IsNullOrEmpty(_apiKey))
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);

            using var response = await _http.SendAsync(request, requestCts.Token);
            if (!response.IsSuccessStatusCode)
            {
                Log("WARN", $"ListModels HTTP {(int)response.StatusCode}");
                return results;
            }

            var body = await response.Content.ReadAsStringAsync(ct);
            var parsed = JsonSerializer.Deserialize<OpenAiModelsResponse>(body, JsonOpts);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in parsed?.Data ?? [])
            {
                if (string.IsNullOrWhiteSpace(entry.Id) || !seen.Add(entry.Id)) continue;
                var info = OpenCodeGoModelCatalog.Lookup(entry.Id);
                results.Add(info is null
                    ? new AiModelInfo(entry.Id, entry.Id, null, null, null, false)
                    : info);
            }
            Log("OK", $"ListModels: {results.Count} models from {_baseUrl}");
            return results;
        }
        catch (Exception ex)
        {
            Log("ERR", $"ListModels failed: {ex.Message}");
            return results;
        }
    }

    public async Task<ConversationResult> ChatAsync(List<ChatMessage> conversation, CancellationToken ct = default)
    {
        if (UseMessagesApi)
        {
            Log("WARN", $"Model {_model} needs the Messages endpoint (unsupported)");
            return new ConversationResult { Reply = MessagesApiNotSupported(_model) };
        }

        // Responses-API models (muse-spark, grok, gpt-luna) live on
        // /responses with an `input` array; everything else uses
        // /chat/completions with `messages`. 8192 output tokens is plenty
        // for coach replies (131072 OOMs the gateway for some models).
        var useResponses = UseResponsesApi;
        var endpoint = useResponses ? "responses" : "chat/completions";
        object body = useResponses
            ? new
            {
                model = _model,
                input = conversation,
                temperature = 0.4,
                max_output_tokens = 8192
            }
            : new
            {
                model = _model,
                messages = conversation,
                temperature = 0.4,
                max_tokens = 8192
            };

        var bodyJson = JsonSerializer.Serialize(body, JsonOpts);
        LogRequest("chat", bodyJson);

        var request = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/{endpoint}")
        {
            Content = new StringContent(bodyJson, System.Text.Encoding.UTF8, "application/json")
        };
        ApplyAuth(request);

        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, ct);
        }
        catch (Exception ex)
        {
            Log("ERR", $"HTTP request failed: {ex.Message}");
            return new ConversationResult { Reply = $"AI request failed: {ex.Message}. Check your connection and try again." };
        }

        var responseBody = await response.Content.ReadAsStringAsync(ct);
        LogResponse("chat", (int)response.StatusCode, responseBody);

        if (!response.IsSuccessStatusCode)
        {
            Log("ERR", $"HTTP {response.StatusCode}: {responseBody}");
            return new ConversationResult { Reply = BuildHttpErrorReply((int)response.StatusCode, responseBody) };
        }

        string? content = useResponses
            ? ExtractResponsesText(responseBody)
            : JsonSerializer.Deserialize<OpenAiChatResponse>(responseBody, JsonOpts)
                ?.Choices?.FirstOrDefault()?.Message?.Content;

        if (string.IsNullOrWhiteSpace(content))
        {
            Log("WARN", "AI returned empty content");
            return new ConversationResult { Reply = "AI returned an empty response. The model may not support structured output. Try a different model in Settings." };
        }

        var stripped = StripMarkdownFence(content);
        if (string.IsNullOrWhiteSpace(stripped))
        {
            Log("WARN", "After stripping markdown, content was empty");
            return new ConversationResult { Reply = "AI returned only markdown formatting. Try a different model." };
        }

        try
        {
            var result = JsonSerializer.Deserialize<ConversationResult>(stripped, JsonOpts);
            Log("OK", $"Parsed ConversationResult with {result?.Recommendations?.Count ?? 0} recs, {result?.Answers?.Count ?? 0} answers");
            return result ?? new ConversationResult { Reply = "I couldn't parse that. Could you rephrase?" };
        }
        catch (JsonException ex)
        {
            Log("WARN", $"JSON parse failed: {ex.Message} | stripped={Truncate(stripped, 500)}");

            // Completely non-JSON (plain text like "Hello! Welcome...") — show as-is
            if (!stripped.Contains('{'))
            {
                return new ConversationResult
                {
                    Reply = stripped,
                    Answers =
                    [
                        new() { Id = "talk", Label = "Continue", Description = "Keep tuning" },
                        new() { Id = "finish", Label = "Finish", Description = "See summary" }
                    ]
                };
            }

            // Partial JSON — try to extract reply text and string recommendations
            try
            {
                ConversationRecommendation[]? stringRecs = null;
                var partial = JsonSerializer.Deserialize<JsonElement>(stripped, JsonOpts);
                if (partial.TryGetProperty("reply", out var replyEl))
                {
                    var replyText = replyEl.GetString() ?? stripped;
                    if (partial.TryGetProperty("recommendations", out var recsEl) && recsEl.ValueKind == JsonValueKind.Array)
                    {
                        var recList = new List<ConversationRecommendation>();
                        foreach (var item in recsEl.EnumerateArray())
                        {
                            if (item.ValueKind == JsonValueKind.String)
                            {
                                var text = item.GetString() ?? "";
                                var parts = text.Split(new[] { " from ", " to " }, StringSplitOptions.None);
                                recList.Add(new ConversationRecommendation
                                {
                                    Parameter = parts.Length >= 1 ? parts[0].Trim() : text,
                                    Reason = text,
                                    Impact = text
                                });
                            }
                        }
                        if (recList.Count > 0)
                            stringRecs = recList.ToArray();
                    }
                    if (stringRecs != null)
                    {
                        return new ConversationResult
                        {
                            Reply = replyText,
                            Recommendations = [.. stringRecs],
                            Answers =
                            [
                                new() { Id = "talk", Label = "Continue", Description = "Keep tuning" },
                                new() { Id = "finish", Label = "Finish", Description = "See summary" }
                            ]
                        };
                    }
                    return new ConversationResult
                    {
                        Reply = replyText,
                        Answers =
                        [
                            new() { Id = "talk", Label = "Continue", Description = "Keep tuning" },
                            new() { Id = "finish", Label = "Finish", Description = "See summary" }
                        ]
                    };
                }
            }
            catch { }
            return new ConversationResult
            {
                Reply = stripped,
                Answers =
                [
                    new() { Id = "retry_json", Label = "Retry as JSON", Description = "Ask AI to reformat as structured data" },
                    new() { Id = "finish", Label = "Finish", Description = "See summary" }
                ]
            };
        }
    }

    public async Task<AIAnalysisResult> AnalyzeAsync(SnapshotCsvData csvData, FfbProfile profile, string gameContext, List<string>? diagnostics = null, CancellationToken ct = default)
    {
        if (UseMessagesApi)
        {
            Log("WARN", $"Model {_model} needs the Messages endpoint (unsupported)");
            return CreateFallbackResult(MessagesApiNotSupported(_model));
        }

        var messages = BuildMessages(csvData, profile, gameContext, diagnostics);
        var useResponses = UseResponsesApi;
        var endpoint = useResponses ? "responses" : "chat/completions";
        object body = useResponses
            ? new
            {
                model = _model,
                input = messages,
                temperature = 0.3,
                max_output_tokens = 8192
            }
            : new
            {
                model = _model,
                messages,
                temperature = 0.3,
                max_tokens = 8192
            };

        var bodyJson = JsonSerializer.Serialize(body, JsonOpts);
        LogRequest("analyze", bodyJson);

        var request = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/{endpoint}")
        {
            Content = new StringContent(bodyJson, System.Text.Encoding.UTF8, "application/json")
        };
        ApplyAuth(request);

        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, ct);
        }
        catch (Exception ex)
        {
            Log("ERR", $"HTTP request failed: {ex.Message}");
            return CreateFallbackResult($"AI request failed: {ex.Message}");
        }

        var responseBody = await response.Content.ReadAsStringAsync(ct);
        LogResponse("analyze", (int)response.StatusCode, responseBody);

        if (!response.IsSuccessStatusCode)
        {
            Log("ERR", $"HTTP {response.StatusCode}: {responseBody}");
            return CreateFallbackResult(BuildHttpErrorReply((int)response.StatusCode, responseBody));
        }

        string? content = useResponses
            ? ExtractResponsesText(responseBody)
            : JsonSerializer.Deserialize<OpenAiChatResponse>(responseBody, JsonOpts)
                ?.Choices?.FirstOrDefault()?.Message?.Content;

        if (string.IsNullOrWhiteSpace(content))
        {
            Log("WARN", "AI returned empty content");
            return CreateFallbackResult("AI returned empty response. The model may not support this request. Try a different model in Settings.");
        }

        var stripped = StripMarkdownFence(content);
        if (string.IsNullOrWhiteSpace(stripped))
        {
            Log("WARN", "After stripping markdown, content was empty");
            return CreateFallbackResult("AI returned only markdown formatting. Try a different model.");
        }

        try
        {
            var result = JsonSerializer.Deserialize<AIAnalysisResult>(stripped, JsonOpts);
            Log("OK", $"Parsed AIAnalysisResult with {result?.Issues?.Count ?? 0} issues, {result?.Issues?.Sum(i => i.Recommendations?.Count ?? 0) ?? 0} recs");
            return result ?? CreateFallbackResult("Failed to parse AI response.");
        }
        catch (JsonException ex)
        {
            Log("WARN", $"JSON parse failed: {ex.Message} | stripped={Truncate(stripped, 500)}");
            return CreateFallbackResult($"AI response parsing failed: {ex.Message}");
        }
    }

    private List<object> BuildMessages(SnapshotCsvData csvData, FfbProfile profile, string gameContext, List<string>? diagnostics = null)
    {
        var profileSection = BuildProfileSection(profile);
        var statsSection = BuildStatsSection(csvData);
        var csvSection = BuildCsvSection(csvData);

        var userContent = string.Empty;
        if (diagnostics != null && diagnostics.Count > 0)
        {
            userContent = "=================================================================\n" +
                "⚠️  MANDATORY DIAGNOSTIC CHECKS — Address Each Below First\n" +
                "=================================================================\n" +
                "The following CRITICAL and HIGH severity issues were detected in the profile.\n" +
                "You MUST include each applicable issue in your response as an issue with HIGH severity.\n" +
                "Do NOT make any tuning recommendations until these are addressed.\n\n" +
                string.Join("\n\n", diagnostics.Select(d => $"• {d}")) +
                "\n\n=================================================================\n" +
                "END OF DIAGNOSTICS — Now analyze the telemetry below for additional tuning.\n" +
                "=================================================================\n\n";
        }

        var requestGame = CoachParameterCatalog.ParseGameName(gameContext);
        userContent += $@"## Game Context
{gameContext}

## Pipeline Path For The Detected Game
{CoachParameterCatalog.PipelinePathFor(requestGame)}

{CoachParameterCatalog.BuildKnowledgeTable(requestGame)}

## Current Profile Values
```json
{profileSection}
```

## Telemetry Statistics
```
{statsSection}
```

{csvSection}

Analyze this telemetry data and the current profile values. Identify FFB issues and provide specific, actionable recommendations.";

        return
        [
            new { role = "system", content = SystemPrompt },
            new { role = "user", content = userContent }
        ];
    }

    private static string BuildProfileSection(FfbProfile profile)
    {
        return JsonSerializer.Serialize(new
        {
            profile.OutputGain,
            profile.ForceScale,
            profile.CompressionPower,
            profile.SoftClipThreshold,
            profile.SignCorrectionEnabled,
            profile.WheelMaxTorqueNm,
            profile.SteeringLockDegrees,
            MzFrontGain = profile.MzFront?.Gain,
            FxFrontGain = profile.FxFront?.Gain,
            FyFrontGain = profile.FyFront?.Gain,
            MzRearGain = profile.MzRear?.Gain,
            profile.WheelLoadWeighting,
            profile.MzScale,
            profile.FxScale,
            profile.FyScale,
            Damping = new
            {
                profile.Damping?.ViscousDamping,
                profile.Damping?.SpeedDamping,
                profile.Damping?.Friction,
                profile.Damping?.Inertia
            },
            Slip = new
            {
                profile.Slip?.SlipRatioGain,
                profile.Slip?.SlipAngleGain,
                profile.Slip?.SlipAngleShapeGain,
                profile.Slip?.SlipThreshold,
                profile.Slip?.UseFrontOnly,
                profile.Slip?.CoreForceMultiplier,
                profile.Slip?.BrakeBoostGain,
                profile.Slip?.BrakeBoostThreshold
            },
            Dynamic = new
            {
                profile.Dynamic?.LateralGGain,
                profile.Dynamic?.LongitudinalGGain,
                profile.Dynamic?.SuspensionGain,
                profile.Dynamic?.YawRateGain
            },
            Vibrations = new
            {
                profile.Vibrations?.KerbGain,
                profile.Vibrations?.SlipGain,
                profile.Vibrations?.RoadGain,
                profile.Vibrations?.AbsGain,
                profile.Vibrations?.MasterGain,
                profile.Vibrations?.SuspensionRoadGain,
                profile.Vibrations?.ScrubGain,
                profile.Vibrations?.RearSlipGain,
                profile.Vibrations?.CurbSeverityScale,
                profile.Vibrations?.ScrubForceScale
            },
            Advanced = new
            {
                profile.Advanced?.MaxSlewRate,
                profile.Advanced?.CenterSuppressionDegrees,
                profile.Advanced?.CenterSharpnessDegrees,
                profile.Advanced?.CenterKneePower,
                profile.Advanced?.HysteresisThreshold,
                profile.Advanced?.NoiseFloor,
                profile.Advanced?.CenterBlendDegrees,
                profile.Advanced?.CoreForceMultiplier,
                profile.Advanced?.LowSpeedSmoothKmh
            },
            TyreFlex = new
            {
                profile.TyreFlex?.FlexGain,
                profile.TyreFlex?.CarcassStiffness,
                profile.TyreFlex?.FlexSmoothing,
                profile.TyreFlex?.LoadFlexGain
            },
            StaticFriction = new
            {
                profile.StaticFriction?.Gain,
                profile.StaticFriction?.MaxElasticStretch,
                profile.StaticFriction?.SpringStiffness,
                profile.StaticFriction?.KineticFrictionBase,
                profile.StaticFriction?.EngineOffDamping,
                profile.StaticFriction?.EngineOnDamping,
                profile.StaticFriction?.BreakoutGain,
                profile.StaticFriction?.ScrubGain
            },
            GripGuard = new
            {
                profile.GripGuard?.Enabled,
                profile.GripGuard?.PeakSlipAngle,
                profile.GripGuard?.AttenuationStrength,
                profile.GripGuard?.MechanicalTrailGain
            },
            Crass = new
            {
                profile.Crash?.Enabled,
                profile.Crash?.ImpactGain,
                profile.Crash?.SafetyClamp,
                profile.Crash?.TriggerThresholdG
            },
            WetWeather = new
            {
                profile.WetWeather?.Enabled,
                profile.WetWeather?.RoadVibSuppression,
                profile.WetWeather?.PeakSlipAngleMultiplier,
                profile.WetWeather?.DampingReduction
            }
        }, JsonOpts);
    }

    private static string BuildStatsSection(SnapshotCsvData csvData)
    {
        return csvData.StatsText;
    }

    private static string BuildCsvSection(SnapshotCsvData csvData)
    {
        if (csvData.CsvLines.Count == 0) return "";

        var header = csvData.CsvLines[0];
        var totalRows = csvData.CsvLines.Count - 1;

        int sampleCount = Math.Min(200, totalRows);
        var line = new List<string> { header };

        if (totalRows <= sampleCount)
        {
            line.AddRange(csvData.CsvLines.Skip(1));
        }
        else
        {
            double step = (double)totalRows / sampleCount;
            for (int i = 0; i < sampleCount; i++)
            {
                int idx = 1 + (int)(i * step);
                idx = Math.Clamp(idx, 1, totalRows);
                line.Add(csvData.CsvLines[idx]);
            }
        }

        return $@"## Telemetry CSV Data ({totalRows} total rows, sampled to {sampleCount})
```
{string.Join('\n', line)}
```";
    }

    /// <summary>
    /// Builds a user-facing error message that distinguishes auth problems
    /// (401/403 = key/endpoint) from upstream provider failures (500/502/503 =
    /// the model or gateway failed, key is fine — try a different model).
    /// </summary>
    private static string BuildHttpErrorReply(int status, string responseBody)
    {
        var detail = Truncate(responseBody?.Trim() ?? "", 300);
        if (status == 401 || status == 403)
            return $"AI returned HTTP {status}. Your API key was rejected — check the key and endpoint in Settings." +
                   (detail.Length > 0 ? $" Server said: {detail}" : "");
        if (status == 429)
            return "AI rate limit reached (HTTP 429) — wait a minute and try again." +
                   (detail.Length > 0 ? $" Server said: {detail}" : "");
        if (status == 400 && detail.Contains("MissingSessionID", StringComparison.OrdinalIgnoreCase))
            return "AI gateway rejected the request (MissingSessionID) — the app did not send a session header. " +
                   "Update to the latest build and try a chat-compatible model (deepseek-v4-flash, mimo-v2.5, glm-5.2).";
        if (status is 500 or 502 or 503)
            return $"AI provider returned HTTP {status} — this is a server-side failure, your API key is fine. " +
                   $"Try a chat-compatible model in Settings (deepseek-v4-flash, mimo-v2.5, glm-5.2)." +
                   (detail.Length > 0 ? $" Server said: {detail}" : "");
        return $"AI returned HTTP {status}. Check your endpoint in Settings." +
               (detail.Length > 0 ? $" Server said: {detail}" : "");
    }

    private static AIAnalysisResult CreateFallbackResult(string reason)
    {
        return new AIAnalysisResult
        {
            Summary = reason,
            Issues =
            [
                new AIAnalysisIssue
                {
                    Type = "fallback",
                    Severity = "info",
                    Description = "AI analysis unavailable. Using rule-based fallback.",
                }
            ]
        };
    }

    public void Dispose() => _http.Dispose();

    private static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "AcEvoFfbTuner", "ai_coach.log");

    public static event Action<string>? OnLog;

    private static void Log(string category, string message)
    {
        var line = $"[{DateTime.Now:HH:mm:ss.fff}] [{category}] {message}";
        OnLog?.Invoke(line);
        try { File.AppendAllText(LogPath, line + Environment.NewLine); }
        catch { }
    }

    private static void LogRequest(string endpoint, string body)
    {
        Log("REQ", $"{endpoint} | body={Truncate(body, 2000)}");
    }

    private static void LogResponse(string endpoint, int status, string body)
    {
        Log("RES", $"{endpoint} | {status} | body={Truncate(body, 4000)}");
    }

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s[..max] + "...";

    private static string StripMarkdownFence(string input)
    {
        if (string.IsNullOrWhiteSpace(input)) return input;
        var trimmed = input.Trim();

        // Try extracting a ```json ... ``` block embedded in the text
        int jsonStart = trimmed.IndexOf("```json");
        if (jsonStart >= 0)
        {
            int contentStart = jsonStart + 7;
            int fenceEnd = trimmed.IndexOf("```", contentStart);
            if (fenceEnd >= 0)
                trimmed = trimmed[(contentStart)..fenceEnd].Trim();
        }

        // Try extracting any ``` ... ``` block
        int fenceStart = trimmed.IndexOf("```");
        if (fenceStart >= 0)
        {
            int newlineAfterFence = trimmed.IndexOf('\n', fenceStart);
            if (newlineAfterFence > 0)
            {
                int closeFence = trimmed.IndexOf("```", newlineAfterFence);
                if (closeFence > newlineAfterFence)
                    trimmed = trimmed[(newlineAfterFence + 1)..closeFence].Trim();
            }
        }

        // Strip leading/trailing fences if the whole content is a code block
        if (trimmed.StartsWith("```"))
        {
            int start = trimmed.IndexOf('\n');
            if (start > 0) trimmed = trimmed[(start + 1)..];
        }
        if (trimmed.EndsWith("```"))
            trimmed = trimmed[..^3].TrimEnd();

        // If it still doesn't start with { or [, look for the first { in the text
        // (AI often wraps conversational text around the JSON)
        if (!trimmed.StartsWith("{") && !trimmed.StartsWith("["))
        {
            int firstBrace = trimmed.IndexOf('{');
            if (firstBrace >= 0)
            {
                trimmed = trimmed[firstBrace..];
                // Find matching closing brace
                int depth = 0;
                for (int i = 0; i < trimmed.Length; i++)
                {
                    if (trimmed[i] == '{') depth++;
                    else if (trimmed[i] == '}')
                    {
                        depth--;
                        if (depth == 0)
                        {
                            trimmed = trimmed[..(i + 1)];
                            break;
                        }
                    }
                }
            }
        }

        // Sanitize: AI sometimes writes comments like "1.0 (pipeline)" or "3.0 (profile)" after number values
        // Fix: remove trailing parenthesized comments after numbers: "value (text)" → "value"
        trimmed = System.Text.RegularExpressions.Regex.Replace(
            trimmed,
            @"(:[\s]*[-0-9.]+)\s*\([^)]*\)",
            "$1");

        // Also handle "/ value (text)" patterns like "1.0 / 3.0 (profile advanced value)"
        trimmed = System.Text.RegularExpressions.Regex.Replace(
            trimmed,
            @"(:[\s]*[-0-9.]+)\s*/\s*[-0-9.]+[^,}\]]*",
            "$1");

        return trimmed.Trim();
    }

    private string BuildSystemPrompt()
    {
        return @"You are an expert sim racing force feedback (FFB) tuner integrated into the FFB Coach tool. You are running on OpenCode Go.

## Your Role
You are a specialized FFB tuning agent for the AcEvoFfbTuner application. You ONLY discuss force feedback tuning, FFB profiles, telemetry analysis, and sim racing wheel setup for RaceRoom, Assetto Corsa, ACC, and Le Mans Ultimate.

## Scope Boundary — Do NOT Answer Off-Topic Questions
You must politely refuse any question that is NOT about FFB tuning, sim racing telemetry, wheel setup, or driving feel.
- If asked about general topics (weather, news, coding, math, history, etc.), respond: ""I'm an FFB tuning assistant and can only help with force feedback and sim racing setup questions.""
- Do NOT generate any output that is not directly related to FFB tuning.
- This saves tokens and keeps the conversation focused.

## CRITICAL — Run These Checks FIRST
Before making any tuning recommendations, you MUST check every item below and include ANY found issues in your response:

1. **Force inversion** — Column-force games (R3E, LMU): MzFrontGain MUST be NEGATIVE (-0.3 to -0.5). If positive → wheel fights steering. Check signCorrectionEnabled too.
2. **Dead channel mix** — Are ALL channel gains zero? Core FFB is disabled. User feels only vibrations.
3. **Clipping root cause** — ClippingPct > 10%? First check OutputGain, then ForceScale
   (slider 0.1–5, R3E/LMU only, default 1.0 — LOWER values amplify and cause clipping).
4. **Wrong CoreForceMultiplier** — Column-force games expect ~3.0x. Physics-based games: ignore.
5. **Center suppression > 5°** — Creates deadzone.
6. **Static friction masking** — Gain > 1.5 + MaxElasticStretch > 0.03 = rubber-band feel.
7. **All vibrations zero** — MasterGain = 0 and all vib gains = 0? Detail path dead.

## IMPORTANT: Column-Force vs Physics-Based Games
- **Column-force games (R3E, LMU)**: The game provides SteeringForce (total column torque), NOT real Mz
  The reader internally converts SteeringForce to an Mz signal that the pipeline processes
  So the profile shows MzFrontGain, MzScale, etc. — these affect the CONVERTED signal
  When talking to the user, explain in terms of steering force or column force, not self-aligning torque
  CoreForceMultiplier (default 3.0) compensates for attenuation in the conversion
  BrakeBoost adds force under braking (simulates weight transfer)
  SlipAngleGain should be 0 (slip angle data is unreliable)

- **Physics-based games (EVO, ACC, AC)**: Real Mz/Fx/Fy telemetry from the game
  MzFrontGain should be POSITIVE (0.2 to 0.6)
  Slip angle data is reliable — SlipAngleGain can be used
  No CoreForceMultiplier or BrakeBoost needed

## FFB Pipeline Architecture

The FFB signal has TWO paths that are mixed together:

### 1. CORE PATH (Zero-Latency)
Raw Mz (self-aligning torque) → Normalize → LUT curve → Damping → Output Gain → Centering shaping → Grip Guard → Crash detection → Tyre condition → Hydroplaning → Speed fade. NO filtering, NO slew limit. This is the primary centering force that prevents oscillation.

### 2. DETAIL PATH (Filtered)
Additive effects only: Slip enhancement + Dynamic effects + Tyre flex + Vibration signals (curb, road, ABS, scrub, rear slip, offtrack) + LFE. Runs through EQ biquads and slew rate limiter. These are ""feel"" details, not centering force.

### FINAL MIX
Core + Detail → Soft clip → Noise floor gate → Output to wheel.

## Wheel Torque
The wheel model and max torque arrive per request in the Game Context section. On low-torque
wheels (e.g. Moza R5 5.5 Nm) efficient use of the torque range is critical: avoid excessive
clipping, but don't leave too much headroom either.

## Game & Pipeline (per-request — the Game Context section states the actual game)
- **Column-force games (R3E, LMU)**: shared memory provides SteeringForce column torque,
  converted to an Mz signal with a sqrt center ramp (0-3°). Fx/Fy are load-synthesized
  estimates, slip-angle telemetry is unreliable (keep SlipAngleGain at 0), MzFrontGain is
  NEGATIVE, CoreForceMultiplier (~3.0x) and BrakeBoost apply.
- **Physics-based games (EVO, ACC, AC)**: real Mz/Fx/Fy telemetry, MzFrontGain POSITIVE,
  slip-angle data is reliable, no CoreForceMultiplier/BrakeBoost concept.
- A per-request pipeline + tunable-parameter table is appended to the user message.
  IT IS AUTHORITATIVE for the detected game. The ranges below are the real UI slider limits.

## Parameter Guide (Range = REAL UI slider min–max. NEVER suggest a value outside it.)

### Strength & Output
| Parameter | Slider Range | Effect |
|-----------|-------|--------|
| OutputGain | 0–20 | Final multiplier on total output. 0.6-0.8 typical for 5.5Nm wheel |
| CompressionPower | NO SLIDER (profile-only) | Exponential curve on core force. Prefer OutputGain |
| ForceScale | 0.1–5 (R3E/LMU slider only, default 1.0) | Inverse sensitivity. LOWER = stronger. NOT 700-1500 |
| SoftClipThreshold | 0.1–1 (UI: Max Force Limit) | Maximum output clamp. 0.8 typical |
| CoreForceMultiplier | 0–10 (R3E/LMU slider only) | Column-force core boost. 3.0 typical |

### Centering & On-Center Feel
| Parameter | Slider Range | Effect |
|-----------|-------|--------|
| CenterSuppressionDegrees | 0–30 | V-shape force reduction near center. 1-3 typical. Higher = more deadzone feel |
| CenterSharpnessDegrees | 0–10 (R3E/LMU only) | Smoothstep ramp. 0 = disabled. 1-3 = progressive feel |
| CenterBlendDegrees | 0–10 | Transition zone between center and full suppression |
| CenterKneePower | 0.5–3 | Non-linear core shaping. >1 = progressive. 1 = linear |
| HysteresisThreshold | NO SLIDER (profile-only) | Deadband for noise rejection. Suggest NoiseFloor instead |
| NoiseFloor | 0–0.05 | Minimum output gate. 0.003-0.01 typical |
| MaxSlewRate | 0.03–0.5 | Per-frame rate limit on DETAIL path only. 0.3-0.5 typical |

### Damping (UI section: EVO/AC/LMU only — do NOT suggest for R3E/ACC)
| Parameter | Slider Range | Effect |
|-----------|-------|--------|
| ViscousDamping | 0–1 | Speed-proportional resistance. 0.03-0.15 typical |
| SpeedDamping | 0–25 | Additional high-speed damping. 0.1-0.4 typical |
| Friction | 0–20 (UI: Friction Level) | Coulomb friction (constant resistance). 0.01-0.08 typical |
| Inertia | 0–20 (UI: Inertia Weight) | Angular inertia simulation. 0.02-0.15 typical |

### Channel Mix (Which Forces Reach the Wheel)
| Parameter | Slider Range | Effect |
|-----------|-------|--------|
| MzFrontGain | -10 to 10 | Self-aligning torque. NEGATIVE for R3E/LMU (-0.3 to -0.5), POSITIVE for EVO/AC/ACC (0.2-0.6) |
| FxFrontGain | -10 to 10 | Longitudinal force (traction/braking). 0-0.2 typical |
| FyFrontGain | -10 to 10 | Lateral force (cornering). 0-0.2 typical |
| MzScale | 1-500 | Scaling divisor for Mz channel. Higher = weaker Mz contribution. Default 30 |

### Slip Enhancement
| Parameter | Slider Range | Effect |
|-----------|-------|--------|
| SlipRatioGain | 0–5 | Force enhancement from tire slip ratio. 0.05-0.2 typical |
| SlipAngleGain | 0–5 (physics games only) | Enhancement from slip angle. Use 0 for R3E/LMU (unreliable data) |
| SlipThreshold | 0–1 | Slip detection threshold. 0.1-0.25 typical |

### Dynamic Effects (UI section: EVO/AC/R3E only)
| Parameter | Slider Range | Effect |
|-----------|-------|--------|
| LateralGGain | 0–5 (UI: Cornering Force) | Cornering G-force feel. 0.05-0.3 typical |
| LongitudinalGGain | 0–5 (UI: Accel/Braking Force) | Acceleration/braking G-force. 0.05-0.2 typical |
| SuspensionGain | 0–5 (UI: Road Feel) | Road surface texture from suspension. 0.05-0.15 typical |
| YawRateGain | 0–5 (UI: Car Rotation Force) | Car rotation feel. 0.02-0.1 typical |

### Vibrations (UI section hidden for ACC — do NOT suggest vibration gains for ACC)
| Parameter | Slider Range | Effect |
|-----------|-------|--------|
| MasterGain | 0–10 (UI: Vibration Master Gain) | Global vibration volume. 0.3-0.7 typical |
| KerbGain | 0–10 (UI: Curb Gain) | Curb/rumble strip intensity. 1-2 typical |
| RoadGain | 0–10 (UI: Road Gain) | Road surface texture vibration. 0.3-1 typical |
| SlipGain | 0–10 (UI: Slip Gain) | Tire slip vibration (wheel slip). 0.5-1.5 typical |
| AbsGain | 0–10 | ABS pulsing vibration. 0.5-1.5 typical |
| SuspensionRoadGain | 0–10 | Suspension-based road feel. 0.5-2 typical |
| ScrubGain | 0–1 | Tire scrub texture. 0.2-0.8 typical |

## Recommendation Rules (HARD — the app discards violating responses)
- `parameter` MUST be tunable for the detected game (see the per-request table). Never suggest a NOT-available parameter.
- `suggestedValue` MUST be inside the slider min–max and differ from `currentValue` by a meaningful step.
- Every recommendation needs `reason` (WHY it fixes the data issue) and `impact` (what the driver will feel).

### Static Friction — Parking Lot Feel (universal: all games, standstill only)
| Parameter | Slider Range | Effect |
|-----------|-------|--------|
| Gain (StaticFrictionGain) | 0–2 | Overall static friction strength. 1-1.5 typical |
| MaxElasticStretch | 0.001–0.05 | How far wheel stretches before breaking friction. 0.01-0.03 typical |
| SpringStiffness | 1-50 | Centering spring rate. 20-50 typical |
| KineticFrictionBase | 0–1 | Sliding friction when wheels not driven. 0.1-0.25 typical |
| BreakoutGain | 0–1 | Stick-slip judder near breakaway. 0.2-0.5 typical |
| ScrubGain | 0–1 | Tyre-twist grain texture. 0.2-0.4 typical |

## Tuning Guidelines

### Clipping (>5% of frames at max output)
- Reduce OutputGain (multiply by 0.7-0.85)
- OR lower SoftClipThreshold slightly (UI: Max Force Limit, slider 0.1–1)
- NOTE: CompressionPower has NO slider — do not present it as a drag adjustment
- OR lower SoftClipThreshold slightly
- For 5.5Nm wheel: clipping is worse than low detail — prioritize eliminating clipping

### Weak Signal (avg output < 0.08, max < 0.3)
- Increase OutputGain (multiply by 1.2-1.5; slider allows 0–20, stay near 0.5-1.0 for DD wheels)
- OR increase channel gains (MzFront, FxFront)
- OR lower ForceScale toward 0.5 (R3E/LMU slider 0.1–5: LOWER = stronger)

### Oscillation / Bounceback (force oscillates after steering input)
- REDUCE MaxSlewRate (slower detail = calmer; slider 0.03–0.5)
- Increase ViscousDamping slightly (EVO/AC/LMU only — hidden for R3E/ACC)
- Check CenterSuppressionDegrees isn't too high (causes snap when leaving center)
- Increase CenterBlendDegrees for smoother center transition

### Snapping / Spike (sudden force jumps)
- Reduce MaxSlewRate (slider 0.03–0.5)
- Verify MzFrontGain sign (NEGATIVE for R3E/LMU, POSITIVE for EVO/AC/ACC)
- Increase NoiseFloor slightly (HysteresisThreshold has NO slider — do not suggest it as a drag adjustment)
- Reduce SuspensionRoadGain if spikes correlate with road texture

### Excessive On-Center Stiffness
- Increase CenterSuppressionDegrees
- Reduce CenterSharpnessDegrees
- Reduce CenterKneePower toward 1.0
- Reduce ViscousDamping

### Too Much Vibration / Buzz
- Reduce MasterGain (vibration global volume, slider 0–10; never for ACC — section hidden)
- Identify which vibration channel is problematic (kerb, road, slip) and reduce individually
- Check SlipGain isn't amplifying noise
- Increase NoiseFloor for baseline noise rejection (HysteresisThreshold has NO slider)

### Not Enough Road Feel
- Increase SuspensionRoadGain
- Increase RoadGain
- Increase ScrubGain for tire texture
- Ensure VibrationMasterGain isn't too low

## Analysis Response Format (first message)
For the initial analysis, use this format:
```json
{
  ""summary"": ""Brief overall assessment of the FFB quality based on data"",
  ""issues"": [
    {
      ""type"": ""clipping"" | ""weak"" | ""oscillation"" | ""snap"" | ""noise"" | ""stiff_center"" | ""low_detail"" | ""balanced"",
      ""severity"": ""high"" | ""medium"" | ""low"",
      ""description"": ""What the data shows and why it matters"",
      ""recommendations"": [
        {
          ""parameter"": ""OutputGain"",
          ""currentValue"": 0.8,
          ""suggestedValue"": 0.64,
          ""reason"": ""Clear explanation of why this change helps"",
          ""impact"": ""What the driver will feel after the change""
        }
      ]
    }
  ]
}
```

## Conversation Response Format (follow-up messages)
When the user replies to your analysis or asks a question, use this format:
```json
{
  ""reply"": ""Your natural language response to the user. Be conversational, specific, and helpful. Reference their previous message when appropriate."",
  ""icon"": ""🤖"",
  ""recommendations"": [
    {
      ""parameter"": ""OutputGain"",
      ""currentValue"": 0.8,
      ""suggestedValue"": 0.64,
      ""reason"": ""Why this change helps with what the user is experiencing"",
      ""impact"": ""What they'll feel""
    }
  ],
  ""answers"": [
    { ""id"": ""apply"", ""label"": ""Apply Change"", ""description"": ""Set OutputGain to 0.64"" },
    { ""id"": ""skip"", ""label"": ""Skip"", ""description"": ""Keep current value"" },
    { ""id"": ""finish"", ""label"": ""Finish"", ""description"": ""See summary"" }
  ]
}
```

The ""answers"" array provides clickable buttons for the user. Use it ONLY for your own follow-ups
(""continue"", ""finish"", clarifying questions). Do NOT emit ""apply"", ""skip"" or ""apply_*"" IDs —
the app adds one Apply button per recommendation automatically, so every profile change is one click away.

Stay concise, be specific with numbers, and prioritize the most impactful change first.";
    }
}

public sealed class ChatMessage
{
    [JsonPropertyName("role")]
    public string Role { get; set; } = "user";

    [JsonPropertyName("content")]
    public string Content { get; set; } = "";
}

public sealed class ConversationResult
{
    [JsonPropertyName("reply")]
    public string Reply { get; set; } = "";

    [JsonPropertyName("icon")]
    public string Icon { get; set; } = "🤖";

    [JsonPropertyName("recommendations")]
    public List<ConversationRecommendation> Recommendations { get; set; } = [];

    [JsonPropertyName("answers")]
    public List<ConversationAnswer> Answers { get; set; } = [];
}

public sealed class ConversationRecommendation
{
    [JsonPropertyName("parameter")]
    public string Parameter { get; set; } = "";

    [JsonPropertyName("currentValue")]
    public float CurrentValue { get; set; }

    [JsonPropertyName("suggestedValue")]
    public float SuggestedValue { get; set; }

    [JsonPropertyName("reason")]
    public string Reason { get; set; } = "";

    [JsonPropertyName("impact")]
    public string Impact { get; set; } = "";
}

public sealed class ConversationAnswer
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("label")]
    public string Label { get; set; } = "";

    [JsonPropertyName("description")]
    public string Description { get; set; } = "";
}

public sealed class AIAnalysisResult
{
    [JsonPropertyName("summary")]
    public string Summary { get; set; } = "";

    [JsonPropertyName("issues")]
    public List<AIAnalysisIssue> Issues { get; set; } = [];
}

public sealed class AIAnalysisIssue
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "";

    [JsonPropertyName("severity")]
    public string Severity { get; set; } = "medium";

    [JsonPropertyName("description")]
    public string Description { get; set; } = "";

    [JsonPropertyName("recommendations")]
    public List<AIRecommendation> Recommendations { get; set; } = [];
}

public sealed class AIRecommendation
{
    [JsonPropertyName("parameter")]
    public string Parameter { get; set; } = "";

    [JsonPropertyName("currentValue")]
    public float CurrentValue { get; set; }

    [JsonPropertyName("suggestedValue")]
    public float SuggestedValue { get; set; }

    [JsonPropertyName("reason")]
    public string Reason { get; set; } = "";

    [JsonPropertyName("impact")]
    public string Impact { get; set; } = "";
}

internal sealed class OpenAiChatResponse
{
    [JsonPropertyName("choices")]
    public List<OpenAiChoice>? Choices { get; set; }
}

internal sealed class OpenAiChoice
{
    [JsonPropertyName("message")]
    public OpenAiMessage? Message { get; set; }
}

internal sealed class OpenAiMessage
{
    [JsonPropertyName("content")]
    public string? Content { get; set; }
}

internal sealed class OpenAiModelsResponse
{
    [JsonPropertyName("data")]
    public List<OpenAiModelEntry>? Data { get; set; }
}

internal sealed class OpenAiModelEntry
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("owned_by")]
    public string? OwnedBy { get; set; }
}

internal sealed class LenientFloatConverter : System.Text.Json.Serialization.JsonConverter<float>
{
    public override float Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Number)
            return reader.GetSingle();
        if (reader.TokenType == JsonTokenType.String)
        {
            var s = reader.GetString();
            if (string.IsNullOrWhiteSpace(s)) return 0f;
            if (float.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var val))
                return val;
            return 0f;
        }
        return 0f;
    }

    public override void Write(Utf8JsonWriter writer, float value, JsonSerializerOptions options)
        => writer.WriteNumberValue(value);
}
