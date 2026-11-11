namespace AcEvoFfbTuner.Core.FfbProcessing;

/// <summary>
/// Games the coach can be tuned for. Mirrors
/// <c>MainViewModel.SupportedGame</c> without referencing the UI layer.
/// </summary>
public enum CoachGame
{
    Unknown,
    AcEvo,
    Raceroom,
    AssettoCorsa,
    LeMansUltimate,
    AssettoCorsaCompetizione,
    Unsupported
}

/// <summary>
/// Single source of truth for what the FFB Coach may suggest.
///
/// SliderMin/Max are the REAL tuning-UI slider limits (FfbTuningPage.xaml
/// LabeledSlider Minimum/Maximum). Null = no UI slider exists for this
/// parameter (profile-only / advanced / toggle) — the coach must say so
/// instead of pretending the user can drag a slider.
///
/// ApplicableGames mirrors the per-game section visibility in
/// MainViewModel.Game.cs (IsTyreForceSectionVisible, IsDampingSectionVisible,
/// IsVibrationSectionVisible, IsColumnForceGame, ...). A parameter that is
/// hidden or has no effect for the current game must NOT be suggested.
/// </summary>
public sealed record CoachParameterInfo(
    string Name,
    float? SliderMin,
    float? SliderMax,
    CoachGame[] ApplicableGames,
    string PipelinePath,
    string Fixes,
    string Effect);

public static class CoachParameterCatalog
{
    // Game sets mirror the per-slider visibility gates in MainViewModel.Game.cs
    // (feel-based Tuning page, 2026-11 consumer audit): a parameter is listed for a
    // game ONLY if that game's pipeline actually reads it.
    private static readonly CoachGame[] None = [];
    private static readonly CoachGame[] All = [
        CoachGame.AcEvo, CoachGame.Raceroom, CoachGame.AssettoCorsa,
        CoachGame.LeMansUltimate, CoachGame.AssettoCorsaCompetizione];
    private static readonly CoachGame[] Evo = [CoachGame.AcEvo];
    private static readonly CoachGame[] Lmu = [CoachGame.LeMansUltimate];
    private static readonly CoachGame[] R3e = [CoachGame.Raceroom];
    private static readonly CoachGame[] Acc = [CoachGame.AssettoCorsaCompetizione];
    /// <summary>Channel mixer / LUT / centre knee+blend / auto-gain: EVO + ACC (AC1 reader zeroes tyre forces; R3E/LMU bypass the mixer).</summary>
    private static readonly CoachGame[] TyreChannel = [CoachGame.AcEvo, CoachGame.AssettoCorsaCompetizione];
    /// <summary>Slip enhancer: all but AC1 (no slip data).</summary>
    private static readonly CoachGame[] SlipEnh = [
        CoachGame.AcEvo, CoachGame.Raceroom, CoachGame.LeMansUltimate, CoachGame.AssettoCorsaCompetizione];
    /// <summary>Slip-angle effects: real or modelled per-axle slip angle (EVO/ACC tyre data, R3E bicycle model, LMU per-wheel ground velocity). Not AC1.</summary>
    private static readonly CoachGame[] SlipAngleGames = [CoachGame.AcEvo, CoachGame.AssettoCorsaCompetizione, CoachGame.Raceroom, CoachGame.LeMansUltimate];
    private static readonly CoachGame[] Column = [CoachGame.Raceroom, CoachGame.LeMansUltimate];
    private static readonly CoachGame[] CoreForce = [CoachGame.Raceroom, CoachGame.LeMansUltimate, CoachGame.AssettoCorsa];
    private static readonly CoachGame[] BrakeBoost = [
        CoachGame.Raceroom, CoachGame.LeMansUltimate, CoachGame.AssettoCorsa, CoachGame.AssettoCorsaCompetizione];
    private static readonly CoachGame[] NoAccVib = [
        CoachGame.AcEvo, CoachGame.Raceroom, CoachGame.AssettoCorsa, CoachGame.LeMansUltimate];
    private static readonly CoachGame[] RearSlip = [CoachGame.AcEvo, CoachGame.Raceroom, CoachGame.LeMansUltimate];
    private static readonly CoachGame[] SlipVib = [CoachGame.AcEvo, CoachGame.AssettoCorsa, CoachGame.LeMansUltimate];
    private static readonly CoachGame[] LoadFlex = [
        CoachGame.AcEvo, CoachGame.AssettoCorsa, CoachGame.AssettoCorsaCompetizione, CoachGame.Raceroom, CoachGame.LeMansUltimate];
    private static readonly CoachGame[] ContactPatch = [CoachGame.AcEvo, CoachGame.AssettoCorsaCompetizione, CoachGame.Raceroom, CoachGame.LeMansUltimate];
    private static readonly CoachGame[] Damping = [CoachGame.AcEvo, CoachGame.AssettoCorsa, CoachGame.LeMansUltimate];
    private static readonly CoachGame[] DampTuning = [
        CoachGame.AcEvo, CoachGame.AssettoCorsa, CoachGame.LeMansUltimate, CoachGame.AssettoCorsaCompetizione];
    private static readonly CoachGame[] Dynamic = [CoachGame.AcEvo, CoachGame.AssettoCorsa, CoachGame.Raceroom];
    private static readonly CoachGame[] Physics = [
        CoachGame.AcEvo, CoachGame.AssettoCorsa, CoachGame.AssettoCorsaCompetizione];

    private static readonly Dictionary<string, CoachParameterInfo> ByName;

