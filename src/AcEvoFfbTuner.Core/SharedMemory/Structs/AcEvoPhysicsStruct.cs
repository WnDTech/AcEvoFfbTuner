using System.Runtime.InteropServices;

namespace AcEvoFfbTuner.Core.SharedMemory.Structs;

[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct SPageFilePhysicsEvo
{
    public int PacketId;
    public float Gas;
    public float Brake;
    public float Fuel;
    public int Gear;
    public int Rpms;
    public float SteerAngle;
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
    public float[] WheelsPressure;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 4)]
    public float[] WheelAngularSpeed;
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
    public float Drs;
    public float Tc;
    public float Heading;
    public float Pitch;
    public float Roll;
    public float CgHeight;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 5)]
    public float[] CarDamage;
    public int NumberOfTyresOut;
    public int PitLimiterOn;
    public float Abs;
    public float KersCharge;
    public float KersInput;
    public int AutoShifterOn;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 2)]
    public float[] RideHeight;
    public float TurboBoost;
    public float Ballast;
    public float AirDensity;
    public float AirTemp;
    public float RoadTemp;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 3)]
    public float[] LocalAngularVel;
    public float FinalFf;
    public float PerformanceMeter;
    public int EngineBrake;
    public int ErsRecoveryLevel;
    public int ErsPowerLevel;
    public int ErsHeatCharging;
    public int ErsIsCharging;
    public float KersCurrentKj;
    public int DrsAvailable;
    public int DrsEnabled;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 4)]
    public float[] BrakeTemp;
    public float Clutch;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 4)]
    public float[] TyreTempI;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 4)]
    public float[] TyreTempM;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 4)]
    public float[] TyreTempO;
    public int IsAiControlled;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 4)]
    public StructVector3[] TyreContactPoint;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 4)]
    public StructVector3[] TyreContactNormal;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 4)]
    public StructVector3[] TyreContactHeading;
    public float BrakeBias;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 3)]
    public float[] LocalVelocity;
    public int P2pActivations;
    public int P2pStatus;
    public int CurrentMaxRpm;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 4)]
    public float[] Mz;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 4)]
    public float[] Fx;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 4)]
    public float[] Fy;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 4)]
    public float[] SlipRatio;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 4)]
    public float[] SlipAngle;
    public int TcinAction;
    public int AbsInAction;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 4)]
    public float[] SuspensionDamage;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 4)]
    public float[] TyreTemp;
    public float WaterTemp;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 4)]
    public float[] BrakeTorque;
    public int FrontBrakeCompound;
    public int RearBrakeCompound;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 4)]
    public float[] PadLife;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 4)]
    public float[] DiscLife;
    public int IgnitionOn;
    public int StarterEngineOn;
    public int IsEngineRunning;
    public float KerbVibration;
    public float SlipVibrations;
    public float RoadVibrations;
    public float AbsVibrations;
}
