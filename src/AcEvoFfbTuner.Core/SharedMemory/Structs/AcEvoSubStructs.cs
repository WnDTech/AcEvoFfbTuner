using System.Runtime.InteropServices;

namespace AcEvoFfbTuner.Core.SharedMemory.Structs;

[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct StructVector3
{
    public float X;
    public float Y;
    public float Z;
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct SmevoTyreState
{
    public float Slip;
    [MarshalAs(UnmanagedType.U1)]
    public bool Lock;
    public float TyrePression;
    public float TyreTemperatureC;
    public float BrakeTemperatureC;
    public float BrakePressure;
    public float TyreTemperatureLeft;
    public float TyreTemperatureCenter;
    public float TyreTemperatureRight;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 33)]
    public byte[] TyreCompoundFront;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 33)]
    public byte[] TyreCompoundRear;
    public float TyreNormalizedPressure;
    public float TyreNormalizedTemperatureLeft;
    public float TyreNormalizedTemperatureCenter;
    public float TyreNormalizedTemperatureRight;
    public float BrakeNormalizedTemperature;
    public float TyreNormalizedTemperatureCore;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 128)]
    public byte[] Padding;
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct SmevoDamageState
{
    public float DamageFront;
    public float DamageRear;
    public float DamageLeft;
    public float DamageRight;
    public float DamageCenter;
    public float DamageSuspensionLf;
    public float DamageSuspensionRf;
    public float DamageSuspensionLr;
    public float DamageSuspensionRr;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 92)]
    public byte[] Padding;
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct SmevoPitInfo
{
    public sbyte Damage;
    public sbyte Fuel;
    public sbyte TyresLf;
    public sbyte TyresRf;
    public sbyte TyresLr;
    public sbyte TyresRr;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 58)]
    public byte[] Padding;
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct SmevoElectronics
{
    public sbyte TcLevel;
    public sbyte TcCutLevel;
    public sbyte AbsLevel;
    public sbyte EscLevel;
    public sbyte EbbLevel;
    public float BrakeBias;
    public sbyte EngineMapLevel;
    public float TurboLevel;
    public sbyte ErsDeploymentMap;
    public float ErsRechargeMap;
    [MarshalAs(UnmanagedType.U1)]
    public bool IsErsHeatChargingOn;
    [MarshalAs(UnmanagedType.U1)]
    public bool IsErsOvertakeModeOn;
    [MarshalAs(UnmanagedType.U1)]
    public bool IsDrsOpen;
    public sbyte DiffPowerLevel;
    public sbyte DiffCoastLevel;
    public sbyte FrontBumpDamperLevel;
    public sbyte FrontReboundDamperLevel;
    public sbyte RearBumpDamperLevel;
    public sbyte RearReboundDamperLevel;
    [MarshalAs(UnmanagedType.U1)]
    public bool IsIgnitionOn;
    [MarshalAs(UnmanagedType.U1)]
    public bool IsPitlimiterOn;
    public sbyte ActivePerformanceMode;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 88)]
    public byte[] Padding;
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct SmevoInstrumentation
{
    public sbyte MainLightStage;
    public sbyte SpecialLightStage;
    public sbyte CockpitLightStage;
    public sbyte WiperLevel;
    [MarshalAs(UnmanagedType.U1)]
    public bool RainLights;
    [MarshalAs(UnmanagedType.U1)]
    public bool DirectionLightLeft;
    [MarshalAs(UnmanagedType.U1)]
    public bool DirectionLightRight;
    [MarshalAs(UnmanagedType.U1)]
    public bool FlashingLights;
    [MarshalAs(UnmanagedType.U1)]
    public bool WarningLights;
    [MarshalAs(UnmanagedType.U1)]
    public bool AreHeadlightsVisible;
    public sbyte SelectedDisplayIndex;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)]
    public byte[] DisplayCurrentPageIndex;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 101)]
    public byte[] Padding;
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct SmevoSessionState
{
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 33)]
    public byte[] PhaseName;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 15)]
    public byte[] TimeLeft;
    public int TimeLeftMs;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 15)]
    public byte[] WaitTime;
    public int TotalLap;
    public int CurrentLap;
    public int LightsOn;
    public int LightsMode;
    public float LapLengthKm;
    public int EndSessionFlag;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 15)]
    public byte[] TimeToNextSession;
    [MarshalAs(UnmanagedType.U1)]
    public bool DisconnectedFromServer;
    [MarshalAs(UnmanagedType.U1)]
    public bool RestartSeasonEnabled;
    [MarshalAs(UnmanagedType.U1)]
    public bool UiEnableDrive;
    [MarshalAs(UnmanagedType.U1)]
    public bool UiEnableSetup;
    [MarshalAs(UnmanagedType.U1)]
    public bool IsReadyToNextBlinking;
    [MarshalAs(UnmanagedType.U1)]
    public bool ShowWaitingForPlayers;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 140)]
    public byte[] Padding;
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct SmevoTimingState
{
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 15)]
    public byte[] CurrentLaptime;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 15)]
    public byte[] DeltaCurrent;
    public int DeltaCurrentP;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 15)]
    public byte[] LastLaptime;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 15)]
    public byte[] DeltaLast;
    public int DeltaLastP;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 15)]
    public byte[] BestLaptime;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 15)]
    public byte[] IdealLaptime;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 15)]
    public byte[] TotalTime;
    [MarshalAs(UnmanagedType.U1)]
    public bool IsInvalid;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 137)]
    public byte[] Padding;
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct SmevoAssistsState
{
    public byte AutoGear;
    public byte AutoBlip;
    public byte AutoClutch;
    public byte AutoClutchOnStart;
    public byte ManualIgnitionEStart;
    public byte AutoPitLimiter;
    public byte StandingStartAssist;
    public float AutoSteer;
    public float ArcadeStabilityControl;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 48)]
    public byte[] Padding;
}
