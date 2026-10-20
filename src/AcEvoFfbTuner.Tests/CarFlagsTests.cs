using AcEvoFfbTuner.Core.SharedMemory;
using FluentAssertions;
using Xunit;

namespace AcEvoFfbTuner.Tests;

public class CarFlagsTests
{
    [Theory]
    // Real LMU vehicle names from a live 20-car session (2026-10-20)
    [InlineData("Cadillac Hertz Team Jota 2026 #12:LM", "12")]
    [InlineData("911GT3R Custom Team 2026 #397", "397")]
    [InlineData("Garage 59 2026 #10:WEC", "10")]
    [InlineData("Kessel Racing 2026 #74:ELMS", "74")]
    [InlineData("Peugeot TotalEnergies #94:LM", "94")]
    [InlineData("Some Car Without Number", "")]
    [InlineData("", "")]
    public void LmuCarNumber_FromVehicleName(string vehicle, string expected) =>
        CarFlags.LmuCarNumber(vehicle).Should().Be(expected);

    [Theory]
    [InlineData(0, 0, false, 0, CarFlag.None)]
    [InlineData(0, 6, false, 0, CarFlag.Blue)]
    [InlineData(0, 0, true, 0, CarFlag.Yellow)]
    [InlineData(0, 6, true, 0, CarFlag.Yellow)]       // safety first: yellow over blue
    [InlineData(0, 0, false, 1, CarFlag.Penalty)]
    [InlineData(0, 6, false, 1, CarFlag.Blue)]        // blue over an outstanding penalty
    [InlineData(1, 6, true, 1, CarFlag.Checkered)]    // finished overrides everything
    [InlineData(2, 0, false, 0, CarFlag.Retired)]     // DNF
    [InlineData(3, 0, false, 0, CarFlag.Retired)]     // DQ
    public void FromLmu(int finish, int flag, bool underYellow, int penalties, CarFlag expected) =>
        CarFlags.FromLmu((sbyte)finish, (byte)flag, underYellow, (short)penalties).Should().Be(expected);

    [Theory]
    [InlineData(0, false, false, CarFlag.None)]
    [InlineData(0, true, false, CarFlag.Yellow)]
    [InlineData(0, false, true, CarFlag.Penalty)]
    [InlineData(1, true, true, CarFlag.Checkered)]
    [InlineData(2, false, false, CarFlag.Retired)]    // DNF
    [InlineData(5, false, false, CarFlag.Retired)]    // DQ
    [InlineData(4, false, false, CarFlag.None)]       // DNS: not on track, no ring
    public void FromR3e(int finish, bool yellowSector, bool penalty, CarFlag expected) =>
        CarFlags.FromR3e(finish, yellowSector, penalty).Should().Be(expected);
}
