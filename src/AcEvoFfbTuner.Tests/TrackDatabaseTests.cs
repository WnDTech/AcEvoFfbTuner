using AcEvoFfbTuner.Core.TrackMapping;
using FluentAssertions;
using Xunit;

namespace AcEvoFfbTuner.Tests;

public class TrackDatabaseTests
{
    [Theory]
    // LMU reports layout variants inside the track name; the venue must still resolve.
    [InlineData("Circuit de la Sarthe")]
    [InlineData("Circuit de la Sarthe Mulsanne")]
    [InlineData("Sebring International Raceway")]
    [InlineData("Bahrain International Circuit")]
    [InlineData("Autodromo Nazionale Monza")]
    public void LookupTrackLocation_ResolvesGazetteerEntries(string trackName)
    {
        TrackDatabase.LookupTrackLocation(trackName).Should().NotBeNull();
    }

    [Theory]
    // Not in the curated tracks.json — resolved by the Wikidata tier.
    [InlineData("Lusail International Circuit", 25.49, 51.45)]
    [InlineData("Circuit of the Americas", 30.13, -97.64)]
    [InlineData("Lime Rock Park", 41.93, -73.38)]
    [InlineData("Yas Marina Circuit", 24.47, 54.61)]
    public void WikidataTier_ResolvesTracksMissingFromCuratedList(string name, double lat, double lon)
    {
        var loc = TrackDatabase.LookupTrackLocation(name);
        loc.Should().NotBeNull();
        loc!.Value.lat.Should().BeApproximately((float)lat, 0.05f);
        loc.Value.lon.Should().BeApproximately((float)lon, 0.05f);
    }

    [Theory]
    [InlineData("Nurburgring")]      // accent-folded match of "nürburgring"
    [InlineData("ks_silverstone")]   // file-style id
    public void NormalisedNames_Resolve(string name) =>
        TrackDatabase.LookupTrackLocation(name).Should().NotBeNull();

    [Fact]
    public void UnknownName_DoesNotFalseMatch() =>
        TrackDatabase.LookupTrackLocation("Zzyzx Fantasy Kartodrome 9000").Should().BeNull();

    [Theory]
    // Names the games report that used to miss (survey of all installed games, 2026-10-20).
    [InlineData("COTA National Circuit", 30.13, -97.64)]       // LMU layout
    [InlineData("cota", 30.13, -97.64)]                        // ACC track id
    [InlineData("Lusail Short Circuit", 25.49, 51.45)]         // LMU layout
    [InlineData("Estoril Circuit", 38.75, -9.39)]              // R3E
    [InlineData("Knutstorp Ring", 55.99, 13.11)]               // R3E
    [InlineData("Zhejiang Circuit", 30.03, 120.47)]            // R3E
    [InlineData("AVUS", 52.48, 13.25)]                         // R3E — 4-letter exact name
    [InlineData("ks_monza66", 45.62, 9.28)]                    // AC — digits glued to the name
    [InlineData("ks_silverstone1967", 52.07, -1.01)]           // AC
    [InlineData("doningtonpark2018", 52.83, -1.38)]            // AC mod id
    [InlineData("Falkenberg Motorbana", 56.98, 12.57)]         // R3E (Wikidata: "Falkenbergs Motorbana")
    [InlineData("Stowe Circuit", 52.07, -1.01)]                // R3E — Silverstone's infield circuit
    public void PreviouslyMissedNames_Resolve(string name, double lat, double lon)
    {
        var loc = TrackDatabase.LookupTrackLocation(name);
        loc.Should().NotBeNull();
        loc!.Value.lat.Should().BeApproximately((float)lat, 0.05f);
        loc.Value.lon.Should().BeApproximately((float)lon, 0.05f);
    }

    [Theory]
    // Curated coordinates that sat 15–60 km off the circuit (outside the 13 km OSM search box).
    [InlineData("valencia", 39.49, -0.63)]                     // was Valencia city centre
    [InlineData("Michelin Raceway Road Atlanta", 34.15, -83.82)]
    [InlineData("slovakiaring", 48.05, 17.57)]
    [InlineData("sugo", 38.14, 140.78)]
    [InlineData("tsukuba", 36.15, 139.92)]
    [InlineData("virginia", 36.56, -79.20)]
    [InlineData("moscow", 56.00, 36.27)]
    [InlineData("okayama", 34.92, 134.22)]
    public void CorrectedCoordinates_SitOnTheCircuit(string name, double lat, double lon)
    {
        var loc = TrackDatabase.LookupTrackLocation(name);
        loc.Should().NotBeNull();
        loc!.Value.lat.Should().BeApproximately((float)lat, 0.02f);
        loc.Value.lon.Should().BeApproximately((float)lon, 0.02f);
    }

    [Theory]
    // Fictional / point-to-point maps must not latch onto a real venue through a common word.
    [InlineData("ks_highlands")]
    [InlineData("ks_black_cat_county")]
    [InlineData("mountain_highway")]
    [InlineData("Twin Forest")]
    [InlineData("RaceRoom Raceway")]
    [InlineData("Genting Highlands Highway")]
    public void FictionalNames_DoNotMatch(string name) =>
        TrackDatabase.LookupTrackLocation(name).Should().BeNull();

    [Theory]
    // Variant names exactly as LMU reports them (live scoring mTrackName, 2026-10-20).
    [InlineData("Circuit de la Sarthe Mulsanne", 47.95, 0.22)]
    [InlineData("Bahrain Outer Circuit", 26.03, 50.51)]
    public void LayoutVariant_ResolvesToItsVenue_NotAnotherTrack(string name, double lat, double lon)
    {
        var loc = TrackDatabase.LookupTrackLocation(name);
        loc.Should().NotBeNull();
        loc!.Value.lat.Should().BeApproximately((float)lat, 0.1f);
        loc.Value.lon.Should().BeApproximately((float)lon, 0.1f);
    }
}
