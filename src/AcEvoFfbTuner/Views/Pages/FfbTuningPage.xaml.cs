using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using AcEvoFfbTuner.Controls;

namespace AcEvoFfbTuner.Views.Pages;

public partial class FfbTuningPage : UserControl
{
    private readonly Dictionary<string, SectionCard> _cardsByTag = new();
    private readonly Dictionary<string, FrameworkElement> _detailPanels = new();
    private List<SectionCard> _allCards = new();
    private List<SectionCard> _visibleCards = new();
    private string _currentSearch = string.Empty;
    private string _currentTag = string.Empty;
    private DispatcherTimer? _gameWatchTimer;

    private static readonly string AppDataPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AcEvoFfbTuner");

    private static readonly string StateFilePath = Path.Combine(AppDataPath, "ffb_effects_state.json");

    public FfbTuningPage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        CacheCards();
        BuildPanelMap();
        // Defer the restore + search filter: during Loaded the section cards'
        // Visibility bindings may still be attaching (the device data context
        // churns at startup when a wheel is connected), and ClearValue on a
        // mid-attach binding throws NRE inside WPF's BindingExpression.Deactivate —
        // the intermittent startup crash on machines with the wheel powered on.
        Dispatcher.BeginInvoke(DispatcherPriority.DataBind, () =>
        {
            ApplySearchFilter();
            RestoreSelection();
        });

