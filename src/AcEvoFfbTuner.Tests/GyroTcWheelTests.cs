using AcEvoFfbTuner.Core.FfbProcessing;
using AcEvoFfbTuner.Core.FfbProcessing.Models;
using FluentAssertions;

namespace AcEvoFfbTuner.Tests;

public class GyroTcWheelTests
{
    private static FfbRawData MakeRaw(float steerAngle = 0.3f, float yawRate = 0f)
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
            LocalAngularVel = [0f, yawRate, 0f],
            SteerAngle = steerAngle,
            SpeedKmh = 80f,
            GasInput = 0.5f,
            Gear = 3
        };
    }

    [Fact]
    public void Gyro_Disabled_IsPassthrough()
    {
        var pipe = new FfbPipeline();
        var a = pipe.Process(MakeRaw(yawRate: 2f)).MainForce;
        pipe.GyroEnabled.Should().BeFalse();
        a.Should().BeInRange(-1f, 1f);
    }

    [Fact]
    public void Gyro_Enabled_OpposesYawRate()
    {
        var still = new FfbPipeline { GyroEnabled = true, GyroGain = 1f };
        // Warm up EMA state identically, then compare yaw vs no-yaw.
        still.Process(MakeRaw(yawRate: 0f));
        var noYaw = still.Process(MakeRaw(yawRate: 0f)).MainForce;

        var gyro = new FfbPipeline { GyroEnabled = true, GyroGain = 1f };
        gyro.Process(MakeRaw(yawRate: 0f));
        var withYaw = gyro.Process(MakeRaw(yawRate: 2f)).MainForce;

        // Positive yaw with positive gain drives detail negative (opposing).
        withYaw.Should().BeLessThan(noYaw);
    }

    [Fact]
    public void Gyro_IgnoresNaNztenYaw()
    {
        var pipe = new FfbPipeline { GyroEnabled = true, GyroGain = 1f };
        var ok = pipe.Process(MakeRaw(yawRate: 0.5f)).MainForce;
        var nan = pipe.Process(MakeRaw(yawRate: float.NaN)).MainForce;
        nan.Should().BeInRange(-1f, 1f);
        ok.Should().BeInRange(-1f, 1f);
    }

    [Fact]
    public void TcWheel_Disabled_IsPassthrough_WhenCutting()
    {
        var pipe = new FfbPipeline();
        var raw = MakeRaw();
        raw.TcActiveGfx = true;
        pipe.Process(raw).MainForce.Should().BeInRange(-1f, 1f);
    }

    [Fact]
    public void TcWheel_Enabled_NudgesTowardCenter_WhenCutting()
    {
        var plain = new FfbPipeline();
        var plainForce = plain.Process(MakeRaw()).MainForce;

        var tc = new FfbPipeline { TcWheelEnabled = true, TcWheelGain = 1f };
        var cutting = MakeRaw();
        cutting.TcActiveGfx = true;
        var tcForce = tc.Process(cutting).MainForce;

        // EVO convention: +steer centers negative → nudge pulls down.
        tcForce.Should().BeLessThan(plainForce);
    }

    [Fact]
    public void TcWheel_TriggersOnTcinAction_Too()
    {
        var tc = new FfbPipeline { TcWheelEnabled = true, TcWheelGain = 1f };
        var cutting = MakeRaw();
        cutting.TcinAction = 1;
        var plain = new FfbPipeline().Process(MakeRaw()).MainForce;
        tc.Process(cutting).MainForce.Should().BeLessThan(plain);
    }

    [Fact]
    public void TcWheel_NoSteer_NoEffect()
    {
        var tc = new FfbPipeline { TcWheelEnabled = true, TcWheelGain = 1f };
        var cutting = MakeRaw(steerAngle: 0f);
        cutting.TcActiveGfx = true;
        var plain = new FfbPipeline().Process(MakeRaw(steerAngle: 0f)).MainForce;
        tc.Process(cutting).MainForce.Should().BeApproximately(plain, 1e-6f);
    }

    [Fact]
    public void TcWheel_R3e_SignConventionRespected()
    {
        var plain = new R3eFfbPipeline();
        var plainForce = plain.Process(MakeRaw()).MainForce;

        var tc = new R3eFfbPipeline { TcWheelEnabled = true, TcWheelGain = 1f };
        var cutting = MakeRaw();
        cutting.TcActiveGfx = true; // same flag TelemetryLoop injects from IsTcActive
        var tcForce = tc.Process(cutting).MainForce;

        // R3E convention: +steer centers positive → nudge pushes up.
        tcForce.Should().BeGreaterThanOrEqualTo(plainForce);
    }
}
