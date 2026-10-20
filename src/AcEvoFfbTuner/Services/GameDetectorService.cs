using System.Diagnostics;
using System.Linq;
using AcEvoFfbTuner.ViewModels;

namespace AcEvoFfbTuner.Services;

public sealed class GameDetectorService : IDisposable
{
    private readonly Dictionary<string, SupportedGame> _processMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["acevo"] = SupportedGame.AcEvo,
        ["ac_evo"] = SupportedGame.AcEvo,
        ["ac_ev"] = SupportedGame.AcEvo,
        ["ac2"] = SupportedGame.AcEvo,
        ["assettocorsaevo"] = SupportedGame.AcEvo,
        ["raceroom"] = SupportedGame.Raceroom,
        ["raceroomracing"] = SupportedGame.Raceroom,
        ["raceroomracingexperience"] = SupportedGame.Raceroom,
        ["rrre"] = SupportedGame.Raceroom,
        ["rrre64"] = SupportedGame.Raceroom,
        ["r3e"] = SupportedGame.Raceroom,
        ["acs"] = SupportedGame.AssettoCorsa,
        ["assettocorsa"] = SupportedGame.AssettoCorsa,
        ["lmu"] = SupportedGame.LeMansUltimate,
        ["lmu64"] = SupportedGame.LeMansUltimate,
        ["le mans ultimate"] = SupportedGame.LeMansUltimate,
        ["acc"] = SupportedGame.AssettoCorsaCompetizione,
        ["acc2"] = SupportedGame.AssettoCorsaCompetizione,
        ["assettocorsacompetizione"] = SupportedGame.AssettoCorsaCompetizione
    };

    private readonly HashSet<string> _unsupportedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "irating",
        "iracing",
        "iracing64",
        "rfactor2",
        "rFactor2",
        "ams2",
        "automobilista2",
        "pcars",
        "pcars2",
        "projectcars",
        "projectcars2",
        "dirtrally",
        "dirtrally2",
        "dirtrally2.0",
        "lfs",
        "liveforspeed",
        "RichardBurnsRally",
        "gtr2",
        "gtlegends",
        "race07"
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
        // 1. Check supported games. Count DISTINCT games, not process-map keys:
        // AC1 runs both acs.exe and AssettoCorsa.exe (same game) — counting
        // keys made AC1 look "ambiguous" and auto-detect never fired.
        var foundGames = new HashSet<SupportedGame>();

        foreach (var kvp in _processMap)
        {
            ct.ThrowIfCancellationRequested();
            var processes = Process.GetProcessesByName(kvp.Key);
            if (processes.Length > 0)
            {
                foundGames.Add(kvp.Value);
                foreach (var p in processes) p.Dispose();
            }
        }

        // Multiple DISTINCT supported games → ambiguous, skip
        if (foundGames.Count > 1)
            return;

        // Exactly one supported game found
        if (foundGames.Count == 1)
        {
            var game = foundGames.First();
            if (_lastDetectedGame != game)
            {
                _lastDetectedGame = game;
                GameDetected?.Invoke(game);
            }
            return;
        }

        // 2. No supported games — check for known unsupported sim racing games
        foreach (var name in _unsupportedNames)
        {
            ct.ThrowIfCancellationRequested();
            var processes = Process.GetProcessesByName(name);
            if (processes.Length > 0)
            {
                foreach (var p in processes) p.Dispose();
                if (_lastDetectedGame != SupportedGame.Unsupported)
                {
                    _lastDetectedGame = SupportedGame.Unsupported;
                    GameDetected?.Invoke(SupportedGame.Unsupported);
                }
                return;
            }
        }

        // 3. Nothing game-related running at all
        if (_lastDetectedGame.HasValue)
        {
            _lastDetectedGame = null;
            GameExitedAll?.Invoke();
        }
    }

    public void Dispose()
    {
        Stop();
        _cts?.Dispose();
    }
}
