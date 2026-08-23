using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using AcEvoFfbTuner.Controls;
using AcEvoFfbTuner.Core;

namespace AcEvoFfbTuner.Views.Pages;

public sealed partial class OverlaysPage : UserControl
{
    private bool _loaded;
    private readonly Dictionary<string, SectionCard> _cardsByTag = new();
    private readonly Dictionary<string, FrameworkElement> _panels = new();

    public OverlaysPage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        IsVisibleChanged += (_, _) => { if (IsVisible) UpdateNetworkStatus(); };
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        CacheCards();
        BuildPanelMap();
        ShowSection("Dashboard");

        _loaded = false;
        var ips = FfbLiveServer.GetLocalNetworkAddresses();
        var activeIp = FfbLiveServer.GetActiveNetworkAddress();
        foreach (var combo in new[] { SourceDashboard, SourceOverlay, SourceClipping, SourceBuilder })
        {
            combo.Items.Clear();
            combo.Items.Add("localhost");
            int sel = 0;
            foreach (var ip in ips)
            {
                combo.Items.Add(ip);
                if (ip == activeIp) sel = combo.Items.Count - 1;
            }
            combo.SelectedIndex = sel;
        }
        _loaded = true;
        UpdateUrl(UrlDashboard, SourceDashboard, "/?theme=dark");
        UpdateUrl(UrlOverlay, SourceOverlay, "/overlay");
        UpdateUrl(UrlClipping, SourceClipping, "/?theme=clipping");
        UpdateBuilderUrl();

        UpdateNetworkStatus();
    }

    private void CacheCards()
    {
        foreach (var card in FindSectionCards(this))
        {
            if (card.Tag is string tag && !string.IsNullOrEmpty(tag) && !_cardsByTag.ContainsKey(tag))
            {
                _cardsByTag[tag] = card;
                card.Selected += OnCardSelected;
            }
        }
    }

    private void BuildPanelMap()
    {
        _panels["Dashboard"] = DtlDashboard;
        _panels["Streamer"] = DtlStreamer;
        _panels["Builder"] = DtlBuilder;
        _panels["Clipping"] = DtlClipping;
        _panels["Setup"] = DtlSetup;
        _panels["Network"] = DtlNetwork;
    }

    private void OnCardSelected(object sender, RoutedEventArgs e)
    {
        if (sender is SectionCard card && card.Tag is string tag)
            ShowSection(tag);
    }

    private void ShowSection(string tag)
    {
        if (!_cardsByTag.TryGetValue(tag, out var card)) return;
        if (!_panels.TryGetValue(tag, out var panel)) return;

        foreach (var kv in _panels)
            kv.Value.Visibility = kv.Key == tag ? Visibility.Visible : Visibility.Collapsed;

        foreach (var kv in _cardsByTag)
            kv.Value.IsSelected = kv.Key == tag;

        DetailHeader.Text = card.Title;
        var brush = card.SectionBrush ?? (Brush)FindResource("SectionOutput");
        DetailHeader.Foreground = brush;
        DetailHeaderAccent.Background = brush;
    }

    private static IEnumerable<SectionCard> FindSectionCards(DependencyObject parent)
    {
        foreach (var child in LogicalTreeHelper.GetChildren(parent))
        {
            if (child is SectionCard sectionCard)
                yield return sectionCard;

            if (child is DependencyObject depChild)
            {
                foreach (var nested in FindSectionCards(depChild))
                    yield return nested;
            }
        }
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

    private static readonly string[] BuilderModNames =
        { "speed", "force", "waveform", "track", "pedals", "tires", "gforce" };

    private void UpdateBuilderUrl()
    {
        var boxes = new[] { BxSpeed, BxForce, BxWaveform, BxTrack, BxPedals, BxTires, BxGforce };
        var selected = new List<string>();
        for (int i = 0; i < boxes.Length; i++)
            if (boxes[i].IsChecked == true) selected.Add(BuilderModNames[i]);
        var path = selected.Count == 0
            ? "/overlay?mods=none"
            : "/overlay?mods=" + string.Join(",", selected);
        UpdateUrl(UrlBuilder, SourceBuilder, path);
    }

    private void OnBuilderChanged(object sender, RoutedEventArgs e)
    {
        if (!_loaded) return;
        UpdateBuilderUrl();
    }

    private void OnBuilderSourceChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_loaded) return;
        UpdateBuilderUrl();
    }

    private void OnBuilderSourceLostFocus(object sender, RoutedEventArgs e)
    {
        if (!_loaded) return;
        UpdateBuilderUrl();
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
