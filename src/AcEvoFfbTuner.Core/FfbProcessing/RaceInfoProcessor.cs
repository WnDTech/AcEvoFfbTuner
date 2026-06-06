using System;
using System.Linq;
using System.Collections.Generic;
using AcEvoFfbTuner.Core.SharedMemory.Structs;

namespace AcEvoFfbTuner.Core.FfbProcessing;

public sealed class RaceInfoProcessor
{
    private readonly Queue<float> _fuelPerLapHistory = new(capacity: 15);
    private float _lastFuelLevel;

    private readonly Queue<float> _gapHistoryAhead = new(capacity: 20);
    private readonly Queue<float> _gapHistoryBehind = new(capacity: 20);

    private int _startingPosition = -1;
    private int _lastPosition = -1;
    private int _positionChange;

    private string _lastPhaseName = "";
    private bool _wasInPitLane;

    private int _lastCompletedLaps;

    public bool IsInitialized { get; private set; }

    public void Reset()
    {
        _fuelPerLapHistory.Clear();
        _gapHistoryAhead.Clear();
        _gapHistoryBehind.Clear();
        _startingPosition = -1;
        _lastPosition = -1;
        _positionChange = 0;
        _lastPhaseName = "";
        _wasInPitLane = false;
        _lastFuelLevel = 0;
        _lastCompletedLaps = 0;
        IsInitialized = false;
    }

    public void Process(SPageFilePhysicsEvo physics, SPageFileGraphicEvo graphics, out RaceInfoOutput output)
    {
        output = new RaceInfoOutput();

        int currentLap = graphics.SessionState.CurrentLap;
        if (currentLap != _lastCompletedLaps && physics.Fuel > 0 && _lastFuelLevel > 0)
        {
            float fuelUsed = _lastFuelLevel - physics.Fuel;
            if (fuelUsed > 0 && fuelUsed < 50f)
            {
                _fuelPerLapHistory.Enqueue(fuelUsed);
                while (_fuelPerLapHistory.Count > 15)
                    _fuelPerLapHistory.TryDequeue(out _);
            }
            _lastCompletedLaps = currentLap;
        }
        _lastFuelLevel = physics.Fuel;

        float avgFuelPerLap = _fuelPerLapHistory.Count > 0
            ? _fuelPerLapHistory.Average()
            : graphics.FuelLiterPerLap;

        output.FuelLevel = physics.Fuel;
        output.FuelPerLap = avgFuelPerLap;
        output.FuelLapsRemaining = avgFuelPerLap > 0.001f
            ? physics.Fuel / avgFuelPerLap
            : 0f;

        output.GapAhead = graphics.GapAhead;
        output.GapBehind = graphics.GapBehind;
        output.GapTrendAhead = GetGapTrend(graphics.GapAhead, _gapHistoryAhead);
        output.GapTrendBehind = GetGapTrend(graphics.GapBehind, _gapHistoryBehind);

        int pos = (int)graphics.CurrentPos;
        if (pos > 0 && graphics.SessionState.CurrentLap >= 1 && graphics.GapAhead != 0)
        {
            if (_startingPosition < 0)
            {
                _startingPosition = pos;
                _lastPosition = pos;
            }
            if (pos != _lastPosition && pos > 0)
            {
                _positionChange = _startingPosition - pos;
                _lastPosition = pos;
            }
        }
        output.Position = pos;
        output.TotalDrivers = (int)graphics.TotalDrivers;
        output.PositionChange = _positionChange;

        string phase = DecodeBytes(graphics.SessionState.PhaseName);
        bool phaseChanged = phase != _lastPhaseName && !string.IsNullOrEmpty(phase);
        _lastPhaseName = string.IsNullOrEmpty(phase) ? _lastPhaseName : phase;
        output.SessionPhase = _lastPhaseName;
        output.PhaseChanged = phaseChanged;

        output.WasInPitLane = _wasInPitLane;
        output.IsInPitLane = graphics.IsInPitLane;
        _wasInPitLane = graphics.IsInPitLane;

        if (physics.TyreWear is { Length: >= 4 })
        {
            output.TyreWearFL = physics.TyreWear[0];
            output.TyreWearFR = physics.TyreWear[1];
            output.TyreWearRL = physics.TyreWear[2];
            output.TyreWearRR = physics.TyreWear[3];
        }
        output.TyreCompound = DecodeBytes(graphics.TyreLf.TyreCompoundFront);

        if (physics.TyreTempI is { Length: >= 4 } && physics.TyreTempM is { Length: >= 4 } && physics.TyreTempO is { Length: >= 4 })
        {
            output.TyreTempFL = (physics.TyreTempI[0] + physics.TyreTempM[0] + physics.TyreTempO[0]) / 3f;
            output.TyreTempFR = (physics.TyreTempI[1] + physics.TyreTempM[1] + physics.TyreTempO[1]) / 3f;
            output.TyreTempRL = (physics.TyreTempI[2] + physics.TyreTempM[2] + physics.TyreTempO[2]) / 3f;
            output.TyreTempRR = (physics.TyreTempI[3] + physics.TyreTempM[3] + physics.TyreTempO[3]) / 3f;
        }

        float maxDmg = 0f;
        if (physics.SuspensionDamage is { Length: >= 4 })
        {
            for (int i = 0; i < 4; i++)
            {
                float d = physics.SuspensionDamage[i];
                // Normalize: R3E may report damage as 0-100 percentage instead of 0-1 float
                if (d > 1f) d /= 100f;
                maxDmg = Math.Max(maxDmg, Math.Clamp(d, 0f, 1f));
            }
        }
        if (physics.CarDamage is { Length: >= 5 })
        {
            for (int i = 0; i < 5; i++)
            {
                float d = physics.CarDamage[i];
                if (d > 1f) d /= 100f;
                maxDmg = Math.Max(maxDmg, Math.Clamp(d, 0f, 1f));
            }
        }
        output.MaxDamage = maxDmg;

        if (physics.WheelsPressure is { Length: >= 4 })
        {
            output.WheelsPressure[0] = physics.WheelsPressure[0];
            output.WheelsPressure[1] = physics.WheelsPressure[1];
            output.WheelsPressure[2] = physics.WheelsPressure[2];
            output.WheelsPressure[3] = physics.WheelsPressure[3];
        }
        if (physics.BrakeTemp is { Length: >= 4 })
        {
            output.BrakeTemp[0] = physics.BrakeTemp[0];
            output.BrakeTemp[1] = physics.BrakeTemp[1];
            output.BrakeTemp[2] = physics.BrakeTemp[2];
            output.BrakeTemp[3] = physics.BrakeTemp[3];
        }
        output.WaterTemp = physics.WaterTemp;

        output.AirTemperature = physics.AirTemp;
        output.RoadTemperature = physics.RoadTemp;
        output.Flag = graphics.Flag;

        output.RaceCutGainedTimeMs = graphics.RaceCutGainedTimeMs;
        output.IsWrongWay = graphics.IsWrongWay;
        output.IsLastLap = graphics.IsLastLap;

        string tyreWearStr = physics.TyreWear is { Length: >= 4 }
            ? $"{physics.TyreWear[0]:F2}/{physics.TyreWear[1]:F2}/{physics.TyreWear[2]:F2}/{physics.TyreWear[3]:F2}"
            : "N/A";
        Console.WriteLine($"[RaceInfo] Fuel={physics.Fuel:F1} Wear={tyreWearStr} Lap={currentLap} Pit={graphics.IsInPitLane}");

        IsInitialized = true;
    }

