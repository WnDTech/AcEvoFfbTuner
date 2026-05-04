using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using AcEvoFfbTuner.Core;
using AcEvoFfbTuner.Core.DirectInput;
using AcEvoFfbTuner.Core.FfbProcessing;
using AcEvoFfbTuner.Core.Profiles;

namespace AcEvoFfbTuner.Views;

public partial class CalibrationWizardOverlay : Window
{
    private bool _isTransparent;
    private int _currentStep;
    private const int MaxSteps = 6;

    private readonly FfbPipeline _pipeline;
    private readonly FfbDeviceManager _deviceManager;
    private readonly TelemetryLoop _telemetryLoop;
    private readonly Action _saveCallback;

    private readonly string[] _stepTitles =
    [
        "Step 1: Wheel Setup",
        "Step 2: Force Direction",
        "Step 3: Force Strength",
        "Step 4: Damping & Weight",
        "Step 5: Vibration & Texture",
        "Complete!"
    ];

    private readonly string[] _panelNames =
    [
        nameof(PanelStep0),
        nameof(PanelStep1),
        nameof(PanelStep2),
        nameof(PanelStep3),
        nameof(PanelStep4),
        nameof(PanelStep5)
    ];

    public CalibrationWizardOverlay(
        FfbPipeline pipeline,
        FfbDeviceManager deviceManager,
        TelemetryLoop telemetryLoop,
        Action saveCallback)
    {
        _pipeline = pipeline;
        _deviceManager = deviceManager;
        _telemetryLoop = telemetryLoop;
        _saveCallback = saveCallback;

        InitializeComponent();
        UpdateStepUI();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        var screen = SystemParameters.WorkArea;
        Left = screen.Right - Width - 20;
        Top = (screen.Height - Height) / 2;
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
            try { DragMove(); } catch { }
        }
    }

    private void ToggleTransparency(object sender, RoutedEventArgs e)
    {
        _isTransparent = !_isTransparent;

        if (_isTransparent)
        {
            ContentBorder.Background = new SolidColorBrush(Color.FromArgb(0x73, 0x0D, 0x0D, 0x0D));
            RootBorder.Background = new SolidColorBrush(Color.FromArgb(0x80, 0xE6, 0x7E, 0x22));
            TransparencyIcon.Text = "TRN";
        }
        else
        {
            ContentBorder.Background = new SolidColorBrush(Color.FromArgb(0xEB, 0x0D, 0x0D, 0x0D));
            RootBorder.Background = new SolidColorBrush(Color.FromArgb(0xDD, 0xE6, 0x7E, 0x22));
            TransparencyIcon.Text = "OPQ";
        }
    }

    private void OnMouseWheel(object sender, MouseWheelEventArgs e)
    {
        double delta = e.Delta > 0 ? -20 : 20;
        double newW = Width + delta;
        double newH = Height + delta;

        if (newW < 380 || newH < 500) return;
        if (newW > 700 || newH > 1000) return;

        Left += delta / 2;
        Top += delta / 2;
        Width = newW;
        Height = newH;
    }

    private void CloseOverlay(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void OnNext(object sender, RoutedEventArgs e)
    {
        if (_currentStep < MaxSteps - 1)
        {
            _currentStep++;
            UpdateStepUI();
        }
        else
        {
            _saveCallback();
            Close();
        }
    }

    private void OnBack(object sender, RoutedEventArgs e)
    {
        if (_currentStep > 0)
        {
            _currentStep--;
            UpdateStepUI();
        }
    }

    private void UpdateStepUI()
    {
        StepTitle.Text = _stepTitles[_currentStep];

        for (int i = 0; i < MaxSteps; i++)
        {
            var dot = (System.Windows.Controls.Border?)FindName($"StepDot{i}");
            if (dot != null)
                dot.Background = new SolidColorBrush(i <= _currentStep ? Color.FromRgb(0xE6, 0x7E, 0x22) : Color.FromRgb(0x33, 0x33, 0x55));

            var panel = (System.Windows.Controls.StackPanel?)FindName(_panelNames[i]);
            if (panel != null)
                panel.Visibility = i == _currentStep ? Visibility.Visible : Visibility.Collapsed;
        }

        BtnBack.IsEnabled = _currentStep > 0;

        if (_currentStep == MaxSteps - 1)
        {
            BtnNext.Content = "Save & Finish";
            BtnNext.Background = new SolidColorBrush(Color.FromRgb(0x22, 0x66, 0x22));
            if (FooterStatus != null)
                FooterStatus.Text = "Changes are LIVE but not saved — click Save & Finish to persist";
        }
        else
        {
            BtnNext.Content = "Next";
            BtnNext.Background = new SolidColorBrush(Color.FromRgb(0x44, 0x55, 0x66));
            if (FooterStatus != null)
                FooterStatus.Text = "Drag to move  |  Scroll to resize  |  Changes apply in real-time";
        }

        if (_currentStep == 0)
        {
            bool connected = _deviceManager.IsDeviceAcquired;
            Step0Status.Text = connected
                ? $"Wheel connected: {_deviceManager.ConnectedDevice?.ProductName ?? "Unknown"}"
                : "Waiting for wheel connection...";
            Step0Status.Foreground = connected
                ? new SolidColorBrush(Color.FromRgb(0x66, 0xBB, 0x6A))
                : new SolidColorBrush(Color.FromRgb(0xFF, 0xD6, 0x00));
            BtnNext.IsEnabled = connected;
        }
    }

    private void OnTestDirection(object sender, RoutedEventArgs e)
    {
        BtnTestDirection.IsEnabled = false;
        Step1Status.Text = "Sending test pulse... (wheel will move)";
        Step1Status.Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0xD6, 0x00));

        _deviceManager.SendConstantForce(0.15f);

        Task.Run(async () =>
        {
            await Task.Delay(500);
            _deviceManager.SendConstantForce(-0.15f);
            await Task.Delay(500);
            _deviceManager.SendConstantForce(0f);

            Dispatcher.Invoke(() =>
            {
                Step1Status.Text = "Force direction test complete. Did your wheel move? If forces feel reversed, toggle Force Invert in FFB Settings.";
                Step1Status.Foreground = new SolidColorBrush(Color.FromRgb(0x66, 0xBB, 0x6A));
                BtnTestDirection.IsEnabled = true;
            });
        });
    }

    private void OnOutputGainChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        float val = (float)Math.Round(e.NewValue, 3);
        _pipeline.OutputGain = val;
        if (LblOutputGain != null) LblOutputGain.Text = val.ToString("F2");
    }

    private void OnFrictionChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        float val = (float)Math.Round(e.NewValue, 3);
        _pipeline.Damping.FrictionLevel = val;
        if (LblFriction != null) LblFriction.Text = val.ToString("F3");
    }

    private void OnSpeedDampChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        float val = (float)Math.Round(e.NewValue, 3);
        _pipeline.Damping.SpeedDampingCoefficient = val;
        if (LblSpeedDamp != null) LblSpeedDamp.Text = val.ToString("F3");
    }

    private void OnKerbChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        float val = (float)Math.Round(e.NewValue, 2);
        _pipeline.VibrationMixer.KerbGain = val;
        if (LblKerb != null) LblKerb.Text = val.ToString("F2");
    }

    private void OnSlipChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        float val = (float)Math.Round(e.NewValue, 2);
        _pipeline.SlipEnhancer.SlipRatioGain = val;
        if (LblSlip != null) LblSlip.Text = val.ToString("F2");
    }

    public void UpdateLiveValues(float speedKmh, float mainForce, bool isClipping)
    {
        if (_currentStep == 2)
        {
            Step2Status.Text = speedKmh > 5f
                ? $"Speed: {speedKmh:F0} km/h | Force: {mainForce:F3}{(isClipping ? " (CLIPPING!)" : "")}"
                : "Drive to see live force values...";
            Step2Status.Foreground = isClipping
                ? new SolidColorBrush(Color.FromRgb(0xFF, 0x17, 0x44))
                : new SolidColorBrush(Color.FromRgb(0x66, 0xBB, 0x6A));
        }
        else if (_currentStep == 3)
        {
            Step3Status.Text = speedKmh > 5f
                ? $"Speed: {speedKmh:F0} km/h | Force: {mainForce:F3}"
                : "Drive to see live force values...";
            Step3Status.Foreground = new SolidColorBrush(Color.FromRgb(0x66, 0xBB, 0x6A));
        }
        else if (_currentStep == 4)
        {
            Step4Status.Text = speedKmh > 5f
                ? $"Speed: {speedKmh:F0} km/h | Force: {mainForce:F3}"
                : "Drive to see live force values...";
            Step4Status.Foreground = new SolidColorBrush(Color.FromRgb(0x66, 0xBB, 0x6A));
        }
    }

    public void InitializeSlidersFromPipeline()
    {
        SliderOutputGain.Value = _pipeline.OutputGain;
        LblOutputGain.Text = _pipeline.OutputGain.ToString("F2");

        SliderFriction.Value = _pipeline.Damping.FrictionLevel;
        LblFriction.Text = _pipeline.Damping.FrictionLevel.ToString("F3");

        SliderSpeedDamp.Value = _pipeline.Damping.SpeedDampingCoefficient;
        LblSpeedDamp.Text = _pipeline.Damping.SpeedDampingCoefficient.ToString("F3");

        SliderKerb.Value = _pipeline.VibrationMixer.KerbGain;
        LblKerb.Text = _pipeline.VibrationMixer.KerbGain.ToString("F2");

        SliderSlip.Value = _pipeline.SlipEnhancer.SlipRatioGain;
        LblSlip.Text = _pipeline.SlipEnhancer.SlipRatioGain.ToString("F2");
    }
}
