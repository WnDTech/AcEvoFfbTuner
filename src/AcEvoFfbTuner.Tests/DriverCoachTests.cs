using AcEvoFfbTuner.Core.TrackMapping;
using Xunit;

namespace AcEvoFfbTuner.Tests;

public class DriverCoachTests
{
    // 1000 m straight-line "lap" of 200 waypoints (5 m apart) with one corner at 500 m.
    private static TrackMap Map()
    {
        var map = new TrackMap { TrackName = "Test", TrackLengthM = 1000f };
        for (int i = 0; i < 200; i++) map.Waypoints.Add(new TrackWaypoint(i * 5f, 0f, 0f, i / 200f));
        map.Corners = new List<TrackCorner>
        {
            new() { CornerNumber = 1, StartWaypointIndex = 90, ApexWaypointIndex = 100, EndWaypointIndex = 110, RealName = "Hairpin" },
        };
        return map;
    }

    private static CoachLap Lap(int no, int brakeAt, float apexSpeed, float extraTimeAfterApex = 0f)
    {
        var t = new float[200]; var v = new float[200]; var b = new float[200]; var g = new float[200];
        float time = 0;
        for (int i = 0; i < 200; i++)
        {
            v[i] = i is >= 95 and <= 105 ? apexSpeed : 150f;
            b[i] = i >= brakeAt && i < 100 ? 0.8f : 0f;
            g[i] = i >= 115 || i < brakeAt ? 1f : 0f;
            t[i] = time;
            time += 5f / (v[i] / 3.6f) + (i >= 100 ? extraTimeAfterApex / 100f : 0f);
        }
        return new CoachLap { LapNumber = no, LapTimeS = time, TimeS = t, SpeedKmh = v, Brake = b, Gas = g };
    }

    [Fact]
    public void Compare_ReportsBrakePointApexSpeedAndTimeLost()
    {
        var map = Map();
        var best = Lap(2, brakeAt: 80, apexSpeed: 90f);
        var lap = Lap(3, brakeAt: 70, apexSpeed: 80f, extraTimeAfterApex: 1.0f);

        var report = DriverCoach.Compare(map, lap, best);
        var c = Assert.Single(report.Corners);

        Assert.Equal("T1 Hairpin", c.Label);
        Assert.Equal(100f, c.BestBrakePointM, 1);   // 20 waypoints × 5 m before the apex
        Assert.Equal(150f, c.BrakePointM, 1);       // braked 50 m earlier
        Assert.Equal(80f, c.MinSpeedKmh, 1);
        Assert.Equal(90f, c.BestMinSpeedKmh, 1);
        Assert.True(c.DeltaS > 0.5f, $"delta {c.DeltaS}");
        Assert.Contains("braked 50 m earlier", c.Advice);
        Assert.Contains("10 km/h slower mid-corner", c.Advice);
    }

    [Fact]
    public void FirstLapObserved_IsNotTimed_NextCrossingLapIs()
    {
        var map = Map();
        var coach = new DriverCoach();
        CoachReport? got = null;
        coach.LapAnalysed += r => got = r;

        void Drive(int lap)
        {
            for (int i = 0; i < 200; i++) coach.Update(map, lap, i, 150f, 0f, 1f, false);
        }
        Drive(1);       // joined here — not a timed lap
        Drive(2);       // crossing 1→2 completes lap 1: must be discarded
        Assert.Null(got);
        Drive(3);       // crossing 2→3 completes lap 2: first valid lap = reference
        Assert.NotNull(got);
        Assert.Equal(2, got!.LapNumber);
        Assert.False(got.HasReference);
    }

    [Fact]
    public void PersonalBest_IsSavedAndUsedInTheNextSession()
    {
        var dir = Path.Combine(Path.GetTempPath(), "coach_pb_" + Guid.NewGuid().ToString("N"));
        try
        {
            var map = Map();
            void Drive(DriverCoach c, int lap, float speed)
            {
                for (int i = 0; i < 200; i++) c.Update(map, lap, i, speed, 0f, 1f, false, "bmw_m4_gt3");
            }

            // Session 1: out lap, then one clean lap → saved as PB.
            var s1 = new DriverCoach { Store = new CoachLapStore(dir) };
            Drive(s1, 1, 150f); Drive(s1, 2, 150f); Drive(s1, 3, 150f);
            Assert.NotNull(s1.BestLap);
            var file = Path.Combine(dir, CoachLapStore.Key(map, "bmw_m4_gt3") + ".json");
            for (int i = 0; i < 50 && !File.Exists(file); i++) Thread.Sleep(20); // saved off-thread
            Assert.True(File.Exists(file));

            // Session 2: the PB is the reference from the first clean lap.
            var s2 = new DriverCoach { Store = new CoachLapStore(dir) };
            CoachReport? got = null;
            s2.LapAnalysed += r => got = r;
            Drive(s2, 1, 150f); Drive(s2, 2, 150f); Drive(s2, 3, 150f);
            Assert.NotNull(got);
            Assert.True(got!.HasReference);
            Assert.True(got.BestFromEarlierSession);

            // Another car: no PB.
            Assert.Null(new CoachLapStore(dir).Load(map, "other_car"));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    // Same as Lap() plus steering / yaw: turning (yaw 0.3 rad/s) from waypoint 60 to 140.
    private static CoachLap GripLap(int no, float steer, int counterFrom = -1, int counterTo = -1)
    {
        var l = Lap(no, brakeAt: 80, apexSpeed: 100f);
        var s = new float[200]; var y = new float[200];
        for (int i = 0; i < 200; i++)
        {
            bool turning = i is >= 60 and <= 140;
            y[i] = turning ? 0.3f : 0.0f;
            s[i] = turning ? steer : 0f;
            if (i >= counterFrom && i <= counterTo) s[i] = -0.05f; // opposite lock
        }
        for (int i = 0; i < 200; i++) l.SpeedKmh[i] = 100f;
        return new CoachLap { LapNumber = no, LapTimeS = l.LapTimeS, TimeS = l.TimeS, SpeedKmh = l.SpeedKmh,
            Brake = l.Brake, Gas = l.Gas, Steer = s, Yaw = y };
    }

    [Fact]
    public void Grip_FlagsUndersteer_AgainstTheBestLap()
    {
        var c = DriverCoach.Compare(Map(), GripLap(3, 0.13f), GripLap(2, 0.10f)).Corners[0];
        Assert.Equal(30f, c.UndersteerPct, 0);
        Assert.Contains("understeer: 30% more steering", c.GripNote);
        Assert.Equal("", c.OversteerPhase);
    }

    [Fact]
    public void Grip_FlagsCountersteerOnPower_WithoutAReference()
    {
        var lap = GripLap(2, 0.10f, counterFrom: 116, counterTo: 120); // after the apex, throttle on
        var c = DriverCoach.Compare(Map(), lap, lap).Corners[0];
        Assert.Equal("on power", c.OversteerPhase);
        Assert.True(float.IsNaN(c.UndersteerPct)); // no separate reference → no understeer verdict
        Assert.Contains("oversteer on power", c.GripNote);
    }

    [Fact]
    public void SameLap_HasNoAdvice()
    {
        var map = Map();
        var lap = Lap(2, 80, 90f);
        var c = DriverCoach.Compare(map, lap, lap).Corners[0];
        Assert.Equal(0f, c.DeltaS, 3);
        Assert.Equal("", c.Advice);
    }
}
