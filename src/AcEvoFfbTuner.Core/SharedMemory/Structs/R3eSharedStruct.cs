using System.Runtime.InteropServices;

namespace AcEvoFfbTuner.Core.SharedMemory.Structs;

[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct R3eVector3d
{
    public double X;
    public double Y;
    public double Z;
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct R3eVector3f
{
    public float X;
    public float Y;
    public float Z;
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct R3eOrientation<T>
{
    public T Pitch;
    public T Yaw;
    public T Roll;
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct R3eSectors<T>
{
    public T Sector1;
    public T Sector2;
    public T Sector3;
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct R3eRaceDuration<T>
{
    public T Race1;
    public T Race2;
    public T Race3;
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct R3eTireData<T>
{
    public T FrontLeft;
    public T FrontRight;
    public T RearLeft;
    public T RearRight;
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct R3eTireTemp3<T>
{
    public T Left;
    public T Center;
    public T Right;
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct R3eTireTempInformation
{
    public R3eTireTemp3<float> CurrentTemp;
    public float OptimalTemp;
    public float ColdTemp;
    public float HotTemp;
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct R3eBrakeTemp
{
    public float CurrentTemp;
    public float OptimalTemp;
    public float ColdTemp;
    public float HotTemp;
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct R3ePlayerData
{
    public int UserId;
    public int GameSimulationTicks;
    public double GameSimulationTime;
    public R3eVector3d Position;
    public R3eVector3d Velocity;
    public R3eVector3d LocalVelocity;
    public R3eVector3d Acceleration;
    public R3eVector3d LocalAcceleration;
    public R3eVector3d Orientation;
    public R3eVector3d Rotation;
    public R3eVector3d AngularAcceleration;
    public R3eVector3d AngularVelocity;
    public R3eVector3d LocalAngularVelocity;
    public R3eVector3d GForce;
    public double SteeringForce;
    public double SteeringForcePercentage;
    public double EngineTorque;
    public double CurrentDownforce;
    public double Voltage;
    public double ErsLevel;
    public double PowerMguH;
    public double PowerMguK;
    public double TorqueMguK;
    public R3eTireData<double> SuspensionDeflection;
    public R3eTireData<double> SuspensionVelocity;
    public R3eTireData<double> Camber;
    public R3eTireData<double> RideHeight;
    public double FrontWingHeight;
    public double FrontRollAngle;
    public double RearRollAngle;
    public double ThirdSpringSuspensionDeflectionFront;
    public double ThirdSpringSuspensionVelocityFront;
    public double ThirdSpringSuspensionDeflectionRear;
    public double ThirdSpringSuspensionVelocityRear;
    public double Unused1;
    public double Unused2;
    public double Unused3;
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct R3eFlags
{
    public int Yellow;
    public int YellowCausedIt;
    public int YellowOvertake;
    public int YellowPositionsGained;
    public R3eSectors<int> SectorYellow;
    public float ClosestYellowDistanceIntoTrack;
    public int Blue;
    public int Black;
    public int Green;
    public int Checkered;
    public int White;
    public int BlackAndWhite;
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct R3eCarDamage
{
    public float Engine;
    public float Transmission;
    public float Aerodynamics;
    public float Suspension;
    public float Unused1;
    public float Unused2;
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct R3ePitMenuState
{
    public int Preset;
    public int Penalty;
    public int Driverchange;
    public int Fuel;
    public int FrontTires;
    public int RearTires;
    public int Body;
    public int FrontWing;
    public int RearWing;
    public int Suspension;
    public int ButtonTop;
    public int ButtonBottom;
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct R3eCutTrackPenalties
{
    public float DriveThrough;
    public float StopAndGo;
    public float PitStop;
    public float TimeDeduction;
    public float SlowDown;
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct R3eDRS
{
    public int Equipped;
    public int Available;
    public int NumActivationsLeft;
    public int Engaged;
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct R3ePushToPass
{
    public int Available;
    public int Engaged;
    public int AmountLeft;
    public float EngagedTimeLeft;
    public float WaitTimeLeft;
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct R3eAidSettings
{
    public int Abs;
    public int Tc;
    public int Esp;
    public int Countersteer;
    public int Cornering;
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct R3eDriverInfo
{
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 64)]
    public byte[] Name;
    public int CarNumber;
    public int ClassId;
    public int ModelId;
    public int TeamId;
    public int LiveryId;
    public int ManufacturerId;
    public int UserId;
    public int SlotId;
    public int ClassPerformanceIndex;
    public int EngineType;
    public float CarWidth;
    public float CarLength;
    public float Rating;
    public float Reputation;
    public float Unused1;
    public float Unused2;
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct R3eDriverData
{
    public R3eDriverInfo DriverInfo;
    public int FinishStatus;
    public int Place;
    public int PlaceClass;
    public float LapDistance;
    public float LapDistanceFraction;
    public R3eVector3f Position;
    public int TrackSector;
    public int CompletedLaps;
    public int CurrentLapValid;
    public float LapTimeCurrentSelf;
    public R3eSectors<float> SectorTimeCurrentSelf;
    public R3eSectors<float> SectorTimePreviousSelf;
    public R3eSectors<float> SectorTimeBestSelf;
    public float TimeDeltaFront;
    public float TimeDeltaBehind;
    public int PitStopStatus;
    public int InPitlane;
    public int NumPitstops;
    public R3eCutTrackPenalties Penalties;
    public float CarSpeed;
    public int TireTypeFront;
    public int TireTypeRear;
    public int TireSubtypeFront;
    public int TireSubtypeRear;
    public float BasePenaltyWeight;
    public float AidPenaltyWeight;
    public int DrsState;
    public int PtpState;
    public float VirtualEnergy;
    public int PenaltyType;
    public int PenaltyReason;
    public int EngineState;
    public R3eVector3f Orientation;
    public float Unused1;
    public float Unused2;
    public float Unused3;
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct R3eShared
{
    //////////////////////////////////////////////////////////////////////////
    // Version
    //////////////////////////////////////////////////////////////////////////
    public int VersionMajor;
    public int VersionMinor;
    public int AllDriversOffset;
    public int DriverDataSize;

    //////////////////////////////////////////////////////////////////////////
    // Game State
    //////////////////////////////////////////////////////////////////////////
    public int GameMode;
    public int GamePaused;
    public int GameInMenus;
    public int GameInReplay;
    public int GameUsingVr;
    public int GamePlayerInGarage;

    //////////////////////////////////////////////////////////////////////////
    // High Detail
    //////////////////////////////////////////////////////////////////////////
    public R3ePlayerData Player;

    //////////////////////////////////////////////////////////////////////////
    // Event And Session
    //////////////////////////////////////////////////////////////////////////
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 64)]
    public byte[] TrackName;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 64)]
    public byte[] LayoutName;
    public int TrackId;
    public int LayoutId;
    public float LayoutLength;
    public R3eSectors<float> SectorStartFactors;
    public R3eRaceDuration<int> RaceSessionLaps;
    public R3eRaceDuration<int> RaceSessionMinutes;
    public int EventIndex;
    public int SessionType;
    public int SessionIteration;
    public int SessionLengthFormat;
    public float SessionPitSpeedLimit;
    public int SessionPhase;
    public int StartLights;
    public int TireWearActive;
    public int FuelUseActive;
    public int NumberOfLaps;
    public float SessionTimeDuration;
    public float SessionTimeRemaining;
    public int MaxIncidentPoints;
    public float EventUnused1;
    public float EventUnused2;

    //////////////////////////////////////////////////////////////////////////
    // Pit
    //////////////////////////////////////////////////////////////////////////
    public int PitWindowStatus;
    public int PitWindowStart;
    public int PitWindowEnd;
    public int InPitlane;
    public int PitMenuSelection;
    public R3ePitMenuState PitMenuState;
    public int PitState;
    public float PitTotalDuration;
    public float PitElapsedTime;
    public int PitAction;
    public int NumPitstopsPerformed;
    public float PitMinDurationTotal;
    public float PitMinDurationLeft;

    //////////////////////////////////////////////////////////////////////////
    // Scoring & Timings
    //////////////////////////////////////////////////////////////////////////
    public R3eFlags Flags;
    public int Position;
    public int PositionClass;
    public int FinishStatus;
    public int CutTrackWarnings;
    public R3eCutTrackPenalties Penalties;
    public int NumPenalties;
    public int CompletedLaps;
    public int CurrentLapValid;
    public int TrackSector;
    public float LapDistance;
    public float LapDistanceFraction;
    public float LapTimeBestLeader;
    public float LapTimeBestLeaderClass;
    public R3eSectors<float> SectorTimesSessionBestLap;
    public float LapTimeBestSelf;
    public R3eSectors<float> SectorTimesBestSelf;
    public float LapTimePreviousSelf;
    public R3eSectors<float> SectorTimesPreviousSelf;
    public float LapTimeCurrentSelf;
    public R3eSectors<float> SectorTimesCurrentSelf;
    public float LapTimeDeltaLeader;
    public float LapTimeDeltaLeaderClass;
    public float TimeDeltaFront;
    public float TimeDeltaBehind;
    public float TimeDeltaBestSelf;
    public R3eSectors<float> BestIndividualSectorTimeSelf;
    public R3eSectors<float> BestIndividualSectorTimeLeader;
    public R3eSectors<float> BestIndividualSectorTimeLeaderClass;
    public int IncidentPoints;
    public int LapValidState;
    public int PrevLapValid;
    public float DischargeRate;
    public float BrakeRegen;
    public float Unused1;

    //////////////////////////////////////////////////////////////////////////
    // Vehicle information
    //////////////////////////////////////////////////////////////////////////
    public R3eDriverInfo VehicleInfo;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 64)]
    public byte[] PlayerName;

    //////////////////////////////////////////////////////////////////////////
    // Vehicle State
    //////////////////////////////////////////////////////////////////////////
    public int ControlType;
    public float CarSpeed;
    public float EngineRps;
    public float MaxEngineRps;
    public float UpshiftRps;
    public int Gear;
    public int NumGears;
    public R3eVector3f CarCgLocation;
    public R3eOrientation<float> CarOrientation;
    public R3eVector3f LocalAcceleration;
    public float TotalMass;
    public float FuelLeft;
    public float FuelCapacity;
    public float FuelPerLap;
    public float VirtualEnergyLeft;
    public float VirtualEnergyCapacity;
    public float VirtualEnergyPerLap;
    public float EngineTemp;
    public float EngineOilTemp;
    public float FuelPressure;
    public float EngineOilPressure;
    public float TurboPressure;
    public float Throttle;
    public float ThrottleRaw;
    public float Brake;
    public float BrakeRaw;
    public float Clutch;
    public float ClutchRaw;
    public float SteerInputRaw;
    public int SteerLockDegrees;
    public int SteerWheelRangeDegrees;
    public R3eAidSettings AidSettings;
    public R3eDRS Drs;
    public int PitLimiter;
    public R3ePushToPass PushToPass;
    public float BrakeBias;
    public int DrsNumActivationsTotal;
    public int PtpNumActivationsTotal;
    public float BatterySoC;
    public float WaterLeft;
    public int AbsSetting;
    public int HeadLights;
    public int SteerWheelMaxRotation;

    //////////////////////////////////////////////////////////////////////////
    // Tires
    //////////////////////////////////////////////////////////////////////////
    public int TireType;
    public R3eTireData<float> TireRps;
    public R3eTireData<float> TireSpeed;
    public R3eTireData<float> TireGrip;
    public R3eTireData<float> TireWear;
    public R3eTireData<int> TireFlatspot;
    public R3eTireData<float> TirePressure;
    public R3eTireData<float> TireDirt;
    public R3eTireData<R3eTireTempInformation> TireTemp;
    public int TireTypeFront;
    public int TireTypeRear;
    public int TireSubtypeFront;
    public int TireSubtypeRear;
    public R3eTireData<R3eBrakeTemp> BrakeTemp;
    public R3eTireData<float> BrakePressure;
    public int TractionControlSetting;
    public int EngineMapSetting;
    public int EngineBrakeSetting;
    public float TractionControlPercent;
    public R3eTireData<int> TireOnMtrl;
    public R3eTireData<float> TireLoad;

    //////////////////////////////////////////////////////////////////////////
    // Damage
    //////////////////////////////////////////////////////////////////////////
    public R3eCarDamage CarDamage;

    //////////////////////////////////////////////////////////////////////////
    // Driver Info
    //////////////////////////////////////////////////////////////////////////
    public int NumCars;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 128)]
    public R3eDriverData[] DriverData;
}
