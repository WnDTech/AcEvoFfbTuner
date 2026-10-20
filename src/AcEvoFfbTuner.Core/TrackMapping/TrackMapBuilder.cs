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
    // Update runs at physics rate (sub-meter steps); a jump this large is a warp.
    private const float TeleportDistance = 50f;
    // A finished lap must close within this gap and match the official length ±15 %.
    private const float MaxCloseGap = 50f;
    private const float LengthTolerance = 0.15f;

    private static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "AcEvoFfbTuner", "trackmap_debug.log");

    private readonly object _lock = new();
    private readonly List<TrackWaypoint> _rawWaypoints = new();
    private TrackWaypoint? _startPoint;
    private bool _recording;
    private bool _lapStarted;
    private float _totalDistance;
    private float _prevNpos = -1f;
    private float _startNpos = -1f;   // Npos at the first point (-1 = no usable Npos)
    private int _wrapIndex = -1;      // first waypoint after the SF crossing (-1 = not yet)
    private float _closestToStart = float.MaxValue; // proximity close: nearest approach so far

    /// <summary>
    /// Official track length (m) from the game's static data, set by TelemetryLoop
    /// before each Update. 0 = unknown (length gate skipped).
    /// </summary>
    public float ExpectedLengthM { get; set; }

    /// <summary>
    /// True when the in-progress/completed recording was started automatically
    /// (telemetry-first auto-record). Auto-started maps are persisted to disk on
    /// completion so alignment and the map survive restarts. Manual recordings
    /// are saved explicitly by the UI instead.
    /// </summary>
    public bool AutoStarted { get; private set; }

    public bool IsRecording { get { lock (_lock) return _recording; } }
    public bool HasCompleteMap { get { lock (_lock) return _hasCompleteMap; } private set { lock (_lock) _hasCompleteMap = value; } }
    public TrackMap? CurrentMap { get { lock (_lock) return _currentMap; } private set { lock (_lock) _currentMap = value; } }
    public int WaypointCount { get { lock (_lock) return _rawWaypoints.Count; } }

    private bool _hasCompleteMap;
    private TrackMap? _currentMap;

    /// <summary>Unit tests turn this off so they don't write into the user's real trackmap_debug.log.</summary>
    internal static bool LogEnabled = true;

    internal static void Log(string msg)
    {
        if (!LogEnabled) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
            File.AppendAllText(LogPath, $"[{DateTime.Now:HH:mm:ss.fff}] {msg}\n");
        }
        catch { }
    }

    public void StartRecording(bool autoStarted = false)
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
            _prevNpos = -1f;
            _startNpos = -1f;
            _wrapIndex = -1;
            AutoStarted = autoStarted;
        }
        Log(autoStarted ? "START RECORDING (auto)" : "START RECORDING");
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

    public void Update(float carX, float carZ, float carY, float speedKmh, int currentLap, bool isOnTrack, float npos = 0f, bool isInPitLane = false)
    {
        lock (_lock)
        {
            if (!_recording) return;
            if (speedKmh < MinSpeedKmh) return;

            // Pitlane driving must not contaminate the racing-line map (pit exit
            // runs parallel to the SF straight — including it warps the loop close).
            // Npos tracking continues so an SF crossing at pit exit still closes cleanly.
            if (isInPitLane)
            {
                _prevNpos = npos;
                return;
            }

            var point = new TrackWaypoint(carX, carZ, carY, npos);

            if (!_lapStarted)
            {
                _startPoint = point;
                _rawWaypoints.Add(point);
                _lapStarted = true;
                _prevNpos = npos;
                _startNpos = IsValidNpos(npos) ? npos : -1f;
                _wrapIndex = -1;
                _closestToStart = float.MaxValue;
                Log($"FIRST POINT: ({carX:F1}, {carZ:F1}) speed={speedKmh:F1} lap={currentLap} onTrack={isOnTrack} npos={npos:F4}");
                return;
            }

            float dist = point.DistanceTo2D(_rawWaypoints[^1]);

            // Garage reset / pit-box warp / session restart: the path is no longer
            // continuous. Closing on proximity after a warp produced partial "laps".
            if (dist > TeleportDistance)
            {
                Log($"TELEPORT {dist:F0} m at {_rawWaypoints.Count} pts — restarting lap");
                RestartLap();
                return;
            }
            if (dist < MinSampleDistance) return;

            bool lapReady = _rawWaypoints.Count > MinLapWaypoints && _totalDistance > MinTrackLength;

            if (_startNpos > 0f && IsValidNpos(npos))
            {
                // Npos mode: the SF crossing (~0.99 -> ~0.01) no longer ends the lap — that
                // cut every pit-exit recording short. The lap closes when Npos returns to
                // its starting value after the crossing: one full lap from ANY start point.
                if (_wrapIndex < 0 && _prevNpos > 0.9f && npos < 0.1f)
                {
                    _wrapIndex = _rawWaypoints.Count;
                    Log($"SF CROSSING at {_wrapIndex} pts ({_prevNpos:F3} -> {npos:F3}) totalDist={_totalDistance:F1}m");
                }
                else if (_wrapIndex >= 0 && npos >= _startNpos && lapReady)
                {
                    _prevNpos = npos;
                    Log($"LAP COMPLETE via NPOS: back at {npos:F4} (start {_startNpos:F4}) pts={_rawWaypoints.Count} totalDist={_totalDistance:F1}m");
                    CloseLap();
                    return;
                }
                _prevNpos = npos;
            }
            else
            {
                // No usable Npos: close on returning to the first point — at the closest
                // approach (keep sampling while inside the radius and still approaching),
                // so the closing gap is the lateral offset, not the 50 m entry radius.
                _prevNpos = npos;
                if (_startPoint != null && lapReady)
                {
                    float distToStart = point.DistanceTo2D(_startPoint);
                    if (distToStart < LapCompleteDistance)
                    {
                        if (distToStart > _closestToStart)
                        {
                            Log($"LAP COMPLETE detected: closest approach {_closestToStart:F1}m pts={_rawWaypoints.Count} totalDist={_totalDistance:F1}m");
                            Log($"  Start: ({_startPoint.X:F1}, {_startPoint.Z:F1})");
                            Log($"  Now:   ({carX:F1}, {carZ:F1})");
                            CloseLap();
                            return;
                        }
                        _closestToStart = distToStart;
                    }
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

    private static bool IsValidNpos(float npos) => npos > 0f && npos < 1f;

    /// <summary>Discard the in-progress lap but keep recording (waits for a fresh first point).</summary>
    private void RestartLap()
    {
        _rawWaypoints.Clear();
        _startPoint = null;
        _lapStarted = false;
        _totalDistance = 0f;
        _startNpos = -1f;
        _wrapIndex = -1;
    }

    /// <summary>
    /// Gate, close and finalize the lap. A lap that does not close, or whose length is
    /// far from the official length, is rejected and recording restarts — partial
    /// maps used to be finalized and auto-saved over better ones.
    /// </summary>
    private void CloseLap()
    {
        float gap = _rawWaypoints[^1].DistanceTo2D(_rawWaypoints[0]);
        float length = _totalDistance + gap;
        float expected = ExpectedLengthM;
        bool lengthOk = expected <= 500f
            || (length >= expected * (1f - LengthTolerance) && length <= expected * (1f + LengthTolerance));
        if (gap > MaxCloseGap || !lengthOk)
        {
            Log($"LAP REJECTED: closeGap={gap:F1}m length={length:F0}m expected={expected:F0}m — restarting lap");
            RestartLap();
            return;
        }

        _recording = false;
        ApplyDriftCorrectionAndClose();

        // Start the map at the SF line (waypoint 0 = Npos ~0) so default sectors and
        // lap-relative consumers line up with the game's own lap.
        if (_wrapIndex > 0 && _wrapIndex < _rawWaypoints.Count)
        {
            var rotated = new List<TrackWaypoint>(_rawWaypoints.Count);
            rotated.AddRange(_rawWaypoints.GetRange(_wrapIndex, _rawWaypoints.Count - _wrapIndex));
            rotated.AddRange(_rawWaypoints.GetRange(0, _wrapIndex));
            _rawWaypoints.Clear();
            _rawWaypoints.AddRange(rotated);
            Log($"  Rotated to SF: waypoint 0 = former #{_wrapIndex}");
        }

        FinalizeMap();
        Log($"  After finalize: {_rawWaypoints.Count} raw, {_currentMap?.Waypoints.Count ?? 0} simplified, length={_currentMap?.TrackLengthM ?? 0:F1}m");
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
            _prevNpos = -1f;
            _startNpos = -1f;
            _wrapIndex = -1;
            AutoStarted = false;
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
            _prevNpos = -1f;
            _startNpos = -1f;
            _wrapIndex = -1;
        }
        Log($"IMPORTED MAP: {map.Waypoints.Count} pts, {map.TrackLengthM:F0}m");
    }
}
