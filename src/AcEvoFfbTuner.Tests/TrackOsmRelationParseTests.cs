using AcEvoFfbTuner.Core.TrackMapping;
using FluentAssertions;
using Xunit;

namespace AcEvoFfbTuner.Tests;

public class TrackOsmRelationParseTests
{
    public TrackOsmRelationParseTests() => TrackOsmRelationService.FileLogEnabled = false;

    private static string Fixture(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));

    private static double LoopLengthM(List<TrackPoint> layout)
    {
        var proj = LocalProjection.ForLayout(layout);
        double len = 0;
        for (int i = 0; i < layout.Count; i++)
        {
            var a = proj.ToLocal(layout[i].Latitude, layout[i].Longitude);
            var b = proj.ToLocal(layout[(i + 1) % layout.Count].Latitude, layout[(i + 1) % layout.Count].Longitude);
            len += Math.Sqrt((a.east - b.east) * (a.east - b.east) + (a.north - b.north) * (a.north - b.north));
        }
        return len;
    }

    /// <summary>
    /// LMU names the layout "Circuit de la Sarthe Mulsanne"; OSM names the relation
    /// "Circuit des 24 Heures du Mans" (no shared token — the gazetteer alias bridges it)
    /// and half the lap is public road (highway=primary/tertiary), which a raceway-only
    /// walk dropped. Expect the full ~13.6 km lap, not the 7.4 km of raceway segments.
    /// </summary>
    [Theory]
    [InlineData("Circuit de la Sarthe Mulsanne")]
    [InlineData("Circuit de la Sarthe")]
    public void LeMans_RoadCourse_ParsesFullLap(string gameTrackName)
    {
        var info = TrackOsmRelationService.ParseRelationResponse(Fixture("lemans_overpass.json"), gameTrackName);

        info.Should().NotBeNull();
        info!.TrackLayout.Should().NotBeNull();
        LoopLengthM(info.TrackLayout!).Should().BeInRange(13300, 13900);
    }
}
