namespace AcEvoFfbTuner.Core.TrackMapping;

/// <summary>
/// Corners that stay the same lap after lap and session after session. The recorded line's
/// curvature changes every time the map is rebuilt (corners split/merge: 21 → 18 at the
/// Nürburgring GP, 2026-11-08), so corners are detected once on the downloaded layout — fixed
/// geometry — and placed onto the recorded waypoints through the geo-fit. Numbered T1.. from
/// the start/finish line in driving order (the game's Npos), so T-numbers never shift.
/// </summary>
public static class StableCorners
{
    /// <summary>Resample spacing — matches the recorded map's ~3 m waypoint spacing the
    /// analyzer's thresholds (±5-point curvature, ±10-point smoothing) were tuned on.</summary>
    public const double SpacingM = 3.0;

    /// <summary>A layout corner whose apex lands farther than this from every recorded
    /// waypoint is dropped (a pit/variant section the recorded lap never drives).</summary>
    public const double MaxPlaceM = 30.0;

    /// <summary>
    /// Corners of <paramref name="layout"/> placed onto <paramref name="recorded"/>'s waypoints.
    /// <paramref name="worldToGps"/> maps recorded world (X, Z) to GPS (the geo-fit).
    /// Returns null when the inputs can't support it (no layout, too few placed corners).
    /// </summary>
    public static List<TrackCorner>? Build(IReadOnlyList<TrackPoint> layout, TrackMap recorded,
        Func<float, float, (double lat, double lon)?> worldToGps)
    {
        if (layout.Count < 10 || recorded.Waypoints.Count < 50) return null;

        var proj = LocalProjection.ForLayout(layout.ToList());
        var layoutMap = ResampledMap(layout, proj);
        if (layoutMap == null) return null;
        var layoutCorners = TrackCornerAnalyzer.DetectCorners(layoutMap);
        if (layoutCorners.Count == 0) return null;

        // Recorded waypoints in the same local metre frame.
        var wps = recorded.Waypoints;
        var rec = new (double e, double n, bool ok)[wps.Count];
        for (int i = 0; i < wps.Count; i++)
        {
            var g = worldToGps(wps[i].X, wps[i].Z);
            if (g == null) continue;
            var (e, n) = proj.ToLocal(g.Value.lat, g.Value.lon);
            rec[i] = (e, n, true);
        }

        (int idx, double dist) Nearest(TrackWaypoint p)
        {
            int best = -1; double bestD = double.MaxValue;
            for (int i = 0; i < rec.Length; i++)
            {
                if (!rec[i].ok) continue;
                double dx = rec[i].e - p.X, dy = rec[i].n - p.Z, d = dx * dx + dy * dy;
                if (d < bestD) { bestD = d; best = i; }
            }
            return (best, Math.Sqrt(bestD));
        }

        var placed = new List<TrackCorner>();
        foreach (var lc in layoutCorners)
        {
            // Kinks in the layout's straights (polyline vertices) register as tiny "corners".
            double turn = HeadingChangeDeg(layoutMap.Waypoints, lc.StartWaypointIndex, lc.EndWaypointIndex);
            if (turn < MinTurnDeg) continue;
            lc.TotalAngleDeg = (float)turn;
            var apex = Nearest(layoutMap.Waypoints[lc.ApexWaypointIndex]);
            if (apex.idx < 0 || apex.dist > MaxPlaceM) continue;
            var start = Nearest(layoutMap.Waypoints[lc.StartWaypointIndex]);
            var end = Nearest(layoutMap.Waypoints[lc.EndWaypointIndex]);
            if (start.idx < 0 || end.idx < 0) continue;

            // The layout may run opposite to the driving direction: entry is whichever end
            // comes first along the recorded lap, measured around the apex.
            int n = wps.Count;
            int Fwd(int from, int to) => ((to - from) % n + n) % n;
            int s = start.idx, e = end.idx;
            if (Fwd(s, apex.idx) > n / 2 && Fwd(e, apex.idx) <= n / 2) (s, e) = (e, s);

            placed.Add(new TrackCorner
            {
                StartWaypointIndex = s,
                EndWaypointIndex = e,
                ApexWaypointIndex = apex.idx,
                Type = lc.Type,
                Curvature = lc.Curvature,
                TotalAngleDeg = lc.TotalAngleDeg,
                LengthM = lc.LengthM,
            });
        }
        if (placed.Count < 2) return null;

        // T1 = first corner after the start/finish line (game Npos); waypoint order otherwise.
        bool hasNpos = placed.Any(c => wps[c.ApexWaypointIndex].Npos > 0f);
        var ordered = hasNpos
            ? placed.OrderBy(c => wps[c.ApexWaypointIndex].Npos).ToList()
            : placed.OrderBy(c => c.ApexWaypointIndex).ToList();
        for (int i = 0; i < ordered.Count; i++) ordered[i].CornerNumber = i + 1;
        return ordered;
    }

    /// <summary>Corners turning less than this (entry → exit heading) are straight-line kinks.</summary>
    public const double MinTurnDeg = 12.0;

    /// <summary>
    /// Total turning through the corner: the sum of |heading change| between ±5-point chords
    /// (≈15 m) stepped every 3 points from entry to exit. Total, not net — a chicane turns
    /// left then right and nets ≈0° (the NGK-Schikane was dropped by a net measure).
    /// </summary>
    internal static double HeadingChangeDeg(List<TrackWaypoint> w, int start, int end)
    {
        int n = w.Count;
        TrackWaypoint P(int i) => w[((i % n) + n) % n];
        double H(int i) { var a = P(i - 5); var b = P(i + 5); return Math.Atan2(b.Z - a.Z, b.X - a.X); }
        int span = ((end - start) % n + n) % n;
        double total = 0, prev = H(start);
        for (int k = 3; k <= span + 2; k += 3)
        {
            double h = H(start + Math.Min(k, span));
            double d = h - prev;
            while (d > Math.PI) d -= 2 * Math.PI;
            while (d < -Math.PI) d += 2 * Math.PI;
            total += Math.Abs(d);
            prev = h;
        }
        return total * 180.0 / Math.PI;
    }

    /// <summary>The layout as a closed TrackMap resampled every <see cref="SpacingM"/> metres (X = east, Z = north).</summary>
    internal static TrackMap? ResampledMap(IReadOnlyList<TrackPoint> layout, LocalProjection proj)
    {
        var pts = layout.Select(p => proj.ToLocal(p.Latitude, p.Longitude)).ToList();
        // Drop a duplicated closing point; the analyzer treats the waypoint list as a loop.
        if (pts.Count > 2 && Math.Abs(pts[0].east - pts[^1].east) < 0.5 && Math.Abs(pts[0].north - pts[^1].north) < 0.5)
            pts.RemoveAt(pts.Count - 1);
        if (pts.Count < 10) return null;

        var outPts = new List<TrackWaypoint>();
        double carry = 0, total = 0;
        for (int i = 0; i < pts.Count; i++)
        {
            var a = pts[i];
            var b = pts[(i + 1) % pts.Count];
            double dx = b.east - a.east, dy = b.north - a.north;
            double len = Math.Sqrt(dx * dx + dy * dy);
            if (len < 1e-6) continue;
            double t = carry;
            while (t < len)
            {
                outPts.Add(new TrackWaypoint((float)(a.east + dx * t / len), (float)(a.north + dy * t / len)));
                t += SpacingM;
            }
            carry = t - len;
            total += len;
        }
        if (outPts.Count < 50) return null;
        var map = new TrackMap { Waypoints = outPts, TrackLengthM = (float)total };
        return map;
    }
}
