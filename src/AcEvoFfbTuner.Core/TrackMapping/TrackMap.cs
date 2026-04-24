using System.Text.Json;

namespace AcEvoFfbTuner.Core.TrackMapping;

public sealed class TrackWaypoint
{
    public float X { get; set; }
    public float Z { get; set; }
    public float Y { get; set; }
    public float Npos { get; set; }

    public TrackWaypoint() { }

    public TrackWaypoint(float x, float z, float y = 0f, float npos = 0f)
    {
        X = x;
        Z = z;
        Y = y;
        Npos = npos;
    }

    public float DistanceTo2D(TrackWaypoint other)
    {
        float dx = X - other.X;
        float dz = Z - other.Z;
        return MathF.Sqrt(dx * dx + dz * dz);
    }

    public float DistanceTo2D(float x, float z)
    {
        float dx = X - x;
        float dz = Z - z;
        return MathF.Sqrt(dx * dx + dz * dz);
    }
}

public sealed class TrackEdge
{
    public float LeftX { get; set; }
    public float LeftZ { get; set; }
    public float RightX { get; set; }
    public float RightZ { get; set; }
    public float WidthM { get; set; }
}

public sealed class PitLocation
{
    public int EntryWaypointIndex { get; set; }
    public int ExitWaypointIndex { get; set; }
    public TrackWaypoint EntryPosition { get; set; } = new();
    public TrackWaypoint ExitPosition { get; set; } = new();
    public float EntryDistanceM { get; set; }
    public float ExitDistanceM { get; set; }
    public bool IsDetected => EntryWaypointIndex > 0 && ExitWaypointIndex > 0;
}

public sealed class TrackFingerprint
{
    public float TrackLengthM { get; set; }
    public int CornerCount { get; set; }
    public float[] CornerAngles { get; set; } = Array.Empty<float>();
    public float[] CornerSpacingM { get; set; } = Array.Empty<float>();
    public int[] CornerTypes { get; set; } = Array.Empty<int>();

    public float MatchScore(TrackFingerprint other)
    {
        if (CornerCount != other.CornerCount) return 0f;
        if (CornerCount == 0) return TrackLengthM > 0 && other.TrackLengthM > 0
            ? 1f - MathF.Abs(TrackLengthM - other.TrackLengthM) / MathF.Max(TrackLengthM, other.TrackLengthM)
            : 0f;

        float lengthDiff = MathF.Abs(TrackLengthM - other.TrackLengthM) / MathF.Max(TrackLengthM, other.TrackLengthM);
        if (lengthDiff > 0.15f) return 0f;

        float lengthScore = 1f - lengthDiff;

        float angleScore = 0f;
        for (int i = 0; i < CornerAngles.Length && i < other.CornerAngles.Length; i++)
        {
            float diff = MathF.Abs(CornerAngles[i] - other.CornerAngles[i]);
            angleScore += 1f - MathF.Min(diff / 90f, 1f);
        }
        angleScore /= CornerAngles.Length;

        float spacingScore = 0f;
        for (int i = 0; i < CornerSpacingM.Length && i < other.CornerSpacingM.Length; i++)
        {
            float maxSpacing = MathF.Max(CornerSpacingM[i], other.CornerSpacingM[i]);
            if (maxSpacing < 1f) { spacingScore += 1f; continue; }
            float diff = MathF.Abs(CornerSpacingM[i] - other.CornerSpacingM[i]) / maxSpacing;
            spacingScore += 1f - MathF.Min(diff, 1f);
        }
        spacingScore /= CornerSpacingM.Length;

        return lengthScore * 0.3f + angleScore * 0.4f + spacingScore * 0.3f;
    }
}

public sealed class TrackMap
{
    public string TrackName { get; set; } = "";
    public string CarModel { get; set; } = "";
    public List<TrackWaypoint> Waypoints { get; set; } = new();
    public float TrackLengthM { get; set; }
    public DateTime RecordedAt { get; set; } = DateTime.UtcNow;

    public List<TrackCorner> Corners { get; set; } = new();
    public List<TrackSector> Sectors { get; set; } = new();
    public List<TrackEdge> TrackEdges { get; set; } = new();
    public WaypointForceSample[]? ForceHeatmap { get; set; }
    public List<LapSnapshot> LapSnapshots { get; set; } = new();
    public PitLocation PitLane { get; set; } = new();
    public TrackFingerprint Fingerprint { get; set; } = new();
    public float[] SectorNpos { get; set; } = Array.Empty<float>();

