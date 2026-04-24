using System.Runtime.InteropServices;

namespace AcEvoFfbTuner.Core.SharedMemory.Structs;

[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct SPageFileGraphicEvo
{
    public int PacketId;
    public AcEvoStatus Status;
    public ulong FocusedCarIdA;
    public ulong FocusedCarIdB;
    public ulong PlayerCarIdA;
    public ulong PlayerCarIdB;

    public ushort Rpm;
    [MarshalAs(UnmanagedType.U1)]
    public bool IsRpmLimiterOn;
    [MarshalAs(UnmanagedType.U1)]
    public bool IsChangeUpRpm;
    [MarshalAs(UnmanagedType.U1)]
    public bool IsChangeDownRpm;
    [MarshalAs(UnmanagedType.U1)]
    public bool TcActive;
    [MarshalAs(UnmanagedType.U1)]
    public bool AbsActive;
    [MarshalAs(UnmanagedType.U1)]
    public bool EscActive;
    [MarshalAs(UnmanagedType.U1)]
    public bool LaunchActive;
    [MarshalAs(UnmanagedType.U1)]
    public bool IsIgnitionOn;
    [MarshalAs(UnmanagedType.U1)]
    public bool IsEngineRunning;
    [MarshalAs(UnmanagedType.U1)]
    public bool KersIsCharging;
    [MarshalAs(UnmanagedType.U1)]
    public bool IsWrongWay;
    [MarshalAs(UnmanagedType.U1)]
    public bool IsDrsAvailable;
    [MarshalAs(UnmanagedType.U1)]
    public bool BatteryIsCharging;
    [MarshalAs(UnmanagedType.U1)]
    public bool IsMaxKjPerLapReached;
    [MarshalAs(UnmanagedType.U1)]
    public bool IsMaxChargeKjPerLapReached;

    public short DisplaySpeedKmh;
    public short DisplaySpeedMph;
    public short DisplaySpeedMs;
    public float PitspeedingDelta;
    public short GearInt;
    public float RpmPercent;
    public float GasPercent;
    public float BrakePercent;
    public float HandbrakePercent;
    public float ClutchPercent;
    public float SteeringPercent;
    public float FfbStrength;
    public float CarFfbMultiplier;

    public float WaterTemperaturePercent;
    public float WaterPressureBar;
    public float FuelPressureBar;
    public sbyte WaterTemperatureC;
    public sbyte AirTemperatureC;
    public float OilTemperatureC;
    public float OilPressureBar;
    public float ExhaustTemperatureC;
    public float GForcesX;
    public float GForcesY;
    public float GForcesZ;
    public float TurboBoost;
    public float TurboBoostLevel;
    public float TurboBoostPerc;
    public int SteerDegrees;

    public float CurrentKm;
    public uint TotalKm;
    public uint TotalDrivingTimeS;
    public int TimeOfDayHours;
    public int TimeOfDayMinutes;
    public int TimeOfDaySeconds;
    public int DeltaTimeMs;
    public int CurrentLapTimeMs;
    public int PredictedLapTimeMs;
    public float FuelLiterCurrentQuantity;
    public float FuelLiterCurrentQuantityPercent;
    public float FuelLiterPerKm;
    public float KmPerFuelLiter;
    public float CurrentTorque;
    public int CurrentBhp;

    public SmevoTyreState TyreLf;
    public SmevoTyreState TyreRf;
    public SmevoTyreState TyreLr;
    public SmevoTyreState TyreRr;

    public float Npos;
    public float KersChargePerc;
    public float KersCurrentPerc;
    public float ControlLockTime;

    public SmevoDamageState CarDamage;
    public AcEvoCarLocation CarLocation;
    public SmevoPitInfo PitInfo;

    public float FuelLiterUsed;
    public float FuelLiterPerLap;
    public float LapsPossibleWithFuel;
    public float BatteryTemperature;
    public float BatteryVoltage;
    public float InstantaneousFuelLiterPerKm;
    public float InstantaneousKmPerFuelLiter;
    public float GearRpmWindow;

    public SmevoInstrumentation Instrumentation;
    public SmevoInstrumentation InstrumentationMinLimit;
    public SmevoInstrumentation InstrumentationMaxLimit;
    public SmevoElectronics Electronics;
    public SmevoElectronics ElectronicsMinLimit;
    public SmevoElectronics ElectronicsMaxLimit;
    public SmevoElectronics ElectronicsIsModifiable;

    public int TotalLapCount;
    public uint CurrentPos;
    public uint TotalDrivers;
    public int LastLaptimeMs;
    public int BestLaptimeMs;
    public AcEvoFlagType Flag;
    public AcEvoFlagType GlobalFlag;
    public uint MaxGears;
    public AcEvoEngineType EngineType;
    [MarshalAs(UnmanagedType.U1)]
    public bool HasKers;
    [MarshalAs(UnmanagedType.U1)]
    public bool IsLastLap;

    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 33)]
    public byte[] PerformanceModeName;

    public float DiffCoastRawValue;
    public float DiffPowerRawValue;
    public int RaceCutGainedTimeMs;
    public int DistanceToDeadline;
    public float RaceCutCurrentDelta;

    public SmevoSessionState SessionState;
    public SmevoTimingState TimingState;

    public int PlayerPing;
    public int PlayerLatency;
    public int PlayerCpuUsage;
    public int PlayerCpuUsageAvg;
    public int PlayerQos;
    public int PlayerQosAvg;
    public int PlayerFps;
    public int PlayerFpsAvg;

    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 33)]
    public byte[] DriverName;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 33)]
    public byte[] DriverSurname;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 33)]
    public byte[] CarModel;

    [MarshalAs(UnmanagedType.U1)]
    public bool IsInPitBox;
    [MarshalAs(UnmanagedType.U1)]
    public bool IsInPitLane;
    [MarshalAs(UnmanagedType.U1)]
    public bool IsValidLap;

    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 60)]
    public StructVector3[] CarCoordinates;

    public float GapAhead;
    public float GapBehind;
    public byte ActiveCars;
    public float FuelPerLap;
    public float FuelEstimatedLaps;
    public SmevoAssistsState AssistsState;
    public float MaxFuel;
    public float MaxTurboBoost;
    [MarshalAs(UnmanagedType.U1)]
    public bool UseSingleCompound;
}
