using AcEvoFfbTuner.Core.SharedMemory;
using Xunit;

namespace AcEvoFfbTuner.Tests;

/// <summary>LMU per-wheel slip angle from tyre-frame ground velocity (live capture 2026-11-11).</summary>
public class LmuWheelSlipTests
{
    [Fact]
    public void UsesPerWheelGroundVelocity()
    {
        // Live frame at 50 m/s: lat ground vel −0.152/−0.031/0.125/−0.126, lon ≈ −50.18.
        var a = LmuSharedMemoryReader.WheelSlipAngles(
            [-0.152f, -0.031f, 0.125f, -0.126f], [-50.18f, -50.184f, -50.186f, -50.183f], 50.18);
        Assert.InRange(a[0], -0.0031f, -0.0029f);
        Assert.InRange(a[2], 0.0024f, 0.0026f);
        Assert.NotEqual(a[0], a[1]);
    }

    [Fact]
    public void FallsBackToBodySideslip_WhenGroundVelocityMissing()
    {
        var a = LmuSharedMemoryReader.WheelSlipAngles(new float[4], new float[4], 30, bodyVx: 1.5, bodyVz: -30);
        foreach (var v in a) Assert.InRange(v, 0.0497f, 0.0501f);
    }

    [Fact]
    public void ZeroBelowWalkingPace_AndClamped()
    {
        Assert.All(LmuSharedMemoryReader.WheelSlipAngles([5f, 5f, 5f, 5f], [-1f, -1f, -1f, -1f], 1.0), v => Assert.Equal(0f, v));
        Assert.All(LmuSharedMemoryReader.WheelSlipAngles([20f, 20f, 20f, 20f], [-20f, -20f, -20f, -20f], 28), v => Assert.Equal(0.30f, v));
    }
}
