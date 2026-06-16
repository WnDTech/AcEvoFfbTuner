using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using AcEvoFfbTuner.Services;
using AcEvoFfbTuner.ViewModels;

namespace AcEvoFfbTuner.Views.Pages;

public partial class FFBCoachPage : UserControl
{
    private int _lastMsgCount = -1;
    private MainViewModel? _cachedVm;

    public FFBCoachPage()
    {
        InitializeComponent();
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible)
            {
                SubscribeToVm();
                Dispatcher.BeginInvoke(() => RebuildUI());
            }
            else
            {
                UnsubscribeFromVm();
            }
        };
    }

    private MainViewModel? VM => DataContext as MainViewModel;

    private void SubscribeToVm()
    {
        UnsubscribeFromVm();
        _cachedVm = VM;
        if (_cachedVm != null)
        {
            _cachedVm.PropertyChanged += OnVmPropertyChanged;
            _cachedVm.CoachMessages.CollectionChanged += OnMessagesChanged;
        }
    }

    private void UnsubscribeFromVm()
    {
        if (_cachedVm != null)
        {
            _cachedVm.PropertyChanged -= OnVmPropertyChanged;
            _cachedVm.CoachMessages.CollectionChanged -= OnMessagesChanged;
            _cachedVm = null;
        }
    }

    private void OnMessagesChanged(object? sender,
        System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        Dispatcher.BeginInvoke(() => RebuildUI());
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.CoachSessionState) ||
            e.PropertyName == nameof(MainViewModel.CoachDataSourceLabel) ||
            e.PropertyName == nameof(MainViewModel.CoachIsBusy) ||
            e.PropertyName == nameof(MainViewModel.SelectedProfile))
        {
            Dispatcher.BeginInvoke(() => RebuildUI());
        }
    }

    public void OnPageActivated()
    {
        SubscribeToVm();
        RebuildUI();
    }

    private void OnUseLatestSnapshot(object sender, RoutedEventArgs e)
    {
        VM?.CoachUseLatestSnapshotCommand?.Execute(null);
    }

    private void OnUseLiveData(object sender, RoutedEventArgs e)
    {
        VM?.CoachUseLiveDataCommand?.Execute(null);
    }

    private void OnRestartSession(object sender, RoutedEventArgs e)
    {
        VM?.CoachRestartCommand?.Execute(null);
    }

    private void OnShowSummary(object sender, RoutedEventArgs e)
    {
        VM?.CoachAnswerCommand?.Execute("finish");
    }

    public void RebuildUI()
    {
        var vm = VM;
        if (vm == null) return;

        RebuildMessages(vm);
        UpdateEmptyState(vm);
        UpdateInfoPanel(vm);
    }

    private void RebuildMessages(MainViewModel vm)
    {
        MessageContainer.Children.Clear();

        foreach (var msg in vm.CoachMessages)
        {
            var bubble = BuildBubble(msg, vm);
            MessageContainer.Children.Add(bubble);
        }

        if (MessageContainer.Children.Count > 0)
        {
            Dispatcher.BeginInvoke(() => ChatScrollViewer.ScrollToBottom());
        }
    }

    private Border BuildBubble(CoachMessage msg, MainViewModel vm)
    {
        var stack = new StackPanel();

        var icon = msg.Icon;
        var text = string.IsNullOrEmpty(icon) ? msg.Text : $"{icon}  {msg.Text}";

        stack.Children.Add(new TextBlock
        {
            Text = text,
            TextWrapping = TextWrapping.Wrap,
            FontSize = 15,
            LineHeight = 22,
            Foreground = msg.IsUser
                ? new SolidColorBrush(Color.FromRgb(0xFF, 0xFF, 0xFF))
                : new SolidColorBrush(Color.FromRgb(0xE6, 0xED, 0xF3))
        });

        if (msg.Answers?.Count > 0)
        {
            var ap = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
            foreach (var a in msg.Answers)
                ap.Children.Add(MakeAnswerButton(a, vm));
            stack.Children.Add(ap);
        }

        bool isRight = msg.IsUser;
        return new Border
        {
            Child = stack,
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(14, 10, 14, 10),
            MaxWidth = 520,
            HorizontalAlignment = isRight ? HorizontalAlignment.Right : HorizontalAlignment.Left,
            Margin = new Thickness(0, 0, 0, 8),
            Background = new SolidColorBrush(Color.FromArgb(
                isRight ? (byte)0x30 : (byte)0x1A,
                isRight ? (byte)0xF0 : (byte)0xFF,
                isRight ? (byte)0x88 : (byte)0xFF,
                isRight ? (byte)0x3E : (byte)0xFF)),
            BorderThickness = new Thickness(1),
            BorderBrush = new SolidColorBrush(Color.FromArgb(
                isRight ? (byte)0x44 : (byte)0x22,
                isRight ? (byte)0xF0 : (byte)0xFF,
                isRight ? (byte)0x88 : (byte)0xFF,
                isRight ? (byte)0x3E : (byte)0xFF))
        };
    }

    private Button MakeAnswerButton(CoachAnswer answer, MainViewModel vm)
    {
        var inner = new StackPanel();
        inner.Children.Add(new TextBlock
        {
            Text = answer.Label,
            FontSize = 14,
            FontWeight = FontWeights.SemiBold
        });

        if (!string.IsNullOrEmpty(answer.Description))
        {
            inner.Children.Add(new TextBlock
            {
                Text = answer.Description,
                FontSize = 11,
                Foreground = new SolidColorBrush(Color.FromRgb(0x6B, 0x76, 0x82)),
                Margin = new Thickness(0, 2, 0, 0)
            });
        }

        string aid = answer.Id;
        bool isPrimary = aid.StartsWith("apply", StringComparison.OrdinalIgnoreCase)
            || aid == "source_latest"
            || aid == "source_live";

        var btn = new Button
        {
            Content = inner,
            Cursor = Cursors.Hand,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Padding = new Thickness(14, 8, 14, 8),
            Margin = new Thickness(0, 3, 0, 3),
            BorderThickness = new Thickness(1),
            FontSize = 14,
            Background = isPrimary
                ? new SolidColorBrush(Color.FromArgb(0x1A, 0xF0, 0x88, 0x3E))
                : Brushes.Transparent,
            Foreground = isPrimary
                ? new SolidColorBrush(Color.FromRgb(0xF0, 0x88, 0x3E))
                : new SolidColorBrush(Color.FromRgb(0x8B, 0x94, 0x9E)),
            BorderBrush = isPrimary
                ? new SolidColorBrush(Color.FromArgb(0x88, 0xF0, 0x88, 0x3E))
                : new SolidColorBrush(Color.FromRgb(0x30, 0x36, 0x3D))
        };

        if (isPrimary)
            btn.FontWeight = FontWeights.SemiBold;

        btn.Click += (_, _) => OnAnswerClick(aid);
        return btn;
    }

    private void OnAnswerClick(string answerId)
    {
        VM?.CoachAnswerCommand?.Execute(answerId);
    }

    private void UpdateEmptyState(MainViewModel vm)
    {
        bool hasMsgs = vm.CoachMessages.Count > 0;
        EmptyState.Visibility = hasMsgs ? Visibility.Collapsed : Visibility.Visible;
    }

    private void UpdateInfoPanel(MainViewModel vm)
    {
        InfoProfileName.Text = vm.SelectedProfile?.Name ?? "No profile selected";
        InfoDataSource.Text = string.IsNullOrEmpty(vm.CoachDataSourceLabel)
            ? "Not started"
            : vm.CoachDataSourceLabel;

        var state = vm.CoachSessionState;
        InfoSessionState.Text = state.ToString();
        InfoSessionState.Foreground = state switch
        {
            CoachSessionState.Analyzing => new SolidColorBrush(Color.FromRgb(0xFF, 0xD6, 0x00)),
            CoachSessionState.Questioning => new SolidColorBrush(Color.FromRgb(0x4C, 0xAF, 0x50)),
            CoachSessionState.Applying => new SolidColorBrush(Color.FromRgb(0x00, 0xBC, 0xD4)),
            CoachSessionState.Summary => new SolidColorBrush(Color.FromRgb(0xE0, 0x39, 0x35)),
            _ => new SolidColorBrush(Color.FromRgb(0x8B, 0x94, 0x9E))
        };

        if (_lastMsgCount != vm.CoachMessages.Count)
        {
            _lastMsgCount = vm.CoachMessages.Count;
            RebuildMessages(vm);
            UpdateEmptyState(vm);
        }
    }
}
