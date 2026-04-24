namespace AcEvoFfbTuner.Core.TrackMapping;

public enum CornerType
{
    Straight,
    Medium,
    Sweeper,
    Hairpin,
    Chicane
}

public sealed class TrackCorner
{
    public int CornerNumber { get; set; }
    public CornerType Type { get; set; }
    public int StartWaypointIndex { get; set; }
    public int EndWaypointIndex { get; set; }
    public int ApexWaypointIndex { get; set; }
    public float EntrySpeedKmh { get; set; }
    public float Curvature { get; set; }
    public float LengthM { get; set; }
    public float TotalAngleDeg { get; set; }

    public string DisplayName => $"T{CornerNumber}";
    public string TypeName => Type.ToString();
}

public sealed class TrackCornerAnalyzer
{
    private const float CurvatureThreshold = 0.001f;
    private const int SmoothingWindow = 10;
    private const int MinCornerPoints = 5;

    public static List<TrackCorner> DetectCorners(TrackMap map)
    {
        if (map.Waypoints.Count < 20) return new();

        var curvature = ComputeCurvature(map);
        var smoothed = SmoothCurvature(curvature);
        var corners = FindCornerRegions(smoothed, map);

        ClassifyCorners(corners, map);
        ComputeCornerGeometry(corners, map);

        return corners;
    }

    private static float[] ComputeCurvature(TrackMap map)
    {
        var pts = map.Waypoints;
        int n = pts.Count;
        var curvature = new float[n];

        for (int i = 0; i < n; i++)
        {
            int prev = (i - 5 + n) % n;
            int next = (i + 5) % n;

            float ax = pts[prev].X, az = pts[prev].Z;
            float bx = pts[i].X, bz = pts[i].Z;
            float cx = pts[next].X, cz = pts[next].Z;

            float d1 = MathF.Sqrt((bx - ax) * (bx - ax) + (bz - az) * (bz - az));
            float d2 = MathF.Sqrt((cx - bx) * (cx - bx) + (cz - bz) * (cz - bz));
            float d3 = MathF.Sqrt((cx - ax) * (cx - ax) + (cz - az) * (cz - az));

            if (d1 < 0.01f || d2 < 0.01f || d3 < 0.01f)
            {
                curvature[i] = 0f;
                continue;
            }

            float area = MathF.Abs((bx - ax) * (cz - az) - (cx - ax) * (bz - az));
            float radius = (d1 * d2 * d3) / (4f * area + 0.0001f);

            float cross = (bx - ax) * (cz - az) - (cx - ax) * (bz - az);
            curvature[i] = cross > 0 ? 1f / radius : -1f / radius;
        }

        return curvature;
    }

    private static float[] SmoothCurvature(float[] curvature)
    {
        int n = curvature.Length;
        var smoothed = new float[n];

        for (int i = 0; i < n; i++)
        {
            float sum = 0f;
            int count = 0;
            for (int j = -SmoothingWindow; j <= SmoothingWindow; j++)
            {
                int idx = (i + j + n) % n;
                sum += curvature[idx];
                count++;
            }
            smoothed[i] = sum / count;
        }

        return smoothed;
    }

