using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using AcEvoFfbTuner.Services;
using AcEvoFfbTuner.ViewModels;

namespace AcEvoFfbTuner.Views.Pages;

public partial class HubPage : UserControl
{
    private MainViewModel? _vm;
    private bool _loadedOnce;
    private bool _loading;
    private bool _reloadQueued;
    private int _requestSeq;
    private string _game = "";
    private string _q = "";
    private string _sort = "newest";
    private int _page = 1;
    private int _pages = 1;
    private int _total;
    private const int PerPage = 30;

    private DispatcherTimer? _searchTimer;

    private static readonly SolidColorBrush BrushAccent = new(Color.FromRgb(0xF0, 0x88, 0x3E));
    private static readonly SolidColorBrush BrushAccentFaint = new(Color.FromArgb(0x30, 0xF0, 0x88, 0x3E));
    private static readonly SolidColorBrush BrushMuted = new(Color.FromRgb(0x8B, 0x94, 0x9E));
    private static readonly SolidColorBrush BrushBadgeBg = new(Color.FromRgb(0x2D, 0x33, 0x3B));
    private static readonly SolidColorBrush BrushCardBg = new(Color.FromRgb(0x1C, 0x21, 0x28));
    private static readonly SolidColorBrush BrushCardBorder = new(Color.FromRgb(0x30, 0x36, 0x3D));
    private static readonly SolidColorBrush BrushForeground = new(Color.FromRgb(0xE6, 0xED, 0xF3));

    public HubPage()
    {
        InitializeComponent();

        GameFilter.Items.Add(new ComboBoxItem { Content = "All Games", Tag = "" });
        GameFilter.Items.Add(new ComboBoxItem { Content = "AC EVO", Tag = "AcEvo" });
        GameFilter.Items.Add(new ComboBoxItem { Content = "RaceRoom", Tag = "Raceroom" });
        GameFilter.Items.Add(new ComboBoxItem { Content = "Assetto Corsa", Tag = "AssettoCorsa" });
        GameFilter.Items.Add(new ComboBoxItem { Content = "Le Mans Ultimate", Tag = "LeMansUltimate" });
        GameFilter.Items.Add(new ComboBoxItem { Content = "ACC", Tag = "AssettoCorsaCompetizione" });
        GameFilter.SelectedIndex = 0;

        SortFilter.Items.Add(new ComboBoxItem { Content = "Newest", Tag = "newest" });
        SortFilter.Items.Add(new ComboBoxItem { Content = "Most Downloaded", Tag = "downloads" });
        SortFilter.Items.Add(new ComboBoxItem { Content = "Top Rated", Tag = "rating" });
        SortFilter.Items.Add(new ComboBoxItem { Content = "Oldest", Tag = "oldest" });
        SortFilter.SelectedIndex = 0;

        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_vm != null)
            _vm.PropertyChanged -= OnVmPropertyChanged;

        _vm = DataContext as MainViewModel;

