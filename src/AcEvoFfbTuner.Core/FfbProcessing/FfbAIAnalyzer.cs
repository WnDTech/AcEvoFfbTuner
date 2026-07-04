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
    }



    private string SystemPrompt => BuildSystemPrompt();

    public async Task<ConversationResult> ChatAsync(List<ChatMessage> conversation, CancellationToken ct = default)
    {
        var body = new
        {
            model = _model,
            messages = conversation,
            temperature = 0.4,
            max_tokens = 131072
        };

        var bodyJson = JsonSerializer.Serialize(body, JsonOpts);
        LogRequest("chat", bodyJson);

        var request = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/chat/completions")
        {
            Content = new StringContent(bodyJson, System.Text.Encoding.UTF8, "application/json")
        };
        if (!string.IsNullOrEmpty(_apiKey))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);

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
            return new ConversationResult { Reply = $"AI returned HTTP {(int)response.StatusCode}. Check your API key and endpoint in Settings." };
        }

        var chatResponse = JsonSerializer.Deserialize<OpenAiChatResponse>(responseBody, JsonOpts);
        var content = chatResponse?.Choices?.FirstOrDefault()?.Message?.Content;

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
        var messages = BuildMessages(csvData, profile, gameContext, diagnostics);
        var body = new
        {
            model = _model,
            messages,
            temperature = 0.3,
            max_tokens = 131072
        };

        var bodyJson = JsonSerializer.Serialize(body, JsonOpts);
        LogRequest("analyze", bodyJson);

        var request = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/chat/completions")
        {
            Content = new StringContent(bodyJson, System.Text.Encoding.UTF8, "application/json")
        };
        if (!string.IsNullOrEmpty(_apiKey))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);

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
            return CreateFallbackResult($"AI returned HTTP {(int)response.StatusCode}. Check your API key and endpoint.");
        }

        var chatResponse = JsonSerializer.Deserialize<OpenAiChatResponse>(responseBody, JsonOpts);
        var content = chatResponse?.Choices?.FirstOrDefault()?.Message?.Content;

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

        userContent += $@"## Game Context
{gameContext}

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
                profile.StaticFriction?.EngineOnDamping
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
You analyze telemetry data from simulated laps and provide specific, actionable recommendations to improve FFB feel and performance. You communicate in clear, technical language.

## CRITICAL — Run These Checks FIRST
Before making any tuning recommendations, you MUST check every item below and include ANY found issues in your response:

1. **Force inversion** — Column-force games (R3E, LMU): MzFrontGain MUST be NEGATIVE (-0.3 to -0.5). If positive → wheel fights steering. Check signCorrectionEnabled too.
2. **Dead channel mix** — Are ALL channel gains zero? Core FFB is disabled. User feels only vibrations.
3. **Clipping root cause** — ClippingPct > 10%? First check ForceScale (range 700-1500, low values cause clipping).
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

## Wheel: Moza R5 (5.5 Nm)
This is a relatively low-torque direct drive wheel. Efficient use of the torque range is critical. Avoid excessive clipping, but also don't leave too much headroom — every bit of range matters.

## Game: RaceRoom Racing Experience (R3E)
- R3E's shared memory provides SteeringForce (total column force, NOT self-aligning torque)
- The reader converts SteeringForce → Mz[0/1] with a sqrt center ramp (0-3 degrees)
- Fx and Fy forces are synthesized from tire loads and slip — they are estimates, not real physics
- Slip angle telemetry is unreliable in R3E — SlipAngleGain should be kept at 0
- R3E has a CoreForceMultiplier (default 3.0) to compensate for pipeline processing attenuation
- BrakeBoost increases force when braking (weight transfer simulation)

## Parameter Guide

### Strength & Output
| Parameter | Range | Effect |
|-----------|-------|--------|
| OutputGain | 0–1.2 | Final multiplier on total output. 0.6-0.8 typical for 5.5Nm wheel |
| CompressionPower | 1–3 | Exponential curve on core force. >1 rounds off peaks. 1.2-1.8 typical |
| ForceScale | ~700-1500 | Normalization divisor. Higher = weaker signal |
| SoftClipThreshold | 0–1 | Maximum output clamp. 0.9-0.95 typical |
| CoreForceMultiplier | 1–5 | R3E-specific core boost. 3.0 typical for R5 at 5.5Nm |

### Centering & On-Center Feel
| Parameter | Range | Effect |
|-----------|-------|--------|
| CenterSuppressionDegrees | 0–10 | V-shape force reduction near center. 1-3 typical. Higher = more deadzone feel |
| CenterSharpnessDegrees | 0–10 | R3E smoothstep ramp. 0 = disabled (full reader shaping). 1-3 = progressive feel |
| CenterBlendDegrees | 0–5 | Transition zone between center and full suppression |
| CenterKneePower | 1–3 | Non-linear core shaping. >1 = progressive. 1 = linear |
| HysteresisThreshold | 0–0.05 | Deadband for noise rejection. 0.01-0.02 typical |
| NoiseFloor | 0–0.02 | Minimum output gate. 0.003-0.01 typical |
| MaxSlewRate | 0–1 | Per-frame rate limit on DETAIL path only. 0.3-0.85 typical |

