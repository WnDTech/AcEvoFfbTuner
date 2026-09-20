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
    public float[] DisplayAccG { get; set; } = new float[3];
    public float[] LocalAngularVel { get; set; } = new float[3];
    public float YawAccel { get; set; }
    public float SteerAngle { get; set; }
    public float SpeedKmh { get; set; }
    public float KerbVibration { get; set; }
    public float KerbVibrationLeft { get; set; }
    public float KerbVibrationRight { get; set; }
    public float SlipVibrations { get; set; }
    public float RoadVibrations { get; set; }
    public float AbsVibrations { get; set; }
    public int AbsInAction { get; set; }
    public float AbsLevel { get; set; }
    public bool AbsActiveGfx { get; set; }
    public bool TcActiveGfx { get; set; }
    public int TcinAction { get; set; }
    public float BrakeInput { get; set; }
    public float GasInput { get; set; }
    public float FfbStrength { get; set; }
    public float CarFfbMultiplier { get; set; }
    public int SteerDegrees { get; set; }
    public int PacketId { get; set; }
    public int Gear { get; set; }
    public int EngineRpm { get; set; }
    public float RpmPercent { get; set; }
    public bool IsRpmLimiterOn { get; set; }
    public bool IsPitLimiterOn { get; set; }
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
    public float[] TyreTemp { get; set; } = new float[4];
    public float EngineTorque { get; set; }
    public float[] TireFlatspot { get; set; } = new float[4];
    public float GapAhead { get; set; }
    public float GapBehind { get; set; }
    public int RacePosition { get; set; }
    public int TotalDrivers { get; set; }
    public int[] TireOnMtrl { get; set; } = new int[4];
    public float[] BrakePressure { get; set; } = new float[4];
    public float TractionControlPercent { get; set; }
    public int AidSettingsTc { get; set; }
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
    /// <summary>
    /// Plan D1: decaying peak-hold of |MainForce| for headroom UI.
    /// Additive field; default 0 when the pipeline does not track it (EVO/R3E).
    /// </summary>
    public float PeakHold { get; set; }
    /// <summary>
    /// Plan D1: clip-event rate over the last window (0..1). Suggestion only;
    /// no pipeline auto-changes gains from it.
    /// </summary>
    public float ClipRate { get; set; }
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

    // ── Stage taps for the WIP Pipeline visualizer (canonical 15 stages) ──
    // Additive telemetry only: every pipeline fills the taps that exist in its
    // chain and leaves the rest at 0. Zero means "stage absent in this game",
    // never "no signal" — the page's per-game badge says which is which.
    /// <summary>02 Mix: channel-mixer output before MasterGain/auto-gain scaling.</summary>
    public float StageMixedForce { get; set; }
    /// <summary>06 Gain: post-OutputGain, before centering/shaping overrides.</summary>
    public float StageGainForce { get; set; }
    /// <summary>08 Slip: slip-path contribution added to detail.</summary>
    public float StageSlipDelta { get; set; }
    /// <summary>09 DynFlex: dynamics + flex contribution added to detail
    /// (LMU reports its tyre-temp term here).</summary>
    public float StageDynFlex { get; set; }
    /// <summary>11 SubMods: ABS + road + scrub + rear + offtrack + LFE
    /// (+ R3E pre-EQ synth textures) contribution added to detail.</summary>
    public float StageSubMods { get; set; }
    /// <summary>13 PreClip: filtered core + detail sum before clipper/soft-lock/noise gate.</summary>
    public float StagePreClipForce { get; set; }

    // ── Diagnostic per-effect detail breakdown (R3E debug, snapshot CSV) ──
    public float DiagSlipVib { get; set; }
    public float DiagVibMixer { get; set; }
    public float DiagDynEffects { get; set; }
    public float DiagTyreFlex { get; set; }
    public float DiagSlipEnhancer { get; set; }
    public float DiagLfe { get; set; }
    public float DiagAbsMod { get; set; }
    public float DiagRoadMod { get; set; }
    public float DiagFrontRatio { get; set; }
    public float DiagRearRatio { get; set; }
    public float DiagEngageThr { get; set; }
}
