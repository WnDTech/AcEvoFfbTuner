using AcEvoFfbTuner.Core.PedalInput;
using AcEvoFfbTuner.Core.PedalInput.Sources;
using FluentAssertions;

namespace AcEvoFfbTuner.Tests;

public class KeyboardPedalSourceTests
{
    [Fact]
    public void SourceType_IsKeyboard()
    {
        var sut = new KeyboardPedalSource();
        sut.SourceType.Should().Be(SourceType.Keyboard);
    }

    [Fact]
    public void DeviceName_ContainsKeyboard()
    {
        var sut = new KeyboardPedalSource();
        sut.DeviceName.Should().Contain("Keyboard");
    }

    [Fact]
    public void IsAvailable_Default_True()
    {
        var sut = new KeyboardPedalSource();
        sut.IsAvailable.Should().BeTrue();
    }

    [Fact]
    public void IsAvailable_WhenDisabled_False()
    {
        var sut = new KeyboardPedalSource();
        sut.Enabled = false;
        sut.IsAvailable.Should().BeFalse();
    }

    [Fact]
    public void TryReadRaw_WhenDisabled_ReturnsFalse()
    {
        var sut = new KeyboardPedalSource();
        sut.Enabled = false;
        var result = sut.TryReadRaw(out var state);
        result.Should().BeFalse();
    }

    [Fact]
    public void TryReadRaw_WhenEnabled_ReturnsTrue()
    {
        var sut = new KeyboardPedalSource();
        var result = sut.TryReadRaw(out var state);
        result.Should().BeTrue();
    }

    [Fact]
    public void TryReadRaw_ReturnsKeyboardSource()
    {
        var sut = new KeyboardPedalSource();
        sut.TryReadRaw(out var state);
        state.Source.Should().Be(SourceType.Keyboard);
    }

    [Fact]
    public void TryReadRaw_AllValuesInRange()
    {
        var sut = new KeyboardPedalSource();
        for (int i = 0; i < 10; i++)
        {
            sut.TryReadRaw(out var state);
            state.GasRaw.Should().BeInRange(0f, 1f);
            state.BrakeRaw.Should().BeInRange(0f, 1f);
            state.ClutchRaw.Should().BeInRange(0f, 1f);
        }
    }
}
