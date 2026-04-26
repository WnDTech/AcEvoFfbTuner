using System.Runtime.InteropServices;

namespace AcEvoFfbTuner.Core.SharedMemory.Structs;

[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct SPageFileStaticEvo
{
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 15)]
    public byte[] SmVersion;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 15)]
    public byte[] AcEvoVersion;
    public AcEvoSessionType Session;
    public byte EventId;
    public byte SessionId;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 33)]
    public byte[] SessionName;
    public AcEvoStartingGrip StartingGrip;
    public float StartingAmbientTemperatureC;
    public float StartingGroundTemperatureC;
    [MarshalAs(UnmanagedType.U1)]
    public bool IsStaticWeather;
    [MarshalAs(UnmanagedType.U1)]
    public bool IsTimedRace;
    [MarshalAs(UnmanagedType.U1)]
    public bool IsOnline;
    public int NumberOfSessions;
    public int NumCars;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 33)]
    public byte[] Nation;
    public float Longitude;
    public float Latitude;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 3)]
    public byte[] _paddingBeforeTrack;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 33)]
    public byte[] Track;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 33)]
    public byte[] TrackConfiguration;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 2)]
    public byte[] _paddingBeforeTrackLen;
    public float TrackLengthM;
    public int MaxRpm;
    public float MaxFuel;
    public float SteerRatio;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 4)]
    public float[] SuspensionMaxTravel;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 33)]
    public byte[] PlayerName;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 33)]
    public byte[] PlayerSurname;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 33)]
    public byte[] PlayerNick;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 33)]
    public byte[] CarModel;
}

public static class StaticFieldReader
{
    public static string GetSmVersion(byte[] buf) => ReadStr(buf, 0, 15);
    public static string GetAcEvoVersion(byte[] buf) => ReadStr(buf, 15, 15);
    public static int GetSession(byte[] buf) => ReadI32(buf, 30);
    public static string GetSessionName(byte[] buf) => ReadStr(buf, 36, 33);
    public static int GetNumberOfSessions(byte[] buf) => ReadI32(buf, 84);
    public static int GetNumCars(byte[] buf) => ReadI32(buf, 88);
    public static string GetNation(byte[] buf) => ReadStr(buf, 92, 33);
    public static float GetLongitude(byte[] buf) => ReadF32(buf, 125);
    public static float GetLatitude(byte[] buf) => ReadF32(buf, 129);
    public static string GetTrack(byte[] buf) => ReadStr(buf, 136, 33);
    public static string GetTrackConfiguration(byte[] buf) => ReadStr(buf, 169, 33);
    public static float GetTrackLengthM(byte[] buf) => ReadF32(buf, 204);
    public static int GetMaxRpm(byte[] buf) => ReadI32(buf, 208);
    public static float GetMaxFuel(byte[] buf) => ReadF32(buf, 212);
    public static float GetSteerRatio(byte[] buf) => ReadF32(buf, 216);

    private static string ReadStr(byte[] buf, int offset, int maxLen)
    {
        int end = Math.Min(offset + maxLen, buf.Length);
        int nullIdx = offset;
        while (nullIdx < end && buf[nullIdx] != 0) nullIdx++;
        return System.Text.Encoding.ASCII.GetString(buf, offset, nullIdx - offset);
    }

    private static int ReadI32(byte[] buf, int offset)
    {
        return offset + 4 <= buf.Length ? BitConverter.ToInt32(buf, offset) : 0;
    }

    private static float ReadF32(byte[] buf, int offset)
    {
        return offset + 4 <= buf.Length ? BitConverter.ToSingle(buf, offset) : 0f;
    }
}
