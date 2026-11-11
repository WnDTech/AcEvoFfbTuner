using System.Diagnostics;

namespace AcEvoFfbTuner.Core.FfbProcessing;

/// <summary>
/// 10-band FFB equalizer (low shelf, 8 peaking bands, high shelf).
///
/// Threading: the UI thread only writes target gains / flags (single float/bool
/// writes, atomic). All filter coefficients and state are owned by the FFB thread
/// (Process / ProcessFinal), so a half-written coefficient set can never be run.
///
/// Adaptive rate: the equalizer measures how often its own pipeline calls
/// Process() (wall clock) and redesigns its filters for that rate. Each sim
/// runs at a different rate (EVO/AC ~333 Hz, R3E ~400 Hz, LMU varies), so
/// a band is only active when its centre is safely below Nyquist for the rate
/// that game actually delivers. Unsupported bands fade to 0 dB and the UI greys
/// them out. No game-specific code: every pipeline gets this behaviour by
/// calling Process().
///
/// Click-free: gain changes ramp (dB/s), filter state is preserved across
/// coefficient changes, and while bypassed the filter state is primed with the
/// signal so engaging a band never starts from a zeroed history.
/// </summary>
public sealed class FfbEqualizer
{
    public const int BandCount = 10;
    public const float MinGainDb = -12f;
    public const float MaxGainDb = 12f;

    /// <summary>A band is usable when its centre is ≤ this fraction of the sample rate (0.5 = Nyquist).</summary>
    public const float MaxUsableFraction = 0.42f;

    private const float RampDbPerSecond = 48f;
    private const float RedesignThresholdDb = 0.02f;
    private const float ActiveThresholdDb = 0.005f;
    private const float MinSampleRate = 30f;
    private const float MaxSampleRate = 2000f;
    private const float RateChangeFraction = 0.03f;
    private const float LimiterKnee = 0.8f;
    private const float LimiterCeiling = 1.0f;

    public static readonly (string Name, float CenterHz, string Description)[] BandInfo = new (string, float, string)[]
    {
        ("Steer Weight", 2f, "Very slow force content: overall steering weight (whole-force mode)."),
        ("Weight Transfer", 4f, "Body roll, brake dive, accel squat, LFE rumble."),
        ("Self Align", 8f, "Self-aligning torque build-up and grip balance (steering force mode)."),
        ("Steering Precision", 12f, "Small corrections, line tracking."),
        ("Turn-In Response", 18f, "Initial bite, direction changes, rear-slip rumble."),
        ("Road Texture", 25f, "Tarmac grain, scrub, surface roughness."),
        ("Bump Detail", 35f, "Patches, seams, road features."),
        ("Kerb Strike", 50f, "Kerb hits, potholes, sharp impacts."),
        ("Wheel Chatter", 70f, "Rapid vibration, wheel buzz."),
        ("ABS/TC Pulses", 100f, "ABS/TC pulsing, finest vibration.")
    };

    private static readonly float[] BandQ = new float[BandCount]
    {
        0.7f, 0.7f, 1.2f, 1.4f, 1.6f, 1.8f, 2.0f, 2.0f, 2.2f, 0.7f
    };

    // ── UI-written state (single-word writes only) ──
    private readonly float[] _targetDb = new float[BandCount];
    private volatile bool _masterEnabled;
    private volatile bool _applyToCore = true;
    private volatile bool _autoSampleRate = true;
    private volatile float _requestedRate = 333f;
    private volatile bool _rateRequested = true;
    private volatile bool _snapRequested;
    private volatile bool _resetRequested;

    // ── FFB-thread state ──
    private readonly Bank _detail = new();
    private readonly Bank _core = new();
    private float _sampleRate = 333f;
    private float _dtAvg = 1f / 333f;
    private long _lastTicks;
    private int _rateCheckCounter;
    private volatile float _publishedRate = 333f;

    public FfbEqualizer()
    {
        _detail.Design(_sampleRate, force: true);
        _core.Design(_sampleRate, force: true);
    }

    public bool MasterEnabled { get => _masterEnabled; set => _masterEnabled = value; }

    /// <summary>
    /// true (default) = EQ shapes the WHOLE force sent to the wheel (steering + effects,
    /// summed just before the output clipper). In sims like R3E/LMU nearly all feel lives
    /// in the game's own steering torque, so an effects-only EQ is barely perceptible.
    /// false = effects/detail path only (steering force untouched, zero added latency).
    /// Switching modes crossfades via the gain ramps.
    /// </summary>
    public bool ApplyToCore { get => _applyToCore; set => _applyToCore = value; }

    /// <summary>Measure the pipeline's real call rate and adapt. Disable for deterministic tests.</summary>
    public bool AutoSampleRate { get => _autoSampleRate; set => _autoSampleRate = value; }

