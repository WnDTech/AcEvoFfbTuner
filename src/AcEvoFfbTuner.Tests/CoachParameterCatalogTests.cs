using AcEvoFfbTuner.Core.FfbProcessing;
using AcEvoFfbTuner.Core.Profiles;
using Xunit;

namespace AcEvoFfbTuner.Tests;

/// <summary>
/// The FFB Coach and auto-configure must respect the feel-based Tuning page:
/// same slider ranges, and only parameters the detected game's pipeline reads.
/// </summary>
public class CoachParameterCatalogTests
{
    private static CoachParameterInfo Get(string name)
    {
        Assert.True(CoachParameterCatalog.TryGet(name, out var info), $"{name} missing from catalog");
        return info!;
    }

    [Theory]
    [InlineData("MaxSlewRate", 0.03f, 1f)]
    [InlineData("SpeedDamping", 0f, 2f)]
    [InlineData("Friction", 0f, 2f)]
    [InlineData("Inertia", 0f, 2f)]
    [InlineData("MasterGain", 0f, 5f)]
    [InlineData("AbsGain", 0f, 3f)]
    [InlineData("LateralGGain", 0f, 2f)]
    [InlineData("CenterKneePower", 1f, 3f)]
    [InlineData("SlipThreshold", 0f, 0.3f)]
    [InlineData("ForceScale", 0.1f, 200f)]
    [InlineData("CoreForceMultiplier", 0f, 4f)]
    [InlineData("MzFrontGain", -2f, 2f)]
    public void SliderRanges_MatchTuningPage(string name, float min, float max)
    {
        var p = Get(name);
        Assert.Equal(min, p.SliderMin);
        Assert.Equal(max, p.SliderMax);
    }

    [Theory]
    [InlineData("CenterSuppressionDegrees")]
    [InlineData("LowSpeedSmoothKmh")]
    [InlineData("HysteresisThreshold")]
    public void DeadParameters_NeverApplicable(string name)
    {
        var p = Get(name);
        foreach (var g in Enum.GetValues<CoachGame>())
            Assert.False(CoachParameterCatalog.IsApplicable(p, g));
        Assert.Equal("no effect in any game", CoachParameterCatalog.GamesText(p));
    }

    [Theory]
    // Channel mixer is EVO/ACC only — R3E/LMU/AC1 never read it.
    [InlineData("MzFrontGain", CoachGame.AcEvo, true)]
    [InlineData("MzFrontGain", CoachGame.AssettoCorsaCompetizione, true)]
    [InlineData("MzFrontGain", CoachGame.Raceroom, false)]
    [InlineData("MzFrontGain", CoachGame.LeMansUltimate, false)]
    [InlineData("MzFrontGain", CoachGame.AssettoCorsa, false)]
    [InlineData("CoreForceMultiplier", CoachGame.AssettoCorsa, true)]
    [InlineData("CoreForceMultiplier", CoachGame.AcEvo, false)]
    [InlineData("ForceScale", CoachGame.LeMansUltimate, true)]
    [InlineData("ForceScale", CoachGame.Raceroom, false)]
    [InlineData("BrakeBoostGain", CoachGame.AcEvo, false)]
    [InlineData("BrakeBoostGain", CoachGame.AssettoCorsaCompetizione, true)]
    [InlineData("ScrubGain", CoachGame.AcEvo, true)]
    [InlineData("ScrubGain", CoachGame.AssettoCorsa, false)]
    [InlineData("SlipGain", CoachGame.Raceroom, false)]
    [InlineData("ViscousDamping", CoachGame.Raceroom, false)]
    [InlineData("ViscousDamping", CoachGame.AssettoCorsaCompetizione, false)]
    [InlineData("StaticFrictionGain", CoachGame.LeMansUltimate, true)]
    [InlineData("TyreConditionEnabled", CoachGame.Raceroom, false)]
    [InlineData("MasterGain", CoachGame.AssettoCorsaCompetizione, false)]
    public void Applicability_MatchesPipelineConsumers(string name, CoachGame game, bool expected)
        => Assert.Equal(expected, CoachParameterCatalog.IsApplicable(Get(name), game));

