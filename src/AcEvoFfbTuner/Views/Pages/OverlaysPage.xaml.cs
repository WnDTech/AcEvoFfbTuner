using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace AcEvoFfbTuner.Views.Pages;

public sealed partial class OverlaysPage : UserControl
{
    public OverlaysPage()
    {
        InitializeComponent();
    }

    private void SelectAllOnClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is TextBox tb)
        {
            tb.SelectAll();
            tb.Focus();
        }
    }

    private void CopyUrl(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string name)
        {
            var box = FindName(name) as TextBox;
            if (box != null)
            {
                Clipboard.SetText(box.Text);
                btn.Content = "Copied!";
                var timer = new System.Windows.Threading.DispatcherTimer
                {
                    Interval = TimeSpan.FromSeconds(1.5),
                    IsEnabled = true
                };
                timer.Tick += (s, _) =>
                {
                    btn.Content = "Copy";
                    timer.Stop();
                };
                timer.Start();
            }
        }
    }
}