    /// <summary>Sample rate the filters are currently designed for (Hz).</summary>
    public float SampleRate => _publishedRate;

    /// <summary>Highest band centre frequency the current rate can carry.</summary>
    public float MaxSupportedHz => _publishedRate * MaxUsableFraction;

    public bool IsBandSupported(int band) =>
        band >= 0 && band < BandCount && BandInfo[band].CenterHz <= MaxSupportedHz;

    public float GetBandGain(int band) =>
        band >= 0 && band < BandCount ? _targetDb[band] : 0f;

    public void SetBandGain(int band, float gainDb)
    {
        if (band < 0 || band >= BandCount) return;
        if (float.IsNaN(gainDb)) gainDb = 0f;
        _targetDb[band] = Math.Clamp(gainDb, MinGainDb, MaxGainDb);
    }

    /// <summary>Seed / force the sample rate (Hz). With AutoSampleRate on, measurement refines it.</summary>
    public void SetSampleRate(float sampleRate)
    {
        _requestedRate = Math.Clamp(sampleRate, MinSampleRate, MaxSampleRate);
        _rateRequested = true;
    }

    /// <summary>Jump straight to the target gains on the next sample (no ramp) — for profile loads.</summary>
    public void SnapToTargets() => _snapRequested = true;

    public void Reset() => _resetRequested = true;

    public float Process(float input)
    {
        ApplyPendingRequests(input);
        if (_autoSampleRate) MeasureRate();

        bool on = _masterEnabled && !_applyToCore;
        float y = _detail.Run(input, _targetDb, on, _sampleRate, out bool active);
        if (active) y = SoftLimit(y);
        TrackDiag(ref _diagFxIn, ref _diagFxDelta, input, y);
        return y;
    }

    /// <summary>
    /// Whole-force EQ, called once per tick on (steering + effects) right before the
    /// output clipper. Exact pass-through unless MasterEnabled and ApplyToCore.
    /// The pipeline's own clipper handles over-range, so no extra limiter here.
    /// </summary>
    public float ProcessFinal(float input)
    {
        ApplyPendingRequests(input);
        bool on = _masterEnabled && _applyToCore;
        float y = _core.Run(input, _targetDb, on, _sampleRate, out _);
        TrackDiag(ref _diagFinalIn, ref _diagFinalDelta, input, y);
        _analyzer.Feed(input, _sampleRate);
        return y;
    }

    // ── Spectrum analyzer: where the whole force's energy actually is ──
    private readonly SpectrumAnalyzer _analyzer = new();

    /// <summary>
    /// Per-band RMS of the force entering the whole-force EQ (measured in every mode).
    /// Fast = ~1 s window (live UI), Slow = ~10 s window (snapshots). Total = broadband RMS
    /// over the same windows. Bands the current rate cannot carry read 0.
    /// </summary>
    public (float[] Fast, float[] Slow, float TotalFast, float TotalSlow) GetSpectrum() => _analyzer.Read();

    private sealed class SpectrumAnalyzer
    {
        private readonly BiquadFilter[] _bp = new BiquadFilter[BandCount];
        private readonly float[] _fastMs = new float[BandCount];
        private readonly float[] _slowMs = new float[BandCount];
        private float _totFast, _totSlow, _designedRate = -1f;

        public SpectrumAnalyzer() { for (int i = 0; i < BandCount; i++) _bp[i] = new BiquadFilter(); }

        public void Feed(float x, float rate)
        {
            if (rate != _designedRate) Design(rate);
            float af = 1f / rate;          // ~1 s
            float aS = 1f / (rate * 10f);  // ~10 s
            _totFast += (x * x - _totFast) * af;
            _totSlow += (x * x - _totSlow) * aS;
            for (int i = 0; i < BandCount; i++)
            {
                float y = Supported(i, rate) ? _bp[i].Process(x) : 0f;
                float e = y * y;
                _fastMs[i] += (e - _fastMs[i]) * af;
                _slowMs[i] += (e - _slowMs[i]) * aS;
            }
        }

        private void Design(float rate)
        {
            _designedRate = rate;
            for (int i = 0; i < BandCount; i++)
            {
                float hz = BandInfo[i].CenterHz;
                if (!Supported(i, rate)) { _bp[i].SetIdentity(); continue; }
                if (i == 0) _bp[i].SetLowpassCoeffs(hz * 1.4f, 0.707f, rate);   // everything below ~3 Hz
                else if (i == BandCount - 1) _bp[i].SetHighpassCoeffs(hz * 0.8f, 0.707f, rate);
                // Analysis Q is narrower than the EQ's own wide shaping Q so a band's
                // meter isn't polluted by its neighbours (Q 0.7 at 4 Hz leaked 22% of a 25 Hz tone).
                else _bp[i].SetBandpassCoeffs(hz, 2.0f, rate);
            }
        }