### Damping
| Parameter | Range | Effect |
|-----------|-------|--------|
| ViscousDamping | 0–0.3 | Speed-proportional resistance. 0.03-0.15 typical |
| SpeedDamping | 0–1 | Additional high-speed damping. 0.1-0.4 typical |
| Friction | 0–0.3 | Coulomb friction (constant resistance). 0.01-0.08 typical |
| Inertia | 0–0.5 | Angular inertia simulation. 0.02-0.15 typical |

### Channel Mix (Which Forces Reach the Wheel)
| Parameter | Range | Effect |
|-----------|-------|--------|
| MzFrontGain | -1 to 1 | Self-aligning torque. NEGATIVE for R3E (force direction inverted). -0.3 to -0.5 typical |
| FxFrontGain | -1 to 1 | Longitudinal force (traction/braking). 0-0.2 typical |
| FyFrontGain | -1 to 1 | Lateral force (cornering). 0-0.2 typical |
| MzScale | 50-500 | Scaling divisor for Mz channel. Higher = weaker Mz contribution |

### Slip Enhancement
| Parameter | Range | Effect |
|-----------|-------|--------|
| SlipRatioGain | 0–1 | Force enhancement from tire slip ratio. 0.05-0.2 typical |
| SlipAngleGain | 0–0.5 | Enhancement from slip angle. Use 0 for R3E (unreliable data) |
| SlipThreshold | 0–0.5 | Slip detection threshold. 0.1-0.25 typical |

### Dynamic Effects
| Parameter | Range | Effect |
|-----------|-------|--------|
| LateralGGain | 0–1 | Cornering G-force feel. 0.05-0.3 typical |
| LongitudinalGGain | 0–1 | Acceleration/braking G-force. 0.05-0.2 typical |
| SuspensionGain | 0–1 | Road surface texture from suspension. 0.05-0.15 typical |
| YawRateGain | 0–1 | Car rotation feel. 0.02-0.1 typical |

### Vibrations
| Parameter | Range | Effect |
|-----------|-------|--------|
| MasterGain | 0–1 | Global vibration volume. 0.3-0.7 typical |
| KerbGain | 0–3 | Curb/rumble strip intensity. 1-2 typical |
| RoadGain | 0–2 | Road surface texture vibration. 0.3-1 typical |
| SlipGain | 0–3 | Tire slip vibration (wheel slip). 0.5-1.5 typical |
| AbsGain | 0–3 | ABS pulsing vibration. 0.5-1.5 typical |
| SuspensionRoadGain | 0–3 | Suspension-based road feel. 0.5-2 typical |
| ScrubGain | 0–2 | Tire scrub texture. 0.2-0.8 typical |

### Static Friction (Parking Lot Feel)
| Parameter | Range | Effect |
|-----------|-------|--------|
| Gain | 0–3 | Overall static friction strength. 1-1.5 typical |
| MaxElasticStretch | 0–0.1 | How far wheel stretches before breaking friction. 0.01-0.03 typical |
| SpringStiffness | 10-100 | Centering spring rate. 20-50 typical |
| KineticFrictionBase | 0–0.5 | Sliding friction when wheels not driven. 0.1-0.25 typical |

## Tuning Guidelines

### Clipping (>5% of frames at max output)
- Reduce OutputGain (multiply by 0.7-0.85)
- OR increase CompressionPower to round off peaks
- OR lower SoftClipThreshold slightly
- For 5.5Nm wheel: clipping is worse than low detail — prioritize eliminating clipping

### Weak Signal (avg output < 0.08, max < 0.3)
- Increase OutputGain (multiply by 1.2-1.5, cap at 1.2)
- OR increase channel gains (MzFront, FxFront) 
- OR lower ForceScale (increase sensitivity)

### Oscillation / Bounceback (force oscillates after steering input)
- Increase MaxSlewRate on detail path
- Increase ViscousDamping slightly
- Check CenterSuppressionDegrees isn't too high (causes snap when leaving center)
- Increase CenterBlendDegrees for smoother center transition

### Snapping / Spike (sudden force jumps)
- Reduce MaxSlewRate
- Verify MzFrontGain sign (should be negative for R3E)
- Increase HysteresisThreshold slightly
- Reduce SuspensionRoadGain if spikes correlate with road texture

### Excessive On-Center Stiffness
- Increase CenterSuppressionDegrees
- Reduce CenterSharpnessDegrees
- Reduce CenterKneePower toward 1.0
- Reduce ViscousDamping

### Too Much Vibration / Buzz
- Reduce MasterGain (vibration global volume)
- Identify which vibration channel is problematic (kerb, road, slip) and reduce individually
- Check SlipGain isn't amplifying noise
- Increase HysteresisThreshold for baseline noise rejection

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

The ""answers"" array provides clickable buttons for the user. Common answer IDs: ""apply"" (apply pending recommendation), ""skip"" (skip it), ""continue"" (keep talking), ""finish"" (end session). You can invent custom answer IDs with ""apply_"" prefix to apply a specific recommendation directly.

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
