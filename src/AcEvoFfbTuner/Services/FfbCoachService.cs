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

    public CoachSessionState State { get; private set; } = CoachSessionState.Idle;
    public string CurrentProfileName { get; private set; } = "";
    public string DataSourceLabel { get; private set; } = "";

    public FfbCoachService(ProfileManager profileManager, FfbPipeline pipeline)
    {
        _profileManager = profileManager;
        _pipeline = pipeline;
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

        if (files.Count > 0)
        {
            answers.Add(new CoachAnswer
            {
                Id = "source_latest",
                Label = $"Latest Snapshot ({files[0].Timestamp:MMM dd, HH:mm})",
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

        answers.Add(new CoachAnswer
        {
            Id = "source_live",
            Label = "Use Live Profiler Data",
            Description = "Current telemetry session"
        });

        return
        [
            new CoachMessage
            {
                Text = "Welcome to FFB Coach! I can analyze your telemetry data and help you fine-tune your FFB profile. Let's start by choosing a data source.",
                Icon = "🤖",
                Answers = answers
            }
        ];
    }

    public CoachResult AnalyzeSnapshot(SnapshotCsvData csvData)
    {
        State = CoachSessionState.Analyzing;
        DataSourceLabel = $"Snapshot: {csvData.ProfileName} ({csvData.RowCount} rows)";
        CurrentProfileName = csvData.ProfileName;
        _lastCsvData = csvData;
        _lastLiveStats = null;

        var profile = FindMatchingProfile(csvData.ProfileName);
        _lastResult = AnalyzeData(csvData, profile);
        return _lastResult;
    }

    public CoachResult AnalyzeLiveData(LiveProfilerStats stats, string profileName)
    {
        State = CoachSessionState.Analyzing;
        DataSourceLabel = $"Live Data ({stats.FrameCount} frames)";
        CurrentProfileName = profileName;
        _lastLiveStats = stats;
        _lastCsvData = null;

        var profile = FindMatchingProfile(profileName);
        _lastResult = AnalyzeLiveStats(stats, profile);
        return _lastResult;
    }

    public CoachResult ProcessAnswer(string answerId)
    {
        var rec = _lastResult?.PendingRecommendation;
        _lastResult = answerId switch
        {
            "clipping_yes" or "weak_yes" or "peaks_yes" => BuildAdjustmentQuestion(rec),
            "clipping_no" or "weak_no" or "peaks_no" => BuildNextIssueQuestion("clipping"),
            "fine_tune_output_gain" => BuildFineTuneOutputGainQuestion(),
            "fine_tune_damping" => BuildFineTuneDampingQuestion(),
            "fine_tune_vibration" => BuildFineTuneVibrationQuestion(),
            "fine_tune_none" => BuildSummaryMessages(),
            "continue" => BuildNextIssueQuestion(""),
            "finish" or "done" => BuildSummaryMessages(),
            "restart" or "source_latest" or "source_live" or "source_pick" or "go_back" => GenerateSourceSelectionResult(),
            "apply" => HandleApplyAnswer(),
            "skip" => BuildNextIssueQuestion(""),
            "apply_output_high" => ApplyOutputGainChange(1.2f),
            "apply_output_low" => ApplyOutputGainChange(0.8f),
            "apply_damping_viscous" => ApplyDampingChange("ViscousDamping"),
            "apply_damping_speed" => ApplyDampingChange("SpeedDamping"),
            "apply_damping_friction" => ApplyDampingChange("Friction"),
            "apply_vib_kerb" => ApplyVibrationChange("KerbGain"),
            "apply_vib_road" => ApplyVibrationChange("RoadGain"),
            "apply_vib_slip" => ApplyVibrationChange("SlipGain"),
            _ => BuildNextIssueQuestion("")
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
        _undoStack.Push(() => WriteProfileValue(profile, rec.Parameter, oldValue));
        State = CoachSessionState.Applying;

        return new CoachResult
        {
            State = CoachSessionState.Questioning,
            Messages =
            [
                new CoachMessage
                {
                    Text = $"Applied: {rec.Parameter} changed to {rec.SuggestedValue:F3}",
                    IsUser = false,
                    Icon = "✓"
                },
                new CoachMessage
                {
                    Text = "Give it a test drive and let me know how it feels! Want to continue with other adjustments or finish up?",
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
            profile?.ApplyToPipeline(_pipeline);
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
    }

    private CoachResult AnalyzeData(SnapshotCsvData csvData, FfbProfile? profile)
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

    private CoachResult AnalyzeLiveStats(LiveProfilerStats stats, FfbProfile? profile)
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
        return AnalyzeData(csvData, profile);
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

    private static float ReadProfileValue(FfbProfile profile, string parameter)
    {
        return parameter switch
        {
            "OutputGain" => profile.OutputGain,
            "CompressionPower" => profile.CompressionPower,
            "SoftClipThreshold" => profile.SoftClipThreshold,
            "MaxSlewRate" => profile.Advanced.MaxSlewRate,
            "HysteresisThreshold" => profile.Advanced.HysteresisThreshold,
            "CenterSuppressionDegrees" => profile.Advanced.CenterSuppressionDegrees,
            "SuspensionRoadGain" => profile.Vibrations.SuspensionRoadGain,
            "KerbGain" => profile.Vibrations.KerbGain,
            "RoadGain" => profile.Vibrations.RoadGain,
            "SlipGain" => profile.Vibrations.SlipGain,
            "ViscousDamping" => profile.Damping.ViscousDamping,
            "SpeedDamping" => profile.Damping.SpeedDamping,
            "Friction" => profile.Damping.Friction,
            "MasterGain" => profile.Vibrations.MasterGain,
            "MzFrontGain" => profile.MzFront.Gain,
            "FxFrontGain" => profile.FxFront.Gain,
            "FyFrontGain" => profile.FyFront.Gain,
            _ => 0f
        };
    }

    private static void WriteProfileValue(FfbProfile profile, string parameter, float value)
    {
        switch (parameter)
        {
            case "OutputGain": profile.OutputGain = value; break;
            case "CompressionPower": profile.CompressionPower = value; break;
            case "SoftClipThreshold": profile.SoftClipThreshold = value; break;
            case "MaxSlewRate": profile.Advanced.MaxSlewRate = value; break;
            case "HysteresisThreshold": profile.Advanced.HysteresisThreshold = value; break;
            case "CenterSuppressionDegrees": profile.Advanced.CenterSuppressionDegrees = value; break;
            case "SuspensionRoadGain": profile.Vibrations.SuspensionRoadGain = value; break;
            case "KerbGain": profile.Vibrations.KerbGain = value; break;
            case "RoadGain": profile.Vibrations.RoadGain = value; break;
            case "SlipGain": profile.Vibrations.SlipGain = value; break;
            case "ViscousDamping": profile.Damping.ViscousDamping = value; break;
            case "SpeedDamping": profile.Damping.SpeedDamping = value; break;
            case "Friction": profile.Damping.Friction = value; break;
            case "MasterGain": profile.Vibrations.MasterGain = value; break;
            case "MzFrontGain": profile.MzFront.Gain = value; break;
            case "FxFrontGain": profile.FxFront.Gain = value; break;
            case "FyFrontGain": profile.FyFront.Gain = value; break;
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
