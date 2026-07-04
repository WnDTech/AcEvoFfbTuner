using System.Runtime.InteropServices;

namespace AcEvoFfbTuner.Core.SharedMemory.Structs;

[StructLayout(LayoutKind.Sequential, Pack = 4, CharSet = CharSet.Unicode)]
public struct AccCoordinates
{
    public float X;
    public float Y;
    public float Z;
}

[StructLayout(LayoutKind.Sequential, Pack = 4, CharSet = CharSet.Unicode)]
public struct AccGraphics
{
    public int PacketId;
    public int Status;
    public int Session;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 15)] public string CurrentTimeString;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 15)] public string LastTimeString;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 15)] public string BestTimeString;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 15)] public string SplitString;
    public int CompletedLaps;
    public int Position;
    public int CurrentTime;
    public int LastTime;
    public int BestTime;
    public float SessionTimeLeft;
    public float DistanceTraveled;
    public int IsInPit;
    public int CurrentSectorIndex;
    public int LastSectorTime;
    public int NumberOfLaps;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 33)] public string TyreCompound;
    public float ReplayTimeMultiplier;
    public float NormalizedCarPosition;
    public int ActiveCars;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 60)] public AccCoordinates[] CarCoordinates;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 60)] public int[] CarIDs;
    public int PlayerCarID;
    public float PenaltyTime;
    public int Flag;
    public int Penalty;
    public int IdealLineOn;
    public int IsInPitLane;
    public float SurfaceGrip;
    public int MandatoryPitDone;
    public float WindSpeed;
    public float WindDirection;
    public int IsSetupMenuVisible;
    public int MainDisplayIndex;
    public int SecondaryDisplayIndex;
    public int TC;
    public int TCCUT;
    public int EngineMap;
    public int ABS;
    public float FuelXLap;
    public int RainLights;
    public int FlashingLights;
    public int LightsStage;
    public float ExhaustTemperature;
    public int WiperLV;
    public int DriverStintTotalTimeLeft;
    public int DriverStintTimeLeft;
    public int RainTyres;
    public int SessionIndex;
    public float UsedFuel;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 15)] public string DeltaLapTimeString;
    public int DeltaLapTime;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 15)] public string EstimatedLapTimeString;
    public int EstimatedLapTime;
    public int IsDeltaPositive;
    public int Split;
    public int IsValidLap;
    public float FuelEstimatedLaps;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 33)] public string TrackStatus;
    public int MissingMandatoryPits;
    public float Clock;
    public int DirectionLightsLeft;
    public int DirectionLightsRight;
    public int GlobalYellow;
    public int GlobalYellow1;
    public int GlobalYellow2;
    public int GlobalYellow3;
    public int GlobalWhite;
    public int GlobalGreen;
    public int GlobalChequered;
    public int GlobalRed;
    public int MfdTyreSet;
    public float MfdFuelToAdd;
    public float MfdTyrePressureLF;
    public float MfdTyrePressureRF;
    public float MfdTyrePressureLR;
    public float MfdTyrePressureRR;
    public int TrackGripStatus;
    public int RainIntensity;
    public int RainIntensityIn10min;
    public int RainIntensityIn30min;
    public int CurrentTyreSet;
    public int StrategyTyreSet;
    public int GapAhead;
    public int GapBehind;
}
