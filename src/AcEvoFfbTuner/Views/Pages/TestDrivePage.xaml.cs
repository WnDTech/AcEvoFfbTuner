using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AcEvoFfbTuner.Services;
using AcEvoFfbTuner.ViewModels;

namespace AcEvoFfbTuner.Views.Pages;

/// <summary>Test Drive page: Discord sign-in, application status + testing
/// tasks, task-linked diagnostic pack sending, and The Podium credit wall.
/// Code-behind style mirrors HubPage: lazy-load on first open, re-fetch on
/// every page open for signed-in users.</summary>
public partial class TestDrivePage : UserControl
{
    private MainViewModel? _vm;
    private bool _loadedOnce;
    private bool _loading;
    private bool _reloadQueued;
    private int _requestSeq;
    private Button? _signInBtn;

    private static readonly SolidColorBrush BrushAccent = new(Color.FromRgb(0xF0, 0x88, 0x3E));
    private static readonly SolidColorBrush BrushAccentFaint = new(Color.FromArgb(0x30, 0xF0, 0x88, 0x3E));
    private static readonly SolidColorBrush BrushMuted = new(Color.FromRgb(0x8B, 0x94, 0x9E));
    private static readonly SolidColorBrush BrushBadgeBg = new(Color.FromRgb(0x2D, 0x33, 0x3B));
    private static readonly SolidColorBrush BrushCardBg = new(Color.FromRgb(0x1C, 0x21, 0x28));
    private static readonly SolidColorBrush BrushCardBorder = new(Color.FromRgb(0x30, 0x36, 0x3D));
    private static readonly SolidColorBrush BrushForeground = new(Color.FromRgb(0xE6, 0xED, 0xF3));
    private static readonly SolidColorBrush BrushGood = new(Color.FromRgb(0x3F, 0xB9, 0x50));
    private static readonly SolidColorBrush BrushWarn = new(Color.FromRgb(0xF0, 0xA0, 0x30));
    private static readonly SolidColorBrush BrushBad = new(Color.FromRgb(0xF8, 0x51, 0x51));

