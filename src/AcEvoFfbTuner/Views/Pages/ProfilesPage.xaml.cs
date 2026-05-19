using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using AcEvoFfbTuner.Core.Profiles;
using AcEvoFfbTuner.ViewModels;

namespace AcEvoFfbTuner.Views.Pages;

public partial class ProfilesPage : UserControl
{
    private MainViewModel? _vm;

    private static readonly SolidColorBrush BrushAccent = new(System.Windows.Media.Color.FromRgb(0xF0, 0x88, 0x3E));
    private static readonly SolidColorBrush BrushAccentFaint = new(System.Windows.Media.Color.FromArgb(0x30, 0xF0, 0x88, 0x3E));
    private static readonly SolidColorBrush BrushHeaderBg = new(System.Windows.Media.Color.FromRgb(0x21, 0x26, 0x2D));
    private static readonly SolidColorBrush BrushMuted = new(System.Windows.Media.Color.FromRgb(0x8B, 0x94, 0x9E));
    private static readonly SolidColorBrush BrushBadgeBg = new(System.Windows.Media.Color.FromRgb(0x30, 0x36, 0x3D));
    private static readonly SolidColorBrush BrushForeground = new(System.Windows.Media.Color.FromRgb(0xE6, 0xED, 0xF3));
    private static readonly SolidColorBrush BrushHover = new(System.Windows.Media.Color.FromRgb(0x2D, 0x33, 0x3B));
    private static readonly SolidColorBrush BrushWarningBadgeBg = new(System.Windows.Media.Color.FromRgb(0x3D, 0x2E, 0x00));
    private static readonly SolidColorBrush BrushWarningFg = new(System.Windows.Media.Color.FromRgb(0xF0, 0xAD, 0x4E));

