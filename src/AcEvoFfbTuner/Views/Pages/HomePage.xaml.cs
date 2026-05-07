using System.Windows;
using System.Windows.Controls;

namespace AcEvoFfbTuner.Views.Pages;

public partial class HomePage : UserControl
{
    public HomePage()
    {
        InitializeComponent();
    }

    private void OnDismissQuickStart(object sender, RoutedEventArgs e)
    {
        QuickStartCard.Visibility = Visibility.Collapsed;
    }
}
