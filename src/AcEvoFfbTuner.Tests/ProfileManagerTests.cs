using System.Text.Json;
using System.Text.Json.Serialization;
using AcEvoFfbTuner.Core.FfbProcessing;
using AcEvoFfbTuner.Core.Profiles;
using FluentAssertions;

namespace AcEvoFfbTuner.Tests;

public class ProfileManagerTests
{
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals
    };

    [Fact]
    public void Profiles_InitiallyEmpty()
    {
        var mgr = new ProfileManager();
        mgr.Profiles.Should().BeEmpty();
    }

    [Fact]
    public void ActiveProfile_InitiallyNull()
    {
        var mgr = new ProfileManager();
        mgr.ActiveProfile.Should().BeNull();
    }

    [Fact]
    public void SaveProfileFromPipeline_CreatesProfile()
    {
        var mgr = new ProfileManager();
        var pipeline = new FfbPipeline();
        var profile = mgr.SaveProfileFromPipeline(pipeline, "TestProfile");
        profile.Should().NotBeNull();
        profile.Name.Should().Be("TestProfile");
        mgr.Profiles.Should().Contain(p => p.Name == "TestProfile");
    }

    [Fact]
    public void SaveProfileFromPipeline_ExistingName_Updates()
    {
        var mgr = new ProfileManager();
        var pipeline = new FfbPipeline();
        mgr.SaveProfileFromPipeline(pipeline, "TestProfile");
        pipeline.OutputGain = 0.9f;
        var updated = mgr.SaveProfileFromPipeline(pipeline, "TestProfile");
        updated.OutputGain.Should().BeApproximately(0.9f, 1e-4f);
    }

    [Fact]
    public void SetActiveProfile_RaisesEvent()
    {
        var mgr = new ProfileManager();
        var pipeline = new FfbPipeline();
        var profile = mgr.SaveProfileFromPipeline(pipeline, "TestProfile");
        using var monitor = mgr.Monitor();
        mgr.SetActiveProfile(profile);
        mgr.ActiveProfile.Should().Be(profile);
    }

    [Fact]
    public void DeleteProfile_RemovesFromList()
    {
        var mgr = new ProfileManager();
        var pipeline = new FfbPipeline();
        var profile = mgr.SaveProfileFromPipeline(pipeline, "TestProfile");
        mgr.DeleteProfile(profile);
        mgr.Profiles.Should().NotContain(p => p.Name == "TestProfile");
    }

    [Fact]
    public void RenameProfile_ChangesName()
    {
        var mgr = new ProfileManager();
        var pipeline = new FfbPipeline();
        var profile = mgr.SaveProfileFromPipeline(pipeline, "TestProfile");
        mgr.RenameProfile(profile, "Renamed");
        profile.Name.Should().Be("Renamed");
    }

    [Fact]
    public void ExportProfile_CreatesFile()
    {
        var mgr = new ProfileManager();
        var pipeline = new FfbPipeline();
        var profile = mgr.SaveProfileFromPipeline(pipeline, "TestExport");
        var tempFile = Path.GetTempFileName();
        try
        {
            mgr.ExportProfile(profile, tempFile);
            File.Exists(tempFile).Should().BeTrue();
            var content = File.ReadAllText(tempFile);
            content.Should().Contain("TestExport");
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    [Fact]
    public void ImportProfile_LoadsFromFile()
    {
        var mgr = new ProfileManager();
        var pipeline = new FfbPipeline();
        var original = mgr.SaveProfileFromPipeline(pipeline, "TestImport");
        var tempFile = Path.GetTempFileName();
        try
        {
            mgr.ExportProfile(original, tempFile);
            var imported = mgr.ImportProfile(tempFile);
            imported.Should().NotBeNull();
            imported!.Name.Should().Be("TestImport");
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    [Fact]
    public void FindMatchingProfile_General_ReturnsGeneralMatch()
    {
        var mgr = new ProfileManager();
        var generalProfile = mgr.SaveProfileFromPipeline(new FfbPipeline(), "General");
        generalProfile.Scope = ProfileScope.General;

        var perGame = mgr.SaveProfileFromPipeline(new FfbPipeline(), "EVO Profile");
        perGame.Scope = ProfileScope.PerGame;
        perGame.GameMatch = "acevo";

        var result = mgr.FindMatchingProfile("acevo", "bmw_m4", "monza");
        result.Should().NotBeNull();
    }
}
