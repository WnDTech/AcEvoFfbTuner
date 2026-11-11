using AcEvoFfbTuner.Services;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace AcEvoFfbTuner.Controls;

/// <summary>
/// Old-school LED VU / volume indicator: 12 horizontal segments that light up
/// with |level| 0..1. Green → yellow → red, with a white peak-hold tick.
/// Pure code-behind rendering — no bindings needed, call <see cref="SetLevel"/> at UI rate.
/// </summary>
public partial class LedVuMeter : UserControl
{
    public const int SegmentCount = 12;

    private readonly Border[] _segments = new Border[SegmentCount];
    private float _peak;
    private bool _isActive = true;
    private bool _compact;

    /// <summary>
    /// Compact rendering for fixed-width flow cards: 11px segments (~154px total)
    /// instead of 16px (~225px). Default false preserves the full-size meter.
    /// </summary>
    public bool Compact
    {
        get => _compact;
        set
        {
            if (_compact == value) return;
            _compact = value;
            BuildSegments();
            Render(_lastLevel);
        }
    }

    private static Color GreenOn => ThemeManager.Color("#FF00E676");
    private static Color YellowOn => ThemeManager.Color("#FFFFD600");
    private static Color RedOn => ThemeManager.Color("#FFFF1744");
    private static Color GreenOff => ThemeManager.Color("#FF1B3A2A");
    private static Color YellowOff => ThemeManager.Color("#FF3A3413");
    private static Color RedOff => ThemeManager.Color("#FF3A1A1A");
    private static Color InactiveOff => ThemeManager.Color("#FF22262C");

    public LedVuMeter()
    {
        InitializeComponent();
        BuildSegments();
    }

    private void BuildSegments()
    {
        SegmentRow.Children.Clear();
        for (int i = 0; i < SegmentCount; i++)
        {
            var b = new Border
            {
                Width = _compact ? 11 : 16,
                Height = _compact ? 18 : 22,
                CornerRadius = new CornerRadius(2),
                Margin = new Thickness(0, 0, _compact ? 2 : 3, 0),
                Background = new SolidColorBrush(OffColor(i)),
                BorderThickness = new Thickness(1),
                BorderBrush = ThemeManager.Brush("#FF000000")
            };
            _segments[i] = b;
            SegmentRow.Children.Add(b);
        }
    }

    private static Color OnColor(int i) => i < 8 ? GreenOn : (i < 10 ? YellowOn : RedOn);
    private Color OffColor(int i) => _isActive ? (i < 8 ? GreenOff : (i < 10 ? YellowOff : RedOff)) : InactiveOff;

    /// <summary>Dim the whole meter when the stage is bypassed for the current game.</summary>
    public bool IsActive
    {
        get => _isActive;
        set
        {
            _isActive = value;
            if (!value) _peak = 0f;
            Render(_lastLevel);
        }
    }

    private float _lastLevel;

    /// <summary>Push a new signed force level; meter shows |level| clamped to 0..1.</summary>
    public void SetLevel(float signedLevel)
    {
        float mag = Math.Abs(signedLevel);
        if (float.IsNaN(mag) || float.IsInfinity(mag)) mag = 0f;
        mag = Math.Clamp(mag, 0f, 1f);
        _lastLevel = mag;

        // Peak-hold with slow decay (caller ticks ~15 Hz → ~0.5s fall time).
        if (mag >= _peak) _peak = mag;
        else _peak = Math.Max(mag, _peak - 0.03f);

        Render(mag);
    }

    public void ResetPeak() => _peak = 0f;

    private void Render(float mag)
    {
        int lit = (int)Math.Round(mag * SegmentCount);
        int peakIdx = (int)Math.Round(_peak * SegmentCount) - 1;
        for (int i = 0; i < SegmentCount; i++)
        {
            var b = _segments[i];
            var bg = (SolidColorBrush)b.Background;
            var border = (SolidColorBrush)b.BorderBrush;
            if (!_isActive)
            {
                bg.Color = InactiveOff;
                border.Color = ThemeManager.Color("#FF30363D");
            }
            else if (i < lit)
            {
                bg.Color = OnColor(i);
                border.Color = Colors.Black;
            }
            else if (i == peakIdx && _peak > 0.02f)
            {
                bg.Color = OffColor(i);
                border.Color = Colors.White;
            }
            else
            {
                bg.Color = OffColor(i);
                border.Color = Colors.Black;
            }
        }
    }
}
