using AcEvoFfbTuner.Core.FfbProcessing;
using AcEvoFfbTuner.Core.FfbProcessing.Models;
using FluentAssertions;

namespace AcEvoFfbTuner.Tests;

public class SoftLockTests
{
    private static FfbRawData MakeRaw(float steerAngle)
    {
        return new FfbRawData
        {
            Mz = [5f, 5f, 1f, 1f],
            Fx = [50f, 50f, 10f, 10f],
            Fy = [200f, 200f, 50f, 50f],
            WheelLoad = [500f, 500f, 500f, 500f],
            SlipRatio = [0.02f, 0.02f, 0.01f, 0.01f],
            SlipAngle = [0.03f, 0.03f, 0.02f, 0.02f],
            SuspensionTravel = [0f, 0f, 0f, 0f],
            SteerAngle = steerAngle,
            SpeedKmh = 80f,
            GasInput = 0.5f,
            Gear = 3
        };
    }

    [Fact]
    public void Disabled_IsPassthrough_AtFullLock()
    {
        var pipe = new FfbPipeline(); // SoftLockEnabled=false by default
        var off = pipe.Process(MakeRaw(1.0f)).MainForce;
        pipe.SoftLockEnabled.Should().BeFalse();
        off.Should().BeInRange(-1f, 1f);
    }

    [Fact]
    public void Enabled_AddsTowardCenterForce_AtFullLock()
    {
        var plain = new FfbPipeline();
        var plainForce = plain.Process(MakeRaw(1.0f)).MainForce;

        var locked = new FfbPipeline { SoftLockEnabled = true, SoftLockStrength = 1f };
        var lockedForce = locked.Process(MakeRaw(1.0f)).MainForce;

        // EVO convention: toward-center at +steer is negative → wall pulls down.
        lockedForce.Should().BeLessThan(plainForce);
        lockedForce.Should().BeInRange(-1f, 1f);
    }

    [Fact]
    public void Enabled_DoesNothing_AtCenter()
    {
        var plain = new FfbPipeline();
        var plainForce = plain.Process(MakeRaw(0.1f)).MainForce;

        var locked = new FfbPipeline { SoftLockEnabled = true, SoftLockStrength = 1f };
        var lockedForce = locked.Process(MakeRaw(0.1f)).MainForce;

        lockedForce.Should().BeApproximately(plainForce, 1e-6f);
    }

    [Fact]
    public void Enabled_IgnoresOutOfRangeUnits()
    {
        var plain = new FfbPipeline();
        var plainForce = plain.Process(MakeRaw(7.0f)).MainForce;

        var locked = new FfbPipeline { SoftLockEnabled = true, SoftLockStrength = 1f };
        var lockedForce = locked.Process(MakeRaw(7.0f)).MainForce;

        lockedForce.Should().BeApproximately(plainForce, 1e-6f);
    }

    [Fact]
    public void R3e_WallFollowsR3eSignConvention()
    {
        var plain = new R3eFfbPipeline();
        var plainForce = plain.Process(MakeRaw(1.0f)).MainForce;

        var locked = new R3eFfbPipeline { SoftLockEnabled = true, SoftLockStrength = 1f };
        var lockedForce = locked.Process(MakeRaw(1.0f)).MainForce;

        // R3E convention: toward-center at +steer is positive → wall pushes up.
        lockedForce.Should().BeGreaterThanOrEqualTo(plainForce);
        lockedForce.Should().BeInRange(-1f, 1f);
    }
}
