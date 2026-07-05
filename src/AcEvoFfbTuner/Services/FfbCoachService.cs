using System.Text;
using System.Text.Json;
using AcEvoFfbTuner.Core.FfbProcessing;
using AcEvoFfbTuner.Core.Profiles;
using AcEvoFfbTuner.Core.TrackMapping;

namespace AcEvoFfbTuner.Services;

public enum CoachSessionState
{
    Idle,
    SelectingSource,
    Analyzing,
    Questioning,
    Applying,
    Summary
}

public sealed class CoachMessage
{
    public required string Text { get; init; }
    public bool IsUser { get; init; }
    public List<CoachAnswer>? Answers { get; init; }
    public FfbRecommendation? Recommendation { get; init; }
    public string? Icon { get; init; }
}

public sealed class CoachAnswer
{
    public required string Id { get; init; }
    public required string Label { get; init; }
    public string? Description { get; init; }
}

public sealed class LiveProfilerStats
{
    public float OutputMin { get; init; }
    public float OutputMax { get; init; }
    public float OutputAvg { get; init; }
    public float ClippingPercent { get; init; }
    public float PeakMz { get; init; }
    public float PeakFx { get; init; }
    public float PeakFy { get; init; }
    public int FrameCount { get; init; }
    public int ClipCount { get; init; }
}

public sealed class FfbCoachService
{
    private readonly ProfileManager _profileManager;
    private readonly FfbPipeline _pipeline;
    private readonly Stack<Action> _undoStack = new();
    private CoachResult? _lastResult;
    private SnapshotCsvData? _lastCsvData;
    private LiveProfilerStats? _lastLiveStats;
    private FfbAIAnalyzer? _aiAnalyzer;
    private List<ChatMessage>? _aiConversation;

    public CoachSessionState State { get; private set; } = CoachSessionState.Idle;
    public string CurrentProfileName { get; private set; } = "";
    public string DataSourceLabel { get; private set; } = "";
    public string CurrentGame { get; set; } = "RaceRoom Racing Experience";
    public string CurrentWheel { get; set; } = "Moza R5 (5.5 Nm)";
    public bool IsColumnForceGame { get; set; } = true;

    /// <summary>Comprehensive parameter knowledge base — the single source of truth for the LLM.</summary>
    private static string KnowledgeBase => @"
## FFB Pipeline Architecture

The FFB signal has TWO paths that are mixed together:

### 1. CORE PATH (Zero-Latency)
Raw Mz (self-aligning torque) → Normalize → LUT curve → Damping → Output Gain → Centering shaping → Grip Guard → Crash detection → Tyre condition → Hydroplaning → Speed fade. NO filtering, NO slew limit. This is the primary centering force that prevents oscillation.

### 2. DETAIL PATH (Filtered)
Additive effects only: Slip enhancement + Dynamic effects + Tyre flex + Vibration signals (curb, road, ABS, scrub, rear slip, offtrack) + LFE. Runs through EQ biquads and slew rate limiter. These are ""feel"" details, not centering force.

### FINAL MIX
Core + Detail → Soft clip → Noise floor gate → Output to wheel.

## Mandatory Diagnostic Checklist — Check ALL items before recommending

1. **Force inversion**: Column-force games → MzFrontGain MUST be NEGATIVE (-0.3 to -0.5). Positive = wheel fights steering.
2. **Dead channels**: All channel gains (mzFrontGain, fxFrontGain, fyFrontGain) = 0? Core FFB is disabled. User feels only vibrations.
3. **Clipping root cause**: ClippingPct > 10%? Check ForceScale first (range 700-1500, low values cause clipping). Then OutputGain and CompressionPower.
4. **CoreForceMultiplier**: Column-force games → expect ~3.0x. Physics-based games → ignore this parameter.
5. **Center suppression > 5°**: Creates deadzone. Wheel feels disconnected, no centering force.
6. **Static friction masking FFB**: Gain > 1.5 + MaxElasticStretch > 0.03 = rubber-band feel.
7. **All vibrations zero**: MasterGain = 0 and all vibration gains = 0? Detail path dead — no road texture, kerbs, or slip feel.

## Parameter Definitions

### Strength & Output
| Parameter | Range | Typical | Effect |
|-----------|-------|---------|--------|
| OutputGain | 0–1.2 | 0.6-0.8 | Final multiplier on total output |
| CompressionPower | 1–3 | 1.2-1.8 | Exponential curve on core force. >1 rounds off peaks |
| ForceScale | ~700-1500 | 800-1000 | Normalization divisor. Higher = weaker signal |
| SoftClipThreshold | 0–1 | 0.9-0.95 | Maximum output clamp |
| CoreForceMultiplier | 1–5 | 3.0 | Column-force game boost. Not used in physics-based games |

### Centering & On-Center Feel
| Parameter | Range | Typical | Effect |
|-----------|-------|---------|--------|
| CenterSuppressionDegrees | 0–10 | 1-3 | V-shape force reduction near center |
| CenterSharpnessDegrees | 0–10 | 0-3 | Smoothstep ramp. 0 = disabled |
| CenterBlendDegrees | 0–5 | 1-3 | Transition zone between center and full suppression |
| CenterKneePower | 1–3 | 1-2 | Non-linear core shaping. >1 = progressive |
| HysteresisThreshold | 0–0.05 | 0.01-0.02 | Deadband for noise rejection |
| NoiseFloor | 0–0.02 | 0.003-0.01 | Minimum output gate |
| MaxSlewRate | 0–1 | 0.3-0.85 | Per-frame rate limit on DETAIL path only |
| HysteresisWatchdogFrames | 0-20 | 0-3 | Frames to wait before engaging hysteresis |
| LowSpeedSmoothKmh | 0-60 | 10-20 | Speed below which low-speed smoothing applies |

### Channel Mix (Which Forces Reach the Wheel)
| Parameter | Range | Typical | Effect |
|-----------|-------|---------|--------|
| MzFrontGain | -1..1 | -0.5 to -0.3 | Self-aligning torque. NEGATIVE for column-force games |
| FxFrontGain | -1..1 | 0.05-0.15 | Longitudinal force (traction/braking) |
| FyFrontGain | -1..1 | 0.08-0.2 | Lateral force (cornering) |
| MzRearGain | -1..1 | 0 | Rear Mz contribution |
| WheelLoadWeighting | 0-1 | 0-0.3 | How much wheel load affects channel mix |
| MzScale | 50-500 | 100-300 | Scaling divisor for Mz channel |
| FxScale | 500-5000 | 2000-4000 | Scaling divisor for Fx channel |
| FyScale | 500-5000 | 1500-3000 | Scaling divisor for Fy channel |

### Damping
| Parameter | Range | Typical | Effect |
|-----------|-------|---------|--------|
| ViscousDamping | 0–0.3 | 0.03-0.15 | Speed-proportional resistance |
| SpeedDamping | 0–1 | 0.1-0.4 | Additional damping that scales with car speed |
| Friction | 0–0.3 | 0.01-0.08 | Coulomb friction (constant resistance) |
| Inertia | 0–0.5 | 0.02-0.15 | Angular inertia simulation |
| MaxSpeedReference | 50-300 | 150-250 | Speed at which speed damping reaches max |

### Slip Enhancement
| Parameter | Range | Typical | Effect |
|-----------|-------|---------|--------|
| SlipRatioGain | 0–1 | 0.05-0.2 | Force enhancement from tire slip ratio |
| SlipAngleGain | 0–0.5 | 0 (R3E) | Enhancement from slip angle. Unreliable in column-force games |
| SlipAngleShapeGain | 0-1 | 0.2-0.6 | Shape-based slip angle response |
| SlipThreshold | 0–0.5 | 0.1-0.25 | Slip detection threshold |
| UseFrontOnly | bool | true | Only use front wheel slip data |
| BrakeBoostGain | 0–1 | 0.2-0.5 | Force increase when braking (column-force games) |
| BrakeBoostThreshold | 0–0.5 | 0.05-0.15 | Brake pedal threshold for boost |
| GearChangeMuteEnabled | bool | true | Mute FFB during gear changes |
| GearSpikeThreshold | 0-10000 | 3000 | RPM threshold for gear spike filtering |

### Dynamic Effects
| Parameter | Range | Typical | Effect |
|-----------|-------|---------|--------|
| LateralGGain | 0–1 | 0.05-0.3 | Cornering G-force feel |
| LongitudinalGGain | 0–1 | 0.05-0.2 | Acceleration/braking G-force |
| SuspensionGain | 0–1 | 0.05-0.15 | Road surface texture from suspension |
| YawRateGain | 0–1 | 0.02-0.1 | Car rotation feel |

### Vibrations
| Parameter | Range | Typical | Effect |
|-----------|-------|---------|--------|
| MasterGain | 0–1 | 0.3-0.7 | Global vibration volume |
| KerbGain | 0–3 | 1-2 | Curb/rumble strip intensity |
| RoadGain | 0–2 | 0.3-1 | Road surface texture vibration |
| SlipGain | 0–3 | 0.5-1.5 | Tire slip vibration |
| AbsGain | 0–3 | 0.5-1.5 | ABS pulsing vibration |
| AbsPulseAmplitude | 0-1 | 0.2-0.5 | ABS pulse strength |
| SuspensionRoadGain | 0–3 | 0.5-2 | Suspension-based road feel |
| ScrubGain | 0–2 | 0.2-0.8 | Tire scrub texture |
| RearSlipGain | 0-2 | 0.2-0.6 | Rear wheel slip vibration |
| OfftrackGain | 0-2 | 0.3-0.8 | Off-track surface feel |
| OfftrackSeverityScale | 1-10 | 3-6 | How strongly offtrack feels |
| CurbSeverityScale | 1-20 | 5-12 | Curb impact harshness scale |
| ScrubForceScale | 0-0.01 | 0.0003-0.001 | Tire scrub force normalization |
| RearSlipForceScale | 0-0.01 | 0.0003-0.001 | Rear slip force normalization |

### Tyre Flex
| Parameter | Range | Typical | Effect |
|-----------|-------|---------|--------|
| FlexGain | 0–0.3 | 0.03-0.12 | Tyre flex feel amplitude |
| CarcassStiffness | 0-5 | 0.5-2 | Tyre sidewall stiffness feel |
| FlexSmoothing | 0-1 | 0.3-0.8 | Smoothing on tyre flex signal |
| ContactPatchWeight | 0-1 | 0.3-0.7 | Contact patch deformation feel |
| LoadFlexGain | 0-1 | 0.1-0.5 | Load-dependent flex gain |

### Static Friction (Parking Lot Feel)
| Parameter | Range | Typical | Effect |
|-----------|-------|---------|--------|
| Gain | 0–3 | 1-1.5 | Overall elastic feel strength |
| MaxElasticStretch | 0–0.1 | 0.01-0.03 | Max stretch before breakaway |
| SpringStiffness | 10-100 | 20-50 | Centering spring rate |
| KineticFrictionBase | 0–0.5 | 0.1-0.25 | Sliding friction feel |
| EngineOffDamping | 0-1 | 0.3-0.7 | Extra damping when car is off |
| EngineOnDamping | 0-1 | 0.1-0.4 | Damping reduction when car is on |
| EngineOffScale | 0-1 | 0.5-1 | Overall scale when car is off |
| EngineOnScale | 0-1 | 0.1-0.5 | Overall scale when car is on |
| ActiveDecay | 0-1 | 0.5-0.95 | How fast elastic force builds when turning |
| ReturnDecay | 0-1 | 0.3-0.8 | How fast elastic force returns to center |
| OutputSmoothAlpha | 0-1 | 0.3-0.7 | Smoothing on static friction output |

### Grip Guard
| Parameter | Range | Typical | Effect |
|-----------|-------|---------|--------|
| Enabled | bool | true | Enable grip-dependent attenuation |
| PeakSlipAngle | 0-0.5 | 0.08-0.15 | Slip angle at which grip peaks |
| AttenuationStrength | 0-1 | 0.3-0.7 | How much force is reduced past peak grip |
| MechanicalTrailGain | 0-1 | 0.05-0.3 | Simulated mechanical trail feel |

### Crash Detection
| Parameter | Range | Typical | Effect |
|-----------|-------|---------|--------|
| Enabled | bool | true | Enable crash force limiting |
| ImpactGain | 0-1 | 0.3-0.7 | Crash force amplitude |
| SafetyClamp | 0-1 | 0.3-0.7 | Maximum allowed crash force |
| TriggerThresholdG | 1-10 | 3-6 | G-force threshold to trigger crash response |

### Tyre Condition
| Parameter | Range | Typical | Effect |
|-----------|-------|---------|--------|
| Enabled | bool | true | Enable tyre wear/blowout effects |
| BlowoutVibrationGain | 0-1 | 0.2-0.6 | Vibration strength during blowout |
| PressureLossGain | 0-1 | 0.1-0.4 | Feel of pressure loss |
| DamageAsymmetryGain | 0-1 | 0.1-0.4 | Asymmetric pull from damage |

### Wet Weather
| Parameter | Range | Typical | Effect |
|-----------|-------|---------|--------|
| Enabled | bool | false | Enable wet weather FFB adjustments |
| RoadVibSuppression | 0-1 | 0.5-0.9 | How much road vibration is reduced in rain |
| PeakSlipAngleMultiplier | 0.5-3 | 1.2-2.0 | Slip angle increase on wet surface |
| DampingReduction | 0-1 | 0.2-0.5 | How much damping is reduced in rain |
";

