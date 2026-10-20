using AcEvoFfbTuner.Core.TrackMapping;
using FluentAssertions;
using Xunit;

namespace AcEvoFfbTuner.Tests;

/// <summary>Parser/selector/venue lookup against real Open Racetrack DB files (pinned commit).</summary>
public class OpenRacetrackDbTests
{
    private static readonly string Dir = Path.Combine(AppContext.BaseDirectory, "Fixtures", "ortdb", "bahrain");

    private static readonly (string id, string name)[] BahrainLayouts =
    {
        ("main", "Connected course candidate"),
        ("bahrain-international-circuit", "Bahrain International Circuit"),
        ("bahrain-international-circuit-inner", "Bahrain International Circuit Inner"),
        ("bahrain-paddock-circuit", "Bahrain Paddock Circuit"),
    };

    private static List<OrtdbLayout> LoadBahrain() => BahrainLayouts
        .Select(l => OrtdbLayouts.Parse(l.id, l.name, File.ReadAllText(Path.Combine(Dir, "layouts", l.id + ".geojson")))!)
        .ToList();

    private static OrtdbVenue Bahrain => OpenRacetrackDbIndex.FindVenue("Bahrain International Circuit", null, null)!;

    [Fact]
    public void Parse_ReadsClosedTraceAndDropsClosingRepeat()
    {
        var gp = LoadBahrain().Single(l => l.Id == "bahrain-international-circuit");
        gp.Closed.Should().BeTrue();
        gp.LengthM.Should().BeApproximately(5419.6, 0.1);
        gp.Trace.Count.Should().Be(238, "239 coordinates minus the closing repeat");
        gp.Trace[0].Latitude.Should().BeInRange(25.9, 26.1);   // [lon, lat] order honoured
        gp.Trace[0].Longitude.Should().BeInRange(50.4, 50.7);
    }

    [Theory]
    [InlineData(5412, "bahrain-international-circuit")]       // GP
    [InlineData(2550, "bahrain-international-circuit-inner")] // Inner
    [InlineData(3705, "bahrain-paddock-circuit")]             // Paddock
    [InlineData(6299, "main")]                                // Endurance-length connected course
    public void Select_ByOfficialLength(double officialM, string expectedId)
    {
        var pick = OrtdbLayouts.Select(LoadBahrain(), Bahrain, "Bahrain International Circuit", officialM,
            "bahrain-international-circuit", out var reason);
        pick.Should().NotBeNull(reason);
        pick!.Id.Should().Be(expectedId, reason);
    }

    [Fact]
    public void Select_MissingVariant_ReturnsNullInsteadOfNearestWrongLoop()
    {
        // Bahrain "Outer" (~3543 m) is not in the dataset; Paddock (3836 m) is 8 % off.
        var pick = OrtdbLayouts.Select(LoadBahrain(), Bahrain, "Bahrain International Circuit Outer", 3543,
            "bahrain-international-circuit", out var reason);
        pick.Should().BeNull();
        reason.Should().Contain("no layout within");
    }

    [Fact]
    public void Select_WithoutLength_UsesVariantName_ButNeverGuessesBetweenCourses()
    {
        var layouts = LoadBahrain();
        OrtdbLayouts.Select(layouts, Bahrain, "Bahrain International Circuit Paddock", 0, "bahrain-international-circuit", out _)!
            .Id.Should().Be("bahrain-paddock-circuit");

        // No variant word and no lap length at a 4-course venue: the default would be a guess
        // (the "Le Mans Bugatti" instead of the 24h circuit bug).
        OrtdbLayouts.Select(layouts, Bahrain, "Bahrain International Circuit", 0, "bahrain-international-circuit", out var reason)
            .Should().BeNull();
        reason.Should().Contain("not guessing");
    }

    [Fact]
    public void Select_WithoutLength_SingleCourseVenue_UsesIt()
    {
        var gp = LoadBahrain().Where(l => l.Id == "bahrain-international-circuit").ToList();
        OrtdbLayouts.Select(gp, Bahrain, "Bahrain International Circuit", 0, "bahrain-international-circuit", out _)!
            .Id.Should().Be("bahrain-international-circuit");
    }

    [Fact]
    public void FindVenue_ByNameAndByGps()
    {
        Bahrain.Should().NotBeNull();
        Bahrain.Id.Should().StartWith("bh-");

        // No usable name, GPS next to Le Mans → the Le Mans venue.
        var byGps = OpenRacetrackDbIndex.FindVenue("Some Game Track 01", 47.955, 0.212);
        byGps.Should().NotBeNull();
        byGps!.Name.Should().Contain("Le Mans");
    }
}

public class TrackNameMatcherTests
{
    [Theory]
    [InlineData("Nürburgring Nordschleife", "nurburgring nordschleife")]
    [InlineData("ks_silverstone", "ks silverstone")]
    [InlineData("  Spa-Francorchamps  ", "spa francorchamps")]
    [InlineData("Autódromo José Carlos Pace", "autodromo jose carlos pace")]
    public void Normalize(string input, string expected) =>
        TrackNameMatcher.Normalize(input).Should().Be(expected);

    [Theory]
    [InlineData("circuit de la sarthe mulsanne", "circuit de la sarthe", true)]
    [InlineData("almost speedway", "most", false)]      // not inside a word
    [InlineData("autodrom most", "most", true)]
    [InlineData("spain test", "spa", false)]
    public void ContainsWords(string haystack, string needle, bool expected) =>
        TrackNameMatcher.ContainsWords(haystack, needle).Should().Be(expected);
}
