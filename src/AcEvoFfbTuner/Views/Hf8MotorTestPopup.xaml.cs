using System.Windows;
using System.Windows.Input;
using AcEvoFfbTuner.Core.DirectInput;

namespace AcEvoFfbTuner.Views;

public partial class Hf8MotorTestPopup : Window
{
    private readonly Hf8HapticController _controller;
    private CancellationTokenSource? _cts;

    private static readonly string[] MotorLabels =
    [
        "Back Upper Right", "Back Upper Left", "Back Lower Right", "Back Lower Left",
        "Seat Rear Right", "Seat Rear Left", "Seat Front Right", "Seat Front Left"
    ];

    private static readonly int[] PhysicalToSdk = [6, 7, 4, 5, 2, 3, 0, 1];

    public Hf8MotorTestPopup(Hf8HapticController controller)
    {
        _controller = controller;
        InitializeComponent();
    }

    private void OnMotorClick(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button btn) return;
        var tag = btn.Tag?.ToString() ?? "";
        if (!tag.StartsWith("Motor ")) return;
        if (!int.TryParse(tag[6..], out int motorNum)) return;

        int physicalIdx = motorNum - 1;
        if (physicalIdx < 0 || physicalIdx > 7) return;

        int sdkIndex = PhysicalToSdk[physicalIdx];
        PulseMotor(sdkIndex, $"Motor {motorNum} ({MotorLabels[physicalIdx]})");
    }

    private async void PulseMotor(int sdkIndex, string label)
    {
        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        var token = _cts.Token;

        StatusText.Text = $"Pulsing {label}...";

        try
        {
            var intensities = new float[8];
            intensities[sdkIndex] = 1.0f;
            _controller.SetMotorIntensities(intensities);

            await Task.Delay(400, token);

            intensities[sdkIndex] = 0f;
            _controller.SetMotorIntensities(intensities);

            StatusText.Text = $"{label} — done";
        }
        catch (OperationCanceledException) { }
    }

    private async void OnTestAll(object sender, RoutedEventArgs e)
    {
        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        var token = _cts.Token;

        TestAllBtn.IsEnabled = false;

        try
        {
            for (int i = 0; i < 8; i++)
            {
                if (token.IsCancellationRequested) break;

                int sdkIndex = PhysicalToSdk[i];
                StatusText.Text = $"Testing Motor {i + 1} ({MotorLabels[i]})...";

                var intensities = new float[8];
                intensities[sdkIndex] = 1.0f;
                _controller.SetMotorIntensities(intensities);

                await Task.Delay(350, token);

                intensities[sdkIndex] = 0f;
                _controller.SetMotorIntensities(intensities);

                await Task.Delay(150, token);
            }

            StatusText.Text = "Sequence complete";
        }
        catch (OperationCanceledException) { }
        finally
        {
            TestAllBtn.IsEnabled = true;
        }
    }

    private void OnAllOff(object sender, RoutedEventArgs e)
    {
        _cts?.Cancel();
        _controller.SetMotorIntensities(new float[8]);
        StatusText.Text = "All motors off";
    }

    private void OnClose(object sender, RoutedEventArgs e)
    {
        _cts?.Cancel();
        _controller.SetMotorIntensities(new float[8]);
        Close();
    }

    private void OnDragMove(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
            DragMove();
    }

    protected override void OnClosed(EventArgs e)
    {
        _cts?.Cancel();
        _controller.SetMotorIntensities(new float[8]);
        base.OnClosed(e);
    }
}
