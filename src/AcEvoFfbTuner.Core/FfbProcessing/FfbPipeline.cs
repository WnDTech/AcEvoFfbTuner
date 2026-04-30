using AcEvoFfbTuner.Core.FfbProcessing.Models;

namespace AcEvoFfbTuner.Core.FfbProcessing;

public sealed class FfbPipeline
{
    public FfbChannelMixer ChannelMixer { get; } = new();
    public FfbLutCurve LutCurve { get; } = new();
    public FfbDamping Damping { get; } = new();
    public FfbSlipEnhancer SlipEnhancer { get; } = new();
    public FfbDynamicEffects DynamicEffects { get; } = new();
    public FfbVibrationMixer VibrationMixer { get; } = new();
    public FfbLfeGenerator LfeGenerator { get; } = new();
    public FfbOutputClipper OutputClipper { get; } = new();
    public FfbEqualizer Equalizer { get; } = new();
    public FfbTyreFlex TyreFlex { get; } = new();

    public float ForceScale { get; set; } = 1.0f;
    public float OutputGain { get; set; } = 1.0f;
    public float MasterGain { get; set; } = 1.0f;
    public float CompressionPower { get; set; } = 1.0f;

    public bool AutoGainEnabled { get; set; } = false;
    public float AutoGainScale { get; set; } = 1.0f;

    public bool SignCorrectionEnabled { get; set; } = true;
    public float CenterDeadzone { get; set; } = 0.04f;
    public float SoftCenterDegrees { get; set; } = 1.5f;
    public float NoiseFloor { get; set; } = 0.003f;
    public float MaxSlewRate { get; set; } = 0.40f;

    public float SteerDirDeadzone { get; set; } = 0.004f;

    public float CenterSuppressionDegrees { get; set; } = 0.5f;

    public float HysteresisThreshold { get; set; } = 0.015f;

    public int HysteresisWatchdogFrames { get; set; } = 5;

    public float CenterKneePower { get; set; } = 1.0f;

    public int GearShiftSmoothingTicks { get; set; } = 7;
    public float GearShiftSlewRate { get; set; } = 0.01f;

    private float _prevSlewOutput;
    private float _smoothSteerAngle;

