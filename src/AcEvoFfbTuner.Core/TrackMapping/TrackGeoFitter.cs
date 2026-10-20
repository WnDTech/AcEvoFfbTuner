using System.Text.Json;

namespace AcEvoFfbTuner.Core.TrackMapping;

/// <summary>A driven-path sample in game world meters, tagged with the game's lap fraction (0 = unknown).</summary>
public readonly record struct GeoPathSample(float X, float Z, float Npos);

/// <summary>
/// Local equirectangular frame (meters east/north) around an origin. Accurate to
/// well under a meter across a circuit-sized area; unlike raw degree math it
/// weights east-west and north-south distance equally.
/// </summary>
public readonly struct LocalProjection
{
    public const double MetersPerDegLat = 111320.0;
    public double OriginLat { get; }
    public double OriginLon { get; }
    public double MetersPerDegLon { get; }

    public LocalProjection(double originLat, double originLon)
    {
        OriginLat = originLat;
        OriginLon = originLon;
        MetersPerDegLon = MetersPerDegLat * Math.Cos(originLat * Math.PI / 180.0);
    }

    /// <summary>Projection centered on the mean of the layout points.</summary>
    public static LocalProjection ForLayout(IList<TrackPoint> layout)
    {
        double lat = 0, lon = 0;
        foreach (var p in layout) { lat += p.Latitude; lon += p.Longitude; }
        return new LocalProjection(lat / layout.Count, lon / layout.Count);
    }

    public (double east, double north) ToLocal(double lat, double lon) =>
        ((lon - OriginLon) * MetersPerDegLon, (lat - OriginLat) * MetersPerDegLat);

    public (double lat, double lon) ToGps(double east, double north) =>
        (OriginLat + north / MetersPerDegLat, OriginLon + east / MetersPerDegLon);
}

/// <summary>
/// Solved transform from a game's world (X, Z) plane to GPS for one track, plus the
/// OSM start/finish anchor. Handedness is solved, not assumed: verified 2026-10-19
/// that LMU's (X, Z) maps to (east, north) un-mirrored, while SatelliteMapService
/// assumes north = -Z for every game.
/// </summary>
public sealed class TrackGeoFit
{
    public string Game { get; set; } = "";
    public string TrackName { get; set; } = "";
    public double OriginLat { get; set; }
    public double OriginLon { get; set; }
    /// <summary>True when game (X, -Z) — not (X, Z) — is the (east, north) handedness.</summary>
    public bool Mirrored { get; set; }
    public double RotationRad { get; set; }
    public double OffsetEast { get; set; }
    public double OffsetNorth { get; set; }
    /// <summary>True when the OSM layout order opposes the driving direction.</summary>
    public bool Reversed { get; set; }
    /// <summary>OSM arc fraction (0..1) where game Npos = 0, or -1 when Npos was unusable.</summary>
    public double SfFraction { get; set; } = -1;
    public double SfSpreadM { get; set; }
    public double Pearson { get; set; }
    public double MedianResidualM { get; set; }
    public double P90ResidualM { get; set; }
    public double OtherHandResidualM { get; set; }
    public double PathLengthM { get; set; }
    public double OsmLengthM { get; set; }
    public DateTime FittedAt { get; set; }

    public (double east, double north) WorldToLocal(float x, float z)
    {
        double hx = x, hy = Mirrored ? -z : z;
        double c = Math.Cos(RotationRad), s = Math.Sin(RotationRad);
        return (c * hx - s * hy + OffsetEast, s * hx + c * hy + OffsetNorth);
    }

    public (double lat, double lon) WorldToGps(float x, float z)
    {
        var (e, n) = WorldToLocal(x, z);
        return new LocalProjection(OriginLat, OriginLon).ToGps(e, n);
    }

    public override string ToString() =>
        $"mirror={Mirrored} rot={RotationRad * 180 / Math.PI:F1}° rev={Reversed} sf={SfFraction:F4} (spread {SfSpreadM:F0} m) " +
        $"r={Pearson:F2} med={MedianResidualM:F1} m p90={P90ResidualM:F1} m otherHand={OtherHandResidualM:F1} m " +
        $"path={PathLengthM:F0}/{OsmLengthM:F0} m";
}

