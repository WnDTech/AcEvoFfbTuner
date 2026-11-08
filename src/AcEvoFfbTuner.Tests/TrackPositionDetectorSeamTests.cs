using AcEvoFfbTuner.Core.TrackMapping;
using Xunit;

namespace AcEvoFfbTuner.Tests;

public class TrackPositionDetectorSeamTests
{
    // A 1000 m circle of 300 waypoints; driving several laps must keep tracking across the
    // index seam (last waypoint → waypoint 0).
    private static TrackMap Circle()
    {
        var map = new TrackMap();
        double r = 1000 / (2 * Math.PI);
        for (int i = 0; i < 300; i++)
        {
            double a = 2 * Math.PI * i / 300;
            map.Waypoints.Add(new TrackWaypoint((float)(r * Math.Cos(a)), (float)(r * Math.Sin(a))));
        }
        map.TrackLengthM = 1000;
        return map;
    }

    [Fact]
    public void Tracks_AcrossTheSeam_ForSeveralLaps()
    {
        var map = Circle();
        var det = new TrackPositionDetector();
        det.SetMap(map);
        double r = 1000 / (2 * Math.PI);
        var seen = new HashSet<int>();

        // Start mid-lap, drive 3 laps in 1 m steps.
        for (int s = 0; s < 3000; s++)
        {
            double a = 2 * Math.PI * (150 + s * 0.3) / 300;
            var pos = det.GetPosition((float)(r * Math.Cos(a)), (float)(r * Math.Sin(a)));
            int expected = (int)Math.Round(150 + s * 0.3) % 300;
            int diff = Math.Abs(pos.NearestWaypointIndex - expected);
            Assert.True(Math.Min(diff, 300 - diff) <= 1, $"step {s}: idx {pos.NearestWaypointIndex}, expected {expected}");
            if (s >= 2000) seen.Add(pos.NearestWaypointIndex);
        }
        Assert.True(seen.Count > 250, $"third lap reached only {seen.Count} waypoints");
    }
}
