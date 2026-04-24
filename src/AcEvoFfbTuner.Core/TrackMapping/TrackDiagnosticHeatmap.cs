namespace AcEvoFfbTuner.Core.TrackMapping;

public sealed class TrackDiagnosticHeatmap
{
    private WaypointDiagnosticSample[]? _samples;
    private int _waypointCount;
    private readonly object _lock = new();

    private int _lapSnapTotal, _lapOscTotal, _lapClipTotal, _lapAnomalyTotal;
    private int _lapCornerEvents, _lapStraightEvents;
    private int _lapExpected, _lapSuspicious;
    private int _lapSuspiciousSnapsStraight, _lapSuspiciousOscStraight, _lapSuspiciousForceAnomaly;
    private int _lapClipCorner, _lapClipStraight;
    private int _lapCauseMz, _lapCauseFx, _lapCauseFy, _lapCauseSlew;
    private int _lapSuspCauseMz, _lapSuspCauseFx, _lapSuspCauseFy, _lapSuspCauseSlew;
    private int _lapExpectedSnaps;
    private float _lapSuspSpeedSum;
    private int _lapSuspSpeedCount;
    private int _currentLapNumber = -1;
    private DateTime _lapStart;

    public bool HasData => _samples != null;
    public DiagnosticLapSummary? LatestLapSummary { get; private set; }

    public int RunningSnapCount => _lapSnapTotal;
    public int RunningOscCount => _lapOscTotal;
    public int RunningClipCount => _lapClipTotal;
    public int RunningAnomalyCount => _lapAnomalyTotal;
    public int RunningSuspiciousCount => _lapSuspicious;
    public int RunningCornerEvents => _lapCornerEvents;
    public int RunningStraightEvents => _lapStraightEvents;
    public int RunningTotalEvents => _lapSnapTotal + _lapOscTotal + _lapClipTotal + _lapAnomalyTotal;

    private int _waypointsSampled;
    public float TrackCoveragePct => _waypointCount > 0 ? (float)_waypointsSampled / _waypointCount * 100f : 0f;
    public bool HasSufficientCoverage => TrackCoveragePct >= 60f;

    public DiagnosticLapSummary? GetRunningSummary()
    {
        int total = RunningTotalEvents;
        if (total == 0) return null;

        return new DiagnosticLapSummary
        {
            LapNumber = _currentLapNumber > 0 ? _currentLapNumber : 0,
            TotalSnapEvents = _lapSnapTotal,
            TotalOscillations = _lapOscTotal,
            TotalClippingEvents = _lapClipTotal,
            TotalForceAnomalies = _lapAnomalyTotal,
            EventsInCorners = _lapCornerEvents,
            EventsOnStraights = _lapStraightEvents,
            EventsExpected = _lapExpected,
            EventsSuspicious = _lapSuspicious,
            SuspiciousSnapsOnStraight = _lapSuspiciousSnapsStraight,
            SuspiciousOscillationsOnStraight = _lapSuspiciousOscStraight,
            SuspiciousForceAnomalies = _lapSuspiciousForceAnomaly,
            ClipEventsInCorners = _lapClipCorner,
            ClipEventsOnStraights = _lapClipStraight,
            SnapCauseMz = _lapCauseMz,
            SnapCauseFx = _lapCauseFx,
            SnapCauseFy = _lapCauseFy,
            SnapCauseSlew = _lapCauseSlew,
            SuspiciousSnapCauseMz = _lapSuspCauseMz,
            SuspiciousSnapCauseFx = _lapSuspCauseFx,
            SuspiciousSnapCauseFy = _lapSuspCauseFy,
            SuspiciousSnapCauseSlew = _lapSuspCauseSlew,
            ExpectedSnapCount = _lapExpectedSnaps,
            AvgSuspiciousSpeed = _lapSuspSpeedCount > 0 ? _lapSuspSpeedSum / _lapSuspSpeedCount : 0f
        };
    }

    public void Initialize(int waypointCount)
    {
        lock (_lock)
        {
            _waypointCount = waypointCount;
            _samples = new WaypointDiagnosticSample[waypointCount];
            for (int i = 0; i < waypointCount; i++)
                _samples[i] = new WaypointDiagnosticSample();
        }
    }

