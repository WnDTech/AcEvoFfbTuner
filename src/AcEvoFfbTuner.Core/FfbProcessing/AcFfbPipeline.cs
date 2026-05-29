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
        // ── Save and apply AC-specific mixer scales ──────────────
        float savedMzScale = ChannelMixer.MzScale;
        float savedMzFrontGain = ChannelMixer.MzFrontGain;
        float savedFxScale = ChannelMixer.FxScale;
        float savedFxFrontGain = ChannelMixer.FxFrontGain;
        float savedFyScale = ChannelMixer.FyScale;
        float savedFyFrontGain = ChannelMixer.FyFrontGain;
        float savedFriction = Damping.FrictionLevel;

        // AC base scales (tuned for synthesized forces vs EVO native forces)
        const float acBaseMzScale = 2f;
        const float acBaseMzgain = 0.4f;
        const float acBaseFxScale = 1500f;
        const float acBaseFxgain = 0.15f;
        const float acBaseFyScale = 1500f;

        // Apply user's slider as relative factor over AC base (EVO default MzScale=30)
        float userMzFactor = Math.Clamp(savedMzScale / 30f, 0.1f, 5f);
        float userFxFactor = Math.Clamp(savedFxScale / 500f, 0.1f, 5f);
        float userFyFactor = Math.Clamp(savedFyScale / 5000f, 0.1f, 5f);

        ChannelMixer.MzScale = acBaseMzScale * userMzFactor;
        ChannelMixer.MzFrontGain = acBaseMzgain * userMzFactor;
        ChannelMixer.FxScale = acBaseFxScale * userFxFactor;
        ChannelMixer.FxFrontGain = acBaseFxgain * userFxFactor;
        ChannelMixer.FyScale = acBaseFyScale * userFyFactor;
        ChannelMixer.FyFrontGain = 0.0f;
        ChannelMixer.FyFrontEnabled = false;
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

        if (raw.SpeedKmh < 0.5f)
            coreOutput = 0f;
        else if (raw.SpeedKmh < 5.0f)
            coreOutput *= (raw.SpeedKmh - 0.5f) / 4.5f;

        float vibration = raw.KerbVibration * VibrationMixer.KerbGain
                        + raw.SlipVibrations * VibrationMixer.SlipGain
                        + raw.RoadVibrations * VibrationMixer.RoadGain
                        + raw.AbsVibrations * VibrationMixer.AbsGain;
        vibration *= VibrationMixer.MasterGain;

        float eqOutput = Equalizer.Process(coreOutput);
        float finalOutput = eqOutput;

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
