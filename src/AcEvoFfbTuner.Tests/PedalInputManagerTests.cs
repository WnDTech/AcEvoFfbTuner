using AcEvoFfbTuner.Core.Config;
using AcEvoFfbTuner.Core.PedalInput;
using FluentAssertions;
using NSubstitute;

namespace AcEvoFfbTuner.Tests;

public class PedalInputManagerTests
{
    private readonly PedalInputManager _sut = new();

    public PedalInputManagerTests()
    {
        PedalConfigManager.Instance.Save(new PedalConfig
        {
            Enabled = true,
            Gas = new PedalAxisConfig { Deadzone = 0f, Smoothing = 0f },
            Brake = new PedalAxisConfig { Deadzone = 0f, Smoothing = 0f },
            Clutch = new PedalAxisConfig { Deadzone = 0f, Smoothing = 0f }
        });
    }

    [Fact]
    public void TryGetState_NoSources_ReturnsFalse()
    {
        var result = _sut.TryGetState(out var state);
        result.Should().BeFalse();
        state.BrakeInput.Should().Be(0f);
    }

    [Fact]
    public void TryGetState_DisabledInConfig_ReturnsFalse()
    {
        var source = Substitute.For<IPedalInputSource>();
        source.IsAvailable.Returns(true);
        source.TryReadRaw(out Arg.Any<RawPedalState>())
            .Returns(x => { x[0] = new RawPedalState { BrakeRaw = 0.5f, GasRaw = 0.3f }; return true; });
        _sut.RegisterSource(source);

        PedalConfigManager.Instance.Save(new PedalConfig { Enabled = false });

        var result = _sut.TryGetState(out var state);
        result.Should().BeFalse();
    }

    [Fact]
    public void TryGetState_WithAvailableSource_ReturnsCalibratedState()
    {
        var source = Substitute.For<IPedalInputSource>();
        source.IsAvailable.Returns(true);
        source.SourceType.Returns(SourceType.Hid);
        source.DeviceName.Returns("Test Pedals");
        source.TryReadRaw(out Arg.Any<RawPedalState>())
            .Returns(x => { x[0] = new RawPedalState { BrakeRaw = 0.5f, GasRaw = 0.3f, Source = SourceType.Hid }; return true; });
        _sut.RegisterSource(source);

        var result = _sut.TryGetState(out var state);
        result.Should().BeTrue();
        state.BrakeInput.Should().BeApproximately(0.5f, 0.001f);
        state.GasInput.Should().BeApproximately(0.3f, 0.001f);
        state.Source.Should().Be(SourceType.Hid);
    }

    [Fact]
    public void TryGetState_Fallback_WhenFirstSourceUnavailable()
    {
        var unavailable = Substitute.For<IPedalInputSource>();
        unavailable.IsAvailable.Returns(false);

        var available = Substitute.For<IPedalInputSource>();
        available.IsAvailable.Returns(true);
        available.SourceType.Returns(SourceType.Keyboard);
        available.TryReadRaw(out Arg.Any<RawPedalState>())
            .Returns(x => { x[0] = new RawPedalState { BrakeRaw = 1.0f, GasRaw = 0f, Source = SourceType.Keyboard }; return true; });

        _sut.RegisterSource(unavailable);
        _sut.RegisterSource(available);

        var result = _sut.TryGetState(out var state);
        result.Should().BeTrue();
        state.BrakeInput.Should().Be(1.0f);
        state.Source.Should().Be(SourceType.Keyboard);
    }

    [Fact]
    public void TryGetState_AllSourcesFail_ReturnsFalse()
    {
        var source1 = Substitute.For<IPedalInputSource>();
        source1.IsAvailable.Returns(true);
        source1.TryReadRaw(out Arg.Any<RawPedalState>()).Returns(false);

        var source2 = Substitute.For<IPedalInputSource>();
        source2.IsAvailable.Returns(true);
        source2.TryReadRaw(out Arg.Any<RawPedalState>()).Returns(false);

        _sut.RegisterSource(source1);
        _sut.RegisterSource(source2);

        var result = _sut.TryGetState(out var state);
        result.Should().BeFalse();
    }

    [Fact]
    public void IsAnySourceAvailable_NoSources_ReturnsFalse()
    {
        _sut.IsAnySourceAvailable.Should().BeFalse();
    }

    [Fact]
    public void IsAnySourceAvailable_WithRegisteredSource_ReturnsTrue()
    {
        var source = Substitute.For<IPedalInputSource>();
        source.IsAvailable.Returns(true);
        _sut.RegisterSource(source);

        _sut.IsAnySourceAvailable.Should().BeTrue();
    }

    [Fact]
    public void RegisterSource_SetsSourceCorrectly()
    {
        var source = Substitute.For<IPedalInputSource>();
        source.SourceType.Returns(SourceType.Hid);
        source.DeviceName.Returns("HID Pedals");

        _sut.RegisterSource(source);

        _sut.Sources.Should().Contain(source);
    }

    [Fact]
    public void UnregisterSource_RemovesSource()
    {
        var source = Substitute.For<IPedalInputSource>();
        _sut.RegisterSource(source);
        _sut.UnregisterSource(source);

        _sut.Sources.Should().NotContain(source);
    }
}
