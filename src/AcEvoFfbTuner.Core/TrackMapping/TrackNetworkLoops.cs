using System.Text;
using System.Text.Json;

namespace AcEvoFfbTuner.Core.TrackMapping;

/// <summary>
/// Circuit layouts assembled from a venue's OSM raceway network, for layout variants no OSM
/// relation or Open Racetrack DB layout describes. LMU "Bahrain Outer Circuit" (3,511 m) is the
/// case that needed it: OSM tags the "Outer Circuit" link ways, but the venue relation is the
/// 5.4 km GP loop and Open Racetrack DB only has GP / Inner / Paddock. Every simple loop of the
/// junction graph is a candidate; the game's lap length shortlists them, way names rank them,
/// and a driven lap confirms the pick by shape (<see cref="TrackGeoFitter"/>).
/// </summary>
public static class TrackNetworkLoops
{
    /// <summary>One highway=raceway way; <see cref="Nodes"/> (OSM node ids) align with <see cref="Geometry"/>.</summary>
    public sealed record Way(long Id, string Name, string Kind, IReadOnlyList<long> Nodes, IReadOnlyList<TrackPoint> Geometry);

    /// <summary>A loop through the network as a closed polyline (last point = first point).</summary>
    public sealed record Loop(List<TrackPoint> Layout, double LengthM, IReadOnlyList<string> WayNames, double Score);

    /// <summary>Loops within this fraction of the game's lap length are candidates.</summary>
    public const double LengthTolerance = 0.04;

    /// <summary>DFS edge expansions before the loop search gives up (keeps dense venues bounded).</summary>
    public const int MaxSearchSteps = 2_000_000;

    // Ranking: relative length error, plus a share-of-length penalty for unnamed ways (service
    // links, apron cut-throughs) and a bonus for ways named after the game's variant ("Outer").
    private const double UnnamedPenalty = 0.05;
    private const double VariantBonus = 0.02;

    private static readonly HashSet<string> GenericWords = new(StringComparer.Ordinal)
    {
        "circuit", "international", "raceway", "race", "racing", "track", "grand", "prix", "gp",
        "park", "speedway", "autodromo", "ring", "motor", "motorsport", "motorsports", "layout",
        "course", "full", "the", "de", "del", "della", "di", "la", "le", "du", "des", "of", "and",
        "national", "club",
    };

    /// <summary>The highway=raceway ways in an Overpass "out body geom" response (areas skipped).</summary>
    public static List<Way> ParseWays(string overpassJson)
    {
        var result = new List<Way>();
        using var doc = JsonDocument.Parse(overpassJson);
        if (!doc.RootElement.TryGetProperty("elements", out var elements)) return result;
        foreach (var el in elements.EnumerateArray())
        {
            if (!el.TryGetProperty("type", out var typeEl) || typeEl.GetString() != "way") continue;
            if (!el.TryGetProperty("nodes", out var nodesEl) || !el.TryGetProperty("geometry", out var geomEl)) continue;
            el.TryGetProperty("tags", out var tags);
            string Tag(string key) => tags.ValueKind == JsonValueKind.Object && tags.TryGetProperty(key, out var v)
                ? v.GetString() ?? "" : "";
            if (Tag("highway") != "raceway" || Tag("area") == "yes") continue; // areas outline aprons, not lines

            var nodes = new List<long>();
            foreach (var n in nodesEl.EnumerateArray()) nodes.Add(n.GetInt64());
            var geom = new List<TrackPoint>();
            bool complete = true;
            foreach (var g in geomEl.EnumerateArray())
            {
                if (g.ValueKind != JsonValueKind.Object) { complete = false; break; }
                geom.Add(new TrackPoint(g.GetProperty("lat").GetDouble(), g.GetProperty("lon").GetDouble()));
            }
            if (!complete || nodes.Count < 2 || nodes.Count != geom.Count) continue;
            result.Add(new Way(el.GetProperty("id").GetInt64(), Tag("name"),
                $"{Tag("raceway")} {Tag("service")}".Trim(), nodes, geom));
        }
        return result;
    }

    // Pit-lane names only: main straights are often called "Pit Straight" (Bathurst, Silverstone).
    private static readonly string[] PitLaneNames =
        { "pit lane", "pitlane", "pit road", "pit entry", "pit entrance", "pit exit", "pits", "boxengasse" };

