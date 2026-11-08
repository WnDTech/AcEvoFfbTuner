namespace AcEvoFfbTuner.Core.TrackMapping;

/// <summary>
/// Real corner names: where they come from (named OSM raceway ways lying on the layout) and
/// how they attach to the app's recorded corners (by position on the circuit, never by list
/// order — OSM member order is not track order, and the counts rarely agree).
/// </summary>
public static class CornerNaming
{
    // Named ways that are not corners: straights, pit lanes, link roads, other venues' tracks.
    private static readonly string[] NonCornerWords =
    {
        "straight", "rettifilo", "gerade", "ligne droite", "recta", "reta", "rechte",
        "pit", "pits", "pitlane", "boxengasse", "boxenausfahrt", "boxeneinfahrt",
        "anbindung", "connection", "connector", "link", "access", "service road", "paddock",
        "rallycross", "kart", "karting", "moto", "drift", "drag", "sprintstrecke", "test track",
        "circuit", "autodromo", "autódromo", "raceway", "speedway", "ring", "variante 24h",
    };

    /// <summary>True when <paramref name="name"/> names a corner (not a straight, pit, link or the venue).</summary>
    public static bool IsCornerName(string? name, string? trackName = null)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        var n = TrackNameMatcher.Normalize(name);
        if (n.Length < 2) return false;
        foreach (var w in NonCornerWords)
        {
            var nw = TrackNameMatcher.Normalize(w);
            // "ring" only as a whole word or suffix ("Nürburgring"), never inside "Spring".
            if (TrackNameMatcher.ContainsWords(n, nw) || (nw == "ring" && n.EndsWith("ring") && n.Length > 8))
                return false;
        }
        if (!string.IsNullOrWhiteSpace(trackName))
        {
            var t = TrackNameMatcher.Normalize(trackName);
            if (t.Length > 0 && (TrackNameMatcher.ContainsWords(t, n) || TrackNameMatcher.ContainsWords(n, t)))
                return false;
        }
        return true;
    }

    /// <summary>
    /// Corner names from the venue's named raceway ways that run along <paramref name="layout"/>.
    /// Ways with the same name merge; the result is numbered in layout order.
    /// </summary>
    public static List<TrackCornerInfo> FromWays(IReadOnlyList<TrackNetworkLoops.Way> ways,
        IReadOnlyList<TrackPoint> layout, string trackName, double onLayoutM = 20)
    {
        var byName = new Dictionary<string, List<TrackPoint>>(StringComparer.OrdinalIgnoreCase);
        foreach (var way in ways)
        {
            if (way.Geometry.Count < 2 || !IsCornerName(way.Name, trackName) || TrackNetworkLoops.IsExcluded(way)) continue;
            int near = way.Geometry.Count(p => TrackNetworkLoops.IsNear(p, layout, onLayoutM));
            if (near < Math.Max(2, way.Geometry.Count * 0.6)) continue; // a parallel/foreign way
            if (!byName.TryGetValue(way.Name.Trim(), out var path)) byName[way.Name.Trim()] = path = new();
            path.AddRange(way.Geometry);
        }

        var proj = LocalProjection.ForLayout(layout.ToList());
        var layoutEn = layout.Select(p => proj.ToLocal(p.Latitude, p.Longitude)).ToArray();
        int LayoutIndex(TrackPoint p)
        {
            var (e, n) = proj.ToLocal(p.Latitude, p.Longitude);
            int best = 0; double bestD = double.MaxValue;
            for (int i = 0; i < layoutEn.Length; i++)
            {
                double dx = layoutEn[i].east - e, dy = layoutEn[i].north - n, d = dx * dx + dy * dy;
                if (d < bestD) { bestD = d; best = i; }
            }
            return best;
        }

        var result = byName
            .Select(kv => (name: kv.Key, path: kv.Value, mid: kv.Value[kv.Value.Count / 2]))
            .Select(x => (x.name, x.path, x.mid, idx: LayoutIndex(x.mid)))
            .OrderBy(x => x.idx)
            .Select((x, i) => new TrackCornerInfo
            {
                Number = i + 1,
                Name = x.name,
                Latitude = x.mid.Latitude,
                Longitude = x.mid.Longitude,
                Path = x.path,
            })
            .ToList();
        return result;
    }

    /// <summary>
    /// Sets <see cref="TrackCorner.RealName"/> on the recorded corners from the named corners whose
    /// path (or marker) the recorded corner actually passes through. <paramref name="project"/> maps
    /// world (X, Z) to GPS — the Live Map's geo-fit. Corners with no named match get null.
    /// Returns the number of corners named.
    /// </summary>
    public static int Apply(IList<TrackCorner> corners, IList<TrackWaypoint> waypoints,
        IReadOnlyList<TrackCornerInfo> named, Func<float, float, (double lat, double lon)?> project,
        string? trackName = null, double pathMaxM = 25, double pointMaxM = 70)
    {
        var candidates = named.Where(c => IsCornerName(c.Name, trackName)).ToList();
        int namedCount = 0;
        foreach (var corner in corners)
        {
            corner.RealName = null;
            if (candidates.Count == 0 || waypoints.Count == 0) continue;

            // Apex first, then a spread through the corner, so a corner whose apex sits between
            // two named ways takes the one it spends most of its length on.
            var idxs = new List<int> { corner.ApexWaypointIndex };
            int start = corner.StartWaypointIndex, end = corner.EndWaypointIndex;
            if (end < start) end += waypoints.Count;
            int span = Math.Max(1, end - start);
            for (int k = 0; k <= 4; k++) idxs.Add(start + span * k / 4);

            var votes = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            foreach (var raw in idxs)
            {
                int i = ((raw % waypoints.Count) + waypoints.Count) % waypoints.Count;
                var g = project(waypoints[i].X, waypoints[i].Z);
                if (g == null) continue;
                var p = new TrackPoint(g.Value.lat, g.Value.lon);

                string? bestName = null; double bestD = double.MaxValue;
                foreach (var c in candidates)
                {
                    double d = c.Path is { Count: > 1 } path
                        ? DistanceToPolylineM(p, path)
                        : TrackNameMatcher.DistanceM(p.Latitude, p.Longitude, c.Latitude, c.Longitude);
                    double max = c.Path is { Count: > 1 } ? pathMaxM : pointMaxM;
                    if (d <= max && d < bestD) { bestD = d; bestName = c.Name; }
                }
                if (bestName == null) continue;
                double w = raw == corner.ApexWaypointIndex ? 2.0 : 1.0; // the apex counts double
                votes[bestName] = votes.GetValueOrDefault(bestName) + w;
            }

            if (votes.Count > 0)
            {
                corner.RealName = votes.OrderByDescending(v => v.Value).First().Key;
                namedCount++;
            }
        }
        return namedCount;
    }

    public static double DistanceToPolylineM(TrackPoint p, IReadOnlyList<TrackPoint> polyline)
    {
        var proj = new LocalProjection(p.Latitude, p.Longitude); // p at the origin
        var prev = proj.ToLocal(polyline[0].Latitude, polyline[0].Longitude);
        double best = Math.Sqrt(prev.east * prev.east + prev.north * prev.north);
        for (int i = 1; i < polyline.Count; i++)
        {
            var cur = proj.ToLocal(polyline[i].Latitude, polyline[i].Longitude);
            double dx = cur.east - prev.east, dy = cur.north - prev.north;
            double len2 = dx * dx + dy * dy;
            double u = len2 > 1e-9 ? Math.Clamp(-(prev.east * dx + prev.north * dy) / len2, 0, 1) : 0;
            double ex = prev.east + u * dx, ey = prev.north + u * dy;
            best = Math.Min(best, Math.Sqrt(ex * ex + ey * ey));
            prev = cur;
        }
        return best;
    }
}
