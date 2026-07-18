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
    private MainViewModel? _cachedVm;

    public FFBCoachPage()
    {
        InitializeComponent();
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible)
            {
                SubscribeToVm();
                Dispatcher.BeginInvoke(() => RefreshUI());
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
        Dispatcher.BeginInvoke(() =>
        {
            RefreshUI();
            ChatScrollViewer.ScrollToBottom();
        });
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainViewModel.CoachIsBusy)
            or nameof(MainViewModel.CoachDataSourceLabel)
            or nameof(MainViewModel.SelectedProfile))
        {
            Dispatcher.BeginInvoke(() => RefreshUI());
        }
    }

    public void OnPageActivated()
    {
        SubscribeToVm();
        RefreshUI();
    }

    private void OnInputKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && sender is TextBox tb && !string.IsNullOrWhiteSpace(tb.Text))
        {
            VM?.CoachSendTextCommand?.Execute(null);
            e.Handled = true;
        }
    }

    private void OnChatWithAi(object sender, RoutedEventArgs e)
    {
        VM?.CoachAnswerCommand?.Execute("source_chat");
    }

    private void OnUseLiveMonitor(object sender, RoutedEventArgs e)
    {
        VM?.CoachAnswerCommand?.Execute("source_monitor");
    }

    private void OnUseLatestSnapshot(object sender, RoutedEventArgs e)
    {
        VM?.CoachUseLatestSnapshotCommand?.Execute(null);
    }

    private void OnRestartSession(object sender, RoutedEventArgs e)
    {
        VM?.CoachRestartCommand?.Execute(null);
    }

    private void OnShowSummary(object sender, RoutedEventArgs e)
    {
        VM?.CoachAnswerCommand?.Execute("finish");
    }

    private void RefreshUI()
    {
        var vm = VM;
        if (vm == null) return;

        bool hasMsgs = vm.CoachMessages.Count > 0;

        // Toggle empty state vs input bar
        EmptyState.Visibility = hasMsgs ? Visibility.Collapsed : Visibility.Visible;
        InputBar.Visibility = hasMsgs ? Visibility.Visible : Visibility.Collapsed;

        if (hasMsgs && !CoachInputBox.IsFocused)
            CoachInputBox.Focus();
    }
}
