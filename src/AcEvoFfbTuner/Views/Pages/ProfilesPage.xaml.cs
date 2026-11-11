using AcEvoFfbTuner.Services;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using AcEvoFfbTuner.Controls;
using AcEvoFfbTuner.Core.Profiles;
using AcEvoFfbTuner.ViewModels;

namespace AcEvoFfbTuner.Views.Pages;

public partial class ProfilesPage : UserControl
{
    private MainViewModel? _vm;
    private string _filterGame = "";
    private string _filterSearch = "";
    private readonly Dictionary<string, bool> _groupCollapsedState = new();
    private readonly Dictionary<string, SectionCard> _cardsByTag = new();
    private readonly Dictionary<string, FrameworkElement> _panels = new();
    private static readonly string AppDataPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AcEvoFfbTuner");
    private static readonly string StateFilePath = Path.Combine(AppDataPath, "browser_state.json");

    private static SolidColorBrush BrushAccent => ThemeManager.Brush("#FFF0883E");
    private static SolidColorBrush BrushAccentFaint => ThemeManager.Brush("#30F0883E");
    private static SolidColorBrush BrushHeaderBg => ThemeManager.Brush("#FF21262D");
    private static SolidColorBrush BrushMuted => ThemeManager.Brush("#FF8B949E");
    private static SolidColorBrush BrushBadgeBg => ThemeManager.Brush("#FF30363D");
    private static SolidColorBrush BrushForeground => ThemeManager.Brush("#FFE6EDF3");
    private static SolidColorBrush BrushHover => ThemeManager.Brush("#FF2D333B");
    private static SolidColorBrush BrushWarningBadgeBg => ThemeManager.Brush("#FF3D2E00");
    private static SolidColorBrush BrushWarningFg => ThemeManager.Brush("#FFF0AD4E");

    public ProfilesPage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        DataContextChanged += OnDataContextChanged;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        CacheCards();
        BuildPanelMap();
        ShowSection("Current");
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
        _panels["Current"] = DtlCurrent;
        _panels["Detection"] = DtlDetection;
        _panels["Actions"] = DtlActions;
        _panels["Import"] = DtlImport;
        _panels["Browser"] = DtlBrowser;
        _panels["AbCompare"] = DtlAbCompare;
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
            LoadCollapsedState();
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
        _groupCollapsedState.Clear();
        SaveCollapsedState();
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

    private void OnGameFilterChanged(object sender, RoutedEventArgs e)
    {
        if (ReferenceEquals(sender, GameFilterAll))
            _filterGame = "";
        else if (ReferenceEquals(sender, GameFilterAcEvo))
            _filterGame = "AC EVO";
        else if (ReferenceEquals(sender, GameFilterRaceroom))
            _filterGame = "RaceRoom";
        else if (ReferenceEquals(sender, GameFilterAssettoCorsa))
            _filterGame = "Assetto Corsa";
        else if (ReferenceEquals(sender, GameFilterLeMansUltimate))
            _filterGame = "Le Mans Ultimate";
        else if (ReferenceEquals(sender, GameFilterAcc))
            _filterGame = "AssettoCorsaCompetizione";

        RebuildBrowser();
    }

    private void OnSearchTextChanged(object sender, TextChangedEventArgs e)
    {
        _filterSearch = SearchBox.Text.Trim();
        RebuildBrowser();
    }

    private IEnumerable<FfbProfile> GetFilteredProfiles()
    {
        var profiles = _vm!.Profiles.AsEnumerable();

        if (!string.IsNullOrEmpty(_filterGame))
        {
            if (_filterGame == "AC EVO")
                profiles = profiles.Where(p => string.IsNullOrEmpty(p.GameMatch) || p.GameMatch == "AC EVO");
            else
                profiles = profiles.Where(p => p.GameMatch == _filterGame);
        }

        if (!string.IsNullOrEmpty(_filterSearch))
        {
            var search = _filterSearch.ToLowerInvariant();
            profiles = profiles.Where(p => p.Name.ToLowerInvariant().Contains(search));
        }

        return profiles;
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
        var filtered = GetFilteredProfiles().ToList();
        foreach (var profile in filtered)
            ProfileBrowserPanel.Children.Add(CreateProfileItem(profile));

        if (filtered.Count == 0)
            ProfileBrowserPanel.Children.Add(CreateEmptyMessage("No profiles match your filters."));
    }

    private void BuildGroupedList(bool groupByTrack)
    {
        var profiles = GetFilteredProfiles();

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
                Text = groupByTrack ? "\u2394" : "\uE804",
                FontSize = 13,
                Foreground = BrushAccent,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 6, 0)
            };
            if (!groupByTrack)
                groupIcon.FontFamily = new System.Windows.Media.FontFamily("Segoe MDL2 Assets");

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

            var stateKey = $"{(groupByTrack ? "track" : "car")}::{group.Key}";
            bool collapsed = _groupCollapsedState.TryGetValue(stateKey, out var saved) && saved;
            if (collapsed)
            {
                childrenPanel.Visibility = Visibility.Collapsed;
                chevron.RenderTransform = new RotateTransform(0);
            }

            headerBorder.MouseLeftButtonUp += (_, _) =>
            {
                collapsed = !collapsed;
                childrenPanel.Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible;
                chevron.RenderTransform = new RotateTransform(collapsed ? 0 : 90);
                _groupCollapsedState[stateKey] = collapsed;
                SaveCollapsedState();
            };
        }

        if (!hasItems)
            ProfileBrowserPanel.Children.Add(CreateEmptyMessage("No profiles match your filters."));
    }

    private void LoadCollapsedState()
    {
        try
        {
            if (File.Exists(StateFilePath))
            {
                var json = File.ReadAllText(StateFilePath);
                var loaded = JsonSerializer.Deserialize<Dictionary<string, bool>>(json);
                if (loaded != null)
                {
                    _groupCollapsedState.Clear();
                    foreach (var kv in loaded)
                        _groupCollapsedState[kv.Key] = kv.Value;
                }
            }
        }
        catch
        {
            _groupCollapsedState.Clear();
        }
    }

    private void SaveCollapsedState()
    {
        try
        {
            Directory.CreateDirectory(AppDataPath);
            var json = JsonSerializer.Serialize(_groupCollapsedState);
            File.WriteAllText(StateFilePath, json);
        }
        catch
        {
        }
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
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            ToolTip = profile.Name
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
