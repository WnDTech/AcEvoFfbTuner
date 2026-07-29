using AcEvoFfbTuner.Core.Config;
using AcEvoFfbTuner.Core.PedalInput;
using FluentAssertions;

namespace AcEvoFfbTuner.Tests;

public class PedalCalibrationTests
{
    private readonly PedalCalibration _sut = new();
    private readonly PedalConfig _config = new();

    public PedalCalibrationTests()
    {
        _config.Enabled = true;
    }

    [Fact]
    public void Apply_NoCalibration_ReturnsRawValues()
    {
        _config.Brake.Deadzone = 0f;
        _config.Brake.Smoothing = 0f;
        _config.Gas.Deadzone = 0f;
        _config.Gas.Smoothing = 0f;

        var raw = new RawPedalState { GasRaw = 0.5f, BrakeRaw = 0.3f, ClutchRaw = 0f };
        var result = _sut.Apply(raw, _config);

        result.GasInput.Should().BeApproximately(0.5f, 0.001f);
        result.BrakeInput.Should().BeApproximately(0.3f, 0.001f);
        result.ClutchInput.Should().BeApproximately(0f, 0.001f);
    }

    [Fact]
    public void Apply_Deadzone_BelowThreshold_ReturnsZero()
    {
        _config.Brake.Deadzone = 0.1f;
        _config.Brake.Smoothing = 0f;

        var raw = new RawPedalState { BrakeRaw = 0.05f };
        var result = _sut.Apply(raw, _config);

        result.BrakeInput.Should().Be(0f);
    }

    [Fact]
    public void Apply_Deadzone_AboveThreshold_MapsCorrectly()
    {
        _config.Brake.Deadzone = 0.2f;
        _config.Brake.Smoothing = 0f;

        var raw = new RawPedalState { BrakeRaw = 0.6f };
        var result = _sut.Apply(raw, _config);

        float expected = (0.6f - 0.2f) / (1f - 0.2f);
        result.BrakeInput.Should().BeApproximately(expected, 0.001f);
    }

    [Fact]
    public void Apply_Invert_FlipsValue()
    {
        _config.Gas.Invert = true;
        _config.Gas.Deadzone = 0f;
        _config.Gas.Smoothing = 0f;

        var raw = new RawPedalState { GasRaw = 0.3f };
        var result = _sut.Apply(raw, _config);

        result.GasInput.Should().BeApproximately(0.7f, 0.001f);
    }

    [Fact]
    public void Apply_MinMax_RemapsRange()
    {
        _config.Brake.Min = 0.2f;
        _config.Brake.Max = 0.8f;
        _config.Brake.Deadzone = 0f;
        _config.Brake.Smoothing = 0f;

        var raw = new RawPedalState { BrakeRaw = 0.5f };
        var result = _sut.Apply(raw, _config);

        result.BrakeInput.Should().BeApproximately(0.5f, 0.001f);
    }

    [Fact]
    public void Apply_MinMax_BelowMin_ReturnsZero()
    {
        _config.Brake.Min = 0.3f;
        _config.Brake.Max = 0.9f;
        _config.Brake.Deadzone = 0f;
        _config.Brake.Smoothing = 0f;

        var raw = new RawPedalState { BrakeRaw = 0.1f };
        var result = _sut.Apply(raw, _config);

        result.BrakeInput.Should().Be(0f);
    }

    [Fact]
    public void Apply_MinMax_AboveMax_ReturnsOne()
    {
        _config.Brake.Min = 0f;
        _config.Brake.Max = 0.7f;
        _config.Brake.Deadzone = 0f;
        _config.Brake.Smoothing = 0f;

        var raw = new RawPedalState { BrakeRaw = 0.9f };
        var result = _sut.Apply(raw, _config);

        result.BrakeInput.Should().Be(1f);
    }

    [Fact]
    public void Apply_ClutchDefaultConfig_PassesThrough()
    {
        var raw = new RawPedalState { ClutchRaw = 0.75f };
        var result = _sut.Apply(raw, _config);

        result.ClutchInput.Should().BeInRange(0f, 1f);
    }

    [Fact]
    public void Apply_SourceType_Preserved()
    {
        var raw = new RawPedalState { Source = SourceType.Keyboard };
        var result = _sut.Apply(raw, _config);

        result.Source.Should().Be(SourceType.Keyboard);
    }
}