/// <summary>
/// Aligns a driven path (world X/Z + Npos) to the OSM reference layout:
///  1. Curvature-profile cross-correlation (rotation/translation invariant) over all
///     shifts, both traversal directions and both handedness hypotheses — a mirror
///     negates every turn, so the wrong handedness correlates poorly.
///  2. Rigid (Kabsch) fit on the matched correspondences, refined by trimmed ICP
///     against the OSM polyline. Residual = distance of the driven line to the
///     OSM centerline, so it is a direct quality measure.
///  3. Start/finish anchor from Npos: every placed sample votes sfArc = arc ∓ npos·L.
/// Works on a partial lap (≥ ~1.5 km of continuous driving); no GPS reference needed.
/// </summary>
public static class TrackGeoFitter
{
    private const double Step = 5.0;          // resample spacing (m)
    private const int Win = 6;                // heading window (samples) => 30 m
    private const double CellM = 40.0;        // ICP nearest-segment grid cell
    private const int IcpIterations = 15;
    private const double IcpTrim = 0.8;       // keep the best 80 % of pairs

    public const double MinPathM = 1500.0;
    public const double MinPearson = 0.6;
    public const double MinPearsonWhenTight = 0.25;
    public const double TightMedianResidualM = 5.0;
    public const double TightP90ResidualM = 20.0;
    public const double MaxMedianResidualM = 15.0;
    public const double MinHandSeparation = 1.6;
    public const double MaxSfSpreadM = 60.0;

