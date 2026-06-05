using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using AcEvoFfbTuner.Core.FfbProcessing;
using AcEvoFfbTuner.Core.SharedMemory.Structs;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AcEvoFfbTuner.ViewModels;

public enum TyreTempClass
{
    Cold,
    Ok,
    Hot,
    Peak,
    Overheating
}

public enum DamageLevel
{
    None,
    Light,
    Moderate,
    Heavy,
    Destroyed
}

public sealed partial class RaceInfoViewModel : ObservableObject
{
    [ObservableProperty]
    private float _gapAhead;

    [ObservableProperty]
    private float _gapBehind;

    [ObservableProperty]
    private int _position;

    [ObservableProperty]
    private int _totalDrivers;

    [ObservableProperty]
    private string _gapTrendAhead = "\u2192";

    [ObservableProperty]
    private string _gapTrendBehind = "\u2192";

    [ObservableProperty]
    private float _fuelLevel;

    [ObservableProperty]
    private float _fuelPerLap;

    [ObservableProperty]
    private float _fuelLapsRemaining;

    [ObservableProperty]
    private int _currentLap;

    [ObservableProperty]
    private int _totalLaps;

    [ObservableProperty]
    private string _sessionTimeLeft = "";

    [ObservableProperty]
    private float _tyreWearFL;

    [ObservableProperty]
    private float _tyreWearFR;

    [ObservableProperty]
    private float _tyreWearRL;

    [ObservableProperty]
    private float _tyreWearRR;

    [ObservableProperty]
    private float _tyrePressureFL;

    [ObservableProperty]
    private float _tyrePressureFR;

    [ObservableProperty]
    private float _tyrePressureRL;

    [ObservableProperty]
    private float _tyrePressureRR;

    [ObservableProperty]
    private float _brakeTempFL;

    [ObservableProperty]
    private float _brakeTempFR;

    [ObservableProperty]
    private float _brakeTempRL;

    [ObservableProperty]
    private float _brakeTempRR;

    [ObservableProperty]
    private TyreTempClass _tyreTempClassFL;

    [ObservableProperty]
    private TyreTempClass _tyreTempClassFR;

    [ObservableProperty]
    private TyreTempClass _tyreTempClassRL;

    [ObservableProperty]
    private TyreTempClass _tyreTempClassRR;

    [ObservableProperty]
    private string _tyreCompound = "--";

    [ObservableProperty]
    private string _flag = "AcGreenFlag";

    [ObservableProperty]
    private string _globalFlag = "";

    [ObservableProperty]
    private bool _isInPitLane;

    [ObservableProperty]
    private bool _isLastLap;

    [ObservableProperty]
    private float _airTemperature;

    [ObservableProperty]
    private float _roadTemperature;

    [ObservableProperty]
    private float _damageBody;

    [ObservableProperty]
    private float _damageSuspensionFL;

    [ObservableProperty]
    private float _damageSuspensionFR;

    [ObservableProperty]
    private float _damageSuspensionRL;

    [ObservableProperty]
    private float _damageSuspensionRR;

    [ObservableProperty]
    private DamageLevel _damageSummary;

    [ObservableProperty]
    private int _raceCutGainedTimeMs;

    [ObservableProperty]
    private bool _isWrongWay;

    [ObservableProperty]
    private float _waterTemperature;

    [ObservableProperty]
    private float _oilTemperatureC;

    private readonly Queue<float> _gapHistoryAhead = new();
    private readonly Queue<float> _gapHistoryBehind = new();

    private static string DecodeBytes(byte[] data)
    {
        if (data == null || data.Length == 0) return "";
        int nullIdx = Array.IndexOf(data, (byte)0);
        int len = nullIdx >= 0 ? nullIdx : data.Length;
        return Encoding.ASCII.GetString(data, 0, len).Trim();
    }

