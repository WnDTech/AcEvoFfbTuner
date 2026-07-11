using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using AcEvoFfbTuner.Core;

namespace AcEvoFfbTuner.Views.Pages;

public sealed partial class OverlaysPage : UserControl
{
    private bool _loaded;

    public OverlaysPage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        IsVisibleChanged += (_, _) => { if (IsVisible) UpdateNetworkStatus(); };
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _loaded = false;
        var ips = FfbLiveServer.GetLocalNetworkAddresses();
        foreach (var combo in new[] { SourceDashboard, SourceOverlay, SourceClipping })
        {
            combo.Items.Clear();
            combo.Items.Add("localhost");
            foreach (var ip in ips)
                combo.Items.Add(ip);
            combo.SelectedIndex = 0;
        }
        _loaded = true;
        UpdateUrl(UrlDashboard, SourceDashboard, "/?theme=dark");
        UpdateUrl(UrlOverlay, SourceOverlay, "/overlay");
        UpdateUrl(UrlClipping, SourceClipping, "/?theme=clipping");

        UpdateNetworkStatus();
    }

    private void UpdateNetworkStatus()
    {
        var server = App.ViewModel.TelemetryLoop.LiveServer;
        if (server.IsRunning)
        {
            if (server.IsNetworkEnabled)
            {
                NetworkStatusLine.Text = "Server is running and accepting connections on all network interfaces.";
                NetworkStatusLine.Foreground = new SolidColorBrush(Color.FromRgb(0x4C, 0xAF, 0x50));
                NetworkDetailLine.Text = "Port 8321 is accessible via all active LAN IPs.";
                NetworkBtn.Visibility = Visibility.Collapsed;
                NetworkResult.Text = "";
            }
            else
            {
                NetworkStatusLine.Text = "Server running on localhost only — remote devices cannot connect.";
                NetworkStatusLine.Foreground = Brushes.Orange;
                NetworkDetailLine.Text = "Click below to configure URL ACL and firewall rule for network access, then restart telemetry.";
                NetworkBtn.Visibility = Visibility.Visible;
                NetworkResult.Text = "";
            }
        }
        else
        {
            NetworkStatusLine.Text = "Server is not running. Start telemetry first.";
            NetworkStatusLine.Foreground = Brushes.Red;
            NetworkDetailLine.Text = "";
            NetworkBtn.Visibility = Visibility.Collapsed;
            NetworkResult.Text = "";
        }
    }

    private static string GetSource(ComboBox combo)
    {
        var text = combo.Text?.Trim();
        return !string.IsNullOrEmpty(text) ? text : "localhost";
    }

    private static void UpdateUrl(TextBox urlBox, ComboBox combo, string path)
    {
        var source = GetSource(combo);
        var baseUrl = source == "localhost" ? "http://localhost:8321" : $"http://{source}:8321";
        urlBox.Text = $"{baseUrl}{path}";
    }

    private void OnDashboardSourceChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_loaded) return;
        UpdateUrl(UrlDashboard, SourceDashboard, "/?theme=dark");
    }

    private void OnDashboardSourceLostFocus(object sender, RoutedEventArgs e)
    {
        if (!_loaded) return;
        UpdateUrl(UrlDashboard, SourceDashboard, "/?theme=dark");
    }

    private void OnOverlaySourceChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_loaded) return;
        UpdateUrl(UrlOverlay, SourceOverlay, "/overlay");
    }

    private void OnOverlaySourceLostFocus(object sender, RoutedEventArgs e)
    {
        if (!_loaded) return;
        UpdateUrl(UrlOverlay, SourceOverlay, "/overlay");
    }

    private void OnClippingSourceChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_loaded) return;
        UpdateUrl(UrlClipping, SourceClipping, "/?theme=clipping");
    }

    private void OnClippingSourceLostFocus(object sender, RoutedEventArgs e)
    {
        if (!_loaded) return;
        UpdateUrl(UrlClipping, SourceClipping, "/?theme=clipping");
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

    private async void OnEnableNetworkAccess(object sender, RoutedEventArgs e)
    {
        NetworkBtn.IsEnabled = false;
        NetworkResult.Text = "Requesting elevation...";
        NetworkResult.Foreground = new SolidColorBrush(Color.FromRgb(0x60, 0x7D, 0x8B));

        var exePath = Environment.ProcessPath ?? Assembly.GetEntryAssembly()?.Location;
        if (string.IsNullOrEmpty(exePath))
        {
            NetworkResult.Text = "Could not determine app path.";
            NetworkResult.Foreground = Brushes.Red;
            NetworkBtn.IsEnabled = true;
            return;
        }

        var args = $"/c netsh http add urlacl url=http://+:8321/ user=Users && " +
                   $"netsh advfirewall firewall add rule name=\"ACE FFB Tuner\" dir=in action=allow protocol=tcp localport=8321 program=\"{exePath}\" profile=private,public";

        var psi = new ProcessStartInfo("cmd.exe")
        {
            Arguments = args,
            Verb = "runas",
            UseShellExecute = true,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        };

        try
        {
            var process = Process.Start(psi);
            if (process == null)
            {
                NetworkResult.Text = "Failed to start elevated command prompt.";
                NetworkResult.Foreground = Brushes.Red;
            }
            else
            {
                await process.WaitForExitAsync();
                if (process.ExitCode == 0)
                {
                    NetworkResult.Text = "Network access configured. Restart telemetry (Stop/Start) for changes to take effect.";
                    NetworkResult.Foreground = new SolidColorBrush(Color.FromRgb(0x4C, 0xAF, 0x50));
                    NetworkBtn.Visibility = Visibility.Collapsed;
                    if (Window.GetWindow(this) is MainWindow mw)
                        mw.ShowToast("Network", "URL ACL and firewall rule added for port 8321. Restart telemetry.");
                }
                else
                {
                    NetworkResult.Text = "Setup failed (already configured or was denied elevation).";
                    NetworkResult.Foreground = Brushes.Orange;
                }
            }
        }
        catch (Exception ex)
        {
            NetworkResult.Text = $"Error: {ex.Message}";
            NetworkResult.Foreground = Brushes.Red;
        }
        finally
        {
            NetworkBtn.IsEnabled = true;
        }
    }
}