    public FfbProcessedData Process(FfbRawData raw)
    {
        float autoGain = 1.0f;
        if (AutoGainEnabled && raw.CarFfbMultiplier > 0.001f)
        {
            autoGain = AutoGainScale / raw.CarFfbMultiplier;
        }

        _smoothSteerAngle = _smoothSteerAngle * 0.85f + raw.SteerAngle * 0.15f;

        float mixedForce = ChannelMixer.Mix(raw, out var channels);

        float normalized = mixedForce * MasterGain * autoGain / Math.Max(ForceScale, 0.001f);

        float absNorm = Math.Abs(normalized);
        float postLut = LutCurve.Apply(absNorm) * Math.Sign(normalized);

        float postSlip = SlipEnhancer.Apply(postLut, raw);

        float postDamping = Damping.Apply(postSlip, raw.SpeedKmh, _smoothSteerAngle);

        float postDynamic = DynamicEffects.Apply(postDamping, raw);

        float postTyreFlex = TyreFlex.Apply(postDynamic, raw);

        float output = OutputClipper.Process(postTyreFlex * OutputGain, out bool isClipping);

        float speedNoiseScale = raw.SpeedKmh < 10.0f
            ? 1.0f + (1.0f - raw.SpeedKmh / 10.0f) * 2.0f
            : 1.0f;
        float effectiveNoiseFloor = NoiseFloor * speedNoiseScale;

        if (Math.Abs(output) < effectiveNoiseFloor)
            output = 0f;

        // ── Physics-preserving center fade ──
        // The physics model's Mz (self-aligning torque) already encodes the correct
        // force direction and magnitude — including the critical Mz peak/dropoff that
        // signals the approach to the tire's grip limit. We preserve the physics sign
        // and magnitude, applying only a gentle linear fade within a narrow band around
        // center to prevent notchiness at the zero-crossing point.
        // Device-level inversion (_invertForce) is now handled at FfbDeviceManager.
        if (SignCorrectionEnabled && raw.SpeedKmh > 0.5f)
        {
            if (Math.Abs(output) > effectiveNoiseFloor)
            {
                float absRawDeg = Math.Abs(raw.SteerAngle) * 450f;

                // Linear ramp from 0 at center to 1 at CenterSuppressionDegrees (default 0.5°).
                // Linear (not quadratic) preserves the Mz slope in the linear region where
                // on-center sensitivity matters most.
                float centerFade = Math.Clamp(absRawDeg / Math.Max(CenterSuppressionDegrees, 0.1f), 0f, 1f);

                output *= centerFade;
            }
        }

        if (CenterKneePower > 1.001f && Math.Abs(output) > 0f)
            output = Math.Sign(output) * MathF.Pow(Math.Abs(output), CenterKneePower);

        float finalOutput = output;

        if (raw.SpeedKmh < 0.5f)
        {
            finalOutput = 0f;
            _prevSlewOutput = 0f;
        }
        else if (raw.SpeedKmh < 5.0f)
        {
            float speedFactor = (raw.SpeedKmh - 0.5f) / 4.5f;
            finalOutput *= speedFactor;
        }

        float lowSpeedSlewScale = Math.Clamp(raw.SpeedKmh / 15.0f, 0.05f, 1.0f);
        float highSpeedSlewScale = raw.SpeedKmh > 200.0f
            ? Math.Max(1.0f - (raw.SpeedKmh - 200.0f) / 250.0f, 0.4f)
            : 1.0f;
        float speedSlewScale = lowSpeedSlewScale * highSpeedSlewScale;

        float effectiveSlewRate = MaxSlewRate * speedSlewScale;

        float slewDelta = finalOutput - _prevSlewOutput;
        if (Math.Abs(slewDelta) > effectiveSlewRate)
        {
            finalOutput = _prevSlewOutput + Math.Sign(slewDelta) * effectiveSlewRate;
        }
        _prevSlewOutput = raw.SpeedKmh < 0.5f ? 0f : finalOutput;

        finalOutput = Equalizer.Process(finalOutput);

        float vibration = VibrationMixer.Mix(raw);

        float absMod = VibrationMixer.AbsForceModulation;
        if (absMod > 0.001f)
        {
            float sign = finalOutput >= 0f ? 1f : -1f;
            if (Math.Abs(finalOutput) < 0.01f)
                sign = Math.Abs(raw.SteerAngle) > SteerDirDeadzone ? -Math.Sign(raw.SteerAngle) : 1f;
            finalOutput = Math.Clamp(finalOutput + absMod * sign, -1f, 1f);
        }

        float roadMod = VibrationMixer.RoadForceModulation;
        if (Math.Abs(roadMod) > 0.001f)
            finalOutput = Math.Clamp(finalOutput + roadMod, -1f, 1f);

        // ── Tire scrub texture: high-frequency grain at the limit ──
        // Injected into main force when front slip angles approach Mz peak.
        float scrubMod = VibrationMixer.ScrubModulation;
        if (Math.Abs(scrubMod) > 0.001f)
            finalOutput = Math.Clamp(finalOutput + scrubMod, -1f, 1f);


        // ── Rear slip warning: low-frequency rumble when rear loses grip ──
        // Distinct from front scrub — uses lower frequencies (12-25Hz) so the
        // driver can distinguish "rear is sliding" from "front is at the limit".
        // Also triggers on yaw acceleration (car snapping into oversteer).
        float rearMod = VibrationMixer.RearSlipModulation;
        if (Math.Abs(rearMod) > 0.001f)
            finalOutput = Math.Clamp(finalOutput + rearMod, -1f, 1f);

        float lfe = LfeGenerator.Generate(raw);
        if (Math.Abs(lfe) > 0.001f)
            finalOutput = Math.Clamp(finalOutput + lfe, -1f, 1f);

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
            PostCompressionForce = normalized,
            PostLutForce = postLut,
            PostSlipForce = postSlip,
            PostDampingForce = postDamping,
            PostDynamicForce = postDynamic,
            AutoGainApplied = autoGain,
            IsClipping = isClipping,
            SpeedKmh = raw.SpeedKmh,
            SteerAngle = raw.SteerAngle,
            PacketId = raw.PacketId
        };
    }

    public void Reset()
    {
        ChannelMixer.Reset();
        Damping.Reset();
        DynamicEffects.Reset();
        VibrationMixer.Reset();
        LfeGenerator.Reset();
        Equalizer.Reset();
        TyreFlex.Reset();
        _prevSlewOutput = 0f;
        _smoothSteerAngle = 0f;
    }
}