    public void UpdateFrom(SPageFilePhysicsEvo p, SPageFileGraphicEvo g)
    {
        GapAhead = g.GapAhead;
        GapBehind = g.GapBehind;

        GapTrendAhead = GetGapTrend(g.GapAhead, _gapHistoryAhead);
        GapTrendBehind = GetGapTrend(g.GapBehind, _gapHistoryBehind);

        Position = (int)g.CurrentPos;
        TotalDrivers = (int)g.TotalDrivers;

        FuelLevel = g.FuelLiterCurrentQuantity;
        FuelPerLap = g.FuelLiterPerLap;
        FuelLapsRemaining = g.LapsPossibleWithFuel;

        CurrentLap = g.SessionState.CurrentLap;
        TotalLaps = g.TotalLapCount;
        SessionTimeLeft = DecodeBytes(g.SessionState.TimeLeft);

        if (p.TyreWear is { Length: >= 4 })
        {
            TyreWearFL = p.TyreWear[0];
            TyreWearFR = p.TyreWear[1];
            TyreWearRL = p.TyreWear[2];
            TyreWearRR = p.TyreWear[3];
        }

        if (p.WheelsPressure is { Length: >= 4 })
        {
            TyrePressureFL = p.WheelsPressure[0];
            TyrePressureFR = p.WheelsPressure[1];
            TyrePressureRL = p.WheelsPressure[2];
            TyrePressureRR = p.WheelsPressure[3];
        }

        if (p.BrakeTemp is { Length: >= 4 })
        {
            BrakeTempFL = p.BrakeTemp[0];
            BrakeTempFR = p.BrakeTemp[1];
            BrakeTempRL = p.BrakeTemp[2];
            BrakeTempRR = p.BrakeTemp[3];
        }

        float tempI_FL = p.TyreTempI is { Length: >= 4 } ? p.TyreTempI[0] : 0f;
        float tempM_FL = p.TyreTempM is { Length: >= 4 } ? p.TyreTempM[0] : 0f;
        float tempO_FL = p.TyreTempO is { Length: >= 4 } ? p.TyreTempO[0] : 0f;
        TyreTempClassFL = ClassifyCoreTemp((tempI_FL + tempM_FL + tempO_FL) / 3f);

        float tempI_FR = p.TyreTempI is { Length: >= 4 } ? p.TyreTempI[1] : 0f;
        float tempM_FR = p.TyreTempM is { Length: >= 4 } ? p.TyreTempM[1] : 0f;
        float tempO_FR = p.TyreTempO is { Length: >= 4 } ? p.TyreTempO[1] : 0f;
        TyreTempClassFR = ClassifyCoreTemp((tempI_FR + tempM_FR + tempO_FR) / 3f);

        float tempI_RL = p.TyreTempI is { Length: >= 4 } ? p.TyreTempI[2] : 0f;
        float tempM_RL = p.TyreTempM is { Length: >= 4 } ? p.TyreTempM[2] : 0f;
        float tempO_RL = p.TyreTempO is { Length: >= 4 } ? p.TyreTempO[2] : 0f;
        TyreTempClassRL = ClassifyCoreTemp((tempI_RL + tempM_RL + tempO_RL) / 3f);

        float tempI_RR = p.TyreTempI is { Length: >= 4 } ? p.TyreTempI[3] : 0f;
        float tempM_RR = p.TyreTempM is { Length: >= 4 } ? p.TyreTempM[3] : 0f;
        float tempO_RR = p.TyreTempO is { Length: >= 4 } ? p.TyreTempO[3] : 0f;
        TyreTempClassRR = ClassifyCoreTemp((tempI_RR + tempM_RR + tempO_RR) / 3f);

        TyreCompound = DecodeBytes(g.TyreLf.TyreCompoundFront);

        Flag = g.Flag.ToString();
        GlobalFlag = g.GlobalFlag.ToString();
        IsInPitLane = g.IsInPitLane;
        IsLastLap = g.IsLastLap;

        AirTemperature = p.AirTemp;
        RoadTemperature = p.RoadTemp;

        DamageBody = g.CarDamage.DamageCenter;
        DamageSuspensionFL = g.CarDamage.DamageSuspensionLf;
        DamageSuspensionFR = g.CarDamage.DamageSuspensionRf;
        DamageSuspensionRL = g.CarDamage.DamageSuspensionLr;
        DamageSuspensionRR = g.CarDamage.DamageSuspensionRr;

        float maxDamage = Math.Max(Math.Max(Math.Max(Math.Max(
            DamageBody, DamageSuspensionFL), DamageSuspensionFR), DamageSuspensionRL), DamageSuspensionRR);
        DamageSummary = ClassifyDamage(maxDamage);

        RaceCutGainedTimeMs = g.RaceCutGainedTimeMs;
        IsWrongWay = g.IsWrongWay;

        WaterTemperature = p.WaterTemp;
        OilTemperatureC = g.OilTemperatureC;
    }

