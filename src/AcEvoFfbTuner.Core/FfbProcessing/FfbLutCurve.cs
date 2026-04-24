namespace AcEvoFfbTuner.Core.FfbProcessing;

public sealed class FfbLutCurve
{
    public const int DefaultPointCount = 33;

    private float[] _inputValues;
    private float[] _outputValues;
    private readonly object _lock = new();

    public FfbLutCurve()
    {
        _inputValues = new float[DefaultPointCount];
        _outputValues = new float[DefaultPointCount];
        SetLinear();
    }

    public float[] InputValues
    {
        get { lock (_lock) return (float[])_inputValues.Clone(); }
        set { lock (_lock) _inputValues = (float[])value.Clone(); }
    }

    public float[] OutputValues
    {
        get { lock (_lock) return (float[])_outputValues.Clone(); }
        set { lock (_lock) _outputValues = (float[])value.Clone(); }
    }

    public void SetLinear()
    {
        lock (_lock)
        {
            for (int i = 0; i < DefaultPointCount; i++)
            {
                float t = (float)i / (DefaultPointCount - 1);
                _inputValues[i] = t;
                _outputValues[i] = t;
            }
        }
    }

    public void SetSoftCenter(float deadzoneWidth = 0.05f, float centerDip = 0.3f)
    {
        lock (_lock)
        {
            for (int i = 0; i < DefaultPointCount; i++)
            {
                float t = (float)i / (DefaultPointCount - 1);
                _inputValues[i] = t;
                if (t < 0.5f - deadzoneWidth)
                    _outputValues[i] = t * (1f - centerDip);
                else if (t > 0.5f + deadzoneWidth)
                    _outputValues[i] = 1f - (1f - t) * (1f - centerDip);
                else
                    _outputValues[i] = t;
            }
        }
    }

    public void SetProgressive(float power = 2.0f)
    {
        lock (_lock)
        {
            for (int i = 0; i < DefaultPointCount; i++)
            {
                float t = (float)i / (DefaultPointCount - 1);
                _inputValues[i] = t;
                _outputValues[i] = MathF.Pow(t, power);
            }
        }
    }

    public void SetDeadZone(float deadzoneWidth = 0.05f)
    {
        lock (_lock)
        {
            for (int i = 0; i < DefaultPointCount; i++)
            {
                float t = (float)i / (DefaultPointCount - 1);
                _inputValues[i] = t;
                float absInput = Math.Abs(t - 0.5f) * 2f;
                if (absInput < deadzoneWidth)
                    _outputValues[i] = 0.5f;
                else
                    _outputValues[i] = t;
            }
        }
    }

    public float Apply(float inputForce)
    {
        float absForce = Math.Abs(inputForce);
        float sign = Math.Sign(inputForce);

        if (absForce < 0.0001f) return 0f;

        float result;
        lock (_lock)
        {
            result = Interpolate(absForce);
        }

        return sign * result;
    }

    private float Interpolate(float input)
    {
        float maxInput = _inputValues[^1];
        input = Math.Clamp(input, 0f, maxInput);

        int lower = 0;
        for (int i = 1; i < _inputValues.Length; i++)
        {
            if (_inputValues[i] >= input)
            {
                lower = i - 1;
                break;
            }
            if (i == _inputValues.Length - 1)
                lower = i - 1;
        }

        int upper = Math.Min(lower + 1, _inputValues.Length - 1);

        float inputRange = _inputValues[upper] - _inputValues[lower];
        if (inputRange < 0.0001f)
            return _outputValues[lower];

        float t = (input - _inputValues[lower]) / inputRange;
        return _outputValues[lower] + t * (_outputValues[upper] - _outputValues[lower]);
    }
}
