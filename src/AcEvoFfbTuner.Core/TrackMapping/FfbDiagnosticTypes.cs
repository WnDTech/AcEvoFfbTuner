namespace AcEvoFfbTuner.Core.TrackMapping;

public enum FfbEventType
{
    None = 0,
    Snap = 1,
    OscillationCluster = 2,
    Clipping = 3,
    ForceDirectionAnomaly = 4
}

public enum FfbEventClassification
{
    Normal = 0,
    ExpectedDynamics = 1,
    Suspicious = 2
}

public enum DrivingState
{
    Unknown = 0,
    StraightCruising = 1,
    BrakingIntoCorner = 2,
    CornerEntry = 3,
    CornerApex = 4,
    CornerExit = 5,
    AcceleratingOutOfCorner = 6,
    Understeer = 7,
    Oversteer = 8
}

public sealed class FfbDiagnosticEvent
{
    public FfbEventType EventType { get; set; }
    public FfbEventClassification Classification { get; set; }
    public DrivingState DrivingState { get; set; }
    public float ForceDelta { get; set; }
    public float OutputForce { get; set; }
    public float SteerAngle { get; set; }
    public float SpeedKmh { get; set; }
    public float SlipAngleFront { get; set; }
    public float SlipRatioFront { get; set; }
    public bool InCorner { get; set; }
    public string? CornerName { get; set; }
    public int WaypointIndex { get; set; }
    public double TimestampS { get; set; }

    public float MzFrontForce { get; set; }
    public float FxFrontForce { get; set; }
    public float FyFrontForce { get; set; }
    public float PostCompressionForce { get; set; }
    public float PostSlipForce { get; set; }
    public float PostDampingForce { get; set; }
    public float PostDynamicForce { get; set; }
    public float PrevMzFrontForce { get; set; }
    public float PrevFxFrontForce { get; set; }
    public float PrevFyFrontForce { get; set; }
    public float RoadForceModulation { get; set; }
    public float PrevRoadForceModulation { get; set; }

    public bool IsRoadVibrationInduced => MathF.Abs(RoadForceModulation) > 0.005f ||
        MathF.Abs(RoadForceModulation - PrevRoadForceModulation) > 0.003f;

    public string LikelyCause
    {
        get
        {
            if (EventType == FfbEventType.Clipping)
                return "output_gain";
            if (EventType == FfbEventType.ForceDirectionAnomaly)
                return "sign_correction";
            if (EventType == FfbEventType.OscillationCluster)
            {
                if (IsRoadVibrationInduced) return "road_vibration";
                return InCorner ? "ema_smoothing" : "slew_rate";
            }

            if (EventType == FfbEventType.Snap && IsRoadVibrationInduced)
                return "road_vibration";

            float mzDelta = MathF.Abs(MzFrontForce - PrevMzFrontForce);
            float fxDelta = MathF.Abs(FxFrontForce - PrevFxFrontForce);
            float fyDelta = MathF.Abs(FyFrontForce - PrevFyFrontForce);
            float maxDelta = MathF.Max(mzDelta, MathF.Max(fxDelta, fyDelta));

            if (maxDelta < 0.001f)
                return "slew_rate";
            if (mzDelta >= fxDelta && mzDelta >= fyDelta)
                return "mz_channel";
            if (fyDelta >= fxDelta)
                return "fy_channel";
            return "fx_channel";
        }
    }
}

public sealed class WaypointDiagnosticSample
{
    public int SnapCount { get; set; }
    public int OscillationCount { get; set; }
    public int ClippingCount { get; set; }
    public int ForceAnomalyCount { get; set; }
    public int SuspiciousCount { get; set; }
    public int ExpectedCount { get; set; }
    public int SampleCount { get; set; }

    public float AvgSlipAngleFront { get; set; }
    public float PeakSlipAngleFront { get; set; }
    public float AvgSlipRatioFront { get; set; }
    public float PeakSlipRatioFront { get; set; }
    public float AvgSteerAngle { get; set; }
    public float PeakSteerAngle { get; set; }
    public float AvgLateralG { get; set; }
    public float PeakLateralG { get; set; }

    public FfbEventClassification WorstClassification { get; set; }
    public int TotalEventCount => SnapCount + OscillationCount + ClippingCount + ForceAnomalyCount;
}

public sealed class DiagnosticLapSummary
{
    public int LapNumber { get; set; }
    public int TotalSnapEvents { get; set; }
    public int TotalOscillations { get; set; }
    public int TotalClippingEvents { get; set; }
    public int TotalForceAnomalies { get; set; }
    public int EventsInCorners { get; set; }
    public int EventsOnStraights { get; set; }
    public int EventsExpected { get; set; }
    public int EventsSuspicious { get; set; }
    public int SuspiciousSnapsOnStraight { get; set; }
    public int SuspiciousOscillationsOnStraight { get; set; }
    public int SuspiciousForceAnomalies { get; set; }
    public int ClipEventsInCorners { get; set; }
    public int ClipEventsOnStraights { get; set; }
    public int SnapCauseMz { get; set; }
    public int SnapCauseFx { get; set; }
    public int SnapCauseFy { get; set; }
    public int SnapCauseSlew { get; set; }
    public int SnapCauseRoadVibration { get; set; }
    public int SuspiciousSnapCauseMz { get; set; }
    public int SuspiciousSnapCauseFx { get; set; }
    public int SuspiciousSnapCauseFy { get; set; }
    public int SuspiciousSnapCauseSlew { get; set; }
    public int SuspiciousSnapCauseRoadVibration { get; set; }
    public int ExpectedSnapCount { get; set; }
    public float AvgSuspiciousSpeed { get; set; }
    public float CornerEventPct => TotalEvents > 0 ? (float)EventsInCorners / TotalEvents * 100f : 0f;
    public float SuspiciousPct => TotalEvents > 0 ? (float)EventsSuspicious / TotalEvents * 100f : 0f;
    public int TotalEvents => TotalSnapEvents + TotalOscillations + TotalClippingEvents + TotalForceAnomalies;
    public int TotalRoadVibrationSnaps => SnapCauseRoadVibration;
    public int SuspiciousRoadVibrationSnaps => SuspiciousSnapCauseRoadVibration;

    public string Verdict
    {
        get
        {
            if (TotalEvents == 0) return "No events detected";
            int roadVibPct = TotalEvents > 0 ? SnapCauseRoadVibration * 100 / Math.Max(TotalEvents, 1) : 0;
            if (SuspiciousPct > 30f)
            {
                if (SuspiciousSnapCauseRoadVibration > 0 && roadVibPct > 20)
                    return $"CHECK PROFILE — road vibration ({SuspiciousPct:F0}% suspicious, {SuspiciousSnapCauseRoadVibration} road-vibration)";
                return $"CODE ISSUE LIKELY ({SuspiciousPct:F0}% suspicious)";
            }
            if (CornerEventPct > 70f) return $"NORMAL DRIVING ({CornerEventPct:F0}% in corners)";
            if (CornerEventPct > 50f) return $"MIXED ({CornerEventPct:F0}% corner, {SuspiciousPct:F0}% suspicious)";
            if (SuspiciousSnapCauseRoadVibration > 0)
                return $"CHECK PROFILE ({CornerEventPct:F0}% corner, {SuspiciousPct:F0}% suspicious, road-vibration active)";
            return $"CHECK CODE ({CornerEventPct:F0}% corner, {SuspiciousPct:F0}% suspicious)";
        }
    }
}
