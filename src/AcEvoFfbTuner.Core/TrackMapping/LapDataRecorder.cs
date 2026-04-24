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
}

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
            WaypointForces = _lapHeatmap.GetSnapshot() ?? Array.Empty<WaypointForceSample>()
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

    public void Clear()
    {
        lock (_lock)
        {
            _completedLaps.Clear();
            _currentLapNumber = -1;
        }
    }
}
