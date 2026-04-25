using System.Linq;
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
    public string? CodeReference { get; init; }
    public string? DevDetail { get; init; }
    public string? DataBreakdown { get; init; }
    public string? Impact { get; init; }

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

    public string DevModeText
    {
        get
        {
            string prefix = Type switch
            {
                RecommendationType.ProfileChange => "PROFILE",
                RecommendationType.CodeIssue => "CODE",
                _ => "INFO"
            };

            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"╔══ [{prefix}] {Description}");

            if (Type == RecommendationType.ProfileChange)
            {
                string dir = SuggestedValue > CurrentValue ? "↑" : "↓";
                sb.AppendLine($"║ Parameter:   {Parameter}");
                sb.AppendLine($"║ Current:     {CurrentValue:F4}");
                sb.AppendLine($"║ Suggested:   {SuggestedValue:F4} ({dir})");
                if (!string.IsNullOrEmpty(Unit))
                    sb.AppendLine($"║ Unit:        {Unit}");
                float pctChange = Math.Abs(CurrentValue) > 0.0001f
                    ? (SuggestedValue - CurrentValue) / Math.Abs(CurrentValue) * 100f
                    : 0f;
                string changeText = MathF.Abs(pctChange) < 0.1f
                    ? "(no change — already at floor)"
                    : $"{pctChange:+0.0;-0.0}%";
                sb.AppendLine($"║ Change:      {changeText}");
            }

            sb.AppendLine($"║");
            sb.AppendLine($"║ REASON: {Reason}");

            if (!string.IsNullOrEmpty(DataBreakdown))
            {
                sb.AppendLine($"║");
                sb.AppendLine($"║ DATA:");
                foreach (var line in DataBreakdown.Split('\n'))
                    if (!string.IsNullOrWhiteSpace(line))
                        sb.AppendLine($"║   {line.TrimEnd()}");
            }

            if (!string.IsNullOrEmpty(DevDetail))
            {
                sb.AppendLine($"║");
                sb.AppendLine($"║ DETAIL:");
                foreach (var line in DevDetail.Split('\n'))
                    if (!string.IsNullOrWhiteSpace(line))
                        sb.AppendLine($"║   {line.TrimEnd()}");
            }

            if (!string.IsNullOrEmpty(AffectedPipelineStage))
            {
                sb.AppendLine($"║");
                sb.AppendLine($"║ Stage:       {AffectedPipelineStage}");
            }

            if (!string.IsNullOrEmpty(CodeReference))
            {
                sb.AppendLine($"║ Code:        {CodeReference}");
            }

            if (!string.IsNullOrEmpty(Impact))
            {
                sb.AppendLine($"║");
                sb.AppendLine($"║ IMPACT: {Impact}");
            }

            sb.Append($"╚═══════════════════════════════════════");
            return sb.ToString();
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

        GenerateClippingRecommendations(recs, summary, profile);
        GenerateSuspiciousSnapRecommendations(recs, summary, profile);
        GenerateOscillationRecommendations(recs, summary, profile);
        GenerateForceAnomalyRecommendations(recs, summary, profile);
        GenerateChannelTuningRecommendations(recs, summary, profile);
        GenerateHighSpeedRecommendations(recs, summary, profile);
        GenerateCenterBlendRecommendations(recs, summary, profile);
        GenerateHealthyInfo(recs, summary, profile);

        return recs;
    }

    private static void GenerateClippingRecommendations(
        List<FfbRecommendation> recs, DiagnosticLapSummary s, FfbProfile p)
    {
        if (s.ClipEventsInCorners <= 3 && s.ClipEventsOnStraights <= 1) return;

        float clipRatio = s.TotalEvents > 0
            ? (float)s.TotalClippingEvents / s.TotalEvents
            : 0f;

        if (clipRatio <= 0.15f) return;

        if (p.OutputGain > 0.5f)
        {
            float suggested = MathF.Max(0.4f, p.OutputGain * 0.8f);
            recs.Add(new FfbRecommendation
            {
                Type = RecommendationType.ProfileChange,
                Parameter = "OutputGain",
                Description = "Reduce output gain to prevent clipping",
                CurrentValue = p.OutputGain,
                SuggestedValue = suggested,
                Unit = "",
                Reason = $"{s.TotalClippingEvents} clipping events ({clipRatio:P0} of all events). Reducing gain gives more headroom.",
                AffectedPipelineStage = "Output Clipper → FfbOutputClipper",
                CodeReference = "FfbPipeline.cs:78 — float output = OutputClipper.Process(postDynamic * OutputGain, out bool isClipping)",
                DataBreakdown =
                    $"Clip in corners:    {s.ClipEventsInCorners}\n" +
                    $"Clip on straights:  {s.ClipEventsOnStraights}\n" +
                    $"Clip ratio:         {clipRatio:P1} of {s.TotalEvents} total events\n" +
                    $"Current OutputGain: {p.OutputGain:F3}\n" +
                    $"Current SoftClip:   {p.SoftClipThreshold:F3}\n" +
                    $"Effective ceiling:  {p.OutputGain * p.SoftClipThreshold:F3}",
                DevDetail =
                    $"The OutputClipper.Process() applies SoftClipThreshold first, then OutputGain scales the result.\n" +
                    $"When OutputGain={p.OutputGain:F2} and SoftClip={p.SoftClipThreshold:F2}, the effective max output is {p.OutputGain * p.SoftClipThreshold:F3}.\n" +
                    $"Reducing OutputGain to {suggested:F2} would give effective max {suggested * p.SoftClipThreshold:F3} — a {(p.OutputGain - suggested) / p.OutputGain * 100f:F0}% reduction.\n" +
                    $"The pipeline applies gain AFTER compression/tanh, so lowering it reduces peak forces without affecting the curve shape.",
                Impact = $"Peak force drops ~{(p.OutputGain - suggested) / p.OutputGain * 100f:F0}%. You may feel less overall strength but lose the clipping artifacts."
            });
        }

        if (p.SoftClipThreshold < 0.9f)
        {
            float suggested = MathF.Min(0.95f, p.SoftClipThreshold + 0.05f);
            recs.Add(new FfbRecommendation
            {
                Type = RecommendationType.ProfileChange,
                Parameter = "SoftClipThreshold",
                Description = "Raise soft clip threshold",
                CurrentValue = p.SoftClipThreshold,
                SuggestedValue = suggested,
                Unit = "",
                Reason = "Clipping is happening before the soft clip threshold. Raising it lets more force through before soft limiting.",
                AffectedPipelineStage = "Output Clipper → SoftClipThreshold",
                CodeReference = "FfbOutputClipper.cs — applies tanh-based soft limiting before OutputGain",
                DataBreakdown =
                    $"Current SoftClipThreshold: {p.SoftClipThreshold:F3}\n" +
                    $"Suggested:                 {suggested:F3}\n" +
                    $"Clip events:               {s.TotalClippingEvents} ({s.ClipEventsInCorners} corners, {s.ClipEventsOnStraights} straights)",
                DevDetail =
                    $"SoftClipThreshold controls the tanh knee in the output clipper.\n" +
                    $"Below threshold: force passes through linearly.\n" +
                    $"Above threshold: tanh progressively limits — the further above, the harder the clip.\n" +
                    $"Raising from {p.SoftClipThreshold:F2} to {suggested:F2} gives {((suggested - p.SoftClipThreshold) / (1f - p.SoftClipThreshold) * 100f):F0}% more headroom before soft limiting begins.",
                Impact = $"More force passes through at peaks. May feel stronger but riskier for wheel safety at high OutputGain."
            });
        }
    }

    private static void GenerateSuspiciousSnapRecommendations(
        List<FfbRecommendation> recs, DiagnosticLapSummary s, FfbProfile p)
    {
        if (s.SuspiciousSnapsOnStraight <= 2) return;

        if (s.SuspiciousSnapCauseRoadVibration > 0 && p.Vibrations.SuspensionRoadGain > 0.5f)
        {
            float suggestedGain = MathF.Max(0.5f, p.Vibrations.SuspensionRoadGain * 0.7f);
            recs.Add(new FfbRecommendation
            {
                Type = RecommendationType.ProfileChange,
                Parameter = "SuspensionRoadGain",
                Description = "Reduce road vibration gain to prevent suspicious snaps on straights",
                CurrentValue = p.Vibrations.SuspensionRoadGain,
                SuggestedValue = suggestedGain,
                Unit = "",
                Reason = $"{s.SuspiciousSnapCauseRoadVibration} of {s.SuspiciousSnapsOnStraight} suspicious snaps on straights caused by road vibration. Reduce gain to smooth output.",
                AffectedPipelineStage = "Vibration Mixer → RoadForceModulation",
                CodeReference = "FfbVibrationMixer.cs — SuspensionRoadGain scales suspension-travel-derived force modulation",
                DataBreakdown =
                    $"Suspicious snaps on straights:    {s.SuspiciousSnapsOnStraight}\n" +
                    $"Road-vibration caused:            {s.SuspiciousSnapCauseRoadVibration}\n" +
                    $"Other causes: Mz={s.SuspiciousSnapCauseMz} Fx={s.SuspiciousSnapCauseFx} Fy={s.SuspiciousSnapCauseFy} Slew={s.SuspiciousSnapCauseSlew}\n" +
                    $"Current SuspensionRoadGain:       {p.Vibrations.SuspensionRoadGain:F2}",
                DevDetail =
                    "Suspension-derived road vibration bypasses the slew rate limiter.\n" +
                    "When the gain is too high, curb impacts and road bumps cause force deltas\n" +
                    "that exceed the snap detection threshold on straights.\n\n" +
                    "This is a PROFILE tuning issue — reduce the gain to balance feel vs. artifacts.",
                Impact = $"Road vibration intensity reduced by {(1f - suggestedGain / p.Vibrations.SuspensionRoadGain) * 100f:F0}%. Less snap artifacts but reduced curb/road feel."
            });
        }

        float suggestedSlew = MathF.Max(0.02f, p.Advanced.MaxSlewRate * 0.6f);
        if (suggestedSlew >= p.Advanced.MaxSlewRate) return;
        float slewReductionPct = (p.Advanced.MaxSlewRate - suggestedSlew) / p.Advanced.MaxSlewRate * 100f;

        recs.Add(new FfbRecommendation
        {
            Type = RecommendationType.ProfileChange,
            Parameter = "MaxSlewRate",
            Description = "Reduce slew rate to smooth suspicious snaps on straights",
            CurrentValue = p.Advanced.MaxSlewRate,
            SuggestedValue = suggestedSlew,
            Unit = "/tick",
            Reason = $"{s.SuspiciousSnapsOnStraight} suspicious snaps on straights. Lower slew rate limits how fast force can change per tick.",
            AffectedPipelineStage = "Slew Rate Limiter → FfbPipeline.Process()",
            CodeReference = "FfbPipeline.cs:188-195 — slewDelta clamped to effectiveSlewRate, with speed-dependent scaling",
            DataBreakdown =
                $"Suspicious snaps on straights: {s.SuspiciousSnapsOnStraight}\n" +
                $"Avg suspicious speed:          {s.AvgSuspiciousSpeed:F0} km/h\n" +
                $"Snap cause breakdown:          Mz={s.SnapCauseMz} Fx={s.SnapCauseFx} Fy={s.SnapCauseFy} Slew={s.SnapCauseSlew}\n" +
                $"Current MaxSlewRate:           {p.Advanced.MaxSlewRate:F4}/tick\n" +
                $"Current HysteresisThreshold:   {p.Advanced.HysteresisThreshold:F4}",
            DevDetail =
                $"The slew rate limiter operates in TWO stages:\n" +
                $"  1. Primary slew (line 188): clamps delta to effectiveSlewRate * speedSlewScale\n" +
                $"     - speedSlewScale = lowSpeedSlewScale * highSpeedSlewScale\n" +
                $"     - lowSpeedSlewScale: ramps from 0.05 at 0km/h to 1.0 at 15km/h\n" +
                $"     - highSpeedSlewScale: stays 1.0 until 200km/h, then ramps down to 0.4 at 450km/h\n" +
                $"  2. Near-center high-speed reduction (line 177-183):\n" +
                $"     - At >150km/h with <0.03 steer angle, effectiveSlewRate *= (1 - 0.6 * nearCenterScale)\n" +
                $"     - This can reduce slew rate by up to 60% at high speed near center\n" +
                $"  3. Safety slew (line 198-200): final clamp to raw MaxSlewRate (no speed scaling)\n\n" +
                $"Reducing MaxSlewRate from {p.Advanced.MaxSlewRate:F4} to {suggestedSlew:F4} ({slewReductionPct:F0}% reduction)\n" +
                $"would limit force changes to ±{suggestedSlew:F4}/tick at full speed scale.\n" +
                $"At {s.AvgSuspiciousSpeed:F0}km/h the effective rate is already scaled by " +
                $"~{Math.Clamp(s.AvgSuspiciousSpeed / 15f, 0.05f, 1f) * (s.AvgSuspiciousSpeed > 200f ? Math.Max(1f - (s.AvgSuspiciousSpeed - 200f) / 250f, 0.4f) : 1f):F2}x = " +
                $"{suggestedSlew * Math.Clamp(s.AvgSuspiciousSpeed / 15f, 0.05f, 1f) * (s.AvgSuspiciousSpeed > 200f ? Math.Max(1f - (s.AvgSuspiciousSpeed - 200f) / 250f, 0.4f) : 1f):F4}/tick effective.",
            Impact = $"Slew rate reduced by {slewReductionPct:F0}%. Force transitions become smoother but slower. May reduce detail in quick direction changes."
        });

        if (p.Advanced.HysteresisThreshold < 0.02f)
        {
            float suggestedHyst = p.Advanced.HysteresisThreshold + 0.01f;
            recs.Add(new FfbRecommendation
            {
                Type = RecommendationType.ProfileChange,
                Parameter = "HysteresisThreshold",
                Description = "Increase hysteresis to prevent jitter",
                CurrentValue = p.Advanced.HysteresisThreshold,
                SuggestedValue = suggestedHyst,
                Unit = "",
                Reason = "Snaps on straights may be from micro-oscillations. Higher hysteresis holds the output steady for small changes.",
                AffectedPipelineStage = "Hysteresis → FfbPipeline.Process()",
                CodeReference = "FfbPipeline.cs:127-152 — hysteresis holds output when |delta| < effectiveHystThreshold for HysteresisWatchdogFrames ticks",
                DataBreakdown =
                    $"Current HysteresisThreshold:    {p.Advanced.HysteresisThreshold:F4}\n" +
                    $"Current HysteresisWatchdogFrames: {p.Advanced.HysteresisWatchdogFrames}\n" +
                    $"Speed-scaled threshold at 100km/h: {p.Advanced.HysteresisThreshold:F4} (no scaling above 15km/h)\n" +
                    $"At low speed (5km/h):             {p.Advanced.HysteresisThreshold * (1f + (1f - 5f / 15f) * 19f):F4} (19x boost)",
                DevDetail =
                    $"Hysteresis works in 3 modes:\n" +
                    $"  1. Rising from zero: if prev output < noiseFloor*2 and new output >= noiseFloor, snap immediately (no hyst)\n" +
                    $"  2. Small delta: if |output - hystOutput| < threshold, hold old value. After WatchdogFrames ticks, snap to new value.\n" +
                    $"  3. Large delta: if |output - hystOutput| >= threshold, pass through immediately.\n\n" +
                    $"The threshold is speed-scaled below 15km/h: threshold * (1 + (1 - speed/15) * 19)\n" +
                    $"At 5km/h: {suggestedHyst:F4} * 6.33 = {suggestedHyst * (1f + (1f - 5f / 15f) * 19f):F4}\n" +
                    $"At 100km/h: {suggestedHyst:F4} (no scaling)",
                Impact = $"Hysteresis deadband increases from {p.Advanced.HysteresisThreshold:F4} to {suggestedHyst:F4}. Small force fluctuations will be suppressed, but micro-detail may feel slightly muted."
            });
        }
    }

    private static void GenerateOscillationRecommendations(
        List<FfbRecommendation> recs, DiagnosticLapSummary s, FfbProfile p)
    {
        if (s.SuspiciousOscillationsOnStraight <= 1) return;

        if (s.SuspiciousRoadVibrationSnaps > 0 && p.Vibrations.SuspensionRoadGain > 0.5f)
        {
            float suggested = MathF.Max(0f, p.Vibrations.SuspensionRoadGain * 0.6f);
            recs.Add(new FfbRecommendation
            {
                Type = RecommendationType.ProfileChange,
                Parameter = "SuspensionRoadGain",
                Description = "Reduce road vibration gain to smooth straight-line oscillations",
                CurrentValue = p.Vibrations.SuspensionRoadGain,
                SuggestedValue = suggested,
                Unit = "",
                Reason = $"{s.SuspiciousOscillationsOnStraight} oscillation clusters on straights, {s.TotalRoadVibrationSnaps} road-vibration-induced events. The suspension-derived road feel may be too aggressive.",
                AffectedPipelineStage = "Vibration Mixer → RoadForceModulation",
                CodeReference = "FfbVibrationMixer.cs — derives vibration from suspension travel deltas, injected into constant force after slew limiter",
                DataBreakdown =
                    $"Suspicious oscillations on straights: {s.SuspiciousOscillationsOnStraight}\n" +
                    $"Road-vibration snaps:               {s.TotalRoadVibrationSnaps}\n" +
                    $"Suspicious road-vibration snaps:    {s.SuspiciousRoadVibrationSnaps}\n" +
                    $"Current SuspensionRoadGain:         {p.Vibrations.SuspensionRoadGain:F2}",
                DevDetail =
                    "Road vibration is derived from per-frame suspension travel deltas.\n" +
                    "It bypasses the slew rate limiter and injects directly into the constant force output.\n" +
                    "When SuspensionRoadGain is too high, even normal road surface variation causes\n" +
                    "force oscillations that the event detector flags.\n\n" +
                    "This is a PROFILE tuning issue, not a code bug. Reduce the gain to smooth the output.",
                Impact = $"Road vibration intensity reduced by {(1f - suggested / Math.Max(p.Vibrations.SuspensionRoadGain, 0.01f)) * 100f:F0}%. Less vibration on smooth road, but also less curb feel."
            });
            return;
        }

        recs.Add(new FfbRecommendation
        {
            Type = RecommendationType.CodeIssue,
            Description = "Oscillation clusters detected on straights",
            Reason = $"{s.SuspiciousOscillationsOnStraight} oscillation clusters on straights (no steering input). Not caused by road vibration. Check EMA smoothing alphas, slew rate near-center logic, or hysteresis watchdog.",
            AffectedPipelineStage = "Channel Mixer EMA / Slew Rate / Hysteresis",
            CodeReference = "FfbChannelMixer.cs:154-156 (parallel EMAs) + FfbPipeline.cs:177-183 (near-center slew reduction)",
            DataBreakdown =
                $"Suspicious oscillations on straights: {s.SuspiciousOscillationsOnStraight}\n" +
                $"Total oscillation events:             {s.TotalOscillations}\n" +
                $"Oscillation in corners:               {s.TotalOscillations - s.SuspiciousOscillationsOnStraight}\n" +
                $"Road-vibration snaps (ruled out):     {s.TotalRoadVibrationSnaps}\n" +
                $"EMA alphas: Mz={0.20f:F2} Fx={0.08f:F2} Fy={0.12f:F2}\n" +
                $"Parallel slow alphas: Mz={0.05f:F2} Fy={0.04f:F2}\n" +
                $"HighSpeedBlend start: {150f} km/h\n" +
                $"CenterBlendDegrees: {p.Advanced.CenterBlendDegrees:F1}\n" +
                $"CenterSuppressionDegrees: {p.Advanced.CenterSuppressionDegrees:F1}",
            DevDetail =
                $"Oscillation detection: ≥3 sign flips in last 10 force deltas, force > 0.01 significance gate, 0.5s cooldown.\n\n" +
                $"Road vibration was checked but NOT the cause (RoadVibrationSnaps={s.TotalRoadVibrationSnaps}).\n\n" +
                $"Possible causes:\n" +
                $"  1. EMA feedback loop: fast alpha (0.20) + slow alpha (0.05) blend at high speed can cause overshoot\n" +
                $"     The parallel EMA is blended as: out = fast + (slow - fast) * highSpeedBlend\n" +
                $"     At 250+km/h, highSpeedBlend=0.85, so output = 0.15*fast + 0.85*slow.\n\n" +
                $"  2. Near-center slew reduction: at >150km/h, |steer|<0.03, slew rate is reduced by up to 60%.\n\n" +
                $"  3. Hysteresis watchdog: if the watchdog releases after 5 ticks, the sudden\n" +
                $"     jump can trigger a snap event that becomes part of an oscillation cluster.\n\n" +
                $"CHECK: FfbEventDetector.cs — oscillation detection uses sign flips of force DELTAS in ring buffer.",
            Impact = "Oscillations on straights with no road vibration cause. This is a code-level issue, not a profile tuning problem."
        });

        if (p.Advanced.CenterSuppressionDegrees > 3f)
        {
            float suggested = p.Advanced.CenterSuppressionDegrees * 0.7f;
            recs.Add(new FfbRecommendation
            {
                Type = RecommendationType.ProfileChange,
                Parameter = "CenterSuppressionDegrees",
                Description = "Reduce center suppression zone to prevent oscillation near center",
                CurrentValue = p.Advanced.CenterSuppressionDegrees,
                SuggestedValue = suggested,
                Unit = "deg",
                Reason = "Wide center suppression with force still leaking through can cause oscillation. Try narrowing the zone.",
                AffectedPipelineStage = "Sign Correction → Center Fade",
                CodeReference = "FfbPipeline.cs:88-97 — speedSuppScale widens zone at higher speed, centerFade = (deg/totalZone)²",
                DataBreakdown =
                    $"Current CenterSuppressionDegrees: {p.Advanced.CenterSuppressionDegrees:F1}°\n" +
                    $"Speed-scaled at 200km/h:          {p.Advanced.CenterSuppressionDegrees * (1f + Math.Clamp((200f - 10f) / 200f, 0f, 0.5f)):F1}°\n" +
                    $"Total zone at 200km/h (incl osc dead):  {Math.Min(Math.Clamp((200f - 30f) / 40f, 0.5f, 3f) + Math.Max(p.Advanced.CenterSuppressionDegrees * (1f + Math.Clamp((200f - 10f) / 200f, 0f, 0.5f)), 5f), 12f):F1}°\n" +
                    $"Center fade at 3° steering:        {Math.Clamp(3f / Math.Max(Math.Min(Math.Clamp((200f - 30f) / 40f, 0.5f, 3f) + Math.Max(p.Advanced.CenterSuppressionDegrees * (1f + Math.Clamp((200f - 10f) / 200f, 0f, 0.5f)), 5f), 12f), 0.1f), 0f, 1f) * Math.Clamp(3f / Math.Max(Math.Min(Math.Clamp((200f - 30f) / 40f, 0.5f, 3f) + Math.Max(p.Advanced.CenterSuppressionDegrees * (1f + Math.Clamp((200f - 10f) / 200f, 0f, 0.5f)), 5f), 12f), 0.1f), 0f, 1f):F3}",
                DevDetail =
                    $"The center suppression zone works as a fade:\n" +
                    $"  - Within zone: centerFade = (steerDeg / totalZone)² — quadratic fade from 0 to 1\n" +
                    $"  - output = |output| * forceDirection * centerFade\n" +
                    $"  - forceDirection = -sign(steerAngle) when |steerAngle| > SteerDirDeadzone ({0.004f:F3})\n\n" +
                    $"At high speed the zone widens: base * (1 + (speed-10)/200), capped at 1.5x\n" +
                    $"Plus oscillation dead zone: at >30km/h, adds 0.5-3° extra dead zone\n" +
                    $"Total zone capped at 12°\n\n" +
                    $"A wide zone means the sign correction fades in very gradually. If force is oscillating\n" +
                    $"just inside the zone boundary, the quadratic fade can amplify it as the force rapidly\n" +
                    $"alternates between suppressed and not-suppressed.",
                Impact = $"Zone reduced from {p.Advanced.CenterSuppressionDegrees:F1}° to {suggested:F1}°. Force will be more present near center. May increase road feel but also noise."
            });
        }
    }

    private static void GenerateForceAnomalyRecommendations(
        List<FfbRecommendation> recs, DiagnosticLapSummary s, FfbProfile p)
    {
        if (s.SuspiciousForceAnomalies <= 0) return;

        recs.Add(new FfbRecommendation
        {
            Type = RecommendationType.CodeIssue,
            Description = "Force direction anomaly — wheel pushes same direction you're turning",
            Reason = $"{s.SuspiciousForceAnomalies} events where force pushes with steering instead of against it. Check sign correction logic and center blend behavior.",
            AffectedPipelineStage = "Sign Correction / Center Blend",
            CodeReference = "FfbPipeline.cs:99-103 — forceDirection = -sign(steerAngle), output = |output| * forceDirection * centerFade",
            DataBreakdown =
                $"Force direction anomalies:  {s.SuspiciousForceAnomalies}\n" +
                $"Detection threshold:        force WITH steering AND |force| > 0.3 AND steer stable >10 ticks",
            DevDetail =
                $"The sign correction (FfbPipeline.cs:80-105) forces output to oppose steering:\n" +
                $"  forceDirection = -sign(steerAngle) when |steerAngle| > 0.004\n" +
                $"  output = |output| * forceDirection * centerFade\n\n" +
                $"This runs BEFORE hysteresis, slew rate limiting, and vibration mixing.\n" +
                $"Those post-correction stages can cause the final output to temporarily align with steering:\n\n" +
                $"  1. Hysteresis (line 127-152): holds previous value when delta < threshold.\n" +
                $"     During steering transitions, the held value has the OLD sign while steering has changed.\n\n" +
                $"  2. Slew rate (line 188-195): limits how fast output can change direction.\n" +
                $"     At high speed near center, slew is reduced by up to 60%, extending the transition window.\n\n" +
                $"  3. Vibration mixing (line 207-214): adds ABS/slip vibration WITH the output sign.\n" +
                $"     If output is near zero (transitioning through center), vibration defaults to +1 sign.\n\n" +
                $"Detection gate: requires steering sign stable for ≥10 ticks to filter transition false positives.\n" +
                $"If anomalies still appear with stable steering, the sign correction itself may be wrong.\n" +
                $"CHECK: Is raw.SteerAngle correctly mapped? Check wheel driver software center calibration.",
            Impact = "If genuine, the wheel fights the driver by pushing in the steering direction instead of providing self-aligning torque. Check ffb_debug.log for sign of output vs steer at anomaly timestamps."
        });
    }

    private static void GenerateChannelTuningRecommendations(
        List<FfbRecommendation> recs, DiagnosticLapSummary s, FfbProfile p)
    {
        if (s.TotalSnapEvents <= 10 || s.CornerEventPct <= 70f || s.SuspiciousPct >= 15f) return;

        if (s.ExpectedSnapCount > s.TotalSnapEvents * 0.5)
            return;

        int suspMz = s.SuspiciousSnapCauseMz;
        int suspFx = s.SuspiciousSnapCauseFx;
        int suspFy = s.SuspiciousSnapCauseFy;
        int suspSlew = s.SuspiciousSnapCauseSlew;
        int totalSuspSnaps = suspMz + suspFx + suspFy + suspSlew;

        if (totalSuspSnaps < 3) return;

        if (suspMz > suspFx + suspFy)
        {
            float suggested = p.MzFront.Gain * 0.85f;
            recs.Add(new FfbRecommendation
            {
                Type = RecommendationType.ProfileChange,
                Parameter = "MzFrontGain",
                Description = "Reduce Mz gain to smooth corner forces",
                CurrentValue = p.MzFront.Gain,
                SuggestedValue = suggested,
                Unit = "",
                Reason = $"Most SUSPICIOUS snaps ({suspMz} of {totalSuspSnaps}) caused by Mz channel. Only suspicious snaps counted — expected corner dynamics excluded.",
                AffectedPipelineStage = "Channel Mixer → MzFront EMA",
                CodeReference = "FfbChannelMixer.cs:118 — mzFront = Normalize(rawMzFront, MzScale) * MzFrontGain",
                DataBreakdown =
                    $"ALL snap causes (for context):\n" +
                    $"  Mz:   {s.SnapCauseMz}  Fx: {s.SnapCauseFx}  Fy: {s.SnapCauseFy}  Slew: {s.SnapCauseSlew}\n" +
                    $"  Expected dynamics: {s.ExpectedSnapCount} of {s.TotalSnapEvents} total snaps\n\n" +
                    $"SUSPICIOUS snap causes (driving recommendation):\n" +
                    $"  Mz:   {suspMz}  Fx: {suspFx}  Fy: {suspFy}  Slew: {suspSlew}\n" +
                    $"  Total suspicious snaps: {totalSuspSnaps}\n\n" +
                    $"Current MzFrontGain: {p.MzFront.Gain:F3}\n" +
                    $"Current MzScale:     {p.MzScale:F0}",
                DevDetail =
                    $"Mz (self-aligning torque) is the primary FFB channel. It comes from the tire's pneumatic trail.\n" +
                    $"The raw Mz from AC Evo is averaged across FL+FR, multiplied by loadFactor, then normalized by MzScale.\n\n" +
                    $"Pipeline path for Mz:\n" +
                    $"  1. rawMzFront = (Mz[0]+Mz[1]) * 0.5 * loadFactor  (line 98)\n" +
                    $"  2. SpikeClamp(rawMzFront)  (line 111)\n" +
                    $"  3. Normalize(clamped, MzScale={p.MzScale:F0}) * MzFrontGain  (line 118)\n" +
                    $"  4. Fast EMA: alpha={0.20f} (line 154)\n" +
                    $"  5. Slow EMA: alpha={0.05f} (line 155)\n" +
                    $"  6. Speed-blended output (line 156)\n\n" +
                    $"Reducing MzFrontGain from {p.MzFront.Gain:F3} to {suggested:F3} scales all Mz contribution by {(suggested / p.MzFront.Gain):F2}x.\n" +
                    $"At 200km/h with highSpeedBlend≈0.42, effective smoothing = fast*0.58 + slow*0.42.\n" +
                    $"The EMA lag at this blend creates ~{(int)(0.58f / 0.20f + 0.42f / 0.05f)} tick equivalent smoothing delay.",
                Impact = $"Mz force reduced by {(1f - suggested / p.MzFront.Gain) * 100f:F0}%. Steering feel becomes smoother but less detailed. Self-aligning torque will feel lighter."
            });
        }

        if (suspFy > suspMz)
        {
            float suggested = p.FyFront.Gain * 0.85f;
            recs.Add(new FfbRecommendation
            {
                Type = RecommendationType.ProfileChange,
                Parameter = "FyFrontGain",
                Description = "Reduce Fy gain to smooth lateral force transients",
                CurrentValue = p.FyFront.Gain,
                SuggestedValue = suggested,
                Unit = "",
                Reason = $"Most SUSPICIOUS snaps ({suspFy} of {totalSuspSnaps}) caused by Fy channel. Only suspicious snaps counted — expected corner dynamics excluded.",
                AffectedPipelineStage = "Channel Mixer → FyFront EMA + Center Blend",
                CodeReference = "FfbChannelMixer.cs:105 — rawFyFront = -(Fy[0]+Fy[1])*0.5 (NEGATED to add to Mz)",
                DataBreakdown =
                    $"ALL snap causes (for context):\n" +
                    $"  Mz:   {s.SnapCauseMz}  Fx: {s.SnapCauseFx}  Fy: {s.SnapCauseFy}  Slew: {s.SnapCauseSlew}\n" +
                    $"  Expected dynamics: {s.ExpectedSnapCount} of {s.TotalSnapEvents} total snaps\n\n" +
                    $"SUSPICIOUS snap causes (driving recommendation):\n" +
                    $"  Mz:   {suspMz}  Fx: {suspFx}  Fy: {suspFy}  Slew: {suspSlew}\n\n" +
                    $"Current FyFrontGain:      {p.FyFront.Gain:F3}\n" +
                    $"Current FyScale:          {p.FyScale:F0}\n" +
                    $"Current CenterBlendDeg:   {p.Advanced.CenterBlendDegrees:F1}°",
                DevDetail =
                    $"Fy (lateral force) is NEGATED before mixing: rawFyFront = -(Fy[0]+Fy[1])*0.5\n" +
                    $"This is because raw Fy opposes Mz (mechanical trail vs pneumatic trail).\n" +
                    $"Negating makes Fy ADD to Mz, representing total steering torque.\n\n" +
                    $"Fy also goes through center blend zone (SmoothStep):\n" +
                    $"  At 0°: Fy weight = 0 (pure Mz)\n" +
                    $"  At CenterBlendDegrees+: Fy weight = 1 (full Fy)\n" +
                    $"  Blend: SmoothStep(t) = t²(3-2t) where t = steerDeg/CenterBlendDegrees\n\n" +
                    $"Fy is noisier than Mz at low steering angles because it's the differential of large values.\n" +
                    $"If CenterBlendDegrees={p.Advanced.CenterBlendDegrees:F1}° is too low, Fy contributes too early and brings noise.",
                Impact = $"Fy force reduced by {(1f - suggested / p.FyFront.Gain) * 100f:F0}%. Lateral force contribution to steering feel decreases. May feel less connected to tire grip changes."
            });
        }

        if (p.CompressionPower < 1.8f)
        {
            float suggested = MathF.Min(3.0f, p.CompressionPower + 0.3f);
            recs.Add(new FfbRecommendation
            {
                Type = RecommendationType.ProfileChange,
                Parameter = "CompressionPower",
                Description = "Increase compression to tame peak forces",
                CurrentValue = p.CompressionPower,
                SuggestedValue = suggested,
                Unit = "",
                Reason = "High snap count in corners with expected dynamics. More compression rounds off force peaks naturally.",
                AffectedPipelineStage = "Tanh Compression → FfbPipeline.Process()",
                CodeReference = "FfbPipeline.cs:67 — compressed = Tanh(absNorm * CompressionPower)",
                DataBreakdown =
                    $"Total snaps:             {s.TotalSnapEvents}\n" +
                    $"Corner events:           {s.CornerEventPct:F0}%\n" +
                    $"Suspicious:              {s.SuspiciousPct:F0}%\n" +
                    $"Current CompressionPower: {p.CompressionPower:F2}\n" +
                    $"Compression curve at 0.5: {MathF.Tanh(0.5f * p.CompressionPower):F4}\n" +
                    $"Compression curve at 1.0: {MathF.Tanh(1.0f * p.CompressionPower):F4}\n" +
                    $"Suggested at 0.5:         {MathF.Tanh(0.5f * suggested):F4}\n" +
                    $"Suggested at 1.0:         {MathF.Tanh(1.0f * suggested):F4}",
                DevDetail =
                    $"Compression uses Tanh: output = sign(input) * tanh(|input| * power)\n" +
                    $"This naturally limits peaks while preserving low-end linearity.\n\n" +
                    $"At power={p.CompressionPower:F2}:\n" +
                    $"  Input 0.3 → Output {MathF.Tanh(0.3f * p.CompressionPower):F3} (ratio {MathF.Tanh(0.3f * p.CompressionPower) / 0.3f:F2}x)\n" +
                    $"  Input 0.7 → Output {MathF.Tanh(0.7f * p.CompressionPower):F3} (ratio {MathF.Tanh(0.7f * p.CompressionPower) / 0.7f:F2}x)\n" +
                    $"  Input 1.0 → Output {MathF.Tanh(1.0f * p.CompressionPower):F3} (ratio {MathF.Tanh(1.0f * p.CompressionPower) / 1.0f:F2}x)\n\n" +
                    $"At power={suggested:F2}:\n" +
                    $"  Input 0.3 → Output {MathF.Tanh(0.3f * suggested):F3} (ratio {MathF.Tanh(0.3f * suggested) / 0.3f:F2}x)\n" +
                    $"  Input 0.7 → Output {MathF.Tanh(0.7f * suggested):F3} (ratio {MathF.Tanh(0.7f * suggested) / 0.7f:F2}x)\n" +
                    $"  Input 1.0 → Output {MathF.Tanh(1.0f * suggested):F3} (ratio {MathF.Tanh(1.0f * suggested) / 1.0f:F2}x)",
                Impact = $"Peak forces reduced more aggressively. At input=0.7: {MathF.Tanh(0.7f * p.CompressionPower):F3} → {MathF.Tanh(0.7f * suggested):F3}. Mid-range feel becomes more compressed/progressive."
            });
        }
    }

    private static void GenerateHighSpeedRecommendations(
        List<FfbRecommendation> recs, DiagnosticLapSummary s, FfbProfile p)
    {
        if (s.AvgSuspiciousSpeed <= 150f || s.SuspiciousSnapsOnStraight <= 0) return;
        if (recs.Any(r => r.Parameter == "MaxSlewRate")) return;

        float suggested = MathF.Max(0.02f, p.Advanced.MaxSlewRate * 0.5f);
        if (suggested >= p.Advanced.MaxSlewRate) return;
        float speedSlewScale = Math.Clamp(s.AvgSuspiciousSpeed / 15f, 0.05f, 1f)
            * (s.AvgSuspiciousSpeed > 200f ? Math.Max(1f - (s.AvgSuspiciousSpeed - 200f) / 250f, 0.4f) : 1f);
        float nearCenterScale = s.AvgSuspiciousSpeed > 150f ? 0.6f : 0f;

        recs.Add(new FfbRecommendation
        {
            Type = RecommendationType.ProfileChange,
            Parameter = "MaxSlewRate",
            Description = "Reduce high-speed slew rate further",
            CurrentValue = p.Advanced.MaxSlewRate,
            SuggestedValue = suggested,
            Unit = "/tick",
            Reason = $"Suspicious snaps at avg {s.AvgSuspiciousSpeed:F0} km/h. High-speed slew needs tighter limiting.",
            AffectedPipelineStage = "Slew Rate Limiter (speed-dependent)",
            CodeReference = "FfbPipeline.cs:168-183 — speedSlewScale + nearCenterScale compound the reduction",
            DataBreakdown =
                $"Avg suspicious speed:  {s.AvgSuspiciousSpeed:F0} km/h\n" +
                $"Suspicious straight snaps: {s.SuspiciousSnapsOnStraight}\n\n" +
                $"Current slew rate breakdown at {s.AvgSuspiciousSpeed:F0}km/h:\n" +
                $"  Base MaxSlewRate:              {p.Advanced.MaxSlewRate:F4}\n" +
                $"  * lowSpeedSlewScale:            {Math.Clamp(s.AvgSuspiciousSpeed / 15f, 0.05f, 1f):F3}\n" +
                $"  * highSpeedSlewScale:           {(s.AvgSuspiciousSpeed > 200f ? Math.Max(1f - (s.AvgSuspiciousSpeed - 200f) / 250f, 0.4f) : 1f):F3}\n" +
                $"  = effectiveSlewRate:            {p.Advanced.MaxSlewRate * speedSlewScale:F4}\n" +
                $"  Near-center further reduces by: up to 60% at >150km/h\n" +
                $"  Near-center effective:          {p.Advanced.MaxSlewRate * speedSlewScale * (1f - 0.6f * nearCenterScale):F4}",
            DevDetail =
                $"At {s.AvgSuspiciousSpeed:F0}km/h with <0.03 steer angle:\n" +
                $"  effectiveSlewRate = {p.Advanced.MaxSlewRate:F4} * {speedSlewScale:F3} * {1f - 0.6f * nearCenterScale:F3} = {p.Advanced.MaxSlewRate * speedSlewScale * (1f - 0.6f * nearCenterScale):F4}/tick\n\n" +
                $"With suggested value {suggested:F4}:\n" +
                $"  effectiveSlewRate = {suggested:F4} * {speedSlewScale:F3} * {1f - 0.6f * nearCenterScale:F3} = {suggested * speedSlewScale * (1f - 0.6f * nearCenterScale):F4}/tick\n\n" +
                $"At 333Hz tick rate, this limits force change to:\n" +
                $"  Current: ±{p.Advanced.MaxSlewRate * speedSlewScale * (1f - 0.6f * nearCenterScale) * 333f:F1}/second\n" +
                $"  Suggested: ±{suggested * speedSlewScale * (1f - 0.6f * nearCenterScale) * 333f:F1}/second",
            Impact = $"Slew rate halved. High-speed force changes become very gradual. May feel disconnected during quick direction changes at high speed."
        });
    }

    private static void GenerateCenterBlendRecommendations(
        List<FfbRecommendation> recs, DiagnosticLapSummary s, FfbProfile p)
    {
        if (s.CornerEventPct <= 60f || s.TotalOscillations <= 3) return;
        if (p.Advanced.CenterBlendDegrees >= 2f) return;

        float suggested = p.Advanced.CenterBlendDegrees + 0.5f;
        recs.Add(new FfbRecommendation
        {
            Type = RecommendationType.ProfileChange,
            Parameter = "CenterBlendDegrees",
            Description = "Widen center blend zone to smooth corner transitions",
            CurrentValue = p.Advanced.CenterBlendDegrees,
            SuggestedValue = suggested,
            Unit = "deg",
            Reason = "Oscillations in corners may be from Fy/Mz channel interaction at center. Wider blend zone smooths the crossover.",
            AffectedPipelineStage = "Channel Mixer (Center Blend)",
            CodeReference = "FfbChannelMixer.cs:183-186 — SmoothStep blend: fyBlend = t²(3-2t) where t = steerDeg/CenterBlendDegrees",
            DataBreakdown =
                $"Corner events:          {s.CornerEventPct:F0}%\n" +
                $"Total oscillations:      {s.TotalOscillations}\n" +
                $"Current CenterBlendDeg:  {p.Advanced.CenterBlendDegrees:F1}°\n\n" +
                $"SmoothStep values at current ({p.Advanced.CenterBlendDegrees:F1}°):\n" +
                $"  0.5°:  t={0.5f / p.Advanced.CenterBlendDegrees:F2} → Fy weight = {SmoothStep(0.5f / p.Advanced.CenterBlendDegrees):F3}\n" +
                $"  1.0°:  t={1.0f / p.Advanced.CenterBlendDegrees:F2} → Fy weight = {SmoothStep(1.0f / p.Advanced.CenterBlendDegrees):F3}\n" +
                $"  2.0°:  t={2.0f / p.Advanced.CenterBlendDegrees:F2} → Fy weight = {SmoothStep(Math.Clamp(2.0f / p.Advanced.CenterBlendDegrees, 0f, 1f)):F3}",
            DevDetail =
                $"The center blend zone suppresses Fy near center where it's noisy:\n" +
                $"  blendedFyFront = outFyFront * SmoothStep(steerDeg / CenterBlendDegrees)\n" +
                $"  SmoothStep(t) = t² * (3 - 2t) for t in [0,1]\n\n" +
                $"With CenterBlendDegrees={p.Advanced.CenterBlendDegrees:F1}°:\n" +
                $"  Fy fully suppressed below {p.Advanced.CenterBlendDegrees * 0.1:F2}°\n" +
                $"  Fy at 50% at {p.Advanced.CenterBlendDegrees * 0.5:F2}°\n" +
                $"  Fy at full above {p.Advanced.CenterBlendDegrees:F1}°\n\n" +
                $"Widening to {suggested:F1}° extends the Mz-only zone, giving cleaner transitions\n" +
                $"but reducing Fy contribution at moderate steering angles.",
            Impact = $"Fy suppressed over wider center zone. Steering feels cleaner near center but lateral force detail reduced up to {suggested:F1}°."
        });
    }

    private static void GenerateHealthyInfo(
        List<FfbRecommendation> recs, DiagnosticLapSummary s, FfbProfile p)
    {
        if (s.TotalEvents <= 0 || s.SuspiciousPct >= 10f || s.CornerEventPct <= 80f) return;

        int suspSnaps = s.SuspiciousSnapCauseMz + s.SuspiciousSnapCauseFx + s.SuspiciousSnapCauseFy + s.SuspiciousSnapCauseSlew;

        recs.Add(new FfbRecommendation
        {
            Type = RecommendationType.Info,
            Description = "FFB looks healthy for this driving style",
            Reason = $"Events are {s.CornerEventPct:F0}% in corners with only {s.SuspiciousPct:F0}% suspicious. The force behavior matches normal driving dynamics. Focus on feel preference tuning, not bug fixes.",
            AffectedPipelineStage = "All stages",
            DataBreakdown =
                $"Total events:       {s.TotalEvents}\n" +
                $"Corner events:      {s.EventsInCorners} ({s.CornerEventPct:F0}%)\n" +
                $"Straight events:    {s.EventsOnStraights}\n" +
                $"Expected dynamics:  {s.EventsExpected}\n" +
                $"Suspicious:         {s.EventsSuspicious} ({s.SuspiciousPct:F0}%)\n\n" +
                $"Event type breakdown:\n" +
                $"  Snaps:            {s.TotalSnapEvents} (expected: {s.ExpectedSnapCount}, suspicious: {suspSnaps})\n" +
                $"    ALL causes:     Mz={s.SnapCauseMz} Fx={s.SnapCauseFx} Fy={s.SnapCauseFy} Slew={s.SnapCauseSlew}\n" +
                $"    Suspicious:     Mz={s.SuspiciousSnapCauseMz} Fx={s.SuspiciousSnapCauseFx} Fy={s.SuspiciousSnapCauseFy} Slew={s.SuspiciousSnapCauseSlew}\n" +
                $"  Oscillations:     {s.TotalOscillations}\n" +
                $"  Clipping:         {s.TotalClippingEvents} (corner:{s.ClipEventsInCorners} straight:{s.ClipEventsOnStraights})\n" +
                $"  Force anomalies:  {s.TotalForceAnomalies}",
            DevDetail =
                $"VERDICT: {s.Verdict}\n\n" +
                $"The pipeline is working correctly for this driving session.\n" +
                $"All detected events are within expected driving dynamics:\n" +
                $"  - Snaps during corner entry/exit = normal tire load transfer\n" +
                $"  - Clipping in corners = normal at high downforce/grip\n" +
                $"  - Oscillations in corners = normal EMA response to rapid input\n\n" +
                $"Current profile snapshot:\n" +
                $"  OutputGain: {p.OutputGain:F2}  CompressionPower: {p.CompressionPower:F2}\n" +
                $"  MzFront: {p.MzFront.Gain:F2}  FyFront: {p.FyFront.Gain:F2}  FxFront: {p.FxFront.Gain:F2}\n" +
                $"  MaxSlewRate: {p.Advanced.MaxSlewRate:F4}  Hysteresis: {p.Advanced.HysteresisThreshold:F4}\n" +
                $"  CenterSupp: {p.Advanced.CenterSuppressionDegrees:F1}°  CenterBlend: {p.Advanced.CenterBlendDegrees:F1}°\n" +
                $"  SoftClip: {p.SoftClipThreshold:F2}  NoiseFloor: {p.Advanced.NoiseFloor:F4}\n" +
                $"  SuspensionRoadGain: {p.Vibrations.SuspensionRoadGain:F2}  CurbGain: {p.Vibrations.KerbGain:F2}",
            Impact = "No changes needed. Adjust feel preferences (gain, damping, vibration levels) to taste."
        });
    }

    private static float SmoothStep(float t)
    {
        t = Math.Clamp(t, 0f, 1f);
        return t * t * (3f - 2f * t);
    }
}
