using CommunityToolkit.Mvvm.ComponentModel;

namespace AcEvoFfbTuner.Models;

public enum DeviceIconType
{
    Wheelbase,
    Wheel,
    Pedals,
    Haptics,
    Game
}

public sealed class DeviceStatus : ObservableObject
{
    public DeviceIconType IconType { get; init; }

    private bool _isConnected;
    public bool IsConnected
    {
        get => _isConnected;
        set => SetProperty(ref _isConnected, value);
    }

    private string _name = "";
    public string Name
    {
        get => _name;
        set => SetProperty(ref _name, value);
    }

    public string TooltipText => $"{Name}\n{(IsConnected ? "Connected" : "Disconnected")}";
}
