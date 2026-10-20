using AcEvoFfbTuner.Core.DirectInput.VJoy;
using FluentAssertions;

namespace AcEvoFfbTuner.Tests;

public class VJoyAcBindingTests
{
    private const string SampleIni = """
        [HEADER]
        INPUT_METHOD=WHEEL

        [CONTROLLERS]
        CON0=MOZA R5 Base
        __IGUID0=FCC60E30-2EE6-11F1-8001-444553540000
        PGUID0=0004346E-0000-0000-0000-504944564944

        [STEER]
        JOY=0
        AXLE=0
        SCALE=1
        LOCK=893
        FF_GAIN=0

        [THROTTLE]
        JOY=0
        AXLE=2
        MIN=-1
        MAX=1
        GAMMA=1 ; keep me

        [BRAKES]
        JOY=0
        AXLE=5
        MIN=-1
        MAX=1

        [CLUTCH]
        JOY=-1
        AXLE=-1
        MIN=-1
        MAX=1

        [GEARUP]
        JOY=0
        BUTTON=13
        KEY=0x58 ; X
        """;

    private static string WriteTempIni(string content)
    {
        string path = Path.Combine(Path.GetTempPath(), $"controls_{Guid.NewGuid():N}.ini");
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public void Write_AppendsVJoyAndRepointsAxes()
    {
        string path = WriteTempIni(SampleIni);
        try
        {
            var res = VJoyAcBinding.WriteBindings(path, Guid.Parse("11111111-2222-3333-4444-555555555555"), Guid.Parse("AAAAAAAA-BBBB-CCCC-DDDD-EEEEEEEEEEEE"), acRunning: false);
            res.Success.Should().BeTrue();
            res.VJoyIndex.Should().Be(1);
            File.Exists(path + ".ffbtuner-bak").Should().BeTrue();

            string text = File.ReadAllText(path);
            text.Should().Contain("CON1=vJoy Device");
            text.Should().Contain("__IGUID1=11111111-2222-3333-4444-555555555555");
            // Axis sections repointed, other keys + comments preserved
            text.Should().MatchRegex(@"(?m)^\[STEER\]\r?$[\r\n]+JOY=1\r?$[\r\n]+AXLE=0\r?$");
            text.Should().Contain("LOCK=893");
            text.Should().Contain("GAMMA=1 ; keep me");
            text.Should().Contain("BUTTON=13");
            text.Should().Contain("CON0=MOZA R5 Base");
        }
        finally { File.Delete(path); File.Delete(path + ".ffbtuner-bak"); }
    }

    [Fact]
    public void Write_IsIdempotent_NoDuplicates()
    {
        string path = WriteTempIni(SampleIni);
        try
        {
            var g = Guid.Parse("11111111-2222-3333-4444-555555555555");
            var p = Guid.Parse("AAAAAAAA-BBBB-CCCC-DDDD-EEEEEEEEEEEE");
            VJoyAcBinding.WriteBindings(path, g, p, acRunning: false).Success.Should().BeTrue();
            var res2 = VJoyAcBinding.WriteBindings(path, g, p, acRunning: false);
            res2.Success.Should().BeTrue();
            res2.VJoyIndex.Should().Be(1);
            string text = File.ReadAllText(path);
            text.Split(new[] { "CON1=vJoy Device" }, StringSplitOptions.None).Should().HaveCount(2);
        }
        finally { File.Delete(path); File.Delete(path + ".ffbtuner-bak"); }
    }

    [Fact]
    public void TaskCatalog_HasUniqueSections()
    {
        var sections = AcControlTask.All.Select(t => t.Section).ToList();
        sections.Should().OnlyHaveUniqueItems();
        sections.Should().OnlyContain(s => !string.IsNullOrEmpty(s));
        AcControlTask.All.Should().HaveCountGreaterThan(20);
        AcControlTask.All.Select(t => t.DisplayName).Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void Write_MissingFile_FailsClosed()
    {
        var res = VJoyAcBinding.WriteBindings(Path.Combine(Path.GetTempPath(), "nope-missing.ini"), Guid.NewGuid(), Guid.NewGuid());
        res.Success.Should().BeFalse();
    }

    [Fact]
    public void Write_TaskButtons_PointSectionsAtVJoy()
    {
        string path = WriteTempIni(SampleIni);
        try
        {
            var tasks = new Dictionary<string, int> { ["GEARUP"] = 2, ["__EXT_PIT_LIMITER"] = 5 };
            var res = VJoyAcBinding.WriteBindings(path, Guid.NewGuid(), Guid.NewGuid(), acRunning: false, taskButtons: tasks);
            res.Success.Should().BeTrue();
            res.Message.Should().Contain("2 button tasks");
            string text = File.ReadAllText(path);
            // vJoy index 1 (only controller), BUTTON is 0-based slot-1
            text.Should().MatchRegex(@"(?m)^\[GEARUP\]\r?$[\r\n]+JOY=1\r?$[\r\n]+BUTTON=1\r?$");
            text.Should().Contain("KEY=0x58 ; X");
            // Missing section created with same shape
            text.Should().MatchRegex(@"(?m)^\[__EXT_PIT_LIMITER\]\r?$[\r\n]+JOY=1\r?$[\r\n]+BUTTON=4\r?$");
            // Axes still written alongside
            text.Should().MatchRegex(@"(?m)^\[STEER\]\r?$[\r\n]+JOY=1\r?$[\r\n]+AXLE=0\r?$");
        }
        finally { File.Delete(path); File.Delete(path + ".ffbtuner-bak"); }
    }
}
