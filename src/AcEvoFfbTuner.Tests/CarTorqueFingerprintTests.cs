using AcEvoFfbTuner.Core.Profiles;
using FluentAssertions;
using Xunit;

namespace AcEvoFfbTuner.Tests;

public class CarTorqueFingerprintTests
{
    [Fact]
    public void Percentiles_TrackUniformDistribution_WithinBinResolution()
    {
        var fp = new CarTorqueFingerprint();
        for (int i = 1; i <= 10000; i++) fp.Add(i / 1000f, 0.003); // uniform 0.001..10
        fp.Recompute();
        fp.P50.Should().BeApproximately(5f, 0.5f);
        fp.P99.Should().BeApproximately(9.9f, 0.9f);
    }

    [Fact]
    public void Percentiles_WorkInLargeNativeUnits()
    {
        var fp = new CarTorqueFingerprint();
        for (int i = 0; i < 5000; i++) fp.Add(3000f, 0.003); // R3E-style Newtons
        fp.Recompute();
        fp.P99.Should().BeApproximately(3000f, 300f);
    }

    [Fact]
    public void Converges_OnlyAfterTimeAndLap()
    {
        var fp = new CarTorqueFingerprint { ValidSeconds = 120, Laps = 0 };
        fp.IsConverged.Should().BeFalse();
        fp.Laps = 1;
        fp.IsConverged.Should().BeTrue();
    }