    /// <summary>Build the complete system context document for the LLM.</summary>
    private string BuildSystemContext(SnapshotCsvData? csvData = null, FfbProfile? profile = null, bool isAnalysis = false)
    {
        var sb = new StringBuilder();
        profile ??= _profileManager.ActiveProfile;
        var game = CurrentGame;
        var wheel = CurrentWheel;
        var isColumn = IsColumnForceGame;

        // Game & wheel section
        var gameNotes = isColumn
            ? @"- SteeringForce (NOT self-aligning torque) is provided by the game
- Reader converts SteeringForce → Mz with sqrt center ramp (0-3 degrees)
- Fx/Fy forces are synthesized from tire loads — estimates, not real physics
- Slip angle telemetry is unreliable — SlipAngleGain should be kept at 0
- CoreForceMultiplier (default 3.0x) compensates for pipeline attenuation
- BrakeBoost simulates weight transfer under braking
- MzFrontGain should be NEGATIVE (typically -0.3 to -0.5)"
            : @"- Real physics telemetry with accurate Mz, Fx, Fy channels
- Slip angle data is reliable — SlipAngleGain can be used
- MzFrontGain should generally be POSITIVE (typically 0.2 to 0.6)
- No CoreForceMultiplier or BrakeBoost — those are column-force only";

        sb.AppendLine($"## Game & Wheel");
        sb.AppendLine($"Game: {game}");
        sb.AppendLine($"Wheel: {wheel}");
        sb.AppendLine($"Pipeline: {(isColumn ? "Column-force" : "Physics-based")}");
        sb.AppendLine();
        sb.AppendLine("### Game-Specific Rules");
        sb.AppendLine(gameNotes);
        sb.AppendLine();

        // Knowledge base
        sb.AppendLine(KnowledgeBase.Trim());
        sb.AppendLine();

        // Current profile
        if (profile != null)
        {
            sb.AppendLine("## Current Profile Values");
            sb.AppendLine($"```json");
            sb.AppendLine(BuildProfileFullJson(profile));
            sb.AppendLine($"```");
            sb.AppendLine();
            sb.AppendLine($"Profile name: {profile.Name}");
            sb.AppendLine($"Wheel max torque: {profile.WheelMaxTorqueNm} Nm");
            sb.AppendLine($"Steering lock: {profile.SteeringLockDegrees}°");
            sb.AppendLine($"Sign correction: {(profile.SignCorrectionEnabled ? "enabled" : "disabled")}");
            sb.AppendLine();
        }

        // Telemetry data
        if (csvData != null)
        {
            sb.AppendLine($"\n## Telemetry Data\nSource: {DataSourceLabel}\nRows: {csvData.RowCount}\n");
            if (!string.IsNullOrWhiteSpace(csvData.StatsText))
            {
                sb.AppendLine("### Profiler Statistics");
                sb.AppendLine($"```");
                sb.AppendLine(csvData.StatsText);
                sb.AppendLine($"```");
                sb.AppendLine();
            }
            if (csvData.CsvLines.Count > 0)
            {
                var header = csvData.CsvLines[0];
                var totalRows = csvData.CsvLines.Count - 1;
                int sampleCount = Math.Min(200, totalRows);
                var lines = new List<string> { header };
                if (totalRows <= sampleCount)
                    lines.AddRange(csvData.CsvLines.Skip(1));
                else
                {
                    double step = (double)totalRows / sampleCount;
                    for (int i = 0; i < sampleCount; i++)
                        lines.Add(csvData.CsvLines[1 + (int)Math.Clamp(i * step, 0, totalRows - 1)]);
                }
                sb.AppendLine("### Time-Series Data (sampled)");
                sb.AppendLine($"```");
                sb.AppendLine(string.Join('\n', lines));
                sb.AppendLine($"```");
            }
            sb.AppendLine();
        }

        return sb.ToString();
    }
    public bool IsAiEnabled => _aiAnalyzer != null;
    public bool HasActiveConversation => _aiConversation != null;

    private string? _customInput;

    public void SetCustomInput(string text) => _customInput = text;

    public FfbCoachService(ProfileManager profileManager, FfbPipeline pipeline)
    {
        _profileManager = profileManager;
        _pipeline = pipeline;
    }

    public void SetAiAnalyzer(FfbAIAnalyzer? analyzer)
    {
        _aiAnalyzer = analyzer;
    }

    public List<CoachMessage> BuildWelcomeMessages()
    {
        State = CoachSessionState.SelectingSource;
        return GenerateSourceSelectionMessages();
    }

    private CoachResult GenerateSourceSelectionResult()
    {
        return new CoachResult
        {
            State = CoachSessionState.SelectingSource,
            Messages = GenerateSourceSelectionMessages()
        };
    }

    private List<CoachMessage> GenerateSourceSelectionMessages()
    {
        var files = SnapshotFileLoader.LoadSnapshotFiles();
        var answers = new List<CoachAnswer>();

        answers.Add(new CoachAnswer
        {
            Id = "source_chat",
            Label = "💬 Chat with AI",
            Description = "Ask FFB tuning questions directly"
        });

        answers.Add(new CoachAnswer
        {
            Id = "source_monitor",
            Label = "🎯 Live Monitor",
            Description = "Sample 20s of driving for AI trend analysis"
        });

        if (files.Count > 0)
        {
            answers.Add(new CoachAnswer
            {
                Id = "source_latest",
                Label = $"Snapshot Data ({files[0].Timestamp:MMM dd, HH:mm})",
                Description = files[0].ProfileName
            });

            if (files.Count > 1)
            {
                answers.Add(new CoachAnswer
                {
                    Id = "source_pick",
                    Label = "Choose a Saved Snapshot...",
                    Description = $"{files.Count} snapshots available"
                });
            }
        }

        return
        [
            new CoachMessage
            {
                Text = "🤖  Welcome! I'm your AI FFB Coach. I can analyze your driving data, or you can just ask me questions about tuning.",
                Icon = ""
            },
            new CoachMessage
            {
                Text = "Choose a data source to get started:",
                Answers = answers
            }
        ];
    }

    public async Task<CoachResult> AnalyzeSnapshotAsync(SnapshotCsvData csvData)
    {
        State = CoachSessionState.Analyzing;
        DataSourceLabel = $"Snapshot: {csvData.ProfileName} ({csvData.RowCount} rows)";
        CurrentProfileName = csvData.ProfileName;
        _lastCsvData = csvData;
        _lastLiveStats = null;

        var profile = FindMatchingProfile(csvData.ProfileName);
        _lastResult = await AnalyzeDataAsync(csvData, profile);
        return _lastResult;
    }

    public async Task<CoachResult> AnalyzeLiveDataAsync(LiveProfilerStats stats, string profileName)
    {
        State = CoachSessionState.Analyzing;
        DataSourceLabel = $"Live Data ({stats.FrameCount} frames)";
        CurrentProfileName = profileName;
        _lastLiveStats = stats;
        _lastCsvData = null;

        var profile = FindMatchingProfile(profileName);
        _lastResult = await AnalyzeLiveStatsAsync(stats, profile);
        return _lastResult;
    }

