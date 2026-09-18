using AcEvoFfbTuner.Core.Profiles;
using FluentAssertions;

namespace AcEvoFfbTuner.Tests;

public class ProfileComparatorTests
{
    [Fact]
    public void Diff_IdenticalProfiles_ReturnsEmpty()
    {
        var a = new FfbProfile();
        var b = new FfbProfile();
        ProfileComparator.Diff(a, b).Should().BeEmpty();
    }

    [Fact]
    public void Diff_ChangedGain_ReturnsEntry()
    {
        var a = new FfbProfile();
        var b = new FfbProfile { OutputGain = a.OutputGain + 0.1f };
        var diffs = ProfileComparator.Diff(a, b);
        diffs.Should().ContainSingle(d => d.Path == "OutputGain");
    }

    [Fact]
    public void Diff_NullInputs_ReturnsEmpty()
    {
        ProfileComparator.Diff(null, new FfbProfile()).Should().BeEmpty();
        ProfileComparator.Diff(new FfbProfile(), null).Should().BeEmpty();
    }

    [Fact]
    public void Format_EmptyDiff_MentionsIdentical()
    {
        var text = ProfileComparator.Format(new List<ProfileDiffEntry>());
        text.Should().Contain("identical");
    }

    [Fact]
    public void Diff_NewScaffoldFields_Compared()
    {
        var a = new FfbProfile();
        var b = new FfbProfile();
        b.Gyro.Gain = 0.5f;
        b.Gyro.Enabled = true;
        var diffs = ProfileComparator.Diff(a, b);
        diffs.Should().Contain(d => d.Path == "Gyro.Gain");
        diffs.Should().Contain(d => d.Path == "Gyro.Enabled");
    }
}