    public void RecordEvent(FfbDiagnosticEvent evt)
    {
        lock (_lock)
        {
            if (_samples == null) return;
            int idx = ((evt.WaypointIndex % _waypointCount) + _waypointCount) % _waypointCount;
            var s = _samples[idx];

            switch (evt.EventType)
            {
                case FfbEventType.Snap: s.SnapCount++; break;
                case FfbEventType.OscillationCluster: s.OscillationCount++; break;
                case FfbEventType.Clipping: s.ClippingCount++; break;
                case FfbEventType.ForceDirectionAnomaly: s.ForceAnomalyCount++; break;
            }

            if (evt.Classification == FfbEventClassification.Suspicious)
            {
                s.SuspiciousCount++;
                if (s.WorstClassification < FfbEventClassification.Suspicious)
                    s.WorstClassification = FfbEventClassification.Suspicious;
            }
            else if (evt.Classification == FfbEventClassification.ExpectedDynamics)
            {
                s.ExpectedCount++;
                if (s.WorstClassification < FfbEventClassification.ExpectedDynamics)
                    s.WorstClassification = FfbEventClassification.ExpectedDynamics;
            }

            _lapCornerEvents += evt.InCorner ? 1 : 0;
            _lapStraightEvents += evt.InCorner ? 0 : 1;
            _lapExpected += evt.Classification == FfbEventClassification.ExpectedDynamics ? 1 : 0;
            _lapSuspicious += evt.Classification == FfbEventClassification.Suspicious ? 1 : 0;

            if (evt.Classification == FfbEventClassification.Suspicious)
            {
                _lapSuspSpeedSum += evt.SpeedKmh;
                _lapSuspSpeedCount++;

                if (evt.EventType == FfbEventType.Snap && !evt.InCorner) _lapSuspiciousSnapsStraight++;
                if (evt.EventType == FfbEventType.OscillationCluster && !evt.InCorner) _lapSuspiciousOscStraight++;
                if (evt.EventType == FfbEventType.ForceDirectionAnomaly) _lapSuspiciousForceAnomaly++;
            }

            if (evt.EventType == FfbEventType.Clipping)
            {
                if (evt.InCorner) _lapClipCorner++;
                else _lapClipStraight++;
            }

            if (evt.EventType == FfbEventType.Snap)
            {
                switch (evt.LikelyCause)
                {
                    case "mz_channel": _lapCauseMz++; break;
                    case "fx_channel": _lapCauseFx++; break;
                    case "fy_channel": _lapCauseFy++; break;
                    default: _lapCauseSlew++; break;
                }

                if (evt.Classification == FfbEventClassification.Suspicious)
                {
                    switch (evt.LikelyCause)
                    {
                        case "mz_channel": _lapSuspCauseMz++; break;
                        case "fx_channel": _lapSuspCauseFx++; break;
                        case "fy_channel": _lapSuspCauseFy++; break;
                        default: _lapSuspCauseSlew++; break;
                    }
                }

                if (evt.Classification == FfbEventClassification.ExpectedDynamics)
                    _lapExpectedSnaps++;
            }

            switch (evt.EventType)
            {
                case FfbEventType.Snap: _lapSnapTotal++; break;
                case FfbEventType.OscillationCluster: _lapOscTotal++; break;
                case FfbEventType.Clipping: _lapClipTotal++; break;
                case FfbEventType.ForceDirectionAnomaly: _lapAnomalyTotal++; break;
            }
        }
    }

    public void RecordDrivingState(int waypointIndex, float slipAngleFront, float slipRatioFront,
        float steerAngle, float lateralG)
    {
        lock (_lock)
        {
            if (_samples == null) return;
            int idx = ((waypointIndex % _waypointCount) + _waypointCount) % _waypointCount;
            var s = _samples[idx];

            if (s.SampleCount == 0)
                _waypointsSampled++;

            float w = 1f / (s.SampleCount + 1f);
            s.AvgSlipAngleFront += (MathF.Abs(slipAngleFront) - s.AvgSlipAngleFront) * w;
            s.AvgSlipRatioFront += (MathF.Abs(slipRatioFront) - s.AvgSlipRatioFront) * w;
            s.AvgSteerAngle += (MathF.Abs(steerAngle) - s.AvgSteerAngle) * w;
            s.AvgLateralG += (MathF.Abs(lateralG) - s.AvgLateralG) * w;

            if (MathF.Abs(slipAngleFront) > s.PeakSlipAngleFront)
                s.PeakSlipAngleFront = MathF.Abs(slipAngleFront);
            if (MathF.Abs(slipRatioFront) > s.PeakSlipRatioFront)
                s.PeakSlipRatioFront = MathF.Abs(slipRatioFront);
            if (MathF.Abs(steerAngle) > s.PeakSteerAngle)
                s.PeakSteerAngle = MathF.Abs(steerAngle);
            if (MathF.Abs(lateralG) > s.PeakLateralG)
                s.PeakLateralG = MathF.Abs(lateralG);

            s.SampleCount++;
        }
    }

