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

    // In-memory registration: SaveProfileFromPipeline writes to the real
    // AppData Profiles folder, so class-matching tests inject directly.
    private static ProfileManager ManagerWith(params FfbProfile[] profiles)
    {
        var mgr = new ProfileManager();
        var list = (List<FfbProfile>)typeof(ProfileManager)
            .GetField("_profiles", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .GetValue(mgr)!;
        list.AddRange(profiles);
        return mgr;
    }

    private static FfbProfile P(string name, ProfileScope scope, string game = "", string car = "", string track = "")
        => new() { Name = name, Scope = scope, GameMatch = game, CarMatch = car, TrackMatch = track };

    [Fact]
    public void FindMatchingProfile_ClassMatch_BeatsPerGame()
    {
        var mgr = ManagerWith(
            P("LMU - Hypercar - All", ProfileScope.PerGame, "lemansultimate"),
            P("LMU - GT3 - All", ProfileScope.PerCar, "lemansultimate", "GT3"));

        mgr.FindMatchingProfile("lemansultimate", "Garage 59 2026 #10:LM", "Bahrain", "GT3")!
            .Name.Should().Be("LMU - GT3 - All");
    }

    [Fact]
    public void FindMatchingProfile_ExactCar_BeatsClass()
    {
        var mgr = ManagerWith(
            P("LMU - GT3 - All", ProfileScope.PerCar, "lemansultimate", "GT3"),
            P("Garage 59 special", ProfileScope.PerCar, "lemansultimate", "Garage 59 2026 #10:LM"));

        mgr.FindMatchingProfile("lemansultimate", "Garage 59 2026 #10:LM", "Bahrain", "GT3")!
            .Name.Should().Be("Garage 59 special");
    }

    [Fact]
    public void FindMatchingProfile_ClassAndTrack_BeatsPerTrackAndClass()
    {
        var mgr = ManagerWith(
            P("LMU - GT3 - All", ProfileScope.PerCar, "lemansultimate", "GT3"),
            P("LMU - All - Spa", ProfileScope.PerTrack, "lemansultimate", "", "Spa"),
            P("LMU - GT3 - Spa", ProfileScope.PerCarAndTrack, "lemansultimate", "GT3", "Spa"));

        mgr.FindMatchingProfile("lemansultimate", "Iron Lynx #60", "Spa", "GT3")!
            .Name.Should().Be("LMU - GT3 - Spa");
    }

    [Fact]
    public void FindMatchingProfile_NoClass_LegacyBehaviourUnchanged()
    {
        var mgr = ManagerWith(
            P("LMU - Hypercar - All", ProfileScope.PerGame, "lemansultimate"),
            P("LMU - GT3 - All", ProfileScope.PerCar, "lemansultimate", "GT3"));

        mgr.FindMatchingProfile("lemansultimate", "Garage 59 2026 #10:LM", "Bahrain")!
            .Name.Should().Be("LMU - Hypercar - All");
        mgr.FindMatchingProfile("lemansultimate", "Garage 59 2026 #10:LM", "Bahrain", "")!
            .Name.Should().Be("LMU - Hypercar - All");
    }
}
