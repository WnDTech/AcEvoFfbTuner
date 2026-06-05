using System.Diagnostics;
using System.Speech.Synthesis;

namespace AcEvoFfbTuner.Services;

public sealed class VoiceService : IDisposable
{
    private readonly SpeechSynthesizer _synth;
    private bool _enabled = true;
    private int _volume = 100;

    public VoiceService()
    {
        _synth = new SpeechSynthesizer();
    }

    public bool Enabled
    {
        get => _enabled;
        set
        {
            _enabled = value;
            if (!value)
                _synth.SpeakAsyncCancelAll();
        }
    }

    public int Volume
    {
        get => _volume;
        set => _volume = Math.Clamp(value, 0, 100);
    }

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
        _synth.Volume = _volume;
        _synth.SpeakAsync(text);
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
        _synth.SpeakAsyncCancelAll();
    }

    public void Dispose()
    {
        _synth.SpeakAsyncCancelAll();
        _synth.Dispose();
    }
}
