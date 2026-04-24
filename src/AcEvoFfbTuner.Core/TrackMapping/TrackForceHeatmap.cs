namespace AcEvoFfbTuner.Core.TrackMapping;

public sealed class WaypointForceSample
{
    public float OutputForce { get; set; }
    public float MzFront { get; set; }
    public float FxFront { get; set; }
    public float FyFront { get; set; }
    public float SpeedKmh { get; set; }
    public bool IsClipping { get; set; }
    public int SampleCount { get; set; }
}

public sealed class TrackForceHeatmap
{
    private WaypointForceSample[]? _samples;
    private int _waypointCount;
    private readonly object _lock = new();

    public bool HasData => _samples != null;

    public void Initialize(int waypointCount)
    {
        lock (_lock)
        {
            _waypointCount = waypointCount;
            _samples = new WaypointForceSample[waypointCount];
            for (int i = 0; i < waypointCount; i++)
                _samples[i] = new WaypointForceSample();
        }
    }

    public void Record(int nearestWaypointIndex, float outputForce, float mzFront, float fxFront,
        float fyFront, float speedKmh, bool isClipping)
    {
        lock (_lock)
        {
            if (_samples == null) return;
            int idx = ((nearestWaypointIndex % _waypointCount) + _waypointCount) % _waypointCount;
            var s = _samples[idx];

            float w = 1f / (s.SampleCount + 1f);
            s.OutputForce += (outputForce - s.OutputForce) * w;
            s.MzFront += (mzFront - s.MzFront) * w;
            s.FxFront += (fxFront - s.FxFront) * w;
            s.FyFront += (fyFront - s.FyFront) * w;
            s.SpeedKmh += (speedKmh - s.SpeedKmh) * w;
            if (isClipping) s.IsClipping = true;
            s.SampleCount++;
        }
    }

    public WaypointForceSample[]? GetSnapshot()
    {
        lock (_lock)
        {
            if (_samples == null) return null;
            var copy = new WaypointForceSample[_waypointCount];
            for (int i = 0; i < _waypointCount; i++)
            {
                copy[i] = new WaypointForceSample
                {
                    OutputForce = _samples[i].OutputForce,
                    MzFront = _samples[i].MzFront,
                    FxFront = _samples[i].FxFront,
                    FyFront = _samples[i].FyFront,
                    SpeedKmh = _samples[i].SpeedKmh,
                    IsClipping = _samples[i].IsClipping,
                    SampleCount = _samples[i].SampleCount
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
                    _samples[i] = new WaypointForceSample();
            }
        }
    }
}
