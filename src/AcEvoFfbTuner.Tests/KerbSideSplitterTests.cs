using AcEvoFfbTuner.Core.PedalHaptics;
using FluentAssertions;

namespace AcEvoFfbTuner.Tests;

public class KerbSideSplitterTests
{
    // Wheel order FL, FR, RL, RR.
    private static (float l, float r) Run(float shared, float[] a, float[] b)
    {
        var s = new KerbSideSplitter();
        s.Split(shared, a);
        return s.Split(shared, b);
    }

    [Fact]
    public void LeftSideKerb_GoesToLeftOnly()
    {
        var (l, r) = Run(0.8f, [0f, 0f, 0f, 0f], [0.02f, 0f, 0.02f, 0f]);
        l.Should().BeApproximately(0.8f, 1e-6f);
        r.Should().Be(0f);
    }

    [Fact]
    public void RightSideKerb_GoesToRightOnly()
    {
        var (l, r) = Run(0.8f, [0f, 0f, 0f, 0f], [0f, 0.03f, 0f, 0.01f]);
        l.Should().Be(0f);
        r.Should().BeApproximately(0.8f, 1e-6f);
    }

    [Fact]
    public void SquareHit_GoesToBoth()
    {
        var (l, r) = Run(0.5f, [0f, 0f, 0f, 0f], [0.02f, 0.02f, 0f, 0f]);
        l.Should().BeApproximately(0.5f, 1e-6f);
        r.Should().BeApproximately(0.5f, 1e-6f);
    }

    [Fact]
    public void MostlyLeft_RightGetsProportionalShare()
    {
        // left 0.03, right 0.01 → wLeft 0.75 → left full, right 0.5x.
        var (l, r) = Run(1f, [0f, 0f, 0f, 0f], [0.03f, 0.01f, 0f, 0f]);
        l.Should().BeApproximately(1f, 1e-5f);
        r.Should().BeApproximately(0.5f, 1e-5f);
    }

    [Fact]
    public void NoSuspensionMovement_FallsBackToBoth()
    {
        var (l, r) = Run(0.6f, [0.1f, 0.1f, 0.1f, 0.1f], [0.1f, 0.1f, 0.1f, 0.1f]);
        l.Should().Be(0.6f);
        r.Should().Be(0.6f);
    }

    [Fact]
    public void FirstFrame_FallsBackToBoth()
    {
        var s = new KerbSideSplitter();
        s.Split(0.4f, [0.02f, 0f, 0f, 0f]).Should().Be((0.4f, 0.4f));
    }

    [Fact]
    public void NoKerb_IsZero()
    {
        Run(0f, [0f, 0f, 0f, 0f], [0.02f, 0f, 0f, 0f]).Should().Be((0f, 0f));
    }
}
