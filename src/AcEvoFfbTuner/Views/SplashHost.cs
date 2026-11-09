using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace AcEvoFfbTuner.Views;

/// <summary>
/// Runs the splash screen on its OWN UI thread. Building the view model and
/// the main window blocks the main dispatcher for seconds; with the splash on
/// the same thread its animations froze for every step. Here they keep running
/// at full frame rate while the main thread loads.
/// </summary>
internal sealed class SplashHost
{
    private SplashScreen? _splash;
    private Dispatcher? _dispatcher;

    public static SplashHost Start(string? customSoundPath, string themeName)
    {
        var host = new SplashHost();
        using var ready = new ManualResetEventSlim(false);
        var thread = new Thread(() =>
        {
            try
            {
                host._dispatcher = Dispatcher.CurrentDispatcher;
                var theme = new ResourceDictionary
                {
                    Source = new Uri($"pack://application:,,,/Themes/{themeName}.xaml")
                };
                var splash = new SplashScreen(customSoundPath, theme);
                splash.Closed += (_, _) =>
                    host._dispatcher.BeginInvokeShutdown(DispatcherPriority.Background);
                splash.Show();
                host._splash = splash;
            }
            catch (Exception ex)
            {
                host._splash = null;
                App.WriteCrashLog("SplashHost", ex);
            }
            finally
            {
                ready.Set();
            }
            if (host._splash != null)
                Dispatcher.Run();
        })
        {
            IsBackground = true,
            Name = "SplashUI",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        ready.Wait(TimeSpan.FromSeconds(5));
        return host;
    }

    public void Report(double fraction, string status, double upTo = -1)
    {
        var s = _splash;
        s?.Dispatcher.BeginInvoke(() => s.Report(fraction, status, upTo));
    }

    public Task FinishAsync()
    {
        var s = _splash;
        if (s == null) return Task.CompletedTask;
        try { return s.Dispatcher.InvokeAsync(s.FinishAsync).Task.Unwrap(); }
        catch { return Task.CompletedTask; }
    }

    public void Close()
    {
        var s = _splash;
        _splash = null;
        try { s?.Dispatcher.BeginInvoke(s.Close); } catch { }
    }
}
