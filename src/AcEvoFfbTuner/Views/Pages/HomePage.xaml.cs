using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AcEvoFfbTuner.ViewModels;

namespace AcEvoFfbTuner.Views.Pages;

public sealed partial class HomePage : UserControl
{
    public event EventHandler? SetupWizardRequested;

    private static readonly string[] DashboardWheelUris =
    [
        "pack://application:,,,/Resources/splash-wheels/MOZA-KS-PRO_1.png",
        "pack://application:,,,/Resources/splash-wheels/FanCSLElite.png",
        "pack://application:,,,/Resources/splash-wheels/GPro.png",
        "pack://application:,,,/Resources/splash-wheels/G27.png",
    ];

    private const double GForceDotCenterX = 50;
    private const double GForceDotCenterY = 50;
    private const double GForceDotHalf = 5;
    private const double GForceRadius = 41;
    private const double GForceMaxG = 2.0;

    private static readonly Brush ForceNormalBrush = new SolidColorBrush(Color.FromRgb(240, 136, 62));
    private static readonly Brush ForceWarningBrush = new SolidColorBrush(Color.FromRgb(255, 167, 38));
    private static readonly Brush ForceClipBrush = new SolidColorBrush(Color.FromRgb(239, 83, 80));

    public HomePage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        var idx = Random.Shared.Next(DashboardWheelUris.Length);
        DashboardWheelImage.Source = new BitmapImage(new Uri(DashboardWheelUris[idx]));

        if (DataContext is MainViewModel vm)
        {
            vm.PropertyChanged += OnViewModelPropertyChanged;
            UpdateForceBar(vm.CurrentForceOutput, vm.IsClipping, vm.MaxGaugeForce);
            UpdateGForceDot(vm.LatG, vm.LongG);
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (DataContext is not MainViewModel vm) return;
        if (e.PropertyName is nameof(MainViewModel.CurrentForceOutput) or nameof(MainViewModel.IsClipping))
            UpdateForceBar(vm.CurrentForceOutput, vm.IsClipping, vm.MaxGaugeForce);
        else if (e.PropertyName is nameof(MainViewModel.LatG) or nameof(MainViewModel.LongG))
            UpdateGForceDot(vm.LatG, vm.LongG);
    }

    private void UpdateForceBar(float force, bool isClipping, float maxForce)
    {
        double pct = Math.Clamp(Math.Abs(force) / maxForce, 0, 1);
        var parent = VisualTreeHelper.GetParent(ForceBarFill) as FrameworkElement;
        double trackWidth = parent?.ActualWidth ?? 200;
        ForceBarFill.Width = pct * trackWidth;
        ForceBarFill.Background = isClipping ? ForceClipBrush : pct < 0.7 ? ForceNormalBrush : ForceWarningBrush;
    }

    private void UpdateGForceDot(float latG, float longG)
    {
        double pxPerG = GForceRadius / GForceMaxG;
        double x = Math.Clamp(latG, -GForceMaxG, GForceMaxG) * pxPerG;
        double y = Math.Clamp(-longG, -GForceMaxG, GForceMaxG) * pxPerG;
        GForceDot.Margin = new Thickness(
            GForceDotCenterX + x - GForceDotHalf,
            GForceDotCenterY + y - GForceDotHalf, 0, 0);
    }

    private void OnLaunchSetupWizard(object sender, RoutedEventArgs e)
    {
        SetupWizardRequested?.Invoke(this, EventArgs.Empty);
    }
}