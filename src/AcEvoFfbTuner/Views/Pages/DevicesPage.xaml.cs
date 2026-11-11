using AcEvoFfbTuner.Services;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using AcEvoFfbTuner.Controls;
using AcEvoFfbTuner.ViewModels;

namespace AcEvoFfbTuner.Views.Pages;

public partial class DevicesPage : UserControl
{
    private MainViewModel? _vm;
    private readonly Dictionary<string, SectionCard> _cardsByTag = new();
    private readonly Dictionary<string, FrameworkElement> _panels = new();

    public event EventHandler? Hf8MotorTestRequested;

    public DevicesPage()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        CacheCards();
        BuildPanelMap();
        ShowSection("Wheelbase");
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
        _panels["Wheelbase"] = WheelPanel;
        _panels["Led"] = LedPanel;
        _panels["Haptic"] = HapticPanel;
        _panels["Buttons"] = ButtonsPanel;
        _panels["Pedals"] = PedalPanel;
        _panels["GameConfig"] = GameConfigPanel;
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

        if (tag == "Led")
            UpdateLedPreview();

        if (tag == "Pedals")
            SelectDefaultPedalHapticTab();

        // Entering Game Config re-checks the vJoy driver + bridge state, so an
        // install done outside this view (setup component, manual download,
        // reboot) is reflected immediately.
        if (tag == "GameConfig" && DataContext is MainViewModel vm)
        {
            try { vm.RefreshGameConfigState(); } catch { }
        }
    }

    // ── Pedals step 3: one haptic hardware card at a time ──────────────
    private bool _pedalTabChosenByUser;

    /// <summary>
    /// Opens on whichever pedal haptic box is already connected (serial box if
    /// only that one is), otherwise MOZA PHF. A manual pick sticks for the session.
    /// </summary>
    private void SelectDefaultPedalHapticTab()
    {
        if (_pedalTabChosenByUser || HapticTabMoza == null) return;
        bool serialOnly = DataContext is MainViewModel vm && vm.OsoyooIsConnected && !vm.MozaPhfIsConnected;
        (serialOnly ? HapticTabSerial : HapticTabMoza).IsChecked = true;
        _pedalTabChosenByUser = false;
    }

    private void OnPedalHapticTabChanged(object sender, RoutedEventArgs e)
    {
        if (MozaHapticCard == null || SerialHapticCard == null) return;
        bool moza = HapticTabMoza.IsChecked == true;
        MozaHapticCard.Visibility = moza ? Visibility.Visible : Visibility.Collapsed;
        SerialHapticCard.Visibility = moza ? Visibility.Collapsed : Visibility.Visible;
        // Vibration pitch per effect is a MOZA PHF setting only.
        if (PhfFrequencyBox != null)
            PhfFrequencyBox.Visibility = moza ? Visibility.Visible : Visibility.Collapsed;
        if (IsLoaded && (sender as RadioButton)?.IsMouseOver == true)
            _pedalTabChosenByUser = true;
    }

    private void OnOpenPedalsSection(object sender, RoutedEventArgs e)
    {
        ShowSection("Pedals");
        SelectSubTab("PedTabs", "Ped_Calibrate");
    }

    // ── Sub-tabs (Pedals, Assetto Corsa Setup) ─────────────────────────
    // Each RadioButton's Tag names the panel it shows; panels sharing the
    // tag prefix ("Ped_", "Ac_") are hidden when another tab is picked.
    private static readonly string[] SubTabPanels =
    {
        "Ped_Input", "Ped_Calibrate", "Ped_Haptics", "Ped_Tune",
        "Ac_Driver", "Ac_Pedals", "Ac_Buttons", "Ac_Apply",
    };

    private void OnSubTabChecked(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton rb || rb.Tag is not string target) return;
        string prefix = target[..(target.IndexOf('_') + 1)];
        foreach (var name in SubTabPanels)
        {
            if (!name.StartsWith(prefix)) continue;
            if (FindName(name) is FrameworkElement panel)
                panel.Visibility = name == target ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private void SelectSubTab(string group, string target)
    {
        foreach (var rb in FindRadios(this))
            if (rb.GroupName == group && rb.Tag as string == target) { rb.IsChecked = true; return; }
    }

    private static IEnumerable<RadioButton> FindRadios(DependencyObject parent)
    {
        foreach (var child in LogicalTreeHelper.GetChildren(parent))
        {
            if (child is RadioButton rb) yield return rb;
            if (child is DependencyObject d)
                foreach (var nested in FindRadios(d)) yield return nested;
        }
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
            _vm.PropertyChanged -= OnVmPropertyChanged;

        _vm = DataContext as MainViewModel;

        if (_vm != null)
        {
            _vm.PropertyChanged += OnVmPropertyChanged;
        }
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.ActiveLedCount) ||
            e.PropertyName == nameof(MainViewModel.LedVisibleCount))
        {
            Dispatcher.BeginInvoke(UpdateLedPreview);
        }
    }

    private void UpdateLedPreview()
    {
        if (LedPreviewBar == null || _vm == null) return;

        int ledCount = _vm.LedVisibleCount;
        if (ledCount <= 0) ledCount = 10;
        int activeCount = _vm.ActiveLedCount;

        if (LedPreviewBar.Children.Count != ledCount)
            BuildLedDots(ledCount);

        var offColor = ThemeManager.Color("#FF21262D");
        var colors = new[]
        {
            Color.FromRgb(0x00, 0xE6, 0x76),
            Color.FromRgb(0x00, 0xE6, 0x76),
            Color.FromRgb(0x66, 0xBB, 0x6A),
            Color.FromRgb(0xFF, 0xD6, 0x00),
            Color.FromRgb(0xFF, 0xD6, 0x00),
            Color.FromRgb(0xFF, 0x98, 0x00),
            Color.FromRgb(0xFF, 0x98, 0x00),
            Color.FromRgb(0xF4, 0x43, 0x36),
            Color.FromRgb(0xF4, 0x43, 0x36),
            Color.FromRgb(0xF4, 0x43, 0x36),
        };

        for (int i = 0; i < LedPreviewBar.Children.Count; i++)
        {
            if (LedPreviewBar.Children[i] is Ellipse dot)
            {
                bool active = i < activeCount;
                Color c = active && i < colors.Length ? colors[i] : offColor;
                dot.Fill = new SolidColorBrush(c);
                dot.Opacity = active ? 1.0 : 0.4;
                dot.StrokeThickness = active ? 2 : 1;
                dot.Stroke = active
                    ? new SolidColorBrush(Color.FromArgb(0x80, c.R, c.G, c.B))
                    : ThemeManager.Brush("#FF30363D");
            }
        }
    }

    private void BuildLedDots(int count)
    {
        LedPreviewBar.Children.Clear();
        for (int i = 0; i < count; i++)
        {
            var ellipse = new Ellipse
            {
                Width = 22,
                Height = 22,
                Margin = new Thickness(6, 0, 6, 0),
                Fill = ThemeManager.Brush("#FF21262D"),
                Stroke = ThemeManager.Brush("#FF30363D"),
                StrokeThickness = 1,
            };
            LedPreviewBar.Children.Add(ellipse);
        }
    }

    private void OnOpenMotorTest(object sender, RoutedEventArgs e)
    {
        Hf8MotorTestRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OnSavePedalConfig(object sender, RoutedEventArgs e)
    {
        _vm?.SavePedalConfig();
    }
}