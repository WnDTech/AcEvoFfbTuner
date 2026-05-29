using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using AcEvoFfbTuner.Controls;

namespace AcEvoFfbTuner.Views.Pages;

public partial class FfbTuningPage : UserControl
{
    private readonly Dictionary<string, bool> _sectionExpandedState = new();
    private readonly HashSet<SectionCard> _subscribedCards = new();

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
        LoadExpandedState();
        ApplyExpandedState();
    }

    private void ApplyExpandedState()
    {
        var cards = FindSectionCards(this).ToList();
        if (cards.Count == 0) return;

        foreach (var card in cards)
        {
            var key = $"section::{card.Title}";

            if (_sectionExpandedState.TryGetValue(key, out var expanded))
                card.IsExpanded = expanded;

            if (_subscribedCards.Add(card))
            {
                var dpd = DependencyPropertyDescriptor.FromProperty(
                    SectionCard.IsExpandedProperty, typeof(SectionCard));
                dpd.AddValueChanged(card, OnCardExpandedChanged);
            }
        }
    }

    private void OnCardExpandedChanged(object? sender, System.EventArgs e)
    {
        if (sender is not SectionCard card) return;

        var key = $"section::{card.Title}";
        _sectionExpandedState[key] = card.IsExpanded;
        SaveExpandedState();
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

    private void LoadExpandedState()
    {
        try
        {
            if (File.Exists(StateFilePath))
            {
                var json = File.ReadAllText(StateFilePath);
                var loaded = JsonSerializer.Deserialize<Dictionary<string, bool>>(json);
                if (loaded != null)
                {
                    _sectionExpandedState.Clear();
                    foreach (var kv in loaded)
                        _sectionExpandedState[kv.Key] = kv.Value;
                }
            }
        }
        catch
        {
            _sectionExpandedState.Clear();
        }
    }

    private void SaveExpandedState()
    {
        try
        {
            Directory.CreateDirectory(AppDataPath);
            var json = JsonSerializer.Serialize(_sectionExpandedState);
            File.WriteAllText(StateFilePath, json);
        }
        catch
        {
        }
    }
}
