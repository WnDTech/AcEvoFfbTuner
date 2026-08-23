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
            RestoreSelection();
            ApplySearchFilter();
        });
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
        if (string.IsNullOrEmpty(_currentTag)) return;
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
            ShowSection("MasterOutput");
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

    private void ApplySearchFilter()
    {
        if (_allCards == null || _allCards.Count == 0) return;

        var query = _currentSearch?.Trim() ?? string.Empty;
        _visibleCards = new List<SectionCard>();

        foreach (var card in _allCards)
        {
            try
            {
                if (string.IsNullOrEmpty(query))
                {
                    // No search active — clear any locally-set visibility so the
                    // XAML binding (game-specific visibility) controls the card.
                    card.ClearValue(VisibilityProperty);
                    _visibleCards.Add(card);
                }
                else
                {
                    var titleMatch = card.Title?.IndexOf(query, System.StringComparison.OrdinalIgnoreCase) >= 0;
                    if (titleMatch)
                    {
                        card.ClearValue(VisibilityProperty);
                        _visibleCards.Add(card);
                    }
                    else
                    {
                        card.Visibility = Visibility.Collapsed;
                    }
                }
            }
            catch (Exception)
            {
                // WPF can throw internally when a card's Visibility binding is
                // mid-detach (device data context updates) — never let the
                // search filter take the app down.
            }
        }

        UpdateFilterCounter();
    }

    private void UpdateFilterCounter()
    {
        if (FilterCounter == null) return;
        var total = _allCards.Count;
        var visible = _visibleCards.Count;
        if (string.IsNullOrEmpty(_currentSearch?.Trim()))
            FilterCounter.Text = $"{total} sections";
        else
            FilterCounter.Text = $"{visible} / {total} sections match";
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