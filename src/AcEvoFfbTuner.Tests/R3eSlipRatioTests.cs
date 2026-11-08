using AcEvoFfbTuner.Core.SharedMemory;
using FluentAssertions;

namespace AcEvoFfbTuner.Tests;

/// <summary>
/// R3E TireSpeed is negative for forward rolling (verified live 2026-11-07).
/// Slip ratio must be ~0 when free rolling, not -2.
/// </summary>
public class R3eSlipRatioTests
{
    [Theory]
    [InlineData(-40f, 40f, 0f)]      // free rolling, R3E sign convention
    [InlineData(40f, 40f, 0f)]       // free rolling, positive convention
    [InlineData(0f, 40f, -1f)]       // full lockup
    [InlineData(-48f, 40f, 0.2f)]    // 20% wheelspin
    [InlineData(-36f, 40f, -0.1f)]   // 10% braking slip
    public void SlipRatio_IsSignConventionFree(float tire, float car, float expected)
    {
        RaceroomSharedMemoryReader.CalculateSlipRatio(tire, car).Should().BeApproximately(expected, 1e-4f);
    }

    [Fact]
    public void SlipRatio_ZeroCarSpeed_IsZero()
    {
        RaceroomSharedMemoryReader.CalculateSlipRatio(-1f, 0f).Should().Be(0f);
    }
}

public class R3eSteeringForceNormalizeTests
{
    [Theory]
    [InlineData(0.4f, 0.4f)]
    [InlineData(-1.0f, -1.0f)]
    [InlineData(2.5f, 2.5f)]     // heavy in-game clipping must NOT collapse to 0.025
    [InlineData(-3.2f, -3.2f)]
    public void Normalize_KeepsApi36Scale(float input, float expected)
    {
        RaceroomSharedMemoryReader.NormalizeSteeringForce(input).Should().Be(expected);
    }

    [Fact]
    public void Normalize_RejectsNonFinite()
    {
        RaceroomSharedMemoryReader.NormalizeSteeringForce(float.NaN).Should().Be(0f);
        RaceroomSharedMemoryReader.NormalizeSteeringForce(float.PositiveInfinity).Should().Be(0f);
    }
}
