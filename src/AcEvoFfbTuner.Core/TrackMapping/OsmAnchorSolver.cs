using System;
using System.Collections.Generic;

namespace AcEvoFfbTuner.Core.TrackMapping;

/// <summary>
/// Solves the OSM start/finish anchor + layout direction by shape-correlating a
/// driven (recorded) lap against the OSM reference layout.
///
/// Why this exists: the Live dot rides game Npos along the OSM layout, but Npos=0
/// (game start/finish) must map to the correct point on the OSM loop, and the OSM
/// loop order may run opposite to the driving direction. The old wrap-time
/// auto-calibration derived the SF anchor via the GameToGps transform — which is
/// itself uncalibrated (rotation unknown) for games without GPS. This solver uses
/// only loop SHAPE (rotation/translation invariant), so it works with zero geo
/// reference. Same principle as industry tools (TrackTitan et al.): dots ride
/// distance fractions on a curated reference; world coords only ever BUILD maps.
/// </summary>
public sealed class OsmAnchorResult
{
    /// <summary>Fractional distance (0..1) along the OSM layout = game Npos 0.</summary>
    public double SfFraction { get; set; }

    /// <summary>True when OSM layout order opposes the driving direction.</summary>
    public bool Reversed { get; set; }

    /// <summary>Correlation margin (0..1). Higher = more distinctive match.</summary>
    public double Confidence { get; set; }
}

public static class OsmAnchorSolver
{
    private const int Samples = 256;
    private const double MinConfidence = 0.2;

    public static OsmAnchorResult? Solve(TrackMap gameMap, IList<TrackPoint> osmLayout)
    {
        if (gameMap?.Waypoints == null || gameMap.Waypoints.Count < 100) return null;
        if (osmLayout == null || osmLayout.Count < 50) return null;

        // 1. Game SF: waypoint index where recorded Npos wraps 0.99 -> 0.01.
        var gamePts = gameMap.Waypoints;
        int sfGameIdx = -1;
        for (int i = 1; i < gamePts.Count; i++)
        {
            if (gamePts[i - 1].Npos > 0.9f && gamePts[i].Npos < 0.1f && gamePts[i].Npos > 0f)
            {
                sfGameIdx = i;
                break;
            }
        }
        if (sfGameIdx < 0) return null;

        // 2. Loops as (x, y) meters. OSM via equirectangular projection.
        var gameLoop = new List<(double x, double y)>(gamePts.Count);
        foreach (var w in gamePts) gameLoop.Add((w.X, w.Z));

        double sumLat = 0, sumLon = 0;
        foreach (var p in osmLayout) { sumLat += p.Latitude; sumLon += p.Longitude; }
        double cLat = sumLat / osmLayout.Count, cLon = sumLon / osmLayout.Count;
        double kx = 111320.0 * Math.Cos(cLat * Math.PI / 180.0);
        var osmLoop = new List<(double x, double y)>(osmLayout.Count);
        foreach (var p in osmLayout) osmLoop.Add(((p.Longitude - cLon) * kx, (p.Latitude - cLat) * 111320.0));

        if (LoopLength(gameLoop) < 500 || LoopLength(osmLoop) < 500) return null;

        // 3. Resample both to N evenly-spaced points; curvature (delta-heading) signature.
        double[] gCurve = CurvatureSignature(Resample(gameLoop, Samples));
        double[] oCurve = CurvatureSignature(Resample(osmLoop, Samples));

        // 4. Pearson cross-correlation of the mean-subtracted curvature profiles
        // over all shifts × both directions. (Mean subtraction is well-defined here:
        // per-step deltas are already wrapped to [-π, π], unlike raw headings.)
        // Forward: game[i] ↔ osm[(i+s)%N].
        // Reversed: opposite traversal negates tangent deltas: game[i] ↔ -osm[(s-i)%N].
        double meanG = 0, meanO = 0;
        for (int i = 0; i < Samples; i++) { meanG += gCurve[i]; meanO += oCurve[i]; }
        meanG /= Samples; meanO /= Samples;
        double normG = 0, normO = 0;
        double[] gm = new double[Samples];
        double[] om = new double[Samples];
        for (int i = 0; i < Samples; i++)
        {
            gm[i] = gCurve[i] - meanG;
            om[i] = oCurve[i] - meanO;
            normG += gm[i] * gm[i];
            normO += om[i] * om[i];
        }
        normG = Math.Sqrt(normG);
        normO = Math.Sqrt(normO);
        if (normG < 1e-9 || normO < 1e-9) return null; // featureless loops: no unique anchor
        double denom = normG * normO;

        double[] scoresF = new double[Samples];
        double[] scoresR = new double[Samples];
        for (int s = 0; s < Samples; s++)
        {
            double sf = 0, sr = 0;
            for (int i = 0; i < Samples; i++)
            {
                double o = om[(i + s) % Samples];
                double or_ = om[((s - i) % Samples + Samples) % Samples];
                sf += gm[i] * o;
                sr += gm[i] * -or_;
            }
            scoresF[s] = sf / denom;
            scoresR[s] = sr / denom;
        }

        // Best peak, then runner-up OUTSIDE its neighborhood: adjacent shifts score
        // nearly identically by construction, so they must not set the margin.
        const int exclusion = 16;
        double best = double.NegativeInfinity;
        int bestShift = 0;
        bool bestRev = false;
        for (int s = 0; s < Samples; s++)
        {
            if (scoresF[s] > best) { best = scoresF[s]; bestShift = s; bestRev = false; }
            if (scoresR[s] > best) { best = scoresR[s]; bestShift = s; bestRev = true; }
        }
        double best2 = double.NegativeInfinity;
        for (int s = 0; s < Samples; s++)
        {
            int dist = Math.Abs(s - bestShift);
            if (dist > Samples / 2) dist = Samples - dist;
            if (dist <= exclusion) continue;
            if (scoresF[s] > best2) best2 = scoresF[s];
            if (scoresR[s] > best2) best2 = scoresR[s];
        }

        // Pearson range is [-1, 1]: aligned ≈ 0.9+, wrong shifts ≈ 0.
        double confidence = Math.Clamp(best - best2, 0, 1);
        if (confidence < MinConfidence) return null;

        // 5. Game SF arc fraction -> corresponding OSM arc fraction.
        double gameTotal = LoopLength(gameLoop);
        double gameSfDist = ArcLengthAt(gameLoop, sfGameIdx);
        double gSf = gameSfDist / gameTotal;
        int gs = (int)Math.Round(gSf * Samples) % Samples;
        int osmIdx = bestRev
            ? ((bestShift - gs) % Samples + Samples) % Samples
            : (gs + bestShift) % Samples;

        return new OsmAnchorResult
        {
            SfFraction = (double)osmIdx / Samples,
            Reversed = bestRev,
            Confidence = confidence,
        };
    }