    public void UpdateFromRaceInfoOutput(RaceInfoOutput data, SPageFilePhysicsEvo physics)
    {
        Console.WriteLine($"[RaceInfoVM] Update: Fuel={data.FuelLevel:F1} GapA={data.GapAhead:F1} Pos={data.Position}/{data.TotalDrivers}");
        FuelLevel = data.FuelLevel;
        FuelPerLap = data.FuelPerLap;
        FuelLapsRemaining = data.FuelLapsRemaining;

        GapAhead = data.GapAhead;
        GapBehind = data.GapBehind;
        GapTrendAhead = data.GapTrendAhead switch
        {
            "closing" => "\u25B2",
            "dropping" => "\u25BC",
            _ => "\u2192"
        };
        GapTrendBehind = data.GapTrendBehind switch
        {
            "closing" => "\u25B2",
            "dropping" => "\u25BC",
            _ => "\u2192"
        };

        Position = data.Position;
        TotalDrivers = data.TotalDrivers;

        TyreWearFL = data.TyreWearFL;
        TyreWearFR = data.TyreWearFR;
        TyreWearRL = data.TyreWearRL;
        TyreWearRR = data.TyreWearRR;

        TyreTempClassFL = ClassifyCoreTemp(data.TyreTempFL);
        TyreTempClassFR = ClassifyCoreTemp(data.TyreTempFR);
        TyreTempClassRL = ClassifyCoreTemp(data.TyreTempRL);
        TyreTempClassRR = ClassifyCoreTemp(data.TyreTempRR);

        TyreCompound = string.IsNullOrEmpty(data.TyreCompound) ? "--" : data.TyreCompound;

        Flag = data.Flag.ToString();
        IsInPitLane = data.IsInPitLane;
        IsLastLap = data.IsLastLap;

        AirTemperature = data.AirTemperature;
        RoadTemperature = data.RoadTemperature;

        DamageSummary = ClassifyDamage(data.MaxDamage);
        RaceCutGainedTimeMs = data.RaceCutGainedTimeMs;
        IsWrongWay = data.IsWrongWay;

        if (data.WheelsPressure is { Length: >= 4 })
        {
            TyrePressureFL = data.WheelsPressure[0];
            TyrePressureFR = data.WheelsPressure[1];
            TyrePressureRL = data.WheelsPressure[2];
            TyrePressureRR = data.WheelsPressure[3];
        }
        if (data.BrakeTemp is { Length: >= 4 })
        {
            BrakeTempFL = data.BrakeTemp[0];
            BrakeTempFR = data.BrakeTemp[1];
            BrakeTempRL = data.BrakeTemp[2];
            BrakeTempRR = data.BrakeTemp[3];
        }

        WaterTemperature = data.WaterTemp;
    }

    public string GetGapTrend(float currentGap, Queue<float> history)
    {
        history.Enqueue(currentGap);
        while (history.Count > 20)
            history.TryDequeue(out _);

        if (history.Count < 5)
            return "\u2192";

        float first = history.First();
        float last = history.Last();
        float delta = last - first;

        if (Math.Abs(delta) < 0.1f)
            return "\u2192";

        return delta < 0 ? "\u25B2" : "\u25BC";
    }

    public static TyreTempClass ClassifyCoreTemp(float tempC)
    {
        if (tempC < 60f)  return TyreTempClass.Cold;
        if (tempC < 85f)  return TyreTempClass.Ok;
        if (tempC < 100f) return TyreTempClass.Hot;
        if (tempC < 115f) return TyreTempClass.Peak;
        return TyreTempClass.Overheating;
    }

    public static DamageLevel ClassifyDamage(float dmg)
    {
        if (dmg < 0.05f)  return DamageLevel.None;
        if (dmg < 0.20f)  return DamageLevel.Light;
        if (dmg < 0.50f)  return DamageLevel.Moderate;
        if (dmg < 0.80f)  return DamageLevel.Heavy;
        return DamageLevel.Destroyed;
    }

    public static string GetTempClassString(TyreTempClass t)
    {
        return t switch
        {
            TyreTempClass.Cold => "COLD",
            TyreTempClass.Ok => "OK",
            TyreTempClass.Hot => "HOT",
            TyreTempClass.Peak => "PEAK",
            TyreTempClass.Overheating => "OVER",
            _ => "--"
        };
    }

    public static string GetDamageString(DamageLevel d)
    {
        return d switch
        {
            DamageLevel.None => "NONE",
            DamageLevel.Light => "LIGHT",
            DamageLevel.Moderate => "MODERATE",
            DamageLevel.Heavy => "HEAVY",
            DamageLevel.Destroyed => "DESTROYED",
            _ => "--"
        };
    }

    public static string GetFlagDisplayName(AcEvoFlagType flag)
    {
        return flag switch
        {
            AcEvoFlagType.AcNoFlag => "GREEN",
            AcEvoFlagType.AcBlueFlag => "BLUE",
            AcEvoFlagType.AcYellowFlag => "YELLOW",
            AcEvoFlagType.AcBlackFlag => "BLACK",
            AcEvoFlagType.AcWhiteFlag => "WHITE",
            AcEvoFlagType.AcCheckeredFlag => "CHECKERED",
            AcEvoFlagType.AcPenaltyFlag => "PENALTY",
            AcEvoFlagType.AcGreenFlag => "GREEN",
            AcEvoFlagType.AcOrangeFlag => "ORANGE",
            _ => "GREEN"
        };
    }
}