        if (_vm != null)
        {
            _vm.PropertyChanged += OnVmPropertyChanged;
            if (!_loadedOnce && _vm.CurrentPage == NavPage.Hub)
            {
                _loadedOnce = true;
                Load();
            }
        }
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.CurrentPage) &&
            _vm?.CurrentPage == NavPage.Hub && !_loadedOnce)
        {
            _loadedOnce = true;
            Load();
        }
    }

    private void OnFilterChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_vm == null) return;
        _game = SelectedTag(GameFilter);
        _sort = SelectedTag(SortFilter) ?? "newest";
        _page = 1;
        Load();
    }

    private static string? SelectedTag(ComboBox box)
    {
        return (box.SelectedItem as ComboBoxItem)?.Tag as string;
    }

    private void OnSearchTextChanged(object sender, TextChangedEventArgs e)
    {
        _searchTimer?.Stop();
        _searchTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
        _searchTimer.Tick += (_, _) =>
        {
            _searchTimer.Stop();
            if (_vm == null) return;
            _q = SearchBox.Text.Trim();
            _page = 1;
            Load();
        };
        _searchTimer.Start();
    }

    private void OnRefreshClick(object sender, RoutedEventArgs e) => Load();

    private void OnPrevClick(object sender, RoutedEventArgs e)
    {
        if (_page <= 1) return;
        _page--;
        Load();
    }

    private void OnNextClick(object sender, RoutedEventArgs e)
    {
        if (_page >= _pages) return;
        _page++;
        Load();
    }

    private async void Load()
    {
        if (_vm == null) return;
        _requestSeq++;
        if (_loading)
        {
            _reloadQueued = true;
            return;
        }
        _loading = true;
        int seq = _requestSeq;

        LoadingText.Visibility = Visibility.Visible;
        CardsHost.Children.Clear();
        EmptyPanel.Visibility = Visibility.Collapsed;
        PrevBtn.IsEnabled = false;
        NextBtn.IsEnabled = false;

        var result = await _vm.HubClient.GetProfilesAsync(_game, _q, _sort, _page, PerPage);

        _loading = false;
        LoadingText.Visibility = Visibility.Collapsed;

        if (seq != _requestSeq)
        {
            if (_reloadQueued)
            {
                _reloadQueued = false;
                Load();
            }
            return;
        }

        if (!result.Ok)
        {
            _total = 0;
            _pages = 1;
            ShowEmpty(result.Error ?? "Failed to load profiles");
            UpdatePaging();
            return;
        }

        _total = result.Total;
        _pages = Math.Max(1, result.Pages);

        if (result.Profiles.Count == 0)
        {
            ShowEmpty(_q.Length > 0 || _game.Length > 0
                ? "No profiles match your filters."
                : "No profiles on the Hub yet — share one from the Profiles page!");
        }
        else
        {
            foreach (var p in result.Profiles)
                CardsHost.Children.Add(CreateCard(p));
        }

        UpdatePaging();

        if (_reloadQueued)
        {
            _reloadQueued = false;
            Load();
        }
    }

    private void ShowEmpty(string message)
    {
        EmptyText.Text = message;
        EmptyPanel.Visibility = Visibility.Visible;
    }

    private void UpdatePaging()
    {
        CountText.Text = _total == 1 ? "1 profile" : $"{_total} profiles";
        PageInfoText.Text = $"Page {_page} of {_pages}";
        PrevBtn.IsEnabled = _page > 1;
        NextBtn.IsEnabled = _page < _pages;
    }

    private Border CreateCard(HubProfileDto p)
    {
        var card = new Border
        {
            Background = BrushCardBg,
            CornerRadius = new CornerRadius(8),
            BorderBrush = BrushCardBorder,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(14, 12, 14, 12),
            Margin = new Thickness(0, 0, 0, 10)
        };

        var root = new StackPanel();

        // Top row: game badge + downloads
        var topRow = new Grid();
        topRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Auto) });
        topRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        topRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Auto) });

        var gameBadge = CreateBadge(HubClient.ToDisplayLabel(p.Game), BrushAccentFaint, BrushAccent);
        Grid.SetColumn(gameBadge, 0);
        topRow.Children.Add(gameBadge);

        var downloads = new TextBlock
        {
            Text = $"↓ {p.Downloads}",
            FontSize = 11,
            Foreground = BrushMuted,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 0, 2)
        };
        Grid.SetColumn(downloads, 2);
        topRow.Children.Add(downloads);

        root.Children.Add(topRow);

        var title = new TextBlock
        {
            Text = p.Title,
            FontSize = 15,
            FontWeight = FontWeights.Bold,
            Foreground = BrushForeground,
            Margin = new Thickness(0, 6, 0, 2),
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        root.Children.Add(title);

        if (!string.IsNullOrWhiteSpace(p.Description))
        {
            root.Children.Add(new TextBlock
            {
                Text = p.Description,
                FontSize = 12,
                Foreground = BrushMuted,
                Margin = new Thickness(0, 0, 0, 8),
                TextWrapping = TextWrapping.Wrap,
                MaxHeight = 34
            });
        }

        // Tags row
        var tagRow = new WrapPanel { Margin = new Thickness(0, 0, 0, 8) };
        foreach (var tag in new[] { p.Car, p.Track, p.Wheel, p.WheelType })
        {
            if (string.IsNullOrWhiteSpace(tag)) continue;
            tagRow.Children.Add(CreateBadge(tag, BrushBadgeBg, BrushMuted));
        }
        if (tagRow.Children.Count > 0)
            root.Children.Add(tagRow);

        // Meta + download
        var metaRow = new Grid();
        metaRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        metaRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Auto) });

        var meta = new TextBlock
        {
            Text = BuildMeta(p),
            FontSize = 11,
            Foreground = BrushMuted,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(meta, 0);
        metaRow.Children.Add(meta);

        var downloadBtn = new Button
        {
            Content = "Download",
            Padding = new Thickness(14, 4, 14, 4),
            FontSize = 12,
            Margin = new Thickness(10, 0, 0, 0),
            Tag = p,
            ToolTip = $"Import '{p.Title}' into your profiles"
        };
        downloadBtn.Style = (Style)FindResource("OutlinedButton");
        downloadBtn.Click += OnDownloadClick;
        Grid.SetColumn(downloadBtn, 1);
        metaRow.Children.Add(downloadBtn);

        root.Children.Add(metaRow);
        card.Child = root;
        return card;
    }

    private static string BuildMeta(HubProfileDto p)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(p.Author))
            parts.Add(p.Author);
        parts.Add($"★ {p.Rating:0.0} ({p.RatingCount})");
        if (p.TorqueNm > 0)
            parts.Add($"{p.TorqueNm:0.#} Nm");
        if (p.ProfileVersion > 0)
            parts.Add($"v{p.ProfileVersion}");
        return string.Join("  ·  ", parts);
    }

    private static Border CreateBadge(string text, SolidColorBrush bg, SolidColorBrush fg)
    {
        var badge = new Border
        {
            Background = bg,
            CornerRadius = new CornerRadius(3),
            Padding = new Thickness(6, 2, 6, 2),
            Margin = new Thickness(0, 0, 6, 3),
            VerticalAlignment = VerticalAlignment.Center
        };
        badge.Child = new TextBlock
        {
            Text = text,
            FontSize = 10,
            FontWeight = FontWeights.Bold,
            Foreground = fg
        };
        return badge;
    }

    private async void OnDownloadClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not HubProfileDto dto || _vm == null) return;

        btn.IsEnabled = false;
        var original = btn.Content;
        btn.Content = "Downloading...";
        try
        {
            var result = await _vm.HubClient.DownloadProfileAsync(dto.Id);
            if (result.Error != null)
            {
                _vm.StatusText = $"Download failed: {result.Error}";
                btn.Content = original;
                btn.IsEnabled = true;
                return;
            }

            var profile = _vm.ImportHubProfile(dto.Id, result.Json!);
            if (profile != null)
            {
                dto.Downloads++;
                btn.Content = "Imported";
                var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
                timer.Tick += (_, _) =>
                {
                    timer.Stop();
                    btn.Content = original;
                    btn.IsEnabled = true;
                };
                timer.Start();
            }
            else
            {
                btn.Content = original;
                btn.IsEnabled = true;
            }
        }
        catch
        {
            btn.Content = original;
            btn.IsEnabled = true;
        }
    }
}