    private float[]? _cumulativeDistances;
    private object _cumLock = new();

    public float[] GetCumulativeDistances()
    {
        lock (_cumLock)
        {
            if (_cumulativeDistances != null && _cumulativeDistances.Length == Waypoints.Count)
                return _cumulativeDistances;

            _cumulativeDistances = new float[Waypoints.Count];
            if (Waypoints.Count == 0) return _cumulativeDistances;

            _cumulativeDistances[0] = 0f;
            for (int i = 1; i < Waypoints.Count; i++)
            {
                _cumulativeDistances[i] = _cumulativeDistances[i - 1] + Waypoints[i - 1].DistanceTo2D(Waypoints[i]);
            }
            TrackLengthM = _cumulativeDistances[^1] + Waypoints[^1].DistanceTo2D(Waypoints[0]);
            return _cumulativeDistances;
        }
    }

    public void InvalidateCache()
    {
        lock (_cumLock)
        {
            _cumulativeDistances = null;
        }
    }

    public void Analyze()
    {
        InvalidateCache();
        GetCumulativeDistances();

        Corners = TrackCornerAnalyzer.DetectCorners(this);
        Sectors = BuildSectors(Corners);
        EstimateEdges();
        BuildFingerprint();
    }

    public void BuildSectorsFromTransitions(List<(int sector, float npos)> transitions)
    {
        if (transitions.Count == 0 || Waypoints.Count < 10) return;

        var realSectors = transitions
            .Where(t => t.sector >= 1)
            .GroupBy(t => t.sector)
            .Select(g => (sector: g.Key, npos: g.First().npos))
            .OrderBy(s => s.npos)
            .ToList();

        if (realSectors.Count < 2)
        {
            Log($"BuildSectorsFromTransitions: only {realSectors.Count} real sectors (need ≥2), keeping defaults");
            return;
        }

        var cumDist = GetCumulativeDistances();

        var sectorStarts = realSectors;

        SectorNpos = sectorStarts.Select(s => s.npos).ToArray();

        var splitWaypointIndices = new List<int>();
        foreach (var ss in sectorStarts)
        {
            float targetDist = ss.npos * TrackLengthM;
            int bestIdx = 0;
            float bestDelta = float.MaxValue;
            for (int i = 0; i < cumDist.Length; i++)
            {
                float delta = MathF.Abs(cumDist[i] - targetDist);
                if (delta < bestDelta) { bestDelta = delta; bestIdx = i; }
            }
            splitWaypointIndices.Add(bestIdx);
        }

        splitWaypointIndices.Sort();
        splitWaypointIndices = splitWaypointIndices.Distinct().ToList();

        var sectors = new List<TrackSector>();
        int sectorNum = 1;

        var allSplits = new List<int> { 0 };
        allSplits.AddRange(splitWaypointIndices);
        allSplits.Sort();

        for (int i = 0; i < allSplits.Count; i++)
        {
            int start = allSplits[i];
            int end = i + 1 < allSplits.Count ? allSplits[i + 1] - 1 : Waypoints.Count - 1;
            if (end < start) continue;

            float startDist = cumDist[start];
            float endDist = cumDist[end];
            float len = endDist - startDist;
            if (len < 0) len += TrackLengthM;

            sectors.Add(new TrackSector
            {
                SectorNumber = sectorNum++,
                StartWaypointIndex = start,
                EndWaypointIndex = end,
                StartDistanceM = startDist,
                EndDistanceM = endDist,
                LengthM = len
            });
        }

        if (sectors.Count > 0)
        {
            Sectors = sectors;
            Log($"Sectors from game data: {sectors.Count} sectors at npos [{string.Join(", ", SectorNpos.Select(n => n.ToString("F3")))}]");
        }
    }