    public async Task<CoachResult> StartChatSessionAsync()
    {
        if (_aiAnalyzer == null)
        {
            State = CoachSessionState.SelectingSource;
            return new CoachResult { State = State, Messages = [.. GenerateSourceSelectionMessages()] };
        }

        var profile = _profileManager.ActiveProfile;
        DataSourceLabel = "Direct Chat";

        var context = BuildSystemContext(profile: profile);
        _aiConversation =
        [
            new() { Role = "system", Content = context },
            new() { Role = "user", Content = "Greet the user and ask what they'd like help with regarding their FFB tuning." }
        ];

        try
        {
            var aiResult = await _aiAnalyzer.ChatAsync(_aiConversation);
            _aiConversation.Add(new ChatMessage { Role = "assistant", Content = JsonSerializer.Serialize(aiResult, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }) });

            var messages = new List<CoachMessage>
            {
                new() { Text = aiResult.Reply, Icon = aiResult.Icon ?? "🤖" }
            };

            var answers = aiResult.Answers.Count > 0
                ? aiResult.Answers.Select(a => new CoachAnswer { Id = a.Id, Label = a.Label, Description = a.Description }).ToList()
                : new List<CoachAnswer> { new() { Id = "finish", Label = "Finish", Description = "Done chatting" } };

            messages.Add(new CoachMessage { Text = "What next?", Answers = answers });

            State = CoachSessionState.Questioning;
            return new CoachResult { State = State, Messages = messages };
        }
        catch (Exception ex)
        {
            _aiConversation = null;
            return new CoachResult
            {
                State = CoachSessionState.SelectingSource,
                Messages =
                [
                    new CoachMessage { Text = $"⚠️ AI chat failed: {ex.Message}", Icon = "⚠️" },
                    .. GenerateSourceSelectionMessages()
                ]
            };
        }
    }

    private string BuildChatSystemPrompt(FfbProfile? profile)
    {
        var game = CurrentGame;
        var wheel = CurrentWheel;
        var isColumn = IsColumnForceGame;

        var gameSpecific = isColumn
            ? @"- Steering force maps to Mz with a sqrt center ramp (0-3 degrees)
- Fx/Fy forces are synthesized from tire loads (estimates, not real physics)
- Slip angle telemetry is unreliable — SlipAngleGain should stay at 0
- CoreForceMultiplier default is 3.0x — this is a key gain for overall feel
- BrakeBoost increases force when braking (simulates weight transfer)
- CenterSharpnessDegrees applies a smoothstep ramp (0 = disabled, uses reader shaping)"
            : @"- Real physics telemetry with accurate Mz, Fx, Fy channels
- Slip angle data is reliable — SlipAngleGain can be used
- MzFrontGain should generally be positive
- No BrakeBoost or CoreForceMultiplier concept — those are R3E/LMU specific";

        return $@"You are an expert sim racing FFB tuner integrated into the FFB Coach tool.

You help the user tune their force feedback settings by answering questions and giving advice.
You have access to their current FFB profile and understand the game and wheel they use.

## Scope Boundary
You ONLY discuss FFB tuning, force feedback, telemetry analysis, wheel setup, and sim racing feel.
If the user asks about anything else (weather, news, coding, general knowledge, etc.), politely refuse:
""I'm an FFB tuning assistant and can only help with force feedback and sim racing setup questions.""
Do not generate off-topic content. This saves tokens and keeps the conversation focused.

## Game: {game}
{wheel}

## Game-Specific Rules
{gameSpecific}

## CRITICAL: Response Format
You MUST respond with valid JSON ONLY. No exceptions. Every response must be a JSON object with the following structure.
Put ALL conversational text in the ``reply`` field. Do NOT write any text outside the JSON structure.

```json
{{
  ""reply"": ""Your full conversational response here. Be helpful, detailed, and specific. This is the only place you write text that the user sees."",
  ""icon"": ""🤖"",
  ""recommendations"": [
    {{
      ""parameter"": ""OutputGain"",
      ""currentValue"": 0.8,
      ""suggestedValue"": 0.64,
      ""reason"": ""Clear explanation of why this change helps the specific issue"",
      ""impact"": ""What the driver will feel on track after the change""
    }}
  ],
  ""answers"": [
    {{ ""id"": ""apply"", ""label"": ""Apply Change"", ""description"": ""Apply this recommendation"" }},
    {{ ""id"": ""skip"", ""label"": ""Skip"", ""description"": ""Keep current value"" }},
    {{ ""id"": ""finish"", ""label"": ""Finish"", ""description"": ""Done for now"" }}
  ]
}}
```

Rules:
- ALL text goes inside ``reply``. No text outside the JSON.
- Include ``recommendations`` when suggesting changes. Set to [] if unsure.
- Include ``answers`` with clickable buttons for the user.
- The ``parameter`` name must be exact (case-insensitive, spaces ignored).

## Exact Parameter Names
Use these EXACT names in the ""parameter"" field, ignoring spaces and case. System normalizes automatically.

Core: OutputGain, CompressionPower, ForceScale, SoftClipThreshold, CoreForceMultiplier
Mix: MzFrontGain, FxFrontGain, FyFrontGain, MzRearGain, WheelLoadWeighting, MzScale, FxScale, FyScale
Damping: ViscousDamping, SpeedDamping, Friction, Inertia, MaxSpeedReference
Slip: SlipRatioGain, SlipAngleGain, SlipAngleShapeGain, SlipThreshold, UseFrontOnly, BrakeBoostGain, BrakeBoostThreshold, GearSpikeThreshold
Dynamic: LateralGGain, LongitudinalGGain, SuspensionGain, YawRateGain
Vibrations: MasterGain, KerbGain, RoadGain, SlipGain, AbsGain, AbsPulseAmplitude, SuspensionRoadGain, ScrubGain, RearSlipGain, OfftrackGain, OfftrackSeverityScale, CurbSeverityScale, ScrubForceScale, RearSlipForceScale
Advanced: MaxSlewRate, CenterSuppressionDegrees, CenterSharpnessDegrees, CenterKneePower, CenterBlendDegrees, HysteresisThreshold, NoiseFloor, HysteresisWatchdogFrames, LowSpeedSmoothKmh
TyreFlex: FlexGain, CarcassStiffness, FlexSmoothing, ContactPatchWeight, LoadFlexGain
GripGuard: GripGuardEnabled, GripGuardPeakSlipAngle, GripGuardAttenuationStrength, GripGuardMechanicalTrailGain
Crash: CrashEnabled, CrashImpactGain, CrashSafetyClamp, CrashTriggerThresholdG
TyreCondition: TyreConditionEnabled, BlowoutVibrationGain, PressureLossGain, DamageAsymmetryGain
WetWeather: WetWeatherEnabled, WetRoadVibSuppression, WetPeakSlipAngleMultiplier, WetDampingReduction
StaticFriction: StaticFrictionGain, StaticFrictionMaxElasticStretch, StaticFrictionSpringStiffness, StaticFrictionKineticFrictionBase, StaticFrictionEngineOffDamping, StaticFrictionEngineOnDamping, StaticFrictionEngineOffScale, StaticFrictionEngineOnScale, StaticFrictionActiveDecay, StaticFrictionReturnDecay, StaticFrictionOutputSmoothAlpha";
    }

    private string BuildChatUserPrompt(FfbProfile? profile)
    {
        var profileJson = profile != null ? BuildProfileFullJson(profile) : "No profile loaded";
        return $@"## Context
Game: {CurrentGame}
Wheel: {CurrentWheel}
Profile name: {profile?.Name ?? "None"}

## Current Profile
{profileJson}

The user wants to chat about their FFB tuning. Greet them and ask what they'd like help with.";
    }

    public async Task<CoachResult> ProcessAnswerAsync(string answerId, List<CoachMessage> conversationHistory)
    {
        // Direct chat start — handled specially
        if (answerId == "source_chat")
            return await StartChatSessionAsync();

        // Navigation answers — always handled
        if (answerId == "restart" || answerId == "source_latest" || answerId == "source_live"
            || answerId == "source_monitor" || answerId == "source_pick" || answerId == "go_back")
            return GenerateSourceSelectionResult();

        // AI path — every answer goes through the LLM
        if (_aiAnalyzer != null && _aiConversation != null)
            return await ProcessAiAnswerAsync(answerId, conversationHistory);
        if (answerId == "__custom__" && _aiConversation != null)
            return await ProcessAiAnswerAsync(answerId, conversationHistory);

        // AI not configured — show source selection as minimal fallback
        State = CoachSessionState.SelectingSource;
        return GenerateSourceSelectionResult();
    }

    private async Task<CoachResult> ProcessAiAnswerAsync(string answerId, List<CoachMessage> conversationHistory)
    {
        if (answerId == "finish" || answerId == "done")
            return BuildSummaryMessages();

        if (answerId == "restart" || answerId == "source_latest" || answerId == "source_live"
            || answerId == "source_monitor" || answerId == "source_pick" || answerId == "go_back"
            )
            return GenerateSourceSelectionResult();

        if (answerId == "apply" && _lastResult?.PendingRecommendation != null)
        {
            var rec = _lastResult.PendingRecommendation;
            var result = ApplyRecommendation(rec);
            _lastResult = null;
            _aiConversation?.Add(new ChatMessage { Role = "system", Content = $"Applied change: {rec.Parameter} = {rec.SuggestedValue:F3} (was {rec.CurrentValue:F3}). The profile has been updated." });
            return result;
        }

        if (answerId.StartsWith("apply_") && _lastResult?.PendingRecommendation != null)
        {
            var rec = _lastResult.PendingRecommendation;
            var result = ApplyRecommendation(rec);
            _lastResult = null;
            _aiConversation?.Add(new ChatMessage { Role = "system", Content = $"Applied change: {rec.Parameter} = {rec.SuggestedValue:F3} (was {rec.CurrentValue:F3}). The profile has been updated." });
            return result;
        }

        if (answerId == "talk" && _lastResult?.Recommendations.Count > 0)
        {
            return ShowRecommendationsFromLastResult();
        }

        try
        {
            string userMessage = answerId switch
            {
                "continue" => "I'd like to continue with the tuning. Tell me what to adjust first.",
                "__custom__" => _customInput ?? "Let me describe what I'm feeling...",
                "retry_json" => "Your last response was not valid JSON. Please respond again using ONLY the JSON format with reply, recommendations, and answers fields. Put all text inside the reply field.",
                _ => ""
            };
            _customInput = null;
            if (string.IsNullOrWhiteSpace(userMessage))
            {
                string lastAnswer = conversationHistory.Count > 0
                    ? conversationHistory[^1].Text
                    : "";
                userMessage = $"User chose: {answerId}";
                if (!string.IsNullOrWhiteSpace(lastAnswer))
                    userMessage += $" (they said: \"{lastAnswer}\")";
            }

            userMessage += $"\n\nWheel: {CurrentWheel} | Game: {CurrentGame} | Profile: {_profileManager.ActiveProfile?.Name ?? "Unknown"} | Source: {DataSourceLabel}";

            _aiConversation!.Add(new ChatMessage { Role = "user", Content = userMessage });

            var aiResult = await _aiAnalyzer!.ChatAsync(_aiConversation);

            _aiConversation.Add(new ChatMessage { Role = "assistant", Content = JsonSerializer.Serialize(aiResult) });

            var messages = new List<CoachMessage>
            {
                new()
                {
                    Text = aiResult.Reply,
                    Icon = aiResult.Icon ?? "🤖"
                }
            };

            var recs = new List<FfbRecommendation>();
            FfbRecommendation? pendingRec = null;

            var recText = new StringBuilder();
            for (int ri = 0; ri < (aiResult.Recommendations?.Count ?? 0); ri++)
            {
                var r = aiResult.Recommendations![ri];
                var fr = new FfbRecommendation
                {
                    Type = RecommendationType.ProfileChange,
                    Parameter = r.Parameter,
                    Description = r.Reason,
                    CurrentValue = r.CurrentValue,
                    SuggestedValue = r.SuggestedValue,
                    Impact = r.Impact
                };
                recs.Add(fr);
                pendingRec ??= fr;

                if (ri == 0) recText.AppendLine("**Suggested adjustments:**\n");
                recText.AppendLine($"**{ri + 1}. {r.Parameter}**");
                recText.AppendLine($"   {r.CurrentValue:F3}  →  **{r.SuggestedValue:F3}**");
                recText.AppendLine($"   {r.Impact}");
                recText.AppendLine();
            }

            if (recText.Length > 0)
            {
                messages.Add(new CoachMessage
                {
                    Text = recText.ToString().TrimEnd(),
                    Icon = "🔧"
                });
            }

            var answers = new List<CoachAnswer>();
            foreach (var a in aiResult.Answers)
            {
                answers.Add(new CoachAnswer
                {
                    Id = a.Id,
                    Label = a.Label,
                    Description = a.Description
                });
            }

            if (pendingRec != null && answers.All(a => a.Id != "apply" && a.Id != "skip"))
            {
                answers.Insert(0, new CoachAnswer { Id = "apply", Label = "Apply Change", Description = $"{pendingRec.Parameter}: {pendingRec.CurrentValue:F2} → {pendingRec.SuggestedValue:F2}" });
                answers.Insert(1, new CoachAnswer { Id = "skip", Label = "Skip", Description = "Keep current" });
            }

            if (answers.Count == 0)
            {
                answers.Add(new CoachAnswer { Id = "continue", Label = "Continue", Description = "More adjustments" });
                answers.Add(new CoachAnswer { Id = "finish", Label = "Finish", Description = "See summary" });
            }

            messages.Add(new CoachMessage
            {
                Text = "What next?",
                Answers = answers
            });

            State = CoachSessionState.Questioning;

            _lastResult = new CoachResult
            {
                State = State,
                Messages = messages,
                Recommendations = recs,
                PendingRecommendation = pendingRec,
                CsvData = _lastCsvData,
                LiveStats = _lastLiveStats
            };

            return _lastResult;
        }
        catch (Exception ex)
        {
            return new CoachResult
            {
                State = CoachSessionState.Questioning,
                Messages =
                [
                    new CoachMessage { Text = $"AI error: {ex.Message}. Type your response below and I'll try again.", Icon = "⚠️" }
                ]
            };
        }
    }

    private CoachResult ShowRecommendationsFromLastResult()
    {
        var recs = _lastResult?.Recommendations;
        if (recs == null || recs.Count == 0)
        {
            State = CoachSessionState.Questioning;
            return new CoachResult
            {
                State = State,
                Messages =
                [
                    new CoachMessage { Text = "No recommendations to show. Try starting a new analysis.", Icon = "ℹ️" }
                ]
            };
        }

        var pendingRec = recs[0];
        var itemList = new System.Text.StringBuilder();
        itemList.AppendLine("**Suggested adjustments:**\n");
        for (int i = 0; i < recs.Count; i++)
        {
            var r = recs[i];
            itemList.AppendLine($"**{i + 1}. {r.Parameter}**");
            itemList.AppendLine($"   {r.CurrentValue:F3}  →  **{r.SuggestedValue:F3}**");
            itemList.AppendLine($"   {r.Impact}");
            itemList.AppendLine();
        }

        var messages = new List<CoachMessage>
        {
            new()
            {
                Text = itemList.ToString().TrimEnd(),
                Icon = "🔧"
            }
        };

        var answers = new List<CoachAnswer>
        {
            new() { Id = "apply", Label = "Apply First Change", Description = $"{pendingRec.Parameter}: {pendingRec.CurrentValue:F2} → {pendingRec.SuggestedValue:F2}" },
            new() { Id = "skip", Label = "Skip", Description = "Keep current" }
        };
        messages.Add(new CoachMessage
        {
            Text = "Which would you like to address first?",
            Answers = answers
        });

        State = CoachSessionState.Questioning;
        _lastResult = new CoachResult
        {
            State = State,
            Messages = messages,
            Recommendations = recs,
            PendingRecommendation = pendingRec,
            CsvData = _lastCsvData,
            LiveStats = _lastLiveStats
        };
        return _lastResult;
    }

    public CoachResult ApplyRecommendation(FfbRecommendation rec)
    {
        var profile = _profileManager.ActiveProfile;
        if (profile == null)
            return new CoachResult
            {
                State = CoachSessionState.Idle,
                Messages =
                [
                    new CoachMessage { Text = "No active profile to modify.", IsUser = false }
                ]
            };

        float oldValue = ReadProfileValue(profile, rec.Parameter);
        WriteProfileValue(profile, rec.Parameter, rec.SuggestedValue);
        profile.ApplyToPipeline(_pipeline);
        _profileManager.SaveProfile(profile);
        _undoStack.Push(() => WriteProfileValue(profile, rec.Parameter, oldValue));
        State = CoachSessionState.Applying;

        return new CoachResult
        {
            State = CoachSessionState.Questioning,
            Messages =
            [
                new CoachMessage
                {
                    Text = $"✓ Applied: {rec.Parameter}: {rec.CurrentValue:F3} → {rec.SuggestedValue:F3}",
                    Icon = "✓"
                },
                new CoachMessage
                {
                    Text = $"*{rec.Impact}*\n\nGive it a test drive and let me know how it feels! Use **Undo** if it doesn't feel right.",
                    Answers =
                    [
                        new CoachAnswer { Id = "continue", Label = "Continue Tuning" },
                        new CoachAnswer { Id = "finish", Label = "See Summary" }
                    ]
                }
            ]
        };
    }

    public CoachResult UndoLastChange()
    {
        if (_undoStack.TryPop(out var undo))
        {
            undo();
            var profile = _profileManager.ActiveProfile;
            if (profile != null)
            {
                profile.ApplyToPipeline(_pipeline);
                _profileManager.SaveProfile(profile);
            }
            return new CoachResult
            {
                State = CoachSessionState.Questioning,
                Messages =
                [
                    new CoachMessage { Text = "Last change reverted.", Icon = "↩" }
                ]
            };
        }

        return new CoachResult
        {
            State = CoachSessionState.Questioning,
            Messages =
            [
                new CoachMessage { Text = "No changes to undo." }
            ]
        };
    }

    public CoachResult BuildSummary()
    {
        State = CoachSessionState.Summary;
        return BuildSummaryMessages();
    }

    private CoachResult HandleApplyAnswer()
    {
        var rec = _lastResult?.PendingRecommendation;
        if (rec == null)
            return new CoachResult
            {
                State = CoachSessionState.Questioning,
                Messages =
                [
                    new CoachMessage { Text = "No pending recommendation to apply.", Icon = "⚠️" }
                ]
            };

        return ApplyRecommendation(rec);
    }

    private CoachResult ApplyOutputGainChange(float multiplier)
    {
        var profile = _profileManager.ActiveProfile;
        if (profile == null) return BuildSummaryMessages();

        float current = profile.OutputGain;
        float suggested = multiplier > 1f
            ? MathF.Min(1.2f, current * multiplier)
            : MathF.Max(0.2f, current * multiplier);

        var rec = new FfbRecommendation
        {
            Type = RecommendationType.ProfileChange,
            Parameter = "OutputGain",
            Description = multiplier > 1f ? "Increase output gain" : "Decrease output gain",
            CurrentValue = current,
            SuggestedValue = suggested,
            Impact = multiplier > 1f ? "Stronger overall FFB" : "Weaker overall FFB, more headroom"
        };

        var result = ApplyRecommendation(rec);
        return new CoachResult
        {
            State = result.State,
            Messages = result.Messages
        };
    }

    private CoachResult ApplyDampingChange(string type)
    {
        var profile = _profileManager.ActiveProfile;
        if (profile == null) return BuildSummaryMessages();

        string param = type;
        float current = ReadProfileValue(profile, param);
        float suggested = current * (current < 0.1f ? 1.5f : 1.2f);

        var rec = new FfbRecommendation
        {
            Type = RecommendationType.ProfileChange,
            Parameter = param,
            Description = $"Adjust {param}",
            CurrentValue = current,
            SuggestedValue = MathF.Min(1.0f, suggested),
            Impact = "Changed damping feel"
        };

        var result = ApplyRecommendation(rec);
        return new CoachResult
        {
            State = result.State,
            Messages = result.Messages
        };
    }

    private CoachResult ApplyVibrationChange(string type)
    {
        var profile = _profileManager.ActiveProfile;
        if (profile == null) return BuildSummaryMessages();

        float current = ReadProfileValue(profile, type);
        float suggested = current * 0.7f;

        var rec = new FfbRecommendation
        {
            Type = RecommendationType.ProfileChange,
            Parameter = type,
            Description = $"Reduce {type}",
            CurrentValue = current,
            SuggestedValue = MathF.Max(0f, suggested),
            Impact = "Reduced vibration feel"
        };

        var result = ApplyRecommendation(rec);
        return new CoachResult
        {
            State = result.State,
            Messages = result.Messages
        };
    }

    public void Reset()
    {
        State = CoachSessionState.Idle;
        _undoStack.Clear();
        CurrentProfileName = "";
        DataSourceLabel = "";
        _lastResult = null;
        _lastCsvData = null;
        _lastLiveStats = null;
        _aiConversation = null;
    }

    private string BuildSystemPromptSlim(FfbProfile profile, SnapshotCsvData csvData)
    {
        var game = CurrentGame;
        var wheel = CurrentWheel;
        var isColumn = IsColumnForceGame;

        var notes = isColumn
            ? "R3E steering force maps to Mz with a sqrt center ramp (0-3 degrees). Fx/Fy synthesized, slip angle unreliable. CoreForceMultiplier default 3.0x."
            : "Real physics Mz/Fx/Fy. Slip angle reliable. MzFrontGain positive. No CoreForceMultiplier or BrakeBoost.";

        return $@"You are an expert sim racing FFB tuner for {game} on {wheel}.
You analyze telemetry and chat with the user to help tune their FFB profile.

## Game Notes
{notes}

## Parameter Quick Reference
| Parameter | Range | Typical | Effect |
|-----------|-------|---------|--------|
| OutputGain | 0-1.2 | 0.6-0.8 | Final output multiplier |
| CompressionPower | 1-3 | 1.2-1.8 | Core force curve exponent |
| MaxSlewRate | 0-1 | 0.3-0.85 | Detail path rate limit |
| CenterSuppression | 0-10 | 1-3 | V-shape center deadzone |
| HysteresisThreshold | 0-0.05 | 0.01-0.02 | Center deadband |
| ViscousDamping | 0-0.3 | 0.03-0.15 | Speed-proportional damping |
| MasterGain (vib) | 0-1 | 0.3-0.7 | Global vibration volume |
| MzFrontGain | {(isColumn ? "-1..1" : "0..1")} | {(isColumn ? "-0.3 to -0.5" : "0.3 to 0.6")} | {(isColumn ? "NEGATIVE for column-force games" : "Positive for physics-based")} |

## Dataset
Profile: {csvData.ProfileName}
Telemetry rows: {csvData.RowCount}
Torque: {csvData.TorqueNm} Nm
 Wheelbase: {wheel}";
    }

    private static string BuildProfileFullJson(FfbProfile profile)
    {
        return JsonSerializer.Serialize(new
        {
            profile.OutputGain,
            profile.ForceScale,
            profile.CompressionPower,
            profile.SoftClipThreshold,
            profile.SignCorrectionEnabled,
            ChannelMix = new { MzFront = profile.MzFront?.Gain, FxFront = profile.FxFront?.Gain, FyFront = profile.FyFront?.Gain, MzRear = profile.MzRear?.Gain, profile.WheelLoadWeighting, profile.MzScale, profile.FxScale, profile.FyScale },
            Damping = new { profile.Damping?.ViscousDamping, profile.Damping?.SpeedDamping, profile.Damping?.Friction, profile.Damping?.Inertia, MaxSpeedReference = profile.Damping?.MaxSpeedReference },
            Slip = new { profile.Slip?.SlipRatioGain, profile.Slip?.SlipAngleGain, profile.Slip?.SlipAngleShapeGain, profile.Slip?.SlipThreshold, profile.Slip?.UseFrontOnly, profile.Slip?.CoreForceMultiplier, profile.Slip?.BrakeBoostGain, profile.Slip?.BrakeBoostThreshold, profile.Slip?.GearChangeMuteEnabled, profile.Slip?.GearSpikeThreshold },
            Dynamic = new { profile.Dynamic?.LateralGGain, profile.Dynamic?.LongitudinalGGain, profile.Dynamic?.SuspensionGain, profile.Dynamic?.YawRateGain },
            Vibrations = new { profile.Vibrations?.KerbGain, profile.Vibrations?.RoadGain, profile.Vibrations?.SlipGain, profile.Vibrations?.AbsGain, profile.Vibrations?.AbsPulseAmplitude, profile.Vibrations?.SuspensionRoadGain, profile.Vibrations?.ScrubGain, profile.Vibrations?.RearSlipGain, profile.Vibrations?.OfftrackGain, profile.Vibrations?.OfftrackSeverityScale, profile.Vibrations?.CurbSeverityScale, profile.Vibrations?.ScrubForceScale, profile.Vibrations?.RearSlipForceScale, profile.Vibrations?.MasterGain },
            Advanced = new { profile.Advanced?.MaxSlewRate, profile.Advanced?.CenterSuppressionDegrees, profile.Advanced?.CenterSharpnessDegrees, profile.Advanced?.CenterKneePower, profile.Advanced?.CenterBlendDegrees, profile.Advanced?.HysteresisThreshold, profile.Advanced?.NoiseFloor, profile.Advanced?.HysteresisWatchdogFrames, profile.Advanced?.CoreForceMultiplier, profile.Advanced?.LowSpeedSmoothKmh },
            TyreFlex = new { profile.TyreFlex?.FlexGain, profile.TyreFlex?.CarcassStiffness, profile.TyreFlex?.FlexSmoothing, profile.TyreFlex?.ContactPatchWeight, profile.TyreFlex?.LoadFlexGain },
            StaticFriction = new { profile.StaticFriction?.Gain, profile.StaticFriction?.MaxElasticStretch, profile.StaticFriction?.SpringStiffness, profile.StaticFriction?.KineticFrictionBase },
            GripGuard = new { profile.GripGuard?.Enabled, profile.GripGuard?.PeakSlipAngle, profile.GripGuard?.AttenuationStrength, profile.GripGuard?.MechanicalTrailGain },
            Crash = new { profile.Crash?.Enabled, profile.Crash?.ImpactGain, profile.Crash?.SafetyClamp, profile.Crash?.TriggerThresholdG },
            TyreCondition = new { profile.TyreCondition?.Enabled, BlowoutVibrationGain = profile.TyreCondition?.BlowoutVibrationGain, PressureLossGain = profile.TyreCondition?.PressureLossGain, DamageAsymmetryGain = profile.TyreCondition?.DamageAsymmetryGain },
            WetWeather = new { profile.WetWeather?.Enabled, profile.WetWeather?.RoadVibSuppression, profile.WetWeather?.PeakSlipAngleMultiplier, profile.WetWeather?.DampingReduction }
        }, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
    }

    private static string BuildInitialAnalysisPrompt(SnapshotCsvData csvData, FfbProfile profile, string gameContext)
    {
        return $@"## Game Context
{gameContext}

## Current Profile Values (full)
```json
{BuildProfileFullJson(profile)}
```

## Telemetry Statistics
```
{csvData.StatsText}
```

Analyze this data. Identify the most important FFB issues and provide specific recommendations. Be conversational and explain what the driver is likely feeling.";
    }

    private static List<string> RunDiagnostics(FfbProfile profile, bool isColumnForce)
    {
        var issues = new List<string>();

        // UNIVERSAL CHECK — applies to ALL games
        // SignCorrectionEnabled inverts the FFB direction. If disabled, the wheel
        // fights steering inputs regardless of any gain settings.
        if (!profile.SignCorrectionEnabled)
            issues.Add("CRITICAL: Sign correction is DISABLED. This inverts the FFB direction — "
                + "the wheel will pull away from center instead of returning to it. "
                + "Enable this for all games.");

        // GAME-SPECIFIC: MzFrontGain sign
        // Physics-based games (EVO, ACC, AC): Mz is real telemetry, gain should be POSITIVE (0.2-0.6)
        // Column-force games (R3E, LMU): Mz is synthesized from steering force, gain should be NEGATIVE (-0.3 to -0.5)
        if (isColumnForce)
        {
            if (profile.MzFront?.Gain >= 0)
                issues.Add("CRITICAL: Force inversion — MzFrontGain is " +
                    (profile.MzFront?.Gain == 0 ? "ZERO" : "POSITIVE") +
                    ". For column-force games this MUST be NEGATIVE (-0.3 to -0.5). "
                    + "The wheel will fight your steering.");
        }
        else
        {
            if (profile.MzFront?.Gain <= 0)
                issues.Add("CRITICAL: Force inversion — MzFrontGain is " +
                    (profile.MzFront?.Gain == 0 ? "ZERO" : "NEGATIVE") +
                    ". For physics-based games this MUST be POSITIVE (0.2 to 0.6). "
                    + "The wheel will feel backwards.");
        }

        // UNIVERSAL: Dead channel mix
        bool allChannelsZero = (profile.MzFront?.Gain ?? 0) == 0
                            && (profile.FxFront?.Gain ?? 0) == 0
                            && (profile.FyFront?.Gain ?? 0) == 0;
        if (allChannelsZero)
            issues.Add("CRITICAL: Dead channel mix — All channel gains (MzFront, FxFront, FyFront) are ZERO. "
                + "The core centering force path is completely disabled.");

        // UNIVERSAL: ForceScale out of range
        if (profile.ForceScale < 100)
            issues.Add("ISSUE: ForceScale extremely low — Current: " + profile.ForceScale.ToString("F0") +
                ". Normal range is 700-1500. This causes massive clipping.");

        // COLUMN-FORCE ONLY: CoreForceMultiplier
        if (isColumnForce && profile.Advanced?.CoreForceMultiplier < 2.0f)
            issues.Add("ISSUE: CoreForceMultiplier too low — Current: " + (profile.Advanced?.CoreForceMultiplier ?? 0).ToString("F1") +
                ". Column-force games typically need ~3.0x. The core centering force feels weak.");

        // UNIVERSAL: CenterSuppression too high
        if (profile.Advanced?.CenterSuppressionDegrees > 5)
            issues.Add("ISSUE: Center suppression too high — " + profile.Advanced.CenterSuppressionDegrees.ToString("F1") +
                "°. Creates a large deadzone.");

        // UNIVERSAL: Static friction masking
        if ((profile.StaticFriction?.Gain ?? 0) > 1.5f && (profile.StaticFriction?.MaxElasticStretch ?? 0) > 0.03f)
            issues.Add("ISSUE: Static friction masking FFB — Gain " + (profile.StaticFriction?.Gain ?? 0).ToString("F2") +
                " + Stretch " + (profile.StaticFriction?.MaxElasticStretch ?? 0).ToString("F3") +
                " creates rubber-band feel that overpowers FFB.");

        // UNIVERSAL: All vibrations zero
        if ((profile.Vibrations?.MasterGain ?? 0) == 0)
            issues.Add("ISSUE: All vibrations disabled — MasterGain is 0. No road texture, kerbs, or slip feel.");

        return issues;
    }

    private async Task<CoachResult> AnalyzeDataAsync(SnapshotCsvData csvData, FfbProfile? profile)
    {
        if (_aiAnalyzer != null && profile != null)
        {
            try
            {
                var context = BuildSystemContext(csvData, profile, isAnalysis: true);
                var diagnostics = RunDiagnostics(profile, IsColumnForceGame);
                var userMsg = diagnostics.Count > 0
                    ? "## CRITICAL ISSUES DETECTED — You MUST address these first before any tuning:\n" +
                      string.Join("\n", diagnostics) +
                      "\n\nAfter addressing these, also analyze the telemetry for additional tuning recommendations."
                    : "Analyze this telemetry data and provide specific, actionable recommendations to improve FFB feel and performance.";

                var aiResult = await _aiAnalyzer.AnalyzeAsync(csvData, profile, $"{CurrentGame}\n{CurrentWheel}", diagnostics);
                _aiConversation = new List<ChatMessage>
                {
                    new() { Role = "system", Content = context },
                    new() { Role = "user", Content = userMsg },
                    new() { Role = "assistant", Content = JsonSerializer.Serialize(aiResult, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }) }
                };
                return ConvertAiResult(aiResult, csvData, profile);
            }
            catch (Exception ex)
            {
                _aiConversation = null;
                return new CoachResult
                {
                    State = CoachSessionState.SelectingSource,
                    Messages =
                    [
                        new CoachMessage { Text = $"⚠️ AI Coach failed: {ex.Message}", Icon = "⚠️" },
                        new CoachMessage { Text = "Check that your OpenCode Go API key is correct in Settings and the endpoint is reachable.", Icon = "ℹ️" },
                        .. GenerateSourceSelectionMessages()
                    ]
                };
            }
        }

        _aiConversation = null;
        if (_aiAnalyzer != null)
        {
            return new CoachResult
            {
                State = CoachSessionState.SelectingSource,
                Messages =
                [
                    new CoachMessage { Text = "⚠️ No active profile found. Select or create a profile first.", Icon = "⚠️" },
                    .. GenerateSourceSelectionMessages()
                ]
            };
        }
        return new CoachResult
        {
            State = CoachSessionState.SelectingSource,
            Messages =
            [
                new CoachMessage { Text = "🤖 AI Coach is not configured. Go to Settings → AI COACH to enter your OpenCode Go API key.", Icon = "ℹ️" },
                .. GenerateSourceSelectionMessages()
            ]
        };
    }

    private CoachResult AnalyzeDataRuleBased(SnapshotCsvData csvData, FfbProfile? profile)
    {
        var recs = new List<FfbRecommendation>();
        var issues = new List<string>();

        float? clippingPct = csvData.ExtractStatValue("ClippingPct")
                            ?? csvData.ExtractStatValue("Clipping");
        float? outputMax = csvData.ExtractStatValue("OutputMax");
        float? outputAvg = csvData.ExtractStatValue("OutputAvg");

        if (clippingPct > 5f)
        {
            recs.Add(new FfbRecommendation
            {
                Type = RecommendationType.ProfileChange,
                Parameter = "OutputGain",
                Description = $"Data shows {clippingPct:F0}% clipping",
                Reason = "Reduce output gain or increase soft clip threshold",
                CurrentValue = profile?.OutputGain ?? 0.62f,
                SuggestedValue = MathF.Max(0.3f, (profile?.OutputGain ?? 0.62f) * 0.8f),
                Impact = "Lower peak forces, less clipping"
            });
            issues.Add("clipping");
        }

        if (outputMax > 0.95f)
        {
            if (!issues.Contains("clipping"))
            {
                recs.Add(new FfbRecommendation
                {
                    Type = RecommendationType.ProfileChange,
                    Parameter = "OutputGain",
                    Description = $"Peak output is {outputMax:F1} — very close to limit",
                    Reason = "Reduce output gain to add headroom",
                    CurrentValue = profile?.OutputGain ?? 0.62f,
                    SuggestedValue = MathF.Max(0.3f, (profile?.OutputGain ?? 0.62f) * 0.85f),
                    Impact = "More headroom, safer for wheel"
                });
                issues.Add("clipping");
            }
        }

        if (outputAvg < 0.08f && outputMax < 0.3f)
        {
            recs.Add(new FfbRecommendation
            {
                Type = RecommendationType.ProfileChange,
                Parameter = "OutputGain",
                Description = $"Output signal seems weak (avg: {outputAvg:F3}, max: {outputMax:F3})",
                Reason = "Increase output gain for more force feedback strength",
                CurrentValue = profile?.OutputGain ?? 0.62f,
                SuggestedValue = MathF.Min(1.2f, (profile?.OutputGain ?? 0.62f) * 1.3f),
                Impact = "Stronger overall FFB feel"
            });
            issues.Add("weak");
        }

        float? mzPeak = csvData.ExtractStatValue("PeakMz");
        float? fxPeak = csvData.ExtractStatValue("PeakFx");
        float? fyPeak = csvData.ExtractStatValue("PeakFy");

        if (!issues.Contains("clipping") && !issues.Contains("weak") && profile != null)
        {
            if (mzPeak > 0.4f || fxPeak > 0.4f || fyPeak > 0.4f)
            {
                recs.Add(new FfbRecommendation
                {
                    Type = RecommendationType.ProfileChange,
                    Parameter = "CompressionPower",
                    Description = $"Channels have moderate peaks (Mz:{mzPeak:F2} Fx:{fxPeak:F2} Fy:{fyPeak:F2})",
                    Reason = "Increase compression to round off peak forces naturally",
                    CurrentValue = profile.CompressionPower,
                    SuggestedValue = MathF.Min(3f, profile.CompressionPower + 0.3f),
                    Impact = "Smoother transitions, more natural feel"
                });
                issues.Add("peaks");
            }
        }

        if (recs.Count == 0)
        {
            var healthyResult = BuildHealthyMessages(profile);
            return new CoachResult
            {
                State = healthyResult.State,
                Messages = healthyResult.Messages,
                Recommendations = recs
            };
        }

        State = CoachSessionState.Questioning;
        return BuildFirstIssueMessages(issues, recs);
    }

    private async Task<CoachResult> AnalyzeLiveStatsAsync(LiveProfilerStats stats, FfbProfile? profile)
    {
        var csvData = new SnapshotCsvData
        {
            ProfileName = profile?.Name ?? "Live",
            TorqueNm = 5.5f,
            StatsText =
                $"OutputMin:          {stats.OutputMin:F6}\n" +
                $"OutputMax:          {stats.OutputMax:F6}\n" +
                $"OutputAvg:          {stats.OutputAvg:F6}\n" +
                $"ClippingPct:        {stats.ClippingPercent:F1}%\n" +
                $"PeakMz:             {stats.PeakMz:F4}\n" +
                $"PeakFx:             {stats.PeakFx:F4}\n" +
                $"PeakFy:             {stats.PeakFy:F4}"
        };

        if (_aiAnalyzer != null && profile != null)
        {
            try
            {
                var context = BuildSystemContext(csvData, profile, isAnalysis: true);
                var diagnostics = RunDiagnostics(profile, IsColumnForceGame);
                var aiResult = await _aiAnalyzer.AnalyzeAsync(csvData, profile, $"Live {stats.FrameCount}f", diagnostics);
                _aiConversation = new List<ChatMessage>
                {
                    new() { Role = "system", Content = context },
                    new() { Role = "user", Content = diagnostics.Count > 0
                        ? "Analyze this live data. CRITICAL issues found: " + string.Join("; ", diagnostics)
                        : "Analyze this live telemetry data and provide specific, actionable recommendations." },
                    new() { Role = "assistant", Content = JsonSerializer.Serialize(aiResult, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }) }
                };
                return ConvertAiResult(aiResult, csvData, profile);
            }
            catch (Exception ex)
            {
                _aiConversation = null;
                return new CoachResult
                {
                    State = CoachSessionState.SelectingSource,
                    Messages =
                    [
                        new CoachMessage { Text = $"⚠️ AI Coach failed: {ex.Message}", Icon = "⚠️" },
                        new CoachMessage { Text = "Check your OpenCode Go API key in Settings and try again.", Icon = "ℹ️" },
                        .. GenerateSourceSelectionMessages()
                    ]
                };
            }
        }

        _aiConversation = null;
        return new CoachResult
        {
            State = CoachSessionState.SelectingSource,
            Messages =
            [
                new CoachMessage { Text = "🤖 AI Coach is not configured. Go to Settings → AI COACH to enter your OpenCode Go API key.", Icon = "ℹ️" },
                .. GenerateSourceSelectionMessages()
            ]
        };
    }

    private CoachResult ConvertAiResult(AIAnalysisResult aiResult, SnapshotCsvData csvData, FfbProfile profile)
    {
        var recs = new List<FfbRecommendation>();
        var issues = new List<string>();
        var messages = new List<CoachMessage>();

        messages.Add(new CoachMessage
        {
            Text = aiResult.Summary,
            Icon = "🤖"
        });

        foreach (var issue in aiResult.Issues)
        {
            if (issue.Type == "fallback" || issue.Type == "balanced") continue;

            issues.Add(issue.Type);
            foreach (var rec in issue.Recommendations)
            {
                recs.Add(new FfbRecommendation
                {
                    Type = RecommendationType.ProfileChange,
                    Parameter = rec.Parameter,
                    Description = issue.Description,
                    Reason = rec.Reason,
                    CurrentValue = rec.CurrentValue,
                    SuggestedValue = rec.SuggestedValue,
                    Impact = rec.Impact
                });
            }

            messages.Add(new CoachMessage
            {
                Text = $"**{issue.Type.ToUpper()}** ({issue.Severity}): {issue.Description}",
                Icon = issue.Severity == "high" ? "🔴" : issue.Severity == "medium" ? "🟡" : "🟢"
            });
        }

        if (recs.Count == 0 && _aiConversation == null)
        {
            var healthy = BuildHealthyMessages(profile);
            return new CoachResult
            {
                State = CoachSessionState.Questioning,
                Messages = [.. messages, .. healthy.Messages],
                Recommendations = recs,
                CsvData = csvData
            };
        }

        if (recs.Count == 0)
        {
            messages.Add(new CoachMessage
            {
                Text = "Your profile looks well-balanced. What aspect of the feel would you like to talk about? I can help with overall strength, damping, vibrations, or anything else.",
                Answers =
                [
                    new CoachAnswer { Id = "talk", Label = "Let's talk feel", Description = "Start AI conversation" },
                    new CoachAnswer { Id = "finish", Label = "I'm done", Description = "See summary" }
                ]
            });
        }
        else
        {
            messages.Add(new CoachMessage
            {
                Text = "I found some things worth adjusting. Tell me what you're feeling on track — oversteer, understeer, wheel too heavy or too light, vibrations? Or I can just walk through my recommendations one by one.",
                Answers =
                [
                    new CoachAnswer { Id = "talk", Label = "Let's tune it", Description = "AI coaching conversation" },
                    new CoachAnswer { Id = "finish", Label = "Not now", Description = "See summary" }
                ]
            });
        }

        State = CoachSessionState.Questioning;
        return new CoachResult
        {
            State = CoachSessionState.Questioning,
            Messages = messages,
            Recommendations = recs,
            ActiveIssue = issues.Count > 0 ? issues[0] : "",
            CsvData = csvData
        };
    }

    private CoachResult BuildFirstIssueMessages(List<string> issues,
        List<FfbRecommendation> recs)
    {
        var messages = new List<CoachMessage>();
        var firstIssue = issues[0];
        var rec = recs[0];

        switch (firstIssue)
        {
            case "clipping":
                messages.Add(new CoachMessage
                {
                    Text = $"I noticed your FFB signal is clipping {rec.Description?.ToLower() ?? ""}. That means the force is hitting the maximum limit and getting chopped off. Does the wheel feel too strong or numb at the limit?",
                    Icon = "📊",
                    Recommendation = rec,
                    Answers =
                    [
                        new CoachAnswer { Id = "clipping_yes", Label = "Yes, too strong", Description = $"Suggested: OutputGain {rec.CurrentValue:F2} → {rec.SuggestedValue:F2}" },
                        new CoachAnswer { Id = "clipping_no", Label = "Feels fine", Description = "Skip this adjustment" }
                    ]
                });
                break;

            case "weak":
                messages.Add(new CoachMessage
                {
                    Text = $"The FFB signal looks weak ({rec.Description?.ToLower() ?? ""}). Do you feel like the wheel lacks strength?",
                    Icon = "💪",
                    Recommendation = rec,
                    Answers =
                    [
                        new CoachAnswer { Id = "weak_yes", Label = "Yes, too weak", Description = $"Suggested: OutputGain {rec.CurrentValue:F2} → {rec.SuggestedValue:F2}" },
                        new CoachAnswer { Id = "weak_no", Label = "Feels about right", Description = "Skip this adjustment" }
                    ]
                });
                break;

            case "peaks":
                messages.Add(new CoachMessage
                {
                    Text = $"Your force channels have moderate peak values. {rec.Description}. Would you like to smooth out these peak forces?",
                    Icon = "📈",
                    Recommendation = rec,
                    Answers =
                    [
                        new CoachAnswer { Id = "peaks_yes", Label = "Yes, smooth it out", Description = $"Suggested: CompressionPower {rec.CurrentValue:F1} → {rec.SuggestedValue:F1}" },
                        new CoachAnswer { Id = "peaks_no", Label = "Feels good as is", Description = "Skip" }
                    ]
                });
                break;

            default:
                return BuildHealthyMessages(profile: null);
        }

        return new CoachResult
        {
            State = CoachSessionState.Questioning,
            Messages = messages,
            Recommendations = recs,
            ActiveIssue = firstIssue,
            CsvData = _lastCsvData,
            LiveStats = _lastLiveStats
        };
    }

    private CoachResult BuildAdjustmentQuestion(FfbRecommendation? rec)
    {
        if (rec == null)
            return BuildSummaryMessages();

        return new CoachResult
        {
            State = CoachSessionState.Questioning,
            Messages =
            [
                new CoachMessage
                {
                    Text = $"I'd recommend reducing OutputGain from {rec.CurrentValue:F2} to {rec.SuggestedValue:F2}. This will lower the peak force and reduce clipping while keeping the overall feel.",
                    Recommendation = rec,
                    Answers =
                    [
                        new CoachAnswer { Id = "apply", Label = "Apply This Change" },
                        new CoachAnswer { Id = "skip", Label = "Skip, I'll adjust manually" }
                    ]
                }
            ],
            PendingRecommendation = rec
        };
    }

    private CoachResult BuildNextIssueQuestion(string skippedIssue)
    {
        var questions = new List<CoachAnswer>
        {
            new() { Id = "fine_tune_output_gain", Label = "Adjust Overall Strength", Description = "Output gain fine-tuning" },
            new() { Id = "fine_tune_damping", Label = "Adjust Damping Feel", Description = "Viscous/speed damping" },
            new() { Id = "fine_tune_vibration", Label = "Adjust Vibrations", Description = "Kerb/road/grip vibrations" },
            new() { Id = "fine_tune_none", Label = "I'm Done — Show Summary", Description = "View all changes" }
        };

        return new CoachResult
        {
            State = CoachSessionState.Questioning,
            Messages =
            [
                new CoachMessage
                {
                    Text = "Alright! Let's fine-tune other aspects. What would you like to adjust?",
                    Answers = questions
                }
            ]
        };
    }

    private CoachResult BuildFineTuneOutputGainQuestion()
    {
        var profile = _profileManager.ActiveProfile;
        float current = profile?.OutputGain ?? 0.62f;

        return new CoachResult
        {
            State = CoachSessionState.Questioning,
            Messages =
            [
                new CoachMessage
                {
                    Text = $"Current Output Gain is {current:F2}. Would you like more or less overall strength?",
                    Answers =
                    [
                        new CoachAnswer { Id = "apply_output_high", Label = "More Strength", Description = $"Increase to {MathF.Min(1.2f, current * 1.2f):F2}" },
                        new CoachAnswer { Id = "apply_output_low", Label = "Less Strength", Description = $"Decrease to {MathF.Max(0.2f, current * 0.8f):F2}" },
                        new CoachAnswer { Id = "skip", Label = "Keep Current", Description = "No change" }
                    ]
                }
            ]
        };
    }

    private CoachResult BuildFineTuneDampingQuestion()
    {
        return new CoachResult
        {
            State = CoachSessionState.Questioning,
            Messages =
            [
                new CoachMessage
                {
                    Text = "Let's tune the damping. What would you like to adjust?",
                    Answers =
                    [
                        new CoachAnswer { Id = "apply_damping_viscous", Label = "Viscous Damping", Description = "General smoothness" },
                        new CoachAnswer { Id = "apply_damping_speed", Label = "Speed Damping", Description = "High-speed stability" },
                        new CoachAnswer { Id = "apply_damping_friction", Label = "Friction", Description = "Static friction feel" },
                        new CoachAnswer { Id = "skip", Label = "Keep Current", Description = "No change" }
                    ]
                }
            ]
        };
    }

    private CoachResult BuildFineTuneVibrationQuestion()
    {
        return new CoachResult
        {
            State = CoachSessionState.Questioning,
            Messages =
            [
                new CoachMessage
                {
                    Text = "Let's adjust vibration levels. Which aspect would you like to change?",
                    Answers =
                    [
                        new CoachAnswer { Id = "apply_vib_kerb", Label = "Kerb Vibration", Description = "Curb/rumble strip feel" },
                        new CoachAnswer { Id = "apply_vib_road", Label = "Road Vibration", Description = "Road surface feel" },
                        new CoachAnswer { Id = "apply_vib_slip", Label = "Slip Vibration", Description = "Tire slip feedback" },
                        new CoachAnswer { Id = "skip", Label = "Keep Current", Description = "No change" }
                    ]
                }
            ]
        };
    }

    private CoachResult BuildHealthyMessages(FfbProfile? profile)
    {
        var messages = new List<CoachMessage>
        {
            new()
            {
                Text = "Your FFB profile looks healthy! The signal levels are in a good range with no major issues detected.",
                Icon = "✅"
            },
            new()
            {
                Text = "Want to fine-tune anything for personal preference?",
                Answers =
                [
                    new CoachAnswer { Id = "fine_tune_output_gain", Label = "Adjust Overall Strength", Description = "Output gain fine-tuning" },
                    new CoachAnswer { Id = "fine_tune_damping", Label = "Adjust Damping Feel", Description = "Viscous/speed/friction damping" },
                    new CoachAnswer { Id = "fine_tune_vibration", Label = "Adjust Vibrations", Description = "Kerb/road/slip vibration gains" },
                    new CoachAnswer { Id = "fine_tune_none", Label = "I'm Good, Thanks!", Description = "Finish session" }
                ]
            }
        };

        // If profile has recent tuning activity, mention it
        if (profile != null && HasRecentActivity(profile))
        {
            messages.Insert(1, new CoachMessage
            {
                Text = $"I noticed you've been tweaking the {GetRecentChanges(profile)} settings. Let's make sure those adjustments are working well for you.",
                Icon = "🔧"
            });
        }

        return new CoachResult
        {
            State = CoachSessionState.Questioning,
            Messages = messages
        };
    }

    private CoachResult BuildSummaryMessages()
    {
        State = CoachSessionState.Summary;
        var count = _undoStack.Count;

        return new CoachResult
        {
            State = CoachSessionState.Summary,
            Messages =
            [
                new CoachMessage
                {
                    Text = "Here's a summary of our tuning session:",
                    Icon = "📋"
                },
                new CoachMessage
                {
                    Text = count > 0
                        ? $"✅ {count} change{(count > 1 ? "s" : "")} applied to your profile.\n\nTip: Take a new telemetry snapshot to verify the changes feel right on track. You can also undo any change from this session."
                        : "No changes were applied this session. Feel free to come back anytime for tuning advice!",
                    Answers =
                    [
                        new CoachAnswer { Id = "restart", Label = "Start Over", Description = "New coaching session" },
                        new CoachAnswer { Id = "done", Label = "Done", Description = "Return to tuning" }
                    ]
                }
            ]
        };
    }

    private FfbProfile? FindMatchingProfile(string profileName)
    {
        if (_profileManager.ActiveProfile?.Name == profileName)
            return _profileManager.ActiveProfile;

        return _profileManager.Profiles.FirstOrDefault(p => p.Name == profileName)
               ?? _profileManager.ActiveProfile;
    }

    private static readonly Dictionary<string, string> ParamNormalize = new()
    {
        { "outputgain", "OutputGain" },
        { "compressionpower", "CompressionPower" },
        { "forcescale", "ForceScale" },
        { "softclipthreshold", "SoftClipThreshold" },
        { "signcorrectionenabled", "SignCorrectionEnabled" },
        { "coreforcemultiplier", "CoreForceMultiplier" },
        { "maxslewrate", "MaxSlewRate" },
        { "hysteresisthreshold", "HysteresisThreshold" },
        { "centersuppressiondegrees", "CenterSuppressionDegrees" },
        { "centersharpnessdegrees", "CenterSharpnessDegrees" },
        { "centerkneepower", "CenterKneePower" },
        { "centerblenddegrees", "CenterBlendDegrees" },
        { "noisefloor", "NoiseFloor" },
        { "hysteresiswatchdogframes", "HysteresisWatchdogFrames" },
        { "lowspeedsmoothkmh", "LowSpeedSmoothKmh" },
        { "mzfrontgain", "MzFrontGain" },
        { "fxfrontgain", "FxFrontGain" },
        { "fyfrontgain", "FyFrontGain" },
        { "mzreargain", "MzRearGain" },
        { "wheelloadweighting", "WheelLoadWeighting" },
        { "mzscale", "MzScale" },
        { "fxscale", "FxScale" },
        { "fyscale", "FyScale" },
        { "viscousdamping", "ViscousDamping" },
        { "speeddamping", "SpeedDamping" },
        { "friction", "Friction" },
        { "inertia", "Inertia" },
        { "maxspeedreference", "MaxSpeedReference" },
        { "slipratiogain", "SlipRatioGain" },
        { "slipanglegain", "SlipAngleGain" },
        { "slipangleshapegain", "SlipAngleShapeGain" },
        { "slipthreshold", "SlipThreshold" },
        { "usefrontonly", "UseFrontOnly" },
        { "brakeboostgain", "BrakeBoostGain" },
        { "brakeboostthreshold", "BrakeBoostThreshold" },
        { "gearquickspike", "GearSpikeThreshold" },
        { "gearquickspikethreshold", "GearSpikeThreshold" },
        { "gearspikethreshold", "GearSpikeThreshold" },
        { "lateralggain", "LateralGGain" },
        { "longitudinalggain", "LongitudinalGGain" },
        { "suspensiongain", "SuspensionGain" },
        { "yawrategain", "YawRateGain" },
        { "mastergain", "MasterGain" },
        { "kerbgain", "KerbGain" },
        { "roadgain", "RoadGain" },
        { "slipgain", "SlipGain" },
        { "absgain", "AbsGain" },
        { "abspulseamplitude", "AbsPulseAmplitude" },
        { "suspensionroadgain", "SuspensionRoadGain" },
        { "scrubgain", "ScrubGain" },
        { "rearslipgain", "RearSlipGain" },
        { "offtrackgain", "OfftrackGain" },
        { "offtrackseverityscale", "OfftrackSeverityScale" },
        { "curbseverityscale", "CurbSeverityScale" },
        { "scrubforcescale", "ScrubForceScale" },
        { "rearslipforcescale", "RearSlipForceScale" },
        { "flexgain", "FlexGain" },
        { "carcassstiffness", "CarcassStiffness" },
        { "flexsmoothing", "FlexSmoothing" },
        { "contactpatchweight", "ContactPatchWeight" },
        { "loadflexgain", "LoadFlexGain" },
        { "gripguardenabled", "GripGuardEnabled" },
        { "gripguardpeakslipangle", "GripGuardPeakSlipAngle" },
        { "gripguardattenuationstrength", "GripGuardAttenuationStrength" },
        { "gripguardmechanicaltrailgain", "GripGuardMechanicalTrailGain" },
        { "crashenabled", "CrashEnabled" },
        { "crashimpactgain", "CrashImpactGain" },
        { "crashsafetyclamp", "CrashSafetyClamp" },
        { "crashtriggerthresholdg", "CrashTriggerThresholdG" },
        { "tyreconditionenabled", "TyreConditionEnabled" },
        { "blowoutvibrationgain", "BlowoutVibrationGain" },
        { "pressurelossgain", "PressureLossGain" },
        { "damageasymmetrygain", "DamageAsymmetryGain" },
        { "wetweather_enabled", "WetWeatherEnabled" },
        { "wetroadvibsuppression", "WetRoadVibSuppression" },
        { "wetpeakslipanglemultiplier", "WetPeakSlipAngleMultiplier" },
        { "wetdampingreduction", "WetDampingReduction" },
        { "staticfrictiongain", "StaticFrictionGain" },
        { "staticfrictionmaxelasticstretch", "StaticFrictionMaxElasticStretch" },
        { "staticfrictionspringstiffness", "StaticFrictionSpringStiffness" },
        { "staticfrictionkineticfrictionbase", "StaticFrictionKineticFrictionBase" },
        { "staticfrictionengineoffdamping", "StaticFrictionEngineOffDamping" },
        { "staticfrictionengineondamping", "StaticFrictionEngineOnDamping" },
        { "staticfrictionengineoffscale", "StaticFrictionEngineOffScale" },
        { "staticfrictionengineonscale", "StaticFrictionEngineOnScale" },
        { "staticfrictionactivedecay", "StaticFrictionActiveDecay" },
        { "staticfrictionreturndecay", "StaticFrictionReturnDecay" },
        { "staticfrictionoutputsmoothalpha", "StaticFrictionOutputSmoothAlpha" },
    };

    private static string NormalizeParam(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return name ?? "";
        var key = new string(name.Where(c => !char.IsWhiteSpace(c)).ToArray()).ToLowerInvariant();
        return ParamNormalize.TryGetValue(key, out var canonical) ? canonical : name;
    }

    private static float ReadProfileValue(FfbProfile profile, string parameter)
    {
        parameter = NormalizeParam(parameter);
        return parameter switch
        {
            "OutputGain" => profile.OutputGain,
            "CompressionPower" => profile.CompressionPower,
            "ForceScale" => profile.ForceScale,
            "SoftClipThreshold" => profile.SoftClipThreshold,
            "SignCorrectionEnabled" => profile.SignCorrectionEnabled ? 1 : 0,
            "CoreForceMultiplier" => profile.Advanced.CoreForceMultiplier,
            "MaxSlewRate" => profile.Advanced.MaxSlewRate,
            "HysteresisThreshold" => profile.Advanced.HysteresisThreshold,
            "CenterSuppressionDegrees" => profile.Advanced.CenterSuppressionDegrees,
            "CenterSharpnessDegrees" => profile.Advanced.CenterSharpnessDegrees,
            "CenterKneePower" => profile.Advanced.CenterKneePower,
            "CenterBlendDegrees" => profile.Advanced.CenterBlendDegrees,
            "NoiseFloor" => profile.Advanced.NoiseFloor,
            "HysteresisWatchdogFrames" => profile.Advanced.HysteresisWatchdogFrames,
            "LowSpeedSmoothKmh" => profile.Advanced.LowSpeedSmoothKmh,
            "MzFrontGain" => profile.MzFront.Gain,
            "FxFrontGain" => profile.FxFront.Gain,
            "FyFrontGain" => profile.FyFront.Gain,
            "MzRearGain" => profile.MzRear.Gain,
            "WheelLoadWeighting" => profile.WheelLoadWeighting,
            "MzScale" => profile.MzScale,
            "FxScale" => profile.FxScale,
            "FyScale" => profile.FyScale,
            "ViscousDamping" => profile.Damping.ViscousDamping,
            "SpeedDamping" => profile.Damping.SpeedDamping,
            "Friction" => profile.Damping.Friction,
            "Inertia" => profile.Damping.Inertia,
            "MaxSpeedReference" => profile.Damping.MaxSpeedReference,
            "SlipRatioGain" => profile.Slip.SlipRatioGain,
            "SlipAngleGain" => profile.Slip.SlipAngleGain,
            "SlipAngleShapeGain" => profile.Slip.SlipAngleShapeGain,
            "SlipThreshold" => profile.Slip.SlipThreshold,
            "UseFrontOnly" => profile.Slip.UseFrontOnly ? 1 : 0,
            "BrakeBoostGain" => profile.Slip.BrakeBoostGain,
            "BrakeBoostThreshold" => profile.Slip.BrakeBoostThreshold,
            "GearSpikeThreshold" => profile.Slip.GearSpikeThreshold,
            "LateralGGain" => profile.Dynamic.LateralGGain,
            "LongitudinalGGain" => profile.Dynamic.LongitudinalGGain,
            "SuspensionGain" => profile.Dynamic.SuspensionGain,
            "YawRateGain" => profile.Dynamic.YawRateGain,
            "MasterGain" => profile.Vibrations.MasterGain,
            "KerbGain" => profile.Vibrations.KerbGain,
            "RoadGain" => profile.Vibrations.RoadGain,
            "SlipGain" => profile.Vibrations.SlipGain,
            "AbsGain" => profile.Vibrations.AbsGain,
            "AbsPulseAmplitude" => profile.Vibrations.AbsPulseAmplitude,
            "SuspensionRoadGain" => profile.Vibrations.SuspensionRoadGain,
            "ScrubGain" => profile.Vibrations.ScrubGain,
            "RearSlipGain" => profile.Vibrations.RearSlipGain,
            "OfftrackGain" => profile.Vibrations.OfftrackGain,
            "OfftrackSeverityScale" => profile.Vibrations.OfftrackSeverityScale,
            "CurbSeverityScale" => profile.Vibrations.CurbSeverityScale,
            "ScrubForceScale" => profile.Vibrations.ScrubForceScale,
            "RearSlipForceScale" => profile.Vibrations.RearSlipForceScale,
            "FlexGain" => profile.TyreFlex.FlexGain,
            "CarcassStiffness" => profile.TyreFlex.CarcassStiffness,
            "FlexSmoothing" => profile.TyreFlex.FlexSmoothing,
            "ContactPatchWeight" => profile.TyreFlex.ContactPatchWeight,
            "LoadFlexGain" => profile.TyreFlex.LoadFlexGain,
            "GripGuardEnabled" => profile.GripGuard.Enabled ? 1 : 0,
            "GripGuardPeakSlipAngle" => profile.GripGuard.PeakSlipAngle,
            "GripGuardAttenuationStrength" => profile.GripGuard.AttenuationStrength,
            "GripGuardMechanicalTrailGain" => profile.GripGuard.MechanicalTrailGain,
            "CrashEnabled" => profile.Crash.Enabled ? 1 : 0,
            "CrashImpactGain" => profile.Crash.ImpactGain,
            "CrashSafetyClamp" => profile.Crash.SafetyClamp,
            "CrashTriggerThresholdG" => profile.Crash.TriggerThresholdG,
            "TyreConditionEnabled" => profile.TyreCondition.Enabled ? 1 : 0,
            "BlowoutVibrationGain" => profile.TyreCondition.BlowoutVibrationGain,
            "PressureLossGain" => profile.TyreCondition.PressureLossGain,
            "DamageAsymmetryGain" => profile.TyreCondition.DamageAsymmetryGain,
            "WetWeatherEnabled" => profile.WetWeather.Enabled ? 1 : 0,
            "WetRoadVibSuppression" => profile.WetWeather.RoadVibSuppression,
            "WetPeakSlipAngleMultiplier" => profile.WetWeather.PeakSlipAngleMultiplier,
            "WetDampingReduction" => profile.WetWeather.DampingReduction,
            "StaticFrictionGain" => profile.StaticFriction.Gain,
            "StaticFrictionMaxElasticStretch" => profile.StaticFriction.MaxElasticStretch,
            "StaticFrictionSpringStiffness" => profile.StaticFriction.SpringStiffness,
            "StaticFrictionKineticFrictionBase" => profile.StaticFriction.KineticFrictionBase,
            "StaticFrictionEngineOffDamping" => profile.StaticFriction.EngineOffDamping,
            "StaticFrictionEngineOnDamping" => profile.StaticFriction.EngineOnDamping,
            "StaticFrictionEngineOffScale" => profile.StaticFriction.EngineOffScale,
            "StaticFrictionEngineOnScale" => profile.StaticFriction.EngineOnScale,
            "StaticFrictionActiveDecay" => profile.StaticFriction.ActiveDecay,
            "StaticFrictionReturnDecay" => profile.StaticFriction.ReturnDecay,
            "StaticFrictionOutputSmoothAlpha" => profile.StaticFriction.OutputSmoothAlpha,
            _ => 0f
        };
    }

    private static void WriteProfileValue(FfbProfile profile, string parameter, float value)
    {
        parameter = NormalizeParam(parameter);
        switch (parameter)
        {
            case "OutputGain": profile.OutputGain = value; break;
            case "CompressionPower": profile.CompressionPower = value; break;
            case "ForceScale": profile.ForceScale = value; break;
            case "SoftClipThreshold": profile.SoftClipThreshold = value; break;
            case "SignCorrectionEnabled": profile.SignCorrectionEnabled = value > 0.5f; break;
            case "CoreForceMultiplier": profile.Advanced.CoreForceMultiplier = value; break;
            case "MaxSlewRate": profile.Advanced.MaxSlewRate = value; break;
            case "HysteresisThreshold": profile.Advanced.HysteresisThreshold = value; break;
            case "CenterSuppressionDegrees": profile.Advanced.CenterSuppressionDegrees = value; break;
            case "CenterSharpnessDegrees": profile.Advanced.CenterSharpnessDegrees = value; break;
            case "CenterKneePower": profile.Advanced.CenterKneePower = value; break;
            case "CenterBlendDegrees": profile.Advanced.CenterBlendDegrees = value; break;
            case "NoiseFloor": profile.Advanced.NoiseFloor = value; break;
            case "HysteresisWatchdogFrames": profile.Advanced.HysteresisWatchdogFrames = (int)value; break;
            case "LowSpeedSmoothKmh": profile.Advanced.LowSpeedSmoothKmh = value; break;
            case "MzFrontGain": profile.MzFront.Gain = value; break;
            case "FxFrontGain": profile.FxFront.Gain = value; break;
            case "FyFrontGain": profile.FyFront.Gain = value; break;
            case "MzRearGain": profile.MzRear.Gain = value; break;
            case "WheelLoadWeighting": profile.WheelLoadWeighting = value; break;
            case "MzScale": profile.MzScale = value; break;
            case "FxScale": profile.FxScale = value; break;
            case "FyScale": profile.FyScale = value; break;
            case "ViscousDamping": profile.Damping.ViscousDamping = value; break;
            case "SpeedDamping": profile.Damping.SpeedDamping = value; break;
            case "Friction": profile.Damping.Friction = value; break;
            case "Inertia": profile.Damping.Inertia = value; break;
            case "MaxSpeedReference": profile.Damping.MaxSpeedReference = value; break;
            case "SlipRatioGain": profile.Slip.SlipRatioGain = value; break;
            case "SlipAngleGain": profile.Slip.SlipAngleGain = value; break;
            case "SlipAngleShapeGain": profile.Slip.SlipAngleShapeGain = value; break;
            case "SlipThreshold": profile.Slip.SlipThreshold = value; break;
            case "UseFrontOnly": profile.Slip.UseFrontOnly = value > 0.5f; break;
            case "BrakeBoostGain": profile.Slip.BrakeBoostGain = value; break;
            case "BrakeBoostThreshold": profile.Slip.BrakeBoostThreshold = value; break;
            case "GearSpikeThreshold": profile.Slip.GearSpikeThreshold = (int)value; break;
            case "LateralGGain": profile.Dynamic.LateralGGain = value; break;
            case "LongitudinalGGain": profile.Dynamic.LongitudinalGGain = value; break;
            case "SuspensionGain": profile.Dynamic.SuspensionGain = value; break;
            case "YawRateGain": profile.Dynamic.YawRateGain = value; break;
            case "MasterGain": profile.Vibrations.MasterGain = value; break;
            case "KerbGain": profile.Vibrations.KerbGain = value; break;
            case "RoadGain": profile.Vibrations.RoadGain = value; break;
            case "SlipGain": profile.Vibrations.SlipGain = value; break;
            case "AbsGain": profile.Vibrations.AbsGain = value; break;
            case "AbsPulseAmplitude": profile.Vibrations.AbsPulseAmplitude = value; break;
            case "SuspensionRoadGain": profile.Vibrations.SuspensionRoadGain = value; break;
            case "ScrubGain": profile.Vibrations.ScrubGain = value; break;
            case "RearSlipGain": profile.Vibrations.RearSlipGain = value; break;
            case "OfftrackGain": profile.Vibrations.OfftrackGain = value; break;
            case "OfftrackSeverityScale": profile.Vibrations.OfftrackSeverityScale = value; break;
            case "CurbSeverityScale": profile.Vibrations.CurbSeverityScale = value; break;
            case "ScrubForceScale": profile.Vibrations.ScrubForceScale = value; break;
            case "RearSlipForceScale": profile.Vibrations.RearSlipForceScale = value; break;
            case "FlexGain": profile.TyreFlex.FlexGain = value; break;
            case "CarcassStiffness": profile.TyreFlex.CarcassStiffness = value; break;
            case "FlexSmoothing": profile.TyreFlex.FlexSmoothing = value; break;
            case "ContactPatchWeight": profile.TyreFlex.ContactPatchWeight = value; break;
            case "LoadFlexGain": profile.TyreFlex.LoadFlexGain = value; break;
            case "GripGuardEnabled": profile.GripGuard.Enabled = value > 0.5f; break;
            case "GripGuardPeakSlipAngle": profile.GripGuard.PeakSlipAngle = value; break;
            case "GripGuardAttenuationStrength": profile.GripGuard.AttenuationStrength = value; break;
            case "GripGuardMechanicalTrailGain": profile.GripGuard.MechanicalTrailGain = value; break;
            case "CrashEnabled": profile.Crash.Enabled = value > 0.5f; break;
            case "CrashImpactGain": profile.Crash.ImpactGain = value; break;
            case "CrashSafetyClamp": profile.Crash.SafetyClamp = value; break;
            case "CrashTriggerThresholdG": profile.Crash.TriggerThresholdG = value; break;
            case "TyreConditionEnabled": profile.TyreCondition.Enabled = value > 0.5f; break;
            case "BlowoutVibrationGain": profile.TyreCondition.BlowoutVibrationGain = value; break;
            case "PressureLossGain": profile.TyreCondition.PressureLossGain = value; break;
            case "DamageAsymmetryGain": profile.TyreCondition.DamageAsymmetryGain = value; break;
            case "WetWeatherEnabled": profile.WetWeather.Enabled = value > 0.5f; break;
            case "WetRoadVibSuppression": profile.WetWeather.RoadVibSuppression = value; break;
            case "WetPeakSlipAngleMultiplier": profile.WetWeather.PeakSlipAngleMultiplier = value; break;
            case "WetDampingReduction": profile.WetWeather.DampingReduction = value; break;
            case "StaticFrictionGain": profile.StaticFriction.Gain = value; break;
            case "StaticFrictionMaxElasticStretch": profile.StaticFriction.MaxElasticStretch = value; break;
            case "StaticFrictionSpringStiffness": profile.StaticFriction.SpringStiffness = value; break;
            case "StaticFrictionKineticFrictionBase": profile.StaticFriction.KineticFrictionBase = value; break;
            case "StaticFrictionEngineOffDamping": profile.StaticFriction.EngineOffDamping = value; break;
            case "StaticFrictionEngineOnDamping": profile.StaticFriction.EngineOnDamping = value; break;
            case "StaticFrictionEngineOffScale": profile.StaticFriction.EngineOffScale = value; break;
            case "StaticFrictionEngineOnScale": profile.StaticFriction.EngineOnScale = value; break;
            case "StaticFrictionActiveDecay": profile.StaticFriction.ActiveDecay = value; break;
            case "StaticFrictionReturnDecay": profile.StaticFriction.ReturnDecay = value; break;
            case "StaticFrictionOutputSmoothAlpha": profile.StaticFriction.OutputSmoothAlpha = value; break;
        }
    }

    private static bool HasRecentActivity(FfbProfile profile)
    {
        var baseline = FfbProfile.GetDefaultProfile("Default");
        return Math.Abs(profile.OutputGain - baseline.OutputGain) > 0.05f
            || Math.Abs(profile.CompressionPower - baseline.CompressionPower) > 0.2f
            || Math.Abs(profile.Advanced.MaxSlewRate - baseline.Advanced.MaxSlewRate) > 0.02f;
    }

    private static string GetRecentChanges(FfbProfile profile)
    {
        var changed = new List<string>();
        var baseline = FfbProfile.GetDefaultProfile("Default");
        if (Math.Abs(profile.OutputGain - baseline.OutputGain) > 0.05f)
            changed.Add("OutputGain");
        if (Math.Abs(profile.CompressionPower - baseline.CompressionPower) > 0.2f)
            changed.Add("CompressionPower");
        if (Math.Abs(profile.Advanced.MaxSlewRate - baseline.Advanced.MaxSlewRate) > 0.02f)
            changed.Add("MaxSlewRate");
        if (Math.Abs(profile.Damping.ViscousDamping - baseline.Damping.ViscousDamping) > 0.03f)
            changed.Add("ViscousDamping");
        if (Math.Abs(profile.Vibrations.SuspensionRoadGain - baseline.Vibrations.SuspensionRoadGain) > 0.3f)
            changed.Add("SuspensionRoadGain");

        return changed.Count > 0 ? string.Join(", ", changed) : "FFB";
    }
}

public sealed class CoachResult
{
    public CoachSessionState State { get; init; }
    public List<CoachMessage> Messages { get; init; } = [];
    public List<FfbRecommendation> Recommendations { get; init; } = [];
    public FfbRecommendation? PendingRecommendation { get; init; }
    public string ActiveIssue { get; init; } = "";
    public SnapshotCsvData? CsvData { get; init; }
    public LiveProfilerStats? LiveStats { get; init; }
}