    private static List<TrackCorner> FindCornerRegions(float[] curvature, TrackMap map)
    {
        int n = curvature.Length;
        var regions = new List<(int start, int end, int apex)>();
        bool inCorner = false;
        int cornerStart = 0;
        float maxCurv = 0f;
        int apexIdx = 0;

        for (int i = 0; i < n; i++)
        {
            if (MathF.Abs(curvature[i]) > CurvatureThreshold)
            {
                if (!inCorner)
                {
                    cornerStart = i;
                    maxCurv = MathF.Abs(curvature[i]);
                    apexIdx = i;
                    inCorner = true;
                }
                else if (MathF.Abs(curvature[i]) > maxCurv)
                {
                    maxCurv = MathF.Abs(curvature[i]);
                    apexIdx = i;
                }
            }
            else if (inCorner)
            {
                if (i - cornerStart >= MinCornerPoints)
                {
                    regions.Add((cornerStart, i - 1, apexIdx));
                }
                inCorner = false;
            }
        }

        if (inCorner && n - cornerStart >= MinCornerPoints)
        {
            regions.Add((cornerStart, n - 1, apexIdx));
        }

        if (regions.Count > 1)
        {
            int lastEnd = regions[^1].end;
            int firstStart = regions[0].start;
            if (lastEnd > n - MinCornerPoints && firstStart < MinCornerPoints)
            {
                int mergedApex = MathF.Abs(curvature[regions[^1].apex]) > MathF.Abs(curvature[regions[0].apex])
                    ? regions[^1].apex : regions[0].apex;
                regions[0] = (regions[^1].start, regions[0].end, mergedApex);
                regions.RemoveAt(regions.Count - 1);
            }
        }

        return regions.Select((r, i) => new TrackCorner
        {
            CornerNumber = i + 1,
            StartWaypointIndex = r.start,
            EndWaypointIndex = r.end,
            ApexWaypointIndex = r.apex,
            Curvature = MathF.Abs(curvature[r.apex])
        }).ToList();
    }

    private static void ClassifyCorners(List<TrackCorner> corners, TrackMap map)
    {
        var cumDist = map.GetCumulativeDistances();

        foreach (var c in corners)
        {
            int span = c.EndWaypointIndex - c.StartWaypointIndex;
            if (span < 0) span += map.Waypoints.Count;
            span = Math.Max(span, 1);

            float avgCurv = c.Curvature;

            int prev = (c.StartWaypointIndex - 1 + map.Waypoints.Count) % map.Waypoints.Count;
            int next = (c.EndWaypointIndex + 1) % map.Waypoints.Count;
            float angle = HeadingChange(map.Waypoints[prev], map.Waypoints[c.ApexWaypointIndex], map.Waypoints[next]);
            c.TotalAngleDeg = MathF.Abs(angle) * (180f / MathF.PI);

            if (avgCurv > 0.01f || c.TotalAngleDeg > 140f)
                c.Type = CornerType.Hairpin;
            else if (avgCurv > 0.005f || c.TotalAngleDeg > 90f)
                c.Type = CornerType.Medium;
            else if (span > MinCornerPoints * 4)
                c.Type = CornerType.Sweeper;
            else
                c.Type = CornerType.Chicane;
        }
    }

    private static void ComputeCornerGeometry(List<TrackCorner> corners, TrackMap map)
    {
        var cumDist = map.GetCumulativeDistances();

        foreach (var c in corners)
        {
            float startDist = cumDist[c.StartWaypointIndex];
            float endDist = cumDist[c.EndWaypointIndex];
            c.LengthM = endDist - startDist;
            if (c.LengthM < 0) c.LengthM += map.TrackLengthM;
        }
    }

    private static float HeadingChange(TrackWaypoint a, TrackWaypoint b, TrackWaypoint c)
    {
        float angle1 = MathF.Atan2(b.Z - a.Z, b.X - a.X);
        float angle2 = MathF.Atan2(c.Z - b.Z, c.X - b.X);
        float diff = angle2 - angle1;
        if (diff > MathF.PI) diff -= 2f * MathF.PI;
        if (diff < -MathF.PI) diff += 2f * MathF.PI;
        return diff;
    }

    public static TrackCorner? FindCurrentCorner(List<TrackCorner> corners, int waypointIndex, int totalWaypoints)
    {
        foreach (var c in corners)
        {
            if (IsInRange(waypointIndex, c.StartWaypointIndex, c.EndWaypointIndex, totalWaypoints))
                return c;
        }

        foreach (var c in corners)
        {
            int distToApex = MinCircularDistance(waypointIndex, c.ApexWaypointIndex, totalWaypoints);
            if (distToApex < 20)
                return c;
        }

        return null;
    }

    private static bool IsInRange(int idx, int start, int end, int total)
    {
        if (start <= end)
            return idx >= start && idx <= end;
        return idx >= start || idx <= end;
    }

    private static int MinCircularDistance(int a, int b, int total)
    {
        int d = Math.Abs(a - b);
        return Math.Min(d, total - d);
    }
}
