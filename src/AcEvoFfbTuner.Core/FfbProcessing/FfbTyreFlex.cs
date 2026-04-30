using AcEvoFfbTuner.Core.FfbProcessing.Models;

namespace AcEvoFfbTuner.Core.FfbProcessing;

public sealed class FfbTyreFlex
{
    public float FlexGain { get; set; } = 0.0f;
    public float CarcassStiffness { get; set; } = 1.0f;
    public float FlexSmoothing { get; set; } = 0.70f;
    public float ContactPatchWeight { get; set; } = 0.5f;
    public float LoadFlexGain { get; set; } = 0.3f;

    private float _prevFrontLoad;
    private float _smFlexForce;
    private float _prevRearLoad;

    public float Apply(float force, FfbRawData raw)
    {
        if (FlexGain < 0.001f && LoadFlexGain < 0.001f)
            return force;

        float contribution = ComputeCarcassFlex(raw) * FlexGain
                           + ComputeContactPatchVariation(raw) * LoadFlexGain;

        contribution = Math.Clamp(contribution, -0.25f, 0.25f);

        float alpha = 1.0f - FlexSmoothing;
        _smFlexForce = _smFlexForce * FlexSmoothing + contribution * alpha;

        return force + _smFlexForce;
    }

    private float ComputeCarcassFlex(FfbRawData raw)
    {
        float frontLoad = raw.WheelLoad[0] + raw.WheelLoad[1];
        float loadDelta = frontLoad - _prevFrontLoad;
        _prevFrontLoad = frontLoad;

        float frontMz = Math.Abs(raw.Mz[0]) + Math.Abs(raw.Mz[1]);
        float frontFy = Math.Abs(raw.Fy[0]) + Math.Abs(raw.Fy[1]);
        float lateralForceTotal = frontMz + frontFy * 0.5f;

        float stiffness = Math.Max(CarcassStiffness, 0.1f);
        float flexResponse = -loadDelta / stiffness * 0.001f;

        float forceCoupling = Math.Clamp(lateralForceTotal * 0.01f / stiffness, 0f, 1f);
        float combined = flexResponse * (1.0f + forceCoupling);

        return Math.Clamp(combined, -0.3f, 0.3f);
    }

    private float ComputeContactPatchVariation(FfbRawData raw)
    {
        float rearLoad = raw.WheelLoad[2] + raw.WheelLoad[3];
        float rearLoadDelta = rearLoad - _prevRearLoad;
        _prevRearLoad = rearLoad;

        float rearSlip = Math.Abs(raw.SlipAngle[2]) + Math.Abs(raw.SlipAngle[3]);
        float slipFactor = Math.Min(rearSlip * 0.5f, 1.0f);

        float frontSlip = Math.Abs(raw.SlipAngle[0]) + Math.Abs(raw.SlipAngle[1]);
        float frontSlipFactor = Math.Min(frontSlip * 0.5f, 1.0f);

        float loadTransfer = Math.Abs(raw.AccG[0]) * 0.1f;

        float contactPatchVar = rearLoadDelta * 0.0005f * (1.0f + slipFactor)
                              - loadTransfer * ContactPatchWeight * frontSlipFactor;

        return Math.Clamp(contactPatchVar, -0.2f, 0.2f);
    }

    public void Reset()
    {
        _prevFrontLoad = 0f;
        _prevRearLoad = 0f;
        _smFlexForce = 0f;
    }
}
