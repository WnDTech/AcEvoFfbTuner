using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using AcEvoFfbTuner.ViewModels;

namespace AcEvoFfbTuner.Controls;

public partial class SidebarControl : UserControl
{
    public event Action<NavPage>? NavigateRequested;
    public event Action? SettingsRequested;

    private bool _isCollapsed;

    public SidebarControl()
    {
        InitializeComponent();
    }

    private void OnNavClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.Tag is NavPage page)
            NavigateRequested?.Invoke(page);
    }

    private void OnSettingsClick(object sender, RoutedEventArgs e)
    {
        SettingsRequested?.Invoke();
    }

    private void OnToggleCollapse(object sender, RoutedEventArgs e)
    {
        _isCollapsed = !_isCollapsed;
        ApplyCollapsedState();
    }

    private static T? FindTemplateChild<T>(Control control, string name) where T : class
    {
        return control.Template?.FindName(name, control) as T;
    }

    private void ApplyCollapsedState()
    {
        if (_isCollapsed)
        {
            Root.Width = 60;

            SetTemplateLabelVisibility(SettingsBtn, "SettingsLabel", Visibility.Collapsed);
            SetTemplateLabelVisibility(CollapseBtn, "CollapseLabel", Visibility.Collapsed);
            CollapseBtn.ToolTip = "Expand sidebar";

            foreach (var btn in new[] { NavHomeBtn, NavTuningBtn, NavEqBtn, NavMapBtn, NavLiveMapBtn, NavTelemetryBtn, NavCoachBtn, NavDevicesBtn, NavProfilesBtn })
                SetTemplateLabelVisibility(btn, "ItemLabel", Visibility.Collapsed);

            if (CollapseBtn.Template.FindName("CollapseArrow", CollapseBtn) is System.Windows.Shapes.Path arrow)
                arrow.Data = PathGeometry.Parse("M9,5 L16,12 L9,19");
        }
        else
        {
            Root.Width = 200;

            SetTemplateLabelVisibility(SettingsBtn, "SettingsLabel", Visibility.Visible);
            SetTemplateLabelVisibility(CollapseBtn, "CollapseLabel", Visibility.Visible);
            CollapseBtn.ToolTip = null;

            foreach (var btn in new[] { NavHomeBtn, NavTuningBtn, NavEqBtn, NavMapBtn, NavLiveMapBtn, NavTelemetryBtn, NavCoachBtn, NavDevicesBtn, NavProfilesBtn })
                SetTemplateLabelVisibility(btn, "ItemLabel", Visibility.Visible);

            if (CollapseBtn.Template.FindName("CollapseArrow", CollapseBtn) is System.Windows.Shapes.Path arrow)
                arrow.Data = PathGeometry.Parse("M15,19 L8,12 L15,5");
        }
    }

    private static void SetTemplateLabelVisibility(Control control, string name, Visibility visibility)
    {
        if (control.Template?.FindName(name, control) is FrameworkElement fe)
            fe.Visibility = visibility;
    }

    public void SetSelected(NavPage page)
    {
        NavHomeBtn.IsChecked = page == NavPage.Home;
        NavTuningBtn.IsChecked = page == NavPage.FfbTuning;
        NavEqBtn.IsChecked = page == NavPage.Equalizer;
        NavMapBtn.IsChecked = page == NavPage.TrackMap;
        NavLiveMapBtn.IsChecked = page == NavPage.LiveTrackMap;
        NavTelemetryBtn.IsChecked = page == NavPage.Telemetry;
        NavCoachBtn.IsChecked = page == NavPage.FfbCoach;
        NavDevicesBtn.IsChecked = page == NavPage.Devices;
        NavProfilesBtn.IsChecked = page == NavPage.Profiles;
    }
}
