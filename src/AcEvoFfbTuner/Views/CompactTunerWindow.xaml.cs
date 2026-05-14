using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using AcEvoFfbTuner.ViewModels;

namespace AcEvoFfbTuner.Views;

public partial class CompactTunerWindow : Window
{
    private bool _isTopmost;
    private bool _isMaximized;
    private double _restoreLeft, _restoreTop, _restoreWidth, _restoreHeight;
    private bool _restoreTopmost;

    private enum NCHitTest
    {
        HTCLIENT = 1,
        HTCAPTION = 2,
        HTMINBUTTON = 8,
        HTMAXBUTTON = 9,
        HTCLOSE = 20,
    }

    public CompactTunerWindow()
    {
        InitializeComponent();
        DataContext = App.ViewModel;
        SourceInitialized += OnSourceInitialized;
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var helper = new WindowInteropHelper(this);
        var source = HwndSource.FromHwnd(helper.Handle);
        source?.AddHook(WndProc);
    }

    private nint WndProc(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        const int WM_NCHITTEST = 0x0084;

        if (msg == WM_NCHITTEST)
        {
            int x = (short)(lParam & 0xFFFF);
            int y = (short)((lParam >> 16) & 0xFFFF);
            var screenPoint = new Point(x, y);
            var clientPoint = PointFromScreen(screenPoint);

            if (IsOverTitleBar(clientPoint))
            {
                handled = true;
                return (nint)NCHitTest.HTCAPTION;
            }
        }

        return nint.Zero;
    }

    private bool IsOverTitleBar(Point clientPoint)
    {
        if (clientPoint.X < 0 || clientPoint.Y < 0) return false;

        var titleBarBounds = new Rect(0, 0, TitleBar.ActualWidth, TitleBar.ActualHeight);
        if (!titleBarBounds.Contains(clientPoint)) return false;

        var titleElements = TitleBar.InputHitTest(clientPoint) as DependencyObject;
        if (titleElements == null) return true;

        while (titleElements != null)
        {
            if (titleElements is Button or ComboBox or ComboBoxItem or TextBox or CheckBox
                or System.Windows.Controls.Primitives.ToggleButton
                or System.Windows.Controls.Primitives.RepeatButton)
                return false;
            titleElements = System.Windows.Media.VisualTreeHelper.GetParent(titleElements);
        }

        return true;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        var screen = SystemParameters.WorkArea;
        Left = screen.Right - Width - 20;
        Top = (screen.Height - Height) / 2;
    }

    private void ToggleMaximize(object? sender, RoutedEventArgs? e)
    {
        if (_isMaximized)
            LeaveMaximized();
        else
            EnterMaximized();
    }

    private void EnterMaximized()
    {
        _restoreLeft = Left;
        _restoreTop = Top;
        _restoreWidth = Width;
        _restoreHeight = Height;
        _restoreTopmost = Topmost;

        var screen = SystemParameters.WorkArea;

        RootBorder.CornerRadius = new CornerRadius(0);
        RootBorder.BorderThickness = new Thickness(0);
        TitleBar.CornerRadius = new CornerRadius(0);

        Left = screen.Left;
        Top = screen.Top;
        Width = screen.Width;
        Height = screen.Height;

        _isMaximized = true;
    }

    private void LeaveMaximized()
    {
        RootBorder.CornerRadius = new CornerRadius(6);
        RootBorder.BorderThickness = new Thickness(1);
        TitleBar.CornerRadius = new CornerRadius(6, 6, 0, 0);

        Left = _restoreLeft;
        Top = _restoreTop;
        Width = _restoreWidth;
        Height = _restoreHeight;
        Topmost = _restoreTopmost;

        _isMaximized = false;
    }

    private void ToggleTopmost(object sender, RoutedEventArgs e)
    {
        _isTopmost = !_isTopmost;
        Topmost = _isTopmost;
        PinIcon.Text = _isTopmost ? "\uD83D\uDCCC" : "\uD83D\uDD13";
    }

    private void MinimizeMain(object sender, RoutedEventArgs e)
    {
        if (Application.Current.MainWindow is Window main)
            main.WindowState = WindowState.Minimized;
    }

    private void CloseWindow(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
