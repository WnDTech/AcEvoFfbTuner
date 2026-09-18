using AcEvoFfbTuner.Core.TrackMapping;
using FluentAssertions;

namespace AcEvoFfbTuner.Tests;

public class LapProfileSummaryTests
{
    private static void DriveLap(LapDataRecorder rec, int lapNumber, float force = 0.5f)
    {
        rec.Initialize(8);
        for (int i = 0; i < 120; i++)
            rec.Update(lapNumber, i % 8, force, 0.1f, 0.1f, 0.1f, 100f, false);
        // Trigger completion by moving to the next lap.
        rec.Update(lapNumber + 1, 0, force, 0.1f, 0.1f, 0.1f, 100f, false);
    }

    [Fact]
    public void CompletedLap_IsStampedWithActiveProfile()
    {
        var rec = new LapDataRecorder { ActiveProfileName = "A: Baseline" };
        DriveLap(rec, 1);
        rec.GetLatestLap()!.ProfileName.Should().Be("A: Baseline");
    }

    [Fact]
    public void Summary_GroupsByProfile()
    {
        var rec = new LapDataRecorder { ActiveProfileName = "A" };
        DriveLap(rec, 1);
        rec.ActiveProfileName = "B";
        DriveLap(rec, 3);
        var summary = rec.GetSummaryByProfile();
        summary.Should().HaveCount(2);
        summary.Select(s => s.ProfileName).Should().Contain(new[] { "A", "B" });
    }

    [Fact]
    public void Summary_EmptyRecorder_ReturnsEmpty()
    {
        new LapDataRecorder().GetSummaryByProfile().Should().BeEmpty();
    }
}
