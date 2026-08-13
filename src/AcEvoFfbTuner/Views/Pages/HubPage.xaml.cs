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
    private string _wheel = "";
    private string _car = "";
    private string _track = "";
    private string _wheelType = "";
    private string _q = "";
    private string _sort = "newest";
    private int _page = 1;
    private int _pages = 1;
    private int _total;
    private bool _facetsLoaded;
    private readonly Dictionary<int, int> _myVotes = new();
    private readonly Dictionary<int, TextBlock> _ratingLabels = new();
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

        WheelFilter.Items.Add(new ComboBoxItem { Content = "All Wheelbases", Tag = "" });
        CarFilter.Items.Add(new ComboBoxItem { Content = "All Cars", Tag = "" });
        TrackFilter.Items.Add(new ComboBoxItem { Content = "All Tracks", Tag = "" });
        WheelTypeFilter.Items.Add(new ComboBoxItem { Content = "All Types", Tag = "" });
        WheelFilter.SelectedIndex = 0;
        CarFilter.SelectedIndex = 0;
        TrackFilter.SelectedIndex = 0;
        WheelTypeFilter.SelectedIndex = 0;

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
        _wheel = SelectedTag(WheelFilter);
        _car = SelectedTag(CarFilter);
        _track = SelectedTag(TrackFilter);
        _wheelType = SelectedTag(WheelTypeFilter);
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

    private void OnRefreshClick(object sender, RoutedEventArgs e)
    {
        _facetsLoaded = false;
        Load();
    }

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
        _ratingLabels.Clear();
        EmptyPanel.Visibility = Visibility.Collapsed;
        PrevBtn.IsEnabled = false;
        NextBtn.IsEnabled = false;

        var result = await _vm.HubClient.GetProfilesAsync(_game, _q, _sort, _page, PerPage, _wheel, _car, _track, _wheelType);

        if (!_facetsLoaded)
        {
            _facetsLoaded = true;
            await LoadFacets();
        }

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

    private async Task LoadFacets()
    {
        if (_vm == null) return;
        var facets = await _vm.HubClient.GetFacetsAsync();
        if (facets == null) return;
        PopulateFacet(WheelFilter, facets.Wheels);
        PopulateFacet(CarFilter, facets.Cars);
        PopulateFacet(TrackFilter, facets.Tracks);
        PopulateFacet(WheelTypeFilter, facets.WheelTypes);
    }

    private static void PopulateFacet(ComboBox box, List<string> values)
    {
        string current = SelectedTag(box) ?? "";
        box.Items.Clear();
        box.Items.Add(new ComboBoxItem { Content = "All", Tag = "" });
        foreach (var v in values)
        {
            if (string.IsNullOrWhiteSpace(v)) continue;
            box.Items.Add(new ComboBoxItem { Content = v, Tag = v });
        }
        int restore = 0;
        for (int i = 0; i < box.Items.Count; i++)
        {
            if ((box.Items[i] as ComboBoxItem)?.Tag as string == current)
            {
                restore = i;
                break;
            }
        }
        box.SelectedIndex = restore;
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

        // Star rating row
        var starRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
        _myVotes.TryGetValue(p.Id, out var voted);
        int filled = voted > 0 ? voted : (int)Math.Round(p.Rating);
        for (int i = 1; i <= 5; i++)
        {
            var starBtn = new Button
            {
                Content = "★",
                FontSize = 13,
                Padding = new Thickness(2, 0, 2, 0),
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Foreground = i <= filled ? BrushAccent : BrushMuted,
                Tag = (p.Id, i),
                Cursor = Cursors.Hand,
                ToolTip = $"Rate {i} star{(i > 1 ? "s" : "")}"
            };
            starBtn.Click += OnRateClick;
            starRow.Children.Add(starBtn);
        }
        var ratingLabel = new TextBlock
        {
            Text = $"{p.Rating:0.0} ({p.RatingCount})",
            FontSize = 11,
            Foreground = BrushMuted,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(6, 0, 0, 0)
        };
        _ratingLabels[p.Id] = ratingLabel;
        starRow.Children.Add(ratingLabel);
        root.Children.Add(starRow);

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
        card.Tag = p;
        card.Child = root;
        return card;
    }

    private async void OnRateClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || _vm == null) return;
        var (id, value) = ((int, int))btn.Tag;
        btn.IsEnabled = false;
        try
        {
            var result = await _vm.HubClient.RateProfileAsync(id, value);
            if (result.Ok)
            {
                _myVotes[id] = value;
                foreach (var child in CardsHost.Children.OfType<Border>())
                {
                    if (child.Tag is HubProfileDto dto && dto.Id == id)
                    {
                        dto.Rating = result.Rating;
                        dto.RatingCount = result.RatingCount;
                    }
                }
                if (_ratingLabels.TryGetValue(id, out var label))
                {
                    label.Text = $"{result.Rating:0.0} ({result.RatingCount})";
                    if (label.Parent is StackPanel row)
                    {
                        foreach (var star in row.Children.OfType<Button>())
                        {
                            if (star.Tag is (int cid, int cval) && cid == id)
                                star.Foreground = cval <= value ? BrushAccent : BrushMuted;
                        }
                    }
                }
                _vm.StatusText = $"Rated {value}★ — profile is now {result.Rating:0.0} ({result.RatingCount} votes)";
            }
            else
            {
                _vm.StatusText = $"Rating failed: {result.Error}";
            }
        }
        finally
        {
            btn.IsEnabled = true;
        }
    }

    private static string BuildMeta(HubProfileDto p)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(p.Author))
            parts.Add(p.Author);
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