    public ProfilesPage()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_vm != null)
        {
            _vm.Profiles.CollectionChanged -= OnProfilesChanged;
            _vm.PropertyChanged -= OnVmPropertyChanged;
        }

        _vm = DataContext as MainViewModel;

        if (_vm != null)
        {
            _vm.Profiles.CollectionChanged += OnProfilesChanged;
            _vm.PropertyChanged += OnVmPropertyChanged;
            RebuildBrowser();
        }
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.SelectedProfile))
            RebuildBrowser();
    }

    private void OnProfilesChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        RebuildBrowser();
    }

    private void OnViewModeChanged(object sender, RoutedEventArgs e)
    {
        if (_vm == null) return;

        if (ReferenceEquals(sender, ViewList))
            _vm.ProfileOrganisationMode = 0;
        else if (ReferenceEquals(sender, ViewByTrack))
            _vm.ProfileOrganisationMode = 1;
        else if (ReferenceEquals(sender, ViewByCar))
            _vm.ProfileOrganisationMode = 2;

        RebuildBrowser();
    }

    private void RebuildBrowser()
    {
        if (_vm == null || ProfileBrowserPanel == null) return;

        ProfileBrowserPanel.Children.Clear();

        if (_vm.ProfileOrganisationMode == 0)
            BuildFlatList();
        else if (_vm.ProfileOrganisationMode == 1)
            BuildGroupedList(groupByTrack: true);
        else
            BuildGroupedList(groupByTrack: false);
    }

    private void BuildFlatList()
    {
        foreach (var profile in _vm!.Profiles)
            ProfileBrowserPanel.Children.Add(CreateProfileItem(profile));

        if (_vm.Profiles.Count == 0)
            ProfileBrowserPanel.Children.Add(CreateEmptyMessage("No profiles found."));
    }

    private void BuildGroupedList(bool groupByTrack)
    {
        var profiles = _vm!.Profiles;

        IOrderedEnumerable<IGrouping<string, FfbProfile>> groups;
        if (groupByTrack)
            groups = profiles.GroupBy(p => string.IsNullOrEmpty(p.TrackMatch) ? "Unassigned" : p.TrackMatch)
                             .OrderBy(g => g.Key == "Unassigned" ? "zzz" : g.Key);
        else
            groups = profiles.GroupBy(p => string.IsNullOrEmpty(p.CarMatch) ? "Unassigned" : p.CarMatch)
                             .OrderBy(g => g.Key == "Unassigned" ? "zzz" : g.Key);

        bool hasItems = false;
        foreach (var group in groups)
        {
            hasItems = true;

            var chevron = new TextBlock
            {
                Text = "\uE70E",
                FontFamily = new System.Windows.Media.FontFamily("Segoe MDL2 Assets"),
                FontSize = 11,
                Foreground = BrushMuted,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0),
                RenderTransform = new RotateTransform(90),
                RenderTransformOrigin = new Point(0.5, 0.5)
            };

            var headerPanel = new StackPanel { Orientation = Orientation.Horizontal };

            var groupIcon = new TextBlock
            {
                Text = groupByTrack ? "\uE8C4" : "\uE804",
                FontFamily = new System.Windows.Media.FontFamily("Segoe MDL2 Assets"),
                FontSize = 13,
                Foreground = BrushAccent,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 6, 0)
            };

            var headerText = new TextBlock
            {
                Text = group.Key,
                FontSize = 13,
                FontWeight = FontWeights.Bold,
                Foreground = BrushForeground,
                VerticalAlignment = VerticalAlignment.Center
            };

            var countText = new TextBlock
            {
                Text = $"({group.Count()})",
                FontSize = 11,
                Foreground = BrushMuted,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(6, 0, 0, 0)
            };

            headerPanel.Children.Add(chevron);
            headerPanel.Children.Add(groupIcon);
            headerPanel.Children.Add(headerText);
            headerPanel.Children.Add(countText);

            var headerBorder = new Border
            {
                Background = BrushHeaderBg,
                Padding = new Thickness(10, 6, 10, 6),
                Margin = new Thickness(0, 8, 0, 0),
                CornerRadius = new CornerRadius(4),
                Cursor = Cursors.Hand,
                Child = headerPanel
            };

            ProfileBrowserPanel.Children.Add(headerBorder);

            var childrenPanel = new StackPanel();
            foreach (var profile in group)
                childrenPanel.Children.Add(CreateProfileItem(profile, isNested: true));

            ProfileBrowserPanel.Children.Add(childrenPanel);

            bool collapsed = false;
            headerBorder.MouseLeftButtonUp += (_, _) =>
            {
                collapsed = !collapsed;
                childrenPanel.Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible;
                chevron.RenderTransform = new RotateTransform(collapsed ? 0 : 90);
            };
        }

        if (!hasItems)
            ProfileBrowserPanel.Children.Add(CreateEmptyMessage("No profiles found."));
    }

    private Border CreateProfileItem(FfbProfile profile, bool isNested = false)
    {
        bool isActive = _vm!.SelectedProfile == profile;

        var border = new Border
        {
            Padding = new Thickness(isNested ? 28 : 12, 8, 12, 8),
            Background = isActive ? BrushAccentFaint : Brushes.Transparent,
            BorderBrush = isActive ? BrushAccent : Brushes.Transparent,
            BorderThickness = new Thickness(1, 0, 0, 0),
            Cursor = Cursors.Hand,
            Tag = profile,
            Margin = new Thickness(0, 0, 0, 1)
        };

        var panel = new DockPanel { LastChildFill = true };

        if (profile.IsBuiltIn)
        {
            var badge = CreateBadge("BUILT-IN", BrushBadgeBg, BrushMuted);
            DockPanel.SetDock(badge, Dock.Right);
            panel.Children.Add(badge);
        }

        if (profile.NeedsMigration)
        {
            var badge = new Border
            {
                Background = BrushWarningBadgeBg,
                CornerRadius = new CornerRadius(3),
                Padding = new Thickness(5, 2, 6, 2),
                Margin = new Thickness(0, 0, 8, 0),
                VerticalAlignment = VerticalAlignment.Center,
                ToolTip = $"Profile format is v{profile.Version}, latest is v{FfbProfile.CurrentVersion}. Enable Auto Upgrade Profiles in Settings to update."
            };
            var badgePanel = new StackPanel { Orientation = Orientation.Horizontal };
            var icon = new TextBlock
            {
                Text = "\uE7BA",
                FontFamily = new System.Windows.Media.FontFamily("Segoe MDL2 Assets"),
                FontSize = 10,
                Foreground = BrushWarningFg,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 4, 0)
            };
            var badgeText = new TextBlock
            {
                Text = $"v{profile.Version}",
                FontSize = 9,
                FontWeight = FontWeights.Bold,
                Foreground = BrushWarningFg,
                VerticalAlignment = VerticalAlignment.Center
            };
            badgePanel.Children.Add(icon);
            badgePanel.Children.Add(badgeText);
            badge.Child = badgePanel;
            DockPanel.SetDock(badge, Dock.Right);
            panel.Children.Add(badge);
        }

        var nameText = new TextBlock
        {
            Text = profile.Name,
            FontSize = 13,
            Foreground = isActive ? BrushAccent : BrushForeground,
            FontWeight = isActive ? FontWeights.SemiBold : FontWeights.Normal,
            VerticalAlignment = VerticalAlignment.Center
        };
        panel.Children.Add(nameText);

        border.Child = panel;

        border.MouseLeftButtonUp += (s, e) =>
        {
            if (_vm != null)
                _vm.SelectedProfile = profile;
            RebuildBrowser();
        };

        border.MouseEnter += (s, e) =>
        {
            if (_vm!.SelectedProfile != profile)
                border.Background = BrushHover;
        };

        border.MouseLeave += (s, e) =>
        {
            if (_vm!.SelectedProfile != profile)
                border.Background = Brushes.Transparent;
        };

        return border;
    }

    private Border CreateBadge(string text, SolidColorBrush bg, SolidColorBrush fg)
    {
        var badge = new Border
        {
            Background = bg,
            CornerRadius = new CornerRadius(3),
            Padding = new Thickness(6, 2, 6, 2),
            Margin = new Thickness(0, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        var badgeText = new TextBlock
        {
            Text = text,
            FontSize = 9,
            FontWeight = FontWeights.Bold,
            Foreground = fg
        };
        badge.Child = badgeText;
        return badge;
    }

    private TextBlock CreateEmptyMessage(string message)
    {
        return new TextBlock
        {
            Text = message,
            FontSize = 13,
            Foreground = BrushMuted,
            Margin = new Thickness(12, 16, 12, 16),
            HorizontalAlignment = HorizontalAlignment.Center
        };
    }
}
