using AcEvoFfbTuner.Core.TrackMapping;
using FluentAssertions;
using Xunit;

namespace AcEvoFfbTuner.Tests;

public class TrackMapBuilderTests
{
    // Ellipse track, SF line at angle 0, driven counter-clockwise.
    private const double A = 600, B = 350;
    private static readonly double Perimeter = ComputePerimeter();

    private static double ComputePerimeter()
    {
        double p = 0;
        var prev = Point(0);
        for (int i = 1; i <= 20000; i++)
        {
            var q = Point(2 * Math.PI * i / 20000);
            p += Math.Sqrt((q.x - prev.x) * (q.x - prev.x) + (q.z - prev.z) * (q.z - prev.z));
            prev = q;
        }
        return p;
    }

    private static (double x, double z) Point(double th) => (A * Math.Cos(th), B * Math.Sin(th));

    public TrackMapBuilderTests() => TrackMapBuilder.LogEnabled = false;

    /// <summary>Drive from angle th0 for the given number of laps in ~1 m steps.</summary>
    private static void Drive(TrackMapBuilder b, double th0, double laps, bool withNpos = true)
    {
        int steps = (int)(Perimeter * laps);
        for (int i = 0; i <= steps; i++)
        {
            double th = th0 + 2 * Math.PI * i / Perimeter;
            var (x, z) = Point(th);
            double frac = (th / (2 * Math.PI)) % 1.0;
            float npos = withNpos ? (float)Math.Max(frac, 1e-4) : 0f;
            b.Update((float)x, (float)z, 0f, 150f, 1, true, npos);
            if (b.HasCompleteMap) return;
        }
    }

    [Fact]
    public void MidLapStart_WithNpos_RecordsFullLapStartingAtSf()
    {
        var b = new TrackMapBuilder { ExpectedLengthM = (float)Perimeter };
        b.StartRecording(autoStarted: true);

        Drive(b, th0: Math.PI / 2, laps: 1.3); // pit exit a quarter lap in

        b.HasCompleteMap.Should().BeTrue();
        var map = b.CurrentMap!;
        map.TrackLengthM.Should().BeApproximately((float)Perimeter, (float)(Perimeter * 0.02));
        map.IsCompleteLap(0, out var reason).Should().BeTrue(reason);
        map.Waypoints[0].Npos.Should().BeLessThan(0.01f, "the map is rotated to start at the SF line");
    }

    [Fact]
    public void Teleport_RestartsLap_InsteadOfClosingOnProximity()
    {
        var b = new TrackMapBuilder { ExpectedLengthM = (float)Perimeter };
        b.StartRecording(autoStarted: true);

        // 40 % of a lap, then a garage reset warps the car back next to the first point.
        Drive(b, th0: 1.0, laps: 0.4, withNpos: false);
        b.HasCompleteMap.Should().BeFalse();
        Drive(b, th0: 1.0, laps: 0.2, withNpos: false);

        b.HasCompleteMap.Should().BeFalse("a warp must not 'complete' a partial lap");
        b.IsRecording.Should().BeTrue();
    }

    [Fact]
    public void NoNpos_ClosesOnProximityAfterFullLoop()
    {
        var b = new TrackMapBuilder { ExpectedLengthM = (float)Perimeter };
        b.StartRecording(autoStarted: true);

        Drive(b, th0: 2.0, laps: 1.2, withNpos: false);

        b.HasCompleteMap.Should().BeTrue();
        b.CurrentMap!.IsCompleteLap(0, out var reason).Should().BeTrue(reason);
    }

    [Fact]
    public void LapShorterThanOfficialLength_IsRejected()
    {
        // Official length says the track is twice as long: a "full loop" of this
        // ellipse cannot be the real lap (e.g. a shortcut or wrong layout).
        var b = new TrackMapBuilder { ExpectedLengthM = (float)(Perimeter * 2) };
        b.StartRecording(autoStarted: true);

        Drive(b, th0: 2.0, laps: 1.2, withNpos: false);

        b.HasCompleteMap.Should().BeFalse();
        b.IsRecording.Should().BeTrue("a rejected lap restarts recording");
    }

    [Fact]
    public void IsCompleteLap_FlagsOpenPartialMap()
    {
        var map = new TrackMap();
        for (int i = 0; i < 400; i++)
        {
            var (x, z) = Point(Math.PI * i / 400); // half the ellipse
            map.Waypoints.Add(new TrackWaypoint((float)x, (float)z, 0f, 0f));
        }
        map.GetCumulativeDistances();

        map.IsCompleteLap(0, out var reason).Should().BeFalse();
        reason.Should().Contain("not closed");
    }
}
