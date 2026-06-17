using System.Runtime.InteropServices;

namespace AcEvoFfbTuner.Core.SharedMemory.Structs;

public static class LmuFieldReader
{
    public static int ReadI32(byte[] buf, int offset) =>
        offset + 4 <= buf.Length ? BitConverter.ToInt32(buf, offset) : 0;

    public static float ReadF32(byte[] buf, int offset) =>
        offset + 4 <= buf.Length ? BitConverter.ToSingle(buf, offset) : 0f;

    public static double ReadF64(byte[] buf, int offset) =>
        offset + 8 <= buf.Length ? BitConverter.ToDouble(buf, offset) : 0.0;

    public static string ReadStr(byte[] buf, int offset, int maxLen)
    {
        int end = Math.Min(offset + maxLen, buf.Length);
        int nullIdx = offset;
        while (nullIdx < end && buf[nullIdx] != 0) nullIdx++;
        return System.Text.Encoding.ASCII.GetString(buf, offset, nullIdx - offset);
    }
}

/// <summary>
/// Native LMU shared memory structs from SharedMemoryInterface.hpp / InternalsPlugin.hpp.
/// All structs use pack=4 to match the C++ #pragma pack(push, 4) in the SDK headers.
/// The map name is "LMU_Data" (320KB total capacity).
/// </summary>
public static class LmuNative
{
    public const string SmMapName = "LMU_Data";
    public const string SmEventName = "LMU_Data_Event";
    public const string SmLockName = "LMU_SharedMemoryLockData";
    public const string SmLockEvent = "LMU_SharedMemoryLockEvent";
    public const int MaxVehicles = 104;
    public const int MaxPath = 260;

    public static int TelemInfoV01Size => Marshal.SizeOf<LmuTelemInfoV01>();
    public static int ScoringInfoV01Size => Marshal.SizeOf<LmuScoringInfoV01>();
    public static int VehScoringInfoV01Size => Marshal.SizeOf<LmuVehScoringInfoV01>();
    public static int GenericSize => Marshal.SizeOf<LmuSharedMemoryGeneric>();
    public static int PathDataSize => Marshal.SizeOf<LmuSharedMemoryPathData>();
}

// ────────────────────────────────────────────────────────────────
// SharedMemoryGeneric (offset 0 in the MMF, 328 bytes)
// ────────────────────────────────────────────────────────────────
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct LmuSharedMemoryGeneric
{
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 15)]
    public uint[] events;               // uint32_t[15]
    public int gameVersion;             // long = int on Win x64 MSVC
    public float ffbTorque;             // float
    public LmuApplicationState appInfo; // ApplicationStateV01
}

[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct LmuApplicationState
{
    public long hwnd;                   // HWND (8 bytes on x64)
    public uint width;
    public uint height;
    public uint refreshRate;
    public uint windowed;
    public byte optionsLocation;

    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 31)]
    public byte[] optionsPage;

    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 204)]
    public byte[] expansion;
}

// ────────────────────────────────────────────────────────────────
// SharedMemoryPathData (after generic: offset 328, 1300 bytes)
// ────────────────────────────────────────────────────────────────
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct LmuSharedMemoryPathData
{
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = LmuNative.MaxPath)]
    public byte[] userData;

    [MarshalAs(UnmanagedType.ByValArray, SizeConst = LmuNative.MaxPath)]
    public byte[] customVariables;

    [MarshalAs(UnmanagedType.ByValArray, SizeConst = LmuNative.MaxPath)]
    public byte[] stewardResults;

    [MarshalAs(UnmanagedType.ByValArray, SizeConst = LmuNative.MaxPath)]
    public byte[] playerProfile;

    [MarshalAs(UnmanagedType.ByValArray, SizeConst = LmuNative.MaxPath)]
    public byte[] pluginsFolder;
}

