using AcEvoFfbTuner.Core.SharedMemory.Structs;

namespace AcEvoFfbTuner.Core.FfbProcessing.Models;

public enum FfbChannel
{
    MzFront,
    FxFront,
    FyFront,
    FinalFf
}

public sealed class FfbRawData
{
    public float FinalFf { get; set; }
    public float[] Mz { get; set; } = new float[4];
    public float[] Fx { get; set; } = new float[4];
    public float[] Fy { get; set; } = new float[4];
    public float[] WheelLoad { get; set; } = new float[4];
    public float[] SlipRatio { get; set; } = new float[4];
    public float[] SlipAngle { get; set; } = new float[4];
    public float[] SuspensionTravel { get; set; } = new float[4];
    public float[] AccG { get; set; } = new float[3];
    public float[] LocalAngularVel { get; set; } = new float[3];
    public float SteerAngle { get; set; }
    public float SpeedKmh { get; set; }
    public float KerbVibration { get; set; }
    public float SlipVibrations { get; set; }
    public float RoadVibrations { get; set; }
    public float AbsVibrations { get; set; }
    public int AbsInAction { get; set; }
    public float AbsLevel { get; set; }
    public bool AbsActiveGfx { get; set; }
    public float BrakeInput { get; set; }
    public float GasInput { get; set; }
    public float FfbStrength { get; set; }
    public float CarFfbMultiplier { get; set; }
    public int SteerDegrees { get; set; }
    public int PacketId { get; set; }
    public int Gear { get; set; }
    public float RpmPercent { get; set; }
    public bool IsRpmLimiterOn { get; set; }
    public bool IsChangeUpRpm { get; set; }
    public int Flag { get; set; }
    public float[] TyreContactNormalX { get; set; } = new float[4];
    public float[] TyreContactNormalY { get; set; } = new float[4];
    public float[] TyreContactNormalZ { get; set; } = new float[4];
    public float[] TyreContactPointY { get; set; } = new float[4];
    public float CarX { get; set; }
    public float CarY { get; set; }
    public float CarZ { get; set; }
    public float Npos { get; set; }
    public int CurrentLap { get; set; }
    public bool IsOnTrack { get; set; }
    public float Heading { get; set; }
    public int CarLocationRaw { get; set; }
    public bool IsInPitLane { get; set; }
    public bool IsPitEntry { get; set; }
    public bool IsPitExit { get; set; }
    public bool IsEngineRunning { get; set; }
    public bool IsIgnitionOn { get; set; }
    public AcEvoEngineType EngineType { get; set; }
    public string TyreCompoundFront { get; set; } = "";
    public string TyreCompoundRear { get; set; } = "";
    public bool UseSingleCompound { get; set; }
    public float[] WheelsPressure { get; set; } = new float[4];
    public float[] SuspensionDamage { get; set; } = new float[4];
    public float AirTemp { get; set; }
    public float RoadTemp { get; set; }
    public float[] TyreDirtyLevel { get; set; } = new float[4];
    public float[] TyreGrip { get; set; } = new float[4];
}

public sealed class FfbProcessedData
{
    public float MainForce { get; set; }
    public float VibrationForce { get; set; }
    public float RawFinalFf { get; set; }
    public float ChannelMzFront { get; set; }
    public float ChannelFxFront { get; set; }
    public float ChannelFyFront { get; set; }
    public float ChannelMzRear { get; set; }
    public float ChannelFxRear { get; set; }
    public float ChannelFyRear { get; set; }
    public float ChannelFinalFf { get; set; }
    public float PostLutForce { get; set; }
    public float PostCompressionForce { get; set; }
    public float PostDampingForce { get; set; }
    public float PostOutputGainForce { get; set; }
    public float PostDynamicForce { get; set; }
    public float AutoGainApplied { get; set; }
    public bool IsClipping { get; set; }
    public float SpeedKmh { get; set; }
    public float SteerAngle { get; set; }
    public int PacketId { get; set; }

    /// <summary>
    /// Core steering force (zero-latency path): raw Mz+Fx+Fy after LUT, damping, and gain.
    /// This is the primary self-aligning torque that keeps the wheel centered.
    /// </summary>
    public float CoreForce { get; set; }

    /// <summary>
    /// Detail force (filtered path): slip enhancement, dynamic effects, tyre flex,
    /// vibrations, scrub, LFE — all routed through EQ biquads.
    /// </summary>
    public float DetailForce { get; set; }

    /// <summary>
    /// Wet weather factor: 0 = dry, 1 = full wet. Computed by FfbWetWeather.
    /// </summary>
    public float WetnessFactor { get; set; }

    /// <summary>
    /// Gear shift mute gain: 0.0 = hard mute, 1.0 = full force. Set by ApplyGearShiftFilter.
    /// TelemetryLoop uses this to skip device interpolation during hard mute phases.
    /// </summary>
    public float GearShiftMuteGain { get; set; } = 1.0f;

    public TyreCompoundCategory TyreCategory { get; set; }
    public string TyreCompoundFrontName { get; set; } = "";
    public string TyreCompoundRearName { get; set; } = "";
}
