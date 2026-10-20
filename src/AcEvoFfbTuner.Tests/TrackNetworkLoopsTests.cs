using System.Globalization;
using AcEvoFfbTuner.Core.TrackMapping;
using FluentAssertions;
using Xunit;

namespace AcEvoFfbTuner.Tests;

public class TrackNetworkLoopsTests
{
    private static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", name);

    // Overpass "out body geom" of every highway=raceway way within 3.3 km of Bahrain (2026-10-20).
    private static List<TrackNetworkLoops.Way> BahrainWays() =>
        TrackNetworkLoops.ParseWays(File.ReadAllText(Fixture("bahrain_raceway_network.json")));

    // One LMU lap of "Bahrain Outer Circuit" recorded by TrackMapBuilder (scoring lap length 3,511 m).
    private static List<GeoPathSample> BahrainOuterLap() =>
        File.ReadLines(Fixture("bahrain_outer_lmu_lap.csv"))
            .Where(l => l.Length > 0 && l[0] != '#')
            .Select(l => l.Split(','))
            .Select(p => new GeoPathSample(
                float.Parse(p[0], CultureInfo.InvariantCulture),
                float.Parse(p[1], CultureInfo.InvariantCulture),
                float.Parse(p[2], CultureInfo.InvariantCulture)))
            .ToList();

    private static readonly string[] OuterLoopNames = { "Grand Prix Circuit", "Outer Circuit" };

    [Fact]
    public void ParseWays_ReadsRacewayWaysWithNodeIds()
    {
        var ways = BahrainWays();
        ways.Should().HaveCount(27);
        ways.Should().OnlyContain(w => w.Nodes.Count == w.Geometry.Count && w.Nodes.Count >= 2);
        ways.Should().Contain(w => w.Name == "Outer Circuit");
    }

    [Fact]
    public void BahrainOuter_BestLoopIsGrandPrixPlusOuter()
    {
        var loops = TrackNetworkLoops.FindLoops(BahrainWays(), 3511, new HashSet<string> { "outer" });

        loops.Should().NotBeEmpty();
        loops[0].LengthM.Should().BeApproximately(3555, 25);
        loops[0].WayNames.Should().BeEquivalentTo(OuterLoopNames);
        loops[0].Layout[0].Should().BeEquivalentTo(loops[0].Layout[^1]); // closed polyline
    }

    [Fact]
    public void PitLanesAndDragStripsNeverJoinALoop()
    {
        var loops = TrackNetworkLoops.FindLoops(BahrainWays(), 3511);
        loops.SelectMany(l => l.WayNames)
            .Should().NotContain(n => n.Contains("Pit", StringComparison.OrdinalIgnoreCase)
                                   || n.Contains("Drag", StringComparison.OrdinalIgnoreCase)
                                   || n.Contains("Karting", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void BahrainOuter_RecordedLmuLapConfirmsTheLoopByShape()
    {
        var loops = TrackNetworkLoops.FindLoops(BahrainWays(), 3511, new HashSet<string> { "outer" });

        var pick = TrackNetworkLoops.PickByShape(loops, BahrainOuterLap(), out var fit, out var diag);

        pick.Should().NotBeNull(diag);
        pick!.WayNames.Should().BeEquivalentTo(OuterLoopNames, diag);
        fit!.MedianResidualM.Should().BeLessThan(8, diag);
        fit.Mirrored.Should().BeFalse("LMU world (X, Z) is (east, north)");
    }

    [Fact]
    public void GrandPrixLength_FindsTheGrandPrixLoop()
    {
        var loops = TrackNetworkLoops.FindLoops(BahrainWays(), 5412);
        loops.Should().NotBeEmpty();
        loops[0].LengthM.Should().BeApproximately(5412, 5412 * 0.02);
        loops[0].WayNames.Should().NotContain("Outer Circuit");
    }

    [Theory]
    [InlineData("Pit Lane", "", true)]
    [InlineData("", "pit_lane", true)]
    [InlineData("Drag Strip", "", true)]
    [InlineData("Bahrain International Karting Circuit CIK-FIA", "", true)]
    [InlineData("Pit Straight", "", false)]          // main straights are often named this
    [InlineData("Grand Prix Circuit", "", false)]
    [InlineData("", "", false)]
    public void IsExcluded(string name, string kind, bool expected) =>
        TrackNetworkLoops.IsExcluded(new TrackNetworkLoops.Way(1, name, kind, new long[] { 1, 2 },
            new[] { new TrackPoint(0, 0), new TrackPoint(0, 0.001) })).Should().Be(expected);

    [Fact]
    public void VariantTokens_KeepOnlyTheLayoutWord()
    {
        var venue = TrackDatabase.GetEntry("Bahrain Outer Circuit");
        TrackNetworkLoops.VariantTokens("Bahrain Outer Circuit", venue).Should().BeEquivalentTo(new[] { "outer" });
    }
}