    [Fact]
    public void Store_IgnoresSlowPitAndFrozenFrames_AndCountsLaps()
    {
        var path = Path.Combine(Path.GetTempPath(), $"fp-{Guid.NewGuid():N}.json");
        try
        {
            var store = new CarTorqueFingerprintStore(path);
            store.AddSample("LMU", "Car A", "GT3", "lmu.shaft_torque", 10f, 20f, false, false, 1);   // too slow
            store.AddSample("LMU", "Car A", "GT3", "lmu.shaft_torque", 10f, 150f, true, false, 1);   // pit lane
            store.AddSample("LMU", "Car A", "GT3", "lmu.shaft_torque", 10f, 150f, false, true, 1);   // frozen
            store.Active!.ValidSeconds.Should().Be(0);

            store.AddSample("LMU", "Car A", "GT3", "lmu.shaft_torque", 10f, 150f, false, false, 2);  // lap 1→2
            store.Active!.Laps.Should().Be(1);
            store.Active!.CarClass.Should().Be("GT3");
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData(2f, 0f, 2f, 0.5f)]      // keep 0 → same peak as reference
    [InlineData(2f, 1f, 2f, 1f)]        // keep 1, cap allows → unchanged
    [InlineData(2f, 1f, 1.2f, 0.6f)]    // keep 1, capped at 1.2× reference peak
    [InlineData(0.5f, 0.7f, 1.2f, 1.231f)] // light car: peak 0.5^0.7=0.616 → gain 1.23
    public void Normaliser_TargetGain(float ratio, float keep, float maxPeak, float expected)
        => CarCharacterNormaliser.TargetGain(ratio * 10f, 10f, keep, maxPeak).Should().BeApproximately(expected, 0.01f);

    [Fact]
    public void Normaliser_UnknownData_IsUnity_AndStepIsSlewLimited()
    {
        CarCharacterNormaliser.TargetGain(0f, 10f, 0.7f, 1.2f).Should().Be(1f);
        CarCharacterNormaliser.TargetGain(10f, 0f, 0.7f, 1.2f).Should().Be(1f);
        CarCharacterNormaliser.Step(1f, 0.5f, 1f, hold: false, parked: false).Should().BeApproximately(0.98f, 1e-4f);
        CarCharacterNormaliser.Step(1f, 0.5f, 1f, hold: true, parked: false).Should().Be(1f);
        CarCharacterNormaliser.Step(1f, 0.5f, 1f, hold: false, parked: true).Should().Be(0.5f);
    }

    [Fact]
    public void Pipelines_CarCharacterGain_ScalesLmuAndR3eCore_AndIsZeroImpactWhenOff()
    {
        static float Core(AcEvoFfbTuner.Core.FfbProcessing.FfbPipeline p, bool active, float gain)
        {
            p.CarCharacterActive = active; p.CarCharacterGain = gain;
            var raw = new AcEvoFfbTuner.Core.FfbProcessing.Models.FfbRawData
            {
                FinalFf = 0.02f, SpeedKmh = 120f, SteerAngle = 0.05f,
                Mz = new float[4], Fx = new float[4], Fy = new float[4], WheelLoad = new float[4],
                SlipRatio = new float[4], SlipAngle = new float[4],
            };
            float last = 0f;
            for (int i = 0; i < 200; i++) { raw.PacketId = i; last = p.Process(raw).CoreForce; }
            return Math.Abs(last);
        }
        float lmuOff = Core(new AcEvoFfbTuner.Core.FfbProcessing.LmuFfbPipeline(), false, 0.5f);
        float lmuOn = Core(new AcEvoFfbTuner.Core.FfbProcessing.LmuFfbPipeline(), true, 0.5f);
        lmuOff.Should().BeGreaterThan(0f);
        lmuOn.Should().BeLessThan(lmuOff);
        Core(new AcEvoFfbTuner.Core.FfbProcessing.LmuFfbPipeline(), true, 1f).Should().BeApproximately(lmuOff, 1e-4f);
    }

    [Theory]
    [InlineData("McLaren 720S LMGT3 Evo", "mclaren-720s-lmgt3-evo")]
    [InlineData("  BMW M4 GT3 (2022)  ", "bmw-m4-gt3-2022")]
    [InlineData("Porsche 911 GT3 R #92", "porsche-911-gt3-r-92")]
    [InlineData("Renault Mégane RS TCR", "renault-megane-rs-tcr")]
    [InlineData("Škoda Fabia Rally2 évo", "skoda-fabia-rally2-evo")]
    public void Slug_MatchesServerFormat(string car, string expected)
        => CarTorqueFingerprint.Slug(car).Should().Be(expected);

    [Fact]
    public void Store_SignalChange_ResetsBins_AndUploadRulesApply()
    {
        var path = Path.Combine(Path.GetTempPath(), $"fp-{Guid.NewGuid():N}.json");
        try
        {
            var store = new CarTorqueFingerprintStore(path);
            for (int i = 0; i < 50; i++) { store.AddSample("R3E", "Car", "", "r3e.old", 5f, 150f, false, false, 1); Thread.SpinWait(2000); }
            store.Active!.ValidSeconds.Should().BeGreaterThan(0);

            store.AddSample("R3E", "Car", "", "r3e.steering_force", 5f, 150f, false, false, 1);
            store.Active!.Signal.Should().Be("r3e.steering_force");
            store.Active!.ValidSeconds.Should().BeLessThan(0.06); // only the post-reset frame

            store.UploadCandidates(_ => true).Should().BeEmpty(); // not converged
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Store_RoundTripsThroughFile_AndComputesClassMedian()
    {
        var path = Path.Combine(Path.GetTempPath(), $"fp-{Guid.NewGuid():N}.json");
        try
        {
            var store = new CarTorqueFingerprintStore(path);
            foreach (var (car, torque) in new[] { ("A", 8f), ("B", 10f), ("C", 12f) })
            {
                for (int i = 0; i < 2000; i++)
                {
                    store.AddSample("LMU", car, "GT3", "lmu.shaft_torque", torque, 150f, false, false, i < 1000 ? 1 : 2);
                    Thread.SpinWait(2000); // real Stopwatch dt between frames
                }
            }
            store.SaveNow();

            var reloaded = new CarTorqueFingerprintStore(path);
            reloaded.All.Should().HaveCount(3);
            reloaded.All.Should().OnlyContain(f => f.Laps == 1 && f.P99 > 0);

            // Converge every car directly in the file, then reload for the median.
            var json = File.ReadAllText(path);
            var list = System.Text.Json.JsonSerializer.Deserialize<List<CarTorqueFingerprint>>(json)!;
            foreach (var f in list) f.ValidSeconds = 200;
            File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(list));

            var conv = new CarTorqueFingerprintStore(path);
            conv.ReferenceP99("LMU", "GT3").Should().BeApproximately(10f, 1f);
            conv.ReferenceP99("R3E", "GT3").Should().Be(0f);
        }
        finally { File.Delete(path); }
    }
}
