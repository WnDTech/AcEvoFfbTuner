using AcEvoFfbTuner.Core.TrackMapping;
using Xunit;

namespace AcEvoFfbTuner.Tests;

public class LayoutQualityRankTests
{
    private static TrackDetailedInfo D(TrackDataSource src, float lengthM) => new()
    {
        DataSource = src,
        TrackLengthM = lengthM,
        TrackLayout = Enumerable.Range(0, 20).Select(i => new TrackPoint(47 + i * 1e-4, 14)).ToList(),
    };

    [Fact]
    public void OsmRelation_BeatsOrtdbDraft_WhenBothMatchTheLapLength()
    {
        // Red Bull Ring GP, 2026-11-09: OSM 4300 m vs ORTDB draft 4329 m, game 4306 m.
        Assert.True(TieredTrackDataProvider.QualityRank(D(TrackDataSource.OsmRelation, 4300), 4306)
                  > TieredTrackDataProvider.QualityRank(D(TrackDataSource.OpenRacetrackDb, 4329), 4306));
    }

    [Fact]
    public void WrongLengthRelation_LosesToMatchingOrtdb()
    {
        // A relation for another variant (e.g. GP outline for a National layout) must not be "kept".
        Assert.True(TieredTrackDataProvider.QualityRank(D(TrackDataSource.OsmRelation, 4300), 2300)
                  < TieredTrackDataProvider.QualityRank(D(TrackDataSource.OpenRacetrackDb, 2310), 2300));
    }

    [Fact]
    public void Nothing_RanksBelowAnything()
    {
        Assert.Equal(-1, TieredTrackDataProvider.QualityRank(null, 4306));
    }
}
