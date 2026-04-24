using AcEvoFfbTuner.Core.Profiles;

namespace AcEvoFfbTuner.Core.TrackMapping;

public enum RecommendationType
{
    ProfileChange,
    CodeIssue,
    Info
}

public sealed class FfbRecommendation
{
    public RecommendationType Type { get; init; }
    public string Parameter { get; init; } = "";
    public string Description { get; init; } = "";
    public float CurrentValue { get; init; }
    public float SuggestedValue { get; init; }
    public string Unit { get; init; } = "";
    public string Reason { get; init; } = "";
    public string? AffectedPipelineStage { get; init; }

    public string DisplayText
    {
        get
        {
            string prefix = Type switch
            {
                RecommendationType.ProfileChange => "PROFILE",
                RecommendationType.CodeIssue => "CODE",
                _ => "INFO"
            };

            if (Type == RecommendationType.CodeIssue || Type == RecommendationType.Info)
                return $"[{prefix}] {Description} — {Reason}";

            string dir = SuggestedValue > CurrentValue ? "↑" : "↓";
            return $"[{prefix}] {Parameter} {dir} {CurrentValue:F2} → {SuggestedValue:F2}{Unit} — {Reason}";
        }
    }
}

public sealed class ProfileRecommender
{
    public static List<FfbRecommendation> Generate(
        DiagnosticLapSummary summary,
        FfbProfile profile)
    {
        var recs = new List<FfbRecommendation>();
        if (summary.TotalEvents == 0) return recs;

        if (summary.ClipEventsInCorners > 3 || summary.ClipEventsOnStraights > 1)
        {
            float clipRatio = summary.TotalEvents > 0
                ? (float)summary.TotalClippingEvents / summary.TotalEvents
                : 0f;

            if (clipRatio > 0.15f)
            {
                if (profile.OutputGain > 0.5f)
                {
                    float suggested = MathF.Max(0.4f, profile.OutputGain * 0.8f);
                    recs.Add(new FfbRecommendation
                    {
                        Type = RecommendationType.ProfileChange,
                        Parameter = "OutputGain",
                        Description = "Reduce output gain to prevent clipping",
                        CurrentValue = profile.OutputGain,
                        SuggestedValue = suggested,
                        Unit = "",
                        Reason = $"{summary.TotalClippingEvents} clipping events ({clipRatio:P0} of all events). Reducing gain gives more headroom.",
                        AffectedPipelineStage = "Output Clipper"
                    });
                }

                if (profile.SoftClipThreshold < 0.9f)
                {
                    recs.Add(new FfbRecommendation
                    {
                        Type = RecommendationType.ProfileChange,
                        Parameter = "SoftClipThreshold",
                        Description = "Raise soft clip threshold",
                        CurrentValue = profile.SoftClipThreshold,
                        SuggestedValue = MathF.Min(0.95f, profile.SoftClipThreshold + 0.05f),
                        Unit = "",
                        Reason = "Clipping is happening before the soft clip threshold. Raising it lets more force through before soft limiting.",
                        AffectedPipelineStage = "Output Clipper"
                    });
                }
            }
        }

        if (summary.SuspiciousSnapsOnStraight > 2)
        {
            float suggestedSlew = MathF.Max(0.05f, profile.Advanced.MaxSlewRate * 0.6f);
            recs.Add(new FfbRecommendation
            {
                Type = RecommendationType.ProfileChange,
                Parameter = "MaxSlewRate",
                Description = "Reduce slew rate to smooth suspicious snaps on straights",
                CurrentValue = profile.Advanced.MaxSlewRate,
                SuggestedValue = suggestedSlew,
                Unit = "/tick",
                Reason = $"{summary.SuspiciousSnapsOnStraight} suspicious snaps on straights. Lower slew rate limits how fast force can change per tick.",
                AffectedPipelineStage = "Slew Rate Limiter"
            });

            if (profile.Advanced.HysteresisThreshold < 0.02f)
            {
                recs.Add(new FfbRecommendation
                {
                    Type = RecommendationType.ProfileChange,
                    Parameter = "HysteresisThreshold",
                    Description = "Increase hysteresis to prevent jitter",
                    CurrentValue = profile.Advanced.HysteresisThreshold,
                    SuggestedValue = profile.Advanced.HysteresisThreshold + 0.01f,
                    Unit = "",
                    Reason = "Snaps on straights may be from micro-oscillations. Higher hysteresis holds the output steady for small changes.",
                    AffectedPipelineStage = "Hysteresis"
                });
            }
        }

        if (summary.SuspiciousOscillationsOnStraight > 1)
        {
            recs.Add(new FfbRecommendation
            {
                Type = RecommendationType.CodeIssue,
                Description = "Oscillation clusters detected on straights",
                Reason = $"{summary.SuspiciousOscillationsOnStraight} oscillation clusters on straights (no steering input). This is likely a feedback loop in the pipeline — check EMA smoothing alphas, slew rate near-center logic, or hysteresis watchdog.",
                AffectedPipelineStage = "Channel Mixer EMA / Slew Rate / Hysteresis"
            });

            if (profile.Advanced.CenterSuppressionDegrees > 3f)
            {
                float suggested = profile.Advanced.CenterSuppressionDegrees * 0.7f;
                recs.Add(new FfbRecommendation
                {
                    Type = RecommendationType.ProfileChange,
                    Parameter = "CenterSuppressionDegrees",
                    Description = "Reduce center suppression zone to prevent oscillation near center",
                    CurrentValue = profile.Advanced.CenterSuppressionDegrees,
                    SuggestedValue = suggested,
                    Unit = "deg",
                    Reason = "Wide center suppression with force still leaking through can cause oscillation. Try narrowing the zone.",
                    AffectedPipelineStage = "Center Suppression"
                });
            }
        }

        if (summary.SuspiciousForceAnomalies > 0)
        {
            recs.Add(new FfbRecommendation
            {
                Type = RecommendationType.CodeIssue,
                Description = "Force direction anomaly — wheel pushes same direction you're turning",
                Reason = $"{summary.SuspiciousForceAnomalies} events where force pushes with steering instead of against it. Check sign correction logic and center blend behavior.",
                AffectedPipelineStage = "Sign Correction / Center Blend"
            });
        }

        if (summary.TotalSnapEvents > 10 && summary.CornerEventPct > 70f && summary.SuspiciousPct < 15f)
        {
            if (summary.SnapCauseMz > summary.SnapCauseFx + summary.SnapCauseFy)
            {
                recs.Add(new FfbRecommendation
                {
                    Type = RecommendationType.ProfileChange,
                    Parameter = "MzFrontGain",
                    Description = "Reduce Mz gain to smooth corner forces",
                    CurrentValue = profile.MzFront.Gain,
                    SuggestedValue = profile.MzFront.Gain * 0.85f,
                    Unit = "",
                    Reason = $"Most snaps ({summary.SnapCauseMz}) caused by Mz channel. Reducing gain smooths self-aligning torque response.",
                    AffectedPipelineStage = "Channel Mixer (Mz)"
                });
            }

            if (summary.SnapCauseFy > summary.SnapCauseMz)
            {
                recs.Add(new FfbRecommendation
                {
                    Type = RecommendationType.ProfileChange,
                    Parameter = "FyFrontGain",
                    Description = "Reduce Fy gain to smooth lateral force transients",
                    CurrentValue = profile.FyFront.Gain,
                    SuggestedValue = profile.FyFront.Gain * 0.85f,
                    Unit = "",
                    Reason = $"Most snaps ({summary.SnapCauseFy}) caused by Fy channel. Lateral forces are spiking during corner transitions.",
                    AffectedPipelineStage = "Channel Mixer (Fy)"
                });
            }

            if (profile.CompressionPower < 1.8f)
            {
                recs.Add(new FfbRecommendation
                {
                    Type = RecommendationType.ProfileChange,
                    Parameter = "CompressionPower",
                    Description = "Increase compression to tame peak forces",
                    CurrentValue = profile.CompressionPower,
                    SuggestedValue = MathF.Min(3.0f, profile.CompressionPower + 0.3f),
                    Unit = "",
                    Reason = "High snap count in corners with expected dynamics. More compression rounds off force peaks naturally.",
                    AffectedPipelineStage = "Tanh Compression"
                });
            }
        }

        if (summary.AvgSuspiciousSpeed > 150f && summary.SuspiciousSnapsOnStraight > 0)
        {
            recs.Add(new FfbRecommendation
            {
                Type = RecommendationType.ProfileChange,
                Parameter = "MaxSlewRate",
                Description = "Reduce high-speed slew rate further",
                CurrentValue = profile.Advanced.MaxSlewRate,
                SuggestedValue = MathF.Max(0.04f, profile.Advanced.MaxSlewRate * 0.5f),
                Unit = "/tick",
                Reason = $"Suspicious snaps at avg {summary.AvgSuspiciousSpeed:F0} km/h. High-speed slew needs tighter limiting.",
                AffectedPipelineStage = "Slew Rate Limiter (speed-dependent)"
            });
        }

        if (summary.CornerEventPct > 60f && summary.TotalOscillations > 3)
        {
            if (profile.Advanced.CenterBlendDegrees < 2f)
            {
                recs.Add(new FfbRecommendation
                {
                    Type = RecommendationType.ProfileChange,
                    Parameter = "CenterBlendDegrees",
                    Description = "Widen center blend zone to smooth corner transitions",
                    CurrentValue = profile.Advanced.CenterBlendDegrees,
                    SuggestedValue = profile.Advanced.CenterBlendDegrees + 0.5f,
                    Unit = "deg",
                    Reason = "Oscillations in corners may be from Fy/Mz channel interaction at center. Wider blend zone smooths the crossover.",
                    AffectedPipelineStage = "Channel Mixer (Center Blend)"
                });
            }
        }

        if (summary.TotalEvents > 0 && summary.SuspiciousPct < 10f && summary.CornerEventPct > 80f)
        {
            recs.Add(new FfbRecommendation
            {
                Type = RecommendationType.Info,
                Description = "FFB looks healthy for this driving style",
                Reason = $"Events are {summary.CornerEventPct:F0}% in corners with only {summary.SuspiciousPct:F0}% suspicious. The force behavior matches normal driving dynamics. Focus on feel preference tuning, not bug fixes."
            });
        }

        return recs;
    }
}
