using AcEvoFfbTuner.Core.SharedMemory;
using FluentAssertions;
using Xunit;

namespace AcEvoFfbTuner.Tests;

public class CarClassesTests
{
    [Theory]
    // LMU mVehicleClass
    [InlineData("Hyper", CarClassCategory.Hypercar)]
    [InlineData("LMP2", CarClassCategory.Lmp2)]
    [InlineData("LMP2_ELMS", CarClassCategory.Lmp2)]
    [InlineData("LMP3", CarClassCategory.Lmp3)]
    [InlineData("GTE", CarClassCategory.Gte)]
    [InlineData("GT3", CarClassCategory.Gt3)]
    [InlineData("LMGT3", CarClassCategory.Gt3)]
    // RaceRoom r3e-data.json class names (from the installed game file)
    [InlineData("Hypercars", CarClassCategory.Hypercar)]
    [InlineData("P1", CarClassCategory.Hypercar)]
    [InlineData("P2", CarClassCategory.Lmp2)]
    [InlineData("Group C", CarClassCategory.Hypercar)]
    [InlineData("Mazda Dpi", CarClassCategory.Hypercar)]
    [InlineData("GTR 3", CarClassCategory.Gt3)]
    [InlineData("ADAC GT Masters 2021", CarClassCategory.Gt3)]
    [InlineData("GTR 4", CarClassCategory.Gt4)]
    [InlineData("GT2", CarClassCategory.Gte)]
    [InlineData("DTM 2025", CarClassCategory.Touring)]
    [InlineData("Esports WTCR Prologue", CarClassCategory.Touring)]
    [InlineData("Group 5", CarClassCategory.Touring)]
    [InlineData("FR X-22 Cup", CarClassCategory.Cup)]
    [InlineData("FR3 Cup", CarClassCategory.Cup)]
    [InlineData("FRX-22 Esports", CarClassCategory.Formula)]
    [InlineData("Porsche 992 GT3 Cup", CarClassCategory.Cup)]   // one-make cup, not GT3 grid
    [InlineData("Cayman GT4 Trophy by Manthey-Racing", CarClassCategory.Cup)]
    [InlineData("Hillclimb Icons", CarClassCategory.Other)]
    [InlineData("", CarClassCategory.Unknown)]
    [InlineData(null, CarClassCategory.Unknown)]
    public void Categorize(string? name, CarClassCategory expected) =>
        CarClasses.Categorize(name).Should().Be(expected);

    [Fact]
    public void P2_InsideOtherWords_IsNotLmp2() =>
        CarClasses.Categorize("Group2 Classics").Should().NotBe(CarClassCategory.Lmp2);
}
