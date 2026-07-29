using System.Diagnostics;
using System.Runtime.InteropServices;

namespace AcEvoFfbTuner.Core.PedalInput.Sources;

public sealed class KeyboardPedalSource : IPedalInputSource
{
    public const int VK_W = 0x57;
    public const int VK_S = 0x53;
    public const int VK_A = 0x41;
    public const int VK_D = 0x44;
    public const int VK_SPACE = 0x20;

    private static readonly TimeSpan GasRampUp = TimeSpan.FromMilliseconds(800);
    private static readonly TimeSpan GasRampDown = TimeSpan.FromMilliseconds(300);
    private static readonly TimeSpan BrakeRampUp = TimeSpan.FromMilliseconds(400);
    private static readonly TimeSpan BrakeRampDown = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan ClutchRampUp = TimeSpan.FromMilliseconds(50);
    private static readonly TimeSpan ClutchRampDown = TimeSpan.FromMilliseconds(50);

    private readonly long _frequency = Stopwatch.Frequency;
    private long _gasPressedTicks;
    private long _gasReleasedTicks;
    private long _brakePressedTicks;
    private long _brakeReleasedTicks;
    private long _clutchPressedTicks;
    private long _clutchReleasedTicks;

    private bool _gasWasDown;
    private bool _brakeWasDown;
    private bool _clutchWasDown;
    private float _gasValue;
    private float _brakeValue;
    private float _clutchValue;

    private bool _enabled = true;

    public SourceType SourceType => SourceType.Keyboard;
    public string DeviceName => "Keyboard Simulator (W/A/S/D)";
    public bool IsAvailable => _enabled;

    public bool Enabled
    {
        get => _enabled;
        set => _enabled = value;
    }

    public bool TryReadRaw(out RawPedalState state)
    {
        state = default;
        if (!_enabled) return false;

        var now = Stopwatch.GetTimestamp();
        UpdateAxis(now, VK_W, ref _gasWasDown, ref _gasPressedTicks, ref _gasReleasedTicks, ref _gasValue, GasRampUp, GasRampDown);
        UpdateAxis(now, VK_S, ref _brakeWasDown, ref _brakePressedTicks, ref _brakeReleasedTicks, ref _brakeValue, BrakeRampUp, BrakeRampDown);
        UpdateClutch(now);

        state = new RawPedalState
        {
            GasRaw = _gasValue,
            BrakeRaw = _brakeValue,
            ClutchRaw = _clutchValue,
            Source = SourceType.Keyboard,
            TimestampTicks = now
        };
        return true;
    }

    private void UpdateAxis(long now, int vkKey, ref bool wasDown, ref long pressedTicks, ref long releasedTicks, ref float value, TimeSpan rampUp, TimeSpan rampDown)
    {
        bool isDown = (GetAsyncKeyState(vkKey) & 0x8000) != 0;

        if (isDown && !wasDown)
        {
            pressedTicks = now;
            wasDown = true;
        }
        else if (!isDown && wasDown)
        {
            releasedTicks = now;
            wasDown = false;
        }

        if (isDown)
        {
            double elapsedSec = (double)(now - pressedTicks) / _frequency;
            double target = Math.Clamp(elapsedSec / rampUp.TotalSeconds, 0.0, 1.0);
            value = Math.Max(value, (float)target);
        }
        else if (value > 0f)
        {
            double elapsedSec = (double)(now - releasedTicks) / _frequency;
            double decay = Math.Clamp(1.0 - elapsedSec / rampDown.TotalSeconds, 0.0, 1.0);
            value = (float)decay;
        }
    }

    private void UpdateClutch(long now)
    {
        bool isDown = (GetAsyncKeyState(VK_A) & 0x8000) != 0;

        if (isDown && !_clutchWasDown)
        {
            _clutchPressedTicks = now;
            _clutchWasDown = true;
        }
        else if (!isDown && _clutchWasDown)
        {
            _clutchReleasedTicks = now;
            _clutchWasDown = false;
        }

        if (isDown)
        {
            double elapsedSec = (double)(now - _clutchPressedTicks) / _frequency;
            double target = Math.Clamp(elapsedSec / ClutchRampUp.TotalSeconds, 0.0, 1.0);
            _clutchValue = Math.Max(_clutchValue, (float)target);
        }
        else if (_clutchValue > 0f)
        {
            double elapsedSec = (double)(now - _clutchReleasedTicks) / _frequency;
            double decay = Math.Clamp(1.0 - elapsedSec / ClutchRampDown.TotalSeconds, 0.0, 1.0);
            _clutchValue = (float)decay;
        }
    }

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);
}
