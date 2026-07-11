using System.Windows;
using AcEvoFfbTuner.Core;
using AcEvoFfbTuner.Core.FfbProcessing;
using AcEvoFfbTuner.Core.SharedMemory;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AcEvoFfbTuner.ViewModels;

public enum SupportedGame
{
    AcEvo,
    Raceroom,
    AssettoCorsa,
    LeMansUltimate,
    AssettoCorsaCompetizione
}

public sealed partial class MainViewModel
{
    [ObservableProperty]
    private SupportedGame _selectedGame = SupportedGame.AcEvo;

    [ObservableProperty]
    private bool _gameAutoMode = true;

    public string GameDisplayName
    {
        get
        {
            if (GameAutoMode && _lastAutoDetectedGame.HasValue)
                return _lastAutoDetectedGame.Value switch
                {
                    SupportedGame.Raceroom => "RaceRoom (auto)",
                    SupportedGame.AssettoCorsa => "Assetto Corsa (auto)",
                    SupportedGame.LeMansUltimate => "Le Mans Ultimate (auto)",
                    SupportedGame.AssettoCorsaCompetizione => "ACC (auto)",
                    _ => "AC EVO (auto)"
                };
            return SelectedGame switch
            {
                SupportedGame.Raceroom => "RaceRoom",
                SupportedGame.AssettoCorsa => "Assetto Corsa",
                SupportedGame.LeMansUltimate => "Le Mans Ultimate",
                SupportedGame.AssettoCorsaCompetizione => "ACC",
                _ => "AC EVO"
            };
        }
    }

    public bool IsAcEvo => SelectedGame == SupportedGame.AcEvo;
    public bool IsRaceroom => SelectedGame == SupportedGame.Raceroom;
    public bool IsAssettoCorsa => SelectedGame == SupportedGame.AssettoCorsa;
    public bool IsLeMansUltimate => SelectedGame == SupportedGame.LeMansUltimate;
    public bool IsAssettoCorsaCompetizione => SelectedGame == SupportedGame.AssettoCorsaCompetizione;
    public bool IsColumnForceGame => SelectedGame is SupportedGame.Raceroom or SupportedGame.LeMansUltimate;
    public bool IsPerWheelGame => SelectedGame is SupportedGame.AcEvo or SupportedGame.AssettoCorsa or SupportedGame.AssettoCorsaCompetizione;

    public int SelectedGameIndex
    {
        get => (int)SelectedGame;
        set => SelectedGame = (SupportedGame)value;
    }

    private static ISharedMemoryReader CreateReader(SupportedGame game) => game switch
    {
        SupportedGame.Raceroom => new RaceroomSharedMemoryReader(),
        SupportedGame.AssettoCorsa => new SharedMemoryReader(),
        SupportedGame.LeMansUltimate => new LmuSharedMemoryReader(),
        SupportedGame.AssettoCorsaCompetizione => new AccSharedMemoryReader(),
        _ => new SharedMemoryReader()
    };

    private static FfbPipeline CreatePipeline(SupportedGame game) => game switch
    {
        SupportedGame.Raceroom => new R3eFfbPipeline(),
        SupportedGame.AssettoCorsa => new AcFfbPipeline(),
        SupportedGame.LeMansUltimate => new LmuFfbPipeline(),
        SupportedGame.AssettoCorsaCompetizione => new AccFfbPipeline(),
        _ => new FfbPipeline()
    };

    private SupportedGame? _lastAutoDetectedGame;

    partial void OnSelectedGameChanged(SupportedGame value)
    {
        _gameDetectorManualOverride = true;
        GameAutoMode = false;
        var wasRunning = _telemetryLoop.IsRunning;
        if (wasRunning)
            _telemetryLoop.Stop();

        _discordPresence.Detach();
        _telemetryLoop.Dispose();
        _reader.Dispose();
        _reader = CreateReader(value);
        _pipeline = CreatePipeline(value);
        OnPropertyChanged(nameof(GameDisplayName));
        OnPropertyChanged(nameof(IsAcEvo));
        OnPropertyChanged(nameof(IsRaceroom));
        OnPropertyChanged(nameof(IsAssettoCorsa));
        OnPropertyChanged(nameof(IsLeMansUltimate));
        OnPropertyChanged(nameof(IsAssettoCorsaCompetizione));
        OnPropertyChanged(nameof(IsColumnForceGame));
        OnPropertyChanged(nameof(IsPerWheelGame));

        _coachService.CurrentGame = value switch
        {
            SupportedGame.Raceroom => "RaceRoom Racing Experience",
            SupportedGame.AssettoCorsa => "Assetto Corsa",
            SupportedGame.LeMansUltimate => "Le Mans Ultimate",
            SupportedGame.AssettoCorsaCompetizione => "Assetto Corsa Competizione",
            _ => "Assetto Corsa EVO"
        };
        _coachService.IsColumnForceGame = value is SupportedGame.Raceroom or SupportedGame.LeMansUltimate;

        if (_profileManager.ActiveProfile != null)
            _profileManager.ActiveProfile.ApplyToPipeline(_pipeline);

        var newLoop = new TelemetryLoop(_reader, _pipeline, _deviceManager);
        WireTelemetryLoopEvents(newLoop);
        _telemetryLoop = newLoop;

        if (wasRunning)
        {
            _telemetryLoop.Start();
            StatusText = $"Switched to {GameDisplayName} — telemetry restarted";
        }
        else
        {
            StatusText = $"Switched to {GameDisplayName}";
        }

        AddSystemLog($"Game source changed to {GameDisplayName}");
    }

    public void SetAutoMode()
    {
        GameAutoMode = true;
        _gameDetectorManualOverride = false;
        OnPropertyChanged(nameof(GameDisplayName));
    }

    [RelayCommand]
    private void EnableAutoGameDetect()
    {
        SetAutoMode();
        StatusText = "Auto-detect mode enabled";
        AddSystemLog("Auto-detect game mode re-enabled");
    }

    public void SetDetectedGame(SupportedGame game)
    {
        _lastAutoDetectedGame = game;
        OnPropertyChanged(nameof(GameDisplayName));
    }
}
