using System.Runtime.InteropServices;

namespace AcEvoFfbTuner.Core.SharedMemory.Structs;

// ── AC1 Physics Struct ─────────────────────────────────────
// MMF: Local\acpmf_physics
// Exact Kunos Assetto Corsa (AC1) layout, verified against the official
// shared-memory documentation and the mdjarv/assettocorsasharedmemory
// reference (Physics.cs). Pack = 4, sequential.
// NOTE: AC1 has NO per-wheel Mz/Fx/Fy, NO SlipRatio/SlipAngle arrays and NO
// TcInAction/AbsInAction flags — its FFB torque arrives as the single
// column-force value FinalFF. WheelSlip[4] is the slip source.
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct SPageFilePhysicsAC
{
    public int PacketId;
    public float Gas;
    public float Brake;
    public float Fuel;
    public int Gear;            // 0 = reverse, 1 = neutral, 2+ = 1st gear and up
    public int Rpms;
    public float SteerAngle;    // -1..+1 normalized
    public float SpeedKmh;

    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 3)]
    public float[] Velocity;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 3)]
    public float[] AccG;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 4)]
    public float[] WheelSlip;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 4)]
    public float[] WheelLoad;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 4)]
    public float[] WheelsPressure;      // psi
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 4)]
    public float[] WheelAngularSpeed;   // rad/s
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 4)]
    public float[] TyreWear;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 4)]
    public float[] TyreDirtyLevel;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 4)]
    public float[] TyreCoreTemperature;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 4)]
    public float[] CamberRad;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 4)]
    public float[] SuspensionTravel;

    public float Drs;           // 0/1 DRS present+enabled
    public float Tc;            // TC slip-ratio limit setting
    public float Heading;
    public float Pitch;
    public float Roll;
    public float CgHeight;

    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 5)]
    public float[] CarDamage;

    public int NumberOfTyresOut;
    public int PitLimiterOn;
    public float Abs;           // ABS setting/level

    public float KersCharge;
    public float KersInput;
    public int AutoShifterOn;

    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 2)]
    public float[] RideHeight;

    // since 1.5
    public float TurboBoost;
    public float Ballast;
    public float AirDensity;

    // since 1.6
    public float AirTemp;
    public float RoadTemp;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 3)]
    public float[] LocalAngularVelocity;
    public float FinalFF;       // game-computed FFB column torque (-1..+1)

    // since 1.7
    public float PerformanceMeter;
    public int EngineBrake;
    public int ErsRecoveryLevel;
    public int ErsPowerLevel;
    public int ErsHeatCharging;
    public int ErsIsCharging;
    public float KersCurrentKJ;
    public int DrsAvailable;
    public int DrsEnabled;

    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 4)]
    public float[] BrakeTemp;

    // since 1.10
    public float Clutch;

    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 4)]
    public float[] TyreTempI;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 4)]
    public float[] TyreTempM;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 4)]
    public float[] TyreTempO;

    // since 1.10.2
    public int IsAIControlled;

    // since 1.11
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 4)]
    public StructVector3[] TyreContactPoint;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 4)]
    public StructVector3[] TyreContactNormal;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 4)]
    public StructVector3[] TyreContactHeading;
    public float BrakeBias;

    // since 1.12
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 3)]
    public float[] LocalVelocity;
}

// ── AC1 Graphics Struct ──────────────────────────────────────
// MMF: Local\acpmf_graphics
// Exact Kunos layout (mdjarv reference). CarCoordinates is the PLAYER car
// position (float[3]), not 60 entries like EVO/ACC. There are no wind,
// DRS or pit-window fields in AC1 graphics.
[StructLayout(LayoutKind.Sequential, Pack = 4, CharSet = CharSet.Unicode)]
public struct SPageFileGraphicAC
{
    public int PacketId;
    public int Status;          // AC_STATUS: 0 off, 1 replay, 2 live, 3 pause
    public int Session;         // AC_SESSION_TYPE: -1 unknown, 0 practice, ...

    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 15)]
    public string CurrentTime;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 15)]
    public string LastTime;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 15)]
    public string BestTime;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 15)]
    public string Split;

    public int CompletedLaps;
    public int Position;
    public int ICurrentTime;
    public int ILastTime;
    public int IBestTime;
    public float SessionTimeLeft;
    public float DistanceTraveled;
    public int IsInPit;
    public int CurrentSectorIndex;
    public int LastSectorTime;
    public int NumberOfLaps;

    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 33)]
    public string TyreCompound;

    public float ReplayTimeMultiplier;
    public float NormalizedCarPosition;

    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 3)]
    public float[] CarCoordinates;

    public float PenaltyTime;
    public int Flag;            // AC_FLAG_TYPE: 0 none, 1 blue, 2 yellow, ...
    public int IdealLineOn;
    public int IsInPitLane;
    public float SurfaceGrip;

    // since 1.13
    public int MandatoryPitDone;
}

// ── AC1 Static Struct ────────────────────────────────────────
// MMF: Local\acpmf_static
// Exact Kunos layout (mdjarv StaticInfo.cs reference). Note SectorCount +
// MaxTorque/MaxPower sit between PlayerNick and MaxRpm — omitting them
// shifts every later field by 12 bytes (the old struct read MaxRpm=3).
[StructLayout(LayoutKind.Sequential, Pack = 4, CharSet = CharSet.Unicode)]
public struct SPageFileStaticAC
{
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 15)]
    public string SmVersion;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 15)]
    public string AcVersion;

    public int NumberOfSessions;
    public int NumCars;

    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 33)]
    public string CarModel;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 33)]
    public string Track;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 33)]
    public string PlayerName;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 33)]
    public string PlayerSurname;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 33)]
    public string PlayerNick;

    public int SectorCount;

    public float MaxTorque;
    public float MaxPower;
    public int MaxRpm;
    public float MaxFuel;

    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 4)]
    public float[] SuspensionMaxTravel;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 4)]
    public float[] TyreRadius;

    // since 1.5
    public float MaxTurboBoost;
    public float Deprecated1;
    public float Deprecated2;
    public int PenaltiesEnabled;
    public float AidFuelRate;
    public float AidTireRate;
    public float AidMechanicalDamage;
    public int AidAllowTyreBlankets;
    public float AidStability;
    public int AidAutoClutch;
    public int AidAutoBlip;

    // since 1.7.1
    public int HasDRS;
    public int HasERS;
    public int HasKERS;
    public float KersMaxJoules;
    public int EngineBrakeSettingsCount;
    public int ErsPowerControllerCount;

    // since 1.7.2
    public float TrackSplineLength;

    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 15)]
    public string TrackConfiguration;

    // since 1.10.2
    public float ErsMaxJ;

    // since 1.13
    public int IsTimedRace;
    public int HasExtraLap;

    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 33)]
    public string CarSkin;

    public int ReversedGridPositions;
    public int PitWindowStart;
    public int PitWindowEnd;
}
