namespace AcEvoFfbTuner.Core.TrackMapping;

public sealed class TrackPositionDetector
{
    private TrackMap? _map;
    private int _lastNearestIndex;
    private const int SearchWindow = 40;
    private const float DefaultOffTrackThreshold = 15f;

    public float OffTrackThresholdM { get; set; } = DefaultOffTrackThreshold;
    public bool HasMap => _map != null;

    public void SetMap(TrackMap map)
    {
        _map = map;
    }

    /// <summary>The map whose corners drive CurrentCorner (stable corners are installed here).</summary>
    public TrackMap? Map => _map;

    public void ClearMap()
    {
        _map = null;
        _lastNearestIndex = 0;
    }

    public TrackPositionResult GetPosition(float carX, float carZ)
    {
        if (_map == null || _map.Waypoints.Count < 3)
            return TrackPositionResult.Unknown;

        int nearestIdx = FindNearestIndex(carX, carZ);
        _lastNearestIndex = nearestIdx;

        var nearest = _map.Waypoints[nearestIdx];
        float distanceToTrack = nearest.DistanceTo2D(carX, carZ);

        var cumDist = _map.GetCumulativeDistances();
        float distanceAlongTrack = cumDist[nearestIdx];
        float trackLength = _map.TrackLengthM;

        float progress = trackLength > 0f ? distanceAlongTrack / trackLength : 0f;
        if (progress > 1f) progress -= 1f;
        if (progress < 0f) progress += 1f;

        int prev = nearestIdx > 0 ? nearestIdx - 1 : _map.Waypoints.Count - 1;
        int next = nearestIdx < _map.Waypoints.Count - 1 ? nearestIdx + 1 : 0;

        float tangentX = _map.Waypoints[next].X - _map.Waypoints[prev].X;
        float tangentZ = _map.Waypoints[next].Z - _map.Waypoints[prev].Z;
        float tangentLen = MathF.Sqrt(tangentX * tangentX + tangentZ * tangentZ);
        if (tangentLen > 0.001f)
        {
            tangentX /= tangentLen;
            tangentZ /= tangentLen;
        }

        float toCarX = carX - nearest.X;
        float toCarZ = carZ - nearest.Z;
        float cross = tangentX * toCarZ - tangentZ * toCarX;
        bool leftOfCenterline = cross > 0;
        float lateralOffset = leftOfCenterline ? distanceToTrack : -distanceToTrack;

        TrackCorner? currentCorner = null;
        TrackSector? currentSector = null;

        if (_map.Corners.Count > 0)
            currentCorner = TrackCornerAnalyzer.FindCurrentCorner(_map.Corners, nearestIdx, _map.Waypoints.Count);

        if (_map.Sectors.Count > 0)
            currentSector = FindCurrentSector(nearestIdx);

        return new TrackPositionResult
        {
            IsValid = true,
            IsOnTrack = distanceToTrack <= OffTrackThresholdM,
            DistanceFromCenterM = distanceToTrack,
            LateralOffsetM = lateralOffset,
            TrackProgress = progress,
            DistanceAlongTrackM = distanceAlongTrack,
            NearestWaypointIndex = nearestIdx,
            TrackLengthM = trackLength,
            CurrentCorner = currentCorner,
            CurrentSector = currentSector
        };
    }

    private TrackSector? FindCurrentSector(int waypointIndex)
    {
        if (_map == null) return null;

        foreach (var sector in _map.Sectors)
        {
            int start = sector.StartWaypointIndex;
            int end = sector.EndWaypointIndex;

            if (start <= end)
            {
                if (waypointIndex >= start && waypointIndex <= end)
                    return sector;
            }
            else
            {
                if (waypointIndex >= start || waypointIndex <= end)
                    return sector;
            }
        }

        return _map.Sectors.FirstOrDefault();
    }

    private int FindNearestIndex(float x, float z)
    {
        var waypoints = _map!.Waypoints;
        int count = waypoints.Count;
        int last = Math.Clamp(_lastNearestIndex, 0, count - 1);

        // The lap is a loop: the window WRAPS past the last waypoint. The old clamped window
        // (and its edge-only fallback) stuck at index count-1 once the car crossed the seam —
        // the detector froze there for the whole next lap (no corners, heatmap or coach data
        // after lap 1; Silverstone 2026-11-08).
        float bestDist = float.MaxValue;
        int bestIdx = last;
        int window = Math.Min(SearchWindow, count / 2);
        for (int k = -window; k <= window; k++)
        {
            int i = ((last + k) % count + count) % count;
            float d = waypoints[i].DistanceTo2D(x, z);
            if (d < bestDist)
            {
                bestDist = d;
                bestIdx = i;
            }
        }

        // Window edge hit, or the car is far from every windowed point (teleport, reset,
        // realign to a wrong spot): re-acquire with a full scan.
        int fromLast = ((bestIdx - last) % count + count) % count;
        bool atEdge = fromLast == window || fromLast == count - window;
        if (atEdge || bestDist > ReacquireM)
        {
            for (int i = 0; i < count; i++)
            {
                float d = waypoints[i].DistanceTo2D(x, z);
                if (d < bestDist)
                {
                    bestDist = d;
                    bestIdx = i;
                }
            }
        }

        return bestIdx;
    }

    /// <summary>Farther than this from the windowed best → full re-scan.</summary>
    private const float ReacquireM = 50f;
}

public sealed class TrackPositionResult
{
    public static readonly TrackPositionResult Unknown = new() { IsValid = false };

    public bool IsValid { get; init; }
    public bool IsOnTrack { get; init; }
    public float DistanceFromCenterM { get; init; }
    public float LateralOffsetM { get; init; }
    public float TrackProgress { get; init; }
    public float DistanceAlongTrackM { get; init; }
    public int NearestWaypointIndex { get; init; }
    public float TrackLengthM { get; init; }
    public TrackCorner? CurrentCorner { get; init; }
    public TrackSector? CurrentSector { get; init; }
}
