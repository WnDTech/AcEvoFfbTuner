using AcEvoFfbTuner.Core.TrackMapping;
using FluentAssertions;

namespace AcEvoFfbTuner.Tests;

public class OsmAnchorSolverTests
{
    // Track-like loop: base ellipse + strong localized kinks (chicanes) at
    // irregular angles so the curvature signature has a unique correlation peak.
    // Game frame: rotated 37° + translated (arbitrary game coordinates).
    private static double Bump(double a, double c, double w)
    {
        double d = Math.Abs(a - c);
        if (d > Math.PI) d = 2 * Math.PI - d;
        return Math.Exp(-d * d / (w * w));
    }

    private static void Shape(double a, out double ex, out double ez)
    {
        ex = 1000 * Math.Cos(a) + 260 * Bump(a, 1.0, 0.35) - 180 * Bump(a, 4.0, 0.25);
        ez = 620 * Math.Sin(a) + 150 * Math.Sin(3 * a) + 200 * Bump(a, 2.5, 0.30);
    }

    private static TrackMap MakeGameLoop()
    {
        var map = new TrackMap { TrackName = "Test" };
        double rot = 37 * Math.PI / 180.0;
        int n = 200;
        for (int i = 0; i < n; i++)
        {
            double a = 2 * Math.PI * i / n;
            Shape(a, out double ex, out double ez);
            double gx = 5000 + ex * Math.Cos(rot) - ez * Math.Sin(rot);
            double gz = -3000 + ex * Math.Sin(rot) + ez * Math.Cos(rot);
            // Npos wraps mid-list like real recordings: SF (Npos ~0) at shape index 0,
            // transition (0.995 -> 0.0005) at the list end.
            float npos = ((i + 1) % n) / (float)n + 0.0005f;
            map.Waypoints.Add(new TrackWaypoint((float)gx, (float)gz, 0f, npos));
        }
        return map;
    }

    // OSM loop: same shape as GPS around a centroid, starting at index offset,
    // optionally reversed.
    private static List<TrackPoint> MakeOsmLoop(int offset, bool reversed)
    {
        int n = 120;
        var pts = new List<TrackPoint>(n);
        double cLat = 45.62, cLon = 9.28;
        double kx = 111320.0 * Math.Cos(cLat * Math.PI / 180.0);
        for (int i = 0; i < n; i++)
        {
            int src = reversed ? (offset - i % n + n * 2) % n : (offset + i) % n;
            double a = 2 * Math.PI * src / n;
            Shape(a, out double ex, out double ez);
            pts.Add(new TrackPoint(cLat + ez / 111320.0, cLon + ex / kx));
        }
        return pts;
    }

    [Fact]
    public void Solve_ForwardDirection_RecoversSfFraction()
    {
        var game = MakeGameLoop();
        // OSM index 0 = game shape index 36 -> game SF (shape 0) sits at OSM fraction (120-36)/120.
        var osm = MakeOsmLoop(36, reversed: false);

        var result = OsmAnchorSolver.Solve(game, osm);

        result.Should().NotBeNull();
        result!.Reversed.Should().BeFalse();
        // Game shape idx 0 == OSM shape idx 36. OSM layout idx0 = shape 36,
        // so shape 0 is at OSM fraction (120-36)/120 = 0.70.
        result.SfFraction.Should().BeApproximately(0.70, 0.03);
        result.Confidence.Should().BeGreaterThan(0.15);
    }

    [Fact]
    public void Solve_ReversedDirection_DetectsReversal()
    {
        var game = MakeGameLoop();
        var osm = MakeOsmLoop(36, reversed: true);

        var result = OsmAnchorSolver.Solve(game, osm);

        result.Should().NotBeNull();
        result!.Reversed.Should().BeTrue();
        result.Confidence.Should().BeGreaterThan(0.15);
    }

    [Fact]
    public void Solve_TooFewPoints_ReturnsNull()
    {
        var game = new TrackMap { TrackName = "Test" };
        for (int i = 0; i < 50; i++)
            game.Waypoints.Add(new TrackWaypoint(i, 0, 0f, (float)i / 50));

        OsmAnchorSolver.Solve(game, MakeOsmLoop(0, false)).Should().BeNull();
    }

    [Fact]
    public void Solve_NoNposWrap_ReturnsNull()
    {
        var game = new TrackMap { TrackName = "Test" };
        for (int i = 0; i < 150; i++)
            game.Waypoints.Add(new TrackWaypoint(i * 10, (i % 2) * 50, 0f, 0.5f));

        OsmAnchorSolver.Solve(game, MakeOsmLoop(0, false)).Should().BeNull();
    }
}
