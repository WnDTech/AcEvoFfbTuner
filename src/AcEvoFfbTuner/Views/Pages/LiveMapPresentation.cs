using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using AcEvoFfbTuner.Core.SharedMemory;

namespace AcEvoFfbTuner.Views.Pages;

/// <summary>
/// Live Map class colours — one palette for every game, keyed by <see cref="CarClassCategory"/>
/// (WEC broadcast conventions where they exist: Hypercar red, LMP2 blue, LMP3 purple,
/// GTE green, GT3 orange). Unrecognised class names get a stable colour from a spare palette.
/// </summary>
public static class CarClassPalette
{
    public const uint Unknown = 0xFF8B949E;

    private static readonly uint[] Spare = { 0xFF79C0FF, 0xFFD2A8FF, 0xFFFFA657, 0xFF7EE787, 0xFFFF7B72, 0xFF56D4DD };

    public static uint ArgbFor(CarClassCategory category, string rawClass) => category switch
    {
        CarClassCategory.Hypercar => 0xFFE5484D,
        CarClassCategory.Lmp2 => 0xFF3E8BFF,
        CarClassCategory.Lmp3 => 0xFFA371F7,
        CarClassCategory.Gte => 0xFF3FB950,
        CarClassCategory.Gt3 => 0xFFF0883E,
        CarClassCategory.Gt4 => 0xFF22D3EE,
        CarClassCategory.Touring => 0xFFF778BA,
        CarClassCategory.Formula => 0xFFE6EDF3,
        CarClassCategory.Cup => 0xFFD29922,
        CarClassCategory.Other => Spare[(int)((uint)StableHash(rawClass) % (uint)Spare.Length)],
        _ => Unknown,
    };

    /// <summary>Legend/list label: the family name, or the game's own name for "Other".</summary>
    public static string LabelFor(CarClassCategory category, string rawClass) =>
        category == CarClassCategory.Other && rawClass.Length > 0 ? rawClass : CarClasses.Label(category);

    private static readonly Dictionary<uint, SolidColorBrush> _brushes = new();

    public static SolidColorBrush Brush(uint argb)
    {
        if (_brushes.TryGetValue(argb, out var b)) return b;
        b = new SolidColorBrush(Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb));
        b.Freeze();
        _brushes[argb] = b;
        return b;
    }

    private static int StableHash(string s)
    {
        unchecked
        {
            int h = 23;
            foreach (var c in s) h = h * 31 + char.ToLowerInvariant(c);
            return h;
        }
    }
}

/// <summary>One chip in the map legend: a filled class dot, or a hollow ring for an active flag.</summary>
public sealed class LegendItem
{
    public string Label { get; init; } = "";
    public int Count { get; init; }
    public SolidColorBrush Brush { get; init; } = CarClassPalette.Brush(CarClassPalette.Unknown);
    public bool IsRing { get; init; }
    public SolidColorBrush DotFill => IsRing ? CarClassPalette.Brush(0x00000000) : Brush;
    public SolidColorBrush DotStroke => IsRing ? Brush : CarClassPalette.Brush(0xC0000000);
    public double DotStrokeThickness => IsRing ? 2.5 : 1;
}

/// <summary>Ring colours / labels for <see cref="CarFlag"/> (must match MapsuiMapControl's rings).</summary>
public static class CarFlagPalette
{
    public static uint ArgbFor(CarFlag flag) => flag switch
    {
        CarFlag.Yellow => 0xFFFFD33D,
        CarFlag.Blue => 0xFF2F81F7,
        CarFlag.Penalty => 0xFF000000,
        CarFlag.Checkered => 0xFFFFFFFF,
        CarFlag.Retired => 0xFF6E7681,
        _ => 0xFFFFFFFF,
    };

    public static string Label(CarFlag flag) => flag switch
    {
        CarFlag.Yellow => "Yellow",
        CarFlag.Blue => "Blue flag",
        CarFlag.Penalty => "Penalty",
        CarFlag.Checkered => "Finished",
        CarFlag.Retired => "Retired",
        _ => "",
    };

    /// <summary>Short Field-list tag text (flags first, else PIT).</summary>
    public static string Tag(CarFlag flag, bool inPits) => flag switch
    {
        CarFlag.Yellow => "YELLOW",
        CarFlag.Blue => "BLUE",
        CarFlag.Penalty => "PEN",
        CarFlag.Checkered => "FIN",
        CarFlag.Retired => "DNF",
        _ => inPits ? "PIT" : "",
    };

    /// <summary>Tag text colour: the flag colour, except black/white which read badly on the tag chip.</summary>
    public static uint TagArgb(CarFlag flag, bool inPits) => flag switch
    {
        CarFlag.Yellow => 0xFFFFD33D,
        CarFlag.Blue => 0xFF58A6FF,
        CarFlag.Penalty => 0xFFFF7B72,
        CarFlag.Checkered => 0xFFE6EDF3,
        CarFlag.Retired => 0xFF8B949E,
        _ => inPits ? 0xFFE6C340 : 0xFF8B949E,
    };
}

/// <summary>One row of the Field (standings) panel; updated in place so the list doesn't flicker.</summary>
public sealed class FieldRow : INotifyPropertyChanged
{
    private int _id;
    private string _place = "", _name = "", _classLabel = "", _number = "", _tag = "";
    private SolidColorBrush _classBrush = CarClassPalette.Brush(CarClassPalette.Unknown);
    private SolidColorBrush _tagBrush = CarClassPalette.Brush(CarClassPalette.Unknown);
    private bool _isPlayer;

    public int Id { get => _id; set => Set(ref _id, value); }
    public string Place { get => _place; set => Set(ref _place, value); }
    public string Name { get => _name; set => Set(ref _name, value); }
    /// <summary>"#12" or empty.</summary>
    public string Number { get => _number; set => Set(ref _number, value); }
    public string ClassLabel { get => _classLabel; set => Set(ref _classLabel, value); }
    public SolidColorBrush ClassBrush { get => _classBrush; set => Set(ref _classBrush, value); }
    public bool IsPlayer { get => _isPlayer; set => Set(ref _isPlayer, value); }
    /// <summary>Status chip: flag (BLUE / YELLOW / PEN / FIN / DNF) or PIT; empty hides it.</summary>
    public string Tag { get => _tag; set { if (Set(ref _tag, value)) OnChanged(nameof(TagVisibility)); } }
    public SolidColorBrush TagBrush { get => _tagBrush; set => Set(ref _tagBrush, value); }
    public Visibility TagVisibility => _tag.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

    public event PropertyChangedEventHandler? PropertyChanged;

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnChanged(name);
        return true;
    }

    private void OnChanged(string? name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
