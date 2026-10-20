using AcEvoFfbTuner.Core.SharedMemory;
using FluentAssertions;
using Xunit;

namespace AcEvoFfbTuner.Tests;

public class EvoCarIdsLocatorTests
{
    private const ulong PlayerA = 4688513219233779986UL;  // real ids from graphics_hex_dump.log
    private const ulong PlayerB = 11748560111381591433UL;

    private static byte[] MakePage(int tableOffset, int active, int playerSlot, bool decoyBeforeTable)
    {
        var buf = new byte[4096];
        var rng = new Random(7);
        // Unrelated non-zero fields before the table (fuel, assists, ...).
        for (int i = 0; i < tableOffset; i++) buf[i] = (byte)rng.Next(1, 255);
        if (decoyBeforeTable)
        {
            // A stray copy of the player's ids outside the table must not be taken as it.
            BitConverter.GetBytes(PlayerA).CopyTo(buf, 40);
            BitConverter.GetBytes(PlayerB).CopyTo(buf, 48);
        }
        for (int k = 0; k < active; k++)
        {
            ulong a = k == playerSlot ? PlayerA : (ulong)(1000 + k);
            ulong b = k == playerSlot ? PlayerB : (ulong)(2000 + k);
            BitConverter.GetBytes(a).CopyTo(buf, tableOffset + k * 16);
            BitConverter.GetBytes(b).CopyTo(buf, tableOffset + k * 16 + 8);
        }
        return buf; // entries [active, 60) stay zero
    }

    [Theory]
    [InlineData(124, 12, 0)]
    [InlineData(124, 12, 7)]
    [InlineData(200, 2, 1)]
    [InlineData(68, 60, 59)]
    public void Locates_TableStart_RegardlessOfPlayerSlot(int tableOffset, int active, int playerSlot)
    {
        var buf = MakePage(tableOffset, active, playerSlot, decoyBeforeTable: false);
        SharedMemoryReader.LocateCarIds(buf, buf.Length, PlayerA, PlayerB, active).Should().Be(tableOffset);
    }

    [Fact]
    public void IgnoresStrayPlayerIdOutsideTable()
    {
        var buf = MakePage(256, 10, 4, decoyBeforeTable: true);
        SharedMemoryReader.LocateCarIds(buf, buf.Length, PlayerA, PlayerB, 10).Should().Be(256);
    }

    [Fact]
    public void ReturnsMinusOne_WhenActiveCountDoesNotMatchTable()
    {
        // Game reports 5 active cars but the table holds 12 entries: layout assumption broken.
        var buf = MakePage(124, 12, 3, decoyBeforeTable: false);
        SharedMemoryReader.LocateCarIds(buf, buf.Length, PlayerA, PlayerB, 5).Should().Be(-1);
    }
}
