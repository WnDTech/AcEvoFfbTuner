namespace AcEvoFfbTuner.Core.TrackMapping;

public sealed class LapSnapshot
{
    public int LapNumber { get; set; }
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    public float LapTimeS { get; set; }
    public float AvgOutputForce { get; set; }
    public float PeakOutputForce { get; set; }
    public float ClippingPct { get; set; }
    public float AvgSpeedKmh { get; set; }
    public WaypointForceSample[] WaypointForces { get; set; } = Array.Empty<WaypointForceSample>();
    // Phase 3: profile attribution (additive — defaults keep old laps valid).
    public string ProfileName { get; set; } = "";
    public string GameName { get; set; } = "";
    public string CarName { get; set; } = "";
    public string TrackName { get; set; } = "";
}

public sealed record ProfileLapSummary(
    string ProfileName,
    int LapCount,
    float BestLapTimeS,
    float AvgLapTimeS,
    float AvgClippingPct,
    float AvgForce);

public sealed class LapDataRecorder
{
    private readonly object _lock = new();
    private readonly List<LapSnapshot> _completedLaps = new();
    private int _currentLapNumber = -1;
    private DateTime _lapStartTime;
    private float _sumForce;
    private float _peakForce;
    private int _clipCount;
    private int _sampleCount;
    private float _sumSpeed;
    private TrackForceHeatmap? _lapHeatmap;
    private int _waypointCount;

    /// <summary>
    /// Phase 3: active setup attribution, stamped onto each completed lap.
    /// Set by the UI layer on profile switch / A-B toggle. Defaults to empty
    /// (older laps / unknown setup). Never affects FFB processing.
    /// </summary>
    public string ActiveProfileName { get; set; } = "";
    public string ActiveGameName { get; set; } = "";
    public string ActiveCarName { get; set; } = "";
    public string ActiveTrackName { get; set; } = "";

    public IReadOnlyList<LapSnapshot> CompletedLaps
    {
        get { lock (_lock) return new List<LapSnapshot>(_completedLaps); }
    }

    public void Initialize(int waypointCount)
    {
        _waypointCount = waypointCount;
    }

    public void Update(int lapNumber, int nearestWaypoint, float outputForce, float mzFront,
        float fxFront, float fyFront, float speedKmh, bool isClipping)
    {
        lock (_lock)
        {
            if (_currentLapNumber < 0 && lapNumber > 0)
            {
                StartLap(lapNumber);
            }
            else if (_currentLapNumber >= 0 && lapNumber != _currentLapNumber)
            {
                CompleteLap();
                if (lapNumber > 0)
                    StartLap(lapNumber);
                return;
            }

            if (_currentLapNumber < 0) return;

            _sumForce += MathF.Abs(outputForce);
            if (MathF.Abs(outputForce) > _peakForce) _peakForce = MathF.Abs(outputForce);
            if (isClipping) _clipCount++;
            _sumSpeed += speedKmh;
            _sampleCount++;

            _lapHeatmap?.Record(nearestWaypoint, outputForce, mzFront, fxFront, fyFront, speedKmh, isClipping);
        }
    }

    private void StartLap(int lapNumber)
    {
        _currentLapNumber = lapNumber;
        _lapStartTime = DateTime.UtcNow;
        _sumForce = 0;
        _peakForce = 0;
        _clipCount = 0;
        _sampleCount = 0;
        _sumSpeed = 0;
        _lapHeatmap = new TrackForceHeatmap();
        _lapHeatmap.Initialize(_waypointCount);
    }

    private void CompleteLap()
    {
        if (_sampleCount < 100 || _lapHeatmap == null)
        {
            _currentLapNumber = -1;
            return;
        }

        var lap = new LapSnapshot
        {
            LapNumber = _currentLapNumber,
            LapTimeS = (float)(DateTime.UtcNow - _lapStartTime).TotalSeconds,
            AvgOutputForce = _sumForce / _sampleCount,
            PeakOutputForce = _peakForce,
            ClippingPct = (float)_clipCount / _sampleCount * 100f,
            AvgSpeedKmh = _sumSpeed / _sampleCount,
            WaypointForces = _lapHeatmap.GetSnapshot() ?? Array.Empty<WaypointForceSample>(),
            ProfileName = ActiveProfileName,
            GameName = ActiveGameName,
            CarName = ActiveCarName,
            TrackName = ActiveTrackName
        };

        _completedLaps.Add(lap);
        if (_completedLaps.Count > 10)
            _completedLaps.RemoveAt(0);

        _currentLapNumber = -1;
    }

    public LapSnapshot? GetLatestLap()
    {
        lock (_lock)
        {
            return _completedLaps.Count > 0 ? _completedLaps[^1] : null;
        }
    }

    /// <summary>
    /// Phase 3: per-setup aggregates for profile A/B comparison.
    /// Groups completed laps by ProfileName (empty → "(unknown)").
    /// Pure read — no recording state changes.
    /// </summary>
    public IReadOnlyList<ProfileLapSummary> GetSummaryByProfile()
    {
        lock (_lock)
        {
            return _completedLaps
                .GroupBy(l => string.IsNullOrWhiteSpace(l.ProfileName) ? "(unknown)" : l.ProfileName)
                .Select(g => new ProfileLapSummary(
                    g.Key,
                    g.Count(),
                    g.Min(l => l.LapTimeS),
                    g.Average(l => l.LapTimeS),
                    g.Average(l => l.ClippingPct),
                    g.Average(l => l.AvgOutputForce)))
                .OrderBy(s => s.BestLapTimeS)
                .ToList();
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            _completedLaps.Clear();
            _currentLapNumber = -1;
        }
    }
}
