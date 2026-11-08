using AcEvoFfbTuner.Core.TrackMapping;
using Xunit;

namespace AcEvoFfbTuner.Tests;

public class CornerNamingTests
{
    [Theory]
    [InlineData("Michael-Schumacher-S", true)]
    [InlineData("Ford-Kurve", true)]
    [InlineData("La Source", true)]
    [InlineData("Spring Corner", true)]
    [InlineData("Boxengasse an T13", false)]
    [InlineData("Anbindung zur Nordschleife", false)]
    [InlineData("Rallycross circuit turn 8a", false)]
    [InlineData("Rettifilo di partenza", false)]
    [InlineData("Hangar Straight", false)]
    [InlineData("Nürburgring Sprintstrecke", false)]
    [InlineData("Autódromo Internacional do Algarve", false)]
    [InlineData("", false)]
    public void IsCornerName_FiltersStraightsPitsAndVenues(string name, bool expected)
    {
        Assert.Equal(expected, CornerNaming.IsCornerName(name, "Nürburgring - Grand Prix"));
    }

    [Fact]
    public void Apply_MatchesByPosition_NotListOrder()
    {
        // World (x, z) metres → GPS around a fixed origin; two named corners listed in the
        // OPPOSITE order to the driven corners.
        var proj = new LocalProjection(50.0, 7.0);
        (double, double)? Project(float x, float z) => proj.ToGps(x, z);

        var wps = new List<TrackWaypoint>();
        for (int i = 0; i < 200; i++) wps.Add(new TrackWaypoint { X = i * 10f, Z = 0f });

        TrackPoint P(double e, double n) { var (lat, lon) = proj.ToGps(e, n); return new TrackPoint(lat, lon); }
        var named = new List<TrackCornerInfo>
        {
            new() { Number = 1, Name = "Far Bend", Path = new() { P(1450, 0), P(1550, 0) } },
            new() { Number = 2, Name = "Near Hairpin", Path = new() { P(250, 0), P(350, 0) } },
            new() { Number = 3, Name = "Main Straight", Path = new() { P(0, 0), P(2000, 0) } },
        };
        var corners = new List<TrackCorner>
        {
            new() { CornerNumber = 1, StartWaypointIndex = 28, ApexWaypointIndex = 30, EndWaypointIndex = 32 },
            new() { CornerNumber = 2, StartWaypointIndex = 148, ApexWaypointIndex = 150, EndWaypointIndex = 152 },
            new() { CornerNumber = 3, StartWaypointIndex = 90, ApexWaypointIndex = 100, EndWaypointIndex = 110 },
        };

        int n = CornerNaming.Apply(corners, wps, named, Project);

        Assert.Equal(2, n);
        Assert.Equal("Near Hairpin", corners[0].RealName);
        Assert.Equal("Far Bend", corners[1].RealName);
        Assert.Null(corners[2].RealName); // only a straight there — no corner name
    }
}