        public (float[] Fast, float[] Slow, float TotalFast, float TotalSlow) Read()
        {
            var f = new float[BandCount];
            var s = new float[BandCount];
            for (int i = 0; i < BandCount; i++)
            {
                f[i] = MathF.Sqrt(Math.Max(0f, _fastMs[i]));
                s[i] = MathF.Sqrt(Math.Max(0f, _slowMs[i]));
            }
            return (f, s, MathF.Sqrt(Math.Max(0f, _totFast)), MathF.Sqrt(Math.Max(0f, _totSlow)));
        }
    }

    // ── Diagnostics (snapshot proof that the EQ is changing the signal) ──
    // Exponential mean-square over ~10 s of ticks. Written on the FFB thread,
    // read for snapshots only.
    private float _diagFinalIn, _diagFinalDelta, _diagFxIn, _diagFxDelta;

    private static void TrackDiag(ref float inMs, ref float deltaMs, float x, float y)
    {
        const float a = 1f / 4000f;
        inMs += (x * x - inMs) * a;
        float d = y - x;
        deltaMs += (d * d - deltaMs) * a;
    }

    /// <summary>RMS of the signal entering each EQ stage and of the change the EQ made to it.</summary>
    public (float FinalInRms, float FinalDeltaRms, float EffectsInRms, float EffectsDeltaRms) GetDiagnostics() =>
        (MathF.Sqrt(_diagFinalIn), MathF.Sqrt(_diagFinalDelta), MathF.Sqrt(_diagFxIn), MathF.Sqrt(_diagFxDelta));

    private void ApplyPendingRequests(float input)
    {
        if (_rateRequested)
        {
            _rateRequested = false;
            float r = _requestedRate;
            _dtAvg = 1f / r;
            SetRate(r);
        }
        if (_resetRequested)
        {
            _resetRequested = false;
            _detail.Prime(input);
            _core.Prime(input);
            _snapRequested = true;
            _lastTicks = 0;
        }
        if (_snapRequested)
        {
            _snapRequested = false;
            _detail.Snap(_targetDb, _masterEnabled && !_applyToCore, _sampleRate);
            _core.Snap(_targetDb, _masterEnabled && _applyToCore, _sampleRate);
        }
    }

    private void MeasureRate()
    {
        long now = Stopwatch.GetTimestamp();
        long last = _lastTicks;
        _lastTicks = now;
        if (last == 0) return;

        float dt = (float)(now - last) / Stopwatch.Frequency;
        // Ignore pauses / stalls and sub-0.5 ms double calls.
        if (dt < 0.0005f || dt > 0.05f) return;
        _dtAvg += (dt - _dtAvg) * 0.01f;

        if (++_rateCheckCounter < 64) return;
        _rateCheckCounter = 0;
        float measured = Math.Clamp(1f / _dtAvg, MinSampleRate, MaxSampleRate);
        if (Math.Abs(measured - _sampleRate) > _sampleRate * RateChangeFraction)
            SetRate(measured);
    }

    private void SetRate(float rate)
    {
        _sampleRate = rate;
        _publishedRate = rate;
        _detail.Design(rate, force: true);
        _core.Design(rate, force: true);
    }

    private static float SoftLimit(float y)
    {
        float a = Math.Abs(y);
        if (a <= LimiterKnee) return y;
        float range = LimiterCeiling - LimiterKnee;
        return Math.Sign(y) * (LimiterKnee + range * MathF.Tanh((a - LimiterKnee) / range));
    }

    private static bool Supported(int band, float rate) =>
        BandInfo[band].CenterHz <= rate * MaxUsableFraction;

    /// <summary>One chain of 10 biquads with its own ramped gains and state.</summary>
    private sealed class Bank
    {
        private readonly BiquadFilter[] _filters = new BiquadFilter[BandCount];
        private readonly float[] _currentDb = new float[BandCount];
        private readonly float[] _designedDb = new float[BandCount];
        private bool _bypassed = true;

        public Bank()
        {
            for (int i = 0; i < BandCount; i++) _filters[i] = new BiquadFilter();
        }

        private static float Effective(float[] target, int i, bool on, float rate) =>
            on && Supported(i, rate) ? target[i] : 0f;

