using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using AcEvoFfbTuner.Core.DirectInput;
using AcEvoFfbTuner.Core.Profiles;
using AcEvoFfbTuner.ViewModels;

namespace AcEvoFfbTuner.Views;

/// <summary>
/// The single guided setup flow: Game → Wheelbase (+ direction test) →
/// Accessories → Strength (optional on-track calibration) → Save.
///
/// Design rules (replacing the old heuristic wizard + Calibration Wizard +
/// Auto Setup button):
///  • Deterministic. The baseline comes from the wheel table
///    (WheelbaseAutoConfigurator); nothing is guessed from driving except
///    the strength scale.
///  • Force direction is a HARDWARE property, decided once by a static test
///    through the real output path → ForceInvertEnabled. The wizard never
///    flips per-game channel signs (native torque signs are authoritative).
///  • Game isolation: the wizard only touches shared, game-agnostic values
///    (OutputGain, wheel torque, invert, vibration master, accessory
///    toggles). Each pipeline keeps reading its own profile fields.
///  • Nothing is saved until Save; closing restores the previous profile.
/// </summary>
public partial class SetupWizardOverlay : Window
{
    private enum Step { Game, Wheel, Accessories, Strength, Save }
    private static readonly string[] StepTitles = ["Game", "Wheelbase", "Accessories", "Strength", "Save"];
    private const int StepCount = 5;

    // Strength targets: share of the wheel's range the strongest sustained
    // corners should use (p98 of core force). Headroom above is left for
    // kerbs/vibration so the output does not clip.
    private const float TargetLight = 0.60f, TargetBalanced = 0.75f, TargetStrong = 0.90f;
    // Without an on-track measurement the feel choice scales the table gain.
    private const float NoCalLight = 0.80f, NoCalBalanced = 1.00f, NoCalStrong = 1.15f;

    private const float TestForce = 0.10f;
    private const float TestForceRetry = 0.20f;
    private const float CalMinSpeedKmh = 30f;
    private const double CalRequiredSeconds = 60.0;

    private readonly FfbDeviceManager _deviceManager;
    private readonly MainViewModel _vm;

    private Step _step = Step.Game;
    private readonly string? _originalProfileName;
    private bool _saved;
    // True until OnLoaded: XAML parsing fires ValueChanged/TextChanged before
    // later-declared elements exist and must never write VM state.
    private bool _suppressEvents = true;

    // Wheelbase
    private bool _directionConfirmed;
    private bool _forceInvert;
    private float _testForceUsed = TestForce;
    private DispatcherTimer? _testTimer;
    private string _baselineKey = "";

    // Strength
    private float _tableOutputGain = 1f;
    private float? _peakPerUnitGain; // measured p98 |core| / OutputGain
    private bool _calRunning;
    private readonly List<float> _calSamples = new();
    private double _calDrivenSeconds;
    private readonly System.Diagnostics.Stopwatch _frameClock = new();
    private int _clipFrames, _calFrames;

    private readonly DispatcherTimer _statusTimer = new() { Interval = TimeSpan.FromSeconds(1) };

    public SetupWizardOverlay(MainViewModel viewModel, FfbDeviceManager deviceManager)
    {
        _vm = viewModel;
        _deviceManager = deviceManager;
        _originalProfileName = viewModel.SelectedProfile?.Name;
        InitializeComponent();
        DataContext = viewModel;
        BuildStepDots();
        _statusTimer.Tick += (_, _) => RefreshLiveStatus();
    }

    private static void Log(string msg)
    {
        try
        {
            File.AppendAllText(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "AcEvoFfbTuner", "setupwizard.log"), $"[{DateTime.Now:HH:mm:ss.fff}] {msg}\n");
        }
        catch { }
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        var wa = SystemParameters.WorkArea;
        Left = wa.Left + (wa.Width - Width) / 2;
        Top = wa.Top + 20;
        if (Height > wa.Height - 40) Height = wa.Height - 40;

        _forceInvert = _vm.ForceInvertEnabled;
        _suppressEvents = true;
        SliderTorque.Value = Math.Clamp(_vm.WheelMaxTorqueNm > 0 ? _vm.WheelMaxTorqueNm : 5f, 1f, 32f);
        RbBalanced.IsChecked = true;
        _suppressEvents = false;
        UpdateTorqueLabel();