    /// <summary>Ways that are never part of a lap: pit lanes, drag strips, kart and other foreign tracks.</summary>
    public static bool IsExcluded(Way way)
    {
        var name = TrackNameMatcher.Normalize(way.Name);
        if (TrackNameMatcher.Normalize(way.Kind).Split(' ').Any(w => w is "pit" or "pits" or "pitlane")) return true;
        if (PitLaneNames.Any(p => TrackNameMatcher.ContainsWords(name, p))) return true;
        if (TrackNameMatcher.ContainsWords(name, "drag") || TrackNameMatcher.ContainsWords(name, "dragstrip")) return true;
        return way.Name.Length > 0 && TrackOsmRelationService.IsForeignWay(way.Name);
    }

    /// <summary>
    /// Words of the game's track name that name the layout rather than the venue: "Bahrain Outer
    /// Circuit" → {"outer"} once the venue's own words and generic ones are removed.
    /// </summary>
    public static HashSet<string> VariantTokens(string trackName, TrackDatabaseEntry? venue)
    {
        var venueWords = new HashSet<string>(StringComparer.Ordinal);
        if (venue != null)
            foreach (var n in venue.Aliases.Append(venue.Name))
                venueWords.UnionWith(TrackNameMatcher.Normalize(n).Split(' ', StringSplitOptions.RemoveEmptyEntries));
        return TrackNameMatcher.Normalize(trackName).Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(t => t.Length >= 3 && !GenericWords.Contains(t) && !venueWords.Contains(t))
            .ToHashSet(StringComparer.Ordinal);
    }

    private sealed record Edge(int A, int B, List<TrackPoint> Points, double LengthM, Way Way);