    public static TrackGeoFit? Fit(IReadOnlyList<GeoPathSample> path, IList<TrackPoint> osmLayout, out string diagnostic)
    {
        diagnostic = "";
        if (osmLayout == null || osmLayout.Count < 20) { diagnostic = "osm layout too small"; return null; }
        if (path == null || path.Count < 50) { diagnostic = "path too short"; return null; }

        var proj = LocalProjection.ForLayout(osmLayout);
        var osmRaw = new List<(double x, double y)>(osmLayout.Count);
        foreach (var p in osmLayout) osmRaw.Add(proj.ToLocal(p.Latitude, p.Longitude));
        var osm = ResampleClosed(osmRaw, Step, out double osmLen);
        int N = osm.Count;
        if (N < 4 * Win || osmLen < 500) { diagnostic = "osm loop too short"; return null; }

        // Driven path, open, resampled with Npos carried along. Cap at 95 % of a lap
        // (most recent part) so the circular shift search never wraps onto itself.
        var game = ResampleOpen(path, Step, out double gameLen);
        int maxSamples = (int)(N * 0.95);
        if (game.Count > maxSamples) game = game.GetRange(game.Count - maxSamples, maxSamples);
        gameLen = (game.Count - 1) * Step;
        if (gameLen < Math.Min(MinPathM, osmLen * 0.5)) { diagnostic = $"path {gameLen:F0} m < minimum"; return null; }

        double[] oc = CurvatureClosed(osm);
        double[] gc = CurvatureOpen(game);
        int K = gc.Length;
        if (K < 4 * Win) { diagnostic = "path curvature too short"; return null; }

        // ── 1. Curvature correlation over shifts × directions × handedness ──
        double gMean = 0; foreach (var v in gc) gMean += v; gMean /= K;
        var gm = new double[K];
        double gNorm = 0;
        for (int j = 0; j < K; j++) { gm[j] = gc[j] - gMean; gNorm += gm[j] * gm[j]; }
        gNorm = Math.Sqrt(gNorm);
        if (gNorm < 1e-9) { diagnostic = "featureless path (no corners)"; return null; }

        // Prefix sums over the doubled OSM curvature for O(1) window mean/variance.
        var ps = new double[2 * N + 1];
        var ps2 = new double[2 * N + 1];
        for (int i = 0; i < 2 * N; i++)
        {
            double v = oc[i % N];
            ps[i + 1] = ps[i] + v;
            ps2[i + 1] = ps2[i] + v * v;
        }
        double WindowStd(int start) // window of K samples starting at start (mod N)
        {
            int a = ((start % N) + N) % N;
            double sum = ps[a + K] - ps[a], sum2 = ps2[a + K] - ps2[a];
            double var = sum2 - sum * sum / K;
            return var > 1e-12 ? Math.Sqrt(var) : 0;
        }

        var best = new (double r, int shift, bool rev)[] { (-2, 0, false), (-2, 0, false) }; // [0] = unmirrored, [1] = mirrored
        for (int s = 0; s < N; s++)
        {
            double dotF = 0, dotR = 0;
            for (int j = 0; j < K; j++)
            {
                dotF += gm[j] * oc[(s + j) % N];
                dotR += gm[j] * oc[((s - j) % N + N) % N];
            }
            double sdF = WindowStd(s);
            double sdR = WindowStd(s - K + 1);
            double pf = sdF > 0 ? dotF / (gNorm * sdF) : 0;
            double pr = sdR > 0 ? -dotR / (gNorm * sdR) : 0; // reversed traversal negates turns
            // Mirrored game path negates every curvature sample => Pearson flips sign.
            Consider(ref best[0], pf, s, false);
            Consider(ref best[0], pr, s, true);
            Consider(ref best[1], -pf, s, false);
            Consider(ref best[1], -pr, s, true);
        }

        // ── 2. Rigid fit + ICP for each handedness; keep the clearly better one ──
        var grid = BuildGrid(osm);
        var cand = new TrackGeoFit?[2];
        var resid = new double[] { double.MaxValue, double.MaxValue };
        for (int h = 0; h < 2; h++)
        {
            bool mirrored = h == 1;
            var (r, shift, rev) = best[h];
            if (r < 0.2) continue;
            var src = new List<(double x, double y)>(K);
            var dst = new List<(double x, double y)>(K);
            for (int j = 0; j < K; j++)
            {
                var g = Handed(game[j + Win], mirrored);
                src.Add(g);
                dst.Add(osm[rev ? ((shift - j) % N + N) % N : (shift + j) % N]);
            }
            var (th, tx, ty) = Kabsch(src, dst);

            var pts = new List<(double x, double y)>(game.Count);
            foreach (var g in game) pts.Add(Handed(g, mirrored));
            for (int it = 0; it < IcpIterations; it++)
            {
                var pairs = new List<((double x, double y) a, (double x, double y) b, double d)>(pts.Count);
                foreach (var p in pts)
                {
                    var q = Apply(p, th, tx, ty);
                    if (Nearest(grid, osm, q, out var near, out _, out double d))
                        pairs.Add((p, near, d));
                }
                if (pairs.Count < 20) break;
                pairs.Sort((u, v) => u.d.CompareTo(v.d));
                int keep = (int)(pairs.Count * IcpTrim);
                var s2 = new List<(double x, double y)>(keep);
                var d2 = new List<(double x, double y)>(keep);
                for (int i = 0; i < keep; i++) { s2.Add(pairs[i].a); d2.Add(pairs[i].b); }
                (th, tx, ty) = Kabsch(s2, d2);
            }

            // Residuals of ALL samples (unmatched ones count as far off).
            var ds = new List<double>(pts.Count);
            var arcs = new List<(double arc, float npos)>(pts.Count);
            foreach (var (p, smp) in pts.Zip(game))
            {
                var q = Apply(p, th, tx, ty);
                if (Nearest(grid, osm, q, out _, out double arc, out double d))
                {
                    ds.Add(d);
                    arcs.Add((arc, smp.npos));
                }
                else ds.Add(999);
            }
            ds.Sort();
            double med = ds[ds.Count / 2], p90 = ds[(int)(ds.Count * 0.9)];
            resid[h] = med;

            // Driving direction from the placed arc progression (more robust than the
            // correlation's direction flag once the transform is known).
            double progress = 0;
            for (int i = 1; i < arcs.Count; i++)
            {
                double da = arcs[i].arc - arcs[i - 1].arc;
                if (da > osmLen / 2) da -= osmLen;
                if (da < -osmLen / 2) da += osmLen;
                progress += da;
            }
            bool reversed = progress < 0;

            // ── 3. SF anchor: circular mean of per-sample votes ──
            double sx = 0, sy = 0; int votes = 0;
            var voteArcs = new List<double>();
            foreach (var (arc, npos) in arcs)
            {
                if (npos <= 0.01f || npos >= 0.99f) continue;
                double sf = reversed ? arc + npos * osmLen : arc - npos * osmLen;
                sf = ((sf % osmLen) + osmLen) % osmLen;
                double ang = sf / osmLen * 2 * Math.PI;
                sx += Math.Cos(ang); sy += Math.Sin(ang); votes++;
                voteArcs.Add(sf);
            }
            double sfFrac = -1, spread = 0;
            if (votes >= 50)
            {
                double meanAng = Math.Atan2(sy, sx);
                if (meanAng < 0) meanAng += 2 * Math.PI;
                double meanArc = meanAng / (2 * Math.PI) * osmLen;
                var dev = voteArcs.Select(a =>
                {
                    double d = Math.Abs(a - meanArc);
                    return Math.Min(d, osmLen - d);
                }).OrderBy(d => d).ToList();
                spread = dev[(int)(dev.Count * 0.9)];
                if (spread <= MaxSfSpreadM) sfFrac = meanArc / osmLen;
            }

            cand[h] = new TrackGeoFit
            {
                OriginLat = proj.OriginLat,
                OriginLon = proj.OriginLon,
                Mirrored = mirrored,
                RotationRad = th,
                OffsetEast = tx,
                OffsetNorth = ty,
                Reversed = reversed,
                SfFraction = sfFrac,
                SfSpreadM = spread,
                Pearson = r,
                MedianResidualM = med,
                P90ResidualM = p90,
                PathLengthM = gameLen,
                OsmLengthM = osmLen,
                FittedAt = DateTime.UtcNow,
            };
        }

        int pick = resid[0] <= resid[1] ? 0 : 1;
        var fit = cand[pick];
        double other = resid[1 - pick];
        diagnostic =
            $"unmirrored: r={best[0].r:F2} med={Fmt(resid[0])} | mirrored: r={best[1].r:F2} med={Fmt(resid[1])}";
        if (fit == null) { diagnostic += " => no candidate"; return null; }
        fit.OtherHandResidualM = other;

        // Curvature correlation only seeds the alignment; the ICP residual proves it. Long-
        // straight circuits (Le Mans: r=0.37, yet 2.3 m median over 13 km) correlate weakly,
        // so a tight residual stands in for a strong correlation.
        bool tight = fit.MedianResidualM <= TightMedianResidualM && fit.P90ResidualM <= TightP90ResidualM;
        if (fit.Pearson < MinPearson && !(tight && fit.Pearson >= MinPearsonWhenTight))
        {
            diagnostic += $" => REJECT r={fit.Pearson:F2} (med {fit.MedianResidualM:F1} m, p90 {fit.P90ResidualM:F1} m)";
            return null;
        }
        if (fit.MedianResidualM > MaxMedianResidualM) { diagnostic += $" => REJECT median>{MaxMedianResidualM} m"; return null; }
        if (other < fit.MedianResidualM * MinHandSeparation) { diagnostic += " => REJECT handedness ambiguous"; return null; }
        diagnostic += " => OK " + fit;
        return fit;
    }

