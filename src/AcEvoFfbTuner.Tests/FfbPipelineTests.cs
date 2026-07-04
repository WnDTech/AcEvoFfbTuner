using AcEvoFfbTuner.Core.FfbProcessing;
using AcEvoFfbTuner.Core.FfbProcessing.Models;
using FluentAssertions;

namespace AcEvoFfbTuner.Tests;

public class FfbPipelineTests
{
    private readonly FfbPipeline _sut = new();
    private readonly FfbRawData _raw = new();

    public FfbPipelineTests()
    {
        _raw.Mz = [5f, 5f, 1f, 1f];
        _raw.Fx = [50f, 50f, 10f, 10f];
        _raw.Fy = [200f, 200f, 50f, 50f];
        _raw.WheelLoad = [500f, 500f, 500f, 500f];
        _raw.SlipRatio = [0.02f, 0.02f, 0.01f, 0.01f];
        _raw.SlipAngle = [0.03f, 0.03f, 0.02f, 0.02f];
        _raw.SteerAngle = 0.1f;
        _raw.SpeedKmh = 80f;
        _raw.GasInput = 0.5f;
        _raw.BrakeInput = 0f;
        _raw.Gear = 3;
        _raw.SuspensionTravel = [0f, 0f, 0f, 0f];
    }

    [Fact]
    public void Process_WithValidInput_ReturnsProcessedData()
    {
        var result = _sut.Process(_raw);
        result.Should().NotBeNull();
        result.MainForce.Should().NotBe(0f);
    }

    [Fact]
    public void Process_OutputForceIsInRange()
    {
        var result = _sut.Process(_raw);
        result.MainForce.Should().BeInRange(-1f, 1f);
    }

    [Fact]
    public void Process_ChannelsPopulated()
    {
        var result = _sut.Process(_raw);
        result.ChannelMzFront.Should().NotBe(0f);
    }

    [Fact]
    public void Process_VibrationForceIsNonNegative()
    {
        var result = _sut.Process(_raw);
        result.VibrationForce.Should().BeGreaterThanOrEqualTo(0f);
    }

    [Fact]
    public void Process_SpeedKmhMatchesInput()
    {
        var result = _sut.Process(_raw);
        result.SpeedKmh.Should().Be(80f);
    }

    [Fact]
    public void Process_SteerAngleMatchesInput()
    {
        var result = _sut.Process(_raw);
        result.SteerAngle.Should().Be(0.1f);
    }

    [Fact]
    public void Process_ZeroInputs_ForceNearZero()
    {
        _raw.SpeedKmh = 0f;
        _raw.Mz = [0f, 0f, 0f, 0f];
        _raw.Fx = [0f, 0f, 0f, 0f];
        _raw.Fy = [0f, 0f, 0f, 0f];
        _raw.WheelLoad = [0f, 0f, 0f, 0f];
        _raw.SlipRatio = [0f, 0f, 0f, 0f];
        _raw.SlipAngle = [0f, 0f, 0f, 0f];
        _raw.GasInput = 0f;
        _raw.BrakeInput = 0f;
        _raw.SuspensionTravel = [0f, 0f, 0f, 0f];
        _raw.KerbVibration = 0f;
        _raw.SlipVibrations = 0f;
        _raw.RoadVibrations = 0f;
        _raw.AbsVibrations = 0f;
        _sut.Reset();
        var result = _sut.Process(_raw);
        Math.Abs(result.MainForce).Should().BeLessThan(0.001f);
    }

    [Fact]
    public void Process_LowSpeed_FadesForce()
    {
        _raw.SpeedKmh = 2f;
        var result = _sut.Process(_raw);
        result.MainForce.Should().BeInRange(-0.5f, 0.5f);
    }

    [Fact]
    public void Process_NegativeForce_PreservesSign()
    {
        _raw.Mz = [-5f, -5f, -1f, -1f];
        var result = _sut.Process(_raw);
        result.MainForce.Should().BeLessThanOrEqualTo(0f);
    }

    [Fact]
    public void Reset_ClearsPipelineState()
    {
        _sut.Process(_raw);
        _sut.Reset();
        _sut.Process(_raw);
    }

    [Fact]
    public void Process_OutputGainScalesForce()
    {
        _sut.OutputGain = 0.5f;
        var result1 = _sut.Process(_raw);
        _sut.OutputGain = 1.0f;
        var result2 = _sut.Process(_raw);
        Math.Abs(result1.MainForce).Should().BeLessThan(Math.Abs(result2.MainForce) + 0.001f);
    }

    [Fact]
    public void Process_DifferentInputs_ProducesDifferentOutput()
    {
        var result1 = _sut.Process(_raw);
        _raw.SteerAngle = 0.5f;
        var result2 = _sut.Process(_raw);
        result1.MainForce.Should().NotBe(result2.MainForce);
    }

    [Fact]
    public void Process_NoiseFloor_GatesTinyForce()
    {
        _sut.NoiseFloor = 0.1f;
        _sut.OutputGain = 0.01f;
        _raw.Mz = [0.001f, 0.001f, 0f, 0f];
        _raw.Fx = [0f, 0f, 0f, 0f];
        _raw.Fy = [0f, 0f, 0f, 0f];
        _raw.SpeedKmh = 80f;
        var result = _sut.Process(_raw);
        result.MainForce.Should().Be(0f);
    }
}