        Log($"Opened: profile='{_originalProfileName}' game={_vm.SelectedGame} device='{_vm.DeviceName}' torque={_vm.WheelMaxTorqueNm:F1} invert={_forceInvert}");
        ShowStep(Step.Game);
        _statusTimer.Start();
    }

    // ═══════════════════════════ Navigation ═══════════════════════════

    private void BuildStepDots()
    {
        StepDots.Children.Clear();
        for (int i = 0; i < StepCount; i++)
        {
            StepDots.Children.Add(new Border
            {
                Height = 4, CornerRadius = new CornerRadius(2), Margin = new Thickness(2, 0, 2, 0),
                Background = Brush("#FF30363D")
            });
        }
    }

    private void ShowStep(Step step)
    {
        _step = step;
        PanelGame.Visibility = Vis(step == Step.Game);
        PanelWheel.Visibility = Vis(step == Step.Wheel);
        PanelAccessories.Visibility = Vis(step == Step.Accessories);
        PanelStrength.Visibility = Vis(step == Step.Strength);
        PanelSave.Visibility = Vis(step == Step.Save);

        int i = (int)step;
        StepTitleText.Text = $"{i + 1}. {StepTitles[i]}";
        StepProgressText.Text = $"{i + 1} / {StepCount}";
        for (int d = 0; d < StepDots.Children.Count; d++)
            ((Border)StepDots.Children[d]).Background = Brush(d <= i ? "#FFF0883E" : "#FF30363D");

        BtnBack.IsEnabled = i > 0;
        BtnNext.Content = step == Step.Save ? "Save & Finish" : "Next";

        switch (step)
        {
            case Step.Game: EnterGame(); break;
            case Step.Wheel: EnterWheel(); break;
            case Step.Accessories: EnterAccessories(); break;
            case Step.Strength: EnterStrength(); break;
            case Step.Save: EnterSave(); break;
        }
        UpdateNextEnabled();
    }

    private void OnNext(object sender, RoutedEventArgs e)
    {
        if (_step == Step.Save) { SaveAndFinish(); return; }
        if (_step == Step.Wheel) ApplyBaselineIfChanged();
        if (_step == Step.Strength) StopCalibration(keepResult: true);
        ShowStep(_step + 1);
    }

    private void OnBack(object sender, RoutedEventArgs e)
    {
        if (_step == Step.Game) return;
        if (_step == Step.Strength) StopCalibration(keepResult: true);
        ShowStep(_step - 1);
    }

    private void UpdateNextEnabled()
    {
        BtnNext.IsEnabled = _step switch
        {
            Step.Game => _vm.SelectedGame is not (SupportedGame.None or SupportedGame.Unsupported),
            Step.Wheel => _deviceManager.IsDeviceAcquired && _directionConfirmed,
            Step.Save => !string.IsNullOrWhiteSpace(TxtProfileName.Text) && SaveMatchesValid,
            _ => true
        };
    }

    private void RefreshLiveStatus()
    {
        switch (_step)
        {
            case Step.Game: EnterGame(); break;
            case Step.Wheel: UpdateWheelStatus(); break;
            case Step.Accessories: RefreshAccessoryStatus(); break;
            case Step.Save: UpdateScopeLabels(); break;
        }
        UpdateNextEnabled();
    }

    // ═══════════════════════════ Step 1: Game ═══════════════════════════

    private void EnterGame()
    {
        bool live = _vm.IsGameConnected;
        var game = _vm.SelectedGame;
        GameDetectedText.Text = game is SupportedGame.None or SupportedGame.Unsupported
            ? "No game selected — pick the game you want to set up below."
            : live ? $"✓ {GameName(game)} is running" : $"{GameName(game)} (not running)";
        GameDetectedText.Foreground = Brush(live ? "#FF3FB950" : "#FFE6EDF3");

        _suppressEvents = true;
        GameCombo.SelectedItem = GameCombo.Items.Cast<ComboBoxItem>()
            .FirstOrDefault(c => (string)c.Tag == game.ToString());
        _suppressEvents = false;
        GameCombo.IsEnabled = !live;
        GameLockedHint.Visibility = Vis(live);
        InGameSettingsText.Text = InGameSettings(game);
    }

    private void OnGameComboChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressEvents || !IsLoaded || GameCombo.SelectedItem is not ComboBoxItem item) return;
        if (Enum.TryParse<SupportedGame>((string)item.Tag, out var game))
        {
            _vm.SelectGameManually(game);
            _baselineKey = ""; // pipeline changed — baseline must be re-applied
            Log($"Game chosen manually: {game}");
        }
        EnterGame();
        UpdateNextEnabled();
    }

    private static string GameName(SupportedGame g) => g switch
    {
        SupportedGame.AcEvo => "AC EVO",
        SupportedGame.Raceroom => "RaceRoom",
        SupportedGame.AssettoCorsa => "Assetto Corsa",
        SupportedGame.LeMansUltimate => "Le Mans Ultimate",
        SupportedGame.AssettoCorsaCompetizione => "Assetto Corsa Competizione",
        _ => "No game"
    };

    // Kept in line with the Help catalog (Help → FFB Tuning → Column Force for
    // RaceRoom; the "IN-GAME FFB IS STILL ACTIVE" banner for the others).
    private static string InGameSettings(SupportedGame g) => g switch
    {
        SupportedGame.Raceroom =>
            "• Power Steering = All Cars\n• FFB Strength = 100%, FFB Maximum Force ≥ FFB Strength\n" +
            "• Smoothing = Off (or low on direct drive) — smoothing is done here\n• Gyro Damping = 0% and Gyro Effect = 0%\n" +
            "The app reads the game's steering force and drives the wheel from it.",
        SupportedGame.None or SupportedGame.Unsupported => "Pick a game to see its settings.",
        _ =>
            "• Set the game's own force feedback gain to 0 and restart the game.\n" +
            "• Close other wheel software that sends force (Pit House / G HUB can stay open for drivers, but not as a second FFB source).\n" +
            "The app reads physics from the game and drives the wheel itself — two FFB sources fight each other."
    };

    // ═══════════════════════════ Step 2: Wheelbase ═══════════════════════════

    private void EnterWheel()
    {
        if (!_deviceManager.IsDeviceAcquired)
            AutoConnectFirstFfbDevice();
        PopulateDevices();
        UpdateWheelStatus();
        UpdateDirectionText();
    }

    private void PopulateDevices()
    {
        _vm.RefreshDevicesCommand.Execute(null);
        var list = _vm.AvailableDevices.Where(d => d.IsFfbCapable).ToList();
        DeviceCombo.ItemsSource = list;
        DeviceCombo.SelectedItem = list.FirstOrDefault(d => d.ProductName == _deviceManager.ConnectedDevice?.ProductName)
                                   ?? list.FirstOrDefault();
    }

    private void AutoConnectFirstFfbDevice()
    {
        _vm.RefreshDevicesCommand.Execute(null);
        var first = _vm.AvailableDevices.FirstOrDefault(d => d.IsFfbCapable);
        if (first == null) return;
        _vm.SelectedDevice = first;
        _vm.ConnectDeviceCommand.Execute(null);
        Log($"Auto-connected '{first.ProductName}' ok={_deviceManager.IsDeviceAcquired}");
        AfterConnect();
    }

    private void OnRefreshDevices(object sender, RoutedEventArgs e) => PopulateDevices();

    private void OnConnectDevice(object sender, RoutedEventArgs e)
    {
        if (DeviceCombo.SelectedItem is not FfbDeviceInfo dev) return;
        _vm.SelectedDevice = dev;
        _vm.ConnectDeviceCommand.Execute(null);
        Log($"Connect '{dev.ProductName}' ok={_deviceManager.IsDeviceAcquired}");
        AfterConnect();
    }

    private void AfterConnect()
    {
        // A different wheel invalidates the direction test and torque.
        _directionConfirmed = false;
        _forceInvert = _vm.ForceInvertEnabled; // connect-time auto-detect result
        _suppressEvents = true;
        SliderTorque.Value = Math.Clamp(_vm.WheelMaxTorqueNm > 0 ? _vm.WheelMaxTorqueNm : 5f, 1f, 32f);
        _suppressEvents = false;
        UpdateTorqueLabel();
        UpdateWheelStatus();
        UpdateDirectionText();
        UpdateNextEnabled();
    }

    private void UpdateWheelStatus()
    {
        bool ok = _deviceManager.IsDeviceAcquired;
        string name = _deviceManager.ConnectedDevice?.ProductName ?? "";
        WheelStatusText.Text = ok ? $"✓ Connected: {name}" : "No wheel connected — pick it below and press Connect.";
        WheelStatusText.Foreground = Brush(ok ? "#FF3FB950" : "#FFFFD600");

        string warn = "";
        if (ok)
        {
            string n = name.ToUpperInvariant();
            bool logitech = n.Contains("LOGITECH") || n.Contains("G29") || n.Contains("G920") || n.Contains("G923")
                            || n.Contains("G27") || n.Contains("DRIVING FORCE") || n.Contains("RS50") || n.Contains("G PRO");
            if (logitech && !_vm.IsTrueForceOutput && !IsProcessRunning("lghub"))
                warn = "⚠ Logitech G HUB is not running — this wheel needs it for force feedback. Start G HUB (it can run minimized).";
        }
        WheelWarningText.Text = warn;
        WheelWarningText.Visibility = Vis(warn.Length > 0);
    }

    private static bool IsProcessRunning(string name)
    {
        try { return System.Diagnostics.Process.GetProcessesByName(name).Length > 0; }
        catch { return false; }
    }

    private void OnTorqueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (LblTorque == null) return;
        UpdateTorqueLabel();
        if (_suppressEvents) return;
        _vm.WheelMaxTorqueNm = (float)SliderTorque.Value;
    }

    private void UpdateTorqueLabel()
    {
        float t = (float)SliderTorque.Value;
        LblTorque.Text = $"{t:F1} Nm";
        var type = WheelbaseAutoConfigurator.DetectWheelType(_deviceManager.ConnectedDevice?.ProductName ?? "");
        TorqueHint.Text = $"Detected as {type switch { WheelCharacteristics.GearDriven => "gear-driven", WheelCharacteristics.BeltDriven => "belt-driven", _ => "direct drive" }}. " +
                          "Set this to your base's rated peak torque — it picks the starting gain, damping and safety limits.";
    }

    private void OnTestDirection(object sender, RoutedEventArgs e) => RunDirectionTest(_testForceUsed);

    private void RunDirectionTest(float force)
    {
        if (!_deviceManager.IsDeviceAcquired)
        {
            DirectionResultText.Text = "Connect the wheel first.";
            return;
        }
        // Apply the current invert choice to the device so the test exercises
        // exactly what driving will use.
        _vm.ForceInvertEnabled = _forceInvert;
        string? err = _vm.BeginManualTestForce(force, out _);
        if (err != null)
        {
            DirectionResultText.Text = err;
            DirectionResultText.Foreground = Brush("#FFFF7B72");
            return;
        }
        Log($"Direction test: +{force:F2} invert={_forceInvert}");
        BtnTestDirection.IsEnabled = false;
        DirectionResultText.Text = "Pushing… watch which way the wheel turns.";
        DirectionResultText.Foreground = Brush("#FFFFD600");
        _testTimer?.Stop();
        _testTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(900) };
        _testTimer.Tick += (_, _) =>
        {
            _testTimer?.Stop();
            _vm.EndManualTestForce();
            BtnTestDirection.IsEnabled = true;
            DirectionAnswerPanel.Visibility = Visibility.Visible;
            DirectionResultText.Text = "Which way did it turn?";
        };
        _testTimer.Start();
    }

    private void OnDirectionLeft(object sender, RoutedEventArgs e)
    {
        _directionConfirmed = true;
        Log($"Direction: LEFT — correct, invert={_forceInvert}");
        DirectionAnswerPanel.Visibility = Visibility.Collapsed;
        UpdateDirectionText();
        UpdateNextEnabled();
    }

    private void OnDirectionRight(object sender, RoutedEventArgs e)
    {
        DirectionAnswerPanel.Visibility = Visibility.Collapsed;
        if (_vm.IsTrueForceOutput)
        {
            // The TrueForce stream applies its own fixed direction; the
            // DirectInput invert flag does not reach it.
            DirectionResultText.Text = "This wheel's TrueForce direction is fixed by the app and should never be reversed. " +
                                       "Please use Send to Dev (Settings) so we can fix it — do not drive with reversed force.";
            DirectionResultText.Foreground = Brush("#FFFF7B72");
            Log("Direction: RIGHT on TrueForce — reported to user, not correctable via invert");
            return;
        }
        _forceInvert = !_forceInvert;
        _vm.ForceInvertEnabled = _forceInvert;
        _directionConfirmed = false;
        Log($"Direction: RIGHT — invert flipped to {_forceInvert}, re-testing");
        DirectionResultText.Text = "Reversed the output. Press Test again to confirm it now turns LEFT.";
        DirectionResultText.Foreground = Brush("#FFFFD600");
        UpdateNextEnabled();
    }

    private void OnDirectionNone(object sender, RoutedEventArgs e)
    {
        DirectionAnswerPanel.Visibility = Visibility.Collapsed;
        if (_testForceUsed < TestForceRetry)
        {
            _testForceUsed = TestForceRetry;
            DirectionResultText.Text = "Trying a firmer push — hands off the wheel.";
            RunDirectionTest(_testForceUsed);
            return;
        }
        DirectionResultText.Text = "Still no movement. Check the wheelbase is powered, its own software isn't blocking force " +
                                   "(e.g. MOZA Pit House 'FFB off', Fanatec 'SEN/FF' set to 0), then test again.";
        DirectionResultText.Foreground = Brush("#FFFF7B72");
        Log("Direction: no movement at retry force");
    }

    private void UpdateDirectionText()
    {
        if (!_directionConfirmed) return;
        DirectionResultText.Text = _forceInvert
            ? "✓ Direction confirmed (this wheel needs reversed output — saved with the profile)."
            : "✓ Direction confirmed.";
        DirectionResultText.Foreground = Brush("#FF3FB950");
    }

    // ═══════════════════════════ Baseline ═══════════════════════════

    private void ApplyBaselineIfChanged()
    {
        string key = $"{_vm.SelectedGame}|{_deviceManager.ConnectedDevice?.ProductName}|{SliderTorque.Value:F1}|{_forceInvert}";
        if (key == _baselineKey) return;
        _baselineKey = key;
        var baseline = _vm.ApplyWizardBaseline((float)SliderTorque.Value, _forceInvert);
        _tableOutputGain = baseline.OutputGain;
        _peakPerUnitGain = null; // a new baseline invalidates an old measurement
        _suppressEvents = true;
        SliderEffects.Value = _vm.VibrationMasterGain;
        _suppressEvents = false;
        LblEffects.Text = $"{SliderEffects.Value:F2}";
        Log($"Baseline applied: key={key} OG={baseline.OutputGain:F3} damp={baseline.Damping.SpeedDamping:F2} fric={baseline.Damping.Friction:F2} lock={baseline.SteeringLockDegrees}");
    }

    // ═══════════════════════════ Step 3: Accessories ═══════════════════════════

    private sealed record AccessoryRow(string Title, string Description, Func<bool> Detected, string? ToggleProperty, string DetectedText, string MissingText);

    private List<(AccessoryRow Row, TextBlock Status)> _accessoryStatus = new();

    private IEnumerable<AccessoryRow> Accessories()
    {
        yield return new("Pedals", "Read your USB pedals directly (needed for pedal haptics and pedal calibration). Leave off if the game reads them.",
            () => _vm.PedalInputEnabled, nameof(MainViewModel.PedalInputEnabled), "Reading pedals", "Off — the game reads them");
        yield return new("Pedal haptics", "Vibration motors on the pedals (MOZA PHF or Osoyoo/serial). Tune routing under Devices → Pedals → Haptics.",
            () => _vm.MozaPhfIsConnected || _vm.OsoyooIsConnected, null, "Connected", "Not detected");
        yield return new("Seat / back haptics (HF8)", "NLR HF8 / HF8 Pro vibration pads.",
            () => _vm.Hf8Connected, nameof(MainViewModel.Hf8Enabled), "Connected", "Not detected");
        yield return new("Wheel LEDs", "Rev lights, flags and ABS flashes on the wheel rim.",
            () => _vm.IsWheelConnected, null, "Connected", "Not detected");
        if (_vm.LogitechWheelDetected)
            yield return new("Logitech TrueForce", "Drives the RS50 / G PRO directly over the TrueForce stream (no G HUB FFB needed).",
                () => _vm.LogitechWheelDetected, nameof(MainViewModel.TrueForceEnabled), "Detected", "Not detected");
    }

    private void EnterAccessories()
    {
        AccessoryRows.Children.Clear();
        _accessoryStatus = new();
        foreach (var a in Accessories())
        {
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var left = new StackPanel();
            left.Children.Add(new TextBlock { Text = a.Title, FontSize = 14, FontWeight = FontWeights.SemiBold });
            var status = new TextBlock { FontSize = 12, Margin = new Thickness(0, 2, 0, 2) };
            left.Children.Add(status);
            left.Children.Add(new TextBlock { Text = a.Description, Style = (Style)FindResource("Hint") });
            grid.Children.Add(left);

            var right = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            if (a.ToggleProperty != null)
            {
                var cb = new CheckBox { Content = "Use", Margin = new Thickness(10, 0, 8, 0) };
                cb.SetBinding(ToggleButtonIsChecked, new Binding(a.ToggleProperty) { Mode = BindingMode.TwoWay });
                right.Children.Add(cb);
            }
            var open = new Button { Content = "Devices…", Style = (Style)FindResource("WizardButton"), Margin = new Thickness(6, 0, 0, 0) };
            open.Click += (_, _) => _vm.CurrentPage = NavPage.Devices;
            right.Children.Add(open);
            Grid.SetColumn(right, 1);
            grid.Children.Add(right);

            AccessoryRows.Children.Add(new Border { Style = (Style)FindResource("Card"), Child = grid });
            _accessoryStatus.Add((a, status));
        }
        RefreshAccessoryStatus();
    }

    private static readonly DependencyProperty ToggleButtonIsChecked = System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty;

    private void RefreshAccessoryStatus()
    {
        foreach (var (row, status) in _accessoryStatus)
        {
            bool on = row.Detected();
            status.Text = (on ? "● " : "○ ") + (on ? row.DetectedText : row.MissingText);
            status.Foreground = Brush(on ? "#FF3FB950" : "#FF8B949E");
        }
    }

    private void OnEffectsChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (LblEffects != null) LblEffects.Text = $"{SliderEffects.Value:F2}";
        if (_suppressEvents) return;
        _vm.VibrationMasterGain = (float)SliderEffects.Value;
    }

    // ═══════════════════════════ Step 4: Strength ═══════════════════════════

    private float SelectedTarget => RbLight.IsChecked == true ? TargetLight : RbStrong.IsChecked == true ? TargetStrong : TargetBalanced;
    private float SelectedNoCalScale => RbLight.IsChecked == true ? NoCalLight : RbStrong.IsChecked == true ? NoCalStrong : NoCalBalanced;
    private string SelectedFeelName => RbLight.IsChecked == true ? "Light" : RbStrong.IsChecked == true ? "Strong" : "Balanced";

    private void EnterStrength()
    {
        ApplyStrength();
        UpdateCalStatus();
        var p = _vm.Pipeline;
        BaselineSummary.Text =
            $"Wheel torque     {_vm.WheelMaxTorqueNm:F1} Nm\n" +
            $"Output reversed  {(_vm.ForceInvertEnabled ? "yes" : "no")}\n" +
            $"Output gain      {_vm.OutputGain:F2}  (table {_tableOutputGain:F2})\n" +
            $"Damping / fric.  {p.Damping.SpeedDampingCoefficient:F2} / {p.Damping.FrictionLevel:F2}\n" +
            $"Steering lock    {_vm.SteeringLockDegrees:F0}°\n" +
            $"Wheel effects    {_vm.VibrationMasterGain:F2}";
    }

    private void OnFeelChanged(object sender, RoutedEventArgs e)
    {
        if (_suppressEvents || !IsLoaded) return;
        ApplyStrength();
        if (_step == Step.Strength) EnterStrength();
    }

    private void ApplyStrength()
    {
        float og = _peakPerUnitGain is float k && k > 0.0001f
            ? SelectedTarget / k
            : _tableOutputGain * SelectedNoCalScale;
        og = Math.Clamp(og, 0.05f, 3.0f);
        _vm.OutputGain = og;
        StrengthSummary.Text = _peakPerUnitGain.HasValue
            ? $"{SelectedFeelName}: strongest corners use ~{SelectedTarget * 100:F0}% of the wheel. Output gain {og:F2} (calibrated on track)."
            : $"{SelectedFeelName}: output gain {og:F2} from the {_vm.WheelMaxTorqueNm:F1} Nm wheel table. Calibrate on track for an exact fit.";
    }

    private void OnCalStart(object sender, RoutedEventArgs e)
    {
        if (!_vm.IsGameConnected)
        {
            CalStatusText.Text = "Start the game and get on track first.";
            CalStatusText.Foreground = Brush("#FFFFD600");
            return;
        }
        _vm.StartTelemetryIfStopped(); // START output so the wheel is live
        _calSamples.Clear();
        _calDrivenSeconds = 0;
        _clipFrames = _calFrames = 0;
        _calRunning = true;
        _frameClock.Restart();
        BtnCalStart.IsEnabled = false;
        Log($"Calibration started OG={_vm.OutputGain:F3}");
        UpdateCalStatus();
    }

    private void OnCalReset(object sender, RoutedEventArgs e)
    {
        StopCalibration(keepResult: false);
        _peakPerUnitGain = null;
        ApplyStrength();
        UpdateCalStatus();
        EnterStrength();
    }

    private void StopCalibration(bool keepResult)
    {
        if (!_calRunning) return;
        _calRunning = false;
        BtnCalStart.IsEnabled = true;
        if (keepResult && _calDrivenSeconds >= CalRequiredSeconds * 0.5 && _calSamples.Count > 200)
            FinishCalibration();
    }

    private void FinishCalibration()
    {
        _calRunning = false;
        BtnCalStart.IsEnabled = true;
        var sorted = _calSamples.OrderBy(v => v).ToArray();
        float p98 = sorted[(int)Math.Min(sorted.Length - 1, sorted.Length * 0.98)];
        if (p98 < 0.002f)
        {
            CalStatusText.Text = "Measured almost no steering force — check the game is sending force (in-game settings, step 1) and try again.";
            CalStatusText.Foreground = Brush("#FFFF7B72");
            Log($"Calibration: p98 per-unit-gain {p98:F4} too low — ignored");
            return;
        }
        _peakPerUnitGain = p98;
        float clipPct = _calFrames > 0 ? 100f * _clipFrames / _calFrames : 0f;
        Log($"Calibration done: samples={sorted.Length} driven={_calDrivenSeconds:F0}s p98/OG={p98:F4} clip={clipPct:F1}%");
        ApplyStrength();
        EnterStrength();
        CalStatusText.Text = $"✓ Calibrated from {_calDrivenSeconds:F0}s of driving (clipping during the run: {clipPct:F0}%). Change Light/Balanced/Strong freely.";
        CalStatusText.Foreground = Brush("#FF3FB950");
        CalProgress.Value = 1;
    }

    private void UpdateCalStatus()
    {
        if (_calRunning)
        {
            CalProgress.Value = Math.Min(1.0, _calDrivenSeconds / CalRequiredSeconds);
            CalStatusText.Text = $"Driving… {_calDrivenSeconds:F0}s / {CalRequiredSeconds:F0}s above {CalMinSpeedKmh:F0} km/h. Keep a normal pace through corners.";
            CalStatusText.Foreground = Brush("#FFFFD600");
        }
        else if (!_peakPerUnitGain.HasValue)
        {
            CalProgress.Value = 0;
            CalStatusText.Text = _vm.IsGameConnected ? "Ready — press Start when you're on track." : "Game not running — you can skip this and save the table-based strength.";
            CalStatusText.Foreground = Brush("#FF8B949E");
        }
    }

    /// <summary>Fed from the telemetry UI tick. <paramref name="coreForce"/> is
    /// the pipeline's core force after OutputGain and before the output
    /// clipper (FfbProcessedData.PostOutputGainForce) — every game pipeline
    /// fills it the same way, so the measurement is game-agnostic.</summary>
    public void UpdateLiveValues(float speedKmh, float coreForce, bool isClipping)
    {
        if (!_calRunning) return;
        double dt = _frameClock.Elapsed.TotalSeconds;
        _frameClock.Restart();
        if (dt > 0.5) dt = 0; // paused / stalled — don't count

        if (speedKmh >= CalMinSpeedKmh)
        {
            float og = Math.Max(_vm.OutputGain, 0.001f);
            _calSamples.Add(Math.Abs(coreForce) / og);
            _calDrivenSeconds += dt;
            _calFrames++;
            if (isClipping) _clipFrames++;
        }
        UpdateCalStatus();
        if (_calDrivenSeconds >= CalRequiredSeconds)
            FinishCalibration();
    }

    // ═══════════════════════════ Step 5: Save ═══════════════════════════

    // Detected values (empty until the game reports them while driving).
    private string DetCar => _vm.DetectedCarModel ?? "";
    private string DetClass => _vm.WizardCarClass;
    private string DetTrack => _vm.DetectedTrackName ?? "";
    // Match values actually saved: editable, pre-filled from detection, so the
    // car/class/track scopes work without being in the car.
    private string Car => TxtMatchCar.Text.Trim();
    private string CarClass => TxtMatchClass.Text.Trim();
    private string Track => TxtMatchTrack.Text.Trim();
    private string _lastAutoName = "";

    private void OnMatchChanged(object sender, TextChangedEventArgs e)
    {
        if (!IsLoaded || _step != Step.Save) return;
        UpdateAutoName();
        UpdateNextEnabled();
    }

    /// <summary>Fills empty match boxes from detection (never overwrites typing).</summary>
    private void PrefillMatches()
    {
        if (TxtMatchClass.Text.Length == 0 && DetClass.Length > 0) TxtMatchClass.Text = DetClass;
        if (TxtMatchCar.Text.Length == 0 && DetCar.Length > 0) TxtMatchCar.Text = DetCar;
        if (TxtMatchTrack.Text.Length == 0 && DetTrack.Length > 0) TxtMatchTrack.Text = DetTrack;
    }

    private bool SaveMatchesValid =>
        ScopeClass.IsChecked == true ? CarClass.Length > 0
        : ScopeCar.IsChecked == true ? Car.Length > 0
        : ScopeCarTrack.IsChecked == true ? Car.Length > 0 && Track.Length > 0
        : true;

    private void EnterSave()
    {
        UpdateScopeLabels();
        if (ScopeGame.IsChecked != true && ScopeClass.IsChecked != true && ScopeCar.IsChecked != true
            && ScopeCarTrack.IsChecked != true && ScopeGeneral.IsChecked != true)
            (DetClass.Length > 0 ? ScopeClass : ScopeGame).IsChecked = true;
        UpdateScopeLabels();
        UpdateAutoName();
    }

    private void UpdateScopeLabels()
    {
        PrefillMatches();
        ScopeGame.Content = $"Every car in {GameName(_vm.SelectedGame)}";
        ScopeClass.Content = "This car class only";
        ScopeCar.Content = "This car only";
        ScopeCarTrack.Content = "This car on this track";
        bool cls = ScopeClass.IsChecked == true, car = ScopeCar.IsChecked == true, ct = ScopeCarTrack.IsChecked == true;
        MatchPanel.Visibility = cls || car || ct ? Visibility.Visible : Visibility.Collapsed;
        RowClass.Visibility = cls ? Visibility.Visible : Visibility.Collapsed;
        RowCar.Visibility = car || ct ? Visibility.Visible : Visibility.Collapsed;
        RowTrack.Visibility = ct ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnScopeChanged(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        UpdateScopeLabels();
        UpdateAutoName();
        UpdateNextEnabled();
    }

    private (ProfileScope Scope, string CarMatch, string TrackMatch, string Name) Selection()
    {
        string g = _vm.GameShortName;
        if (ScopeClass.IsChecked == true) return (ProfileScope.PerCar, CarClass, "", $"{g} - {CarClass} - All");
        if (ScopeCar.IsChecked == true) return (ProfileScope.PerCar, Car, "", $"{g} - {Car} - All");
        if (ScopeCarTrack.IsChecked == true) return (ProfileScope.PerCarAndTrack, Car, Track, $"{g} - {Car} - {Track}");
        if (ScopeGeneral.IsChecked == true) return (ProfileScope.General, "", "", "All - All - All");
        return (ProfileScope.PerGame, "", "", $"{g} - All - All");
    }

    private void UpdateAutoName()
    {
        if (TxtProfileName == null) return;
        var sel = Selection();
        // Only overwrite the name while the user hasn't typed their own.
        if (string.IsNullOrWhiteSpace(TxtProfileName.Text) || TxtProfileName.Text == _lastAutoName)
        {
            TxtProfileName.Text = sel.Name;
            _lastAutoName = sel.Name;
        }
        ScopeHint.Text = sel.Scope switch
        {
            ProfileScope.General => "Never auto-loads. Pick it by hand on the Profiles page.",
            ProfileScope.PerGame => $"Auto-loads in {GameName(_vm.SelectedGame)} whenever no more specific profile matches.",
            ProfileScope.PerCarAndTrack => "Auto-loads only for this car on this track (most specific).",
            _ => $"Auto-loads when \"{sel.CarMatch}\" is detected in {GameName(_vm.SelectedGame)}."
        };
        UpdateNameHint();
    }

    private void OnNameChanged(object sender, TextChangedEventArgs e)
    {
        if (!IsLoaded) return;
        UpdateNameHint();
        UpdateNextEnabled();
    }

    private void UpdateNameHint()
    {
        if (NameHint == null) return;
        string name = TxtProfileName.Text.Trim();
        var existing = _vm.Profiles.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
        NameHint.Text = existing == null
            ? "Format: Game - Car type - Track (\"All\" where it doesn't restrict)."
            : existing.IsBuiltIn
                ? "That's a built-in profile name — pick another."
                : "A profile with this name exists. Saving replaces it (a .bak copy of the old one is kept).";
        NameHint.Foreground = Brush(existing == null ? "#FF8B949E" : "#FFFFB74D");
    }

    private void SaveAndFinish()
    {
        string name = TxtProfileName.Text.Trim();
        if (name.Length == 0) return;
        var sel = Selection();
        string? err = _vm.WizardSaveProfile(name, sel.Scope, sel.CarMatch, sel.TrackMatch);
        if (err != null)
        {
            SaveErrorText.Text = err;
            SaveErrorText.Visibility = Visibility.Visible;
            Log($"Save failed: {err}");
            return;
        }
        _saved = true;
        Log($"Saved '{name}' scope={sel.Scope} car='{sel.CarMatch}' track='{sel.TrackMatch}' OG={_vm.OutputGain:F3} invert={_vm.ForceInvertEnabled}");
        Speak("Profile saved. Setup complete.");
        Close();
    }

    // ═══════════════════════════ Window plumbing ═══════════════════════════

    private void OnCloseWizard(object sender, RoutedEventArgs e) => Close();

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        _statusTimer.Stop();
        _testTimer?.Stop();
        _calRunning = false;
        _vm.EndManualTestForce();
        if (!_saved)
        {
            Log($"Closed without saving — restoring '{_originalProfileName}'");
            _vm.RestoreProfileAfterWizard(_originalProfileName);
        }
    }

    private void Window_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left)
            try { DragMove(); } catch { }
    }

    private void Window_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (Keyboard.Modifiers != ModifierKeys.Control) return; // plain wheel scrolls content
        double s = e.Delta > 0 ? 1.05 : 0.95;
        if (Width * s >= MinWidth && Width * s <= 900) Width *= s;
        if (Height * s >= MinHeight && Height * s <= 1100) Height *= s;
        e.Handled = true;
    }

    private void Speak(string text) => _vm.VoiceService?.Speak(text);

    private static Visibility Vis(bool v) => v ? Visibility.Visible : Visibility.Collapsed;
    private static SolidColorBrush Brush(string hex) => (SolidColorBrush)new BrushConverter().ConvertFromString(hex)!;
}