    private static string Fmt(double v) => v >= double.MaxValue / 2 ? "n/a" : $"{v:F1} m";

    private static void Consider(ref (double r, int shift, bool rev) best, double r, int s, bool rev)
    {
        if (r > best.r) best = (r, s, rev);
    }

    private static (double x, double y) Handed((double x, double y, float npos) g, bool mirrored) =>
        (g.x, mirrored ? -g.y : g.y);

    private static (double x, double y) Apply((double x, double y) p, double th, double tx, double ty)
    {
        double c = Math.Cos(th), s = Math.Sin(th);
        return (c * p.x - s * p.y + tx, s * p.x + c * p.y + ty);
    }

    /// <summary>2D Kabsch: rotation + translation minimizing squared error src→dst (no scale, no reflection).</summary>
    private static (double th, double tx, double ty) Kabsch(List<(double x, double y)> src, List<(double x, double y)> dst)
    {
        int n = src.Count;
        double mx = 0, my = 0, nx = 0, ny = 0;
        for (int i = 0; i < n; i++) { mx += src[i].x; my += src[i].y; nx += dst[i].x; ny += dst[i].y; }
        mx /= n; my /= n; nx /= n; ny /= n;
        double a = 0, b = 0;
        for (int i = 0; i < n; i++)
        {
            double x = src[i].x - mx, y = src[i].y - my, u = dst[i].x - nx, v = dst[i].y - ny;
            a += x * u + y * v;
            b += x * v - y * u;
        }
        double th = Math.Atan2(b, a);
        double c = Math.Cos(th), s = Math.Sin(th);
        return (th, nx - (c * mx - s * my), ny - (s * mx + c * my));
    }

