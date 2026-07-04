using AcEvoFfbTuner.Core.FfbProcessing;
using AcEvoFfbTuner.Core.Profiles;
using FluentAssertions;

namespace AcEvoFfbTuner.Tests;

public class FfbProfileTests
{
    [Fact]
    public void DefaultProfile_HasCurrentVersion()
    {
        var profile = new FfbProfile();
        profile.Version.Should().Be(FfbProfile.CurrentVersion);
    }

    [Fact]
    public void DefaultProfile_NameIsDefault()
    {
        var profile = new FfbProfile();
        profile.Name.Should().Be("Default");
    }

    [Fact]
    public void DefaultProfile_ScopeIsGeneral()
    {
        var profile = new FfbProfile();
        profile.Scope.Should().Be(ProfileScope.General);
    }

    [Fact]
    public void SanitizeFloats_NaN_BecomesZero()
    {
        var profile = new FfbProfile
        {
            OutputGain = float.NaN,
            NormalizationScale = float.NaN
        };
        profile.SanitizeFloats();
        profile.OutputGain.Should().Be(0f);
        profile.NormalizationScale.Should().Be(0f);
    }

    [Fact]
    public void SanitizeFloats_Infinity_Clamped()
    {
        var profile = new FfbProfile
        {
            OutputGain = float.PositiveInfinity,
        };
        profile.SanitizeFloats();
        profile.OutputGain.Should().Be(float.MaxValue);
    }

    [Fact]
    public void SanitizeFloats_NegativeInfinity_Clamped()
    {
        var profile = new FfbProfile
        {
            OutputGain = float.NegativeInfinity,
        };
        profile.SanitizeFloats();
        profile.OutputGain.Should().Be(float.MinValue);
    }

    [Fact]
    public void SanitizeFloats_NormalValue_Unchanged()
    {
        var profile = new FfbProfile
        {
            OutputGain = 0.5f,
        };
        profile.SanitizeFloats();
        profile.OutputGain.Should().Be(0.5f);
    }

    [Fact]
    public void SerializationRoundTrip_PreservesKeyProperties()
    {
        var profile = new FfbProfile
        {
            Name = "TestProfile",
            OutputGain = 0.75f,
            ForceScale = 1.2f,
            CarMatch = "bmw_m4_gt3",
            TrackMatch = "monza",
            GameMatch = "acevo",
            Scope = ProfileScope.PerCarAndTrack,
            Equalizer = new EqConfig { Enabled = true }
        };
        profile.Equalizer.SetGain(2, 3f);

        var json = System.Text.Json.JsonSerializer.Serialize(profile, ProfileManagerTests.JsonOptions);
        var deserialized = System.Text.Json.JsonSerializer.Deserialize<FfbProfile>(json, ProfileManagerTests.JsonOptions);

        deserialized.Should().NotBeNull();
        deserialized!.Name.Should().Be("TestProfile");
        deserialized.OutputGain.Should().BeApproximately(0.75f, 1e-6f);
        deserialized.ForceScale.Should().BeApproximately(1.2f, 1e-6f);
        deserialized.CarMatch.Should().Be("bmw_m4_gt3");
        deserialized.TrackMatch.Should().Be("monza");
        deserialized.GameMatch.Should().Be("acevo");
        deserialized.Scope.Should().Be(ProfileScope.PerCarAndTrack);
    }

    [Fact]
    public void Migrate_FromV3_UpgradesToCurrent()
    {
        var profile = new FfbProfile
        {
            Version = 3,
            Name = "Default",
        };
        profile.Migrate();
        profile.Version.Should().Be(FfbProfile.CurrentVersion);
    }

    [Fact]
    public void Migrate_CurrentVersion_DoesNothing()
    {
        var profile = new FfbProfile();
        var beforeName = profile.Name;
        profile.Migrate();
        profile.Version.Should().Be(FfbProfile.CurrentVersion);
        profile.Name.Should().Be(beforeName);
    }

    [Fact]
    public void GetDefaultProfile_Heavy_HasExpectedGains()
    {
        var profile = FfbProfile.GetDefaultProfile("Heavy");
        profile.Name.Should().Be("Heavy");
        profile.OutputGain.Should().Be(0.85f);
    }

    [Fact]
    public void GetDefaultProfile_UnknownName_ReturnsGenericDefault()
    {
        var profile = FfbProfile.GetDefaultProfile("NonExistent");
        profile.Should().NotBeNull();
        profile.Name.Should().Be("NonExistent");
    }

    [Fact]
    public void ApplyToPipeline_SetsPipelineProperties()
    {
        var profile = FfbProfile.GetDefaultProfile("Heavy");
        var pipeline = new FfbPipeline();
        profile.ApplyToPipeline(pipeline);
        pipeline.OutputGain.Should().Be(profile.OutputGain);
        pipeline.Damping.ViscousCoefficient.Should().Be(profile.Damping.ViscousDamping);
    }

    [Fact]
    public void ApplyToPipeline_ThenUpdateFromPipeline_RoundTrips()
    {
        var profile = FfbProfile.GetDefaultProfile("Heavy");
        var pipeline = new FfbPipeline();
        profile.ApplyToPipeline(pipeline);

        var profile2 = FfbProfile.CreateFromPipeline(pipeline, "RoundTrip");
        profile2.OutputGain.Should().BeApproximately(profile.OutputGain, 1e-4f);
        profile2.ForceScale.Should().Be(profile.ForceScale);
    }

    [Fact]
    public void BuiltInCheck_DefaultNames_AreBuiltIn()
    {
        foreach (var name in FfbProfile.AllDefaultNames)
        {
            var profile = FfbProfile.GetDefaultProfile(name);
            profile.IsBuiltIn.Should().BeTrue();
        }
    }

    [Fact]
    public void CustomProfile_IsNotBuiltIn()
    {
        var profile = new FfbProfile { Name = "Custom" };
        profile.IsBuiltIn.Should().BeFalse();
    }
}
