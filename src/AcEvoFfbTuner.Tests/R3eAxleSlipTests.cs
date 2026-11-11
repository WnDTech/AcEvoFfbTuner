using AcEvoFfbTuner.Core.SharedMemory;
using Xunit;

namespace AcEvoFfbTuner.Tests;

/// <summary>
/// R3E per-axle slip angle model, checked against a live 2026-11-10 capture
/// (19° steer lock, fitted wheelbase 2.85 m, 42% front static weight).
/// </summary>
public class R3eAxleSlipTests
{
    private const float Deg = MathF.PI / 180f;

    [Fact]
    public void RearLedSlide_RearExceedsFront_SameSignAsLegacyBeta()
    {
        // t=56.3 s: counter-steering slide, legacy β = atan2(Vx,−Vz) = −0.178.
        var (front, rear) = RaceroomSharedMemoryReader.AxleSlip(
            beta: 0.178f, yawRate: -0.521f, speedMs: 32.321f,
            steerRad: -0.018f * 19f * Deg, wheelbase: 2.852f, frontShare: 0.42f);
        Assert.InRange(front, -0.167f, -0.147f);
        Assert.InRange(rear, -0.207f, -0.187f);
        Assert.True(Math.Abs(rear) > Math.Abs(front));
    }

    [Fact]
    public void NoSlipCornering_GivesZero()
    {
        // Kinematic corner: δ = L·r/V and β = b·r/V → both axles at zero slip.
        float L = 2.85f, f = 0.42f, v = 30f, r = 0.3f;
        float beta = f * L * r / v;
        var (front, rear) = RaceroomSharedMemoryReader.AxleSlip(beta, r, v, L * r / v, L, f);
        Assert.InRange(front, -1e-4f, 1e-4f);
        Assert.InRange(rear, -1e-4f, 1e-4f);
    }

    [Fact]
    public void Output_IsClampedToLegacyRange()
    {
        var (front, rear) = RaceroomSharedMemoryReader.AxleSlip(1.2f, 0f, 20f, 0f, 2.7f, 0.45f);
        Assert.Equal(-0.30f, front);
        Assert.Equal(-0.30f, rear);
    }
}