    /// <summary>
    /// Simple loops of the junction graph within <see cref="LengthTolerance"/> of the game's lap,
    /// best first (at most <paramref name="max"/>). Excluded ways never enter the graph.
    /// </summary>
    public static List<Loop> FindLoops(IReadOnlyList<Way> ways, double targetLengthM,
        IReadOnlyCollection<string>? variantTokens = null, int max = 5)
    {
        var loops = new List<Loop>();
        if (targetLengthM <= 0) return loops;
        var usable = ways.Where(w => !IsExcluded(w)).ToList();

        // Vertices: way endpoints and nodes that more than one way passes through.
        var uses = new Dictionary<long, int>();
        foreach (var w in usable)
            for (int i = 0; i < w.Nodes.Count; i++)
                uses[w.Nodes[i]] = uses.GetValueOrDefault(w.Nodes[i]) + (i == 0 || i == w.Nodes.Count - 1 ? 2 : 1);
        var vertexOf = new Dictionary<long, int>();
        int Vertex(long node)
        {
            if (!vertexOf.TryGetValue(node, out var v)) { v = vertexOf.Count; vertexOf[node] = v; }
            return v;
        }

        var edges = new List<Edge>();
        foreach (var w in usable)
        {
            int start = 0;
            for (int i = 1; i < w.Nodes.Count; i++)
            {
                if (i < w.Nodes.Count - 1 && uses[w.Nodes[i]] < 2) continue;
                var pts = new List<TrackPoint>(i - start + 1);
                for (int k = start; k <= i; k++) pts.Add(w.Geometry[k]);
                edges.Add(new Edge(Vertex(w.Nodes[start]), Vertex(w.Nodes[i]), pts, PolylineLength(pts), w));
                start = i;
            }
        }
        int vCount = vertexOf.Count;
        var adj = new List<(int to, int edge)>[vCount];
        for (int v = 0; v < vCount; v++) adj[v] = new List<(int, int)>();
        for (int e = 0; e < edges.Count; e++)
        {
            adj[edges[e].A].Add((edges[e].B, e));
            if (edges[e].B != edges[e].A) adj[edges[e].B].Add((edges[e].A, e));
        }

        // Each simple cycle is enumerated from its lowest-index vertex only (both directions,
        // deduplicated by edge set), pruned once it outgrows the longest acceptable lap.
        double minLen = targetLengthM * (1 - LengthTolerance), maxLen = targetLengthM * (1 + LengthTolerance);
        var usedEdge = new bool[edges.Count];
        var onPath = new bool[vCount];
        var path = new List<(int edge, bool forward)>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var cycles = new List<(List<(int edge, bool forward)> seq, double len)>();
        int steps = 0;

        void Dfs(int start, int at, double len)
        {
            foreach (var (to, e) in adj[at])
            {
                if (steps++ > MaxSearchSteps) return;
                if (usedEdge[e]) continue;
                double next = len + edges[e].LengthM;
                if (next > maxLen) continue;
                bool forward = edges[e].A == at;
                if (to == start)
                {
                    if (next < minLen) continue;
                    var seq = new List<(int edge, bool forward)>(path) { (e, forward) };
                    if (seen.Add(string.Join(",", seq.Select(s => s.edge).OrderBy(x => x))))
                        cycles.Add((seq, next));
                    continue;
                }
                if (to < start || onPath[to]) continue;
                usedEdge[e] = true; onPath[to] = true; path.Add((e, forward));
                Dfs(start, to, next);
                path.RemoveAt(path.Count - 1); onPath[to] = false; usedEdge[e] = false;
            }
        }
        for (int s = 0; s < vCount && steps <= MaxSearchSteps; s++)
        {
            onPath[s] = true;
            Dfs(s, s, 0);
            onPath[s] = false;
        }

        foreach (var (seq, len) in cycles)
        {
            var pts = new List<TrackPoint>();
            double unnamed = 0, variant = 0;
            var names = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (e, forward) in seq)
            {
                var edge = edges[e];
                var ordered = forward ? edge.Points : Enumerable.Reverse(edge.Points).ToList();
                pts.AddRange(pts.Count == 0 ? ordered : ordered.Skip(1));
                var name = edge.Way.Name;
                if (string.IsNullOrWhiteSpace(name)) { unnamed += edge.LengthM; continue; }
                names.Add(name);
                if (variantTokens is { Count: > 0 })
                {
                    var norm = TrackNameMatcher.Normalize(name);
                    if (variantTokens.Any(t => TrackNameMatcher.ContainsWords(norm, t))) variant += edge.LengthM;
                }
            }
            double score = Math.Abs(len - targetLengthM) / targetLengthM
                + UnnamedPenalty * unnamed / len - VariantBonus * variant / len;
            loops.Add(new Loop(pts, len, names.ToList(), score));
        }
        loops.Sort((a, b) => a.Score.CompareTo(b.Score));
        if (loops.Count > max) loops.RemoveRange(max, loops.Count - max);
        return loops;
    }

    /// <summary>
    /// The candidate a driven lap fits best (lowest median residual among accepted fits), or
    /// null when no candidate passes the fitter's gates.
    /// </summary>
    public static Loop? PickByShape(IReadOnlyList<Loop> loops, IReadOnlyList<GeoPathSample> lap,
        out TrackGeoFit? fit, out string diagnostic)
    {
        fit = null;
        Loop? best = null;
        var sb = new StringBuilder();
        foreach (var loop in loops)
        {
            var f = TrackGeoFitter.Fit(lap, loop.Layout, out _);
            sb.Append(f == null ? $"[{loop.LengthM:F0} m: no fit] " : $"[{loop.LengthM:F0} m: ±{f.MedianResidualM:F1} m] ");
            if (f != null && (fit == null || f.MedianResidualM < fit.MedianResidualM)) { fit = f; best = loop; }
        }
        diagnostic = sb.ToString().TrimEnd();
        return best;
    }

    /// <summary>True when <paramref name="p"/> lies within <paramref name="maxM"/> of the polyline.</summary>
    public static bool IsNear(TrackPoint p, IReadOnlyList<TrackPoint> polyline, double maxM)
    {
        if (polyline.Count == 0) return false;
        var proj = new LocalProjection(p.Latitude, p.Longitude); // p at the origin
        var prev = proj.ToLocal(polyline[0].Latitude, polyline[0].Longitude);
        if (Math.Sqrt(prev.east * prev.east + prev.north * prev.north) <= maxM) return true;
        for (int i = 1; i < polyline.Count; i++)
        {
            var cur = proj.ToLocal(polyline[i].Latitude, polyline[i].Longitude);
            double dx = cur.east - prev.east, dy = cur.north - prev.north;
            double len2 = dx * dx + dy * dy;
            double u = len2 > 1e-9 ? Math.Clamp(-(prev.east * dx + prev.north * dy) / len2, 0, 1) : 0;
            double ex = prev.east + u * dx, ey = prev.north + u * dy;
            if (Math.Sqrt(ex * ex + ey * ey) <= maxM) return true;
            prev = cur;
        }
        return false;
    }

    private static double PolylineLength(IReadOnlyList<TrackPoint> pts)
    {
        double total = 0;
        for (int i = 1; i < pts.Count; i++)
            total += TrackNameMatcher.DistanceM(pts[i - 1].Latitude, pts[i - 1].Longitude, pts[i].Latitude, pts[i].Longitude);
        return total;
    }
}
