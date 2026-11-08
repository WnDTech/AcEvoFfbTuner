using AcEvoFfbTuner.Core.TrackMapping;
using Xunit;

namespace AcEvoFfbTuner.Tests;

public class StableCornersTests
{
    // A 600 x 300 m rounded rectangle (4 corners, r = 40 m) with small kinks on the long
    // straights, driven counter-clockwise; the start/finish line is mid-way along the bottom.
    private static List<(double e, double n)> Loop(double step)
    {
        const double w = 600, h = 300, r = 40;
        var pts = new List<(double, double)>();
        void Line(double x0, double y0, double x1, double y1)
        {
            double len = Math.Sqrt((x1 - x0) * (x1 - x0) + (y1 - y0) * (y1 - y0));
            for (double t = 0; t < len; t += step) pts.Add((x0 + (x1 - x0) * t / len, y0 + (y1 - y0) * t / len));
        }
        void Arc(double cx, double cy, double a0)
        {
            for (double a = 0; a < Math.PI / 2; a += step / r)
                pts.Add((cx + r * Math.Cos(a0 + a), cy + r * Math.Sin(a0 + a)));
        }
        Line(r, 0, w - r, 0);              Arc(w - r, r, -Math.PI / 2);
        Line(w, r, w, h - r);              Arc(w - r, h - r, 0);
        Line(w - r, h, r, h);              Arc(r, h - r, Math.PI / 2);
        Line(0, h - r, 0, r);              Arc(r, r, Math.PI);
        return pts;
    }

    [Fact]
    public void Build_FindsRealCorners_IgnoresKinks_NumbersFromStartFinish()
    {
        var proj = new LocalProjection(50.0, 7.0);

        // Downloaded layout: coarse polyline with 1° kinks every ~100 m on the straights.
        var layout = Loop(20).Select((p, i) =>
        {
            double kink = i % 5 == 0 ? 0.6 : 0; // ~0.6 m sideways jog
            var (lat, lon) = proj.ToGps(p.e, p.n + kink);
            return new TrackPoint(lat, lon);
        }).ToList();

        // Recorded lap at 3 m, world X/Z = east/north, starting mid-bottom-straight (Npos 0).
        var lap = Loop(3);
        int sf = lap.FindIndex(p => p.e >= 300 && p.n == 0);
        var ordered = lap.Skip(sf).Concat(lap.Take(sf)).ToList();
        var map = new TrackMap
        {
            Waypoints = ordered.Select((p, i) => new TrackWaypoint((float)p.e, (float)p.n, 0f, (float)i / ordered.Count)).ToList(),
        };
        (double, double)? W2G(float x, float z) => proj.ToGps(x, z);

        var corners = StableCorners.Build(layout, map, W2G);

        Assert.NotNull(corners);
        Assert.Equal(4, corners!.Count);
        Assert.Equal(new[] { 1, 2, 3, 4 }, corners.Select(c => c.CornerNumber));
        // T1 is the bottom-right corner: the first one after the start/finish line.
        var t1 = map.Waypoints[corners[0].ApexWaypointIndex];
        Assert.True(t1.X > 500 && t1.Z < 100, $"T1 apex at ({t1.X:F0}, {t1.Z:F0})");
        // Same input → same corners (no shifting between rebuilds).
        var again = StableCorners.Build(layout, map, W2G)!;
        Assert.Equal(corners.Select(c => c.ApexWaypointIndex), again.Select(c => c.ApexWaypointIndex));
    }
}