    public TestDrivePage()
    {
        InitializeComponent();
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
            if (!_loadedOnce && _vm.CurrentPage == NavPage.TestDrive)
            {
                _loadedOnce = true;
                Refresh();
            }
        }
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(MainViewModel.CurrentPage) ||
            _vm?.CurrentPage != NavPage.TestDrive) return;

        if (!_loadedOnce)
        {
            _loadedOnce = true;
            Refresh();
        }
        else if (!string.IsNullOrEmpty(_vm.BetaSessionToken))
        {
            // Signed-in users get fresh data on every page open (unlike HubPage,
            // which only loads once) — task status and points change often.
            Refresh();
        }
    }

    private async void Refresh()
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
        try
        {
            if (!string.IsNullOrEmpty(_vm.BetaSessionToken))
                await LoadUserAsync();
            else
                RenderSignedOut(null);
            await LoadPodiumAsync();
        }
        finally
        {
            _loading = false;
        }
        if (seq != _requestSeq && _reloadQueued)
        {
            _reloadQueued = false;
            Refresh();
        }
    }

    /* ---------- Data loading ---------- */

    private async Task LoadUserAsync()
    {
        if (_vm == null) return;
        var token = _vm.BetaSessionToken;
        if (string.IsNullOrEmpty(token))
        {
            RenderSignedOut(null);
            return;
        }

        var result = await _vm.BetaClient.GetMeAsync(token);
        if (result.Unauthorized)
        {
            _vm.ClearBetaSession();
            RenderSignedOut("Session expired — sign in again");
            return;
        }
        if (!result.Ok || result.User == null)
        {
            var cache = LoadCache();
            if (cache != null)
            {
                RenderSignedIn(cache, null, offline: true);
            }
            else
            {
                RenderSignedOut(result.Error ?? "Could not reach the Test Drive server");
            }
            return;
        }

        _vm.CacheBetaUser(result.User, result.Application);

        // The server is the authority on channel eligibility — an application
        // that is no longer approved/paused silently drops the beta channel.
        if (!result.BetaChannel && _vm.BetaChannel)
        {
            _vm.BetaChannel = false;
            _vm.StatusText = "Test Drive build channel disabled — your application is no longer active";
        }

        var fresh = new BetaUserCache
        {
            Name = result.User.Name,
            Avatar = result.User.Avatar,
            Tier = result.Application?.Tier,
            Status = result.Application?.Status,
            BetaChannel = result.BetaChannel
        };
        if (result.Application != null)
            RenderSignedIn(fresh, result.Application, offline: false);
        else
            RenderNoApplication(result.User);
    }

    private BetaUserCache? LoadCache()
    {
        try
        {
            var json = _vm?.BetaUserCacheJson;
            if (string.IsNullOrWhiteSpace(json)) return null;
            return JsonSerializer.Deserialize<BetaUserCache>(json, BetaClient.JsonOptions);
        }
        catch
        {
            return null;
        }
    }

    private async Task LoadPodiumAsync()
    {
        if (_vm == null) return;
        RenderPodium(await _vm.BetaClient.GetPodiumAsync());
    }

    /* ---------- Rendering: signed out ---------- */

    private void RenderSignedOut(string? note)
    {
        var root = new StackPanel();
        root.Children.Add(SectionTitle("MY TESTING"));

        var card = NewCard();
        var inner = new StackPanel();

        inner.Children.Add(new TextBlock
        {
            Text = "Sign in with Discord to check your application status and testing tasks.",
            FontSize = 13,
            Foreground = BrushMuted,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 14)
        });

        _signInBtn = new Button
        {
            Content = "Sign in with Discord",
            Padding = new Thickness(18, 9, 18, 9),
            FontSize = 14,
            FontWeight = FontWeights.Bold,
            HorizontalAlignment = HorizontalAlignment.Left,
            Background = BrushAccent,
            Foreground = Brushes.White,
            BorderThickness = new Thickness(0),
            Cursor = Cursors.Hand
        };
        _signInBtn.Click += OnSignInClick;
        inner.Children.Add(_signInBtn);

        inner.Children.Add(new TextBlock
        {
            Text = "Test Drive is a closed beta program — test new FFB features, send diagnostic packs, earn points, and climb The Podium.",
            FontSize = 12,
            Foreground = BrushMuted,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 14, 0, 0)
        });

        if (!string.IsNullOrEmpty(note))
        {
            inner.Children.Add(new TextBlock
            {
                Text = note,
                FontSize = 12,
                Foreground = BrushWarn,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 12, 0, 0)
            });
        }

        card.Child = inner;
        root.Children.Add(card);
        MyTestingHost.Children.Clear();
        MyTestingHost.Children.Add(root);
    }

    /* ---------- Rendering: signed in, no application ---------- */

    private void RenderNoApplication(BetaUserDto user)
    {
        var root = new StackPanel();
        root.Children.Add(SectionTitle("MY TESTING"));

        var card = NewCard();
        var inner = new StackPanel();

        var head = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 10) };
        head.Children.Add(CreateAvatar(user.Avatar, user.Name));
        head.Children.Add(new TextBlock
        {
            Text = user.Name,
            FontSize = 16,
            FontWeight = FontWeights.Bold,
            Foreground = BrushForeground,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(10, 0, 0, 0)
        });
        inner.Children.Add(head);

        inner.Children.Add(new TextBlock
        {
            Text = "You're not in the Test Drive program yet — apply on the website to become a tester.",
            FontSize = 13,
            Foreground = BrushMuted,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 12)
        });

        var openBtn = new Button
        {
            Content = "Open Application Page",
            Padding = new Thickness(14, 6, 14, 6),
            FontSize = 13,
            HorizontalAlignment = HorizontalAlignment.Left
        };
        openBtn.Style = (Style)FindResource("OutlinedButton");
        openBtn.Click += OnOpenAppPageClick;
        inner.Children.Add(openBtn);

        inner.Children.Add(CreateActionsRow());
        card.Child = inner;
        root.Children.Add(card);
        MyTestingHost.Children.Clear();
        MyTestingHost.Children.Add(root);
    }

    /* ---------- Rendering: signed in with application ---------- */

    private void RenderSignedIn(BetaUserCache cache, BetaApplicationDto? application, bool offline)
    {
        var root = new StackPanel();
        root.Children.Add(SectionTitle("MY TESTING"));

        var card = NewCard();
        var inner = new StackPanel();

        var head = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 10) };
        head.Children.Add(CreateAvatar(cache.Avatar, cache.Name));

        var nameCol = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) };
        nameCol.Children.Add(new TextBlock
        {
            Text = cache.Name ?? "Test Driver",
            FontSize = 16,
            FontWeight = FontWeights.Bold,
            Foreground = BrushForeground
        });
        var badgeRow = new WrapPanel { Margin = new Thickness(0, 4, 0, 0) };
        if (!string.IsNullOrEmpty(cache.Tier))
            badgeRow.Children.Add(CreateBadge(TierLabel(cache.Tier), BrushAccentFaint, BrushAccent));
        if (!string.IsNullOrEmpty(cache.Status))
            badgeRow.Children.Add(CreateBadge(StatusLabel(cache.Status), StatusBrush(cache.Status), StatusBrush(cache.Status)));
        nameCol.Children.Add(badgeRow);
        head.Children.Add(nameCol);
        inner.Children.Add(head);

        if (offline)
        {
            inner.Children.Add(new TextBlock
            {
                Text = "Offline — showing saved info. Press Refresh to retry.",
                FontSize = 12,
                Foreground = BrushWarn,
                Margin = new Thickness(0, 0, 0, 10)
            });
        }

        if (application != null)
        {
            var meta = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
            meta.Children.Add(new TextBlock
            {
                Text = $"{application.Points} pts",
                FontSize = 13,
                FontWeight = FontWeights.Bold,
                Foreground = BrushAccent,
                VerticalAlignment = VerticalAlignment.Center
            });
            var parts = new List<string>();
            if (!string.IsNullOrEmpty(application.Timezone))
                parts.Add($"TZ {application.Timezone}");
            if (!string.IsNullOrEmpty(application.AppliedAt))
                parts.Add($"Applied {FormatDate(application.AppliedAt)}");
            if (parts.Count > 0)
            {
                meta.Children.Add(new TextBlock
                {
                    Text = "  ·  " + string.Join("  ·  ", parts),
                    FontSize = 12,
                    Foreground = BrushMuted,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(8, 0, 0, 0)
                });
            }
            inner.Children.Add(meta);

            inner.Children.Add(new TextBlock
            {
                Text = "TASKS",
                FontSize = 11,
                FontWeight = FontWeights.Bold,
                Foreground = BrushMuted,
                Margin = new Thickness(0, 10, 0, 6)
            });
            if (application.Tasks.Count == 0)
            {
                inner.Children.Add(new TextBlock
                {
                    Text = "No tasks assigned yet — check back soon.",
                    FontSize = 12,
                    Foreground = BrushMuted,
                    Margin = new Thickness(0, 0, 0, 8)
                });
            }
            else
            {
                foreach (var task in application.Tasks)
                    inner.Children.Add(CreateTaskCard(task));
            }
        }
        else if (!offline)
        {
            inner.Children.Add(new TextBlock
            {
                Text = "You're not in the Test Drive program yet — apply on the website to become a tester.",
                FontSize = 13,
                Foreground = BrushMuted,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 12)
            });
            var openBtn = new Button
            {
                Content = "Open Application Page",
                Padding = new Thickness(14, 6, 14, 6),
                FontSize = 13,
                HorizontalAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(0, 0, 0, 12)
            };
            openBtn.Style = (Style)FindResource("OutlinedButton");
            openBtn.Click += OnOpenAppPageClick;
            inner.Children.Add(openBtn);
        }

        inner.Children.Add(CreateActionsRow());

        if (cache.BetaChannel)
        {
            var chk = new CheckBox
            {
                Content = "Test Drive build channel — receive beta builds",
                IsChecked = _vm?.BetaChannel == true,
                Foreground = BrushForeground,
                FontSize = 13,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 12, 0, 0)
            };
            chk.Checked += OnBetaChannelChanged;
            chk.Unchecked += OnBetaChannelChanged;
            inner.Children.Add(chk);
            inner.Children.Add(new TextBlock
            {
                Text = "Beta builds are published as prereleases — the updater will offer them automatically while this is on. Turn it off to receive stable releases only.",
                FontSize = 11,
                Foreground = BrushMuted,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(24, 3, 0, 0)
            });
        }

        card.Child = inner;
        root.Children.Add(card);
        MyTestingHost.Children.Clear();
        MyTestingHost.Children.Add(root);
    }

    private void OnBetaChannelChanged(object sender, RoutedEventArgs e)
    {
        if (_vm == null || sender is not CheckBox cb) return;
        _vm.BetaChannel = cb.IsChecked == true;
        _vm.StatusText = cb.IsChecked == true
            ? "Test Drive build channel enabled — beta updates will be offered"
            : "Test Drive build channel disabled — stable releases only";
    }

    private StackPanel CreateActionsRow()
    {
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 0) };
        var refreshBtn = new Button { Content = "Refresh", Padding = new Thickness(14, 6, 14, 6), FontSize = 13, Margin = new Thickness(0, 0, 8, 0) };
        refreshBtn.Style = (Style)FindResource("OutlinedButton");
        refreshBtn.Click += OnRefreshClick;
        actions.Children.Add(refreshBtn);
        var signOutBtn = new Button { Content = "Sign out", Padding = new Thickness(14, 6, 14, 6), FontSize = 13 };
        signOutBtn.Style = (Style)FindResource("OutlinedButton");
        signOutBtn.Click += OnSignOutClick;
        actions.Children.Add(signOutBtn);
        return actions;
    }

    private Border CreateTaskCard(BetaTaskDto t)
    {
        var card = NewCard();
        var root = new StackPanel();

        var topRow = new Grid();
        topRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Auto) });
        topRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        topRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Auto) });

        var code = new TextBlock
        {
            Text = t.TaskCode,
            FontSize = 12,
            FontWeight = FontWeights.Bold,
            Foreground = BrushAccent,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(code, 0);
        topRow.Children.Add(code);

        var statusBadge = CreateBadge(StatusLabel(t.Status), StatusBrush(t.Status), StatusBrush(t.Status));
        Grid.SetColumn(statusBadge, 2);
        topRow.Children.Add(statusBadge);
        root.Children.Add(topRow);

        root.Children.Add(new TextBlock
        {
            Text = t.Title,
            FontSize = 14,
            FontWeight = FontWeights.Bold,
            Foreground = BrushForeground,
            Margin = new Thickness(0, 6, 0, 2),
            TextTrimming = TextTrimming.CharacterEllipsis
        });

        if (!string.IsNullOrWhiteSpace(t.Details))
        {
            root.Children.Add(new TextBlock
            {
                Text = t.Details,
                FontSize = 12,
                Foreground = BrushMuted,
                TextWrapping = TextWrapping.Wrap,
                MaxHeight = 40,
                Margin = new Thickness(0, 0, 0, 8)
            });
        }

        if (!string.IsNullOrWhiteSpace(t.Notes))
        {
            root.Children.Add(new TextBlock
            {
                Text = "📝 " + t.Notes,
                FontSize = 12,
                Foreground = BrushWarn,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 8)
            });
        }

        var metaRow = new Grid();
        metaRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        metaRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Auto) });

        var tags = new WrapPanel { VerticalAlignment = VerticalAlignment.Center };
        tags.Children.Add(CreateBadge(TypeLabel(t.Type), BrushBadgeBg, BrushMuted));
        if (t.Points > 0)
            tags.Children.Add(CreateBadge($"{t.Points} pts", BrushAccentFaint, BrushAccent));
        Grid.SetColumn(tags, 0);
        metaRow.Children.Add(tags);

        bool canSend = t.Status != "complete" && (t.Type == "diag_bundle" || t.Type == "verify_fix");
        var actions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        if (canSend)
        {
            var sendBtn = new Button
            {
                Content = string.IsNullOrEmpty(t.ReportId) ? "Send diagnostics" : "Send diagnostics again",
                Padding = new Thickness(14, 5, 14, 5),
                FontSize = 12,
                Margin = new Thickness(0, 0, 0, 0),
                Tag = t,
                ToolTip = $"Send a diagnostic pack linked to {t.TaskCode}"
            };
            sendBtn.Style = (Style)FindResource("OutlinedButton");
            sendBtn.Click += OnSendDiagClick;
            actions.Children.Add(sendBtn);
        }
        else if (!string.IsNullOrEmpty(t.ReportId))
        {
            actions.Children.Add(new TextBlock
            {
                Text = $"Report: {t.ReportId}",
                FontSize = 11,
                Foreground = BrushMuted,
                VerticalAlignment = VerticalAlignment.Center
            });
        }
        if (!string.IsNullOrEmpty(t.DiscordThreadUrl))
        {
            var threadBtn = new Button
            {
                Content = "Open thread",
                Padding = new Thickness(14, 5, 14, 5),
                FontSize = 12,
                Margin = new Thickness(8, 0, 0, 0),
                Tag = t,
                ToolTip = $"Open the task's Discord thread ({t.TaskCode})"
            };
            threadBtn.Style = (Style)FindResource("OutlinedButton");
            threadBtn.Click += OnOpenThreadClick;
            actions.Children.Add(threadBtn);
        }
        if (actions.Children.Count > 0)
        {
            Grid.SetColumn(actions, 1);
            metaRow.Children.Add(actions);
        }

        root.Children.Add(metaRow);
        card.Child = root;
        return card;
    }

    /* ---------- Rendering: The Podium ---------- */

    private void RenderPodium(BetaPodiumResult result)
    {
        var root = new StackPanel();
        root.Children.Add(SectionTitle("THE PODIUM"));

        var card = NewCard();
        var inner = new StackPanel();

        if (!result.Ok)
        {
            inner.Children.Add(new TextBlock
            {
                Text = $"Offline — {result.Error}",
                FontSize = 12,
                Foreground = BrushMuted,
                TextWrapping = TextWrapping.Wrap
            });
        }
        else if (result.Podium.Count == 0)
        {
            inner.Children.Add(new TextBlock
            {
                Text = "No testers yet — be the first to earn a spot on The Podium.",
                FontSize = 12,
                Foreground = BrushMuted,
                TextWrapping = TextWrapping.Wrap,
                TextAlignment = TextAlignment.Center
            });
        }
        else
        {
            foreach (var (entry, index) in result.Podium.Take(10).Select((p, i) => (p, i)))
                inner.Children.Add(CreatePodiumRow(index + 1, entry));
        }

        card.Child = inner;
        root.Children.Add(card);
        PodiumHost.Children.Clear();
        PodiumHost.Children.Add(root);
    }

    private static Border CreatePodiumRow(int rank, BetaPodiumEntryDto e)
    {
        var row = new Grid { Margin = new Thickness(0, 0, 0, 8) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Auto) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Auto) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Auto) });

        var rankTb = new TextBlock
        {
            Text = rank.ToString(),
            FontSize = 15,
            FontWeight = FontWeights.Black,
            Foreground = rank <= 3 ? BrushAccent : BrushMuted,
            Width = 24,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(rankTb, 0);
        row.Children.Add(rankTb);

        var avatar = CreateAvatar(e.Avatar, e.Name);
        Grid.SetColumn(avatar, 1);
        row.Children.Add(avatar);

        var nameCol = new StackPanel { Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        nameCol.Children.Add(new TextBlock
        {
            Text = e.Name,
            FontSize = 13,
            FontWeight = FontWeights.Bold,
            Foreground = BrushForeground,
            TextTrimming = TextTrimming.CharacterEllipsis
        });
        nameCol.Children.Add(new TextBlock
        {
            Text = $"{e.Points} pts · {e.Credits} verified",
            FontSize = 11,
            Foreground = BrushMuted
        });
        Grid.SetColumn(nameCol, 2);
        row.Children.Add(nameCol);

        var tier = CreateBadge(TierLabel(e.Tier), BrushBadgeBg, BrushMuted);
        Grid.SetColumn(tier, 3);
        row.Children.Add(tier);

        var border = new Border
        {
            Background = BrushCardBg,
            CornerRadius = new CornerRadius(6),
            BorderBrush = BrushCardBorder,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(8, 8, 8, 8)
        };
        border.Child = row;
        return border;
    }

    /* ---------- Actions ---------- */

    private async void OnSignInClick(object sender, RoutedEventArgs e)
    {
        if (_vm == null) return;
        if (_signInBtn != null) _signInBtn.IsEnabled = false;
        try
        {
            var result = await _vm.BetaClient.SignInAsync();
            if (!result.Ok || string.IsNullOrEmpty(result.Token))
            {
                var msg = result.Error ?? "Sign-in failed — try again";
                _vm.StatusText = msg;
                RenderSignedOut(msg);
                return;
            }
            _vm.SetBetaSession(result.Token, result.User, null);
            _vm.StatusText = $"Signed in as {result.User?.Name ?? "Discord user"}";
            await LoadUserAsync();
        }
        catch (Exception ex)
        {
            _vm.StatusText = $"Sign-in failed: {ex.Message}";
            RenderSignedOut("Sign-in failed — try again");
        }
        finally
        {
            if (_signInBtn != null) _signInBtn.IsEnabled = true;
        }
    }

    private async void OnSendDiagClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not BetaTaskDto task || _vm == null) return;
        var token = _vm.BetaSessionToken;
        if (string.IsNullOrEmpty(token))
        {
            RenderSignedOut("Session expired — sign in again");
            return;
        }

        btn.IsEnabled = false;
        var original = btn.Content;
        btn.Content = "Sending...";
        try
        {
            var (success, reportId, message) = await _vm.SendDiagnosticPackForTaskAsync(task.TaskCode);
            if (!success)
            {
                _vm.StatusText = $"Send failed: {message} — task {task.TaskCode} still assigned, report manually";
                return;
            }

            _vm.StatusText = $"Pack sent ({message}) — reporting task {task.TaskCode}...";
            var report = await _vm.BetaClient.ReportTaskAsync(token, task.TaskCode, reportId ?? "");
            if (report.Ok)
            {
                _vm.StatusText = $"Task {task.TaskCode} submitted — thanks for testing!";
                await LoadUserAsync();
            }
            else if (report.Error?.Contains("Session", StringComparison.OrdinalIgnoreCase) == true)
            {
                _vm.ClearBetaSession();
                RenderSignedOut("Session expired — sign in again");
            }
            else
            {
                _vm.StatusText = $"Pack sent, but the task could not be linked ({report.Error}) — task {task.TaskCode} still assigned, report manually";
            }
        }
        finally
        {
            btn.IsEnabled = true;
            btn.Content = original;
        }
    }

    private void OnOpenThreadClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not BetaTaskDto task || _vm == null) return;
        if (string.IsNullOrEmpty(task.DiscordThreadUrl)) return;
        try
        {
            Process.Start(new ProcessStartInfo(task.DiscordThreadUrl) { UseShellExecute = true });
        }
        catch
        {
            _vm.StatusText = $"Could not open Discord — task {task.TaskCode} thread: {task.DiscordThreadUrl}";
        }
    }

    private void OnRefreshClick(object sender, RoutedEventArgs e)
    {
        Refresh();
    }

    private void OnSignOutClick(object sender, RoutedEventArgs e)
    {
        if (_vm == null) return;
        _vm.ClearBetaSession();
        _vm.StatusText = "Signed out of Test Drive";
        RenderSignedOut(null);
    }

    private void OnOpenAppPageClick(object sender, RoutedEventArgs e)
    {
        if (_vm == null) return;
        try
        {
            Process.Start(new ProcessStartInfo(BetaClient.ApplicationPageUrl(null)) { UseShellExecute = true });
        }
        catch
        {
            _vm.StatusText = "Could not open the browser — visit ffbtuner.wndtech.tips/beta.html";
        }
    }

    /* ---------- Small builders ---------- */

    private static Border NewCard() => new()
    {
        Background = BrushCardBg,
        CornerRadius = new CornerRadius(8),
        BorderBrush = BrushCardBorder,
        BorderThickness = new Thickness(1),
        Padding = new Thickness(16, 14, 16, 14),
        Margin = new Thickness(0, 0, 0, 14)
    };

    private static TextBlock SectionTitle(string text) => new()
    {
        Text = text,
        FontSize = 13,
        FontWeight = FontWeights.Black,
        Foreground = BrushAccent,
        Margin = new Thickness(0, 0, 0, 8)
    };

    private static FrameworkElement CreateAvatar(string? avatarUrl, string? name)
    {
        var fallback = new Border
        {
            Width = 36,
            Height = 36,
            CornerRadius = new CornerRadius(18),
            Background = BrushAccentFaint,
            VerticalAlignment = VerticalAlignment.Center
        };
        fallback.Child = new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(name) ? "?" : char.ToUpperInvariant(name[0]).ToString(),
            FontSize = 15,
            FontWeight = FontWeights.Bold,
            Foreground = BrushAccent,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        if (string.IsNullOrWhiteSpace(avatarUrl)) return fallback;
        try
        {
            var img = new Image
            {
                Width = 36,
                Height = 36,
                Stretch = Stretch.UniformToFill,
                VerticalAlignment = VerticalAlignment.Center,
                Clip = new EllipseGeometry(new Point(18, 18), 18, 18)
            };
            img.Source = new BitmapImage(new Uri(avatarUrl));
            return img;
        }
        catch
        {
            return fallback;
        }
    }

    private static Border CreateBadge(string text, Brush bg, Brush fg)
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

    private static Brush StatusBrush(string status) => status switch
    {
        "approved" or "verified" or "complete" => BrushGood,
        "applied" or "paused" or "submitted" => BrushWarn,
        "removed" => BrushBad,
        _ => BrushMuted
    };

    private static string StatusLabel(string status) => status switch
    {
        "applied" => "Applied",
        "approved" => "Approved",
        "paused" => "Paused",
        "removed" => "Removed",
        "assigned" => "Assigned",
        "submitted" => "Submitted",
        "verified" => "Verified",
        "complete" => "Complete",
        _ => status
    };

    private static string TierLabel(string tier) => tier switch
    {
        "test_driver" => "Test Driver",
        "dev_driver" => "Dev Driver",
        "podium" => "Podium",
        _ => tier
    };

    private static string TypeLabel(string type) => type switch
    {
        "diag_bundle" => "Diagnostics",
        "snapshot" => "Snapshot",
        "recording" => "Recording",
        "verify_fix" => "Verify Fix",
        "other" => "Other",
        _ => type
    };

    private static string FormatDate(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "";
        return DateTime.TryParse(s, out var dt) ? dt.ToString("d MMM yyyy") : s;
    }
}
