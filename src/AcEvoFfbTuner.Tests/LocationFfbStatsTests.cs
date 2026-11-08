using AcEvoFfbTuner.Core.TrackMapping;
using Xunit;

namespace AcEvoFfbTuner.Tests;

public class LocationFfbStatsTests
{
    private static FfbDiagnosticEvent Evt(string? loc, FfbEventType type, FfbEventClassification cls) =>
        new() { Location = loc, EventType = type, Classification = cls, WaypointIndex = 3 };

    [Fact]
    public void WorstLocations_RanksSuspiciousFirst_AndClearsWithMap()
    {
        var h = new TrackDiagnosticHeatmap();
        h.Initialize(10);
        h.RecordEvent(Evt("T1 La Source", FfbEventType.Snap, FfbEventClassification.ExpectedDynamics));
        h.RecordEvent(Evt("T1 La Source", FfbEventType.Snap, FfbEventClassification.ExpectedDynamics));
        h.RecordEvent(Evt("after T3 Raidillon", FfbEventType.Snap, FfbEventClassification.Suspicious));
        h.RecordEvent(Evt("after T3 Raidillon", FfbEventType.Clipping, FfbEventClassification.Suspicious));
        h.RecordEvent(Evt(null, FfbEventType.Clipping, FfbEventClassification.Suspicious));

        var worst = h.GetWorstLocations();
        Assert.Equal(2, worst.Count);
        Assert.Equal("after T3 Raidillon", worst[0].Location);
        Assert.Equal(2, worst[0].Suspicious);
        Assert.Equal(1, worst[0].Clips);
        Assert.Equal("after T3 Raidillon: 2 suspicious, 1 clip, 1 snap", worst[0].Describe());

        h.Initialize(10);
        Assert.Empty(h.GetWorstLocations());
    }

    [Fact]
    public void CornerLabel_IncludesRealNameWhenKnown()
    {
        Assert.Equal("T5", new TrackCorner { CornerNumber = 5 }.Label);
        Assert.Equal("T5 Les Combes", new TrackCorner { CornerNumber = 5, RealName = "Les Combes" }.Label);
    }
}
