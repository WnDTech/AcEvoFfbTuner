using System.Diagnostics;
using AcEvoFfbTuner.ViewModels;

namespace AcEvoFfbTuner.Services;

public sealed class GameDetectorService : IDisposable
{
    private readonly Dictionary<string, SupportedGame> _processMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["acevo"] = SupportedGame.AcEvo,
        ["ac_evo"] = SupportedGame.AcEvo,
        ["ac_ev"] = SupportedGame.AcEvo,
        ["raceroom"] = SupportedGame.Raceroom,
        ["raceroomracing"] = SupportedGame.Raceroom,
        ["rrre"] = SupportedGame.Raceroom,
        ["acs"] = SupportedGame.AssettoCorsa,
        ["assettocorsa"] = SupportedGame.AssettoCorsa,
        ["lmu"] = SupportedGame.LeMansUltimate,
        ["lmu64"] = SupportedGame.LeMansUltimate,
        ["le mans ultimate"] = SupportedGame.LeMansUltimate,
        ["acc"] = SupportedGame.AssettoCorsaCompetizione,
        ["acc2"] = SupportedGame.AssettoCorsaCompetizione,
        ["assettocorsacompetizione"] = SupportedGame.AssettoCorsaCompetizione
    };

    private CancellationTokenSource? _cts;
    private Task? _pollTask;
    private SupportedGame? _lastDetectedGame;

    public event Action<SupportedGame>? GameDetected;
    public event Action? GameExitedAll;

    public bool IsRunning => _pollTask != null;

    public void Start()
    {
        if (_pollTask != null) return;
        _cts = new CancellationTokenSource();
        _pollTask = Task.Run(() => PollLoop(_cts.Token));
    }

    public void Stop()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
        _pollTask = null;
        _lastDetectedGame = null;
    }

    private async Task PollLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                DetectGames(ct);
                await Task.Delay(2000, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private void DetectGames(CancellationToken ct)
    {
        var detected = new List<SupportedGame>();

        foreach (var kvp in _processMap)
        {
            ct.ThrowIfCancellationRequested();
            var processes = Process.GetProcessesByName(kvp.Key);
            if (processes.Length > 0)
            {
                detected.Add(kvp.Value);
                foreach (var p in processes) p.Dispose();
            }
        }

        if (detected.Count == 0)
        {
            if (_lastDetectedGame.HasValue)
            {
                _lastDetectedGame = null;
                GameExitedAll?.Invoke();
            }
            return;
        }

        if (detected.Count > 1)
        {
            return;
        }

        var game = detected[0];
        if (_lastDetectedGame != game)
        {
            _lastDetectedGame = game;
            GameDetected?.Invoke(game);
        }
    }

    public void Dispose()
    {
        Stop();
        _cts?.Dispose();
    }
}