    public void OnNewLap(int lapNumber)
    {
        lock (_lock)
        {
            if (_currentLapNumber >= 0 && (_lapSnapTotal + _lapOscTotal + _lapClipTotal + _lapAnomalyTotal) > 0)
            {
                LatestLapSummary = new DiagnosticLapSummary
                {
                    LapNumber = _currentLapNumber,
                    TotalSnapEvents = _lapSnapTotal,
                    TotalOscillations = _lapOscTotal,
                    TotalClippingEvents = _lapClipTotal,
                    TotalForceAnomalies = _lapAnomalyTotal,
                    EventsInCorners = _lapCornerEvents,
                    EventsOnStraights = _lapStraightEvents,
                    EventsExpected = _lapExpected,
                    EventsSuspicious = _lapSuspicious,
                    SuspiciousSnapsOnStraight = _lapSuspiciousSnapsStraight,
                    SuspiciousOscillationsOnStraight = _lapSuspiciousOscStraight,
                    SuspiciousForceAnomalies = _lapSuspiciousForceAnomaly,
                    ClipEventsInCorners = _lapClipCorner,
                    ClipEventsOnStraights = _lapClipStraight,
                    SnapCauseMz = _lapCauseMz,
                    SnapCauseFx = _lapCauseFx,
                    SnapCauseFy = _lapCauseFy,
                    SnapCauseSlew = _lapCauseSlew,
                    SuspiciousSnapCauseMz = _lapSuspCauseMz,
                    SuspiciousSnapCauseFx = _lapSuspCauseFx,
                    SuspiciousSnapCauseFy = _lapSuspCauseFy,
                    SuspiciousSnapCauseSlew = _lapSuspCauseSlew,
                    ExpectedSnapCount = _lapExpectedSnaps,
                    AvgSuspiciousSpeed = _lapSuspSpeedCount > 0 ? _lapSuspSpeedSum / _lapSuspSpeedCount : 0f
                };
            }

            _currentLapNumber = lapNumber;
            _lapStart = DateTime.UtcNow;
            _lapSnapTotal = 0;
            _lapOscTotal = 0;
            _lapClipTotal = 0;
            _lapAnomalyTotal = 0;
            _lapCornerEvents = 0;
            _lapStraightEvents = 0;
            _lapExpected = 0;
            _lapSuspicious = 0;
            _lapSuspiciousSnapsStraight = 0;
            _lapSuspiciousOscStraight = 0;
            _lapSuspiciousForceAnomaly = 0;
            _lapClipCorner = 0;
            _lapClipStraight = 0;
            _lapCauseMz = 0;
            _lapCauseFx = 0;
            _lapCauseFy = 0;
            _lapCauseSlew = 0;
            _lapSuspCauseMz = 0;
            _lapSuspCauseFx = 0;
            _lapSuspCauseFy = 0;
            _lapSuspCauseSlew = 0;
            _lapExpectedSnaps = 0;
            _lapSuspSpeedSum = 0;
            _lapSuspSpeedCount = 0;
            _waypointsSampled = 0;
        }
    }

    public WaypointDiagnosticSample[]? GetSnapshot()
    {
        lock (_lock)
        {
            if (_samples == null) return null;
            var copy = new WaypointDiagnosticSample[_waypointCount];
            for (int i = 0; i < _waypointCount; i++)
            {
                copy[i] = new WaypointDiagnosticSample
                {
                    SnapCount = _samples[i].SnapCount,
                    OscillationCount = _samples[i].OscillationCount,
                    ClippingCount = _samples[i].ClippingCount,
                    ForceAnomalyCount = _samples[i].ForceAnomalyCount,
                    SuspiciousCount = _samples[i].SuspiciousCount,
                    ExpectedCount = _samples[i].ExpectedCount,
                    SampleCount = _samples[i].SampleCount,
                    AvgSlipAngleFront = _samples[i].AvgSlipAngleFront,
                    PeakSlipAngleFront = _samples[i].PeakSlipAngleFront,
                    AvgSlipRatioFront = _samples[i].AvgSlipRatioFront,
                    PeakSlipRatioFront = _samples[i].PeakSlipRatioFront,
                    AvgSteerAngle = _samples[i].AvgSteerAngle,
                    PeakSteerAngle = _samples[i].PeakSteerAngle,
                    AvgLateralG = _samples[i].AvgLateralG,
                    PeakLateralG = _samples[i].PeakLateralG,
                    WorstClassification = _samples[i].WorstClassification
                };
            }
            return copy;
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            if (_samples != null)
            {
                for (int i = 0; i < _waypointCount; i++)
                    _samples[i] = new WaypointDiagnosticSample();
            }
            _lapSnapTotal = 0;
            _lapOscTotal = 0;
            _lapClipTotal = 0;
            _lapAnomalyTotal = 0;
            _lapCornerEvents = 0;
            _lapStraightEvents = 0;
            _lapExpected = 0;
            _lapSuspicious = 0;
            _waypointsSampled = 0;
            LatestLapSummary = null;
        }
    }
}