    private static double LoopLength(List<(double x, double y)> loop)
    {
        double total = 0;
        for (int i = 1; i <= loop.Count; i++)
        {
            var a = loop[i - 1];
            var b = loop[i % loop.Count];
            double dx = b.x - a.x, dy = b.y - a.y;
            total += Math.Sqrt(dx * dx + dy * dy);
        }
        return total;
    }

    private static double ArcLengthAt(List<(double x, double y)> loop, int idx)
    {
        double total = 0;
        for (int i = 1; i <= idx && i <= loop.Count; i++)
        {
            var a = loop[i - 1];
            var b = loop[i % loop.Count];
            double dx = b.x - a.x, dy = b.y - a.y;
            total += Math.Sqrt(dx * dx + dy * dy);
        }
        return total;
    }

    private static List<(double x, double y)> Resample(List<(double x, double y)> loop, int n)
    {
        int m = loop.Count;
        var cum = new double[m + 1];
        for (int i = 1; i <= m; i++)
        {
            var a = loop[i - 1];
            var b = loop[i % m];
            double dx = b.x - a.x, dy = b.y - a.y;
            cum[i] = cum[i - 1] + Math.Sqrt(dx * dx + dy * dy);
        }
        double total = cum[m];
        var result = new List<(double x, double y)>(n);
        int seg = 1;
        for (int k = 0; k < n; k++)
        {
            double target = total * k / n;
            while (seg < m && cum[seg] < target) seg++;
            var a = loop[seg - 1];
            var b = loop[seg % m];
            double segLen = cum[seg] - cum[seg - 1];
            double t = segLen > 1e-9 ? (target - cum[seg - 1]) / segLen : 0;
            result.Add((a.x + (b.x - a.x) * t, a.y + (b.y - a.y) * t));
        }
        return result;
    }

    private static double[] CurvatureSignature(List<(double x, double y)> pts)
    {
        int n = pts.Count;
        var headings = new double[n];
        for (int i = 0; i < n; i++)
        {
            var a = pts[i];
            var b = pts[(i + 1) % n];
            headings[i] = Math.Atan2(b.y - a.y, b.x - a.x);
        }
        var curve = new double[n];
        for (int i = 0; i < n; i++)
        {
            double d = headings[i] - headings[(i - 1 + n) % n];
            while (d > Math.PI) d -= 2 * Math.PI;
            while (d < -Math.PI) d += 2 * Math.PI;
            curve[i] = d;
        }
        return curve;
    }
}