    private static List<(double x, double y)> ResampleClosed(List<(double x, double y)> pts, double step, out double total)
    {
        int m = pts.Count;
        var cum = new double[m + 1];
        for (int i = 1; i <= m; i++)
        {
            var a = pts[i - 1]; var b = pts[i % m];
            cum[i] = cum[i - 1] + Math.Sqrt((b.x - a.x) * (b.x - a.x) + (b.y - a.y) * (b.y - a.y));
        }
        total = cum[m];
        var result = new List<(double x, double y)>((int)(total / step) + 1);
        int seg = 1;
        for (double t = 0; t < total - 1e-9; t += step)
        {
            while (seg < m && cum[seg] < t) seg++;
            var a = pts[seg - 1]; var b = pts[seg % m];
            double len = cum[seg] - cum[seg - 1];
            double u = len > 1e-9 ? (t - cum[seg - 1]) / len : 0;
            result.Add((a.x + (b.x - a.x) * u, a.y + (b.y - a.y) * u));
        }
        return result;
    }

    private static List<(double x, double y, float npos)> ResampleOpen(IReadOnlyList<GeoPathSample> path, double step, out double total)
    {
        int m = path.Count;
        var cum = new double[m];
        for (int i = 1; i < m; i++)
        {
            double dx = path[i].X - path[i - 1].X, dz = path[i].Z - path[i - 1].Z;
            cum[i] = cum[i - 1] + Math.Sqrt(dx * dx + dz * dz);
        }
        total = cum[m - 1];
        var result = new List<(double x, double y, float npos)>((int)(total / step) + 1);
        int seg = 1;
        for (double t = 0; t <= total; t += step)
        {
            while (seg < m - 1 && cum[seg] < t) seg++;
            var a = path[seg - 1]; var b = path[seg];
            double len = cum[seg] - cum[seg - 1];
            double u = len > 1e-9 ? (t - cum[seg - 1]) / len : 0;
            // Npos: nearest sample's value (interpolating across an SF wrap would be wrong).
            float npos = u < 0.5 ? a.Npos : b.Npos;
            result.Add((a.X + (b.X - a.X) * u, a.Z + (b.Z - a.Z) * u, npos));
        }
        return result;
    }

    private static double Heading((double x, double y) a, (double x, double y) b) => Math.Atan2(b.y - a.y, b.x - a.x);

    private static double WrapPi(double d)
    {
        while (d > Math.PI) d -= 2 * Math.PI;
        while (d < -Math.PI) d += 2 * Math.PI;
        return d;
    }

    /// <summary>Heading change over ±Win samples, centered on each sample of the closed loop.</summary>
    private static double[] CurvatureClosed(List<(double x, double y)> pts)
    {
        int n = pts.Count;
        var c = new double[n];
        for (int i = 0; i < n; i++)
        {
            double h1 = Heading(pts[i], pts[(i + Win) % n]);
            double h0 = Heading(pts[((i - Win) % n + n) % n], pts[i]);
            c[i] = WrapPi(h1 - h0);
        }
        return c;
    }