    private void BuildFingerprint()
    {
        var fp = Fingerprint;
        fp.TrackLengthM = TrackLengthM;
        fp.CornerCount = Corners.Count;

        if (Corners.Count > 0)
        {
            fp.CornerAngles = new float[Corners.Count];
            fp.CornerSpacingM = new float[Corners.Count];
            fp.CornerTypes = new int[Corners.Count];

            for (int i = 0; i < Corners.Count; i++)
            {
                fp.CornerAngles[i] = Corners[i].TotalAngleDeg;
                fp.CornerTypes[i] = (int)Corners[i].Type;

                if (i + 1 < Corners.Count)
                {
                    float startDist = GetCumulativeDistances()[Corners[i].ApexWaypointIndex];
                    float nextDist = GetCumulativeDistances()[Corners[i + 1].ApexWaypointIndex];
                    fp.CornerSpacingM[i] = nextDist - startDist;
                    if (fp.CornerSpacingM[i] < 0) fp.CornerSpacingM[i] += TrackLengthM;
                }
                else
                {
                    float apexDist = GetCumulativeDistances()[Corners[i].ApexWaypointIndex];
                    float firstApexDist = GetCumulativeDistances()[Corners[0].ApexWaypointIndex];
                    fp.CornerSpacingM[i] = (TrackLengthM - apexDist) + firstApexDist;
                }
            }
        }
    }

    public static TrackMap? TryAutoDetect(TrackMap recorded)
    {
        if (recorded.Fingerprint.CornerCount == 0 && recorded.TrackLengthM < 100f)
            return null;

        string dir = GetMapsDirectory();
        if (!Directory.Exists(dir)) return null;

        TrackMap? bestMatch = null;
        float bestScore = 0f;

        foreach (var file in Directory.GetFiles(dir, "*.json"))
        {
            try
            {
                var json = File.ReadAllText(file);
                var saved = JsonSerializer.Deserialize<TrackMap>(json);
                if (saved == null || saved.Waypoints.Count < 10) continue;
                if (saved.Fingerprint.CornerCount == 0 && saved.Corners.Count > 0)
                {
                    saved.GetCumulativeDistances();
                    saved.Fingerprint = new TrackFingerprint();
                    saved.BuildFingerprint();
                }

                float score = recorded.Fingerprint.MatchScore(saved.Fingerprint);
                if (score > bestScore)
                {
                    bestScore = score;
                    bestMatch = saved;
                }
            }
            catch { }
        }

        if (bestScore > 0.7f && bestMatch != null)
            return bestMatch;

        return null;
    }

    public void SetPitLocation(int entryWaypointIdx, int exitWaypointIdx)
    {
        var cumDist = GetCumulativeDistances();
        if (entryWaypointIdx < 0 || entryWaypointIdx >= Waypoints.Count) return;
        if (exitWaypointIdx < 0 || exitWaypointIdx >= Waypoints.Count) return;

        PitLane.EntryWaypointIndex = entryWaypointIdx;
        PitLane.ExitWaypointIndex = exitWaypointIdx;
        PitLane.EntryPosition = Waypoints[entryWaypointIdx];
        PitLane.ExitPosition = Waypoints[exitWaypointIdx];
        PitLane.EntryDistanceM = cumDist[entryWaypointIdx];
        PitLane.ExitDistanceM = cumDist[exitWaypointIdx];
    }

    private List<TrackSector> BuildSectors(List<TrackCorner> corners)
    {
        var cumDist = GetCumulativeDistances();
        var sectorSplits = DetectSectorSplitsFromNpos();

        if (sectorSplits.Count == 0)
        {
            sectorSplits = DefaultSectorSplits();
        }

        var sectors = new List<TrackSector>();
        int sectorNum = 1;

        var allSplits = new List<int> { 0 };
        allSplits.AddRange(sectorSplits);
        allSplits.Sort();

        for (int i = 0; i < allSplits.Count; i++)
        {
            int start = allSplits[i];
            int end = i + 1 < allSplits.Count ? allSplits[i + 1] - 1 : Waypoints.Count - 1;
            if (end < 0) end = 0;
            if (start > end && i + 1 < allSplits.Count) continue;

            float startDist = cumDist[start];
            float endDist = cumDist[end];
            float len = endDist - startDist;
            if (len < 0) len += TrackLengthM;

            var nearestCorner = corners.FirstOrDefault(c =>
                c.StartWaypointIndex >= start && c.StartWaypointIndex <= start + 30);

            sectors.Add(new TrackSector
            {
                SectorNumber = sectorNum++,
                StartWaypointIndex = start,
                EndWaypointIndex = end,
                StartDistanceM = startDist,
                EndDistanceM = endDist,
                LengthM = len,
                StartCornerName = nearestCorner?.DisplayName ?? ""
            });
        }

        if (sectors.Count == 0)
        {
            sectors.Add(new TrackSector
            {
                SectorNumber = 1,
                StartWaypointIndex = 0,
                EndWaypointIndex = Waypoints.Count - 1,
                LengthM = TrackLengthM
            });
        }

        return sectors;
    }