// ────────────────────────────────────────────────────────────────
// ScoringInfoV01 (inside SharedMemoryScoringData)
// ────────────────────────────────────────────────────────────────
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct LmuScoringInfoV01
{
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 64)]
    public byte[] trackName;       // char[64]
    public int session;            // long
    public double currentET;
    public double endET;
    public int maxLaps;
    public double lapDist;
    public long resultsStream;     // char* pointer (8 bytes on x64)
    public int numVehicles;
    public byte gamePhase;
    public sbyte yellowFlagState;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 3)]
    public sbyte[] sectorFlag;
    public byte startLight;
    public byte numRedLights;
    public byte inRealtime;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)]
    public byte[] playerName;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 64)]
    public byte[] plrFileName;
    public double darkCloud;
    public double raining;
    public double ambientTemp;
    public double trackTemp;
    public LmuVect3 wind;
    public double minPathWetness;
    public double maxPathWetness;
    public byte gameMode;
    public byte isPasswordProtected;
    public ushort serverPort;
    public uint serverPublicIP;
    public int maxPlayers;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)]
    public byte[] serverName;
    public float startET;
    public double avgPathWetness;
    public float sessionTimeRemaining;
    public float timeOfDay;
    public byte isFixedSetup;
    public byte trackGripLevel;
    public byte cloudCoverage;
    public byte trackLimitsStepsPerPenalty;
    public byte trackLimitsStepsPerPoint;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 187)]
    public byte[] expansion;
    public long vehicle;           // VehicleScoringInfoV01* pointer
}

// ────────────────────────────────────────────────────────────────
// VehicleScoringInfoV01 (array inside SharedMemoryScoringData)
// ────────────────────────────────────────────────────────────────
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct LmuVehScoringInfoV01
{
    public int id;                              // long
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)]
    public byte[] driverName;                   // char[32]
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 64)]
    public byte[] vehicleName;                  // char[64]
    public short totalLaps;
    public sbyte sector;
    public sbyte finishStatus;
    public double lapDist;
    public double pathLateral;
    public double trackEdge;
    public double bestSector1;
    public double bestSector2;
    public double bestLapTime;
    public double lastSector1;
    public double lastSector2;
    public double lastLapTime;
    public double curSector1;
    public double curSector2;
    public short numPitstops;
    public short numPenalties;
    public byte isPlayer;          // bool
    public sbyte control;
    public byte inPits;            // bool
    public byte place;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)]
    public byte[] vehicleClass;
    public double timeBehindNext;
    public int lapsBehindNext;
    public double timeBehindLeader;
    public int lapsBehindLeader;
    public double lapStartET;
    public LmuVect3 pos;
    public LmuVect3 localVel;
    public LmuVect3 localAccel;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 3)]
    public LmuVect3[] ori;
    public LmuVect3 localRot;
    public LmuVect3 localRotAccel;
    public byte headlights;
    public byte pitState;
    public byte serverScored;
    public byte individualPhase;
    public int qualification;
    public double timeIntoLap;
    public double estimatedLapTime;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 24)]
    public byte[] pitGroup;
    public byte flag;
    public byte underYellow;
    public byte countLapFlag;
    public byte inGarageStall;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)]
    public byte[] upgradePack;
    public float pitLapDist;
    public float bestLapSector1;
    public float bestLapSector2;
    public ulong steamID;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)]
    public byte[] vehFilename;
    public short attackMode;
    public byte fuelFraction;
    public byte drsState;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 4)]
    public byte[] expansion;
}

// ────────────────────────────────────────────────────────────────
// TelemVect3 — 3 doubles (24 bytes)
// ────────────────────────────────────────────────────────────────
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct LmuVect3
{
    public double x;
    public double y;
    public double z;
}

// ────────────────────────────────────────────────────────────────
// TelemWheelV01 — per-wheel data (inside TelemInfoV01)
// ────────────────────────────────────────────────────────────────
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct LmuTelemWheelV01
{
    public double suspensionDeflection;
    public double rideHeight;
    public double suspForce;
    public double brakeTemp;
    public double brakePressure;
    public double rotation;              // radians/sec (wheel speed)
    public double lateralPatchVel;
    public double longitudinalPatchVel;
    public double lateralGroundVel;
    public double longitudinalGroundVel;
    public double camber;
    public double lateralForce;
    public double longitudinalForce;
    public double tireLoad;
    public double gripFract;
    public double pressure;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 3)]
    public double[] temperature;       // Kelvin
    public double wear;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)]
    public byte[] terrainName;
    public byte surfaceType;
    public byte flat;
    public byte detached;
    public byte staticUndeflectedRadius;
    public double verticalTireDeflection;
    public double wheelYLocation;
    public double toe;
    public double tireCarcassTemperature;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 3)]
    public double[] tireInnerLayerTemperature;
    public float optimalTemp;
    public byte compoundIndex;
    public byte compoundType;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 18)]
    public byte[] expansion;
}

