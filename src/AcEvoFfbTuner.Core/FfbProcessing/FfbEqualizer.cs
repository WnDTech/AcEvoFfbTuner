namespace AcEvoFfbTuner.Core.FfbProcessing;

public sealed class FfbEqualizer
{
    public const int BandCount = 10;
    private const float NyquistFractionMin = 0.01f;

    public static readonly (string Name, float CenterHz, string Description)[] BandInfo = new (string, float, string)[]
    {
        ("Sub", 2f, "Very slow force drift, static weight bias."),
        ("Weight", 4f, "Weight transfer, heave, steady-state load."),
        ("Align", 8f, "Self-aligning torque, chassis balance."),
        ("Detail", 12f, "Steering precision, small corrections."),
        ("Response", 18f, "Transient response, initial turn-in feel."),
        ("Texture", 25f, "Road surface texture, fine grain."),
        ("Road", 35f, "Bumps, patches, surface irregularities."),
        ("Impact", 50f, "Kerb strikes, sharp bumps, potholes."),
        ("Chatter", 70f, "High-frequency chatter, wheel buzz."),
        ("Buzz", 100f, "ABS pulses, slip vibration, finest detail.")
    };

    private static readonly float[] BandQ = new float[BandCount]
    {
        0.7f, 0.7f, 1.2f, 1.4f, 1.6f, 1.8f, 2.0f, 2.0f, 2.2f, 0.7f
    };

    private readonly BiquadFilter[] _filters = new BiquadFilter[BandCount];
    private readonly float[] _gains = new float[BandCount];
    private float _sampleRate = 333f;

    public bool MasterEnabled { get; set; }

    public FfbEqualizer()
    {
        for (int i = 0; i < BandCount; i++)
        {
        _gains[i] = 0f;
        _filters[i] = new BiquadFilter();
        }
        RecalculateAll();
    }

    public float GetBandGain(int band) =>
        band >= 0 && band < BandCount ? _gains[band] : 0f;

    public void SetBandGain(int band, float gainDb)
    {
        if (band < 0 || band >= BandCount) return;
        _gains[band] = Math.Clamp(gainDb, -12f, 12f);
        RecalculateFilter(band);
    }

    public void SetSampleRate(float sampleRate)
    {
        _sampleRate = Math.Max(sampleRate, 100f);
        RecalculateAll();
    }

        public float Process(float input)
        {
            if (!MasterEnabled) return input;

            float output = input;
            for (int i = 0; i < BandCount; i++)
            {
                if (Math.Abs(_gains[i]) > 0.01f)
                    output = _filters[i].Process(output);
            }
            return output;
        }

    public void Reset()
    {
        for (int i = 0; i < BandCount; i++)
            _filters[i].Reset();
    }

    private void RecalculateFilter(int band)
    {
        if (band < 0 || band >= BandCount) return;

        float gainDb = _gains[band];
        float centerHz = BandInfo[band].CenterHz;
        float sr = _sampleRate;

        if (band == 0)
            _filters[band].SetLowShelfCoeffs(centerHz, gainDb, 0.7f, sr);
        else if (band == BandCount - 1)
            _filters[band].SetHighShelfCoeffs(centerHz, gainDb, 0.7f, sr);
        else
            _filters[band].SetPeakingCoeffs(centerHz, gainDb, BandQ[band], sr);
    }

    private void RecalculateAll()
    {
        for (int i = 0; i < BandCount; i++)
            RecalculateFilter(i);
    }

    private sealed class BiquadFilter
    {
        private float _b0, _b1, _b2, _a1, _a2;
        private float _x1, _x2, _y1, _y2;

        public void SetLowShelfCoeffs(float freqHz, float gainDb, float shelfSlope, float sampleRate)
        {
            float w0 = 2f * MathF.PI * freqHz / sampleRate;
            float A = MathF.Pow(10f, gainDb / 40f);
            float cosW0 = MathF.Cos(w0);
            float sinW0 = MathF.Sin(w0);
            float alpha = sinW0 / 2f * MathF.Sqrt((A + 1f / A) * (1f / shelfSlope - 1f) + 2f);

            float twoSqrtAAlpha = 2f * MathF.Sqrt(A) * alpha;
            float Ap1 = A + 1f;
            float Am1 = A - 1f;

            float b0 = A * (Ap1 - Am1 * cosW0 + twoSqrtAAlpha);
            float b1 = 2f * A * (Am1 - Ap1 * cosW0);
            float b2 = A * (Ap1 - Am1 * cosW0 - twoSqrtAAlpha);
            float a0 = Ap1 + Am1 * cosW0 + twoSqrtAAlpha;
            float a1 = -2f * (Am1 + Ap1 * cosW0);
            float a2 = Ap1 + Am1 * cosW0 - twoSqrtAAlpha;

            SetCoeffs(b0 / a0, b1 / a0, b2 / a0, a1 / a0, a2 / a0);
        }

        public void SetHighShelfCoeffs(float freqHz, float gainDb, float shelfSlope, float sampleRate)
        {
            float w0 = 2f * MathF.PI * freqHz / sampleRate;
            float A = MathF.Pow(10f, gainDb / 40f);
            float cosW0 = MathF.Cos(w0);
            float sinW0 = MathF.Sin(w0);
            float alpha = sinW0 / 2f * MathF.Sqrt((A + 1f / A) * (1f / shelfSlope - 1f) + 2f);

            float twoSqrtAAlpha = 2f * MathF.Sqrt(A) * alpha;
            float Ap1 = A + 1f;
            float Am1 = A - 1f;

            float b0 = A * (Ap1 + Am1 * cosW0 + twoSqrtAAlpha);
            float b1 = -2f * A * (Am1 + Ap1 * cosW0);
            float b2 = A * (Ap1 + Am1 * cosW0 - twoSqrtAAlpha);
            float a0 = Ap1 - Am1 * cosW0 + twoSqrtAAlpha;
            float a1 = 2f * (Am1 - Ap1 * cosW0);
            float a2 = Ap1 - Am1 * cosW0 - twoSqrtAAlpha;

            SetCoeffs(b0 / a0, b1 / a0, b2 / a0, a1 / a0, a2 / a0);
        }

        public void SetPeakingCoeffs(float freqHz, float gainDb, float q, float sampleRate)
        {
            float w0 = 2f * MathF.PI * freqHz / sampleRate;
            float A = MathF.Pow(10f, gainDb / 40f);
            float cosW0 = MathF.Cos(w0);
            float sinW0 = MathF.Sin(w0);
            float alpha = sinW0 / (2f * q);

            float b0 = 1f + alpha * A;
            float b1 = -2f * cosW0;
            float b2 = 1f - alpha * A;
            float a0 = 1f + alpha / A;
            float a1 = -2f * cosW0;
            float a2 = 1f - alpha / A;

            SetCoeffs(b0 / a0, b1 / a0, b2 / a0, a1 / a0, a2 / a0);
        }

        private void SetCoeffs(float b0, float b1, float b2, float a1, float a2)
        {
            _b0 = b0; _b1 = b1; _b2 = b2; _a1 = a1; _a2 = a2;
            _x1 = _x2 = _y1 = _y2 = 0f;
        }

        public float Process(float x)
        {
            float y = _b0 * x + _b1 * _x1 + _b2 * _x2 - _a1 * _y1 - _a2 * _y2;
            _x2 = _x1; _x1 = x;
            _y2 = _y1; _y1 = y;
            return y;
        }

        public void Reset()
        {
            _x1 = _x2 = _y1 = _y2 = 0f;
        }
    }
}
