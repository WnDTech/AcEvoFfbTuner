using AcEvoFfbTuner.Core.FfbProcessing.Models;

namespace AcEvoFfbTuner.Core.FfbProcessing;

public sealed class AcFfbPipeline : FfbPipeline
{
    public AcFfbPipeline()
    {
        ChannelMixer.AutoMinPeak = float.MaxValue;
    }

    private float _dcBlockSmooth;
    private int _processCallCount;
    private float _prevCoreOutput;

    private bool _gearChangeMuteEnabled;
    public bool GearChangeMuteEnabled
    {
        get => _gearChangeMuteEnabled;
        set
        {
            _gearChangeMuteEnabled = value;
            base.GearShiftFilterEnabled = value;
        }
    }
    public float GearSpikeThreshold { get; set; } = 3000f;
    public float BrakeBoostGain { get; set; } = 0.4f;
    public float BrakeBoostThreshold { get; set; } = 0.1f;

    public override bool GearShiftFilterEnabled
    {
        get => _gearChangeMuteEnabled;
        set { _gearChangeMuteEnabled = value; }
    }

    public override FfbProcessedData Process(FfbRawData raw)
    {
        float brakeBoost = 1f;
        if (raw.BrakeInput > BrakeBoostThreshold)
        {
            float brakeIntensity = Math.Clamp((raw.BrakeInput - BrakeBoostThreshold) / (1f - BrakeBoostThreshold), 0f, 1f);
            brakeBoost = 1f + BrakeBoostGain * brakeIntensity;
        }

        float savedMzScale = ChannelMixer.MzScale;
        float savedMzFrontGain = ChannelMixer.MzFrontGain;
        float savedFxScale = ChannelMixer.FxScale;
        float savedFxFrontGain = ChannelMixer.FxFrontGain;
        float savedFyScale = ChannelMixer.FyScale;
        float savedFyFrontGain = ChannelMixer.FyFrontGain;
        float savedFriction = Damping.FrictionLevel;

        ChannelMixer.MzScale = 2f;
        ChannelMixer.MzFrontGain = 0.4f;
        ChannelMixer.FxScale = 1500f;
        ChannelMixer.FxFrontGain = 0.15f;
        ChannelMixer.FyScale = 1500f;
        ChannelMixer.FyFrontGain = 0.4f;
        Damping.FrictionLevel = 0.02f;

        float mixedForce = ChannelMixer.Mix(raw, out var channels);

        ChannelMixer.MzScale = savedMzScale;
        ChannelMixer.MzFrontGain = savedMzFrontGain;
        ChannelMixer.FxScale = savedFxScale;
        ChannelMixer.FxFrontGain = savedFxFrontGain;
        ChannelMixer.FyScale = savedFyScale;
        ChannelMixer.FyFrontGain = savedFyFrontGain;
        Damping.FrictionLevel = savedFriction;

        float rawCoreForce = channels.RawCoreForce;

        float autoGain = 1f;
        if (AutoGainEnabled && raw.CarFfbMultiplier > 0.001f)
            autoGain = AutoGainScale / raw.CarFfbMultiplier;

        float coreNorm = rawCoreForce * MasterGain * autoGain / Math.Max(ForceScale, 0.001f);
        float absCoreNorm = Math.Abs(coreNorm);
        float corePostLut = LutCurve.Apply(absCoreNorm) * Math.Sign(coreNorm);

        float coreDamped = Damping.Apply(corePostLut, raw.SpeedKmh, raw.SteerAngle);
        float coreOutput = coreDamped * OutputGain;
        coreOutput = _prevCoreOutput * 0.8f + coreOutput * 0.2f;
        _prevCoreOutput = coreOutput;

        coreOutput *= brakeBoost;

        if (raw.SpeedKmh < 0.5f)
            coreOutput = 0f;
        else if (raw.SpeedKmh < 5.0f)
            coreOutput *= (raw.SpeedKmh - 0.5f) / 4.5f;

        float vibration = raw.KerbVibration * VibrationMixer.KerbGain
                        + raw.SlipVibrations * VibrationMixer.SlipGain
                        + raw.RoadVibrations * VibrationMixer.RoadGain
                        + raw.AbsVibrations * VibrationMixer.AbsGain;
        vibration *= VibrationMixer.MasterGain;

        float finalOutput = coreOutput;

        finalOutput = OutputClipper.Process(finalOutput, out bool isClipping);

        _processCallCount++;
        if (_processCallCount % 300 == 0)
        {
            try
            {
                var path = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "AcEvoFfbTuner", "ac_pipeline_debug.log");
                File.AppendAllText(path,
                    $"[{DateTime.Now:HH:mm:ss.fff}] call#{_processCallCount} Mz=[{raw.Mz[0]:F2},{raw.Mz[1]:F2}] Fy=[{raw.Fy[0]:F2}] spd={raw.SpeedKmh:F1} steer={raw.SteerAngle:F3} coreNorm={coreNorm:F4} coreDamped={coreDamped:F4} coreOutput={coreOutput:F4} final={finalOutput:F4}\n");
            }
            catch { }
        }

        return new FfbProcessedData
        {
            MainForce = finalOutput,
            VibrationForce = vibration,
            RawFinalFf = raw.FinalFf,
            ChannelMzFront = channels.MzFront,
            ChannelFxFront = channels.FxFront,
            ChannelFyFront = channels.FyFront,
            ChannelMzRear = channels.MzRear,
            ChannelFxRear = channels.FxRear,
            ChannelFyRear = channels.FyRear,
            ChannelFinalFf = channels.FinalFf,
            PostCompressionForce = coreNorm,
            PostLutForce = corePostLut,
            PostDampingForce = coreDamped,
            PostOutputGainForce = coreOutput,
            PostDynamicForce = 0f,
            AutoGainApplied = autoGain,
            IsClipping = isClipping,
            GearShiftMuteGain = 1f,
        };
    }

    protected override void OnDetailForceProcessed(float coreOutput, ref float detailForce)
    {
        detailForce = 0f;
    }
}