    private List<int> DetectSectorSplitsFromNpos()
    {
        var splits = new List<int>();
        if (Waypoints.Count < 50) return splits;

        for (int i = 1; i < Waypoints.Count; i++)
        {
            float prev = Waypoints[i - 1].Npos;
            float cur = Waypoints[i].Npos;

            if (prev > 0.01f && cur > 0.01f && prev > cur)
            {
                float jump = prev - cur;
                if (jump > 0.15f)
                {
                    splits.Add(i);
                }
            }
        }

        if (splits.Count > 0)
        {
            Log($"Sector splits from Npos: [{string.Join(", ", splits)}]");
        }

        return splits;
    }

    private List<int> DefaultSectorSplits()
    {
        var cumDist = GetCumulativeDistances();
        float third = TrackLengthM / 3f;
        var splits = new List<int>();

        for (int s = 1; s <= 2; s++)
        {
            float targetDist = third * s;
            int bestIdx = 0;
            float bestDelta = float.MaxValue;
            for (int i = 0; i < cumDist.Length; i++)
            {
                float delta = MathF.Abs(cumDist[i] - targetDist);
                if (delta < bestDelta)
                {
                    bestDelta = delta;
                    bestIdx = i;
                }
            }
            splits.Add(bestIdx);
        }

        Log($"Default sector splits (thirds): [{string.Join(", ", splits)}] at dist {third:F0}m, {third * 2:F0}m");
        return splits;
    }