    static CoachParameterCatalog()
    {
        var list = new List<CoachParameterInfo>
        {
            // Paths are "UI: <Section> -> <exact slider label>" from FfbTuningPage.xaml so the coach
            // (and UiLabel) name the control the user actually sees. Game notes live in Effect.
            // ── Overall Strength & Wheel ──
            E("OutputGain", 0f, 20f, All, "UI: Overall Strength & Wheel -> Output Gain",
                "clipping, weak", "Final multiplier for every force. Lower fixes clipping, higher fixes weak feel."),
            E("SoftClipThreshold", 0.1f, 1f, All, "UI: Overall Strength & Wheel -> Max Force Limit (clipping knee)",
                "clipping", "Soft-clip knee. Real ceiling is (1+T)/2. On ACC, Force Gain is applied after it."),
            E("NormalizationScale", 1f, 1000f, All, "UI: Overall Strength & Wheel -> Sensitivity (input divisor)",
                "clipping, weak", "First gain stage, a divisor: LOWER = STRONGER. 1000 = neutral. Prefer Output Gain for strength."),
            E("CoreForceMultiplier", 0f, 4f, CoreForce, "UI: Overall Strength & Wheel -> Main Force Gain",
                "weak, clipping", "Linear gain on the game column torque (R3E/LMU/AC). 1.0 = game force as-is."),
            E("ForceScale", 0.1f, 200f, Lmu, "UI: Overall Strength & Wheel -> LMU Force Scale",
                "clipping, weak", "LMU shaft-torque normaliser used as 120/value: HIGHER = HEAVIER. 120+ all behave the same. No effect in R3E."),
            E("ForceGain", 0f, 5f, Acc, "UI: Overall Strength & Wheel -> Force Gain (ACC)",
                "clipping, weak", "ACC whole-output multiplier applied AFTER the force limiter: high values hard-clip."),
            E("WheelMaxTorqueNm", 1f, 25f, All, "UI: Overall Strength & Wheel -> Wheelbase Max Torque",
                "feel", "Must match the wheelbase's real peak torque (hardware fact, not a feel knob). Only suggest correcting it to the wheelbase spec."),
            E("SteeringLockDegrees", null, null, None, "UI: Overall Strength & Wheel -> Steering Rotation",
                "feel", "Display/telemetry only: match it to the wheel/game rotation. Does not change force."),
            E("SoftLockStrength", 0f, 1.5f, All, "UI: Overall Strength & Wheel -> Soft Lock Strength",
                "feel", "End-stop wall strength (needs Soft Lock on)."),
            E("AutoGainScale", 0.1f, 5f, TyreChannel, "UI: Overall Strength & Wheel -> Auto-Gain Target",
                "clipping, weak", "Auto-gain target level (needs Enable Auto-Gain). EVO/ACC only."),
            E("CompressionPower", null, null, All, "Core: tanh curve (profile-only, no slider)",
                "clipping", "Rounds off peaks. Profile-only — no UI slider; prefer Output Gain / Max Force Limit."),
            E("SignCorrectionEnabled", null, null, All, "UI: Overall Strength & Wheel -> Sign Correction (toggle)",
                "inversion", "Inverts FFB direction. Disabling flips the wheel backwards."),
            // ── Steering Weight & Centre ──
            E("MzFrontGain", -2f, 2f, TyreChannel, "UI: Steering Weight & Centre -> Self-Aligning Torque (Mz)",
                "weak, inversion", "Main steering weight (EVO/ACC; ACC forces 0.5). POSITIVE. Channel saturates near +-1."),
            E("MzScale", 1f, 300f, Evo, "UI: Steering Weight & Centre -> Mz Scale (divisor)",
                "clipping, weak", "EVO only (ACC forces 5). Divisor: LOWER = HEAVIER. Below the car raw peak, auto-normalisation takes over."),
            E("FyFrontGain", -2f, 2f, TyreChannel, "UI: Steering Weight & Centre -> Cornering Load (Fy)",
                "weak", "Lateral cornering load, faded near centre by Centre Blend."),
            E("FyScale", 100f, 20000f, TyreChannel, "UI: Steering Weight & Centre -> Fy Scale (divisor)",
                "clipping, weak", "Divisor: LOWER = heavier cornering load."),
            E("WheelLoadWeighting", 0f, 1f, TyreChannel, "UI: Steering Weight & Centre -> Wheel Load Weighting",
                "feel", "0 = simple average, 1 = follows axle load transfer."),
            E("CenterKneePower", 1f, 3f, TyreChannel, "UI: Steering Weight & Centre -> Centre Softness (knee)",
                "stiff_center", "1 = linear; above 1 = lighter centre, sharper build-up. Below 1 has no effect."),
            E("CenterBlendDegrees", 0f, 10f, TyreChannel, "UI: Steering Weight & Centre -> Centre Blend",
                "stiff_center", "Angle over which Fy fades in from centre."),
            E("CenterSharpnessDegrees", 0f, 10f, Column, "UI: Steering Weight & Centre -> Centre Sharpness",
                "stiff_center", "R3E/LMU. Angle where centring reaches full. 0 = crispest, higher = softer centre."),
            E("GyroGain", -2f, 2f, All, "UI: Steering Weight & Centre -> Gyro Gain",
                "oscillation", "Gyro stabiliser strength (needs Gyro Stabilizer on). Calms wheel shake on straights."),
            E("StraightGain", 0f, 2f, Damping, "UI: Steering Weight & Centre -> Straight Stability",
                "oscillation", "Extra damping near centre above 80 km/h."),
            E("CenterDampFloor", 0f, 0.1f, Damping, "UI: Steering Weight & Centre -> Centre Damping Floor",
                "oscillation", "Damping authority at centre. Fixes on-centre weave. LMU uses 0.03."),
            E("SteeringAngleCompensationEnabled", null, null, R3e, "UI: Steering Weight & Centre -> Steering Angle Comp (RaceRoom) (toggle)",
                "feel", "Legacy +15% high-steer boost for the pre-Historic-Update fixed SAT drop. Off = feel the Historic Update's own dynamic SAT curve."),
            // ── Cornering & Front Grip ──
            E("SlipAngleGain", 0f, 2f, SlipAngleGames, "UI: Cornering & Front Grip -> Slip Angle Feel",
                "low_detail", "Force add as tyres slide sideways. EVO/ACC real tyre slip; R3E per-axle model (front ~0.08 rad at the limit). LMU per-wheel slip from tyre ground velocity (front ~0.03 rad in hard corners, ~0.06 near the limit); AC1 has no slip data."),
            E("SlipAngleShapeGain", 0f, 2f, SlipAngleGames, "UI: Cornering & Front Grip -> Peak Grip Bump",
                "low_detail", "Peak-grip rise and drop-off (understeer cue)."),
            E("SlipThreshold", 0f, 0.3f, SlipEnh, "UI: Cornering & Front Grip -> Slip Trigger Point",
                "low_detail, noise", "Slip needed before slip effects fire. Above 0.3 never fires."),
            E("UseFrontOnly", null, null, SlipEnh, "UI: Cornering & Front Grip -> Front Wheels Only (toggle)",
                "feel", "Slip effects from front tyres only."),
            E("ScrubGain", 0f, 1f, Evo, "UI: Cornering & Front Grip -> Front Tyre Scrub (texture)",
                "buzz, low_detail", "Front scrub grain at the limit. EVO only (needs per-wheel Mz + slip)."),
            E("SlipGain", 0f, 5f, SlipVib, "UI: Cornering & Front Grip -> Tyre Slip Vibration",
                "buzz, low_detail", "Tyre slip vibration. Zeroed by the R3E reader."),
            E("TyreGripScale", 0f, 1f, R3e, "UI: Cornering & Front Grip -> Grip Loss Lightening (RaceRoom)",
                "weak, low_detail", "How much the wheel lightens as front grip goes (understeer cue). 0 = never lightens."),
            E("GripScaleGain", 0f, 1f, Lmu, "UI: Cornering & Front Grip -> Lock/Spin Lightening (LMU)",
                "low_detail", "Lightening on lock-ups/wheelspin."),
            E("TyreTempGain", 0f, 1f, Lmu, "UI: Cornering & Front Grip -> Cold/Hot Tyre Texture (LMU)",
                "low_detail", "Texture change on cold/overheated tyres."),
            E("LateralGGain", 0f, 2f, Dynamic, "UI: Cornering & Front Grip -> Lateral G Weight",
                "low_detail", "Cornering G force add. Capped, above ~1 changes little."),
            E("FlexGain", 0f, 2f, Evo, "UI: Cornering & Front Grip -> Sidewall Flex",
                "low_detail", "EVO only. Carcass flex texture (only EVO supplies the contact data)."),
            E("LoadFlexGain", 0f, 2f, LoadFlex, "UI: Cornering & Front Grip -> Contact Patch Feel",
                "low_detail", "Contact-patch load texture."),
            E("CarcassStiffness", 0.1f, 3f, Evo, "UI: Cornering & Front Grip -> Carcass Stiffness",
                "feel", "EVO only. Divisor: higher = stiffer tyre, less flex."),
            E("FlexSmoothing", 0f, 0.9f, LoadFlex, "UI: Cornering & Front Grip -> Flex Smoothing",
                "noise", "Smoothing on flex forces."),
            E("ContactPatchWeight", 0f, 1f, ContactPatch, "UI: Cornering & Front Grip -> Weight Transfer Influence",
                "low_detail", "Lateral load-transfer term in contact-patch feel."),
            // ── Oversteer & Slides ──
            E("RearSlipGain", 0f, 1f, RearSlip, "UI: Oversteer & Slides -> Rear Slip Warning",
                "buzz, low_detail", "Rear slip rumble (early oversteer warning)."),
            E("SelfSteerGain", 0f, 1f, Lmu, "UI: Oversteer & Slides -> Slide Self-Steer (LMU)",
                "low_detail", "How strongly the wheel counter-steers itself in a slide."),
            E("RearFyScale", 0f, 1f, Lmu, "UI: Oversteer & Slides -> Rear Slip Texture (LMU)",
                "low_detail", "LMU rear warning from rear Fy + yaw accel. Capped near 1."),
            E("RearGripDetailGain", 0f, 1f, Lmu, "UI: Oversteer & Slides -> Rear Grip Drop (LMU)",
                "low_detail", "LMU rear warning from rear grip drop."),
            E("YawOnsetGain", 0f, 2f, Lmu, "UI: Oversteer & Slides -> Slide Onset Kick (LMU)",
                "low_detail", "Slide-onset kick. Needs Onset Threshold above 0."),
            E("YawOnsetThreshold", 0f, 10f, Lmu, "UI: Oversteer & Slides -> Onset Threshold (LMU)",
                "low_detail", "Yaw accel (rad/s2) to fire the kick. 0 = never. Start ~2."),
            E("YawOnsetDecay", null, null, Lmu, "Detail: LMU onset decay (profile-only)",
                "feel", "Onset-kick decay rate. No UI slider."),
            E("YawOnsetMax", null, null, Lmu, "Detail: LMU onset cap (profile-only)",
                "feel", "Onset-kick output cap. No UI slider."),
            E("YawRateGain", 0f, 2f, Dynamic, "UI: Oversteer & Slides -> Yaw Rate Force",
                "low_detail", "Force from car rotation rate."),
            E("MzRearGain", -2f, 2f, TyreChannel, "UI: Oversteer & Slides -> Rear Self-Aligning (Mz)",
                "feel", "Rear Mz. Usually 0."),
            E("FyRearGain", -2f, 2f, TyreChannel, "UI: Oversteer & Slides -> Rear Lateral Load (Fy)",
                "feel", "Rear lateral load. Usually 0."),
            // ── Braking & Acceleration ──
            E("FxFrontGain", -2f, 2f, TyreChannel, "UI: Braking & Acceleration -> Braking/Drive Force (Fx)",
                "weak", "Front longitudinal force through the wheel."),
            E("FxScale", 100f, 20000f, TyreChannel, "UI: Braking & Acceleration -> Fx Scale (divisor)",
                "clipping, weak", "Divisor: LOWER = stronger braking/accel feel."),
            E("BrakeBoostGain", 0f, 1f, BrakeBoost, "UI: Braking & Acceleration -> Brake Weight Boost",
                "feel", "Not EVO. Extra steering weight under braking. Live in R3E; other games apply on profile save."),
            E("BrakeBoostThreshold", 0f, 0.5f, BrakeBoost, "UI: Braking & Acceleration -> Boost Starts At (pedal)",
                "feel", "Brake pedal fraction where boost starts."),
            E("BrakeLoadBias", 0f, 1f, Lmu, "UI: Braking & Acceleration -> Front Load Brake Bias (LMU)",
                "feel", "Blends real front load into brake boost."),
            E("LongitudinalGGain", 0f, 2f, Dynamic, "UI: Braking & Acceleration -> Longitudinal G Weight",
                "low_detail", "Braking/accel G force add. Capped."),
            E("SlipRatioGain", 0f, 2f, SlipEnh, "UI: Braking & Acceleration -> Lock-up / Wheelspin Feel",
                "low_detail", "Force add on lock-ups and wheelspin. Capped at ~30% of core force."),
            E("FlatspotGain", 0f, 2f, R3e, "UI: Braking & Acceleration -> Flatspot Thump (RaceRoom)",
                "low_detail", "Once-per-revolution thump from a flat-spotted tyre."),
            E("AbsGain", 0f, 3f, NoAccVib, "UI: Braking & Acceleration -> ABS Vibration",
                "buzz, low_detail", "ABS pulses. Above ~2 starts clipping."),
            E("AbsPulseAmplitude", 0f, 1f, NoAccVib, "UI: Braking & Acceleration -> ABS Pulse Punch",
                "buzz", "Sharpness of each ABS pulse."),
            E("BrakePressureGain", 0f, 2f, R3e, "UI: Braking & Acceleration -> Brake Bite Texture (RaceRoom)",
                "low_detail", "Brake-pressure texture through the rim."),
            E("TcWheelGain", 0f, 2f, All, "UI: Braking & Acceleration -> TC Feel Strength",
                "low_detail", "Traction-control cut felt in the wheel (needs TC Wheel Feel on)."),
            E("TcFeelGain", 0f, 2f, R3e, "UI: Braking & Acceleration -> TC Release (RaceRoom)",
                "low_detail", "R3E TC-cut release feel."),
            E("SynthAbsEnabled", null, null, Lmu, "UI: Braking & Acceleration -> Synthesise ABS (LMU, experimental) (toggle)",
                "feel", "LMU ABS-flag synthesis. Experimental."),
            E("SynthTcEnabled", null, null, Lmu, "UI: Braking & Acceleration -> Synthesise TC (LMU, experimental) (toggle)",
                "feel", "LMU TC-flag synthesis. Experimental."),
            E("GearSpikeThreshold", null, null, All, "Core: gear-shift filter (numeric, no slider)",
                "snap", "RPM gate for shift-spike mute."),
            // ── Kerbs, Road & Off-Track ──
            E("MasterGain", 0f, 5f, NoAccVib, "UI: Kerbs, Road & Off-Track -> Vibration Master",
                "buzz, low_detail", "Volume for all vibrations."),
            E("KerbGain", 0f, 5f, NoAccVib, "UI: Kerbs, Road & Off-Track -> Kerb Strikes",
                "buzz, low_detail", "Kerb/rumble strip intensity."),
            E("RoadTextureGain", 0f, 2f, Lmu, "UI: Kerbs, Road & Off-Track -> Kerb & Bump Knocks (LMU)",
                "low_detail", "LMU kerb and bump knock strength."),
            E("RoadTextureDecayMs", 3f, 80f, Lmu, "UI: Kerbs, Road & Off-Track -> Knock Sharpness (LMU)",
                "buzz", "Knock decay in ms. Short = crisp separate hits, long = blurred."),
            E("RoadGain", 0f, 5f, NoAccVib, "UI: Kerbs, Road & Off-Track -> Road Vibration",
                "buzz, low_detail", "Road surface vibration."),
            E("SuspensionRoadGain", 0f, 5f, NoAccVib, "UI: Kerbs, Road & Off-Track -> Suspension Road Texture",
                "buzz, low_detail", "Suspension road texture in the main force."),
            E("RoadCompressionGain", 0f, 2f, R3e, "UI: Kerbs, Road & Off-Track -> Road Compression (RaceRoom)",
                "low_detail", "Heave weight through compressions."),
            E("BumpTwistGain", 0f, 2f, R3e, "UI: Kerbs, Road & Off-Track -> Bump Twist (RaceRoom)",
                "low_detail", "One-sided bump twist of the rim."),
            E("SuspensionGain", 0f, 2f, Dynamic, "UI: Kerbs, Road & Off-Track -> Bump Feel",
                "low_detail", "Force from rapid suspension compression."),
            E("SuspRoadScale", 0f, 2f, Lmu, "UI: Kerbs, Road & Off-Track -> Suspension Road Layer (LMU)",
                "low_detail", "LMU susp-delta road layer scale."),
            E("UseMixerSuspRoad", null, null, Lmu, "UI: Kerbs, Road & Off-Track -> Suspension Road Layer on (LMU) (toggle)",
                "low_detail", "LMU mixer road layer gate."),
            E("OfftrackGain", 0f, 2f, NoAccVib, "UI: Kerbs, Road & Off-Track -> Off-Track Rumble",
                "buzz, low_detail", "Grass/gravel feel strength."),
            E("OfftrackSeverityScale", 0f, 10f, NoAccVib, "UI: Kerbs, Road & Off-Track -> Off-Track Roughness",
                "buzz", "How violent off-track texture is."),
            E("SurfaceFeelGain", 0f, 2f, R3e, "UI: Kerbs, Road & Off-Track -> Surface Material Feel (RaceRoom)",
                "low_detail", "Grass/gravel/rumble-strip material texture."),
            E("KerbStrikeDecayMs", 3f, 100f, R3e, "Core: R3E kerb-strike decay (profile-only)",
                "feel", "Knock decay time in ms. Short = separate rigid hits per ridge; long = blur into wobble. No UI slider."),
            E("CurbSeverityScale", null, null, NoAccVib, "Detail: vibration mixer (profile-only, no slider)",
                "buzz", "Curb impact harshness. No UI slider."),
            E("ScrubForceScale", null, null, NoAccVib, "Detail: vibration mixer (profile-only, no slider)",
                "buzz, low_detail", "Scrub force normalization. No UI slider."),
            E("RearSlipForceScale", null, null, NoAccVib, "Detail: vibration mixer (profile-only, no slider)",
                "buzz, low_detail", "Rear slip force normalization. No UI slider."),
            // ── Engine & Chassis Rumble ──
            E("LfeGain", 0f, 1f, All, "UI: Engine & Chassis Rumble -> Rumble Strength",
                "buzz, low_detail", "Engine/chassis rumble volume (needs Enable LFE)."),
            E("LfeFrequency", 3f, 30f, All, "UI: Engine & Chassis Rumble -> Rumble Pitch",
                "buzz", "Rumble frequency in Hz."),
            E("LfeRpmDrive", 0f, 1f, All, "UI: Engine & Chassis Rumble -> Engine RPM Drive",
                "buzz", "How much engine RPM drives the rumble."),
            E("EngineTorqueLfeMod", 0f, 1f, R3e, "UI: Engine & Chassis Rumble -> Engine Torque Drive (RaceRoom)",
                "buzz", "How much engine torque drives the rumble."),
            E("LfeSuspensionDrive", 0f, 1f, All, "UI: Engine & Chassis Rumble -> Suspension Drive",
                "buzz", "How much suspension movement drives the rumble."),
            E("LfeSpeedScaling", 0f, 1f, All, "UI: Engine & Chassis Rumble -> Speed Drive",
                "buzz", "How much car speed drives the rumble."),
            // ── Smoothness & Damping ──
            E("ViscousDamping", 0f, 1f, Damping, "UI: Smoothness & Damping -> Damping",
                "oscillation", "Resists fast wheel movement. 0.15 default, 0.25-0.40 calms a nervous wheel. Dulls detail."),
            E("SpeedDamping", 0f, 2f, Damping, "UI: Smoothness & Damping -> Speed Damping",
                "oscillation", "Speed-scaled damping. Total damping is capped, so above ~0.5 changes little."),
            E("Friction", 0f, 2f, Damping, "UI: Smoothness & Damping -> Friction",
                "oscillation", "Constant drag. Capped, above ~0.5 changes little. Too much feels numb."),
            E("Inertia", 0f, 2f, DampTuning, "UI: Smoothness & Damping -> Inertia",
                "oscillation", "Resists quick direction changes. Capped, above ~0.5 changes little."),
            E("MaxSpeedReference", 50f, 500f, Damping, "UI: Smoothness & Damping -> Speed Damping Full At",
                "oscillation", "Car speed where speed damping reaches full."),
            E("SteerVelocityReference", 1f, 50f, DampTuning, "UI: Smoothness & Damping -> Steering Speed Reference",
                "oscillation", "Steering speed at which damping reaches full."),
            E("VelocityDeadzone", 0f, 0.5f, DampTuning, "UI: Smoothness & Damping -> Damping Deadzone",
                "oscillation, stiff_center", "Steering speed below which damping does nothing."),
            E("MaxSlewRate", 0.03f, 1f, All, "UI: Smoothness & Damping -> Detail Sharpness (slew limit)",
                "snap, oscillation", "Per-frame limit on DETAIL path only (core is zero-latency). Default 0.85. Lower = rounder."),
            E("NoiseFloor", 0f, 0.05f, All, "UI: Smoothness & Damping -> Noise Floor",
                "noise", "Forces below this are silenced."),
            E("CoreSmoothing", 0f, 0.5f, CoreForce, "UI: Smoothness & Damping -> Steering Force Smoothing",
                "noise, snap", "R3E/LMU/AC. Smoothing on the column force. Adds latency."),
            E("DetailSmoothing", 0f, 0.8f, CoreForce, "UI: Smoothness & Damping -> Texture Smoothing",
                "noise, buzz", "R3E/LMU/AC. Smoothing on the detail/texture layer."),
            E("CenterSuppressionDegrees", null, null, None, "RESERVED - not read by any pipeline, no UI slider",
                "stiff_center", "NO EFFECT in any game. Use Centre Softness (EVO/ACC) or Centre Sharpness (R3E/LMU)."),
            E("HysteresisThreshold", null, null, None, "RESERVED - not read by the pipelines, no UI slider",
                "noise, oscillation", "NO EFFECT. Use Noise Floor."),
            E("HysteresisWatchdogFrames", null, null, All, "Core: hysteresis timing (profile-only)",
                "noise", "Frames before hysteresis engages. No UI slider."),
            E("LowSpeedSmoothKmh", null, null, None, "Diagnostics only - no effect on output, no UI slider",
                "noise", "NO EFFECT on the wheel in any game. Use Damping or Steering Force Smoothing."),
            // ── R3E profile-only core shaping ──
            E("GripReleaseRate", 0.5f, 20f, R3e, "Core: R3E grip reload limiter (profile-only)",
                "snap", "Upward recovery rate of the grip scale after unloads. Lower = softer touchdown landings. No UI slider."),
            E("BrakeShimmerGain", 0f, 3f, R3e, "Core: R3E braking shimmer (profile-only)",
                "feel", "Brake-gated tremble amplitude. 0 = steady weight only. No UI slider."),
            E("CoreFloor", 0f, 0.3f, R3e, "Core: R3E core floor (profile-only)",
                "feel", "Minimum column-torque magnitude before grip scaling. Keeps tissue through center crossings. No UI slider."),
            E("CenterDirectionBlend", 0.002f, 0.1f, R3e, "Core: R3E center blend (profile-only)",
                "feel", "Fraction of full lock over which centering direction ramps through zero. Wider = softer crossings. No UI slider."),
            // ── Parked & Low Speed (no game gate on the page) ──
            E("StaticFrictionGain", 0f, 2f, All, "UI: Parked & Low Speed -> Parked Steering Weight",
                "feel", "Overall elastic feel strength."),
            E("StaticFrictionEngineOffScale", 0f, 2f, All, "UI: Parked & Low Speed -> Engine Off Weight",
                "feel", "Overall scale when car is off."),
            E("StaticFrictionEngineOnScale", 0f, 1f, All, "UI: Parked & Low Speed -> Engine On Weight",
                "feel", "Overall scale when car is on."),
            E("StaticFrictionMaxElasticStretch", 0.001f, 0.05f, All, "UI: Parked & Low Speed -> Elastic Twist",
                "feel", "Max stretch before breakaway."),
            E("StaticFrictionSpringStiffness", 1f, 50f, All, "UI: Parked & Low Speed -> Spring Stiffness",
                "feel", "Centering spring rate."),
            E("StaticFrictionKineticFrictionBase", 0f, 1f, All, "UI: Parked & Low Speed -> Sliding Friction",
                "feel", "Sliding friction feel."),
            E("StaticFrictionEngineOffDamping", 0f, 0.5f, All, "UI: Parked & Low Speed -> Engine Off Damping",
                "feel", "Extra damping when car is off."),
            E("StaticFrictionEngineOnDamping", 0f, 0.2f, All, "UI: Parked & Low Speed -> Engine On Damping",
                "feel", "Damping reduction when car is on."),
            E("StaticFrictionActiveDecay", 0.5f, 1f, All, "UI: Parked & Low Speed -> Hold While Turning",
                "feel", "How fast elastic force builds when turning."),
            E("StaticFrictionReturnDecay", 0.1f, 0.99f, All, "UI: Parked & Low Speed -> Hold On Return",
                "feel", "How fast elastic force returns to center."),
            E("StaticFrictionOutputSmoothAlpha", 0.05f, 1f, All, "UI: Parked & Low Speed -> Output Raw-ness",
                "feel", "Smoothing on static friction output."),
            E("StaticFrictionBreakoutGain", 0f, 1f, All, "UI: Parked & Low Speed -> Breakout Judder",
                "feel", "Stick-slip judder near breakaway."),
            E("StaticFrictionBreakoutFrequencyHz", 20f, 70f, All, "UI: Parked & Low Speed -> Judder Pitch",
                "feel", "Stick-slip judder frequency."),
            E("StaticFrictionScrubGain", 0f, 1f, All, "UI: Parked & Low Speed -> Scrub Grain",
                "feel", "Tyre-twist grain texture."),
            E("StaticFrictionLoadAsymmetryGain", 0f, 1f, All, "UI: Parked & Low Speed -> Left/Right Load Difference",
                "feel", "Front load-asymmetry weight."),
            E("StaticFrictionPressureSensitivity", 0f, 1f, All, "UI: Parked & Low Speed -> Tyre Pressure Feel",
                "feel", "Tyre-pressure sensitivity."),
            // ── Grip guard (profile-only) ──
            E("GripGuardEnabled", null, null, All, "Core: grip guard (toggle, no slider)",
                "feel", "Grip-dependent attenuation toggle."),
            E("GripGuardPeakSlipAngle", null, null, All, "Core: grip guard (profile-only)",
                "feel", "Slip angle at peak grip. No UI slider."),
            E("GripGuardAttenuationStrength", null, null, All, "Core: grip guard (profile-only)",
                "feel", "Force reduction past peak grip. No UI slider."),
            E("GripGuardMechanicalTrailGain", null, null, All, "Core: grip guard (profile-only)",
                "feel", "Simulated mechanical trail. No UI slider."),
            // ── Crashes & Tyre Damage (EVO/AC/ACC) ──
            E("CrashEnabled", null, null, Physics, "UI: Crashes & Tyre Damage -> Enable Crash Forces (toggle)",
                "snap", "Crash kick toggle."),
            E("CrashImpactGain", 0f, 1f, Physics, "UI: Crashes & Tyre Damage -> Impact Kick",
                "snap", "Crash kick size."),
            E("CrashSafetyClamp", 0.1f, 1f, Physics, "UI: Crashes & Tyre Damage -> Safety Clamp",
                "snap", "Absolute cap on the kick. No effect above Impact Kick."),
            E("CrashTriggerThresholdG", 1f, 10f, Physics, "UI: Crashes & Tyre Damage -> Trigger Threshold",
                "snap", "G spike to count as a crash. Higher = fewer triggers."),
            E("CrashDecayRate", 0.5f, 0.98f, Physics, "UI: Crashes & Tyre Damage -> Kick Length",
                "snap", "How long the kick lasts. Higher = longer."),
            E("CrashMinSpeedKmh", 0f, 50f, Physics, "UI: Crashes & Tyre Damage -> Min Speed",
                "snap", "Below this speed crashes never kick."),
            E("TyreConditionEnabled", null, null, Physics, "UI: Crashes & Tyre Damage -> Enable Tyre Condition Effects (toggle)",
                "feel", "Blowout / pressure loss / damage pull toggle."),
            E("BlowoutVibrationGain", 0f, 1f, Physics, "UI: Crashes & Tyre Damage -> Blowout Shake",
                "buzz", "Blowout shake."),
            E("PressureLossGain", 0f, 1f, Physics, "UI: Crashes & Tyre Damage -> Pressure Loss Rumble",
                "feel", "Pressure-loss rumble."),
            E("DamageAsymmetryGain", 0f, 1f, Physics, "UI: Crashes & Tyre Damage -> Damage Pull",
                "feel", "Pull toward the damaged side."),
            E("BlowoutPressureThreshold", 0.2f, 0.7f, Physics, "UI: Crashes & Tyre Damage -> Blowout Pressure",
                "feel", "Pressure fraction counted as a blowout."),
            E("MaxBlowoutAmplitude", 0f, 0.5f, Physics, "UI: Crashes & Tyre Damage -> Max Blowout Shake",
                "buzz", "Cap on blowout shake."),
            // ── Wet Weather (EVO/AC/ACC) ──
            E("WetWeatherEnabled", null, null, Physics, "UI: Wet Weather -> Enable Wet Weather (toggle)",
                "feel", "Wet FFB adjustments. No effect in R3E/LMU."),
            E("WetRoadVibSuppression", 0f, 1f, Physics, "UI: Wet Weather -> Road Detail Reduction",
                "buzz", "Road vibration removed in rain."),
            E("WetCurbSuppression", 0f, 1f, Physics, "UI: Wet Weather -> Kerb Reduction",
                "buzz", "Kerb vibration removed in rain."),
            E("WetScrubSuppression", 0f, 1f, Physics, "UI: Wet Weather -> Scrub Reduction",
                "buzz", "Scrub texture removed in rain."),
            E("WetManualIntensity", 0f, 1f, Physics, "UI: Wet Weather -> Manual Intensity",
                "feel", "Wetness used when auto-detect is off."),
            E("WetPeakSlipAngleMultiplier", 1f, 3f, TyreChannel, "UI: Wet Weather -> Wet Slip Curve Width",
                "feel", "EVO/ACC. Only affects Peak Grip Bump."),
            E("WetDampingReduction", 0f, 1f, TyreChannel, "UI: Wet Weather -> Damping Reduction",
                "feel", "EVO/ACC. Speed damping reduction in rain."),
            E("WetNoiseFloorSuppression", 0f, 1f, Physics, "UI: Wet Weather -> Noise Floor Boost",
                "noise", "Raises the noise floor in rain."),
            E("WetHydroplaningSpeedThreshold", 60f, 250f, Physics, "UI: Wet Weather -> Hydroplaning From",
                "feel", "Speed (km/h) where hydroplaning lightness starts."),
            E("WetHydroplaningMaxAttenuation", 0f, 0.8f, Physics, "UI: Wet Weather -> Hydroplaning Lightness",
                "feel", "Max force removed while hydroplaning."),
            // ── LMU telemetry synthesis ──
            E("SynthYawEnabled", null, null, Lmu, "Telemetry synthesis toggle",
                "low_detail", "LMU yaw-accel synthesis. Measurement only."),
        };

        ByName = new Dictionary<string, CoachParameterInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in list)
            ByName[Normalize(p.Name)] = p;
    }

    private static CoachParameterInfo E(
        string name, float? min, float? max, CoachGame[] games,
        string path, string fixes, string effect)
        => new(name, min, max, games, path, fixes, effect);

    public static string Normalize(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "";
        return new string(name.Where(c => !char.IsWhiteSpace(c)).ToArray()).ToLowerInvariant();
    }

    public static bool TryGet(string name, out CoachParameterInfo? info)
        => ByName.TryGetValue(Normalize(name), out info);

    /// <summary>All catalog entries (for name resolution / tests).</summary>
    public static IEnumerable<CoachParameterInfo> Entries => ByName.Values;

    /// <summary>
    /// The control label exactly as shown on the Tuning page ("Centre Damping Floor"),
    /// or the internal name when the parameter has no UI control.
    /// </summary>
    public static string UiLabel(CoachParameterInfo info)
    {
        var path = info.PipelinePath;
        if (!path.StartsWith("UI:", StringComparison.Ordinal)) return info.Name;
        int i = path.IndexOf("-> ", StringComparison.Ordinal);
        if (i < 0) return info.Name;
        var label = path[(i + 3)..];
        if (label.EndsWith(" (toggle)", StringComparison.Ordinal)) label = label[..^" (toggle)".Length];
        return label;
    }

    /// <summary>The Tuning page section ("Steering Weight & Centre"), or "" when there is no UI control.</summary>
    public static string UiSection(CoachParameterInfo info)
    {
        var path = info.PipelinePath;
        if (!path.StartsWith("UI:", StringComparison.Ordinal)) return "";
        int i = path.IndexOf(" -> ", StringComparison.Ordinal);
        return i < 0 ? "" : path[3..i].Trim();
    }

    /// <summary>UI label for any parameter name the coach or the model produced; passes unknown names through.</summary>
    public static string DisplayName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return name ?? "";
        return TryResolve(name, out var info) && info != null ? UiLabel(info) : name;
    }

    /// <summary>"Steering Weight &amp; Centre -> Centre Damping Floor", or the label alone when there is no UI section.</summary>
    public static string DisplayLocation(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return name ?? "";
        if (!TryResolve(name, out var info) || info == null) return name;
        var section = UiSection(info);
        return section.Length > 0 ? $"{section} -> {UiLabel(info)}" : UiLabel(info);
    }

    /// <summary>
    /// Resolve a parameter by internal name OR by its Tuning-page label (the model is told
    /// to speak in labels, so it may hand back "Centre Damping Floor" as the parameter).
    /// </summary>
    public static bool TryResolve(string name, out CoachParameterInfo? info)
    {
        if (TryGet(name, out info)) return true;
        var key = Normalize(name);
        foreach (var p in ByName.Values)
        {
            if (!p.PipelinePath.StartsWith("UI:", StringComparison.Ordinal)) continue;
            if (Normalize(UiLabel(p)) == key) { info = p; return true; }
        }
        info = null;
        return false;
    }

    public static float ClampToSlider(CoachParameterInfo info, float value)
    {
        if (info.SliderMin.HasValue) value = Math.Max(info.SliderMin.Value, value);
        if (info.SliderMax.HasValue) value = Math.Min(info.SliderMax.Value, value);
        return value;
    }

    public static bool IsApplicable(CoachParameterInfo info, CoachGame game)
        => info.ApplicableGames.Contains(game);

    /// <summary>"EVO/ACC", or a clear note when the parameter has no effect in any game.</summary>
    public static string GamesText(CoachParameterInfo info) =>
        info.ApplicableGames.Length == 0
            ? "no effect in any game"
            : "only: " + string.Join("/", info.ApplicableGames.Select(GameShortName));

    public static string GameShortName(CoachGame game) => game switch
    {
        CoachGame.AcEvo => "EVO",
        CoachGame.Raceroom => "R3E",
        CoachGame.AssettoCorsa => "AC",
        CoachGame.LeMansUltimate => "LMU",
        CoachGame.AssettoCorsaCompetizione => "ACC",
        _ => game.ToString(),
    };

    public static CoachGame ParseGameName(string? gameName)
    {
        if (string.IsNullOrWhiteSpace(gameName)) return CoachGame.Unknown;
        // NOTE: display names contain spaces ("Le Mans Ultimate", "RaceRoom (auto)").
        // Match the spaced forms first — "lemans" alone never matches "le mans".
        var g = gameName.ToLowerInvariant();
        if (g.Contains("le mans") || g.Contains("lemans") || g.Contains("lmu")) return CoachGame.LeMansUltimate;
        if (g.Contains("raceroom") || g.Contains("race room") || g.Contains("r3e")) return CoachGame.Raceroom;
        if (g.Contains("competizione") || g.Contains("acc")) return CoachGame.AssettoCorsaCompetizione;
        if (g.Contains("assetto corsa evo") || g.Contains("ac evo") || g.Trim() == "evo") return CoachGame.AcEvo;
        if (g.Contains("assetto corsa") || g.Trim() == "ac") return CoachGame.AssettoCorsa;
        return CoachGame.Unknown;
    }

    /// <summary>
    /// Canonical display name for a game string in any known variant
    /// (auto-detect suffixes, abbreviations, snapshot headers).
    /// Unknown inputs pass through unchanged.
    /// </summary>
    public static string CanonicalGameName(string? gameName)
    {
        return ParseGameName(gameName) switch
        {
            CoachGame.AcEvo => "Assetto Corsa EVO",
            CoachGame.Raceroom => "RaceRoom Racing Experience",
            CoachGame.AssettoCorsa => "Assetto Corsa",
            CoachGame.LeMansUltimate => "Le Mans Ultimate",
            CoachGame.AssettoCorsaCompetizione => "Assetto Corsa Competizione",
            _ => gameName ?? "",
        };
    }

    /// <summary>
    /// Per-game pipeline path description so the coach reasons about the
    /// pipeline the detected game actually runs through.
    /// </summary>
    public static string PipelinePathFor(CoachGame game) => game switch
    {
        CoachGame.Raceroom =>
            "R3E pipeline (R3eFfbPipeline): game SteeringForce column torque -> CoreForceMultiplier -> grip lightening (TyreGripScale) + BrakeBoost -> centering override (CenterSharpnessDegrees) -> output gain; DETAIL path (slip enhancer, dynamic effects, vibrations, LFE, R3E flatspot/surface/brake/TC effects -> MaxSlewRate). R3E does NOT use the channel mixer: Mz/Fx/Fy gains + scales, LUT, CenterKneePower, CenterBlendDegrees and AutoGain have no effect. Damping, crash, tyre condition and wet weather are not applied. Slip angles are per-axle (front/rear) from a bicycle model with a learned wheelbase, so SlipAngleGain/SlipAngleShapeGain work; front slip is ~0.08 rad at the limit, so a SlipThreshold above ~0.10 only fires past the limit. Slip vibration is zeroed by the reader.",
        CoachGame.LeMansUltimate =>
            "LMU pipeline (LmuFfbPipeline): shaft torque normalised by ForceScale (used as 120/value, HIGHER = HEAVIER) -> CoreForceMultiplier -> grip/lock fade (GripScaleGain) + BrakeBoost/BrakeLoadBias -> native torque sign (SelfSteerGain for slides) -> output gain; DETAIL: road texture (RoadTextureGain/Decay), SuspRoadScale, rear-slip warning (RearFyScale/RearGripDetailGain), slide-onset kick (YawOnset*), vibrations, LFE. Damping section applies (CenterDampFloor 0.03 typical). LMU does NOT use the channel mixer (Mz/Fx/Fy gains, scales, LUT, CenterKnee/Blend, AutoGain), crash, tyre condition or wet weather. Slip angle is real per-wheel (tyre ground velocity, front ~0.03 rad hard cornering, ~0.06 near the limit) so SlipAngleGain works with SlipThreshold ~0.04-0.06.",
        CoachGame.AcEvo =>
            "EVO pipeline (FfbPipeline base): real per-wheel Mz/Fx/Fy -> channel mixer (gains, scales, WheelLoadWeighting) -> LUT/CenterKneePower -> damping -> output gain -> crash -> tyre condition -> noise gate (core is zero-latency); DETAIL: slip enhancer, dynamic effects, tyre flex, scrub/rear-slip/vibrations, LFE -> MaxSlewRate. MzFrontGain POSITIVE. BrakeBoost is NOT used in EVO. Every section applies.",
        CoachGame.AssettoCorsa =>
            "AC1 pipeline (AcFfbPipeline): the AC1 reader supplies NO tyre forces or slip (Mz/Fx/Fy/slip are zero), so channel gains/scales, slip enhancer, scrub, auto-gain and wet slip curve do nothing. Strength comes from the game FFB via CoreForceMultiplier and OutputGain. Damping, dynamic effects, vibrations (incl. slip vibration), LFE, crash, tyre condition, wet weather and stationary friction apply. CoreSmoothing/DetailSmoothing/BrakeBoost apply on profile save.",
        CoachGame.AssettoCorsaCompetizione =>
            "ACC pipeline (AccFfbPipeline): base EVO pipeline with per-frame overrides: MzScale=5, MzFrontGain=0.5, vibration gains (except Master/AbsPulse/Offtrack) = 0, dynamic effects = 0, FlexGain = 0, viscous/friction = 0.01, speed damping = 0 -- do NOT suggest those. ForceGain then multiplies the whole output AFTER the force limiter (high values hard-clip). Fx/Fy channels, slip enhancer, crash, tyre condition, auto-gain and wet weather apply. Vibrations section is hidden.",
        _ =>
            "Unknown game: only suggest parameters that work in every game (OutputGain, SoftClipThreshold, " +
            "NoiseFloor, MaxSlewRate) and ask the user which game they are driving.",
    };

    /// <summary>
    /// Compact slider-range + applicability table injected into every coach prompt.
    /// Only parameters applicable to <paramref name="game"/> are listed as tunable;
    /// others are listed as NOT available so the model stops suggesting them.
    /// </summary>
    public static string BuildKnowledgeTable(CoachGame game)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"### Tunable parameters for {GameShortName(game)} (slider min–max are HARD limits)");
        sb.AppendLine("Each line is: internal name [slider min–max] | UI location | fixes | effect. " +
                      "In the \"parameter\" JSON field use the INTERNAL name; in reply text always call it by its slider label. " +
                      "When telling the user what to change, name the UI location exactly as given (\"Section -> Slider label\"). " +
                      "The Tuning page is grouped by feel: Overall Strength & Wheel, Steering Weight & Centre, Cornering & Front Grip, " +
                      "Oversteer & Slides, Braking & Acceleration, Kerbs Road & Off-Track, Engine & Chassis Rumble, Smoothness & Damping, " +
                      "Parked & Low Speed, Crashes & Tyre Damage, Wet Weather.");
        foreach (var p in ByName.Values.OrderBy(p => p.Name))
        {
            if (!IsApplicable(p, game)) continue;
            var range = p.SliderMin.HasValue
                ? $"{p.SliderMin:0.###}–{p.SliderMax:0.###}"
                : "no slider (profile-only)";
            sb.AppendLine($"- {p.Name} [{range}] | {p.PipelinePath} | fixes: {p.Fixes} | {p.Effect}");
        }
        sb.AppendLine();
        sb.AppendLine($"### NOT available for {GameShortName(game)} — NEVER suggest these here");
        bool any = false;
        foreach (var p in ByName.Values.OrderBy(p => p.Name))
        {
            if (IsApplicable(p, game)) continue;
            any = true;
            sb.AppendLine($"- {p.Name} ({GamesText(p)}) — {p.Effect}");
        }
        if (!any) sb.AppendLine("- (none — all parameters apply)");
        return sb.ToString();
    }
}
