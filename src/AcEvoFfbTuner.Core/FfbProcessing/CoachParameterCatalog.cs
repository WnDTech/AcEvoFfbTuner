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
    private static readonly CoachGame[] All = [
        CoachGame.AcEvo, CoachGame.Raceroom, CoachGame.AssettoCorsa,
        CoachGame.LeMansUltimate, CoachGame.AssettoCorsaCompetizione];
    private static readonly CoachGame[] Physics = [
        CoachGame.AcEvo, CoachGame.AssettoCorsa, CoachGame.AssettoCorsaCompetizione];
    private static readonly CoachGame[] Column = [
        CoachGame.Raceroom, CoachGame.LeMansUltimate];
    private static readonly CoachGame[] NoAccVib = [
        CoachGame.AcEvo, CoachGame.Raceroom, CoachGame.AssettoCorsa,
        CoachGame.LeMansUltimate];
    private static readonly CoachGame[] EvoAcR3e = [
        CoachGame.AcEvo, CoachGame.AssettoCorsa, CoachGame.Raceroom];
    private static readonly CoachGame[] EvoAcLmu = [
        CoachGame.AcEvo, CoachGame.AssettoCorsa, CoachGame.LeMansUltimate];
    private static readonly CoachGame[] EvoAcR3eDyn = [
        CoachGame.AcEvo, CoachGame.AssettoCorsa, CoachGame.Raceroom];

    private static readonly Dictionary<string, CoachParameterInfo> ByName;

    static CoachParameterCatalog()
    {
        var list = new List<CoachParameterInfo>
        {
            // ── Strength & output ──
            E("OutputGain", 0f, 20f, All, "Output: final multiplier before limiter",
                "clipping, weak", "Master output strength. Lower fixes clipping, higher fixes weak feel."),
            E("SoftClipThreshold", 0.1f, 1f, All, "Output: soft limiter (UI: Max Force Limit)",
                "clipping", "Force ceiling. Lowering adds headroom but caps peaks."),
            E("ForceScale", 0.1f, 5f, Column, "Core: first gain stage (UI: COLUMN FORCE → Scale, R3E/LMU only)",
                "clipping, weak", "Inverse sensitivity for column-force games. Higher = weaker forces. Default 1.0."),
            E("CompressionPower", null, null, All, "Core: tanh curve (profile-only, no slider)",
                "clipping", "Rounds off peaks. Profile-only — no UI slider; prefer OutputGain."),
            E("CoreForceMultiplier", 0f, 10f, Column, "Core: column-force boost (UI: COLUMN FORCE → Gain, R3E/LMU only)",
                "weak", "Boosts converted column force. ~3.0x typical for column-force games."),
            // ── Channel mix ──
            E("MzFrontGain", -10f, 10f, All, "Core: channel mixer Mz front",
                "weak, inversion", "Self-aligning torque. NEGATIVE for R3E/LMU, POSITIVE for EVO/AC/ACC."),
            E("FxFrontGain", -10f, 10f, All, "Core: channel mixer Fx front",
                "weak", "Longitudinal force (traction/braking)."),
            E("FyFrontGain", -10f, 10f, All, "Core: channel mixer Fy front",
                "weak", "Lateral force (cornering)."),
            E("MzRearGain", -10f, 10f, All, "Core: channel mixer Mz rear",
                "feel", "Rear Mz contribution. Usually 0."),
            E("WheelLoadWeighting", 0f, 1f, All, "Core: channel mixer load weighting",
                "feel", "How much wheel load modulates the channel mix."),
            E("MzScale", 1f, 500f, All, "Core: Mz normalization divisor",
                "clipping, weak", "Higher = weaker Mz. Default 30."),
            E("FxScale", 100f, 20000f, All, "Core: Fx normalization divisor",
                "clipping, weak", "Higher = weaker Fx. Default 4000."),
            E("FyScale", 100f, 20000f, All, "Core: Fy normalization divisor",
                "clipping, weak", "Higher = weaker Fy. Default 5000."),
            // ── Damping (UI section visible for EVO/AC/LMU only) ──
            E("ViscousDamping", 0f, 1f, EvoAcLmu, "Core: damping stage",
                "oscillation", "Speed-proportional resistance. Raises stability, dulls detail."),
            E("SpeedDamping", 0f, 25f, EvoAcLmu, "Core: damping stage",
                "oscillation", "Extra high-speed damping."),
            E("Friction", 0f, 20f, EvoAcLmu, "Core: damping stage (UI: Friction Level)",
                "oscillation", "Constant resistance. Too much feels numb."),
            E("Inertia", 0f, 20f, EvoAcLmu, "Core: damping stage (UI: Inertia Weight)",
                "oscillation", "Angular inertia simulation."),
            E("MaxSpeedReference", 50f, 500f, EvoAcLmu, "Core: damping reference (UI: Damping Speed Reference)",
                "oscillation", "Speed at which speed damping reaches max."),
            E("CenterDampFloor", 0f, 0.1f, EvoAcLmu, "Core: center damping floor",
                "oscillation", "Absolute damping authority at center. Fixes weave."),
            E("StraightGain", 0f, 2f, EvoAcLmu, "Core: straight-line damping",
                "oscillation", "Extra damping above 80 km/h near center."),
            // ── Slip ──
            E("SlipRatioGain", 0f, 5f, All, "Detail: slip enhancer",
                "low_detail", "Force enhancement from tire slip ratio."),
            E("SlipAngleGain", 0f, 5f, Physics, "Detail: slip enhancer (physics games only — unreliable in R3E/LMU)",
                "low_detail", "Keep at 0 for R3E/LMU: slip-angle telemetry is unreliable there."),
            E("SlipAngleShapeGain", 0f, 5f, Physics, "Detail: slip enhancer (physics games only)",
                "low_detail", "Shape-based slip angle response."),
            E("SlipThreshold", 0f, 1f, All, "Detail: slip enhancer gate",
                "low_detail, noise", "Minimum slip before enhancement. Lower = more sensitive."),
            E("BrakeBoostGain", 0f, 1f, Column, "Core: brake weight-transfer boost (column-force games)",
                "feel", "Adds force under braking. No effect outside R3E/LMU pipeline path."),
            E("BrakeBoostThreshold", 0f, 1f, Column, "Core: brake boost gate",
                "feel", "Brake pedal threshold for boost."),
            E("GearSpikeThreshold", null, null, All, "Core: gear-shift filter (numeric, no slider)",
                "snap", "RPM gate for shift-spike mute."),
            E("SteeringAngleCompensationEnabled", null, null, [CoachGame.Raceroom], "Core: R3E high-steer boost toggle",
                "feel", "Legacy +15% high-steer boost for the pre-Historic-Update fixed SAT drop. Off = feel the Historic Update's own dynamic SAT curve."),
            E("GripReleaseRate", 0.5f, 20f, [CoachGame.Raceroom], "Core: R3E grip reload limiter (profile-only)",
                "snap", "Upward recovery rate of the grip scale after unloads. Lower = softer touchdown landings. No UI slider."),
            // LMU-only slip extras
            E("RearFyScale", 0f, 2f, [CoachGame.LeMansUltimate], "Detail: LMU rear-slip warning",
                "low_detail", "LMU rear warning from rear Fy oscillation. No effect in other games."),
            E("RearGripDetailGain", 0f, 2f, [CoachGame.LeMansUltimate], "Detail: LMU rear warning",
                "low_detail", "LMU rear warning from grip-fraction drop."),
            E("YawOnsetGain", 0f, 2f, [CoachGame.LeMansUltimate], "Detail: LMU slide-onset kick",
                "low_detail", "LMU slide-onset kick strength."),
            E("YawOnsetThreshold", 0f, 10f, [CoachGame.LeMansUltimate], "Detail: LMU onset gate",
                "low_detail", "Yaw-accel threshold to fire the onset kick. 0 = off."),
            E("YawOnsetDecay", null, null, [CoachGame.LeMansUltimate], "Detail: LMU onset decay (profile-only)",
                "feel", "Onset-kick decay rate. No UI slider."),
            E("YawOnsetMax", null, null, [CoachGame.LeMansUltimate], "Detail: LMU onset cap (profile-only)",
                "feel", "Onset-kick output cap. No UI slider."),
            E("SuspRoadScale", 0f, 2f, [CoachGame.LeMansUltimate], "Detail: LMU road layer",
                "low_detail", "LMU mixer susp-delta road layer scale."),
            E("BrakeLoadBias", 0f, 1f, [CoachGame.LeMansUltimate], "Core: LMU brake bias",
                "feel", "LMU front-load bias in brake boost. 0 = legacy scalar."),
            // ── Dynamic effects (UI section: EVO/AC/R3E) ──
            E("LateralGGain", 0f, 5f, EvoAcR3eDyn, "Detail: dynamic effects (UI: Cornering Force)",
                "low_detail", "Cornering G-force feel."),
            E("LongitudinalGGain", 0f, 5f, EvoAcR3eDyn, "Detail: dynamic effects (UI: Accel/Braking Force)",
                "low_detail", "Acceleration/braking G-force."),
            E("SuspensionGain", 0f, 5f, EvoAcR3eDyn, "Detail: dynamic effects (UI: Road Feel)",
                "low_detail", "Road surface texture from suspension."),
            E("YawRateGain", 0f, 5f, EvoAcR3eDyn, "Detail: dynamic effects (UI: Car Rotation Force)",
                "low_detail", "Car rotation feel."),
            // ── Vibrations (UI section hidden for ACC) ──
            E("MasterGain", 0f, 10f, NoAccVib, "Detail: vibration mixer master (UI: Vibration Master Gain)",
                "buzz, low_detail", "Global vibration volume."),
            E("KerbGain", 0f, 10f, NoAccVib, "Detail: vibration mixer (UI: Curb Gain)",
                "buzz, low_detail", "Curb/rumble strip intensity."),
            E("RoadGain", 0f, 10f, NoAccVib, "Detail: vibration mixer (UI: Road Gain)",
                "buzz, low_detail", "Road surface texture vibration."),
            E("SlipGain", 0f, 10f, NoAccVib, "Detail: vibration mixer (UI: Slip Gain)",
                "buzz, low_detail", "Tire slip vibration."),
            E("AbsGain", 0f, 10f, NoAccVib, "Detail: vibration mixer",
                "buzz, low_detail", "ABS pulsing vibration."),
            E("AbsPulseAmplitude", 0f, 1f, NoAccVib, "Detail: vibration mixer",
                "buzz", "ABS pulse strength."),
            E("SuspensionRoadGain", 0f, 10f, NoAccVib, "Detail: vibration mixer",
                "buzz, low_detail", "Suspension-based road feel."),
            E("ScrubGain", 0f, 1f, NoAccVib, "Detail: vibration mixer",
                "buzz, low_detail", "Tire scrub texture."),
            E("RearSlipGain", 0f, 1f, NoAccVib, "Detail: vibration mixer",
                "buzz, low_detail", "Rear wheel slip vibration."),
            E("OfftrackGain", 0f, 2f, NoAccVib, "Detail: vibration mixer",
                "buzz, low_detail", "Off-track surface feel."),
            E("OfftrackSeverityScale", 0f, 10f, NoAccVib, "Detail: vibration mixer",
                "buzz", "How strongly offtrack feels."),
            E("CurbSeverityScale", null, null, NoAccVib, "Detail: vibration mixer (profile-only, no slider)",
                "buzz", "Curb impact harshness. No UI slider."),
            E("ScrubForceScale", null, null, NoAccVib, "Detail: vibration mixer (profile-only, no slider)",
                "buzz, low_detail", "Scrub force normalization. No UI slider."),
            E("RearSlipForceScale", null, null, NoAccVib, "Detail: vibration mixer (profile-only, no slider)",
                "buzz, low_detail", "Rear slip force normalization. No UI slider."),
            // ── Signal shaping ──
            E("MaxSlewRate", 0.03f, 0.5f, All, "Detail: slew limiter (detail path only — core is zero-latency)",
                "snap, oscillation", "Per-frame rate limit on DETAIL path only. Lower = smoother."),
            E("CenterSuppressionDegrees", 0f, 30f, All, "Core: centering shaping",
                "stiff_center, snap", "V-shape force reduction near center. >5° creates a deadzone."),
            E("CenterSharpnessDegrees", 0f, 10f, Column, "Core: R3E/LMU smoothstep ramp (hidden for physics games)",
                "stiff_center", "0 = disabled (reader shaping). 1-3 = progressive."),
            E("CenterKneePower", 0.5f, 3f, All, "Core: centering shaping",
                "stiff_center", "Non-linear core shaping. >1 = progressive."),
            E("CenterBlendDegrees", 0f, 10f, All, "Core: centering shaping",
                "stiff_center", "Transition zone between center and full force."),
            E("HysteresisThreshold", null, null, All, "Core: noise deadband (profile-only, no slider)",
                "noise, oscillation", "Deadband for noise rejection. No UI slider — use NoiseFloor slider instead."),
            E("NoiseFloor", 0f, 0.05f, All, "Output: noise gate",
                "noise", "Minimum output gate. Forces below this are silenced."),
            E("HysteresisWatchdogFrames", null, null, All, "Core: hysteresis timing (profile-only)",
                "noise", "Frames before hysteresis engages. No UI slider."),
            E("LowSpeedSmoothKmh", 0f, 100f, All, "Core/detail: low-speed smoothing gate",
                "noise", "Speed below which extra smoothing applies."),
            // ── Tyre flex (UI section: EVO/AC/R3E) ──
            E("FlexGain", 0f, 5f, EvoAcR3e, "Detail: tyre flex (UI: Tyre Flex Gain)",
                "low_detail", "Tyre flex feel amplitude."),
            E("CarcassStiffness", 0.1f, 5f, EvoAcR3e, "Detail: tyre flex",
                "feel", "Tyre sidewall stiffness feel."),
            E("FlexSmoothing", 0f, 0.95f, EvoAcR3e, "Detail: tyre flex",
                "noise", "Smoothing on tyre flex signal."),
            E("ContactPatchWeight", 0f, 2f, EvoAcR3e, "Detail: tyre flex",
                "low_detail", "Contact patch deformation feel."),
            E("LoadFlexGain", 0f, 5f, EvoAcR3e, "Detail: tyre flex",
                "low_detail", "Load-dependent flex gain."),
            // ── Static friction (UI section: per-wheel games only) ──
            E("StaticFrictionGain", 0f, 2f, Physics, "Stationary friction (parking-lot feel, all games)",
                "feel", "Overall elastic feel strength."),
            E("StaticFrictionMaxElasticStretch", 0.001f, 0.05f, Physics, "Stationary friction",
                "feel", "Max stretch before breakaway."),
            E("StaticFrictionSpringStiffness", 1f, 50f, Physics, "Stationary friction",
                "feel", "Centering spring rate."),
            E("StaticFrictionKineticFrictionBase", 0f, 1f, Physics, "Stationary friction",
                "feel", "Sliding friction feel."),
            E("StaticFrictionEngineOffDamping", 0f, 0.5f, Physics, "Stationary friction",
                "feel", "Extra damping when car is off."),
            E("StaticFrictionEngineOnDamping", 0f, 0.2f, Physics, "Stationary friction",
                "feel", "Damping reduction when car is on."),
            E("StaticFrictionEngineOffScale", 0f, 2f, Physics, "Stationary friction",
                "feel", "Overall scale when car is off."),
            E("StaticFrictionEngineOnScale", 0f, 1f, Physics, "Stationary friction",
                "feel", "Overall scale when car is on."),
            E("StaticFrictionActiveDecay", 0.5f, 1f, Physics, "Stationary friction",
                "feel", "How fast elastic force builds when turning."),
            E("StaticFrictionReturnDecay", 0.1f, 0.99f, Physics, "Stationary friction",
                "feel", "How fast elastic force returns to center."),
            E("StaticFrictionOutputSmoothAlpha", 0.05f, 1f, Physics, "Stationary friction",
                "feel", "Smoothing on static friction output."),
            E("StaticFrictionBreakoutGain", 0f, 1f, Physics, "Stationary friction",
                "feel", "Stick-slip judder near breakaway."),
            E("StaticFrictionBreakoutFrequencyHz", 20f, 70f, Physics, "Stationary friction",
                "feel", "Stick-slip judder frequency."),
            E("StaticFrictionScrubGain", 0f, 1f, Physics, "Stationary friction",
                "feel", "Tyre-twist grain texture."),
            E("StaticFrictionLoadAsymmetryGain", 0f, 1f, Physics, "Stationary friction",
                "feel", "Front load-asymmetry weight."),
            E("StaticFrictionPressureSensitivity", 0f, 1f, Physics, "Stationary friction",
                "feel", "Tyre-pressure sensitivity."),
            // ── Grip guard / crash / tyre condition / wet ──
            E("GripGuardEnabled", null, null, All, "Core: grip guard (toggle, no slider)",
                "feel", "Grip-dependent attenuation toggle."),
            E("GripGuardPeakSlipAngle", null, null, All, "Core: grip guard (profile-only)",
                "feel", "Slip angle at peak grip. No UI slider."),
            E("GripGuardAttenuationStrength", null, null, All, "Core: grip guard (profile-only)",
                "feel", "Force reduction past peak grip. No UI slider."),
            E("GripGuardMechanicalTrailGain", null, null, All, "Core: grip guard (profile-only)",
                "feel", "Simulated mechanical trail. No UI slider."),
            E("CrashEnabled", null, null, Physics, "Core: crash limiter (toggle; UI section EVO/AC/ACC)",
                "snap", "Crash force limiting toggle."),
            E("CrashImpactGain", 0f, 1f, Physics, "Core: crash limiter",
                "snap", "Crash force amplitude."),
            E("CrashSafetyClamp", 0.1f, 1f, Physics, "Core: crash limiter",
                "snap", "Maximum allowed crash force."),
            E("CrashTriggerThresholdG", 1f, 10f, Physics, "Core: crash limiter gate",
                "snap", "G-force threshold for crash response."),
            E("TyreConditionEnabled", null, null, All, "Core: tyre condition (toggle)",
                "feel", "Tyre wear/blowout effects toggle."),
            E("BlowoutVibrationGain", 0f, 1f, All, "Detail: tyre condition vibration",
                "buzz", "Vibration strength during blowout."),
            E("PressureLossGain", 0f, 1f, All, "Core: tyre condition feel",
                "feel", "Feel of pressure loss."),
            E("DamageAsymmetryGain", 0f, 1f, All, "Core: tyre condition feel",
                "feel", "Asymmetric pull from damage."),
            E("WetWeatherEnabled", null, null, Physics, "Wet weather (toggle; per-wheel games only)",
                "feel", "Wet-weather FFB adjustments. No effect in R3E/LMU."),
            E("WetRoadVibSuppression", 0f, 1f, Physics, "Wet weather",
                "buzz", "Road vibration reduction in rain."),
            E("WetPeakSlipAngleMultiplier", 1f, 3f, Physics, "Wet weather",
                "feel", "Slip angle increase on wet surface."),
            E("WetDampingReduction", 0f, 1f, Physics, "Wet weather",
                "feel", "Damping reduction in rain."),
            // ── Toggles with no slider ──
            E("SignCorrectionEnabled", null, null, All, "Output: direction toggle (checkbox, no slider)",
                "inversion", "Inverts FFB direction. Disabling flips the wheel backwards."),
            E("UseFrontOnly", null, null, All, "Detail: slip source toggle",
                "feel", "Only use front wheel slip data."),
            E("UseMixerSuspRoad", null, null, [CoachGame.LeMansUltimate], "Detail: LMU road layer toggle",
                "low_detail", "LMU mixer road layer gate."),
            E("SynthYawEnabled", null, null, [CoachGame.LeMansUltimate], "Telemetry synthesis toggle",
                "low_detail", "LMU yaw-accel synthesis. Measurement only."),
            E("SynthAbsEnabled", null, null, [CoachGame.LeMansUltimate], "Telemetry synthesis toggle",
                "feel", "LMU ABS-flag synthesis. Experimental."),
            E("SynthTcEnabled", null, null, [CoachGame.LeMansUltimate], "Telemetry synthesis toggle",
                "feel", "LMU TC-flag synthesis. Experimental."),
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

    public static float ClampToSlider(CoachParameterInfo info, float value)
    {
        if (info.SliderMin.HasValue) value = Math.Max(info.SliderMin.Value, value);
        if (info.SliderMax.HasValue) value = Math.Min(info.SliderMax.Value, value);
        return value;
    }

    public static bool IsApplicable(CoachParameterInfo info, CoachGame game)
        => info.ApplicableGames.Contains(game);

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
            "R3E pipeline (R3eFfbPipeline): game sends SteeringForce column torque → reader converts " +
            "to Mz with sqrt center ramp (0-3°) → CORE path (channel mix → CoreForceMultiplier → " +
            "ApplyCenteringOverride smoothstep → output gain) + DETAIL path (slip/dynamic/vibrations → " +
            "slew limiter) → mix → soft clip → output. Fx/Fy are load-synthesized estimates, slip-angle " +
            "telemetry is unreliable (keep SlipAngleGain at 0). BrakeBoost adds weight-transfer force.",
        CoachGame.LeMansUltimate =>
            "LMU pipeline (LmuFfbPipeline): column-force like R3E (CoreForceMultiplier default 3.0x, " +
            "BrakeBoost, center override) PLUS LMU-only detail overrides: rear-slip warning " +
            "(RearFyScale/RearGripDetailGain — rear Mz is always zero in LMU telemetry), slide-onset " +
            "kick (YawOnset*) from synthesized yaw accel, and mixer road layer (SuspRoadScale). " +
            "Slip-angle is a single bicycle value — keep SlipAngleGain at 0.",
        CoachGame.AcEvo =>
            "EVO pipeline (FfbPipeline base): real physics Mz/Fx/Fy per wheel → CORE path " +
            "(channel mix → LUT → damping incl. CenterDampFloor/StraightGain → output gain → " +
            "centering → GripGuard → crash → tyre condition → noise gate, zero-latency, no slew) + " +
            "DETAIL path (slip/dynamic/tyre-flex/vibrations/LFE → EQ → MaxSlewRate limiter) → mix → " +
            "soft clip → output. MzFrontGain POSITIVE. All sections (damping, flex, crash, wet) apply.",
        CoachGame.AssettoCorsa =>
            "AC pipeline (AcFfbPipeline): real physics Mz/Fx/Fy like EVO (MzFrontGain POSITIVE, " +
            "slip-angle reliable). Damping, tyre-flex, crash, wet-weather and stationary-friction " +
            "sections apply. No LMU rear-slip/yaw-onset overrides.",
        CoachGame.AssettoCorsaCompetizione =>
            "ACC pipeline (AccFfbPipeline): real physics Mz/Fx/Fy (MzFrontGain POSITIVE). NOTE: the " +
            "VIBRATIONS section is hidden for ACC and DYNAMIC effects are physics-driven — do NOT " +
            "suggest vibration gains (MasterGain/KerbGain/RoadGain/...) for ACC; they have no UI " +
            "slider in this game context. Crash, auto-gain and wet-weather sections apply.",
        _ =>
            "Unknown game: only suggest game-agnostic core parameters (OutputGain, channel gains, " +
            "center shaping) and ask the user which game they are driving.",
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
            sb.AppendLine($"- {p.Name} (only: {string.Join("/", p.ApplicableGames.Select(GameShortName))}) — {p.Effect}");
        }
        if (!any) sb.AppendLine("- (none — all parameters apply)");
        return sb.ToString();
    }
}
