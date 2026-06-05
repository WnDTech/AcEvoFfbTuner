using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using AcEvoFfbTuner.Core.FfbProcessing;
using AcEvoFfbTuner.Core.SharedMemory.Structs;
using AcEvoFfbTuner.ViewModels;

namespace AcEvoFfbTuner.Views;

public partial class RaceInfoOverlay : Window
{
    private bool _isTransparent;
    private bool _isCompact;
    private int _updateCount;
    private readonly RaceInfoViewModel _vm = new();

    private static readonly SolidColorBrush _green = new(Color.FromRgb(0x00, 0xE6, 0x76));
    private static readonly SolidColorBrush _yellow = new(Color.FromRgb(0xFF, 0xD6, 0x00));
    private static readonly SolidColorBrush _red = new(Color.FromRgb(0xE5, 0x39, 0x35));
    private static readonly SolidColorBrush _blue = new(Color.FromRgb(0x4F, 0xC3, 0xF7));
    private static readonly SolidColorBrush _orange = new(Color.FromRgb(0xF0, 0x88, 0x3E));
    private static readonly SolidColorBrush _white = new(Color.FromRgb(0xE6, 0xED, 0xF3));
    private static readonly SolidColorBrush _gray = new(Color.FromRgb(0x8B, 0x94, 0x9E));

    public RaceInfoOverlay()
    {
        InitializeComponent();
    }

    public void UpdateData(RaceInfoOutput processed, SPageFilePhysicsEvo physics, SPageFileGraphicEvo graphics)
    {
        _vm.UpdateFromRaceInfoOutput(processed, physics);
        UpdateUi();
    }

    private void UpdateUi()
    {
        _updateCount++;
        HeaderText.Text = $"RACE INFO [{_updateCount}]";
        UpdateGaps();
        UpdateFuel();
        UpdateTyres();
        UpdateDamage();
        UpdateSessionBar();
    }

    private void UpdateGaps()
    {
        GapAheadText.Text = $"{_vm.GapAhead:F1}s";
        GapTrendAhead.Text = _vm.GapTrendAhead;
        GapTrendAhead.Foreground = GetTrendColor(_vm.GapTrendAhead);

        GapBehindText.Text = $"{_vm.GapBehind:F1}s";
        GapTrendBehind.Text = _vm.GapTrendBehind;
        GapTrendBehind.Foreground = GetTrendColor(_vm.GapTrendBehind);

        PositionText.Text = $"P{_vm.Position} / {_vm.TotalDrivers}";
    }

    private void UpdateFuel()
    {
        FuelLevelText.Text = $"{_vm.FuelLevel:F1} L";
        FuelPerLapText.Text = $"{_vm.FuelPerLap:F2}";
        FuelLapsText.Text = $"{_vm.FuelLapsRemaining:F1}";
    }

    private void UpdateTyres()
    {
        UpdateTyreCell(TyreWearFL, TyreTempFL, _vm.TyreWearFL, _vm.TyreTempClassFL);
        UpdateTyreCell(TyreWearFR, TyreTempFR, _vm.TyreWearFR, _vm.TyreTempClassFR);
        UpdateTyreCell(TyreWearRL, TyreTempRL, _vm.TyreWearRL, _vm.TyreTempClassRL);
        UpdateTyreCell(TyreWearRR, TyreTempRR, _vm.TyreWearRR, _vm.TyreTempClassRR);

        TyreCompoundText.Text = string.IsNullOrEmpty(_vm.TyreCompound) ? "--" : _vm.TyreCompound;
    }

    private static void UpdateTyreCell(System.Windows.Controls.TextBlock wearBlock,
        System.Windows.Controls.TextBlock tempBlock, float wear, TyreTempClass tempClass)
    {
        wearBlock.Text = $"{wear * 100f:F0}%";
        wearBlock.Foreground = wear > 0.8f ? _red : wear > 0.5f ? _yellow : _green;

        tempBlock.Text = RaceInfoViewModel.GetTempClassString(tempClass);
        tempBlock.Foreground = GetTempColor(tempClass);
    }

    private void UpdateDamage()
    {
        var dmg = _vm.DamageSummary;
        DamageSummaryText.Text = RaceInfoViewModel.GetDamageString(dmg);
        DamageSummaryText.Foreground = GetDamageColor(dmg);

        if (_vm.RaceCutGainedTimeMs > 0)
        {
            PenaltyText.Text = $"PENALTY: {_vm.RaceCutGainedTimeMs}ms";
            PenaltyText.Foreground = _yellow;
        }
        else if (_vm.IsWrongWay)
        {
            PenaltyText.Text = "WRONG WAY";
            PenaltyText.Foreground = _red;
        }
        else
        {
            PenaltyText.Text = "";
        }
    }

