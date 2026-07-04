using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;
using AcEvoFfbTuner.Core.SharedMemory;
using AcEvoFfbTuner.Core.SharedMemory.Structs;

namespace TelemetryBrowser.Services;

public sealed class TelemetryService : BackgroundService
{
    private readonly ConcurrentDictionary<Guid, Channel<string>> _subscribers = new();
    private readonly ConcurrentDictionary<string, Func<ISharedMemoryReader>> _readerFactories = new();
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
        MaxDepth = 64
    };

    private ISharedMemoryReader? _reader;
    private string _currentGame = "";
    private int _readCount;
    private DateTime _lastActivity = DateTime.MinValue;
    private readonly object _lock = new();
    private bool _disposed;

    public bool IsConnected => _reader?.IsConnected ?? false;
    public string CurrentGame => _currentGame;
    public int ReadCount => _readCount;
    public DateTime LastActivity => _lastActivity;

    public TelemetryService()
    {
        _readerFactories["acevo"] = () => new SharedMemoryReader();
        _readerFactories["assettocorsa"] = () => new AssettoCorsaSharedMemoryReader();
        _readerFactories["assettocorsac"] = () => new AssettoCorsaSharedMemoryReader();
        _readerFactories["raccoroom"] = () => new RaceroomSharedMemoryReader();
        _readerFactories["lemansultimate"] = () => new LmuSharedMemoryReader();
        _readerFactories["rfactor2"] = () => new RFactor2SharedMemoryReader();
    }

    public Guid Subscribe(Channel<string> channel)
    {
        var id = Guid.NewGuid();
        _subscribers[id] = channel;
        return id;
    }

    public void Unsubscribe(Guid id)
    {
        if (_subscribers.TryRemove(id, out var ch))
            ch.Writer.TryComplete();
    }

    public (bool success, string message) Connect(string gameId)
    {
        lock (_lock)
        {
            DisconnectReader();

            if (!_readerFactories.TryGetValue(gameId, out var factory))
                return (false, $"Unknown game: {gameId}");

            var reader = factory();
            if (!reader.TryConnect())
            {
                reader.Dispose();
                return (false, $"Cannot connect to {gameId} shared memory. Is the game running?");
            }

            _reader = reader;
            _currentGame = gameId;
            _readCount = 0;
            _lastActivity = DateTime.UtcNow;
            return (true, $"Connected to {gameId}");
        }
    }

    private void DisconnectReader()
    {
        if (_reader != null)
        {
            try { _reader.Disconnect(); } catch { }
            _reader.Dispose();
            _reader = null;
        }
        _readCount = 0;
    }

    public void Disconnect()
    {
        lock (_lock)
        {
            DisconnectReader();
            _currentGame = "";
        }
    }

    public FlatTelemetrySnapshot? TryRead()
    {
        lock (_lock)
        {
            if (_reader == null || !_reader.IsConnected)
                return null;

            try
            {
                if (!_reader.TryReadPhysics(out var physics))
                    return null;

                bool gotGraphics = _reader.TryReadGraphics(out var graphics);
                bool gotStatic = _reader.TryReadStatic(out var staticData);

                _readCount++;
                _lastActivity = DateTime.UtcNow;

                var snap = FlatTelemetrySnapshot.Create(_currentGame, DateTime.UtcNow, _readCount);
                ConvertPhysics(physics, snap);
                if (gotGraphics && _currentGame is "acevo" or "assettocorsa")
                    ConvertGraphics(graphics, snap);
                if (gotStatic && _currentGame is "acevo" or "assettocorsa")
                    ConvertStatic(staticData, snap);
                SanitizeSnapshot(snap);
                return snap;
            }
            catch
            {
                return null;
            }
        }
    }

    private static void ConvertPhysics(SPageFilePhysicsEvo p, FlatTelemetrySnapshot s)
    {
        s["PacketId"] = p.PacketId; s["Gas"] = p.Gas; s["Brake"] = p.Brake;
        s["Fuel"] = p.Fuel; s["Gear"] = p.Gear; s["Rpms"] = p.Rpms;
        s["SteerAngle"] = p.SteerAngle; s["SpeedKmh"] = p.SpeedKmh;
        s["Heading"] = p.Heading; s["Pitch"] = p.Pitch; s["Roll"] = p.Roll;
        s["CgHeight"] = p.CgHeight; s["NumberOfTyresOut"] = p.NumberOfTyresOut;
        s["PitLimiterOn"] = p.PitLimiterOn; s["Abs"] = p.Abs;
        s["AutoShifterOn"] = p.AutoShifterOn; s["TurboBoost"] = p.TurboBoost;
        s["Ballast"] = p.Ballast; s["AirDensity"] = p.AirDensity;
        s["AirTemp"] = p.AirTemp; s["RoadTemp"] = p.RoadTemp;
        s["FinalFf"] = p.FinalFf; s["PerformanceMeter"] = p.PerformanceMeter;
        s["EngineBrake"] = p.EngineBrake; s["Clutch"] = p.Clutch;
        s["BrakeBias"] = p.BrakeBias; s["IsAiControlled"] = p.IsAiControlled;
        s["Drs"] = p.Drs; s["Tc"] = p.Tc;
        s["KersCharge"] = p.KersCharge; s["KersInput"] = p.KersInput;
        s["LocalAngularVel"] = p.LocalAngularVel; s["LocalVelocity"] = p.LocalVelocity;
        s["WaterTemp"] = p.WaterTemp;
        s["IgnitionOn"] = p.IgnitionOn; s["StarterEngineOn"] = p.StarterEngineOn;
        s["IsEngineRunning"] = p.IsEngineRunning;
        s["KerbVibration"] = p.KerbVibration; s["SlipVibrations"] = p.SlipVibrations;
        s["RoadVibrations"] = p.RoadVibrations; s["AbsVibrations"] = p.AbsVibrations;

        StoreWheelArray(s, p.WheelSlip, "WheelSlip");
        StoreWheelArray(s, p.WheelLoad, "WheelLoad");
        StoreWheelArray(s, p.WheelsPressure, "WheelsPressure");
        StoreWheelArray(s, p.WheelAngularSpeed, "WheelAngularSpeed");
        StoreWheelArray(s, p.Mz, "Mz");
        StoreWheelArray(s, p.Fx, "Fx");
        StoreWheelArray(s, p.Fy, "Fy");
        StoreWheelArray(s, p.SlipRatio, "SlipRatio");
        StoreWheelArray(s, p.SlipAngle, "SlipAngle");
        StoreWheelArray(s, p.TyreWear, "TyreWear");
        StoreWheelArray(s, p.TyreDirtyLevel, "TyreDirtyLevel");
        StoreWheelArray(s, p.TyreCoreTemperature, "TyreCoreTemperature");
        StoreWheelArray(s, p.CamberRad, "CamberRad");
        StoreWheelArray(s, p.SuspensionTravel, "SuspensionTravel");
        StoreWheelArray(s, p.BrakeTemp, "BrakeTemp");
        StoreWheelArray(s, p.TyreTempI, "TyreTempI");
        StoreWheelArray(s, p.TyreTempM, "TyreTempM");
        StoreWheelArray(s, p.TyreTempO, "TyreTempO");
        StoreWheelArray(s, p.TyreTemp, "TyreTemp");
        StoreWheelArray(s, p.BrakeTorque, "BrakeTorque");
        StoreWheelArray(s, p.PadLife, "PadLife");
        StoreWheelArray(s, p.DiscLife, "DiscLife");

        s["CarDamage"] = p.CarDamage; s["RideHeight"] = p.RideHeight;
        s["SuspensionDamage"] = p.SuspensionDamage;
        s["Velocity"] = p.Velocity; s["AccG"] = p.AccG;
    }

    private static void ConvertGraphics(SPageFileGraphicEvo g, FlatTelemetrySnapshot s)
    {
        s["G_PacketId"] = g.PacketId;
        s["G_Status"] = g.Status.ToString();
        s["G_Rpm"] = g.Rpm;
        s["G_IsRpmLimiterOn"] = g.IsRpmLimiterOn;
        s["G_DisplaySpeedKmh"] = g.DisplaySpeedKmh;
        s["G_GearInt"] = g.GearInt;
        s["G_RpmPercent"] = g.RpmPercent;
        s["G_GasPercent"] = g.GasPercent;
        s["G_BrakePercent"] = g.BrakePercent;
        s["G_HandbrakePercent"] = g.HandbrakePercent;
        s["G_SteeringPercent"] = g.SteeringPercent;
        s["G_FfbStrength"] = g.FfbStrength;
        s["G_CarFfbMultiplier"] = g.CarFfbMultiplier;
        s["G_SteerDegrees"] = g.SteerDegrees;
        s["G_WaterTempC"] = g.WaterTemperatureC;
        s["G_AirTempC"] = g.AirTemperatureC;
        s["G_OilTempC"] = g.OilTemperatureC;
        s["G_OilPressureBar"] = g.OilPressureBar;
        s["G_FuelLiter"] = g.FuelLiterCurrentQuantity;
        s["G_CurrentTorque"] = g.CurrentTorque;
        s["G_CurrentBhp"] = g.CurrentBhp;
        s["G_TotalKm"] = g.TotalKm;
        s["G_CurrentLapTimeMs"] = g.CurrentLapTimeMs;
        s["G_LastLaptimeMs"] = g.LastLaptimeMs;
        s["G_BestLaptimeMs"] = g.BestLaptimeMs;
        s["G_TotalLapCount"] = g.TotalLapCount;
        s["G_CurrentPos"] = g.CurrentPos;
        s["G_TotalDrivers"] = g.TotalDrivers;
        s["G_Flag"] = g.Flag.ToString();
        s["G_GlobalFlag"] = g.GlobalFlag.ToString();
        s["G_DriverName"] = ByteStr(g.DriverName);
        s["G_CarModel"] = ByteStr(g.CarModel);
        s["G_DriverSurname"] = ByteStr(g.DriverSurname);
        s["G_IsInPitBox"] = g.IsInPitBox;
        s["G_IsInPitLane"] = g.IsInPitLane;
        s["G_FuelPerLap"] = g.FuelPerLap;
        s["G_FuelEstimatedLaps"] = g.FuelEstimatedLaps;
        s["G_DeltaTimeMs"] = g.DeltaTimeMs;
        s["G_PlayerFps"] = g.PlayerFps;
        s["G_PlayerFpsAvg"] = g.PlayerFpsAvg;
        s["G_GapAhead"] = g.GapAhead;
        s["G_GapBehind"] = g.GapBehind;
        s["G_ActiveCars"] = g.ActiveCars;
        s["G_AccGForceX"] = g.GForcesX;
        s["G_AccGForceY"] = g.GForcesY;
        s["G_AccGForceZ"] = g.GForcesZ;
        s["G_MaxGears"] = g.MaxGears;
        s["G_EngineType"] = g.EngineType.ToString();
        s["G_CarLocation"] = g.CarLocation.ToString();
        s["G_PlayerPing"] = g.PlayerPing;
        s["G_PlayerLatency"] = g.PlayerLatency;

        for (int i = 0; i < 4; i++)
        {
            var label = i switch { 0 => "Lf", 1 => "Rf", 2 => "Lr", _ => "Rr" };
            SmevoTyreState ts;
            byte[] compound;
            switch (i)
            {
                case 0: ts = g.TyreLf; compound = ts.TyreCompoundFront; break;
                case 1: ts = g.TyreRf; compound = ts.TyreCompoundFront; break;
                case 2: ts = g.TyreLr; compound = ts.TyreCompoundRear; break;
                default: ts = g.TyreRr; compound = ts.TyreCompoundRear; break;
            }
            s[$"G_Tyre{label}_Temp"] = ts.TyreTemperatureC;
            s[$"G_Tyre{label}_Pressure"] = ts.TyrePression;
            s[$"G_Tyre{label}_BrakeTemp"] = ts.BrakeTemperatureC;
            s[$"G_Tyre{label}_Left"] = ts.TyreTemperatureLeft;
            s[$"G_Tyre{label}_Center"] = ts.TyreTemperatureCenter;
            s[$"G_Tyre{label}_Right"] = ts.TyreTemperatureRight;
            s[$"G_Tyre{label}_Slip"] = ts.Slip;
            s[$"G_Tyre{label}_Lock"] = ts.Lock;
            s[$"G_Tyre{label}_Compound"] = ByteStr(compound);
        }
    }

    private static void ConvertStatic(SPageFileStaticEvo st, FlatTelemetrySnapshot s)
    {
        s["S_SmVersion"] = ByteStr(st.SmVersion);
        s["S_AcVersion"] = ByteStr(st.AcEvoVersion);
        s["S_Session"] = st.Session.ToString();
        s["S_SessionName"] = ByteStr(st.SessionName);
        s["S_Track"] = ByteStr(st.Track);
        s["S_TrackConfig"] = ByteStr(st.TrackConfiguration);
        s["S_TrackLengthM"] = st.TrackLengthM;
        s["S_Nation"] = ByteStr(st.Nation);
        s["S_Longitude"] = st.Longitude;
        s["S_Latitude"] = st.Latitude;
        s["S_IsOnline"] = st.IsOnline;
        s["S_NumberOfSessions"] = st.NumberOfSessions;
        s["S_StartingGrip"] = st.StartingGrip.ToString();
        s["S_AmbientTemp"] = st.StartingAmbientTemperatureC;
        s["S_GroundTemp"] = st.StartingGroundTemperatureC;
        s["S_IsTimedRace"] = st.IsTimedRace;
        s["S_IsStaticWeather"] = st.IsStaticWeather;
    }

    private static void StoreWheelArray(FlatTelemetrySnapshot s, float[]? arr, string key)
    {
        if (arr == null) return;
        for (int i = 0; i < arr.Length && i < 4; i++)
            s[$"{key}_{i}"] = arr[i];
    }

    private static string ByteStr(byte[]? buf)
    {
        if (buf == null || buf.Length == 0) return "";
        int len = Array.IndexOf(buf, (byte)0);
        len = len < 0 ? buf.Length : len;
        return System.Text.Encoding.UTF8.GetString(buf, 0, len);
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(16));

        while (await timer.WaitForNextTickAsync(ct))
        {
            var snap = TryRead();
            if (snap == null) continue;

            var json = JsonSerializer.Serialize(snap, _jsonOptions);

            var dead = new List<Guid>();
            foreach (var (id, channel) in _subscribers)
            {
                if (!channel.Writer.TryWrite(json))
                    dead.Add(id);
            }

            foreach (var id in dead)
            {
                if (_subscribers.TryRemove(id, out var ch))
                    ch.Writer.TryComplete();
            }
        }
    }

    private static void SanitizeSnapshot(FlatTelemetrySnapshot snap)
    {
        foreach (var key in snap.Keys.ToList())
        {
            var val = snap[key];
            if (val is float f && (float.IsNaN(f) || float.IsInfinity(f)))
                snap[key] = 0f;
            else if (val is double d && (double.IsNaN(d) || double.IsInfinity(d)))
                snap[key] = 0.0;
        }
    }

    public override void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        DisconnectReader();
        foreach (var (_, ch) in _subscribers)
            ch.Writer.TryComplete();
        _subscribers.Clear();
        base.Dispose();
    }
}

public class FlatTelemetrySnapshot : Dictionary<string, object?>
{
    private FlatTelemetrySnapshot() { }

    public static FlatTelemetrySnapshot Create(string game, DateTime timestamp, int readIndex)
    {
        var s = new FlatTelemetrySnapshot();
        s["_game"] = game;
        s["_timestamp"] = timestamp;
        s["_readIndex"] = readIndex;
        return s;
    }

    public string Game => this["_game"] as string ?? "";
    public DateTime Timestamp => this["_timestamp"] is DateTime dt ? dt : DateTime.MinValue;
    public int ReadIndex => this["_readIndex"] is int ri ? ri : 0;
}
