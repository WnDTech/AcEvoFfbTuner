using System.Diagnostics;
using System.IO;
using System.Speech.Synthesis;
using NAudio.Wave;

namespace AcEvoFfbTuner.Services;

public sealed class VoiceService : IDisposable
{
    private readonly SpeechSynthesizer _synth;
    private bool _enabled = true;
    private int _volume = 100;
    private WaveOutEvent? _activePlayer;
    private bool _disposed;

    private readonly object _queueLock = new();
    private readonly Queue<string> _queue = new();
    private bool _isPlaying;
    private string? _currentPlayingPath;

    private static readonly string CacheDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "AcEvoFfbTuner", "voice-cache");

    private static readonly Dictionary<string, string> PhraseFiles = new()
    {
        { "wheelbase connected", "wheelbase-connected.wav" },
        { "wheelbase disconnected", "wheelbase-disconnected.wav" },
        { "game connected", "game-connected.wav" },
        { "game disconnected", "game-disconnected.wav" },
        { "snapshot saved", "snapshot-saved.wav" },
        { "telemetry started", "telemetry-started.wav" },
        { "telemetry stopped", "telemetry-stopped.wav" },
        { "natural voices installed", "natural-voices-installed.wav" },

        { "setup wizard loaded. drive safely and follow the on screen instructions.", "setup-wizard-loaded.wav" },
        { "profile saved. setup complete.", "profile-saved.wav" },
        { "phase 1 of 3. drive straight and hold the wheel steady.", "phase-1-of-3.wav" },
        { "phase 2 of 3. drive through turns normally.", "phase-2-of-3.wav" },
        { "phase 3 of 3. fine tuning center response.", "phase-3-of-3.wav" },
        { "centering auto tune complete.", "centering-autotune-complete.wav" },
        { "core tyre forces tuned.", "core-tyre-forces-tuned.wav" },
        { "damping and friction tuning complete.", "damping-tuning-complete.wav" },
        { "force level calibrated.", "force-calibrated.wav" },
        { "vibration levels sampled.", "vibration-sampled.wav" },

        { "step 1. welcome & safety.", "step-1-welcome-safety.wav" },
        { "step 2. wheel centering.", "step-2-wheel-centering.wav" },
        { "step 3. core tyre forces.", "step-3-core-tyre-forces.wav" },
        { "step 4. master output gain.", "step-4-master-output-gain.wav" },
        { "step 5. damping & friction.", "step-5-damping-friction.wav" },
        { "step 6. curb & vibration.", "step-6-curb-vibration.wav" },
        { "step 7. review & confirm.", "step-7-review-confirm.wav" },
        { "step 8. save profile.", "step-8-save-profile.wav" },

        // Setup wizard voice-over prompts
        { "welcome. make sure your wheel is connected and you are in the car.", "wiz-welcome.wav" },
        { "wheel centering. drive straight at 15 plus kilometers per hour, then gently steer left or right to check the centering direction.", "wiz-wheel-centering.wav" },
        { "force strength calibration. drive through several corners at racing speed so i can measure your peak forces.", "wiz-force-strength.wav" },
        { "core tyre forces. drive through a few turns so i can sample your force levels.", "wiz-core-tyre.wav" },
        { "master output gain. this step is automatic.", "wiz-master-gain.wav" },
        { "damping and friction. this step is automatic.", "wiz-damping.wav" },
        { "curb and vibration. this step is automatic.", "wiz-vibration.wav" },
        { "review and confirm. drive a corner and adjust the sliders to your preference.", "wiz-review.wav" },
        { "save profile. give your profile a name and click save and finish.", "wiz-save-profile.wav" },
    };

    private static readonly string InstallPackDir = Path.Combine(
        AppDomain.CurrentDomain.BaseDirectory, "VoicePack");

    public VoiceService()
    {
        _synth = new SpeechSynthesizer();
        EnsureVoicePack();
    }

    private void EnsureVoicePack()
    {
        try
        {
            if (Directory.Exists(CacheDir) && IsCacheReady)
                return;

            if (Directory.Exists(InstallPackDir))
            {
                Directory.CreateDirectory(CacheDir);
                foreach (var file in Directory.GetFiles(InstallPackDir, "*.mp3"))
                {
                    var dest = Path.Combine(CacheDir, Path.GetFileName(file));
                    if (!File.Exists(dest))
                        File.Copy(file, dest);
                }
            }
        }
        catch { }

        // Generate any remaining cached voice files using TTS
        GenerateCachedVoices();
    }

    private void GenerateCachedVoices()
    {
        try
        {
            if (IsCacheReady) return;
            Directory.CreateDirectory(CacheDir);
            var generated = 0;
            foreach (var kvp in PhraseFiles)
            {
                var path = Path.Combine(CacheDir, kvp.Value);
                if (File.Exists(path)) continue;
                try
                {
                    using var synth = new SpeechSynthesizer();
                    synth.Volume = 100;
                    synth.SetOutputToWaveFile(path);
                    synth.Speak(kvp.Key);
                    generated++;
                    Log($"[Voice] Generated: {kvp.Value}");
                }
                catch (Exception ex)
                {
                    Log($"[Voice] Generation failed for '{kvp.Key}': {ex.Message}");
                }
            }
            Log($"[Voice] Generated {generated} voice files in cache");
        }
        catch (Exception ex)
        {
            Log($"[Voice] Cache generation error: {ex.Message}");
        }
    }

    public bool Enabled
    {
        get => _enabled;
        set
        {
            _enabled = value;
            if (!value)
            {
                StopPlayback();
                _synth.SpeakAsyncCancelAll();
            }
        }
    }

    public int Volume
    {
        get => _volume;
        set => _volume = Math.Clamp(value, 0, 100);
    }

    public static bool IsCacheReady
    {
        get
        {
            if (!Directory.Exists(CacheDir)) return false;
            return PhraseFiles.Values.All(f => File.Exists(Path.Combine(CacheDir, f)));
        }
    }

    public int CachedCount
    {
        get
        {
            if (!Directory.Exists(CacheDir)) return 0;
            return PhraseFiles.Values.Count(f => File.Exists(Path.Combine(CacheDir, f)));
        }
    }

    public int TotalPhrases => PhraseFiles.Count;

    public string ActiveEngine => IsCacheReady ? "Google TTS (cached)" : "Windows SAPI";

    public event Action<string>? LogMessage;

    public IReadOnlyList<string> AvailableVoices
    {
        get
        {
            try
            {
                return _synth.GetInstalledVoices()
                    .Where(v => v.Enabled)
                    .Select(v => v.VoiceInfo.Name)
                    .ToList();
            }
            catch
            {
                return Array.Empty<string>();
            }
        }
    }

    public string? SelectedVoice
    {
        get
        {
            try { return _synth.Voice?.Name; }
            catch { return null; }
        }
        set
        {
            if (string.IsNullOrEmpty(value)) return;
            try { _synth.SelectVoice(value); }
            catch { }
        }
    }

    public bool HasNaturalVoice
    {
        get
        {
            try
            {
                return _synth.GetInstalledVoices()
                    .Any(v => v.Enabled &&
                              v.VoiceInfo.Name.Contains("natural", StringComparison.OrdinalIgnoreCase));
            }
            catch
            {
                return false;
            }
        }
    }

    public static void OpenSpeechSettings()
    {
        try
        {
            Process.Start(new ProcessStartInfo("ms-settings:speech") { UseShellExecute = true });
        }
        catch { }
    }

    public void Speak(string text)
    {
        if (!_enabled || string.IsNullOrWhiteSpace(text)) return;

        var lower = text.ToLowerInvariant();
        if (PhraseFiles.TryGetValue(lower, out var filename))
        {
            var path = Path.Combine(CacheDir, filename);
            if (File.Exists(path))
            {
                EnqueuePlay(path);
                return;
            }
        }

        _synth.Volume = _volume;
        _synth.SpeakAsync(text);
    }

    private void EnqueuePlay(string path)
    {
        lock (_queueLock)
        {
            if (_queue.Count >= 5)
                return;
            _queue.Enqueue(path);
            if (!_isPlaying)
                DequeueAndPlay();
        }
    }

    private void DequeueAndPlay()
    {
        string? path;
        lock (_queueLock)
        {
            if (_queue.Count == 0)
            {
                _isPlaying = false;
                return;
            }
            path = _queue.Dequeue();
            _isPlaying = true;
            _currentPlayingPath = path;
        }

        StopPlayback();
        try
        {
            var reader = new MediaFoundationReader(path);
            _activePlayer = new WaveOutEvent { Volume = _volume / 100f };
            _activePlayer.PlaybackStopped += (_, _) =>
            {
                reader.Dispose();
                StopPlayback();
                DequeueAndPlay();
            };
            _activePlayer.Init(reader);
            _activePlayer.Play();
        }
        catch (Exception ex)
        {
            Log($"[Voice] Playback failed: {ex.Message}");
            StopPlayback();
            DequeueAndPlay();
        }
    }

    public void Speak(string format, params object?[] args)
    {
        if (!_enabled || args.Length == 0)
        {
            Speak(format);
            return;
        }
        Speak(string.Format(format, args));
    }

    public void CancelAll()
    {
        StopPlayback();
        _synth.SpeakAsyncCancelAll();
        lock (_queueLock)
        {
            _queue.Clear();
            _isPlaying = false;
            _currentPlayingPath = null;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        StopPlayback();
        _synth.SpeakAsyncCancelAll();
        _synth.Dispose();
    }

    private void Log(string msg) => LogMessage?.Invoke(msg);

    private void StopPlayback()
    {
        try
        {
            _activePlayer?.Stop();
            _activePlayer?.Dispose();
            _activePlayer = null;
        }
        catch { }
    }


}