        // Game switches flip per-slider visibility bindings; re-evaluate which
        // feel-cards have anything to show. Cheap tree walk, ~2x per second.
        _gameWatchTimer ??= new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(500) };
        _gameWatchTimer.Tick -= OnGameWatchTick;
        _gameWatchTimer.Tick += OnGameWatchTick;
        _gameWatchTimer.Start();
        Unloaded -= OnUnloaded;
        Unloaded += OnUnloaded;
    }

    private void OnUnloaded(object sender, RoutedEventArgs e) => _gameWatchTimer?.Stop();

    private void OnGameWatchTick(object? sender, EventArgs e)
    {
        ApplySearchFilter();
        if (!string.IsNullOrEmpty(_currentTag) && _cardsByTag.TryGetValue(_currentTag, out var cur)
            && cur.Visibility != Visibility.Visible)
            ShowFirstVisibleSection();
    }

    private void CacheCards()
    {
        _allCards = FindSectionCards(this).ToList();

        foreach (var card in _allCards)
        {
            if (card.Tag is string tag && !string.IsNullOrEmpty(tag))
            {
                if (!_cardsByTag.ContainsKey(tag))
                    _cardsByTag[tag] = card;
                card.Selected += OnCardSelected;
                card.IsVisibleChanged += OnCardIsVisibleChanged;
            }
        }
    }

    private void BuildPanelMap()
    {
        foreach (var tag in _cardsByTag.Keys)
        {
            if (FindName("Dtl" + tag) is FrameworkElement panel)
                _detailPanels[tag] = panel;
        }
    }

    private void OnCardSelected(object sender, RoutedEventArgs e)
    {
        if (sender is SectionCard card && card.Tag is string tag)
            ShowSection(tag);
    }

    private void OnCardIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        // React only once a selection exists (avoids startup binding churn).
        if (string.IsNullOrEmpty(_currentTag)) return;
        if (sender is not SectionCard card || card.Tag is not string tag) return;
        if (tag == _currentTag && card.Visibility != Visibility.Visible)
            ShowFirstVisibleSection();
    }

    private void ShowFirstVisibleSection()
    {
        foreach (var kv in _cardsByTag)
        {
            if (kv.Value.Visibility == Visibility.Visible)
            {
                ShowSection(kv.Key);
                return;
            }
        }
    }

    private void ShowSection(string tag)
    {
        if (!_cardsByTag.TryGetValue(tag, out var card)) return;
        if (!_detailPanels.ContainsKey(tag)) return;
        if (card.Visibility != Visibility.Visible) return;

        _currentTag = tag;

        foreach (var kv in _detailPanels)
            kv.Value.Visibility = kv.Key == tag ? Visibility.Visible : Visibility.Collapsed;

        foreach (var kv in _cardsByTag)
            kv.Value.IsSelected = kv.Key == tag;

        DetailHeader.Text = card.Title;
        DetailHeader.Foreground = card.SectionBrush ?? (Brush)FindResource("SectionOutput");
        DetailHeaderAccent.Background = card.SectionBrush ?? (Brush)FindResource("SectionOutput");

        SaveSelectedState();
    }

    private void RestoreSelection()
    {
        var tag = LoadSelectedTag();
        if (!string.IsNullOrEmpty(tag) && _cardsByTag.TryGetValue(tag, out var card) &&
            card.Visibility == Visibility.Visible && _detailPanels.ContainsKey(tag))
        {
            ShowSection(tag);
        }
        else
        {
            ShowSection("Output");
        }
    }

    private string LoadSelectedTag()
    {
        try
        {
            if (!File.Exists(StateFilePath)) return string.Empty;
            var json = File.ReadAllText(StateFilePath);
            var loaded = JsonSerializer.Deserialize<Dictionary<string, bool>>(json);
            if (loaded == null) return string.Empty;
            const string prefix = "selected::";
            foreach (var kv in loaded)
            {
                if (kv.Key.StartsWith(prefix) && kv.Value)
                    return kv.Key.Substring(prefix.Length);
            }
        }
        catch
        {
        }
        return string.Empty;
    }

    private void SaveSelectedState()
    {
        try
        {
            Directory.CreateDirectory(AppDataPath);
            var state = new Dictionary<string, bool> { [$"selected::{_currentTag}"] = true };
            File.WriteAllText(StateFilePath, JsonSerializer.Serialize(state));
        }
        catch
        {
        }
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        _currentSearch = SearchBox?.Text ?? string.Empty;
        ApplySearchFilter();
    }

    /// <summary>
    /// Card visibility is computed here (not bound): a feel-card is shown when
    /// its detail panel contains at least one control that is visible for the
    /// current game, AND it matches the search text (title, subtitle, or any
    /// slider label / description / end-label inside it). Re-evaluated on a
    /// light timer so game switches update the list without per-card bindings.
    /// </summary>
    private void ApplySearchFilter()
    {
        if (_allCards == null || _allCards.Count == 0) return;

        var query = _currentSearch?.Trim() ?? string.Empty;
        _visibleCards = new List<SectionCard>();

        foreach (var card in _allCards)
        {
            try
            {
                if (card.Tag is not string tag || !_detailPanels.TryGetValue(tag, out var panel))
                    continue;

                bool hasContent = HasVisibleControl(panel);
                bool matches = string.IsNullOrEmpty(query)
                    || Contains(card.Title, query)
                    || Contains(card.ToolTip as string, query)
                    || PanelMatches(panel, query);

                var want = hasContent && matches ? Visibility.Visible : Visibility.Collapsed;
                if (card.Visibility != want)
                    card.Visibility = want;
                if (want == Visibility.Visible)
                    _visibleCards.Add(card);
            }
            catch (Exception)
            {
                // Never let the filter take the app down mid-binding churn.
            }
        }

        UpdateFilterCounter();
    }

    private static bool Contains(string? text, string query) =>
        !string.IsNullOrEmpty(text) && text.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0;

    private static bool IsInteractive(object o) =>
        o is LabeledSlider || o is CheckBox || o is RadioButton || o is ComboBox;

    /// <summary>True if any slider/toggle under <paramref name="root"/> is not collapsed by its own (game) visibility or an ancestor's.</summary>
    private static bool HasVisibleControl(DependencyObject root)
    {
        foreach (var child in LogicalTreeHelper.GetChildren(root))
        {
            if (child is UIElement ui && ui.Visibility == Visibility.Collapsed) continue;
            if (IsInteractive(child)) return true;
            if (child is DependencyObject d && HasVisibleControl(d)) return true;
        }
        return false;
    }

    private static bool PanelMatches(DependencyObject root, string query)
    {
        foreach (var child in LogicalTreeHelper.GetChildren(root))
        {
            if (child is UIElement ui && ui.Visibility == Visibility.Collapsed) continue;
            switch (child)
            {
                case LabeledSlider ls when Contains(ls.Label, query) || Contains(ls.Description, query)
                                           || Contains(ls.LowLabel, query) || Contains(ls.HighLabel, query):
                    return true;
                case TextBlock tb when Contains(tb.Text, query):
                    return true;
                case Expander ex when Contains(ex.Header as string, query):
                    return true;
            }
            if (child is DependencyObject d && PanelMatches(d, query)) return true;
        }
        return false;
    }

    private void UpdateFilterCounter()
    {
        if (FilterCounter == null) return;
        var total = _allCards.Count(c => c.Tag is string t && _detailPanels.TryGetValue(t, out var p) && HasVisibleControl(p));
        var visible = _visibleCards.Count;
        if (string.IsNullOrEmpty(_currentSearch?.Trim()))
            FilterCounter.Text = $"{total} sections";
        else
            FilterCounter.Text = $"{visible} / {total} match";
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
}