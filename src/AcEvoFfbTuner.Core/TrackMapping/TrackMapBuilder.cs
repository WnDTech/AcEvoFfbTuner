using System.Diagnostics;

namespace AcEvoFfbTuner.Core.TrackMapping;

public sealed class TrackMapBuilder
{
    private const float MinSampleDistance = 3f;
    private const float LapCompleteDistance = 50f;
    private const float OverlapTrimSearch = 0.15f;
    private const int MinLapWaypoints = 100;
    private const float SimplifyTolerance = 1.5f;
    private const float MinTrackLength = 500f;
    private const float MinSpeedKmh = 10f;

    private static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "AcEvoFfbTuner", "trackmap_debug.log");

    private readonly object _lock = new();
    private readonly List<TrackWaypoint> _rawWaypoints = new();
    private TrackWaypoint? _startPoint;
    private bool _recording;
    private bool _lapStarted;
    private float _totalDistance;

    public bool IsRecording { get { lock (_lock) return _recording; } }
    public bool HasCompleteMap { get { lock (_lock) return _hasCompleteMap; } private set { lock (_lock) _hasCompleteMap = value; } }
    public TrackMap? CurrentMap { get { lock (_lock) return _currentMap; } private set { lock (_lock) _currentMap = value; } }
    public int WaypointCount { get { lock (_lock) return _rawWaypoints.Count; } }

    private bool _hasCompleteMap;
    private TrackMap? _currentMap;

    internal static void Log(string msg)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
            File.AppendAllText(LogPath, $"[{DateTime.Now:HH:mm:ss.fff}] {msg}\n");
        }
        catch { }
    }

    public void StartRecording()
    {
        lock (_lock)
        {
            _rawWaypoints.Clear();
            _startPoint = null;
            _recording = true;
            _lapStarted = false;
            _hasCompleteMap = false;
            _currentMap = null;
            _totalDistance = 0f;
        }
        Log("START RECORDING");
    }

    public void StopRecording()
    {
        lock (_lock) _recording = false;
        Log($"STOP RECORDING (waypoints={WaypointCount}, dist={_totalDistance:F1}m)");
    }

    public bool ForceComplete()
    {
        Log($"FORCE COMPLETE requested (waypoints={WaypointCount}, hasMap={HasCompleteMap})");

        lock (_lock)
        {
            if (_rawWaypoints.Count < 10)
            {
                Log($"FORCE COMPLETE rejected: only {_rawWaypoints.Count} waypoints");
                return false;
            }
            if (_hasCompleteMap)
            {
                Log($"FORCE COMPLETE: already complete");
                return true;
            }

            int beforeCount = _rawWaypoints.Count;
            float startToEnd = _rawWaypoints.Count > 1
                ? _rawWaypoints[^1].DistanceTo2D(_rawWaypoints[0])
                : 0f;

            Log($"FORCE COMPLETE: {beforeCount} raw pts, totalDist={_totalDistance:F1}m, startToEnd={startToEnd:F1}m");
            Log($"  Start: ({_rawWaypoints[0].X:F1}, {_rawWaypoints[0].Z:F1})");
            Log($"  End:   ({_rawWaypoints[^1].X:F1}, {_rawWaypoints[^1].Z:F1})");

            _recording = false;
            ApplyDriftCorrectionAndClose();
            Log($"  After trim: {_rawWaypoints.Count} pts (removed {beforeCount - _rawWaypoints.Count})");

            bool ok = FinalizeMap();
            Log($"  FinalizeMap result: {ok} (map pts={_currentMap?.Waypoints.Count ?? 0}, length={_currentMap?.TrackLengthM ?? 0:F1}m)");
            return ok;
        }
    }

    public void Update(float carX, float carZ, float carY, float speedKmh, int currentLap, bool isOnTrack, float npos = 0f)
    {
        lock (_lock)
        {
            if (!_recording) return;
            if (speedKmh < MinSpeedKmh) return;

            var point = new TrackWaypoint(carX, carZ, carY, npos);

            if (!_lapStarted)
            {
                _startPoint = point;
                _rawWaypoints.Add(point);
                _lapStarted = true;
                Log($"FIRST POINT: ({carX:F1}, {carZ:F1}) speed={speedKmh:F1} lap={currentLap} onTrack={isOnTrack}");
                return;
            }

            float dist = point.DistanceTo2D(_rawWaypoints[^1]);
            if (dist < MinSampleDistance) return;

            if (_startPoint != null && _rawWaypoints.Count > MinLapWaypoints && _totalDistance > MinTrackLength)
            {
                float distToStart = point.DistanceTo2D(_startPoint);
                if (distToStart < LapCompleteDistance)
                {
                    Log($"LAP COMPLETE detected: distToStart={distToStart:F1}m pts={_rawWaypoints.Count} totalDist={_totalDistance:F1}m");
                    Log($"  Start: ({_startPoint.X:F1}, {_startPoint.Z:F1})");
                    Log($"  Now:   ({carX:F1}, {carZ:F1})");

                    _recording = false;
                    ApplyDriftCorrectionAndClose();
                    FinalizeMap();

                    Log($"  After finalize: {_rawWaypoints.Count} raw, {_currentMap?.Waypoints.Count ?? 0} simplified, length={_currentMap?.TrackLengthM ?? 0:F1}m");
                    return;
                }
            }

            _rawWaypoints.Add(point);
            _totalDistance += dist;

            if (_rawWaypoints.Count % 200 == 0)
            {
                float d2s = _startPoint != null ? point.DistanceTo2D(_startPoint) : -1f;
                Log($"  [{_rawWaypoints.Count} pts] pos=({carX:F1},{carZ:F1}) totalDist={_totalDistance:F1}m distToStart={d2s:F1}m speed={speedKmh:F1} lap={currentLap}");
            }
        }
    }

    private void ApplyDriftCorrectionAndClose()
    {
        if (_rawWaypoints.Count < 10) return;

        var first = _rawWaypoints[0];
        var last = _rawWaypoints[^1];

        float driftX = first.X - last.X;
        float driftZ = first.Z - last.Z;
        float driftMag = MathF.Sqrt(driftX * driftX + driftZ * driftZ);

        Log($"  Close gap: dx={driftX:F1} dz={driftZ:F1} mag={driftMag:F1}m");

        if (driftMag > 0.1f && driftMag < 100f)
        {
            int blendCount = Math.Min(80, _rawWaypoints.Count / 4);
            for (int i = 0; i < blendCount; i++)
            {
                int idx = _rawWaypoints.Count - blendCount + i;
                float t = (float)i / blendCount;
                float alpha = t * t * (3f - 2f * t);
                _rawWaypoints[idx] = new TrackWaypoint(
                    _rawWaypoints[idx].X + driftX * alpha,
                    _rawWaypoints[idx].Z + driftZ * alpha,
                    _rawWaypoints[idx].Y,
                    _rawWaypoints[idx].Npos);
            }
        }
    }

    private bool FinalizeMap()
    {
        if (_rawWaypoints.Count < 10) return false;

        var map = new TrackMap
        {
            Waypoints = new List<TrackWaypoint>(_rawWaypoints),
            RecordedAt = DateTime.UtcNow
        };
        map.GetCumulativeDistances();

        _currentMap = map;
        _hasCompleteMap = true;
        _recording = false;

        Log($"  Finalized: {map.Waypoints.Count} pts, length={map.TrackLengthM:F1}m");
        Log($"  First: ({map.Waypoints[0].X:F1},{map.Waypoints[0].Z:F1})  Last: ({map.Waypoints[^1].X:F1},{map.Waypoints[^1].Z:F1})");
        float closeGap = map.Waypoints[^1].DistanceTo2D(map.Waypoints[0]);
        Log($"  Close gap (last->first): {closeGap:F1}m");

        return true;
    }

    private static List<TrackWaypoint> DouglasPeuckerSimplify(List<TrackWaypoint> points, float tolerance)
    {
        if (points.Count <= 2) return new List<TrackWaypoint>(points);

        float maxDist = 0f;
        int maxIndex = 0;

        var first = points[0];
        var last = points[^1];

        for (int i = 1; i < points.Count - 1; i++)
        {
            float d = PointLineDistance(points[i], first, last);
            if (d > maxDist)
            {
                maxDist = d;
                maxIndex = i;
            }
        }

        if (maxDist > tolerance)
        {
            var left = DouglasPeuckerSimplify(points[..maxIndex], tolerance);
            var right = DouglasPeuckerSimplify(points[maxIndex..], tolerance);

            var result = new List<TrackWaypoint>(left.Count + right.Count - 1);
            result.AddRange(left);
            result.AddRange(right[1..]);
            return result;
        }

        return new List<TrackWaypoint> { first, last };
    }

    private static float PointLineDistance(TrackWaypoint p, TrackWaypoint a, TrackWaypoint b)
    {
        float dx = b.X - a.X;
        float dz = b.Z - a.Z;
        float lenSq = dx * dx + dz * dz;

        if (lenSq < 0.0001f) return p.DistanceTo2D(a);

        float t = Math.Clamp(((p.X - a.X) * dx + (p.Z - a.Z) * dz) / lenSq, 0f, 1f);
        float projX = a.X + t * dx;
        float projZ = a.Z + t * dz;
        return MathF.Sqrt((p.X - projX) * (p.X - projX) + (p.Z - projZ) * (p.Z - projZ));
    }

    public void Reset()
    {
        lock (_lock)
        {
            _rawWaypoints.Clear();
            _startPoint = null;
            _recording = false;
            _lapStarted = false;
            _hasCompleteMap = false;
            _currentMap = null;
            _totalDistance = 0f;
        }
        Log("RESET");
    }

    public void SetImportedMap(TrackMap map)
    {
        lock (_lock)
        {
            _rawWaypoints.Clear();
            _startPoint = null;
            _recording = false;
            _lapStarted = false;
            _hasCompleteMap = true;
            _currentMap = map;
            _totalDistance = map.TrackLengthM;
        }
        Log($"IMPORTED MAP: {map.Waypoints.Count} pts, {map.TrackLengthM:F0}m");
    }
}
