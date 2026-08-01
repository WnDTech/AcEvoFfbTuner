using System.Collections.ObjectModel;
using System.Windows;
using AcEvoFfbTuner.Services;

namespace AcEvoFfbTuner.Views;

public partial class FeedbackChatDialog : Window
{
    private readonly ObservableCollection<FeedbackReport> _reports = [];
    private int _loadSeq;

    public FeedbackChatDialog()
    {
        InitializeComponent();
        Loaded += (_, _) => LoadReports();
    }

    private void LoadReports()
    {
        var selectedId = (ReportCombo.SelectedItem as FeedbackReport)?.ReportId;

        _reports.Clear();
        foreach (var r in FeedbackRelayService.GetActiveReports())
            _reports.Add(r);

        ReportCombo.ItemsSource = _reports;

        if (_reports.Count == 0)
        {
            StatusText.Text = "No active reports";
            ConversationBox.ItemsSource = null;
            ReplyBox.IsEnabled = false;
            return;
        }

        ReplyBox.IsEnabled = true;
        var keep = selectedId != null
            ? _reports.FirstOrDefault(r => r.ReportId == selectedId)
            : null;
        ReportCombo.SelectedItem = keep;
        if (ReportCombo.SelectedItem == null)
            ReportCombo.SelectedIndex = 0;
    }

    private async void OnReportSelected(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (ReportCombo.SelectedItem is not FeedbackReport report) return;
        await LoadConversation(report);
    }

    private async Task LoadConversation(FeedbackReport report)
    {
        var seq = ++_loadSeq;
        StatusText.Text = "Loading...";
        var messages = await FeedbackRelayService.GetConversationAsync(report.ReportId);
        if (seq != _loadSeq) return;
        if (ReportCombo.SelectedItem is not FeedbackReport current || current.ReportId != report.ReportId) return;

        if (messages == null)
        {
            ConversationBox.ItemsSource = null;
            StatusText.Text = $"Relay unreachable ({FeedbackRelayService.ResolvedRelayUrl}) — start the relay";
            return;
        }

        ConversationBox.ItemsSource = null;
        ConversationBox.ItemsSource = messages.Select(m =>
            $"{(m.IsFix ? "[FIX] " : "")}[{m.At}] {m.Author}: {m.Content}").ToList();
        StatusText.Text = messages.Count > 0 ? $"{messages.Count} message(s)" : "No replies yet — wait for support";
    }

    private async void OnRefresh(object sender, RoutedEventArgs e)
    {
        await FeedbackRelayService.PollForRepliesAsync();
        var before = (ReportCombo.SelectedItem as FeedbackReport)?.ReportId;
        LoadReports();
        var after = (ReportCombo.SelectedItem as FeedbackReport)?.ReportId;
        if (before == after && ReportCombo.SelectedItem is FeedbackReport report)
            await LoadConversation(report);
    }

    private async void OnSend(object sender, RoutedEventArgs e)
    {
        await SendReplyAsync();
    }

    private async void OnReplyKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Enter)
        {
            e.Handled = true;
            await SendReplyAsync();
        }
    }

    private async Task SendReplyAsync()
    {
        if (ReportCombo.SelectedItem is not FeedbackReport report) return;
        var text = ReplyBox.Text?.Trim();
        if (string.IsNullOrWhiteSpace(text))
        {
            ReplyBox.Focus();
            return;
        }

        StatusText.Text = "Sending...";
        var ok = await FeedbackRelayService.SendReplyAsync(report.ReportId, text);
        if (ok)
        {
            ReplyBox.Text = "";
            StatusText.Text = "Sent";
            await LoadConversation(report);
        }
        else
        {
            StatusText.Text = "Send failed — check relay";
        }
    }

    private void OnClose(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