    private static string GetGapTrend(float currentGap, Queue<float> history)
    {
        history.Enqueue(currentGap);
        while (history.Count > 20)
            history.TryDequeue(out _);

        if (history.Count < 5)
            return "stable";

        float first = history.First();
        float last = history.Last();
        float delta = last - first;

        if (Math.Abs(delta) < 0.1f) return "stable";
        return delta < 0 ? "closing" : "dropping";
    }

    private static string DecodeBytes(byte[]? data)
    {
        if (data == null || data.Length == 0) return "";
        int nullIdx = Array.IndexOf(data, (byte)0);
        int len = nullIdx >= 0 ? nullIdx : data.Length;
        return System.Text.Encoding.ASCII.GetString(data, 0, len).Trim();
    }
}

public sealed class RaceInfoOutput
{
    public float FuelLevel { get; set; }
    public float FuelPerLap { get; set; }
    public float FuelLapsRemaining { get; set; }

    public float GapAhead { get; set; }
    public float GapBehind { get; set; }
    public string GapTrendAhead { get; set; } = "stable";
    public string GapTrendBehind { get; set; } = "stable";

    public int Position { get; set; }
    public int TotalDrivers { get; set; }
    public int PositionChange { get; set; }

    public string SessionPhase { get; set; } = "";
    public bool PhaseChanged { get; set; }
    public bool IsInPitLane { get; set; }
    public bool WasInPitLane { get; set; }

    public float TyreWearFL { get; set; }
    public float TyreWearFR { get; set; }
    public float TyreWearRL { get; set; }
    public float TyreWearRR { get; set; }
    public float TyreTempFL { get; set; }
    public float TyreTempFR { get; set; }
    public float TyreTempRL { get; set; }
    public float TyreTempRR { get; set; }
    public string TyreCompound { get; set; } = "";

    public float MaxDamage { get; set; }

    public float AirTemperature { get; set; }
    public float RoadTemperature { get; set; }
    public AcEvoFlagType Flag { get; set; }

    public float[] WheelsPressure { get; set; } = new float[4];
    public float[] BrakeTemp { get; set; } = new float[4];
    public float WaterTemp { get; set; }

    public int RaceCutGainedTimeMs { get; set; }
    public bool IsWrongWay { get; set; }
    public bool IsLastLap { get; set; }
}