    [Fact]
    public void Clamp_UsesNewRanges()
    {
        Assert.Equal(1f, CoachParameterCatalog.ClampToSlider(Get("MaxSlewRate"), 1.5f));
        Assert.Equal(0.85f, CoachParameterCatalog.ClampToSlider(Get("MaxSlewRate"), 0.85f));
        Assert.Equal(2f, CoachParameterCatalog.ClampToSlider(Get("Friction"), 8f));
    }

    [Fact]
    public void KnowledgeTable_GivesUiLocation_AndHidesOtherGames()
    {
        var t = CoachParameterCatalog.BuildKnowledgeTable(CoachGame.LeMansUltimate);
        Assert.Contains("Section -> Slider label", t);
        var tunable = t[..t.IndexOf("### NOT available")];
        Assert.Contains("RearFyScale", tunable);
        Assert.DoesNotContain("- MzFrontGain [", tunable);
        Assert.DoesNotContain("- CenterSuppressionDegrees [", tunable);
    }

    public static IEnumerable<object[]> BuiltInProfiles() => new[]
    {
        "Heavy", "Light", "ACCTEST", "Moza R5 - Final Stable Baseline", "Default - Logitech RS50/G PRO",
        "Default - Logitech G27", "Default - Logitech G29/G920", "Default - Thrustmaster T300/TX",
        "Default - Fanatec CSL DD 5Nm", "Default - Fanatec CSL DD 8Nm", "Default - Moza R9",
        "Default - Fanatec ClubSport DD", "Default - Simagic Alpha", "Default - Simagic Alpha Ultimate",
        "Default - Simucube 2 Pro",
    }.Select(n => new object[] { n });

    /// <summary>Auto-configure starts from these — none may sit outside a slider the user can touch.</summary>
    [Theory]
    [MemberData(nameof(BuiltInProfiles))]
    public void BuiltInProfiles_InsideSliderRanges(string profileName)
    {
        var p = FfbProfile.GetDefaultProfile(profileName);
        var values = new (string Name, float Value)[]
        {
            ("OutputGain", p.OutputGain), ("SoftClipThreshold", p.SoftClipThreshold),
            ("MaxSlewRate", p.Advanced.MaxSlewRate), ("NoiseFloor", p.Advanced.NoiseFloor),
            ("CenterKneePower", p.Advanced.CenterKneePower), ("CenterBlendDegrees", p.Advanced.CenterBlendDegrees),
            ("MzFrontGain", p.MzFront.Gain), ("FxFrontGain", p.FxFront.Gain), ("FyFrontGain", p.FyFront.Gain),
            ("MzScale", p.MzScale), ("FxScale", p.FxScale), ("FyScale", p.FyScale),
            ("ViscousDamping", p.Damping.ViscousDamping), ("SpeedDamping", p.Damping.SpeedDamping),
            ("Friction", p.Damping.Friction), ("Inertia", p.Damping.Inertia),
            ("SlipRatioGain", p.Slip.SlipRatioGain), ("SlipAngleGain", p.Slip.SlipAngleGain),
            ("SlipThreshold", p.Slip.SlipThreshold), ("BrakeBoostGain", p.Slip.BrakeBoostGain),
            ("BrakeBoostThreshold", p.Slip.BrakeBoostThreshold), ("CoreForceMultiplier", p.Slip.CoreForceMultiplier),
            ("MasterGain", p.Vibrations.MasterGain), ("KerbGain", p.Vibrations.KerbGain),
            ("RoadGain", p.Vibrations.RoadGain), ("SlipGain", p.Vibrations.SlipGain),
            ("AbsGain", p.Vibrations.AbsGain), ("ScrubGain", p.Vibrations.ScrubGain),
            ("RearSlipGain", p.Vibrations.RearSlipGain), ("SuspensionGain", p.Dynamic.SuspensionGain),
            ("LateralGGain", p.Dynamic.LateralGGain),
        };
        var bad = new List<string>();
        foreach (var (name, v) in values)
        {
            var info = Get(name);
            if (CoachParameterCatalog.ClampToSlider(info, v) != v)
                bad.Add($"{name}={v} (slider {info.SliderMin}..{info.SliderMax})");
        }
        Assert.True(bad.Count == 0, $"{profileName}: " + string.Join(", ", bad));
    }

    [Fact]
    public void AutoConfigurator_ClampsSteeringLock()
    {
        var p = WheelbaseAutoConfigurator.GenerateProfile(5.5f, "MOZA R5 Base", null);
        Assert.InRange(p.SteeringLockDegrees, 180, 1080);
    }
}