    private void UpdateSessionBar()
    {
        AcEvoFlagType flag;
        try
        {
            flag = (AcEvoFlagType)Enum.Parse(typeof(AcEvoFlagType), _vm.Flag);
        }
        catch
        {
            flag = AcEvoFlagType.AcGreenFlag;
        }
        FlagText.Text = RaceInfoViewModel.GetFlagDisplayName(flag);
        FlagText.Foreground = GetFlagColor(flag);

        LapText.Text = _vm.TotalLaps > 0
            ? $"Lap {_vm.CurrentLap} / {_vm.TotalLaps}"
            : $"Lap {_vm.CurrentLap}";

        AirTempText.Text = _vm.AirTemperature > 0 ? $"Air {_vm.AirTemperature:F0}°C" : "Air --°C";
        RoadTempText.Text = _vm.RoadTemperature > 0 ? $"Road {_vm.RoadTemperature:F0}°C" : "Road --°C";
    }

    private static SolidColorBrush GetTrendColor(string trend)
    {
        return trend switch
        {
            "\u25B2" => _green,
            "\u25BC" => _red,
            _ => _gray
        };
    }

    private static SolidColorBrush GetTempColor(TyreTempClass t)
    {
        return t switch
        {
            TyreTempClass.Cold => _blue,
            TyreTempClass.Ok => _green,
            TyreTempClass.Hot => _yellow,
            TyreTempClass.Peak => _orange,
            TyreTempClass.Overheating => _red,
            _ => _gray
        };
    }

    private static SolidColorBrush GetDamageColor(DamageLevel d)
    {
        return d switch
        {
            DamageLevel.None => _green,
            DamageLevel.Light => _yellow,
            DamageLevel.Moderate => _orange,
            DamageLevel.Heavy => _red,
            DamageLevel.Destroyed => new SolidColorBrush(Color.FromRgb(0xFF, 0x00, 0x00)),
            _ => _gray
        };
    }

    private static SolidColorBrush GetFlagColor(AcEvoFlagType flag)
    {
        return flag switch
        {
            AcEvoFlagType.AcNoFlag or AcEvoFlagType.AcGreenFlag => _green,
            AcEvoFlagType.AcYellowFlag => _yellow,
            AcEvoFlagType.AcBlueFlag or AcEvoFlagType.AcWhiteFlag => _blue,
            AcEvoFlagType.AcBlackFlag => _red,
            AcEvoFlagType.AcCheckeredFlag => _white,
            AcEvoFlagType.AcPenaltyFlag => _orange,
            AcEvoFlagType.AcOrangeFlag => _orange,
            _ => _gray
        };
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        var screen = SystemParameters.WorkArea;
        Left = screen.Right - Width - 20;
        Top = screen.Bottom - Height - 100;
    }

    private void OnDeactivated(object sender, EventArgs e)
    {
        Topmost = false;
        Topmost = true;
    }

    private void OnDragMove(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left)
        {
            if (e.ClickCount == 2)
            {
                OnToggleCompact();
                return;
            }
            try { DragMove(); } catch { }
        }
    }

    private void OnToggleCompact()
    {
        _isCompact = !_isCompact;
        HeaderBar.Visibility = _isCompact ? Visibility.Collapsed : Visibility.Visible;
        FooterBar.Visibility = _isCompact ? Visibility.Collapsed : Visibility.Visible;
        ApplyBorderStyle();
    }

    private void ToggleTransparency(object sender, RoutedEventArgs e)
    {
        _isTransparent = !_isTransparent;
        ApplyBorderStyle();
    }

    private void ApplyBorderStyle()
    {
        if (_isCompact)
        {
            ContentBorder.Background = new SolidColorBrush(Color.FromArgb(0x99, 0x0D, 0x0D, 0x0D));
            RootBorder.Background = new SolidColorBrush(Color.FromArgb(0x22, 0xE6, 0x7E, 0x22));
            if (TransparencyIcon != null) TransparencyIcon.Text = "MIN";
        }
        else if (_isTransparent)
        {
            ContentBorder.Background = new SolidColorBrush(Color.FromArgb(0x55, 0x0D, 0x0D, 0x0D));
            RootBorder.Background = new SolidColorBrush(Color.FromArgb(0x22, 0xE6, 0x7E, 0x22));
            if (TransparencyIcon != null) TransparencyIcon.Text = "TRN";
        }
        else
        {
            ContentBorder.Background = new SolidColorBrush(Color.FromArgb(0xDD, 0x0D, 0x0D, 0x0D));
            RootBorder.Background = new SolidColorBrush(Color.FromArgb(0x55, 0xE6, 0x7E, 0x22));
            if (TransparencyIcon != null) TransparencyIcon.Text = "OPQ";
        }
    }

    private void OnMouseWheel(object sender, MouseWheelEventArgs e)
    {
        double delta = e.Delta > 0 ? -15 : 15;
        double newW = Width + delta;
        double newH = Height + delta;

        if (newW < 240 || newH < 260) return;
        if (newW > 500 || newH > 500) return;

        Left += delta / 2;
        Top += delta / 2;
        Width = newW;
        Height = newH;
    }

    private void CloseOverlay(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
