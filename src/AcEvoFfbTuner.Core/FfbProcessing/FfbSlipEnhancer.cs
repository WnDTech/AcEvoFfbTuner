using AcEvoFfbTuner.Core.FfbProcessing.Models;

namespace AcEvoFfbTuner.Core.FfbProcessing;

public sealed class FfbSlipEnhancer
{
    public float SlipRatioGain { get; set; } = 0.0f;
    public float SlipAngleGain { get; set; } = 0.0f;
    public float SlipThreshold { get; set; } = 0.05f;
    public bool UseFrontOnly { get; set; } = true;

    private float _smSlipForce;

    public float Apply(float force, FfbRawData raw)
    {
        if (SlipRatioGain < 0.001f && SlipAngleGain < 0.001f)
            return force;

        int startIdx = UseFrontOnly ? 0 : 0;
        int endIdx = UseFrontOnly ? 2 : 4;

        float avgSlipRatio = 0f;
        float avgSlipAngle = 0f;
        int count = 0;

        for (int i = startIdx; i < endIdx; i++)
        {
            if (Math.Abs(raw.SlipRatio[i]) > SlipThreshold)
                avgSlipRatio += raw.SlipRatio[i];
            if (Math.Abs(raw.SlipAngle[i]) > SlipThreshold)
                avgSlipAngle += raw.SlipAngle[i];
            count++;
        }

        if (count > 0)
        {
            avgSlipRatio /= count;
            avgSlipAngle /= count;
        }

        float slipForce = (avgSlipRatio * SlipRatioGain + avgSlipAngle * SlipAngleGain);
        float maxSlip = Math.Max(Math.Abs(force) * 0.3f, 0.1f);
        slipForce = Math.Clamp(slipForce, -maxSlip, maxSlip);

        _smSlipForce = _smSlipForce * 0.65f + slipForce * 0.35f;
        _smSlipForce = Math.Clamp(_smSlipForce, -maxSlip, maxSlip);

        return force + _smSlipForce;
    }
}
