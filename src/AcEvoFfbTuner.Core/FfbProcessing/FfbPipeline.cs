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
    public FfbOutputClipper OutputClipper { get; } = new();

    public float ForceScale { get; set; } = 1.0f;
    public float OutputGain { get; set; } = 1.0f;
    public float MasterGain { get; set; } = 1.0f;
    public float CompressionPower { get; set; } = 1.0f;

    public bool AutoGainEnabled { get; set; } = false;
    public float AutoGainScale { get; set; } = 1.0f;

    public bool SignCorrectionEnabled { get; set; } = true;
    public float CenterDeadzone { get; set; } = 0.04f;
    public float SoftCenterDegrees { get; set; } = 1.5f;
    public float NoiseFloor { get; set; } = 0.005f;
    public float MaxSlewRate { get; set; } = 0.20f;

    public float SteerDirDeadzone { get; set; } = 0.004f;

    public float CenterSuppressionDegrees { get; set; } = 6f;

    public float HysteresisThreshold { get; set; } = 0.015f;

    public int HysteresisWatchdogFrames { get; set; } = 5;

    public float CenterKneePower { get; set; } = 1.0f;

    public int GearShiftSmoothingTicks { get; set; } = 7;
    public float GearShiftSlewRate { get; set; } = 0.01f;

    private float _prevOutput;
    private float _prevSlewOutput;
    private float _lastSentOutput;
    private float _hysteresisOutput;
    private bool _hysteresisInitialized;
    private int _hysteresisHoldCount;
    private float _smoothSteerAngle;
    private int _prevGear = -1;
    private int _gearShiftCounter;

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
        float compressed = MathF.Tanh(absNorm * CompressionPower);
        float signedCompressed = Math.Sign(normalized) * compressed;

        float postLut = LutCurve.Apply(compressed) * Math.Sign(normalized);

        float postSlip = SlipEnhancer.Apply(postLut, raw);

        float postDamping = Damping.Apply(postSlip, raw.SpeedKmh, _smoothSteerAngle);

        float postDynamic = DynamicEffects.Apply(postDamping, raw);

        float output = OutputClipper.Process(postDynamic * OutputGain, out bool isClipping);

        if (SignCorrectionEnabled && raw.SpeedKmh > 2.0f)
        {
            float absOutput = Math.Abs(output);
            if (absOutput > NoiseFloor)
            {
                float absRawAngle = Math.Abs(raw.SteerAngle);
                float absRawDeg = absRawAngle * 450f;

                float speedSuppScale = 1.0f + Math.Clamp((raw.SpeedKmh - 10.0f) / 200.0f, 0f, 0.5f);
                float suppressionDeg = Math.Max(CenterSuppressionDegrees * speedSuppScale, 5f);

                float oscillationDeadDeg = raw.SpeedKmh > 30.0f
                    ? Math.Clamp((raw.SpeedKmh - 30.0f) / 40.0f, 0.5f, 3f)
                    : 0f;

                float totalZone = Math.Min(oscillationDeadDeg + Math.Max(suppressionDeg, 5f), 12f);
                float ct = Math.Clamp(absRawDeg / Math.Max(totalZone, 0.1f), 0f, 1f);
                float centerFade = ct * ct;

                float forceDirection = absRawAngle > SteerDirDeadzone
                    ? -Math.Sign(raw.SteerAngle)
                    : 0f;

                output = absOutput * forceDirection * centerFade;
            }
        }

        float speedNoiseScale = raw.SpeedKmh < 10.0f
            ? 1.0f + (1.0f - raw.SpeedKmh / 10.0f) * 2.0f
            : 1.0f;
        float effectiveNoiseFloor = NoiseFloor * speedNoiseScale;

        if (Math.Abs(output) < effectiveNoiseFloor)
            output = 0f;

        if (CenterKneePower > 1.001f && Math.Abs(output) > 0f)
            output = Math.Sign(output) * MathF.Pow(Math.Abs(output), CenterKneePower);

        if (_prevGear >= 0 && raw.Gear != _prevGear)
            _gearShiftCounter = GearShiftSmoothingTicks;
        _prevGear = raw.Gear;

        float speedHystScale = raw.SpeedKmh < 15.0f
            ? 1.0f + (1.0f - raw.SpeedKmh / 15.0f) * 19.0f
            : 1.0f;
        float effectiveHystThreshold = HysteresisThreshold * speedHystScale;

        if (!_hysteresisInitialized)
        {
            _hysteresisOutput = output;
            _hysteresisInitialized = true;
        }
        else if (Math.Abs(_hysteresisOutput) < effectiveNoiseFloor * 2f
            && Math.Abs(output) >= effectiveNoiseFloor)
        {
            _hysteresisOutput = output;
            _hysteresisHoldCount = 0;
        }
        else if (Math.Abs(output - _hysteresisOutput) < effectiveHystThreshold)
        {
            _hysteresisHoldCount++;
            if (_hysteresisHoldCount >= HysteresisWatchdogFrames)
            {
                _hysteresisOutput = _hysteresisOutput * 0.5f + output * 0.5f;
                _hysteresisHoldCount = 0;
            }
            output = _hysteresisOutput;
        }
        else
        {
            _hysteresisOutput = output;
            _hysteresisHoldCount = 0;
        }

        float finalOutput = output;

        if (raw.SpeedKmh < 2.0f)
        {
            finalOutput = 0f;
            _prevSlewOutput = 0f;
            _hysteresisInitialized = false;
        }
        else if (raw.SpeedKmh < 20.0f)
        {
            float speedFactor = (raw.SpeedKmh - 2.0f) / 18.0f;
            finalOutput *= speedFactor;
        }

        float lowSpeedSlewScale = Math.Clamp(raw.SpeedKmh / 15.0f, 0.05f, 1.0f);
        float highSpeedSlewScale = raw.SpeedKmh > 200.0f
            ? Math.Max(1.0f - (raw.SpeedKmh - 200.0f) / 250.0f, 0.4f)
            : 1.0f;
        float speedSlewScale = lowSpeedSlewScale * highSpeedSlewScale;
        float baseSlewRate = _gearShiftCounter > 0 ? GearShiftSlewRate : MaxSlewRate;
        float effectiveSlewRate = baseSlewRate * speedSlewScale;

        float absSteerForSlew = Math.Abs(raw.SteerAngle);
        if (raw.SpeedKmh > 150.0f && absSteerForSlew < 0.03f)
        {
            float speedFactor = Math.Clamp((raw.SpeedKmh - 150.0f) / 100.0f, 0f, 1f);
            float centerFactor = 1.0f - absSteerForSlew / 0.03f;
            float nearCenterScale = speedFactor * centerFactor;
            effectiveSlewRate *= 1.0f - 0.5f * nearCenterScale;
        }

        if (_gearShiftCounter > 0)
            _gearShiftCounter--;

        float slewDelta = finalOutput - _prevSlewOutput;
        if (Math.Abs(slewDelta) > effectiveSlewRate)
        {
            bool isSignFlip = finalOutput * _prevSlewOutput < -0.001f;
            bool nearCenter = Math.Abs(raw.SteerAngle) < 0.08f;
            float dirChangeScale = (isSignFlip && raw.SpeedKmh > 40f && nearCenter) ? 0.25f : 1.0f;
            finalOutput = _prevSlewOutput + Math.Sign(slewDelta) * effectiveSlewRate * dirChangeScale;
        }
        _prevSlewOutput = raw.SpeedKmh < 2.0f ? 0f : finalOutput;

        float safetyDelta = finalOutput - _lastSentOutput;
        if (Math.Abs(safetyDelta) > MaxSlewRate)
            finalOutput = _lastSentOutput + Math.Sign(safetyDelta) * MaxSlewRate;
        _lastSentOutput = raw.SpeedKmh < 2.0f ? 0f : finalOutput;

        _prevOutput = finalOutput;

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
            PostCompressionForce = signedCompressed,
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
        Damping.Reset();
        DynamicEffects.Reset();
        VibrationMixer.Reset();
        _prevOutput = 0f;
        _prevSlewOutput = 0f;
        _lastSentOutput = 0f;
        _hysteresisOutput = 0f;
        _hysteresisInitialized = false;
        _hysteresisHoldCount = 0;
        _smoothSteerAngle = 0f;
        _prevGear = -1;
        _gearShiftCounter = 0;
    }
}
