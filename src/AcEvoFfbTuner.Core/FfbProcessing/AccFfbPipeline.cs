using AcEvoFfbTuner.Core.FfbProcessing.Models;

namespace AcEvoFfbTuner.Core.FfbProcessing;

public sealed class AccFfbPipeline : FfbPipeline
{
    public AccFfbPipeline()
    {
        ChannelMixer.MzScale = 5f;
    }

    private bool _gearChangeMuteEnabled = true;
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
    public float ForceGain { get; set; } = 4.0f;

    public override bool GearShiftFilterEnabled
    {
        get => _gearChangeMuteEnabled;
        set { _gearChangeMuteEnabled = value; }
    }

    public override FfbProcessedData Process(FfbRawData raw)
    {
        ChannelMixer.MzScale = 5f;
        ChannelMixer.MzFrontGain = 0.5f;

        VibrationMixer.KerbGain = 0f;
        VibrationMixer.RoadGain = 0f;
        VibrationMixer.SlipGain = 0f;
        VibrationMixer.AbsGain = 0f;
        VibrationMixer.ScrubGain = 0f;
        VibrationMixer.RearSlipGain = 0f;
        VibrationMixer.SuspensionRoadGain = 0f;
        DynamicEffects.SuspensionGain = 0f;
        DynamicEffects.LateralGGain = 0f;
        DynamicEffects.LongitudinalGGain = 0f;
        DynamicEffects.YawRateGain = 0f;
        TyreFlex.FlexGain = 0f;

        Damping.ViscousCoefficient = 0.01f;
        Damping.FrictionLevel = 0.01f;
        Damping.SpeedDampingCoefficient = 0f;

        var result = base.Process(raw);

        float brakeBoost = 1f;
        if (raw.BrakeInput > BrakeBoostThreshold)
        {
            float brakeIntensity = Math.Min((raw.BrakeInput - BrakeBoostThreshold) / (1f - BrakeBoostThreshold), 1f);
            brakeBoost = 1f + BrakeBoostGain * brakeIntensity;
        }
        float gain = brakeBoost * ForceGain;
        result.MainForce *= gain;
        result.CoreForce *= gain;

        return result;
    }
}