    /// <summary>Same as CurvatureClosed for an open path; element j is centered on sample j + Win.</summary>
    private static double[] CurvatureOpen(List<(double x, double y, float npos)> pts)
    {
        int n = pts.Count - 2 * Win;
        if (n <= 0) return Array.Empty<double>();
        var c = new double[n];
        for (int j = 0; j < n; j++)
        {
            int i = j + Win;
            double h1 = Heading((pts[i].x, pts[i].y), (pts[i + Win].x, pts[i + Win].y));
            double h0 = Heading((pts[i - Win].x, pts[i - Win].y), (pts[i].x, pts[i].y));
            c[j] = WrapPi(h1 - h0);
        }
        return c;
    }

    private static Dictionary<(int, int), List<int>> BuildGrid(List<(double x, double y)> osm)
    {
        var grid = new Dictionary<(int, int), List<int>>();
        for (int k = 0; k < osm.Count; k++)
        {
            var key = ((int)Math.Floor(osm[k].x / CellM), (int)Math.Floor(osm[k].y / CellM));
            if (!grid.TryGetValue(key, out var list)) grid[key] = list = new List<int>();
            list.Add(k);
        }
        return grid;
    }

    /// <summary>Nearest point on the closed OSM polyline within ~CellM, with its arc position.</summary>
    private static bool Nearest(Dictionary<(int, int), List<int>> grid, List<(double x, double y)> osm,
        (double x, double y) q, out (double x, double y) near, out double arc, out double dist)
    {
        int n = osm.Count;
        int cx = (int)Math.Floor(q.x / CellM), cy = (int)Math.Floor(q.y / CellM);
        double bestD2 = double.MaxValue;
        near = default; arc = 0;
        for (int dx = -1; dx <= 1; dx++)
        for (int dy = -1; dy <= 1; dy++)
        {
            if (!grid.TryGetValue((cx + dx, cy + dy), out var list)) continue;
            foreach (int k in list)
            {
                // Segment k → k+1 (closed loop).
                var a = osm[k]; var b = osm[(k + 1) % n];
                double sx = b.x - a.x, sy = b.y - a.y;
                double l2 = sx * sx + sy * sy;
                double u = l2 > 1e-12 ? Math.Clamp(((q.x - a.x) * sx + (q.y - a.y) * sy) / l2, 0, 1) : 0;
                double px = a.x + sx * u, py = a.y + sy * u;
                double d2 = (q.x - px) * (q.x - px) + (q.y - py) * (q.y - py);
                // Arc: every resampled segment is Step long except the closing one.
                if (d2 < bestD2) { bestD2 = d2; near = (px, py); arc = k * Step + u * Math.Sqrt(l2); }
            }
        }
        dist = Math.Sqrt(bestD2);
        return bestD2 < double.MaxValue;
    }
}

/// <summary>Persists solved geo-fits per game + track (+ layout) next to the OSM relation cache.</summary>
public static class TrackGeoFitStore
{
    private static readonly string Dir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "AcEvoFfbTuner", "TrackData");

    private static string PathFor(string game, string trackName, string? config) =>
        Path.Combine(Dir, $"{TrackMapKeys.Sanitize(TrackMapKeys.BuildKey(trackName, config))}_{TrackMapKeys.Sanitize(game)}_geofit.json");

    public static TrackGeoFit? Load(string game, string trackName, string? config)
    {
        try
        {
            var path = PathFor(game, trackName, config);
            if (!File.Exists(path)) return null;
            return JsonSerializer.Deserialize<TrackGeoFit>(File.ReadAllText(path));
        }
        catch { return null; }
    }

    public static void Save(TrackGeoFit fit, string? config)
    {
        if (string.IsNullOrWhiteSpace(fit.TrackName) || string.IsNullOrWhiteSpace(fit.Game)) return;
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(PathFor(fit.Game, fit.TrackName, config),
                JsonSerializer.Serialize(fit, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }

    /// <summary>Fit logging goes to trackmap_debug.log (always on, low volume: one line per attempt).</summary>
    public static void Log(string msg) => TrackMapBuilder.Log("[GeoFit] " + msg);
}
