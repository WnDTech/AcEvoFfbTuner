using AcEvoFfbTuner.Core.Config;

namespace AcEvoFfbTuner.Core.PedalInput;

public sealed class PedalCalibration
{
    private float _smoothedGas;
    private float _smoothedBrake;
    private float _smoothedClutch;

    public PedalState Apply(RawPedalState raw, PedalConfig config)
    {
        var gas = CalibrateAxis(raw.GasRaw, config.Gas, ref _smoothedGas);
        var brake = CalibrateAxis(raw.BrakeRaw, config.Brake, ref _smoothedBrake);
        var clutch = CalibrateAxis(raw.ClutchRaw, config.Clutch, ref _smoothedClutch);

        return new PedalState
        {
            GasInput = gas,
            BrakeInput = brake,
            ClutchInput = clutch,
            Source = raw.Source,
            TimestampTicks = raw.TimestampTicks
        };
    }

    public void Reset()
    {
        _smoothedGas = 0f;
        _smoothedBrake = 0f;
        _smoothedClutch = 0f;
    }

    private static float CalibrateAxis(float raw, PedalAxisConfig axis, ref float smoothed)
    {
        float value = raw;

        if (axis.Invert)
            value = 1f - value;

        value = ApplyDeadzone(value, axis.Deadzone);

        value = RemapRange(value, axis.Min, axis.Max);

        value = ApplyEma(value, axis.Smoothing, ref smoothed);

        return Math.Clamp(value, 0f, 1f);
    }

    private static float ApplyDeadzone(float value, float deadzone)
    {
        if (deadzone <= 0f) return value;
        if (value <= deadzone) return 0f;
        return (value - deadzone) / (1f - deadzone);
    }

    private static float RemapRange(float value, float min, float max)
    {
        if (max <= min) return value;
        if (value <= min) return 0f;
        if (value >= max) return 1f;
        return (value - min) / (max - min);
    }

    private static float ApplyEma(float raw, float alpha, ref float smoothed)
    {
        if (alpha <= 0f) return raw;
        if (alpha >= 0.999f)
        {
            smoothed = raw;
            return raw;
        }
        smoothed = alpha * raw + (1f - alpha) * smoothed;
        return smoothed;
    }
}
