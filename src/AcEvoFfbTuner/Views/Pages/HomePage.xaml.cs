using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;

namespace AcEvoFfbTuner.Views.Pages;

public partial class HomePage : UserControl
{
    private static readonly string[] DashboardWheelUris =
    {
        "pack://application:,,,/Resources/splash-wheels/MOZA-KS-PRO_1.png",
        "pack://application:,,,/Resources/splash-wheels/FanCSLElite.png",
        "pack://application:,,,/Resources/splash-wheels/GPro.png",
        "pack://application:,,,/Resources/splash-wheels/G27.png",
    };

    public HomePage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        var idx = Random.Shared.Next(DashboardWheelUris.Length);
        DashboardWheelImage.Source = new BitmapImage(new Uri(DashboardWheelUris[idx]));
    }

    private void OnDismissQuickStart(object sender, RoutedEventArgs e)
    {
        QuickStartCard.Visibility = Visibility.Collapsed;
    }
}