        public float Run(float input, float[] target, bool on, float rate, out bool active)
        {
            float step = RampDbPerSecond / rate;
            bool anyActive = false;
            bool needsWork = false;

            for (int i = 0; i < BandCount; i++)
            {
                float t = Effective(target, i, on, rate);
                float c = _currentDb[i];
                if (c != t)
                {
                    float d = t - c;
                    c = Math.Abs(d) <= step ? t : c + Math.Sign(d) * step;
                    _currentDb[i] = c;
                }
                if (Math.Abs(c) > ActiveThresholdDb) { anyActive = true; needsWork = true; }
                if (Math.Abs(t) > ActiveThresholdDb) needsWork = true;
            }

            if (!needsWork)
            {
                // Exact pass-through; keep the filter history tracking the signal so
                // the next engagement starts from steady state instead of zero.
                if (!_bypassed) _bypassed = true;
                for (int i = 0; i < BandCount; i++) _filters[i].Prime(input);
                active = false;
                return input;
            }
            _bypassed = false;

            for (int i = 0; i < BandCount; i++)
            {
                if (Math.Abs(_currentDb[i] - _designedDb[i]) > RedesignThresholdDb)
                    DesignBand(i, rate);
            }

            float y = input;
            for (int i = 0; i < BandCount; i++)
                y = _filters[i].Process(y);

            active = anyActive;
            return y;
        }

        public void Snap(float[] target, bool on, float rate)
        {
            for (int i = 0; i < BandCount; i++)
                _currentDb[i] = Effective(target, i, on, rate);
            Design(rate, force: true);
        }

        public void Prime(float x)
        {
            for (int i = 0; i < BandCount; i++) _filters[i].Prime(x);
        }

        public void Design(float rate, bool force)
        {
            for (int i = 0; i < BandCount; i++)
                if (force || Math.Abs(_currentDb[i] - _designedDb[i]) > RedesignThresholdDb)
                    DesignBand(i, rate);
        }

        private void DesignBand(int band, float rate)
        {
            float g = _currentDb[band];
            _designedDb[band] = g;
            float hz = BandInfo[band].CenterHz;

            if (!Supported(band, rate) || Math.Abs(g) <= ActiveThresholdDb)
            {
                _filters[band].SetIdentity();
                return;
            }

            if (band == 0)
                _filters[band].SetLowShelfCoeffs(hz, g, 0.7f, rate);
            else if (band == BandCount - 1)
                _filters[band].SetHighShelfCoeffs(hz, g, 0.7f, rate);
            else
                _filters[band].SetPeakingCoeffs(hz, g, BandQ[band], rate);
        }
    }

    private sealed class BiquadFilter
    {
        private float _b0 = 1f, _b1, _b2, _a1, _a2;
        private float _x1, _x2, _y1, _y2;

        public void SetIdentity() => SetCoeffs(1f, 0f, 0f, 0f, 0f);

        public void SetBandpassCoeffs(float freqHz, float q, float sampleRate)
        {
            float w0 = 2f * MathF.PI * freqHz / sampleRate;
            float alpha = MathF.Sin(w0) / (2f * q);
            float a0 = 1f + alpha;
            SetCoeffs(alpha / a0, 0f, -alpha / a0, -2f * MathF.Cos(w0) / a0, (1f - alpha) / a0);
        }

        public void SetLowpassCoeffs(float freqHz, float q, float sampleRate)
        {
            float w0 = 2f * MathF.PI * freqHz / sampleRate;
            float c = MathF.Cos(w0), alpha = MathF.Sin(w0) / (2f * q), a0 = 1f + alpha;
            SetCoeffs((1f - c) / 2f / a0, (1f - c) / a0, (1f - c) / 2f / a0, -2f * c / a0, (1f - alpha) / a0);
        }

        public void SetHighpassCoeffs(float freqHz, float q, float sampleRate)
        {
            float w0 = 2f * MathF.PI * freqHz / sampleRate;
            float c = MathF.Cos(w0), alpha = MathF.Sin(w0) / (2f * q), a0 = 1f + alpha;
            SetCoeffs((1f + c) / 2f / a0, -(1f + c) / a0, (1f + c) / 2f / a0, -2f * c / a0, (1f - alpha) / a0);
        }

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

        // Coefficients change WITHOUT clearing history (Direct Form I tolerates
        // small coefficient steps; the gain ramp keeps every step small).
        private void SetCoeffs(float b0, float b1, float b2, float a1, float a2)
        {
            _b0 = b0; _b1 = b1; _b2 = b2; _a1 = a1; _a2 = a2;
        }

        public void Prime(float x)
        {
            _x1 = _x2 = _y1 = _y2 = x;
        }

        public float Process(float x)
        {
            float y = _b0 * x + _b1 * _x1 + _b2 * _x2 - _a1 * _y1 - _a2 * _y2;
            if (!float.IsFinite(y)) y = x; // never pass NaN/Inf to the wheel
            _x2 = _x1; _x1 = x;
            _y2 = _y1; _y1 = y;
            return y;
        }
    }
}