    private static void Log(string msg)
    {
        try
        {
            var logPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "AcEvoFfbTuner", "trackmap_debug.log");
            Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
            File.AppendAllText(logPath, $"[{DateTime.Now:HH:mm:ss.fff}] {msg}\n");
        }
        catch { }
    }

    public void UpdateSectorStats(TrackForceHeatmap heatmap)
    {
        var snapshot = heatmap.GetSnapshot();
        if (snapshot == null) return;

        var cumDist = GetCumulativeDistances();

        foreach (var sector in Sectors)
        {
            sector._sumForce = 0;
            sector._sumMz = 0;
            sector._sumSpeed = 0;
            sector._clipCount = 0;
            sector._peakForce = 0;
            sector._peakMz = 0;
            sector.SampleCount = 0;

            int start = sector.StartWaypointIndex;
            int end = sector.EndWaypointIndex;
            int count = end >= start ? end - start + 1 : (Waypoints.Count - start) + end + 1;

            for (int i = 0; i < count; i++)
            {
                int idx = (start + i) % Waypoints.Count;
                if (idx < snapshot.Length && snapshot[idx].SampleCount > 0)
                {
                    var s = snapshot[idx];
                    float absForce = MathF.Abs(s.OutputForce);
                    float absMz = MathF.Abs(s.MzFront);
                    sector._sumForce += absForce;
                    sector._sumMz += absMz;
                    sector._sumSpeed += s.SpeedKmh;
                    if (s.IsClipping) sector._clipCount++;
                    if (absForce > sector._peakForce) sector._peakForce = absForce;
                    if (absMz > sector._peakMz) sector._peakMz = absMz;
                    sector.SampleCount++;
                }
            }

            if (sector.SampleCount > 0)
            {
                sector.AvgOutputForce = sector._sumForce / sector.SampleCount;
                sector.AvgMzFront = sector._sumMz / sector.SampleCount;
                sector.AvgSpeedKmh = sector._sumSpeed / sector.SampleCount;
                sector.ClippingPct = (float)sector._clipCount / sector.SampleCount * 100f;
            }
            sector.PeakOutputForce = sector._peakForce;
            sector.PeakMzFront = sector._peakMz;
        }
    }

    public void AccumulateTrackEdges(float[] lateralOffsets)
    {
        if (lateralOffsets.Length != Waypoints.Count) return;

        if (TrackEdges.Count != Waypoints.Count)
        {
            TrackEdges = new List<TrackEdge>(Waypoints.Count);
            for (int i = 0; i < Waypoints.Count; i++)
                TrackEdges.Add(new TrackEdge());
        }

        for (int i = 0; i < Waypoints.Count; i++)
        {
            int prev = (i - 1 + Waypoints.Count) % Waypoints.Count;
            int next = (i + 1) % Waypoints.Count;
            float tx = Waypoints[next].X - Waypoints[prev].X;
            float tz = Waypoints[next].Z - Waypoints[prev].Z;
            float tLen = MathF.Sqrt(tx * tx + tz * tz);
            if (tLen < 0.001f) continue;

            float nx = -tz / tLen;
            float nz = tx / tLen;

            float offset = lateralOffsets[i];
            float edgeX = Waypoints[i].X + nx * offset;
            float edgeZ = Waypoints[i].Z + nz * offset;

            var edge = TrackEdges[i];
            if (offset > 0)
            {
                if (edge.LeftX == 0 && edge.LeftZ == 0)
                {
                    edge.LeftX = edgeX;
                    edge.LeftZ = edgeZ;
                }
                else
                {
                    edge.LeftX = MathF.Max(edge.LeftX, edgeX);
                    edge.LeftZ = edge.LeftZ * 0.9f + edgeZ * 0.1f;
                }
            }
            else
            {
                if (edge.RightX == 0 && edge.RightZ == 0)
                {
                    edge.RightX = edgeX;
                    edge.RightZ = edgeZ;
                }
                else
                {
                    edge.RightX = MathF.Min(edge.RightX, edgeX);
                    edge.RightZ = edge.RightZ * 0.9f + edgeZ * 0.1f;
                }
            }

            float leftDist = MathF.Sqrt((edge.LeftX - Waypoints[i].X) * (edge.LeftX - Waypoints[i].X) +
                                         (edge.LeftZ - Waypoints[i].Z) * (edge.LeftZ - Waypoints[i].Z));
            float rightDist = MathF.Sqrt((edge.RightX - Waypoints[i].X) * (edge.RightX - Waypoints[i].X) +
                                          (edge.RightZ - Waypoints[i].Z) * (edge.RightZ - Waypoints[i].Z));
            edge.WidthM = leftDist + rightDist;
        }
    }

    public void EstimateEdges(float halfWidth = 6f)
    {
        if (Waypoints.Count < 10) return;

        TrackEdges = new List<TrackEdge>(Waypoints.Count);
        for (int i = 0; i < Waypoints.Count; i++)
        {
            int prev = (i - 1 + Waypoints.Count) % Waypoints.Count;
            int next = (i + 1) % Waypoints.Count;
            float tx = Waypoints[next].X - Waypoints[prev].X;
            float tz = Waypoints[next].Z - Waypoints[prev].Z;
            float tLen = MathF.Sqrt(tx * tx + tz * tz);
            if (tLen < 0.001f)
            {
                TrackEdges.Add(new TrackEdge());
                continue;
            }

            float nx = -tz / tLen;
            float nz = tx / tLen;

            TrackEdges.Add(new TrackEdge
            {
                LeftX = Waypoints[i].X + nx * halfWidth,
                LeftZ = Waypoints[i].Z + nz * halfWidth,
                RightX = Waypoints[i].X - nx * halfWidth,
                RightZ = Waypoints[i].Z - nz * halfWidth,
                WidthM = halfWidth * 2
            });
        }
    }

    private static readonly string MapsDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "AcEvoFfbTuner", "TrackMaps");

    public static string GetMapsDirectory() => MapsDirectory;

    public void Save()
    {
        InvalidateCache();
        GetCumulativeDistances();
        Directory.CreateDirectory(MapsDirectory);
        var safeName = string.IsNullOrWhiteSpace(TrackName) ? "unknown" : string.Join("_", TrackName.Split(Path.GetInvalidFileNameChars()));
        var path = Path.Combine(MapsDirectory, $"{safeName}.json");
        var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(path, json);
    }

    public void ExportProfile(string filePath)
    {
        InvalidateCache();
        GetCumulativeDistances();
        var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(filePath, json);
    }

    public static TrackMap? Load(string trackName)
    {
        var safeName = string.IsNullOrWhiteSpace(trackName) ? "unknown" : string.Join("_", trackName.Split(Path.GetInvalidFileNameChars()));
        var path = Path.Combine(MapsDirectory, $"{safeName}.json");
        if (!File.Exists(path)) return null;
        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<TrackMap>(json);
    }

    public static TrackMap? ImportProfile(string filePath)
    {
        if (!File.Exists(filePath)) return null;
        var json = File.ReadAllText(filePath);
        return JsonSerializer.Deserialize<TrackMap>(json);
    }

    public static List<string> GetAvailableTracks()
    {
        if (!Directory.Exists(MapsDirectory)) return new();
        return Directory.GetFiles(MapsDirectory, "*.json")
            .Select(f => Path.GetFileNameWithoutExtension(f))
            .ToList();
    }
}
