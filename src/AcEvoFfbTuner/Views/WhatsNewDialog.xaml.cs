using System.Windows;
using System.Windows.Controls;
using AcEvoFfbTuner.Services;

namespace AcEvoFfbTuner.Views;

public partial class WhatsNewDialog : Window
{
    public bool ShowOnStartup { get; private set; } = true;
    public bool ShowAllEntries { get; set; }

    public WhatsNewDialog()
    {
        InitializeComponent();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        var entries = ShowAllEntries
            ? ChangeLogService.Entries
            : ChangeLogService.GetEntriesSince(App.Settings.LastSeenVersion);

        if (entries.Count == 0)
        {
            VersionLabel.Text = $"Release Notes (v{ChangeLogService.CurrentVersion})";
        }
        else if (entries.Count == 1)
        {
            VersionLabel.Text = $"What's New in v{entries[0].Version}";
        }
        else
        {
            VersionLabel.Text = $"What's New — {entries.Count} Releases";
        }

        foreach (var entry in entries)
        {
            ContentPanel.Children.Add(CreateEntryPanel(entry));
        }
    }

    private StackPanel CreateEntryPanel(ChangeLogEntry entry)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 0, 0, 20) };

        var header = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 0, 0, 8)
        };

        var versionBadge = new Border
        {
            Background = new System.Windows.Media.SolidColorBrush(
                (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#FFE67E22")),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(10, 4, 10, 4),
            Margin = new Thickness(0, 0, 10, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        var versionText = new TextBlock
        {
            Text = $"v{entry.Version}",
            FontSize = 14,
            FontWeight = FontWeights.Bold,
            Foreground = System.Windows.Media.Brushes.White,
            VerticalAlignment = VerticalAlignment.Center
        };
        versionBadge.Child = versionText;
        header.Children.Add(versionBadge);

        if (entry.Date != default)
        {
            var dateText = new TextBlock
            {
                Text = entry.Date.ToString("MMM d, yyyy"),
                FontSize = 13,
                Foreground = new System.Windows.Media.SolidColorBrush(
                    (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#FF888888")),
                VerticalAlignment = VerticalAlignment.Center
            };
            header.Children.Add(dateText);
        }

        panel.Children.Add(header);

        if (!string.IsNullOrEmpty(entry.Title))
        {
            panel.Children.Add(new TextBlock
            {
                Text = entry.Title,
                FontSize = 16,
                FontWeight = FontWeights.SemiBold,
                Foreground = System.Windows.Media.Brushes.White,
                Margin = new Thickness(0, 0, 0, 10),
                TextWrapping = TextWrapping.Wrap
            });
        }

        if (entry.Features.Count > 0)
            panel.Children.Add(CreateSection("New Features", entry.Features, "#FF00E676"));

        if (entry.Improvements.Count > 0)
            panel.Children.Add(CreateSection("Improvements", entry.Improvements, "#FF4FC3F7"));

        if (entry.Fixes.Count > 0)
            panel.Children.Add(CreateSection("Bug Fixes", entry.Fixes, "#FFFFD600"));

        return panel;
    }

    private StackPanel CreateSection(string title, List<string> items, string accentColor)
    {
        var section = new StackPanel { Margin = new Thickness(0, 0, 0, 6) };

        section.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            Foreground = new System.Windows.Media.SolidColorBrush(
                (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(accentColor)),
            Margin = new Thickness(0, 6, 0, 4)
        });

        foreach (var item in items)
        {
            var itemPanel = new Grid { Margin = new Thickness(0, 2, 0, 2) };
            itemPanel.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            itemPanel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var bullet = new TextBlock
            {
                Text = "\u2022",
                FontSize = 14,
                Foreground = new System.Windows.Media.SolidColorBrush(
                    (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(accentColor)),
                Margin = new Thickness(8, 0, 8, 0),
                VerticalAlignment = VerticalAlignment.Top
            };
            Grid.SetColumn(bullet, 0);
            itemPanel.Children.Add(bullet);

            var itemText = new TextBlock
            {
                Text = item,
                FontSize = 13,
                Foreground = new System.Windows.Media.SolidColorBrush(
                    (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#FFD0D0D0")),
                TextWrapping = TextWrapping.Wrap
            };
            Grid.SetColumn(itemText, 1);
            itemPanel.Children.Add(itemText);

            section.Children.Add(itemPanel);
        }

        return section;
    }

    private void OnContinue(object sender, RoutedEventArgs e)
    {
        ShowOnStartup = ShowOnStartupCheck.IsChecked == true;
        DialogResult = true;
        Close();
    }
}
