using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using AcEvoFfbTuner.Core.TrackMapping;
using AcEvoFfbTuner.ViewModels;
using AcEvoFfbTuner.Views.Pages;

namespace AcEvoFfbTuner.Views;

public partial class MainWindow : Window
{
    public static readonly RoutedCommand OpenIconPreviewCommand = new();

    private IconPreviewWindow? _iconPreview;
    private TestingGuideOverlay? _guideOverlay;
    private ProfilerOverlay? _profilerOverlay;
    private TrackMapPopout? _trackMapPopout;
    private CalibrationWizardOverlay? _calibrationWizard;

    public MainWindow()
    {
        InitializeComponent();

        DataContext = App.ViewModel;

        CommandBindings.Add(new CommandBinding(OpenIconPreviewCommand, OpenIconPreview));

        Sidebar.NavigateRequested += OnNavigateRequested;
        Sidebar.SettingsRequested += OnSettingsRequested;

        TelemetryPageCtrl.ProfilerOverlayRequested += OnProfilerOverlayRequested;
        TrackMapPageCtrl.TrackMapPopoutRequested += OnTrackMapPopoutRequested;
        SettingsPageCtrl.TestingGuideRequested += OnTestingGuideRequested;
        SettingsPageCtrl.CalibrationWizardRequested += OnCalibrationWizardRequested;

        ((MainViewModel)DataContext).PropertyChanged += (s, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.CurrentPage))
                UpdatePageVisibility();
        };
    }

    private void OnNavigateRequested(NavPage page)
    {
        if (DataContext is MainViewModel vm)
            vm.CurrentPage = page;
    }

    private void OnSettingsRequested()
    {
        if (DataContext is MainViewModel vm)
            vm.CurrentPage = NavPage.Settings;
    }

    private void UpdatePageVisibility()
    {
        if (DataContext is not MainViewModel vm) return;

        HomePageCtrl.Visibility = vm.CurrentPage == NavPage.Home ? Visibility.Visible : Visibility.Collapsed;
        FfbTuningPageCtrl.Visibility = vm.CurrentPage == NavPage.FfbTuning ? Visibility.Visible : Visibility.Collapsed;
        EqualizerPageCtrl.Visibility = vm.CurrentPage == NavPage.Equalizer ? Visibility.Visible : Visibility.Collapsed;
        TrackMapPageCtrl.Visibility = vm.CurrentPage == NavPage.TrackMap ? Visibility.Visible : Visibility.Collapsed;
        TelemetryPageCtrl.Visibility = vm.CurrentPage == NavPage.Telemetry ? Visibility.Visible : Visibility.Collapsed;
        DevicesPageCtrl.Visibility = vm.CurrentPage == NavPage.Devices ? Visibility.Visible : Visibility.Collapsed;
        ProfilesPageCtrl.Visibility = vm.CurrentPage == NavPage.Profiles ? Visibility.Visible : Visibility.Collapsed;
        SettingsPageCtrl.Visibility = vm.CurrentPage == NavPage.Settings ? Visibility.Visible : Visibility.Collapsed;

        Sidebar.SetSelected(vm.CurrentPage);
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        _guideOverlay?.Close();
        _profilerOverlay?.Close();
        _trackMapPopout?.Close();
        _calibrationWizard?.Close();
        _iconPreview?.Close();
        Application.Current.Shutdown();
    }

    public void UpdateCalibrationWizard(float speedKmh, float mainForce, bool isClipping)
    {
        _calibrationWizard?.UpdateLiveValues(speedKmh, mainForce, isClipping);
    }

    public void UpdateProfiler(float speed, float steerAngle, float forceOut, float rawFF,
        float compress, float slip, float damping, float dynEff,
        float mzFront, float fxFront, float fyFront, float lut, bool clipping,
        float gasInput, float brakeInput)
    {
        TelemetryPageCtrl.UpdateProfiler(speed, steerAngle, forceOut, rawFF,
            compress, slip, damping, dynEff,
            mzFront, fxFront, fyFront, lut, clipping,
            gasInput, brakeInput);
    }

    public void UpdateTrackMapDisplay(float carX, float carZ, float heading, float speedKmh,
        bool isOnTrack, float trackProgress, float distanceFromCenter,
        float trackLengthM, int waypointCount, bool isRecording, bool hasMap,
        TrackMap? currentMap,
        WaypointForceSample[]? forceHeatmap = null,
        bool showHeatmap = false,
        bool showTrackEdges = false,
        WaypointDiagnosticSample[]? diagnosticHeatmap = null,
        bool showDiagnostics = false)
    {
        TrackMapPageCtrl.UpdateTrackMapDisplay(carX, carZ, heading, speedKmh,
            isOnTrack, trackProgress, distanceFromCenter,
            trackLengthM, waypointCount, isRecording, hasMap,
            currentMap, forceHeatmap, showHeatmap,
            showTrackEdges, diagnosticHeatmap, showDiagnostics);
    }

    public string AutoSaveSnapshot()
    {
        return TelemetryPageCtrl.AutoSaveSnapshot();
    }

    private void OpenGuideOverlay(object sender, RoutedEventArgs e)
    {
        OpenTestingGuide();
    }

    private void OnTestingGuideRequested(object? sender, EventArgs e)
    {
        OpenTestingGuide();
    }

    private void OpenTestingGuide()
    {
        if (_guideOverlay != null)
        {
            _guideOverlay.Activate();
            return;
        }

        _guideOverlay = new TestingGuideOverlay();
        _guideOverlay.Closed += (_, _) => _guideOverlay = null;
        _guideOverlay.Show();
    }

    private void OnProfilerOverlayRequested(object? sender, EventArgs e)
    {
        if (_profilerOverlay != null)
        {
            _profilerOverlay.Activate();
            return;
        }

        _profilerOverlay = new ProfilerOverlay();
        _profilerOverlay.Closed += (_, _) => _profilerOverlay = null;
        TelemetryPageCtrl.SetProfilerOverlay(_profilerOverlay);
        _profilerOverlay.Show();
    }

    private void OpenProfilerOverlay(object sender, RoutedEventArgs e)
    {
        OnProfilerOverlayRequested(sender, e);
    }

    private void OnTrackMapPopoutRequested(object? sender, EventArgs e)
    {
        if (_trackMapPopout != null)
        {
            _trackMapPopout.Activate();
            return;
        }

        _trackMapPopout = new TrackMapPopout();
        _trackMapPopout.Closed += (_, _) => _trackMapPopout = null;
        _trackMapPopout.Show();
    }

    private void OpenTrackMapPopout(object sender, RoutedEventArgs e)
    {
        OnTrackMapPopoutRequested(sender, e);
    }

    private void OnCalibrationWizardRequested(object? sender, EventArgs e)
    {
        OpenCalibrationWizard();
    }

    private void OpenCalibrationWizard(object sender, RoutedEventArgs e)
    {
        OpenCalibrationWizard();
    }

    private void OpenCalibrationWizard()
    {
        if (_calibrationWizard != null)
        {
            _calibrationWizard.Activate();
            return;
        }

        if (DataContext is not ViewModels.MainViewModel vm) return;

        _calibrationWizard = new CalibrationWizardOverlay(
            vm.Pipeline,
            vm.DeviceManager,
            vm.TelemetryLoop,
            () => vm.SaveCurrentProfileCommand.Execute(null));

        _calibrationWizard.InitializeSlidersFromPipeline();
        _calibrationWizard.Closed += (_, _) => _calibrationWizard = null;
        _calibrationWizard.Show();
    }

    private void CopyDebugToClipboard(object sender, RoutedEventArgs e)
    {
        if (DataContext is ViewModels.MainViewModel vm)
        {
            Clipboard.SetText(vm.DebugSnapshot);
            vm.StatusText = "Debug info copied to clipboard";
        }
    }

    private void OpenIconPreview(object sender, RoutedEventArgs e)
    {
        if (_iconPreview != null)
        {
            _iconPreview.Activate();
            return;
        }

        _iconPreview = new IconPreviewWindow { Owner = this };
        _iconPreview.Closed += (_, _) => _iconPreview = null;
        _iconPreview.Show();
    }

    private void OnConnectionPillClick(object sender, RoutedEventArgs e)
    {
        if (DevicePopup.IsOpen)
        {
            DevicePopup.IsOpen = false;
            return;
        }

        if (DataContext is ViewModels.MainViewModel vm)
            vm.RefreshDevicesCommand.Execute(null);

        DevicePopup.IsOpen = true;
    }

    private void OnDevicePopupOpened(object sender, EventArgs e)
    {
        UpdateDeviceEmptyVisibility();
    }

    private void OnDeviceRefreshClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is ViewModels.MainViewModel vm)
            vm.RefreshDevicesCommand.Execute(null);
        UpdateDeviceEmptyVisibility();
    }

    private void OnDeviceListSelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (e.AddedItems.Count == 0) return;
        if (DataContext is ViewModels.MainViewModel vm)
        {
            vm.SelectedDevice = e.AddedItems[0] as Core.DirectInput.FfbDeviceInfo;
            vm.ConnectDeviceCommand.Execute(null);
        }
        DevicePopup.IsOpen = false;
    }

    private void UpdateDeviceEmptyVisibility()
    {
        if (DeviceList.Items.Count == 0)
            DeviceEmptyText.Visibility = Visibility.Visible;
        else
            DeviceEmptyText.Visibility = Visibility.Collapsed;
    }

    private void OnProfilePillClick(object sender, RoutedEventArgs e)
    {
        if (ProfilePopup.IsOpen)
        {
            ProfilePopup.IsOpen = false;
            return;
        }
        ProfilePopup.IsOpen = true;
    }

    private void OnProfilePopupOpened(object sender, EventArgs e)
    {
        ProfileEmptyText.Visibility = ProfileList.Items.Count == 0
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void OnProfileListSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.AddedItems.Count > 0)
            ProfilePopup.IsOpen = false;
    }

    private void OnDonateClick(object sender, RoutedEventArgs e)
    {
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("https://paypal.me/willndad") { UseShellExecute = true });
    }
}