// ────────────────────────────────────────────────────────────────
// TelemInfoV01 — full player telemetry struct
// ────────────────────────────────────────────────────────────────
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct LmuTelemInfoV01
{
    public int id;                          // long
    public double deltaTime;
    public double elapsedTime;
    public int lapNumber;
    public double lapStartET;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 64)]
    public byte[] vehicleName;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 64)]
    public byte[] trackName;
    public LmuVect3 pos;
    public LmuVect3 localVel;
    public LmuVect3 localAccel;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 3)]
    public LmuVect3[] ori;
    public LmuVect3 localRot;
    public LmuVect3 localRotAccel;
    public int gear;
    public double engineRPM;
    public double engineWaterTemp;
    public double engineOilTemp;
    public double clutchRPM;
    public double unfilteredThrottle;
    public double unfilteredBrake;
    public double unfilteredSteering;
    public double unfilteredClutch;

    // Misc
    public double steeringShaftTorque;
    public double front3rdDeflection;
    public double rear3rdDeflection;

    // Aero
    public double frontWingHeight;
    public double frontRideHeight;
    public double rearRideHeight;
    public double drag;
    public double frontDownforce;
    public double rearDownforce;

    // State/damage
    public double fuel;
    public double engineMaxRPM;
    public byte scheduledStops;
    public byte overheating;
    public byte detached;
    public byte headlights;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 8)]
    public byte[] dentSeverity;
    public double lastImpactET;
    public double lastImpactMagnitude;
    public LmuVect3 lastImpactPos;

    // Expanded
    public double engineTorque;
    public int currentSector;
    public byte speedLimiter;
    public byte maxGears;
    public byte frontTireCompoundIndex;
    public byte rearTireCompoundIndex;
    public double fuelCapacity;
    public byte frontFlapActivated;
    public byte rearFlapActivated;
    public byte rearFlapLegalStatus;
    public byte ignitionStarter;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 18)]
    public byte[] frontTireCompoundName;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 18)]
    public byte[] rearTireCompoundName;
    public byte speedLimiterAvailable;
    public byte antiStallActivated;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 2)]
    public byte[] unused1;
    public float visualSteeringWheelRange;
    public double rearBrakeBias;
    public double turboBoostPressure;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 3)]
    public float[] physicsToGraphicsOffset;
    public float physicalSteeringWheelRange;
    public double deltaBest;
    public double batteryChargeFraction;
    public double electricBoostMotorTorque;
    public double electricBoostMotorRPM;
    public double electricBoostMotorTemperature;
    public double electricBoostWaterTemperature;
    public byte electricBoostMotorState;
    public byte lapInvalidated;
    public byte absActive;
    public byte tcActive;
    public byte speedLimiterActive;
    public byte wiperState;
    public byte tc;
    public byte tcMax;
    public byte tcSlip;
    public byte tcSlipMax;
    public byte tcCut;
    public byte tcCutMax;
    public byte abs;
    public byte absMax;
    public byte motorMap;
    public byte motorMapMax;
    public byte migration;
    public byte migrationMax;
    public byte frontAntiSway;
    public byte frontAntiSwayMax;
    public byte rearAntiSway;
    public byte rearAntiSwayMax;
    public byte liftAndCoastProgress;
    public byte trackLimitsSteps;
    public float regen;
    public float soc;
    public float virtualEnergy;
    public float timeGapCarAhead;
    public float timeGapCarBehind;
    public float timeGapPlaceAhead;
    public float timeGapPlaceBehind;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 30)]
    public byte[] vehicleModel;
    public byte vehicleClass;
    public byte vehicleChampionship;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 20)]
    public byte[] expansion;
    // mWheel[4] — must read manually at end
}
