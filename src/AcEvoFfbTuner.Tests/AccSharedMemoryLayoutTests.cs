using System.Runtime.InteropServices;
using System.Text;
using AcEvoFfbTuner.Core.SharedMemory;
using AcEvoFfbTuner.Core.SharedMemory.Structs;
using FluentAssertions;
using Xunit;

namespace AcEvoFfbTuner.Tests;

public class AccSharedMemoryLayoutTests
{
    [Fact]
    public void StaticOffsets_MatchTheKunosLayoutSharedWithAc1()
    {
        // ACC's static page is AC1's layout up to trackConfiguration (wchar_t strings).
        AccSharedMemoryReader.StaticTrackOffset.Should().Be((int)Marshal.OffsetOf<SPageFileStaticAC>(nameof(SPageFileStaticAC.Track)));
        AccSharedMemoryReader.StaticMaxRpmOffset.Should().Be((int)Marshal.OffsetOf<SPageFileStaticAC>(nameof(SPageFileStaticAC.MaxRpm)));
        AccSharedMemoryReader.StaticTrackSplineOffset.Should().Be((int)Marshal.OffsetOf<SPageFileStaticAC>(nameof(SPageFileStaticAC.TrackSplineLength)));
        AccSharedMemoryReader.StaticTrackConfigOffset.Should().Be((int)Marshal.OffsetOf<SPageFileStaticAC>(nameof(SPageFileStaticAC.TrackConfiguration)));
    }

    [Fact]
    public void ReadWideString_DecodesUtf16TrackId()
    {
        var buf = new byte[600];
        Encoding.Unicode.GetBytes("mount_panorama").CopyTo(buf, AccSharedMemoryReader.StaticTrackOffset);

        var bytes = AccSharedMemoryReader.ReadWideString(buf, AccSharedMemoryReader.StaticTrackOffset, 33);

        int nul = Array.IndexOf(bytes, (byte)0);
        Encoding.ASCII.GetString(bytes, 0, nul).Should().Be("mount_panorama");
    }

    private static AccGraphics Graphics(int active, int playerId, params (int id, float x, float z)[] cars)
    {
        var g = new AccGraphics { CarCoordinates = new AccCoordinates[60], CarIDs = new int[60], ActiveCars = active, PlayerCarID = playerId };
        for (int i = 0; i < cars.Length; i++)
        {
            g.CarIDs[i] = cars[i].id;
            g.CarCoordinates[i] = new AccCoordinates { X = cars[i].x, Z = cars[i].z };
        }
        return g;
    }

    [Fact]
    public void PlayerFirstCoordinates_MovesThePlayersSlotToIndexZero()
    {
        // ACC slot i belongs to carID[i]; the player is wherever carID == playerCarID.
        var g = Graphics(3, playerId: 1007, (1003, 10, 20), (1005, 30, 40), (1007, 50, 60));

        var coords = AccSharedMemoryReader.PlayerFirstCoordinates(g);

        coords[0].X.Should().Be(50); coords[0].Z.Should().Be(60);
        coords[1].X.Should().Be(10);
        coords[2].X.Should().Be(30);
    }

    [Fact]
    public void PlayerFirstCoordinates_SoloSessionUsesTheOnlyCar()
    {
        var g = Graphics(1, playerId: 0, (42, 5, 6));
        AccSharedMemoryReader.PlayerFirstCoordinates(g)[0].X.Should().Be(5);
    }

    [Fact]
    public void PlayerFirstCoordinates_UnknownPlayerInAFieldLeavesPositionEmpty()
    {
        var g = Graphics(2, playerId: 99, (1, 5, 6), (2, 7, 8));
        AccSharedMemoryReader.PlayerFirstCoordinates(g)[0].X.Should().Be(0);
    }
}
