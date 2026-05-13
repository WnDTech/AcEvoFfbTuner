using AcEvoFfbTuner.Core.FfbProcessing.Models;

namespace AcEvoFfbTuner.Core.FfbProcessing;

public sealed class FfbTyreFlex
{
    public float FlexGain { get; set; } = 0.0f;
    public float CarcassStiffness { get; set; } = 1.0f;
    public float FlexSmoothing { get; set; } = 0.70f;
    public float ContactPatchWeight { get; set; } = 0.5f;
    public float LoadFlexGain { get; set; } = 0.3f;

    /// <summary>
    /// Sensitivity for TyreContactPointY (vertical contact patch position) changes.
    /// Higher = more responsive to tyre compression. AC EVO contact points are
    /// typically in meters, so per-frame deltas are small (0.001–0.01).
    /// Default 1.0. Increase if tyre compression feel is too subtle.
    /// </summary>
    public float ContactPatchSensitivity { get; set; } = 1.0f;

    private float _smFlexForce;

    private float[] _prevContactNormalDeviation = new float[4];
    private float[] _prevContactPointY = new float[4];
    private float[] _prevWheelLoad = new float[4];

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

    /// <summary>
    /// Carcass flex from real TyreContactNormal changes.
    /// When a tyre hits a curb or angled surface, the contact normal tilts away from (0,1,0).
    /// The rate-of-change of this deviation = real carcass deformation rate.
    /// Combined with Mz/Fy forces for coupling (lateral forces deform the carcass).
    /// </summary>
    private float ComputeCarcassFlex(FfbRawData raw)
    {
        float frontDeviationDelta = 0f;
        for (int i = 0; i < 2; i++)
        {
            float deviation = MathF.Sqrt(
                raw.TyreContactNormalX[i] * raw.TyreContactNormalX[i] +
                raw.TyreContactNormalZ[i] * raw.TyreContactNormalZ[i]);
            float delta = deviation - _prevContactNormalDeviation[i];
            _prevContactNormalDeviation[i] = deviation;
            frontDeviationDelta += delta;
        }
        frontDeviationDelta *= 0.5f;

        float frontMz = Math.Abs(raw.Mz[0]) + Math.Abs(raw.Mz[1]);
        float frontFy = Math.Abs(raw.Fy[0]) + Math.Abs(raw.Fy[1]);
        float lateralForceTotal = frontMz + frontFy * 0.5f;

        float stiffness = Math.Max(CarcassStiffness, 0.1f);

        float flexResponse = frontDeviationDelta / stiffness * 10f;

        float forceCoupling = Math.Clamp(lateralForceTotal * 0.01f / stiffness, 0f, 1f);
        float combined = flexResponse * (1.0f + forceCoupling);

        return Math.Clamp(combined, -0.3f, 0.3f);
    }

    /// <summary>
    /// Contact patch variation from real TyreContactPoint Y changes + WheelLoad changes.
    /// TyreContactPoint Y changing = actual contact patch vertical movement (compression).
    /// WheelLoad rate-of-change = actual force transmission through the contact patch.
    /// Real data, no simulation.
    /// </summary>
    private float ComputeContactPatchVariation(FfbRawData raw)
    {
        float frontCompressionDelta = 0f;
        for (int i = 0; i < 2; i++)
        {
            float delta = raw.TyreContactPointY[i] - _prevContactPointY[i];
            _prevContactPointY[i] = raw.TyreContactPointY[i];
            frontCompressionDelta += delta;
        }
        frontCompressionDelta *= 0.5f;

        float rearLoadDelta = 0f;
        for (int i = 2; i < 4; i++)
        {
            float delta = raw.WheelLoad[i] - _prevWheelLoad[i];
            _prevWheelLoad[i] = raw.WheelLoad[i];
            rearLoadDelta += delta;
        }
        rearLoadDelta *= 0.5f;

        float rearSlip = Math.Abs(raw.SlipAngle[2]) + Math.Abs(raw.SlipAngle[3]);
        float slipFactor = Math.Min(rearSlip * 0.5f, 1.0f);

        float frontSlip = Math.Abs(raw.SlipAngle[0]) + Math.Abs(raw.SlipAngle[1]);
        float frontSlipFactor = Math.Min(frontSlip * 0.5f, 1.0f);

        float loadTransfer = Math.Abs(raw.AccG[0]) * 0.1f;

        float contactPatchVar = (frontCompressionDelta * ContactPatchSensitivity + rearLoadDelta * 0.0005f) * (1.0f + slipFactor)
                              - loadTransfer * ContactPatchWeight * frontSlipFactor;

        return Math.Clamp(contactPatchVar, -0.2f, 0.2f);
    }

    public void Reset()
    {
        _smFlexForce = 0f;
        Array.Clear(_prevContactNormalDeviation);
        Array.Clear(_prevContactPointY);
        Array.Clear(_prevWheelLoad);
    }
}
