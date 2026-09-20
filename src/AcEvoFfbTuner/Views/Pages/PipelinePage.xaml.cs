using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using AcEvoFfbTuner.Controls;
using AcEvoFfbTuner.ViewModels;

namespace AcEvoFfbTuner.Views.Pages;

public sealed partial class PipelinePage : UserControl
{
    private readonly DispatcherTimer _timer;
    private int _selectedStage = -1;

    private record StageInfo(string Num, string Short, string Title, string Field, string Desc, string Lane,
        Func<Core.FfbProcessing.Models.FfbProcessedData, float> Value,
        Func<SupportedGame, bool> Active, Func<SupportedGame, string?> Note);

    private readonly StageInfo[] _stages;

    public PipelinePage()
    {
        InitializeComponent();

        _stages = new StageInfo[]
        {
            new("01","INPUT",   "GAME INPUT",              "FfbRawData.FinalFf + Mz/Fx/Fy",
                "Raw physics from shared memory. Per-wheel tyre forces (EVO/AC/ACC) or steering-column % (R3E/LMU).",  "HEAD",
                p => p.RawFinalFf, _ => true, _ => null),

            new("02","MIX",     "CHANNEL MIX",             "StageMixedForce (pre-gain)",
                "Per-wheel Mz/Fx/Fy blended to centre, before any gain. Column games (R3E/LMU) skip this.",  "HEAD",
                p => p.StageMixedForce, g => g is not SupportedGame.Raceroom and not SupportedGame.LeMansUltimate, g => g switch { SupportedGame.AssettoCorsa => "REMAPPED", SupportedGame.AssettoCorsaCompetizione => "+ForceGain", _ => null }),

            new("03","NORM",    "NORMALIZE",                "PostCompressionForce",
                "MasterGain × auto-gain ÷ ForceScale. R3E: MasterGain only. LMU: rolling-peak auto-norm.",  "HEAD",
                p => p.PostCompressionForce, _ => true, _ => null),

            new("04","LUT",     "LUT CURVE",                "PostLutForce",
                "33-point response curve (linear default). R3E/LMU pass through — their shaping lives in Normalize.",  "HEAD",
                p => p.PostLutForce, g => g is not SupportedGame.Raceroom and not SupportedGame.LeMansUltimate, g => g switch { SupportedGame.Raceroom => "PASSTHROUGH", SupportedGame.LeMansUltimate => "PASSTHROUGH", _ => null }),

            new("05","DAMP",    "DAMPING",                  "PostDampingForce",
                "Viscous + friction (always on) plus speed-scaled gyro/inertia. R3E shows centering-shaper output here.",  "CORE",
                p => p.PostDampingForce, _ => true, g => g switch { SupportedGame.Raceroom => "CENTERING", _ => null }),

            new("06","GAIN",    "OUTPUT GAIN",              "StageGainForce (post-gain)",
                "OutputGain slider in isolation. Everything downstream is shaping, not gain.",  "CORE",
                p => p.StageGainForce, _ => true, _ => null),

            new("07","CORE",    "CORE — SELF-ALIGNING TORQUE", "CoreForce (= PostOutputGainForce)",
                "Centering → grip guard → crash → tyre condition → hydroplaning → brake → speed fade → EMA. No EQ, no slew.",  "CORE",
                p => p.CoreForce, _ => true, _ => null),

            new("08","SLIP",    "SLIP",                     "StageSlipDelta",
                "Slip-path contribution to detail. EVO/LMU: delta vs reference. R3E: absolute add.",  "DETAIL",
                p => p.StageSlipDelta, g => g is not SupportedGame.AssettoCorsa, g => g switch { SupportedGame.AssettoCorsaCompetizione => "ZEROED", _ => null }),

            new("09","DYN",     "DYNAMICS + FLEX",          "StageDynFlex",
                "G-force / suspension / yaw dynamics + tyre carcass flex. LMU reports tyre-temp term here.",  "DETAIL",
                p => p.StageDynFlex, g => g is not SupportedGame.AssettoCorsa and not SupportedGame.AssettoCorsaCompetizione, g => g switch { SupportedGame.LeMansUltimate => "TEMP ONLY", _ => null }),

            new("10","VIB",     "VIBRATION MIX",            "VibrationForce (kerb+slip+road+ABS)",
                "Sums into detail (EVO/R3E/LMU). AC mixes raw channels into core instead; ACC zeroes gains by default.",  "DETAIL",
                p => p.VibrationForce, g => g is not SupportedGame.AssettoCorsa, g => g switch { SupportedGame.AssettoCorsaCompetizione => "ZEROED", _ => null }),

            new("11","MODS",    "ROAD / ABS / LFE",         "StageSubMods",
                "ABS + road + scrub + rear + offtrack + LFE (+ R3E pre-EQ synth: flatspot/surface/brake/TC).",  "DETAIL",
                p => p.StageSubMods, g => g is not SupportedGame.AssettoCorsa, g => g switch { SupportedGame.AssettoCorsaCompetizione => "ZEROED", SupportedGame.Raceroom => "SYNTH", _ => null }),

            new("12","DETAIL",  "DETAIL (EQ+SLEW)",         "DetailForce (= PostDynamicForce)",
                "Summed texture through 10-band EQ, DC-blocker, slew, gyro/TC hooks. Bypassed on AC.",  "DETAIL",
                p => p.DetailForce != 0f ? p.DetailForce : p.PostDynamicForce, g => g is not SupportedGame.AssettoCorsa, g => g switch { SupportedGame.AssettoCorsa => "BYPASSED", _ => null }),

            new("13","PRE",     "PRE-CLIP SUM",             "StagePreClipForce",
                "Filtered core + detail before any limiting — compare with FINAL to see exactly what the limiter took.",  "MERGE",
                p => p.StagePreClipForce, _ => true, _ => null),

            new("14","CLIP",    "FINAL + CLIP",             "MainForce (+ IsClipping)",
                "Soft-clip (knee 0.8) → soft-lock → noise gate. ACC multiplies ×ForceGain here.",  "MERGE",
                p => p.MainForce, _ => true, g => g switch { SupportedGame.AssettoCorsaCompetizione => "×ForceGain", _ => null }),

            new("15","WHEEL",   "WHEEL",                    "DirectInput / TrueForce command",
                "The actual command sent to your wheelbase.",  "OUTPUT",
                p => p.MainForce, _ => true, _ => null),
        };

        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(66) };
        _timer.Tick += (_, _) => Refresh(false);
        Loaded += (_, _) => _timer.Start();
        Unloaded += (_, _) => _timer.Stop();
        IsVisibleChanged += (_, _) => { if (IsVisible) Refresh(true); };
    }

    private void OnResetPeaks(object sender, RoutedEventArgs e)
    {
        for (int i = 0; i < 15; i++) GetMeter(i)?.ResetPeak();
        DetailMeter?.ResetPeak();
    }

    private void OnStageClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is Border border && border.Tag is string tag && int.TryParse(tag, out int idx))
        {
            _selectedStage = _selectedStage == idx - 1 ? -1 : idx - 1;
            UpdateCardSelection();
            UpdateDetail();
        }
    }

    private void UpdateCardSelection()
    {
        for (int i = 0; i < 15; i++)
        {
            var card = GetCard(i);
            if (card == null) continue;
            bool sel = i == _selectedStage;
            card.BorderBrush = sel
                ? new SolidColorBrush(Color.FromRgb(0xFF, 0xF0, 0x88))
                : new SolidColorBrush(Color.FromRgb(0x30, 0x36, 0x3D));
        }
    }

    private static readonly SolidColorBrush ActiveBadgeBrush = new(Color.FromArgb(0x1A, 0x7E, 0xE7, 0x87));
    private static readonly SolidColorBrush DimBadgeBrush = new(Color.FromArgb(0x33, 0x48, 0x4F, 0x58));
    private static readonly SolidColorBrush ActiveTextBrush = new(Color.FromRgb(0x7E, 0xE7, 0x87));
    private static readonly SolidColorBrush DimTextBrush = new(Color.FromRgb(0x8B, 0x94, 0x9E));
    private static readonly SolidColorBrush RedTextBrush = new(Color.FromRgb(0xFF, 0x17, 0x44));
    private static readonly SolidColorBrush GreenTextBrush = new(Color.FromRgb(0x7E, 0xE7, 0x87));

    private void Refresh(bool force)
    {
        if (!IsVisible) return;
        if (FreezeCheck?.IsChecked == true && !force) return;

        var vm = App.ViewModel;
        if (vm == null) return;

        var proc = vm.TelemetryLoop?.LatestProcessed;
        var raw = vm.TelemetryLoop?.LatestRaw;
        var game = vm.SelectedGame;
        bool live = proc != null && raw != null && vm.IsRunning;

        GameNameLabel.Text = vm.GameDisplayName;
        PipelineClassLabel.Text = vm.Pipeline != null ? $"({vm.Pipeline.GetType().Name})" : "";

        if (!live)
        {
            LiveStatusLabel.Text = vm.IsRunning
                ? "Running — waiting for game telemetry…"
                : "Idle — click any stage below to inspect it, or press START for live levels.";
        }
        else
        {
            bool clipping = proc!.IsClipping;
            LiveStatusLabel.Text = $"LIVE  •  {raw!.SpeedKmh:F0} km/h  •  steer {raw.SteerAngle:+0.00;-0.00}  •  {(clipping ? "⚠ CLIPPING" : "headroom OK")}";
            LiveStatusLabel.Foreground = clipping
                ? new SolidColorBrush(Color.FromRgb(0xFF, 0x17, 0x44))
                : new SolidColorBrush(Color.FromRgb(0x7E, 0xE7, 0x87));
        }

        for (int i = 0; i < 15; i++)
        {
            var s = _stages[i];
            var meter = GetMeter(i);
            var val = GetVal(i);
            if (meter == null || val == null) continue;

            float level = live ? s.Value(proc!) : 0f;
            bool active = s.Active(game);
            meter.IsActive = active;
            meter.SetLevel(live && active ? level : 0f);
            val.Text = live && active ? $"{level:+0.000;-0.000}" : (active ? "0.000" : "—");

            // Dim inactive cards
            var card = GetCard(i);
            if (card != null && i != _selectedStage)
            {
                card.Opacity = active ? 1.0 : 0.5;
            }
        }

        // Side strip
        float vib = live ? Math.Abs(proc!.VibrationForce) : 0f;
        MeterHf8.IsActive = vm.Hf8Connected;
        MeterHf8.SetLevel(vib);
        ValHf8.Text = live ? $"{vib:F3}" : "0.000";

        float pedal = live ? Math.Max(raw!.BrakeInput, raw.GasInput) : 0f;
        MeterPedal.IsActive = true;
        MeterPedal.SetLevel(pedal);
        ValPedal.Text = live ? $"{pedal * 100f:F0}%" : "0%";

        float rpm = live ? Math.Clamp(raw!.RpmPercent / 100f, 0f, 1f) : 0f;
        MeterLed.IsActive = vm.IsWheelConnected;
        MeterLed.SetLevel(rpm);
        ValLed.Text = live ? $"{rpm * 100f:F0}%" : "0%";

        GameNotesText.Text = GameNotes(game);

        UpdateDetail();
    }

    private void UpdateDetail()
    {
        if (_selectedStage < 0 || _selectedStage >= 15)
        {
            DetailPanel.Visibility = Visibility.Collapsed;
            return;
        }

        DetailPanel.Visibility = Visibility.Visible;
        var s = _stages[_selectedStage];
        var vm = App.ViewModel;
        var proc = vm?.TelemetryLoop?.LatestProcessed;
        var game = vm?.SelectedGame ?? SupportedGame.None;
        bool live = proc != null && vm?.IsRunning == true;

        DetailNum.Text = s.Num;
        DetailTitle.Text = s.Title;
        DetailField.Text = s.Field;
        DetailDesc.Text = s.Desc;
        DetailLane.Text = $"Branch: {s.Lane}";

        float level = live ? s.Value(proc!) : 0f;
        bool active = s.Active(game);
        string? note = s.Note(game);

        DetailMeter.IsActive = active;
        DetailMeter.SetLevel(live && active ? level : 0f);
        DetailSign.Text = live && active ? (level >= 0 ? "+" : "−") : "";
        DetailVal.Text = live && active ? $"{level:+0.000;-0.000}" : (active ? "0.000" : "—");

        SetBadge(DetailBadge, DetailBadgeText, active, note ?? (active ? "ACTIVE" : "BYPASSED"));

        string gameNote = _selectedStage switch
        {
            1 => game switch { SupportedGame.AssettoCorsa => "AC: mix scales are remapped (Mz 2×0.4, Fx 1500×0.15, Fy 1500).", SupportedGame.AssettoCorsaCompetizione => "ACC: mix runs through full EVO chain.", _ => "" },
            3 => game switch { SupportedGame.Raceroom => "R3E: no LUT — column force is already normalized.", SupportedGame.LeMansUltimate => "LMU: no LUT — tanh normalization in stage 03.", _ => "" },
            4 => game switch { SupportedGame.Raceroom => "R3E: this meter shows the centering-shaper output (smoothstep at small angles).", _ => "" },
            7 => game switch { SupportedGame.AssettoCorsaCompetizione => "ACC: slip gains zeroed by default.", _ => "" },
            8 => game switch { SupportedGame.LeMansUltimate => "LMU: no dynamics/flex — this shows the tyre-temp term only.", _ => "" },
            9 => game switch { SupportedGame.AssettoCorsaCompetizione => "ACC: vibration gains zeroed by default.", _ => "" },
            10 => game switch { SupportedGame.Raceroom => "R3E: includes synth flatspot/surface/brake/TC textures.", SupportedGame.AssettoCorsaCompetizione => "ACC: sub-mod gains zeroed by default.", _ => "" },
            11 => game switch { SupportedGame.AssettoCorsa => "AC: detail bypassed — raw vibration goes to core after EQ.", _ => "" },
            13 => game switch { SupportedGame.AssettoCorsaCompetizione => "ACC: ×ForceGain multiplier applied here.", _ => "" },
            _ => ""
        };
        DetailGameNote.Text = gameNote;
    }

    private static void SetBadge(Border badge, TextBlock badgeText, bool active, string text)
    {
        badgeText.Text = text;
        badge.Background = active
            ? new SolidColorBrush(Color.FromArgb(0x1A, 0x7E, 0xE7, 0x87))
            : new SolidColorBrush(Color.FromArgb(0x33, 0x48, 0x4F, 0x58));
        badgeText.Foreground = active
            ? new SolidColorBrush(Color.FromRgb(0x7E, 0xE7, 0x87))
            : new SolidColorBrush(Color.FromRgb(0x8B, 0x94, 0x9E));
    }

    private Border? GetCard(int i) => i switch
    {
        0 => Card01, 1 => Card02, 2 => Card03, 3 => Card04, 4 => Card05,
        5 => Card06, 6 => Card07, 7 => Card08, 8 => Card09, 9 => Card10,
        10 => Card11, 11 => Card12, 12 => Card13, 13 => Card14, 14 => Card15,
        _ => null
    };

    private LedVuMeter? GetMeter(int i) => i switch
    {
        0 => Meter01, 1 => Meter02, 2 => Meter03, 3 => Meter04, 4 => Meter05,
        5 => Meter06, 6 => Meter07, 7 => Meter08, 8 => Meter09, 9 => Meter10,
        10 => Meter11, 11 => Meter12, 12 => Meter13, 13 => Meter14, 14 => Meter15,
        _ => null
    };

    private TextBlock? GetVal(int i) => i switch
    {
        0 => Val01, 1 => Val02, 2 => Val03, 3 => Val04, 4 => Val05,
        5 => Val06, 6 => Val07, 7 => Val08, 8 => Val09, 9 => Val10,
        10 => Val11, 11 => Val12, 12 => Val13, 13 => Val14, 14 => Val15,
        _ => null
    };

    private static bool IsPerWheel(SupportedGame g) => g is SupportedGame.AcEvo or SupportedGame.AssettoCorsa or SupportedGame.AssettoCorsaCompetizione;

    private static string GameNotes(SupportedGame g) => g switch
    {
        SupportedGame.AcEvo =>
            "AC EVO: full 15-stage chain. Per-wheel Mz/Fx/Fy through all stages to wheel output.",
        SupportedGame.AssettoCorsa =>
            "ASSETTO CORSA: simplified chain. Stages 02 Mix remapped, 08–12 bypassed — raw vibration joins core after EQ.",
        SupportedGame.AssettoCorsaCompetizione =>
            "ACC: full chain but stages 08–11 zeroed by default. Enable gains on the FFB Tuning page to see them.",
        SupportedGame.Raceroom =>
            "RACEROOM: column-force pipeline. Stages 02 Mix and 04 LUT are PASSTHROUGH. Stage 11 carries synth textures.",
        SupportedGame.LeMansUltimate =>
            "LMU: column-force pipeline. Stages 02 Mix and 04 LUT are PASSTHROUGH. Stage 09 carries tyre-temp only.",
        _ =>
            "No game selected. Pick a game (or enable auto-detect) and press START. Click any stage above to see details."
    };
}
